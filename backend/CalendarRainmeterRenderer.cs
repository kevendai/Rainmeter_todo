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
    private static bool Render(Dictionary<string,object> cache, Dictionary<string,object> state)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset), end = start.AddDays(1);
        HashSet<string> hidden = new HashSet<string>(Conversions(state).Where(c => JsonUtil.Bool(c, "hide_event", true)).Select(c => S(c, "occurrence_key")));
        foreach(Dictionary<string,object> h in HiddenEvents(state)) hidden.Add(S(h,"occurrence_key"));
        List<Dictionary<string,object>> events = AllEvents(cache,state).Where(e => RuntimeUtil.Date(e, "start_at").HasValue && RuntimeUtil.Date(e, "end_at").HasValue && RuntimeUtil.Date(e, "start_at").Value < end && RuntimeUtil.Date(e, "end_at").Value > start && !hidden.Contains(S(e, "occurrence_key"))).OrderBy(e => RuntimeUtil.Date(e, "start_at")).ThenBy(e => RuntimeUtil.Date(e, "end_at")).ThenBy(e => S(e, "title")).ToList();

        List<string> lines = new List<string>(); int y = 86, row = 0;
        Meter(lines, "CalendarSummary", "Meter=String", "MeterStyle=StyleText", "X=23", "Y=49", "W=340", "H=16", "FontSize=9", "FontColor=#MutedColor#", "Text=" + now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN")) + "  ·  " + events.Count + " 项");
        int eventHeight = events.Count == 0 ? 54 : events.Sum(e => S(e, "location") == "" ? 44 : 56);
        Meter(lines, "EventSurface", "Meter=Shape", "X=16", "Y=" + y, "Shape=Rectangle 0,0,488," + eventHeight + ",14 | Fill Color 247,251,255,242 | Stroke Color 198,216,232,210 | StrokeWidth 1");
        if (events.Count == 0)
        {
            Meter(lines, "CalendarEmpty", "Meter=String", "MeterStyle=StyleText", "X=32", "Y=" + (y + 17), "W=456", "H=22", "FontColor=#MutedColor#", "Text=今天没有日程");
            y += eventHeight;
        }
        else
        {
            foreach (Dictionary<string,object> e in events)
            {
                row++; DateTimeOffset es = RuntimeUtil.Date(e, "start_at").Value, ee = RuntimeUtil.Date(e, "end_at").Value;
                bool conflict = events.Any(o => S(o, "id") != S(e, "id") && RuntimeUtil.Date(o, "start_at").Value < ee && RuntimeUtil.Date(o, "end_at").Value > es), ongoing = now >= es && now < ee, ended = now >= ee;
                string color = conflict ? "#ConflictColor#" : ongoing ? "#OngoingColor#" : ended ? "#MutedColor#" : "#TextColor#";
                string time = TimeLabel(e, start), title = RuntimeUtil.CleanRainmeter(S(e, "title")); int rowHeight = S(e, "location") == "" ? 44 : 56;
                string action = "LeftMouseUpAction=[\"#@#CalendarHost.exe\" \"Detail\" \"" + S(e, "id") + "\"]";
                Meter(lines, "EventTime" + row, "Meter=String", "MeterStyle=StyleText", "X=30", "Y=" + (y + 11), "W=74", "H=22", "FontSize=9", "FontWeight=600", "FontColor=" + color, "Text=" + time, action);
                Meter(lines, "EventTitle" + row, "Meter=String", "MeterStyle=StyleText", "X=114", "Y=" + (y + 9), "W=374", "H=22", "FontColor=" + color, "Text=" + title, action);
                if (S(e, "location") != "") Meter(lines, "EventLocation" + row, "Meter=String", "MeterStyle=StyleText", "X=114", "Y=" + (y + 30), "W=374", "H=18", "FontSize=9", "FontColor=#MutedColor#", "Text=" + RuntimeUtil.CleanRainmeter(S(e, "location")), action);
                if (row < events.Count) Meter(lines, "EventDivider" + row, "Meter=Shape", "X=114", "Y=" + (y + rowHeight - 1), "Shape=Rectangle 0,0,374,1 | Fill Color 202,218,232,170 | StrokeWidth 0");
                y += rowHeight;
            }
        }

        string status = RuntimeUtil.CleanRainmeter(S(cache, "status")); y += 18;
        Meter(lines, "FooterRule", "Meter=Shape", "X=22", "Y=" + y, "Shape=Rectangle 0,0,476,1 | Fill Color 198,216,232,210 | StrokeWidth 0");
        Meter(lines, "CalendarStatus", "Meter=String", "MeterStyle=StyleText", "X=23", "Y=" + (y + 13), "W=470", "H=18", "FontSize=9", "FontColor=#MutedColor#", "Text=" + status, "ToolTipText=" + status); y += 42;
        Meter(lines, "BottomSpacer", "Meter=Shape", "X=0", "Y=" + y, "Shape=Rectangle 0,0,520,1 | Fill Color 0,0,0,0 | StrokeWidth 0");
        List<string> output = new List<string>();
        Meter(output, "Panel", "Meter=Shape", "X=0", "Y=0", "Shape=Rectangle 1,1,518," + (y - 1) + ",18 | Fill Color 239,248,255,248 | Stroke Color 198,216,232,210 | StrokeWidth 1");
        Meter(output, "PanelHighlight", "Meter=Shape", "X=18", "Y=1", "Shape=Rectangle 0,0,482,1 | Fill Color 255,255,255,180 | StrokeWidth 0");
        output.AddRange(lines);
        return RuntimeUtil.WriteUtf16IfChanged(IncludePath, String.Join("\r\n", output) + "\r\n");
    }
    private static string TimeLabel(Dictionary<string,object>e,DateTimeOffset day){DateTimeOffset s=RuntimeUtil.Date(e,"start_at").Value,x=RuntimeUtil.Date(e,"end_at").Value,next=day.AddDays(1);if(B(e,"all_day"))return s.Date<day.Date||x.Date>next.Date?"全天 · 延续":"全天";if(s<day&&x>next)return"全天 · 延续";if(s<day)return"延续–"+x.ToString("HH:mm");if(x>next)return s.ToString("HH:mm")+"–次日";return x<=s?s.ToString("HH:mm"):s.ToString("HH:mm")+"–"+x.ToString("HH:mm");}
    private static void Meter(List<string>l,string n,params string[]b){l.Add("["+n+"]");l.AddRange(b);l.Add("");}
    private static void MarkGuard(){File.WriteAllText(GuardPath,RuntimeUtil.Iso(DateTimeOffset.Now),RuntimeUtil.Utf8NoBom);}private static bool ConsumeGuard(){if(!File.Exists(GuardPath))return false;try{bool fresh=(DateTime.Now-File.GetLastWriteTime(GuardPath)).TotalSeconds<20;File.Delete(GuardPath);return fresh;}catch{return true;}}
}
