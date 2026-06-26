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

internal static class CalendarApp
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
        using(Mutex mutex=new Mutex(false,@"Global\RainmeterCalendarState")){bool held=false;Dictionary<string,object> cache=null,state=null;try{
            held=mutex.WaitOne(TimeSpan.FromSeconds(20));if(!held)return 4;cache=Load(CachePath,NewCache());state=Load(StatePath,NewState());Shape(cache,state);if(Reconcile(state))Save(StatePath,state);bool refresh=false,refreshTodo=false;
            if(action=="Startup"||action=="Rollover"||action=="Sync"){
                bool guarded=action=="Startup"&&ConsumeGuard();DateTimeOffset? last=RuntimeUtil.Date(cache,"fetched_at");bool need=action=="Sync"||!last.HasValue||(action=="Rollover"&&last.Value.Date!=DateTimeOffset.Now.Date)||(DateTimeOffset.Now-last.Value).TotalMinutes>=15;
                if(need)Sync(cache,state,ref refreshTodo);refresh=Render(cache,state)&&(action!="Startup"||!guarded);if(action=="Sync")refresh=true;
            } else if(action=="Render")refresh=Render(cache,state);
            else if(action=="Open"){Dictionary<string,object> ev=FindEvent(cache,id);if(ev!=null)RuntimeUtil.Run(S(ev,"url"));}
            else if(action=="Detail"||action=="Convert"){Dictionary<string,object> ev=FindEvent(cache,id);if(ev!=null){bool already=Conversions(state).Any(c=>S(c,"occurrence_key")==S(ev,"occurrence_key"));if(action=="Convert"||ShowDetails(ev,already)){if(ConvertInteractive(ev,state,cache))refreshTodo=true;Save(StatePath,state);Save(CachePath,cache);Render(cache,state);refresh=true;}}}
            else if(action=="Manage"||action=="Settings"){Settings(state,cache,ref refreshTodo);Render(cache,state);refresh=true;}
            if(refreshTodo){string todoExe=Path.Combine(TodoDir,"TodoHost.exe");if(File.Exists(todoExe))System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(todoExe,"Render"){UseShellExecute=false,CreateNoWindow=true});}
            if(refresh){MarkGuard();RuntimeUtil.Refresh("Calendar");if(refreshTodo)RuntimeUtil.Refresh("Todo");}else if(refreshTodo)RuntimeUtil.Refresh("Todo");return 0;
        }catch(Exception ex){if(cache!=null){cache["status"]="操作失败："+ex.Message;try{Save(CachePath,cache);Render(cache,state);RuntimeUtil.Refresh("Calendar");}catch{}}return 1;}finally{if(held)mutex.ReleaseMutex();}}
    }

    private static Dictionary<string,object> NewCache(){return new Dictionary<string,object>{{"version",1},{"fetched_at",""},{"calendar_url",""},{"status",File.Exists(SecretPath)?"尚未同步":"CalDAV 未连接"},{"events",new List<object>()}};}
    private static Dictionary<string,object> NewState(){return new Dictionary<string,object>{{"version",1},{"series_rules",new List<object>()},{"conversions",new List<object>()}};}
    private static Dictionary<string,object> Load(string path,Dictionary<string,object> fallback){if(!File.Exists(path))return fallback;try{return JsonUtil.LoadObject(path);}catch{File.Copy(path,path+".corrupt-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"),true);return fallback;}}
    private static void Shape(Dictionary<string,object> cache,Dictionary<string,object> state){if(JsonUtil.Get(cache,"events")==null)cache["events"]=new List<object>();if(JsonUtil.Get(cache,"status")==null)cache["status"]=File.Exists(SecretPath)?"就绪":"CalDAV 未连接";if(JsonUtil.Get(state,"series_rules")==null)state["series_rules"]=new List<object>();if(JsonUtil.Get(state,"conversions")==null)state["conversions"]=new List<object>();}
    private static void Save(string path,object value){JsonUtil.SaveAtomic(path,value);}
    private static string S(Dictionary<string,object> v,string k){return JsonUtil.String(v,k,"");}
    private static bool B(Dictionary<string,object> v,string k){return JsonUtil.Bool(v,k,false);}
    private static List<Dictionary<string,object>> List(Dictionary<string,object> root,string key){List<Dictionary<string,object>> result=JsonUtil.Array(JsonUtil.Get(root,key)).Select(JsonUtil.Object).ToList();root[key]=result;return result;}
    private static List<Dictionary<string,object>> Events(Dictionary<string,object> c){return List(c,"events");} private static List<Dictionary<string,object>> Rules(Dictionary<string,object>s){return List(s,"series_rules");} private static List<Dictionary<string,object>> Conversions(Dictionary<string,object>s){return List(s,"conversions");}
    private static Dictionary<string,object> FindEvent(Dictionary<string,object> cache,string id){return Events(cache).FirstOrDefault(e=>S(e,"id")==id);}

    private static bool Reconcile(Dictionary<string,object> state){if(!File.Exists(TodoPath))return false;try{Dictionary<string,object>todo=JsonUtil.LoadObject(TodoPath);List<Dictionary<string,object>>tasks=List(todo,"tasks");HashSet<string>ids=new HashSet<string>(tasks.Select(t=>S(t,"id")));HashSet<string>keys=new HashSet<string>(tasks.Select(t=>S(t,"calendar_occurrence_key")));List<Dictionary<string,object>>conversions=Conversions(state);int before=conversions.Count;conversions.RemoveAll(c=>!ids.Contains(S(c,"task_id"))&&!keys.Contains(S(c,"occurrence_key")));return before!=conversions.Count;}catch{return false;}}
    private sealed class DavResult{public int Status;public string Text,Location;}
    private static DavResult Dav(string method,string uri,Dictionary<string,object> credentials,string body,int depth){HttpWebRequest q=(HttpWebRequest)WebRequest.Create(uri);q.Method=method;q.Credentials=new NetworkCredential(S(credentials,"Username"),S(credentials,"Password"));q.PreAuthenticate=true;q.AllowAutoRedirect=false;q.Timeout=20000;q.ReadWriteTimeout=20000;q.UserAgent="Rainmeter-Calendar/2.0";if(depth>=0)q.Headers["Depth"]=depth.ToString();if(body!=""){byte[]b=Encoding.UTF8.GetBytes(body);q.ContentType="application/xml; charset=utf-8";q.ContentLength=b.Length;using(Stream s=q.GetRequestStream())s.Write(b,0,b.Length);}WebResponse response;try{response=q.GetResponse();}catch(WebException ex){if(ex.Response==null)throw;response=ex.Response;}using(response)using(StreamReader reader=new StreamReader(response.GetResponseStream())){HttpWebResponse h=(HttpWebResponse)response;return new DavResult{Status=(int)h.StatusCode,Text=reader.ReadToEnd(),Location=h.Headers["Location"]};}}
    private static string Resolve(string root,string href){return new Uri(new Uri(root.TrimEnd('/')+"/"),href).AbsoluteUri;}
    private sealed class CalendarInfo{public string Uri,Name;}
    private static CalendarInfo Discover(Dictionary<string,object> c){string root=S(c,"Server");if(root=="")root="https://davis.manao.dpdns.org";root=root.TrimEnd('/');string prop="<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><d:current-user-principal/><c:calendar-home-set/><d:displayname/><d:resourcetype/><c:supported-calendar-component-set/></d:prop></d:propfind>";DavResult wk=Dav("PROPFIND",root+"/.well-known/caldav",c,prop,0);string davUri=wk.Location!=""&&wk.Location!=null?Resolve(root,wk.Location):root+"/dav/";DavResult dav=Dav("PROPFIND",davUri,c,prop,0);if(dav.Status==401)throw new Exception("CalDAV 账号或密码无效");if(dav.Status!=207)throw new Exception("CalDAV 服务发现失败：HTTP "+dav.Status);XmlDocument doc=Xml(dav.Text);XmlNamespaceManager ns=Ns(doc);XmlNode home=doc.SelectSingleNode("//c:calendar-home-set/d:href",ns);if(home==null){XmlNode principal=doc.SelectSingleNode("//d:current-user-principal/d:href",ns);if(principal==null)throw new Exception("CalDAV 未返回 current-user-principal");DavResult pr=Dav("PROPFIND",Resolve(root,principal.InnerText),c,prop,0);if(pr.Status!=207)throw new Exception("CalDAV principal 查询失败：HTTP "+pr.Status);XmlDocument pd=Xml(pr.Text);home=pd.SelectSingleNode("//c:calendar-home-set/d:href",Ns(pd));}if(home==null)throw new Exception("CalDAV 未返回 calendar-home-set");DavResult list=Dav("PROPFIND",Resolve(root,home.InnerText),c,prop,1);if(list.Status!=207)throw new Exception("日历列表读取失败：HTTP "+list.Status);XmlDocument ld=Xml(list.Text);XmlNamespaceManager lns=Ns(ld);List<CalendarInfo>found=new List<CalendarInfo>();foreach(XmlNode response in ld.SelectNodes("//d:response",lns)){if(response.SelectSingleNode(".//d:resourcetype/c:calendar",lns)==null)continue;XmlNode href=response.SelectSingleNode("./d:href",lns),name=response.SelectSingleNode(".//d:displayname",lns);if(href!=null)found.Add(new CalendarInfo{Uri=Resolve(root,href.InnerText),Name=name==null?"":name.InnerText});}if(found.Count==0)throw new Exception("没有找到支持 VEVENT 的日历");return found.OrderBy(x=>x.Name=="Default Calendar"?0:1).ThenBy(x=>x.Name).First();}
    private static XmlDocument Xml(string text){XmlDocument d=new XmlDocument();d.LoadXml(text);return d;} private static XmlNamespaceManager Ns(XmlDocument d){XmlNamespaceManager n=new XmlNamespaceManager(d.NameTable);n.AddNamespace("d","DAV:");n.AddNamespace("c","urn:ietf:params:xml:ns:caldav");return n;}

    private sealed class IProp{public string Value;public Dictionary<string,string> P=new Dictionary<string,string>();}
    private static List<IProp> Props(IEnumerable<string>lines,string name){List<IProp>r=new List<IProp>();foreach(string line in lines){int colon=line.IndexOf(':');if(colon<1)continue;string[]left=line.Substring(0,colon).Split(';');if(!left[0].Equals(name,StringComparison.OrdinalIgnoreCase))continue;IProp p=new IProp{Value=line.Substring(colon+1)};foreach(string part in left.Skip(1)){int eq=part.IndexOf('=');if(eq>0)p.P[part.Substring(0,eq).ToUpperInvariant()]=part.Substring(eq+1).Trim('"');}r.Add(p);}return r;}
    private static string IText(string v){return(v??"").Replace("\\n","\n").Replace("\\N","\n").Replace("\\,",",").Replace("\\;",";").Replace("\\\\","\\");}
    private sealed class IDate{public DateTimeOffset Value;public bool AllDay;}
    private static IDate IcsDate(IProp p){if(p==null)return null;string value=p.Value;string kind;p.P.TryGetValue("VALUE",out kind);bool all=kind=="DATE"||Regex.IsMatch(value,"^\\d{8}$");if(all){DateTime d=DateTime.ParseExact(value.Substring(0,8),"yyyyMMdd",CultureInfo.InvariantCulture);return new IDate{Value=new DateTimeOffset(d,TimeZoneInfo.Local.GetUtcOffset(d)),AllDay=true};}if(value.EndsWith("Z")){DateTime d=DateTime.ParseExact(value,"yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal);return new IDate{Value=new DateTimeOffset(d.ToUniversalTime()).ToLocalTime()};}string format=value.Length>=15?"yyyyMMdd'T'HHmmss":"yyyyMMdd'T'HHmm";DateTime local=DateTime.SpecifyKind(DateTime.ParseExact(value,format,CultureInfo.InvariantCulture),DateTimeKind.Unspecified);string tzid;p.P.TryGetValue("TZID",out tzid);TimeZoneInfo zone=TimeZoneInfo.Local;if(!String.IsNullOrEmpty(tzid)){if(tzid=="Asia/Shanghai")tzid="China Standard Time";else if(tzid=="Etc/UTC")tzid="UTC";try{zone=TimeZoneInfo.FindSystemTimeZoneById(tzid);}catch{}}return new IDate{Value=new DateTimeOffset(local,zone.GetUtcOffset(local))};}
    private static DateTimeOffset? Reminder(List<string>block,DateTimeOffset start,DateTimeOffset end){List<DateTimeOffset>r=new List<DateTimeOffset>();foreach(IProp p in Props(block,"TRIGGER")){try{if(Regex.IsMatch(p.Value,"^[+-]?P")){string related;p.P.TryGetValue("RELATED",out related);r.Add((related=="END"?end:start).Add(XmlConvert.ToTimeSpan(p.Value)));}else r.Add(IcsDate(p).Value);}catch{}}return r.Count==0?(DateTimeOffset?)null:r.Min();}
    private static List<Dictionary<string,object>> ParseIcs(string text){string unfolded=Regex.Replace(text,"\r?\n[ \t]","");List<List<string>>blocks=new List<List<string>>();List<string>cur=null;foreach(string line in Regex.Split(unfolded,"\r?\n")){if(line=="BEGIN:VEVENT")cur=new List<string>();else if(line=="END:VEVENT"){if(cur!=null)blocks.Add(cur);cur=null;}else if(cur!=null)cur.Add(line);}List<Dictionary<string,object>>events=new List<Dictionary<string,object>>();foreach(List<string>b in blocks){IProp uid=Props(b,"UID").FirstOrDefault(),sp=Props(b,"DTSTART").FirstOrDefault();if(uid==null||sp==null)continue;IDate start=IcsDate(sp),end=IcsDate(Props(b,"DTEND").FirstOrDefault());DateTimeOffset evEnd=end!=null?end.Value:start.AllDay?start.Value.AddDays(1):start.Value.AddMinutes(1);IProp rp=Props(b,"RECURRENCE-ID").FirstOrDefault();string recurrence=(rp==null?start.Value:IcsDate(rp).Value).ToUniversalTime().ToString("o"),key=IText(uid.Value)+"|"+recurrence;Func<string,string>one=n=>{IProp p=Props(b,n).FirstOrDefault();return p==null?"":IText(p.Value);};DateTimeOffset? reminder=Reminder(b,start.Value,evEnd);Dictionary<string,object>e=new Dictionary<string,object>{{"id",RuntimeUtil.Sha256Hex(key).Substring(0,32)},{"occurrence_key",key},{"uid",IText(uid.Value)},{"recurrence_id",recurrence},{"title",one("SUMMARY")==""?"（无标题）":one("SUMMARY")},{"start_at",RuntimeUtil.Iso(start.Value)},{"end_at",RuntimeUtil.Iso(evEnd)},{"all_day",start.AllDay},{"url",one("URL")},{"location",one("LOCATION")},{"description",one("DESCRIPTION")},{"status",one("STATUS")},{"reminder_at",reminder.HasValue?RuntimeUtil.Iso(reminder.Value):""},{"reminder_count",Props(b,"TRIGGER").Count},{"recurring",rp!=null}};if(S(e,"status")!="CANCELLED")events.Add(e);}return events.GroupBy(e=>S(e,"occurrence_key")).Select(g=>g.First()).ToList();}
    private sealed class FetchResult{public CalendarInfo Calendar;public List<Dictionary<string,object>> Events;}
    private static FetchResult Fetch(Dictionary<string,object>c){CalendarInfo cal=Discover(c);DateTimeOffset now=DateTimeOffset.Now,start=new DateTimeOffset(now.Year,now.Month,now.Day,0,0,0,now.Offset),end=start.AddDays(1);string a=start.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"),b=end.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");string body="<?xml version=\"1.0\" encoding=\"utf-8\"?><c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><d:getetag/><c:calendar-data><c:expand start=\""+a+"\" end=\""+b+"\"/></c:calendar-data></d:prop><c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\"><c:time-range start=\""+a+"\" end=\""+b+"\"/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>";DavResult report=Dav("REPORT",cal.Uri,c,body,1);if(report.Status!=207)throw new Exception("日程查询失败：HTTP "+report.Status);XmlDocument d=Xml(report.Text);List<Dictionary<string,object>>events=new List<Dictionary<string,object>>();foreach(XmlNode node in d.SelectNodes("//c:calendar-data",Ns(d)))events.AddRange(ParseIcs(node.InnerText));return new FetchResult{Calendar=cal,Events=events.GroupBy(e=>S(e,"occurrence_key")).Select(g=>g.First()).ToList()};}
    private static void Sync(Dictionary<string,object>cache,Dictionary<string,object>state,ref bool todo){try{if(!File.Exists(SecretPath)){cache["events"]=new List<object>();cache["calendar_url"]="";cache["fetched_at"]="";cache["status"]="CalDAV 未连接";Save(CachePath,cache);return;}Dictionary<string,object>c=JsonUtil.ReadDpapiJson(SecretPath);FetchResult r=Fetch(c);cache["events"]=r.Events.Cast<object>().ToList();cache["calendar_url"]=r.Calendar.Uri;cache["fetched_at"]=RuntimeUtil.Iso(DateTimeOffset.Now);cache["status"]="已同步 "+r.Events.Count+" 项";if(AutoConvert(cache,state))todo=true;Save(CachePath,cache);Save(StatePath,state);}catch(Exception ex){cache["status"]="同步失败："+ex.Message;Save(CachePath,cache);}}

    private static string CleanTitle(string t){return Regex.Replace(t??"",@"^\s*\[(?:待办|代办)\]\s*","").Trim();}
    private static string Target(Dictionary<string,object>e){foreach(string k in new[]{"url","location","description"}){Match m=Regex.Match(S(e,k),@"(?i)(?:https?://|wemeet://)[^\s<>\""'，。；;]+");if(m.Success)return m.Value.TrimEnd(')',']','}');}return "";}
    private static string FullTime(Dictionary<string,object>e){DateTimeOffset?start=RuntimeUtil.Date(e,"start_at"),end=RuntimeUtil.Date(e,"end_at");if(!start.HasValue)return"";if(B(e,"all_day")){DateTimeOffset last=end.HasValue?end.Value.AddDays(-1):start.Value;return last.Date==start.Value.Date?start.Value.ToString("yyyy年M月d日 全天"):start.Value.ToString("yyyy年M月d日")+"–"+last.ToString("yyyy年M月d日")+" 全天";}if(!end.HasValue||end<=start)return start.Value.ToString("yyyy年M月d日 HH:mm");return end.Value.Date==start.Value.Date?start.Value.ToString("yyyy年M月d日 HH:mm")+"–"+end.Value.ToString("HH:mm"):start.Value.ToString("yyyy年M月d日 HH:mm")+" → "+end.Value.ToString("yyyy年M月d日 HH:mm");}
    private static bool AddTask(Dictionary<string,object>e,Dictionary<string,object>state,string mode,bool hide){using(Mutex m=new Mutex(false,@"Global\RainmeterTodoState")){bool held=m.WaitOne(TimeSpan.FromSeconds(15));if(!held)throw new Exception("待办数据正忙，请稍后重试");try{if(!File.Exists(TodoPath))throw new Exception("未找到待办数据");Dictionary<string,object>todo=JsonUtil.LoadObject(TodoPath);List<Dictionary<string,object>>tasks=List(todo,"tasks");Dictionary<string,object>task=tasks.FirstOrDefault(t=>S(t,"calendar_occurrence_key")==S(e,"occurrence_key"));if(task!=null){if(!Conversions(state).Any(c=>S(c,"occurrence_key")==S(e,"occurrence_key")))Conversions(state).Add(Conversion(e,task,mode,hide));return false;}Conversions(state).RemoveAll(c=>S(c,"occurrence_key")==S(e,"occurrence_key"));DateTimeOffset?start=RuntimeUtil.Date(e,"start_at"),end=RuntimeUtil.Date(e,"end_at"),reminder=RuntimeUtil.Date(e,"reminder_at");if(B(e,"all_day")&&end.HasValue)end=end.Value.AddMinutes(-1);List<string>notes=new List<string>{"来自 CalDAV 日程","日程时间："+FullTime(e)};if(reminder.HasValue)notes.Add("最早提醒："+reminder.Value.ToString("yyyy年M月d日 HH:mm"));if(S(e,"location")!="")notes.Add("地点："+S(e,"location"));if(S(e,"description")!="")notes.Add("日程备注："+S(e,"description"));task=new Dictionary<string,object>{{"id",Guid.NewGuid().ToString("N")},{"title","（日程）"+CleanTitle(S(e,"title"))},{"target",Target(e)},{"note",String.Join("\r\n",notes)},{"labels",new List<object>{"日程"}},{"completed",false},{"source","caldav"},{"created_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"completed_at",null},{"available_from",reminder.HasValue?RuntimeUtil.Iso(reminder.Value):start.HasValue?RuntimeUtil.Iso(start.Value):null},{"due_at",end.HasValue?RuntimeUtil.Iso(end.Value):null},{"calendar_uid",S(e,"uid")},{"calendar_occurrence_key",S(e,"occurrence_key")}};tasks.Add(task);Save(TodoPath,todo);Conversions(state).Add(Conversion(e,task,mode,hide));return true;}finally{m.ReleaseMutex();}}}
    private static Dictionary<string,object> Conversion(Dictionary<string,object>e,Dictionary<string,object>task,string mode,bool hide){return new Dictionary<string,object>{{"occurrence_key",S(e,"occurrence_key")},{"uid",S(e,"uid")},{"recurrence_id",S(e,"recurrence_id")},{"task_id",S(task,"id")},{"converted_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"mode",mode},{"hide_event",hide}};}
    private static bool AutoConvert(Dictionary<string,object>cache,Dictionary<string,object>state){bool changed=false;foreach(Dictionary<string,object>e in Events(cache)){Dictionary<string,object>rule=Rules(state).FirstOrDefault(r=>S(r,"uid")==S(e,"uid"));if(Regex.IsMatch(S(e,"title"),@"^\s*\[(?:待办|代办)\]")&&rule==null){rule=new Dictionary<string,object>{{"uid",S(e,"uid")},{"title",CleanTitle(S(e,"title"))},{"effective_from",S(e,"start_at")},{"created_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"reason","title-tag"},{"hide_event",true}};Rules(state).Add(rule);changed=true;}if(rule!=null&&AddTask(e,state,"series",JsonUtil.Bool(rule,"hide_event",true)))changed=true;}return changed;}
    private sealed class Choice{public string Mode;public bool Hide;}
    private static Choice Choose(Dictionary<string,object> e)
    {
        Form f = DarkUi.Form("转为待办", 540, 340);
        DarkUi.Heading(f, "转为带时间待办", B(e, "recurring") ? "选择仅转换这一期，或让后续周期自动进入待办。" : "开始、结束和提醒时间会一并带入待办。");
        Label eventTitle = new Label { Text = CleanTitle(S(e, "title")), Left = 26, Top = 100, Width = 488, Height = 48, BackColor = DarkUi.Surface, ForeColor = DarkUi.Text, Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold), Padding = new Padding(14, 13, 14, 8) };
        DarkUi.Round(eventTitle, 10);
        f.Controls.Add(eventTitle);
        CheckBox hide = new CheckBox { Text = "转换后从今日日程磁贴隐藏", Checked = true, Left = 28, Top = 168, Width = 360, Height = 28, ForeColor = DarkUi.Text, BackColor = Color.Transparent, FlatStyle = FlatStyle.Flat };
        f.Controls.Add(hide); f.Controls.Add(DarkUi.Label("原事件仍保留在 CalDAV 和手机日历中。", 50, 199, 400));
        Button cancel = DarkUi.Button("取消", 222, 270, 86, DialogResult.Cancel);
        Button once = DarkUi.Button("仅本次", 318, 270, 86, DialogResult.OK);
        Button series = DarkUi.PrimaryButton("本次及今后", 414, 270, 100, DialogResult.Yes);
        if (!B(e, "recurring")) series.Visible = false;
        f.Controls.AddRange(new Control[] { cancel, once, series }); f.CancelButton = cancel;
        DialogResult result = f.ShowDialog();
        if (result != DialogResult.OK && result != DialogResult.Yes) return null;
        return new Choice { Mode = result == DialogResult.Yes ? "Series" : "Once", Hide = hide.Checked };
    }
    private static bool ConvertInteractive(Dictionary<string,object>e,Dictionary<string,object>state,Dictionary<string,object>cache){Choice c=Choose(e);if(c==null)return false;if(c.Mode=="Series"){Dictionary<string,object>rule=Rules(state).FirstOrDefault(r=>S(r,"uid")==S(e,"uid"));if(rule==null){rule=new Dictionary<string,object>{{"uid",S(e,"uid")},{"title",CleanTitle(S(e,"title"))},{"effective_from",S(e,"start_at")},{"created_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"reason","manual"}};Rules(state).Add(rule);}rule["hide_event"]=c.Hide;}bool added=AddTask(e,state,c.Mode=="Series"?"series":"single",c.Hide);if(added)cache["status"]=c.Hide?"已转为待办并从日程隐藏":"已转为待办，日程继续显示";return added;}
    private static bool ShowDetails(Dictionary<string,object> e, bool converted)
    {
        Form f = DarkUi.Form("日程详情", 680, 630);
        DarkUi.Heading(f, CleanTitle(S(e, "title")), "日程详情");
        DateTimeOffset? reminder = RuntimeUtil.Date(e, "reminder_at");
        Panel facts = new Panel { Left = 24, Top = 92, Width = 632, Height = 112, BackColor = DarkUi.Surface };
        DarkUi.Round(facts, 10);
        facts.Controls.Add(DarkUi.Label("时间", 16, 12, 64)); facts.Controls.Add(new Label { Text = FullTime(e), Left = 88, Top = 12, Width = 520, Height = 22, ForeColor = DarkUi.Text, BackColor = Color.Transparent });
        facts.Controls.Add(DarkUi.Label("提醒", 16, 44, 64)); facts.Controls.Add(new Label { Text = reminder.HasValue ? reminder.Value.ToString("yyyy年M月d日 HH:mm") : "未设置", Left = 88, Top = 44, Width = 520, Height = 22, ForeColor = DarkUi.Text, BackColor = Color.Transparent });
        facts.Controls.Add(DarkUi.Label("地点", 16, 76, 64)); facts.Controls.Add(new Label { Text = S(e, "location") == "" ? "未设置" : S(e, "location"), Left = 88, Top = 76, Width = 520, Height = 22, ForeColor = DarkUi.Text, BackColor = Color.Transparent });
        f.Controls.Add(facts); f.Controls.Add(DarkUi.Label("备注", 25, 222, 200));
        TextBox note = DarkUi.TextBox(24, 246, 632, S(e, "description") == "" ? "（没有备注）" : S(e, "description")); note.Multiline = true; note.ReadOnly = true; note.Height = 270; note.ScrollBars = ScrollBars.Vertical; f.Controls.Add(note);
        Button open = DarkUi.Button("打开会议 / 网页", 276, 552, 126, DialogResult.None);
        Button convert = DarkUi.PrimaryButton(converted ? "已转为待办" : "转为待办", 412, 552, 116, DialogResult.OK);
        Button close = DarkUi.Button("关闭", 538, 552, 118, DialogResult.Cancel);
        open.Visible = Target(e) != ""; open.Click += delegate { RuntimeUtil.Run(Target(e)); }; convert.Enabled = !converted;
        f.Controls.AddRange(new Control[] { open, convert, close }); f.CancelButton = close;
        return f.ShowDialog() == DialogResult.OK;
    }

    private static Dictionary<string,object> ReadCredentials()
    {
        if (!File.Exists(SecretPath)) return new Dictionary<string,object>();
        try { return JsonUtil.ReadDpapiJson(SecretPath); }
        catch { return new Dictionary<string,object>(); }
    }

    private static void SaveCredentials(TextBox server, TextBox username, TextBox password, Dictionary<string,object> cache)
    {
        string s = server.Text.Trim(), u = username.Text.Trim(), p = password.Text;
        if (s == "") s = "https://davis.manao.dpdns.org";
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
        if (u == "" || p == "") throw new Exception("账号和密码不能为空");
        JsonUtil.WriteDpapiJson(SecretPath, new Dictionary<string,object>{{"Server",s},{"Username",u},{"Password",p}});
        cache["status"] = "CalDAV 凭据已保存";
        Save(CachePath, cache);
    }

    private static Dictionary<string,object> CredentialsFromFields(TextBox server, TextBox username, TextBox password)
    {
        string s = server.Text.Trim(), u = username.Text.Trim(), p = password.Text;
        if (s == "") s = "https://davis.manao.dpdns.org";
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
        if (u == "" || p == "") throw new Exception("账号和密码不能为空");
        return new Dictionary<string,object>{{"Server",s},{"Username",u},{"Password",p}};
    }

    private static string TestCredentials(TextBox server, TextBox username, TextBox password)
    {
        CalendarInfo calendar = Discover(CredentialsFromFields(server, username, password));
        return "连接成功：" + (calendar.Name == "" ? calendar.Uri : calendar.Name);
    }

    private static void ClearCredentials(Dictionary<string,object> cache)
    {
        if (File.Exists(SecretPath)) File.Delete(SecretPath);
        cache["events"] = new List<object>();
        cache["calendar_url"] = "";
        cache["fetched_at"] = "";
        cache["status"] = "CalDAV 未连接";
        Save(CachePath, cache);
    }

    private static void FillRules(ListBox list, Dictionary<string,object> state)
    {
        list.Items.Clear();
        foreach (Dictionary<string,object> rule in Rules(state)) list.Items.Add(new KeyValuePair<Dictionary<string,object>,string>(rule, S(rule, "title") == "" ? "周期日程" : S(rule, "title")));
        if (list.Items.Count == 0) list.Items.Add(new KeyValuePair<Dictionary<string,object>,string>(null, "还没有周期自动转入规则"));
    }

    private static void StyleRuleList(ListBox list)
    {
        list.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.IntegralHeight = false;
        list.ItemHeight = Math.Max(32, list.Font.Height + 14);
        list.DrawItem += delegate(object sender, DrawItemEventArgs e) {
            if (e.Index < 0) return;
            ListBox source = (ListBox)sender;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color back = selected ? Color.FromArgb(11, 128, 214) : source.BackColor;
            Color fore = selected ? Color.White : source.ForeColor;
            using (SolidBrush background = new SolidBrush(back)) e.Graphics.FillRectangle(background, e.Bounds);
            string text = source.GetItemText(source.Items[e.Index]);
            Rectangle textBounds = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, source.Font, textBounds, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
    }

    private static void StyleTab(TabControl tabs)
    {
        tabs.Appearance = TabAppearance.FlatButtons;
        tabs.ItemSize = new Size(118, 34);
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
    }

    private static void DrawRound(Graphics graphics, Pen pen, float x, float y, float width, float height, float radius)
    {
        using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            float d = radius * 2F;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            graphics.DrawPath(pen, path);
        }
    }

    private static Panel IconPanel(string kind, int x, int y, int size, Color color)
    {
        Panel icon = new Panel { Left = x, Top = y, Width = size, Height = size, BackColor = Color.Transparent, Tag = kind };
        icon.Paint += delegate(object sender, PaintEventArgs e) {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, Math.Max(2F, size / 15F)))
            using (SolidBrush brush = new SolidBrush(color))
            {
                float w = size, h = size;
                string currentKind = Convert.ToString(((Control)sender).Tag, CultureInfo.InvariantCulture);
                if (currentKind == "calendar")
                {
                    DrawRound(e.Graphics, pen, 4, 7, w - 8, h - 11, 5);
                    e.Graphics.DrawLine(pen, 10, 13, w - 10, 13);
                    e.Graphics.FillRectangle(brush, 12, 3, 3, 8);
                    e.Graphics.FillRectangle(brush, w - 15, 3, 3, 8);
                    e.Graphics.FillRectangle(brush, 13, 20, 4, 4);
                    e.Graphics.FillRectangle(brush, 22, 20, 4, 4);
                    e.Graphics.FillRectangle(brush, 31, 20, 4, 4);
                }
                else if (currentKind == "globe")
                {
                    e.Graphics.DrawEllipse(pen, 4, 4, w - 8, h - 8);
                    e.Graphics.DrawLine(pen, w / 2, 5, w / 2, h - 5);
                    e.Graphics.DrawArc(pen, 11, 4, w - 22, h - 8, 90, 180);
                    e.Graphics.DrawArc(pen, 11, 4, w - 22, h - 8, -90, 180);
                    e.Graphics.DrawLine(pen, 6, h / 2, w - 6, h / 2);
                }
                else if (currentKind == "user")
                {
                    e.Graphics.DrawEllipse(pen, w / 2 - 7, 5, 14, 14);
                    e.Graphics.DrawArc(pen, 7, 21, w - 14, h - 18, 200, 140);
                    e.Graphics.DrawLine(pen, 11, h - 6, w - 11, h - 6);
                }
                else if (currentKind == "lock")
                {
                    e.Graphics.DrawArc(pen, w / 2 - 10, 5, 20, 20, 180, 180);
                    DrawRound(e.Graphics, pen, 8, 18, w - 16, h - 21, 4);
                    e.Graphics.FillEllipse(brush, w / 2 - 2, 27, 4, 4);
                }
                else if (currentKind == "shield")
                {
                    using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.StartFigure();
                        path.AddBezier(w / 2, 4, w / 2 + 7, 7, w - 8, 8, w - 7, 8);
                        path.AddBezier(w - 7, 8, w - 7, h - 8, w / 2 + 5, h - 4, w / 2, h - 3);
                        path.AddBezier(w / 2, h - 3, w / 2 - 5, h - 4, 7, h - 8, 7, 8);
                        path.AddBezier(7, 8, 8, 8, w / 2 - 7, 7, w / 2, 4);
                        path.CloseFigure();
                        e.Graphics.DrawPath(pen, path);
                    }
                    e.Graphics.DrawLine(pen, w / 2 - 6, h / 2, w / 2 - 1, h / 2 + 5);
                    e.Graphics.DrawLine(pen, w / 2 - 1, h / 2 + 5, w / 2 + 8, h / 2 - 5);
                }
                else if (currentKind == "eye" || currentKind == "eye-off")
                {
                    using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.StartFigure();
                        path.AddBezier(4, h / 2, w / 4, 6, w * 3 / 4, 6, w - 4, h / 2);
                        path.AddBezier(w - 4, h / 2, w * 3 / 4, h - 6, w / 4, h - 6, 4, h / 2);
                        path.CloseFigure();
                        e.Graphics.DrawPath(pen, path);
                    }
                    e.Graphics.DrawEllipse(pen, w / 2 - 5, h / 2 - 5, 10, 10);
                    using (SolidBrush pupil = new SolidBrush(color)) e.Graphics.FillEllipse(pupil, w / 2 - 2, h / 2 - 2, 4, 4);
                    if (currentKind == "eye-off") e.Graphics.DrawLine(pen, 5, h - 5, w - 5, 5);
                }
                else if (currentKind == "link")
                {
                    e.Graphics.DrawArc(pen, 7, 10, 18, 18, 120, 230);
                    e.Graphics.DrawArc(pen, w - 25, h - 28, 18, 18, -60, 230);
                    e.Graphics.DrawLine(pen, 18, 25, w - 18, 13);
                }
                else if (currentKind == "trash")
                {
                    e.Graphics.DrawLine(pen, 10, 12, w - 10, 12);
                    e.Graphics.DrawRectangle(pen, 13, 15, w - 26, h - 21);
                    e.Graphics.DrawLine(pen, w / 2 - 7, 8, w / 2 + 7, 8);
                }
                else if (currentKind == "save")
                {
                    DrawRound(e.Graphics, pen, 8, 6, w - 16, h - 12, 3);
                    e.Graphics.DrawRectangle(pen, 14, 7, w - 28, 10);
                    e.Graphics.DrawRectangle(pen, 15, h - 17, w - 30, 10);
                }
            }
        };
        return icon;
    }

    private static Panel RoundedPanel(int x, int y, int width, int height, Color fill, Color border, int radius)
    {
        Panel panel = new Panel { Left = x, Top = y, Width = width, Height = height, BackColor = fill };
        DarkUi.Round(panel, radius);
        panel.Paint += delegate(object sender, PaintEventArgs e) {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(border, 1F))
                DrawRound(e.Graphics, pen, 0, 0, panel.Width - 1, panel.Height - 1, radius);
        };
        return panel;
    }

    private static TextBox AddCredentialField(Panel parent, string icon, string label, int y, string text, bool password, out Panel reveal)
    {
        parent.Controls.Add(IconPanel(icon, 34, y + 13, 28, DarkUi.Accent));
        parent.Controls.Add(new Label { Text = label, Left = 84, Top = y + 13, Width = 180, Height = 24, ForeColor = DarkUi.Text, BackColor = Color.Transparent, Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F) });
        Panel box = RoundedPanel(84, y + 44, 560, 44, Color.FromArgb(252, 254, 255), Color.FromArgb(220, 230, 241), 10);
        TextBox input = new TextBox { Left = 14, Top = 10, Width = password ? 480 : 532, Height = 24, AutoSize = false, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(252, 254, 255), ForeColor = Color.FromArgb(5, 16, 34), Font = new System.Drawing.Font("Microsoft YaHei UI", 10F), Text = text ?? "" };
        input.UseSystemPasswordChar = password;
        box.Controls.Add(input);
        parent.Controls.Add(box);
        reveal = null;
        if (password)
        {
            reveal = IconPanel("eye-off", 516, 8, 28, Color.FromArgb(94, 111, 150));
            reveal.Cursor = Cursors.Hand;
            box.Controls.Add(reveal);
        }
        return input;
    }

    private static void Settings(Dictionary<string,object> state, Dictionary<string,object> cache, ref bool refreshTodo)
    {
        Dictionary<string,object> credentials = ReadCredentials();
        bool syncChangedTodo = false;
        Form f = DarkUi.Form("日程设置", 720, 560);
        Panel headerIcon = RoundedPanel(38, 34, 48, 48, DarkUi.AccentFill, Color.FromArgb(92, 162, 246), 14);
        headerIcon.Controls.Add(IconPanel("calendar", 8, 8, 32, Color.White));
        Label title = new Label { Text = "日程设置", Left = 112, Top = 34, Width = 240, Height = 36, BackColor = Color.Transparent, ForeColor = DarkUi.Text, Font = new System.Drawing.Font("Microsoft YaHei UI", 18F, System.Drawing.FontStyle.Bold) };
        Label subtitle = new Label { Text = "管理 CalDAV 同步账号和周期自动转入规则。", Left = 42, Top = 92, Width = 540, Height = 26, BackColor = Color.Transparent, ForeColor = Color.FromArgb(76, 94, 132), Font = new System.Drawing.Font("Microsoft YaHei UI", 10F) };
        Button closeTop = DarkUi.Button("×", 648, 38, 42, DialogResult.Cancel);
        closeTop.Height = 42;
        closeTop.Font = new System.Drawing.Font("Microsoft YaHei UI", 18F);
        closeTop.ForeColor = Color.FromArgb(37, 52, 82);
        f.Controls.AddRange(new Control[] { headerIcon, title, subtitle, closeTop });
        title.BringToFront();
        closeTop.BringToFront();

        Panel tabRail = RoundedPanel(42, 138, 272, 44, Color.FromArgb(235, 245, 253), Color.FromArgb(235, 245, 253), 11);
        Button tabAccount = DarkUi.Button("同步账号", 0, 0, 136, DialogResult.None);
        Button tabRules = DarkUi.Button("自动转入", 136, 0, 136, DialogResult.None);
        tabAccount.Height = tabRules.Height = 44;
        tabAccount.Font = tabRules.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold);
        tabRail.Controls.AddRange(new Control[] { tabAccount, tabRules });
        f.Controls.Add(tabRail);

        Panel accountPage = RoundedPanel(32, 196, 656, 286, Color.FromArgb(248, 252, 255), Color.FromArgb(224, 233, 244), 18);
        Panel rulePage = RoundedPanel(32, 196, 656, 286, Color.FromArgb(248, 252, 255), Color.FromArgb(224, 233, 244), 18);
        rulePage.Visible = false;
        f.Controls.AddRange(new Control[] { accountPage, rulePage });

        Action<bool> showAccount = delegate(bool account) {
            accountPage.Visible = account; rulePage.Visible = !account;
            tabAccount.BackColor = account ? Color.FromArgb(248, 252, 255) : Color.FromArgb(235, 245, 253);
            tabAccount.ForeColor = account ? DarkUi.Accent : DarkUi.Text;
            tabRules.BackColor = account ? Color.FromArgb(235, 245, 253) : Color.FromArgb(248, 252, 255);
            tabRules.ForeColor = account ? DarkUi.Text : DarkUi.Accent;
        };
        tabAccount.Click += delegate { showAccount(true); };
        tabRules.Click += delegate { showAccount(false); };

        Panel reveal;
        TextBox server = AddCredentialField(accountPage, "globe", "CalDAV 地址", 10, S(credentials, "Server") == "" ? "https://davis.manao.dpdns.org" : S(credentials, "Server"), false, out reveal);
        TextBox username = AddCredentialField(accountPage, "user", "账号", 92, S(credentials, "Username"), false, out reveal);
        TextBox password = AddCredentialField(accountPage, "lock", "密码", 174, S(credentials, "Password"), true, out reveal);
        if (reveal != null) reveal.Click += delegate { password.UseSystemPasswordChar = !password.UseSystemPasswordChar; reveal.Tag = password.UseSystemPasswordChar ? "eye-off" : "eye"; reveal.Invalidate(); };

        ListBox list = new ListBox { Left = 34, Top = 28, Width = 588, Height = 176, BackColor = Color.FromArgb(252, 254, 255), ForeColor = DarkUi.Text, DisplayMember = "Value", BorderStyle = BorderStyle.None };
        StyleRuleList(list);
        DarkUi.Round(list, 12);
        FillRules(list, state);
        rulePage.Controls.Add(list);
        rulePage.Controls.Add(new Label { Text = "停止规则只影响未来周期，已经生成的待办保持不变。", Left = 34, Top = 232, Width = 420, Height = 24, ForeColor = Color.FromArgb(76, 94, 132), BackColor = Color.Transparent, Font = new System.Drawing.Font("Microsoft YaHei UI", 9F) });
        Button stop = DarkUi.DangerButton("清除规则", 506, 224, 116, DialogResult.None);
        stop.Height = 40;
        stop.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
        rulePage.Controls.Add(stop);

        Label saveStatus = new Label { Text = S(cache, "status"), Left = 64, Top = 502, Width = 280, Height = 28, BackColor = Color.Transparent, ForeColor = S(cache, "status").Contains("成功") || S(cache, "status").Contains("已同步") ? Color.FromArgb(63, 178, 119) : Color.FromArgb(76, 94, 132), Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold) };
        Button clearAccount = DarkUi.DangerButton("清除设置", 364, 494, 98, DialogResult.None);
        Button testAccount = DarkUi.Button("测试连接", 476, 494, 98, DialogResult.None);
        Button saveAccount = DarkUi.PrimaryButton("保存凭据", 588, 494, 100, DialogResult.None);
        clearAccount.Height = testAccount.Height = saveAccount.Height = 38;
        clearAccount.Font = testAccount.Font = saveAccount.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
        f.Controls.AddRange(new Control[] { saveStatus, clearAccount, testAccount, saveAccount });
        showAccount(true);

        testAccount.Click += delegate {
            try { saveStatus.Text = "正在测试…"; saveStatus.ForeColor = Color.FromArgb(76, 94, 132); saveStatus.Refresh(); saveStatus.Text = TestCredentials(server, username, password); saveStatus.ForeColor = Color.FromArgb(63, 178, 119); }
            catch (Exception ex) { DarkUi.Error("连接失败：" + ex.Message); saveStatus.Text = "连接失败"; saveStatus.ForeColor = DarkUi.Danger; }
        };
        saveAccount.Click += delegate {
            try { SaveCredentials(server, username, password, cache); saveStatus.Text = "已保存"; saveStatus.ForeColor = Color.FromArgb(63, 178, 119); }
            catch (Exception ex) { DarkUi.Error(ex.Message); }
        };
        clearAccount.Click += delegate {
            try { ClearCredentials(cache); server.Text = "https://davis.manao.dpdns.org"; username.Text = ""; password.Text = ""; saveStatus.Text = "CalDAV 未连接"; saveStatus.ForeColor = Color.FromArgb(76, 94, 132); }
            catch (Exception ex) { DarkUi.Error(ex.Message); }
        };
        stop.Click += delegate {
            if (list.SelectedItem == null) return;
            Dictionary<string,object> selected = ((KeyValuePair<Dictionary<string,object>,string>)list.SelectedItem).Key;
            if (selected == null) return;
            Rules(state).Remove(selected);
            Save(StatePath, state);
            cache["status"] = "已停止该系列的未来自动转入";
            Save(CachePath, cache);
            FillRules(list, state);
            saveStatus.Text = "已停止该系列的未来自动转入";
            saveStatus.ForeColor = Color.FromArgb(76, 94, 132);
        };
        closeTop.Click += delegate { f.Close(); };
        f.CancelButton = closeTop;
        f.ShowDialog();
        if (syncChangedTodo) refreshTodo = true;
    }

    private static void Manage(Dictionary<string,object> state, Dictionary<string,object> cache)
    {
        while (true)
        {
            Form f = DarkUi.Form("周期自动转入规则", 620, 500);
            DarkUi.Heading(f, "周期自动转入规则", "停止规则只影响未来周期，已经生成的待办保持不变。");
            ListBox list = new ListBox { Left = 24, Top = 96, Width = 572, Height = 292, BackColor = DarkUi.Surface, ForeColor = DarkUi.Text, DisplayMember = "Value", BorderStyle = BorderStyle.FixedSingle };
            StyleRuleList(list);
            DarkUi.Round(list, 10);
            foreach (Dictionary<string,object> rule in Rules(state)) list.Items.Add(new KeyValuePair<Dictionary<string,object>,string>(rule, S(rule, "title") == "" ? "周期日程" : S(rule, "title")));
            if (list.Items.Count == 0) list.Items.Add(new KeyValuePair<Dictionary<string,object>,string>(null, "还没有周期自动转入规则"));
            f.Controls.Add(list); f.Controls.Add(DarkUi.Label("选择一条规则后可以停止未来自动转入。", 25, 404, 360));
            Button stop = DarkUi.DangerButton("停止自动转入", 374, 430, 130, DialogResult.OK), close = DarkUi.Button("关闭", 514, 430, 82, DialogResult.Cancel);
            f.Controls.AddRange(new Control[] { stop, close }); f.CancelButton = close;
            if (f.ShowDialog() != DialogResult.OK || list.SelectedItem == null) break;
            Dictionary<string,object> selected = ((KeyValuePair<Dictionary<string,object>,string>)list.SelectedItem).Key;
            if (selected == null) continue;
            Rules(state).Remove(selected); Save(StatePath, state); cache["status"] = "已停止该系列的未来自动转入"; Save(CachePath, cache);
        }
    }

    private static bool Render(Dictionary<string,object> cache, Dictionary<string,object> state)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset), end = start.AddDays(1);
        HashSet<string> hidden = new HashSet<string>(Conversions(state).Where(c => JsonUtil.Bool(c, "hide_event", true)).Select(c => S(c, "occurrence_key")));
        List<Dictionary<string,object>> events = Events(cache).Where(e => RuntimeUtil.Date(e, "start_at").HasValue && RuntimeUtil.Date(e, "end_at").HasValue && RuntimeUtil.Date(e, "start_at").Value < end && RuntimeUtil.Date(e, "end_at").Value > start && !hidden.Contains(S(e, "occurrence_key"))).OrderBy(e => RuntimeUtil.Date(e, "start_at")).ThenBy(e => RuntimeUtil.Date(e, "end_at")).ThenBy(e => S(e, "title")).ToList();

        List<string> lines = new List<string>(); int y = 86, row = 0;
        Meter(lines, "CalendarSummary", "Meter=String", "MeterStyle=StyleText", "X=23", "Y=49", "W=340", "H=16", "FontSize=9", "FontColor=#MutedColor#", "Text=" + now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN")) + "  ·  " + events.Count + " 项");
        int eventHeight = events.Count == 0 ? 54 : events.Sum(e => S(e, "location") == "" ? 44 : 56);
        Meter(lines, "EventSurface", "Meter=Shape", "X=16", "Y=" + y, "Shape=Rectangle 0,0,488," + eventHeight + ",14 | Fill Color 247,251,255,242 | Stroke Color 198,216,232,210 | StrokeWidth 1");
        if (events.Count == 0)
        {
            Meter(lines, "CalendarEmpty", "Meter=String", "MeterStyle=StyleText", "X=32", "Y=" + (y + 17), "W=456", "H=22", "FontColor=#MutedColor#", "Text=今天没有日程");
            y += eventHeight;
        }
        else
        {
            foreach (Dictionary<string,object> e in events)
            {
                row++; DateTimeOffset es = RuntimeUtil.Date(e, "start_at").Value, ee = RuntimeUtil.Date(e, "end_at").Value;
                bool conflict = events.Any(o => S(o, "id") != S(e, "id") && RuntimeUtil.Date(o, "start_at").Value < ee && RuntimeUtil.Date(o, "end_at").Value > es), ongoing = now >= es && now < ee, ended = now >= ee;
                string color = conflict ? "#ConflictColor#" : ongoing ? "#OngoingColor#" : ended ? "#MutedColor#" : "#TextColor#";
                string time = TimeLabel(e, start), title = RuntimeUtil.CleanRainmeter(S(e, "title")); int rowHeight = S(e, "location") == "" ? 44 : 56;
                string action = "LeftMouseUpAction=[\"#@#CalendarHost.exe\" \"Detail\" \"" + S(e, "id") + "\"]";
                Meter(lines, "EventTime" + row, "Meter=String", "MeterStyle=StyleText", "X=30", "Y=" + (y + 11), "W=74", "H=22", "FontSize=9", "FontWeight=600", "FontColor=" + color, "Text=" + time, action);
                Meter(lines, "EventTitle" + row, "Meter=String", "MeterStyle=StyleText", "X=114", "Y=" + (y + 9), "W=374", "H=22", "FontColor=" + color, "Text=" + title, action);
                if (S(e, "location") != "") Meter(lines, "EventLocation" + row, "Meter=String", "MeterStyle=StyleText", "X=114", "Y=" + (y + 30), "W=374", "H=18", "FontSize=9", "FontColor=#MutedColor#", "Text=" + RuntimeUtil.CleanRainmeter(S(e, "location")), action);
                if (row < events.Count) Meter(lines, "EventDivider" + row, "Meter=Shape", "X=114", "Y=" + (y + rowHeight - 1), "Shape=Rectangle 0,0,374,1 | Fill Color 202,218,232,170 | StrokeWidth 0");
                y += rowHeight;
            }
        }

        string status = RuntimeUtil.CleanRainmeter(S(cache, "status")); y += 18;
        Meter(lines, "FooterRule", "Meter=Shape", "X=22", "Y=" + y, "Shape=Rectangle 0,0,476,1 | Fill Color 198,216,232,210 | StrokeWidth 0");
        Meter(lines, "CalendarStatus", "Meter=String", "MeterStyle=StyleText", "X=23", "Y=" + (y + 13), "W=470", "H=18", "FontSize=9", "FontColor=#MutedColor#", "Text=" + status, "ToolTipText=" + status); y += 42;
        Meter(lines, "BottomSpacer", "Meter=Shape", "X=0", "Y=" + y, "Shape=Rectangle 0,0,520,1 | Fill Color 0,0,0,0 | StrokeWidth 0");
        List<string> output = new List<string>();
        Meter(output, "Panel", "Meter=Shape", "X=0", "Y=0", "Shape=Rectangle 1,1,518," + (y - 1) + ",18 | Fill Color 239,248,255,248 | Stroke Color 198,216,232,210 | StrokeWidth 1");
        Meter(output, "PanelHighlight", "Meter=Shape", "X=18", "Y=1", "Shape=Rectangle 0,0,482,1 | Fill Color 255,255,255,180 | StrokeWidth 0");
        output.AddRange(lines);
        return RuntimeUtil.WriteUtf16IfChanged(IncludePath, String.Join("\r\n", output) + "\r\n");
    }
    private static string TimeLabel(Dictionary<string,object>e,DateTimeOffset day){DateTimeOffset s=RuntimeUtil.Date(e,"start_at").Value,x=RuntimeUtil.Date(e,"end_at").Value,next=day.AddDays(1);if(B(e,"all_day"))return s.Date<day.Date||x.Date>next.Date?"全天 · 延续":"全天";if(s<day&&x>next)return"全天 · 延续";if(s<day)return"延续–"+x.ToString("HH:mm");if(x>next)return s.ToString("HH:mm")+"–次日";return x<=s?s.ToString("HH:mm"):s.ToString("HH:mm")+"–"+x.ToString("HH:mm");}
    private static void Meter(List<string>l,string n,params string[]b){l.Add("["+n+"]");l.AddRange(b);l.Add("");}
    private static void MarkGuard(){File.WriteAllText(GuardPath,RuntimeUtil.Iso(DateTimeOffset.Now),RuntimeUtil.Utf8NoBom);}private static bool ConsumeGuard(){if(!File.Exists(GuardPath))return false;try{bool fresh=(DateTime.Now-File.GetLastWriteTime(GuardPath)).TotalSeconds<20;File.Delete(GuardPath);return fresh;}catch{return true;}}
}
