using System;
using System.IO;
using RainmeterBackend;

internal static class ScaleConfigProbe
{
    private static int Main()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string tile = Path.Combine(directory, "ui-scale.txt");
        string window = Path.Combine(directory, "ui-window-scale.txt");
        string theme = Path.Combine(directory, "ui-theme.txt");
        string previousScale = Environment.GetEnvironmentVariable("RAINMETER_UI_SCALE_OVERRIDE");
        string previousTheme = Environment.GetEnvironmentVariable("RAINMETER_UI_THEME_OVERRIDE");
        try
        {
            Environment.SetEnvironmentVariable("RAINMETER_UI_SCALE_OVERRIDE", null);
            File.WriteAllText(tile, "0.80");
            if (UiScale.Mode != "0.80" || UiScale.WindowMode != "1.00") return 1;
            UiScale.SaveWindowMode("1.10");
            if (UiScale.WindowMode != "1.10" || Math.Abs(UiScale.TileCurrent - 0.80F) > 0.001F ||
                Math.Abs(UiScale.Current - 1.10F) > 0.001F) return 2;
            Environment.SetEnvironmentVariable("RAINMETER_UI_THEME_OVERRIDE", null);
            UiTheme.Save(UiTheme.Acrylic);
            if (UiTheme.Current != UiTheme.Acrylic ||
                UiTheme.RainmeterVariables(UiTheme.Acrylic).IndexOf("ThemeBlur=1", StringComparison.Ordinal) < 0 ||
                UiTheme.RainmeterVariables(UiTheme.Classic).IndexOf("PanelColor=239,248,255,248", StringComparison.Ordinal) < 0) return 3;
            Console.WriteLine("PASS scale-config");
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAINMETER_UI_SCALE_OVERRIDE", previousScale);
            Environment.SetEnvironmentVariable("RAINMETER_UI_THEME_OVERRIDE", previousTheme);
            foreach (string path in new[] { tile, window, theme })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
