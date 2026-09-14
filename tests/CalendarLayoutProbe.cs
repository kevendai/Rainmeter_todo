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
        DpiLayoutAssertions.AssertWindowDpiCompensation();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string scenario = args.Length == 0 ? "manager" : args[0];
        if (scenario == "manager") ProbeManager();
        else if (scenario == "settings") ProbeSettings();
        else if (scenario == "detail-restore") ProbeRestoreRuleDetail();
        else if (scenario == "editor-recurrence") ProbeEditorRecurrence();
        else if (scenario == "recurrence-dialog") ProbeRecurrenceDialog();
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

    private static void AssertInsideParent(Control control, string name)
    {
        if (control == null || control.Parent == null) throw new Exception(name + " is missing its parent");
        if (control.Left < 0 || control.Top < 0 || control.Right > control.Parent.ClientSize.Width + 1 || control.Bottom > control.Parent.ClientSize.Height + 1)
            throw new Exception(name + " is outside its parent: bounds=" + control.Bounds + ", parent=" + control.Parent.ClientSize);
    }

    private static void AssertHorizontalRow(IEnumerable<Control> controls, string name)
    {
        List<Control> row = controls.OrderBy(control => control.Left).ToList();
        for (int index = 0; index < row.Count; index++)
        {
            AssertInsideParent(row[index], name);
            if (index > 0 && row[index - 1].Right > row[index].Left)
                throw new Exception(name + " controls overlap: '" + row[index - 1].Text + "' and '" + row[index].Text + "'");
        }
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
        DateTime? keyboardExpected = null;
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
                if (rows.Count == 0 && stage < 3) throw new Exception("Calendar event rows missing after tab switch");
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
                if (stage == 2)
                {
                    Control keyboardDay = days[10];
                    keyboardExpected = ((DateTime)keyboardDay.Tag).AddDays(1).Date;
                    keyboardDay.Focus();
                    MethodInfo onKeyDown = keyboardDay.GetType().GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
                    onKeyDown.Invoke(keyboardDay, new object[] { new KeyEventArgs(Keys.Right) });
                    stage = 3;
                    return;
                }
                if (stage == 3)
                {
                    Control focusedDay = days.FirstOrDefault(day => ((DateTime)day.Tag).Date == keyboardExpected.Value.Date);
                    if (focusedDay == null || !focusedDay.Focused) throw new Exception("Calendar keyboard navigation lost focus after rebuilding the day grid");
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

    private static void ProbeEditorRecurrence()
    {
        Dictionary<string, object> state = State(), cache = Cache();
        Timer timer = new Timer { Interval = 100 };
        timer.Tick += delegate {
            Form form = Application.OpenForms.Cast<Form>().FirstOrDefault(candidate => candidate.Text == "新建日程");
            if (form == null) return;
            try
            {
                DpiLayoutAssertions.AssertManualScaling(form);
                DpiLayoutAssertions.AssertPixelFonts(form);
                Button recurrence = Descendants(form).OfType<Button>().FirstOrDefault(button => button.Text == "重复：不重复");
                if (recurrence == null || !recurrence.Visible || !recurrence.Enabled)
                    throw new Exception("New-event recurrence button is missing or unavailable");
                Button allDay = Descendants(form).OfType<Button>().FirstOrDefault(button => button.Text == "全天");
                if (allDay == null) throw new Exception("All-day button is missing beside recurrence control");
                if (recurrence.Top != allDay.Top || recurrence.Bottom != allDay.Bottom)
                    throw new Exception("Recurrence button is not aligned with the date controls");
                if (recurrence.Left < allDay.Right)
                    throw new Exception("Recurrence button overlaps the all-day button");
                AssertInsideParent(recurrence, "Calendar recurrence button");
                DpiLayoutAssertions.AssertFitsAt200Percent(recurrence, true, "Calendar recurrence button");
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
        try { Method("EditInteractive").Invoke(null, new object[] { null, state, cache }); }
        catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
        timer.Dispose();
    }

    private static void ProbeRecurrenceDialog()
    {
        object recurrence = Method("RecurrenceFromEvent").Invoke(null, new object[] { null });
        Timer timer = new Timer { Interval = 100 };
        timer.Tick += delegate {
            Form form = Application.OpenForms.Cast<Form>().FirstOrDefault(candidate => candidate.Text == "周期设置");
            if (form == null) return;
            try
            {
                DpiLayoutAssertions.AssertManualScaling(form);
                DpiLayoutAssertions.AssertPixelFonts(form);
                AssertTextFits(form, "周期设置", true, "Recurrence dialog title");
                AssertTextFits(form, "设置日程重复频率和结束方式。", true, "Recurrence dialog subtitle");
                AssertTextFits(form, "重复频率", true, "Recurrence frequency label");
                AssertTextFits(form, "每周重复日", true, "Recurrence weekday label");
                AssertTextFits(form, "结束方式", true, "Recurrence end label");
                if (Descendants(form).Any(control => control.Text == "重复间隔" || control.Text == "截至日期" || control.Text == "月末或闰日不存在时，将自动跳过该期。"))
                    throw new Exception("Removed recurrence options are still visible");

                string[] frequencyValues = { "none", "daily", "weekly", "monthly", "yearly" };
                List<Button> frequencyButtons = Descendants(form).OfType<Button>()
                    .Where(button => button.Tag is string && frequencyValues.Contains(Convert.ToString(button.Tag, CultureInfo.InvariantCulture)))
                    .OrderBy(button => button.Left).ToList();
                if (frequencyButtons.Count != frequencyValues.Length)
                    throw new Exception("Recurrence frequency buttons are incomplete");
                if (!frequencyButtons.Select(button => Convert.ToString(button.Tag, CultureInfo.InvariantCulture)).SequenceEqual(frequencyValues))
                    throw new Exception("Recurrence frequency buttons are out of order");
                AssertHorizontalRow(frequencyButtons, "Recurrence frequency row");
                foreach (Button button in frequencyButtons)
                    DpiLayoutAssertions.AssertFitsAt200Percent(button, true, "Recurrence frequency button");

                List<Button> weekdayButtons = Descendants(form).OfType<Button>()
                    .Where(button => button.Tag is int).OrderBy(button => (int)button.Tag).ToList();
                if (weekdayButtons.Count != 7 || !weekdayButtons.Select(button => (int)button.Tag).SequenceEqual(Enumerable.Range(1, 7)))
                    throw new Exception("Recurrence weekday buttons are incomplete or out of order");
                AssertHorizontalRow(weekdayButtons, "Recurrence weekday row");
                foreach (Button button in weekdayButtons)
                    DpiLayoutAssertions.AssertFitsAt200Percent(button, true, "Recurrence weekday button");

                NumericUpDown[] numericInputs = Descendants(form).OfType<NumericUpDown>().ToArray();
                TextBox countInput = Descendants(form).OfType<TextBox>().SingleOrDefault();
                RadioButton[] endModes = Descendants(form).OfType<RadioButton>().OrderBy(input => input.Top).ToArray();
                DateTimePicker untilDate = Descendants(form).OfType<DateTimePicker>().FirstOrDefault();
                if (numericInputs.Length != 0 || countInput == null || endModes.Length != 2 || untilDate != null)
                    throw new Exception("Recurrence end controls do not match the simplified design");
                foreach (Control control in endModes.Cast<Control>().Concat(new Control[] { countInput }))
                    AssertInsideParent(control, "Recurrence option control");
                if (endModes.Any(input => input.Enabled) || countInput.Enabled)
                    throw new Exception("Recurrence details should be disabled while '不重复' is selected");
                Label endLabel = Descendants(form).OfType<Label>().First(input => input.Text == "结束方式");
                int compactHeight = form.Height;
                if (endLabel.Top - frequencyButtons[0].Bottom > RainmeterBackend.UiScale.Logical(form, 24))
                    throw new Exception("Non-repeating recurrence dialog left an empty option row");

                Button weekly = frequencyButtons.First(button => Convert.ToString(button.Tag, CultureInfo.InvariantCulture) == "weekly");
                weekly.PerformClick();
                if (form.Height <= compactHeight) throw new Exception("Weekly recurrence dialog did not expand for weekday controls");
                if (endModes.Any(input => !input.Enabled) || weekdayButtons.Any(button => !button.Enabled))
                    throw new Exception("Weekly recurrence controls did not become available");
                if (weekdayButtons.Any(button => button.ForeColor == Color.White && button.BackColor != Color.White))
                    throw new Exception("Weekly recurrence preselected a weekday");
                if (countInput.Enabled)
                    throw new Exception("Conditional recurrence end input became enabled before its mode was selected");
                RadioButton countMode = endModes.First(input => input.Text == "共重复");
                countMode.Checked = true;
                if (!countInput.Enabled)
                    throw new Exception("Count end mode did not switch to the repeat-count input");
                if (countInput.Text != "10") throw new Exception("Repeat-count input default changed");

                Button selectedWeekday = weekdayButtons.First();
                selectedWeekday.PerformClick();
                if (selectedWeekday.FlatAppearance.MouseOverBackColor == Color.White)
                    throw new Exception("Selected weekday turns white on hover");

                Button monthly = frequencyButtons.First(button => Convert.ToString(button.Tag, CultureInfo.InvariantCulture) == "monthly");
                monthly.PerformClick();
                ComboBox[] visibleMonthly = Descendants(form).OfType<ComboBox>().Where(input => input.Visible).ToArray();
                if (weekdayButtons.Any(button => button.Visible) || visibleMonthly.Length != 1 || !Descendants(form).Any(control => control.Visible && control.Text == "每月重复日期"))
                    throw new Exception("Monthly recurrence controls did not replace the weekday row");
                Button yearly = frequencyButtons.First(button => Convert.ToString(button.Tag, CultureInfo.InvariantCulture) == "yearly");
                yearly.PerformClick();
                ComboBox[] visibleYearly = Descendants(form).OfType<ComboBox>().Where(input => input.Visible).ToArray();
                if (weekdayButtons.Any(button => button.Visible) || visibleYearly.Length != 2 || !Descendants(form).Any(control => control.Visible && control.Text == "每年重复日期"))
                    throw new Exception("Yearly recurrence controls did not replace the weekday row");
                foreach (ComboBox input in visibleYearly) AssertInsideParent(input, "Yearly recurrence date input");
                ComboBox yearMonth = visibleYearly.First(input => input.Items.Count == 12), yearDay = visibleYearly.First(input => input != yearMonth);
                yearMonth.SelectedItem = 2;
                if (yearDay.Items.Count != 29 || yearDay.Items.Cast<object>().Any(item => Convert.ToInt32(item, CultureInfo.InvariantCulture) > 29))
                    throw new Exception("Yearly February date options include an impossible day");
                yearDay.SelectedItem = 29; yearMonth.SelectedItem = 4;
                if (yearDay.Items.Count != 30 || Convert.ToInt32(yearDay.SelectedItem, CultureInfo.InvariantCulture) != 29)
                    throw new Exception("Yearly date options did not refresh for a 30-day month");
                yearMonth.SelectedItem = 1; yearDay.SelectedItem = 31; yearMonth.SelectedItem = 2;
                if (Convert.ToInt32(yearDay.SelectedItem, CultureInfo.InvariantCulture) != 29)
                    throw new Exception("Yearly invalid date was not clamped to the month's final day");

                Button daily = frequencyButtons.First(button => Convert.ToString(button.Tag, CultureInfo.InvariantCulture) == "daily");
                daily.PerformClick();
                if (form.Height != compactHeight || endLabel.Top - frequencyButtons[0].Bottom > RainmeterBackend.UiScale.Logical(form, 24))
                    throw new Exception("Daily recurrence dialog did not collapse the empty option row");

                Button cancel = Descendants(form).OfType<Button>().FirstOrDefault(button => button.Text == "取消");
                Button save = Descendants(form).OfType<Button>().FirstOrDefault(button => button.Text == "确定");
                if (cancel == null || save == null)
                    throw new Exception("Recurrence dialog footer is incomplete");
                DpiLayoutAssertions.AssertFitsAt200Percent(cancel, true, "Recurrence cancel button");
                DpiLayoutAssertions.AssertFitsAt200Percent(save, true, "Recurrence save button");
                AssertHorizontalRow(new Control[] { cancel, save }, "Recurrence footer buttons");
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
        object[] parameters = { recurrence, DateTime.Today, null };
        try { Method("ShowRecurrenceDialog").Invoke(null, parameters); }
        catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
        timer.Dispose();
    }

    private static void ProbeRestoreRuleDetail()
    {
        Dictionary<string, object> calendar = Cache();
        Dictionary<string, object> recurring = (Dictionary<string, object>)((List<object>)calendar["events"])[0];
        recurring["recurring"] = true;
        Timer timer = new Timer { Interval = 100 };
        timer.Tick += delegate {
            Form form = Application.OpenForms.Cast<Form>().FirstOrDefault(candidate => candidate.Text == "日程详情");
            if (form == null) return;
            try
            {
                Button restore = Descendants(form).OfType<Button>().FirstOrDefault(button => button.Text == "恢复自动转入");
                if (restore == null || !restore.Visible) throw new Exception("Restore auto-import button is missing for a converted occurrence without a series rule");
                DpiLayoutAssertions.AssertFitsAt200Percent(restore, true, "Restore auto-import button");
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
        try { Method("ShowDetails").Invoke(null, new object[] { recurring, true, false }); }
        catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
        timer.Dispose();
    }
}
