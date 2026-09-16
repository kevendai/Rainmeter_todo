using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RainmeterBackend;

// 插件包安装器的回归测试，替代原先的 PowerShell 版 tests/Test-PluginInstaller.ps1。
// 自带一个极简的 stored(未压缩) ZIP 写入器，因此可以构造各种坏包（路径穿越、缺清单、
// 字段非法）而不依赖任何外部工具或 PowerShell。
internal static class PluginInstallerProbe
{
    private static int checks, failures;
    private const string HostVersion = "2.0.3";

    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "rwplugin-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string plugins = Path.Combine(root, "Plugins");
            string good = WritePackage(root, "good.rwplugin", Entries(Manifest("io.github.example.probe", "1.2.3", 1, "2.0.0", "bin/Probe.exe", "todo_transform"), "bin/Probe.exe", "MZ-fake-plugin"));
            string sha = Sha256(good);

            InstalledPluginInfo info = PluginPackageInstaller.Install(good, plugins, HostVersion, sha);
            Expect(info.Id == "io.github.example.probe" && info.Version == "1.2.3" && info.Sha256 == sha, "合法包可安装并返回 id / 版本 / 哈希");
            Expect(File.Exists(Path.Combine(plugins, "io.github.example.probe", "versions", "1.2.3", "bin", "Probe.exe")), "入口文件被解压到 versions/<版本>/");
            string current = Path.Combine(plugins, "io.github.example.probe", "current.json");
            Expect(File.Exists(current), "写出 current.json");
            Expect(ReadText(current).IndexOf("\"enabled\":false", StringComparison.Ordinal) >= 0, "首次安装默认禁用");

            File.WriteAllText(current, "{\"version\":\"1.2.3\",\"enabled\":true}", new UTF8Encoding(false));
            PluginPackageInstaller.Install(good, plugins, HostVersion, sha);
            Expect(ReadText(current).IndexOf("true", StringComparison.Ordinal) >= 0, "同版本重装保留 enabled 开关");
            Expect(Directory.GetDirectories(Path.Combine(plugins, "io.github.example.probe", "versions")).Length == 1, "同版本重装不产生重复版本目录");

            Reject("SHA256 不匹配", "SHA256", delegate { PluginPackageInstaller.Install(good, plugins, HostVersion, new string('0', 64)); });
            Reject("包不存在", "插件包不存在", delegate { PluginPackageInstaller.Install(Path.Combine(root, "nope.rwplugin"), plugins, HostVersion, ""); });
            Reject("缺少 plugin.json", "plugin.json", delegate { Install(root, plugins, "nomanifest", new Dictionary<string, string> { { "readme.txt", "no manifest" } }, ""); });
            Reject("路径穿越条目", "路径", delegate { Install(root, plugins, "traversal", new Dictionary<string, string> { { "plugin.json", Manifest("io.github.example.probe", "1.2.3", 1, "2.0.0", "bin/Probe.exe", "todo_transform") }, { "../escape.txt", "escaped" } }, ""); });
            Expect(!File.Exists(Path.Combine(root, "escape.txt")) && !File.Exists(Path.Combine(plugins, "escape.txt")), "路径穿越没有写出任何文件");
            Reject("插件 ID 非法", "ID", delegate { Install(root, plugins, "badid", Entries(Manifest("Bad_Id", "1.2.3", 1, "2.0.0", "bin/Probe.exe", "todo_transform"), "bin/Probe.exe", "x"), ""); });
            Reject("版本号非法", "版本", delegate { Install(root, plugins, "badversion", Entries(Manifest("io.github.example.probe", "1.2", 1, "2.0.0", "bin/Probe.exe", "todo_transform"), "bin/Probe.exe", "x"), ""); });
            Reject("API 版本不支持", "API", delegate { Install(root, plugins, "badapi", Entries(Manifest("io.github.example.probe", "1.2.3", 2, "2.0.0", "bin/Probe.exe", "todo_transform"), "bin/Probe.exe", "x"), ""); });
            Reject("宿主版本过低", "更高版本", delegate { Install(root, plugins, "newhost", Entries(Manifest("io.github.example.probe", "1.2.3", 1, "9.9.9", "bin/Probe.exe", "todo_transform"), "bin/Probe.exe", "x"), ""); });
            Reject("capability 非法", "capability", delegate { Install(root, plugins, "badcap", Entries(Manifest("io.github.example.probe", "1.2.3", 1, "2.0.0", "bin/Probe.exe", "system_shell"), "bin/Probe.exe", "x"), ""); });
            Reject("入口越界", "入口", delegate { Install(root, plugins, "badescape", Entries(Manifest("io.github.example.probe", "1.2.3", 1, "2.0.0", "../outside.exe", "todo_transform"), "bin/Probe.exe", "x"), ""); });
            Reject("入口不存在", "入口", delegate { Install(root, plugins, "badentry", Entries(Manifest("io.github.example.probe", "1.2.3", 1, "2.0.0", "bin/Missing.exe", "todo_transform"), "bin/Probe.exe", "x"), ""); });
            Expect(Directory.GetDirectories(root, "rwplugin-*").Length == 0, "所有失败路径都不留暂存目录");
        }
        finally { if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("RWPLUGIN_PROBE_KEEP"))) { try { Directory.Delete(root, true); } catch { } } else { Console.WriteLine("KEEP " + root); } }
        Console.WriteLine((failures == 0 ? "PASS" : "FAIL") + " plugin installer probe: checks=" + checks.ToString() + " failures=" + failures.ToString());
        return failures == 0 ? 0 : 1;
    }

    private static InstalledPluginInfo Install(string root, string plugins, string name, Dictionary<string, string> entries, string sha)
    {
        string package = WritePackage(root, name + ".rwplugin", entries);
        return PluginPackageInstaller.Install(package, plugins, HostVersion, sha);
    }

    private static Dictionary<string, string> Entries(string manifest, string extraName, string extraValue)
    {
        return new Dictionary<string, string> { { "plugin.json", manifest }, { extraName, extraValue } };
    }

    private static string Manifest(string id, string version, int apiVersion, string minHost, string entry, string capability)
    {
        return "{\"id\":\"" + id + "\",\"name\":\"Probe\",\"version\":\"" + version + "\",\"api_version\":" + apiVersion.ToString() +
            ",\"min_host_version\":\"" + minHost + "\",\"entry\":\"" + entry + "\",\"capabilities\":[\"" + capability + "\"]}";
    }

    private static void Reject(string title, string expectedFragment, Action action)
    {
        try
        {
            action();
            Expect(false, title + " —— 本应被拒绝，却安装成功");
        }
        catch (Exception ex)
        {
            Expect(ex.Message.IndexOf(expectedFragment, StringComparison.Ordinal) >= 0, title + " —— 拒绝原因包含「" + expectedFragment + "」（实际：" + ex.Message + "）");
        }
    }

    private static void Expect(bool condition, string title)
    {
        checks++;
        if (condition) return;
        failures++;
        Console.WriteLine("FAIL: " + title);
    }

    private static string ReadText(string path) { return File.ReadAllText(path, Encoding.UTF8); }

    private static string Sha256(string path)
    {
        using (SHA256 hash = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    // 最简 ZIP 写入器：所有条目用 stored（method 0），UTF-8 名字位打开。
    private static string WritePackage(string directory, string name, Dictionary<string, string> entries)
    {
        string path = Path.Combine(directory, name);
        using (FileStream stream = File.Create(path))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            List<byte[]> names = new List<byte[]>(), payloads = new List<byte[]>();
            List<uint> crcs = new List<uint>(); List<long> offsets = new List<long>();
            foreach (KeyValuePair<string, string> entry in entries)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Key), payload = Encoding.UTF8.GetBytes(entry.Value);
                ZipCrc32 crc = new ZipCrc32(); crc.Update(payload, 0, payload.Length);
                offsets.Add(stream.Position);
                writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)0x800); writer.Write((ushort)0); writer.Write(0u);
                writer.Write(crc.Value); writer.Write((uint)payload.Length); writer.Write((uint)payload.Length);
                writer.Write((ushort)nameBytes.Length); writer.Write((ushort)0);
                writer.Write(nameBytes); writer.Write(payload);
                names.Add(nameBytes); payloads.Add(payload); crcs.Add(crc.Value);
            }
            long centralStart = stream.Position;
            for (int i = 0; i < names.Count; i++)
            {
                writer.Write(0x02014b50u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)0x800); writer.Write((ushort)0); writer.Write(0u);
                writer.Write(crcs[i]); writer.Write((uint)payloads[i].Length); writer.Write((uint)payloads[i].Length);
                writer.Write((ushort)names[i].Length); writer.Write((ushort)0); writer.Write((ushort)0);
                writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u);
                writer.Write((uint)offsets[i]);
                writer.Write(names[i]);
            }
            long centralSize = stream.Position - centralStart;
            writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)names.Count); writer.Write((ushort)names.Count);
            writer.Write((uint)centralSize); writer.Write((uint)centralStart); writer.Write((ushort)0);
        }
        return path;
    }
}
