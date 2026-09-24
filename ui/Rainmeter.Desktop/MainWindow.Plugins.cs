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
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = installed ? "更新插件？" : "安装插件？",
            Content = Text("将从官方发布地址" + (installed ? "更新" : "安装") + "“" + name + "”。插件是可执行程序，请确认你信任它。", 14),
            PrimaryButtonText = installed ? "更新" : "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
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

    private static bool HasNewMarketVersion(string installedVersion, string marketVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion)) return true;
        if (Version.TryParse(installedVersion, out var current) &&
            Version.TryParse(marketVersion, out var available))
            return available > current;
        return !string.Equals(installedVersion, marketVersion, StringComparison.OrdinalIgnoreCase);
    }

    private async void ConfigurePlugin(string id)
    {
        try
        {
            var host = Path.Combine(todoRoot, "TodoHost.exe");
            using var process = Process.Start(new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                ArgumentList = { "UiPluginConfig", id }
            }) ?? throw new InvalidOperationException("无法打开插件配置。");
            await process.WaitForExitAsync();
            Render();
        }
        catch (Exception ex) { ShowMessage("无法配置插件：" + ex.Message); }
    }

    private void RunInstalledPlugin(string id, string capability)
    {
        var verb = capability == "value_provider" ? "Values" :
            capability == "todo_source" ? "Sync" : "";
        if (verb == "") { ShowMessage("此插件由日历按需调用，无需手动执行。"); return; }
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(todoRoot, "PluginHost.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { verb, id }
            });
            Render();
        }
        catch (Exception ex) { ShowMessage("插件未启动：" + ex.Message); }
    }

    private string InstalledPluginStatus(string id, bool enabled)
    {
        if (!enabled) return "未启用";
        using var job = ReadJson(Path.Combine(pluginDataRoot, "PluginJobs", id + ".json"));
        if (job is null) return "尚未执行";
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
        StartPluginUtility("TodoHost.exe", "PluginAction", id, actionId);
    }

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
        Add("取消当前任务", () => StartPluginUtility("PluginHost.exe", "Cancel", id));
        Add("清除该插件创建的待办", () => StartPluginUtility("TodoHost.exe", "PluginClearTasks", id));
        Add("查看最近错误", () =>
        {
            var path = Path.Combine(pluginDataRoot, "PluginLogs", id + ".log");
            ShowMessage(File.Exists(path) ? File.ReadAllText(path) : "暂无插件日志。");
        });
        if (manifest is JsonElement data)
            foreach (var action in Items(data, "actions"))
            {
                var actionId = Value(action, "id");
                if (actionId == "") continue;
                var label = Value(action, "name", actionId);
                var warning = Value(action, "risk");
                var confirm = Value(action, "confirm") == "True";
                Add(label, () => RunCustomPluginAction(id, actionId, warning, confirm));
            }
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
            PageContent.Children.Add(Action("刷新状态", Render));
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
                var hasConfig = manifest is not null && Value(manifest.RootElement, "settings_schema") != "";
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
