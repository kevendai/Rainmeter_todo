using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RainmeterBackend;

// Phase 5「付费确认」全链路离线探针（对照 docs/V2.1-PROVIDER-INTERFACE.md）：
//   §5.2 attention ⇒ 磁贴横幅 + 按钮       §5.5 皮肤侧入口由 meter 渲染
//   §5.6 没有明确同意就不允许"自动同意"     §4.6-6 同日已拒绝 / 跨天重置 / 取消≠失败
//   §11-#9 #16 #23 #25 的宿主机与渲染侧口径
// 全程不碰网络：consumer 是 tests/FakeTodoSource.cs 编译出的假 todo_source 插件，
// 它只在拿到 allow_paid_ai=true 时才"用 AI 评分"，其余一律返回 attention。
internal static class PaidConsentProbe
{
    private static int checks, failures;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static string root = "", build = "", pluginHost = "", todoHost = "";
    private const string Source = "io.github.test.todo-source";
    private const string Arxiv = "io.github.kevendai.arxiv";

    private static int Main(string[] args)
    {
        build = AppDomain.CurrentDomain.BaseDirectory;
        pluginHost = Path.Combine(build, "PluginHost.exe");
        todoHost = Path.Combine(build, "TodoHost.exe");
        root = Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT") ?? "";
        Expect(File.Exists(pluginHost), "PluginHost.exe 在构建目录里");
        Expect(File.Exists(todoHost), "TodoHost.exe 在构建目录里");
        Expect(File.Exists(Path.Combine(build, "FakeTodoSource.exe")), "FakeTodoSource.exe 在构建目录里");
        Expect(root != "", "RAINMETER_PLUGIN_ROOT 已设置");
        if (failures > 0) return Report();
        try
        {
            Install();
            AttentionSection();
            GateSection();
            ConsentSection();
            DeclineSection();
            RenderSection();
        }
        catch (Exception ex) { Expect(false, "探针自身不应抛异常：" + ex); }
        return Report();
    }

    // ── §5.2 插件带 attention 正常退出，宿主把它写进 job ───────────────────────
    private static void AttentionSection()
    {
        int before = Calls().Length;
        string output;
        int code = RunHost("Sync " + Source, out output, false);
        Expect(code == 0, "后台同步（未请求付费）退出码为 0，实际 " + code);
        string[] calls = Calls();
        Expect(calls.Length == before + 1, "插件被调用一次，实际新增 " + (calls.Length - before));
        Expect(calls.Length > 0 && calls[calls.Length - 1].Contains("allow_paid_ai=0"), "插件看到 allow_paid_ai=0，实际 " + (calls.Length > 0 ? calls[calls.Length - 1] : ""));

        Dictionary<string, object> job = ReadJob();
        Expect(JsonUtil.String(job, "state", "") == "attention", "job 进入 attention（正常中间态，不是失败），实际 " + JsonUtil.String(job, "state", ""));
        Expect(JsonUtil.String(job, "resume_action", "") == "sync_with_ai", "job 记下 resume_action=sync_with_ai");
        Expect(JsonUtil.Bool(JsonUtil.Object(JsonUtil.Get(job, "resume_input")), "allow_paid_ai", false), "job 记下 resume_input.allow_paid_ai=true");
        Expect(JsonUtil.String(job, "message", "") != "", "job 带可读文案");
        Expect(JsonUtil.Int(job, "pid", 0) > 0, "attention 期间仍保留 pid（规格 §4.6-1）");
        Expect(JsonUtil.String(job, "job_id", "") != "", "job 带 job_id");
    }

    // ── §5.6 请求付费但没有一次性同意标记 ⇒ 拒绝，且一次都不启动插件 ────────────
    private static void GateSection()
    {
        int before = Calls().Length;
        string dir = TempDir();
        string inputPath = Path.Combine(dir, "consent.json"), outputPath = Path.Combine(dir, "consent.out.json");
        WriteText(inputPath, "{\"allow_paid_ai\":true}");
        string output;
        int code = RunHost("PluginAction " + Source + " sync_with_ai \"" + inputPath + "\" \"" + outputPath + "\"", out output, false);
        Expect(code == 1, "请求付费但没有同意标记 ⇒ 退出码 1，实际 " + code);
        Dictionary<string, object> response = File.Exists(outputPath) ? JsonUtil.LoadObject(outputPath) : new Dictionary<string, object>();
        Expect(JsonUtil.String(response, "error_kind", "") == "provider_denied", "拒绝理由是 provider_denied，实际 " + JsonUtil.String(response, "error_kind", ""));
        Expect(!JsonUtil.Bool(response, "ok", true), "拒绝时 ok=false");
        Expect(Calls().Length == before, "被拒绝的调用**一次都没有启动插件**（0 次 API 调用），实际新增 " + (Calls().Length - before));

        Dictionary<string, object> job = ReadJob();
        Expect(JsonUtil.String(job, "state", "") == "attention", "job 保持 attention 等用户点（不被改写成 failed），实际 " + JsonUtil.String(job, "state", ""));
        Expect(JsonUtil.String(job, "resume_action", "") == "sync_with_ai", "被拒绝后 resume_action 仍在，入口不丢");
    }

    // ── §5.6 带同意标记（只有 TodoHost 确认框会注入）⇒ 放行；插件自己也看不到它 ──
    private static void ConsentSection()
    {
        int before = Calls().Length;
        string dir = TempDir();
        string inputPath = Path.Combine(dir, "consent.json"), outputPath = Path.Combine(dir, "consent.out.json");
        WriteText(inputPath, "{\"allow_paid_ai\":true}");
        string output;
        int code = RunHost("PluginAction " + Source + " sync_with_ai \"" + inputPath + "\" \"" + outputPath + "\"", out output, true);
        Expect(code == 0, "带同意标记 ⇒ 退出码 0，实际 " + code);
        Dictionary<string, object> response = File.Exists(outputPath) ? JsonUtil.LoadObject(outputPath) : new Dictionary<string, object>();
        Expect(JsonUtil.Bool(response, "ok", false), "带同意标记 ⇒ ok=true" + (JsonUtil.Bool(response, "ok", false) ? "" : "，实际 " + JsonUtil.String(response, "error", "")));
        string[] calls = Calls();
        Expect(calls.Length == before + 1, "带同意标记时插件被调用一次，实际新增 " + (calls.Length - before));
        Expect(calls.Length > 0 && calls[calls.Length - 1].Contains("allow_paid_ai=1"), "插件看到 allow_paid_ai=1");
        Expect(calls.Length > 0 && calls[calls.Length - 1].Contains("consent_env=0"), "插件**自己**看不到一次性同意标记（机制保障，规格 §5.6）");
        Expect(JsonUtil.String(ReadJob(), "state", "") == "completed", "同意后这一轮正常完成，实际 " + JsonUtil.String(ReadJob(), "state", ""));

        // 没请求付费的普通调用不能被闸门误伤
        before = Calls().Length;
        WriteText(inputPath, "{}");
        code = RunHost("PluginAction " + Source + " sync_with_ai \"" + inputPath + "\" \"" + outputPath + "\"", out output, false);
        Expect(code == 0, "没请求付费的调用照常成功，实际退出码 " + code);
        Expect(Calls().Length == before + 1, "没请求付费的调用正常启动插件一次，实际新增 " + (Calls().Length - before));
    }

    // ── §4.6-6 取消记号：只有用户明确拒绝才算"今天别再问"，跨天自动重置 ─────────
    private static void DeclineSection()
    {
        string output;
        RunHost("Sync " + Source, out output, false);
        Expect(JsonUtil.String(ReadJob(), "state", "") == "attention", "重新同步后回到 attention（取消已不再是终态）");

        RunHost("Cancel " + Source + " user_cancelled", out output, false);
        Dictionary<string, object> job = ReadJob();
        Expect(JsonUtil.String(job, "state", "") == "cancelled", "user_cancelled ⇒ job 变 cancelled，实际 " + JsonUtil.String(job, "state", ""));
        Expect(JsonUtil.String(job, "cancel_reason", "") == "user_cancelled", "cancel_reason 记下 user_cancelled");
        Expect(JsonUtil.String(job, "cancel_date", "") == PluginRuntime.LocalDate(), "取消写下了本机日期");
        Expect(JsonUtil.String(job, "paid_declined_date", "") == "", "用户主动中断不算「今天已拒绝」（不写粘性记号）");
        Expect(JsonUtil.String(job, "resume_action", "") == "sync_with_ai", "取消后仍保留入口（规格 §4.6-6）");

        RunHost("Sync " + Source, out output, false);
        RunHost("Cancel " + Source + " user_declined", out output, false);
        job = ReadJob();
        Expect(JsonUtil.String(job, "cancel_reason", "") == "user_declined", "cancel_reason 记下 user_declined");
        Expect(JsonUtil.String(job, "paid_declined_date", "") == PluginRuntime.LocalDate(), "user_declined 写下粘性记号 paid_declined_date");
        string message = JsonUtil.String(job, "message", "");
        Expect(message.Contains("不使用 AI 评分"), "取消文案说人话（取消 ≠ 失败），实际 " + message);
        Expect(!message.Contains("失败"), "取消文案不得出现「失败」");

        RunHost("Sync " + Source, out output, false);
        Expect(Calls().Last().Contains("declined_today=1"), "同日拒绝过 ⇒ 插件收到 RW_PLUGIN_DECLINED_TODAY=1，实际 " + Calls().Last());

        RewriteJob("paid_declined_date", Yesterday());
        RunHost("Sync " + Source, out output, false);
        Expect(Calls().Last().Contains("declined_today=0"), "跨天后自动恢复询问（declined_today=0），实际 " + Calls().Last());
    }

    // ── §5.5 磁贴上的入口是 meter 的活 ────────────────────────────────────────
    private static void RenderSection()
    {
        WriteJob("attention");
        RunTodo("Render");
        string generated = ReadGenerated();
        Expect(generated.Contains("AttentionButton") && generated.Contains("AttentionButtonText"), "attention ⇒ 磁贴渲染出可点按钮");
        Expect(generated.Contains("\"PluginConfirmAttention\" \"" + Source + "\""), "按钮执行 PluginConfirmAttention <pluginId>");
        Expect(generated.Contains("使用 DeepSeek AI"), "按钮文案是「使用 DeepSeek AI」");
        Expect(MeterBlock(generated, "AttentionTitle").Contains("需要确认"), "横幅文案表明这是「需要确认」而不是失败");

        WriteJob("declined");
        RunTodo("Render");
        generated = ReadGenerated();
        Expect(!generated.Contains("AttentionButton"), "同日已拒绝 ⇒ 不再显示醒目横幅");
        Expect(!generated.Contains("DeclinedAiEntry"), "今天不再提醒后磁贴不显示二次入口");
        Expect(!generated.Contains("\"PluginConfirmAttention\" \"" + Source + "\""), "今天不再提醒后不再从磁贴询问");

        // §11-#25：cancelled 的文案不得是「失败：…」
        WriteText(Path.Combine(root, "PluginJobs", Arxiv + ".json"),
            "{\"job_id\":\"probe\",\"state\":\"cancelled\",\"cancel_reason\":\"user_declined\",\"message\":\"已取消：你选择了不使用 AI 评分\"}");
        RunTodo("Render");
        generated = ReadGenerated();
        Expect(MeterBlock(generated, "Status").Contains("已取消"), "cancelled 在磁贴页脚照实显示，实际 " + MeterBlock(generated, "Status").Trim());
        Expect(!MeterBlock(generated, "Status").Contains("失败"), "cancelled 不得渲染成「失败：…」（§11-#25）");
        try { File.Delete(Path.Combine(root, "PluginJobs", Arxiv + ".json")); } catch { }
    }

    // ── 工具 ──────────────────────────────────────────────────────────────────
    private static void Install()
    {
        string version = Path.Combine(root, "Plugins", Source, "versions", "1.0.0");
        Directory.CreateDirectory(Path.Combine(version, "bin"));
        File.Copy(Path.Combine(build, "FakeTodoSource.exe"), Path.Combine(version, "bin", "FakeTodoSource.exe"), true);
        WriteText(Path.Combine(version, "plugin.json"),
            "{\"id\":\"" + Source + "\",\"name\":\"Fake Todo Source\",\"version\":\"1.0.0\",\"api_version\":1,\"min_host_version\":\"2.0.0\"," +
            "\"entry\":\"bin/FakeTodoSource.exe\",\"capabilities\":[\"todo_source\"],\"permissions\":[],\"provides\":[],\"uses\":[]}");
        WriteText(Path.Combine(root, "Plugins", Source, "current.json"), "{\"version\":\"1.0.0\",\"enabled\":true}");
        try { File.Delete(CallsPath()); } catch { }
    }

    private static string CallsPath() { return Path.Combine(root, "PluginData", Source, "calls.log"); }    private static string[] Calls() { return File.Exists(CallsPath()) ? File.ReadAllLines(CallsPath()) : new string[0]; }
    private static Dictionary<string, object> ReadJob() { string path = Path.Combine(root, "PluginJobs", Source + ".json"); return File.Exists(path) ? JsonUtil.LoadObject(path) : new Dictionary<string, object>(); }

    private static void WriteJob(string kind)
    {
        string today = PluginRuntime.LocalDate();
        string tail = kind == "declined"
            ? ",\"cancel_reason\":\"user_declined\",\"cancel_date\":\"" + today + "\",\"paid_declined_date\":\"" + today + "\",\"message\":\"已取消：你选择了不使用 AI 评分\""
            : ",\"message\":\"远端论文同步失败，是否使用 DeepSeek AI 重新评分？\"";
        WriteText(Path.Combine(root, "PluginJobs", Source + ".json"),
            "{\"job_id\":\"probe\",\"plugin_id\":\"" + Source + "\",\"state\":\"" + (kind == "declined" ? "cancelled" : "attention") + "\",\"current\":0,\"total\":0," +
            "\"resume_action\":\"sync_with_ai\",\"resume_input\":{\"allow_paid_ai\":true}" + tail + "}");
    }

    private static void RewriteJob(string key, string value)
    {
        string path = Path.Combine(root, "PluginJobs", Source + ".json");
        Dictionary<string, object> job = JsonUtil.LoadObject(path);
        job[key] = value;
        JsonUtil.SaveAtomic(path, job);
    }

    // Generated.inc 是 INI 片段：取 [name] 到下一个 [ 之间的块，避免被别的插件的 meter 串味。
    private static string MeterBlock(string generated, string name)
    {
        int start = generated.IndexOf("[" + name + "]", StringComparison.Ordinal);
        if (start < 0) return "";
        int end = generated.IndexOf("\n[", start, StringComparison.Ordinal);
        return end < 0 ? generated.Substring(start) : generated.Substring(start, end - start);
    }

    private static string ReadGenerated() { return File.ReadAllText(Path.Combine(build, "Generated.inc"), Encoding.Unicode); }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwpaid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // 「跨天重置」的判定只看日期字符串，所以这里只需要造出昨天的日期。
    private static string Yesterday() { return DateTimeOffset.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

    // consent=false 时**显式剔除**同意标记，保证"没有同意"这件事本身可复现，
    // 不会被探针进程自己（或 CI 环境）里可能存在的同名变量影响。
    private static int RunHost(string arguments, out string stdout, bool consent)
    {
        ProcessStartInfo info = new ProcessStartInfo(pluginHost, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        Dictionary<string, string> extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        extra[PluginRuntime.PaidConsentVariable] = consent ? "1" : null;
        PluginRuntime.ApplyPluginEnvironment(info, null, null, extra);
        using (Process process = Process.Start(info))
        {
            stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static void RunTodo(string arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo(todoHost, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        PluginRuntime.ApplyPluginEnvironment(info, null, null, null);
        using (Process process = Process.Start(info))
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60000)) { try { process.Kill(); } catch { } Expect(false, "TodoHost " + arguments + " 超时"); return; }
            Expect(process.ExitCode == 0, "TodoHost " + arguments + " 退出码 0，实际 " + process.ExitCode);
        }
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, Utf8);
    }

    private static int Report()
    {
        Console.WriteLine((failures == 0 ? "PASS" : "FAIL") + " paid consent probe: checks=" + checks.ToString() + " failures=" + failures.ToString());
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
