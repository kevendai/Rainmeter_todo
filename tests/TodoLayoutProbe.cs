using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class TodoLayoutProbe
{
    private static Exception failure;

    [STAThread]
    private static void Main(string[] args)
    {
        RainmeterBackend.UiScale.EnableDpiAwareness();
        DpiLayoutAssertions.AssertWindowDpiCompensation();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string scenario = args.Length == 0 ? "editor" : args[0];
        if (scenario == "editor") ProbeEditor();
        else if (scenario == "manager") ProbeManager();
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
        return typeof(TodoApp).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(method => method.Name == name);
    }

    private static void AssertTextFits(Form form, string text, bool checkWidth, string name)
    {
        foreach (Control control in Descendants(form).Where(candidate => candidate.Text == text))
            DpiLayoutAssertions.AssertFitsAt200Percent(control, checkWidth, name);
    }

    private static void ProbeEditor()
    {
        int stage = 0;
        Timer timer = new Timer { Interval = 80 };
        timer.Tick += delegate {
            Form form = Application.OpenForms.Cast<Form>().FirstOrDefault(candidate => candidate.Text == "新增待办");
            if (form == null) return;
            try
            {
                DpiLayoutAssertions.AssertManualScaling(form);
                DpiLayoutAssertions.AssertPixelFonts(form);
                AssertTextFits(form, "新增待办", true, "Todo editor title");
                AssertTextFits(form, "备注", true, "Todo note label");
                Button expand = Descendants(form).OfType<Button>().First(button => button.Text == "展开" || button.Text == "收起");
                Label note = Descendants(form).OfType<Label>().First(label => label.Text == "备注");
                Panel surface = expand.Parent as Panel;
                Panel selector = surface == null ? null : surface.Parent as Panel;
                if (surface == null || selector == null) throw new Exception("Label selector hierarchy missing");
                if (stage == 0)
                {
                    Size preferred = TextRenderer.MeasureText(expand.Text, expand.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    if (preferred.Width > expand.ClientSize.Width + 2 || preferred.Height > expand.ClientSize.Height + 2) throw new Exception("Expand button text clipped");
                    expand.PerformClick();
                    stage = 1;
                    return;
                }
                if (stage == 1)
                {
                    if (expand.Text != "收起") throw new Exception("Expand action did not open selector");
                    expand.PerformClick();
                    stage = 2;
                    return;
                }
                List<Button> chips = surface.Controls.OfType<Button>().Where(button => button != expand).ToList();
                if (chips.Count == 0 || chips.Count(button => button.Visible) == 0) throw new Exception("Collapsed selector lost first-row labels");
                if (selector.Bottom > note.Top) throw new Exception("Collapsed selector overlaps note label");
                DpiLayoutAssertions.AssertPixelFonts(form);
                DpiLayoutAssertions.AssertFitsAt200Percent(expand, true, "Todo expand button");
                foreach (Button chip in chips.Where(button => button.Visible))
                    DpiLayoutAssertions.AssertFitsAt200Percent(chip, true, "Todo label chip");
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
        try { Method("ShowEditor").Invoke(null, new object[] { null }); }
        catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
        timer.Dispose();
    }

    private static void ProbeManager()
    {
        Dictionary<string, object> state = (Dictionary<string, object>)Method("NewState").Invoke(null, null);
        MethodInfo tasksMethod = Method("Tasks");
        List<Dictionary<string, object>> tasks = (List<Dictionary<string, object>>)tasksMethod.Invoke(null, new object[] { state });
        tasks.Add(new Dictionary<string, object> {
            {"id", "probe-task"}, {"title", "缩放探针任务"}, {"target", ""}, {"note", ""},
            {"labels", new List<object> { "功能" }}, {"completed", false}, {"source", "manual"},
            {"created_at", DateTimeOffset.Now.ToString("o")}, {"completed_at", null},
            {"available_from", null}, {"due_at", null}
        });
        int stage = 0;
        Timer timer = new Timer { Interval = 80 };
        timer.Tick += delegate {
            Form form = Application.OpenForms.Cast<Form>().FirstOrDefault(candidate => candidate.Text == "全部任务");
            if (form == null) return;
            try
            {
                DpiLayoutAssertions.AssertManualScaling(form);
                DpiLayoutAssertions.AssertPixelFonts(form);
                AssertTextFits(form, "全部任务", true, "Todo manager title");
                AssertTextFits(form, "只看未完成", true, "Todo open-only filter");
                List<CheckBox> rowChecks = Descendants(form).OfType<CheckBox>().Where(check => String.IsNullOrEmpty(check.Text) && check.Parent != null && check.Parent.Tag is string).ToList();
                if (rowChecks.Count == 0) throw new Exception("Task row checkbox missing");
                foreach (CheckBox check in rowChecks)
                {
                    if (check.Width < 18 || check.Height < 18) throw new Exception("Task row checkbox clipped");
                    if (check.Left < 0 || check.Top < 0 || check.Right > check.Parent.ClientSize.Width || check.Bottom > check.Parent.ClientSize.Height) throw new Exception("Task row checkbox outside row");
                }
                if (stage == 0)
                {
                    Button all = Descendants(form).OfType<Button>().First(button => button.Text.StartsWith("全部", StringComparison.Ordinal));
                    all.PerformClick();
                    stage = 1;
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
        object[] parameters = { state, false };
        try { Method("Manage").Invoke(null, parameters); }
        catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
        timer.Dispose();
    }
}
