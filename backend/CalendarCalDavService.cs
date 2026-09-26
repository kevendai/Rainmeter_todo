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
    private sealed class DavResult { public int Status; public string Text, Location; }

    private static DavResult Dav(string method, string uri, Dictionary<string, object> credentials, string body, int depth, int timeoutMs = 20000)
    {
        HttpWebRequest request = CreateDavRequest(method, uri, credentials, timeoutMs);
        if (depth >= 0) request.Headers["Depth"] = depth.ToString(CultureInfo.InvariantCulture);
        WriteDavBody(request, body, "application/xml; charset=utf-8");
        return ReadDavResponse(request);
    }

    private static DavResult DavText(string method, string uri, Dictionary<string, object> credentials, string body, string contentType, string etag)
    {
        HttpWebRequest request = CreateDavRequest(method, uri, credentials, 20000);
        if (etag == "*") request.Headers["If-None-Match"] = "*";
        else if (etag != "") request.Headers["If-Match"] = etag;
        WriteDavBody(request, body, contentType);
        return ReadDavResponse(request);
    }

    private static HttpWebRequest CreateDavRequest(string method, string uri, Dictionary<string, object> credentials, int timeoutMs)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(DynamicPluginValues.BindSelected(uri,"calendar.caldav",S(credentials,"AddressSource")));
        request.Method = method;
        request.Credentials = new NetworkCredential(S(credentials, "Username"), S(credentials, "Password"));
        request.PreAuthenticate = true;
        request.AllowAutoRedirect = false;
        request.Timeout = timeoutMs;
        request.ReadWriteTimeout = timeoutMs;
        request.UserAgent = "Rainmeter-Calendar/2.0";
        return request;
    }

    private static void WriteDavBody(HttpWebRequest request, string body, string contentType)
    {
        if (body == "") return;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        request.ContentType = contentType;
        request.ContentLength = bytes.Length;
        using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
    }

    private static DavResult ReadDavResponse(HttpWebRequest request)
    {
        WebResponse response;
        try { response = request.GetResponse(); }
        catch (WebException ex) { if (ex.Response == null) throw; response = ex.Response; }
        using (response)
        using (StreamReader reader = new StreamReader(response.GetResponseStream()))
        {
            HttpWebResponse http = (HttpWebResponse)response;
            return new DavResult { Status = (int)http.StatusCode, Text = reader.ReadToEnd(), Location = http.Headers["Location"] };
        }
    }

    private static string Resolve(string root, string href) { return new Uri(new Uri(root.TrimEnd('/') + "/"), href).AbsoluteUri; }
    private sealed class CalendarInfo { public string Uri, Name; }

    private static CalendarInfo Discover(Dictionary<string, object> credentials)
    {
        string root = S(credentials, "Server").Trim();
        if (root == "") throw new Exception("CalDAV 地址不能为空");
        root = root.TrimEnd('/');
        string properties = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><d:current-user-principal/><c:calendar-home-set/><d:displayname/><d:resourcetype/><c:supported-calendar-component-set/></d:prop></d:propfind>";
        DavResult wellKnown = Dav("PROPFIND", root + "/.well-known/caldav", credentials, properties, 0);
        string davUri = !String.IsNullOrEmpty(wellKnown.Location) ? Resolve(root, wellKnown.Location) : root + "/dav/";
        DavResult dav = Dav("PROPFIND", davUri, credentials, properties, 0);
        if (dav.Status == 401) throw new Exception("CalDAV 账号或密码无效");
        if (dav.Status != 207) throw new Exception("CalDAV 服务发现失败：HTTP " + dav.Status);

        XmlDocument document = Xml(dav.Text);
        XmlNamespaceManager namespaces = Ns(document);
        XmlNode home = document.SelectSingleNode("//c:calendar-home-set/d:href", namespaces);
        if (home == null)
        {
            XmlNode principal = document.SelectSingleNode("//d:current-user-principal/d:href", namespaces);
            if (principal == null) throw new Exception("CalDAV 未返回 current-user-principal");
            DavResult principalResult = Dav("PROPFIND", Resolve(root, principal.InnerText), credentials, properties, 0);
            if (principalResult.Status != 207) throw new Exception("CalDAV principal 查询失败：HTTP " + principalResult.Status);
            XmlDocument principalDocument = Xml(principalResult.Text);
            home = principalDocument.SelectSingleNode("//c:calendar-home-set/d:href", Ns(principalDocument));
        }
        if (home == null) throw new Exception("CalDAV 未返回 calendar-home-set");

        DavResult list = Dav("PROPFIND", Resolve(root, home.InnerText), credentials, properties, 1);
        if (list.Status != 207) throw new Exception("日历列表读取失败：HTTP " + list.Status);
        XmlDocument listDocument = Xml(list.Text);
        XmlNamespaceManager listNamespaces = Ns(listDocument);
        List<CalendarInfo> found = new List<CalendarInfo>();
        foreach (XmlNode response in listDocument.SelectNodes("//d:response", listNamespaces))
        {
            if (response.SelectSingleNode(".//d:resourcetype/c:calendar", listNamespaces) == null) continue;
            XmlNode href = response.SelectSingleNode("./d:href", listNamespaces);
            XmlNode name = response.SelectSingleNode(".//d:displayname", listNamespaces);
            if (href != null) found.Add(new CalendarInfo { Uri = Resolve(root, href.InnerText), Name = name == null ? "" : name.InnerText });
        }
        if (found.Count == 0) throw new Exception("没有找到支持 VEVENT 的日历");
        return found.OrderBy(item => item.Name == "Default Calendar" ? 0 : 1).ThenBy(item => item.Name).First();
    }
    private static XmlDocument Xml(string text){XmlDocument d=new XmlDocument();d.LoadXml(text);return d;} private static XmlNamespaceManager Ns(XmlDocument d){XmlNamespaceManager n=new XmlNamespaceManager(d.NameTable);n.AddNamespace("d","DAV:");n.AddNamespace("c","urn:ietf:params:xml:ns:caldav");return n;}

    private sealed class IProp{public string Value;public Dictionary<string,string> P=new Dictionary<string,string>();}
    private static List<IProp> Props(IEnumerable<string>lines,string name){List<IProp>r=new List<IProp>();foreach(string line in lines){int colon=line.IndexOf(':');if(colon<1)continue;string[]left=line.Substring(0,colon).Split(';');if(!left[0].Equals(name,StringComparison.OrdinalIgnoreCase))continue;IProp p=new IProp{Value=line.Substring(colon+1)};foreach(string part in left.Skip(1)){int eq=part.IndexOf('=');if(eq>0)p.P[part.Substring(0,eq).ToUpperInvariant()]=part.Substring(eq+1).Trim('"');}r.Add(p);}return r;}
    private static string IText(string v){return(v??"").Replace("\\n","\n").Replace("\\N","\n").Replace("\\,",",").Replace("\\;",";").Replace("\\\\","\\");}
    private sealed class IDate{public DateTimeOffset Value;public bool AllDay;}
    private static IDate IcsDate(IProp p){if(p==null)return null;string value=p.Value;string kind;p.P.TryGetValue("VALUE",out kind);bool all=kind=="DATE"||Regex.IsMatch(value,"^\\d{8}$");if(all){DateTime d=DateTime.ParseExact(value.Substring(0,8),"yyyyMMdd",CultureInfo.InvariantCulture);return new IDate{Value=new DateTimeOffset(d,TimeZoneInfo.Local.GetUtcOffset(d)),AllDay=true};}if(value.EndsWith("Z")){DateTime d=DateTime.ParseExact(value,"yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal);return new IDate{Value=new DateTimeOffset(d.ToUniversalTime()).ToLocalTime()};}string format=value.Length>=15?"yyyyMMdd'T'HHmmss":"yyyyMMdd'T'HHmm";DateTime local=DateTime.SpecifyKind(DateTime.ParseExact(value,format,CultureInfo.InvariantCulture),DateTimeKind.Unspecified);string tzid;p.P.TryGetValue("TZID",out tzid);TimeZoneInfo zone=TimeZoneInfo.Local;if(!String.IsNullOrEmpty(tzid)){if(tzid=="Asia/Shanghai")tzid="China Standard Time";else if(tzid=="Etc/UTC")tzid="UTC";try{zone=TimeZoneInfo.FindSystemTimeZoneById(tzid);}catch{}}return new IDate{Value=new DateTimeOffset(local,zone.GetUtcOffset(local))};}
    private static DateTimeOffset? Reminder(List<string>block,DateTimeOffset start,DateTimeOffset end){List<DateTimeOffset>r=new List<DateTimeOffset>();foreach(IProp p in Props(block,"TRIGGER")){try{if(Regex.IsMatch(p.Value,"^[+-]?P")){string related;p.P.TryGetValue("RELATED",out related);r.Add((related=="END"?end:start).Add(XmlConvert.ToTimeSpan(p.Value)));}else r.Add(IcsDate(p).Value);}catch{}}return r.Count==0?(DateTimeOffset?)null:r.Min();}
    private static List<int> ReminderOffsets(List<string>block,DateTimeOffset start,DateTimeOffset end){List<int>m=new List<int>();foreach(IProp p in Props(block,"TRIGGER")){try{string related;p.P.TryGetValue("RELATED",out related);if(!Regex.IsMatch(p.Value,"^-P")||(!String.IsNullOrEmpty(related)&&related!="START"))continue;DateTimeOffset at=start.Add(XmlConvert.ToTimeSpan(p.Value));int minutes=(int)Math.Round((start-at).TotalMinutes);if(minutes>0&&!m.Contains(minutes))m.Add(minutes);}catch{}}m.Sort();return m;}
    private static List<int> Reminders(Dictionary<string,object> e){List<int> result=new List<int>();foreach(object item in JsonUtil.Array(JsonUtil.Get(e,"reminders"))){int v;if(Int32.TryParse(Convert.ToString(item,CultureInfo.InvariantCulture),out v)&&v>0&&!result.Contains(v))result.Add(v);}result.Sort();return result;}
    private static void ApplyReminders(Dictionary<string,object> e,List<int> minutes,DateTimeOffset start){List<int> values=minutes.Where(x=>x>0).Distinct().OrderBy(x=>x).ToList();e["reminders"]=values.Cast<object>().ToList();e["reminder_count"]=values.Count;e["reminder_at"]=values.Count==0?"":RuntimeUtil.Iso(start.AddMinutes(-values.Max()));}
    private static List<List<string>> EventBlocks(string text)
    {
        string unfolded=Regex.Replace(text??"","\r?\n[ \t]","");List<List<string>> blocks=new List<List<string>>();List<string> current=null;
        foreach(string line in Regex.Split(unfolded,"\r?\n")){if(line=="BEGIN:VEVENT")current=new List<string>();else if(line=="END:VEVENT"){if(current!=null)blocks.Add(current);current=null;}else if(current!=null)current.Add(line);}return blocks;
    }
    private static List<Dictionary<string,object>> ParseIcs(string text)
    {
        List<List<string>> blocks=EventBlocks(text);HashSet<string> exceptionUids=new HashSet<string>(blocks.Where(block=>Props(block,"RECURRENCE-ID").Any()).Select(block=>Props(block,"UID").FirstOrDefault()).Where(prop=>prop!=null).Select(prop=>IText(prop.Value)));
        List<Dictionary<string,object>> events=new List<Dictionary<string,object>>();
        foreach(List<string>b in blocks){
            IProp uid=Props(b,"UID").FirstOrDefault(),sp=Props(b,"DTSTART").FirstOrDefault();if(uid==null||sp==null)continue;
            IDate start=IcsDate(sp),end=IcsDate(Props(b,"DTEND").FirstOrDefault());DateTimeOffset evEnd=end!=null?end.Value:start.AllDay?start.Value.AddDays(1):start.Value.AddHours(1);if(!start.AllDay&&evEnd<=start.Value)evEnd=start.Value.AddHours(1);
            IProp rp=Props(b,"RECURRENCE-ID").FirstOrDefault(),rrule=Props(b,"RRULE").FirstOrDefault();string uidText=IText(uid.Value);bool hasRDate=Props(b,"RDATE").Any(),preserve=hasRDate||Props(b,"EXDATE").Any()||Props(b,"EXRULE").Any()||exceptionUids.Contains(uidText);string recurrence=(rp==null?start.Value:IcsDate(rp).Value).ToUniversalTime().ToString("o"),key=uidText+"|"+recurrence;
            Func<string,string>one=n=>{IProp p=Props(b,n).FirstOrDefault();return p==null?"":IText(p.Value);};DateTimeOffset? reminder=Reminder(b,start.Value,evEnd);List<int> reminderOffsets=ReminderOffsets(b,start.Value,evEnd);List<object> custom=RawAlarmBlocks(b).Where(x=>!AlarmIsStartOffset(x)).Cast<object>().ToList();string link=one("X-RAINMETER-LINK");if(link=="")link=DisplayLink(one("URL"));
            Dictionary<string,object>e=new Dictionary<string,object>{{"id",RuntimeUtil.Sha256Hex(key).Substring(0,32)},{"occurrence_key",key},{"uid",uidText},{"recurrence_id",recurrence},{"title",one("SUMMARY")==""?"（无标题）":one("SUMMARY")},{"start_at",RuntimeUtil.Iso(start.Value)},{"end_at",RuntimeUtil.Iso(evEnd)},{"all_day",start.AllDay},{"url",link},{"location",one("LOCATION")},{"description",one("DESCRIPTION")},{"status",one("STATUS")},{"reminder_at",reminder.HasValue?RuntimeUtil.Iso(reminder.Value):""},{"reminder_count",reminderOffsets.Count+custom.Count},{"reminders",reminderOffsets.Cast<object>().ToList()},{"custom_alarms",custom},{"rrule",rrule==null?"":rrule.Value},{"recurrence_exception",rp!=null},{"recurrence_preserve",preserve},{"recurring",rp!=null||rrule!=null||preserve}};
            if(S(e,"status")!="CANCELLED")events.Add(e);
        }
        return events.GroupBy(e=>S(e,"occurrence_key")).Select(g=>g.First()).ToList();
    }
    private sealed class RecurrenceMetadata{public string Rule="";public bool Recurring,Preserve,AllDay;public DateTimeOffset? Start,End;public Dictionary<string,object> Master;}
    private static Dictionary<string,RecurrenceMetadata> ParseRecurrenceMetadata(string text)
    {
        List<List<string>> blocks=EventBlocks(text);HashSet<string> exceptionUids=new HashSet<string>(blocks.Where(block=>Props(block,"RECURRENCE-ID").Any()).Select(block=>Props(block,"UID").FirstOrDefault()).Where(prop=>prop!=null).Select(prop=>IText(prop.Value)));Dictionary<string,Dictionary<string,object>> masters=ParseIcs(text).Where(e=>!B(e,"recurrence_exception")).GroupBy(e=>S(e,"uid")).ToDictionary(group=>group.Key,group=>group.First());
        Dictionary<string,RecurrenceMetadata> result=new Dictionary<string,RecurrenceMetadata>();
        foreach(List<string> block in blocks){
            IProp uid=Props(block,"UID").FirstOrDefault(),startProp=Props(block,"DTSTART").FirstOrDefault();if(uid==null||Props(block,"RECURRENCE-ID").Any())continue;string uidText=IText(uid.Value);IProp rule=Props(block,"RRULE").FirstOrDefault();bool preserve=Props(block,"RDATE").Any()||Props(block,"EXDATE").Any()||Props(block,"EXRULE").Any()||exceptionUids.Contains(uidText);IDate start=IcsDate(startProp),end=IcsDate(Props(block,"DTEND").FirstOrDefault());
            DateTimeOffset? finish=end==null?(start==null?(DateTimeOffset?)null:start.Value.Add(start.AllDay?TimeSpan.FromDays(1):TimeSpan.FromHours(1))):end.Value;
            Dictionary<string,object> master;masters.TryGetValue(uidText,out master);result[uidText]=new RecurrenceMetadata{Rule=rule==null?"":rule.Value,Recurring=rule!=null||preserve,Preserve=preserve,Start=start==null?(DateTimeOffset?)null:start.Value,End=finish,AllDay=start!=null&&start.AllDay,Master=master};
        }
        return result;
    }
    private sealed class FetchResult{public CalendarInfo Calendar;public List<Dictionary<string,object>> Events;public int FailedWindows;}
    private static List<Dictionary<string,object>> FetchWindow(CalendarInfo cal,Dictionary<string,object>c,DateTimeOffset start,DateTimeOffset end)
    {
        string a=start.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"),b=end.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");string filter="<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\"><c:time-range start=\""+a+"\" end=\""+b+"\"/></c:comp-filter></c:comp-filter></c:filter>";
        string body="<?xml version=\"1.0\" encoding=\"utf-8\"?><c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><d:getetag/><c:calendar-data><c:expand start=\""+a+"\" end=\""+b+"\"/></c:calendar-data></d:prop>"+filter+"</c:calendar-query>";
        DavResult report=Dav("REPORT",cal.Uri,c,body,1,6000);if(report.Status!=207)throw new Exception("日程查询失败：HTTP "+report.Status);XmlDocument d=Xml(report.Text);XmlNamespaceManager ns=Ns(d);List<Dictionary<string,object>>events=new List<Dictionary<string,object>>();
        foreach(XmlNode response in d.SelectNodes("//d:response",ns)){XmlNode data=response.SelectSingleNode(".//c:calendar-data",ns);if(data==null)continue;XmlNode href=response.SelectSingleNode("./d:href",ns),etag=response.SelectSingleNode(".//d:getetag",ns);string absolute=href==null?"":Resolve(cal.Uri,href.InnerText);foreach(Dictionary<string,object> e in ParseIcs(data.InnerText)){e["source"]="caldav";e["calendar"]="caldav";e["href"]=absolute;e["etag"]=etag==null?"":etag.InnerText;events.Add(e);}}
        foreach(IGrouping<string,Dictionary<string,object>> group in events.GroupBy(e=>S(e,"href")+"|"+S(e,"uid")))if(group.Count()>1)foreach(Dictionary<string,object> e in group)e["recurring"]=true;
        try{
            string metadataBody="<?xml version=\"1.0\" encoding=\"utf-8\"?><c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-data/></d:prop>"+filter+"</c:calendar-query>";DavResult metadata=Dav("REPORT",cal.Uri,c,metadataBody,1,6000);
            if(metadata.Status==207){XmlDocument md=Xml(metadata.Text);XmlNamespaceManager mns=Ns(md);foreach(XmlNode response in md.SelectNodes("//d:response",mns)){XmlNode data=response.SelectSingleNode(".//c:calendar-data",mns),href=response.SelectSingleNode("./d:href",mns);if(data==null)continue;string absolute=href==null?"":Resolve(cal.Uri,href.InnerText);Dictionary<string,RecurrenceMetadata> found=ParseRecurrenceMetadata(data.InnerText);foreach(Dictionary<string,object> e in events.Where(x=>S(x,"href")==absolute)){RecurrenceMetadata value;if(found.TryGetValue(S(e,"uid"),out value)){e["rrule"]=value.Rule;e["recurring"]=value.Recurring;e["recurrence_preserve"]=value.Preserve;if(value.Start.HasValue)e["series_start_at"]=RuntimeUtil.Iso(value.Start.Value);if(value.End.HasValue)e["series_end_at"]=RuntimeUtil.Iso(value.End.Value);e["series_all_day"]=value.AllDay;if(value.Master!=null)e["series_event"]=value.Master;}}}}
        }catch{}
        return events;
    }
    private static bool CachedCalendarMatchesServer(Dictionary<string,object>c,string cachedCalendarUrl)
    {
        string root=S(c,"Server").Trim().TrimEnd('/'), cached=(cachedCalendarUrl??"").Trim();
        if(root==""||cached=="")return false;
        return cached.StartsWith(root+"/",StringComparison.OrdinalIgnoreCase)||cached.Equals(root,StringComparison.OrdinalIgnoreCase);
    }
    private static FetchResult Fetch(Dictionary<string,object>c,string cachedCalendarUrl){if(!CachedCalendarMatchesServer(c,cachedCalendarUrl))cachedCalendarUrl="";DateTimeOffset now=DateTimeOffset.Now,start=new DateTimeOffset(now.Year,now.Month,now.Day,0,0,0,now.Offset),end=start.AddDays(21);CalendarInfo cal=String.IsNullOrEmpty(cachedCalendarUrl)?Discover(c):new CalendarInfo{Uri=cachedCalendarUrl,Name="Cached Calendar"};List<Dictionary<string,object>>events=new List<Dictionary<string,object>>();int failed=0;try{events.AddRange(FetchWindow(cal,c,start,end));}catch{failed++;if(!String.IsNullOrEmpty(cachedCalendarUrl)){cal=Discover(c);events.AddRange(FetchWindow(cal,c,start,end));failed=0;}else throw;}return new FetchResult{Calendar=cal,Events=events.GroupBy(e=>S(e,"occurrence_key")).Select(g=>g.First()).OrderBy(e=>RuntimeUtil.Date(e,"start_at")).ToList(),FailedWindows=failed};}

}
