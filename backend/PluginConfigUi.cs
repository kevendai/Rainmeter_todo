using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using RainmeterBackend;

internal static partial class TodoApp
{
    private static int UiPluginConfigModel(string pluginId, string resultPath)
    {
        try
        {
            PluginManifest manifest = PluginRuntime.Resolve(pluginId, false);
            string versionRoot = PluginPaths.VersionRoot(pluginId, manifest.Version);
            Dictionary<string, object> schema = JsonUtil.LoadObject(
                PluginManifest.SafeChildPath(versionRoot, manifest.SettingsSchema, "设置 Schema"));
            List<SettingsField> fields = PluginSettingsLayout.Parse(
                JsonUtil.Object(JsonUtil.Get(schema, "properties")));
            string dataRoot = PluginPaths.DataRoot(pluginId);
            string configPath = Path.Combine(dataRoot, "config.json");
            string secretPath = Path.Combine(dataRoot, "secret.dat");
            Dictionary<string, object> config = File.Exists(configPath)
                ? JsonUtil.LoadObject(configPath) : new Dictionary<string, object>();
            Dictionary<string, object> secret = File.Exists(secretPath)
                ? JsonUtil.ReadDpapiJson(secretPath) : new Dictionary<string, object>();
            HashSet<string> required = new HashSet<string>(
                JsonUtil.Array(JsonUtil.Get(schema, "required")).Select(Convert.ToString),
                StringComparer.OrdinalIgnoreCase);
            List<object> rows = new List<object>();
            foreach (SettingsField field in fields)
            {
                object existing = PluginSettingValue(pluginId, field.Key,
                    field.Secret ? secret : config, secret, field.Property);
                Dictionary<string, object> row = new Dictionary<string, object>{
                    {"key", field.Key}, {"title", field.Title}, {"type", field.Type},
                    {"section", field.Advanced ? PluginSettingsLayout.AdvancedSection :
                        field.Section == "" ? PluginSettingsLayout.BasicSection : field.Section},
                    {"advanced", field.Advanced}, {"secret", field.Secret},
                    {"required", required.Contains(field.Key)}, {"address", field.Address},
                    {"service", field.Service}, {"requires", field.Requires},
                    {"description", JsonUtil.String(field.Property, "description", "")},
                    {"value", field.Secret ? null : existing},
                    {"has_secret", field.Secret && existing != null &&
                        !String.IsNullOrWhiteSpace(Convert.ToString(existing, CultureInfo.InvariantCulture))},
                    {"minimum", JsonUtil.Get(field.Property, "minimum")},
                    {"maximum", JsonUtil.Get(field.Property, "maximum")},
                    {"options", JsonUtil.Array(JsonUtil.Get(field.Property, "enum"))}
                };
                if (field.Service != "")
                {
                    ServiceRowState state = PluginSettingsLayout.Inspect(manifest, field);
                    row["value"] = state.Bound;
                    row["available"] = state.Available;
                    row["reason"] = state.ReasonText;
                    row["options"] = state.Candidates.Select(candidate =>
                        new Dictionary<string, object>{{"id", candidate.Id},
                            {"name", candidate.Name}, {"billing", candidate.Billing}}).ToArray();
                }
                else if (field.Requires != "")
                {
                    string reason;
                    row["available"] = PluginSettingsLayout.RequiresSatisfied(manifest, field.Requires, out reason);
                    row["reason"] = ServiceRegistry.ReasonText(reason);
                }
                rows.Add(row);
            }
            JsonUtil.SaveAtomic(resultPath, new Dictionary<string, object>{
                {"ok", true}, {"name", PluginNames.Display(pluginId, manifest.Name)},
                {"version", manifest.Version}, {"scan", pluginId == DynamicPluginValues.SsdpPluginId},
                {"fields", rows}
            });
            return 0;
        }
        catch (Exception ex)
        {
            try { JsonUtil.SaveAtomic(resultPath, new Dictionary<string, object>{
                {"ok", false}, {"error", ex.Message}}); } catch { }
            return 1;
        }
    }

    private static int UiPluginConfigSave(string pluginId, string requestPath, string resultPath)
    {
        try
        {
            PluginManifest manifest = PluginRuntime.Resolve(pluginId, false);
            string versionRoot = PluginPaths.VersionRoot(pluginId, manifest.Version);
            Dictionary<string, object> schema = JsonUtil.LoadObject(
                PluginManifest.SafeChildPath(versionRoot, manifest.SettingsSchema, "设置 Schema"));
            Dictionary<string, object> properties = JsonUtil.Object(JsonUtil.Get(schema, "properties"));
            Dictionary<string, object> request = requestPath == "-"
                ? JsonUtil.Object(JsonUtil.Deserialize(Console.In.ReadToEnd()))
                : JsonUtil.LoadObject(requestPath);
            Dictionary<string, object> edits = JsonUtil.Object(JsonUtil.Get(request, "values"));
            HashSet<string> clearSecrets = new HashSet<string>(
                JsonUtil.Array(JsonUtil.Get(request, "clear_secrets")).Select(Convert.ToString),
                StringComparer.OrdinalIgnoreCase);
            string dataRoot = PluginPaths.DataRoot(pluginId);
            Directory.CreateDirectory(dataRoot);
            string configPath = Path.Combine(dataRoot, "config.json");
            string secretPath = Path.Combine(dataRoot, "secret.dat");
            bool hadConfig = File.Exists(configPath), hadSecret = File.Exists(secretPath);
            Dictionary<string, object> config = hadConfig
                ? JsonUtil.LoadObject(configPath) : new Dictionary<string, object>();
            Dictionary<string, object> secret = hadSecret
                ? JsonUtil.ReadDpapiJson(secretPath) : new Dictionary<string, object>();
            string oldConfig = JsonUtil.Serialize(config), oldSecret = JsonUtil.Serialize(secret);
            HashSet<string> required = new HashSet<string>(
                JsonUtil.Array(JsonUtil.Get(schema, "required")).Select(Convert.ToString),
                StringComparer.OrdinalIgnoreCase);
            AddressProviderBinding provider = String.IsNullOrWhiteSpace(manifest.AddressTarget)
                ? null : DynamicPluginValues.AddressProvider(manifest.AddressTarget);
            List<KeyValuePair<string, string>> selections = new List<KeyValuePair<string, string>>();
            foreach (SettingsField field in PluginSettingsLayout.Parse(properties))
            {
                if (field.Secret && clearSecrets.Contains(field.Key))
                {
                    if (required.Contains(field.Key)) throw new InvalidDataException(field.Title + "为必填项。");
                    secret[field.Key] = "";
                    continue;
                }
                object raw;
                if (!edits.TryGetValue(field.Key, out raw)) continue;
                if (field.Service != "")
                {
                    if (!PluginSettingsLayout.DeclaresService(manifest, field.Service)) continue;
                    string chosen = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
                    if (chosen != "" && !ServiceRegistry.Candidates(field.Service).Any(
                        x => String.Equals(x.Id, chosen, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException(field.Title + " 的服务不可用。");
                    selections.Add(new KeyValuePair<string, string>(field.Service, chosen));
                    continue;
                }
                if (field.Secret && String.IsNullOrWhiteSpace(Convert.ToString(raw, CultureInfo.InvariantCulture)))
                    continue;
                object value;
                if (field.Type == "boolean") value = Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
                else if (field.Type == "integer")
                {
                    int number;
                    if (!Int32.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out number))
                        throw new InvalidDataException(field.Title + "必须是整数。");
                    int min = JsonUtil.Int(field.Property, "minimum", Int32.MinValue);
                    int max = JsonUtil.Int(field.Property, "maximum", Int32.MaxValue);
                    if (number < min || number > max) throw new InvalidDataException(field.Title + "超出允许范围。");
                    value = number;
                }
                else
                {
                    string text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
                    object[] options = JsonUtil.Array(JsonUtil.Get(field.Property, "enum")).ToArray();
                    if (options.Length > 0 && !options.Any(x => String.Equals(
                        Convert.ToString(x, CultureInfo.InvariantCulture), text, StringComparison.Ordinal)))
                        throw new InvalidDataException(field.Title + "不是允许的选项。");
                    value = text;
                }
                if (required.Contains(field.Key) &&
                    String.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                    throw new InvalidDataException(field.Title + "为必填项。");
                if (provider != null && field.Address)
                {
                    object stored = PluginSettingValue(pluginId, field.Key,
                        field.Secret ? secret : config, secret, field.Property);
                    value = DynamicPluginValues.MergeAddressEdit(
                        Convert.ToString(stored, CultureInfo.InvariantCulture) ?? "",
                        Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", manifest.AddressTarget);
                }
                (field.Secret ? secret : config)[field.Key] = value;
            }
            if (pluginId == DynamicPluginValues.SsdpPluginId)
                foreach (string key in new[]{"selected_usn", "selected_server", "last_ip"})
                {
                    object raw;
                    if (!edits.TryGetValue(key, out raw)) continue;
                    string value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
                    if (value.Length > 2048) throw new InvalidDataException("设备标识过长。");
                    config[key] = value;
                }
            List<KeyValuePair<string, string>> previousBindings = selections.Select(x =>
                new KeyValuePair<string, string>(x.Key, ServiceRegistry.BoundProvider(pluginId, x.Key))).ToList();
            try
            {
                JsonUtil.SaveAtomic(configPath, config);
                JsonUtil.WriteDpapiJson(secretPath, secret);
                PluginSettingsLayout.ApplyBindings(pluginId, selections);
                using (Process validation = Process.Start(new ProcessStartInfo(
                    PluginHostPath, "PluginAction " + pluginId + " validate_settings"){
                    UseShellExecute = false, CreateNoWindow = true }))
                    if (validation == null || !validation.WaitForExit(35000) || validation.ExitCode != 0)
                        throw new InvalidDataException("插件拒绝了当前设置。");
            }
            catch
            {
                if (hadConfig) JsonUtil.SaveAtomic(configPath, JsonUtil.Object(JsonUtil.Deserialize(oldConfig)));
                else if (File.Exists(configPath)) File.Delete(configPath);
                if (hadSecret) JsonUtil.WriteDpapiJson(secretPath, JsonUtil.Object(JsonUtil.Deserialize(oldSecret)));
                else if (File.Exists(secretPath)) File.Delete(secretPath);
                PluginSettingsLayout.ApplyBindings(pluginId, previousBindings);
                throw;
            }
            JsonUtil.SaveAtomic(resultPath, new Dictionary<string, object>{{"ok", true}});
            if (pluginId == DynamicPluginValues.SsdpPluginId &&
                JsonUtil.Bool(PluginRuntime.Current(pluginId), "enabled", false) &&
                JsonUtil.String(config, "selected_usn", "") != "")
                StartPluginCommand("Values", pluginId);
            return 0;
        }
        catch (Exception ex)
        {
            try { JsonUtil.SaveAtomic(resultPath, new Dictionary<string, object>{
                {"ok", false}, {"error", ex.Message}}); } catch { }
            return 1;
        }
    }
}
