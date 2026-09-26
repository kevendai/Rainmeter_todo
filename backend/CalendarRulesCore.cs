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
using System.Xml;
using RainmeterBackend;


internal static partial class CalendarApp
{
    private static string CleanTitle(string t){return Regex.Replace(t??"",@"^\s*\[(?:待办|代办)\]\s*","").Trim();}
    private static bool IsLocalPath(string value){return Regex.IsMatch((value??"").Trim(),@"^(?:[A-Za-z]:[\\/]|\\\\)[^\r\n<>""|?*]+$");}
    private static string TrimTarget(string value){return (value??"").Trim().TrimEnd(')',']','}','，','。','；',';');}
    private static string DisplayLink(string value){Uri uri;if(Uri.TryCreate((value??"").Trim(),UriKind.Absolute,out uri)&&uri.IsFile)return uri.LocalPath;return value??"";}
    private static bool IsWebLink(string value){Uri uri;return Uri.TryCreate((value??"").Trim(),UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https"||uri.Scheme=="wemeet");}
    private static string Target(Dictionary<string,object>e){string direct=TrimTarget(S(e,"url"));if(direct!=""){if(IsLocalPath(direct))return direct;Uri uri;if(Uri.TryCreate(direct,UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https"||uri.Scheme=="wemeet"||uri.IsFile))return uri.IsFile?uri.LocalPath:direct;}foreach(string k in new[]{"location","description"}){string text=S(e,k);Match m=Regex.Match(text,@"(?i)(?:https?://|wemeet://|file:///)[^\s<>\""'，。；;]+");if(m.Success)return DisplayLink(TrimTarget(m.Value));m=Regex.Match(text,@"(?i)(?:[A-Z]:\\|\\\\)[^\r\n<>""|?*]+");if(m.Success)return TrimTarget(m.Value);}return "";}
    private static string FullTime(Dictionary<string,object>e){DateTimeOffset?start=RuntimeUtil.Date(e,"start_at"),end=RuntimeUtil.Date(e,"end_at");if(!start.HasValue)return"";if(B(e,"all_day")){DateTimeOffset last=end.HasValue?end.Value.AddDays(-1):start.Value;return last.Date==start.Value.Date?start.Value.ToString("yyyy年M月d日 全天"):start.Value.ToString("yyyy年M月d日")+"–"+last.ToString("yyyy年M月d日")+" 全天";}if(!end.HasValue||end<=start)return start.Value.ToString("yyyy年M月d日 HH:mm");return end.Value.Date==start.Value.Date?start.Value.ToString("yyyy年M月d日 HH:mm")+"–"+end.Value.ToString("HH:mm"):start.Value.ToString("yyyy年M月d日 HH:mm")+" → "+end.Value.ToString("yyyy年M月d日 HH:mm");}
    private static bool AddTask(Dictionary<string,object>e,Dictionary<string,object>state,string mode,bool hide)
    {
        string pluginHost=Path.Combine(TodoDir,"PluginHost.exe");
        if(!File.Exists(pluginHost))throw new Exception("未找到 PluginHost.exe");
        string token=Guid.NewGuid().ToString("N"),input=Path.Combine(Path.GetTempPath(),"rw-calendar-"+token+".json"),output=Path.Combine(Path.GetTempPath(),"rw-calendar-"+token+".result.json");
        try
        {
            JsonUtil.SaveAtomic(input,e);
            using(Process p=Process.Start(new ProcessStartInfo(pluginHost,"Transform io.github.kevendai.calendar-to-todo "+Quote(input)+" "+Quote(output)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))
            {
                if(p==null||!p.WaitForExit(45000)){try{if(p!=null)p.Kill();}catch{}throw new Exception("日程转换插件执行超时");}
                if(p.ExitCode!=0||!File.Exists(output))throw new Exception("日程转换插件执行失败");
            }
            Dictionary<string,object> response=JsonUtil.LoadObject(output);
            if(!JsonUtil.Bool(response,"ok",false))throw new Exception(JsonUtil.String(response,"error","日程转换失败"));
            Dictionary<string,object> imported=JsonUtil.Object(JsonUtil.Get(response,"import"));
            string taskId=JsonUtil.String(imported,"task_id","");
            if(taskId=="")throw new Exception("日程转换未返回待办 ID");
            Conversions(state).RemoveAll(c=>S(c,"occurrence_key")==S(e,"occurrence_key"));
            Conversions(state).Add(Conversion(e,new Dictionary<string,object>{{"id",taskId}},mode,hide));
            return JsonUtil.Int(imported,"created",0)>0;
        }
        finally{try{File.Delete(input);}catch{}try{File.Delete(output);}catch{}}
    }
    private static string Quote(string value){return "\""+value.Replace("\"","\\\"")+"\"";}
    private static Dictionary<string,object> Conversion(Dictionary<string,object>e,Dictionary<string,object>task,string mode,bool hide){return new Dictionary<string,object>{{"occurrence_key",S(e,"occurrence_key")},{"uid",S(e,"uid")},{"recurrence_id",S(e,"recurrence_id")},{"task_id",S(task,"id")},{"converted_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"mode",mode},{"hide_event",hide}};}
    private static bool OccursOn(Dictionary<string,object>e,DateTime date){DateTimeOffset?start=RuntimeUtil.Date(e,"start_at"),end=RuntimeUtil.Date(e,"end_at");if(!start.HasValue||!end.HasValue)return false;DateTimeOffset ds=new DateTimeOffset(date,TimeZoneInfo.Local.GetUtcOffset(date)),de=ds.AddDays(1);return start.Value<de&&end.Value>ds;}
    private static bool AutoConvertDue(Dictionary<string,object> e,DateTime today){DateTimeOffset? reminder=RuntimeUtil.Date(e,"reminder_at");return OccursOn(e,today)||(reminder.HasValue&&reminder.Value.Date<=today);}
    private static List<string> SkippedOccurrences(Dictionary<string,object> rule){return JsonUtil.Array(JsonUtil.Get(rule,"skipped_occurrences")).Select(Convert.ToString).Where(x=>!String.IsNullOrEmpty(x)).ToList();}
    private static bool IsSkipped(Dictionary<string,object> rule,string occurrenceKey){return rule!=null&&SkippedOccurrences(rule).Contains(occurrenceKey,StringComparer.Ordinal);}
    private static bool IsUpcomingOccurrence(Dictionary<string,object> e,DateTimeOffset now){DateTimeOffset?start=RuntimeUtil.Date(e,"start_at");return start.HasValue&&(B(e,"all_day")?start.Value.Date>=now.Date:start.Value>=now);}
    private static bool AutoConvert(Dictionary<string,object>cache,Dictionary<string,object>state){bool changed=false;DateTime today=DateTime.Now.Date;foreach(Dictionary<string,object>e in AllEvents(cache,state).Where(e=>AutoConvertDue(e,today))){Dictionary<string,object>rule=Rules(state).FirstOrDefault(r=>S(r,"uid")==S(e,"uid"));if(Regex.IsMatch(S(e,"title"),@"^\s*\[(?:待办|代办)\]")&&rule==null){rule=new Dictionary<string,object>{{"uid",S(e,"uid")},{"title",CleanTitle(S(e,"title"))},{"effective_from",S(e,"start_at")},{"created_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"reason","title-tag"},{"hide_event",true}};Rules(state).Add(rule);changed=true;}if(rule!=null&&!JsonUtil.Bool(rule,"disabled",false)&&!IsSkipped(rule,S(e,"occurrence_key"))&&AddTask(e,state,"series",JsonUtil.Bool(rule,"hide_event",true)))changed=true;}return changed;}
    private static bool NeedsSeriesRuleRestore(Dictionary<string,object>e,Dictionary<string,object>state){return B(e,"recurring")&&Conversions(state).Any(c=>S(c,"occurrence_key")==S(e,"occurrence_key"))&&!Rules(state).Any(r=>S(r,"uid")==S(e,"uid")&&!JsonUtil.Bool(r,"disabled",false));}
    private static bool ConvertWithoutUi(Dictionary<string,object> e,Dictionary<string,object> state,Dictionary<string,object> cache,string mode,bool hide)
    {
        if(mode!="once"&&mode!="series")throw new InvalidDataException("转换范围无效");
        if(mode=="series"&&!B(e,"recurring"))throw new InvalidDataException("非周期日程不能设置未来自动转入");
        bool restoreOnly=NeedsSeriesRuleRestore(e,state);
        if(restoreOnly&&mode!="series")throw new InvalidDataException("此日程只能恢复未来自动转入");
        if(mode=="series")
        {
            Dictionary<string,object> rule=Rules(state).FirstOrDefault(r=>S(r,"uid")==S(e,"uid"));
            if(rule==null){rule=new Dictionary<string,object>{{"uid",S(e,"uid")},{"title",CleanTitle(S(e,"title"))},{"effective_from",S(e,"start_at")},{"created_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"reason","manual"}};Rules(state).Add(rule);}
            rule["hide_event"]=hide;rule["disabled"]=false;
        }
        bool added=AddTask(e,state,mode,hide);
        cache["status"]=restoreOnly?"已恢复该系列的未来自动转入":added?(hide?"已转为待办并从日程隐藏":"已转为待办，日程继续显示"):"待办已存在，未重复创建";
        return added;
    }

    private static Dictionary<string,object> ConversionModel(Dictionary<string,object> cache,Dictionary<string,object> state)
    {
        List<object> rows=new List<object>();
        List<Dictionary<string,object>> events=AllEvents(cache,state).ToList();
        Dictionary<string,Dictionary<string,object>> todoTasks=QueryConversionTasks(Conversions(state));
        DateTimeOffset now=DateTimeOffset.Now;
        List<Dictionary<string,object>> conversions=Conversions(state);
        foreach(Dictionary<string,object> conversion in conversions.Where(c=>S(c,"mode")!="series").OrderByDescending(c=>S(c,"converted_at")))
        {
            string key=S(conversion,"occurrence_key");
            Dictionary<string,object> ev=events.FirstOrDefault(e=>S(e,"occurrence_key")==key);
            if(ev!=null&&!IsUpcomingOccurrence(ev,now))continue;
            if(ev==null){Dictionary<string,object> task;todoTasks.TryGetValue(S(conversion,"task_id"),out task);DateTimeOffset taskDate;if(!DateTimeOffset.TryParse(S(task,"available_from"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out taskDate)||taskDate.Date<now.Date)continue;}
            Dictionary<string,object> todoTask;
            todoTasks.TryGetValue(S(conversion,"task_id"),out todoTask);
            string title=ev==null?JsonUtil.String(todoTask,"title",""):CleanTitle(S(ev,"title"));
            if(title.StartsWith("（日程）",StringComparison.Ordinal))title=title.Substring("（日程）".Length);
            if(title.StartsWith("(日程)",StringComparison.Ordinal))title=title.Substring("(日程)".Length);
            string date=ev==null?ScheduleTimeFromTask(todoTask):FullTime(ev);
            rows.Add(new Dictionary<string,object>{
                {"occurrence_key",key},{"uid",S(conversion,"uid")},{"task_id",S(conversion,"task_id")},
                {"title",title==""?"日程信息暂不可用":title},{"date",date},
                {"mode",S(conversion,"mode")},{"series",S(conversion,"mode")=="series"},
                {"hide_event",JsonUtil.Bool(conversion,"hide_event",true)},
                {"source",ev==null?"":S(ev,"source")}
            });
        }
        foreach(Dictionary<string,object> rule in Rules(state).Where(r=>S(r,"uid")!=""&&!JsonUtil.Bool(r,"disabled",false)))
        {
            string uid=S(rule,"uid");
            Dictionary<string,object> next=events.Where(e=>S(e,"uid")==uid&&B(e,"recurring")&&IsUpcomingOccurrence(e,now)&&!IsSkipped(rule,S(e,"occurrence_key")))
                .OrderBy(e=>RuntimeUtil.Date(e,"start_at")).FirstOrDefault();
            if(next==null)
            {
                Dictionary<string,object> sample=events.FirstOrDefault(e=>S(e,"uid")==uid);
                Dictionary<string,object> master=sample==null?null:JsonUtil.Object(JsonUtil.Get(sample,"series_event"));
                if(master!=null&&S(master,"rrule")!="")
                    next=ExpandLocalSeries(master).Where(e=>IsUpcomingOccurrence(e,now)&&!IsSkipped(rule,S(e,"occurrence_key"))).OrderBy(e=>RuntimeUtil.Date(e,"start_at")).FirstOrDefault();
            }
            if(next==null)continue;
            string key=S(next,"occurrence_key");
            Dictionary<string,object> existing=conversions.FirstOrDefault(c=>S(c,"occurrence_key")==key&&S(c,"mode")=="series");
            Dictionary<string,object> task=null;
            if(existing!=null)todoTasks.TryGetValue(S(existing,"task_id"),out task);
            string title=CleanTitle(S(next,"title"));
            if(title.StartsWith("（日程）",StringComparison.Ordinal))title=title.Substring("（日程）".Length);
            if(title.StartsWith("(日程)",StringComparison.Ordinal))title=title.Substring("(日程)".Length);
            rows.Add(new Dictionary<string,object>{
                {"occurrence_key",key},{"uid",uid},{"task_id",existing==null?"":S(existing,"task_id")},
                {"title",title==""?JsonUtil.String(task,"title","日程信息暂不可用"):title},{"date",FullTime(next)},
                {"mode","series"},{"series",true},{"hide_event",existing==null?JsonUtil.Bool(rule,"hide_event",true):JsonUtil.Bool(existing,"hide_event",true)},
                {"source",S(next,"source")}
            });
        }
        rows=rows.OrderBy(r=>S(JsonUtil.Object(r),"date"),StringComparer.Ordinal).Cast<object>().ToList();
        return new Dictionary<string,object>{{"ok",true},{"conversions",rows}};
    }

    private static Dictionary<string,Dictionary<string,object>> QueryConversionTasks(List<Dictionary<string,object>> conversions)
    {
        Dictionary<string,Dictionary<string,object>> result=new Dictionary<string,Dictionary<string,object>>(StringComparer.OrdinalIgnoreCase);
        string host=Path.Combine(TodoDir,"PluginHost.exe");
        if(conversions.Count==0||!File.Exists(host))return result;
        string token=Guid.NewGuid().ToString("N"),input=Path.Combine(Path.GetTempPath(),"rw-query-conversions-"+token+".json"),output=Path.Combine(Path.GetTempPath(),"rw-query-conversions-"+token+".result.json");
        try
        {
            JsonUtil.SaveAtomic(input,new Dictionary<string,object>{{"task_ids",conversions.Select(c=>(object)S(c,"task_id")).ToList()}});
            using(Process p=Process.Start(new ProcessStartInfo(host,"QueryTasks "+Quote(input)+" "+Quote(output)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))
                if(p==null||!p.WaitForExit(30000)||p.ExitCode!=0)return result;
            Dictionary<string,object> response=JsonUtil.LoadObject(output);
            foreach(object item in JsonUtil.Array(JsonUtil.Get(response,"tasks")))
            {
                Dictionary<string,object> task=JsonUtil.Object(item);string id=S(task,"id");if(id!="")result[id]=task;
            }
        }
        catch{}
        finally{try{File.Delete(input);}catch{}try{File.Delete(output);}catch{}}
        return result;
    }

    private static string ScheduleTimeFromTask(Dictionary<string,object> task)
    {
        if(task==null)return "";
        Match match=Regex.Match(S(task,"note"),@"(?:^|\r?\n)日程时间：([^\r\n]+)");
        if(match.Success)return match.Groups[1].Value.Trim();
        DateTimeOffset start,end;
        bool hasStart=DateTimeOffset.TryParse(S(task,"available_from"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out start);
        bool hasEnd=DateTimeOffset.TryParse(S(task,"due_at"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out end);
        if(hasStart&&hasEnd)return start.ToLocalTime().ToString("yyyy年M月d日 HH:mm")+"–"+end.ToLocalTime().ToString("HH:mm");
        if(hasStart)return start.ToLocalTime().ToString("yyyy年M月d日 HH:mm");
        return hasEnd?end.ToLocalTime().ToString("yyyy年M月d日 HH:mm"):"";
    }

    private static void CancelConversion(Dictionary<string,object> state,string occurrenceKey,string cancelMode)
    {
        List<Dictionary<string,object>> conversions=Conversions(state);
        Dictionary<string,object> selected=conversions.FirstOrDefault(c=>S(c,"occurrence_key")==occurrenceKey);
        string uid=selected==null?"":S(selected,"uid");
        if(uid==""&&occurrenceKey.Contains("|"))uid=occurrenceKey.Substring(0,occurrenceKey.IndexOf('|'));
        Dictionary<string,object> rule=Rules(state).FirstOrDefault(r=>S(r,"uid")==uid);
        bool series=(selected!=null&&S(selected,"mode")=="series")||(rule!=null&&!JsonUtil.Bool(rule,"disabled",false));
        if(series&&cancelMode=="once")
        {
            if(rule==null)throw new InvalidDataException("周期转入规则已不存在，请刷新后重试。");
            List<string> skipped=SkippedOccurrences(rule);
            if(!skipped.Contains(occurrenceKey,StringComparer.Ordinal))skipped.Add(occurrenceKey);
            rule["skipped_occurrences"]=skipped;
            conversions.RemoveAll(c=>S(c,"occurrence_key")==occurrenceKey&&S(c,"mode")=="series");
        }
        else if(series&&cancelMode=="future")
        {
            conversions.RemoveAll(c=>S(c,"uid")==uid&&S(c,"mode")=="series");
            if(rule==null)Rules(state).Add(new Dictionary<string,object>{{"uid",uid},{"disabled",true},{"reason","user-cancelled"}});
            else rule["disabled"]=true;
        }
        else if(selected!=null&&S(selected,"mode")!="series")conversions.Remove(selected);
        else throw new InvalidDataException("这条日程转换记录已不存在，请刷新后重试。");
    }
}
