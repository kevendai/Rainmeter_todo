using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow
{
    private async Task<JsonDocument> CalendarServerCommandAsync(string action, object? request = null)
    {
        var resultPath = Path.Combine(Path.GetTempPath(), "rw-calendar-server-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo(Path.Combine(calendarRoot, "CalendarHost.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = request is not null,
                ArgumentList = { action, request is null ? resultPath : "-" }
            };
            if (request is not null)
            {
                start.ArgumentList.Add(resultPath);
                start.StandardInputEncoding = new UTF8Encoding(false);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动日程服务。");
            if (request is not null)
            {
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
                process.StandardInput.Close();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(action == "UiServerTest" ? 45 : 20));
            await process.WaitForExitAsync(timeout.Token);
            if (!File.Exists(resultPath)) throw new InvalidOperationException("日程服务未返回结果。");
            var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            if (process.ExitCode != 0 || Value(result.RootElement, "ok") != "True")
            {
                var error = Value(result.RootElement, "error", "日程服务器操作失败。");
                result.Dispose();
                throw new InvalidOperationException(error);
            }
            return result;
        }
        finally { if (File.Exists(resultPath)) File.Delete(resultPath); }
    }

    private async Task ShowCalendarServerAsync()
    {
        JsonDocument model;
        try { model = await CalendarServerCommandAsync("UiServerModel"); }
        catch (Exception ex) { ShowMessage("无法读取日程服务器配置：" + ex.Message); return; }
        using (model)
        {
            var data = model.RootElement;
            var savedSource = Value(data, "source", "manual");
            var savedHost = Value(data, "host");
            var savedQuery = Value(data, "query");
            var ssdpIp = Value(data, "provider_ip");
            var body = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
            body.Children.Add(Text("连接到你自己的 CalDAV 服务器。SSDP 只管理 IP；端口、协议和路径由你决定。", 13, muted: true));

            var address = new StackPanel { Spacing = 12 };
            address.Children.Add(Text("服务器地址", 17, true));
            var source = EditorControl(new ComboBox { Header = "IP 地址来源" });
            source.Items.Add(new ComboBoxItem { Content = "手动填写", Tag = "manual" });
            source.Items.Add(new ComboBoxItem { Content = "SSDP 自动发现", Tag = "ssdp" });
            source.SelectedIndex = savedSource == "ssdp" ? 1 : 0;
            address.Children.Add(source);
            var host = EditorControl(new TextBox { Header = "IP 地址 / 主机名", Text = savedHost });
            var manualHost = savedHost;
            host.TextChanged += (_, _) => { if (source.SelectedIndex == 0) manualHost = host.Text; };
            var port = EditorControl(new TextBox { Header = "端口", Text = Value(data, "port", "443") });
            var endpoint = new Grid { ColumnSpacing = 10 };
            endpoint.ColumnDefinitions.Add(new ColumnDefinition());
            endpoint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            endpoint.Children.Add(host); Grid.SetColumn(port, 1); endpoint.Children.Add(port);
            address.Children.Add(endpoint);
            var scheme = EditorControl(new ComboBox { Header = "协议" });
            scheme.Items.Add("https"); scheme.Items.Add("http");
            scheme.SelectedIndex = Value(data, "scheme") == "http" ? 1 : 0;
            var path = EditorControl(new TextBox { Header = "路径（可选）", Text = Value(data, "path"),
                PlaceholderText = "例如 /caldav" });
            address.Children.Add(scheme); address.Children.Add(path);
            body.Children.Add(Card(address, 18));

            var account = new StackPanel { Spacing = 12 };
            account.Children.Add(Text("登录账号", 17, true));
            var username = EditorControl(new TextBox { Header = "用户名", Text = Value(data, "username") });
            var password = EditorControl(new PasswordBox { Header = "密码",
                PlaceholderText = Value(data, "has_password") == "True" ? "已保存，留空保持不变" : "输入密码",
                PasswordRevealMode = PasswordRevealMode.Peek });
            account.Children.Add(username); account.Children.Add(password);
            body.Children.Add(Card(account, 18));

            var feedback = Text(Value(data, "status"), 12, muted: true);
            body.Children.Add(feedback);
            void UpdateSource()
            {
                var automatic = source.SelectedIndex == 1;
                host.IsEnabled = !automatic;
                host.Text = automatic ? ssdpIp : manualHost;
            }
            source.SelectionChanged += (_, _) => UpdateSource();
            UpdateSource();
            object Request() => new
            {
                source = source.SelectedIndex == 1 ? "ssdp" : "manual",
                scheme = scheme.SelectedIndex == 1 ? "http" : "https",
                host = host.Text.Trim(), port = port.Text.Trim(), path = path.Text.Trim(), query = savedQuery,
                username = username.Text.Trim(), password = password.Password
            };
            var test = Action("测试连接", () => { });
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                feedback.Text = "正在测试连接…";
                try
                {
                    using var response = await CalendarServerCommandAsync("UiServerTest", Request());
                    feedback.Text = Value(response.RootElement, "message", "连接成功。");
                }
                catch (Exception ex) { feedback.Text = "连接失败：" + ex.Message; }
                finally { test.IsEnabled = true; }
            };
            var clear = Action("清除设置", () => { });
            var buttons = Row(test, clear);
            body.Children.Add(buttons);
            var dialog = EditorDialog("日程同步服务器", body, "保存设置");
            dialog.Width = 720;
            if (dialog.Content is ScrollViewer scroller) scroller.MaxHeight = 630;
            var clearArmed = false;
            clear.Click += async (_, _) =>
            {
                if (!clearArmed)
                {
                    clearArmed = true;
                    clear.Content = "确认清除";
                    feedback.Text = "再次点击将移除本机保存的 CalDAV 账号；本地日程不会删除。";
                    return;
                }
                try
                {
                    using var response = await CalendarServerCommandAsync("UiServerClear", new { });
                    dialog.Hide();
                    Render();
                    ShowMessage(Value(response.RootElement, "message", "日程同步设置已清除。"));
                }
                catch (Exception ex) { feedback.Text = ex.Message; }
            };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try { using var response = await CalendarServerCommandAsync("UiServerSave", Request()); }
                catch (Exception ex) { feedback.Text = ex.Message; args.Cancel = true; }
                finally { deferral.Complete(); }
            };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            var polling = false;
            timer.Tick += async (_, _) =>
            {
                if (polling) return;
                polling = true;
                try
                {
                    using var updated = await CalendarServerCommandAsync("UiServerModel");
                    ssdpIp = Value(updated.RootElement, "provider_ip");
                    if (source.SelectedIndex == 1) host.Text = ssdpIp;
                }
                catch { }
                finally { polling = false; }
            };
            timer.Start();
            try { if (await dialog.ShowAsync() == ContentDialogResult.Primary) Render(); }
            finally { timer.Stop(); }
        }
    }
}
