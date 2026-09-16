using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RainmeterBackend;

// Phase 1 基础设施的回归测试（对照 docs/V2.1-PROVIDER-INTERFACE.md）：
//   §1.3 capabilities/provides 校验放宽    §2 manifest 增量
//   §3   plugin-bindings.json 与 4 条解析分支
//   §4   Broker（请求文件白名单 / error_kind / depth=1 / provider 环境剔除 HostExe）
//   §4.6 取消传播（consumer → Broker → provider 三层都要收干净）
//   §4.7 busy 语义（重复点击不得把在跑的 job 打成 failed）
// 全程不碰网络：provider 是 tests/FakeProvider.cs 编译出的假插件。
internal static class ProviderProbe
{
    private static int checks, failures;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static string root = "", build = "", pluginHost = "", fakePlugin = "";

    private const string Consumer = "io.github.test.consumer";
    private const string ProviderA = "io.github.test.provider-a";
    private const string ProviderB = "io.github.test.provider-b";
    private const string ProviderOld = "io.github.test.provider-old";
    private const string Service = "ai_provider@1";

    private static int Main(string[] args)
    {
        build = AppDomain.CurrentDomain.BaseDirectory;
        pluginHost = Path.Combine(build, "PluginHost.exe");
        fakePlugin = Path.Combine(build, "FakeProvider.exe");
        root = Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT") ?? "";
        Expect(File.Exists(pluginHost), "PluginHost.exe 在构建目录里");
        Expect(File.Exists(fakePlugin), "FakeProvider.exe 在构建目录里");
        Expect(root != "", "RAINMETER_PLUGIN_ROOT 已设置");
        if (failures > 0) return Report();
        try
        {
            ManifestSection();
            BindingSection();
            ConsumerEnvironmentSection();
            BrokerSection();
            AuditSection();
            CancelSection();
            BusySection();
        }
        catch (Exception ex) { Expect(false, "探针自身不应抛异常：" + ex); }
        return Report();
    }

    // ── §1.3 / §2 manifest 增量 ────────────────────────────────────────────────
    private static void ManifestSection()
    {
        Install(ProviderA, ManifestText(ProviderA, "Fake AI A", "[]", "[\"" + Service + "\"]", "[]", "may_charge"));
        Install(ProviderB, ManifestText(ProviderB, "Fake AI B", "[]", "[\"" + Service + "\"]", "[]", "may_charge"));
        Install(ProviderOld, ManifestText(ProviderOld, "Fake AI Old", "[]", "[\"ai_provider@2\"]", "[]", "may_charge"));
        Install(Consumer, ManifestText(Consumer, "Test consumer", "[\"todo_source\"]", "[]",
            "[{\"service\":\"" + Service + "\",\"binding_key\":\"ai_provider\",\"optional\":true}]", ""));

        Expect(LoadOk(ProviderA), "capabilities 为空但 provides 非空的纯 Provider 通过校验");
        Expect(LoadOk(Consumer), "带 uses 的 consumer 通过校验");

        Expect(Throws(delegate { PluginManifest.Load(Bare("io.github.test.empty", "[]", "[]", "[]")); }),
            "既无 capability 也无 provides 的插件被拒");
        Expect(Throws(delegate { PluginManifest.Load(Bare("io.github.test.badprovides", "[]", "[\"ai_provider\"]", "[]")); }),
            "provides 缺少 @版本 被拒");
        Expect(Throws(delegate { PluginManifest.Load(Bare("io.github.test.baduses", "[]", "[]", "[{\"binding_key\":\"ai_provider\"}]")); }),
            "uses 缺 service 被拒");
        Expect(Throws(delegate { PluginManifest.Load(Bare("io.github.test.dupuses", "[]", "[]", "[{\"service\":\"" + Service + "\"},{\"service\":\"translation_provider@1\",\"binding_key\":\"ai_provider\"}]")); }),
            "uses 的 binding_key 重复被拒");
        Expect(Throws(delegate { PluginManifest.Load(Bare("io.github.test.badkey", "[]", "[]", "[{\"service\":\"" + Service + "\",\"binding_key\":\"Bad-Key\"}]")); }),
            "uses 的 binding_key 格式非法被拒");
    }

    // ── §3 绑定解析的 4 条分支 ─────────────────────────────────────────────────
    private static void BindingSection()
    {
        try { if (File.Exists(PluginPaths.Bindings)) File.Delete(PluginPaths.Bindings); } catch { }
        ServiceUse use = new ServiceUse { Service = Service, BindingKey = "ai_provider" };

        ServiceResolution ambiguous = ServiceRegistry.Resolve(Consumer, use, false);
        Expect(!ambiguous.Available && ambiguous.Reason == "ambiguous",
            "两个候选且未绑定 ⇒ 视为未绑定（reason=ambiguous），实际 " + ambiguous.Reason);

        ServiceRegistry.SetBinding(Consumer, Service, ProviderA);
        ServiceResolution bound = ServiceRegistry.Resolve(Consumer, use, false);
        Expect(bound.Available && bound.ProviderId == ProviderA, "显式绑定被采纳");
        Expect(bound.Billing == "may_charge", "billing 元数据随解析结果返回，实际 " + bound.Billing);
        Expect(ServiceRegistry.BoundProvider(Consumer, Service) == ProviderA, "绑定表已落盘 plugin-bindings.json");

        SetEnabled(ProviderA, false);
        Expect(ServiceRegistry.Resolve(Consumer, use, false).Reason == "disabled", "绑定到已禁用的 provider ⇒ disabled");
        SetEnabled(ProviderA, true);

        ServiceRegistry.SetBinding(Consumer, Service, ProviderOld);
        Expect(ServiceRegistry.Resolve(Consumer, use, false).Reason == "version_mismatch", "绑定到只提供 @2 的 provider ⇒ version_mismatch");

        ServiceRegistry.SetBinding(Consumer, Service, "io.github.test.gone");
        Expect(ServiceRegistry.Resolve(Consumer, use, false).Reason == "not_installed", "绑定到已卸载的 provider ⇒ not_installed");

        ServiceRegistry.SetBinding(Consumer, Service, "");
        SetEnabled(ProviderB, false);
        ServiceResolution auto = ServiceRegistry.Resolve(Consumer, use, true);
        Expect(auto.Available && auto.AutoBound && auto.ProviderId == ProviderA, "唯一候选自动绑定");
        Expect(ServiceRegistry.BoundProvider(Consumer, Service) == ProviderA, "自动绑定已落盘 plugin-bindings.json");
        Expect(ServiceRegistry.ReasonFor(Consumer, "translation_provider@1") == "not_installed", "没有任何 provider 的 service ⇒ not_installed");
    }

    // ── §3 呈现给 consumer 的 context.services / RW_SERVICE_* ───────────────────
    private static void ConsumerEnvironmentSection()
    {
        PluginCallResult result = PluginRuntime.Invoke(Consumer, "dump_env", new Dictionary<string, object>(), "probe", 60, null);
        Expect(result.Ok, "consumer dump_env 调用成功：" + result.Error);
        Dictionary<string, object> env = JsonUtil.Object(JsonUtil.Get(result.Payload, "env"));
        Expect(JsonUtil.String(env, "RW_PLUGIN_ID", "") == Consumer, "RW_PLUGIN_ID 是 consumer 自己");
        Expect(JsonUtil.String(env, "RW_SERVICE_AI_PROVIDER_PROVIDER", "") == ProviderA, "RW_SERVICE_*_PROVIDER 指出被绑定的 provider");
        Expect(JsonUtil.String(env, "RW_SERVICE_AI_PROVIDER_NAME", "") == "Fake AI A", "RW_SERVICE_*_NAME 是 provider 名字");
        Expect(JsonUtil.String(env, "RW_PLUGIN_HOST_EXE", "") == pluginHost, "RW_PLUGIN_HOST_EXE 指向 Broker 入口");
        Expect(JsonUtil.String(env, "RW_PLUGIN_JOB_ID", "") == "", "未起 job 时 RW_PLUGIN_JOB_ID 为空");
        Expect(JsonUtil.String(env, "RW_SERVICE_CALL_DEPTH", "") == "", "consumer 环境里没有 depth 标记");
        Expect(JsonUtil.String(env, "RW_PLUGIN_PID", "") != "", "RW_PLUGIN_PID 指向父进程");
    }

    // ── §4 Broker ──────────────────────────────────────────────────────────────
    private static void BrokerSection()
    {
        Dictionary<string, object> noProvider = Broker("{\"protocol\":1,\"request_id\":\"r1\",\"service\":\"translation_provider@1\",\"action\":\"translate\",\"input\":{}}", false);
        Expect(!JsonUtil.Bool(noProvider, "ok", true) && JsonUtil.String(noProvider, "error_kind", "") == "no_provider",
            "没有 provider 的 service ⇒ error_kind=no_provider（不是失败），实际 " + JsonUtil.String(noProvider, "error_kind", "") + " / " + JsonUtil.String(noProvider, "error", ""));

        Dictionary<string, object> extra = Broker("{\"protocol\":1,\"request_id\":\"r2\",\"service\":\"" + Service + "\",\"action\":\"structured_complete\",\"input\":{},\"provider_id\":\"" + ProviderA + "\"}", false);
        Expect(JsonUtil.String(extra, "error_kind", "") == "protocol_error", "请求里出现 provider_id ⇒ protocol_error（插件不得指定 provider）");

        Dictionary<string, object> badService = Broker("{\"protocol\":1,\"request_id\":\"r3\",\"service\":\"ai_provider\",\"action\":\"structured_complete\"}", false);
        Expect(JsonUtil.String(badService, "error_kind", "") == "protocol_error", "service 缺 @版本 ⇒ protocol_error");

        Dictionary<string, object> depth = Broker("{\"protocol\":1,\"request_id\":\"r4\",\"service\":\"" + Service + "\",\"action\":\"structured_complete\"}", true);
        Expect(JsonUtil.String(depth, "error_kind", "") == "broker_depth_exceeded", "带 depth 标记的二级转发被拒 ⇒ broker_depth_exceeded");

        Dictionary<string, object> success = Broker("{\"protocol\":1,\"request_id\":\"r5\",\"service\":\"" + Service + "\",\"action\":\"structured_complete\",\"input\":{\"messages\":[]},\"timeout_seconds\":30}", false);
        Expect(JsonUtil.Bool(success, "ok", false) && JsonUtil.String(success, "status", "") == "ok", "provider 成功 ⇒ ok:true / status:ok");
        Dictionary<string, object> scores = JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(success, "output")), "json")), "scores"));
        object first; int firstScore = 0;
        if (scores.TryGetValue("1", out first)) Int32.TryParse(Convert.ToString(first), out firstScore);
        Expect(firstScore == 8, "结构化结果按契约原样回传，实际 " + firstScore);

        Dictionary<string, object> attention = Broker("{\"protocol\":1,\"request_id\":\"r6\",\"service\":\"" + Service + "\",\"action\":\"needs_attention\",\"input\":{}}", false);
        Expect(JsonUtil.Bool(attention, "ok", false) && JsonUtil.String(attention, "status", "") == "attention", "attention 是 ok:true + status:attention 的正式结果");
        Dictionary<string, object> attentionBody = JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(attention, "output")), "attention"));
        Expect(JsonUtil.String(attentionBody, "resume_action", "") == "sync_with_ai", "attention 携带 resume_action");
        Expect(JsonUtil.String(attentionBody, "message", "") != "", "attention 携带可读文案");

        Dictionary<string, object> fatal = Broker("{\"protocol\":1,\"request_id\":\"r7\",\"service\":\"" + Service + "\",\"action\":\"fail_fatal\",\"input\":{}}", false);
        Expect(JsonUtil.String(fatal, "error_kind", "") == "provider_error" && JsonUtil.Bool(fatal, "fatal", false), "provider 明确报错 ⇒ provider_error + fatal:true");
        Expect(JsonUtil.String(fatal, "error", "").IndexOf("402", StringComparison.Ordinal) >= 0, "provider 的错误文案原样回传");

        Dictionary<string, object> providerEnv = Broker("{\"protocol\":1,\"request_id\":\"r8\",\"service\":\"" + Service + "\",\"action\":\"dump_env\",\"input\":{}}", false);
        Dictionary<string, object> env = JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(providerEnv, "output")), "env"));
        Expect(JsonUtil.String(env, "RW_PLUGIN_HOST_EXE", "") == "", "provider 环境块里没有 RW_PLUGIN_HOST_EXE（depth=1 的机制保障）");
        Expect(JsonUtil.String(env, "RW_SERVICE_CALL_DEPTH", "") == "1", "provider 环境块里 RW_SERVICE_CALL_DEPTH=1");
        Expect(JsonUtil.String(env, "RW_PLUGIN_ID", "") == ProviderA, "provider 看到的 RW_PLUGIN_ID 是自己");
        Expect(JsonUtil.String(env, "RW_SERVICE_AI_PROVIDER_PROVIDER", "") == "", "provider 自己没有 uses，因此不解析服务");

        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<string, object> timeout = Broker("{\"protocol\":1,\"request_id\":\"r9\",\"service\":\"" + Service + "\",\"action\":\"hang\",\"input\":{},\"timeout_seconds\":2}", false);
        watch.Stop();
        Expect(JsonUtil.String(timeout, "error_kind", "") == "provider_timeout", "provider 超时 ⇒ provider_timeout，实际 " + JsonUtil.String(timeout, "error_kind", ""));
        Expect(watch.ElapsedMilliseconds < 30000, "超时由宿主强制生效（" + watch.ElapsedMilliseconds + "ms）");
    }

    private static void AuditSection()
    {
        string path = Path.Combine(PluginPaths.Logs, "service-call.log");
        Expect(File.Exists(path), "Broker 审计日志 service-call.log 已写入");
        if (!File.Exists(path)) return;
        string text = File.ReadAllText(path, RuntimeUtil.Utf8NoBom);
        Expect(text.IndexOf(Consumer, StringComparison.Ordinal) >= 0, "审计行含 consumer");
        Expect(text.IndexOf("provider_error", StringComparison.Ordinal) >= 0, "审计行含 error_kind");
        Expect(text.IndexOf("may_charge", StringComparison.Ordinal) >= 0, "审计行含 billing");
        Expect(text.IndexOf("messages", StringComparison.Ordinal) < 0, "审计不记录 input 正文");
    }

    // ── §4.6 取消传播 ──────────────────────────────────────────────────────────
    private static void CancelSection()
    {
        // §4.6-2：children 属于具体 job。前面那几节没有 job 的 Broker 调用不得在这里留下任何痕迹。
        Expect(!File.Exists(Path.Combine(PluginPaths.Jobs, Consumer + ".json")), "没有 job 的 Broker 调用不会在 consumer 的 job 文件里留下 children");
        if (File.Exists(Path.Combine(PluginPaths.Jobs, Consumer + ".json"))) File.Delete(Path.Combine(PluginPaths.Jobs, Consumer + ".json"));
        string dir = TempDir();
        string inputPath = Path.Combine(dir, "input.json"), outputPath = Path.Combine(dir, "output.json");
        WriteText(inputPath, "{}");
        Process host = StartHost("PluginAction " + Consumer + " call_service \"" + inputPath + "\" \"" + outputPath + "\"");
        Dictionary<string, object> job = new Dictionary<string, object>();
        List<Dictionary<string, object>> children = new List<Dictionary<string, object>>();
        for (int attempt = 0; attempt < 300; attempt++)
        {
            job = ReadJob(Consumer);
            children = JsonUtil.Array(JsonUtil.Get(job, "children")).Select(JsonUtil.Object).ToList();
            if (children.Count > 0 && JsonUtil.Int(children[0], "pid", 0) > 0) break;
            Thread.Sleep(100);
        }
        Expect(children.Count > 0, "Broker 把 provider 进程登记进了 consumer job 的 children[]");
        if (children.Count == 0) { try { host.Kill(); } catch { } return; }

        int consumerPid = JsonUtil.Int(job, "pid", 0), providerPid = JsonUtil.Int(children[0], "pid", 0), brokerPid = JsonUtil.Int(children[0], "broker_pid", 0);
        Diag("before-cancel job=" + JsonUtil.Serialize(job));
        Diag("before-cancel children=" + JsonUtil.Serialize(children.Cast<object>().ToList()));
        Expect(consumerPid > 0 && providerPid > 0 && brokerPid > 0, "job 与 children 记录了三层 pid（consumer / provider / broker）");
        Expect(Alive(providerPid), "取消前 provider 进程仍在运行");

        string cancelError;
        int exit = RunHost("Cancel " + Consumer + " user_cancelled", out cancelError);
        Diag("cancel exit=" + exit + " stderr=" + cancelError);
        Expect(exit == 0, "取消命令返回 0，实际 " + exit);
        Expect(!Alive(consumerPid), "取消后 consumer 进程已退出");
        Expect(!Alive(providerPid), "取消后 provider 进程已退出（不留下孤儿请求）");
        Expect(!Alive(brokerPid), "取消后 Broker 进程已退出");
        Expect(host.WaitForExit(10000), "consumer 宿主进程已收尾");

        job = ReadJob(Consumer);
        Expect(JsonUtil.String(job, "state", "") == "cancelled", "job 状态为 cancelled，实际 " + JsonUtil.String(job, "state", ""));
        Expect(JsonUtil.String(job, "cancel_reason", "") == "user_cancelled", "cancel_reason = user_cancelled");
        Expect(JsonUtil.String(job, "message", "").IndexOf("失败", StringComparison.Ordinal) < 0, "取消文案不含「失败」");

        Expect(RunHost("Cancel " + Consumer + " user_cancelled") == 1, "已经收尾的 job 再次取消返回 1，不会把 completed/cancelled 改写回 cancelled");
        try { Directory.Delete(dir, true); } catch { }
    }

    // ── §4.7 busy 语义 ─────────────────────────────────────────────────────────
    private static void BusySection()
    {
        Process first = StartHost("Sync " + Consumer);
        bool running = false;
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (JsonUtil.String(ReadJob(Consumer), "state", "") == "running") { running = true; break; }
            Thread.Sleep(100);
        }
        Expect(running, "第一个 Sync 进入了 running");
        Expect(RunHost("Sync " + Consumer) == 0, "第二个 Sync 请求返回 0（busy 不是失败）");
        Dictionary<string, object> during = ReadJob(Consumer);
        Expect(JsonUtil.String(during, "state", "") == "running", "在跑的 job 仍是 running，没有被写成 failed");
        Expect(JsonUtil.String(during, "message", "") != "该插件已有同步任务正在运行", "busy 请求没有踩坏在跑 job 的 message，实际「" + JsonUtil.String(during, "message", "") + "」");
        Expect(first.WaitForExit(60000), "第一个 Sync 正常收尾");
        Expect(JsonUtil.String(ReadJob(Consumer), "state", "") == "completed", "第一个 job 最终 completed");
    }

    // ── 工具 ──────────────────────────────────────────────────────────────────
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Dictionary<string, object> Broker(string requestJson, bool depth)
    {
        string dir = TempDir();
        string requestPath = Path.Combine(dir, "request.json"), outputPath = Path.Combine(dir, "output.json");
        WriteText(requestPath, requestJson);
        ProcessStartInfo info = new ProcessStartInfo(pluginHost, "-Mode ServiceCall -RequestFile \"" + requestPath + "\" -OutputFile \"" + outputPath + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        Dictionary<string, string> extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        extra["RW_PLUGIN_ID"] = Consumer; extra["RW_PLUGIN_JOB_ID"] = ""; extra["RW_PLUGIN_HOST_EXE"] = pluginHost;
        if (depth) extra["RW_SERVICE_CALL_DEPTH"] = "1";
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

    private static void Install(string id, string manifest) { WriteText(Path.Combine(VersionRoot(id), "plugin.json"), manifest); InstallEntry(id); SetEnabled(id, true); }

    private static string Bare(string id, string capabilities, string provides, string uses)
    {
        WriteText(Path.Combine(VersionRoot(id), "plugin.json"), ManifestText(id, "Probe", capabilities, provides, uses, ""));
        InstallEntry(id);
        return VersionRoot(id);
    }

    private static void InstallEntry(string id)
    {
        string bin = Path.Combine(VersionRoot(id), "bin");
        Directory.CreateDirectory(bin);
        File.Copy(fakePlugin, Path.Combine(bin, "FakeProvider.exe"), true);
    }

    private static string ManifestText(string id, string name, string capabilities, string provides, string uses, string billing)
    {
        string billingPart = String.IsNullOrEmpty(billing) ? "" : "\"billing\":\"" + billing + "\",";
        return "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"version\":\"1.0.0\",\"api_version\":1,\"min_host_version\":\"2.0.0\"," +
            "\"entry\":\"bin/FakeProvider.exe\",\"capabilities\":" + capabilities + "," + billingPart +
            "\"permissions\":[],\"provides\":" + provides + ",\"uses\":" + uses + "}";
    }

    private static string VersionRoot(string id) { return Path.Combine(root, "Plugins", id, "versions", "1.0.0"); }

    private static void SetEnabled(string id, bool enabled)
    {
        WriteText(Path.Combine(root, "Plugins", id, "current.json"), "{\"version\":\"1.0.0\",\"enabled\":" + (enabled ? "true" : "false") + "}");
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, Utf8);
    }

    private static bool LoadOk(string id) { try { PluginManifest.Load(VersionRoot(id)); return true; } catch { return false; } }
    private static bool Throws(Action action) { try { action(); return false; } catch { return true; } }

    private static Dictionary<string, object> ReadJob(string id)
    {
        try
        {
            string path = Path.Combine(PluginPaths.Jobs, id + ".json");
            return File.Exists(path) ? JsonUtil.LoadObject(path) : new Dictionary<string, object>();
        }
        catch { return new Dictionary<string, object>(); }
    }

    private static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        try { using (Process process = Process.GetProcessById(pid)) return !process.HasExited; } catch { return false; }
    }

    private static Process StartHost(string arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo(pluginHost, arguments) { UseShellExecute = false, CreateNoWindow = true };
        PluginRuntime.ApplyPluginEnvironment(info, null, null, null);
        return Process.Start(info);
    }

    private static int RunHost(string arguments)
    {
        string ignored;
        return RunHost(arguments, out ignored);
    }

    private static int RunHost(string arguments, out string stderr)
    {
        ProcessStartInfo info = new ProcessStartInfo(pluginHost, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        PluginRuntime.ApplyPluginEnvironment(info, null, null, null);
        using (Process process = Process.Start(info))
        {
            stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    // 详细现场只在需要排查时打开：set RWPROBE_VERBOSE=1
    private static void Diag(string message)
    {
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("RWPROBE_VERBOSE"))) Console.WriteLine("DIAG " + message);
    }

    private static int Report()
    {
        Console.WriteLine((failures == 0 ? "PASS" : "FAIL") + " provider probe: checks=" + checks.ToString() + " failures=" + failures.ToString());
        return failures == 0 ? 0 : 1;
    }

    private static void Expect(bool condition, string message)
    {
        checks++;
        if (condition) return;
        failures++;
        Console.WriteLine("FAIL: " + message);
    }
}
