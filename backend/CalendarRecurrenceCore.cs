using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RainmeterBackend;


internal static partial class CalendarApp
{
    private sealed class RecurrenceSpec
    {
        public string Frequency = "none";
        public int Interval = 1;
        public List<int> Weekdays = new List<int>();
        public int Month;
        public int MonthDay;
        public string EndMode = "never";
        public DateTime Until = DateTime.Today.AddMonths(1);
        public int Count = 10;
        public bool Changed;
        public bool Preserve;
    }

    private static RecurrenceSpec CopyRecurrence(RecurrenceSpec value)
    {
        return new RecurrenceSpec {
            Frequency=value.Frequency, Interval=value.Interval,
            Weekdays=new List<int>(value.Weekdays), Month=value.Month, MonthDay=value.MonthDay, EndMode=value.EndMode,
            Until=value.Until, Count=value.Count, Changed=value.Changed,
            Preserve=value.Preserve
        };
    }

    private static bool SameRecurrence(RecurrenceSpec a,RecurrenceSpec b)
    {
        if(a==null||b==null)return a==b;
        return a.Frequency==b.Frequency&&a.Interval==b.Interval&&a.Month==b.Month&&a.MonthDay==b.MonthDay&&a.EndMode==b.EndMode&&a.Until.Date==b.Until.Date&&a.Count==b.Count&&a.Preserve==b.Preserve&&a.Weekdays.OrderBy(x=>x).SequenceEqual(b.Weekdays.OrderBy(x=>x));
    }

    private static int WeekdayNumber(DayOfWeek value){return value==DayOfWeek.Sunday?7:(int)value;}
    private static string WeekdayCode(int value){return new[]{"","MO","TU","WE","TH","FR","SA","SU"}[Math.Max(1,Math.Min(7,value))];}
    private static string WeekdayName(int value){return new[]{"","周一","周二","周三","周四","周五","周六","周日"}[Math.Max(1,Math.Min(7,value))];}

    private static Dictionary<string,string> RRuleParts(string value)
    {
        Dictionary<string,string> result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string part in (value??"").Split(';')){int eq=part.IndexOf('=');if(eq>0)result[part.Substring(0,eq).Trim()]=part.Substring(eq+1).Trim();}
        return result;
    }

    private static DateTime ParseUntilDate(string value,DateTime fallback)
    {
        DateTime parsed;
        if(Regex.IsMatch(value??"",@"^\d{8}$")&&DateTime.TryParseExact(value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))return parsed.Date;
        if(Regex.IsMatch(value??"",@"^\d{8}T\d{6}Z$")&&DateTime.TryParseExact(value,"yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out parsed))return parsed.ToLocalTime().Date;
        return fallback.Date;
    }

    private static RecurrenceSpec RecurrenceFromEvent(Dictionary<string,object> e)
    {
        RecurrenceSpec spec=new RecurrenceSpec();
        DateTimeOffset start=e==null?DateTimeOffset.Now:RuntimeUtil.Date(e,"series_start_at")??RuntimeUtil.Date(e,"start_at")??DateTimeOffset.Now;
        spec.Until=start.Date.AddMonths(1);
        if(e!=null&&B(e,"recurrence_preserve")){spec.Frequency="preserve";spec.Preserve=true;return spec;}
        string raw=e==null?"":S(e,"rrule");
        if(raw==""){
            if(e!=null&&B(e,"recurring")){spec.Frequency="preserve";spec.Preserve=true;}
            return spec;
        }
        string[] rawParts=raw.Split(';');if(rawParts.Any(part=>part.IndexOf('=')<=0||part.IndexOf('=')!=part.LastIndexOf('='))){spec.Frequency="preserve";spec.Preserve=true;return spec;}
        Dictionary<string,string> parts=RRuleParts(raw);if(parts.Count!=rawParts.Length){spec.Frequency="preserve";spec.Preserve=true;return spec;}string frequency;parts.TryGetValue("FREQ",out frequency);frequency=(frequency??"").ToLowerInvariant();
        if(!new[]{"daily","weekly","monthly","yearly"}.Contains(frequency)){spec.Frequency="preserve";spec.Preserve=true;return spec;}
        HashSet<string> allowed=new HashSet<string>(new[]{"FREQ","INTERVAL","COUNT","UNTIL"},StringComparer.OrdinalIgnoreCase);
        if(frequency=="weekly")allowed.Add("BYDAY");else if(frequency=="monthly")allowed.Add("BYMONTHDAY");else if(frequency=="yearly"){allowed.Add("BYMONTH");allowed.Add("BYMONTHDAY");}
        if(parts.Keys.Any(key=>!allowed.Contains(key))||(parts.ContainsKey("COUNT")&&parts.ContainsKey("UNTIL"))){spec.Frequency="preserve";spec.Preserve=true;return spec;}
        spec.Frequency=frequency;int interval;if(parts.TryGetValue("INTERVAL",out raw)){if(!Int32.TryParse(raw,out interval)||interval<1){spec.Frequency="preserve";spec.Preserve=true;return spec;}spec.Interval=interval;}
        string days;if(parts.TryGetValue("BYDAY",out days))foreach(string token in days.Split(',')){Match match=Regex.Match(token.Trim().ToUpperInvariant(),@"^(MO|TU|WE|TH|FR|SA|SU)$");if(!match.Success){spec.Frequency="preserve";spec.Preserve=true;return spec;}int day=Array.IndexOf(new[]{"","MO","TU","WE","TH","FR","SA","SU"},match.Groups[1].Value);if(day>0&&!spec.Weekdays.Contains(day))spec.Weekdays.Add(day);}
        if(spec.Frequency=="weekly"&&spec.Weekdays.Count==0)spec.Weekdays.Add(WeekdayNumber(start.DayOfWeek));
        if(spec.Frequency=="weekly"&&!spec.Weekdays.Contains(WeekdayNumber(start.DayOfWeek))){spec.Frequency="preserve";spec.Preserve=true;return spec;}
        string expected;if(frequency=="monthly"){spec.MonthDay=start.Day;if(parts.TryGetValue("BYMONTHDAY",out expected)&&(!Int32.TryParse(expected,out spec.MonthDay)||spec.MonthDay<1||spec.MonthDay>31)){spec.Frequency="preserve";spec.Preserve=true;return spec;}}
        if(frequency=="yearly"){spec.Month=start.Month;spec.MonthDay=start.Day;if(parts.TryGetValue("BYMONTH",out expected)&&(!Int32.TryParse(expected,out spec.Month)||spec.Month<1||spec.Month>12)){spec.Frequency="preserve";spec.Preserve=true;return spec;}if(parts.TryGetValue("BYMONTHDAY",out expected)&&(!Int32.TryParse(expected,out spec.MonthDay)||spec.MonthDay<1||spec.MonthDay>DateTime.DaysInMonth(2024,spec.Month))){spec.Frequency="preserve";spec.Preserve=true;return spec;}}
        string until;if(parts.TryGetValue("UNTIL",out until)){if(!Regex.IsMatch(until??"",@"^(?:\d{8}|\d{8}T\d{6}Z)$")){spec.Frequency="preserve";spec.Preserve=true;return spec;}spec.EndMode="until";spec.Until=ParseUntilDate(until,spec.Until);}
        string count;if(parts.TryGetValue("COUNT",out count)){if(!Int32.TryParse(count,out interval)||interval<1){spec.Frequency="preserve";spec.Preserve=true;return spec;}spec.EndMode="count";spec.Count=interval;}
        return spec;
    }

    private static string BuildRecurrenceRule(RecurrenceSpec spec,DateTimeOffset start,bool allDay)
    {
        if(spec==null||spec.Preserve||spec.Frequency=="none"||spec.Frequency=="")return "";
        string frequency=spec.Frequency.ToUpperInvariant();List<string> parts=new List<string>{"FREQ="+frequency};
        if(spec.Interval>1)parts.Add("INTERVAL="+spec.Interval.ToString(CultureInfo.InvariantCulture));
        if(spec.Frequency=="weekly")parts.Add("BYDAY="+String.Join(",",spec.Weekdays.Distinct().OrderBy(x=>x).Select(WeekdayCode)));
        else if(spec.Frequency=="monthly")parts.Add("BYMONTHDAY="+(spec.MonthDay>0?spec.MonthDay:start.Day).ToString(CultureInfo.InvariantCulture));
        else if(spec.Frequency=="yearly"){parts.Add("BYMONTH="+(spec.Month>0?spec.Month:start.Month).ToString(CultureInfo.InvariantCulture));parts.Add("BYMONTHDAY="+(spec.MonthDay>0?spec.MonthDay:start.Day).ToString(CultureInfo.InvariantCulture));}
        if(spec.EndMode=="until"){
            if(allDay)parts.Add("UNTIL="+spec.Until.ToString("yyyyMMdd",CultureInfo.InvariantCulture));
            else {DateTime local=new DateTime(spec.Until.Year,spec.Until.Month,spec.Until.Day,start.Hour,start.Minute,start.Second,DateTimeKind.Unspecified);DateTimeOffset end=new DateTimeOffset(local,TimeZoneInfo.Local.GetUtcOffset(local));parts.Add("UNTIL="+end.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture));}
        } else if(spec.EndMode=="count")parts.Add("COUNT="+Math.Max(1,spec.Count).ToString(CultureInfo.InvariantCulture));
        return String.Join(";",parts);
    }

    private static string RecurrenceLabel(RecurrenceSpec spec)
    {
        if(spec==null||spec.Frequency=="none")return "不重复";
        if(spec.Preserve||spec.Frequency=="preserve")return "保持现有周期";
        string prefix=spec.Interval>1?"每 "+spec.Interval+" ":"每";
        if(spec.Frequency=="daily")return spec.Interval>1?prefix+"天":"每天";
        if(spec.Frequency=="weekly"){List<int> selected=spec.Weekdays.Distinct().OrderBy(x=>x).ToList();string days=selected.Count==7?"每天":selected.SequenceEqual(new[]{1,2,3,4,5})?"工作日":selected.Count<=3?String.Join("、",selected.Select(WeekdayName)):selected.Count+" 天";return (spec.Interval>1?prefix+"周":"每周")+(days==""?"":" · "+days);}
        if(spec.Frequency=="monthly")return "每月"+(spec.MonthDay>0?" · "+spec.MonthDay+"日":"");
        if(spec.Frequency=="yearly")return "每年"+(spec.Month>0&&spec.MonthDay>0?" · "+spec.Month+"月"+spec.MonthDay+"日":"");
        return "周期日程";
    }

    private static DateTimeOffset LocalDateTime(DateTime date,DateTimeOffset template)
    {
        DateTime local=new DateTime(date.Year,date.Month,date.Day,template.Hour,template.Minute,template.Second,DateTimeKind.Unspecified);
        return new DateTimeOffset(local,TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static void AlignRecurrenceStart(RecurrenceSpec spec,ref DateTimeOffset start,ref DateTimeOffset end)
    {
        if(spec==null||spec.Preserve)return;
        TimeSpan duration=end-start;
        DateTime target=start.Date;
        if(spec.Frequency=="weekly"&&spec.Weekdays.Count>0&&!spec.Weekdays.Contains(WeekdayNumber(start.DayOfWeek))){for(int offset=1;offset<=7;offset++){DateTime date=start.Date.AddDays(offset);if(spec.Weekdays.Contains(WeekdayNumber(date.DayOfWeek))){target=date;break;}}}
        else if(spec.Frequency=="monthly"&&spec.MonthDay>0&&start.Day!=spec.MonthDay){for(int offset=0;offset<=12;offset++){DateTime month=start.Date.AddMonths(offset);if(spec.MonthDay>DateTime.DaysInMonth(month.Year,month.Month))continue;DateTime date=new DateTime(month.Year,month.Month,spec.MonthDay);if(date>=start.Date){target=date;break;}}}
        else if(spec.Frequency=="yearly"&&spec.Month>0&&spec.MonthDay>0&&(start.Month!=spec.Month||start.Day!=spec.MonthDay)){for(int offset=0;offset<=8;offset++){int year=start.Year+offset;if(year>9999)break;if(spec.MonthDay>DateTime.DaysInMonth(year,spec.Month))continue;DateTime date=new DateTime(year,spec.Month,spec.MonthDay);if(date>=start.Date){target=date;break;}}}
        if(target!=start.Date){start=LocalDateTime(target,start);end=start.Add(duration);}
    }

    private static IEnumerable<DateTimeOffset> RecurrenceStarts(RecurrenceSpec spec,DateTimeOffset start,DateTime limit)
    {
        int safety=0,interval=Math.Max(1,spec.Interval);
        if(spec.Frequency=="daily"){
            for(int i=0;safety<100000;i++,safety++){DateTime date;try{date=start.Date.AddDays((long)i*interval);}catch{yield break;}DateTimeOffset candidate=LocalDateTime(date,start);if(candidate.Date>limit.Date)yield break;yield return candidate;}
        } else if(spec.Frequency=="weekly"){
            List<int> days=spec.Weekdays.Distinct().OrderBy(x=>x).ToList();DateTime firstWeek=start.Date.AddDays(-(WeekdayNumber(start.DayOfWeek)-1));
            for(int cycle=0;safety<100000;cycle++,safety++){DateTime week;try{week=firstWeek.AddDays((long)cycle*interval*7);}catch{yield break;}if(week.Date>limit.Date)yield break;foreach(int day in days){DateTime date;try{date=week.AddDays(day-1);}catch{continue;}if(date<start.Date)continue;if(date>limit.Date)yield break;yield return LocalDateTime(date,start);}}
        } else if(spec.Frequency=="monthly"){
            int monthDay=spec.MonthDay>0?spec.MonthDay:start.Day;for(int i=0;safety<100000;i++,safety++){DateTime month;try{month=start.Date.AddMonths(i*interval);}catch{yield break;}int days=DateTime.DaysInMonth(month.Year,month.Month);if(monthDay>days)continue;DateTime date=new DateTime(month.Year,month.Month,monthDay);if(date<start.Date)continue;if(date>limit.Date)yield break;yield return LocalDateTime(date,start);}
        } else if(spec.Frequency=="yearly"){
            int month=spec.Month>0?spec.Month:start.Month,monthDay=spec.MonthDay>0?spec.MonthDay:start.Day;for(int i=0;safety<100000;i++,safety++){long yearValue=(long)start.Year+(long)i*interval;if(yearValue>limit.Year||yearValue>9999)yield break;DateTime date;try{date=new DateTime((int)yearValue,month,monthDay);}catch{continue;}if(date<start.Date)continue;if(date>limit.Date)yield break;yield return LocalDateTime(date,start);}
        }
    }

    private static IEnumerable<Dictionary<string,object>> ExpandLocalSeries(Dictionary<string,object> master)
    {
        string raw=S(master,"rrule");if(raw==""){yield return master;yield break;}
        DateTimeOffset? startValue=RuntimeUtil.Date(master,"start_at"),endValue=RuntimeUtil.Date(master,"end_at");if(!startValue.HasValue||!endValue.HasValue){yield return master;yield break;}
        RecurrenceSpec spec=RecurrenceFromEvent(master);if(spec.Preserve||spec.Frequency=="none"){yield return master;yield break;}
        DateTimeOffset start=startValue.Value;TimeSpan duration=endValue.Value-start;DateTime limit;
        if(spec.EndMode=="until")limit=spec.Until.Date;
        else if(spec.EndMode=="count")limit=DateTime.MaxValue.Date;
        else {limit=DateTime.Today.AddYears(2);if(start.Date>limit)limit=start.Date.AddYears(2);}
        int emitted=0,maxCount=spec.EndMode=="count"?Math.Max(1,spec.Count):Int32.MaxValue;
        foreach(DateTimeOffset occurrenceStart in RecurrenceStarts(spec,start,limit)){
            if(emitted>=maxCount)yield break;emitted++;
            Dictionary<string,object> occurrence=new Dictionary<string,object>(master);string recurrence=occurrenceStart.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture),key=S(master,"uid")+"|"+recurrence;
            occurrence["series_id"]=S(master,"id");occurrence["series_start_at"]=S(master,"start_at");occurrence["id"]=RuntimeUtil.Sha256Hex(key).Substring(0,32);occurrence["occurrence_key"]=key;occurrence["recurrence_id"]=recurrence;occurrence["start_at"]=RuntimeUtil.Iso(occurrenceStart);occurrence["end_at"]=RuntimeUtil.Iso(occurrenceStart.Add(duration));occurrence["recurring"]=true;
            ApplyReminders(occurrence,Reminders(master),occurrenceStart);DateTimeOffset? originalReminder=RuntimeUtil.Date(master,"reminder_at");if(originalReminder.HasValue&&CustomAlarms(master).Count>0){DateTimeOffset shifted=occurrenceStart.Add(originalReminder.Value-start);DateTimeOffset? generated=RuntimeUtil.Date(occurrence,"reminder_at");if(!generated.HasValue||shifted<generated.Value)occurrence["reminder_at"]=RuntimeUtil.Iso(shifted);}occurrence["reminder_count"]=Reminders(occurrence).Count+CustomAlarms(occurrence).Count;
            yield return occurrence;
        }
    }

    private static Dictionary<string,object> LocalSeriesMaster(Dictionary<string,object> state,Dictionary<string,object> occurrence)
    {
        string series=S(occurrence,"series_id"),uid=S(occurrence,"uid");
        return LocalEvents(state).FirstOrDefault(e=>(series!=""&&S(e,"id")==series)||(S(e,"rrule")!=""&&S(e,"uid")==uid));
    }
}
