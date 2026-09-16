using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// CI 用的假插件：同一个 exe 既当 consumer（manifest 里 uses ai_provider@1）又当 provider
// （manifest 里 provides ai_provider@1）。它把「consumer → Broker → provider」这条链真正跑通，
// 且**绝不发任何网络请求**，所以 CI 不需要 DeepSeek / Ollama / 任何凭据（规格 §11、§12）。
internal static class FakeProvider
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    private static readonly UTF8Encoding Utf8=new UTF8Encoding(false);

    private static int Main(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;
        Dictionary<string,object> request;
        try{request=(Dictionary<string,object>)Json.DeserializeObject(Console.In.ReadLine()??"");}catch{return 2;}
        string requestId=Text(request,"request_id"),action=Text(request,"action");
        Dictionary<string,object> payload=new Dictionary<string,object>();
        bool ok=true;string status="ok",error="";
        switch(action)
        {
            case "dump_env":
                payload["env"]=DumpEnvironment();
                break;
            case "structured_complete":
                payload["json"]=new Dictionary<string,object>{{"scores",new Dictionary<string,object>{{"1",8},{"2",42}}}};
                payload["usage"]=new Dictionary<string,object>{{"prompt_tokens",10},{"completion_tokens",4}};
                break;
            case "needs_attention":
                // 正式协议结果：ok=true + status=attention（规格 §4.3）。
                status="attention";
                payload["attention"]=new Dictionary<string,object>{
                    {"type","paid_service_confirmation"},{"service","ai_provider@1"},
                    {"message","远端论文同步失败，是否使用 DeepSeek AI 重新评分？"},
                    {"resume_action","sync_with_ai"},
                    {"resume_input",new Dictionary<string,object>{{"allow_paid_ai",true}}}};
                break;
            case "fail_fatal":
                ok=false;error="DeepSeek 账户余额不足（HTTP 402），请充值后重新同步论文";payload["fatal"]=true;
                break;
            case "slow_structured_complete":
                Thread.Sleep(15000);
                payload["json"]=new Dictionary<string,object>{{"scores",new Dictionary<string,object>{{"1",7}}}};
                break;
            case "hang":
                Thread.Sleep(120000);
                break;
            case "call_service":
                payload=CallService();
                if(payload.ContainsKey("__ok")){ok=(bool)payload["__ok"];payload.Remove("__ok");}
                break;
            case "sync":
                Thread.Sleep(4000);
                payload["tasks"]=new List<object>();
                payload["summary"]="已完成（假插件）";
                break;
            case "sleep":
                Thread.Sleep(60000);
                break;
        }
        Dictionary<string,object> result=new Dictionary<string,object>{
            {"type","result"},{"request_id",requestId},{"ok",ok},{"status",status},{"payload",payload},{"error",error}};
        Console.WriteLine(Json.Serialize(result));
        return 0;
    }

    // 假 consumer 的一半：读宿主注入的 RW_PLUGIN_HOST_EXE，写请求文件，跑一次 Broker，等它结束。
    private static Dictionary<string,object> CallService()
    {
        Dictionary<string,object> payload=new Dictionary<string,object>();
        string host=Environment.GetEnvironmentVariable("RW_PLUGIN_HOST_EXE")??"";
        if(String.IsNullOrEmpty(host)){payload["broker_error"]="缺少 RW_PLUGIN_HOST_EXE";payload["__ok"]=false;return payload;}
        string dir=Path.Combine(Path.GetTempPath(),"rwfake-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string requestPath=Path.Combine(dir,"request.json"),outputPath=Path.Combine(dir,"output.json");
        File.WriteAllText(requestPath,Json.Serialize(new Dictionary<string,object>{
            {"protocol",1},{"request_id","fake-1"},{"service","ai_provider@1"},
            {"action","slow_structured_complete"},{"input",new Dictionary<string,object>()},
            {"timeout_seconds",120}}),Utf8);
        ProcessStartInfo info=new ProcessStartInfo(host,"-Mode ServiceCall -RequestFile \""+requestPath+"\" -OutputFile \""+outputPath+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        using(Process broker=Process.Start(info))
        {
            string stderr=broker.StandardError.ReadToEnd();
            broker.WaitForExit();
            payload["broker_exit_code"]=broker.ExitCode;
            payload["broker_stderr"]=stderr;
        }
        payload["broker_response"]=File.Exists(outputPath)?(object)Json.DeserializeObject(File.ReadAllText(outputPath)):"<missing>";
        return payload;
    }

    private static Dictionary<string,object> DumpEnvironment()
    {
        Dictionary<string,object> map=new Dictionary<string,object>();
        foreach(string name in new[]{"RW_PLUGIN_ID","RW_PLUGIN_JOB_ID","RW_PLUGIN_HOST_EXE","RW_PLUGIN_PID","RW_SERVICE_CALL_DEPTH","RW_SERVICE_AI_PROVIDER_PROVIDER","RW_SERVICE_AI_PROVIDER_NAME","RW_PLUGIN_DATA_DIR","RW_WINDOW_SCALE"})
            map[name]=Environment.GetEnvironmentVariable(name)??"";
        return map;
    }

    private static string Text(Dictionary<string,object> source,string key)
    {
        object value;
        if(source==null||!source.TryGetValue(key,out value)||value==null)return "";
        return Convert.ToString(value);
    }
}
