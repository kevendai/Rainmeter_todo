using System;
using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using RainmeterBackend;

// PluginRuntime.ApplyPluginEnvironment 的回归测试。
// Windows 的环境块允许同时存在大小写不同的同名变量（例如安装器、Git Bash 或 CI 代理会额外
// 注入一份 PATH，于是 Path 与 PATH 并存）。ProcessStartInfo.EnvironmentVariables 懒加载时用
// StringDictionary 存键（键全部转小写），遇到这种重复会抛 ArgumentException，结果所有插件都
// 启动不了。这里同时验证「去重构造」与「环境真的送达子进程」两件事。
internal static class PluginEnvironmentProbe
{
    private static int checks, failures;

    private static int Main(string[] args)
    {
        Hashtable source = new Hashtable();
        source["Path"] = @"C:\first-path";
        source["PATH"] = @"C:\second-path";
        source["SystemRoot"] = @"C:\Windows";

        StringDictionary built = PluginRuntime.BuildChildEnvironment(source, @"C:\probe\data", "1.25");
        Expect(built != null, "大小写重复的环境块可以构造出子进程环境");
        Expect(built["path"] == @"C:\first-path", "同名不同大小写只保留最先出现的一项（与系统解析顺序一致）");
        Expect(built["systemroot"] == @"C:\Windows", "其余环境变量原样保留");
        Expect(built["rw_plugin_data_dir"] == @"C:\probe\data" && built["rw_window_scale"] == "1.25", "RW_* 变量被写入");
        Expect(built.Count == 4, "去重后条目数为 4，实际 " + built.Count.ToString());
        Expect(PluginRuntime.BuildChildEnvironment(null, @"C:\probe\data", "1") != null, "空环境块不抛异常");

        ProcessStartInfo info = new ProcessStartInfo(ComSpec()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        PluginRuntime.ApplyPluginEnvironment(info, @"C:\probe\data", "1.25");
        Expect(info.EnvironmentVariables["rw_plugin_data_dir"] == @"C:\probe\data", "写入后 EnvironmentVariables 仍可安全读取");
        Expect(info.EnvironmentVariables["rw_window_scale"] == "1.25", "窗口缩放写入成功");
        info.Arguments = "/c echo RW=[%RW_PLUGIN_DATA_DIR%] SCALE=[%RW_WINDOW_SCALE%] SYS=[%SystemRoot%]";
        using (Process child = Process.Start(info))
        {
            string text = child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            Expect(text.IndexOf("RW=[C:\\probe\\data]", StringComparison.Ordinal) >= 0, "插件子进程收到 RW_PLUGIN_DATA_DIR，实际输出 " + text.Trim());
            Expect(text.IndexOf("SCALE=[1.25]", StringComparison.Ordinal) >= 0, "插件子进程收到 RW_WINDOW_SCALE");
            Expect(text.IndexOf("SYS=[C:\\Windows]", StringComparison.OrdinalIgnoreCase) >= 0, "系统变量未被环境重建丢失");
        }

        Console.WriteLine((failures == 0 ? "PASS" : "FAIL") + " plugin environment probe: checks=" + checks.ToString() + " failures=" + failures.ToString());
        return failures == 0 ? 0 : 1;
    }

    private static string ComSpec()
    {
        string shell = Environment.GetEnvironmentVariable("ComSpec");
        if (!String.IsNullOrWhiteSpace(shell) && File.Exists(shell)) return shell;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    }

    private static void Expect(bool condition, string message)
    {
        checks++;
        if (!condition) { failures++; Console.WriteLine("FAIL " + message); }
    }
}
