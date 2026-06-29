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

internal static partial class TodoApp
{
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
        Dictionary<string, object> task = Find(state, id); if (task == null || !LightUi.Confirm("确定删除“" + S(task, "title") + "”？", "删除待办")) return;
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

}

