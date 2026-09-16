using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace RainmeterBackend
{
    // plugin.json 里 "uses" 的一项：本插件需要一个服务。
    // binding_key 是宿主呈现在 context.services / RW_SERVICE_* 里的键，缺省 = service 去掉 @版本。
    internal sealed class ServiceUse
    {
        public string Service = "", BindingKey = "";
        public bool Optional = true;
    }

    // 一次服务解析的结果。Available=false 时 Reason 说明原因（不是失败）。
    internal sealed class ServiceResolution
    {
        public string Service = "", BindingKey = "", ProviderId = "", ProviderName = "", Billing = "", Reason = "";
        public bool Available, AutoBound;
        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>{
                {"provider_id",Available?ProviderId:null},
                {"provider_name",Available?ProviderName:""},
                {"available",Available},
                {"reason",Available?"":Reason},
                {"billing",Billing??""}};
        }
    }

    // 已安装插件的服务提供能力（宽松解析：不因某个插件损坏而影响整体扫描）。
    internal sealed class ServiceCandidate
    {
        public string Id = "", Name = "", Version = "", Billing = "";
        public bool Enabled;
        public List<string> Provides = new List<string>();
    }

    // 跨插件服务绑定表（plugin-bindings.json）与服务解析。
    //
    // 三条铁律（见 docs/V2.1-PROVIDER-INTERFACE.md §3、§8）：
    //   1. 绑定表由宿主独占写入，插件看不到表、只能看到解析结果；
    //   2. 解析绝不按 priority / 安装顺序 / 字典序挑 provider，多候选一律视为未绑定；
    //   3. 绑定校验失败（卸载 / 禁用 / 版本不符）视为"没有该 provider"，不回落也不自动改绑。
    internal static class ServiceRegistry
    {
        public const string ReasonNotInstalled = "not_installed";
        public const string ReasonDisabled = "disabled";
        public const string ReasonAmbiguous = "ambiguous";
        public const string ReasonVersionMismatch = "version_mismatch";

        private const string ServicePattern = @"^[a-z0-9_]+@[0-9]+$";
        private const string BindingKeyPattern = @"^[a-z0-9_]+$";

        public static bool ValidService(string service) { return Regex.IsMatch(service ?? "", ServicePattern); }
        public static bool ValidBindingKey(string key) { return Regex.IsMatch(key ?? "", BindingKeyPattern); }
        public static string KeyOf(string service)
        {
            if (String.IsNullOrEmpty(service)) return "";
            int index = service.IndexOf('@');
            return index < 0 ? service : service.Substring(0, index);
        }

        // 宿主扫描"谁提供了什么"。只看已安装 + 有 current.json 的插件；损坏的插件被静默跳过。
        public static List<ServiceCandidate> Installed()
        {
            List<ServiceCandidate> list = new List<ServiceCandidate>();
            try
            {
                if (!Directory.Exists(PluginPaths.Plugins)) return list;
                foreach (string root in Directory.GetDirectories(PluginPaths.Plugins))
                {
                    try
                    {
                        ServiceCandidate candidate = new ServiceCandidate { Id = Path.GetFileName(root) };
                        string currentPath = Path.Combine(root, "current.json");
                        if (File.Exists(currentPath))
                        {
                            Dictionary<string, object> current = JsonUtil.LoadObject(currentPath);
                            candidate.Enabled = JsonUtil.Bool(current, "enabled", false);
                            candidate.Version = JsonUtil.String(current, "version", "");
                        }
                        string manifestPath = candidate.Version == "" ? "" : Path.Combine(root, "versions", candidate.Version, "plugin.json");
                        if (manifestPath == "" || !File.Exists(manifestPath)) { list.Add(candidate); continue; }
                        Dictionary<string, object> manifest = JsonUtil.LoadObject(manifestPath);
                        candidate.Name = JsonUtil.String(manifest, "name", candidate.Id);
                        candidate.Billing = JsonUtil.String(manifest, "billing", "");
                        candidate.Provides = JsonUtil.Array(JsonUtil.Get(manifest, "provides")).Select(Convert.ToString)
                            .Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        list.Add(candidate);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        public static string BoundProvider(string consumerId, string service)
        {
            if (String.IsNullOrWhiteSpace(consumerId) || String.IsNullOrWhiteSpace(service)) return "";
            Dictionary<string, object> own = JsonUtil.Object(JsonUtil.Get(ReadBindings(), consumerId));
            return JsonUtil.String(own, service, "").Trim();
        }

        private static Dictionary<string, object> ReadBindings()
        {
            try
            {
                if (!File.Exists(PluginPaths.Bindings)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                return JsonUtil.Object(JsonUtil.Get(JsonUtil.LoadObject(PluginPaths.Bindings), "bindings"));
            }
            catch { return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
        }

        // 只有宿主调用。providerId 为空 = 解除绑定。
        public static void SetBinding(string consumerId, string service, string providerId)
        {
            if (String.IsNullOrWhiteSpace(consumerId) || !ValidService(service)) throw new InvalidDataException("服务绑定参数无效");
            using (Mutex mutex = new Mutex(false, @"Global\RainmeterPluginBindings"))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new TimeoutException("插件绑定表正忙");
                    Dictionary<string, object> root = File.Exists(PluginPaths.Bindings)
                        ? JsonUtil.LoadObject(PluginPaths.Bindings)
                        : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    root["version"] = 1;
                    Dictionary<string, object> all = JsonUtil.Object(JsonUtil.Get(root, "bindings")); root["bindings"] = all;
                    Dictionary<string, object> own = JsonUtil.Object(JsonUtil.Get(all, consumerId)); all[consumerId] = own;
                    if (String.IsNullOrWhiteSpace(providerId)) own.Remove(service); else own[service] = providerId.Trim();
                    if (own.Count == 0) all.Remove(consumerId);
                    PluginPaths.Ensure();
                    JsonUtil.SaveAtomic(PluginPaths.Bindings, root);
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        public static ServiceResolution Resolve(string consumerId, ServiceUse use, bool autoBind)
        {
            ServiceResolution result = new ServiceResolution { Service = use.Service, BindingKey = use.BindingKey };
            List<ServiceCandidate> installed = Installed();
            string bound = BoundProvider(consumerId, use.Service);

            if (bound != "")
            {
                ServiceCandidate claimed = installed.FirstOrDefault(x => String.Equals(x.Id, bound, StringComparison.OrdinalIgnoreCase));
                if (claimed == null || claimed.Version == "") { result.Reason = ReasonNotInstalled; return result; }
                if (!claimed.Enabled) { result.Reason = ReasonDisabled; return result; }
                if (!claimed.Provides.Contains(use.Service, StringComparer.OrdinalIgnoreCase)) { result.Reason = ReasonVersionMismatch; return result; }
                result.Available = true; result.ProviderId = claimed.Id; result.ProviderName = claimed.Name; result.Billing = claimed.Billing;
                return result;
            }

            List<ServiceCandidate> candidates = installed
                .Where(x => x.Enabled && x.Provides.Contains(use.Service, StringComparer.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 1)
            {
                ServiceCandidate only = candidates[0];
                if (autoBind && !String.IsNullOrWhiteSpace(consumerId))
                {
                    try { SetBinding(consumerId, use.Service, only.Id); result.AutoBound = true; Audit("auto-bind " + consumerId + " " + use.Service + " -> " + only.Id); }
                    catch (Exception ex) { Audit("auto-bind failed " + consumerId + " " + use.Service + " : " + ex.Message); }
                }
                result.Available = true; result.ProviderId = only.Id; result.ProviderName = only.Name; result.Billing = only.Billing;
                return result;
            }
            if (candidates.Count > 1) { result.Reason = ReasonAmbiguous; return result; }
            bool sameName = installed.Any(x => x.Enabled && x.Provides.Contains(KeyOf(use.Service), StringComparer.OrdinalIgnoreCase));
            result.Reason = sameName ? ReasonVersionMismatch : ReasonNotInstalled;
            return result;
        }

        public static List<ServiceResolution> ResolveAll(string consumerId, IEnumerable<ServiceUse> uses, bool autoBind)
        {
            List<ServiceResolution> list = new List<ServiceResolution>();
            foreach (ServiceUse use in uses ?? Enumerable.Empty<ServiceUse>())
            {
                if (use == null || !ValidService(use.Service) || !ValidBindingKey(use.BindingKey)) continue;
                list.Add(Resolve(consumerId, use, autoBind));
            }
            return list;
        }

        public static Dictionary<string, object> ToContextServices(IEnumerable<ServiceResolution> resolutions)
        {
            Dictionary<string, object> services = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (ServiceResolution resolution in resolutions ?? Enumerable.Empty<ServiceResolution>())
                services[resolution.BindingKey] = resolution.ToJson();
            return services;
        }

        // 让插件不调 Broker 就能判断"有没有可用的 provider"。
        public static Dictionary<string, string> ToEnvironment(IEnumerable<ServiceResolution> resolutions)
        {
            Dictionary<string, string> environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ServiceResolution resolution in resolutions ?? Enumerable.Empty<ServiceResolution>())
            {
                string key = "RW_SERVICE_" + Regex.Replace(resolution.BindingKey.ToUpperInvariant(), @"[^A-Z0-9_]", "_");
                environment[key + "_PROVIDER"] = resolution.Available ? resolution.ProviderId : "";
                environment[key + "_NAME"] = resolution.Available ? resolution.ProviderName : "";
            }
            return environment;
        }

        // Broker 用：把 service 解析成 provider id（"" = 没有可用 provider，而不是失败）。
        public static string ProviderFor(string consumerId, string service)
        {
            ServiceResolution resolution = Resolve(consumerId, new ServiceUse { Service = service, BindingKey = KeyOf(service) }, true);
            return resolution.Available ? resolution.ProviderId : "";
        }

        public static string ReasonFor(string consumerId, string service)
        {
            return Resolve(consumerId, new ServiceUse { Service = service, BindingKey = KeyOf(service) }, false).Reason;
        }

        public static string BillingOf(string providerId)
        {
            ServiceCandidate candidate = Installed().FirstOrDefault(x => String.Equals(x.Id, providerId, StringComparison.OrdinalIgnoreCase));
            return candidate == null ? "" : candidate.Billing;
        }

        // 审计：每次跨插件调用一行，绝不记录 input 正文（可能含论文摘要）。
        public static void Audit(string line)
        {
            try
            {
                PluginPaths.Ensure();
                string path = Path.Combine(PluginPaths.Logs, "service-call.log");
                string text = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture) + " | " + line + "\r\n";
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { File.AppendAllText(path, text, RuntimeUtil.Utf8NoBom); return; }
                    catch (IOException) { Thread.Sleep(40); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(40); }
                }
            }
            catch { }
        }
    }
}
