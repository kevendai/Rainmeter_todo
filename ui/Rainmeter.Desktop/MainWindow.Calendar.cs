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

    private async Task RunCalendarEventActionAsync(string action, string id, string mode, bool hide)
    {
        var host = Path.Combine(calendarRoot, "CalendarHost.exe");
        var input = Path.Combine(Path.GetTempPath(), "rainmeter-ui-convert-" + Guid.NewGuid().ToString("N") + ".json");
        var result = input + ".result.json";
        try
        {
            File.WriteAllText(input, JsonSerializer.Serialize(new { id, mode, hide }));
            using var process = Process.Start(new ProcessStartInfo(host)
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { action, input }
            }) ?? throw new InvalidOperationException("无法启动日历宿主。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token);
            using var response = ReadJson(result);
            if (process.ExitCode != 0 || response is null || Value(response.RootElement, "ok") != "True")
                throw new InvalidOperationException(response is null ? "日程操作失败。"
                    : Value(response.RootElement, "error", "日程操作失败。"));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(result)) File.Delete(result);
        }
    }

    private async Task ShowCalendarConvertAsync(string id, JsonElement item)
    {
        var recurring = Value(item, "recurring") == "True";
        var hide = new CheckBox { Content = "转换后从今日日程磁贴隐藏", IsChecked = true };
        var content = new StackPanel { Spacing = 14, MinWidth = 340 };
        content.Children.Add(Text(Value(item, "title", "未命名日程"), 17, true));
        content.Children.Add(Text(recurring ? "可以只转换这一次，也可以让今后的周期自动转入待办。" : "时间与提醒会一并带入待办。", 13, muted: true));
        content.Children.Add(hide);
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot, Title = "转为待办", Content = content,
            PrimaryButtonText = recurring ? "仅本次" : "转为待办",
            SecondaryButtonText = recurring ? "本次及今后" : "",
            CloseButtonText = "取消"
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None) return;
        try
        {
            await RunCalendarEventActionAsync("UiConvert", id, choice == ContentDialogResult.Secondary ? "series" : "once", hide.IsChecked == true);
            Render();
        }
        catch (Exception ex) { Render(); ShowMessage(ex.Message); }
    }

    private async Task ShowCalendarDeleteAsync(string id, JsonElement item)
    {
        var recurring = Value(item, "recurring") == "True";
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot, Title = recurring ? "删除周期日程" : "删除日程",
            Content = Text(recurring ? "可以只在本机隐藏这一次，或删除整个周期日程。" : "确定删除这条日程吗？", 14),
            PrimaryButtonText = recurring ? "仅隐藏本次" : "删除",
            SecondaryButtonText = recurring ? "删除整个周期" : "",
            CloseButtonText = "取消"
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None) return;
        try
        {
            await RunCalendarEventActionAsync("UiDelete", id, choice == ContentDialogResult.Primary && recurring ? "once" : "series", false);
            Render();
        }
        catch (Exception ex) { Render(); ShowMessage(ex.Message); }
    }

    private async void ShowCalendarEditor(string? id)
    {
        var (original, previousSource) = id is null ? (null, "local") : FindCalendarEvent(id);
        if (id is not null && original is null) { ShowMessage("这条日程已不存在，请刷新后重试。"); return; }
        var preserveComplexRule = original is JsonElement protectedEvent && Value(protectedEvent, "recurrence_preserve") == "True";
        var editingSeries = original is JsonElement seriesEvent && Value(seriesEvent, "recurring") == "True";
        if (original is JsonElement recurring && Value(recurring, "recurring") == "True"
            && recurring.TryGetProperty("series_event", out var series) && series.ValueKind == JsonValueKind.Object)
            original = series.Clone();
        var rule = original is JsonElement ruleEvent ? Value(ruleEvent, "rrule") : "";
        var ruleTokens = rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2).ToArray();
        var duplicateRuleKeys = ruleTokens.GroupBy(part => part[0], StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
        var ruleParts = ruleTokens.GroupBy(part => part[0], StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(part => part[0].ToUpperInvariant(), part => part[1], StringComparer.OrdinalIgnoreCase);
        var supportedRule = !preserveComplexRule && !duplicateRuleKeys && ruleTokens.Length == rule.Split(';', StringSplitOptions.RemoveEmptyEntries).Length
            && (rule == "" || ruleParts.TryGetValue("FREQ", out var parsedFrequency)
            && new[] { "DAILY", "WEEKLY", "MONTHLY", "YEARLY" }.Contains(parsedFrequency.ToUpperInvariant())
            && ruleParts.Keys.All(key => new[] { "FREQ", "INTERVAL", "BYDAY", "BYMONTH", "BYMONTHDAY", "COUNT", "UNTIL" }.Contains(key)));
        var initialStart = original is JsonElement oldStart
            ? ExistingLocalDate(Value(oldStart, "start_at")) ?? DateTimeOffset.Now
            : DateTimeOffset.Now;
        var initialEnd = original is JsonElement oldEnd
            ? ExistingLocalDate(Value(oldEnd, "end_at")) ?? initialStart.AddHours(1)
            : initialStart.AddHours(1);
        var isAllDay = original is JsonElement oldAllDay && Value(oldAllDay, "all_day") == "True";
        var visibleEnd = isAllDay ? initialEnd.AddDays(-1) : initialEnd;
        var title = EditorControl(new TextBox { Header = "标题", PlaceholderText = "这段时间安排什么？",
            Text = original is JsonElement oldTitle ? Value(oldTitle, "title") : "" });
        var startDate = new CalendarDatePicker { Date = LocalPickerDate(initialStart.DateTime) };
        var startHour = TimePart(24, initialStart.Hour);
        var startMinute = TimePart(60, initialStart.Minute);
        var endDate = new CalendarDatePicker { Date = LocalPickerDate(visibleEnd.DateTime) };
        var endHour = TimePart(24, visibleEnd.Hour);
        var endMinute = TimePart(60, visibleEnd.Minute);
        var location = EditorControl(new TextBox { Header = "地点", PlaceholderText = "添加地点",
            Text = original is JsonElement oldLocation ? Value(oldLocation, "location") : "" });
        var url = EditorControl(new TextBox { Header = "链接", PlaceholderText = "https://...",
            Text = original is JsonElement oldUrl ? Value(oldUrl, "url") : "" });
        var description = EditorControl(new TextBox { Header = "备注", PlaceholderText = "添加备注…",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 82,
            Text = original is JsonElement oldDescription ? Value(oldDescription, "description") : "" });
        var allDay = new ToggleSwitch { Header = "全天", IsOn = isAllDay, OnContent = "", OffContent = "" };
        var repeat = EditorControl(new ComboBox { Header = "重复", HorizontalAlignment = HorizontalAlignment.Stretch });
        foreach (var (label, value) in new[] { ("不重复", "none"), ("每天", "daily"), ("每周", "weekly"), ("每月", "monthly"), ("每年", "yearly") })
            repeat.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var ruleFrequency = ruleParts.TryGetValue("FREQ", out var rawFrequency) ? rawFrequency.ToLowerInvariant() : "none";
        repeat.SelectedIndex = supportedRule ? ruleFrequency switch { "daily" => 1, "weekly" => 2, "monthly" => 3, "yearly" => 4, _ => 0 } : 0;
        repeat.IsEnabled = supportedRule;
        var repeatHint = Text(supportedRule ? "重复设置作用于整个日程系列。" : "此日程有复杂周期规则；保存时会保持原规则，日期与时间不可修改。", 12, muted: true);
        var intervalBox = EditorControl(new TextBox { Header = "每隔几次重复", InputScope = new Microsoft.UI.Xaml.Input.InputScope
        { Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Number } } },
            Text = ruleParts.TryGetValue("INTERVAL", out var originalInterval) ? originalInterval : "1" });
        var weekdayChoices = new List<(int Day, CheckBox Choice)>();
        var weekdayRow = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 78, ItemHeight = 34 };
        var selectedWeekdays = ruleParts.TryGetValue("BYDAY", out var originalDays)
            ? originalDays.Split(',').ToHashSet() : new HashSet<string>();
        var dayCodes = new[] { "MO", "TU", "WE", "TH", "FR", "SA", "SU" };
        var dayLabels = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        for (var day = 0; day < 7; day++)
        {
            var choice = new CheckBox { Content = dayLabels[day], IsChecked = selectedWeekdays.Contains(dayCodes[day]) };
            weekdayChoices.Add((day + 1, choice));
            weekdayRow.Children.Add(choice);
        }
        var endMode = EditorControl(new ComboBox { Header = "结束方式", HorizontalAlignment = HorizontalAlignment.Stretch });
        foreach (var (label, value) in new[] { ("一直重复", "never"), ("到指定日期", "until"), ("指定次数后", "count") })
            endMode.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        endMode.SelectedIndex = ruleParts.ContainsKey("COUNT") ? 2 : ruleParts.ContainsKey("UNTIL") ? 1 : 0;
        var untilDate = new CalendarDatePicker { Date = ruleParts.TryGetValue("UNTIL", out var originalUntil)
            && DateTime.TryParseExact(originalUntil[..Math.Min(8, originalUntil.Length)], "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedUntil)
                ? LocalPickerDate(parsedUntil) : LocalPickerDate(DateTime.Today.AddMonths(1)) };
        var countBox = EditorControl(new TextBox { Header = "重复次数", Text = ruleParts.TryGetValue("COUNT", out var originalCount) ? originalCount : "10" });
        var repeatDetails = new StackPanel { Spacing = 10 };
        repeatDetails.Children.Add(intervalBox);
        repeatDetails.Children.Add(weekdayRow);
        repeatDetails.Children.Add(endMode);
        repeatDetails.Children.Add(untilDate);
        repeatDetails.Children.Add(countBox);
        void UpdateRepeatControls()
        {
            var frequency = (repeat.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            repeatDetails.Visibility = supportedRule && frequency != "none" ? Visibility.Visible : Visibility.Collapsed;
            weekdayRow.Visibility = frequency == "weekly" ? Visibility.Visible : Visibility.Collapsed;
            var ending = (endMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            untilDate.Visibility = ending == "until" ? Visibility.Visible : Visibility.Collapsed;
            countBox.Visibility = ending == "count" ? Visibility.Visible : Visibility.Collapsed;
        }
        repeat.SelectionChanged += (_, _) => UpdateRepeatControls();
        endMode.SelectionChanged += (_, _) => UpdateRepeatControls();
        UpdateRepeatControls();
        var reminderValues = new[] { 5, 15, 30, 60, 300, 1440 };
        var reminderChoices = new List<(int Minutes, CheckBox Choice)>();
        var reminderPanel = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 125, ItemHeight = 36 };
        var existingReminders = original is JsonElement reminderEvent
            ? Items(reminderEvent, "reminders").Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToHashSet()
            : new HashSet<int>();
        foreach (var minutes in reminderValues)
        {
            var label = minutes >= 1440 ? "1 天前" : minutes >= 60 ? minutes / 60 + " 小时前" : minutes + " 分钟前";
            var choice = new CheckBox { Content = label, IsChecked = existingReminders.Contains(minutes) };
            reminderChoices.Add((minutes, choice));
            reminderPanel.Children.Add(choice);
        }
        var reminderSection = new StackPanel { Spacing = 6 };
        reminderSection.Children.Add(Text("提醒 · 日程开始前", 13, true));
        reminderSection.Children.Add(reminderPanel);
        startHour.IsEnabled = startMinute.IsEnabled = endHour.IsEnabled = endMinute.IsEnabled = !isAllDay;
        allDay.Toggled += (_, _) => startHour.IsEnabled = startMinute.IsEnabled =
            endHour.IsEnabled = endMinute.IsEnabled = !allDay.IsOn;
        var source = EditorControl(new ComboBox { Header = "保存到", HorizontalAlignment = HorizontalAlignment.Stretch });
        source.Items.Add(new ComboBoxItem { Content = "本地日历", Tag = "local" });
        var hasCalDav = File.Exists(Path.Combine(todoRoot, "caldav.secret"));
        if (hasCalDav || previousSource == "caldav")
            source.Items.Add(new ComboBoxItem { Content = "CalDAV 日历", Tag = "caldav" });
        source.SelectedIndex = previousSource == "caldav" || id is null && hasCalDav ? 1 : 0;
        source.IsEnabled = id is null;
        var error = Text("", 12, muted: true);
        var timeHeader = new Grid();
        timeHeader.ColumnDefinitions.Add(new ColumnDefinition());
        timeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var timeTitle = Text("日期与时间", 15, true);
        timeTitle.VerticalAlignment = VerticalAlignment.Center;
        timeHeader.Children.Add(timeTitle);
        Grid.SetColumn(allDay, 1);
        timeHeader.Children.Add(allDay);
        var extras = new StackPanel { Spacing = 12 };
        extras.Children.Add(location);
        extras.Children.Add(url);
        extras.Children.Add(description);
        extras.Visibility = id is not null && (location.Text != "" || url.Text != "" || description.Text != "")
            ? Visibility.Visible : Visibility.Collapsed;
        var moreToggle = Action("添加地点、链接或备注", () =>
            extras.Visibility = extras.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible);
        moreToggle.HorizontalAlignment = HorizontalAlignment.Left;
        var fields = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
        fields.Children.Add(Text(id is null ? "为这一天留一段时间。桌面日程磁贴会同步显示。" : "修改日程信息并同步到原来的日历来源。", 13, muted: true));
        fields.Children.Add(source);
        fields.Children.Add(title);
        fields.Children.Add(timeHeader);
        fields.Children.Add(CompactTodoTimeRow("开始", startDate, startHour, startMinute, false));
        fields.Children.Add(CompactTodoTimeRow("结束", endDate, endHour, endMinute, false));
        fields.Children.Add(repeat);
        fields.Children.Add(repeatDetails);
        fields.Children.Add(repeatHint);
        fields.Children.Add(reminderSection);
        var seriesConfirmation = new CheckBox { Content = "我知道保存会修改整个周期日程及后续重复项" };
        if (editingSeries) fields.Children.Add(seriesConfirmation);
        fields.Children.Add(moreToggle);
        fields.Children.Add(extras);
        fields.Children.Add(error);
        var dialog = EditorDialog(id is null ? "新建日程" : "编辑日程", fields,
            id is null ? "创建日程" : "保存修改");
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var startsAt = PickerIso(startDate, startHour, startMinute) ?? throw new ArgumentException("请选择开始日期。");
                var endsAt = PickerIso(endDate, endHour, endMinute) ?? throw new ArgumentException("请选择结束日期。");
                if (allDay.IsOn)
                {
                    var day = startDate.Date!.Value.LocalDateTime.Date;
                    var lastDay = endDate.Date!.Value.LocalDateTime.Date;
                    if (lastDay < day) throw new ArgumentException("结束日期不能早于开始日期。");
                    startsAt = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day)).ToString("O");
                    var next = lastDay.AddDays(1);
                    endsAt = new DateTimeOffset(next, TimeZoneInfo.Local.GetUtcOffset(next)).ToString("O");
                }
                if (DateTimeOffset.Parse(endsAt) <= DateTimeOffset.Parse(startsAt))
                    throw new ArgumentException("结束时间必须晚于开始时间。");
                if (!supportedRule && (startsAt != Value(original!.Value, "start_at") || endsAt != Value(original.Value, "end_at") || allDay.IsOn != isAllDay))
                    throw new ArgumentException("复杂周期的日期与时间已锁定。");
                if (!int.TryParse(intervalBox.Text, out var repeatInterval) || repeatInterval < 1 || repeatInterval > 365)
                    throw new ArgumentException("重复间隔须为 1 到 365。");
                if (!int.TryParse(countBox.Text, out var repeatCount) || repeatCount < 1 || repeatCount > 10000)
                    throw new ArgumentException("重复次数须为 1 到 10000。");
                if ((repeat.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "weekly"
                    && !weekdayChoices.Any(item => item.Choice.IsChecked == true))
                    throw new ArgumentException("请至少选择一个重复日。");
                if ((endMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "until" && untilDate.Date is null)
                    throw new ArgumentException("请选择周期结束日期。");
                if (editingSeries && seriesConfirmation.IsChecked != true)
                    throw new ArgumentException("请先确认要修改整个周期日程。");
                await SaveCalendarDraftAsync(new
                {
                    id = id ?? "",
                    source = (source.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local",
                    title = title.Text.Trim(),
                    start_at = startsAt,
                    end_at = endsAt,
                    all_day = allDay.IsOn,
                    location = location.Text.Trim(),
                    url = url.Text.Trim(),
                    description = description.Text,
                    reminders = existingReminders.Except(reminderValues)
                        .Concat(reminderChoices.Where(item => item.Choice.IsChecked == true).Select(item => item.Minutes)).ToArray(),
                    recurrence = new
                    {
                        frequency = supportedRule ? (repeat.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none" : "preserve",
                        interval = repeatInterval,
                        weekdays = weekdayChoices.Where(item => item.Choice.IsChecked == true).Select(item => item.Day).ToArray(),
                        end_mode = (endMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "never",
                        count = repeatCount,
                        until = untilDate.Date?.LocalDateTime.ToString("yyyy-MM-dd") ?? ""
                    }
                });
            }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
            finally { deferral.Complete(); }
        };
        Title = id is null ? "桌面组件 · 新建日程" : "桌面组件 · 编辑日程";
        try { await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            Render();
            ShowMessage("无法打开日程编辑窗口：" + ex.Message);
            return;
        }
        Render();
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
            SecondaryButtonText = "转为待办",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };
        var deleteRequested = false;
        content.Children.Add(Action("删除日程", () => { deleteRequested = true; dialog.Hide(); }));
        var action = await dialog.ShowAsync();
        if (deleteRequested) await ShowCalendarDeleteAsync(id, eventData);
        else if (action == ContentDialogResult.Primary) ShowCalendarEditor(id);
        else if (action == ContentDialogResult.Secondary) await ShowCalendarConvertAsync(id, eventData);
    }

    private void RenderCalendar()
    {
        Heading("日程管理", "桌面继续显示日程磁贴；这里按月浏览、按天查看安排。");
        PageContent.Children.Add(Row(
            Action("新增日程", () => ShowCalendarEditor(null), true),
            Action("同步", () => Calendar("Sync"))));
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
        var offset = ((int)calendarMonth.DayOfWeek + 6) % 7;
        var weekRows = (offset + DateTime.DaysInMonth(calendarMonth.Year, calendarMonth.Month) + 6) / 7;
        for (var row = 0; row <= weekRows; row++)
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
        for (var index = 0; index < weekRows * 7; index++)
        {
            var day = calendarMonth.AddDays(index - offset);
            var count = events.Count(item => CalendarOverlapsDay(item, day));
            var dayContent = new StackPanel { Spacing = 5 };
            var outsideMonth = day.Month != calendarMonth.Month;
            var dayNumber = Text(day.Day.ToString(), 16, day.Date == selectedCalendarDay.Date, outsideMonth);
            if (outsideMonth) dayNumber.Opacity = DarkTheme ? 0.45 : 0.5;
            dayContent.Children.Add(dayNumber);
            var dayCount = Text(count == 0 ? " " : count + " 项日程", 11, count > 0, count == 0);
            if (outsideMonth) dayCount.Opacity = 0.35;
            dayContent.Children.Add(dayCount);
            var dayButton = new Button
            {
                Content = dayContent,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 72,
                Padding = new Thickness(12, 9, 8, 6),
                Background = day.Date == selectedCalendarDay.Date ? Brush(91, 62, 124, DarkTheme ? 225 : 45)
                    : outsideMonth ? DarkTheme ? Brush(25, 21, 32) : Brush(238, 234, 243) : Surface,
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
