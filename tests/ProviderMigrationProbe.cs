using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using RainmeterBackend;

// Phase 8 探针：v2.1 配置迁移（migration-v2.1.json）+ min_host_version 不满足的可见提示。
//
// PluginPaths.Root 是 static readonly（进程级），所以每个场景都在**自己的子进程**里跑，
// 由父进程为它准备独立的 RAINMETER_PLUGIN_ROOT。子进程把断言计数打在 stdout 的
// P8RESULT 行上，父进程汇总。
internal static class ProviderMigrationProbe
{
    private const string ScenarioVariable = "RW_PROBE_P8_SCENARIO";
    private const string ResultPrefix = "P8RESULT ";

    private static int checks, failures;

    private static void Check(bool condition, string label)
    {
        checks++;
        if (condition) return;
        failures++;
        Console.Error.WriteLine("FAIL: " + label);
    }

    private static void Equal(string expected, string actual, string label)
    {
        Check(String.Equals(expected, actual, StringComparison.Ordinal),
            label + "（期望 [" + expected + "]，实际 [" + actual + "]）");
    }

    private static string Text(object value)
    {
        return value == null ? "<null>" : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static int Main(string[] args)
    {
        string scenario = Environment.GetEnvironmentVariable(ScenarioVariable);
        if (!String.IsNullOrWhiteSpace(scenario))
        {
            int code;
            try { code = RunScenario(scenario.Trim()); }
            catch (Exception ex) { Console.Error.WriteLine("SCENARIO CRASH: " + ex); code = 1; }
            Console.Out.WriteLine(ResultPrefix + "checks=" + checks + " failures=" + failures + " scenario=" + scenario);
            Console.Out.Flush();
            if (failures > 0) Console.Error.WriteLine(scenario + ": checks=" + checks + " failures=" + failures);
            return code == 0 && failures == 0 ? 0 : 1;
        }
        return RunAll();
    }

    // ------------------------------------------------------------------ 父进程

    private static readonly string[] Scenarios = new[]
    {
        "full", "no_key", "corrupt_secret", "partial_failure", "no_arxiv", "rollback", "host_too_old"
    };

    private static int RunAll()
    {
        string self = Assembly.GetExecutingAssembly().Location;
        string parent = Path.Combine(Path.GetTempPath(), "p8probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        // ProcessStartInfo.EnvironmentVariables 是 StringDictionary，本机 PATH/Path 大小写并存时会
        // 抛"已添加项"，所以改走进程环境变量（子进程继承），跑完还原。
        string previousRoot = Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT");
        string previousScenario = Environment.GetEnvironmentVariable(ScenarioVariable);
        string previousCommands = Environment.GetEnvironmentVariable("RAINMETER_COMMANDS_DISABLED");
        int totalChecks = 0, totalFailures = 0, badScenarios = 0;
        try
        {
            foreach (string scenario in Scenarios)
            {
                string root = Path.Combine(parent, scenario);
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT", root);
                Environment.SetEnvironmentVariable(ScenarioVariable, scenario);
                Environment.SetEnvironmentVariable("RAINMETER_COMMANDS_DISABLED", "1");
                ProcessStartInfo info = new ProcessStartInfo(self);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = Process.Start(info))
                {
                    string stdout = process.StandardOutput.ReadToEnd(), stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    int childChecks = 0, childFailures = 0;
                    bool parsed = false;
                    foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!line.StartsWith(ResultPrefix, StringComparison.Ordinal)) continue;
                        parsed = true;
                        foreach (string field in line.Substring(ResultPrefix.Length).Split(' '))
                        {
                            string[] pair = field.Split('=');
                            if (pair.Length != 2) continue;
                            int value;
                            if (!Int32.TryParse(pair[1], out value)) continue;
                            if (pair[0] == "checks") childChecks = value;
                            if (pair[0] == "failures") childFailures = value;
                        }
                    }
                    totalChecks += childChecks; totalFailures += childFailures;
                    Boolean ok = parsed && process.ExitCode == 0 && childFailures == 0;
                    if (!ok)
                    {
                        badScenarios++;
                        Console.Error.WriteLine("SCENARIO " + scenario + " FAILED (exit " + process.ExitCode + ", checks " + childChecks + ", failures " + childFailures + ")");
                        if (stderr.Trim() != "") Console.Error.WriteLine(stderr.Trim());
                    }
                    else
                    {
                        Console.WriteLine("PASS migration scenario " + scenario + ": checks=" + childChecks + " failures=0");
                    }
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT", previousRoot);
            Environment.SetEnvironmentVariable(ScenarioVariable, previousScenario);
            Environment.SetEnvironmentVariable("RAINMETER_COMMANDS_DISABLED", previousCommands);
            try { Directory.Delete(parent, true); } catch { }
        }
        Console.WriteLine("ProviderMigrationProbe: checks=" + totalChecks + " failures=" + totalFailures + " scenarios=" + Scenarios.Length);
        return badScenarios == 0 && totalFailures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 场景分发

    private static int RunScenario(string scenario)
    {
        switch (scenario)
        {
            case "full": return ScenarioFull(false);
            case "rollback": return ScenarioFull(true);
            case "no_key": return ScenarioNoKey();
            case "corrupt_secret": return ScenarioCorruptSecret();
            case "partial_failure": return ScenarioPartialFailure();
            case "no_arxiv": return ScenarioNoArxiv();
            case "host_too_old": return ScenarioHostTooOld();
            default: Console.Error.WriteLine("未知场景：" + scenario); return 2;
        }
    }

    // ------------------------------------------------------------------ 夹具

    private static string ConfigPath(string id) { return Path.Combine(PluginPaths.DataRoot(id), "config.json"); }
    private static string SecretPath(string id) { return Path.Combine(PluginPaths.DataRoot(id), "secret.dat"); }

    // 旧 arxiv 1.0.2 的默认样子：评分 / RSS 设置留在本地，AI / 翻译 / 文件服务器是本次要搬走的。
    private static Dictionary<string, object> LegacyArxivConfig()
    {
        Dictionary<string, object> config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        config["enabled"] = true;
        config["api_url"] = "https://api.deepseek.com/chat/completions";
        config["api_model"] = "deepseek-v4-flash";
        config["max_concurrency"] = 8;
        config["timeout_seconds"] = 180;
        config["categories"] = "cs.CV, cs.LG";
        config["exclude_categories"] = "";
        config["title_threshold"] = 7;
        config["import_count"] = 5;
        config["cache_days"] = 14;
        config["rss_enabled"] = false;
        config["file_enabled"] = true;
        config["file_url"] = "http://10.150.179.74:8900";
        config["file_account"] = "changzhou";
        return config;
    }

    private static Dictionary<string, object> LegacyArxivSecret()
    {
        Dictionary<string, object> secret = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        secret["api_key"] = "sk-legacy-deepseek-key";
        secret["translation_secret_id"] = "AKIDlegacy0001";
        secret["translation_secret_key"] = "legacySecretKey0001";
        secret["file_password"] = "legacy-file-password";
        return secret;
    }

    private static void WriteJson(string path, Dictionary<string, object> value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        JsonUtil.SaveAtomic(path, value);
    }

    private static void WriteSecret(string path, Dictionary<string, object> value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        JsonUtil.WriteDpapiJson(path, value);
    }

    private static void InstallPlugin(string id, bool enabled)
    {
        Dictionary<string, object> current = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        current["version"] = "1.0.0";
        current["enabled"] = enabled;
        WriteJson(Path.Combine(PluginPaths.PluginRoot(id), "current.json"), current);
    }

    private static void WriteLegacyPaperCache()
    {
        string path = Path.Combine(PluginPaths.DataRoot(ProviderMigration.ArxivId), "PaperCache", "papers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"version\":1,\"papers\":[]}", new UTF8Encoding(false));
    }

    // 整棵 PluginRoot 的内容指纹：相对路径 + 文件哈希，按路径排序。
    private static Dictionary<string, string> Fingerprint()
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = PluginPaths.Root;
        if (!Directory.Exists(root)) return map;
        foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            string relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
            using (SHA256 sha = SHA256.Create())
                map[relative] = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }
        return map;
    }

    private static bool SameFingerprint(Dictionary<string, string> left, Dictionary<string, string> right, string label)
    {
        List<string> problems = new List<string>();
        foreach (KeyValuePair<string, string> pair in left)
        {
            string other;
            if (!right.TryGetValue(pair.Key, out other)) problems.Add("消失 " + pair.Key);
            else if (other != pair.Value) problems.Add("被改写 " + pair.Key);
        }
        foreach (string key in right.Keys) if (!left.ContainsKey(key)) problems.Add("新增 " + key);
        if (problems.Count > 0) Console.Error.WriteLine("指纹差异（" + label + "）：" + String.Join("；", problems.ToArray()));
        return problems.Count == 0;
    }

    private static Dictionary<string, object> Bindings()
    {
        if (!File.Exists(PluginPaths.Bindings)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        return JsonUtil.Object(JsonUtil.Get(JsonUtil.LoadObject(PluginPaths.Bindings), "bindings"));
    }

    private static string BoundService(string service)
    {
        Dictionary<string, object> own = JsonUtil.Object(JsonUtil.Get(Bindings(), ProviderMigration.ArxivId));
        return JsonUtil.String(own, service, "");
    }

    // ------------------------------------------------------------------ 场景：完整迁移 / 回退保护

    private static int ScenarioFull(bool rollback)
    {
        Dictionary<string, object> arxivConfig = LegacyArxivConfig();
        Dictionary<string, object> arxivSecret = LegacyArxivSecret();
        WriteJson(ConfigPath(ProviderMigration.ArxivId), arxivConfig);
        WriteSecret(SecretPath(ProviderMigration.ArxivId), arxivSecret);
        WriteLegacyPaperCache();
        string paperCachePath = Path.Combine(PluginPaths.DataRoot(ProviderMigration.ArxivId), "PaperCache", "papers.json");
        string paperCacheBytes = Convert.ToBase64String(File.ReadAllBytes(paperCachePath));

        InstallPlugin(ProviderMigration.ArxivId, true);
        InstallPlugin(ProviderMigration.DeepSeekId, false);
        InstallPlugin(ProviderMigration.TencentId, false);
        InstallPlugin(ProviderMigration.SnapshotId, false);

        MigrationReport report = ProviderMigration.Run();

        Check(!report.AlreadyApplied, "首次运行不应判定为已迁移完成");
        Check(report.Changed, "首次运行应报告发生了写盘");
        Equal(ProviderMigration.StatusOk, report.Status(ProviderMigration.StepDeepSeek), "deepseek step 状态");
        Equal(ProviderMigration.StatusOk, report.Status(ProviderMigration.StepTencent), "tencent step 状态");
        Equal(ProviderMigration.StatusOk, report.Status(ProviderMigration.StepFileServer), "fileserver step 状态");
        Equal(ProviderMigration.StatusKept, report.Status(ProviderMigration.StepPaperCache), "papercache step 状态");
        Check(report.Warnings.Count == 0, "完整迁移不应产生 warning（实际 " + report.Warnings.Count + " 条：" + String.Join(" | ", report.Warnings.ToArray()) + "）");

        // ai-deepseek：§9.1 的五个字段，密钥进 secret.dat。
        Dictionary<string, object> deepSeek = JsonUtil.LoadObject(ConfigPath(ProviderMigration.DeepSeekId));
        Equal("https://api.deepseek.com/chat/completions", JsonUtil.String(deepSeek, "api_url", ""), "ai-deepseek api_url");
        Equal("deepseek-v4-flash", JsonUtil.String(deepSeek, "api_model", ""), "ai-deepseek api_model");
        Equal("180", Text(JsonUtil.Int(deepSeek, "timeout_seconds", 0)), "ai-deepseek timeout_seconds");
        Equal("8", Text(JsonUtil.Int(deepSeek, "max_concurrency", 0)), "ai-deepseek max_concurrency");
        Dictionary<string, object> deepSeekSecret = JsonUtil.ReadDpapiJson(SecretPath(ProviderMigration.DeepSeekId));
        Equal("sk-legacy-deepseek-key", JsonUtil.String(deepSeekSecret, "api_key", ""), "ai-deepseek secret.dat 里的 api_key");

        // translate-tencent：secret_id 是普通项（arxiv 那边是 x-secret），secret_key 进 secret.dat。
        Dictionary<string, object> tencent = JsonUtil.LoadObject(ConfigPath(ProviderMigration.TencentId));
        Equal("AKIDlegacy0001", JsonUtil.String(tencent, "secret_id", ""), "translate-tencent secret_id");
        Dictionary<string, object> tencentSecret = JsonUtil.ReadDpapiJson(SecretPath(ProviderMigration.TencentId));
        Equal("legacySecretKey0001", JsonUtil.String(tencentSecret, "secret_key", ""), "translate-tencent secret.dat 里的 secret_key");

        // paper-snapshot-sync：file_enabled → enabled，file_password 进 secret.dat。
        Dictionary<string, object> snapshot = JsonUtil.LoadObject(ConfigPath(ProviderMigration.SnapshotId));
        Check(JsonUtil.Bool(snapshot, "enabled", false), "paper-snapshot-sync enabled 应为 true（旧 file_enabled=true）");
        Equal("http://10.150.179.74:8900", JsonUtil.String(snapshot, "file_url", ""), "paper-snapshot-sync file_url");
        Equal("changzhou", JsonUtil.String(snapshot, "file_account", ""), "paper-snapshot-sync file_account");
        Dictionary<string, object> snapshotSecret = JsonUtil.ReadDpapiJson(SecretPath(ProviderMigration.SnapshotId));
        Equal("legacy-file-password", JsonUtil.String(snapshotSecret, "file_password", ""), "paper-snapshot-sync secret.dat 里的 file_password");

        // 三个 Provider 被启用 + 绑定；自动启用 ≠ 自动允许付费（§5 的确认流程不变）。
        foreach (string id in new[] { ProviderMigration.DeepSeekId, ProviderMigration.TencentId, ProviderMigration.SnapshotId })
        {
            Dictionary<string, object> current = JsonUtil.LoadObject(Path.Combine(PluginPaths.PluginRoot(id), "current.json"));
            Check(JsonUtil.Bool(current, "enabled", false), "Provider 应被启用：" + id);
        }
        Equal(ProviderMigration.DeepSeekId, BoundService(ProviderMigration.ServiceAi), "arxiv → ai_provider@1 绑定");
        Equal(ProviderMigration.TencentId, BoundService(ProviderMigration.ServiceTranslation), "arxiv → translation_provider@1 绑定");
        Equal(ProviderMigration.SnapshotId, BoundService(ProviderMigration.ServiceSnapshot), "arxiv → paper_snapshot_provider@1 绑定");
        Check(report.Activated.Count == 3, "应报告 3 个 Provider 被启用绑定（实际 " + report.Activated.Count + "）");

        // marker：step 级 + 备份路径。
        Check(File.Exists(ProviderMigration.MarkerPath), "应写出 migration-v2.1.json");
        Dictionary<string, object> marker = JsonUtil.LoadObject(ProviderMigration.MarkerPath);
        Equal("1", Text(JsonUtil.Int(marker, "version", 0)), "marker version");
        Dictionary<string, object> steps = JsonUtil.Object(JsonUtil.Get(marker, "steps"));
        Equal(ProviderMigration.StatusOk, JsonUtil.String(steps, "deepseek", ""), "marker steps.deepseek");
        Equal(ProviderMigration.StatusKept, JsonUtil.String(steps, "papercache", ""), "marker steps.papercache");
        string backup = JsonUtil.String(marker, "backup", "");
        Check(backup != "" && Directory.Exists(backup), "marker 应记录存在的备份目录（" + backup + "）");
        foreach (string name in new[]
        {
            ProviderMigration.ArxivId + ".config.json", ProviderMigration.ArxivId + ".secret.dat",
            ProviderMigration.DeepSeekId + ".current.json", ProviderMigration.TencentId + ".current.json",
            ProviderMigration.SnapshotId + ".current.json"
        })
            Check(File.Exists(Path.Combine(backup, name)), "备份应包含 " + name);
        // 备份必须是"动手之前"的状态：里面那份 current.json 还是禁用态，这样才能真正回退。
        Dictionary<string, object> backedUp = JsonUtil.LoadObject(Path.Combine(backup, ProviderMigration.DeepSeekId + ".current.json"));
        Check(!JsonUtil.Bool(backedUp, "enabled", false), "备份里的 current.json 必须是迁移前的禁用态");
        Check(File.Exists(Path.Combine(backup, ProviderMigration.ArxivId + ".config.json"))
            && File.ReadAllText(Path.Combine(backup, ProviderMigration.ArxivId + ".config.json"), Encoding.UTF8).IndexOf("api_key", StringComparison.OrdinalIgnoreCase) < 0,
            "备份里的 arxiv config 不得含 api_key（它在 secret.dat 里）");
        Check(File.Exists(ProviderMigration.LogPath), "应写出 migration-v2.1.log");
        Check(File.ReadAllText(ProviderMigration.LogPath, new UTF8Encoding(false)).IndexOf("deepseek step", StringComparison.Ordinal) >= 0, "迁移日志应记录 deepseek step");

        // 复制不删除：arxiv 的旧 config 一个键都不少、一个值都不变；secret 同样原样保留。
        Dictionary<string, object> arxivConfigAfter = JsonUtil.LoadObject(ConfigPath(ProviderMigration.ArxivId));
        foreach (KeyValuePair<string, object> pair in arxivConfig)
            Equal(Text(pair.Value), arxivConfigAfter.ContainsKey(pair.Key) ? Text(arxivConfigAfter[pair.Key]) : "<缺失>", "arxiv 旧 config 键未被删除/改写：" + pair.Key);
        Check(JsonUtil.Bool(arxivConfigAfter, "translate_enabled", false), "迁移应补上 translate_enabled=true（旧版配了翻译密钥即视为开启）");
        Check(arxivConfigAfter.Count == arxivConfig.Count + 1, "arxiv config 只应新增 translate_enabled 一个键（实际 " + arxivConfig.Count + " → " + arxivConfigAfter.Count + "）");
        Dictionary<string, object> arxivSecretAfter = JsonUtil.ReadDpapiJson(SecretPath(ProviderMigration.ArxivId));
        Check(arxivSecretAfter.Count == arxivSecret.Count, "arxiv secret 键数不应变化（" + arxivSecret.Count + " → " + arxivSecretAfter.Count + "）");
        foreach (KeyValuePair<string, object> pair in arxivSecret)
            Equal(Text(pair.Value), arxivSecretAfter.ContainsKey(pair.Key) ? Text(arxivSecretAfter[pair.Key]) : "<缺失>", "arxiv 旧 secret 键未被删除/改写：" + pair.Key);

        // 旧 paper-cache / papers.json 保留但不使用（禁止删除用户数据）。
        Check(File.Exists(paperCachePath), "旧 PaperCache/papers.json 必须保留");
        Equal(paperCacheBytes, Convert.ToBase64String(File.ReadAllBytes(paperCachePath)), "旧 PaperCache/papers.json 内容不得改动");

        // 幂等：第二次运行必须一个字节都不改。
        Dictionary<string, string> before = Fingerprint();
        MigrationReport second = ProviderMigration.Run();
        Check(second.AlreadyApplied, "第二次运行应判定为已完成");
        Check(!second.Changed, "第二次运行不应报告写盘");
        Equal(ProviderMigration.StatusKept, second.Status(ProviderMigration.StepPaperCache), "第二次运行仍应读到 papercache=kept");
        Check(SameFingerprint(before, Fingerprint(), "第二次运行"), "第二次运行不得改动任何文件");
        Check(JsonUtil.String(JsonUtil.LoadObject(ProviderMigration.MarkerPath), "backup", "") == backup, "备份路径应保持稳定");

        if (rollback)
        {
            // §11-#22：一键回退到 arxiv 1.0.2 后配置仍可直接用。旧插件读的就是它自己那份 config/secret，
            // 所以这里换"旧插件的视角"再断言一次：它要的键一个不少、值一模一样。
            Dictionary<string, object> asLegacy = JsonUtil.LoadObject(ConfigPath(ProviderMigration.ArxivId));
            foreach (string key in new[] { "api_url", "api_model", "max_concurrency", "timeout_seconds", "file_enabled", "file_url", "file_account", "title_threshold", "categories" })
                Equal(Text(arxivConfig[key]), asLegacy.ContainsKey(key) ? Text(asLegacy[key]) : "<缺失>", "回退视角：arxiv 1.0.2 仍能读到 " + key);
            foreach (string key in new[] { "api_key", "translation_secret_id", "translation_secret_key", "file_password" })
                Equal(Text(arxivSecret[key]), arxivSecretAfter.ContainsKey(key) ? Text(arxivSecretAfter[key]) : "<缺失>", "回退视角：arxiv 1.0.2 的 secret 仍有 " + key);
            // 三个 Provider 的副本必须与旧配置**同源**，否则"不回退"那条路会跑到不一样的模型 / 地址上。
            Equal(JsonUtil.String(asLegacy, "api_url", ""), JsonUtil.String(deepSeek, "api_url", ""), "回退视角：新旧 api_url 同源");
            Equal(JsonUtil.String(asLegacy, "api_model", ""), JsonUtil.String(deepSeek, "api_model", ""), "回退视角：新旧 api_model 同源");
            Equal(JsonUtil.String(asLegacy, "file_url", ""), JsonUtil.String(snapshot, "file_url", ""), "回退视角：新旧 file_url 同源");
        }
        return 0;
    }

    // ------------------------------------------------------------------ 场景：没配任何东西

    private static int ScenarioNoKey()
    {
        Dictionary<string, object> config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        config["enabled"] = true;
        config["file_enabled"] = false;
        config["file_url"] = "http://10.150.179.74:8900";
        WriteJson(ConfigPath(ProviderMigration.ArxivId), config);
        InstallPlugin(ProviderMigration.ArxivId, true);
        InstallPlugin(ProviderMigration.DeepSeekId, false);
        InstallPlugin(ProviderMigration.TencentId, false);
        InstallPlugin(ProviderMigration.SnapshotId, false);

        MigrationReport report = ProviderMigration.Run();

        Equal(ProviderMigration.StatusSkipped, report.Status(ProviderMigration.StepDeepSeek), "无 api_key 时 deepseek=skipped");
        Equal(ProviderMigration.StatusSkipped, report.Status(ProviderMigration.StepTencent), "无翻译密钥时 tencent=skipped");
        Equal(ProviderMigration.StatusOk, report.Status(ProviderMigration.StepFileServer), "有 file_url 时 fileserver=ok");

        Check(!File.Exists(ConfigPath(ProviderMigration.DeepSeekId)), "skipped 时不得创建 ai-deepseek 的 config.json");
        Check(!File.Exists(SecretPath(ProviderMigration.DeepSeekId)), "skipped 时不得创建 ai-deepseek 的 secret.dat");
        Check(!File.Exists(ConfigPath(ProviderMigration.TencentId)), "skipped 时不得创建 translate-tencent 的 config.json");
        Equal("", BoundService(ProviderMigration.ServiceAi), "skipped 时不得自动绑定 ai_provider@1");
        Equal("", BoundService(ProviderMigration.ServiceTranslation), "skipped 时不得自动绑定 translation_provider@1");

        // file_enabled=false ⇒ 只复制配置，不替用户打开。
        Dictionary<string, object> snapshot = JsonUtil.LoadObject(ConfigPath(ProviderMigration.SnapshotId));
        Equal("http://10.150.179.74:8900", JsonUtil.String(snapshot, "file_url", ""), "file_enabled=false 仍应复制 file_url");
        Check(!JsonUtil.Bool(snapshot, "enabled", false), "file_enabled=false 时 snapshot 的 enabled 应为 false");
        Dictionary<string, object> snapshotCurrent = JsonUtil.LoadObject(Path.Combine(PluginPaths.PluginRoot(ProviderMigration.SnapshotId), "current.json"));
        Check(!JsonUtil.Bool(snapshotCurrent, "enabled", false), "file_enabled=false 时不得启用 paper-snapshot-sync");
        Equal("", BoundService(ProviderMigration.ServiceSnapshot), "file_enabled=false 时不得绑定 paper_snapshot_provider@1");

        Dictionary<string, object> arxivAfter = JsonUtil.LoadObject(ConfigPath(ProviderMigration.ArxivId));
        Check(!arxivAfter.ContainsKey("translate_enabled"), "tencent 被跳过时不得擅自补 translate_enabled");
        return 0;
    }

    // ------------------------------------------------------------------ 场景：arxiv secret 坏掉

    private static int ScenarioCorruptSecret()
    {
        WriteJson(ConfigPath(ProviderMigration.ArxivId), LegacyArxivConfig());
        WriteSecret(SecretPath(ProviderMigration.ArxivId), LegacyArxivSecret());
        // 用合法 base64 但非 DPAPI 密文：ProtectedData.Unprotect 会抛 CryptographicException。
        File.WriteAllText(SecretPath(ProviderMigration.ArxivId), Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), new UTF8Encoding(false));
        InstallPlugin(ProviderMigration.ArxivId, true);
        InstallPlugin(ProviderMigration.DeepSeekId, false);
        InstallPlugin(ProviderMigration.TencentId, false);
        InstallPlugin(ProviderMigration.SnapshotId, false);

        MigrationReport report = ProviderMigration.Run();

        Equal(ProviderMigration.StatusFailed, report.Status(ProviderMigration.StepDeepSeek), "secret 坏掉时 deepseek=failed");
        Equal(ProviderMigration.StatusFailed, report.Status(ProviderMigration.StepTencent), "secret 坏掉时 tencent=failed");
        Equal(ProviderMigration.StatusFailed, report.Status(ProviderMigration.StepFileServer), "secret 坏掉时 fileserver=failed");
        Check(report.Failed, "report.Failed 应为 true");
        Check(report.Status(ProviderMigration.StepPaperCache) != ProviderMigration.StatusKept, "有 step 失败时不得补 papercache=kept");
        Check(report.Warnings.Count >= 3, "每个失败的 step 都要记 warning（实际 " + report.Warnings.Count + " 条）");
        Check(!File.Exists(ConfigPath(ProviderMigration.DeepSeekId)), "失败时不得留下半截 ai-deepseek 配置");

        // marker 绝不能把失败写成成功态。
        Dictionary<string, object> marker = JsonUtil.LoadObject(ProviderMigration.MarkerPath);
        Dictionary<string, object> steps = JsonUtil.Object(JsonUtil.Get(marker, "steps"));
        Check(!steps.ContainsKey("deepseek") && !steps.ContainsKey("tencent") && !steps.ContainsKey("fileserver") && !steps.ContainsKey("papercache"),
            "失败时三个 step 与 papercache 都不得落 marker（实际：" + JsonUtil.Serialize(steps) + "）");
        string firstBackup = JsonUtil.String(marker, "backup", "");
        Check(firstBackup != "" && Directory.Exists(firstBackup), "即使 step 失败也要先留下备份点");

        // 修好 secret 后重试：只补未完成的 step，且复用同一份备份。
        WriteSecret(SecretPath(ProviderMigration.ArxivId), LegacyArxivSecret());
        MigrationReport retry = ProviderMigration.Run();
        Check(!retry.AlreadyApplied, "重试时不应判定为已完成");
        Equal(ProviderMigration.StatusOk, retry.Status(ProviderMigration.StepDeepSeek), "修好后 deepseek=ok");
        Equal(ProviderMigration.StatusOk, retry.Status(ProviderMigration.StepTencent), "修好后 tencent=ok");
        Equal(ProviderMigration.StatusOk, retry.Status(ProviderMigration.StepFileServer), "修好后 fileserver=ok");
        Equal(ProviderMigration.StatusKept, retry.Status(ProviderMigration.StepPaperCache), "修好后补上 papercache=kept");
        Equal(firstBackup, JsonUtil.String(JsonUtil.LoadObject(ProviderMigration.MarkerPath), "backup", ""), "重试应复用同一份备份快照");
        Dictionary<string, object> deepSeek = JsonUtil.ReadDpapiJson(SecretPath(ProviderMigration.DeepSeekId));
        Equal("sk-legacy-deepseek-key", JsonUtil.String(deepSeek, "api_key", ""), "重试后 api_key 应真的搬过去");
        return 0;
    }

    // ------------------------------------------------------------------ 场景：某一步失败后只补那一步

    private static int ScenarioPartialFailure()
    {
        WriteJson(ConfigPath(ProviderMigration.ArxivId), LegacyArxivConfig());
        WriteSecret(SecretPath(ProviderMigration.ArxivId), LegacyArxivSecret());
        InstallPlugin(ProviderMigration.ArxivId, true);
        InstallPlugin(ProviderMigration.DeepSeekId, false);
        InstallPlugin(ProviderMigration.TencentId, false);
        InstallPlugin(ProviderMigration.SnapshotId, false);
        // 让 paper-snapshot-sync 的 config.json 写不进去：占位同名目录 ⇒ File.Move 抛 IOException。
        string blocked = ConfigPath(ProviderMigration.SnapshotId);
        Directory.CreateDirectory(Path.GetDirectoryName(blocked));
        Directory.CreateDirectory(blocked);

        MigrationReport report = ProviderMigration.Run();
        Equal(ProviderMigration.StatusOk, report.Status(ProviderMigration.StepDeepSeek), "部分失败：deepseek=ok");
        Equal(ProviderMigration.StatusOk, report.Status(ProviderMigration.StepTencent), "部分失败：tencent=ok");
        Equal(ProviderMigration.StatusFailed, report.Status(ProviderMigration.StepFileServer), "部分失败：fileserver=failed");
        Check(report.Status(ProviderMigration.StepPaperCache) != ProviderMigration.StatusKept, "有 step 失败时不补 papercache");
        Dictionary<string, object> steps = JsonUtil.Object(JsonUtil.Get(JsonUtil.LoadObject(ProviderMigration.MarkerPath), "steps"));
        Equal(ProviderMigration.StatusOk, JsonUtil.String(steps, "deepseek", ""), "marker 里 deepseek 已终态");
        Equal(ProviderMigration.StatusOk, JsonUtil.String(steps, "tencent", ""), "marker 里 tencent 已终态");
        Check(!steps.ContainsKey("fileserver"), "marker 里不得有 fileserver");
        Check(!steps.ContainsKey("papercache"), "marker 里不得有 papercache");

        // 删掉已完成的 step 的产物：如果重试时 deepseek 又被跑一遍，文件会被重新创建。
        File.Delete(ConfigPath(ProviderMigration.DeepSeekId));
        Directory.Delete(blocked, false);

        MigrationReport retry = ProviderMigration.Run();
        Check(!File.Exists(ConfigPath(ProviderMigration.DeepSeekId)), "已终态的 deepseek step 不得被重跑");
        Check(File.Exists(ConfigPath(ProviderMigration.SnapshotId)), "重试应把 fileserver 补上");
        Equal(ProviderMigration.StatusOk, retry.Status(ProviderMigration.StepFileServer), "重试后 fileserver=ok");
        Equal(ProviderMigration.StatusKept, retry.Status(ProviderMigration.StepPaperCache), "重试后 papercache=kept");
        Equal(ProviderMigration.StatusOk, retry.Status(ProviderMigration.StepDeepSeek), "重试时 deepseek 仍读作 ok");
        return 0;
    }

    // ------------------------------------------------------------------ 场景：没装 arxiv

    private static int ScenarioNoArxiv()
    {
        InstallPlugin(ProviderMigration.DeepSeekId, false);
        MigrationReport report = ProviderMigration.Run();
        Check(report.Warnings.Count == 1, "没装 arxiv 时应有且只有一条 warning（实际 " + report.Warnings.Count + "）");
        Check(report.Warnings.Count > 0 && report.Warnings[0].IndexOf("arxiv", StringComparison.OrdinalIgnoreCase) >= 0, "warning 应说明缺 arxiv");
        Check(!File.Exists(ProviderMigration.MarkerPath), "没装 arxiv 时不得写 marker（装了之后还能补做）");
        Check(!File.Exists(ConfigPath(ProviderMigration.DeepSeekId)), "没装 arxiv 时不得创建 provider 配置");
        Check(!report.AlreadyApplied, "没装 arxiv 时不应判定为已完成");
        return 0;
    }

    // ------------------------------------------------------------------ 场景：宿主版本过低

    private static string FutureVersion()
    {
        string host = PluginRuntime.HostVersion;
        int major = 0;
        string[] parts = host.Split('.');
        if (parts.Length > 0) Int32.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
        return (major + 1).ToString(CultureInfo.InvariantCulture) + ".0.0";
    }

    private static int ScenarioHostTooOld()
    {
        string id = "io.github.test.needs-higher-host", required = FutureVersion();
        string versionRoot = PluginPaths.VersionRoot(id, "1.0.0");
        Directory.CreateDirectory(Path.Combine(versionRoot, "bin"));
        File.WriteAllText(Path.Combine(versionRoot, "bin", "Needs.exe"), "", new UTF8Encoding(false));
        Dictionary<string, object> manifest = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        manifest["id"] = id;
        manifest["name"] = "需要新宿主";
        manifest["version"] = "1.0.0";
        manifest["api_version"] = 1;
        manifest["min_host_version"] = required;
        manifest["entry"] = "bin/Needs.exe";
        manifest["capabilities"] = new List<object>();
        manifest["provides"] = new List<object> { ProviderMigration.ServiceAi };
        WriteJson(Path.Combine(versionRoot, "plugin.json"), manifest);
        InstallPlugin(id, true);

        // 拒跑仍然要拒跑：正常运行路径不受影响。
        bool threw = false;
        try { PluginManifest.Load(versionRoot); }
        catch (InvalidDataException) { threw = true; }
        Check(threw, "Load 仍必须拒绝宿主版本不够的插件");

        PluginManifest status = PluginManifest.LoadForStatus(versionRoot);
        Check(status.HostTooOld, "LoadForStatus 应把 HostTooOld 置位");
        Equal(required, status.MinHostVersion, "LoadForStatus 仍能读到 min_host_version");
        Equal("需要新宿主", status.Name, "LoadForStatus 仍能读到插件名");

        PluginManifest resolved = PluginRuntime.ResolveForStatus(id);
        Check(resolved.HostTooOld, "ResolveForStatus 应返回 HostTooOld 的 manifest");

        // UI：插件列表必须**看得见**这个插件并说明原因，而不是静默算成"安装损坏"。
        Type todoApp = typeof(TodoApp);
        Type listType = todoApp.GetNestedType("PluginListControl", BindingFlags.NonPublic);
        Type rowType = todoApp.GetNestedType("PluginRow", BindingFlags.NonPublic);
        Check(listType != null && rowType != null, "应能通过反射拿到 PluginListControl / PluginRow");
        object view = Activator.CreateInstance(listType, true);
        Label label = new Label();
        MethodInfo reload = todoApp.GetMethod("ReloadPlugins", BindingFlags.NonPublic | BindingFlags.Static);
        Check(reload != null, "应能通过反射拿到 TodoApp.ReloadPlugins");
        reload.Invoke(null, new object[] { view, label });

        IList rows = (IList)listType.GetField("Rows").GetValue(view);
        string title = "", subtitle = "", badge = "";
        foreach (object row in rows)
        {
            if (Text(rowType.GetField("Title").GetValue(row)) != "需要新宿主") continue;
            title = Text(rowType.GetField("Title").GetValue(row));
            subtitle = Text(rowType.GetField("Subtitle").GetValue(row));
            badge = Text(rowType.GetField("Badge").GetValue(row));
        }
        Equal("需要新宿主", title, "版本不匹配的插件必须出现在列表里（不得静默）");
        Check(subtitle.IndexOf("需要主程序 " + required + " 或更高版本", StringComparison.Ordinal) >= 0, "副标题应写明所需的宿主版本（实际 [" + subtitle + "]）");
        Equal("版本不匹配", badge, "徽标应标明版本不匹配");
        Check(label.Text.IndexOf("因主程序版本过低已暂停", StringComparison.Ordinal) >= 0, "底部状态行应显式提示（实际 [" + label.Text + "]）");

        // 这类插件能被选中，但不允许被启用/配置/运行。
        MethodInfo runnable = todoApp.GetMethod("SelectedRunnablePlugin", BindingFlags.NonPublic | BindingFlags.Static);
        Check(runnable != null, "应能通过反射拿到 SelectedRunnablePlugin");
        listType.GetField("SelectedIndex").SetValue(view, 0);
        bool refused = false;
        try { runnable.Invoke(null, new object[] { view }); }
        catch (TargetInvocationException ex) { refused = ex.InnerException != null && ex.InnerException.Message.IndexOf("需要主程序", StringComparison.Ordinal) >= 0; }
        Check(refused, "选中版本不匹配的插件时启用/配置/运行必须被明确拒绝");
        return 0;
    }
}
