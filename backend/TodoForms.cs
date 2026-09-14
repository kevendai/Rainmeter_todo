using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using RainmeterBackend;

internal static partial class TodoApp
{
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmGetLineCount = 0x00BA;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    private sealed class EditorResult { public string Title, Target, Note, Available, Due; public List<string> Labels; }
    private static EditorResult ShowEditor(Dictionary<string, object> task)
    {
        bool editing = task != null;
        Form f = LightUi.Form(editing ? "修改待办" : "新增待办", 560, 840); int x = 28, w = 504;
        LightUi.Heading(f, editing ? "修改待办" : "新增待办", editing ? "调整待办事项，明确目标，高效执行" : "创建一项新的待办事项，明确目标，高效执行", "todo.svg");
        Button close = LightUi.CloseButton(f); f.Controls.Add(close);

        TextBox title = Field(f, "标题 *", x, 112, w, editing ? S(task, "title") : "");
        TextBox target = FieldWithButton(f, "打开目标", x, 204, w, editing ? S(task, "target") : "", "浏览");
        TextBox available = DateField(f, "开始时间", x, 304, 230, RuntimeUtil.Date(task, "available_from"));
        TextBox due = DateField(f, "截止时间", 302, 304, 230, RuntimeUtil.Date(task, "due_at"));

        HashSet<string> selectedLabels = new HashSet<string>(editing ? Labels(task) : Enumerable.Empty<string>());
        Panel labelPanel = LabelSelector("标签", x, 400, w, CommonLabels(task), selectedLabels);
        f.Controls.Add(labelPanel);

        f.Controls.Add(LightUi.Label("备注", x, 500, w));
        Panel noteSurface = new Panel { Left = x, Top = 522, Width = w, Height = 144, BackColor = LightUi.Panel };
        LightUi.Round(noteSurface, 10);
        TextBox note = new TextBox { Left = 14, Top = 14, Width = w - 28, Height = 116, Text = editing ? S(task, "note") : "", Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, BorderStyle = BorderStyle.None, BackColor = LightUi.Panel, ForeColor = LightUi.Text, Font = LightUi.UiFont(10F) };
        noteSurface.Controls.Add(note); f.Controls.Add(noteSurface);
        labelPanel.BringToFront();
        Label hint = LightUi.Label("标题为必填项。截止时间不能早于开始时间。", x, 682, 340); f.Controls.Add(hint);
        Button cancel = LightUi.Button("取消", 210, 722, 112, DialogResult.Cancel), save = LightUi.PrimaryButton(editing ? "+ 保存修改" : "+ 添加待办", 338, 722, 194, DialogResult.OK);
        f.Controls.Add(cancel); f.Controls.Add(save); f.AcceptButton = save; f.CancelButton = cancel;
        while (f.ShowDialog() == DialogResult.OK)
        {
            if (String.IsNullOrWhiteSpace(title.Text)) { LightUi.Error("标题不能为空"); continue; }
            DateTimeOffset a = default(DateTimeOffset), d = default(DateTimeOffset); string av = "", du = "";
            if (!String.IsNullOrWhiteSpace(available.Text) && !TryEditorDate(available.Text, out a)) { LightUi.Error("开始时间格式应为 YYYY-MM-DD HH:mm"); continue; } else if (!String.IsNullOrWhiteSpace(available.Text)) av = RuntimeUtil.Iso(a);
            if (!String.IsNullOrWhiteSpace(due.Text) && !TryEditorDate(due.Text, out d)) { LightUi.Error("截止时间格式应为 YYYY-MM-DD HH:mm"); continue; } else if (!String.IsNullOrWhiteSpace(due.Text)) du = RuntimeUtil.Iso(d);
            if (av != "" && du != "" && d < a) { LightUi.Error("截止时间不能早于开始时间"); continue; }
            return new EditorResult { Title = title.Text.Trim(), Target = target.Text.Trim(), Note = note.Text, Available = av, Due = du, Labels = selectedLabels.Where(v => v != "").Distinct().ToList() };
        }
        return null;
    }


    private static void RenderUiScaleSkins()
    {
        string todoExe = Application.ExecutablePath;
        using (Process todo = Process.Start(new ProcessStartInfo(todoExe, "Render") { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (todo != null && !todo.WaitForExit(15000)) throw new Exception("待办磁贴刷新超时");
            if (todo != null && todo.ExitCode != 0) throw new Exception("待办磁贴刷新失败");
        }
        string calendarExe = Path.GetFullPath(Path.Combine(ResourceDir, "..", "..", "Calendar", "@Resources", "CalendarHost.exe"));
        if (File.Exists(calendarExe))
        {
            using (Process calendar = Process.Start(new ProcessStartInfo(calendarExe, "Render") { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (calendar != null && !calendar.WaitForExit(15000)) throw new Exception("日程磁贴刷新超时");
                if (calendar != null && calendar.ExitCode != 0) throw new Exception("日程磁贴刷新失败");
            }
        }
        // Both generated includes are ready at this point. Refresh the Rainmeter
        // app once so the two tiles cannot remain on different/previous scales.
        RuntimeUtil.RefreshAll();
    }


    private static IEnumerable<string> CommonLabels(Dictionary<string, object> task)
    {
        string[] defaults = { "论文", "考试", "功能", "修复", "日程", "已读", "自动归档" };
        return defaults.Concat(task == null ? Enumerable.Empty<string>() : Labels(task)).Where(x => !String.IsNullOrWhiteSpace(x)).Distinct();
    }

    private static Panel LabelSelector(string title, int x, int y, int width, IEnumerable<string> options, HashSet<string> selected)
    {
        Panel panel = new Panel { Left = x, Top = y, Width = width, Height = 86, BackColor = Color.Transparent };
        panel.Controls.Add(LightUi.Label(title, 0, 0, width));
        Panel surface = new Panel { Left = 0, Top = 28, Width = width, Height = 56, BackColor = LightUi.Panel, AutoScroll = true };
        LightUi.Round(surface, 10);
        panel.Controls.Add(surface);
        Button expand = LightUi.Button("展开", width - 74, 12, 60, DialogResult.None);
        expand.Height = 32;
        expand.Font = LightUi.UiFont(9F, FontStyle.Bold);
        expand.UseVisualStyleBackColor = false;
        expand.BackColor = LightUi.AccentFill;
        expand.ForeColor = Color.White;
        expand.FlatAppearance.BorderColor = Color.FromArgb(31, 103, 201);
        expand.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 116, 224);
        expand.FlatAppearance.MouseDownBackColor = Color.FromArgb(22, 88, 176);
        expand.MouseEnter += delegate { if (expand.Enabled) expand.BackColor = Color.FromArgb(31, 116, 224); };
        expand.MouseLeave += delegate { expand.BackColor = LightUi.AccentFill; };
        surface.Controls.Add(expand);
        HashSet<Button> firstRowChips = new HashSet<Button>();
        int left = 12, top = 13;
        foreach (string label in options)
        {
            int buttonWidth = Math.Max(58, Math.Min(104, TextRenderer.MeasureText(label, LightUi.UiFont(9F)).Width + 28));
            if (left + buttonWidth > width - 76) { left = 12; top += 32; }
            Button button = LightUi.Button(label, left, top, buttonWidth, DialogResult.None);
            button.Height = 28;
            button.Tag = label;
            button.Visible = top == 13;
            if (top == 13) firstRowChips.Add(button);
            PaintLabelChoice(button, selected.Contains(label));
            button.Click += delegate(object sender, EventArgs e) {
                Button current = (Button)sender;
                string value = Convert.ToString(current.Tag);
                if (selected.Contains(value)) selected.Remove(value); else selected.Add(value);
                PaintLabelChoice(current, selected.Contains(value));
            };
            button.MouseEnter += delegate(object sender, EventArgs e) {
                Button current = (Button)sender;
                PaintLabelChoice(current, selected.Contains(Convert.ToString(current.Tag)));
            };
            button.MouseLeave += delegate(object sender, EventArgs e) {
                Button current = (Button)sender;
                PaintLabelChoice(current, selected.Contains(Convert.ToString(current.Tag)));
            };
            surface.Controls.Add(button);
            left += buttonWidth + 8;
        }
        int expandedSurfaceLogicalHeight = Math.Min(200, Math.Max(94, surface.Controls.Cast<Control>().Where(control => control != expand).Select(control => control.Bottom).DefaultIfEmpty(42).Max() + 12));
        int expandedPanelLogicalHeight = expandedSurfaceLogicalHeight + 32;
        bool expanded = false;
        expand.Click += delegate {
            expanded = !expanded;
            surface.Height = UiScale.Logical(surface, expanded ? expandedSurfaceLogicalHeight : 56);
            panel.Height = UiScale.Logical(panel, expanded ? expandedPanelLogicalHeight : 86);
            expand.Text = expanded ? "收起" : "展开";
            expand.BackColor = LightUi.AccentFill;
            expand.ForeColor = Color.White;
            if (expanded) panel.BringToFront();
            foreach (Control control in surface.Controls)
            {
                Button chip = control as Button;
                if (chip != null && chip != expand) chip.Visible = expanded || firstRowChips.Contains(chip);
            }
        };
        return panel;
    }

    private static void PaintLabelChoice(Button button, bool active)
    {
        button.BackColor = active ? LightUi.Selected : LightUi.Panel;
        button.ForeColor = active ? LightUi.Accent : LightUi.Text;
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
    }

    private static TextBox Field(Control f, string label, int x, int y, int width, string text)
    {
        f.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 28, Height = 24, AutoSize = false, Text = text ?? "", BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = LightUi.UiFont(10F) };
        surface.Controls.Add(box);
        f.Controls.Add(surface);
        return box;
    }

    private static TextBox PasswordField(Control f, string label, int x, int y, int width, string text)
    {
        f.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 92, Height = 24, AutoSize = false, Text = text ?? "", UseSystemPasswordChar = true, BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = LightUi.UiFont(10F) };
        Button reveal = LightUi.Button("显示", width - 70, 8, 56, DialogResult.None);
        reveal.Height = 34;
        reveal.Click += delegate {
            box.UseSystemPasswordChar = !box.UseSystemPasswordChar;
            reveal.Text = box.UseSystemPasswordChar ? "显示" : "隐藏";
        };
        surface.Controls.Add(box);
        surface.Controls.Add(reveal);
        f.Controls.Add(surface);
        return box;
    }

    private static TextBox FieldWithButton(Form f, string label, int x, int y, int width, string text, string buttonText)
    {
        f.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 98, Height = 24, AutoSize = false, Text = text ?? "", BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = LightUi.UiFont(10F) };
        Button browse = LightUi.Button(buttonText, width - 78, 8, 64, DialogResult.None);
        browse.Height = 34;
        browse.Click += delegate {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择打开目标";
                dialog.CheckFileExists = false;
                dialog.CheckPathExists = true;
                dialog.Filter = "所有文件 (*.*)|*.*";
                if (dialog.ShowDialog() == DialogResult.OK) box.Text = dialog.FileName;
            }
        };
        surface.Controls.Add(box); surface.Controls.Add(browse); f.Controls.Add(surface);
        return box;
    }

    private static TextBox SearchField(Form f, int x, int y, int width)
    {
        Panel surface = new Panel { Left = x, Top = y, Width = width, Height = 42, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        Label icon = new Label { Left = 12, Top = 11, Width = 22, Height = 22, Text = "\xE721", Font = LightUi.IconFont(9F), ForeColor = LightUi.Muted, BackColor = Color.Transparent };
        TextBox box = new TextBox { Left = 38, Top = 12, Width = width - 50, Height = 22, AutoSize = false, Text = "", BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = LightUi.UiFont(9F) };
        surface.Controls.Add(icon); surface.Controls.Add(box); f.Controls.Add(surface);
        return box;
    }

    private static TextBox DateField(Form f, string label, int x, int y, int width, DateTimeOffset? value)
    {
        f.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 58, Height = 24, AutoSize = false, Text = DateEdit(value), ReadOnly = true, BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = LightUi.UiFont(10F) };
        Button choose = LightUi.Button("\xE787", width - 42, 8, 30, DialogResult.None);
        choose.Height = 34;
        choose.Font = LightUi.IconFont(9F);
        choose.Click += delegate {
            string picked = PickDateTime(box.Text);
            if (picked != null) box.Text = picked;
        };
        surface.Controls.Add(box); surface.Controls.Add(choose); f.Controls.Add(surface);
        return box;
    }

    private static string PickDateTime(string current)
    {
        DateTime initial;
        if (!DateTime.TryParseExact(current, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out initial)) initial = DateTime.Now;
        Form dialog = LightUi.Form("选择时间", 360, 210);
        LightUi.Heading(dialog, "选择时间", "选择日期和时间；清空表示不限制。");
        DateTimePicker picker = new DateTimePicker { Left = 26, Top = 92, Width = 308, Height = 32, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Value = initial, Font = LightUi.UiFont(10F) };
        Button clear = LightUi.Button("清空", 82, 150, 76, DialogResult.Retry);
        Button cancel = LightUi.Button("取消", 168, 150, 76, DialogResult.Cancel);
        Button ok = LightUi.PrimaryButton("确定", 254, 150, 80, DialogResult.OK);
        dialog.Controls.AddRange(new Control[] { picker, clear, cancel, ok });
        DialogResult result = dialog.ShowDialog();
        if (result == DialogResult.OK) return picker.Value.ToString("yyyy-MM-dd HH:mm");
        if (result == DialogResult.Retry) return "";
        return null;
    }
    private static string DateEdit(DateTimeOffset? value) { return value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : ""; }
    private static bool TryEditorDate(string text, out DateTimeOffset result)
    {
        DateTime local; if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) { result = default(DateTimeOffset); return false; }
        result = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)); return true;
    }


    private static void Manage(Dictionary<string, object> state, ref bool refresh)
    {
        bool managerChanged = false;
        Func<LockedStateAction, bool> mutate = delegate(LockedStateAction mutation) {
            Dictionary<string, object> latest = null;
            bool changed = false;
            int result = WithLockedState(delegate(Dictionary<string, object> current, ref bool shouldRefresh) {
                mutation(current, ref shouldRefresh);
                changed = shouldRefresh;
                latest = current;
            });
            if (result != 0 || latest == null) throw new Exception("待办数据保存失败，请重试。");
            state = latest;
            managerChanged |= changed;
            return changed;
        };
        Form f = LightUi.Form("全部任务", 1120, 760); LightUi.Heading(f, "全部任务", "管理你的所有待办事项，支持批量操作", "all-tasks.svg");
        Button close = LightUi.CloseButton(f); f.Controls.Add(close);
        TextBox search = SearchField(f, 610, 38, 378);
        int filter = 0;
        CheckBox onlyOpen = new CheckBox { Left = 944, Top = 128, Width = 130, Height = 24, Text = "只看未完成", ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = LightUi.UiFont(9F) };
        f.Controls.Add(onlyOpen);
        Button allTab = LightUi.Button("全部  0", 32, 118, 96, DialogResult.None), overdueTab = LightUi.Button("逾期  0", 138, 118, 96, DialogResult.None), futureTab = LightUi.Button("未开始  0", 244, 118, 104, DialogResult.None), pendingTab = LightUi.Button("待办  0", 358, 118, 96, DialogResult.None), doneTab = LightUi.Button("已办  0", 464, 118, 96, DialogResult.None);
        f.Controls.AddRange(new Control[]{allTab,overdueTab,futureTab,pendingTab,doneTab});
        Panel table = new Panel { Left = 32, Top = 180, Width = 1056, Height = 470, BackColor = Color.FromArgb(247, 251, 255), AutoScroll = true };
        LightUi.EnableDoubleBuffer(table);
        LightUi.Round(table, 12); f.Controls.Add(table);
        Panel footer = new Panel { Left = 32, Top = 682, Width = 1056, Height = 54, BackColor = Color.FromArgb(245, 251, 255) };
        LightUi.EnableDoubleBuffer(footer);
        LightUi.Round(footer, 12); f.Controls.Add(footer);
        Label selectionHint = LightUi.Label("已选择 0 项", 18, 16, 240); footer.Controls.Add(selectionHint);
        Button edit = LightUi.Button("修改选中项", 604, 8, 112, DialogResult.None), toggle = LightUi.Button("批量完成", 728, 8, 112, DialogResult.None), delete = LightUi.DangerButton("删除", 852, 8, 76, DialogResult.None), add = LightUi.PrimaryButton("+ 新建待办", 940, 8, 100, DialogResult.None);
        footer.Controls.AddRange(new Control[]{edit,toggle,delete,add}); f.CancelButton = close;
        List<CheckBox> rowChecks = new List<CheckBox>();
        Dictionary<string, Panel> rowPanels = new Dictionary<string, Panel>();
        string selectedId = "";
        Action paintTabs = delegate {
            Button[] tabs = { allTab, overdueTab, futureTab, pendingTab, doneTab };
            for (int i = 0; i < tabs.Length; i++) PaintTabButton(tabs[i], filter == i);
        };
        Action paintRows = delegate {
            foreach (KeyValuePair<string, Panel> pair in rowPanels)
                pair.Value.BackColor = pair.Key == selectedId ? LightUi.Selected : Color.FromArgb(247, 251, 255);
        };
        MouseEventHandler selectRow = delegate(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            Control control = sender as Control;
            while (control != null && !(control.Tag is string)) control = control.Parent;
            if (control == null) return;
            selectedId = Convert.ToString(control.Tag);
            paintRows();
        };
        Action<bool> reload = null;
        reload = delegate(bool preserveScroll) {
            int previousScrollY = preserveScroll ? Math.Max(0, -table.AutoScrollPosition.Y) : 0;
            // Clearing rows while the panel is still scrolled leaves its negative display
            // offset in the next layout pass. Reset first, then restore the clamped offset
            // after the rebuilt controls have established the new scroll range.
            table.AutoScrollPosition = Point.Empty;
            List<Dictionary<string, object>> all = Tasks(state);
            DateTimeOffset now = DateTimeOffset.Now;
            allTab.Text = "全部  " + all.Count;
            overdueTab.Text = "逾期  " + all.Count(t => !B(t, "completed") && RuntimeUtil.Date(t, "due_at").HasValue && now > RuntimeUtil.Date(t, "due_at").Value);
            futureTab.Text = "未开始  " + all.Count(t => !B(t, "completed") && RuntimeUtil.Date(t, "available_from").HasValue && now < RuntimeUtil.Date(t, "available_from").Value);
            pendingTab.Text = "待办  " + all.Count(t => !B(t, "completed") && (!RuntimeUtil.Date(t, "due_at").HasValue || now <= RuntimeUtil.Date(t, "due_at").Value) && (!RuntimeUtil.Date(t, "available_from").HasValue || now >= RuntimeUtil.Date(t, "available_from").Value));
            doneTab.Text = "已办  " + all.Count(t => B(t, "completed"));
            string query = search.Text.Trim();
            table.SuspendLayout(); LightUi.SetRedraw(table, false); table.Controls.Clear(); rowChecks.Clear(); rowPanels.Clear();
            AddCellLabel(table, "状态", 50, 14, 70, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "标题", 128, 14, 350, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "标签", 486, 14, 140, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "开始时间", 640, 14, 132, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "截止时间", 788, 14, 132, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "操作", 936, 14, 86, LightUi.Muted, FontStyle.Bold);
            int y = 42;
            foreach (Dictionary<string, object> t in all.Where(t => TaskMatchesFilter(t, filter, now)).Where(t => !onlyOpen.Checked || !B(t, "completed")).Where(t => query == "" || S(t, "title").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || String.Join("、", Labels(t)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(t => B(t,"completed") ? 3 : (RuntimeUtil.Date(t,"due_at").HasValue && now > RuntimeUtil.Date(t,"due_at").Value ? 0 : RuntimeUtil.Date(t,"available_from").HasValue && now < RuntimeUtil.Date(t,"available_from").Value ? 1 : 2)).ThenByDescending(t => RuntimeUtil.Date(t,"created_at") ?? DateTimeOffset.MinValue)) {
                string id = S(t, "id");
                Panel row = new Panel { Left = 12, Top = y, Width = 1016, Height = 42, BackColor = Color.FromArgb(247, 251, 255), Tag = id, Cursor = Cursors.Hand };
                LightUi.EnableDoubleBuffer(row);
                LightUi.Round(row, 8);
                row.MouseDown += selectRow;
                CheckBox check = new CheckBox { Left = 6, Top = 11, Width = 20, Height = 20, BackColor = Color.Transparent, Tag = id };
                check.CheckedChanged += delegate { selectionHint.Text = "已选择 " + rowChecks.Count(c => c.Checked) + " 项"; };
                row.Controls.Add(check); rowChecks.Add(check);
                AddCellLabel(row, TaskStatusText(t, now), 36, 11, 70, TaskStatusColor(t, now), FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, S(t,"title"), 114, 11, 350, LightUi.Text, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, String.Join("  ", Labels(t)), 472, 11, 140, LightUi.Accent, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, DateEdit(RuntimeUtil.Date(t,"available_from")) == "" ? "—" : DateEdit(RuntimeUtil.Date(t,"available_from")), 626, 11, 132, LightUi.Text, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, DateEdit(RuntimeUtil.Date(t,"due_at")) == "" ? "—" : DateEdit(RuntimeUtil.Date(t,"due_at")), 774, 11, 132, LightUi.Text, FontStyle.Regular).MouseDown += selectRow;
                Button openBtn = RowIcon("\xE72A", 914, 5);
                Button editBtn = RowIcon("\xE70F", 948, 5);
                Button deleteBtn = RowIcon("\xE74D", 982, 5);
                openBtn.Click += delegate { bool changed = mutate(delegate(Dictionary<string, object> current, ref bool shouldRefresh) { Open(current, id, ref shouldRefresh); }); if (changed) reload(true); };
                editBtn.Click += delegate {
                    Dictionary<string, object> shown = Find(state, id); if (shown == null) return; EditorResult value = ShowEditor(new Dictionary<string, object>(shown)); if (value == null) return;
                    if (mutate(delegate(Dictionary<string, object> current, ref bool shouldRefresh) { Dictionary<string, object> task = Find(current, id); if (task == null) return; ApplyEditorResult(task, value); Meta(current)["status"] = "已修改待办"; Commit(current); shouldRefresh = true; })) reload(true);
                };
                deleteBtn.Click += delegate {
                    Dictionary<string, object> shown = Find(state, id); if (shown == null || !LightUi.Confirm("确定删除“" + S(shown, "title") + "”？", "删除待办")) return;
                    if (mutate(delegate(Dictionary<string, object> current, ref bool shouldRefresh) { if (Tasks(current).RemoveAll(item => S(item, "id") == id) == 0) return; Meta(current)["status"] = "已删除"; Commit(current); shouldRefresh = true; })) reload(true);
                };
                row.Controls.Add(openBtn); row.Controls.Add(editBtn); row.Controls.Add(deleteBtn);
                table.Controls.Add(row); rowPanels[id] = row; y += 42;
            }
            table.ResumeLayout();
            int maxScrollY = Math.Max(0, table.DisplayRectangle.Height - table.ClientSize.Height);
            if (previousScrollY > 0) table.AutoScrollPosition = new Point(0, Math.Min(previousScrollY, maxScrollY));
            LightUi.SetRedraw(table, true); paintTabs(); paintRows(); selectionHint.Text = "已选择 " + rowChecks.Count(c => c.Checked) + " 项";
        };
        allTab.Click += delegate { filter = 0; reload(false); };
        overdueTab.Click += delegate { filter = 1; reload(false); };
        futureTab.Click += delegate { filter = 2; reload(false); };
        pendingTab.Click += delegate { filter = 3; reload(false); };
        doneTab.Click += delegate { filter = 4; reload(false); };
        search.TextChanged += delegate { reload(false); };
        onlyOpen.CheckedChanged += delegate { reload(false); };
        search.Parent.BringToFront();
        search.BringToFront();
        close.BringToFront();
        reload(false);
        edit.Click += delegate { if (selectedId == "") { selectionHint.Text="请先选中一项需要修改的任务。"; selectionHint.ForeColor=LightUi.Danger; return; } Dictionary<string, object> shown = Find(state, selectedId); if (shown == null) { reload(true); return; } EditorResult value = ShowEditor(new Dictionary<string, object>(shown)); if (value == null) return; if (mutate(delegate(Dictionary<string, object> current, ref bool shouldRefresh) { Dictionary<string, object> task = Find(current, selectedId); if (task == null) return; ApplyEditorResult(task, value); Meta(current)["status"] = "已修改待办"; Commit(current); shouldRefresh = true; })) reload(true); selectionHint.ForeColor=LightUi.Muted; };
        toggle.Click += delegate { List<string> selected=rowChecks.Where(c=>c.Checked).Select(c=>Convert.ToString(c.Tag)).ToList(); if(selected.Count==0){selectionHint.Text="请先勾选需要完成或恢复的任务。";selectionHint.ForeColor=LightUi.Danger;return;} mutate(delegate(Dictionary<string, object> current, ref bool shouldRefresh) { foreach (string id in selected) Toggle(current, id, ref shouldRefresh); }); selectionHint.ForeColor=LightUi.Muted; reload(true); };
        delete.Click += delegate { List<string> selected=rowChecks.Where(c=>c.Checked).Select(c=>Convert.ToString(c.Tag)).ToList(); if(selected.Count==0){selectionHint.Text="请先勾选需要删除的任务。";selectionHint.ForeColor=LightUi.Danger;return;} if(!LightUi.Confirm("确定删除勾选的 "+selected.Count+" 项任务？","批量删除"))return; mutate(delegate(Dictionary<string, object> current, ref bool shouldRefresh) { int removed = Tasks(current).RemoveAll(t => selected.Contains(S(t, "id"))); if (removed == 0) return; Meta(current)["status"]="已批量删除"; Commit(current); shouldRefresh=true; }); selectionHint.ForeColor=LightUi.Muted;reload(true); };
        add.Click += delegate { EditorResult value = ShowEditor(null); if (value == null) return; if (mutate(delegate(Dictionary<string, object> current, ref bool shouldRefresh) { Tasks(current).Add(NewTask(value, "manual")); Meta(current)["status"] = "已新增待办"; Commit(current); shouldRefresh = true; })) reload(false); };
        table.DoubleClick += delegate { edit.PerformClick(); }; f.ShowDialog(); refresh |= managerChanged;
    }

    private static void ApplyEditorResult(Dictionary<string, object> task, EditorResult value)
    {
        task["title"] = value.Title;
        task["target"] = value.Target;
        task["note"] = value.Note;
        task["labels"] = value.Labels.Cast<object>().ToList();
        task["available_from"] = value.Available == "" ? null : (object)value.Available;
        task["due_at"] = value.Due == "" ? null : (object)value.Due;
    }

    private static Label AddCellLabel(Control parent, string text, int x, int y, int width, Color color, FontStyle style)
    {
        Label label = new Label { Left = x, Top = y, Width = width, Height = 22, Text = text, ForeColor = color, BackColor = Color.Transparent, AutoEllipsis = true, Font = LightUi.UiFont(9F, style) };
        parent.Controls.Add(label);
        return label;
    }

    private static Button RowIcon(string text, int x, int y)
    {
        Button button = LightUi.Button(text, x, y, 28, DialogResult.None);
        button.Height = 30;
        button.Font = LightUi.IconFont(9F);
        button.BackColor = Color.FromArgb(247, 251, 255);
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private static void PaintTabButton(Button button, bool active)
    {
        button.BackColor = active ? LightUi.Selected : LightUi.Panel;
        button.ForeColor = active ? LightUi.Accent : LightUi.Text;
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
    }

    private static string TaskStatusText(Dictionary<string, object> task, DateTimeOffset now)
    {
        bool completed = B(task, "completed");
        bool overdue = !completed && RuntimeUtil.Date(task, "due_at").HasValue && now > RuntimeUtil.Date(task, "due_at").Value;
        bool future = !completed && RuntimeUtil.Date(task, "available_from").HasValue && now < RuntimeUtil.Date(task, "available_from").Value;
        return completed ? "已办" : overdue ? "逾期" : future ? "未开始" : "待办";
    }

    private static Color TaskStatusColor(Dictionary<string, object> task, DateTimeOffset now)
    {
        string status = TaskStatusText(task, now);
        if (status == "已办") return LightUi.Done;
        if (status == "逾期") return LightUi.Danger;
        if (status == "未开始") return Color.FromArgb(145, 96, 28);
        return LightUi.Accent;
    }

}

