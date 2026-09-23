using System.Diagnostics;
using System.Text.Json;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow
{
    private sealed record BackupPasswordChoice(string Password, bool Full);

    private Grid BackupPasswordRow(PasswordBox box)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        Button reveal = null!;
        reveal = Action("显示", () =>
        {
            var visible = box.PasswordRevealMode != PasswordRevealMode.Visible;
            box.PasswordRevealMode = visible ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
            reveal.Content = visible ? "隐藏" : "显示";
        });
        reveal.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(reveal, 1);
        row.Children.Add(reveal);
        return row;
    }

    private async Task<BackupPasswordChoice?> AskBackupPasswordAsync(bool exporting)
    {
        var password = EditorControl(new PasswordBox { Header = "备份密码（可选）",
            PasswordRevealMode = PasswordRevealMode.Hidden });
        var confirm = EditorControl(new PasswordBox { Header = "确认密码",
            PasswordRevealMode = PasswordRevealMode.Hidden });
        var full = new CheckBox { Content = "同时备份待办和本地日程", IsChecked = false };
        var hint = Text("留空则不设密码，备份文件不具备保密性；设置密码时至少 10 个字符。", 12, muted: true);
        var error = Text("", 12, muted: true);
        var fields = new StackPanel { Width = 420, Spacing = 14 };
        fields.Children.Add(Text(exporting ? "选择备份内容，并决定是否用密码保护。" : "输入备份密码；如果导出时未设置密码，这里保持空白。", 13, muted: true));
        fields.Children.Add(BackupPasswordRow(password));
        if (exporting)
        {
            fields.Children.Add(BackupPasswordRow(confirm));
            fields.Children.Add(full);
        }
        fields.Children.Add(hint);
        fields.Children.Add(error);
        var dialog = EditorDialog(exporting ? "导出用户配置" : "打开用户备份", fields,
            exporting ? "选择保存位置" : "查看备份内容");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (password.Password.Length > 0 && password.Password.Length < 10)
            {
                error.Text = "密码至少需要 10 个字符；也可以留空。";
                args.Cancel = true;
            }
            else if (exporting && password.Password != confirm.Password)
            {
                error.Text = "两次输入的密码不一致。";
                args.Cancel = true;
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? new BackupPasswordChoice(password.Password, exporting && full.IsChecked == true) : null;
    }

    private async Task<JsonElement> RunBackupCommandAsync(string action, object request)
    {
        var host = Path.Combine(todoRoot, "TodoHost.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("尚未找到待办宿主。", host);
        using var process = Process.Start(new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            ArgumentList = { action }
        }) ?? throw new InvalidOperationException("无法启动备份操作。 ");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        using var response = JsonDocument.Parse(output);
        if (Value(response.RootElement, "ok") != "True" || process.ExitCode != 0)
            throw new InvalidOperationException(Value(response.RootElement, "error", "备份操作失败。"));
        return response.RootElement.Clone();
    }

    private async Task ExportBackupAsync()
    {
        try
        {
            var choice = await AskBackupPasswordAsync(true);
            if (choice is null) return;
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = "Rainmeter-用户备份-" + DateTime.Today.ToString("yyyy-MM-dd")
            };
            picker.FileTypeChoices.Add("Rainmeter 用户备份", new List<string> { ".rwbackup" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await RunBackupCommandAsync("UiBackupExport", new { path = file.Path, password = choice.Password, full = choice.Full });
            ShowMessage("配置已导出到 " + file.Path);
        }
        catch (Exception ex) { ShowMessage("导出未完成：" + ex.Message); }
    }

    private async Task ImportBackupAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".rwbackup");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var choice = await AskBackupPasswordAsync(false);
            if (choice is null) return;
            var preview = await RunBackupCommandAsync("UiBackupPreview", new { path = file.Path, password = choice.Password });
            var hasData = Value(preview, "has_data") == "True";
            var configuration = new CheckBox { Content = "配置与凭据", IsChecked = true };
            var data = new CheckBox { Content = "待办和本地日程", IsChecked = hasData, IsEnabled = hasData };
            var warning = Text("所选内容会覆盖本机对应数据。导入前会建立临时回滚副本。", 12, muted: true);
            var fields = new StackPanel { Width = 420, Spacing = 12 };
            fields.Children.Add(Text("备份来自版本 " + Value(preview, "app_version", "未知"), 13));
            fields.Children.Add(configuration);
            fields.Children.Add(data);
            fields.Children.Add(Text(hasData
                ? $"包含 {Value(preview, "tasks")} 条待办和 {Value(preview, "events")} 条本地日程。"
                : "这份备份不包含待办和本地日程。", 13, muted: true));
            fields.Children.Add(warning);
            var dialog = EditorDialog("确认导入内容", fields, "确认导入");
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (configuration.IsChecked != true && data.IsChecked != true)
                {
                    warning.Text = "请至少选择一类要导入的内容。";
                    args.Cancel = true;
                }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await RunBackupCommandAsync("UiBackupApply", new { path = file.Path, password = choice.Password,
                configuration = configuration.IsChecked == true, data = data.IsChecked == true });
            Render();
            ShowMessage("备份已导入。敏感配置已重新绑定到当前 Windows 用户。 ");
        }
        catch (Exception ex) { ShowMessage("导入未完成：" + ex.Message); }
    }
}
