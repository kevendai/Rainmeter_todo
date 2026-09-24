using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using RainmeterBackend;

internal static partial class CalendarApp
{
    private static int HandleServerUi(string action,string inputPath,string resultPath)
    {
        string output=action=="UiServerModel"?inputPath:resultPath;
        try
        {
            if(String.IsNullOrWhiteSpace(output))throw new InvalidDataException("缺少日程服务器操作结果路径。");
            Dictionary<string,object> result;
            if(action=="UiServerModel")result=ServerUiModel();
            else
            {
                Dictionary<string,object> input=action=="UiServerClear"?new Dictionary<string,object>():
                    inputPath=="-"?JsonUtil.Object(JsonUtil.Deserialize(Console.In.ReadToEnd())):JsonUtil.LoadObject(inputPath);
                if(action=="UiServerTest")
                {
                    Dictionary<string,object> existing=ReadCredentials();
                    if(calendarCredentialsLoadError!="")throw new InvalidDataException(calendarCredentialsLoadError);
                    string password=JsonUtil.String(input,"password","");if(password=="")password=JsonUtil.String(existing,"Password","");
                    string source=JsonUtil.String(input,"source","manual");
                    string server=ServerUrlFromInput(input,source);
                    result=new Dictionary<string,object>{{"message",TestCredentials(server,JsonUtil.String(input,"username",""),password,source)}};
                }
                else
                {
                    using(Mutex mutex=new Mutex(false,@"Global\RainmeterCalendarState"))
                    {
                        if(!mutex.WaitOne(TimeSpan.FromSeconds(20)))throw new IOException("日程服务正在忙，请稍后重试。");
                        try
                        {
                            Dictionary<string,object> cache=Load(CachePath,NewCache());
                            if(action=="UiServerClear")ClearCredentials(cache);
                            else
                            {
                                Dictionary<string,object> existing=ReadCredentials();
                                if(calendarCredentialsLoadError!="")throw new InvalidDataException(calendarCredentialsLoadError);
                                string password=JsonUtil.String(input,"password","");if(password=="")password=JsonUtil.String(existing,"Password","");
                                string source=JsonUtil.String(input,"source","manual");
                                SaveCredentials(ServerUrlFromInput(input,source),JsonUtil.String(input,"username",""),password,cache,source);
                            }
                            Render(cache,Load(StatePath,NewState()));
                            result=new Dictionary<string,object>{{"message",action=="UiServerClear"?"已清除日程同步服务器。":"日程同步服务器已保存。"}};
                        }
                        finally{mutex.ReleaseMutex();}
                    }
                }
            }
            result["ok"]=true;JsonUtil.SaveAtomic(output,result);return 0;
        }
        catch(Exception ex)
        {
            try{if(!String.IsNullOrWhiteSpace(output))JsonUtil.SaveAtomic(output,new Dictionary<string,object>{{"ok",false},{"error",ex.Message}});}catch{}
            return 1;
        }
    }

    private static Dictionary<string,object> ServerUiModel()
    {
        Dictionary<string,object> credentials=ReadCredentials();
        if(calendarCredentialsLoadError!="")throw new InvalidDataException(calendarCredentialsLoadError);
        string stored=S(credentials,"_StoredServer");
        Uri uri;bool valid=Uri.TryCreate(stored,UriKind.Absolute,out uri);
        AddressProviderBinding provider=DynamicPluginValues.AddressProvider("calendar.caldav");
        string source=S(credentials,"AddressSource");
        if(source=="")source=provider==null?"manual":"ssdp";
        return new Dictionary<string,object>{
            {"source",source},{"scheme",valid?uri.Scheme:"https"},{"host",valid?uri.Host:""},
            {"port",valid?uri.Port:443},{"path",valid?uri.AbsolutePath=="/"?"":uri.AbsolutePath:""},
            {"query",valid?uri.Query:""},
            {"username",S(credentials,"Username")},{"has_password",S(credentials,"Password")!=""},
            {"provider_ip",provider==null?"":provider.Value},{"status",File.Exists(SecretPath)?"已配置 CalDAV":"尚未配置日程同步"}
        };
    }

    private static string ServerUrlFromInput(Dictionary<string,object> input,string source)
    {
        if(source!="manual"&&source!="ssdp")throw new InvalidDataException("IP 地址来源无效。");
        string scheme=JsonUtil.String(input,"scheme","https");
        if(scheme!="http"&&scheme!="https")throw new InvalidDataException("协议只能是 HTTP 或 HTTPS。");
        AddressProviderBinding provider=source=="ssdp"?DynamicPluginValues.AddressProvider("calendar.caldav"):null;
        string host=source=="ssdp"?(provider==null?"":provider.Value):JsonUtil.String(input,"host","").Trim();
        if(host=="")throw new InvalidDataException(source=="ssdp"?"SSDP 尚未提供服务器 IP，请先执行 SSDP 插件。":"请填写服务器 IP 或主机名。");
        int port=JsonUtil.Int(input,"port",0);if(port<1||port>65535)throw new InvalidDataException("端口必须是 1–65535。");
        string path=JsonUtil.String(input,"path","").Trim();
        try{UriBuilder builder=new UriBuilder(scheme,host,port,path);builder.Query=JsonUtil.String(input,"query","");return builder.Uri.AbsoluteUri.TrimEnd('/');}
        catch(Exception ex){throw new InvalidDataException("日程服务器地址无效："+ex.Message);}
    }
}
