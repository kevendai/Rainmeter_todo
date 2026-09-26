using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RainmeterBackend;

// DeepSeek AI Provider（ai_provider@1）的全链路离线探针（规格 §7.2 / §11）。
//
// 不依赖真实 DeepSeek：用 TcpListener 手写一个最小的 /chat/completions 端点，
// 让「consumer 请求 → Broker(-Mode ServiceCall) → ai-deepseek 插件 → 假 API」
// 整条链路真正跑起来（真 HTTP、真 DPAPI secret、真 error_kind）。规格 §12：CI 一律离线。
//
// 重点盯三件事：
//   ① OK 路径：messages 原样转发、json/usage 原样回传、请求体形态与 2.0.4 一致；
//   ② 失败语义：401/402/403 + 未配 Key ⇒ fatal:true 且**只请求一次**（不许重试）；
//      429/5xx ⇒ 插件内部重试，耗尽后 fatal:false；
//   ③ 输入非法时**一次 API 都不许发**（这是 2.0.4 那种「1400 次注定失败的请求」的预防）。
internal static class AiDeepSeekProbe
{
    private static string build, pluginHost, root;
    private static int checks, failures;
    private const string PluginId = "io.github.kevendai.ai-deepseek";
    private const string Service = "ai_provider@1";
    private const string Consumer = "io.github.test.ai-consumer";
    private const string Unbound = "io.github.test.ai-unbound";
    private const string ApiKey = "sk-probe-key";
    private static FakeDeepSeek server;

    private static int Main(string[] args)
    {
        build = AppDomain.CurrentDomain.BaseDirectory;
        pluginHost = Path.Combine(build, "PluginHost.exe");
        root = Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT") ?? "";
        Expect(File.Exists(pluginHost), "PluginHost.exe 在构建目录里");
        Expect(root != "", "RAINMETER_PLUGIN_ROOT 已设置");
        if (failures > 0) return Report();
        try
        {
            using (server = new FakeDeepSeek())
            {
                server.Start();
                ManifestSection();
                SetupSection();
                CompletionSection();
                InputSection();
                RetrySection();
                FatalSection();
                ActionSection();
            }
        }
        catch (Exception ex) { Expect(false, "探针自身不应抛异常：" + ex); }
        return Report();
    }

    // ── manifest：真实 plugin.json 按 v2.1 语义加载 ───────────────────────────
    private static void ManifestSection()
    {
        PluginManifest manifest = PluginRuntime.Resolve(PluginId, false);
        Expect(manifest.Provides.Count == 1 && manifest.Provides[0] == Service, "manifest 声明 provides=ai_provider@1");
        Expect(manifest.Billing == "may_charge", "billing=may_charge（可能收费，确认框要据此提示）");
        Expect(manifest.Capabilities.Count == 0, "纯 Provider：capabilities 为空也能加载");
        Expect(manifest.AddressTarget == "", "AI provider 不做地址代管（无 address_target）");
        Expect(manifest.MinHostVersion == "2.1.0", "min_host_version=2.1.0");
    }

    // ── 启用 Bootstrap 装好的插件、写入设置与凭据、验证绑定解析 ────────────────
    private static void SetupSection()
    {
        WritePluginConfig(server.Url, "deepseek-v4-flash", true);
        string current = Path.Combine(root, "Plugins", PluginId, "current.json");
        Dictionary<string, object> state = JsonUtil.LoadObject(current);
        state["enabled"] = true;
        JsonUtil.SaveAtomic(current, state);
        Expect(JsonUtil.Bool(JsonUtil.LoadObject(current), "enabled", false), "插件已启用");
        // ⚠️ 这个 plugin root 是**共享**的：同一轮套件里 ProviderProbe 已经装好了它的假 provider，
        // 那个假 provider 同样声明 ai_provider@1 且处于启用态 ⇒ 候选有两个，**不能**指望"唯一候选
        // 自动绑定"。按 §3/§8，多候选时必须由用户明确选择、宿主把选择写进 plugin-bindings.json；
        // 探针直接模拟这一步，然后验证绑定解析（这正是真实宿主的行为）。
        ServiceResolution unbound = ServiceRegistry.Resolve(Unbound, new ServiceUse { Service = Service, BindingKey = "ai_provider" }, false);
        Expect(!unbound.Available || unbound.ProviderId == PluginId, "§8：未绑定的 consumer 要么唯一候选解析到 ai-deepseek，要么因多候选而不可用");
        ServiceRegistry.SetBinding(Consumer, Service, PluginId);
        Expect(ServiceRegistry.ProviderFor(Consumer, Service) == PluginId, "显式绑定后解析到 ai-deepseek");
        Expect(ServiceRegistry.BillingOf(PluginId) == "may_charge", "宿主能读到该 provider 的 billing");
    }

    // api_key 只进 secret.dat（DPAPI），config.json 里不落明文。
    private static void WritePluginConfig(string url, string model, bool withKey)
    {
        string data = PluginPaths.DataRoot(PluginId);
        Directory.CreateDirectory(data);
        JsonUtil.SaveAtomic(Path.Combine(data, "config.json"), new Dictionary<string, object>{
            {"api_url", url}, {"api_model", model}, {"timeout_seconds", 60}, {"max_concurrency", 4}});
        JsonUtil.WriteDpapiJson(Path.Combine(data, "secret.dat"),
            new Dictionary<string, object>{{"api_key", withKey ? ApiKey : ""}});
    }

    // ── OK 路径：结构化补全 + 请求体形态 + usage ─────────────────────────────
    private static void CompletionSection()
    {
        server.Mode = "ok";
        int before = server.Requests;
        Dictionary<string, object> response = Broker(CompleteInput(), "structured_complete");
        Expect(JsonUtil.Bool(response, "ok", false), "structured_complete 成功，实际 " + JsonUtil.String(response, "error", ""));
        Expect(JsonUtil.String(response, "error_kind", "") == "", "成功时 error_kind 为空");
        Expect(server.Requests - before == 1, "成功路径只发一次请求");
        Expect(server.LastPath == "/chat/completions", "POST 到 /chat/completions，实际 " + server.LastPath);
        Expect(server.LastAuth == "Bearer " + ApiKey, "Authorization 用的是 secret.dat 里的 Key");

        Dictionary<string, object> output = Output(response);
        Dictionary<string, object> json = JsonUtil.Object(JsonUtil.Get(output, "json"));
        Dictionary<string, object> scores = JsonUtil.Object(JsonUtil.Get(json, "scores"));
        Expect(JsonUtil.Int(scores, "1", -1) == 9 && JsonUtil.Int(scores, "2", -1) == 7, "模型返回的 json 原样回传");
        Dictionary<string, object> usage = JsonUtil.Object(JsonUtil.Get(output, "usage"));
        Expect(JsonUtil.Int(usage, "prompt_tokens", -1) == 11 && JsonUtil.Int(usage, "completion_tokens", -1) == 22, "usage 原样回传");

        // 请求体形态必须和 2.0.4 的本体一致（搬过来不是重写）。
        Dictionary<string, object> sent = JsonUtil.Object(JsonUtil.Deserialize(server.LastBody));
        Expect(JsonUtil.String(sent, "model", "") == "deepseek-v4-flash", "model 取自设置");
        List<object> messages = JsonUtil.Array(JsonUtil.Get(sent, "messages"));
        Expect(messages.Count == 2, "请求体带两条消息");
        Expect(JsonUtil.String(At(messages, 0), "role", "") == "system", "消息顺序与 role 保序（system 在前）");
        Expect(JsonUtil.String(At(messages, 1), "content", "").IndexOf("PROBE_PAPER_LIST", StringComparison.Ordinal) >= 0, "user 消息内容原样转发（prompt 不被改写）");
        Expect(JsonUtil.String(JsonUtil.Object(JsonUtil.Get(sent, "thinking")), "type", "") == "enabled", "thinking 与 2.0.4 一致");
        Expect(JsonUtil.String(JsonUtil.Object(JsonUtil.Get(sent, "response_format")), "type", "") == "json_object", "response_format=json_object");
        Expect(!JsonUtil.Bool(sent, "stream", true), "stream=false");

        // response_schema 的 required 缺失 ⇒ 不该当成功（格式级检查，不是业务校验）。
        Dictionary<string, object> strict = CompleteInput();
        strict["response_schema"] = new Dictionary<string, object>{{"type", "object"}, {"required", new List<object>{"scores", "notes"}}};
        Dictionary<string, object> missing = Broker(strict, "structured_complete");
        Expect(!JsonUtil.Bool(missing, "ok", true), "缺 response_schema.required 字段 ⇒ ok:false");
        Expect(!JsonUtil.Bool(missing, "fatal", true), "缺字段不是 fatal（记 warning 即可）");
        Expect(JsonUtil.String(missing, "error", "").IndexOf("notes", StringComparison.Ordinal) >= 0, "点名缺了哪个字段：" + JsonUtil.String(missing, "error", ""));

        // usage 缺失 ⇒ 回零而不是报错。
        server.Mode = "missing_usage";
        Dictionary<string, object> noUsage = Output(Broker(CompleteInput(), "structured_complete"));
        Dictionary<string, object> zeroUsage = JsonUtil.Object(JsonUtil.Get(noUsage, "usage"));
        Expect(JsonUtil.Int(zeroUsage, "prompt_tokens", -1) == 0 && JsonUtil.Int(zeroUsage, "completion_tokens", -1) == 0, "usage 缺失时回零");
        server.Mode = "ok";
    }

    // ── 输入校验：非法输入必须 0 次 API 调用 ─────────────────────────────────
    private static void InputSection()
    {
        int before = server.Requests;
        ExpectRejected("缺 messages 被拒", Broker(new Dictionary<string, object>{{"purpose", "arxiv_title_scoring"}}, "structured_complete"), "messages");
        ExpectRejected("messages 不是数组被拒", Broker(new Dictionary<string, object>{{"messages", "nope"}, {"purpose", "arxiv_title_scoring"}}, "structured_complete"), "messages");
        ExpectRejected("messages 项不是对象被拒", Broker(new Dictionary<string, object>{{"messages", new List<object>{"plain"}}, {"purpose", "arxiv_title_scoring"}}, "structured_complete"), "messages");
        ExpectRejected("坏 role 被拒", Broker(Input(Message("tool", "x"), "arxiv_title_scoring", null), "structured_complete"), "role");
        ExpectRejected("空 content 被拒", Broker(Input(Message("user", "  "), "arxiv_title_scoring", null), "structured_complete"), "content");
        ExpectRejected("缺 purpose 被拒", Broker(Input(Message("user", "x"), "", null), "structured_complete"), "purpose");
        ExpectRejected("大写 purpose 被拒", Broker(Input(Message("user", "x"), "Arxiv_Title", null), "structured_complete"), "purpose");
        Dictionary<string, object> many = CompleteInput();
        List<object> overflow = new List<object>();
        for (int i = 0; i < 65; i++) overflow.Add(Message("user", "x"));
        many["messages"] = overflow;
        ExpectRejected("messages 超过 64 条被拒", Broker(many, "structured_complete"), "messages");
        ExpectRejected("未知 action 被拒", Broker(CompleteInput(), "chat_anything"), "action");
        Expect(server.Requests == before, "输入非法时一次 API 都不发（实际多发 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次）");
    }

    // ── 429/5xx：插件内部有限重试，耗尽后 fatal:false（记 warning 后继续）──────
    private static void RetrySection()
    {
        server.Mode = "transient";
        server.FailuresBeforeSuccess = 1;
        int before = server.Requests;
        Dictionary<string, object> recovered = Broker(CompleteInput(), "structured_complete");
        Expect(JsonUtil.Bool(recovered, "ok", false), "先 503 后 200 ⇒ 重试后成功，实际 " + JsonUtil.String(recovered, "error", ""));
        Expect(server.Requests - before == 2, "重试了一次（实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次请求）");

        // 一直 503 ⇒ 3 次重试后放弃；这一条要跑满 2s+4s+8s 的重试节奏。
        server.Mode = "transient";
        server.FailuresBeforeSuccess = 999;
        before = server.Requests;
        Dictionary<string, object> exhausted = Broker(CompleteInput(), "structured_complete");
        Expect(!JsonUtil.Bool(exhausted, "ok", true), "持续 503 ⇒ ok:false");
        Expect(JsonUtil.String(exhausted, "error_kind", "") == "provider_error", "持续 503 ⇒ error_kind=provider_error");
        Expect(!JsonUtil.Bool(exhausted, "fatal", true), "持续 503 ⇒ fatal:false（可记 warning 继续）");
        Expect(server.Requests - before == 4, "重试上限 3 次（共 4 次请求，实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次）");
        Expect(JsonUtil.String(exhausted, "error", "").IndexOf("HTTP 503", StringComparison.Ordinal) >= 0, "文案带状态码：" + JsonUtil.String(exhausted, "error", ""));
        server.Mode = "ok";
    }

    // ── 致命错误：401/402/403 + 未配 Key ⇒ fatal:true 且不重试 ─────────────────
    private static void FatalSection()
    {
        ExpectFatal("认证失败", "auth_fail", 401, "API Key");
        ExpectFatal("余额不足", "payment_required", 402, "余额不足");
        ExpectFatal("被拒绝", "forbidden", 403, "拒绝");

        // 内容不是 JSON / 没有 choices ⇒ 非致命（模型偶尔抽风不该中止整轮）。
        server.Mode = "bad_json";
        Dictionary<string, object> badJson = Broker(CompleteInput(), "structured_complete");
        Expect(!JsonUtil.Bool(badJson, "ok", true) && !JsonUtil.Bool(badJson, "fatal", true), "content 非 JSON ⇒ 非致命失败");
        Expect(JsonUtil.String(badJson, "error", "").IndexOf("JSON", StringComparison.Ordinal) >= 0, "非 JSON 文案可读：" + JsonUtil.String(badJson, "error", ""));
        server.Mode = "no_choices";
        Dictionary<string, object> noChoices = Broker(CompleteInput(), "structured_complete");
        Expect(!JsonUtil.Bool(noChoices, "ok", true), "没有 choices ⇒ ok:false");
        Expect(JsonUtil.String(noChoices, "error", "").IndexOf("choices", StringComparison.Ordinal) >= 0, "文案点名 choices");
        server.Mode = "ok";

        // 未配置 API Key：每一次调用都会失败 ⇒ 必须 fatal，不能对每批白跑。
        WritePluginConfig(server.Url, "deepseek-v4-flash", false);
        int before = server.Requests;
        Dictionary<string, object> noKey = Broker(CompleteInput(), "structured_complete");
        Expect(!JsonUtil.Bool(noKey, "ok", true) && JsonUtil.Bool(noKey, "fatal", false), "未配 Key ⇒ fatal:true（消费方要立即中止整轮）");
        Expect(JsonUtil.String(noKey, "error", "").IndexOf("API Key", StringComparison.Ordinal) >= 0, "未配 Key 文案点名 API Key");
        Expect(server.Requests == before, "未配 Key 时一次请求都不发");
        WritePluginConfig(server.Url, "deepseek-v4-flash", true);
    }

    // 注意 fatal 的位置：插件把 fatal 放进自己的 payload，**Broker 会把它提升成响应信封的顶层
    // 字段**（PluginHostApp.ServiceEnvelope 的 {"fatal",fatal}），所以这里读 response 而不是 output。
    private static void ExpectFatal(string label, string mode, int status, string keyword)
    {
        server.Mode = mode;
        int before = server.Requests;
        Dictionary<string, object> response = Broker(CompleteInput(), "structured_complete");
        Expect(!JsonUtil.Bool(response, "ok", true), label + " ⇒ ok:false");
        Expect(JsonUtil.String(response, "error_kind", "") == "provider_error", label + " ⇒ error_kind=provider_error");
        Expect(JsonUtil.Bool(response, "fatal", false), label + " ⇒ fatal:true（消费方要立即中止整轮）");
        Expect(JsonUtil.String(response, "error", "").IndexOf(keyword, StringComparison.Ordinal) >= 0, label + " 文案含「" + keyword + "」：" + JsonUtil.String(response, "error", ""));
        Expect(JsonUtil.String(response, "error", "").IndexOf("HTTP " + status.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0, label + " 文案带状态码 HTTP " + status);
        Expect(server.Requests - before == 1, label + " 不重试（只请求一次，实际 " + (server.Requests - before).ToString(CultureInfo.InvariantCulture) + " 次）");
    }

    // ── PluginAction：设置页 [测试连接] / validate_settings ─────────────────────
    private static void ActionSection()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwai-act-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "in.json");
        JsonUtil.SaveAtomic(input, new Dictionary<string, object>());

        server.Mode = "ok";
        string output = Path.Combine(dir, "ok.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") == 0, "test_connection 成功退出 0");
        Dictionary<string, object> result = JsonUtil.LoadObject(output);
        Dictionary<string, object> payload = JsonUtil.Object(JsonUtil.Get(result, "payload"));
        Expect(JsonUtil.Bool(result, "ok", false), "test_connection ok");
        Expect(JsonUtil.String(payload, "message", "") == "模型可用", "test_connection 文案 = 模型可用");
        Dictionary<string, object> limits = JsonUtil.Object(JsonUtil.Get(payload, "limits"));
        Expect(JsonUtil.Int(limits, "max_concurrency", -1) == 4 && JsonUtil.Int(limits, "timeout_seconds", -1) == 60, "test_connection 报告 limits（消费方据此开并发）");

        // 余额不足时 [测试连接] 必须如实报错，不能假装成功。
        server.Mode = "payment_required";
        output = Path.Combine(dir, "paid.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") != 0, "余额不足 ⇒ 非零退出");
        result = JsonUtil.LoadObject(output);
        Expect(JsonUtil.String(result, "error", "").IndexOf("余额不足", StringComparison.Ordinal) >= 0, "余额不足文案可读");
        Expect(JsonUtil.Bool(JsonUtil.Object(JsonUtil.Get(result, "payload")), "fatal", false), "余额不足带 payload.fatal");
        server.Mode = "ok";

        // 未配 Key ⇒ 提示填 Key。
        WritePluginConfig(server.Url, "deepseek-v4-flash", false);
        output = Path.Combine(dir, "nokey.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") != 0, "未配 Key ⇒ 非零退出");
        result = JsonUtil.LoadObject(output);
        Expect(JsonUtil.String(result, "error", "").IndexOf("API Key", StringComparison.Ordinal) >= 0, "未配 Key 文案可读");
        WritePluginConfig(server.Url, "deepseek-v4-flash", true);

        // validate_settings：宿主保存设置后会跑它（退出码非 0 会回滚设置）。
        output = Path.Combine(dir, "validate.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") == 0, "validate_settings 成功");
        payload = JsonUtil.Object(JsonUtil.Get(JsonUtil.LoadObject(output), "payload"));
        Expect(JsonUtil.String(payload, "api_url", "") == server.Url, "validate_settings 回报实际生效的 api_url");
        Expect(JsonUtil.String(payload, "model", "") == "deepseek-v4-flash", "validate_settings 回报 model");
        Expect(JsonUtil.Int(JsonUtil.Object(JsonUtil.Get(payload, "limits")), "max_concurrency", -1) == 4, "validate_settings 回报 limits");

        // 没填 Key 也必须能保存（否则用户没法先改设置再填 Key）。
        WritePluginConfig(server.Url, "deepseek-v4-flash", false);
        output = Path.Combine(dir, "validate-nokey.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") == 0, "未配 Key 时 validate_settings 仍应放行");
        WritePluginConfig(server.Url, "deepseek-v4-flash", true);

        // 地址非法 ⇒ 拒绝保存。
        WritePluginConfig("https://bad host/", "deepseek-v4-flash", true);
        output = Path.Combine(dir, "badurl.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") != 0, "非法 API 地址 ⇒ 非零退出");
        result = JsonUtil.LoadObject(output);
        Expect(JsonUtil.String(result, "error", "").IndexOf("http(s)", StringComparison.Ordinal) >= 0, "非法地址文案可读：" + JsonUtil.String(result, "error", ""));
        WritePluginConfig(server.Url, "deepseek-v4-flash", true);

        // 审计：service-call.log 记了 consumer|provider|service|billing，且不含 prompt 正文与 Key。
        string auditPath = Path.Combine(PluginPaths.Logs, "service-call.log");
        string audit = File.Exists(auditPath) ? File.ReadAllText(auditPath, Encoding.UTF8) : "";
        Expect(audit.IndexOf(Consumer + " | " + PluginId + " | " + Service, StringComparison.Ordinal) >= 0, "审计日志记录了 consumer|provider|service");
        Expect(audit.IndexOf("may_charge", StringComparison.Ordinal) >= 0, "审计日志带 billing=may_charge");
        Expect(audit.IndexOf("PROBE_PAPER_LIST", StringComparison.Ordinal) < 0, "审计日志不含 prompt 正文（论文摘要可能在里面）");
        Expect(audit.IndexOf(ApiKey, StringComparison.Ordinal) < 0, "审计日志不含 API Key");
        try { Directory.Delete(dir, true); } catch { }
    }

    // 真实消费方（arxiv）发的是 system + user 两条消息，探针照这个形态发。
    private static Dictionary<string, object> CompleteInput()
    {
        List<object> messages = new List<object>();
        messages.Add(Message("system", "Follow the scoring instructions exactly and return valid JSON only."));
        messages.Add(Message("user", "PROBE_PAPER_LIST 1: Fake paper title"));
        Dictionary<string, object> input = new Dictionary<string, object>();
        input["messages"] = messages;
        input["purpose"] = "arxiv_title_scoring";
        input["response_schema"] = new Dictionary<string, object>{{"type", "object"}, {"required", new List<object>{"scores"}}};
        return input;
    }

    private static Dictionary<string, object> Input(object message, string purpose, Dictionary<string, object> schema)
    {
        Dictionary<string, object> input = new Dictionary<string, object>();
        input["messages"] = new List<object>{message};
        input["purpose"] = purpose;
        if (schema != null) input["response_schema"] = schema;
        return input;
    }

    private static object Message(string role, string content)
    {
        return new Dictionary<string, object>{{"role", role}, {"content", content}};
    }

    private static void ExpectRejected(string label, Dictionary<string, object> response, string field)
    {
        Expect(!JsonUtil.Bool(response, "ok", true), label + " ⇒ ok:false");
        Expect(JsonUtil.String(response, "error_kind", "") == "provider_error", label + " ⇒ error_kind=provider_error");
        Expect(JsonUtil.String(response, "error", "").IndexOf(field, StringComparison.Ordinal) >= 0, label + " 文案点名 " + field + "：" + JsonUtil.String(response, "error", ""));
    }

    private static Dictionary<string, object> Output(Dictionary<string, object> response)
    {
        return JsonUtil.Object(JsonUtil.Get(response, "output"));
    }

    // 越界时返回空对象：断言失败要报"值不对"，而不是让探针自己抛异常把后面的检查全吞掉。
    private static Dictionary<string, object> At(List<object> list, int index)
    {
        return list != null && index >= 0 && index < list.Count ? JsonUtil.Object(list[index]) : new Dictionary<string, object>();
    }

    // 以一个未安装的 consumer 身份直接调用 Broker（§4.4：caller 身份只认环境变量）。
    private static Dictionary<string, object> Broker(Dictionary<string, object> input, string action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwai-broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string requestPath = Path.Combine(dir, "request.json"), outputPath = Path.Combine(dir, "output.json");
        JsonUtil.SaveAtomic(requestPath, new Dictionary<string, object>{
            {"protocol", 1}, {"request_id", "ai-" + Guid.NewGuid().ToString("N").Substring(0, 8)},
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
        Console.Out.WriteLine("AiDeepSeekProbe: checks=" + checks.ToString(CultureInfo.InvariantCulture) + " failures=" + failures.ToString(CultureInfo.InvariantCulture));
        Console.Out.WriteLine(failures == 0 ? "PASS ai deepseek probe" : "FAIL ai deepseek probe");
        return failures == 0 ? 0 : 1;
    }
}

// 最小 DeepSeek 兼容端点：只实现 POST /chat/completions，够驱动真实插件。
// 探针侧请求体含 ASCII prompt，响应体全 ASCII ⇒ Content-Length 直接按 UTF-8 字节数算。
internal sealed class FakeDeepSeek : IDisposable
{
    private TcpListener listener;
    private Thread accept;
    private volatile bool running = true;
    private readonly object gate = new object();
    private int requests;
    private int modeRequests;
    private volatile string mode = "ok";

    // 切换模式时把"本模式的请求数"清零 ⇒ transient 可以表达"先失败 N 次再成功"。
    public string Mode
    {
        get { return mode; }
        set { lock (gate) { modeRequests = 0; mode = value; } }
    }

    public volatile int FailuresBeforeSuccess;
    public string Content = "{\"scores\":{\"1\":9,\"2\":7}}";
    public int PromptTokens = 11, CompletionTokens = 22;
    public string LastBody = "", LastAuth = "", LastPath = "";

    public int Requests { get { lock (gate) return requests; } }
    public int Port { get; private set; }
    public string Url { get { return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/chat/completions"; } }

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
                LastAuth = Header(headerBlock, "Authorization");
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
                lock (gate) { requests++; attempt = ++modeRequests; }
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
        string current = mode;
        if (current == "transient")
        {
            if (attempt <= FailuresBeforeSuccess) { status = 503; return "{\"error\":{\"message\":\"Service Unavailable\"}}"; }
            current = "ok";
        }
        if (current == "auth_fail") { status = 401; return "{\"error\":{\"message\":\"Authentication Fails\"}}"; }
        if (current == "payment_required") { status = 402; return "{\"error\":{\"message\":\"Insufficient Balance\"}}"; }
        if (current == "forbidden") { status = 403; return "{\"error\":{\"message\":\"Forbidden\"}}"; }
        if (current == "bad_json") { status = 200; return Envelope("this is not json at all", true); }
        if (current == "no_choices") { status = 200; return "{\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}"; }
        if (current == "missing_usage") { status = 200; return "{\"choices\":[{\"message\":{\"content\":" + JsonUtil.Serialize(Content) + "}}]}"; }
        status = 200;
        return Envelope(Content, true);
    }

    private string Envelope(string content, bool withUsage)
    {
        string json = "{\"choices\":[{\"message\":{\"content\":" + JsonUtil.Serialize(content) + "}}]";
        if (withUsage)
            json += ",\"usage\":{\"prompt_tokens\":" + PromptTokens.ToString(CultureInfo.InvariantCulture)
                + ",\"completion_tokens\":" + CompletionTokens.ToString(CultureInfo.InvariantCulture) + "}";
        return json + "}";
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
        if (status == 402) return "Payment Required";
        if (status == 403) return "Forbidden";
        if (status == 503) return "Service Unavailable";
        return "Error";
    }
}
