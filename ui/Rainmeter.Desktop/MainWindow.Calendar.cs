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
    private (JsonElement? Event, string Source) FindCalendarEvent(string id)
    {
        using var cache = ReadJson(Path.Combine(calendarRoot, "calendar-cache.json"));
        using var state = ReadJson(Path.Combine(calendarRoot, "calendar-state.json"));
        if (cache is not null)
            foreach (var item in Items(cache.RootElement, "events"))
                if (Value(item, "id") == id) return (item.Clone(), "caldav");
        if (state is not null)
            foreach (var item in Items(state.RootElement, "local_events"))
                if (Value(item, "id") == id) return (item.Clone(), "local");
        return (null, "local");
    }

    private async Task SaveCalendarDraftAsync(object payload)
    {
        var host = Path.Combine(calendarRoot, "CalendarHost.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("尚未找到日历宿主。", host);
        var input = Path.Combine(Path.GetTempPath(), "rainmeter-ui-calendar-" + Guid.NewGuid().ToString("N") + ".json");
        var result = input + ".result.json";
        try
        {
            File.WriteAllText(input, JsonSerializer.Serialize(payload));
            using var process = Process.Start(new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "UiSave", input }
            }) ?? throw new InvalidOperationException("无法启动日历宿主。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token);
            using var response = ReadJson(result);
            if (process.ExitCode != 0 || response is null || Value(response.RootElement, "ok") != "True")
                throw new InvalidOperationException(response is null
                    ? "日程保存失败，请检查日历连接。"
                    : Value(response.RootElement, "error", "日程保存失败。"));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(result)) File.Delete(result);
        }
    }

    private async void ShowCalendarEditor(string? id)
    {
        var (original, previousSource) = id is null ? (null, "local") : FindCalendarEvent(id);
        if (id is not null && original is null) { ShowMessage("这条日程已不存在，请刷新后重试。"); return; }
        if (original is JsonElement recurring && Value(recurring, "recurring") == "True")
        {
            ShowMessage("周期日程目前仍需使用高级编辑，避免改动周期规则。");
            Calendar("LegacyEdit", id!);
            return;
        }
        var title = new TextBox { Header = "标题", PlaceholderText = "这段时间安排什么？",
            Text = original is JsonElement oldTitle ? Value(oldTitle, "title") : "" };
        var start = new TextBox { Header = "开始时间", PlaceholderText = "YYYY-MM-DD HH:mm",
            Text = original is JsonElement oldStart ? DisplayDate(Value(oldStart, "start_at"))
                : DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        var end = new TextBox { Header = "结束时间", PlaceholderText = "YYYY-MM-DD HH:mm",
            Text = original is JsonElement oldEnd ? DisplayDate(Value(oldEnd, "end_at"))
                : DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm") };
        var location = new TextBox { Header = "地点", Text = original is JsonElement oldLocation ? Value(oldLocation, "location") : "" };
        var url = new TextBox { Header = "链接", Text = original is JsonElement oldUrl ? Value(oldUrl, "url") : "" };
        var description = new TextBox { Header = "备注", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 92, Text = original is JsonElement oldDescription ? Value(oldDescription, "description") : "" };
        var allDay = new CheckBox { Content = "全天日程", IsChecked = original is JsonElement oldAllDay && Value(oldAllDay, "all_day") == "True" };
        var source = new ComboBox { Header = "保存到", MinWidth = 180 };
        source.Items.Add(new ComboBoxItem { Content = "本地日历", Tag = "local" });
        source.Items.Add(new ComboBoxItem { Content = "CalDAV 日历", Tag = "caldav" });
        source.SelectedIndex = previousSource == "caldav" ? 1 : 0;
        source.IsEnabled = id is null;
        var error = Text("", 12, muted: true);
        var fields = new StackPanel { Spacing = 16, MaxWidth = 520 };
        foreach (var field in new UIElement[] { source, title, start, end, allDay, location, url, description, error })
            fields.Children.Add(field);
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = id is null ? "新建日程" : "编辑日程",
            Content = new ScrollViewer { Content = fields, MaxHeight = 550 },
            PrimaryButtonText = id is null ? "创建日程" : "保存修改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        bool saved = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var startsAt = ParseDate(start.Text, "开始时间") ?? throw new ArgumentException("开始时间不能为空。");
                var endsAt = ParseDate(end.Text, "结束时间") ?? throw new ArgumentException("结束时间不能为空。");
                if (allDay.IsChecked == true)
                {
                    var day = DateTimeOffset.Parse(startsAt).LocalDateTime.Date;
                    startsAt = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day)).ToString("O");
                    var next = day.AddDays(1);
                    endsAt = new DateTimeOffset(next, TimeZoneInfo.Local.GetUtcOffset(next)).ToString("O");
                }
                if (DateTimeOffset.Parse(endsAt) <= DateTimeOffset.Parse(startsAt))
                    throw new ArgumentException("结束时间必须晚于开始时间。");
                await SaveCalendarDraftAsync(new
                {
                    id = id ?? "",
                    source = (source.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local",
                    title = title.Text.Trim(),
                    start_at = startsAt,
                    end_at = endsAt,
                    all_day = allDay.IsChecked == true,
                    location = location.Text.Trim(),
                    url = url.Text.Trim(),
                    description = description.Text
                });
                saved = true;
            }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
        if (saved) Render();
    }

    private async void ShowCalendarDetail(string id)
    {
        var (item, source) = FindCalendarEvent(id);
        if (item is not JsonElement eventData) { ShowMessage("这条日程已不存在，请刷新后重试。"); return; }
        var content = new StackPanel { Spacing = 12, MinWidth = 340 };
        content.Children.Add(Text(Value(eventData, "title", "未命名日程"), 20, true));
        content.Children.Add(Text("开始  " + DisplayDate(Value(eventData, "start_at")), 14));
        content.Children.Add(Text("结束  " + DisplayDate(Value(eventData, "end_at")), 14));
        var location = Value(eventData, "location");
        if (location != "") content.Children.Add(Text("地点  " + location, 14));
        var description = Value(eventData, "description");
        if (description != "") content.Children.Add(Text(description, 14, muted: true));
        content.Children.Add(Text(source == "caldav" ? "CalDAV 日历" : "本地日历", 12, muted: true));
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = "日程详情",
            Content = content,
            PrimaryButtonText = "编辑",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) ShowCalendarEditor(id);
    }

    private void RenderCalendar()
    {
        Heading("日历管理", "桌面日程继续使用磁贴，在这里查看和安排。");
        PageContent.Children.Add(Row(Action("新建日程", () => ShowCalendarEditor(null), true), Action("同步日历", () => Calendar("Sync"))));
        using var cache = ReadJson(Path.Combine(calendarRoot, "calendar-cache.json"));
        using var state = ReadJson(Path.Combine(calendarRoot, "calendar-state.json"));
        var events = (cache is null ? [] : Items(cache.RootElement, "events"))
            .Concat(state is null ? [] : Items(state.RootElement, "local_events")).ToArray();
        if (events.Length == 0) { Empty("暂无日程。你可以新建日程，或从日历服务同步。"); return; }
        PageContent.Children.Add(Text("近期日程 · " + events.Length, 16, true));
        var board = TileBoard();
        foreach (var item in events.Take(80))
        {
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(Text("日程  /  " + DisplayDate(Value(item, "start", Value(item, "start_at"))), 12, muted: true));
            panel.Children.Add(Text(Value(item, "summary", Value(item, "title", "未命名日程")), 18, true));
            var id = Value(item, "id");
            panel.Children.Add(Row(Action("详情", () => ShowCalendarDetail(id)), Action("编辑", () => ShowCalendarEditor(id))));
            var tile = Card(panel, 22);
            tile.Width = 314;
            tile.Height = 214;
            board.Children.Add(tile);
        }
        PageContent.Children.Add(board);
    }

}
