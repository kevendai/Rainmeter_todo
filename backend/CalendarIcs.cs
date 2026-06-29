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
    private static string IcsText(string v){return(v??"").Replace("\\","\\\\").Replace("\r\n","\\n").Replace("\n","\\n").Replace(";","\\;").Replace(",","\\,");}
    private static string IcsDateTime(DateTimeOffset value,bool allDay,bool end){return allDay?value.ToString("yyyyMMdd",CultureInfo.InvariantCulture):value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture);}
    private static string IcsDateProp(string name,DateTimeOffset value,bool allDay){return (allDay?name+";VALUE=DATE:":name+":")+IcsDateTime(value,allDay,false);}
    private static bool IsProp(string line,string name){return line.StartsWith(name+":",StringComparison.OrdinalIgnoreCase)||line.StartsWith(name+";",StringComparison.OrdinalIgnoreCase);}
    private static string DurationForReminder(int minutes){TimeSpan span=TimeSpan.FromMinutes(Math.Max(1,minutes));return "-P"+(span.Days>0?span.Days+"D":"")+(span.Hours>0||span.Minutes>0?"T":"")+(span.Hours>0?span.Hours+"H":"")+(span.Minutes>0?span.Minutes+"M":"");}
    private static List<string> AlarmLines(Dictionary<string,object> e){List<string> lines=new List<string>();foreach(int minutes in Reminders(e)){lines.Add("BEGIN:VALARM");lines.Add("ACTION:DISPLAY");lines.Add("DESCRIPTION:"+IcsText(S(e,"title")));lines.Add("TRIGGER:"+DurationForReminder(minutes));lines.Add("END:VALARM");}return lines;}
    private static void RemoveAlarmBlocks(List<string> lines){for(int i=0;i<lines.Count;){if(lines[i]=="BEGIN:VALARM"){int depth=1,j=i+1;for(;j<lines.Count;j++){if(lines[j]=="BEGIN:VALARM")depth++;else if(lines[j]=="END:VALARM"&&--depth==0){j++;break;}}lines.RemoveRange(i,Math.Max(1,j-i));}else i++;}}
    private static List<string> RawAlarmBlocks(List<string> block){List<string> result=new List<string>();for(int i=0;i<block.Count;){if(block[i]=="BEGIN:VALARM"){int depth=1,j=i+1;for(;j<block.Count;j++){if(block[j]=="BEGIN:VALARM")depth++;else if(block[j]=="END:VALARM"&&--depth==0){j++;break;}}result.Add(String.Join("\r\n",block.GetRange(i,Math.Max(1,j-i)))+"\r\n");i=j;}else i++;}return result;}
    private static bool AlarmIsStartOffset(string raw){return Regex.IsMatch(raw??"", @"(?im)^TRIGGER:-P(?:\d+D)?(?:T(?:\d+H)?(?:\d+M)?)?\s*$")&&!Regex.IsMatch(raw??"", @"(?im)^TRIGGER;");}
    private static List<string> CustomAlarms(Dictionary<string,object> e){return JsonUtil.Array(JsonUtil.Get(e,"custom_alarms")).Select(x=>Convert.ToString(x,CultureInfo.InvariantCulture)).Where(x=>!String.IsNullOrWhiteSpace(x)).ToList();}
    private static string UpsertProp(List<string> lines,string name,string value,int insertBefore)
    {
        for(int i=0;i<lines.Count;i++)if(IsProp(lines[i],name)){lines[i]=value;return value;}
        lines.Insert(Math.Max(0,insertBefore),value);return value;
    }
    private static string UpdateSeriesIcs(string raw,Dictionary<string,object> e)
    {
        string normalized=Regex.Replace(raw??"","\r?\n[ \t]","");List<string> lines=Regex.Split(normalized.TrimEnd(),"\r?\n").ToList();
        int begin=-1,end=-1,depth=0;for(int i=0;i<lines.Count;i++){if(lines[i]=="BEGIN:VEVENT"){if(depth==0)begin=i;depth++;}else if(lines[i]=="END:VEVENT"&&depth>0){depth--;if(depth==0&&begin>=0){bool recurrence=false;for(int j=begin+1;j<i;j++)if(IsProp(lines[j],"RECURRENCE-ID"))recurrence=true;if(!recurrence){end=i;break;}begin=-1;}}}
        if(begin<0||end<0)throw new Exception("未找到可改写的周期主日程。");
        List<string> block=lines.GetRange(begin+1,end-begin-1);DateTimeOffset start=RuntimeUtil.Date(e,"start_at")??DateTimeOffset.Now, finish=RuntimeUtil.Date(e,"end_at")??start.AddHours(1);bool all=B(e,"all_day");
        int insert=block.Count;UpsertProp(block,"DTSTAMP","DTSTAMP:"+DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture),insert);
        UpsertProp(block,"DTSTART",IcsDateProp("DTSTART",start,all),insert);
        UpsertProp(block,"DTEND",IcsDateProp("DTEND",finish,all),insert);
        UpsertProp(block,"SUMMARY","SUMMARY:"+IcsText(S(e,"title")),insert);
        if(S(e,"location")!="")UpsertProp(block,"LOCATION","LOCATION:"+IcsText(S(e,"location")),insert);else block.RemoveAll(l=>IsProp(l,"LOCATION"));
        if(S(e,"description")!="")UpsertProp(block,"DESCRIPTION","DESCRIPTION:"+IcsText(S(e,"description")),insert);else block.RemoveAll(l=>IsProp(l,"DESCRIPTION"));
        string link = S(e, "url");
        if(IsLocalPath(link)){block.RemoveAll(l=>IsProp(l,"URL"));UpsertProp(block,"X-RAINMETER-LINK","X-RAINMETER-LINK:"+IcsText(link),insert);}
        else {block.RemoveAll(l=>IsProp(l,"X-RAINMETER-LINK"));if(IsWebLink(link))UpsertProp(block,"URL","URL:"+IcsText(link),insert);else block.RemoveAll(l=>IsProp(l,"URL"));}
        RemoveAlarmBlocks(block);block.AddRange(CustomAlarms(e).SelectMany(x=>Regex.Split(x.TrimEnd(),"\r?\n")));block.AddRange(AlarmLines(e));
        lines.RemoveRange(begin+1,end-begin-1);lines.InsertRange(begin+1,block);return String.Join("\r\n",lines)+"\r\n";
    }
    private static string EventIcs(Dictionary<string,object> e)
    {
        DateTimeOffset start=RuntimeUtil.Date(e,"start_at")??DateTimeOffset.Now,end=RuntimeUtil.Date(e,"end_at")??start.AddHours(1);bool all=B(e,"all_day");
        StringBuilder b=new StringBuilder();b.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Rainmeter Calendar//CN\r\nCALSCALE:GREGORIAN\r\nBEGIN:VEVENT\r\n");
        b.Append("UID:").Append(S(e,"uid")).Append("\r\nDTSTAMP:").Append(DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture)).Append("\r\n");
        b.Append(all?"DTSTART;VALUE=DATE:":"DTSTART:").Append(IcsDateTime(start,all,false)).Append("\r\n");
        b.Append(all?"DTEND;VALUE=DATE:":"DTEND:").Append(IcsDateTime(end,all,true)).Append("\r\n");
        b.Append("SUMMARY:").Append(IcsText(S(e,"title"))).Append("\r\n");
        if(S(e,"location")!="")b.Append("LOCATION:").Append(IcsText(S(e,"location"))).Append("\r\n");
        if(S(e,"description")!="")b.Append("DESCRIPTION:").Append(IcsText(S(e,"description"))).Append("\r\n");
        string link=S(e,"url");
        if(IsLocalPath(link))b.Append("X-RAINMETER-LINK:").Append(IcsText(link)).Append("\r\n");
        else if(IsWebLink(link))b.Append("URL:").Append(IcsText(link)).Append("\r\n");
        foreach(string raw in CustomAlarms(e))b.Append(raw.TrimEnd()).Append("\r\n");
        foreach(string line in AlarmLines(e))b.Append(line).Append("\r\n");
        b.Append("END:VEVENT\r\nEND:VCALENDAR\r\n");return b.ToString();
    }

    private static Dictionary<string,object> DraftEvent(Dictionary<string,object> original,string source,string title,DateTimeOffset start,DateTimeOffset end,bool allDay,string location,string url,string description,List<int> reminders,List<string> customAlarms)
    {
        string uid=original==null||S(original,"uid")==""?Guid.NewGuid().ToString("N")+"@rainmeter.local":S(original,"uid");
        string key=uid+"|"+start.ToUniversalTime().ToString("o");
        Dictionary<string,object> e=new Dictionary<string,object>{{"id",original==null?RuntimeUtil.Sha256Hex(key).Substring(0,32):S(original,"id")},{"occurrence_key",key},{"uid",uid},{"recurrence_id",start.ToUniversalTime().ToString("o")},{"title",title==""?"（无标题）":title},{"start_at",RuntimeUtil.Iso(start)},{"end_at",RuntimeUtil.Iso(end)},{"all_day",allDay},{"url",url},{"location",location},{"description",description},{"status",""},{"reminder_at",""},{"reminder_count",0},{"reminders",new List<object>()},{"recurring",original!=null&&B(original,"recurring")},{"source",source},{"calendar",source}};
        e["custom_alarms"]=customAlarms.Cast<object>().ToList();ApplyReminders(e,reminders,start);if(customAlarms.Count>0&&original!=null){DateTimeOffset? oldReminder=RuntimeUtil.Date(original,"reminder_at"),newReminder=RuntimeUtil.Date(e,"reminder_at");if(oldReminder.HasValue&&(!newReminder.HasValue||oldReminder.Value<newReminder.Value))e["reminder_at"]=RuntimeUtil.Iso(oldReminder.Value);}e["reminder_count"]=Reminders(e).Count+CustomAlarms(e).Count;
        if(original!=null){if(S(original,"href")!="")e["href"]=S(original,"href");if(S(original,"etag")!="")e["etag"]=S(original,"etag");}
        return e;
    }

}
