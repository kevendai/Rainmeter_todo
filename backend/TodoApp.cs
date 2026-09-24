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
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string action = args.Length > 0 ? args[0] : "Render";
        string id = args.Length > 1 ? args[1] : "";
        string pluginAction = args.Length > 2 ? args[2] : "";
        bool force = args.Any(x => String.Equals(x, "Force", StringComparison.OrdinalIgnoreCase));
        if ((action == "Add" || action == "Edit" || action == "Manage" || action == "Settings")
            && DesktopUiBridge.TryOpen(ResourceDir, "todo", action, id)) return 0;
        if (action == "LegacySettings") return SettingsInteractive();
        if (action == "UiExportBackup") return RunUiSetting(delegate {
            string path = ExportUserBackupInteractive();
            if (path != "") MessageBox.Show("备份已保存：\r\n" + path, "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        if (action == "UiImportBackup") return RunUiSetting(delegate {
            string result = ImportUserBackupInteractive();
            if (result != "") { RenderUiScaleSkins(); MessageBox.Show(result, "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        });
        if (action == "UiCheckUpdate") return RunUiSetting(delegate {
            UpdateCheckResult info = CheckLatestUpdate();
            if (!info.IsNewer) MessageBox.Show("已是最新版本：" + info.Tag, "检查更新");
            else if (MessageBox.Show("发现 " + info.Tag + "，现在启动升级器？", "检查更新",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) StartExternalUpdater();
        });
        if (action == "UiTileScale") return RunUiSetting(delegate {
            UiScale.SaveMode(id);
            RenderUiScaleSkins();
            MessageBox.Show("桌面磁贴大小已应用。", "磁贴缩放", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        if (action == "UiBackupExport" || action == "UiBackupPreview" || action == "UiBackupApply")
            return RunBackupUiCommand(action);
        if (action == "Add") return AddInteractive();
        if (action == "Edit") return EditInteractive(id);
        if (action == "Manage") return ManageInteractive();
        if (action == "Settings") return SettingsInteractive();
        if (action == "BackupSelfTest") return RunBackupSelfTests();
        if (action == "UiMarketRefresh") return UiMarketRefresh(id);
        if (action == "UiMarketInstall") return UiMarketInstall(id, pluginAction);
        if (action == "UiPluginConfigModel") return UiPluginConfigModel(id, pluginAction);
        if (action == "UiPluginConfigSave") return UiPluginConfigSave(id, pluginAction, args.Length > 3 ? args[3] : "");
        if(action=="PluginAction"){if(id==""||pluginAction=="")return 2;StartPluginCommand("PluginAction",id+" "+pluginAction);return 0;}
        // 规格 §5.2：磁贴上的【使用 DeepSeek AI】按钮与插件管理里的「处理待确认…」都走这里。
        if(action=="PluginConfirmAttention")return ConfirmPaidAttention(id);
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
                        Save(state); Render(state); StartPluginCommand("SyncAll", "manual");StartPluginCommand("ValuesAll","manual"); refresh = true; break;
                    case "Render": Render(state); break;
                    case "Delete": Delete(state, id, ref refresh); break;
                    case "Toggle": Toggle(state, id, ref refresh); break;
                    case "Open": Open(state, id, ref refresh); break;
                    case "PluginClearTasks":
                        if(id!=""&&LightUi.Confirm("确定清除插件 "+id+" 已创建的全部待办？此操作不会卸载插件。","清除插件待办")){int removed=Tasks(state).RemoveAll(t=>JsonUtil.String(JsonUtil.Object(JsonUtil.Get(t,"origin")),"plugin_id","").Equals(id,StringComparison.OrdinalIgnoreCase));Meta(state)["status"]="已清除 "+removed+" 项插件待办";Commit(state);refresh=true;}break;
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

    private static int RunUiSetting(Action action)
    {
        try { action(); return 0; }
        catch (Exception ex) { LightUi.Error(ex.Message); return 1; }
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

    // 规格 §5.2：整个系统里**唯一**能产生"用户已明确同意付费"的地方。
    // 返回 0 表示流程已走完（同意或拒绝都算）；1 表示没能处理（job 保持原状）。
    private static int ConfirmPaidAttention(string pluginId)
    {
        if(pluginId=="")return 2;
        string jobPath=Path.Combine(RainmeterBackend.PluginPaths.Jobs,pluginId+".json");
        if(!File.Exists(jobPath)){LightUi.Error("没有找到这个插件的任务状态。");return 1;}
        Dictionary<string,object> job;
        try{job=JsonUtil.LoadObject(jobPath);}catch(Exception ex){LightUi.Error("任务状态无法读取："+ex.Message);return 1;}
        string state=JsonUtil.String(job,"state",""),resumeAction=JsonUtil.String(job,"resume_action","");
        if(String.Equals(state,"running",StringComparison.OrdinalIgnoreCase)){LightUi.Error("这个插件正在运行，请先等它结束或在插件管理里取消它。");return 1;}
        if(resumeAction==""){LightUi.Error("这个任务现在不需要你确认。");return 1;}
        // 规格 §5.6：无交互桌面 / 无人值守 ⇒ 不允许"自动同意"。这里什么都不做就退出：
        // 不写付费记号、不启动任何进程、job 原样保持 attention 等用户在界面上点。
        if(!Environment.UserInteractive){Console.Error.WriteLine("没有交互式桌面，拒绝没有人确认的付费调用（provider_denied）");return 1;}
        string name=pluginId;try{name=RainmeterBackend.PluginRuntime.Resolve(pluginId,false).Name;}catch{}
        string message=JsonUtil.String(job,"message","");
        string text=(message==""?"这个任务需要用 AI 继续。":message)+"\r\n\r\n继续会用你填好的 API Key 调用一次模型，费用记在你的账号上；取消则本轮什么都不同步，也不会产生任何调用。";
        if(!LightUi.ConfirmRisk(text,name+" 需要你的确认","需要你的确认","使用 AI 评分"))
        {
            StartPluginCommand("Cancel",pluginId+" user_declined");
            Refresh();
            return 0;
        }
        Dictionary<string,object> input=JsonUtil.Object(JsonUtil.Get(job,"resume_input"));
        input["allow_paid_ai"]=true;
        string inputPath=Path.Combine(Path.GetTempPath(),"RainmeterPaidConsent-"+Guid.NewGuid().ToString("N")+".json");
        string outputPath=inputPath+".result.json";
        try{JsonUtil.SaveAtomic(inputPath,input);}
        catch(Exception ex){LightUi.Error("无法写入确认输入："+ex.Message);return 1;}
        StartPluginCommand("PluginAction",pluginId,resumeAction,inputPath,outputPath,true);
        Refresh();
        return 0;
    }
    private static void Refresh(){File.WriteAllText(GuardPath,RuntimeUtil.Iso(DateTimeOffset.Now),RuntimeUtil.Utf8NoBom);RuntimeUtil.Refresh("Todo");string calendar=Path.GetFullPath(Path.Combine(ResourceDir,"..","..","Calendar","Calendar.ini"));if(File.Exists(calendar))RuntimeUtil.Refresh("Calendar");}
}
