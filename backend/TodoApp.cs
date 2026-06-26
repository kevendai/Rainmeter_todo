using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using RainmeterBackend;

internal static class TodoApp
{
    private const string AppVersion = "1.0.2";
    private const string GitHubRepoApi = "https://api.github.com/repos/kevendai/Rainmeter_todo";
#if NO_PAPER_FEATURES
    private static readonly bool PaperFeaturesEnabled = false;
    private const string AppFlavor = "lite";
    private const string AppFlavorName = "Lite 精简版";
#else
    private static readonly bool PaperFeaturesEnabled = true;
    private const string AppFlavor = "full";
    private const string AppFlavorName = "Full 完整版";
#endif
    private static string ResourceDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static string StatePath { get { return Path.Combine(ResourceDir, "tasks.json"); } }
    private static string IncludePath { get { return Path.Combine(ResourceDir, "Generated.inc"); } }
    private static string GuardPath { get { return Path.Combine(ResourceDir, ".refresh-guard"); } }
    private static string PaperCache { get { return Path.Combine(ResourceDir, "PaperCache"); } }
    private static string PaperSyncSecret { get { return Path.Combine(ResourceDir, "paper-sync.secret"); } }
    private static string TranslationSecret { get { return Path.Combine(ResourceDir, "translation.secret"); } }

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string action = args.Length > 0 ? args[0] : "Render";
        string id = args.Length > 1 ? args[1] : "";
        bool force = args.Any(x => String.Equals(x, "Force", StringComparison.OrdinalIgnoreCase));
        if (action == "Add") return AddInteractive();
        if (action == "Edit") return EditInteractive(id);
        if (action == "Manage") return ManageInteractive();
        if (action == "Settings") return SettingsInteractive();
        using (Mutex mutex = new Mutex(false, @"Global\RainmeterTodoState"))
        {
            bool held = false;
            Dictionary<string, object> state = null;
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(15));
                if (!held) return 4;
                state = LoadState();
                int rolled = Normalize(state);
                bool refresh = rolled > 0;
                if (rolled > 0) Meta(state)["status"] = "已自动归档昨日论文" + rolled + " 篇";
                switch (action)
                {
                    case "Startup":
                        bool guarded = ConsumeGuard();
                        if (PaperFeaturesEnabled) SyncArxiv(state, false, "");
                        Save(state);
                        refresh |= Render(state) && !guarded;
                        break;
                    case "Rollover": refresh |= Render(state); break;
                    case "Render": Render(state); break;
                    case "Delete": Delete(state, id, ref refresh); break;
                    case "Toggle": Toggle(state, id, ref refresh); break;
                    case "Open": Open(state, id, ref refresh); break;
                    case "ClearArxiv":
                        if (!PaperFeaturesEnabled) { Meta(state)["status"] = "此版本未包含论文功能"; Commit(state); refresh = true; break; }
                        Tasks(state).RemoveAll(t => S(t, "source") == "arxiv");
                        Meta(state)["last_arxiv_sync_date"] = "";
                        Meta(state)["status"] = "已清除论文待办";
                        Commit(state); refresh = true; break;
                    case "SyncArxiv":
                        if (!PaperFeaturesEnabled) { Meta(state)["status"] = "此版本未包含论文功能"; Commit(state); refresh = true; break; }
                        SyncArxiv(state, force, ""); Commit(state); refresh = true; break;
                }
                if (refresh) Refresh();
                return 0;
            }
            catch (Exception ex)
            {
                if (state != null)
                {
                    Meta(state)["status"] = "操作失败：" + ex.Message;
                    try { Commit(state); Refresh(); } catch { }
                }
                return 1;
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
    }

    private static int AddInteractive()
    {
        EditorResult e = ShowEditor(null);
        if (e == null) return 0;
        return WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
            int rolled = Normalize(state);
            if (rolled > 0) Meta(state)["status"] = "已自动归档昨日论文" + rolled + " 篇";
            Tasks(state).Add(NewTask(e, "manual"));
            Meta(state)["status"] = "已新增待办";
            Commit(state);
            refresh = true;
        });
    }

    private static int EditInteractive(string id)
    {
        Dictionary<string, object> snapshot = null;
        int loaded = WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
            Dictionary<string, object> task = Find(state, id);
            if (task != null) snapshot = new Dictionary<string, object>(task);
        });
        if (loaded != 0 || snapshot == null) return loaded;
        EditorResult e = ShowEditor(snapshot);
        if (e == null) return 0;
        return WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
            Dictionary<string, object> task = Find(state, id);
            if (task == null) return;
            task["title"] = e.Title; task["target"] = e.Target; task["note"] = e.Note; task["labels"] = e.Labels.Cast<object>().ToList(); task["available_from"] = e.Available == "" ? null : (object)e.Available; task["due_at"] = e.Due == "" ? null : (object)e.Due;
            Meta(state)["status"] = "已修改待办";
            Commit(state);
            refresh = true;
        });
    }

    private static int ManageInteractive()
    {
        Dictionary<string, object> state = null;
        int loaded = WithLockedState(delegate(Dictionary<string, object> current, ref bool refresh) { state = current; });
        if (loaded != 0 || state == null) return loaded;
        bool refreshAfter = false;
        try { Manage(state, ref refreshAfter); if (refreshAfter) Refresh(); return 0; }
        catch (Exception ex)
        {
            return WithLockedState(delegate(Dictionary<string, object> current, ref bool refresh) {
                Meta(current)["status"] = "操作失败：" + ex.Message;
                Commit(current);
                refresh = true;
            });
        }
    }

    private static int SettingsInteractive()
    {
        try { ShowSettings(); return 0; }
        catch (Exception ex) { DarkUi.Error("设置失败：" + ex.Message); return 1; }
    }

    private delegate void LockedStateAction(Dictionary<string, object> state, ref bool refresh);
    private static int WithLockedState(LockedStateAction action)
    {
        using (Mutex mutex = new Mutex(false, @"Global\RainmeterTodoState"))
        {
            bool held = false;
            Dictionary<string, object> state = null;
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(15));
                if (!held) return 4;
                state = LoadState();
                bool refresh = false;
                action(state, ref refresh);
                if (refresh) Refresh();
                return 0;
            }
            catch (Exception ex)
            {
                if (state != null)
                {
                    Meta(state)["status"] = "操作失败：" + ex.Message;
                    try { Commit(state); Refresh(); } catch { }
                }
                return 1;
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
    }

    private static Dictionary<string, object> NewState()
    {
        return new Dictionary<string, object> {
            {"version", 2}, {"meta", new Dictionary<string, object>{{"last_arxiv_sync_date", ""}, {"status", "就绪"}}},
            {"tasks", new List<object>()}
        };
    }

    private static Dictionary<string, object> LoadState()
    {
        if (!File.Exists(StatePath)) return NewState();
        try
        {
            Dictionary<string, object> state = JsonUtil.LoadObject(StatePath);
            state["version"] = 2;
            if (!(JsonUtil.Get(state, "meta") is Dictionary<string, object>)) state["meta"] = new Dictionary<string, object>{{"last_arxiv_sync_date", ""}, {"status", "就绪"}};
            if (JsonUtil.Get(state, "tasks") == null) state["tasks"] = new List<object>();
            return state;
        }
        catch
        {
            File.Copy(StatePath, StatePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            Dictionary<string, object> state = NewState(); Meta(state)["status"] = "数据损坏，已备份并重建"; return state;
        }
    }

    private static Dictionary<string, object> Meta(Dictionary<string, object> state) { return JsonUtil.Object(JsonUtil.Get(state, "meta")); }
    private static List<Dictionary<string, object>> Tasks(Dictionary<string, object> state)
    {
        List<Dictionary<string, object>> result = JsonUtil.Array(JsonUtil.Get(state, "tasks")).Select(JsonUtil.Object).ToList();
        state["tasks"] = result;
        return result;
    }
    private static string S(Dictionary<string, object> value, string key) { return JsonUtil.String(value, key, ""); }
    private static bool B(Dictionary<string, object> value, string key) { return JsonUtil.Bool(value, key, false); }
    private static List<string> Labels(Dictionary<string, object> task) { return JsonUtil.Array(JsonUtil.Get(task, "labels")).Select(Convert.ToString).Where(x => !String.IsNullOrWhiteSpace(x)).Distinct().ToList(); }
    private static void SetLabels(Dictionary<string, object> task, IEnumerable<string> labels) { task["labels"] = labels.Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().Cast<object>().ToList(); }
    private static void AddLabel(Dictionary<string, object> task, string label) { List<string> labels = Labels(task); if (!labels.Contains(label)) labels.Add(label); SetLabels(task, labels); }
    private static void RemoveLabel(Dictionary<string, object> task, string label) { SetLabels(task, Labels(task).Where(x => x != label)); }

    private static int Normalize(Dictionary<string, object> state)
    {
        List<Dictionary<string, object>> tasks = Tasks(state);
        int changed = 0, rolled = 0;
        DateTimeOffset now = DateTimeOffset.Now, window = CompletionWindow(now), cutoff = now.AddDays(-30);
        foreach (Dictionary<string, object> task in tasks.ToList())
        {
            if (S(task, "source") == "arxiv" && (task.ContainsKey("translated_title") || task.ContainsKey("abstract_score") || task.ContainsKey("arxiv_id")))
            {
                string original = S(task, "title"), translated = S(task, "translated_title"), score = S(task, "abstract_score"), arxiv = S(task, "arxiv_id");
                if (arxiv == "") { Match match = Regex.Match(S(task, "target"), @"/([^/?#]+)(?:[?#].*)?$"); if (match.Success) arxiv = match.Groups[1].Value; }
                string display = translated == "" ? original : translated;
                task["title"] = score == "" ? display : "(" + score + ") " + display;
                string metadata = "论文原标题：" + original + (arxiv == "" ? "" : "\r\narXiv ID：" + arxiv), note = S(task, "note");
                task["note"] = note == "" ? metadata : note + "\r\n\r\n" + metadata;
                task.Remove("translated_title"); task.Remove("abstract_score"); task.Remove("arxiv_id"); changed++;
            }
            string labelsBefore = String.Join("|", Labels(task));
            if (S(task, "source") == "arxiv") AddLabel(task, "论文");
            if (S(task, "source") == "caldav") AddLabel(task, "日程");
            if (S(task, "source") == "arxiv" && B(task, "completed") && !Labels(task).Contains("已读") && !Labels(task).Contains("自动归档"))
            {
                DateTimeOffset? completed = RuntimeUtil.Date(task, "completed_at");
                AddLabel(task, completed.HasValue && completed.Value.Hour == 5 && completed.Value.Minute == 59 ? "自动归档" : "已读"); changed++;
            }
            if (labelsBefore != String.Join("|", Labels(task))) changed++;
            if (!B(task, "completed") && S(task, "source") == "arxiv")
            {
                DateTimeOffset? created = RuntimeUtil.Date(task, "created_at");
                if (created.HasValue && created.Value < window)
                {
                    task["completed"] = true; task["completed_at"] = RuntimeUtil.Iso(window.AddTicks(-1));
                    AddLabel(task, "论文"); AddLabel(task, "自动归档"); RemoveLabel(task, "已读"); rolled++;
                }
            }
        }
        int removed = tasks.RemoveAll(t => B(t, "completed") && RuntimeUtil.Date(t, "completed_at").HasValue && RuntimeUtil.Date(t, "completed_at").Value < cutoff);
        if (changed + rolled + removed > 0) Save(state);
        return rolled;
    }

    private static DateTimeOffset CompletionWindow(DateTimeOffset now)
    {
        DateTimeOffset boundary = new DateTimeOffset(now.Year, now.Month, now.Day, 6, 0, 0, now.Offset);
        return now < boundary ? boundary.AddDays(-1) : boundary;
    }

    private static void Save(Dictionary<string, object> state) { JsonUtil.SaveAtomic(StatePath, state); }
    private static void Commit(Dictionary<string, object> state) { Save(state); Render(state); }

    private static bool Render(Dictionary<string, object> state)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        List<Dictionary<string, object>> tasks = Tasks(state);
        List<Dictionary<string, object>> pending = tasks.Where(t => !B(t, "completed") && (!RuntimeUtil.Date(t, "available_from").HasValue || now >= RuntimeUtil.Date(t, "available_from").Value))
            .OrderBy(t => RuntimeUtil.Date(t, "due_at").HasValue ? 0 : 1)
            .ThenBy(t => RuntimeUtil.Date(t, "due_at") ?? DateTimeOffset.MaxValue)
            .ThenBy(t => RuntimeUtil.Date(t, "created_at") ?? DateTimeOffset.MinValue).ThenBy(t => S(t, "id")).ToList();
        DateTimeOffset window = CompletionWindow(now);
        List<Dictionary<string, object>> done = tasks.Where(t => B(t, "completed") && RuntimeUtil.Date(t, "completed_at").HasValue && RuntimeUtil.Date(t, "completed_at").Value >= window)
            .OrderByDescending(t => RuntimeUtil.Date(t, "completed_at").Value).ToList();
        List<string> lines = new List<string>(); int y = 86, row = 0;
        Meter(lines, "HeaderSummary", "Meter=String", "MeterStyle=StyleText", "X=23", "Y=49", "W=285", "H=16", "FontSize=9", "FontColor=#MutedColor#", "Text=" + pending.Count + " 项待办  ·  " + done.Count + " 项已办");

        int pendingHeight = pending.Count == 0 ? 54 : pending.Sum(t => String.IsNullOrEmpty(TimeLabel(t, now)) ? 44 : 56);
        Meter(lines, "PendingSurface", "Meter=Shape", "X=16", "Y=" + y, "Shape=Rectangle 0,0,488," + pendingHeight + ",14 | Fill Color 247,251,255,242 | Stroke Color 198,216,232,210 | StrokeWidth 1");
        if (pending.Count == 0)
        {
            Meter(lines, "Empty", "Meter=String", "MeterStyle=StyleText", "X=32", "Y=" + (y + 17), "W=456", "H=22", "FontColor=#MutedColor#", "Text=清单是空的。右上角可以新增任务");
            y += pendingHeight;
        }
        else
        {
            foreach (Dictionary<string, object> task in pending)
            {
                row++; string title = RuntimeUtil.CleanRainmeter(S(task, "title")); DateTimeOffset? due = RuntimeUtil.Date(task, "due_at"); bool overdue = due.HasValue && now > due.Value;
                string time = TimeLabel(task, now), color = overdue ? "#DangerColor#" : "#TextColor#", toggle = overdue ? "#DangerColor#" : "#DoneColor#";
                string tip = String.IsNullOrEmpty(time) ? title : title + " · " + time; int rowHeight = String.IsNullOrEmpty(time) ? 44 : 56;
                Meter(lines, "PendingToggle" + row, "Meter=Shape", "X=30", "Y=" + (y + 12), "W=22", "H=22", "Shape=Ellipse 10,10,6 | Fill Color 0,0,0,0 | Stroke Color " + toggle + " | StrokeWidth 1.8", Action("Toggle", S(task, "id")), "ToolTipText=标记完成");
                Meter(lines, "PendingTitle" + row, "Meter=String", "MeterStyle=StyleText", "X=60", "Y=" + (y + 9), "W=352", "H=22", "FontColor=" + color, "Text=" + title, Action("Open", S(task, "id")));
                if (!String.IsNullOrEmpty(time)) Meter(lines, "PendingTime" + row, "Meter=String", "MeterStyle=StyleText", "X=60", "Y=" + (y + 30), "W=352", "H=18", "FontSize=9", "FontColor=" + (overdue ? "#DangerColor#" : "#MutedColor#"), "Text=" + time, Action("Open", S(task, "id")));
                Meter(lines, "PendingEdit" + row, "Meter=String", "X=443", "Y=" + (y + (rowHeight / 2)), "W=34", "H=34", "FontFace=Segoe Fluent Icons", "FontSize=10", "FontColor=#SubtleColor#", "StringAlign=CenterCenter", "AntiAlias=1", "Text=\xE70F", "MouseOverAction=[!SetOption PendingEdit" + row + " FontColor \"#AccentColor#\"][!UpdateMeter PendingEdit" + row + "][!Redraw]", "MouseLeaveAction=[!SetOption PendingEdit" + row + " FontColor \"#SubtleColor#\"][!UpdateMeter PendingEdit" + row + "][!Redraw]", Action("Edit", S(task, "id")), "ToolTipText=修改");
                Meter(lines, "PendingDelete" + row, "Meter=String", "X=479", "Y=" + (y + (rowHeight / 2)), "W=34", "H=34", "FontFace=Segoe Fluent Icons", "FontSize=10", "FontColor=#SubtleColor#", "StringAlign=CenterCenter", "AntiAlias=1", "Text=\xE74D", "MouseOverAction=[!SetOption PendingDelete" + row + " FontColor \"#DangerColor#\"][!UpdateMeter PendingDelete" + row + "][!Redraw]", "MouseLeaveAction=[!SetOption PendingDelete" + row + " FontColor \"#SubtleColor#\"][!UpdateMeter PendingDelete" + row + "][!Redraw]", Action("Delete", S(task, "id")), "ToolTipText=删除");
                if (row < pending.Count) Meter(lines, "PendingDivider" + row, "Meter=Shape", "X=60", "Y=" + (y + rowHeight - 1), "Shape=Rectangle 0,0,428,1 | Fill Color 202,218,232,170 | StrokeWidth 0");
                y += rowHeight;
            }
        }

        y += 24;
        Meter(lines, "DoneHeading", "Meter=String", "MeterStyle=StyleText", "X=20", "Y=" + y, "W=470", "H=22", "FontSize=10", "FontWeight=600", "FontColor=#MutedColor#", "Text=已办  " + done.Count); y += 28; row = 0;
        List<Dictionary<string, object>> shownDone = done.Take(8).ToList();
        if (shownDone.Count > 0) Meter(lines, "DoneSurface", "Meter=Shape", "X=16", "Y=" + y, "Shape=Rectangle 0,0,488," + (shownDone.Count * 42) + ",14 | Fill Color 239,249,244,238 | Stroke Color 198,216,232,190 | StrokeWidth 1");
        foreach (Dictionary<string, object> task in shownDone)
        {
            row++; string title = RuntimeUtil.CleanRainmeter(S(task, "title"));
            Meter(lines, "DoneToggle" + row, "Meter=String", "X=42", "Y=" + (y + 21), "W=30", "H=34", "FontFace=Segoe Fluent Icons", "FontSize=10", "FontColor=#DoneColor#", "StringAlign=CenterCenter", "AntiAlias=1", "Text=\xE72C", Action("Toggle", S(task, "id")), "ToolTipText=恢复到待办");
            Meter(lines, "DoneTitle" + row, "Meter=String", "MeterStyle=StyleText", "X=60", "Y=" + (y + 10), "W=390", "H=22", "FontColor=#MutedColor#", "Text=" + title, Action("Open", S(task, "id")));
            Meter(lines, "DoneDelete" + row, "Meter=String", "X=479", "Y=" + (y + 21), "W=34", "H=34", "FontFace=Segoe Fluent Icons", "FontSize=9", "FontColor=#SubtleColor#", "StringAlign=CenterCenter", "AntiAlias=1", "Text=\xE74D", "MouseOverAction=[!SetOption DoneDelete" + row + " FontColor \"#DangerColor#\"][!UpdateMeter DoneDelete" + row + "][!Redraw]", "MouseLeaveAction=[!SetOption DoneDelete" + row + " FontColor \"#SubtleColor#\"][!UpdateMeter DoneDelete" + row + "][!Redraw]", Action("Delete", S(task, "id")), "ToolTipText=删除");
            if (row < shownDone.Count) Meter(lines, "DoneDivider" + row, "Meter=Shape", "X=60", "Y=" + (y + 41), "Shape=Rectangle 0,0,428,1 | Fill Color 202,218,232,145 | StrokeWidth 0");
            y += 42;
        }
        if (done.Count > 8) { Meter(lines, "DoneMore", "Meter=String", "MeterStyle=StyleText", "X=60", "Y=" + (y + 7), "W=420", "H=20", "FontSize=9", "FontColor=#MutedColor#", "Text=另有 " + (done.Count - 8) + " 项未展开"); y += 30; }
        string status = RuntimeUtil.CleanRainmeter(JsonUtil.String(Meta(state), "status", "就绪")); y += 18;
        Meter(lines, "FooterRule", "Meter=Shape", "X=22", "Y=" + y, "Shape=Rectangle 0,0,476,1 | Fill Color #BorderColor# | StrokeWidth 0");
        Meter(lines, "Status", "Meter=String", "MeterStyle=StyleText", "X=23", "Y=" + (y + 13), "W=470", "H=18", "FontSize=9", "FontColor=#MutedColor#", "Text=" + status, "ToolTipText=" + status); y += 42;
        Meter(lines, "BottomSpacer", "Meter=Shape", "X=0", "Y=" + y, "Shape=Rectangle 0,0,520,1 | Fill Color 0,0,0,0 | StrokeWidth 0");

        List<string> output = new List<string>();
        Meter(output, "Panel", "Meter=Shape", "X=0", "Y=0", "Shape=Rectangle 1,1,518," + (y - 1) + ",18 | Fill Color 239,248,255,248 | Stroke Color 198,216,232,210 | StrokeWidth 1");
        Meter(output, "PanelHighlight", "Meter=Shape", "X=18", "Y=1", "Shape=Rectangle 0,0,482,1 | Fill Color 255,255,255,180 | StrokeWidth 0");
        output.AddRange(lines);
        return RuntimeUtil.WriteUtf16IfChanged(IncludePath, String.Join("\r\n", output) + "\r\n");
    }

    private static void Meter(List<string> lines, string name, params string[] body) { lines.Add("[" + name + "]"); lines.AddRange(body); lines.Add(""); }
    private static string Action(string action, string id) { return "LeftMouseUpAction=[\"#@#TodoHost.exe\" \"" + action + "\" \"" + id + "\"]"; }
    private static string TimeLabel(Dictionary<string, object> task, DateTimeOffset now)
    {
        DateTimeOffset? due = RuntimeUtil.Date(task, "due_at"), available = RuntimeUtil.Date(task, "available_from");
        if (due.HasValue && now > due.Value) return "逾期 · 截止于" + due.Value.ToString("M月d日 HH:mm");
        if (due.HasValue) return (due.Value.Date == now.Date ? "今天" : due.Value.Date == now.Date.AddDays(1) ? "明天" : due.Value.ToString("M月d日")) + " " + due.Value.ToString("HH:mm") + " 截止";
        return available.HasValue ? available.Value.ToString("M月d日 HH:mm") + " 开始" : "";
    }

    private sealed class EditorResult { public string Title, Target, Note, Available, Due; public List<string> Labels; }
    private static EditorResult ShowEditor(Dictionary<string, object> task)
    {
        bool editing = task != null;
        Form f = DarkUi.Form(editing ? "修改待办" : "新增待办", 560, 840); int x = 28, w = 504;
        DarkUi.Heading(f, editing ? "修改待办" : "新增待办", editing ? "调整待办事项，明确目标，高效执行" : "创建一项新的待办事项，明确目标，高效执行");
        Button close = DarkUi.Button("×", 500, 22, 34, DialogResult.Cancel); close.Height = 34; f.Controls.Add(close);

        TextBox title = Field(f, "标题 *", x, 112, w, editing ? S(task, "title") : "");
        TextBox target = FieldWithButton(f, "打开目标", x, 204, w, editing ? S(task, "target") : "", "浏览");
        TextBox available = DateField(f, "开始时间", x, 304, 230, RuntimeUtil.Date(task, "available_from"));
        TextBox due = DateField(f, "截止时间", 302, 304, 230, RuntimeUtil.Date(task, "due_at"));

        HashSet<string> selectedLabels = new HashSet<string>(editing ? Labels(task) : Enumerable.Empty<string>());
        Panel labelPanel = LabelSelector("标签", x, 400, w, CommonLabels(task), selectedLabels);
        f.Controls.Add(labelPanel);

        f.Controls.Add(DarkUi.Label("备注", x, 500, w));
        Panel noteSurface = new Panel { Left = x, Top = 522, Width = w, Height = 144, BackColor = DarkUi.Panel };
        DarkUi.Round(noteSurface, 10);
        TextBox note = new TextBox { Left = 14, Top = 14, Width = w - 28, Height = 116, Text = editing ? S(task, "note") : "", Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, BorderStyle = BorderStyle.None, BackColor = DarkUi.Panel, ForeColor = DarkUi.Text, Font = new Font("Microsoft YaHei UI", 10F) };
        noteSurface.Controls.Add(note); f.Controls.Add(noteSurface);
        labelPanel.BringToFront();
        Label hint = DarkUi.Label("标题为必填项。截止时间不能早于开始时间。", x, 682, 340); f.Controls.Add(hint);
        Button cancel = DarkUi.Button("取消", 210, 722, 112, DialogResult.Cancel), save = DarkUi.PrimaryButton(editing ? "+ 保存修改" : "+ 添加待办", 338, 722, 194, DialogResult.OK);
        f.Controls.Add(cancel); f.Controls.Add(save); f.AcceptButton = save; f.CancelButton = cancel;
        while (f.ShowDialog() == DialogResult.OK)
        {
            if (String.IsNullOrWhiteSpace(title.Text)) { DarkUi.Error("标题不能为空"); continue; }
            DateTimeOffset a = default(DateTimeOffset), d = default(DateTimeOffset); string av = "", du = "";
            if (!String.IsNullOrWhiteSpace(available.Text) && !TryEditorDate(available.Text, out a)) { DarkUi.Error("开始时间格式应为 YYYY-MM-DD HH:mm"); continue; } else if (!String.IsNullOrWhiteSpace(available.Text)) av = RuntimeUtil.Iso(a);
            if (!String.IsNullOrWhiteSpace(due.Text) && !TryEditorDate(due.Text, out d)) { DarkUi.Error("截止时间格式应为 YYYY-MM-DD HH:mm"); continue; } else if (!String.IsNullOrWhiteSpace(due.Text)) du = RuntimeUtil.Iso(d);
            if (av != "" && du != "" && d < a) { DarkUi.Error("截止时间不能早于开始时间"); continue; }
            return new EditorResult { Title = title.Text.Trim(), Target = target.Text.Trim(), Note = note.Text, Available = av, Due = du, Labels = selectedLabels.Where(v => v != "").Distinct().ToList() };
        }
        return null;
    }

    private static void ShowSettings()
    {
        Dictionary<string, object> credentials = ReadTranslationCredentials();
        Dictionary<string, object> paperSync = ReadPaperSyncSettings();
        Form f = DarkUi.Form(PaperFeaturesEnabled ? "论文设置" : "关于", 620, 560);
        DarkUi.Heading(f, PaperFeaturesEnabled ? "论文设置" : "关于", PaperFeaturesEnabled ? "分别配置论文网页同步、arXiv 标题翻译和版本更新。" : "查看当前版本并检查可用更新。");
        Button close = DarkUi.Button("×", 560, 22, 34, DialogResult.Cancel);
        close.Height = 34;
        f.Controls.Add(close);

        int x = 28, w = 564;
        int tabCount = PaperFeaturesEnabled ? 3 : 1;
        Panel tabRail = new Panel { Left = x, Top = 98, Width = 140 * tabCount, Height = 42, BackColor = Color.FromArgb(235, 245, 253) };
        DarkUi.Round(tabRail, 11);
        Button tabPaper = DarkUi.Button("论文同步", 0, 0, 140, DialogResult.None);
        Button tabTranslation = DarkUi.Button("标题翻译", 140, 0, 140, DialogResult.None);
        Button tabAbout = DarkUi.Button("关于", PaperFeaturesEnabled ? 280 : 0, 0, 140, DialogResult.None);
        tabPaper.Height = tabTranslation.Height = tabAbout.Height = 42;
        tabPaper.Font = tabTranslation.Font = tabAbout.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        if (PaperFeaturesEnabled) tabRail.Controls.AddRange(new Control[] { tabPaper, tabTranslation, tabAbout });
        else tabRail.Controls.Add(tabAbout);
        f.Controls.Add(tabRail);

        Panel paperPage = new Panel { Left = x, Top = 154, Width = w, Height = 280, BackColor = Color.Transparent };
        Panel translationPage = new Panel { Left = x, Top = 154, Width = w, Height = 280, BackColor = Color.Transparent, Visible = false };
        Panel aboutPage = new Panel { Left = x, Top = 154, Width = w, Height = 280, BackColor = Color.Transparent, Visible = false };
        f.Controls.AddRange(new Control[] { paperPage, translationPage, aboutPage });

        TextBox paperBaseUrl = Field(paperPage, "论文网页同步地址", 0, 0, w, S(paperSync, "BaseUrl"));
        TextBox paperAccount = Field(paperPage, "论文网页同步账号", 0, 94, w, S(paperSync, "Account"));
        TextBox paperPassword = PasswordField(paperPage, "论文网页同步密码", 0, 188, w, S(paperSync, "Password"));
        Label paperHint = DarkUi.Label("论文网页同步配置会保存到 paper-sync.secret，并使用 Windows DPAPI CurrentUser 加密。", x, 448, w);
        Label paperStatus = DarkUi.Label(File.Exists(PaperSyncSecret) ? "已保存论文网页同步配置" : "尚未配置论文网页同步", x, 476, 250);
        paperStatus.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        paperStatus.ForeColor = File.Exists(PaperSyncSecret) ? Color.FromArgb(63, 178, 119) : DarkUi.Muted;

        Panel paperActions = new Panel { Left = 270, Top = 474, Width = 322, Height = 42, BackColor = Color.Transparent };
        Button paperClear = DarkUi.DangerButton("清除同步", 0, 0, 94, DialogResult.None);
        Button paperTest = DarkUi.Button("测试登录", 106, 0, 94, DialogResult.None);
        Button paperSave = DarkUi.PrimaryButton("保存同步", 212, 0, 110, DialogResult.None);
        paperClear.Height = paperTest.Height = paperSave.Height = 38;
        paperClear.Font = paperTest.Font = paperSave.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        paperActions.Controls.AddRange(new Control[] { paperClear, paperTest, paperSave });

        TextBox secretId = Field(translationPage, "Tencent Cloud SecretId", 0, 0, w, S(credentials, "SecretId"));
        TextBox secretKey = PasswordField(translationPage, "Tencent Cloud SecretKey", 0, 94, w, S(credentials, "SecretKey"));
        Label hint = DarkUi.Label("翻译凭据会保存到 translation.secret，并使用 Windows DPAPI CurrentUser 加密。", x, 448, w);
        Label status = DarkUi.Label(File.Exists(TranslationSecret) ? "已保存翻译凭据" : "尚未配置翻译凭据", x, 476, 250);
        status.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        status.ForeColor = File.Exists(TranslationSecret) ? Color.FromArgb(63, 178, 119) : DarkUi.Muted;

        Panel translationActions = new Panel { Left = 270, Top = 474, Width = 322, Height = 42, BackColor = Color.Transparent, Visible = false };
        Button clear = DarkUi.DangerButton("清除设置", 0, 0, 94, DialogResult.None);
        Button test = DarkUi.Button("测试连接", 106, 0, 94, DialogResult.None);
        Button save = DarkUi.PrimaryButton("保存凭据", 212, 0, 110, DialogResult.None);
        clear.Height = test.Height = save.Height = 38;
        clear.Font = test.Font = save.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        translationActions.Controls.AddRange(new Control[] { clear, test, save });
        f.Controls.AddRange(new Control[] { paperHint, paperStatus, paperActions, hint, status, translationActions });

        Label aboutTitle = new Label { Text = "Rainmeter Desktop Widgets", Left = 0, Top = 4, Width = w, Height = 32, ForeColor = DarkUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold) };
        Label aboutVersion = new Label { Text = "当前版本：" + AppVersion + "（" + AppFlavorName + "）", Left = 0, Top = 54, Width = w, Height = 24, ForeColor = DarkUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
        Label aboutRepo = DarkUi.Label("更新源：github.com/kevendai/Rainmeter_todo", 0, 88, w);
        Label aboutHint = DarkUi.Label("检查更新会下载与当前版本类型相同的 zip 包。", 0, 120, w);
        aboutPage.Controls.AddRange(new Control[] { aboutTitle, aboutVersion, aboutRepo, aboutHint });

        Label updateStatus = DarkUi.Label("尚未检查更新", x, 476, 300);
        updateStatus.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        updateStatus.ForeColor = DarkUi.Muted;
        Panel aboutActions = new Panel { Left = 412, Top = 474, Width = 180, Height = 42, BackColor = Color.Transparent, Visible = false };
        Button checkUpdate = DarkUi.PrimaryButton("检查更新", 70, 0, 110, DialogResult.None);
        checkUpdate.Height = 38;
        checkUpdate.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        aboutActions.Controls.Add(checkUpdate);
        f.Controls.AddRange(new Control[] { updateStatus, aboutActions });

        Action<int> showPage = delegate(int page) {
            bool paper = page == 0 && PaperFeaturesEnabled;
            bool translation = page == 1 && PaperFeaturesEnabled;
            bool about = page == 2 || !PaperFeaturesEnabled;
            paperPage.Visible = paper;
            translationPage.Visible = translation;
            aboutPage.Visible = about;
            paperHint.Visible = paper;
            paperStatus.Visible = paper;
            paperActions.Visible = paper;
            hint.Visible = translation;
            status.Visible = translation;
            translationActions.Visible = translation;
            updateStatus.Visible = about;
            aboutActions.Visible = about;
            PaintTabButton(tabPaper, paper);
            PaintTabButton(tabTranslation, translation);
            PaintTabButton(tabAbout, about);
        };
        tabPaper.Click += delegate { showPage(0); };
        tabTranslation.Click += delegate { showPage(1); };
        tabAbout.Click += delegate { showPage(2); };
        showPage(PaperFeaturesEnabled ? 0 : 2);

        paperTest.Click += delegate {
            try
            {
                paperStatus.Text = "正在测试...";
                paperStatus.ForeColor = DarkUi.Muted;
                paperStatus.Refresh();
                TestPaperSyncConnection(paperBaseUrl.Text, paperAccount.Text, paperPassword.Text);
                paperStatus.Text = "连接成功";
                paperStatus.ForeColor = Color.FromArgb(63, 178, 119);
            }
            catch (Exception ex)
            {
                DarkUi.Error("连接失败：" + ex.Message);
                paperStatus.Text = "连接失败";
                paperStatus.ForeColor = DarkUi.Danger;
            }
        };
        paperSave.Click += delegate {
            try
            {
                SavePaperSyncSettings(paperBaseUrl.Text, paperAccount.Text, paperPassword.Text);
                paperStatus.Text = "已保存论文网页同步配置";
                paperStatus.ForeColor = Color.FromArgb(63, 178, 119);
            }
            catch (Exception ex) { DarkUi.Error(ex.Message); }
        };
        paperClear.Click += delegate {
            try
            {
                if (File.Exists(PaperSyncSecret)) File.Delete(PaperSyncSecret);
                paperBaseUrl.Text = "";
                paperAccount.Text = "";
                paperPassword.Text = "";
                paperStatus.Text = "尚未配置论文网页同步";
                paperStatus.ForeColor = DarkUi.Muted;
            }
            catch (Exception ex) { DarkUi.Error(ex.Message); }
        };

        test.Click += delegate {
            try
            {
                status.Text = "正在测试...";
                status.ForeColor = DarkUi.Muted;
                status.Refresh();
                string result = TestTranslationCredentials(secretId.Text, secretKey.Text);
                status.Text = "连接成功：" + result;
                status.ForeColor = Color.FromArgb(63, 178, 119);
            }
            catch (Exception ex)
            {
                DarkUi.Error("连接失败：" + ex.Message);
                status.Text = "连接失败";
                status.ForeColor = DarkUi.Danger;
            }
        };
        save.Click += delegate {
            try
            {
                SaveTranslationCredentials(secretId.Text, secretKey.Text);
                status.Text = "已保存翻译凭据";
                status.ForeColor = Color.FromArgb(63, 178, 119);
            }
            catch (Exception ex) { DarkUi.Error(ex.Message); }
        };
        clear.Click += delegate {
            try
            {
                if (File.Exists(TranslationSecret)) File.Delete(TranslationSecret);
                secretId.Text = "";
                secretKey.Text = "";
                status.Text = "尚未配置翻译凭据";
                status.ForeColor = DarkUi.Muted;
            }
            catch (Exception ex) { DarkUi.Error(ex.Message); }
        };

        checkUpdate.Click += delegate {
            try
            {
                checkUpdate.Enabled = false;
                updateStatus.Text = "正在检查 GitHub...";
                updateStatus.ForeColor = DarkUi.Muted;
                updateStatus.Refresh();
                Application.DoEvents();
                UpdateInfo info = CheckUpdate();
                if (info.IsNewer)
                {
                    updateStatus.Text = "检测到新版本：" + info.Tag;
                    updateStatus.ForeColor = DarkUi.Accent;
                    DialogResult update = MessageBox.Show(
                        "检测到新版本 " + info.Tag + "（" + AppFlavorName + "）。\r\n\r\n是否现在下载并自动部署？部署脚本会重启 Rainmeter。",
                        "检查更新",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (update != DialogResult.Yes)
                    {
                        updateStatus.Text = "已取消更新：" + info.Tag;
                        updateStatus.ForeColor = DarkUi.Muted;
                        return;
                    }
                    updateStatus.Text = "正在下载 " + info.Tag + "...";
                    updateStatus.ForeColor = DarkUi.Muted;
                    updateStatus.Refresh();
                    Application.DoEvents();
                    DownloadAndStartUpdate(info);
                    updateStatus.Text = "已下载并开始部署 " + info.Tag + "（" + AppFlavor + "）";
                    updateStatus.ForeColor = Color.FromArgb(63, 178, 119);
                    MessageBox.Show("已下载最新安装包并开始自动部署。\r\n部署脚本会重启 Rainmeter。\r\n\r\n" + info.DownloadPath, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    f.BeginInvoke(new Action(f.Close));
                }
                else
                {
                    updateStatus.Text = CompareVersions(NormalizeVersion(info.Tag), AppVersion) == 0 ? "已是最新版本：" + info.Tag : "当前版本高于最新标签：" + info.Tag;
                    updateStatus.ForeColor = Color.FromArgb(63, 178, 119);
                }
            }
            catch (Exception ex)
            {
                updateStatus.Text = "检查更新失败";
                updateStatus.ForeColor = DarkUi.Danger;
                DarkUi.Error("检查更新失败：" + ex.Message);
            }
            finally { checkUpdate.Enabled = true; }
        };

        f.CancelButton = close;
        f.ShowDialog();
    }

    private sealed class UpdateInfo
    {
        public string Tag;
        public string AssetName;
        public string AssetUrl;
        public string DownloadPath;
        public bool IsNewer;
    }

    private static UpdateInfo CheckUpdate()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        string tagsRaw = GitHubGet(GitHubRepoApi + "/tags");
        string tag = LatestTag(tagsRaw);
        if (tag == "") throw new Exception("GitHub 上没有可用版本标签");
        string latestVersion = NormalizeVersion(tag);
        bool newer = CompareVersions(latestVersion, AppVersion) > 0;
        UpdateInfo info = new UpdateInfo { Tag = tag, IsNewer = newer };
        if (!newer) return info;

        string raw = GitHubGet(GitHubRepoApi + "/releases/tags/" + Uri.EscapeDataString(tag));
        Dictionary<string, object> release = JsonUtil.Object(JsonUtil.Deserialize(raw));
        string expectedPrefix = "rainmeter-desktop-widgets-" + AppFlavor + "-";
        foreach (object item in JsonUtil.Array(JsonUtil.Get(release, "assets")))
        {
            Dictionary<string, object> asset = JsonUtil.Object(item);
            string name = S(asset, "name");
            string url = S(asset, "browser_download_url");
            if (name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && url != "")
            {
                info.AssetName = name;
                info.AssetUrl = url;
                break;
            }
        }
        if (String.IsNullOrEmpty(info.AssetUrl)) throw new Exception("最新 release 中没有 " + AppFlavor + " 版本的 zip 包");

        return info;
    }

    private static void DownloadAndStartUpdate(UpdateInfo info)
    {
        if (info == null || String.IsNullOrEmpty(info.AssetUrl) || String.IsNullOrEmpty(info.AssetName)) throw new Exception("更新包信息不完整");
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        string target = Path.Combine(downloads, info.AssetName);
        using (WebClient client = new WebClient())
        {
            client.Headers[HttpRequestHeader.UserAgent] = "RainmeterDesktopWidgets/" + AppVersion;
            client.DownloadFile(info.AssetUrl, target);
        }
        info.DownloadPath = target;
        StartUpdateInstaller(target);
    }

    private static void StartUpdateInstaller(string zipPath)
    {
        string rainmeterRoot = CurrentRainmeterRoot();
        string script = Path.Combine(Path.GetTempPath(), "RainmeterDesktopWidgetsUpdate-" + Guid.NewGuid().ToString("N") + ".ps1");
        string content =
@"param(
    [string]$ZipPath,
    [string]$RainmeterRoot,
    [int]$WaitForProcessId
)
$ErrorActionPreference = 'Stop'
$extractRoot = Join-Path $env:TEMP ('RainmeterDesktopWidgetsUpdate-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
Expand-Archive -LiteralPath $ZipPath -DestinationPath $extractRoot -Force
$installer = Get-ChildItem -LiteralPath $extractRoot -Recurse -Filter 'Install-Skins.ps1' -File | Select-Object -First 1
if ($null -eq $installer) { throw 'Install-Skins.ps1 not found in update package.' }
if ($WaitForProcessId -gt 0) {
    try { Wait-Process -Id $WaitForProcessId -Timeout 30 -ErrorAction SilentlyContinue } catch {}
}
& powershell -ExecutionPolicy Bypass -File $installer.FullName -RainmeterRoot $RainmeterRoot -Activate
";
        File.WriteAllText(script, content, new UTF8Encoding(false));
        string arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArg(script)
            + " -ZipPath " + QuoteArg(zipPath)
            + " -RainmeterRoot " + QuoteArg(rainmeterRoot)
            + " -WaitForProcessId " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        Process.Start(new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = false, CreateNoWindow = false });
    }

    private static string CurrentRainmeterRoot()
    {
        DirectoryInfo resources = new DirectoryInfo(ResourceDir);
        DirectoryInfo todo = resources.Parent;
        DirectoryInfo skins = todo == null ? null : todo.Parent;
        DirectoryInfo root = skins == null ? null : skins.Parent;
        if (root == null || !File.Exists(Path.Combine(root.FullName, "Rainmeter.exe"))) throw new Exception("无法定位当前 Rainmeter 安装目录");
        return root.FullName;
    }

    private static string QuoteArg(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }

    private static string LatestTag(string raw)
    {
        string best = "";
        foreach (object item in JsonUtil.Array(JsonUtil.Deserialize(raw)))
        {
            Dictionary<string, object> tag = JsonUtil.Object(item);
            string name = S(tag, "name");
            if (!Regex.IsMatch(NormalizeVersion(name), @"^\d")) continue;
            if (best == "" || CompareVersions(name, best) > 0) best = name;
        }
        return best;
    }

    private static string GitHubGet(string url)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Timeout = 10000;
        request.ReadWriteTimeout = 10000;
        request.UserAgent = "RainmeterDesktopWidgets/" + AppVersion;
        request.Accept = "application/vnd.github+json";
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static string NormalizeVersion(string value)
    {
        value = (value ?? "").Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
        Match match = Regex.Match(value, @"\d+(?:\.\d+){0,3}");
        return match.Success ? match.Value : value;
    }

    private static int CompareVersions(string left, string right)
    {
        int[] a = VersionParts(left), b = VersionParts(right);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int av = i < a.Length ? a[i] : 0, bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    private static int[] VersionParts(string value)
    {
        return NormalizeVersion(value).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => { int parsed; return Int32.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0; })
            .ToArray();
    }

    private static IEnumerable<string> CommonLabels(Dictionary<string, object> task)
    {
        string[] defaults = PaperFeaturesEnabled
            ? new[] { "论文", "考试", "功能", "修复", "日程", "已读", "自动归档" }
            : new[] { "考试", "功能", "修复", "日程" };
        return defaults.Concat(task == null ? Enumerable.Empty<string>() : Labels(task)).Where(x => !String.IsNullOrWhiteSpace(x)).Distinct();
    }

    private static Panel LabelSelector(string title, int x, int y, int width, IEnumerable<string> options, HashSet<string> selected)
    {
        Panel panel = new Panel { Left = x, Top = y, Width = width, Height = 86, BackColor = Color.Transparent };
        panel.Controls.Add(DarkUi.Label(title, 0, 0, width));
        Panel surface = new Panel { Left = 0, Top = 28, Width = width, Height = 56, BackColor = DarkUi.Panel };
        DarkUi.Round(surface, 10);
        panel.Controls.Add(surface);
        Button expand = DarkUi.Button("展开", width - 66, 12, 52, DialogResult.None);
        expand.Height = 30;
        expand.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        expand.UseVisualStyleBackColor = false;
        expand.BackColor = DarkUi.AccentFill;
        expand.ForeColor = Color.White;
        expand.FlatAppearance.BorderColor = Color.FromArgb(31, 103, 201);
        expand.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 116, 224);
        expand.FlatAppearance.MouseDownBackColor = Color.FromArgb(22, 88, 176);
        expand.MouseEnter += delegate { if (expand.Enabled) expand.BackColor = Color.FromArgb(31, 116, 224); };
        expand.MouseLeave += delegate { expand.BackColor = DarkUi.AccentFill; };
        surface.Controls.Add(expand);
        int left = 12, top = 13;
        foreach (string label in options)
        {
            int buttonWidth = Math.Max(58, Math.Min(104, TextRenderer.MeasureText(label, new Font("Microsoft YaHei UI", 9F)).Width + 28));
            if (left + buttonWidth > width - 76) { left = 12; top += 32; }
            Button button = DarkUi.Button(label, left, top, buttonWidth, DialogResult.None);
            button.Height = 28;
            button.Tag = label;
            button.Visible = top == 13;
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
        bool expanded = false;
        expand.Click += delegate {
            expanded = !expanded;
            surface.Height = expanded ? 94 : 56;
            panel.Height = expanded ? 126 : 86;
            expand.Text = expanded ? "收起" : "展开";
            expand.BackColor = DarkUi.AccentFill;
            expand.ForeColor = Color.White;
            if (expanded) panel.BringToFront();
            foreach (Control control in surface.Controls)
            {
                Button chip = control as Button;
                if (chip != null && chip != expand) chip.Visible = expanded || chip.Top == 13;
            }
        };
        return panel;
    }

    private static void PaintLabelChoice(Button button, bool active)
    {
        button.BackColor = active ? Color.FromArgb(220, 238, 255) : DarkUi.Panel;
        button.ForeColor = active ? DarkUi.Accent : DarkUi.Text;
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
    }

    private static TextBox Field(Control f, string label, int x, int y, int width, string text)
    {
        f.Controls.Add(DarkUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = DarkUi.Panel };
        DarkUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 28, Height = 24, AutoSize = false, Text = text ?? "", BackColor = DarkUi.Panel, ForeColor = DarkUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
        surface.Controls.Add(box);
        f.Controls.Add(surface);
        return box;
    }

    private static TextBox PasswordField(Control f, string label, int x, int y, int width, string text)
    {
        f.Controls.Add(DarkUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = DarkUi.Panel };
        DarkUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 92, Height = 24, AutoSize = false, Text = text ?? "", UseSystemPasswordChar = true, BackColor = DarkUi.Panel, ForeColor = DarkUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
        Button reveal = DarkUi.Button("显示", width - 70, 8, 56, DialogResult.None);
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
        f.Controls.Add(DarkUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = DarkUi.Panel };
        DarkUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 98, Height = 24, AutoSize = false, Text = text ?? "", BackColor = DarkUi.Panel, ForeColor = DarkUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
        Button browse = DarkUi.Button(buttonText, width - 78, 8, 64, DialogResult.None);
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
        Panel surface = new Panel { Left = x, Top = y, Width = width, Height = 42, BackColor = DarkUi.Panel };
        DarkUi.Round(surface, 10);
        Label icon = new Label { Left = 12, Top = 11, Width = 22, Height = 22, Text = "\xE721", Font = new Font("Segoe Fluent Icons", 9F), ForeColor = DarkUi.Muted, BackColor = Color.Transparent };
        TextBox box = new TextBox { Left = 38, Top = 12, Width = width - 50, Height = 22, AutoSize = false, Text = "", BackColor = DarkUi.Panel, ForeColor = DarkUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 9F) };
        surface.Controls.Add(icon); surface.Controls.Add(box); f.Controls.Add(surface);
        return box;
    }

    private static TextBox DateField(Form f, string label, int x, int y, int width, DateTimeOffset? value)
    {
        f.Controls.Add(DarkUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = DarkUi.Panel };
        DarkUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 58, Height = 24, AutoSize = false, Text = DateEdit(value), ReadOnly = true, BackColor = DarkUi.Panel, ForeColor = DarkUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
        Button choose = DarkUi.Button("\xE787", width - 42, 8, 30, DialogResult.None);
        choose.Height = 34;
        choose.Font = new Font("Segoe Fluent Icons", 9F);
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
        Form dialog = DarkUi.Form("选择时间", 360, 210);
        DarkUi.Heading(dialog, "选择时间", "选择日期和时间；清空表示不限制。");
        DateTimePicker picker = new DateTimePicker { Left = 26, Top = 92, Width = 308, Height = 32, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Value = initial, Font = new Font("Microsoft YaHei UI", 10F) };
        Button clear = DarkUi.Button("清空", 82, 150, 76, DialogResult.Retry);
        Button cancel = DarkUi.Button("取消", 168, 150, 76, DialogResult.Cancel);
        Button ok = DarkUi.PrimaryButton("确定", 254, 150, 80, DialogResult.OK);
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

    private static void Add(Dictionary<string, object> state, ref bool refresh)
    {
        EditorResult e = ShowEditor(null); if (e == null) return;
        Tasks(state).Add(NewTask(e, "manual")); Meta(state)["status"] = "已新增待办"; Commit(state); refresh = true;
    }
    private static Dictionary<string, object> NewTask(EditorResult e, string source)
    {
        return new Dictionary<string, object>{{"id", Guid.NewGuid().ToString("N")}, {"title", e.Title}, {"target", e.Target}, {"note", e.Note}, {"labels", e.Labels.Cast<object>().ToList()}, {"completed", false}, {"source", source}, {"created_at", RuntimeUtil.Iso(DateTimeOffset.Now)}, {"completed_at", null}, {"available_from", String.IsNullOrEmpty(e.Available) ? null : (object)e.Available}, {"due_at", String.IsNullOrEmpty(e.Due) ? null : (object)e.Due}};
    }
    private static Dictionary<string, object> Find(Dictionary<string, object> state, string id) { return Tasks(state).FirstOrDefault(t => S(t, "id") == id); }
    private static void Edit(Dictionary<string, object> state, string id, ref bool refresh)
    {
        Dictionary<string, object> task = Find(state, id); if (task == null) return; EditorResult e = ShowEditor(task); if (e == null) return;
        task["title"] = e.Title; task["target"] = e.Target; task["note"] = e.Note; task["labels"] = e.Labels.Cast<object>().ToList(); task["available_from"] = e.Available == "" ? null : (object)e.Available; task["due_at"] = e.Due == "" ? null : (object)e.Due;
        Meta(state)["status"] = "已修改待办"; Commit(state); refresh = true;
    }
    private static void Delete(Dictionary<string, object> state, string id, ref bool refresh)
    {
        Dictionary<string, object> task = Find(state, id); if (task == null || !DarkUi.Confirm("确定删除“" + S(task, "title") + "”？", "删除待办")) return;
        Tasks(state).RemoveAll(t => S(t, "id") == id); Meta(state)["status"] = "已删除"; Commit(state); refresh = true;
    }
    private static void Toggle(Dictionary<string, object> state, string id, ref bool refresh)
    {
        Dictionary<string, object> task = Find(state, id); if (task == null) return;
        if (B(task, "completed")) { task["completed"] = false; task["completed_at"] = null; if (S(task, "source") == "arxiv") { task["created_at"] = RuntimeUtil.Iso(DateTimeOffset.Now); RemoveLabel(task, "已读"); RemoveLabel(task, "自动归档"); } Meta(state)["status"] = "已恢复到待办"; }
        else { task["completed"] = true; task["completed_at"] = RuntimeUtil.Iso(DateTimeOffset.Now); if (Labels(task).Contains("论文")) { AddLabel(task, "已读"); RemoveLabel(task, "自动归档"); } Meta(state)["status"] = "已完成"; }
        Commit(state); refresh = true;
    }
    private static void Open(Dictionary<string, object> state, string id, ref bool refresh)
    {
        Dictionary<string, object> task = Find(state, id); if (task == null) return; string target = Environment.ExpandEnvironmentVariables(S(task, "target").Trim().Trim('"'));
        if (target.StartsWith("http://") || target.StartsWith("https://") || target.StartsWith("wemeet://") || File.Exists(target) || Directory.Exists(target)) RuntimeUtil.Run(target);
        else if (target != "") { Meta(state)["status"] = "找不到：" + Path.GetFileName(target); Commit(state); refresh = true; }
    }

    private static void Manage(Dictionary<string, object> state, ref bool refresh)
    {
        bool managerChanged = false;
        Form f = DarkUi.Form("全部任务", 1120, 760); DarkUi.Heading(f, "全部任务", "管理你的所有待办事项，支持批量操作");
        Button close = DarkUi.Button("×", 1054, 22, 36, DialogResult.Cancel); close.Height = 34; f.Controls.Add(close);
        TextBox search = SearchField(f, 560, 38, 330);
        Button searchButton = DarkUi.Button("筛选", 904, 38, 84, DialogResult.None); f.Controls.Add(searchButton);
        int filter = 0;
        CheckBox onlyOpen = new CheckBox { Left = 944, Top = 128, Width = 130, Height = 24, Text = "只看未完成", ForeColor = DarkUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F) };
        f.Controls.Add(onlyOpen);
        Button allTab = DarkUi.Button("全部  0", 32, 118, 96, DialogResult.None), overdueTab = DarkUi.Button("逾期  0", 138, 118, 96, DialogResult.None), futureTab = DarkUi.Button("未开始  0", 244, 118, 104, DialogResult.None), pendingTab = DarkUi.Button("待办  0", 358, 118, 96, DialogResult.None), doneTab = DarkUi.Button("已办  0", 464, 118, 96, DialogResult.None);
        f.Controls.AddRange(new Control[]{allTab,overdueTab,futureTab,pendingTab,doneTab});
        Panel table = new Panel { Left = 32, Top = 180, Width = 1056, Height = 470, BackColor = Color.FromArgb(247, 251, 255), AutoScroll = true };
        DarkUi.EnableDoubleBuffer(table);
        DarkUi.Round(table, 12); f.Controls.Add(table);
        Panel footer = new Panel { Left = 32, Top = 682, Width = 1056, Height = 54, BackColor = Color.FromArgb(245, 251, 255) };
        DarkUi.EnableDoubleBuffer(footer);
        DarkUi.Round(footer, 12); f.Controls.Add(footer);
        Label selectionHint = DarkUi.Label("已选择 0 项", 18, 16, 240); footer.Controls.Add(selectionHint);
        Button edit = DarkUi.Button("修改选中项", 604, 8, 112, DialogResult.None), toggle = DarkUi.Button("批量完成", 728, 8, 112, DialogResult.None), delete = DarkUi.DangerButton("删除", 852, 8, 76, DialogResult.None), add = DarkUi.PrimaryButton("+ 新建待办", 940, 8, 100, DialogResult.None);
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
                pair.Value.BackColor = pair.Key == selectedId ? Color.FromArgb(232, 244, 255) : Color.FromArgb(247, 251, 255);
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
            List<Dictionary<string, object>> all = Tasks(state);
            DateTimeOffset now = DateTimeOffset.Now;
            allTab.Text = "全部  " + all.Count;
            overdueTab.Text = "逾期  " + all.Count(t => !B(t, "completed") && RuntimeUtil.Date(t, "due_at").HasValue && now > RuntimeUtil.Date(t, "due_at").Value);
            futureTab.Text = "未开始  " + all.Count(t => !B(t, "completed") && RuntimeUtil.Date(t, "available_from").HasValue && now < RuntimeUtil.Date(t, "available_from").Value);
            pendingTab.Text = "待办  " + all.Count(t => !B(t, "completed") && (!RuntimeUtil.Date(t, "due_at").HasValue || now <= RuntimeUtil.Date(t, "due_at").Value) && (!RuntimeUtil.Date(t, "available_from").HasValue || now >= RuntimeUtil.Date(t, "available_from").Value));
            doneTab.Text = "已办  " + all.Count(t => B(t, "completed"));
            string query = search.Text.Trim();
            table.SuspendLayout(); DarkUi.SetRedraw(table, false); table.Controls.Clear(); rowChecks.Clear(); rowPanels.Clear();
            AddCellLabel(table, "状态", 50, 14, 70, DarkUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "标题", 128, 14, 350, DarkUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "标签", 486, 14, 140, DarkUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "开始时间", 640, 14, 132, DarkUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "截止时间", 788, 14, 132, DarkUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "操作", 936, 14, 86, DarkUi.Muted, FontStyle.Bold);
            int y = 42;
            foreach (Dictionary<string, object> t in all.Where(t => TaskMatchesFilter(t, filter, now)).Where(t => !onlyOpen.Checked || !B(t, "completed")).Where(t => query == "" || S(t, "title").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || String.Join("、", Labels(t)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(t => B(t,"completed") ? 3 : (RuntimeUtil.Date(t,"due_at").HasValue && now > RuntimeUtil.Date(t,"due_at").Value ? 0 : RuntimeUtil.Date(t,"available_from").HasValue && now < RuntimeUtil.Date(t,"available_from").Value ? 1 : 2)).ThenByDescending(t => RuntimeUtil.Date(t,"created_at") ?? DateTimeOffset.MinValue)) {
                string id = S(t, "id");
                Panel row = new Panel { Left = 12, Top = y, Width = 1016, Height = 42, BackColor = Color.FromArgb(247, 251, 255), Tag = id, Cursor = Cursors.Hand };
                DarkUi.EnableDoubleBuffer(row);
                DarkUi.Round(row, 8);
                row.MouseDown += selectRow;
                CheckBox check = new CheckBox { Left = 6, Top = 11, Width = 20, Height = 20, BackColor = Color.Transparent, Tag = id };
                check.CheckedChanged += delegate { selectionHint.Text = "已选择 " + rowChecks.Count(c => c.Checked) + " 项"; };
                row.Controls.Add(check); rowChecks.Add(check);
                AddCellLabel(row, TaskStatusText(t, now), 36, 11, 70, TaskStatusColor(t, now), FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, S(t,"title"), 114, 11, 350, DarkUi.Text, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, String.Join("  ", Labels(t)), 472, 11, 140, DarkUi.Accent, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, DateEdit(RuntimeUtil.Date(t,"available_from")) == "" ? "一" : DateEdit(RuntimeUtil.Date(t,"available_from")), 626, 11, 132, DarkUi.Text, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, DateEdit(RuntimeUtil.Date(t,"due_at")) == "" ? "一" : DateEdit(RuntimeUtil.Date(t,"due_at")), 774, 11, 132, DarkUi.Text, FontStyle.Regular).MouseDown += selectRow;
                Button openBtn = RowIcon("\xE72A", 914, 5);
                Button editBtn = RowIcon("\xE70F", 948, 5);
                Button deleteBtn = RowIcon("\xE74D", 982, 5);
                openBtn.Click += delegate { bool changed=false; Open(state, id, ref changed); managerChanged |= changed; if (changed) reload(true); };
                editBtn.Click += delegate { bool changed=false; Edit(state, id, ref changed); managerChanged |= changed; if (changed) reload(true); };
                deleteBtn.Click += delegate { bool changed=false; Delete(state, id, ref changed); managerChanged |= changed; if (changed) reload(true); };
                row.Controls.Add(openBtn); row.Controls.Add(editBtn); row.Controls.Add(deleteBtn);
                table.Controls.Add(row); rowPanels[id] = row; y += 42;
            }
            table.ResumeLayout();
            int maxScrollY = Math.Max(0, table.DisplayRectangle.Height - table.ClientSize.Height);
            if (previousScrollY > 0) table.AutoScrollPosition = new Point(0, Math.Min(previousScrollY, maxScrollY));
            DarkUi.SetRedraw(table, true); paintTabs(); paintRows(); selectionHint.Text = "已选择 " + rowChecks.Count(c => c.Checked) + " 项";
        };
        allTab.Click += delegate { filter = 0; reload(false); };
        overdueTab.Click += delegate { filter = 1; reload(false); };
        futureTab.Click += delegate { filter = 2; reload(false); };
        pendingTab.Click += delegate { filter = 3; reload(false); };
        doneTab.Click += delegate { filter = 4; reload(false); };
        search.TextChanged += delegate { reload(false); };
        onlyOpen.CheckedChanged += delegate { reload(false); };
        searchButton.Click += delegate { reload(false); };
        search.Parent.BringToFront();
        search.BringToFront();
        searchButton.BringToFront();
        close.BringToFront();
        reload(false);
        edit.Click += delegate { if (selectedId == "") { selectionHint.Text="请先选中一项需要修改的任务。"; selectionHint.ForeColor=DarkUi.Danger; return; } bool changed=false; Edit(state, selectedId, ref changed); managerChanged |= changed; selectionHint.ForeColor=DarkUi.Muted; if (changed) reload(true); };
        toggle.Click += delegate { List<string> selected=rowChecks.Where(c=>c.Checked).Select(c=>Convert.ToString(c.Tag)).ToList(); if(selected.Count==0){selectionHint.Text="请先勾选需要完成或恢复的任务。";selectionHint.ForeColor=DarkUi.Danger;return;} foreach (string id in selected) { bool changed=false; Toggle(state,id,ref changed); managerChanged |= changed; } selectionHint.ForeColor=DarkUi.Muted; reload(true); };
        delete.Click += delegate { List<string> selected=rowChecks.Where(c=>c.Checked).Select(c=>Convert.ToString(c.Tag)).ToList(); if(selected.Count==0){selectionHint.Text="请先勾选需要删除的任务。";selectionHint.ForeColor=DarkUi.Danger;return;} if(!DarkUi.Confirm("确定删除勾选的 "+selected.Count+" 项任务？","批量删除"))return; foreach (string id in selected) Tasks(state).RemoveAll(t => S(t, "id") == id); Meta(state)["status"]="已批量删除";Commit(state);managerChanged=true;selectionHint.ForeColor=DarkUi.Muted;reload(true); };
        add.Click += delegate { bool changed=false; Add(state, ref changed); managerChanged |= changed; if (changed) reload(false); };
        table.DoubleClick += delegate { edit.PerformClick(); }; f.ShowDialog(); refresh |= managerChanged;
    }

    private static Label AddCellLabel(Control parent, string text, int x, int y, int width, Color color, FontStyle style)
    {
        Label label = new Label { Left = x, Top = y, Width = width, Height = 22, Text = text, ForeColor = color, BackColor = Color.Transparent, AutoEllipsis = true, Font = new Font("Microsoft YaHei UI", 9F, style) };
        parent.Controls.Add(label);
        return label;
    }

    private static Button RowIcon(string text, int x, int y)
    {
        Button button = DarkUi.Button(text, x, y, 28, DialogResult.None);
        button.Height = 30;
        button.Font = new Font("Segoe Fluent Icons", 9F);
        button.BackColor = Color.FromArgb(247, 251, 255);
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private static void PaintTabButton(Button button, bool active)
    {
        button.BackColor = active ? Color.FromArgb(220, 238, 255) : DarkUi.Panel;
        button.ForeColor = active ? DarkUi.Accent : DarkUi.Text;
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
        if (status == "已办") return Color.FromArgb(28, 145, 82);
        if (status == "逾期") return DarkUi.Danger;
        if (status == "未开始") return Color.FromArgb(145, 96, 28);
        return DarkUi.Accent;
    }

    private static bool TaskMatchesFilter(Dictionary<string, object> task, int filter, DateTimeOffset now)
    {
        bool completed = B(task, "completed");
        bool overdue = !completed && RuntimeUtil.Date(task, "due_at").HasValue && now > RuntimeUtil.Date(task, "due_at").Value;
        bool future = !completed && RuntimeUtil.Date(task, "available_from").HasValue && now < RuntimeUtil.Date(task, "available_from").Value;
        if (filter == 1) return overdue;
        if (filter == 2) return future;
        if (filter == 3) return !completed && !overdue && !future;
        if (filter == 4) return completed;
        return true;
    }

    private static string Http(string method, string url, string body, IDictionary<string,string> headers, int timeout)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url); request.Method = method; request.Timeout = timeout; request.ReadWriteTimeout = timeout; request.KeepAlive = false; request.ContentType = "application/json; charset=utf-8";
        if (headers != null) foreach (KeyValuePair<string,string> header in headers) { if (header.Key.Equals("Host",StringComparison.OrdinalIgnoreCase)) request.Host=header.Value; else request.Headers[header.Key]=header.Value; }
        if (body != null) { byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length; using(Stream s=request.GetRequestStream()) s.Write(bytes,0,bytes.Length); }
        using (HttpWebResponse response=(HttpWebResponse)request.GetResponse()) using(StreamReader reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) return reader.ReadToEnd();
    }
    private static Dictionary<string, object> ReadPaperSyncSettings()
    {
        if (!File.Exists(PaperSyncSecret)) return new Dictionary<string, object>();
        try { return JsonUtil.ReadDpapiJson(PaperSyncSecret); }
        catch { return new Dictionary<string, object>(); }
    }

    private static void SavePaperSyncSettings(string baseUrl, string account, string password)
    {
        baseUrl = (baseUrl ?? "").Trim();
        account = (account ?? "").Trim();
        password = (password ?? "").Trim();
        if (baseUrl == "" || account == "") throw new Exception("论文网页同步地址和账号不能为空");
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl;
        JsonUtil.WriteDpapiJson(PaperSyncSecret, new Dictionary<string, object>{{"BaseUrl", baseUrl.TrimEnd('/')}, {"Account", account}, {"Password", password}});
    }

    private static string LoginPaperSync(string baseUrl, string account, string password)
    {
        string login = JsonUtil.Serialize(new Dictionary<string, object>{{"username", account}, {"password", password}});
        return Http("POST", baseUrl.TrimEnd('/') + "/api/login", login, null, 5000).Trim().Trim('"');
    }

    private static void TestPaperSyncConnection(string baseUrl, string account, string password)
    {
        baseUrl = (baseUrl ?? "").Trim();
        account = (account ?? "").Trim();
        password = (password ?? "").Trim();
        if (baseUrl == "" || account == "") throw new Exception("论文网页同步地址和账号不能为空");
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl;
        LoginPaperSync(baseUrl, account, password);
    }

    private static bool DownloadPaper(string path, out string error)
    {
        error = ""; Dictionary<string, object> paperSync = ReadPaperSyncSettings(); string baseUrl = S(paperSync, "BaseUrl"), user = S(paperSync, "Account"), password = S(paperSync, "Password"); if(baseUrl==""||user==""){error="尚未配置论文网页同步";return false;} if(!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))baseUrl="http://"+baseUrl;
        try { string token=LoginPaperSync(baseUrl,user,password); string raw=Http("GET",baseUrl.TrimEnd('/')+"/api/resources/paper/"+Path.GetFileName(path),null,new Dictionary<string,string>{{"X-Auth",token}},5000); Dictionary<string,object> result=JsonUtil.Object(JsonUtil.Deserialize(raw)); object content=JsonUtil.Get(result,"content"); File.WriteAllText(path,content is string?(string)content:JsonUtil.Serialize(content??result),RuntimeUtil.Utf8NoBom); return true; }
        catch(WebException ex){HttpWebResponse response=ex.Response as HttpWebResponse;error=response!=null&&response.StatusCode==HttpStatusCode.NotFound?"远端暂无该日期的已评分论文数据":"论文数据服务连接失败";return false;} catch{error="论文数据服务连接失败";return false;}
    }

    private static Dictionary<string, object> ReadTranslationCredentials()
    {
        if (!File.Exists(TranslationSecret)) return new Dictionary<string, object>();
        try { return JsonUtil.ReadDpapiJson(TranslationSecret); }
        catch { return new Dictionary<string, object>(); }
    }

    private static void SaveTranslationCredentials(string secretId, string secretKey)
    {
        secretId = (secretId ?? "").Trim();
        secretKey = (secretKey ?? "").Trim();
        if (secretId == "" || secretKey == "") throw new Exception("SecretId 和 SecretKey 不能为空");
        JsonUtil.WriteDpapiJson(TranslationSecret, new Dictionary<string, object>{{"SecretId", secretId}, {"SecretKey", secretKey}});
    }

    private static string TestTranslationCredentials(string secretId, string secretKey)
    {
        Dictionary<string, object> credentials = new Dictionary<string, object>{{"SecretId", (secretId ?? "").Trim()}, {"SecretKey", (secretKey ?? "").Trim()}};
        string result = TranslateWithCredentials(credentials, "hello");
        return result == "" ? "翻译服务可用" : result;
    }

    private static string TranslateWithCredentials(Dictionary<string, object> credentials, string text)
    {
        string id = S(credentials, "SecretId"), key = S(credentials, "SecretKey");
        if (id == "" || key == "") throw new Exception("SecretId 和 SecretKey 不能为空");
        const string service = "tmt", host = "tmt.tencentcloudapi.com", action = "TextTranslate";
        long timestamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        string date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");
        string payload = JsonUtil.Serialize(new Dictionary<string, object>{{"SourceText", text}, {"Source", "en"}, {"Target", "zh"}, {"ProjectId", 0}});
        string canonicalHeaders = "content-type:application/json; charset=utf-8\nhost:" + host + "\nx-tc-action:texttranslate\n";
        string signed = "content-type;host;x-tc-action";
        string request = "POST\n/\n\n" + canonicalHeaders + "\n" + signed + "\n" + RuntimeUtil.Sha256Hex(payload);
        string scope = date + "/" + service + "/tc3_request";
        string toSign = "TC3-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + RuntimeUtil.Sha256Hex(request);
        byte[] secretDate = RuntimeUtil.Hmac(Encoding.UTF8.GetBytes("TC3" + key), date);
        byte[] secretService = RuntimeUtil.Hmac(secretDate, service);
        byte[] secretSigning = RuntimeUtil.Hmac(secretService, "tc3_request");
        string signature = BitConverter.ToString(RuntimeUtil.Hmac(secretSigning, toSign)).Replace("-", "").ToLowerInvariant();
        Dictionary<string, string> headers = new Dictionary<string, string>{{"Authorization", "TC3-HMAC-SHA256 Credential=" + id + "/" + scope + ", SignedHeaders=" + signed + ", Signature=" + signature}, {"Host", host}, {"X-TC-Action", action}, {"X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture)}, {"X-TC-Version", "2018-03-21"}, {"X-TC-Region", "ap-guangzhou"}};
        Dictionary<string, object> root = JsonUtil.Object(JsonUtil.Deserialize(Http("POST", "https://" + host, payload, headers, 15000)));
        Dictionary<string, object> response = JsonUtil.Object(JsonUtil.Get(root, "Response"));
        Dictionary<string, object> error = JsonUtil.Object(JsonUtil.Get(response, "Error"));
        string message = JsonUtil.String(error, "Message", "");
        if (message != "") throw new Exception(message);
        string translated = JsonUtil.String(response, "TargetText", "");
        if (translated == "") throw new Exception("腾讯云未返回翻译结果");
        return translated;
    }

    private static string Translate(string text)
    {
        if (!File.Exists(TranslationSecret)) return null;
        try { return TranslateWithCredentials(JsonUtil.ReadDpapiJson(TranslationSecret), text); }
        catch { return null; }
    }
    private static void SyncArxiv(Dictionary<string, object> state, bool manual, string paperDate)
    {
        Directory.CreateDirectory(PaperCache); foreach(string f in Directory.GetFiles(PaperCache,"*_papers.json"))if(File.GetLastWriteTime(f)<DateTime.Now.AddDays(-14))File.Delete(f);
        DateTime now=DateTime.Now; string today=String.IsNullOrEmpty(paperDate)?now.ToString("yyyy-MM-dd"):paperDate;
        if(!manual&&(now.TimeOfDay<TimeSpan.FromHours(8)||now.TimeOfDay>TimeSpan.FromHours(20))){Meta(state)["status"]="arXiv 自动检查时段为 08:00-20:00";return;}
        if(Tasks(state).Any(t=>!B(t,"completed")&&S(t,"source")=="arxiv")){Meta(state)["status"]="待办中已有论文，未重复添加";return;}
        if(!manual&&JsonUtil.String(Meta(state),"last_arxiv_sync_date","")==today){Meta(state)["status"]="今日 arXiv 已检查";return;}
        string name=today+"_papers.json",path=Path.Combine(PaperCache,name),error="";if(!File.Exists(path))DownloadPaper(path,out error);if(!File.Exists(path)){Meta(state)["status"]=error!=""?error:"暂无 "+today+" 已评分论文数据";return;}
        object parsed;try{string json=File.ReadAllText(path,Encoding.UTF8);while(json.StartsWith("[][") )json=json.Substring(2);parsed=JsonUtil.Deserialize(json);}catch{Meta(state)["status"]="今日论文 JSON 无法读取";return;}
        List<Dictionary<string,object>> ranked=JsonUtil.Array(parsed).Select(JsonUtil.Object).Where(p=>JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p,"score")),"abstract")!=null).OrderByDescending(p=>Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p,"score")),"abstract"),CultureInfo.InvariantCulture)).ThenByDescending(p=>Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p,"score")),"title")??0,CultureInfo.InvariantCulture)).Take(5).ToList();if(ranked.Count==0){Meta(state)["status"]="今日还没有完成摘要评分的论文";return;}
        int added=0,translated=0;foreach(Dictionary<string,object> p in ranked){string arxiv=S(p,"arxiv_id"),target="https://arxiv.org/html/"+arxiv;if(Tasks(state).Any(t=>S(t,"target")==target))continue;string original=S(p,"title"),translatedTitle=Translate(original);if(translatedTitle!=null){translated++;Thread.Sleep(220);}Dictionary<string,object> score=JsonUtil.Object(JsonUtil.Get(p,"score"));EditorResult e=new EditorResult{Title="("+Convert.ToString(JsonUtil.Get(score,"abstract"),CultureInfo.InvariantCulture)+") "+(translatedTitle??original),Target=target,Note="论文原标题："+original+"\r\narXiv ID："+arxiv,Labels=new List<string>{"论文"},Available="",Due=""};Tasks(state).Add(NewTask(e,"arxiv"));added++;}
        Meta(state)["last_arxiv_sync_date"]=today;Meta(state)["status"]=added>0?"已添加 "+today+" 共 "+added+" 篇，翻译 "+translated+" 篇":today+" 前五篇均已存在";
    }
    private static bool ConsumeGuard(){if(!File.Exists(GuardPath))return false;try{bool fresh=(DateTime.Now-File.GetLastWriteTime(GuardPath)).TotalSeconds<20;File.Delete(GuardPath);return fresh;}catch{return true;}}
    private static void Refresh(){File.WriteAllText(GuardPath,RuntimeUtil.Iso(DateTimeOffset.Now),RuntimeUtil.Utf8NoBom);RuntimeUtil.Refresh("Todo");string calendar=Path.GetFullPath(Path.Combine(ResourceDir,"..","..","Calendar","Calendar.ini"));if(File.Exists(calendar))RuntimeUtil.Refresh("Calendar");}
}

