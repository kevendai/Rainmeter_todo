using System;
using System.Collections.Generic;
using System.IO;
using RainmeterBackend;

internal static class CalendarServerUiProbe
{
    private static int Main(){return CalendarApp.RunServerUiProbe();}
}

internal static partial class CalendarApp
{
    internal static int RunServerUiProbe()
    {
        string previous=R;
        string previousPluginRoot=Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT");
        string root=Path.Combine(Path.GetTempPath(),"RainmeterCalendarServerUiProbe-"+Guid.NewGuid().ToString("N"));
        try
        {
            R=Path.Combine(root,"Skins","Calendar","@Resources");
            Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",Path.Combine(root,"PluginRoot"));
            Directory.CreateDirectory(R);Directory.CreateDirectory(TodoDir);
            Dictionary<string,object> input=new Dictionary<string,object>{{"source","manual"},{"scheme","https"},{"host","192.0.2.42"},{"port","9843"},{"path","/caldav"},{"query","view=calendar"}};
            string url=ServerUrlFromInput(input,"manual");
            if(url!="https://192.0.2.42:9843/caldav?view=calendar"){Console.WriteLine("URL="+url);return 1;}
            bool missing=false;try{ServerUrlFromInput(input,"ssdp");}catch(InvalidDataException){missing=true;}
            if(!missing){Console.WriteLine("SSDP unexpectedly resolved");return 2;}
            Dictionary<string,object> cache=NewCache();SaveCredentials(url,"user","probe-password",cache,"manual");
            Dictionary<string,object> model=ServerUiModel();
            if(JsonUtil.String(model,"source","")!="manual"||JsonUtil.String(model,"host","")!="192.0.2.42"||
               JsonUtil.Int(model,"port",0)!=9843||JsonUtil.String(model,"query","")!="?view=calendar"||!JsonUtil.Bool(model,"has_password",false)||model.ContainsKey("password")){Console.WriteLine("MODEL="+JsonUtil.Serialize(model));return 3;}
            if(JsonUtil.String(ReadCredentials(),"Server","")!=url){Console.WriteLine("CREDENTIAL SERVER MISMATCH");return 4;}
            ClearCredentials(cache);if(File.Exists(SecretPath)){Console.WriteLine("SECRET STILL EXISTS");return 5;}
            Console.WriteLine("PASS calendar server UI probe: manual URL, SSDP absence, secret-free model, DPAPI save and clear");
            return 0;
        }
        finally
        {
            R=previous;
            Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",previousPluginRoot);
            string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)
                &&Path.GetFileName(root).StartsWith("RainmeterCalendarServerUiProbe-",StringComparison.Ordinal)
                &&Directory.Exists(root))Directory.Delete(root,true);
        }
    }
}
