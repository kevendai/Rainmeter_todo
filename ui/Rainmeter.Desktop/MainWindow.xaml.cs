using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Rainmeter.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ListView Nav = new();
    private readonly TextBlock PageEyebrow = new();
    private readonly TextBlock PageTitle = new();
    private readonly TextBlock PageSubtitle = new();
    private readonly StackPanel PageContent = new();
    private readonly Grid Shell = new();
    private readonly TaskCompletionSource<bool> uiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Border Sidebar = new();
    private readonly TextBlock BrandTitle = new();
    private readonly TextBlock BrandSubtitle = new();
    private readonly TextBlock BrandFooter = new();
    private readonly string skinsRoot;
    private readonly string todoRoot;
    private readonly string calendarRoot;
    private readonly TodoRepository todoRepository;
    private readonly string pluginDataRoot;
    private bool pluginMarketSelected;
    private static readonly string StylePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainmeterDesktop", "appearance.txt");
    private string page = "home";
    private bool studioStyle;
    private readonly UISettings systemAppearance = new();
    private string themeMode = LoadTheme();
    private bool DarkTheme => themeMode == "dark" || themeMode == "system" && SystemDark();
    private Brush Ink => DarkTheme ? Brush(244, 239, 249) : Brush(38, 30, 48);
    private Brush Muted => DarkTheme ? Brush(183, 172, 192) : Brush(103, 91, 115);
    private Brush Surface => DarkTheme
        ? studioStyle ? Brush(48, 40, 57, 208) : Brush(47, 40, 54, 243)
        : Brush(255, 255, 255, studioStyle ? 224 : 248);
    private Brush Accent => DarkTheme ? Brush(206, 147, 255) : Brush(103, 58, 166);
    private Brush PrimaryFill => Brush(123, 86, 196);
    private Brush Canvas => !DarkTheme
        ? new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(255, 249, 246, 253), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(255, 236, 243, 251), Offset = 1 }
            }
        }
        : studioStyle
        ? new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(184, 30, 18, 39), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(184, 25, 29, 48), Offset = 1 }
            }
        }
        : new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(245, 31, 22, 40), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(245, 26, 28, 47), Offset = 1 }
            }
        };
    private static Brush Brush(int r, int g, int b) => new SolidColorBrush(Color.FromArgb(255, (byte)r, (byte)g, (byte)b));
    private static Brush Brush(int r, int g, int b, int alpha) => new SolidColorBrush(Color.FromArgb((byte)alpha, (byte)r, (byte)g, (byte)b));
    private static readonly string ThemePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainmeterDesktop", "theme.txt");

    public MainWindow()
    {
        studioStyle = LoadStyle();
        systemAppearance.ColorValuesChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (themeMode == "system") { ApplyAppearance(); Render(); }
            });
        BuildWindow();
        Shell.Loaded += (_, _) => uiReady.TrySetResult(true);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "brand-mark.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        skinsRoot = Environment.GetEnvironmentVariable("RAINMETER_SKINS_ROOT")
            ?? FindSkinsRoot();
        todoRoot = Path.Combine(skinsRoot, "Todo", "@Resources");
        calendarRoot = Path.Combine(skinsRoot, "Calendar", "@Resources");
        todoRepository = new TodoRepository(todoRoot);
        pluginDataRoot = Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterDesktopWidgets");
        ExtendsContentIntoTitleBar = false;
        SystemBackdrop = studioStyle ? new DesktopAcrylicBackdrop() : new MicaBackdrop { Kind = MicaKind.Base };
        if (Content is FrameworkElement shell) shell.RequestedTheme = DarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        AppWindow.Resize(new SizeInt32(1190, 820));
        Nav.SelectedIndex = 0;
        var startPage = Environment.GetEnvironmentVariable("RAINMETER_UI_START_PAGE");
        if (startPage is "todo" or "calendar" or "plugins" or "settings") Navigate(startPage);
        Render();
    }

    public async void OpenInitialRoute(string[] route)
    {
        if (route.Length == 0) return;
        await uiReady.Task;
        // ContentDialog requires an attached XamlRoot and a completed first layout pass.
        await Task.Delay(150);
        DispatcherQueue.TryEnqueue(() =>
        {
            Activate();
            var destination = route[0] == "calendar" ? "calendar" : route[0] == "todo" ? "todo" : route[0];
            if (destination is not ("todo" or "calendar" or "plugins" or "settings")) return;
            Navigate(destination);
            var action = route.Length > 1 ? route[1] : "Manage";
            var id = route.Length > 2 ? route[2] : "";
            if (destination == "todo")
            {
                if (action == "Add") ShowTodoEditor(null);
                else if (action == "Edit" && id != "") ShowTodoEditor(id);
                else if (action == "Settings") Navigate("settings");
            }
            else if (destination == "calendar")
            {
                if (action == "New") ShowCalendarEditor(null);
                else if (action == "Edit" && id != "") ShowCalendarEditor(id);
                else if (action == "Detail" && id != "") ShowCalendarDetail(id);
                else if (action == "Settings") Navigate("settings");
            }
        });
    }

    private static bool LoadStyle()
    {
        try { return File.Exists(StylePath) && File.ReadAllText(StylePath).Trim() == "acrylic"; }
        catch { return false; }
    }

    private static string LoadTheme()
    {
        try
        {
            var value = File.ReadAllText(ThemePath).Trim();
            return value is "light" or "dark" or "system" ? value : "dark";
        }
        catch { return "dark"; }
    }

    private static bool SystemDark()
    {
        try
        {
            var color = new UISettings().GetColorValue(UIColorType.Background);
            return (color.R * 299 + color.G * 587 + color.B * 114) / 1000 < 128;
        }
        catch { return true; }
    }

    private static void SaveStyle(bool acrylic)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StylePath)!);
        File.WriteAllText(StylePath, acrylic ? "acrylic" : "mica");
    }

    private void BuildWindow()
    {
        Title = "桌面组件";
        var root = Shell;
        root.Background = Canvas;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Grid();
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition());
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new Grid { ColumnSpacing = 12, Margin = new Thickness(2, 0, 0, 36) };
        brand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        brand.ColumnDefinitions.Add(new ColumnDefinition());
        var brandImage = Path.Combine(AppContext.BaseDirectory, "Assets", "brand-mark.png");
        if (File.Exists(brandImage))
            brand.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(brandImage)),
                Width = 46,
                Height = 46
            });
        var brandText = new StackPanel { Spacing = 4 };
        BrandTitle.Text = "桌面组件";
        BrandTitle.FontSize = 24;
        BrandTitle.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        BrandTitle.Foreground = Ink;
        BrandSubtitle.Text = "让每天井井有条";
        BrandSubtitle.FontSize = 12;
        BrandSubtitle.Foreground = Muted;
        brandText.Children.Add(BrandTitle);
        brandText.Children.Add(BrandSubtitle);
        Grid.SetColumn(brandText, 1);
        brand.Children.Add(brandText);
        sidebar.Children.Add(brand);
        foreach (var entry in new[] { ("总览", "home", Symbol.Home), ("待办", "todo", Symbol.AllApps),
                     ("日历", "calendar", Symbol.Calendar), ("插件", "plugins", Symbol.Library),
                     ("外观与设置", "settings", Symbol.Setting) })
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 13 };
            label.Children.Add(new SymbolIcon(entry.Item3) { Foreground = Accent });
            label.Children.Add(Text(entry.Item1, 15));
            Nav.Items.Add(new ListViewItem { Content = label, Tag = entry.Item2, MinHeight = 54 });
        }
        Nav.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Nav.BorderThickness = new Thickness(0);
        Nav.Resources["ListViewItemBackgroundSelected"] = Brush(106, 72, 141, 155);
        Nav.Resources["ListViewItemBackgroundSelectedPointerOver"] = Brush(106, 72, 141, 190);
        Nav.Resources["ListViewItemBackgroundSelectedPressed"] = Brush(106, 72, 141, 210);
        Nav.SelectionChanged += Nav_SelectionChanged;
        Grid.SetRow(Nav, 1);
        sidebar.Children.Add(Nav);
        BrandFooter.Text = "RAINMETER · DESKTOP";
        BrandFooter.FontSize = 10;
        BrandFooter.Foreground = Muted;
        BrandFooter.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetRow(BrandFooter, 2);
        sidebar.Children.Add(BrandFooter);
        Sidebar.Background = DarkTheme ? Brush(24, 16, 32, 235) : Brush(241, 235, 247, 245);
        Sidebar.Padding = new Thickness(20, 28, 20, 28);
        Sidebar.Child = sidebar;
        root.Children.Add(Sidebar);

        var body = new StackPanel { Spacing = 22, Margin = new Thickness(34, 28, 34, 42) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 6 };
        PageEyebrow.FontSize = 12;
        PageEyebrow.Foreground = Accent;
        PageTitle.FontSize = 32;
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
        ForwardHandledWheel(body, scroll);
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
        Title = "桌面组件 · " + (page switch
        {
            "todo" => "待办管理",
            "calendar" => "日程管理",
            "plugins" => "插件中心",
            "settings" => "外观与设置",
            _ => "主页"
        });
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

    private Button Action(string title, Action callback, bool primary = false)
    {
        var button = new Button { Content = title, CornerRadius = new CornerRadius(10), MinHeight = 38,
            Style = primary ? (Style)Application.Current.Resources["AccentButtonStyle"] : null };
        if (primary)
        {
            button.Background = PrimaryFill;
            button.Foreground = Brush(255, 255, 255);
        }
        button.Click += (_, _) => callback();
        return button;
    }

    private static StackPanel Row(params UIElement[] elements)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var element in elements) row.Children.Add(element);
        return row;
    }

    private static VariableSizedWrapGrid TileBoard()
        => new()
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 330,
            ItemHeight = 230,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

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

    private void ShowMessage(string message) => PageContent.Children.Insert(0, Card(Text(message, 13, muted: true), 14));

    private static string DisplayDate(string? iso)
        => DateTimeOffset.TryParse(iso, out var date) ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";

    private static string? ParseDate(string input, string label)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (!DateTime.TryParseExact(input.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value))
            throw new ArgumentException(label + "格式应为 YYYY-MM-DD HH:mm。");
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value)).ToString("O");
    }

    private void Heading(string title, string subtitle)
    {
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private void RenderHome()
    {
        Heading("今天，先做重要的事。", DateTime.Now.ToString("M月d日 dddd") + " · 你的桌面工作台");
        using var todo = ReadJson(Path.Combine(todoRoot, "tasks.json"));
        using var calendar = ReadJson(Path.Combine(calendarRoot, "calendar-cache.json"));
        using var calendarState = ReadJson(Path.Combine(calendarRoot, "calendar-state.json"));
        int tasks = todo is null ? 0 : Items(todo.RootElement, "tasks")
            .Count(item => Value(item, "completed") != "True");
        var today = DateTimeOffset.Now.Date;
        var tomorrow = today.AddDays(1);
        int events = (calendar is null ? [] : Items(calendar.RootElement, "events"))
            .Concat(calendarState is null ? [] : Items(calendarState.RootElement, "local_events"))
            .Count(item => DateTimeOffset.TryParse(Value(item, "start_at"), out var start)
                && DateTimeOffset.TryParse(Value(item, "end_at"), out var end)
                && start.ToLocalTime().DateTime < tomorrow && end.ToLocalTime().DateTime > today);
        var hero = new StackPanel { Spacing = 14 };
        hero.Children.Add(Text(DateTime.Now.Hour switch
        {
            < 5 => "夜深了",
            < 11 => "早上好",
            < 14 => "中午好",
            < 18 => "下午好",
            _ => "晚上好"
        }, 12, true, true));
        hero.Children.Add(Text("把注意力留给真正重要的事", 28, true));
        hero.Children.Add(Text("用磁贴组织今天，点开卡片继续完成工作。", 14, muted: true));
        hero.Children.Add(Row(Action("新增待办", () => ShowTodoEditor(null), true), Action("新增日程", () => ShowCalendarEditor(null), true)));
        PageContent.Children.Add(Card(hero, 30));
        var tiles = new Grid { ColumnSpacing = 16 };
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.Children.Add(StatTile("待办事项", tasks.ToString("00"), "未完成", () => Navigate("todo")));
        var calTile = StatTile("日历日程", events.ToString("00"), "今天", () => Navigate("calendar"));
        Grid.SetColumn(calTile, 1);
        tiles.Children.Add(calTile);
        PageContent.Children.Add(tiles);
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

    private void Empty(string message) => PageContent.Children.Add(Card(Text(message, 16, muted: true), 32));
}
