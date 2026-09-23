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

    private async void InstallMarketPlugin(string id, string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = "安装插件？",
            Content = Text("将从官方发布地址安装“" + name + "”。插件是可执行程序，请确认你信任它。", 14),
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await RunMarketCommandAsync("UiMarketInstall", id); Render(); }
        catch (Exception ex) { ShowMessage("安装未完成：" + ex.Message); }
    }

    private void RenderPlugins()
    {
        Heading(pluginMarketSelected ? "插件市场" : "已安装插件",
            pluginMarketSelected ? "先看本地市场；只有主动刷新才向远端获取新内容。" : "扩展状态与操作，清楚明了。");
        PageContent.Children.Add(Row(
            Action("已安装", () => { pluginMarketSelected = false; Render(); }, !pluginMarketSelected),
            Action("插件市场", () => { pluginMarketSelected = true; Render(); }, pluginMarketSelected)));
        if (pluginMarketSelected) PageContent.Children.Add(Action("刷新市场", RefreshMarket));
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
            PageContent.Children.Add(Text((source == cache ? "本地市场缓存" : "内置官方索引") +
                " · " + (File.Exists(source) ? File.GetLastWriteTime(source).ToString("yyyy-MM-dd HH:mm") : "尚未下载"), 12, muted: true));
            if (records.Length == 0) { Empty("本地市场暂无插件。点击“刷新市场”获取索引。"); return; }
            var search = new TextBox { PlaceholderText = "搜索插件名称或用途", MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
            PageContent.Children.Add(search);
            var board = TileBoard();
            board.ItemHeight = 262;
            var filter = new List<(FrameworkElement Card, string Keywords)>();
            foreach (var record in records)
            {
                var id = Value(record, "id");
                var name = HumanPluginName(id, Value(record, "name"));
                var description = HumanPluginDescription(id, Value(record, "description"));
                var version = Value(record, "version");
                var installed = Directory.Exists(Path.Combine(pluginDataRoot, "Plugins", id));
                var stack = new StackPanel { Spacing = 11 };
                var heading = new StackPanel { Spacing = 2 };
                heading.Children.Add(Text(name, 17, true));
                heading.Children.Add(Text("官方扩展 · v" + version, 11, muted: true));
                stack.Children.Add(heading);
                var desc = Text(description, 12, muted: true);
                desc.MaxLines = 3;
                desc.TextTrimming = TextTrimming.CharacterEllipsis;
                stack.Children.Add(desc);
                stack.Children.Add(Text(installed ? "已安装" : "可安装", 12, true, !installed));
                stack.Children.Add(Action(installed ? "查看已安装" : "安装插件",
                    installed ? () => { pluginMarketSelected = false; Render(); } : () => InstallMarketPlugin(id, name), !installed));
                var tile = Card(stack, 18);
                tile.Width = 314;
                tile.Height = 246;
                board.Children.Add(tile);
                filter.Add((tile, (name + " " + description).ToLowerInvariant()));
            }
            search.TextChanged += (_, _) =>
            {
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
            if (directories.Length == 0) { Empty("还没有安装插件。前往插件市场探索扩展。"); return; }
            var board = TileBoard();
            board.ItemHeight = 220;
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
                var stack = new StackPanel { Spacing = 10 };
                stack.Children.Add(Text(name, 18, true));
                stack.Children.Add(Text("v" + version + "  ·  " + (enabled ? "已启用" : "已禁用"), 12, muted: !enabled));
                stack.Children.Add(Text("已安装到本机，可随时调整状态。", 12, muted: true));
                stack.Children.Add(Action(enabled ? "禁用插件" : "启用插件", () => TogglePlugin(id), true));
                var tile = Card(stack, 18);
                tile.Width = 314;
                tile.Height = 204;
                board.Children.Add(tile);
            }
            PageContent.Children.Add(board);
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
