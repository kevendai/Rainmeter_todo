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
        var title = new TextBox { Header = "标题", PlaceholderText = "这件事叫什么？", Text = existing?.Title ?? "" };
        var target = new TextBox { Header = "打开目标", PlaceholderText = "链接或文件路径（可选）", Text = existing?.Target ?? "" };
        var labels = new TextBox { Header = "标签", PlaceholderText = "用逗号分隔", Text = string.Join("，", existing?.Labels ?? []) };
        var available = new TextBox { Header = "开始时间", PlaceholderText = "YYYY-MM-DD HH:mm（可选）", Text = DisplayDate(existing?.AvailableFrom) };
        var due = new TextBox { Header = "截止时间", PlaceholderText = "YYYY-MM-DD HH:mm（可选）", Text = DisplayDate(existing?.DueAt) };
        var note = new TextBox { Header = "备注", PlaceholderText = "补充一点背景或细节…",
            Text = existing?.Note ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110 };
        var error = Text("", 12, muted: true);
        var fields = new StackPanel { Spacing = 16, MaxWidth = 520 };
        foreach (var field in new UIElement[] { title, target, labels, available, due, note, error }) fields.Children.Add(field);
        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            Title = id is null ? "新增待办" : "编辑待办",
            Content = new ScrollViewer { Content = fields, MaxHeight = 550 },
            PrimaryButtonText = id is null ? "添加待办" : "保存修改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        bool saved = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var draft = new TodoDraft(id, title.Text, target.Text, note.Text,
                    labels.Text.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => value.Trim()).Where(value => value != "").Distinct().ToArray(),
                    ParseDate(available.Text, "开始时间"), ParseDate(due.Text, "截止时间"));
                todoRepository.Save(draft);
                saved = true;
            }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
        };
        await dialog.ShowAsync();
        if (saved) { RunTodoAction("Render"); Render(); }
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
        PageContent.Children.Add(Text("全部磁贴 · " + tasks.Length, 16, true));
        var board = TileBoard();
        board.ItemHeight = 272;
        foreach (var task in tasks.Take(80))
        {
            var id = Value(task, "id");
            var title = Value(task, "title", "未命名待办");
            var note = Value(task, "note");
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(Text("待办  /  " + Value(task, "source", "手动"), 12, muted: true));
            var titleText = Text(title, 19, true);
            titleText.MaxLines = 2;
            titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            panel.Children.Add(titleText);
            if (!string.IsNullOrWhiteSpace(note))
            {
                var noteText = Text(note, 13, muted: true);
                noteText.MaxLines = 2;
                noteText.TextTrimming = TextTrimming.CharacterEllipsis;
                panel.Children.Add(noteText);
            }
            var due = Value(task, "due_at");
            if (due != "") panel.Children.Add(Text("截止  " + DisplayDate(due), 12, muted: true));
            var actions = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var buttons = new[]
            {
                Action("打开", () => RunTodoAction("Open", id)),
                Action("编辑", () => ShowTodoEditor(id)),
                Action("完成 / 恢复", () => RunTodoAction("Toggle", id)),
                Action("删除", () => DeleteTodo(id, title))
            };
            for (int index = 0; index < buttons.Length; index++)
            {
                Grid.SetColumn(buttons[index], index % 2);
                Grid.SetRow(buttons[index], index / 2);
                actions.Children.Add(buttons[index]);
            }
            panel.Children.Add(actions);
            var tile = Card(panel, 20);
            tile.Width = 314;
            tile.Height = 256;
            board.Children.Add(tile);
        }
        PageContent.Children.Add(board);
    }

}
