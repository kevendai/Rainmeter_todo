using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RainmeterBackend
{
    // 设置页的一行（规格 §7.4）。x- 前缀的提示字段**只影响显示**，不改变配置本身的语义。
    //
    //   x-section : 分组名（缺省"基本"）
    //   x-order   : 组内与全局的排序键
    //   x-advanced: 折进「高级设置」，默认收起
    //   x-service : 这一行是**服务绑定**（下拉框），值写 plugin-bindings.json，**不写 config.json**
    //   x-requires: 该行依赖的服务；解析不到可用 provider 时控件置灰（值不动）
    internal sealed class SettingsField
    {
        public string Key = "", Type = "string", Title = "", Section = "", Service = "", Requires = "";
        public int Order = 999;
        public bool Advanced, Secret, Address;
        public Dictionary<string, object> Property = new Dictionary<string, object>();
    }

    internal sealed class SettingsSection
    {
        public string Name = "";
        public bool Advanced;
        public List<SettingsField> Fields = new List<SettingsField>();
    }

    // 一个服务绑定行在界面上的状态。全部由「已安装插件 + 绑定表」推导，不含任何业务判断。
    internal sealed class ServiceRowState
    {
        public string Service = "", Title = "", Bound = "", BoundName = "", Reason = "", ReasonText = "", Billing = "";
        public bool Declared, Available, Ambiguous, Optional = true;
        public bool ProviderInstalledButDisabled;
        public string DisabledProviderId = "", DisabledProviderName = "";
        public List<ServiceCandidate> Candidates = new List<ServiceCandidate>();

        // 界面上给的两个"修一把"入口：没装 ⇒ 去市场；装了没启用 ⇒ 直接启用。
        public bool InstallNeeded { get { return !Available && !ProviderInstalledButDisabled; } }
        public bool NeedsEnable { get { return !Available && ProviderInstalledButDisabled; } }
    }

    internal static class PluginSettingsLayout
    {
        public const string BasicSection = "基本";
        public const string AdvancedSection = "高级设置";

        public static List<SettingsField> Parse(Dictionary<string, object> properties)
        {
            List<SettingsField> fields = new List<SettingsField>();
            foreach (KeyValuePair<string, object> pair in properties ?? new Dictionary<string, object>())
            {
                Dictionary<string, object> property = JsonUtil.Object(pair.Value);
                if (property.Count == 0) continue;
                SettingsField field = new SettingsField();
                field.Key = pair.Key;
                field.Property = property;
                field.Type = JsonUtil.String(property, "type", "string");
                field.Title = JsonUtil.String(property, "title", pair.Key);
                field.Section = JsonUtil.String(property, "x-section", "").Trim();
                field.Service = JsonUtil.String(property, "x-service", "").Trim();
                field.Requires = JsonUtil.String(property, "x-requires", "").Trim();
                field.Order = JsonUtil.Int(property, "x-order", 999);
                field.Advanced = JsonUtil.Bool(property, "x-advanced", false);
                field.Secret = JsonUtil.Bool(property, "x-secret", false)
                    || String.Equals(field.Type, "password", StringComparison.OrdinalIgnoreCase);
                field.Address = JsonUtil.Bool(property, "x-address", false);
                fields.Add(field);
            }
            fields.Sort(CompareFields);
            return fields;
        }

        private static int CompareFields(SettingsField left, SettingsField right)
        {
            // 高级项一律排在最后（它们要一起折进「高级设置」）；其余按 x-order，同 order 用 key 保证稳定。
            int advanced = (left.Advanced ? 1 : 0).CompareTo(right.Advanced ? 1 : 0);
            if (advanced != 0) return advanced;
            int order = left.Order.CompareTo(right.Order);
            if (order != 0) return order;
            return String.CompareOrdinal(left.Key, right.Key);
        }

        // 分组顺序 = 组内最小 x-order（规格 §7.4）。因为入参已经按 order 排好，先出现的组就是最小 order。
        // 高级项**一定**合并进同一个「高级设置」组，不按 x-section 拆开（规格 §7.4 只允许一个折叠区）。
        public static List<SettingsSection> Group(IEnumerable<SettingsField> fields)
        {
            List<SettingsSection> sections = new List<SettingsSection>();
            Dictionary<string, SettingsSection> byName = new Dictionary<string, SettingsSection>(StringComparer.OrdinalIgnoreCase);
            foreach (SettingsField field in fields ?? Enumerable.Empty<SettingsField>())
            {
                string name = field.Advanced ? AdvancedSection : (field.Section == "" ? BasicSection : field.Section);
                SettingsSection section;
                if (!byName.TryGetValue(name, out section))
                {
                    section = new SettingsSection { Name = name, Advanced = field.Advanced };
                    byName[name] = section;
                    sections.Add(section);
                }
                section.Fields.Add(field);
            }
            return sections;
        }

        public static bool IsServiceField(SettingsField field)
        {
            return field != null && !String.IsNullOrWhiteSpace(field.Service);
        }

        // 把服务渲染成人话时用的名字：优先用声明该服务的那个 x-service 行的 title（插件自己写的）。
        public static string ServiceLabel(IEnumerable<SettingsField> fields, string service)
        {
            foreach (SettingsField field in fields ?? Enumerable.Empty<SettingsField>())
                if (String.Equals(field.Service, service, StringComparison.OrdinalIgnoreCase) && field.Title != "") return field.Title;
            return service ?? "";
        }

        public static ServiceUse UseOf(PluginManifest manifest, string service)
        {
            if (manifest == null || manifest.Uses == null) return null;
            return manifest.Uses.FirstOrDefault(x => String.Equals(x.Service, service, StringComparison.OrdinalIgnoreCase));
        }

        public static bool DeclaresService(PluginManifest manifest, string service)
        {
            return UseOf(manifest, service) != null;
        }

        public static ServiceRowState Inspect(PluginManifest manifest, SettingsField field)
        {
            ServiceRowState state = new ServiceRowState();
            state.Service = field == null ? "" : field.Service;
            state.Title = field == null ? "" : field.Title;
            if (String.IsNullOrWhiteSpace(state.Service) || manifest == null) return state;

            state.Declared = DeclaresService(manifest, state.Service);
            ServiceUse use = UseOf(manifest, state.Service);
            state.Optional = use == null || use.Optional;

            // 与插件运行时拿到 context.services 的口径完全一致（含"唯一候选自动绑定"）。
            ServiceResolution resolution = ServiceRegistry.Status(manifest.Id, state.Service);
            state.Available = resolution.Available;
            state.Bound = resolution.Available ? resolution.ProviderId : ServiceRegistry.BoundProvider(manifest.Id, state.Service);
            state.BoundName = resolution.Available ? resolution.ProviderName : "";
            state.Billing = resolution.Available ? (resolution.Billing ?? "") : "";
            state.Reason = resolution.Available ? "" : resolution.Reason;
            state.ReasonText = resolution.Available ? "" : ServiceRegistry.ReasonText(resolution.Reason);
            state.Candidates = ServiceRegistry.Candidates(state.Service);

            List<ServiceCandidate> disabled = ServiceRegistry.DisabledCandidates(state.Service);
            if (!state.Available && state.Candidates.Count == 0 && disabled.Count == 1)
            {
                state.ProviderInstalledButDisabled = true;
                state.DisabledProviderId = disabled[0].Id;
                state.DisabledProviderName = disabled[0].Name == "" ? disabled[0].Id : disabled[0].Name;
            }
            state.Ambiguous = !state.Available && state.Candidates.Count > 1;
            return state;
        }

        public static bool RequiresSatisfied(PluginManifest manifest, string requires, out string reason)
        {
            reason = "";
            if (String.IsNullOrWhiteSpace(requires) || manifest == null) return true;
            ServiceResolution resolution = ServiceRegistry.Status(manifest.Id, requires);
            if (resolution.Available) return true;
            reason = resolution.Reason;
            return false;
        }

        // 只写"真的变了"的那几条：绑定表是宿主独占资源，没变就不动它（少一次加锁与落盘）。
        public static int ApplyBindings(string consumerId, IEnumerable<KeyValuePair<string, string>> selections)
        {
            int changed = 0;
            foreach (KeyValuePair<string, string> selection in selections ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                string service = selection.Key, wanted = (selection.Value ?? "").Trim();
                if (!ServiceRegistry.ValidService(service)) continue;
                if (String.Equals(ServiceRegistry.BoundProvider(consumerId, service), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                ServiceRegistry.SetBinding(consumerId, service, wanted);
                // 与自动绑定记同一本账（规格 §3 第 1 条：绑定表写者只能是宿主）。
                ServiceRegistry.Audit("settings-bind " + consumerId + " " + service + " -> " + (wanted == "" ? "(解除绑定)" : wanted));
                changed++;
            }
            return changed;
        }
    }
}
