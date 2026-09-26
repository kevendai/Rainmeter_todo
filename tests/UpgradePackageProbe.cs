using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Diagnostics;
using System.Threading;

// Run with the published 2.1.0 ZIP, candidate ZIP and a new disposable directory.
// The executable also serves as a no-op Rainmeter stub inside that directory.
internal static class UpgradePackageProbe
{
    static int Main(string[] args)
    {
        if (args.Length != 3) return 0;
        try
        {
            string root = Path.GetFullPath(args[2]);
            if (Directory.Exists(root)) throw new Exception("Probe directory must not already exist.");
            Directory.CreateDirectory(root);
            string old = Path.Combine(root, "old"), package = Path.Combine(root, "package"), install = Path.Combine(root, "install");
            ZipFile.ExtractToDirectory(Path.GetFullPath(args[0]), old);
            Assembly assembly = Assembly.LoadFile(Path.Combine(old, "Updater", "UpdaterHost.exe"));
            assembly.GetType("RainmeterBackend.ZipArchiveReader", true).GetMethod("Extract").Invoke(null,
                new object[] { Path.GetFullPath(args[1]), package, 512L*1024*1024, 256L*1024*1024, 512L*1024*1024, 8192 });
            Console.WriteLine("Published 2.1.0 archive reader: PASS");
            Directory.CreateDirectory(install);
            Directory.Move(Path.Combine(old, "Skins"), Path.Combine(install, "Skins"));
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(install, "Rainmeter.exe"));
            string resources = Path.Combine(install, "Skins", "Todo", "@Resources");
            string pluginState = Path.Combine(root, "plugin-state");
            string snapshotId = "io.github.kevendai.paper-snapshot-sync";
            foreach (string bundled in Directory.GetDirectories(Path.Combine(resources, "BundledPlugins")))
            {
                var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                var manifest = json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(Path.Combine(bundled, "plugin.json")));
                string id = (string)manifest["id"], version = (string)manifest["version"];
                string plugin = Path.Combine(pluginState, "Plugins", id), destination = Path.Combine(plugin, "versions", version);
                foreach (string file in Directory.GetFiles(bundled, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(destination, file.Substring(bundled.Length).TrimStart('\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target);
                }
                File.WriteAllText(Path.Combine(plugin, "current.json"), "{\"version\":\"" + version + "\",\"enabled\":false}");
            }
            string taskData = "{\"version\":3,\"meta\":{\"status\":\"probe\"},\"tasks\":[]}";
            File.WriteAllText(Path.Combine(resources, "tasks.json"), taskData);
            File.WriteAllText(Path.Combine(resources, "ui-theme.txt"), "dark");
            File.WriteAllText(Path.Combine(resources, "retired-ui.dll"), "obsolete");
            Directory.CreateDirectory(Path.Combine(resources, "PaperCache"));
            File.WriteAllText(Path.Combine(resources, "PaperCache", "probe.json"), "cache");
            var start = new ProcessStartInfo(Path.Combine(package, "Updater", "UpdaterHost.exe"),
                "-Mode InstallPackage -PackageRoot \"" + package + "\" -RainmeterRoot \"" + install + "\" -Quiet -WaitForProcessId " + Process.GetCurrentProcess().Id);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = Path.GetTempPath();
            start.EnvironmentVariables["RAINMETER_PLUGIN_ROOT"] = Path.Combine(root, "plugin-state");
            start.EnvironmentVariables["RAINMETER_COMMANDS_DISABLED"] = "1";
            start.EnvironmentVariables["RW_UPDATER_QUIET"] = "1";
            using (Process child = Process.Start(start))
            {
                if (!child.WaitForExit(30000) || child.ExitCode != 0) throw new Exception("Updater handoff failed.");
            }
            string marker = Path.Combine(resources, "app-version.txt");
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == "2.2.0" &&
                    Directory.GetDirectories(Path.Combine(install, "Skins"), ".rainmeter-update-*").Length == 0) break;
                Thread.Sleep(500);
            }
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != "2.2.0") throw new Exception("Upgrade did not finish.");
            if (File.ReadAllText(Path.Combine(resources, "tasks.json")) != taskData ||
                File.ReadAllText(Path.Combine(resources, "ui-theme.txt")) != "dark" ||
                File.ReadAllText(Path.Combine(resources, "PaperCache", "probe.json")) != "cache") throw new Exception("User data changed.");
            if (File.Exists(Path.Combine(resources, "retired-ui.dll"))) throw new Exception("Retired file survived.");
            if (!File.Exists(Path.Combine(resources, "DesktopUI", "coreclr.dll"))) throw new Exception("Desktop runtime missing.");
            if (Directory.GetDirectories(Path.Combine(install, "Skins"), ".rainmeter-update-*").Length != 0) throw new Exception("Transaction remains.");
            string pluginCurrent = File.ReadAllText(Path.Combine(pluginState, "Plugins", snapshotId, "current.json"));
            if (!pluginCurrent.Contains("1.0.1") || !pluginCurrent.Contains("false")) throw new Exception("Existing plugin version/enablement migration failed.");
            Console.WriteLine("Waiting-parent handoff, installation, data preservation, legacy removal: PASS");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
