using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RainmeterBackend;

// 腾讯云翻译 Provider（translation_provider@1）的全链路离线探针（规格 §7.3 / §11）。
//
// 不依赖真实腾讯云：用 TcpListener 手写一个最小的机器翻译（TMT）端点，让
// 「consumer 请求 → Broker(-Mode ServiceCall) → translate-tencent 插件 → 假 TMT」
// 整条链路真正跑起来（真 HTTP、真 TC3 签名、真 DPAPI secret、真 error_kind）。规格 §12：CI 一律离线。
//
// 重点盯四件事：
//   ① TC3 签名：探针**独立重算**一遍（自己实现的 SHA256/HMAC）并与实际 Authorization 头逐字节比对。
//      签名算错在线上只会换回一个 SignatureFailure，靠"上线试试"是查不出来的。
//   ② 花钱的边界：输入非法 / 语言非法 / 没密钥 / 源=目标 ⇒ 一次请求都不许发；
//      批量内部分批与 250ms 节流真的生效；同一批标题重评必须命中缓存（§7.3「重评不重复付费」）。
//   ③ 契约：translations 与 texts **等长保序**；长度不一致必须拒绝整批，不能返回短数组让消费方错位。
//   ④ 失败语义：腾讯云错误码分级（fatal vs 可重试）、HTTP 层错误、坏 JSON 的可读文案。
internal static class TranslateTencentProbe
{
    private const string PluginId = "io.github.kevendai.translate-tencent";
    private const string PluginVersion = "1.0.0";
    private const string Service = "translation_provider@1";
    private const string Consumer = "io.github.test.translation-consumer";
    private const string SecretId = "AKIDprobe000000000000000000";
    private const string SecretKey = "probeSecretKey000000000000000000";
    private const string ContentType = "application/json; charset=utf-8";
    private const string SignedHeaders = "content-type;host;x-tc-action";
    private const int ChunkCharBudget = 1800;

    private static string build, pluginHost, root, dataRoot;
    private static int checks, failures;
    private static FakeTencentTmt server;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        build = AppDomain.CurrentDomain.BaseDirectory;
        pluginHost = Path.Combine(build, "PluginHost.exe");
        root = Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT") ?? "";
        dataRoot = root == "" ? "" : PluginPaths.DataRoot(PluginId);
        Expect(File.Exists(pluginHost), "PluginHost.exe 在构建目录里");
        Expect(root != "", "RAINMETER_PLUGIN_ROOT 已设置");
        if (failures > 0) return Report();
        try
        {
            using (server = new FakeTencentTmt())
            {
                server.Start();
                ManifestSection();
                SetupSection();
                TranslateSection();
                LanguageSection();
                CacheSection();
                SplitSection();
                SameLanguageSection();
                ErrorSection();
                InputSection();
                ActionSection();
            }
        }
        catch (Exception ex) { Expect(false, "探针自身不应抛异常：" + ex); }
        return Report();
    }

    // ── manifest：真实 plugin.json 按 v2.1 语义加载 ───────────────────────────
    private static void ManifestSection()
    {
        PluginManifest manifest = PluginManifest.Load(PluginPaths.VersionRoot(PluginId, PluginVersion));
        Expect(manifest.Provides.Count == 1 && manifest.Provides[0] == Service, "manifest 声明 provides=translation_provider@1");
        Expect(manifest.Billing == "may_charge", "billing=may_charge（可能收费，确认框要据此提示）");
        Expect(manifest.Capabilities.Count == 0, "纯 Provider：capabilities 为空也能加载");
        Expect(manifest.AddressTarget == "", "翻译 provider 不做地址代管（无 address_target）");
        Expect(manifest.MinHostVersion == "2.1.0", "min_host_version=2.1.0");
        Expect(!manifest.DefaultEnabled, "翻译服务默认不启用（要用户自己去装并且同意付费）");
        bool hasTest = false;
        foreach (Dictionary<string, object> action in manifest.Actions)
            if (JsonUtil.String(action, "id", "") == "test_connection") hasTest = true;
        Expect(hasTest, "manifest 里声明了 test_connection 动作（设置页 [测试连接] 靠它）");
    }

    // ── 启用 Bootstrap 装好的插件、写入设置与凭据、验证绑定解析 ────────────────
    private static void SetupSection()
    {
        WriteConfig(server.Url, "en", "zh-CN", true, 200);
        WriteSecret(true);
        string current = Path.Combine(root, "Plugins", PluginId, "current.json");
        Dictionary<string, object> state = JsonUtil.LoadObject(current);
        state["enabled"] = true;
        JsonUtil.SaveAtomic(current, state);
        Expect(JsonUtil.Bool(JsonUtil.LoadObject(current), "enabled", false), "插件已启用");
        // 本 plugin root 是**共享**的：同一轮套件里可能有别的 translate provider。按 §3/§8，
        // 多候选时不自动绑定，必须由用户明确选择、宿主把选择写进 plugin-bindings.json；
        // 探针直接模拟这一步，然后验证绑定解析（这正是真实宿主的行为）。
        ServiceRegistry.SetBinding(Consumer, Service, PluginId);
        Expect(ServiceRegistry.ProviderFor(Consumer, Service) == PluginId, "显式绑定后解析到 translate-tencent");
        Expect(ServiceRegistry.BillingOf(PluginId) == "may_charge", "宿主能读到该 provider 的 billing");
        Expect(ServiceRegistry.ValidService(Service), "service 名格式合法（name@version）");
    }

    // ── OK 路径：TC3 签名逐字节核对 + 请求体形态 + 等长保序 ──────────────────
    private static void TranslateSection()
    {
        server.Mode = "ok";
        ClearCache();
        List<string> texts = new List<string> { "NEURAL DOMAIN ADAPTATION FOR DETECTION", "PAPER TWO TITLE", "PAPER THREE" };
        int before = server.Requests;
        Dictionary<string, object> response = Broker(Texts(texts), "translate");
        Expect(Ok(response), "translate 成功，实际 error=" + JsonUtil.String(response, "error", ""));
        Expect(JsonUtil.String(response, "error_kind", "") == "", "成功时 error_kind 为空");
        Expect(!JsonUtil.Bool(response, "fatal", true), "成功时 fatal=false");
        Expect(server.Requests - before == 1, "一批标题只发一次请求（实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次）");

        // 请求形态：Host / Action / Version / Region / Content-Type 都要对得上，签名才可能对。
        Expect(server.LastPath == "/", "POST 到 /，实际 " + server.LastPath);
        Expect(server.LastAction == "TextTranslateBatch", "X-TC-Action=TextTranslateBatch，实际 " + server.LastAction);
        Expect(server.LastVersion == "2018-03-21", "X-TC-Version=2018-03-21，实际 " + server.LastVersion);
        Expect(server.LastRegion == "ap-guangzhou", "X-TC-Region 取自设置，实际 " + server.LastRegion);
        Expect(server.LastContentType == ContentType, "Content-Type 与签名里声明的一致，实际 " + server.LastContentType);
        Expect(server.LastAccept.IndexOf("application/json", StringComparison.Ordinal) >= 0, "Accept=application/json");

        long timestamp;
        Int64.TryParse(server.LastTimestamp, out timestamp);
        Expect(Math.Abs(NowSeconds() - timestamp) <= 300, "X-TC-Timestamp 与标准时间同刻（>5 分钟偏差会被判签名过期）");
        // ① 签名独立重算：探针用自己的 SHA256/HMAC 重算整条链，与实际头发出的头逐字节比对。
        string expected = ExpectedAuthorization(server.LastHost, ContentType, "TextTranslateBatch", server.LastBody, timestamp);
        Expect(server.LastAuthorization == expected, "Authorization 独立重算一致（host=" + server.LastHost + "）");
        Expect(server.LastAuthorization.IndexOf("Credential=" + SecretId + "/", StringComparison.Ordinal) >= 0, "Credential 用 Secret ID");
        Expect(server.LastAuthorization.IndexOf("/tmt/tc3_request,", StringComparison.Ordinal) >= 0, "CredentialScope 是 tmt（不是 cvm）");
        Expect(server.LastAuthorization.IndexOf("SignedHeaders=" + SignedHeaders + ",", StringComparison.Ordinal) >= 0, "SignedHeaders 只含 content-type;host;x-tc-action");
        Expect(server.LastAuthorization.IndexOf(SecretKey, StringComparison.Ordinal) < 0, "Authorization 不含 Secret Key");
        // host 变了签名必须跟着变：否则就是把 host 写错了还能过。
        Expect(ExpectedAuthorization("tmt.tencentcloudapi.com", ContentType, "TextTranslateBatch", server.LastBody, timestamp) != server.LastAuthorization, "签名覆盖 host（换个 host 签名就不同）");
        // 时间戳变了签名必须跟着变：否则就是忘了把 timestamp 带进 StringToSign。
        Expect(ExpectedAuthorization(server.LastHost, ContentType, "TextTranslateBatch", server.LastBody, timestamp + 1) != server.LastAuthorization, "签名覆盖 timestamp");
        Expect(ExpectedAuthorization(server.LastHost, ContentType, "TextTranslateBatch", "{}", timestamp) != server.LastAuthorization, "签名覆盖 payload 哈希");

        // 请求体形态。
        Dictionary<string, object> sent = JsonUtil.Object(JsonUtil.Deserialize(server.LastBody));
        List<object> wire = JsonUtil.Array(JsonUtil.Get(sent, "SourceTextList"));
        Expect(wire.Count == 3, "SourceTextList 条数与 texts 一致");
        Expect(ToStringValue(wire, 0) == texts[0] && ToStringValue(wire, 1) == texts[1] && ToStringValue(wire, 2) == texts[2], "SourceTextList 保序");
        Expect(JsonUtil.String(sent, "Source", "") == "en", "Source=en，实际 " + JsonUtil.String(sent, "Source", ""));
        Expect(JsonUtil.String(sent, "Target", "") == "zh", "目标语言 zh-CN 归一化成 zh，实际 " + JsonUtil.String(sent, "Target", ""));
        Expect(JsonUtil.Int(sent, "ProjectId", -1) == 0, "ProjectId=0（腾讯云默认项目）");
        Expect(server.LastBody.IndexOf(SecretKey, StringComparison.Ordinal) < 0, "请求体不含 Secret Key");

        // 输出契约：等长、保序、语言回报、usage、limits。
        Dictionary<string, object> output = Output(response);
        List<object> translations = JsonUtil.Array(JsonUtil.Get(output, "translations"));
        Expect(translations.Count == 3, "返回 3 条译文，实际 " + translations.Count.ToString(CultureInfo.InvariantCulture));
        Expect(ToStringValue(translations, 0) == "[zh]" + texts[0], "译文与输入一一对应（第 1 条）");
        Expect(ToStringValue(translations, 2) == "[zh]" + texts[2], "译文与输入一一对应（第 3 条）");
        Expect(JsonUtil.String(output, "source_language", "") == "en", "回报归一化后的源语言");
        Expect(JsonUtil.String(output, "target_language", "") == "zh", "回报归一化后的目标语言");
        Dictionary<string, object> usage = JsonUtil.Object(JsonUtil.Get(output, "usage"));
        Expect(JsonUtil.Int(usage, "requests", -1) == 1, "usage.requests=1");
        Expect(JsonUtil.Int(usage, "cached", -1) == 0, "首次调用没有命中缓存");
        int expectedChars = 0;
        foreach (string text in texts) expectedChars += ("[zh]" + text).Length;
        Expect(JsonUtil.Int(usage, "translated_chars", -1) == expectedChars, "usage.translated_chars=" + expectedChars.ToString(CultureInfo.InvariantCulture));
        Dictionary<string, object> limits = JsonUtil.Object(JsonUtil.Get(output, "limits"));
        Expect(JsonUtil.Int(limits, "chunk_char_budget", -1) == ChunkCharBudget, "limits.chunk_char_budget=" + ChunkCharBudget.ToString(CultureInfo.InvariantCulture));
        Expect(JsonUtil.Int(limits, "request_interval_ms", -1) == 250, "limits.request_interval_ms=250（腾讯云默认 5 次/秒）");
    }

    // ── 语言换算：消费方给 BCP-47，腾讯云收短码，换算必须在 Provider 里完成 ────
    private static void LanguageSection()
    {
        server.Mode = "ok";
        // 关缓存：这一节只关心"发出去的 Source/Target 是什么"。
        WriteConfig(server.Url, "en", "zh-CN", false, 200);
        ExpectWireLanguages("auto", "zh-CN", "auto", "zh");
        ExpectWireLanguages("en", "zh-TW", "en", "zh-TW");
        ExpectWireLanguages("en-US", "ja", "en", "ja");
        ExpectWireLanguages("zh", "en", "zh", "en");
        ExpectWireLanguages("ko", "en", "ko", "en");
    }

    private static void ExpectWireLanguages(string source, string target, string wireSource, string wireTarget)
    {
        Dictionary<string, object> input = Texts(new List<string> { "LANG PROBE " + source + "-" + target });
        input["source_language"] = source;
        input["target_language"] = target;
        Dictionary<string, object> response = Broker(input, "translate");
        string label = source + "→" + target;
        if (!Ok(response))
        {
            Expect(false, label + " 应当成功，实际 " + JsonUtil.String(response, "error", ""));
            return;
        }
        Dictionary<string, object> sent = JsonUtil.Object(JsonUtil.Deserialize(server.LastBody));
        Expect(JsonUtil.String(sent, "Source", "") == wireSource, label + " 的 Source 应为 " + wireSource + "，实际 " + JsonUtil.String(sent, "Source", ""));
        Expect(JsonUtil.String(sent, "Target", "") == wireTarget, label + " 的 Target 应为 " + wireTarget + "，实际 " + JsonUtil.String(sent, "Target", ""));
    }

    // ── 本地缓存：§7.3「重评不重复付费」──────────────────────────────────────
    private static void CacheSection()
    {
        server.Mode = "ok";
        WriteConfig(server.Url, "en", "zh-CN", true, 200);
        ClearCache();
        string cachePath = Path.Combine(dataRoot, "translation-cache.json");
        List<string> texts = new List<string> { "CACHE PROBE ONE", "CACHE PROBE TWO", "CACHE PROBE THREE" };

        int before = server.Requests;
        Dictionary<string, object> first = Broker(Texts(texts), "translate");
        Expect(Ok(first), "首次调用成功，实际 " + JsonUtil.String(first, "error", ""));
        Expect(server.Requests - before == 1, "首次调用发一次请求");
        Expect(File.Exists(cachePath), "翻译缓存已落盘：" + cachePath);

        before = server.Requests;
        Dictionary<string, object> second = Broker(Texts(texts), "translate");
        Expect(Ok(second), "第二次调用成功（缓存命中不该把翻译弄坏）");
        Expect(server.Requests == before, "全部命中缓存 ⇒ 0 次请求（实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次）");
        Dictionary<string, object> usage = JsonUtil.Object(JsonUtil.Get(Output(second), "usage"));
        Expect(JsonUtil.Int(usage, "cached", -1) == 3, "usage.cached=3");
        Expect(JsonUtil.Int(usage, "requests", -1) == 0, "usage.requests=0");
        Expect(Translations(second)[0] == Translations(first)[0], "命中缓存的译文与第一次完全一致");

        // 同一批标题换目标语言 ⇒ 缓存键含语言对，必须重新翻译（不许串味）。
        before = server.Requests;
        Dictionary<string, object> other = Broker(BrokerInput(texts, "en", "ja"), "translate");
        Expect(server.Requests - before == 1, "换目标语言 ⇒ 缓存不串味，重新发一次请求");
        Expect(Translations(other)[0] == "[ja]CACHE PROBE ONE", "换语言后的译文是 ja 的结果，实际 " + Translations(other)[0]);

        // 换文本 ⇒ 必须重新翻译。
        before = server.Requests;
        Dictionary<string, object> fresh = Broker(Texts(new List<string> { "CACHE PROBE FOUR" }), "translate");
        Expect(server.Requests - before == 1, "新标题 ⇒ 重新发一次请求");
        Expect(Ok(fresh), "新标题翻译成功");

        // 缓存文件损坏：不许把翻译带崩，也不许让缓存**永久**失效（否则就要反复付费）。
        File.WriteAllText(cachePath, "{ not json at all", RuntimeUtil.Utf8NoBom);
        before = server.Requests;
        Dictionary<string, object> healed = Broker(Texts(texts), "translate");
        Expect(Ok(healed), "缓存文件损坏时翻译仍然成功");
        Expect(server.Requests - before == 1, "坏缓存 ⇒ 当没有缓存，重新发一次请求");
        Expect(File.Exists(cachePath + ".corrupt"), "坏缓存被挪成 .corrupt 保留现场");
        Expect(Translations(healed)[0] == "[zh]CACHE PROBE ONE", "坏缓存之后仍然拿到正确译文");
        // 挪走坏文件的收益就在这一步：下一次调用能重新命中缓存，而不是永远重翻（永远付费）。
        before = server.Requests;
        Dictionary<string, object> recovered = Broker(Texts(texts), "translate");
        Expect(Ok(recovered), "重建缓存后翻译成功");
        Expect(server.Requests == before, "重建之后缓存恢复生效（缓存没有永久失效）");

        // 关掉缓存 ⇒ 不读也不写：每次调用都真的打接口。
        WriteConfig(server.Url, "en", "zh-CN", false, 200);
        before = server.Requests;
        Dictionary<string, object> noCache = Broker(Texts(texts), "translate");
        Expect(Ok(noCache), "关缓存时翻译成功");
        Expect(server.Requests - before == 1, "关缓存 ⇒ 每次都发请求");
        Expect(JsonUtil.Int(JsonUtil.Object(JsonUtil.Get(Output(noCache), "usage")), "cached", -1) == 0, "关缓存时 usage.cached=0");
        WriteConfig(server.Url, "en", "zh-CN", true, 200);
    }

    // ── 内部分批：一次调用 40k 字符，必须按 1800 预算切开并节流 ────────────────
    private static void SplitSection()
    {
        server.Mode = "ok";
        WriteConfig(server.Url, "en", "zh-CN", false, 200);
        List<string> texts = new List<string>();
        for (int index = 0; index < 4; index++) texts.Add(new string((char)('A' + index), 1500)); // 4×1500 = 6000 字符
        int before = server.Requests;
        Dictionary<string, object> response = Broker(Texts(texts), "translate");
        Expect(Ok(response), "6000 字符的批量调用成功，实际 " + JsonUtil.String(response, "error", ""));
        Expect(server.Requests - before == 4, "按 1800 字符预算切成 4 批（实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 批）");
        List<object> translations = JsonUtil.Array(JsonUtil.Get(Output(response), "translations"));
        Expect(translations.Count == 4, "内部分批之后仍然返回等长数组（消费方不必知道分批）");
        Expect(ToStringValue(translations, 3) == "[zh]" + texts[3], "内部分批之后顺序不乱（第 4 条）");
        Dictionary<string, object> usage = JsonUtil.Object(JsonUtil.Get(Output(response), "usage"));
        Expect(JsonUtil.Int(usage, "requests", -1) == 4, "usage.requests=4（消费方据此知道花了 4 次额度）");

        // 节流：相邻请求起点至少 250ms（默认 5 次/秒）。不节流的话一次大批量必然换来一片限流。
        List<DateTime> starts = server.Starts;
        Expect(starts.Count >= 4, "假端点收到了足够多的请求用于测节流");
        long minimum = long.MaxValue;
        for (int index = starts.Count - 4; index < starts.Count; index++)
        {
            if (index == 0) continue;
            long gap = (long)(starts[index] - starts[index - 1]).TotalMilliseconds;
            if (gap < minimum) minimum = gap;
        }
        Expect(minimum >= 230, "相邻请求按 250ms 节流（实测最小间隔 " + minimum.ToString(CultureInfo.InvariantCulture) + "ms）");

        // 长度不一致 ⇒ 拒绝这一批，绝不让消费方拿到错位的短数组（§7.3）。
        server.Mode = "count_mismatch";
        server.TargetCountOverride = 1;
        Dictionary<string, object> mismatch = Broker(Texts(new List<string> { "MISMATCH A", "MISMATCH B" }), "translate");
        Expect(!Ok(mismatch), "返回条数与请求条数不一致 ⇒ ok:false");
        Expect(JsonUtil.String(mismatch, "error_kind", "") == "provider_error", "长度不一致 ⇒ error_kind=provider_error");
        Expect(!JsonUtil.Bool(mismatch, "fatal", true), "长度不一致不是 fatal（换个时机重试可能有结果）");
        Expect(JsonUtil.String(mismatch, "error", "").IndexOf("长度不一致", StringComparison.Ordinal) >= 0, "文案点名长度不一致：" + JsonUtil.String(mismatch, "error", ""));
        server.TargetCountOverride = -1;
        server.Mode = "ok";
    }

    // ── 源语言 = 目标语言：翻译没有意义，一分钱都不许花 ───────────────────────
    private static void SameLanguageSection()
    {
        server.Mode = "ok";
        WriteConfig(server.Url, "en", "zh-CN", false, 200);
        int before = server.Requests;
        Dictionary<string, object> input = BrokerInput(new List<string> { "ALREADY CHINESE" }, "zh", "zh-CN");
        Dictionary<string, object> response = Broker(input, "translate");
        Expect(Ok(response), "源=目标时调用成功（原样返回），实际 " + JsonUtil.String(response, "error", ""));
        Expect(server.Requests == before, "源=目标 ⇒ 一次请求都不发（实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次）");
        Expect(Translations(response)[0] == "ALREADY CHINESE", "源=目标时译文就是原文");
        Dictionary<string, object> usage = JsonUtil.Object(JsonUtil.Get(Output(response), "usage"));
        Expect(JsonUtil.Int(usage, "requests", -1) == 0 && JsonUtil.Int(usage, "translated_chars", -1) == 0, "源=目标时 usage 全零");
        Expect(JsonUtil.Array(JsonUtil.Get(Output(response), "notes")).Count > 0, "源=目标时给出说明，让消费方知道这次没花钱");
    }

    // ── 失败语义：厂商错误码分级 + HTTP 层 + 坏响应 ───────────────────────────
    private static void ErrorSection()
    {
        WriteConfig(server.Url, "en", "zh-CN", false, 200);
        List<string> texts = new List<string> { "ERROR PROBE" };

        // 致命：重试没有意义，消费方要立即中止整轮（不要对每一批白跑一遍）。
        ExpectCodeFailure("签名失败", "AuthFailure.SignatureFailure", true, "签名");
        ExpectCodeFailure("Secret ID 不存在", "AuthFailure.SecretIdNotFound", true, "Secret ID");
        ExpectCodeFailure("签名过期", "AuthFailure.SignatureExpire", true, "系统时间");
        ExpectCodeFailure("免费额度用完", "FailedOperation.NoFreeAmount", true, "免费额度");
        ExpectCodeFailure("账号欠费停服", "FailedOperation.ServiceIsolate", true, "欠费");
        ExpectCodeFailure("未开通机器翻译", "FailedOperation.UserNotRegistered", true, "开通");
        ExpectCodeFailure("CAM 未授权", "UnauthorizedOperation.ActionNotFound", true, "控制台");
        ExpectCodeFailure("目标语言不支持", "UnsupportedOperation.UnSupportedTargetLanguage", true, "目标语言");
        // 非致命且不该重试：文本超长是插件内部分批的问题，重试只会重复同样的错误。
        ExpectCodeFailure("文本超长", "UnsupportedOperation.TextTooLong", false, "2000");

        // 可重试：限流先失败一次再成功 ⇒ 内部重试后应当成功，消费方无感。
        server.ErrorCode = "RequestLimitExceeded.UinLimitExceeded";
        server.FailuresBeforeSuccess = 1;
        int before = server.Requests;
        server.Mode = "transient_code";
        Dictionary<string, object> recovered = Broker(Texts(texts), "translate");
        Expect(Ok(recovered), "限流一次后重试成功，实际 " + JsonUtil.String(recovered, "error", ""));
        Expect(server.Requests - before == 2, "限流时内部重试一次（实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次请求）");
        server.FailuresBeforeSuccess = 0;

        // HTTP 5xx 也可重试。
        server.FailuresBeforeSuccess = 1;
        before = server.Requests;
        server.Mode = "transient_http";
        Dictionary<string, object> httpRecovered = Broker(Texts(new List<string> { "HTTP RETRY PROBE" }), "translate");
        Expect(Ok(httpRecovered), "HTTP 503 一次后重试成功，实际 " + JsonUtil.String(httpRecovered, "error", ""));
        Expect(server.Requests - before == 2, "HTTP 503 内部重试一次");
        server.FailuresBeforeSuccess = 0;

        // 一直内部错误 ⇒ 3 次重试后放弃，但**不是** fatal（记 warning 后继续）。
        server.ErrorCode = "InternalError.ErrorGetRoute";
        before = server.Requests;
        server.Mode = "error_code";
        Dictionary<string, object> exhausted = Broker(Texts(new List<string> { "EXHAUSTED PROBE" }), "translate");
        Expect(!Ok(exhausted), "持续 InternalError ⇒ ok:false");
        Expect(JsonUtil.String(exhausted, "error_kind", "") == "provider_error", "持续 InternalError ⇒ error_kind=provider_error");
        Expect(!JsonUtil.Bool(exhausted, "fatal", true), "持续 InternalError ⇒ fatal:false（可记 warning 继续下一批）");
        Expect(server.Requests - before == 4, "重试上限 3 次（共 4 次请求，实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次）");
        server.ErrorCode = "";

        // HTTP 层：401/403 是被拒绝（致命，不重试）；429/5xx 可重试。
        server.ErrorHttpStatus = 401;
        before = server.Requests;
        server.Mode = "http";
        Dictionary<string, object> denied = Broker(Texts(texts), "translate");
        Expect(!Ok(denied) && JsonUtil.Bool(denied, "fatal", false), "HTTP 401 ⇒ fatal:true");
        Expect(server.Requests - before == 1, "HTTP 401 不重试");
        Expect(JsonUtil.String(denied, "error", "").IndexOf("HTTP 401", StringComparison.Ordinal) >= 0, "HTTP 401 文案带状态码：" + JsonUtil.String(denied, "error", ""));
        server.ErrorHttpStatus = 0;

        // 坏响应：可读中文，别把原始 JSON 砸到界面上。
        ExpectBrokenResponse("坏 JSON", "bad_json", "JSON");
        ExpectBrokenResponse("没有 Response 对象", "no_response", "Response");
        ExpectBrokenResponse("缺 TargetTextList", "missing_targets", "TargetTextList");
        server.Mode = "ok";
    }

    private static void ExpectCodeFailure(string label, string code, bool fatal, string keyword)
    {
        server.ErrorCode = code;
        int before = server.Requests;
        server.Mode = "error_code";
        Dictionary<string, object> response = Broker(Texts(new List<string> { "CODE PROBE " + label }), "translate");
        Expect(!Ok(response), label + "（" + code + "）⇒ ok:false");
        Expect(JsonUtil.String(response, "error_kind", "") == "provider_error", label + " ⇒ error_kind=provider_error");
        Expect(JsonUtil.Bool(response, "fatal", false) == fatal, label + " ⇒ fatal=" + fatal.ToString());
        Expect(JsonUtil.String(response, "error", "").IndexOf(keyword, StringComparison.Ordinal) >= 0, label + " 文案含「" + keyword + "」：" + JsonUtil.String(response, "error", ""));
        Expect(JsonUtil.String(response, "error", "").IndexOf(code, StringComparison.Ordinal) >= 0, label + " 文案带原始错误码（便于用户搜文档）");
        Expect(JsonUtil.Get(response, "output") == null, label + " 失败时 output 为空");
        if (fatal) Expect(server.Requests - before == 1, label + " 是致命错误 ⇒ 不重试（只请求一次）");
        server.ErrorCode = "";
        server.Mode = "ok";
    }

    private static void ExpectBrokenResponse(string label, string mode, string keyword)
    {
        int before = server.Requests;
        server.Mode = mode;
        Dictionary<string, object> response = Broker(Texts(new List<string> { "BROKEN PROBE" }), "translate");
        Expect(!Ok(response), label + " ⇒ ok:false");
        Expect(!JsonUtil.Bool(response, "fatal", true), label + " 不是 fatal（服务端偶发抽风不该中止整轮）");
        Expect(JsonUtil.String(response, "error", "").IndexOf(keyword, StringComparison.Ordinal) >= 0, label + " 文案点名 " + keyword + "：" + JsonUtil.String(response, "error", ""));
        Expect(server.Requests - before == 1, label + " 不重试（重试也还是坏响应）");
        server.Mode = "ok";
    }

    // ── 输入校验：非法输入必须 0 次 API 调用（这是"最贵的 bug"）───────────────
    private static void InputSection()
    {
        server.Mode = "ok";
        WriteConfig(server.Url, "en", "zh-CN", false, 200);
        WriteSecret(true);
        int before = server.Requests;

        ExpectRejected("缺 texts", Broker(new Dictionary<string, object>(), "translate"), "texts");
        ExpectRejected("texts 不是数组", Broker(new Dictionary<string, object> { { "texts", "nope" } }, "translate"), "texts");
        ExpectRejected("texts 空数组", Broker(Texts(new List<string>()), "translate"), "非空");
        ExpectRejected("项不是字符串", Broker(new Dictionary<string, object> { { "texts", new List<object> { 1, 2 } } }, "translate"), "字符串");
        ExpectRejected("项是空字符串", Broker(new Dictionary<string, object> { { "texts", new List<object> { "   " } } }, "translate"), "空字符串");
        ExpectRejected("单条超过 2000 字符", Broker(Texts(new List<string> { new string('a', 2001) }), "translate"), "2000");

        // 条数超过 max_texts_per_call（设置里可调）。
        WriteConfig(server.Url, "en", "zh-CN", false, 3);
        List<string> four = new List<string> { "a1", "a2", "a3", "a4" };
        ExpectRejected("条数超过单次上限", Broker(Texts(four), "translate"), "超过单次调用上限");
        WriteConfig(server.Url, "en", "zh-CN", false, 200);

        // 合计超过 40000 字符：要求调用方自己分批，别让一次调用跑到超时。
        List<string> huge = new List<string>();
        for (int index = 0; index < 21; index++) huge.Add(new string('b', 2000));
        ExpectRejected("合计超过 40000 字符", Broker(Texts(huge), "translate"), "40000");

        Expect(server.Requests == before, "输入非法时一次 API 都不发（实际多发 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次）");

        // 语言非法 ⇒ fatal（改设置才有用），同样一次请求都不发。
        ExpectProviderFailure("目标语言不支持", Broker(BrokerInput(new List<string> { "x" }, "en", "klingon"), "translate"), true, "不受腾讯云机器翻译支持");
        ExpectProviderFailure("目标语言不能是 auto", Broker(BrokerInput(new List<string> { "x" }, "en", "auto"), "translate"), true, "auto");
        ExpectProviderFailure("源语言不支持", Broker(BrokerInput(new List<string> { "x" }, "klingon", "zh"), "translate"), true, "源语言");
        Expect(server.Requests == before, "语言非法时一次 API 都不发");

        // 没填密钥 ⇒ fatal，一次请求都不发（否则每批都白跑一次）。
        WriteSecret(false);
        ExpectProviderFailure("未配置密钥", Broker(Texts(new List<string> { "x" }), "translate"), true, "Secret");
        Expect(server.Requests == before, "未配置密钥时一次 API 都不发");
        WriteSecret(true);

        // 未知 action。
        Dictionary<string, object> unknown = Broker(Texts(new List<string> { "x" }), "translate_all");
        Expect(!Ok(unknown), "未知 action ⇒ ok:false");
        Expect(JsonUtil.String(unknown, "error", "").IndexOf("action", StringComparison.Ordinal) >= 0, "未知 action 文案可读：" + JsonUtil.String(unknown, "error", ""));
        Expect(server.Requests == before, "未知 action 一次 API 都不发");
    }

    private static void ExpectRejected(string label, Dictionary<string, object> response, string field)
    {
        Expect(!Ok(response), label + " ⇒ ok:false");
        Expect(JsonUtil.String(response, "error_kind", "") == "provider_error", label + " ⇒ error_kind=provider_error");
        Expect(JsonUtil.String(response, "error", "").IndexOf(field, StringComparison.Ordinal) >= 0, label + " 文案点名 " + field + "：" + JsonUtil.String(response, "error", ""));
    }

    private static void ExpectProviderFailure(string label, Dictionary<string, object> response, bool fatal, string keyword)
    {
        Expect(!Ok(response), label + " ⇒ ok:false");
        Expect(JsonUtil.String(response, "error_kind", "") == "provider_error", label + " ⇒ error_kind=provider_error");
        Expect(JsonUtil.Bool(response, "fatal", false) == fatal, label + " ⇒ fatal=" + fatal.ToString());
        Expect(JsonUtil.String(response, "error", "").IndexOf(keyword, StringComparison.Ordinal) >= 0, label + " 文案含「" + keyword + "」：" + JsonUtil.String(response, "error", ""));
    }

    // ── PluginAction：设置页 [测试连接] / validate_settings ─────────────────────
    private static void ActionSection()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwtt-act-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "in.json");
        JsonUtil.SaveAtomic(input, new Dictionary<string, object>());

        server.Mode = "ok";
        WriteConfig(server.Url, "en", "zh-CN", true, 200);
        WriteSecret(true);
        // [测试连接] 刻意**不走缓存**：按钮的意义就是真的打一次接口，缓存命中会让用户以为密钥是对的。
        int before = server.Requests;
        string output = Path.Combine(dir, "ok.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") == 0, "test_connection 成功退出 0");
        Dictionary<string, object> payload = Payload(output);
        Expect(JsonUtil.String(payload, "message", "") == "翻译服务可用", "test_connection 文案 = 翻译服务可用");
        Dictionary<string, object> sample = JsonUtil.Object(JsonUtil.Get(payload, "sample"));
        Expect(JsonUtil.String(sample, "text", "") == "translation service check", "test_connection 用固定探针句");
        Expect(JsonUtil.String(sample, "translation", "").IndexOf("translation service check", StringComparison.Ordinal) >= 0, "test_connection 回显真实译文：" + JsonUtil.String(sample, "translation", ""));
        Expect(JsonUtil.String(payload, "region", "") == "ap-guangzhou", "test_connection 回报 region");
        Dictionary<string, object> limits = JsonUtil.Object(JsonUtil.Get(payload, "limits"));
        Expect(JsonUtil.Int(limits, "max_texts_per_call", -1) == 200, "test_connection 回报 limits.max_texts_per_call");
        Expect(JsonUtil.Int(limits, "request_interval_ms", -1) == 250, "test_connection 回报节流间隔");
        Expect(server.Requests - before == 1, "test_connection 只发一次请求");
        output = Path.Combine(dir, "ok2.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") == 0, "test_connection 第二次也成功");
        Expect(server.Requests - before == 2, "test_connection 不走缓存（第二次仍然真的打了接口）");

        // 额度用完时 [测试连接] 必须如实报错，不能假装成功。
        server.ErrorCode = "FailedOperation.NoFreeAmount";
        server.Mode = "error_code";
        output = Path.Combine(dir, "paid.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") != 0, "免费额度用完 ⇒ 非零退出");
        Dictionary<string, object> failed = JsonUtil.LoadObject(output);
        Expect(JsonUtil.String(failed, "error", "").IndexOf("免费额度", StringComparison.Ordinal) >= 0, "免费额度用完的文案可读");
        Expect(JsonUtil.Bool(JsonUtil.Object(JsonUtil.Get(failed, "payload")), "fatal", false), "免费额度用完带 payload.fatal（设置页可以直接高亮）");
        server.ErrorCode = "";
        server.Mode = "ok";

        // validate_settings：宿主保存设置后会跑它（退出码非 0 会回滚设置）。
        output = Path.Combine(dir, "validate.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") == 0, "validate_settings 成功");
        payload = Payload(output);
        Expect(JsonUtil.String(payload, "endpoint", "") == server.Url, "validate_settings 回报实际生效的接口地址");
        Expect(JsonUtil.String(payload, "source_language", "") == "en" && JsonUtil.String(payload, "target_language", "") == "zh", "validate_settings 回报归一化后的语言");
        Expect(JsonUtil.Bool(payload, "cache_enabled", false), "validate_settings 回报缓存开关");
        Expect(JsonUtil.Int(payload, "cache_entries", -1) > 0, "validate_settings 回报缓存条数");
        Expect(JsonUtil.Int(JsonUtil.Object(JsonUtil.Get(payload, "limits")), "timeout_seconds", -1) == 30, "validate_settings 回报 timeout_seconds");

        // 没填密钥也必须能保存（否则用户没法先改语言再填密钥）。
        WriteSecret(false);
        output = Path.Combine(dir, "validate-nokey.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") == 0, "未配密钥时 validate_settings 仍应放行");
        WriteSecret(true);

        // 地址非法 ⇒ 拒绝保存；地址缺 scheme ⇒ 自动补 https 并如实回报。
        WriteConfig("not a url", "en", "zh-CN", true, 200);
        output = Path.Combine(dir, "badurl.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") != 0, "非法接口地址 ⇒ 非零退出");
        Expect(JsonUtil.String(JsonUtil.LoadObject(output), "error", "").IndexOf("http(s)", StringComparison.Ordinal) >= 0, "非法地址文案可读");
        WriteConfig("127.0.0.1:1", "en", "zh-CN", true, 200);
        output = Path.Combine(dir, "scheme.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") == 0, "缺 scheme 的地址仍然可以保存");
        Expect(JsonUtil.String(Payload(output), "endpoint", "") == "https://127.0.0.1:1", "缺 scheme 时自动补 https 并回报实际值");

        // 源语言 auto 要给出提示（短标题语种识别可能出错），但不阻止保存。
        WriteConfig(server.Url, "auto", "zh-CN", true, 200);
        output = Path.Combine(dir, "auto.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") == 0, "源语言 auto 可以保存");
        Expect(JsonUtil.Array(JsonUtil.Get(Payload(output), "notes")).Count > 0, "源语言 auto 时给出提示");
        WriteConfig(server.Url, "en", "zh-CN", true, 200);

        // 审计：service-call.log 记了 consumer|provider|service|action|billing，且不含正文与密钥。
        string auditPath = Path.Combine(PluginPaths.Logs, "service-call.log");
        string audit = File.Exists(auditPath) ? File.ReadAllText(auditPath, Encoding.UTF8) : "";
        Expect(audit.IndexOf(Consumer + " | " + PluginId + " | " + Service, StringComparison.Ordinal) >= 0, "审计日志记录了 consumer|provider|service");
        Expect(audit.IndexOf("| translate |", StringComparison.Ordinal) >= 0, "审计日志记录了 action");
        Expect(audit.IndexOf("may_charge", StringComparison.Ordinal) >= 0, "审计日志带 billing=may_charge");
        Expect(audit.IndexOf("input_keys=", StringComparison.Ordinal) >= 0, "审计日志只记 input 字段数与字节数");
        Expect(audit.IndexOf("NEURAL DOMAIN ADAPTATION", StringComparison.Ordinal) < 0, "审计日志不含待翻译的正文");
        Expect(audit.IndexOf(SecretKey, StringComparison.Ordinal) < 0, "审计日志不含 Secret Key");
        Expect(audit.IndexOf(SecretId, StringComparison.Ordinal) < 0, "审计日志不含 Secret ID");
        try { Directory.Delete(dir, true); } catch { }
    }

    private static Dictionary<string, object> Payload(string outputPath)
    {
        if (!File.Exists(outputPath)) return new Dictionary<string, object>();
        return JsonUtil.Object(JsonUtil.Get(JsonUtil.LoadObject(outputPath), "payload"));
    }

    // ── 夹具 ─────────────────────────────────────────────────────────────────
    private static Dictionary<string, object> Texts(List<string> texts)
    {
        return BrokerInput(texts, "", "");
    }

    private static Dictionary<string, object> BrokerInput(List<string> texts, string source, string target)
    {
        List<object> wire = new List<object>();
        foreach (string text in texts) wire.Add(text);
        Dictionary<string, object> input = new Dictionary<string, object>();
        input["texts"] = wire;
        if (source != "") input["source_language"] = source;
        if (target != "") input["target_language"] = target;
        return input;
    }

    private static bool Ok(Dictionary<string, object> response)
    {
        return JsonUtil.Bool(response, "ok", false);
    }

    private static Dictionary<string, object> Output(Dictionary<string, object> response)
    {
        return JsonUtil.Object(JsonUtil.Get(response, "output"));
    }

    private static List<string> Translations(Dictionary<string, object> response)
    {
        List<string> result = new List<string>();
        foreach (object item in JsonUtil.Array(JsonUtil.Get(Output(response), "translations"))) result.Add(item as string ?? "");
        return result;
    }

    // 越界时返回空串：断言失败要报"值不对"，而不是让探针自己抛异常把后面的检查全吞掉。
    private static string ToStringValue(List<object> list, int index)
    {
        return list != null && index >= 0 && index < list.Count ? (list[index] as string ?? "") : "";
    }

    private static void WriteConfig(string endpoint, string source, string target, bool useCache, int maxTexts)
    {
        Directory.CreateDirectory(dataRoot);
        JsonUtil.SaveAtomic(Path.Combine(dataRoot, "config.json"), new Dictionary<string, object>{
            {"api_endpoint", endpoint}, {"region", "ap-guangzhou"},
            {"source_language", source}, {"target_language", target},
            {"use_local_cache", useCache}, {"timeout_seconds", 30}, {"max_texts_per_call", maxTexts}});
    }

    // 密钥只进 secret.dat（DPAPI），config.json 里不落明文。
    private static void WriteSecret(bool withCredentials)
    {
        Directory.CreateDirectory(dataRoot);
        JsonUtil.WriteDpapiJson(Path.Combine(dataRoot, "secret.dat"), new Dictionary<string, object>{
            {"secret_id", withCredentials ? SecretId : ""}, {"secret_key", withCredentials ? SecretKey : ""}});
    }

    private static void ClearCache()
    {
        try { File.Delete(Path.Combine(dataRoot, "translation-cache.json")); } catch { }
        try { File.Delete(Path.Combine(dataRoot, "translation-cache.json.corrupt")); } catch { }
    }

    // 以一个未安装的 consumer 身份直接调用 Broker（§4.4：caller 身份只认环境变量）。
    private static Dictionary<string, object> Broker(Dictionary<string, object> input, string action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwtt-broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string requestPath = Path.Combine(dir, "request.json"), outputPath = Path.Combine(dir, "output.json");
        JsonUtil.SaveAtomic(requestPath, new Dictionary<string, object>{
            {"protocol", 1}, {"request_id", "tt-" + Guid.NewGuid().ToString("N").Substring(0, 8)},
            {"service", Service}, {"action", action}, {"input", input}, {"timeout_seconds", 300}});
        ProcessStartInfo info = new ProcessStartInfo(pluginHost, "-Mode ServiceCall -RequestFile \"" + requestPath + "\" -OutputFile \"" + outputPath + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        Dictionary<string, string> extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        extra["RW_PLUGIN_ID"] = Consumer; extra["RW_PLUGIN_JOB_ID"] = ""; extra["RW_PLUGIN_HOST_EXE"] = pluginHost;
        PluginRuntime.ApplyPluginEnvironment(info, null, null, extra);
        using (Process broker = Process.Start(info))
        {
            broker.StandardOutput.ReadToEnd();
            broker.StandardError.ReadToEnd();
            broker.WaitForExit();
        }
        Dictionary<string, object> response = File.Exists(outputPath) ? JsonUtil.LoadObject(outputPath) : new Dictionary<string, object>();
        try { Directory.Delete(dir, true); } catch { }
        return response;
    }

    private static int RunHost(string arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo(pluginHost, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        PluginRuntime.ApplyPluginEnvironment(info, null, null, null);
        using (Process process = Process.Start(info))
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static void Expect(bool condition, string label)
    {
        checks++;
        if (!condition) { failures++; Console.Out.WriteLine("FAIL: " + label); }
    }

    private static int Report()
    {
        Console.Out.WriteLine("TranslateTencentProbe: checks=" + checks.ToString(CultureInfo.InvariantCulture) + " failures=" + failures.ToString(CultureInfo.InvariantCulture));
        Console.Out.WriteLine(failures == 0 ? "PASS translate tencent probe" : "FAIL translate tencent probe");
        return failures == 0 ? 0 : 1;
    }

    // ── TC3-HMAC-SHA256：探针侧的**独立**实现 ────────────────────────────────
    // 故意不复用插件的代码（插件用的是 RuntimeUtil），这样"两边算出来一样"才有意义：
    // 签名错在线上只换回一个 SignatureFailure，靠上线试是查不出来的。
    private static string ExpectedAuthorization(string host, string contentType, string action, string body, long timestamp)
    {
        string payloadHash = Sha256Hex(body);
        string canonicalHeaders = "content-type:" + contentType + "\n" + "host:" + host + "\n" + "x-tc-action:" + action.ToLowerInvariant() + "\n";
        string canonicalRequest = "POST" + "\n" + "/" + "\n" + "" + "\n" + canonicalHeaders + "\n" + SignedHeaders + "\n" + payloadHash;
        string date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string stringToSign = "TC3-HMAC-SHA256" + "\n" + timestamp.ToString(CultureInfo.InvariantCulture) + "\n" + date + "/tmt/tc3_request" + "\n" + Sha256Hex(canonicalRequest);
        byte[] kDate = Hmac(Encoding.UTF8.GetBytes("TC3" + SecretKey), date);
        byte[] kService = Hmac(kDate, "tmt");
        byte[] kSigning = Hmac(kService, "tc3_request");
        string signature = Hex(Hmac(kSigning, stringToSign));
        return "TC3-HMAC-SHA256 Credential=" + SecretId + "/" + date + "/tmt/tc3_request, SignedHeaders=" + SignedHeaders + ", Signature=" + signature;
    }

    private static byte[] Hmac(byte[] key, string message)
    {
        using (HMACSHA256 hmac = new HMACSHA256(key)) return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
    }

    private static string Sha256Hex(string text)
    {
        using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private static string Hex(byte[] value)
    {
        StringBuilder builder = new StringBuilder(value.Length * 2);
        foreach (byte item in value) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static long NowSeconds()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }
}

// 最小腾讯云机器翻译（TMT）端点：只实现 POST /，够驱动真实插件。
// 请求体里是中文标题（UTF-8），响应也按 UTF-8 字节数算 Content-Length。
internal sealed class FakeTencentTmt : IDisposable
{
    private TcpListener listener;
    private Thread accept;
    private volatile bool running = true;
    private readonly object gate = new object();
    private int requests;
    private int modeRequests;
    private volatile string mode = "ok";
    private readonly List<DateTime> starts = new List<DateTime>();

    // 切换模式时把"本模式的请求数"清零 ⇒ transient 可以表达"先失败 N 次再成功"。
    public string Mode
    {
        get { return mode; }
        set { lock (gate) { modeRequests = 0; mode = value; } }
    }

    public volatile int FailuresBeforeSuccess;
    public string ErrorCode = "";
    public int ErrorHttpStatus = 200;
    public int TargetCountOverride = -1;

    public string LastBody = "", LastAuthorization = "", LastPath = "", LastHost = "";
    public string LastAction = "", LastVersion = "", LastRegion = "", LastTimestamp = "", LastContentType = "", LastAccept = "";
    public readonly Dictionary<string, string> LastHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int Requests { get { lock (gate) return requests; } }
    public List<DateTime> Starts { get { lock (gate) return new List<DateTime>(starts); } }
    public int Port { get; private set; }
    public string Url { get { return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture); } }

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accept = new Thread(AcceptLoop);
        accept.IsBackground = true;
        accept.Start();
    }

    public void Dispose()
    {
        running = false;
        try { listener.Stop(); } catch { }
    }

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }
            Thread worker = new Thread(delegate() { Serve(client); });
            worker.IsBackground = true;
            worker.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                string headerBlock = ReadHead(stream);
                string[] lines = headerBlock.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                LastPath = lines.Length > 0 ? PathOf(lines[0]) : "";
                LastAuthorization = Header(headerBlock, "Authorization");
                LastHost = Header(headerBlock, "Host");
                LastAction = Header(headerBlock, "X-TC-Action");
                LastVersion = Header(headerBlock, "X-TC-Version");
                LastRegion = Header(headerBlock, "X-TC-Region");
                LastTimestamp = Header(headerBlock, "X-TC-Timestamp");
                LastContentType = Header(headerBlock, "Content-Type");
                LastAccept = Header(headerBlock, "Accept");
                lock (gate)
                {
                    LastHeaders.Clear();
                    foreach (string line in lines)
                    {
                        int colon = line.IndexOf(':');
                        if (colon > 0) LastHeaders[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                    }
                }
                // .NET 的 HttpWebRequest 默认 Expect: 100-continue —— 必须先回 100，否则
                // 客户端不会发正文（会白等 350ms 才自行放行）。
                if (Header(headerBlock, "Expect").IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    byte[] interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    stream.Write(interim, 0, interim.Length);
                    stream.Flush();
                }
                int length;
                Int32.TryParse(Header(headerBlock, "Content-Length"), out length);
                byte[] body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int chunk = stream.Read(body, read, length - read);
                    if (chunk <= 0) break;
                    read += chunk;
                }
                LastBody = Encoding.UTF8.GetString(body, 0, read);

                int attempt;
                lock (gate) { requests++; attempt = ++modeRequests; starts.Add(DateTime.Now); }
                int status;
                byte[] bytes = Encoding.UTF8.GetBytes(Respond(attempt, out status));
                string response = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + Reason(status) + "\r\n"
                    + "Content-Type: application/json; charset=utf-8\r\n"
                    + "Content-Length: " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                    + "Connection: close\r\n\r\n";
                byte[] header = Encoding.ASCII.GetBytes(response);
                stream.Write(header, 0, header.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch { }
    }

    private string Respond(int attempt, out int status)
    {
        string current, code;
        int mismatch;
        lock (gate) { current = mode; code = ErrorCode; mismatch = TargetCountOverride; }

        if (current == "transient_http")
        {
            if (attempt <= FailuresBeforeSuccess) { status = 503; return "{\"message\":\"Service Unavailable\"}"; }
            current = "ok";
        }
        if (current == "transient_code")
        {
            if (attempt <= FailuresBeforeSuccess)
            {
                status = 200;
                return ErrorEnvelope(code, "请求过于频繁");
            }
            current = "ok";
        }
        if (current == "http")
        {
            status = ErrorHttpStatus == 0 ? 500 : ErrorHttpStatus;
            return "{\"message\":\"upstream rejected\"}";
        }
        if (current == "error_code") { status = 200; return ErrorEnvelope(code, "厂商返回的错误"); }
        if (current == "bad_json") { status = 200; return "this is not json at all"; }
        if (current == "no_response") { status = 200; return "{\"foo\":1}"; }
        if (current == "missing_targets") { status = 200; return "{\"Response\":{\"RequestId\":\"probe\"}}"; }
        status = 200;
        return SuccessEnvelope(mismatch);
    }

    private string SuccessEnvelope(int mismatch)
    {
        List<string> texts = new List<string>();
        string target = "?";
        try
        {
            Dictionary<string, object> sent = JsonUtil.Object(JsonUtil.Deserialize(LastBody));
            target = JsonUtil.String(sent, "Target", "?");
            foreach (object item in JsonUtil.Array(JsonUtil.Get(sent, "SourceTextList"))) texts.Add(item as string ?? "");
        }
        catch { }
        int count = mismatch >= 0 ? mismatch : texts.Count;
        StringBuilder builder = new StringBuilder("{\"Response\":{\"RequestId\":\"probe\",\"Source\":\"auto\",\"Target\":\"" + Escape(target) + "\",\"TargetTextList\":[");
        for (int index = 0; index < count; index++)
        {
            if (index > 0) builder.Append(',');
            string text = index < texts.Count ? texts[index] : "EXTRA";
            builder.Append('"').Append(Escape("[" + target + "]" + text)).Append('"');
        }
        return builder.Append("]}}").ToString();
    }

    private static string ErrorEnvelope(string code, string message)
    {
        return "{\"Response\":{\"RequestId\":\"probe\",\"Error\":{\"Code\":\"" + Escape(code) + "\",\"Message\":\"" + Escape(message) + "\"}}}";
    }

    private static string Escape(string value)
    {
        StringBuilder builder = new StringBuilder();
        foreach (char character in value ?? "")
        {
            if (character == '"' || character == '\\') builder.Append('\\').Append(character);
            else if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else builder.Append(character);
        }
        return builder.ToString();
    }

    // 只读到 \r\n\r\n 为止，返回不含结尾空行的头块。
    private static string ReadHead(NetworkStream stream)
    {
        StringBuilder builder = new StringBuilder();
        byte[] one = new byte[1];
        while (true)
        {
            int chunk = stream.Read(one, 0, 1);
            if (chunk <= 0) break;
            builder.Append((char)one[0]);
            int length = builder.Length;
            if (length >= 4 && builder[length - 4] == '\r' && builder[length - 3] == '\n' && builder[length - 2] == '\r' && builder[length - 1] == '\n')
                return builder.ToString(0, length - 4);
            if (length > 65536) break;
        }
        return builder.ToString();
    }

    private static string PathOf(string requestLine)
    {
        string[] parts = (requestLine ?? "").Split(' ');
        if (parts.Length < 2) return "";
        int query = parts[1].IndexOf('?');
        return query < 0 ? parts[1] : parts[1].Substring(0, query);
    }

    private static string Header(string block, string name)
    {
        foreach (string line in (block ?? "").Split(new string[] { "\r\n" }, StringSplitOptions.None))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (String.Equals(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase))
                return line.Substring(colon + 1).Trim();
        }
        return "";
    }

    private static string Reason(int status)
    {
        if (status == 200) return "OK";
        if (status == 401) return "Unauthorized";
        if (status == 503) return "Service Unavailable";
        return "Error";
    }
}
