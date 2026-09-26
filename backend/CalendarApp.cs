using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
        if(args.Length>0&&args[0]=="UiSaveSelfTest")return UiSaveSelfTest();
        UiScale.EnableDpiAwareness();
        string action=args.Length>0?args[0]:"Render",id=args.Length>1?args[1]:"";
        if(action=="UiServerModel"||action=="UiServerSave"||action=="UiServerTest"||action=="UiServerClear")
            return HandleServerUi(action,id,args.Length>2?args[2]:"");
        if(action=="New"||action=="Edit"||action=="Detail"||action=="Manage"||action=="Settings"||action=="Convert")
            return DesktopUiBridge.TryOpen(TodoDir,"calendar",action,id)?0:5;
        using(Mutex mutex=new Mutex(false,@"Global\RainmeterCalendarState")){bool held=false;Dictionary<string,object> cache=null,state=null;try{
            held=mutex.WaitOne(TimeSpan.FromSeconds(20));if(!held)return 4;cache=Load(CachePath,NewCache());state=Load(StatePath,NewState());Shape(cache,state);if(Reconcile(state))Save(StatePath,state);bool refresh=false,refreshTodo=false;
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
            else if(action=="UiConvert"){
                string resultPath=id+".result.json";
                try{
                    Dictionary<string,object> input=JsonUtil.LoadObject(id);
                    Dictionary<string,object> ev=FindEvent(cache,state,JsonUtil.String(input,"id",""));
                    if(ev==null)throw new Exception("日程已不存在，请刷新后重试。");
                    bool added=ConvertWithoutUi(ev,state,cache,JsonUtil.String(input,"mode","once"),JsonUtil.Bool(input,"hide",true));
                    Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;refreshTodo=added;
                    JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",true},{"message",S(cache,"status")}});
                }catch(Exception uiError){JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",false},{"error",uiError.Message}});throw;}
            }
            else if(action=="UiConversionModel"){
                try{JsonUtil.SaveAtomic(id,ConversionModel(cache,state));}
                catch(Exception uiError){JsonUtil.SaveAtomic(id,new Dictionary<string,object>{{"ok",false},{"error",uiError.Message}});throw;}
            }
            else if(action=="UiConversionCancel"){
                string resultPath=args.Length>2?args[2]:"";
                try{
                    Dictionary<string,object> request=JsonUtil.LoadObject(id);
                    string key=JsonUtil.String(request,"occurrence_key","");
                    if(key=="")throw new InvalidDataException("缺少日程转换标识。");
                    CancelConversion(state,key,JsonUtil.String(request,"cancel_mode","once"));
                    Save(StatePath,state);Render(cache,state);refresh=true;
                    JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",true}});
                }catch(Exception uiError){JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",false},{"error",uiError.Message}});throw;}
            }
            else if(action=="UiDelete"){
                string resultPath=id+".result.json";
                try{
                    Dictionary<string,object> input=JsonUtil.LoadObject(id);
                    Dictionary<string,object> ev=FindEvent(cache,state,JsonUtil.String(input,"id",""));
                    if(ev==null)throw new Exception("日程已不存在，请刷新后重试。");
                    string mode=JsonUtil.String(input,"mode","series");
                    if(mode!="once"&&mode!="series")throw new InvalidDataException("删除范围无效");
                    if(S(ev,"source")=="caldav")DeleteCalDavEvent(ev,cache,state,mode);
                    else DeleteLocalEvent(ev,state,mode);
                    Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;
                    JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",true}});
                }catch(Exception uiError){JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",false},{"error",uiError.Message}});throw;}
            }
            else if(action=="Open"){Dictionary<string,object> ev=FindEvent(cache,state,id);if(ev!=null)RuntimeUtil.Run(Target(ev));}
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
        bool recurring=original!=null&&B(original,"recurring");
        if(recurring&&S(original,"source")=="caldav"){
            Dictionary<string,object> master=JsonUtil.Object(JsonUtil.Get(original,"series_event"));
            if(master.Count>0){Dictionary<string,object> edit=new Dictionary<string,object>(master);
                foreach(string key in new[]{"href","etag","source","calendar","recurrence_preserve","series_event"})
                    if(JsonUtil.Get(original,key)!=null)edit[key]=JsonUtil.Get(original,key);
                edit["recurring"]=true;original=edit;}
        }else if(recurring){Dictionary<string,object> master=LocalSeriesMaster(state,original);if(master!=null)original=master;}
        if(source!="local"&&source!="caldav")throw new Exception("日程来源无效。");
        if(original!=null&&S(original,"source")!=source)throw new Exception("当前不支持更改已有日程来源。");
        string title=JsonUtil.String(input,"title","").Trim();
        if(title=="")throw new Exception("标题不能为空。");
        DateTimeOffset start,end;
        if(!DateTimeOffset.TryParse(JsonUtil.String(input,"start_at",""),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out start)
            ||!DateTimeOffset.TryParse(JsonUtil.String(input,"end_at",""),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out end)
            ||end<=start)throw new Exception("日程开始和结束时间无效。");
        bool allDay=JsonUtil.Bool(input,"all_day",false);
        RecurrenceSpec recurrence=RecurrenceFromEvent(original);
        Dictionary<string,object> recurrenceInput=JsonUtil.Object(JsonUtil.Get(input,"recurrence"));
        string frequency=JsonUtil.String(recurrenceInput,"frequency",recurrence.Frequency).ToLowerInvariant();
        if(!new[]{"none","daily","weekly","monthly","yearly","preserve"}.Contains(frequency))throw new Exception("重复频率无效。");
        if(frequency!=recurrence.Frequency&&recurrence.Preserve)throw new Exception("复杂周期只能保持原规则。");
        if(frequency!=recurrence.Frequency)recurrence.Changed=true;
        recurrence.Frequency=frequency;
        recurrence.Preserve=frequency=="preserve";
        int interval=JsonUtil.Int(recurrenceInput,"interval",recurrence.Interval);
        if(interval<1||interval>365)throw new Exception("重复间隔无效。");
        if(interval!=recurrence.Interval)recurrence.Changed=true;
        recurrence.Interval=interval;
        if(frequency=="weekly"){
            List<int> days=new List<int>();foreach(object item in JsonUtil.Array(JsonUtil.Get(recurrenceInput,"weekdays"))){int day;
                if(Int32.TryParse(Convert.ToString(item,CultureInfo.InvariantCulture),out day)&&day>=1&&day<=7&&!days.Contains(day))days.Add(day);}
            if(days.Count==0)days.Add(WeekdayNumber(start.DayOfWeek));
            if(!days.OrderBy(x=>x).SequenceEqual(recurrence.Weekdays.OrderBy(x=>x)))recurrence.Changed=true;
            recurrence.Weekdays=days;
        }
        string endMode=JsonUtil.String(recurrenceInput,"end_mode",recurrence.EndMode);
        if(!new[]{"never","until","count"}.Contains(endMode))throw new Exception("重复结束方式无效。");
        if(endMode!=recurrence.EndMode)recurrence.Changed=true;recurrence.EndMode=endMode;
        if(endMode=="count"){
            int count=JsonUtil.Int(recurrenceInput,"count",recurrence.Count);if(count<1||count>10000)throw new Exception("重复次数无效。");
            if(count!=recurrence.Count)recurrence.Changed=true;recurrence.Count=count;
        }else if(endMode=="until"){
            string rawUntil=JsonUtil.String(recurrenceInput,"until","");
            DateTime until=ParseUntilDate(rawUntil,DateTime.MinValue);
            if(until==DateTime.MinValue&& !DateTime.TryParse(rawUntil,out until) || until.Date<start.Date)
                throw new Exception("周期结束日期不能早于开始日期。");
            if(until.Date!=recurrence.Until.Date)recurrence.Changed=true;recurrence.Until=until.Date;
        }
        if(frequency=="monthly")recurrence.MonthDay=start.Day;
        if(frequency=="yearly"){recurrence.Month=start.Month;recurrence.MonthDay=start.Day;}
        List<int> reminders=new List<int>();foreach(object item in JsonUtil.Array(JsonUtil.Get(input,"reminders"))){int minute;
            if(Int32.TryParse(Convert.ToString(item,CultureInfo.InvariantCulture),out minute)&&minute>0&&minute<=525600&&!reminders.Contains(minute))reminders.Add(minute);}
        if(!input.ContainsKey("reminders")&&original!=null)reminders=Reminders(original);
        bool timingChanged=original!=null&&(RuntimeUtil.Date(original,"start_at")!=start||RuntimeUtil.Date(original,"end_at")!=end||B(original,"all_day")!=allDay);
        if(recurrence.Preserve&&timingChanged)throw new Exception("复杂周期的日期与时间已锁定，只能修改内容和提醒。");
        if(recurring&&S(original,"source")=="local"&&(timingChanged||recurrence.Changed)){
            string uid=S(original,"uid");bool tracked=Conversions(state).Any(item=>S(item,"uid")==uid)
                ||Rules(state).Any(item=>S(item,"uid")==uid&&!JsonUtil.Bool(item,"disabled",false))
                ||HiddenEvents(state).Any(item=>S(item,"uid")==uid||S(item,"occurrence_key").StartsWith(uid+"|",StringComparison.Ordinal));
            if(tracked)throw new Exception("此周期日程已有隐藏、待办转换或自动转入记录，不能修改周期或起止时间。");
        }
        AlignRecurrenceStart(recurrence,ref start,ref end);
        Dictionary<string,object> draft=DraftEvent(original,source,title,start,end,
            allDay,JsonUtil.String(input,"location",""),
            JsonUtil.String(input,"url",""),JsonUtil.String(input,"description",""),
            reminders,
            original==null?new List<string>():CustomAlarms(original),
            recurrence);
        draft["time_changed"]=timingChanged;
        if(recurring&&timingChanged&&!recurrence.Preserve){recurrence.Changed=true;draft["rrule"]=BuildRecurrenceRule(recurrence,start,allDay);draft["rrule_changed"]=true;}
        if(source=="caldav") {if(recurring)SaveCalDavSeriesEvent(draft,cache);else SaveCalDavEvent(draft,cache);}
        else{SaveLocalEvent(draft,state);cache["status"]="已保存到本地日历";}
    }

    private static int UiSaveSelfTest()
    {
        string path=Path.Combine(Path.GetTempPath(),"calendar-ui-save-"+Guid.NewGuid().ToString("N")+".json");
        try{
            Dictionary<string,object> state=NewState(),cache=NewCache();
            Dictionary<string,object> recurrence=new Dictionary<string,object>{{"frequency","weekly"},{"interval",2},
                {"weekdays",new List<object>{1,3}},{"end_mode","count"},{"count",8}};
            Dictionary<string,object> input=new Dictionary<string,object>{{"source","local"},{"title","周期测试"},
                {"start_at","2026-09-28T09:00:00+08:00"},{"end_at","2026-09-28T10:00:00+08:00"},
                {"reminders",new List<object>{15,60}},{"recurrence",recurrence}};
            JsonUtil.SaveAtomic(path,input);SaveFromUi(path,state,cache);
            Dictionary<string,object> saved=LocalEvents(state).Single();
            if(S(saved,"rrule")!="FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=8"||!Reminders(saved).SequenceEqual(new[]{15,60}))
                throw new Exception("新建周期或提醒未正确保存");
            input["id"]=S(saved,"id");input["title"]="周期测试更新";
            JsonUtil.SaveAtomic(path,input);SaveFromUi(path,state,cache);
            saved=LocalEvents(state).Single();
            if(S(saved,"title")!="周期测试更新"||S(saved,"rrule")!="FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=8")
                throw new Exception("编辑周期日程丢失规则");
            Console.WriteLine("PASS Calendar UiSave: weekly recurrence, reminders and series edit");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{try{File.Delete(path);}catch{}}
    }

}
