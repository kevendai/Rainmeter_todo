using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using RainmeterBackend;

internal static partial class CalendarApp
{
    private static string R = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static string CachePath { get { return Path.Combine(R,"calendar-cache.json"); } }
    private static string StatePath { get { return Path.Combine(R,"calendar-state.json"); } }
    private static string IncludePath { get { return Path.Combine(R,"Generated.inc"); } }
    private static string GuardPath { get { return Path.Combine(R,".refresh-guard"); } }
    private static string TodoDir { get { return Path.GetFullPath(Path.Combine(R,"..","..","Todo","@Resources")); } }
    private static string SecretPath { get { return Path.Combine(TodoDir,"caldav.secret"); } }

    [STAThread] private static int Main(string[] args)
    {
        UiScale.EnableDpiAwareness();
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string action=args.Length>0?args[0]:"Render",id=args.Length>1?args[1]:"";
        if(action=="UiServerModel"||action=="UiServerSave"||action=="UiServerTest"||action=="UiServerClear")
            return HandleServerUi(action,id,args.Length>2?args[2]:"");
        if((action=="New"||action=="Edit"||action=="Detail"||action=="Manage"||action=="Settings")
            && DesktopUiBridge.TryOpen(TodoDir,"calendar",action,id))return 0;
        if(action=="LegacyEdit")action="Edit";
        bool softOpen=action=="Manage"||action=="Settings";
        using(Mutex mutex=new Mutex(false,@"Global\RainmeterCalendarState")){bool held=false;Dictionary<string,object> cache=null,state=null;try{
            held=mutex.WaitOne(softOpen?TimeSpan.FromMilliseconds(300):TimeSpan.FromSeconds(20));if(!held&&!softOpen)return 4;cache=Load(CachePath,NewCache());state=Load(StatePath,NewState());Shape(cache,state);if(held&&Reconcile(state))Save(StatePath,state);bool refresh=false,refreshTodo=false;
            if(action=="Startup"||action=="Rollover"||action=="Sync"){
                bool guarded=action=="Startup"&&ConsumeGuard();DateTimeOffset? last=RuntimeUtil.Date(cache,"fetched_at");bool need=action=="Sync"||!last.HasValue||(action=="Rollover"&&last.Value.Date!=DateTimeOffset.Now.Date)||(DateTimeOffset.Now-last.Value).TotalMinutes>=15;
                if(need)Sync(cache,state,ref refreshTodo);refresh=Render(cache,state)&&(action!="Startup"||!guarded);if(action=="Sync")refresh=true;
            } else if(action=="Render")refresh=Render(cache,state);
            else if(action=="UiSave"){
                try{
                    SaveFromUi(id,state,cache);
                    Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;
                    JsonUtil.SaveAtomic(id+".result.json",new Dictionary<string,object>{{"ok",true}});
                }catch(Exception uiError){
                    JsonUtil.SaveAtomic(id+".result.json",new Dictionary<string,object>{{"ok",false},{"error",uiError.Message}});
                    throw;
                }
            }
            else if(action=="Open"){Dictionary<string,object> ev=FindEvent(cache,state,id);if(ev!=null)RuntimeUtil.Run(Target(ev));}
            else if(action=="New"){if(EditInteractive(null,state,cache)){Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}
            else if(action=="Edit"){Dictionary<string,object> ev=FindEvent(cache,state,id);if(ev!=null&&EditInteractive(ev,state,cache)){Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}
            else if(action=="Detail"||action=="Convert"){Dictionary<string,object> ev=FindEvent(cache,state,id);if(ev!=null){bool already=Conversions(state).Any(c=>S(c,"occurrence_key")==S(ev,"occurrence_key")),hasRule=Rules(state).Any(r=>S(r,"uid")==S(ev,"uid"));DialogResult detail=action=="Convert"?DialogResult.OK:ShowDetails(ev,already,hasRule);if(detail==DialogResult.Yes){if(EditInteractive(ev,state,cache)){Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}else if(detail==DialogResult.OK){if(ConvertInteractive(ev,state,cache))refreshTodo=true;Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}}
            else if(action=="Manage"){if(held){mutex.ReleaseMutex();held=false;}if(ManageEvents(state,cache,ref refreshTodo)){Render(cache,state);refresh=true;}}
            else if(action=="Settings"){if(held){mutex.ReleaseMutex();held=false;}Settings(state,cache,ref refreshTodo);Render(cache,state);refresh=true;}
            if(refreshTodo){string todoExe=Path.Combine(TodoDir,"TodoHost.exe");if(File.Exists(todoExe)){using(System.Diagnostics.Process p=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(todoExe,"Render"){UseShellExecute=false,CreateNoWindow=true})){if(p!=null&&!p.WaitForExit(10000))try{p.Kill();}catch{}}}}
            if(refresh){MarkGuard();RuntimeUtil.Refresh("Calendar");if(refreshTodo)RuntimeUtil.Refresh("Todo");}else if(refreshTodo)RuntimeUtil.Refresh("Todo");return 0;
        }catch(Exception ex){if(cache!=null){cache["status"]="操作失败："+ex.Message;try{Save(CachePath,cache);Render(cache,state);if(!action.Equals("Startup",StringComparison.OrdinalIgnoreCase))RuntimeUtil.Refresh("Calendar");}catch{}}return 1;}finally{if(held)mutex.ReleaseMutex();}}
    }

    private static void SaveFromUi(string inputPath,Dictionary<string,object> state,Dictionary<string,object> cache)
    {
        if(String.IsNullOrWhiteSpace(inputPath)||!File.Exists(inputPath))throw new Exception("日程输入文件不存在。");
        Dictionary<string,object> input=JsonUtil.LoadObject(inputPath);
        string id=JsonUtil.String(input,"id",""),source=JsonUtil.String(input,"source","local");
        Dictionary<string,object> original=id==""?null:FindEvent(cache,state,id);
        if(id!=""&&original==null)throw new Exception("日程已不存在，请刷新后重试。");
        if(original!=null&&B(original,"recurring"))throw new Exception("周期日程需要使用高级编辑。");
        if(source!="local"&&source!="caldav")throw new Exception("日程来源无效。");
        if(original!=null&&S(original,"source")!=source)throw new Exception("当前不支持更改已有日程来源。");
        string title=JsonUtil.String(input,"title","").Trim();
        if(title=="")throw new Exception("标题不能为空。");
        DateTimeOffset start,end;
        if(!DateTimeOffset.TryParse(JsonUtil.String(input,"start_at",""),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out start)
            ||!DateTimeOffset.TryParse(JsonUtil.String(input,"end_at",""),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out end)
            ||end<=start)throw new Exception("日程开始和结束时间无效。");
        Dictionary<string,object> draft=DraftEvent(original,source,title,start,end,
            JsonUtil.Bool(input,"all_day",false),JsonUtil.String(input,"location",""),
            JsonUtil.String(input,"url",""),JsonUtil.String(input,"description",""),
            original==null?new List<int>():Reminders(original),
            original==null?new List<string>():CustomAlarms(original),
            new RecurrenceSpec());
        if(source=="caldav")SaveCalDavEvent(draft,cache);
        else{SaveLocalEvent(draft,state);cache["status"]="已保存到本地日历";}
    }

}
