using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal static class DpiLayoutAssertions
{
    private const float HighDpi = 192F;

    public static IEnumerable<Control> SelfAndDescendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control control in SelfAndDescendants(child))
                yield return control;
    }

    public static void AssertManualScaling(Form form)
    {
        if (form.AutoScaleMode != AutoScaleMode.None)
            throw new Exception("Form still enables WinForms DPI autoscaling: " + form.Text);
    }

    public static void AssertWindowDpiCompensation()
    {
        float at125 = RainmeterBackend.UiScale.WindowScaleForDpi(0.75F, 120F);
        float at200 = RainmeterBackend.UiScale.WindowScaleForDpi(0.75F, 192F);
        if (Math.Abs(at125 - 0.75F) > 0.001F || Math.Abs(at200 - 1.20F) > 0.001F)
            throw new Exception("Window DPI compensation is incorrect: 125%=" + at125 + ", 200%=" + at200);
    }

    public static void AssertPixelFonts(Form form)
    {
        foreach (Control control in SelfAndDescendants(form))
        {
            if (control is WebBrowser || control.Font == null || String.IsNullOrWhiteSpace(control.Text)) continue;
            if (control.Font.Unit != GraphicsUnit.Pixel)
                throw new Exception("Non-pixel UI font: " + control.GetType().Name + " '" + control.Text + "' " + control.Font.Unit);
        }
    }

    public static void AssertFitsAt200Percent(Control control, bool checkWidth, string name)
    {
        if (control == null || String.IsNullOrWhiteSpace(control.Text)) return;
        SizeF measured = MeasureAt200Percent(control);
        int availableWidth = Math.Max(0, control.ClientSize.Width - control.Padding.Horizontal);
        int availableHeight = Math.Max(0, control.ClientSize.Height - control.Padding.Vertical);
        if (Math.Ceiling(measured.Height) > availableHeight + 2)
            throw new Exception(name + " clips vertically at 200% DPI: measured=" + measured + ", available=" + availableWidth + "x" + availableHeight + ", font=" + control.Font.Size + control.Font.Unit);
        if (checkWidth && Math.Ceiling(measured.Width) > availableWidth + 2)
            throw new Exception(name + " clips horizontally at 200% DPI: measured=" + measured + ", available=" + availableWidth + "x" + availableHeight + ", font=" + control.Font.Size + control.Font.Unit);
    }

    private static SizeF MeasureAt200Percent(Control control)
    {
        using (Bitmap bitmap = new Bitmap(8, 8))
        {
            bitmap.SetResolution(HighDpi, HighDpi);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                return graphics.MeasureString(control.Text, control.Font, Int32.MaxValue, format);
            }
        }
    }
}
