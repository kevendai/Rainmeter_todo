using System;
using System.Collections.Generic;
using System.IO;
using RainmeterBackend;

internal static class CalendarServerUiProbe
{
    private static int Main(string[] args)
    {
        try { return args.Length>0&&args[0]=="LiveDiscovery"?CalendarApp.RunDiscoveryLiveProbe():CalendarApp.RunServerUiProbe(); }
        catch (Exception ex) { Console.Error.WriteLine("Calendar server UI probe failed: " + ex); return 1; }
    }
}

internal static partial class CalendarApp
{
    internal static int RunDiscoveryLiveProbe()
    {
        var input=JsonUtil.Object(JsonUtil.Deserialize(Console.In.ReadToEnd()));
        var credentials=CredentialsFromValues(JsonUtil.String(input,"server",""),JsonUtil.String(input,"username",""),JsonUtil.String(input,"password",""),"manual");
        var result=Fetch(credentials,"");
        var direct=Discover(CredentialsFromValues(result.Calendar.Uri,JsonUtil.String(input,"username",""),JsonUtil.String(input,"password",""),"manual"));
        if(direct.Uri!=result.Calendar.Uri)throw new Exception("Direct calendar discovery mismatch");
        Console.WriteLine(JsonUtil.Serialize(new Dictionary<string,object>{{"calendar",result.Calendar.Uri},{"name",result.Calendar.Name},{"events",result.Events.Count},{"direct_path",true}}));
        return 0;
    }
    internal static int RunServerUiProbe()
    {
        string previous=R;
        string previousPluginRoot=Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT");
        string root=Path.Combine(Path.GetTempPath(),"RainmeterCalendarServerUiProbe-"+Guid.NewGuid().ToString("N"));
        try
        {
            var fixture=Xml("<d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><d:response><d:href>/renamed/tasks/</d:href><d:propstat><d:prop><d:displayname>A Tasks</d:displayname><d:resourcetype><c:calendar/></d:resourcetype><c:supported-calendar-component-set><c:comp name='VTODO'/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response><d:response><d:href>/renamed/events/</d:href><d:propstat><d:prop><d:displayname>Renamed Calendar</d:displayname><d:resourcetype><c:calendar/></d:resourcetype><c:supported-calendar-component-set><c:comp name='VEVENT'/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>");
            var discovered=ParseCalendarCollections(fixture,"https://example.org/");
            if(discovered.Count!=1||discovered[0].Uri!="https://example.org/renamed/events/")return 10;
            R=Path.Combine(root,"Skins","Calendar","@Resources");
            Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",Path.Combine(root,"PluginRoot"));
            Directory.CreateDirectory(R);Directory.CreateDirectory(TodoDir);
            Dictionary<string,object> input=new Dictionary<string,object>{{"source","manual"},{"scheme","https"},{"host","192.0.2.42"},{"port","9843"},{"path","/caldav"},{"query","view=calendar"}};
            string url=ServerUrlFromInput(input,"manual");
            if(url!="https://192.0.2.42/caldav?view=calendar"){Console.WriteLine("URL="+url);return 1;}
            input["port"]="";
            if(ServerUrlFromInput(input,"manual")!=url)return 6;
            input["scheme"]="http";input["port"]="9843";
            if(ServerUrlFromInput(input,"manual")!="http://192.0.2.42:9843/caldav?view=calendar")return 7;
            input["scheme"]="https";
            input["host"]="HTTPS://example.org/caldav?view=calendar";
            if(ServerUrlFromInput(input,"manual")!="https://example.org/caldav?view=calendar")return 8;
            input["host"]="http://example.org:9843/caldav?view=calendar";
            if(ServerUrlFromInput(input,"manual")!="http://example.org:9843/caldav?view=calendar")return 9;
            input["host"]="192.0.2.42";
            bool missing=false;try{ServerUrlFromInput(input,"ssdp");}catch(InvalidDataException){missing=true;}
            if(!missing){Console.WriteLine("SSDP unexpectedly resolved");return 2;}
            Dictionary<string,object> cache=NewCache();SaveCredentials(url,"user","probe-password",cache,"manual");
            Dictionary<string,object> model=ServerUiModel();
            if(JsonUtil.String(model,"source","")!="manual"||JsonUtil.String(model,"host","")!="192.0.2.42"||
               JsonUtil.Int(model,"port",0)!=443||JsonUtil.String(model,"query","")!="?view=calendar"||!JsonUtil.Bool(model,"has_password",false)||model.ContainsKey("password")){Console.WriteLine("MODEL="+JsonUtil.Serialize(model));return 3;}
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
