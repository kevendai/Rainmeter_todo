using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow
{
    private async Task RunMarketCommandAsync(string action, string? pluginId = null)
    {
        var host = Path.Combine(todoRoot, "TodoHost.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("尚未找到待办宿主。", host);
        var result = Path.Combine(Path.GetTempPath(), "rainmeter-ui-market-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { action, pluginId ?? result }
            };
            if (pluginId is not null) start.ArgumentList.Add(result);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动插件市场命令。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token);
            using var response = ReadJson(result);
            if (process.ExitCode != 0 || response is null || Value(response.RootElement, "ok") != "True")
                throw new InvalidOperationException(response is null
                    ? "插件市场操作失败。"
                    : Value(response.RootElement, "error", "插件市场操作失败。"));
        }
        finally { if (File.Exists(result)) File.Delete(result); }
    }

    private async void RefreshMarket()
    {
        try { await RunMarketCommandAsync("UiMarketRefresh"); Render(); }
        catch (Exception ex) { ShowMessage("远端刷新失败，仍显示本地市场：" + ex.Message); }
    }

    private async void InstallMarketPlugin(string id, string name, bool installed, Button button)
    {
        var dialog = MaterialDialog(new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = installed ? "更新插件？" : "安装插件？",
            Content = Text("将从官方发布地址" + (installed ? "更新" : "安装") + "“" + name + "”。插件是可执行程序，请确认你信任它。", 14),
            PrimaryButtonText = installed ? "更新" : "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        button.IsEnabled = false;
        button.Content = installed ? "更新中…" : "安装中…";
        try { await RunMarketCommandAsync("UiMarketInstall", id); Render(); }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            button.Content = installed ? "更新" : "安装插件";
            ShowMessage((installed ? "更新" : "安装") + "未完成：" + ex.Message);
        }
    }

    private async void InstallLocalPlugin()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".rwplugin");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var package = await picker.PickSingleFileAsync();
            if (package is null) return;
            var dialog = MaterialDialog(new ContentDialog
            {
                XamlRoot = Shell.XamlRoot,
                Title = "安装本地插件？",
                Content = Text("插件是可执行程序，权限声明不能限制系统访问。请确认你信任“" + package.Name + "”。安装后默认禁用。", 14),
                PrimaryButtonText = "安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            });
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await RunMarketCommandAsync("UiPluginInstallLocal", package.Path);
            Render();
            ShowMessage("插件已安装，默认禁用。检查配置与权限后再启用。");
        }
        catch (Exception ex) { ShowMessage("本地安装未完成：" + ex.Message); }
    }

    private async Task UninstallPluginAsync(string id, string name)
    {
        var installedRoot = Path.GetFullPath(Path.Combine(pluginDataRoot, "Plugins"));
        var path = Path.GetFullPath(Path.Combine(installedRoot, id));
        if (id != Path.GetFileName(id) || Path.GetDirectoryName(path) != installedRoot)
        {
            ShowMessage("插件路径无效，已停止卸载。");
            return;
        }
        var dialog = MaterialDialog(new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = "卸载插件？",
            Content = Text("将卸载“" + name + "”的程序文件。配置和缓存默认保留。", 14),
            PrimaryButtonText = "卸载程序",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("插件已不存在，请刷新后重试。");
            Directory.Delete(path, true);
            Render();
            var dataPath = Path.Combine(pluginDataRoot, "PluginData", id);
            if (!Directory.Exists(dataPath)) return;
            var dataDialog = MaterialDialog(new ContentDialog
            {
                XamlRoot = Shell.XamlRoot,
                Title = "同时删除插件数据？",
                Content = Text("程序已卸载。是否永久删除“" + name + "”的配置、密钥、状态和缓存？", 14),
                PrimaryButtonText = "删除数据",
                CloseButtonText = "保留数据",
                DefaultButton = ContentDialogButton.Close
            });
            if (await dataDialog.ShowAsync() == ContentDialogResult.Primary)
                Directory.Delete(dataPath, true);
        }
        catch (Exception ex) { ShowMessage("卸载未完成：" + ex.Message); }
    }

    private void OpenPluginHomepage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            ShowMessage("插件主页地址无效。");
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { ShowMessage("无法打开插件主页：" + ex.Message); }
    }

    private static bool HasNewMarketVersion(string installedVersion, string marketVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion)) return true;
        if (Version.TryParse(installedVersion, out var current) &&
            Version.TryParse(marketVersion, out var available))
            return available > current;
        return !string.Equals(installedVersion, marketVersion, StringComparison.OrdinalIgnoreCase);
    }

    private void ConfigurePlugin(string id) => _ = ShowPluginConfigAsync(id);

    private async void RunInstalledPlugin(string id, string capability)
    {
        var verb = capability == "value_provider" ? "Values" :
            capability == "todo_source" ? "Sync" : "";
        if (verb == "") { ShowMessage("此插件由日历按需调用，无需手动执行。"); return; }
        try
        {
            if (id == "io.github.kevendai.arxiv")
            {
                using var pending = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json"));
                if (pending is not null && Value(pending.RootElement, "state") == "running")
                {
                    await MaterialDialog(new ContentDialog
                    {
                        XamlRoot = Shell.XamlRoot, Title = "正在执行",
                        Content = Text("论文评分仍在进行。可在“更多”中查看进度或取消任务。", 14),
                        PrimaryButtonText = "知道了"
                    }).ShowAsync();
                    return;
                }
                if (pending is not null && Value(pending.RootElement, "state") == "attention" &&
                    Value(pending.RootElement, "resume_action") != "")
                {
                    await ShowPendingPluginConfirmationAsync(id);
                    Render();
                    return;
                }
            }
            var start = new ProcessStartInfo(Path.Combine(todoRoot, "PluginHost.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { verb, id }
            };
            if (id == "io.github.kevendai.arxiv")
            {
                // Sync only checks local/remote results and may return an AI-consent request.
                // Opening another ContentDialog immediately after the progress dialog closes
                // can crash WinUI. Show progress only for the confirmed scoring operation.
                using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动插件。");
                await process.WaitForExitAsync();
                await ShowPendingPluginConfirmationAsync(id);
            }
            else
            {
                using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动插件。");
                await process.WaitForExitAsync();
                if (capability == "todo_source") await ShowPendingPluginConfirmationAsync(id);
            }
            Render();
        }
        catch (Exception ex) { ShowMessage("插件未启动：" + ex.Message); }
    }

    private async Task<(int ExitCode, bool Cancelled)> RunPluginWithProgressAsync(
        string id, ProcessStartInfo start, string title)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动插件。");
        var wait = process.WaitForExitAsync();
        if (await Task.WhenAny(wait, Task.Delay(250)) == wait)
        {
            await wait;
            return (process.ExitCode, false);
        }
        var phase = Text("正在启动…", 14, true);
        var count = Text("等待进度", 12, muted: true);
        var fill = new Border { Background = PrimaryFill, Width = 60, Height = 8,
            CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
        var bar = new Border { Background = EditorField, Width = 400, Height = 8,
            CornerRadius = new CornerRadius(4), Child = fill };
        var content = new StackPanel { Spacing = 14, MinWidth = 390 };
        content.Children.Add(phase);
        content.Children.Add(bar);
        content.Children.Add(count);
        content.Children.Add(Text("进度来自插件作业记录；取消会停止本次任务。", 12, muted: true));
        var dialog = MaterialDialog(new ContentDialog
        {
            XamlRoot = Shell.XamlRoot, Title = title, Content = content,
            CloseButtonText = "取消任务"
        });
        var cancelled = false;
        var cancelling = false;
        async Task CancelAsync()
        {
            if (cancelling || process.HasExited) return;
            cancelling = true;
            phase.Text = "正在取消任务…";
            using var cancel = Process.Start(new ProcessStartInfo(Path.Combine(todoRoot, "PluginHost.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "Cancel", id, "user_cancelled" }
            });
            if (cancel is null) { phase.Text = "取消未启动，请重试。"; cancelling = false; return; }
            using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await cancel.WaitForExitAsync(cancelTimeout.Token); }
            catch (OperationCanceledException)
            {
                using var state = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json"));
                if (state is null || Value(state.RootElement, "state") != "cancelled")
                    throw new TimeoutException("取消操作未完成，请重试。");
            }
            using var cancelledJob = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json"));
            if ((cancel.HasExited && cancel.ExitCode == 0) ||
                (cancelledJob is not null && Value(cancelledJob.RootElement, "state") == "cancelled"))
            {
                cancelled = true;
                Shell.DispatcherQueue.TryEnqueue(() => dialog.Hide());
            }
            else { phase.Text = "取消未完成，请重试。"; cancelling = false; }
        }
        dialog.CloseButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            try { await CancelAsync(); }
            catch (Exception ex) { phase.Text = "取消未完成：" + ex.Message; cancelling = false; }
        };
        void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
            Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            args.Cancel = true;
            _ = CancelAndReportAsync();
        }
        async Task CancelAndReportAsync()
        {
            try { await CancelAsync(); }
            catch (Exception ex) { phase.Text = "取消未完成：" + ex.Message; cancelling = false; }
        }
        async Task ObserveAsync()
        {
            var started = process.StartTime.ToUniversalTime();
            var jobPath = Path.Combine(pluginDataRoot, "PluginJobs", id + ".json");
            var paperPath = Path.Combine(pluginDataRoot, "PluginData", id, "cache", "paper-job.json");
            while (!wait.IsCompleted && !cancelled)
            {
                var latest = new[] { paperPath, jobPath }
                    .FirstOrDefault(path => File.Exists(path) && File.GetLastWriteTimeUtc(path) >= started.AddSeconds(-2));
                if (latest is not null)
                {
                    using var job = ReadJson(latest);
                    if (job is not null)
                    {
                        if (!cancelling) phase.Text = Value(job.RootElement, "desktop_message",
                            Value(job.RootElement, "message", "正在处理论文…"));
                        var current = int.TryParse(Value(job.RootElement, "completed",
                            Value(job.RootElement, "current")), out var n) ? n : 0;
                        var total = int.TryParse(Value(job.RootElement, "total"), out var t) ? t : 0;
                        if (total > 0)
                        {
                            fill.Width = Math.Clamp(current * 400.0 / total, 0, 400);
                            var elapsed = DateTime.UtcNow - started;
                            count.Text = $"{Math.Min(current, total)} / {total}" +
                                (current == 0 ? $" · 等待首批结果 {Math.Max(0, (int)elapsed.TotalSeconds)} 秒" : "");
                        }
                        else { fill.Width = 60; count.Text = "正在处理"; }
                    }
                }
                await Task.WhenAny(wait, Task.Delay(500));
            }
        }
        AppWindow.Closing += OnWindowClosing;
        try
        {
            await Task.Delay(200);
            var shown = dialog.ShowAsync();
            await ObserveAsync();
            if (!cancelled) await wait;
            dialog.Hide();
            await shown;
        }
        finally
        {
            AppWindow.Closing -= OnWindowClosing;
            dialog.Hide();
        }
        using (var finalJob = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json")))
            if (finalJob is not null && Value(finalJob.RootElement, "state") == "cancelled") cancelled = true;
        return (cancelled ? 0 : process.ExitCode, cancelled);
    }

    private async Task ShowRunningPluginProgressAsync(string id)
    {
        var phase = Text("正在读取任务进度…", 14, true);
        var count = Text("等待进度", 12, muted: true);
        var fill = new Border { Background = PrimaryFill, Width = 60, Height = 8,
            CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
        var bar = new Border { Background = EditorField, Width = 400, Height = 8,
            CornerRadius = new CornerRadius(4), Child = fill };
        var content = new StackPanel { Spacing = 14, MinWidth = 390 };
        content.Children.Add(phase);
        content.Children.Add(bar);
        content.Children.Add(count);
        var dialog = MaterialDialog(new ContentDialog
        {
            XamlRoot = Shell.XamlRoot, Title = "论文 AI 评分", Content = content,
            CloseButtonText = "取消任务"
        });
        var cancelling = false;
        dialog.CloseButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (cancelling) return;
            cancelling = true;
            phase.Text = "正在取消任务…";
            try
            {
                using var cancel = Process.Start(new ProcessStartInfo(Path.Combine(todoRoot, "PluginHost.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    ArgumentList = { "Cancel", id, "user_cancelled" }
                }) ?? throw new InvalidOperationException("无法启动取消操作。");
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await cancel.WaitForExitAsync(cancelTimeout.Token);
                if (cancel.ExitCode != 0) throw new InvalidOperationException("取消未完成。");
                Shell.DispatcherQueue.TryEnqueue(() => dialog.Hide());
            }
            catch (Exception ex) { phase.Text = ex.Message; cancelling = false; }
        };
        var shown = dialog.ShowAsync();
        try
        {
            var jobPath = Path.Combine(pluginDataRoot, "PluginJobs", id + ".json");
            var paperPath = Path.Combine(pluginDataRoot, "PluginData", id, "cache", "paper-job.json");
            while (true)
            {
                using var job = ReadJson(jobPath);
                var hostRunning = job is not null && Value(job.RootElement, "state") == "running";
                var workerRunning = IsPaperWorkerRunning();
                if (!hostRunning && !workerRunning) break;
                using var paper = ReadJson(paperPath);
                var progress = workerRunning && paper is not null
                    ? paper.RootElement : job?.RootElement ?? paper?.RootElement;
                if (progress is null) { await Task.Delay(500); continue; }
                if (!cancelling)
                    phase.Text = Value(progress.Value, "desktop_message", Value(progress.Value, "message", "正在评分…"));
                var current = int.TryParse(Value(progress.Value, "completed", Value(progress.Value, "current")), out var n) ? n : 0;
                var total = int.TryParse(Value(progress.Value, "total"), out var t) ? t : 0;
                if (total > 0)
                {
                    fill.Width = Math.Clamp(current * 400.0 / total, 0, 400);
                    var elapsed = DateTime.UtcNow - File.GetLastWriteTimeUtc(paperPath);
                    count.Text = $"{Math.Min(current, total)} / {total}" +
                        (current == 0 ? $" · 等待首批结果 {Math.Max(0, (int)elapsed.TotalSeconds)} 秒" : "");
                }
                else { fill.Width = 60; count.Text = "正在处理"; }
                await Task.Delay(500);
            }
        }
        finally { dialog.Hide(); await shown; Render(); }
    }

    private static bool IsPaperWorkerRunning()
    {
        using var mutex = new Mutex(false, @"Global\RainmeterTodoPaperWorker");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            return !acquired;
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private async Task ShowPendingPluginConfirmationAsync(string id)
    {
        using var job = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json"));
        if (job is null || Value(job.RootElement, "state") != "attention" ||
            Value(job.RootElement, "resume_action") == "") return;
        var attention = job.RootElement.TryGetProperty("attention", out var info) ? info : default;
        var allowSnooze = attention.ValueKind == JsonValueKind.Object &&
            Value(attention, "allow_snooze") == "True";
        var continuing = Value(job.RootElement, "message").StartsWith("上次评分中断", StringComparison.Ordinal);
        var content = new StackPanel { Spacing = 14, MinWidth = 350 };
        content.Children.Add(Text(Value(job.RootElement, "message", "这个任务需要用 AI 继续。"), 14));
        content.Children.Add(Text(continuing
            ? "继续评分会使用已配置的 API Key，仅对未完成的论文调用模型；可能产生费用。"
            : "继续会使用已配置的 API Key 调用模型，费用由你的账号承担。", 13, muted: true));
        if (allowSnooze)
            content.Children.Add(Text("今天不再提醒会取消本次任务，并停止今天的自动提醒；取消本次任务只结束这一轮。", 12, muted: true));
        var dialog = MaterialDialog(new ContentDialog
        {
            XamlRoot = Shell.XamlRoot, Title = HumanPluginName(id) + " · AI 评分确认",
            Content = content, PrimaryButtonText = continuing ? "继续评分" : "使用 AI 评分",
            SecondaryButtonText = allowSnooze ? "今天不再提醒" : "", CloseButtonText = "取消本次任务"
        });
        bool closeWindow = false;
        void CancelBeforeClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            args.Cancel = true;
            closeWindow = true;
            dialog.Hide();
        }
        ContentDialogResult choice;
        AppWindow.Closing += CancelBeforeClosing;
        try { choice = await dialog.ShowAsync(); }
        finally { AppWindow.Closing -= CancelBeforeClosing; }

        var host = Path.Combine(todoRoot, "PluginHost.exe");
        if (choice != ContentDialogResult.Primary)
        {
            using var cancelled = Process.Start(new ProcessStartInfo(host)
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "Cancel", id, choice == ContentDialogResult.Secondary ? "user_declined" : "user_cancelled" }
            }) ?? throw new InvalidOperationException("无法保存本次选择。");
            await cancelled.WaitForExitAsync();
            if (cancelled.ExitCode != 0) throw new InvalidOperationException("未能保存本次选择。");
            if (closeWindow) { Close(); return; }
            Render();
            return;
        }
        var input = Path.Combine(Path.GetTempPath(), "rainmeter-paid-consent-" + Guid.NewGuid().ToString("N") + ".json");
        var output = input + ".result.json";
        try
        {
            var resume = job.RootElement.TryGetProperty("resume_input", out var payload) && payload.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(payload.GetRawText())?.AsObject() ?? new JsonObject() : new JsonObject();
            resume["allow_paid_ai"] = true;
            File.WriteAllText(input, resume.ToJsonString());
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("PluginAction"); start.ArgumentList.Add(id);
            start.ArgumentList.Add(Value(job.RootElement, "resume_action"));
            start.ArgumentList.Add(input); start.ArgumentList.Add(output);
            start.Environment["RW_PAID_CONSENT"] = "1";
            var (exitCode, cancelledByUser) = await RunPluginWithProgressAsync(id, start, "论文 AI 评分");
            await ShowPaperOutcomeAsync(id, exitCode, cancelledByUser);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
        Render();
    }

    private async Task ShowPaperOutcomeAsync(string id, int exitCode, bool cancelled)
    {
        if (id != "io.github.kevendai.arxiv") return;
        await Task.Delay(200);
        using var hostJob = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json"));
        cancelled |= hostJob is not null && Value(hostJob.RootElement, "state") == "cancelled";
        using var paper = ReadJson(Path.Combine(pluginDataRoot, "PluginData", id, "cache", "paper-job.json"));
        var state = paper is null ? "" : Value(paper.RootElement, "state");
        var detail = paper is null ? "没有找到论文作业结果。" : Value(paper.RootElement, "message", "论文作业没有返回说明。");
        if (cancelled || exitCode != 0 || state != "completed")
        {
            var message = cancelled ? "本次评分已取消。" : "评分未完成：" + detail;
            await MaterialDialog(new ContentDialog
            {
                XamlRoot = Shell.XamlRoot, Title = cancelled ? "任务已取消" : "评分未完成",
                Content = Text(message, 14), PrimaryButtonText = "知道了"
            }).ShowAsync();
            return;
        }
        if (Value(paper!.RootElement, "remote_upload_available") != "True")
        {
            await MaterialDialog(new ContentDialog
            {
                XamlRoot = Shell.XamlRoot, Title = "评分完成",
                Content = Text(detail, 14), PrimaryButtonText = "知道了"
            }).ShowAsync();
            return;
        }
        var date = Value(paper.RootElement, "remote_upload_date");
        var overwrite = Value(paper.RootElement, "remote_upload_mode") == "overwrite";
        if (date == "") return;
        await PromptRemoteUploadAsync(id, date, overwrite);
    }

    private async Task PromptRemoteUploadAsync(string id, string date, bool overwrite)
    {
        var question = overwrite
            ? $"{date} 的本地评分已完成，但远端现在已有该日结果。是否覆盖远端结果？"
            : $"{date} 的本地评分已完成，远端尚无该日结果。是否同步到远端？";
        var dialog = MaterialDialog(new ContentDialog
        {
            XamlRoot = Shell.XamlRoot, Title = overwrite ? "覆盖远端论文结果？" : "同步论文结果到远端？",
            Content = Text(question, 14),
            PrimaryButtonText = overwrite ? "覆盖远端" : "同步到远端",
            CloseButtonText = "暂不",
            DefaultButton = ContentDialogButton.Close
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Task.Delay(200);
        var result = await UploadRemoteSnapshotAsync(id, date, overwrite);
        if (result == "remote_exists" && !overwrite)
        {
            await PromptRemoteUploadAsync(id, date, true);
            return;
        }
        await MaterialDialog(new ContentDialog
        {
            XamlRoot = Shell.XamlRoot, Title = result.StartsWith("已同步", StringComparison.Ordinal) ? "远端同步完成" : "远端同步结果",
            Content = Text(result, 14), PrimaryButtonText = "知道了"
        }).ShowAsync();
    }

    private async Task<string> UploadRemoteSnapshotAsync(string id, string date, bool overwrite)
    {
        var input = Path.Combine(Path.GetTempPath(), "rainmeter-paper-upload-" + Guid.NewGuid().ToString("N") + ".json");
        var output = input + ".result.json";
        ContentDialog? waiting = null;
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new { date, overwrite }));
            var start = new ProcessStartInfo(Path.Combine(todoRoot, "PluginHost.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "PluginAction", id, "upload_snapshot", input, output }
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动远端同步。");
            waiting = MaterialDialog(new ContentDialog
            {
                XamlRoot = Shell.XamlRoot, Title = "正在同步远端",
                Content = Text("正在检查远端并同步论文结果…", 15)
            });
            var shown = waiting.ShowAsync();
            await process.WaitForExitAsync();
            waiting.Hide();
            await shown;
            using var result = ReadJson(output);
            if (result is null) return "远端同步没有返回结果。";
            if (Value(result.RootElement, "ok") != "True") return "远端同步失败：" + Value(result.RootElement, "error", "未知错误");
            var payload = result.RootElement.TryGetProperty("payload", out var value) ? value : default;
            return payload.ValueKind == JsonValueKind.Object && Value(payload, "remote_exists") == "True"
                ? "remote_exists" : Value(payload, "summary", "远端同步已完成。");
        }
        catch (Exception ex) { return "远端同步失败：" + ex.Message; }
        finally
        {
            waiting?.Hide();
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private string InstalledPluginStatus(string id, bool enabled)
    {
        if (!enabled) return "未启用";
        if (id == "io.github.kevendai.arxiv")
        {
            var cache = Path.Combine(pluginDataRoot, "PluginData", id, "cache");
            var complete = Path.Combine(cache, DateTime.Today.ToString("yyyy-MM-dd") + "_papers.json");
            if (!File.Exists(complete))
            {
                using var paper = ReadJson(Path.Combine(cache, "paper-job.json"));
                if (paper is not null && Value(paper.RootElement, "state") == "failed")
                    return "评分失败 · " + Value(paper.RootElement, "message", "请查看论文任务日志");
            }
        }
        using var job = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json"));
        if (job is null) return "尚未执行";
        var currentPath = Path.Combine(pluginDataRoot, "Plugins", id, "current.json");
        if (DateTimeOffset.TryParse(Value(job.RootElement, "updated_at"), out var lastJob))
        {
            if (File.Exists(currentPath) && File.GetLastWriteTimeUtc(currentPath) > lastJob.UtcDateTime)
                return "启用后尚未执行";
            var configurationRoot = Path.Combine(pluginDataRoot, "PluginData", id);
            if (new[] { "config.json", "secret.dat" }
                .Select(name => Path.Combine(configurationRoot, name))
                .Any(path => File.Exists(path) && File.GetLastWriteTimeUtc(path) > lastJob.UtcDateTime))
                return "配置已更新，等待再次运行";
        }
        var state = Value(job.RootElement, "state");
        var message = Value(job.RootElement, "message");
        return state switch
        {
            "running" => "执行中" + (message == "" ? "" : " · " + message),
            "failed" => "失败" + (message == "" ? "" : " · " + message),
            "attention" => "需要确认" + (message == "" ? "" : " · " + message),
            "cancelled" => "已取消",
            "done" or "completed" or "success" => "上次执行成功",
            _ => message == "" ? state : message
        };
    }

    private void StartPluginUtility(string executable, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(todoRoot, executable))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Process.Start(start);
        }
        catch (Exception ex) { ShowMessage("插件操作未启动：" + ex.Message); }
    }

    private async void RunCustomPluginAction(string id, string actionId, string warning, bool confirm)
    {
        if (confirm)
        {
            var dialog = EditorDialog("确认插件操作",
                Text(warning == "" ? "确定执行此插件操作？" : warning, 14), "继续执行");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        if (actionId != "test_connection")
        {
            if (id == "io.github.kevendai.arxiv" &&
                (actionId == "sync_with_ai" || actionId == "rescore"))
            {
                try
                {
                    var rescore = actionId == "rescore";
                    var start = new ProcessStartInfo(
                        Path.Combine(todoRoot, rescore ? "TodoHost.exe" : "PluginHost.exe"))
                    {
                        UseShellExecute = false, CreateNoWindow = true
                    };
                    start.ArgumentList.Add(rescore ? "PluginRescore" : "PluginAction");
                    start.ArgumentList.Add(id);
                    if (!rescore) start.ArgumentList.Add(actionId);
                    var (exitCode, cancelledByUser) = await RunPluginWithProgressAsync(id, start,
                        rescore ? "重新爬取并评分" : "论文推荐同步");
                    if (rescore) await ShowPaperOutcomeAsync(id, exitCode, cancelledByUser);
                    if (!rescore && !cancelledByUser) await ShowPendingPluginConfirmationAsync(id);
                    Render();
                }
                catch (Exception ex) { ShowMessage("论文推荐未完成：" + ex.Message); }
            }
            else StartPluginUtility("TodoHost.exe", "PluginAction", id, actionId);
            return;
        }
        var resultPath = Path.Combine(Path.GetTempPath(), "rw-plugin-test-" +
            Guid.NewGuid().ToString("N") + ".json");
        string message;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(todoRoot, "PluginHost.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "PluginAction", id, actionId, "", resultPath }
            }) ?? throw new InvalidOperationException("无法启动插件测试。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                throw;
            }
            using var result = ReadJson(resultPath);
            if (result is null)
                throw new InvalidOperationException("插件未返回测试结果。");
            var ok = Value(result.RootElement, "ok") == "True";
            var detail = ok && result.RootElement.TryGetProperty("payload", out var payload)
                ? Value(payload, "summary") : Value(result.RootElement, "error");
            message = (ok ? "连接测试成功" : "连接测试失败") +
                (string.IsNullOrWhiteSpace(detail) ? "。" : "：" + detail);
        }
        catch (OperationCanceledException) { message = "连接测试超时，请检查网络或插件配置。"; }
        catch (Exception ex) { message = "连接测试未完成：" + ex.Message; }
        finally { if (File.Exists(resultPath)) File.Delete(resultPath); }
        await EditorDialog("测试连接", Text(message, 14), "确定").ShowAsync();
    }

    private async Task ConfirmClearPluginTasksAsync(string id)
    {
        var taskIds = PluginTaskIds(id);
        if (taskIds.Length == 0) { ShowMessage("该插件没有创建可清除的待办。"); return; }
        var dialog = MaterialDialog(new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = "清除插件待办？",
            Content = Text($"将删除来源标记属于此插件的 {taskIds.Length} 项待办（包括后来手动编辑过的项）。插件和配置仍会保留。", 14),
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var input = Path.Combine(Path.GetTempPath(), "rainmeter-plugin-clear-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new { plugin_id = id, task_ids = taskIds }));
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(todoRoot, "TodoHost.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "PluginClearTasks", id, input }
            }) ?? throw new InvalidOperationException("无法启动清除操作。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("清除未完成，请检查待办状态。");
            Render();
        }
        catch (Exception ex) { ShowMessage("清除未完成：" + ex.Message); }
        finally { if (File.Exists(input)) File.Delete(input); }
    }

    private string[] PluginTaskIds(string id)
    {
        using var state = ReadJson(Path.Combine(todoRoot, "tasks.json"));
        return state is null ? [] : Items(state.RootElement, "tasks")
            .Where(task => task.TryGetProperty("origin", out var origin)
                && origin.ValueKind == JsonValueKind.Object && Value(origin, "plugin_id") == id)
            .Select(task => Value(task, "id")).Where(taskId => taskId != "")
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private int CountPluginTasks(string id) => PluginTaskIds(id).Length;

    private Button PluginMoreActions(string id, JsonElement? manifest)
    {
        var button = Action("更多", () => { });
        var menu = new MenuFlyout();
        void Add(string label, Action callback)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => callback();
            menu.Items.Add(item);
        }
        using var job = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json"));
        if (id == "io.github.kevendai.arxiv" &&
            ((job is not null && Value(job.RootElement, "state") == "running") || IsPaperWorkerRunning()))
            Add("查看进度", () => _ = ShowRunningPluginProgressAsync(id));
        if (job is not null && Value(job.RootElement, "state") is "running" or "attention")
            Add("取消当前任务", () => StartPluginUtility("PluginHost.exe", "Cancel", id));
        if (manifest is JsonElement sourcePlugin
            && Items(sourcePlugin, "capabilities").Any(capability => capability.GetString() == "todo_source")
            && CountPluginTasks(id) > 0)
            Add("清除该插件创建的待办", () => _ = ConfirmClearPluginTasksAsync(id));
        if (manifest is JsonElement plugin && Value(plugin, "homepage") is { Length: > 0 } homepage)
            Add("打开主页", () => OpenPluginHomepage(homepage));
        if (manifest is JsonElement data)
            foreach (var action in Items(data, "actions"))
            {
                if (Value(action, "settings_ui") == "True") continue;
                var actionId = Value(action, "id");
                if (actionId == "") continue;
                if (id == "io.github.kevendai.arxiv" && actionId == "sync_with_ai") continue;
                var label = Value(action, "name", actionId);
                var warning = Value(action, "risk");
                var confirm = Value(action, "confirm") == "True";
                Add(label, () => RunCustomPluginAction(id, actionId, warning, confirm));
            }
        Add("卸载程序", () => _ = UninstallPluginAsync(id,
            manifest is JsonElement named ? HumanPluginName(id, Value(named, "name")) : HumanPluginName(id)));
        button.Flyout = menu;
        return button;
    }

    private void RenderPlugins()
    {
        Heading(pluginMarketSelected ? "插件市场" : "已安装插件",
            pluginMarketSelected ? "先看本地市场；只有主动刷新才向远端获取新内容。" : "扩展状态与操作，清楚明了。");
        PageContent.Children.Add(Row(
            Action("已安装", () => { pluginMarketSelected = false; Render(); }, !pluginMarketSelected),
            Action("插件市场", () => { pluginMarketSelected = true; Render(); }, pluginMarketSelected)));
        var cache = Path.Combine(pluginDataRoot, "registry-cache.json");
        var bundled = Path.Combine(todoRoot, "plugin-registry-v1.json");
        var project = Path.GetFullPath(Path.Combine(skinsRoot, "..", "plugin-registry-template", "index-v1.json"));
        var source = new[] { cache, bundled, project }
            .FirstOrDefault(HasValidMarketIndex) ?? project;
        using var index = ReadJson(source);
        var records = index is null ? Array.Empty<JsonElement>() : Items(index.RootElement, "plugins")
            .Where(item => Value(item, "official") == "True").ToArray();
        var byId = records.Where(item => Value(item, "id") != "").ToDictionary(item => Value(item, "id"));
        if (pluginMarketSelected)
        {
            var toolbar = new Grid { ColumnSpacing = 10 };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition());
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var search = EditorControl(new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 42, VerticalContentAlignment = VerticalAlignment.Center });
            var searchWrap = new Grid();
            searchWrap.Children.Add(search);
            var searchHint = Text("搜索插件名称或用途", 14, muted: true);
            searchHint.VerticalAlignment = VerticalAlignment.Center;
            searchHint.Margin = new Thickness(13, 0, 0, 0);
            searchHint.IsHitTestVisible = false;
            searchWrap.Children.Add(searchHint);
            toolbar.Children.Add(searchWrap);
            var refresh = Action("刷新市场", RefreshMarket);
            Grid.SetColumn(refresh, 1);
            toolbar.Children.Add(refresh);
            var cacheTime = Text((source == cache ? "缓存 " : "内置 ") +
                (File.Exists(source) ? File.GetLastWriteTime(source).ToString("MM-dd HH:mm") : "尚未下载"), 12, muted: true);
            cacheTime.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cacheTime, 2);
            toolbar.Children.Add(cacheTime);
            PageContent.Children.Add(toolbar);
            if (records.Length == 0) { Empty("本地市场暂无插件。点击“刷新市场”获取索引。"); return; }
            var board = TileBoard();
            board.ItemWidth = 280;
            board.ItemHeight = 262;
            var filter = new List<(FrameworkElement Card, string Keywords)>();
            foreach (var record in records)
            {
                var id = Value(record, "id");
                var name = HumanPluginName(id, Value(record, "name"));
                var description = HumanPluginDescription(id, Value(record, "description"));
                var version = Value(record, "version");
                var installed = Directory.Exists(Path.Combine(pluginDataRoot, "Plugins", id));
                using var current = installed ? ReadJson(Path.Combine(pluginDataRoot, "Plugins", id, "current.json")) : null;
                var updateAvailable = !installed || HasNewMarketVersion(
                    current is null ? "" : Value(current.RootElement, "version"), version);
                var stack = new StackPanel { Spacing = 11 };
                var heading = new StackPanel { Spacing = 2 };
                heading.Children.Add(Text(name, 17, true));
                heading.Children.Add(Text("官方扩展 · v" + version, 11, muted: true));
                stack.Children.Add(heading);
                var desc = Text(description, 12, muted: true);
                desc.MaxLines = 3;
                desc.TextTrimming = TextTrimming.CharacterEllipsis;
                stack.Children.Add(desc);
                stack.Children.Add(Text(installed ? updateAvailable ? "有新版本" : "已是最新版本" : "可安装", 12, true, !installed || updateAvailable));
                Button installButton = null!;
                installButton = Action(installed ? "更新" : "安装插件",
                    () => InstallMarketPlugin(id, name, installed, installButton), updateAvailable);
                installButton.IsEnabled = updateAvailable;
                var cardLayout = new Grid { RowSpacing = 8 };
                cardLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                cardLayout.Children.Add(stack);
                Grid.SetRow(installButton, 1);
                cardLayout.Children.Add(installButton);
                var tile = Card(cardLayout, 18);
                tile.Width = 265;
                tile.Height = 246;
                board.Children.Add(tile);
                filter.Add((tile, (name + " " + description).ToLowerInvariant()));
            }
            search.TextChanged += (_, _) =>
            {
                searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                var query = search.Text.Trim().ToLowerInvariant();
                foreach (var entry in filter) entry.Card.Visibility =
                    query == "" || entry.Keywords.Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            };
            PageContent.Children.Add(board);
        }
        else
        {
            var installedRoot = Path.Combine(pluginDataRoot, "Plugins");
            var directories = Directory.Exists(installedRoot) ? Directory.EnumerateDirectories(installedRoot).ToArray() : [];
            PageContent.Children.Add(Text("已安装 · " + directories.Length, 16, true));
            PageContent.Children.Add(Row(Action("从本地安装", InstallLocalPlugin, true),
                Action("刷新状态", Render)));
            if (directories.Length == 0) { Empty("还没有安装插件。前往插件市场探索扩展。"); return; }
            var list = new StackPanel { Spacing = 10 };
            foreach (var directory in directories)
            {
                var id = Path.GetFileName(directory);
                using var current = ReadJson(Path.Combine(directory, "current.json"));
                var version = current is null ? "" : Value(current.RootElement, "version");
                var enabled = current is not null && Value(current.RootElement, "enabled") == "True";
                using var manifest = ReadJson(Path.Combine(directory, "versions", version, "plugin.json"));
                var name = manifest is null
                    ? byId.TryGetValue(id, out var market) ? HumanPluginName(id, Value(market, "name")) : HumanPluginName(id)
                    : HumanPluginName(id, Value(manifest.RootElement, "name"));
                var capability = manifest is null ? "" :
                    Items(manifest.RootElement, "capabilities").Select(item => item.GetString() ?? "")
                        .FirstOrDefault(value => value is "value_provider" or "todo_source") ?? "";
                var hasConfig = id == "io.github.kevendai.calendar-to-todo" ||
                    manifest is not null && Value(manifest.RootElement, "settings_schema") != "";
                var line = new Grid { ColumnSpacing = 16 };
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel { Spacing = 4 };
                info.Children.Add(Text(name, 18, true));
                info.Children.Add(Text("v" + version + " · " + (enabled ? "已启用" : "已禁用") +
                    " · " + InstalledPluginStatus(id, enabled), 12, muted: true));
                line.Children.Add(info);
                var toggleButton = Action(enabled ? "禁用" : "启用", () => TogglePlugin(id));
                var configButton = Action("配置", () => ConfigurePlugin(id));
                var runButton = Action("执行", () => RunInstalledPlugin(id, capability));
                var moreButton = PluginMoreActions(id, manifest?.RootElement.Clone());
                configButton.IsEnabled = hasConfig;
                runButton.IsEnabled = enabled && capability != "";
                var actions = Row(toggleButton, configButton, runButton, moreButton);
                actions.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(actions, 1);
                line.Children.Add(actions);
                list.Children.Add(Card(line, 16));
            }
            PageContent.Children.Add(list);
        }
    }

    private static bool HasValidMarketIndex(string path)
    {
        using var index = ReadJson(path);
        return index is not null && index.RootElement.TryGetProperty("plugins", out var plugins)
            && plugins.ValueKind == JsonValueKind.Array;
    }

    private static string HumanPluginName(string id, string fallback = "")
        => id switch
        {
            "io.github.kevendai.arxiv" => "arXiv 论文推荐",
            "io.github.kevendai.calendar-to-todo" => "日程转待办",
            "io.github.kevendai.paper-snapshot-sync" => "论文快照同步",
            "io.github.kevendai.ai-deepseek" => "DeepSeek AI 评分",
            "io.github.kevendai.translate-tencent" => "腾讯云翻译",
            "io.github.kevendai.ssdp-server-ip" => "SSDP 服务器 IP 同步",
            _ => fallback != "" ? fallback : id.Split('.').LastOrDefault()?.Replace('-', ' ') ?? "未命名插件"
        };

    private static string HumanPluginDescription(string id, string fallback)
        => id switch
        {
            "io.github.kevendai.arxiv" => "抓取并筛选 arXiv 论文，生成每日推荐。",
            "io.github.kevendai.calendar-to-todo" => "按规则把日程转换为待办，保留来源和时间。",
            "io.github.kevendai.paper-snapshot-sync" => "将论文推荐快照同步到服务器，便于多设备共享。",
            "io.github.kevendai.ai-deepseek" => "为论文等插件提供 DeepSeek AI 评分服务。",
            "io.github.kevendai.translate-tencent" => "提供腾讯云机器翻译服务。",
            "io.github.kevendai.ssdp-server-ip" => "自动发现服务器 IP，端口仍由你手动设置。",
            _ => fallback != "" ? fallback : "用这款插件扩展你的桌面工作台。"
        };

    private void TogglePlugin(string id)
    {
        var path = Path.Combine(pluginDataRoot, "Plugins", id, "current.json");
        try
        {
            var current = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("插件状态文件无效。");
            var enabled = current["enabled"]?.GetValue<bool>() ?? false;
            current["enabled"] = !enabled;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Render();
        }
        catch (Exception ex) { ShowMessage("插件状态未更改：" + ex.Message); }
    }

}
