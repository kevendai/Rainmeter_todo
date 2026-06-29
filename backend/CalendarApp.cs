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
    private static string TodoPath { get { return Path.Combine(TodoDir,"tasks.json"); } }
    private static string SecretPath { get { return Path.Combine(TodoDir,"caldav.secret"); } }

    [STAThread] private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string action=args.Length>0?args[0]:"Render",id=args.Length>1?args[1]:"";
        bool softOpen=action=="Manage"||action=="Settings";
        using(Mutex mutex=new Mutex(false,@"Global\RainmeterCalendarState")){bool held=false;Dictionary<string,object> cache=null,state=null;try{
            held=mutex.WaitOne(softOpen?TimeSpan.FromMilliseconds(300):TimeSpan.FromSeconds(20));if(!held&&!softOpen)return 4;cache=Load(CachePath,NewCache());state=Load(StatePath,NewState());Shape(cache,state);if(held&&Reconcile(state))Save(StatePath,state);bool refresh=false,refreshTodo=false;
            if(action=="Startup"||action=="Rollover"||action=="Sync"){
                bool guarded=action=="Startup"&&ConsumeGuard();DateTimeOffset? last=RuntimeUtil.Date(cache,"fetched_at");bool need=action=="Sync"||!last.HasValue||(action=="Rollover"&&last.Value.Date!=DateTimeOffset.Now.Date)||(DateTimeOffset.Now-last.Value).TotalMinutes>=15;
                if(need)Sync(cache,state,ref refreshTodo);refresh=Render(cache,state)&&(action!="Startup"||!guarded);if(action=="Sync")refresh=true;
            } else if(action=="Render")refresh=Render(cache,state);
            else if(action=="Open"){Dictionary<string,object> ev=FindEvent(cache,state,id);if(ev!=null)RuntimeUtil.Run(Target(ev));}
            else if(action=="New"){if(EditInteractive(null,state,cache)){Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}
            else if(action=="Edit"){Dictionary<string,object> ev=FindEvent(cache,state,id);if(ev!=null&&EditInteractive(ev,state,cache)){Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}
            else if(action=="Detail"||action=="Convert"){Dictionary<string,object> ev=FindEvent(cache,state,id);if(ev!=null){bool already=Conversions(state).Any(c=>S(c,"occurrence_key")==S(ev,"occurrence_key"));DialogResult detail=action=="Convert"?DialogResult.OK:ShowDetails(ev,already);if(detail==DialogResult.Yes){if(EditInteractive(ev,state,cache)){Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}else if(detail==DialogResult.OK){if(ConvertInteractive(ev,state,cache))refreshTodo=true;Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}}
            else if(action=="Manage"){if(held){mutex.ReleaseMutex();held=false;}if(ManageEvents(state,cache,ref refreshTodo)){Render(cache,state);refresh=true;}}
            else if(action=="Settings"){if(held){mutex.ReleaseMutex();held=false;}Settings(state,cache,ref refreshTodo);Render(cache,state);refresh=true;}
            if(refreshTodo){string todoExe=Path.Combine(TodoDir,"TodoHost.exe");if(File.Exists(todoExe)){using(System.Diagnostics.Process p=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(todoExe,"Render"){UseShellExecute=false,CreateNoWindow=true})){if(p!=null&&!p.WaitForExit(10000))try{p.Kill();}catch{}}}}
            if(refresh){MarkGuard();RuntimeUtil.Refresh("Calendar");if(refreshTodo)RuntimeUtil.Refresh("Todo");}else if(refreshTodo)RuntimeUtil.Refresh("Todo");return 0;
        }catch(Exception ex){if(cache!=null){cache["status"]="操作失败："+ex.Message;try{Save(CachePath,cache);Render(cache,state);RuntimeUtil.Refresh("Calendar");}catch{}}return 1;}finally{if(held)mutex.ReleaseMutex();}}
    }

}
