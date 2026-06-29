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
    private static Dictionary<string,object> NewCache(){return new Dictionary<string,object>{{"version",1},{"fetched_at",""},{"calendar_url",""},{"status",File.Exists(SecretPath)?"尚未同步":"CalDAV 未连接"},{"events",new List<object>()}};}
    private static Dictionary<string,object> NewState(){return new Dictionary<string,object>{{"version",1},{"series_rules",new List<object>()},{"conversions",new List<object>()},{"local_events",new List<object>()},{"hidden_events",new List<object>()}};}
    private static Dictionary<string,object> Load(string path,Dictionary<string,object> fallback){if(!File.Exists(path))return fallback;try{return JsonUtil.LoadObject(path);}catch{File.Copy(path,path+".corrupt-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"),true);return fallback;}}
    private static void Shape(Dictionary<string,object> cache,Dictionary<string,object> state){if(JsonUtil.Get(cache,"events")==null)cache["events"]=new List<object>();if(JsonUtil.Get(cache,"status")==null)cache["status"]=File.Exists(SecretPath)?"就绪":"CalDAV 未连接";if(JsonUtil.Get(state,"series_rules")==null)state["series_rules"]=new List<object>();if(JsonUtil.Get(state,"conversions")==null)state["conversions"]=new List<object>();if(JsonUtil.Get(state,"local_events")==null)state["local_events"]=new List<object>();if(JsonUtil.Get(state,"hidden_events")==null)state["hidden_events"]=new List<object>();}
    private static void Save(string path,object value){JsonUtil.SaveAtomic(path,value);}
    private static string S(Dictionary<string,object> v,string k){return JsonUtil.String(v,k,"");}
    private static bool B(Dictionary<string,object> v,string k){return JsonUtil.Bool(v,k,false);}
    private static List<Dictionary<string,object>> List(Dictionary<string,object> root,string key){List<Dictionary<string,object>> result=JsonUtil.Array(JsonUtil.Get(root,key)).Select(JsonUtil.Object).ToList();root[key]=result;return result;}
    private static List<Dictionary<string,object>> Events(Dictionary<string,object> c){return List(c,"events");} private static List<Dictionary<string,object>> LocalEvents(Dictionary<string,object>s){return List(s,"local_events");} private static List<Dictionary<string,object>> HiddenEvents(Dictionary<string,object>s){return List(s,"hidden_events");} private static List<Dictionary<string,object>> Rules(Dictionary<string,object>s){return List(s,"series_rules");} private static List<Dictionary<string,object>> Conversions(Dictionary<string,object>s){return List(s,"conversions");}
    private static IEnumerable<Dictionary<string,object>> AllEvents(Dictionary<string,object> cache,Dictionary<string,object> state){foreach(Dictionary<string,object> e in Events(cache)){e["source"]="caldav";if(S(e,"calendar")=="")e["calendar"]="caldav";yield return e;}foreach(Dictionary<string,object> e in LocalEvents(state)){e["source"]="local";if(S(e,"calendar")=="")e["calendar"]="local";yield return e;}}
    private static Dictionary<string,object> FindEvent(Dictionary<string,object> cache,Dictionary<string,object> state,string id){return AllEvents(cache,state).FirstOrDefault(e=>S(e,"id")==id);}

    private static bool Reconcile(Dictionary<string,object> state){if(!File.Exists(TodoPath))return false;try{Dictionary<string,object>todo=JsonUtil.LoadObject(TodoPath);List<Dictionary<string,object>>tasks=List(todo,"tasks");HashSet<string>ids=new HashSet<string>(tasks.Select(t=>S(t,"id")));HashSet<string>keys=new HashSet<string>(tasks.Select(t=>S(t,"calendar_occurrence_key")));List<Dictionary<string,object>>conversions=Conversions(state);int before=conversions.Count;conversions.RemoveAll(c=>!ids.Contains(S(c,"task_id"))&&!keys.Contains(S(c,"occurrence_key")));return before!=conversions.Count;}catch{return false;}}
}
