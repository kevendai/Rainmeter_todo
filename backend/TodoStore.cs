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

    private static void Save(Dictionary<string, object> state) { JsonUtil.SaveAtomic(StatePath, state); }
    private static void Commit(Dictionary<string, object> state) { Save(state); Render(state); }

}

