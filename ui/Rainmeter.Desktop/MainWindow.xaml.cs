using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ListView Nav = new();
    private readonly TextBlock PageEyebrow = new();
    private readonly TextBlock PageTitle = new();
    private readonly TextBlock PageSubtitle = new();
    private readonly StackPanel PageContent = new();
    private readonly Grid Shell = new();
    private readonly Border Sidebar = new();
    private readonly TextBlock BrandTitle = new();
    private readonly TextBlock BrandSubtitle = new();
    private readonly TextBlock BrandFooter = new();
    private readonly string skinsRoot;
    private readonly string todoRoot;
    private readonly string calendarRoot;
    private string page = "home";
    private bool studioStyle;
    private Brush Ink => Brush(studioStyle ? 240 : 23, studioStyle ? 244 : 32, studioStyle ? 249 : 43);
    private Brush Muted => Brush(studioStyle ? 163 : 104, studioStyle ? 180 : 117, studioStyle ? 199 : 134);
    private Brush Surface => Brush(studioStyle ? 28 : 255, studioStyle ? 40 : 255, studioStyle ? 54 : 255);
    private static Brush Brush(int r, int g, int b) => new SolidColorBrush(Color.FromArgb(255, (byte)r, (byte)g, (byte)b));

    public MainWindow()
    {
        BuildWindow();
        skinsRoot = Environment.GetEnvironmentVariable("RAINMETER_SKINS_ROOT")
            ?? FindSkinsRoot();
        todoRoot = Path.Combine(skinsRoot, "Todo", "@Resources");
        calendarRoot = Path.Combine(skinsRoot, "Calendar", "@Resources");
        ExtendsContentIntoTitleBar = false;
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        Nav.SelectedIndex = 0;
        Render();
    }

    private void BuildWindow()
    {
        Title = "桌面组件";
        var root = Shell;
        root.Background = Brush(245, 245, 241);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Grid();
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition());
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new StackPanel { Spacing = 4, Margin = new Thickness(10, 0, 0, 36) };
        BrandTitle.Text = "桌面组件";
        BrandTitle.FontSize = 24;
        BrandTitle.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        BrandTitle.Foreground = Ink;
        BrandSubtitle.Text = "让每天井井有条";
        BrandSubtitle.FontSize = 12;
        BrandSubtitle.Foreground = Muted;
        brand.Children.Add(BrandTitle);
        brand.Children.Add(BrandSubtitle);
        sidebar.Children.Add(brand);
        foreach (var entry in new[] { ("总览", "home"), ("待办", "todo"), ("日历", "calendar"),
                     ("插件", "plugins"), ("外观与设置", "settings") })
            Nav.Items.Add(new ListViewItem { Content = entry.Item1, Tag = entry.Item2, MinHeight = 48 });
        Nav.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Nav.BorderThickness = new Thickness(0);
        Nav.SelectionChanged += Nav_SelectionChanged;
        Grid.SetRow(Nav, 1);
        sidebar.Children.Add(Nav);
        BrandFooter.Text = "RAINMETER · DESKTOP";
        BrandFooter.FontSize = 10;
        BrandFooter.Foreground = Muted;
        BrandFooter.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetRow(BrandFooter, 2);
        sidebar.Children.Add(BrandFooter);
        Sidebar.Background = Surface;
        Sidebar.Padding = new Thickness(20, 28, 20, 28);
        Sidebar.Child = sidebar;
        root.Children.Add(Sidebar);

        var body = new StackPanel { Spacing = 22, Margin = new Thickness(42, 30, 42, 48) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 6 };
        PageEyebrow.FontSize = 12;
        PageEyebrow.Foreground = new SolidColorBrush(Color.FromArgb(255, 41, 107, 223));
        PageTitle.FontSize = 34;
        PageTitle.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        PageTitle.TextWrapping = TextWrapping.Wrap;
        PageTitle.Foreground = Ink;
        PageSubtitle.FontSize = 14;
        PageSubtitle.TextWrapping = TextWrapping.Wrap;
        PageSubtitle.Foreground = Muted;
        titles.Children.Add(PageEyebrow);
        titles.Children.Add(PageTitle);
        titles.Children.Add(PageSubtitle);
        header.Children.Add(titles);
        var refresh = Action("刷新", Render);
        refresh.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        body.Children.Add(header);
        PageContent.Spacing = 16;
        body.Children.Add(PageContent);
        var scroll = new ScrollViewer { Content = body };
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
    }

    private static string FindSkinsRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var candidate = Path.Combine(cursor.FullName, "skins");
            if (Directory.Exists(Path.Combine(candidate, "Todo"))) return candidate;
            cursor = cursor.Parent;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rainmeter", "Skins");
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Nav.SelectedItem is ListViewItem item)
        {
            page = item.Tag?.ToString() ?? "home";
            Render();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Render();

    private void Render()
    {
        PageContent.Children.Clear();
        PageEyebrow.Text = page.ToUpperInvariant();
        switch (page)
        {
            case "todo": RenderTodo(); break;
            case "calendar": RenderCalendar(); break;
            case "plugins": RenderPlugins(); break;
            case "settings": RenderSettings(); break;
            default: RenderHome(); break;
        }
    }

    private static JsonDocument? ReadJson(string file)
    {
        try { return File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)) : null; }
        catch { return null; }
    }

    private static string Value(JsonElement obj, string key, string fallback = "")
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()
            : fallback;

    private static IEnumerable<JsonElement> Items(JsonElement root, string key)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToArray() : [];

    private TextBlock Text(string value, int size = 14, bool bold = false, bool muted = false)
        => new() { Text = value, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Foreground = muted ? Muted : Ink };

    private Border Card(UIElement content, int padding = 22) => new()
    {
        Background = Surface,
        CornerRadius = new CornerRadius(22),
        Padding = new Thickness(padding),
        Child = content
    };

    private static Button Action(string title, Action callback, bool primary = false)
    {
        var button = new Button { Content = title, CornerRadius = new CornerRadius(10), MinHeight = 38,
            Style = primary ? (Style)Application.Current.Resources["AccentButtonStyle"] : null };
        button.Click += (_, _) => callback();
        return button;
    }

    private static StackPanel Row(params UIElement[] elements)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var element in elements) row.Children.Add(element);
        return row;
    }

    private void Run(string root, string exe, params string[] args)
    {
        var path = Path.Combine(root, exe);
        if (!File.Exists(path)) { PageContent.Children.Add(Card(Text("尚未找到本地宿主：" + path, 13, muted: true))); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = string.Join(" ", args.Select(Quote)) }); }
        catch (Exception ex) { PageContent.Children.Add(Card(Text("启动失败：" + ex.Message, 13, muted: true))); }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private void Todo(string action, string id = "") => Run(todoRoot, "TodoHost.exe", action, id);
    private void Calendar(string action, string id = "") => Run(calendarRoot, "CalendarHost.exe", action, id);

    private void Heading(string title, string subtitle)
    {
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private void RenderHome()
    {
        Heading("今天，先做重要的事。", DateTime.Now.ToString("M月d日 dddd") + " · 你的桌面工作台");
        var todo = ReadJson(Path.Combine(todoRoot, "tasks.json"));
        var calendar = ReadJson(Path.Combine(calendarRoot, "calendar-cache.json"));
        int tasks = todo is null ? 0 : Items(todo.RootElement, "tasks").Count();
        int events = calendar is null ? 0 : Items(calendar.RootElement, "events").Count();
        var hero = new StackPanel { Spacing = 14 };
        hero.Children.Add(Text("GOOD MORNING", 12, true, true));
        hero.Children.Add(Text("把注意力留给真正重要的事", 28, true));
        hero.Children.Add(Text("用磁贴组织今天，点开卡片继续完成工作。", 14, muted: true));
        hero.Children.Add(Row(Action("新增待办", () => Todo("Add"), true), Action("新建日程", () => Calendar("New"))));
        PageContent.Children.Add(Card(hero, 30));
        var tiles = new Grid { ColumnSpacing = 16 };
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.Children.Add(StatTile("待办事项", tasks.ToString("00"), "让计划落地", () => { page = "todo"; Render(); }));
        var calTile = StatTile("日历日程", events.ToString("00"), "掌握接下来的安排", () => { page = "calendar"; Render(); });
        Grid.SetColumn(calTile, 1);
        tiles.Children.Add(calTile);
        PageContent.Children.Add(tiles);
        PageContent.Children.Add(Card(Text("磁贴布局与 Rainmeter 桌面配置保持独立；这里是完整的管理工作台。", 13, muted: true)));
    }

    private FrameworkElement StatTile(string title, string number, string note, Action action)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Text(title, 15, true));
        content.Children.Add(Text(number, 48, true));
        content.Children.Add(Text(note, 13, muted: true));
        var card = Card(content, 28);
        card.Tapped += (_, _) => action();
        return card;
    }

    private void RenderTodo()
    {
        Heading("待办事项", "清晰地看到下一步，轻松管理每件小事。");
        PageContent.Children.Add(Row(Action("新增待办", () => Todo("Add"), true), Action("管理待办", () => Todo("Manage")), Action("同步", () => Todo("Refresh"))));
        using var state = ReadJson(Path.Combine(todoRoot, "tasks.json"));
        if (state is null) { Empty("还没有待办数据。"); return; }
        var tasks = Items(state.RootElement, "tasks").ToArray();
        if (tasks.Length == 0) { Empty("一切就绪。新增一条待办，开始安排今天。"); return; }
        foreach (var task in tasks.Take(80))
        {
            var id = Value(task, "id");
            var title = Value(task, "title", "未命名待办");
            var note = Value(task, "description");
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(Text(title, 18, true));
            if (!string.IsNullOrWhiteSpace(note)) panel.Children.Add(Text(note, 13, muted: true));
            panel.Children.Add(Row(Action("打开", () => Todo("Open", id)), Action("编辑", () => Todo("Edit", id)), Action("完成 / 恢复", () => Todo("Toggle", id))));
            PageContent.Children.Add(Card(panel));
        }
    }

    private void RenderCalendar()
    {
        Heading("日历日程", "为生活和工作留出恰好的空间。");
        PageContent.Children.Add(Row(Action("新建日程", () => Calendar("New"), true), Action("管理日程", () => Calendar("Manage")), Action("同步日历", () => Calendar("Sync"))));
        using var cache = ReadJson(Path.Combine(calendarRoot, "calendar-cache.json"));
        if (cache is null) { Empty("还没有日历数据。"); return; }
        var events = Items(cache.RootElement, "events").ToArray();
        if (events.Length == 0) { Empty("暂无日程。你可以新建日程，或从日历服务同步。"); return; }
        foreach (var item in events.Take(80))
        {
            var panel = new StackPanel { Spacing = 7 };
            panel.Children.Add(Text(Value(item, "summary", Value(item, "title", "未命名日程")), 18, true));
            panel.Children.Add(Text(Value(item, "start", Value(item, "start_at")), 13, muted: true));
            var id = Value(item, "occurrence_key", Value(item, "uid"));
            panel.Children.Add(Row(Action("详情", () => Calendar("Detail", id)), Action("编辑", () => Calendar("Edit", id))));
            PageContent.Children.Add(Card(panel));
        }
    }

    private void RenderPlugins()
    {
        Heading("插件中心", "用恰好的扩展，让工作台更称手。");
        PageContent.Children.Add(Card(Row(Action("已安装插件与市场", () => Todo("Settings"), true), Action("立即同步", () => Todo("Refresh")))));
        var bundled = Path.Combine(todoRoot, "BundledPlugins");
        var projectBundled = Path.Combine(skinsRoot, "..", "plugins", "official");
        var source = Directory.Exists(bundled) ? bundled : projectBundled;
        if (!Directory.Exists(source)) { Empty("插件目录尚不可用。"); return; }
        foreach (var directory in Directory.EnumerateDirectories(source).OrderBy(Path.GetFileName))
        {
            var manifest = Directory.EnumerateFiles(directory, "*.json").FirstOrDefault();
            string name = Path.GetFileName(directory);
            if (manifest is not null)
            {
                using var data = ReadJson(manifest);
                if (data is not null) name = Value(data.RootElement, "name", name);
            }
            var stack = new StackPanel { Spacing = 5 };
            stack.Children.Add(Text(name, 18, true));
            stack.Children.Add(Text("本地插件 · " + Path.GetFileName(directory), 12, muted: true));
            PageContent.Children.Add(Card(stack));
        }
    }

    private void RenderSettings()
    {
        Heading("外观与设置", "选择适合你的氛围，其他配置清爽归位。");
        var style = new StackPanel { Spacing = 12 };
        style.Children.Add(Text("两种界面风格", 20, true));
        style.Children.Add(Text(studioStyle ? "当前：沉浸风格" : "当前：明亮风格", 13, muted: true));
        style.Children.Add(Row(Action("明亮风格", () => SetStyle(false), !studioStyle), Action("沉浸风格", () => SetStyle(true), studioStyle)));
        PageContent.Children.Add(Card(style));
        PageContent.Children.Add(Card(Row(Action("待办与插件设置", () => Todo("Settings")), Action("日历设置", () => Calendar("Settings")))));
        PageContent.Children.Add(Card(Text("数据位置：" + skinsRoot, 12, muted: true)));
    }

    private void SetStyle(bool immersive)
    {
        studioStyle = immersive;
        if (Content is FrameworkElement root) root.RequestedTheme = immersive ? ElementTheme.Dark : ElementTheme.Light;
        SystemBackdrop = immersive ? new DesktopAcrylicBackdrop() : new MicaBackdrop { Kind = MicaKind.Base };
        Shell.Background = immersive ? Brush(17, 25, 35) : Brush(245, 245, 241);
        Sidebar.Background = Surface;
        BrandTitle.Foreground = Ink;
        BrandSubtitle.Foreground = Muted;
        BrandFooter.Foreground = Muted;
        PageTitle.Foreground = Ink;
        PageSubtitle.Foreground = Muted;
        Render();
    }

    private void Empty(string message) => PageContent.Children.Add(Card(Text(message, 16, muted: true), 32));
}
