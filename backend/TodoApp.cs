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
    private const string GitHubRepository = "kevendai/Rainmeter_todo";
    private static string ResourceDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static readonly string AppVersion = LoadAppVersion();
    private const string AppEditionName = "统一版";
    private static string StatePath { get { return Path.Combine(ResourceDir, "tasks.json"); } }
    private static string IncludePath { get { return Path.Combine(ResourceDir, "Generated.inc"); } }
    private static string GuardPath { get { return Path.Combine(ResourceDir, ".refresh-guard"); } }
    private static string UpdaterExecutable { get { return Path.Combine(ResourceDir, "Updater", "UpdaterHost.exe"); } }
    private static string PaperSyncSecret { get { return Path.Combine(ResourceDir, "paper-sync.secret"); } }
    private static string TranslationSecret { get { return Path.Combine(ResourceDir, "translation.secret"); } }
    private static string PluginHostPath { get { return Path.Combine(ResourceDir, "PluginHost.exe"); } }

    private static string LoadAppVersion()
    {
        string versionPath = Path.Combine(ResourceDir, "app-version.txt");
        if (File.Exists(versionPath))
        {
            string value = File.ReadAllText(versionPath, Encoding.UTF8).Trim();
            if (value != "") return value;
        }
        return "0.0.0";
    }

    [STAThread]
    private static int Main(string[] args)
    {
        UiScale.EnableDpiAwareness();
        string action = args.Length > 0 ? args[0] : "Render";
        string id = args.Length > 1 ? args[1] : "";
        string pluginAction = args.Length > 2 ? args[2] : "";
        bool force = args.Any(x => String.Equals(x, "Force", StringComparison.OrdinalIgnoreCase));
        if (action == "Add" || action == "Edit" || action == "Manage" || action == "Settings" || action == "Delete" || action == "PluginConfirmAttention")
            return DesktopUiBridge.TryOpen(ResourceDir, "todo", action, id) ? 0 : 5;
        if (action == "UiCheckUpdateModel") {
            try { UpdateCheckResult info=CheckLatestUpdate();
                JsonUtil.SaveAtomic(id,new Dictionary<string,object>{{"ok",true},{"tag",info.Tag},{"is_newer",info.IsNewer}});return 0;
            } catch(Exception ex) { JsonUtil.SaveAtomic(id,new Dictionary<string,object>{{"ok",false},{"error",ex.Message}});return 1; }
        }
        if (action == "UiStartUpdate") { StartExternalUpdater(); return 0; }
        if (action == "UiApplyTileScale") {
            try { UiScale.SaveMode(id); RenderUiScaleSkins(); return 0; }
            catch { return 1; }
        }
        if (action == "UiBackupExport" || action == "UiBackupPreview" || action == "UiBackupApply")
            return RunBackupUiCommand(action);
        if (action == "BackupSelfTest") return RunBackupSelfTests();
        if (action == "UiMarketRefresh") return UiMarketRefresh(id);
        if (action == "UiMarketInstall") return UiMarketInstall(id, pluginAction);
        if (action == "UiPluginConfigModel") return UiPluginConfigModel(id, pluginAction);
        if (action == "UiPluginConfigSave") return UiPluginConfigSave(id, pluginAction, args.Length > 3 ? args[3] : "");
        if (action == "UiPluginConfigCancel") return UiPluginConfigCancel(id, pluginAction, args.Length > 3 ? args[3] : "");
        if(action=="PluginAction"){if(id==""||pluginAction=="")return 2;StartPluginCommand("PluginAction",id+" "+pluginAction);return 0;}
        if(action=="PluginRescore")return RunExplicitPaperRescore(id);
        // 规格 §5.2：磁贴上的【使用 DeepSeek AI】按钮与插件管理里的「处理待确认…」都走这里。
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
                if (rolled > 0) Meta(state)["status"] = "已按任务策略自动归档 " + rolled + " 项";
                switch (action)
                {
                    case "Startup":
                        bool guarded = ConsumeGuard();
                        Save(state);
                        refresh |= Render(state) && !guarded;
                        if (!guarded)
                        {
                            StartPluginCommand("SyncAll", "startup");
                            StartPluginCommand("ValuesAll", "startup");
                        }
                        break;
                    case "Rollover": refresh |= Render(state); break;
                    case "Refresh":
                        Save(state); Render(state); StartTilePluginSync();StartPluginCommand("ValuesAll","manual"); refresh = true; break;
                    case "Render": Render(state); break;
                    case "RenderAndRefresh": Render(state); refresh = true; break;
                    case "Toggle": Toggle(state, id, ref refresh); break;
                    case "Open": Open(state, id, ref refresh); break;
                    case "PluginClearTasks":
                        if(id!=""){int removed=Tasks(state).RemoveAll(t=>JsonUtil.String(JsonUtil.Object(JsonUtil.Get(t,"origin")),"plugin_id","").Equals(id,StringComparison.OrdinalIgnoreCase));Meta(state)["status"]="已清除 "+removed+" 项插件待办";Commit(state);refresh=true;}break;
                    case "PluginSync":
                        if(id!="")StartPluginCommand("Sync",id);Commit(state);refresh=true;break;
                }
                if (refresh) Refresh();
                return 0;
            }
            catch (Exception ex)
            {
                if (state != null)
                {
                    Meta(state)["status"] = "操作失败：" + ex.Message;
                    try { Commit(state); if (!action.Equals("Startup", StringComparison.OrdinalIgnoreCase)) Refresh(); } catch { }
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






    private static bool ConsumeGuard(){if(!File.Exists(GuardPath))return false;try{bool fresh=(DateTime.Now-File.GetLastWriteTime(GuardPath)).TotalSeconds<20;if(!fresh)File.Delete(GuardPath);return fresh;}catch{return true;}}
    private static void StartPluginCommand(string action,string pluginId)
    {
        StartPluginCommand(action,pluginId,"","","",false);
    }

    private static void StartTilePluginSync()
    {
        if(!File.Exists(PluginHostPath))return;
        Process sync=Process.Start(new ProcessStartInfo(PluginHostPath,"SyncAll tile_refresh"){
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
        if(sync==null)return;
        ThreadPool.QueueUserWorkItem(delegate{
            try
            {
                if(!sync.WaitForExit(1800000))return;
                using(Mutex gate=new Mutex(false,@"Global\RainmeterTileAiPrompt"))
                {
                    bool held=false;
                    try
                    {
                        try{held=gate.WaitOne(TimeSpan.FromSeconds(35));}catch(AbandonedMutexException){held=true;}
                        if(!held)return;
                        string id="io.github.kevendai.arxiv";
                        string path=Path.Combine(RainmeterBackend.PluginPaths.Jobs,id+".json");
                        if(!File.Exists(path))return;
                        Dictionary<string,object> job=JsonUtil.LoadObject(path);
                        if(JsonUtil.String(job,"state","")!="attention"||
                            !JsonUtil.Bool(JsonUtil.Object(JsonUtil.Get(job,"attention")),"allow_snooze",false))return;
                        using(Process prompt=Process.Start(new ProcessStartInfo(Path.Combine(ResourceDir,"TodoHost.exe"),
                            "PluginConfirmAttention "+id){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))
                            if(prompt!=null)prompt.WaitForExit();
                    }
                    finally{if(held)gate.ReleaseMutex();}
                }
            }
            catch(Exception ex){try{Console.Error.WriteLine("论文确认未能打开："+ex.Message);}catch{}}
            finally{sync.Dispose();}
        });
    }

    // An explicit click on “重新爬取并打分” authorizes this one paid AI run.
    // The consent marker is scoped to this child only and never reaches plugins.
    private static int RunExplicitPaperRescore(string id)
    {
        if(id!="io.github.kevendai.arxiv"||!Environment.UserInteractive)return 2;
        string input=Path.Combine(Path.GetTempPath(),"RainmeterPaperRescore-"+Guid.NewGuid().ToString("N")+".json");
        string output=input+".result.json";
        try
        {
            JsonUtil.SaveAtomic(input,new Dictionary<string,object>{{"allow_paid_ai",true}});
            ProcessStartInfo info=new ProcessStartInfo(PluginHostPath,
                "PluginAction "+id+" rescore \""+input+"\" \""+output+"\""){
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            info.EnvironmentVariables[RainmeterBackend.PluginRuntime.PaidConsentVariable]="1";
            using(Process process=Process.Start(info))
            {
                if(process==null)return 1;
                if(!process.WaitForExit(1800000))return 1;
                Refresh();
                return process.ExitCode;
            }
        }
        catch(Exception ex){try{Console.Error.WriteLine("重新爬取并打分未能启动："+ex.Message);}catch{}return 1;}
        finally
        {
            try{if(File.Exists(input))File.Delete(input);}catch{}
            try{if(File.Exists(output))File.Delete(output);}catch{}
        }
    }

    // 规格 §5.3 第 3 行：宿主侧早就支持 `PluginAction <id> <actionId> <inputPath> <outputPath>`，
    // 但 TodoApp 原来只能传前两个参数 —— 付费确认就没法把 resume_input 送进去。
    // consent=true 时给自己启动的这个 PluginHost 打上一次性同意标记：**只有这条路径**能产生它，
    // 而 PluginRuntime 又绝不会把它下发给插件子进程（规格 §5.6 的机制保障）。
    private static void StartPluginCommand(string action,string pluginId,string pluginAction,string inputPath,string outputPath,bool consent)
    {
        if(!File.Exists(PluginHostPath))return;
        string arguments=action+" "+pluginId+(pluginAction==""?"":" "+pluginAction);
        if(inputPath!="")arguments+=" \""+inputPath+"\""+(outputPath==""?"":" \""+outputPath+"\"");
        ProcessStartInfo info=new ProcessStartInfo(PluginHostPath,arguments){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        if(consent)info.EnvironmentVariables[RainmeterBackend.PluginRuntime.PaidConsentVariable]="1";
        try
        {
            using(Process child=Process.Start(info))
            {
                if(child==null||!consent)return;
                // 付费这一轮可能跑几分钟：等它收尾再刷一次磁贴，免得停在"正在运行"。
                Process running=child;
                ThreadPool.QueueUserWorkItem(delegate{try{running.WaitForExit(1800000);}catch{}try{Refresh();}catch{}});
            }
        }
        catch(ObjectDisposedException){}
    }

    private static void Refresh(){File.WriteAllText(GuardPath,RuntimeUtil.Iso(DateTimeOffset.Now),RuntimeUtil.Utf8NoBom);RuntimeUtil.Refresh("Todo");string calendar=Path.GetFullPath(Path.Combine(ResourceDir,"..","..","Calendar","Calendar.ini"));if(File.Exists(calendar))RuntimeUtil.Refresh("Calendar");}
}
