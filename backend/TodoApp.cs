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
    private const string AppVersion = "1.3.3";
    private const string GitHubRepository = "kevendai/Rainmeter_todo";
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
    private static string UpdaterScript { get { return Path.Combine(ResourceDir, "Updater", "RainmeterDesktopWidgetsUpdater.ps1"); } }
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
                    case "Refresh":
                        if (PaperFeaturesEnabled) SyncArxiv(state, force, "");
                        Save(state); Render(state); refresh = true; break;
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





    private static string TimeLabel(Dictionary<string, object> task, DateTimeOffset now)
    {
        DateTimeOffset? due = RuntimeUtil.Date(task, "due_at"), available = RuntimeUtil.Date(task, "available_from");
        if (due.HasValue && now > due.Value) return "逾期 · 截止于" + due.Value.ToString("M月d日 HH:mm");
        if (due.HasValue) return (due.Value.Date == now.Date ? "今天" : due.Value.Date == now.Date.AddDays(1) ? "明天" : due.Value.ToString("M月d日")) + " " + due.Value.ToString("HH:mm") + " 截止";
        return available.HasValue ? available.Value.ToString("M月d日 HH:mm") + " 开始" : "";
    }






    private static bool ConsumeGuard(){if(!File.Exists(GuardPath))return false;try{bool fresh=(DateTime.Now-File.GetLastWriteTime(GuardPath)).TotalSeconds<20;File.Delete(GuardPath);return fresh;}catch{return true;}}
    private static void Refresh(){File.WriteAllText(GuardPath,RuntimeUtil.Iso(DateTimeOffset.Now),RuntimeUtil.Utf8NoBom);RuntimeUtil.Refresh("Todo");string calendar=Path.GetFullPath(Path.Combine(ResourceDir,"..","..","Calendar","Calendar.ini"));if(File.Exists(calendar))RuntimeUtil.Refresh("Calendar");}
}

