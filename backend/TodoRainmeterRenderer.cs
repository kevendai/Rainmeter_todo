using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using RainmeterBackend;

internal static partial class TodoApp
{
    private static bool Render(Dictionary<string, object> state)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        List<Dictionary<string, object>> tasks = Tasks(state);
        List<Dictionary<string, object>> pending = tasks.Where(t => !B(t, "completed") && (!RuntimeUtil.Date(t, "available_from").HasValue || now >= RuntimeUtil.Date(t, "available_from").Value))
            .OrderBy(t => RuntimeUtil.Date(t, "due_at").HasValue ? 0 : 1)
            .ThenBy(t => RuntimeUtil.Date(t, "due_at") ?? DateTimeOffset.MaxValue)
            .ThenBy(t => RuntimeUtil.Date(t, "created_at") ?? DateTimeOffset.MinValue).ThenBy(t => S(t, "id")).ToList();
        DateTimeOffset window = CompletionWindow(now);
        List<Dictionary<string, object>> done = tasks.Where(t => B(t, "completed") && RuntimeUtil.Date(t, "completed_at").HasValue && RuntimeUtil.Date(t, "completed_at").Value >= window)
            .OrderByDescending(t => RuntimeUtil.Date(t, "completed_at").Value).ToList();
        List<string> lines = new List<string>(); int y = 86, row = 0;
        Meter(lines, "HeaderSummary", "Meter=String", "MeterStyle=StyleText", "X=23", "Y=49", "W=285", "H=16", "FontSize=9", "FontColor=#MutedColor#", "Text=" + pending.Count + " 项待办  ·  " + done.Count + " 项已办");

        int pendingHeight = pending.Count == 0 ? 54 : pending.Sum(t => String.IsNullOrEmpty(TimeLabel(t, now)) ? 44 : 56);
        Meter(lines, "PendingSurface", "Meter=Shape", "X=16", "Y=" + y, "Shape=Rectangle 0,0,488," + pendingHeight + ",14 | Fill Color 247,251,255,242 | Stroke Color 198,216,232,210 | StrokeWidth 1");
        if (pending.Count == 0)
        {
            Meter(lines, "Empty", "Meter=String", "MeterStyle=StyleText", "X=32", "Y=" + (y + 17), "W=456", "H=22", "FontColor=#MutedColor#", "Text=清单是空的。右上角可以新增任务");
            y += pendingHeight;
        }
        else
        {
            foreach (Dictionary<string, object> task in pending)
            {
                row++; string title = RuntimeUtil.CleanRainmeter(S(task, "title")); DateTimeOffset? due = RuntimeUtil.Date(task, "due_at"); bool overdue = due.HasValue && now > due.Value;
                string time = TimeLabel(task, now), color = overdue ? "#DangerColor#" : "#TextColor#", toggle = overdue ? "#DangerColor#" : "#DoneColor#";
                string tip = String.IsNullOrEmpty(time) ? title : title + " · " + time; int rowHeight = String.IsNullOrEmpty(time) ? 44 : 56;
                Meter(lines, "PendingToggle" + row, "Meter=Shape", "X=30", "Y=" + (y + 12), "W=22", "H=22", "Shape=Ellipse 10,10,6 | Fill Color 0,0,0,0 | Stroke Color " + toggle + " | StrokeWidth 1.8", Action("Toggle", S(task, "id")), "ToolTipText=标记完成");
                Meter(lines, "PendingTitle" + row, "Meter=String", "MeterStyle=StyleText", "X=60", "Y=" + (y + 9), "W=352", "H=22", "FontColor=" + color, "Text=" + title, Action("Open", S(task, "id")));
                if (!String.IsNullOrEmpty(time)) Meter(lines, "PendingTime" + row, "Meter=String", "MeterStyle=StyleText", "X=60", "Y=" + (y + 30), "W=352", "H=18", "FontSize=9", "FontColor=" + (overdue ? "#DangerColor#" : "#MutedColor#"), "Text=" + time, Action("Open", S(task, "id")));
                Meter(lines, "PendingEdit" + row, "Meter=String", "X=443", "Y=" + (y + (rowHeight / 2)), "W=34", "H=34", "FontFace=Segoe Fluent Icons", "FontSize=10", "FontColor=#SubtleColor#", "StringAlign=CenterCenter", "AntiAlias=1", "Text=\xE70F", "MouseOverAction=[!SetOption PendingEdit" + row + " FontColor \"#AccentColor#\"][!UpdateMeter PendingEdit" + row + "][!Redraw]", "MouseLeaveAction=[!SetOption PendingEdit" + row + " FontColor \"#SubtleColor#\"][!UpdateMeter PendingEdit" + row + "][!Redraw]", Action("Edit", S(task, "id")), "ToolTipText=修改");
                Meter(lines, "PendingDelete" + row, "Meter=String", "X=479", "Y=" + (y + (rowHeight / 2)), "W=34", "H=34", "FontFace=Segoe Fluent Icons", "FontSize=10", "FontColor=#SubtleColor#", "StringAlign=CenterCenter", "AntiAlias=1", "Text=\xE74D", "MouseOverAction=[!SetOption PendingDelete" + row + " FontColor \"#DangerColor#\"][!UpdateMeter PendingDelete" + row + "][!Redraw]", "MouseLeaveAction=[!SetOption PendingDelete" + row + " FontColor \"#SubtleColor#\"][!UpdateMeter PendingDelete" + row + "][!Redraw]", Action("Delete", S(task, "id")), "ToolTipText=删除");
                if (row < pending.Count) Meter(lines, "PendingDivider" + row, "Meter=Shape", "X=60", "Y=" + (y + rowHeight - 1), "Shape=Rectangle 0,0,428,1 | Fill Color 202,218,232,170 | StrokeWidth 0");
                y += rowHeight;
            }
        }

        y += 24;
        Meter(lines, "DoneHeading", "Meter=String", "MeterStyle=StyleText", "X=20", "Y=" + y, "W=470", "H=22", "FontSize=10", "FontWeight=600", "FontColor=#MutedColor#", "Text=已办  " + done.Count); y += 28; row = 0;
        List<Dictionary<string, object>> shownDone = done.Take(8).ToList();
        if (shownDone.Count > 0) Meter(lines, "DoneSurface", "Meter=Shape", "X=16", "Y=" + y, "Shape=Rectangle 0,0,488," + (shownDone.Count * 42) + ",14 | Fill Color 239,249,244,238 | Stroke Color 198,216,232,190 | StrokeWidth 1");
        foreach (Dictionary<string, object> task in shownDone)
        {
            row++; string title = RuntimeUtil.CleanRainmeter(S(task, "title"));
            Meter(lines, "DoneToggle" + row, "Meter=String", "X=42", "Y=" + (y + 21), "W=30", "H=34", "FontFace=Segoe Fluent Icons", "FontSize=10", "FontColor=#DoneColor#", "StringAlign=CenterCenter", "AntiAlias=1", "Text=\xE72C", Action("Toggle", S(task, "id")), "ToolTipText=恢复到待办");
            Meter(lines, "DoneTitle" + row, "Meter=String", "MeterStyle=StyleText", "X=60", "Y=" + (y + 10), "W=390", "H=22", "FontColor=#MutedColor#", "Text=" + title, Action("Open", S(task, "id")));
            Meter(lines, "DoneDelete" + row, "Meter=String", "X=479", "Y=" + (y + 21), "W=34", "H=34", "FontFace=Segoe Fluent Icons", "FontSize=9", "FontColor=#SubtleColor#", "StringAlign=CenterCenter", "AntiAlias=1", "Text=\xE74D", "MouseOverAction=[!SetOption DoneDelete" + row + " FontColor \"#DangerColor#\"][!UpdateMeter DoneDelete" + row + "][!Redraw]", "MouseLeaveAction=[!SetOption DoneDelete" + row + " FontColor \"#SubtleColor#\"][!UpdateMeter DoneDelete" + row + "][!Redraw]", Action("Delete", S(task, "id")), "ToolTipText=删除");
            if (row < shownDone.Count) Meter(lines, "DoneDivider" + row, "Meter=Shape", "X=60", "Y=" + (y + 41), "Shape=Rectangle 0,0,428,1 | Fill Color 202,218,232,145 | StrokeWidth 0");
            y += 42;
        }
        if (done.Count > 8) { Meter(lines, "DoneMore", "Meter=String", "MeterStyle=StyleText", "X=60", "Y=" + (y + 7), "W=420", "H=20", "FontSize=9", "FontColor=#MutedColor#", "Text=另有 " + (done.Count - 8) + " 项未展开"); y += 30; }
        string status = RuntimeUtil.CleanRainmeter(PaperDisplayStatus(state)); y += 18;
        Meter(lines, "FooterRule", "Meter=Shape", "X=22", "Y=" + y, "Shape=Rectangle 0,0,476,1 | Fill Color #BorderColor# | StrokeWidth 0");
        Meter(lines, "Status", "Meter=String", "MeterStyle=StyleText", "X=23", "Y=" + (y + 13), "W=470", "H=18", "FontSize=9", "FontColor=#MutedColor#", "Text=" + status, "ToolTipText=" + status); y += 42;
        Meter(lines, "BottomSpacer", "Meter=Shape", "X=0", "Y=" + y, "Shape=Rectangle 0,0,520,1 | Fill Color 0,0,0,0 | StrokeWidth 0");

        List<string> output = new List<string>();
        Meter(output, "Panel", "Meter=Shape", "X=0", "Y=0", "Shape=Rectangle 1,1,518," + (y - 1) + ",18 | Fill Color 239,248,255,248 | Stroke Color 198,216,232,210 | StrokeWidth 1");
        Meter(output, "PanelHighlight", "Meter=Shape", "X=18", "Y=1", "Shape=Rectangle 0,0,482,1 | Fill Color 255,255,255,180 | StrokeWidth 0");
        output.AddRange(lines);
        return RuntimeUtil.WriteUtf16IfChanged(IncludePath, String.Join("\r\n", output) + "\r\n");
    }

    private static void Meter(List<string> lines, string name, params string[] body) { lines.Add("[" + name + "]"); lines.AddRange(body); lines.Add(""); }
    private static string Action(string action, string id) { return "LeftMouseUpAction=[\"#@#TodoHost.exe\" \"" + action + "\" \"" + id + "\"]"; }

}

