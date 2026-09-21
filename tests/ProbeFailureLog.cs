using System;
using System.IO;
using System.Text;

// 布局探针的失败原因落盘。套件用 Start-Process（-WindowStyle Hidden）起探针，探针的 stderr 拿不到，
// 只报一句 "exit code 1" 等于没有诊断信息（2026-09-21 Phase 8 查一次偶发失败就卡在这上面）。
// 探针把 scenario + 异常全文追加到 RAINMETER_PROBE_FAILURE_LOG 指向的文件；套件失败时把它读出来
// 拼进异常消息。环境变量没设时退回 exe 同目录的 probe-failures.log。
internal static class ProbeFailureLog
{
    public static void Record(string scenario, Exception failure)
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("RAINMETER_PROBE_FAILURE_LOG");
            if (String.IsNullOrWhiteSpace(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-failures.log");
            File.AppendAllText(path,
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)
                + " | " + scenario + " | " + failure + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }
}
