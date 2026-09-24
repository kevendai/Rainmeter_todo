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
    private async void RunTodoAction(string action, string id = "")
    {
        var path = Path.Combine(todoRoot, "TodoHost.exe");
        if (!File.Exists(path)) { Render(); return; }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { action, id }
            });
            if (process is not null) await process.WaitForExitAsync();
            Render();
        }
        catch (Exception ex) { ShowMessage("操作未完成：" + ex.Message); }
    }

    private async void ShowTodoEditor(string? id)
    {
        TodoDraft? existing;
        try { existing = id is null ? null : todoRepository.Find(id); }
        catch (Exception ex) { ShowMessage(ex.Message); return; }
        if (id is not null && existing is null) { ShowMessage("这条待办已不存在，请刷新后重试。"); return; }
        var title = EditorControl(new TextBox { Header = "标题", PlaceholderText = "例如：整理实验结果", Text = existing?.Title ?? "" });
        var target = EditorControl(new TextBox { Header = "打开目标", PlaceholderText = "输入链接或选择本地文件", Text = existing?.Target ?? "" });
        var browse = Action("浏览文件…", () => _ = PickTodoTargetAsync(target));
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        browse.MinHeight = 36;
        browse.Height = 36;
        browse.Padding = new Thickness(10, 4, 10, 4);
        var targetRow = new Grid { ColumnSpacing = 10 };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition());
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetRow.Children.Add(target);
        Grid.SetColumn(browse, 1);
        targetRow.Children.Add(browse);
        var labelChoices = new List<CheckBox>();
        var options = new HashSet<string>(["工作", "学习", "生活", "论文", "其他"]);
        using (var state = ReadJson(Path.Combine(todoRoot, "tasks.json")))
            if (state is not null)
                foreach (var item in Items(state.RootElement, "tasks"))
                    foreach (var label in Items(item, "labels"))
                        if (label.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(label.GetString()))
                            options.Add(label.GetString()!);
        foreach (var label in existing?.Labels ?? []) options.Add(label);
        var labelGrid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal,
            ItemWidth = 102, ItemHeight = 35 };
        foreach (var option in options.OrderBy(x => x, StringComparer.CurrentCulture))
        {
            var choice = new CheckBox { Content = option, IsChecked = existing?.Labels.Contains(option) == true,
                MinWidth = 100 };
            labelChoices.Add(choice);
            labelGrid.Children.Add(choice);
        }
        var labelSection = new StackPanel { Spacing = 4 };
        labelSection.Children.Add(Text("标签", 13, true));
        labelSection.Children.Add(labelGrid);
        var note = EditorControl(new TextBox { Header = "备注", PlaceholderText = "补充一点背景或细节…",
            Text = existing?.Note ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 86 });
        var initialStart = ExistingLocalDate(existing?.AvailableFrom);
        var initialDue = ExistingLocalDate(existing?.DueAt);
        var availableDate = new CalendarDatePicker { Date = initialStart is null ? null : LocalPickerDate(initialStart.Value.DateTime) };
        var dueDate = new CalendarDatePicker { Date = initialDue is null ? null : LocalPickerDate(initialDue.Value.DateTime) };
        var availableHour = TimePart(24, initialStart?.Hour ?? 9);
        var availableMinute = TimePart(60, initialStart?.Minute ?? 0);
        var dueHour = TimePart(24, initialDue?.Hour ?? 18);
        var dueMinute = TimePart(60, initialDue?.Minute ?? 0);
        var error = Text("", 12, muted: true);
        var schedule = new StackPanel { Spacing = 12 };
        schedule.Children.Add(CompactTodoTimeRow("开始", availableDate, availableHour, availableMinute));
        schedule.Children.Add(CompactTodoTimeRow("截止", dueDate, dueHour, dueMinute));
        schedule.Children.Add(Text("日期留空表示不限定时间。", 12, muted: true));
        schedule.Visibility = initialStart is not null || initialDue is not null
            ? Visibility.Visible : Visibility.Collapsed;
        var timeToggle = Action("设置时间安排", () =>
            schedule.Visibility = schedule.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible);
        timeToggle.HorizontalAlignment = HorizontalAlignment.Left;
        var fields = new StackPanel { Spacing = 16, Width = 420 };
        fields.Children.Add(Text(id is null ? "把想做的事记下来，之后仍会显示在桌面磁贴。" : "调整内容与时间，桌面磁贴会同步更新。", 13, muted: true));
        fields.Children.Add(title);
        fields.Children.Add(targetRow);
        fields.Children.Add(labelSection);
        fields.Children.Add(timeToggle);
        fields.Children.Add(schedule);
        fields.Children.Add(note);
        fields.Children.Add(error);
        var dialog = EditorDialog(id is null ? "新增待办" : "编辑待办", fields,
            id is null ? "添加待办" : "保存修改");
        bool saved = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var draft = new TodoDraft(id, title.Text, target.Text, note.Text,
                    labelChoices.Where(choice => choice.IsChecked == true)
                        .Select(choice => choice.Content?.ToString() ?? "").Where(value => value != "").ToArray(),
                    PickerIso(availableDate, availableHour, availableMinute),
                    PickerIso(dueDate, dueHour, dueMinute));
                todoRepository.Save(draft);
                saved = true;
            }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
        };
        Title = id is null ? "桌面组件 · 新增待办" : "桌面组件 · 编辑待办";
        try { await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            Render();
            ShowMessage("无法打开待办编辑窗口：" + ex.Message);
            return;
        }
        Render();
        if (saved) RunTodoAction("Render");
    }

    private async Task PickTodoTargetAsync(TextBox target)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null) target.Text = file.Path;
        }
        catch (Exception ex) { ShowMessage("无法选择文件：" + ex.Message); }
    }

    private async void DeleteTodo(string id, string title)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = "删除待办？",
            Content = Text("将删除“" + title + "”。", 14),
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { todoRepository.Delete(id); RunTodoAction("Render"); Render(); }
        catch (Exception ex) { ShowMessage(ex.Message); }
    }

    private void RenderTodo()
    {
        Heading("待办管理", "桌面仍以磁贴展示；在这里整理、编辑和安排它们。");
        PageContent.Children.Add(Row(Action("新增待办", () => ShowTodoEditor(null), true), Action("同步", () => RunTodoAction("Refresh"))));
        using var state = ReadJson(Path.Combine(todoRoot, "tasks.json"));
        if (state is null) { Empty("还没有待办数据。"); return; }
        var tasks = Items(state.RootElement, "tasks").ToArray();
        if (tasks.Length == 0) { Empty("一切就绪。新增一条待办，开始安排今天。"); return; }
        var pending = tasks.Where(task => Value(task, "completed") != "True").ToArray();
        var completed = tasks.Where(task => Value(task, "completed") == "True").ToArray();
        RenderTodoSection("未完成", pending, false);
        RenderTodoSection("已完成", completed, true);
    }

    private void RenderTodoSection(string heading, JsonElement[] tasks, bool done)
    {
        PageContent.Children.Add(Text($"{heading} · {tasks.Length}", 18, true));
        if (tasks.Length == 0)
        {
            PageContent.Children.Add(Text(done ? "还没有已完成的待办。" : "当前没有未完成的待办。", 13, muted: true));
            return;
        }
        foreach (var task in tasks.Take(100))
        {
            var id = Value(task, "id");
            var title = Value(task, "title", "未命名待办");
            var note = Value(task, "note");
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { IsChecked = done, VerticalAlignment = VerticalAlignment.Center };
            check.Click += (_, _) => RunTodoAction("Toggle", id);
            row.Children.Add(check);
            var details = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var titleText = Text(title, 16, !done, done);
            titleText.MaxLines = 1;
            titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Children.Add(titleText);
            var due = Value(task, "due_at");
            var summary = new[] { due == "" ? "" : "截止 " + DisplayDate(due),
                note.Length == 0 ? "" : note.ReplaceLineEndings(" ") }
                .Where(value => value != "");
            var summaryText = Text(string.Join(" · ", summary), 12, muted: true);
            summaryText.MaxLines = 1;
            summaryText.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Children.Add(summaryText);
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            var actions = Row();
            if (Value(task, "target") != "") actions.Children.Add(Action("打开", () => RunTodoAction("Open", id)));
            actions.Children.Add(Action("编辑", () => ShowTodoEditor(id)));
            actions.Children.Add(Action("删除", () => DeleteTodo(id, title)));
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            var card = Card(row, 15);
            card.Opacity = done ? 0.7 : 1;
            PageContent.Children.Add(card);
        }
    }

}
