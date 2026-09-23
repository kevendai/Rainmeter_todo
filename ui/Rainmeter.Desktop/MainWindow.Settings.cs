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
    private void RenderSettings()
    {
        Heading("外观与设置", "让窗口材质、管理入口和桌面磁贴各归其位。");
        PageContent.Children.Add(Text("外观", 18, true));
        PageContent.Children.Add(SettingsRow(Symbol.Preview, "窗口材质",
            studioStyle ? "亚克力 · 更通透的背景层次" : "云母 · 更稳定的背景层次",
            Row(Action("云母", () => SetStyle(false), !studioStyle),
                Action("亚克力", () => SetStyle(true), studioStyle))));
        PageContent.Children.Add(SettingsRow(Symbol.Edit, "界面配色",
            "日间、夜间，或跟随 Windows 外观。", Row(
                Action("日间", () => SetTheme("light"), themeMode == "light"),
                Action("夜间", () => SetTheme("dark"), themeMode == "dark"),
                Action("跟随", () => SetTheme("system"), themeMode == "system"))));
        PageContent.Children.Add(Text("桌面磁贴与管理", 18, true));
        PageContent.Children.Add(SettingsRow(Symbol.AllApps, "待办磁贴",
            "保留 Rainmeter 磁贴展示，在新界面管理任务。", Action("打开管理", () => Navigate("todo"))));
        PageContent.Children.Add(SettingsRow(Symbol.Calendar, "日程磁贴",
            "保留日程磁贴展示，在新界面查看安排。", Action("打开管理", () => Navigate("calendar"))));
        PageContent.Children.Add(SettingsRow(Symbol.Library, "插件中心",
            "查看已安装插件与扩展入口。", Action("打开插件", () => Navigate("plugins"))));
        PageContent.Children.Add(Text("兼容配置", 18, true));
        PageContent.Children.Add(SettingsRow(Symbol.Setting, "旧版高级配置",
            "迁移期间仍可打开尚未完成的配置功能。", Action("打开", () => Todo("LegacySettings"))));
        PageContent.Children.Add(Text("数据目录  " + skinsRoot, 12, muted: true));
    }

    private Border SettingsRow(Symbol icon, string title, string subtitle, FrameworkElement trailing)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new SymbolIcon(icon) { Foreground = Accent, Width = 24, Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(glyph);
        var copy = new StackPanel { Spacing = 3 };
        copy.Children.Add(Text(title, 16, true));
        copy.Children.Add(Text(subtitle, 12, muted: true));
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        trailing.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(trailing, 2);
        row.Children.Add(trailing);
        return Card(row, 18);
    }

    private void Navigate(string destination)
    {
        foreach (var candidate in Nav.Items.OfType<ListViewItem>())
            if (candidate.Tag?.ToString() == destination) { Nav.SelectedItem = candidate; break; }
    }

    private void SetStyle(bool immersive)
    {
        studioStyle = immersive;
        SystemBackdrop = immersive ? new DesktopAcrylicBackdrop() : new MicaBackdrop { Kind = MicaKind.Base };
        ApplyAppearance();
        SaveStyle(immersive);
        Render();
    }

    private void SetTheme(string mode)
    {
        themeMode = mode;
        Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)!);
        File.WriteAllText(ThemePath, mode);
        ApplyAppearance();
        Render();
    }

    private void ApplyAppearance()
    {
        if (Content is FrameworkElement root) root.RequestedTheme = DarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        Shell.Background = Canvas;
        Sidebar.Background = DarkTheme
            ? studioStyle ? Brush(24, 16, 32, 192) : Brush(24, 16, 32, 235)
            : Brush(241, 235, 247, studioStyle ? 220 : 245);
        BrandTitle.Foreground = Ink;
        BrandSubtitle.Foreground = Muted;
        BrandFooter.Foreground = Muted;
        PageTitle.Foreground = Ink;
        PageSubtitle.Foreground = Muted;
        PageEyebrow.Foreground = Accent;
        foreach (var item in Nav.Items.OfType<ListViewItem>())
            if (item.Content is StackPanel content)
                foreach (var child in content.Children)
                {
                    if (child is TextBlock label) label.Foreground = Ink;
                    if (child is SymbolIcon symbol) symbol.Foreground = Accent;
                }
        Nav.Resources["ListViewItemBackgroundSelected"] = DarkTheme ? Brush(106, 72, 141, 155) : Brush(181, 159, 207, 165);
        Nav.Resources["ListViewItemBackgroundSelectedPointerOver"] = DarkTheme ? Brush(106, 72, 141, 190) : Brush(181, 159, 207, 195);
        Nav.Resources["ListViewItemBackgroundSelectedPressed"] = DarkTheme ? Brush(106, 72, 141, 210) : Brush(181, 159, 207, 220);
    }

}
