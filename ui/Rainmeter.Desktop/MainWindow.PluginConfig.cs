using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow
{
    private async Task<JsonDocument> PluginConfigCommandAsync(string action, string id, object? request = null)
    {
        var resultPath = Path.Combine(Path.GetTempPath(), "rw-plugin-config-" +
            Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo(Path.Combine(todoRoot, "TodoHost.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = request is not null,
                ArgumentList = { action, id, request is null ? resultPath : "-" }
            };
            if (request is not null) start.StandardInputEncoding = new UTF8Encoding(false);
            if (request is not null) start.ArgumentList.Add(resultPath);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("无法启动插件配置服务。");
            if (request is not null)
            {
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
                process.StandardInput.Close();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch
            {
                if (!process.HasExited) process.Kill();
                throw;
            }
            if (!File.Exists(resultPath)) throw new InvalidOperationException("配置服务没有返回结果。");
            var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            if (process.ExitCode != 0 || Value(result.RootElement, "ok") != "True")
            {
                var error = Value(result.RootElement, "error", "插件配置操作失败。");
                result.Dispose();
                throw new InvalidOperationException(error);
            }
            return result;
        }
        finally { if (File.Exists(resultPath)) File.Delete(resultPath); }
    }

    private static string ConfigFieldValue(JsonElement field)
    {
        if (!field.TryGetProperty("value", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" :
            value.ValueKind == JsonValueKind.True ? "True" :
            value.ValueKind == JsonValueKind.False ? "False" : value.GetRawText();
    }

    private async Task ShowPluginConfigAsync(string id)
    {
        JsonDocument model;
        try { model = await PluginConfigCommandAsync("UiPluginConfigModel", id); }
        catch (Exception ex) { ShowMessage("无法读取插件配置：" + ex.Message); return; }
        try
        {
          using (model)
          {
            var name = Value(model.RootElement, "name", "插件");
            var fieldRows = Items(model.RootElement, "fields").ToArray();
            var edits = new Dictionary<string, Func<object?>>();
            var clearSecrets = new Dictionary<string, CheckBox>();
            var sections = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
            sections.Children.Add(Text("调整“" + name + "”的运行设置。敏感项只在保存时提交，留空保持原值。", 13, muted: true));
            var grouped = fieldRows.GroupBy(field => Value(field, "section"));
            foreach (var group in grouped)
            {
                var section = new StackPanel { Spacing = 13 };
                section.Children.Add(Text(group.Key, 17, true));
                foreach (var field in group)
                {
                    var key = Value(field, "key");
                    var kind = Value(field, "type");
                    var service = Value(field, "service");
                    var enabled = Value(field, "available", "True") != "False";
                    var item = new StackPanel { Spacing = 6 };
                    item.Children.Add(Text(Value(field, "title"), 14, true));
                    var description = Value(field, "description");
                    if (description != "") item.Children.Add(Text(description, 12, muted: true));
                    if (!enabled) item.Children.Add(Text("依赖服务当前不可用：" +
                        Value(field, "reason", "请先启用对应插件。"), 12, muted: true));
                    if (service != "")
                    {
                        var choices = EditorControl(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch });
                        choices.Items.Add(new ComboBoxItem { Content = "不使用", Tag = "" });
                        foreach (var option in Items(field, "options"))
                        {
                            var optionId = Value(option, "id");
                            var label = Value(option, "name", optionId);
                            if (Value(option, "billing") == "may_charge") label += "（可能收费）";
                            choices.Items.Add(new ComboBoxItem { Content = label, Tag = optionId });
                        }
                        var current = ConfigFieldValue(field);
                        choices.SelectedItem = choices.Items.OfType<ComboBoxItem>()
                            .FirstOrDefault(choice => choice.Tag?.ToString() == current) ?? choices.Items[0];
                        edits[key] = () => (choices.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                        item.Children.Add(choices);
                    }
                    else if (kind == "boolean")
                    {
                        var toggle = new ToggleSwitch { IsOn = ConfigFieldValue(field) == "True",
                            OnContent = "", OffContent = "", IsEnabled = enabled };
                        edits[key] = () => toggle.IsOn;
                        item.Children.Add(toggle);
                    }
                    else if (field.TryGetProperty("options", out var choicesJson) &&
                        choicesJson.ValueKind == JsonValueKind.Array && choicesJson.GetArrayLength() > 0)
                    {
                        var choices = EditorControl(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
                            IsEnabled = enabled });
                        foreach (var option in choicesJson.EnumerateArray())
                        {
                            var label = option.ValueKind == JsonValueKind.String ? option.GetString() ?? "" : option.GetRawText();
                            choices.Items.Add(new ComboBoxItem { Content = label, Tag = label });
                        }
                        var current = ConfigFieldValue(field);
                        choices.SelectedItem = choices.Items.OfType<ComboBoxItem>()
                            .FirstOrDefault(choice => choice.Tag?.ToString() == current) ?? choices.Items[0];
                        edits[key] = () => choices.IsEnabled ? (choices.SelectedItem as ComboBoxItem)?.Tag?.ToString() : null;
                        item.Children.Add(choices);
                    }
                    else if (Value(field, "secret") == "True")
                    {
                        var box = EditorControl(new PasswordBox {
                            PlaceholderText = Value(field, "has_secret") == "True" ? "已保存，留空保持不变" : "输入密钥",
                            PasswordRevealMode = PasswordRevealMode.Peek, IsEnabled = enabled });
                        edits[key] = () => box.IsEnabled && box.Password != "" ? box.Password : null;
                        item.Children.Add(box);
                        if (Value(field, "has_secret") == "True")
                        {
                            var clear = new CheckBox { Content = "清除此密钥", IsEnabled = enabled };
                            clear.Checked += (_, _) => box.IsEnabled = false;
                            clear.Unchecked += (_, _) => box.IsEnabled = enabled;
                            clearSecrets[key] = clear;
                            item.Children.Add(clear);
                        }
                    }
                    else
                    {
                        var multi = kind == "multiline" || key.Contains("prompt", StringComparison.OrdinalIgnoreCase);
                        var box = EditorControl(new TextBox { Text = ConfigFieldValue(field),
                            IsEnabled = enabled, AcceptsReturn = multi,
                            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                            MinHeight = multi ? 94 : 40 });
                        edits[key] = () => box.IsEnabled ? box.Text : null;
                        item.Children.Add(box);
                    }
                    section.Children.Add(item);
                }
                sections.Children.Add(Card(section, 18));
            }
            if (Value(model.RootElement, "scan") == "True")
                sections.Children.Add(BuildSsdpScanner(id, edits));
            var error = Text("", 12, muted: true);
            sections.Children.Add(error);
            var dialog = EditorDialog(name + " · 配置", sections, "保存配置");
            dialog.Width = 760;
            if (dialog.Content is ScrollViewer scroller) scroller.MaxHeight = 640;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    var values = new Dictionary<string, object?>();
                    foreach (var edit in edits)
                    {
                        var value = edit.Value();
                        if (value is not null) values[edit.Key] = value;
                    }
                    var clear_secrets = clearSecrets.Where(entry => entry.Value.IsChecked == true)
                        .Select(entry => entry.Key).ToArray();
                    using var result = await PluginConfigCommandAsync("UiPluginConfigSave", id,
                        new { values, clear_secrets });
                }
                catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
                finally { deferral.Complete(); }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) Render();
          }
        }
        catch (Exception ex) { ShowMessage("插件配置界面未能打开：" + ex.Message); }
    }

    private Border BuildSsdpScanner(string id, Dictionary<string, Func<object?>> edits)
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(Text("发现服务器", 17, true));
        section.Children.Add(Text("先填写扫描 IP 和子网掩码，再搜索并选择设备；只接管主机 IP，不锁定端口和路径。", 12, muted: true));
        var status = Text("尚未扫描。", 12, muted: true);
        var choices = EditorControl(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "选择找到的服务器" });
        choices.IsEnabled = false;
        var systems = EditorControl(new ComboBox { Header = "系统筛选",
            HorizontalAlignment = HorizontalAlignment.Stretch });
        systems.Items.Add("全部系统");
        systems.SelectedIndex = 0;
        systems.IsEnabled = false;
        var devices = new List<JsonElement>();
        void ApplySystemFilter()
        {
            var selected = systems.SelectedItem?.ToString() ?? "全部系统";
            choices.Items.Clear();
            foreach (var device in devices.Where(device => selected == "全部系统" ||
                         SsdpSystemName(Value(device, "server")) == selected))
                choices.Items.Add(new ComboBoxItem { Content = Value(device, "ip") + " · " +
                    SsdpSystemName(Value(device, "server")) + " · " + Value(device, "server"), Tag = device });
            choices.IsEnabled = choices.Items.Count > 0;
            status.Text = devices.Count == 0 ? "未发现设备，请检查扫描范围。" :
                "显示 " + choices.Items.Count + " / " + devices.Count + " 台设备。";
        }
        systems.SelectionChanged += (_, _) => ApplySystemFilter();
        var search = Action("搜索设备", () => _ = SearchSsdpDevicesAsync(id, edits, systems,
            devices, ApplySystemFilter, status));
        section.Children.Add(search);
        section.Children.Add(systems);
        section.Children.Add(choices);
        section.Children.Add(status);
        edits["selected_usn"] = () => (choices.SelectedItem as ComboBoxItem)?.Tag is JsonElement device
            ? Value(device, "usn") : null;
        edits["selected_server"] = () => (choices.SelectedItem as ComboBoxItem)?.Tag is JsonElement device
            ? Value(device, "server") : null;
        edits["last_ip"] = () => (choices.SelectedItem as ComboBoxItem)?.Tag is JsonElement device
            ? Value(device, "ip") : null;
        return Card(section, 18);
    }

    private static string SsdpSystemName(string server)
    {
        if (server.Contains("istoreos", StringComparison.OrdinalIgnoreCase)) return "iStoreOS";
        if (server.Contains("nanopi-r2s", StringComparison.OrdinalIgnoreCase)) return "NanoPi R2S";
        if (server.StartsWith("microsoft-windows", StringComparison.OrdinalIgnoreCase)) return "Windows UPnP";
        if (server.StartsWith("go upnp", StringComparison.OrdinalIgnoreCase)) return "Go UPnP";
        var system = server.Contains('|') ? server[(server.IndexOf('|') + 1)..].Trim() : server.Trim();
        var slash = system.IndexOf('/');
        return slash > 0 ? system[..slash] : system == "" ? "未知系统" : system;
    }

    private async Task SearchSsdpDevicesAsync(string id, Dictionary<string, Func<object?>> edits,
        ComboBox systems, List<JsonElement> devices, Action applyFilter, TextBlock status)
    {
        try
        {
            var ip = edits.TryGetValue("scan_ip", out var readIp) ? readIp()?.ToString() ?? "" : "";
            var mask = edits.TryGetValue("subnet_mask", out var readMask) ? readMask()?.ToString() ?? "" : "";
            var wait = edits.TryGetValue("scan_wait_ms", out var readWait) ? readWait()?.ToString() ?? "1500" : "1500";
            if (ip == "") throw new ArgumentException("请先填写扫描 IP。");
            var currentPath = Path.Combine(pluginDataRoot, "Plugins", id, "current.json");
            using var current = ReadJson(currentPath) ?? throw new InvalidDataException("插件未安装。");
            var version = Value(current.RootElement, "version");
            var versionRoot = Path.Combine(pluginDataRoot, "Plugins", id, "versions", version);
            using var manifest = ReadJson(Path.Combine(versionRoot, "plugin.json"))
                ?? throw new InvalidDataException("插件清单无效。");
            var entry = Path.GetFullPath(Path.Combine(versionRoot, Value(manifest.RootElement, "entry")));
            if (!entry.StartsWith(Path.GetFullPath(versionRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件入口不在安装目录内。");
            status.Text = "正在扫描，请稍候…";
            var start = new ProcessStartInfo(entry)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                ArgumentList = { "--scan-child" }
            };
            using var process = Process.Start(start) ?? throw new IOException("无法启动设备扫描。");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {
                scan_ip = ip, subnet_mask = mask, scan_wait_ms = wait }));
            process.StandardInput.Close();
            var found = new List<JsonElement>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            string? line;
            try
            {
                while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token)) is not null)
                {
                    using var message = JsonDocument.Parse(line.TrimStart('\uFEFF'));
                    if (Value(message.RootElement, "type") != "result") continue;
                    if (Value(message.RootElement, "ok") != "True")
                        throw new InvalidOperationException(Value(message.RootElement, "error", "扫描失败。"));
                    found.AddRange(Items(message.RootElement, "devices").Select(value => value.Clone()));
                }
                await process.WaitForExitAsync(timeout.Token);
            }
            finally { if (!process.HasExited) process.Kill(); }
            devices.Clear();
            devices.AddRange(found);
            systems.Items.Clear();
            systems.Items.Add("全部系统");
            foreach (var name in devices.Select(device => SsdpSystemName(Value(device, "server")))
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name))
                systems.Items.Add(name);
            systems.IsEnabled = devices.Count > 0;
            systems.SelectedIndex = 0;
            applyFilter();
        }
        catch (Exception ex) { status.Text = "搜索失败：" + ex.Message; }
    }
}
