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
    private static Dictionary<string, object> NewState()
    {
        return new Dictionary<string, object> {
            {"version", 3}, {"meta", new Dictionary<string, object>{{"status", "就绪"}}},
            {"tasks", new List<object>()}
        };
    }

    private static Dictionary<string, object> LoadState()
    {
        PluginRuntime.BootstrapBundled(Path.Combine(ResourceDir,"BundledPlugins"));
        PluginRuntime.MigrateInstallation(ResourceDir,StatePath,Path.GetFullPath(Path.Combine(ResourceDir,"..","..","Calendar","@Resources","calendar-state.json")));
        // v2.1：把 arxiv 的旧 AI / 翻译 / 文件服务器配置复制一份给三个官方 Provider（复制不删除，§9.2）。
        // 靠 migration-v2.1.json 的 step 级 marker 幂等；全部终态时这里只剩一次 File.Exists。
        ProviderMigration.Run();
        if (!File.Exists(StatePath)) return NewState();
        try
        {
            Dictionary<string, object> state = JsonUtil.LoadObject(StatePath);
            int oldVersion = JsonUtil.Int(state, "version", 1);
            TodoExternalImport.MigrateV3(state);
            if (!(JsonUtil.Get(state, "meta") is Dictionary<string, object>)) state["meta"] = new Dictionary<string, object>{{"status", "就绪"}};
            if (JsonUtil.Get(state, "tasks") == null) state["tasks"] = new List<object>();
            if (oldVersion < 3) JsonUtil.SaveAtomic(StatePath, state);
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

    private static void Save(Dictionary<string, object> state) { JsonUtil.SaveAtomic(StatePath, state); try{PluginRuntime.WritePluginTaskSnapshots(state);}catch{} }
    private static void Commit(Dictionary<string, object> state) { Save(state); Render(state); }

}
