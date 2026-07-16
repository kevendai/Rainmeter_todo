using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CalendarLayoutProbe
{
    private static Exception failure;

    [STAThread]
    private static void Main(string[] args)
    {
        RainmeterBackend.UiScale.EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string scenario = args.Length == 0 ? "manager" : args[0];
        if (scenario == "manager") ProbeManager();
        else if (scenario == "settings") ProbeSettings();
        else throw new ArgumentException("Unknown scenario: " + scenario);
        if (failure != null) { Console.Error.WriteLine(scenario + ": " + failure.Message); Environment.ExitCode = 1; return; }
        Console.WriteLine("PASS " + scenario);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static MethodInfo Method(string name)
    {
        return typeof(CalendarApp).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(method => method.Name == name);
    }

    private static void AssertTextFits(Form form, string text, bool checkWidth, string name)
    {
        foreach (Control control in Descendants(form).Where(candidate => candidate.Text == text))
            DpiLayoutAssertions.AssertFitsAt200Percent(control, checkWidth, name);
    }

    private static void AssertManagerHighDpi(Form form, Button allTime, List<Control> days)
    {
        DpiLayoutAssertions.AssertManualScaling(form);
        DpiLayoutAssertions.AssertPixelFonts(form);
        AssertTextFits(form, "日程管理", true, "Calendar manager title");
        AssertTextFits(form, "查看、编辑和同步你的本地日历与 CalDAV 日历。", true, "Calendar manager subtitle");
        AssertTextFits(form, "日历筛选", true, "Calendar filter title");
        AssertTextFits(form, "今天", true, "Calendar today button");
        AssertTextFits(form, "未来7天", true, "Calendar week tab");
        AssertTextFits(form, "全部时间", true, "Calendar all-time tab");
        AssertTextFits(form, "设置", true, "Calendar settings button");
        AssertTextFits(form, "刷新同步", true, "Calendar sync button");
        AssertTextFits(form, "新建日程", true, "Calendar add button");
        foreach (Control month in Descendants(form).Where(control => control is Label && control.Text.Contains(" 年 ") && control.Text.EndsWith(" 月", StringComparison.Ordinal)))
            DpiLayoutAssertions.AssertFitsAt200Percent(month, true, "Calendar month title");
        foreach (Control source in Descendants(form).Where(control => control is Button && (control.Text.Contains("本地") || control.Text.Contains("CalDAV"))))
            DpiLayoutAssertions.AssertFitsAt200Percent(source, true, "Calendar source filter");
        foreach (Control day in days)
            DpiLayoutAssertions.AssertFitsAt200Percent(day, true, "Calendar date");
        Panel grid = days.Count == 0 ? null : days[0].Parent as Panel;
        if (grid != null)
            foreach (Label weekday in grid.Controls.OfType<Label>())
                DpiLayoutAssertions.AssertFitsAt200Percent(weekday, true, "Calendar weekday");
        DpiLayoutAssertions.AssertFitsAt200Percent(allTime, true, "Calendar all-time tab");
    }

    private static Dictionary<string, object> State()
    {
        return (Dictionary<string, object>)Method("NewState").Invoke(null, null);
    }

    private static Dictionary<string, object> Cache()
    {
        Dictionary<string, object> cache = (Dictionary<string, object>)Method("NewCache").Invoke(null, null);
        DateTimeOffset start = DateTimeOffset.Now.AddMinutes(15);
        cache["events"] = new List<object> {
            new Dictionary<string, object> {
                {"id", "probe-event"}, {"occurrence_key", "probe|instance"}, {"uid", "probe"},
                {"title", "缩放探针日程"}, {"start_at", start.ToString("o")}, {"end_at", start.AddHours(1).ToString("o")},
                {"all_day", false}, {"url", ""}, {"location", "会议室"}, {"description", ""},
                {"status", ""}, {"reminder_at", ""}, {"reminder_count", 0}, {"recurring", false}, {"source", "caldav"}
            }
        };
        return cache;
    }

    private static void ProbeManager()
    {
        Dictionary<string, object> state = State(), cache = Cache();
        int stage = 0;
        Timer timer = new Timer { Interval = 100 };
        timer.Tick += delegate {
            Form form = Application.OpenForms.Cast<Form>().FirstOrDefault(candidate => candidate.Text == "日程管理");
            if (form == null) return;
            try
            {
                Button allTime = Descendants(form).OfType<Button>().First(button => button.Text == "全部时间");
                if (stage == 0)
                {
                    allTime.PerformClick();
                    stage = 1;
                    return;
                }
                List<Control> days = Descendants(form).Where(control => control.Tag is DateTime).ToList();
                if (days.Count != 42) throw new Exception("Calendar day grid was not rebuilt");
                foreach (Control day in days)
                {
                    Size measured = TextRenderer.MeasureText(day.Text, day.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    if (measured.Width > day.ClientSize.Width + 2 || measured.Height > day.ClientSize.Height + 2) throw new Exception("Calendar date clipped: " + day.Text);
                    if (day.Left < 0 || day.Top < 0 || day.Right > day.Parent.ClientSize.Width + 1 || day.Bottom > day.Parent.ClientSize.Height + 1) throw new Exception("Calendar date outside grid");
                }
                FlowLayoutPanel list = Descendants(form).OfType<FlowLayoutPanel>().First(panel => panel.AutoScroll && panel.FlowDirection == FlowDirection.TopDown);
                List<Panel> rows = list.Controls.OfType<Panel>().Where(panel => panel.Tag is string).ToList();
                if (rows.Count == 0) throw new Exception("Calendar event rows missing after tab switch");
                if (rows.Any(row => row.Width > list.ClientSize.Width + 1)) throw new Exception("Calendar event row did not scale with list");
                Size tabText = TextRenderer.MeasureText(allTime.Text, allTime.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                if (tabText.Width > allTime.ClientSize.Width + 2) throw new Exception("All-time tab text clipped");
                AssertManagerHighDpi(form, allTime, days);
                if (stage == 1)
                {
                    string capturePath = Environment.GetEnvironmentVariable("RAINMETER_UI_CAPTURE_PATH");
                    if (!String.IsNullOrWhiteSpace(capturePath))
                    {
                        using (Bitmap bitmap = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height)))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(capturePath);
                        }
                    }
                    Button today = Descendants(form).OfType<Button>().First(button => button.Text == "今天" && button.Parent == allTime.Parent);
                    today.PerformClick();
                    stage = 2;
                    return;
                }
                timer.Stop();
                form.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
                timer.Stop();
                form.Close();
            }
        };
        timer.Start();
        object[] parameters = { state, cache, false };
        try { Method("ManageEvents").Invoke(null, parameters); }
        catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
        timer.Dispose();
    }

    private static void ProbeSettings()
    {
        Dictionary<string, object> state = State(), cache = Cache();
        Timer timer = new Timer { Interval = 100 };
        timer.Tick += delegate {
            Form form = Application.OpenForms.Cast<Form>().FirstOrDefault(candidate => candidate.Text == "日程设置");
            if (form == null) return;
            try
            {
                DpiLayoutAssertions.AssertManualScaling(form);
                DpiLayoutAssertions.AssertPixelFonts(form);
                AssertTextFits(form, "日程设置", true, "Calendar settings title");
                HashSet<string> kinds = new HashSet<string>(new[] { "globe", "user", "lock" });
                List<Panel> icons = Descendants(form).OfType<Panel>().Where(panel => panel.Tag is string && kinds.Contains(Convert.ToString(panel.Tag, CultureInfo.InvariantCulture))).ToList();
                if (icons.Count < 3) throw new Exception("Credential icons missing");
                foreach (Panel icon in icons)
                {
                    if (icon.Width < 14 || icon.Height < 14) throw new Exception("Credential icon bounds too small");
                    if (icon.Left < 0 || icon.Top < 0 || icon.Right > icon.Parent.ClientSize.Width || icon.Bottom > icon.Parent.ClientSize.Height) throw new Exception("Credential icon clipped by parent");
                    using (Bitmap bitmap = new Bitmap(Math.Max(1, icon.Width), Math.Max(1, icon.Height))) icon.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                }
                timer.Stop();
                form.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
                timer.Stop();
                form.Close();
            }
        };
        timer.Start();
        object[] parameters = { state, cache, false };
        try { Method("Settings").Invoke(null, parameters); }
        catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
        timer.Dispose();
    }
}
