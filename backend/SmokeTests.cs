using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal static class SmokeTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected TodoHost.exe and CalendarHost.exe");
        string root = Path.Combine(Path.GetTempPath(), "RainmeterBackendSmoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            string todoDir = Path.Combine(root, "Skins", "Todo", "@Resources");
            string calendarDir = Path.Combine(root, "Skins", "Calendar", "@Resources");
            Directory.CreateDirectory(todoDir); Directory.CreateDirectory(calendarDir);
            File.Copy(args[0], Path.Combine(todoDir, "TodoHost.exe"));
            File.Copy(args[1], Path.Combine(calendarDir, "CalendarHost.exe"));
            string now = DateTimeOffset.Now.ToString("o");
            Dictionary<string, object> todo = new Dictionary<string, object> {
                {"version", 1}, {"meta", new Dictionary<string,object>{{"last_arxiv_sync_date",""},{"status","就绪"}}},
                {"tasks", new object[] {
                    new Dictionary<string,object>{{"id","11111111111111111111111111111111"},{"title","普通任务"},{"target",""},{"completed",false},{"source","manual"},{"created_at",now},{"completed_at",null}},
                    new Dictionary<string,object>{{"id","22222222222222222222222222222222"},{"title","Paper"},{"translated_title","论文"},{"abstract_score",9.5},{"arxiv_id","2601.00001"},{"target","https://arxiv.org/html/2601.00001"},{"completed",false},{"source","arxiv"},{"created_at",now},{"completed_at",null}}
                }}
            };
            File.WriteAllText(Path.Combine(todoDir,"tasks.json"), Json.Serialize(todo), new UTF8Encoding(false));
            Run(Path.Combine(todoDir,"TodoHost.exe"), "PaperSelfTest");
            Run(Path.Combine(todoDir,"TodoHost.exe"), "Toggle 11111111111111111111111111111111");
            Dictionary<string,object> saved = (Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(todoDir,"tasks.json"),Encoding.UTF8));
            object[] tasks=(object[])saved["tasks"]; Dictionary<string,object> manual=(Dictionary<string,object>)tasks[0],paper=(Dictionary<string,object>)tasks[1];
            Check((bool)manual["completed"], "Todo toggle did not persist");
            Check(!paper.ContainsKey("translated_title") && ((string)paper["title"]).StartsWith("(9.5)"), "Legacy paper migration failed");
            Check(File.ReadAllText(Path.Combine(todoDir,"Generated.inc"),Encoding.Unicode).Contains("TodoHost.exe\" \"Toggle"), "Todo actions were not generated for the C# host");

            DateTimeOffset start=DateTimeOffset.Now.AddMinutes(10),end=start.AddHours(1);
            Dictionary<string,object> testEvent=new Dictionary<string,object> {
                {"id","aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"occurrence_key","uid|instance"},{"uid","uid"},{"recurrence_id",now},
                {"title","测试日程"},{"start_at",start.ToString("o")},{"end_at",end.ToString("o")},{"all_day",false},
                {"url",""},{"location","会议室"},{"description",""},{"status",""},{"reminder_at",""},{"reminder_count",0},{"recurring",false}
            };
            Dictionary<string,object> cache=new Dictionary<string,object> {
                {"version",1},{"fetched_at",now},{"calendar_url","https://example.invalid/calendar/"},{"status","测试"},{"events",new object[]{testEvent}}
            };
            Dictionary<string,object> calendarState=new Dictionary<string,object>{{"version",1},{"series_rules",new object[0]},{"conversions",new object[0]}};
            File.WriteAllText(Path.Combine(calendarDir,"calendar-cache.json"),Json.Serialize(cache),new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(calendarDir,"calendar-state.json"),Json.Serialize(calendarState),new UTF8Encoding(false));
            Run(Path.Combine(calendarDir,"CalendarHost.exe"),"Render");
            string generated=File.ReadAllText(Path.Combine(calendarDir,"Generated.inc"),Encoding.Unicode);
            Check(generated.Contains("测试日程") && generated.Contains("CalendarHost.exe\" \"Detail"),"Calendar render/action generation failed");
            Environment.SetEnvironmentVariable("RAINMETER_UI_SMOKE","1");
            try {
                RunUi(Path.Combine(todoDir,"TodoHost.exe"),"Add");
                RunUi(Path.Combine(todoDir,"TodoHost.exe"),"Manage");
                RunUi(Path.Combine(todoDir,"TodoHost.exe"),"Settings");
                RunUi(Path.Combine(calendarDir,"CalendarHost.exe"),"Manage");
                RunUi(Path.Combine(calendarDir,"CalendarHost.exe"),"Detail aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            } finally { Environment.SetEnvironmentVariable("RAINMETER_UI_SMOKE",null); }
            Console.WriteLine("PASS: Todo paper self-tests, settings, migration/toggle/render and Calendar compatibility");
        }
        finally { try { Directory.Delete(root,true); } catch { } }
    }

    private static void Run(string file,string arguments)
    {
        using(Process p=Process.Start(new ProcessStartInfo(file,arguments){UseShellExecute=false,CreateNoWindow=true})) { if(!p.WaitForExit(20000))throw new Exception("Timed out: "+file);if(p.ExitCode!=0)throw new Exception(Path.GetFileName(file)+" failed with "+p.ExitCode); }
    }
    private static void RunUi(string file,string arguments)
    {
        Console.WriteLine("UI: "+Path.GetFileName(file)+" "+arguments);
        ProcessStartInfo info=new ProcessStartInfo(file,arguments){UseShellExecute=false,CreateNoWindow=true};
        using(Process p=Process.Start(info)){if(!p.WaitForExit(20000))throw new Exception("UI timed out: "+arguments);Console.WriteLine("UI exit: "+p.ExitCode);if(p.ExitCode!=0)throw new Exception("UI failed: "+arguments+" ("+p.ExitCode+")");}
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
