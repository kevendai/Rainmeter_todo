using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal static class CalendarToTodoPlugin
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024};
    private static int Main()
    {
        Console.InputEncoding=System.Text.Encoding.UTF8;Console.OutputEncoding=System.Text.Encoding.UTF8;
        string requestId="";
        try
        {
            Dictionary<string,object> request=Obj(Json.DeserializeObject(Console.In.ReadLine()??""));
            requestId=S(request,"request_id");
            if(S(request,"action")!="transform")return Result(requestId,false,null,"不支持的 action");
            Dictionary<string,object> e=Obj(Get(request,"input"));
            string external=S(e,"occurrence_key"),title=CleanTitle(S(e,"title"));
            if(external==""||title=="")return Result(requestId,false,null,"事件缺少 occurrence_key 或标题");
            DateTimeOffset? start=Date(e,"start_at"),end=Date(e,"end_at"),reminder=Date(e,"reminder_at");
            DateTimeOffset? available=start.HasValue&&reminder.HasValue?(start.Value<=reminder.Value?start:reminder):(reminder.HasValue?reminder:start);
            if(B(e,"all_day")&&end.HasValue)end=end.Value.AddMinutes(-1);
            string source=S(e,"source")=="local"?"本地":"CalDAV";
            List<string> notes=new List<string>{"来自 "+source+" 日程","日程时间："+FullTime(e)};
            if(reminder.HasValue)notes.Add("最早提醒："+reminder.Value.ToString("yyyy年M月d日 HH:mm"));
            if(S(e,"location")!="")notes.Add("地点："+S(e,"location"));
            if(S(e,"description")!="")notes.Add("日程备注："+S(e,"description"));
            Dictionary<string,object> task=new Dictionary<string,object>{{"external_id",external},{"title","（日程）"+title},{"target",Target(e)},{"note",String.Join("\r\n",notes)},{"labels",new object[]{"日程"}},{"available_from",available.HasValue?available.Value.ToString("o"):null},{"due_at",end.HasValue?end.Value.ToString("o"):null},{"policy",new Dictionary<string,object>()}};
            return Result(requestId,true,new Dictionary<string,object>{{"task",task}},null);
        }catch(Exception ex){return Result(requestId,false,null,ex.Message);}
    }
    private static string CleanTitle(string t){return Regex.Replace(t??"",@"^\s*\[(?:待办|代办)\]\s*","").Trim();}
    private static string Target(Dictionary<string,object> e){string direct=S(e,"url").Trim();Uri u;if(Uri.TryCreate(direct,UriKind.Absolute,out u)&&(u.Scheme=="http"||u.Scheme=="https"||u.Scheme=="wemeet"||u.IsFile))return u.IsFile?u.LocalPath:direct;foreach(string k in new[]{"location","description"}){Match m=Regex.Match(S(e,k),@"(?i)(?:https?://|wemeet://|file:///)[^\s<>]+");if(m.Success)return m.Value.TrimEnd(')',']','}','，','。','；',';');}return "";}
    private static string FullTime(Dictionary<string,object> e){DateTimeOffset? s=Date(e,"start_at"),n=Date(e,"end_at");if(!s.HasValue)return "";if(B(e,"all_day")){DateTimeOffset last=n.HasValue?n.Value.AddDays(-1):s.Value;return last.Date==s.Value.Date?s.Value.ToString("yyyy年M月d日 全天"):s.Value.ToString("yyyy年M月d日")+"–"+last.ToString("yyyy年M月d日")+" 全天";}if(!n.HasValue||n<=s)return s.Value.ToString("yyyy年M月d日 HH:mm");return n.Value.Date==s.Value.Date?s.Value.ToString("yyyy年M月d日 HH:mm")+"–"+n.Value.ToString("HH:mm"):s.Value.ToString("yyyy年M月d日 HH:mm")+" → "+n.Value.ToString("yyyy年M月d日 HH:mm");}
    private static int Result(string id,bool ok,object payload,string error){Console.Out.WriteLine(Json.Serialize(new Dictionary<string,object>{{"type","result"},{"request_id",id},{"ok",ok},{"payload",payload??new Dictionary<string,object>()},{"error",error??""}}));return ok?0:1;}
    private static Dictionary<string,object> Obj(object v){return v as Dictionary<string,object>??new Dictionary<string,object>();}
    private static object Get(Dictionary<string,object> v,string k){object x;return v.TryGetValue(k,out x)?x:null;}
    private static string S(Dictionary<string,object> v,string k){object x=Get(v,k);return x==null?"":Convert.ToString(x);}
    private static bool B(Dictionary<string,object> v,string k){bool x;return Boolean.TryParse(S(v,k),out x)&&x;}
    private static DateTimeOffset? Date(Dictionary<string,object> v,string k){DateTimeOffset x;return DateTimeOffset.TryParse(S(v,k),CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out x)?x:(DateTimeOffset?)null;}
}
