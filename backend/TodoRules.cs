using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RainmeterBackend;

internal static partial class TodoApp
{
    private sealed class EditorResult
    {
        public string Title { get; set; }
        public string Target { get; set; }
        public string Note { get; set; }
        public string Available { get; set; }
        public string Due { get; set; }
        public List<string> Labels { get; set; }
    }
    private static int Normalize(Dictionary<string, object> state)
    {
        List<Dictionary<string, object>> tasks = Tasks(state);
        int changed = 0, rolled = 0;
        DateTimeOffset now = DateTimeOffset.Now, cutoff = now.AddDays(-30);
        foreach (Dictionary<string, object> task in tasks.ToList())
        {
            Dictionary<string, object> policy = JsonUtil.Object(JsonUtil.Get(task, "policy"));
            string manualLabel = JsonUtil.String(policy, "manual_complete_label", "");
            string rolloverLabel = JsonUtil.String(policy, "rollover_label", "");
            if (B(task, "completed") && manualLabel != "" && !Labels(task).Contains(manualLabel) && (rolloverLabel == "" || !Labels(task).Contains(rolloverLabel)))
            {
                AddLabel(task, manualLabel); changed++;
            }
            if (!B(task, "completed") && JsonUtil.String(policy, "daily_rollover", "") == "auto_complete")
            {
                DateTimeOffset window = PolicyWindow(now, JsonUtil.String(policy, "daily_boundary", "06:00"));
                DateTimeOffset? created = RuntimeUtil.Date(task, "created_at");
                if (created.HasValue && created.Value < window)
                {
                    task["completed"] = true; task["completed_at"] = RuntimeUtil.Iso(window.AddTicks(-1));
                    if (rolloverLabel != "") AddLabel(task, rolloverLabel);
                    if (manualLabel != "") RemoveLabel(task, manualLabel);
                    rolled++;
                }
            }
        }
        int removed = tasks.RemoveAll(t => B(t, "completed") && RuntimeUtil.Date(t, "completed_at").HasValue && RuntimeUtil.Date(t, "completed_at").Value < cutoff);
        if (changed + rolled + removed > 0) Save(state);
        return rolled;
    }

    private static DateTimeOffset PolicyWindow(DateTimeOffset now, string boundaryText)
    {
        TimeSpan time;
        if (!TimeSpan.TryParseExact(boundaryText, @"hh\:mm", CultureInfo.InvariantCulture, out time)) time = TimeSpan.FromHours(6);
        DateTimeOffset boundary = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hours, time.Minutes, 0, now.Offset);
        return now < boundary ? boundary.AddDays(-1) : boundary;
    }

    private static DateTimeOffset CompletionWindow(DateTimeOffset now) { return PolicyWindow(now, "06:00"); }

    private static Dictionary<string, object> NewTask(EditorResult e, string source)
    {
        return new Dictionary<string, object>{{"id", Guid.NewGuid().ToString("N")}, {"title", e.Title}, {"target", e.Target}, {"note", e.Note}, {"labels", e.Labels.Cast<object>().ToList()}, {"completed", false}, {"source", source}, {"created_at", RuntimeUtil.Iso(DateTimeOffset.Now)}, {"completed_at", null}, {"available_from", String.IsNullOrEmpty(e.Available) ? null : (object)e.Available}, {"due_at", String.IsNullOrEmpty(e.Due) ? null : (object)e.Due}};
    }
    private static Dictionary<string, object> Find(Dictionary<string, object> state, string id) { return Tasks(state).FirstOrDefault(t => S(t, "id") == id); }
    private static void Toggle(Dictionary<string, object> state, string id, ref bool refresh)
    {
        Dictionary<string, object> task = Find(state, id); if (task == null) return;
        Dictionary<string, object> policy = JsonUtil.Object(JsonUtil.Get(task, "policy"));
        string manualLabel = JsonUtil.String(policy, "manual_complete_label", ""), rolloverLabel = JsonUtil.String(policy, "rollover_label", "");
        if (B(task, "completed")) {
            task["completed"] = false; task["completed_at"] = null;
            if (JsonUtil.Bool(policy, "restore_resets_age", false)) task["created_at"] = RuntimeUtil.Iso(DateTimeOffset.Now);
            if (manualLabel != "") RemoveLabel(task, manualLabel); if (rolloverLabel != "") RemoveLabel(task, rolloverLabel);
            Meta(state)["status"] = "已恢复到待办";
        }
        else {
            task["completed"] = true; task["completed_at"] = RuntimeUtil.Iso(DateTimeOffset.Now);
            if (manualLabel != "") AddLabel(task, manualLabel); if (rolloverLabel != "") RemoveLabel(task, rolloverLabel);
            Meta(state)["status"] = "已完成";
        }
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

