using System;
using System.Diagnostics;
using System.IO;

internal static class DesktopUiBridge
{
    internal static bool TryOpen(string todoResourceDirectory, string page, string action, string id)
    {
        string configured = Environment.GetEnvironmentVariable("RAINMETER_DESKTOP_UI_EXE");
        string executable = !String.IsNullOrEmpty(configured)
            ? configured
            : Path.Combine(todoResourceDirectory, "DesktopUI", "Rainmeter.Desktop.exe");
        if (!File.Exists(executable)) return false;
        try
        {
            ProcessStartInfo start = new ProcessStartInfo(executable);
            start.UseShellExecute = false;
            start.WorkingDirectory = Path.GetDirectoryName(executable);
            start.Arguments = Quote(page) + " " + Quote(action) + " " + Quote(id ?? "");
            Process process = Process.Start(start);
            if (process == null) return false;
            process.Dispose();
            return true;
        }
        catch { return false; }
    }

    private static string Quote(string value)
    {
        return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
