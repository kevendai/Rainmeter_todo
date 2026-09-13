using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

internal static class NetworkIpPlugin
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024};
    private static int Main()
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;
        string requestId="";
        try
        {
            Dictionary<string,object> request=Obj(Json.DeserializeObject(Console.In.ReadLine()??""));
            requestId=Str(request,"request_id","");
            string action=Str(request,"action","");if(action!="get_values"&&action!="validate_settings")return Result(requestId,false,null,"不支持的 action");
            Dictionary<string,object> config=Obj(Get(request,"config"));
            Dictionary<string,object> secret=Obj(Get(request,"secret"));
            string url=Str(config,"url","https://api.ipify.org?format=json"),path=Str(config,"json_path","ip"),name=Str(config,"value_name","wan_ip");
            if(!url.StartsWith("https://",StringComparison.OrdinalIgnoreCase))return Result(requestId,false,null,"v1 只允许 HTTPS 地址");
            if(path.Trim()==""||name.Trim()=="")return Result(requestId,false,null,"字段路径和变量名不能为空");
            if(action=="validate_settings")return Result(requestId,true,new Dictionary<string,object>{{"message","设置有效"}},null);
            string body;
            using(WebClient client=new WebClient()){
                client.Encoding=Encoding.UTF8;client.Headers["User-Agent"]="RainmeterDesktopWidgets-NetworkIP/1.0";string auth=Str(config,"auth_type","none");
                if(auth=="bearer"){string token=Str(secret,"token","");if(token=="")return Result(requestId,false,null,"Bearer Token 未配置");client.Headers[HttpRequestHeader.Authorization]="Bearer "+token;}
                else if(auth=="basic"){string user=Str(config,"username",""),password=Str(secret,"password","");if(user=="")return Result(requestId,false,null,"Basic 用户名未配置");client.Headers[HttpRequestHeader.Authorization]="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(user+":"+password));}
                else if(auth!="none")return Result(requestId,false,null,"未知认证方式");body=client.DownloadString(url);
            }
            object value=Json.DeserializeObject(body);foreach(string part in path.Split('.'))value=Get(Obj(value),part);
            if(value==null||String.IsNullOrWhiteSpace(Convert.ToString(value)))return Result(requestId,false,null,"响应中没有目标字段");
            int ttl=Int(config,"ttl",300);ttl=Math.Max(30,Math.Min(86400,ttl));
            return Result(requestId,true,new Dictionary<string,object>{{"values",new Dictionary<string,object>{{name,value}}},{"ttl",ttl}},null);
        }catch(Exception ex){return Result(requestId,false,null,ex.Message);}
    }
    private static int Result(string id,bool ok,object payload,string error){Console.Out.WriteLine(Json.Serialize(new Dictionary<string,object>{{"type","result"},{"request_id",id},{"ok",ok},{"payload",payload??new Dictionary<string,object>()},{"error",error??""}}));return ok?0:1;}
    private static Dictionary<string,object> Obj(object v){return v as Dictionary<string,object>??new Dictionary<string,object>();}
    private static object Get(Dictionary<string,object> v,string k){object x;return v.TryGetValue(k,out x)?x:null;}
    private static string Str(Dictionary<string,object> v,string k,string d){object x=Get(v,k);return x==null?d:Convert.ToString(x);}
    private static int Int(Dictionary<string,object> v,string k,int d){int x;return Int32.TryParse(Str(v,k,""),out x)?x:d;}
}
