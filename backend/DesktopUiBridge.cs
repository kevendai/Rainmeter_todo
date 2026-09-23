using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

internal static class DesktopUiBridge
{
    internal static bool TryOpen(string todoResourceDirectory, string page, string action, string id)
    {
        string configured = Environment.GetEnvironmentVariable("RAINMETER_DESKTOP_UI_EXE");
        string executable = !String.IsNullOrEmpty(configured)
            ? configured
            : Path.Combine(todoResourceDirectory, "DesktopUI", "Rainmeter.Desktop.exe");
        if (!File.Exists(executable)) return false;
        string route = Encode(page) + "|" + Encode(action) + "|" + Encode(id ?? "");
        if (TrySend(route, 150)) return true;
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

    private static bool TrySend(string route, int timeout)
    {
        try
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", "RainmeterDesktopUi", PipeDirection.Out))
            {
                pipe.Connect(timeout);
                using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false)))
                {
                    writer.WriteLine(route);
                    writer.Flush();
                }
            }
            return true;
        }
        catch { return false; }
    }

    private static string Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
    }

    private static string Quote(string value)
    {
        return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
