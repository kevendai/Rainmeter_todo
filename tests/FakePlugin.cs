using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

internal static class FakePlugin
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    private static int Main()
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;Dictionary<string,object> request=(Dictionary<string,object>)Json.DeserializeObject(Console.In.ReadLine()??"");string id=Convert.ToString(request["request_id"]),action=Convert.ToString(request["action"]);
        if(action=="invalid_json"){Console.WriteLine("not-json");return 0;}if(action=="no_result")return 0;if(action=="crash")return 7;
        if(action=="sleep")System.Threading.Thread.Sleep(60000);
        Dictionary<string,object> result=new Dictionary<string,object>{{"type","result"},{"request_id",id},{"ok",true},{"payload",new Dictionary<string,object>()},{"error",""}};
        if(action=="get_values")
        {
            Dictionary<string,object> config=request["config"] as Dictionary<string,object>;
            if(config!=null&&config.ContainsKey("fail")&&Convert.ToBoolean(config["fail"])){result["ok"]=false;result["error"]="provider failed";}
            else result["payload"]=new Dictionary<string,object>{{"values",new Dictionary<string,object>{{"wan_ip","测试地址"}}},{"ttl",60}};
            if(config!=null&&config.ContainsKey("emit_secret")&&Convert.ToBoolean(config["emit_secret"])){((Dictionary<string,object>)result["payload"])["secret_updates"]=new Dictionary<string,object>{{"session_token",new Dictionary<string,object>{{"value","encrypted-by-host"}}}};((Dictionary<string,object>)result["payload"])["config_updates"]=new Dictionary<string,object>{{"selected_did","test-device"}};}
        }
        if(action=="progress"){Console.WriteLine(Json.Serialize(new Dictionary<string,object>{{"type","progress"},{"request_id",id},{"current",1},{"total",2},{"message","中文进度"}}));System.Threading.Thread.Sleep(1200);}Console.WriteLine(Json.Serialize(result));if(action=="duplicate_result")Console.WriteLine(Json.Serialize(result));return 0;
    }
}
