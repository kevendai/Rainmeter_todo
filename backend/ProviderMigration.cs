using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RainmeterBackend
{
    // 一次 v2.1 迁移的战果，供调用方记日志/弹提示，也供探针断言。
    internal sealed class MigrationReport
    {
        public string MarkerPath = "";
        public string BackupDir = "";
        // marker 里三个待办 step 都已是终态 ⇒ 本次什么都没做。
        public bool AlreadyApplied;
        // 本次是否真的写盘（provider 的 config/secret/current.json、绑定表、arxiv 的 translate_enabled）。
        public bool Changed;
        // step -> ok / skipped / kept / failed（failed 只存在于内存，绝不落 marker）。
        public Dictionary<string, object> Steps = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings = new List<string>();
        // 本次被启用并绑定到 arxiv 的 provider id。
        public List<string> Activated = new List<string>();

        public string Status(string step)
        {
            object value;
            return Steps.TryGetValue(step, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "absent";
        }
        public bool Failed { get { return ProviderMigration.PendingSteps.Any(s => Status(s) == ProviderMigration.StatusFailed); } }
    }

    // v2.1 配置迁移（docs/V2.1-PROVIDER-INTERFACE.md §9.1 / §9.2）。
    //
    // 铁律：**复制，不删除**。arxiv 的 config.json / secret.dat 里一个键都不删，只是把对应值
    // 复制一份给新 Provider。这样"一键回退到 arxiv 1.0.2"回来时配置直接可用，否则 §10 的回滚
    // 保护就是空话。清理旧键推迟到 arxiv 2.1.0，且清理前必须再问维护者。
    //
    // 原子性：① 写 provider 的 config/secret（都是新文件）→ ② 读回逐键校验 → ③ 才把该 step
    // 落进 marker。某 step 失败 ⇒ 只在内存记 failed，**绝不写 marker**，下次启动重试。
    // marker 是 step 级的，所以"重复运行只补未完成的 step"与"失败不写 marker"同时成立。
    //
    // ⚠️ 与 §9.2 的一处实现口径（已写进规格 §9.2 落地说明）：规格写的"走既有 TodoBackupService
    // 快照"在实际代码里只能人工交互（便携备份要用户输密码），自动迁移走不了那条路，改为沿用
    // **v2 迁移既有的** MigrationBackups 树做定向快照：只备份本次可能读到/写到的那些文件。
    internal static class ProviderMigration
    {
        public const string ArxivId = "io.github.kevendai.arxiv";
        public const string DeepSeekId = "io.github.kevendai.ai-deepseek";
        public const string TencentId = "io.github.kevendai.translate-tencent";
        public const string SnapshotId = "io.github.kevendai.paper-snapshot-sync";

        public const string ServiceAi = "ai_provider@1";
        public const string ServiceTranslation = "translation_provider@1";
        public const string ServiceSnapshot = "paper_snapshot_provider@1";

        public const string StepDeepSeek = "deepseek";
        public const string StepTencent = "tencent";
        public const string StepFileServer = "fileserver";
        public const string StepPaperCache = "papercache";

        public const string StatusOk = "ok";
        public const string StatusSkipped = "skipped";
        public const string StatusKept = "kept";
        public const string StatusFailed = "failed";

        public static readonly string[] PendingSteps = new[] { StepDeepSeek, StepTencent, StepFileServer };

        public static string MarkerPath { get { return Path.Combine(PluginPaths.Root, "migration-v2.1.json"); } }
        public static string LogPath { get { return Path.Combine(PluginPaths.Logs, "migration-v2.1.log"); } }

        // 终态 = 不需要再重试的状态（failed 不是终态）。
        public static bool Terminal(string status)
        {
            return status == StatusOk || status == StatusSkipped || status == StatusKept;
        }

        public static MigrationReport Run()
        {
            PluginPaths.Ensure();
            MigrationReport report = new MigrationReport { MarkerPath = MarkerPath };

            Dictionary<string, object> marker = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(MarkerPath))
            {
                try { marker = JsonUtil.LoadObject(MarkerPath); }
                catch (Exception ex) { Log("marker 无法解析，本次重建：" + ex.Message); marker = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
            }
            Dictionary<string, object> persisted = Copy(JsonUtil.Object(JsonUtil.Get(marker, "steps")));
            report.Steps = Copy(persisted);

            if (!PendingSteps.Any(delegate (string step) { return !Terminal(report.Status(step)); }))
            {
                report.AlreadyApplied = true;
                return report;
            }
            if (!IsArxivInstalled())
            {
                // 没有 arxiv 就没有旧配置可搬。**不写 marker**：用户之后装上 arxiv 还能补做。
                Warn(report, "未检测到 arxiv 插件，跳过 v2.1 配置迁移");
                return report;
            }
            if (!Backup(marker, report)) return report;

            // 上游数据只读一次。arxiv 的 secret 解不开时三个 step 都会失败——这是正确行为：
            // 不能假装搬成功了。config.json 坏掉则退化为空表，只影响"该不该启用"的判断。
            Dictionary<string, object> arxivConfig = ReadConfig(ArxivId, "arxiv config");
            Dictionary<string, object> arxivSecret = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string secretError = "";
            try { arxivSecret = ReadSecret(ArxivId); }
            catch (Exception ex) { secretError = ex.Message; }

            RunStep(report, persisted, marker, StepDeepSeek, delegate { return MigrateDeepSeek(arxivConfig, arxivSecret, secretError, report); });
            RunStep(report, persisted, marker, StepTencent, delegate { return MigrateTencent(arxivConfig, arxivSecret, secretError, report); });
            RunStep(report, persisted, marker, StepFileServer, delegate { return MigrateFileServer(arxivConfig, arxivSecret, secretError, report); });

            // 旧 paper-cache / papers.json（无 profile_hash）**保留但不使用**，禁止删除用户数据（§9.2）。
            // 只有三个 step 都没失败才落这一笔：否则"任一步失败→不写 marker"就破了。
            if (!report.Failed && !Terminal(report.Status(StepPaperCache)))
            {
                persisted[StepPaperCache] = StatusKept;
                report.Steps[StepPaperCache] = StatusKept;
                SaveMarker(marker, persisted, report);
            }
            return report;
        }

        // ---------------------------------------------------------------- steps

        private static string MigrateDeepSeek(Dictionary<string, object> arxivConfig, Dictionary<string, object> arxivSecret, string secretError, MigrationReport report)
        {
            if (secretError != "") throw new InvalidDataException("arxiv secret 无法读取：" + secretError);
            string apiKey = Value(arxivSecret, "api_key");
            if (apiKey == "")
            {
                Log("deepseek step：arxiv 未配置 api_key，跳过（不自动启用付费 Provider）");
                return StatusSkipped;
            }
            // §9.1：api_url / api_model / api_key / timeout_seconds / max_concurrency → ai-deepseek
            Dictionary<string, object> wantedConfig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "api_url", "api_model", "timeout_seconds", "max_concurrency" })
            {
                object value = Get(arxivConfig, key);
                if (!IsBlank(value)) wantedConfig[key] = value;
            }
            Dictionary<string, object> wantedSecret = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            wantedSecret["api_key"] = apiKey;
            WriteProvider(DeepSeekId, wantedConfig, wantedSecret, report);
            Activate(report, DeepSeekId, ServiceAi);
            Log("deepseek step：已复制 " + wantedConfig.Count + " 个配置项 + api_key 到 " + DeepSeekId);
            return StatusOk;
        }

        private static string MigrateTencent(Dictionary<string, object> arxivConfig, Dictionary<string, object> arxivSecret, string secretError, MigrationReport report)
        {
            if (secretError != "") throw new InvalidDataException("arxiv secret 无法读取：" + secretError);
            string secretId = Value(arxivSecret, "translation_secret_id"), secretKey = Value(arxivSecret, "translation_secret_key");
            if (secretId == "" || secretKey == "")
            {
                Log("tencent step：arxiv 未配置完整的 translation_secret_id/key，跳过");
                return StatusSkipped;
            }
            // arxiv 里 secret_id 标了 x-secret（存在 secret.dat 里），translate-tencent 里它是普通字符串项。
            Dictionary<string, object> wantedConfig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            wantedConfig["secret_id"] = secretId;
            Dictionary<string, object> wantedSecret = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            wantedSecret["secret_key"] = secretKey;
            WriteProvider(TencentId, wantedConfig, wantedSecret, report);
            Activate(report, TencentId, ServiceTranslation);
            // 旧版本里"配了翻译密钥就等于翻译开着"。§9.1 新增的 translate_enabled 要把这个事实带过去，
            // 否则默认值会让老用户升级后翻译静默失效。**只补不改**：用户已显式设过就不动。
            Dictionary<string, object> arxivConfigPath = ReadConfig(ArxivId, "arxiv config");
            if (!arxivConfigPath.ContainsKey("translate_enabled"))
            {
                arxivConfigPath["translate_enabled"] = true;
                JsonUtil.SaveAtomic(Path.Combine(PluginPaths.DataRoot(ArxivId), "config.json"), arxivConfigPath);
                report.Changed = true;
            }
            Log("tencent step：已复制 secret_id + secret_key 到 " + TencentId);
            return StatusOk;
        }

        private static string MigrateFileServer(Dictionary<string, object> arxivConfig, Dictionary<string, object> arxivSecret, string secretError, MigrationReport report)
        {
            if (secretError != "") throw new InvalidDataException("arxiv secret 无法读取：" + secretError);
            string fileUrl = Value(arxivConfig, "file_url");
            if (fileUrl == "")
            {
                Log("fileserver step：arxiv 未配置 file_url，跳过");
                return StatusSkipped;
            }
            bool fileEnabled = Bool(arxivConfig, "file_enabled", false);
            Dictionary<string, object> wantedConfig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            wantedConfig["enabled"] = fileEnabled;
            wantedConfig["file_url"] = fileUrl;
            string account = Value(arxivConfig, "file_account");
            if (account != "") wantedConfig["file_account"] = account;
            Dictionary<string, object> wantedSecret = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string password = Value(arxivSecret, "file_password");
            if (password != "") wantedSecret["file_password"] = password;
            WriteProvider(SnapshotId, wantedConfig, wantedSecret, report);
            // 配置照搬，但只有用户本来就开着同步才替他启用+绑定（关掉是用户的明确意图）。
            if (fileEnabled) Activate(report, SnapshotId, ServiceSnapshot);
            else Log("fileserver step：file_enabled=false，仅复制配置，不启用 " + SnapshotId);
            Log("fileserver step：已复制文件服务器配置到 " + SnapshotId);
            return StatusOk;
        }

        // ---------------------------------------------------------------- 基础设施

        private static void RunStep(MigrationReport report, Dictionary<string, object> persisted, Dictionary<string, object> marker, string step, Func<string> action)
        {
            string current = report.Status(step);
            if (Terminal(current)) return;
            string status;
            try { status = action(); }
            catch (Exception ex)
            {
                report.Steps[step] = StatusFailed;
                Warn(report, step + " 迁移失败（未写 marker，下次启动重试）：" + ex.Message);
                return;
            }
            report.Steps[step] = status;
            persisted[step] = status;
            SaveMarker(marker, persisted, report);
        }

        private static void SaveMarker(Dictionary<string, object> marker, Dictionary<string, object> persisted, MigrationReport report)
        {
            marker["version"] = 1;
            marker["applied_at"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            if (report.BackupDir != "") marker["backup"] = report.BackupDir;
            marker["steps"] = persisted;
            JsonUtil.SaveAtomic(MarkerPath, marker);
        }

        private static bool Backup(Dictionary<string, object> marker, MigrationReport report)
        {
            string previous = Value(marker, "backup");
            if (previous != "" && Directory.Exists(previous))
            {
                // 上一次跑了一半（某 step failed）⇒ 复用同一份快照，不堆垃圾。
                report.BackupDir = previous;
                return true;
            }
            try
            {
                string dir = Path.Combine(PluginPaths.Root, "MigrationBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-v21");
                Directory.CreateDirectory(dir);
                foreach (string path in SnapshotTargets()) if (File.Exists(path)) File.Copy(path, Path.Combine(dir, SnapshotName(path)), true);
                report.BackupDir = dir;
                // 立刻把备份路径留痕（不写任何 step），这样下一次重试会复用同一份快照。
                SaveMarker(marker, Copy(JsonUtil.Object(JsonUtil.Get(marker, "steps"))), report);
                return true;
            }
            catch (Exception ex)
            {
                // 备份失败绝不动手：宁可不迁，也不能在没有回退点的前提下改用户配置。
                Warn(report, "备份失败，本次不做 v2.1 迁移：" + ex.Message);
                return false;
            }
        }

        private static IEnumerable<string> SnapshotTargets()
        {
            yield return Path.Combine(PluginPaths.DataRoot(ArxivId), "config.json");
            yield return Path.Combine(PluginPaths.DataRoot(ArxivId), "secret.dat");
            foreach (string id in new[] { DeepSeekId, TencentId, SnapshotId })
            {
                yield return Path.Combine(PluginPaths.DataRoot(id), "config.json");
                yield return Path.Combine(PluginPaths.DataRoot(id), "secret.dat");
                yield return Path.Combine(PluginPaths.PluginRoot(id), "current.json");
            }
            yield return PluginPaths.Bindings;
        }

        // 快照里同名文件会互相覆盖（三个 provider 都叫 config.json / secret.dat）⇒ 加 id 前缀。
        private static string SnapshotName(string path)
        {
            string name = Path.GetFileName(path);
            if (name == "plugin-bindings.json") return "plugin-bindings.json";
            string owner = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            return owner + "." + name;
        }

        // 写 provider 的 config/secret → 读回逐键校验。目标里已有的键**不覆盖**（用户已设置的值优先，
        // 也让 failed 之后的重试能收敛而不是反复重写）。
        private static void WriteProvider(string id, Dictionary<string, object> wantedConfig, Dictionary<string, object> wantedSecret, MigrationReport report)
        {
            string data = PluginPaths.DataRoot(id);
            Directory.CreateDirectory(data);
            string configPath = Path.Combine(data, "config.json"), secretPath = Path.Combine(data, "secret.dat");

            Dictionary<string, object> config = File.Exists(configPath)
                ? JsonUtil.LoadObject(configPath) : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int configWritten = 0;
            foreach (KeyValuePair<string, object> pair in wantedConfig)
                if (!config.ContainsKey(pair.Key)) { config[pair.Key] = pair.Value; configWritten++; }
            if (configWritten > 0) { JsonUtil.SaveAtomic(configPath, config); report.Changed = true; }

            if (wantedSecret.Count > 0)
            {
                Dictionary<string, object> secret = File.Exists(secretPath)
                    ? JsonUtil.ReadDpapiJson(secretPath) : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                int secretWritten = 0;
                foreach (KeyValuePair<string, object> pair in wantedSecret)
                    if (!secret.ContainsKey(pair.Key)) { secret[pair.Key] = pair.Value; secretWritten++; }
                if (secretWritten > 0) { JsonUtil.WriteDpapiJson(secretPath, secret); report.Changed = true; }
            }

            // 读回校验（规格 §9.2 第 ② 步）。
            Dictionary<string, object> back = File.Exists(configPath)
                ? JsonUtil.LoadObject(configPath) : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in wantedConfig)
                if (!Same(Get(back, pair.Key), pair.Value)) throw new InvalidDataException(id + " 配置读回校验失败：" + pair.Key);
            if (wantedSecret.Count > 0)
            {
                if (!File.Exists(secretPath)) throw new InvalidDataException(id + " 敏感配置未落盘");
                Dictionary<string, object> secretBack = JsonUtil.ReadDpapiJson(secretPath);
                foreach (KeyValuePair<string, object> pair in wantedSecret)
                    if (Get(secretBack, pair.Key) == null) throw new InvalidDataException(id + " 敏感配置读回校验失败：" + pair.Key);
            }
        }

        // 自动"安装"由 BootstrapBundled 负责（官方插件随包安装）。这里只负责"用户在旧版配好了
        // ⇒ 替他启用并绑定"。**自动启用 ≠ 自动允许付费**：首次 AI 调用仍走 §5 的确认流程（§9.2）。
        private static void Activate(MigrationReport report, string providerId, string service)
        {
            if (!SetEnabled(providerId, true)) { Log("无法启用 " + providerId + "（未安装？），跳过绑定"); return; }
            try
            {
                ServiceRegistry.SetBinding(ArxivId, service, providerId);
                ServiceRegistry.Audit("migrate " + service + " -> " + providerId + " (" + ArxivId + ")");
                report.Activated.Add(providerId);
            }
            catch (Exception ex) { Log("绑定 " + service + " -> " + providerId + " 失败：" + ex.Message); }
        }

        private static bool SetEnabled(string id, bool enabled)
        {
            string path = Path.Combine(PluginPaths.PluginRoot(id), "current.json");
            if (!File.Exists(path)) return false;
            try
            {
                Dictionary<string, object> current = JsonUtil.LoadObject(path);
                if (JsonUtil.Bool(current, "enabled", false) == enabled) return true;
                current["enabled"] = enabled;
                JsonUtil.SaveAtomic(path, current);
                return true;
            }
            catch (Exception ex) { Log("改写 " + id + " 的 current.json 失败：" + ex.Message); return false; }
        }

        private static bool IsArxivInstalled()
        {
            return File.Exists(Path.Combine(PluginPaths.PluginRoot(ArxivId), "current.json"));
        }

        private static Dictionary<string, object> ReadConfig(string id, string label)
        {
            string path = Path.Combine(PluginPaths.DataRoot(id), "config.json");
            if (!File.Exists(path)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try { return JsonUtil.LoadObject(path); }
            catch (Exception ex) { Log(label + " 无法解析（按空配置继续）：" + ex.Message); return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
        }

        private static Dictionary<string, object> ReadSecret(string id)
        {
            string path = Path.Combine(PluginPaths.DataRoot(id), "secret.dat");
            if (!File.Exists(path)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return JsonUtil.ReadDpapiJson(path);
        }

        private static Dictionary<string, object> Copy(Dictionary<string, object> source)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (source != null) foreach (KeyValuePair<string, object> pair in source) result[pair.Key] = pair.Value;
            return result;
        }

        // 大小写不敏感取值：LoadObject 走 JavaScriptSerializer，键是大小写敏感的。
        private static object Get(Dictionary<string, object> value, string key)
        {
            if (value == null) return null;
            object direct;
            if (value.TryGetValue(key, out direct)) return direct;
            foreach (KeyValuePair<string, object> pair in value)
                if (String.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }

        private static string Value(Dictionary<string, object> value, string key)
        {
            object found = Get(value, key);
            return found == null ? "" : (Convert.ToString(found, CultureInfo.InvariantCulture) ?? "").Trim();
        }

        private static bool Bool(Dictionary<string, object> value, string key, bool fallback)
        {
            object found = Get(value, key);
            if (found == null) return fallback;
            if (found is bool) return (bool)found;
            bool parsed;
            return Boolean.TryParse(Convert.ToString(found, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        private static bool IsBlank(object value)
        {
            return value == null || (value is string && ((string)value).Trim() == "");
        }

        private static bool Same(object left, object right)
        {
            if (left == null || right == null) return left == right;
            return String.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private static void Warn(MigrationReport report, string line)
        {
            report.Warnings.Add(line);
            Log(line);
        }

        private static void Log(string line)
        {
            try
            {
                PluginPaths.Ensure();
                File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture) + " | " + line + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
