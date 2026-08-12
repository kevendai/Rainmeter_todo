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
    private static bool ResourceContainsRecurringUid(string raw,string uid)
    {
        RecurrenceMetadata metadata;return ParseRecurrenceMetadata(raw).TryGetValue(uid,out metadata)&&metadata.Recurring;
    }
    private static void RefreshCalDavCache(Dictionary<string,object> cache,Dictionary<string,object> credentials,string status)
    {
        FetchResult r=Fetch(credentials,S(cache,"calendar_url"));
        cache["events"]=r.Events.Cast<object>().ToList();
        cache["calendar_url"]=r.Calendar.Uri;
        cache["fetched_at"]=RuntimeUtil.Iso(DateTimeOffset.Now);
        cache["status"]=status+(r.FailedWindows>0?"（"+r.FailedWindows+" 次查询超时）":"");
    }
    private static void Sync(Dictionary<string,object>cache,Dictionary<string,object>state,ref bool todo){try{if(!File.Exists(SecretPath)){cache["events"]=new List<object>();cache["calendar_url"]="";cache["fetched_at"]="";cache["status"]="CalDAV 未连接";if(AutoConvert(cache,state))todo=true;Save(CachePath,cache);Save(StatePath,state);return;}Dictionary<string,object>c=JsonUtil.ReadDpapiJson(SecretPath);FetchResult r=Fetch(c,S(cache,"calendar_url"));cache["events"]=r.Events.Cast<object>().ToList();cache["calendar_url"]=r.Calendar.Uri;cache["fetched_at"]=RuntimeUtil.Iso(DateTimeOffset.Now);cache["status"]="已同步 "+r.Events.Count+" 项"+(r.FailedWindows>0?"（"+r.FailedWindows+" 次查询超时）":"");if(AutoConvert(cache,state))todo=true;Save(CachePath,cache);Save(StatePath,state);}catch(Exception ex){cache["status"]="同步失败："+ex.Message;Save(CachePath,cache);}}
    private static System.Diagnostics.Process StartBackgroundSync(){try{return System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath,"Sync"){UseShellExecute=false,CreateNoWindow=true});}catch{return null;}}

    private static bool SaveLocalEvent(Dictionary<string,object> e,Dictionary<string,object> state)
    {
        List<Dictionary<string,object>> local=LocalEvents(state);Dictionary<string,object> old=local.FirstOrDefault(x=>S(x,"id")==S(e,"id"));if(old!=null)local.Remove(old);local.Add(e);return true;
    }

    private static void DeleteLocalEvent(Dictionary<string,object> e,Dictionary<string,object> state,string mode="series")
    {
        if(B(e,"recurring")&&mode=="once"){if(!HiddenEvents(state).Any(x=>S(x,"occurrence_key")==S(e,"occurrence_key")))HiddenEvents(state).Add(new Dictionary<string,object>{{"id",S(e,"id")},{"uid",S(e,"uid")},{"occurrence_key",S(e,"occurrence_key")},{"hidden_at",RuntimeUtil.Iso(DateTimeOffset.Now)}});return;}
        Dictionary<string,object> master=LocalSeriesMaster(state,e);string id=master==null?S(e,"id"):S(master,"id"),uid=S(e,"uid");LocalEvents(state).RemoveAll(x=>S(x,"id")==id||(B(e,"recurring")&&S(x,"uid")==uid));
    }

    private static void SaveCalDavEvent(Dictionary<string,object> e,Dictionary<string,object> cache)
    {
        Dictionary<string,object> credentials=ReadCredentials();if(credentials.Count==0)throw new Exception("CalDAV 未连接，请先在日程设置里保存账号。");
        string href=S(e,"href");bool creating=href=="";if(creating){CalendarInfo cal=Discover(credentials);href=cal.Uri.TrimEnd('/')+"/"+S(e,"uid")+".ics";e["href"]=href;}else{DavResult existing=Dav("GET",href,credentials,"",-1);if(existing.Status<200||existing.Status>=300)throw new Exception("CalDAV 原始日程读取失败：HTTP "+existing.Status);if(ResourceContainsRecurringUid(existing.Text,S(e,"uid")))throw new Exception("服务器上的这个资源实际是周期日程，但本地周期元数据尚未校准。为避免覆盖整组，已停止保存；请先刷新同步后再编辑。");}
        DavResult put=DavText("PUT",href,credentials,EventIcs(e),"text/calendar; charset=utf-8",creating?"*":S(e,"etag"));
        if(put.Status==412&&creating)throw new Exception("CalDAV 中已存在同名资源，请重新保存生成新的日程标识");if(put.Status<200||put.Status>=300)throw new Exception("CalDAV 保存失败：HTTP "+put.Status);
        try{RefreshCalDavCache(cache,credentials,"已保存并同步 CalDAV");}
        catch{Events(cache).RemoveAll(x=>S(x,"id")==S(e,"id")||S(x,"uid")==S(e,"uid"));Events(cache).Add(e);cache["status"]="已保存到 CalDAV，本地缓存将在下次同步校准";}
    }

    private static void SaveCalDavSeriesEvent(Dictionary<string,object> e,Dictionary<string,object> cache)
    {
        if(S(e,"href")=="")throw new Exception("这个 CalDAV 周期日程缺少资源地址，请先同步后再编辑。");
        Dictionary<string,object> credentials=ReadCredentials();if(credentials.Count==0)throw new Exception("CalDAV 未连接，请先在日程设置里保存账号。");
        DavResult get=Dav("GET",S(e,"href"),credentials,"",-1);if(get.Status<200||get.Status>=300)throw new Exception("CalDAV 原始日程读取失败：HTTP "+get.Status);
        string ics=UpdateSeriesIcs(get.Text,e);DavResult put=DavText("PUT",S(e,"href"),credentials,ics,"text/calendar; charset=utf-8",S(e,"etag"));
        if(put.Status<200||put.Status>=300)throw new Exception("CalDAV 周期日程保存失败：HTTP "+put.Status);
        try{RefreshCalDavCache(cache,credentials,"已改写并同步 CalDAV 周期日程整组");}
        catch{cache["status"]="已改写 CalDAV 周期日程整组，本地缓存将在下次同步校准";}
    }

    private static void DeleteCalDavEvent(Dictionary<string,object> e,Dictionary<string,object> cache,Dictionary<string,object> state,string mode)
    {
        if(S(e,"href")=="")throw new Exception("这个 CalDAV 日程缺少资源地址，请先同步后再删除。");
        Dictionary<string,object> credentials=ReadCredentials();if(credentials.Count==0)throw new Exception("CalDAV 未连接。");
        if(!B(e,"recurring")){DavResult existing=Dav("GET",S(e,"href"),credentials,"",-1);if(existing.Status<200||existing.Status>=300)throw new Exception("CalDAV 原始日程读取失败：HTTP "+existing.Status);if(ResourceContainsRecurringUid(existing.Text,S(e,"uid")))throw new Exception("服务器上的这个资源实际是周期日程，但本地周期元数据尚未校准。为避免误删整组，已停止删除；请先刷新同步后再操作。");}
        if(B(e,"recurring")&&mode=="once"){if(!HiddenEvents(state).Any(x=>S(x,"occurrence_key")==S(e,"occurrence_key")))HiddenEvents(state).Add(new Dictionary<string,object>{{"id",S(e,"id")},{"uid",S(e,"uid")},{"occurrence_key",S(e,"occurrence_key")},{"hidden_at",RuntimeUtil.Iso(DateTimeOffset.Now)}});cache["status"]="已从本机隐藏本次周期日程";return;}
        DavResult del=DavText("DELETE",S(e,"href"),credentials,"","text/calendar; charset=utf-8",S(e,"etag"));
        if(del.Status<200||del.Status>=300)throw new Exception("CalDAV 删除失败：HTTP "+del.Status);
        if(B(e,"recurring")){Events(cache).RemoveAll(x=>S(x,"uid")==S(e,"uid"));Rules(state).RemoveAll(x=>S(x,"uid")==S(e,"uid"));}else Events(cache).RemoveAll(x=>S(x,"id")==S(e,"id"));cache["status"]="已从 CalDAV 删除";
    }
}
