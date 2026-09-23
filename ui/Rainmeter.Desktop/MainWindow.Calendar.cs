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
    private DateTime calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime selectedCalendarDay = DateTime.Today;
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
        var when = new Grid { ColumnSpacing = 14 };
        when.ColumnDefinitions.Add(new ColumnDefinition());
        when.ColumnDefinitions.Add(new ColumnDefinition());
        when.Children.Add(start);
        Grid.SetColumn(end, 1);
        when.Children.Add(end);
        var essentials = new StackPanel { Spacing = 14 };
        essentials.Children.Add(title);
        essentials.Children.Add(when);
        essentials.Children.Add(allDay);
        var extras = new StackPanel { Spacing = 14 };
        extras.Children.Add(location);
        extras.Children.Add(url);
        extras.Children.Add(description);
        var fields = new StackPanel { Spacing = 17, Width = 640 };
        fields.Children.Add(Text(id is null ? "为这一天留一段时间。桌面日程磁贴会同步显示。" : "修改日程信息并同步到原来的日历来源。", 13, muted: true));
        fields.Children.Add(Card(source, 20));
        fields.Children.Add(Card(essentials, 20));
        fields.Children.Add(Card(extras, 20));
        fields.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = id is null ? "新建日程" : "编辑日程",
            Content = new ScrollViewer { Content = fields, MaxHeight = 570 },
            Width = 720,
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
        Heading("日程管理", "桌面继续显示日程磁贴；这里按月浏览、按天查看安排。");
        PageContent.Children.Add(Row(
            Action("新增日程", () => ShowCalendarEditor(null), true),
            Action("同步日历", () => Calendar("Sync"))));
        using var cache = ReadJson(Path.Combine(calendarRoot, "calendar-cache.json"));
        using var state = ReadJson(Path.Combine(calendarRoot, "calendar-state.json"));
        var events = (cache is null ? [] : Items(cache.RootElement, "events"))
            .Concat(state is null ? [] : Items(state.RootElement, "local_events"))
            .Where(item => DateTimeOffset.TryParse(Value(item, "start_at"), out _))
            .OrderBy(item => DateTimeOffset.Parse(Value(item, "start_at"))).ToArray();
        var monthBar = new Grid { ColumnSpacing = 10 };
        monthBar.ColumnDefinitions.Add(new ColumnDefinition());
        monthBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        monthBar.Children.Add(Text(calendarMonth.ToString("yyyy 年 M 月"), 22, true));
        var monthActions = Row(
            Action("上个月", () => MoveCalendarMonth(-1)),
            Action("今天", () => { selectedCalendarDay = DateTime.Today; calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); Render(); }),
            Action("下个月", () => MoveCalendarMonth(1)));
        Grid.SetColumn(monthActions, 1);
        monthBar.Children.Add(monthActions);
        PageContent.Children.Add(monthBar);
        var monthGrid = new Grid { ColumnSpacing = 5, RowSpacing = 5 };
        for (var column = 0; column < 7; column++)
            monthGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < 7; row++)
            monthGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var weekdays = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        for (var column = 0; column < 7; column++)
        {
            var label = Text(weekdays[column], 12, true, true);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 0, 0, 5);
            Grid.SetColumn(label, column);
            monthGrid.Children.Add(label);
        }
        var offset = ((int)calendarMonth.DayOfWeek + 6) % 7;
        for (var index = 0; index < 42; index++)
        {
            var day = calendarMonth.AddDays(index - offset);
            var count = events.Count(item => CalendarOverlapsDay(item, day));
            var dayContent = new StackPanel { Spacing = 5 };
            dayContent.Children.Add(Text(day.Day.ToString(), 16, day.Date == selectedCalendarDay.Date, day.Month != calendarMonth.Month));
            dayContent.Children.Add(Text(count == 0 ? " " : count + " 项日程", 11, count > 0, count == 0));
            var dayButton = new Button
            {
                Content = dayContent,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 72,
                Padding = new Thickness(12, 9, 8, 6),
                Background = day.Date == selectedCalendarDay.Date ? Brush(91, 62, 124, DarkTheme ? 225 : 45) : Surface,
                BorderBrush = day.Date == DateTime.Today ? Accent : Brush(0, 0, 0, 0),
                BorderThickness = new Thickness(day.Date == DateTime.Today ? 2 : 0),
                CornerRadius = new CornerRadius(12)
            };
            dayButton.Click += (_, _) =>
            {
                selectedCalendarDay = day.Date;
                calendarMonth = new DateTime(day.Year, day.Month, 1);
                Render();
            };
            Grid.SetColumn(dayButton, index % 7);
            Grid.SetRow(dayButton, index / 7 + 1);
            monthGrid.Children.Add(dayButton);
        }
        PageContent.Children.Add(monthGrid);
        var selected = events.Where(item => CalendarOverlapsDay(item, selectedCalendarDay)).ToArray();
        PageContent.Children.Add(Text(selectedCalendarDay.ToString("M 月 d 日 dddd") + "  ·  " + selected.Length + " 项", 19, true));
        if (selected.Length == 0) { Empty("这一天暂无日程。"); return; }
        foreach (var item in selected)
        {
            var id = Value(item, "id");
            var begins = DateTimeOffset.Parse(Value(item, "start_at")).ToLocalTime();
            var allDay = Value(item, "all_day") == "True";
            var row = new Grid { ColumnSpacing = 17 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Text(allDay ? "全天" : begins.ToString("HH:mm"), 16, true));
            var details = new StackPanel { Spacing = 5 };
            details.Children.Add(Text(Value(item, "title", "未命名日程"), 16, true));
            var location = Value(item, "location");
            details.Children.Add(Text(location == "" ? "日程安排" : location, 12, muted: true));
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            var actions = Row(Action("详情", () => ShowCalendarDetail(id)), Action("编辑", () => ShowCalendarEditor(id)));
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            PageContent.Children.Add(Card(row, 17));
        }
    }

    private void MoveCalendarMonth(int offset)
    {
        calendarMonth = calendarMonth.AddMonths(offset);
        selectedCalendarDay = calendarMonth;
        Render();
    }

    private static bool CalendarOverlapsDay(JsonElement item, DateTime day)
    {
        return DateTimeOffset.TryParse(Value(item, "start_at"), out var start)
            && DateTimeOffset.TryParse(Value(item, "end_at"), out var end)
            && start.ToLocalTime().DateTime < day.Date.AddDays(1)
            && end.ToLocalTime().DateTime > day.Date;
    }

}
