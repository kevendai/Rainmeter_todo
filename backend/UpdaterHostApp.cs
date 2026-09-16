using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RainmeterUpdater
{
    internal static class Program
    {
        private const string UpdaterVersion = "2.0";
        private const long MaxArchiveBytes = 512L * 1024 * 1024;
        private const long MaxEntryBytes = 256L * 1024 * 1024;
        private const int MaxEntries = 8192;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 };
        private static readonly string LogRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterDesktopWidgets", "Updater");
        private static readonly string ErrorLog = Path.Combine(LogRoot, "last-error.log");

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                ServicePointManager.Expect100Continue = false;
                Options options = Options.Parse(args);
                if (options.DelayMilliseconds > 0) Thread.Sleep(Math.Min(options.DelayMilliseconds, 10000));
                if (options.Mode.Equals("SelfTest", StringComparison.OrdinalIgnoreCase)) return SelfTest();
                if (options.Mode.Equals("UpdateUpdater", StringComparison.OrdinalIgnoreCase)) { UpdateUpdater(options); return 0; }
                if (options.Mode.Equals("InstallPackage", StringComparison.OrdinalIgnoreCase)) { WaitForProcess(options.WaitForProcessId); InstallPackage(options.PackageRoot, options.RainmeterRoot, options.Activate, true); return 0; }
                if (options.Mode.Equals("CheckAndInstall", StringComparison.OrdinalIgnoreCase)) { CheckAndInstall(options); return 0; }
                throw new InvalidOperationException("Unknown updater mode: " + options.Mode);
            }
            catch (Exception ex)
            {
                try { Directory.CreateDirectory(LogRoot); File.WriteAllText(ErrorLog, DateTimeOffset.Now.ToString("o") + "\r\n" + ex + "\r\n", new UTF8Encoding(false)); } catch { }
                try { MessageBox.Show("更新失败：" + ex.Message + "\r\n\r\n错误日志：" + ErrorLog, "Rainmeter Desktop Widgets Update", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                return 1;
            }
        }

        private static int SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "rw-updater-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                if (CompareVersions("2.0.3", "2.0.2") <= 0 || NormalizeVersion("v2.0.3") != "2.0.3") return 2;
                string candidate = SafeDestination(root, "folder/file.txt");
                if (!candidate.StartsWith(FullDirectory(root), StringComparison.OrdinalIgnoreCase)) return 3;
                try { SafeDestination(root, "../escape"); return 4; } catch (InvalidDataException) { }
                return 0;
            }
            finally { TryDeleteDirectory(root); }
        }

        private static void CheckAndInstall(Options options)
        {
            string latestTag = GetLatestTag(options.Repository);
            string latestVersion = NormalizeVersion(latestTag);
            int comparison = CompareVersions(latestVersion, options.CurrentVersion);
            if (comparison <= 0)
            {
                MessageBox.Show(comparison == 0 ? "已经是最新版本：" + latestTag : "当前版本比最新发布版更新。", "Rainmeter Desktop Widgets", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!options.AssumeYes && MessageBox.Show("发现新版本 " + latestTag + "。\r\n\r\n现在下载并安装吗？Rainmeter 将重新启动。", "Rainmeter Desktop Widgets Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string asset = "rainmeter-desktop-widgets-" + latestVersion + ".zip";
            string temp = Path.Combine(Path.GetTempPath(), "RainmeterDesktopWidgetsUpdate-" + Guid.NewGuid().ToString("N"));
            string zip = Path.Combine(temp, asset);
            string extract = Path.Combine(temp, "package");
            Directory.CreateDirectory(temp);
            try
            {
                DownloadVerifiedReleaseAsset(options.Repository, latestTag, asset, zip);
                ZipExtractor.Extract(zip, extract);
                string packageRoot = FindPackageRoot(extract);
                string updater = Path.Combine(packageRoot, "Updater", "UpdaterHost.exe");
                if (!File.Exists(updater)) throw new InvalidDataException("更新包中缺少 UpdaterHost.exe。");
                Process child = Process.Start(new ProcessStartInfo(updater, BuildArguments("InstallPackage", packageRoot, options.RainmeterRoot, options.Activate) + " -WaitForProcessId " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = false, CreateNoWindow = false });
                if (child == null) throw new InvalidOperationException("无法启动新版本更新器。");
                child.Dispose();
                temp = ""; // Detached child owns the extracted package.
            }
            finally { TryDeleteDirectory(temp); }
        }

        private static void UpdateUpdater(Options options)
        {
            string source = String.IsNullOrWhiteSpace(options.PackageRoot) ? Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).FullName : Path.GetFullPath(options.PackageRoot);
            Roots roots = GetRoots(options.RainmeterRoot);
            CopyUpdaterFiles(source, roots.SkinsRoot);
        }

        private static void InstallPackage(string sourcePackageRoot, string rainmeterRoot, bool activate, bool allowBootstrap)
        {
            if (String.IsNullOrWhiteSpace(sourcePackageRoot)) sourcePackageRoot = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).FullName;
            sourcePackageRoot = Path.GetFullPath(sourcePackageRoot);
            string bootstrapPath = Path.Combine(sourcePackageRoot, "unified-bootstrap.json");
            if (allowBootstrap && File.Exists(bootstrapPath))
            {
                IDictionary<string, object> bootstrap = ReadObject(bootstrapPath);
                string repo = Text(bootstrap, "repository"), tag = Text(bootstrap, "tag"), asset = Text(bootstrap, "asset"), bootstrapHash = Text(bootstrap, "sha256");
                if (!Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") || !Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || Path.GetFileName(asset) != asset || !asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(bootstrapHash, @"(?i)^[0-9a-f]{64}$")) throw new InvalidDataException("兼容引导元数据无效。");
                string temp = Path.Combine(Path.GetTempPath(), "rw-bootstrap-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    string zip = Path.Combine(temp, asset), extract = Path.Combine(temp, "package");
                    DownloadVerifiedReleaseAsset(repo, tag, asset, zip, bootstrapHash);
                    ZipExtractor.Extract(zip, extract);
                    InstallPackage(FindPackageRoot(extract), rainmeterRoot, activate, false);
                    CopyUpdaterFiles(sourcePackageRoot, GetRoots(rainmeterRoot).SkinsRoot);
                    return;
                }
                finally { TryDeleteDirectory(temp); }
            }

            Package package = ValidatePackage(sourcePackageRoot);
            Roots roots = GetRoots(rainmeterRoot);
            string rainmeterExe = FindRainmeterExe(roots.RainmeterRoot);
            string transaction = Path.Combine(roots.SkinsRoot, ".rainmeter-update-" + Guid.NewGuid().ToString("N"));
            var swapped = new List<string>();
            var preserved = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            List<FileSnapshot> pluginSnapshot = null;
            bool rainmeterWasRunning = false;
            try
            {
                // Build the new skin tree before touching Rainmeter.  The
                // actual swap is only a pair of fast Directory.Move calls, so
                // it can happen the moment the directory locks are released.
                pluginSnapshot = SnapshotPluginState(package.PluginIds, transaction);
                foreach (string skin in new[] { "Todo", "Calendar" })
                {
                    string source = Path.Combine(sourcePackageRoot, "Skins", skin);
                    string target = Path.Combine(roots.SkinsRoot, skin);
                    string stage = Path.Combine(transaction, "stage", skin);
                    CopyDirectory(source, stage);
                    var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in PreservedNames())
                    {
                        string current = Path.Combine(target, "@Resources", name);
                        if (!File.Exists(current)) continue;
                        hashes[name] = Sha256(current);
                        string destination = Path.Combine(stage, "@Resources", name);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        File.Copy(current, destination, true);
                    }
                    preserved[skin] = hashes;
                    string scaleInclude = Path.Combine(stage, "@Resources", "UiScale.inc");
                    if (File.Exists(scaleInclude)) File.Delete(scaleInclude);
                }
                CopyUpdaterFilesToStage(sourcePackageRoot, Path.Combine(transaction, "stage", "Todo", "@Resources", "Updater"));
                VerifyPreserved(transaction, preserved, "stage");

                // Release every skin/host handle before swapping directories.
                rainmeterWasRunning = IsExactProcessRunning("Rainmeter", rainmeterExe);
                if (rainmeterWasRunning) StopRainmeter(rainmeterExe);
                StopKnownHosts(roots.SkinsRoot);
                StopManagedPluginProcesses();
                RecoverInterruptedSkins(roots.SkinsRoot);

                // Make sure the target directories are actually unlocked
                // before we try to move them; otherwise Directory.Move throws
                // "access denied" while Rainmeter is still releasing handles.
                foreach (string skin in new[] { "Todo", "Calendar" })
                {
                    string target = Path.Combine(roots.SkinsRoot, skin);
                    if (Directory.Exists(target) && !WaitForDirectoryUnlocked(target, 30000))
                        throw new TimeoutException(skin + " 目录在进程退出后仍处于锁定状态，请关闭 Rainmeter 后重试。");
                }

                foreach (string skin in new[] { "Todo", "Calendar" })
                {
                    string target = Path.Combine(roots.SkinsRoot, skin), stage = Path.Combine(transaction, "stage", skin), backup = Path.Combine(transaction, "backup", skin);
                    MoveDirectoryWithRetry(target, backup);
                    MoveDirectoryWithRetry(stage, target);
                    swapped.Add(skin);
                }
                VerifyPreserved(roots.SkinsRoot, preserved, null);
                ValidateInstalledHosts(roots.SkinsRoot, package.RequiresPluginHost);
                TryDeleteDirectory(transaction);
                if (File.Exists(rainmeterExe)) StartAndRefreshRainmeter(rainmeterExe, activate);
            }
            catch
            {
                RestorePluginState(pluginSnapshot);
                RollbackSkins(roots.SkinsRoot, transaction, swapped);
                throw;
            }
        }

        private static readonly string[] Preserve = { "tasks.json", "Generated.inc", "PluginValues.inc", "ui-scale.txt", "ui-window-scale.txt", "calendar-cache.json", "calendar-state.json", "caldav.secret", "translation.secret", "paper-sync.secret" };
        private static IEnumerable<string> PreservedNames() { return Preserve; }

        private sealed class Package { public bool RequiresPluginHost; public List<string> PluginIds = new List<string>(); }
        private static Package ValidatePackage(string root)
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("更新包缺少 manifest.json。");
            IDictionary<string, object> manifest = ReadObject(manifestPath);
            string version = NormalizeVersion(Text(manifest, "version"));
            if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("更新包版本格式无效。");
            if (CompareVersions(Text(manifest, "updater_version"), UpdaterVersion) > 0) throw new InvalidDataException("更新包需要更高版本的更新器。");
            foreach (string skin in new[] { "Todo", "Calendar" })
            {
                string skinRoot = Path.Combine(root, "Skins", skin);
                string host = Path.Combine(skinRoot, "@Resources", skin + "Host.exe");
                string versionFile = Path.Combine(skinRoot, "@Resources", "app-version.txt");
                if (!Directory.Exists(skinRoot) || !File.Exists(host) || !File.Exists(versionFile) || File.ReadAllText(versionFile, Encoding.UTF8).Trim() != version) throw new InvalidDataException(skin + " 文件或版本不完整。");
            }
            var result = new Package { RequiresPluginHost = CompareVersions(version, "2.0.0") >= 0 };
            string lockPath = Path.Combine(root, "Skins", "Todo", "@Resources", "bundled-plugins.lock.json");
            if (result.RequiresPluginHost)
            {
                if (!File.Exists(Path.Combine(root, "Skins", "Todo", "@Resources", "PluginHost.exe")) || !File.Exists(lockPath)) throw new InvalidDataException("更新包缺少插件运行时资源。");
                IDictionary<string, object> pluginLock = ReadObject(lockPath);
                object raw; if (!pluginLock.TryGetValue("plugins", out raw)) throw new InvalidDataException("插件锁文件无效。");
                foreach (object item in (IEnumerable)raw)
                {
                    IDictionary<string, object> entry = item as IDictionary<string, object>;
                    if (entry == null) throw new InvalidDataException("插件锁条目无效。");
                    string id = Text(entry, "id"), file = Text(entry, "file"), expected = Text(entry, "sha256").ToLowerInvariant();
                    if (!Regex.IsMatch(id, @"^([A-Za-z0-9-]+\.){2,}[A-Za-z0-9-]+$") || Path.GetFileName(file) != file || !Regex.IsMatch(expected, "^[0-9a-f]{64}$")) throw new InvalidDataException("插件锁条目无效：" + id);
                    string packagePath = Path.Combine(root, "Skins", "Todo", "@Resources", "BundledPluginPackages", file);
                    if (!File.Exists(packagePath) || Sha256(packagePath).ToLowerInvariant() != expected) throw new InvalidDataException("捆绑插件校验失败：" + id);
                    result.PluginIds.Add(id);
                }
            }
            return result;
        }

        private static void DownloadVerifiedReleaseAsset(string repository, string tag, string asset, string destination, string expectedHash = "")
        {
            string baseUrl = "https://github.com/" + repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
            if (String.IsNullOrWhiteSpace(expectedHash))
            {
                string checksum = DownloadText(baseUrl + Uri.EscapeDataString(asset + ".sha256"), 20);
                Match match = Regex.Match(checksum.Trim(), @"(?i)^([0-9a-f]{64})(?:\s+\*?.+)?$");
                if (!match.Success) throw new InvalidDataException("SHA256 文件格式无效。");
                expectedHash = match.Groups[1].Value;
            }
            DownloadFile(baseUrl + Uri.EscapeDataString(asset), destination, 120);
            if (!Sha256(destination).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载文件 SHA256 校验失败。");
        }

        private static string GetLatestTag(string repository)
        {
            HttpWebRequest request = CreateRequest("https://github.com/" + repository + "/releases/latest", "HEAD", 20);
            request.AllowAutoRedirect = true;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                string tag = Uri.UnescapeDataString(response.ResponseUri.Segments.Last().Trim('/'));
                if (!Regex.IsMatch(NormalizeVersion(tag), @"^\d")) throw new InvalidDataException("GitHub 未返回有效版本标签。");
                return tag;
            }
        }

        private static HttpWebRequest CreateRequest(string url, string method, int seconds)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method; request.UserAgent = "RainmeterDesktopWidgetsUpdater/" + UpdaterVersion; request.Timeout = seconds * 1000; request.ReadWriteTimeout = seconds * 1000; request.KeepAlive = false; request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return request;
        }
        private static string DownloadText(string url, int seconds)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++) try { using (HttpWebResponse response = (HttpWebResponse)CreateRequest(url, "GET", seconds).GetResponse()) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd(); } catch (Exception ex) { last = ex; Thread.Sleep(400 * (attempt + 1)); }
            throw last;
        }
        private static void DownloadFile(string url, string path, int seconds)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    using (HttpWebResponse response = (HttpWebResponse)CreateRequest(url, "GET", seconds).GetResponse())
                    {
                        if (response.ContentLength > MaxArchiveBytes) throw new InvalidDataException("更新包超过大小限制。");
                        using (Stream input = response.GetResponseStream()) using (FileStream output = File.Create(path)) CopyLimited(input, output, MaxArchiveBytes);
                    }
                    return;
                }
                catch (Exception ex) { last = ex; Thread.Sleep(400 * (attempt + 1)); }
            }
            throw last;
        }

        private static Roots GetRoots(string root)
        {
            string value = (root ?? "").Trim().Trim('"');
            if (value.Length == 0) throw new ArgumentException("RainmeterRoot 不能为空。");
            value = Path.GetFullPath(value);
            string skins = Path.Combine(value, "Skins"), rainmeter = value;
            if (Path.GetFileName(value.TrimEnd('\\')).Equals("Skins", StringComparison.OrdinalIgnoreCase)) { skins = value; rainmeter = Directory.GetParent(value).FullName; }
            else if (File.Exists(Path.Combine(value, "Rainmeter.exe")))
            {
                string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rainmeter", "Rainmeter.ini");
                if (File.Exists(ini)) { Match m = Regex.Match(File.ReadAllText(ini), @"(?m)^SkinPath=(.+)$"); if (m.Success) skins = Path.GetFullPath(Environment.ExpandEnvironmentVariables(m.Groups[1].Value.Trim().Trim('"'))); }
            }
            Directory.CreateDirectory(skins);
            return new Roots { RainmeterRoot = rainmeter, SkinsRoot = skins };
        }
        private sealed class Roots { public string RainmeterRoot; public string SkinsRoot; }

        private static string FindRainmeterExe(string root)
        {
            string direct = Path.Combine(root, "Rainmeter.exe"); if (File.Exists(direct)) return direct;
            foreach (Process p in Process.GetProcessesByName("Rainmeter")) try { if (!String.IsNullOrEmpty(p.MainModule.FileName)) return p.MainModule.FileName; } catch { } finally { p.Dispose(); }
            foreach (string keyName in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Rainmeter.exe", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\Rainmeter.exe" }) using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyName)) { string value = key == null ? null : key.GetValue(null) as string; if (!String.IsNullOrWhiteSpace(value)) return value; }
            return direct;
        }

        private static void CopyUpdaterFiles(string packageRoot, string skinsRoot) { CopyUpdaterFilesToStage(packageRoot, Path.Combine(skinsRoot, "Todo", "@Resources", "Updater")); }
        private static void CopyUpdaterFilesToStage(string packageRoot, string target)
        {
            string source = Path.Combine(packageRoot, "Updater");
            // v1.4.4 is intentionally the first compatibility hop, but its
            // historical unified package predates UpdaterHost.exe.  Keep the
            // bridge updater that is currently executing so the next hop is
            // already EXE-based after the old skins are installed.
            if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, "UpdaterHost.exe")))
                source = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, "UpdaterHost.exe")) || !File.Exists(Path.Combine(source, "RainmeterDesktopWidgetsUpdater.ps1"))) throw new InvalidDataException("更新包的 Updater 目录不完整。");
            if (Directory.Exists(target)) TryDeleteDirectory(target);
            CopyDirectory(source, target);
        }
        private static string FindPackageRoot(string extract)
        {
            foreach (string manifest in Directory.GetFiles(extract, "manifest.json", SearchOption.AllDirectories))
            {
                string root = Path.GetDirectoryName(manifest);
                if (Directory.Exists(Path.Combine(root, "Skins")) &&
                    (File.Exists(Path.Combine(root, "Updater", "UpdaterHost.exe")) ||
                     File.Exists(Path.Combine(root, "Updater", "RainmeterDesktopWidgetsUpdater.ps1")))) return root;
            }
            throw new InvalidDataException("压缩包中未找到完整更新包。");
        }

        private static void StopKnownHosts(string skinsRoot)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(skinsRoot, "Todo", "@Resources", "TodoHost.exe"), Path.Combine(skinsRoot, "Calendar", "@Resources", "CalendarHost.exe"), Path.Combine(skinsRoot, "Todo", "@Resources", "PluginHost.exe") };
            foreach (string name in new[] { "TodoHost", "CalendarHost", "PluginHost" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (allowed.Contains(path))
                        {
                            try { p.CloseMainWindow(); } catch { }
                            if (!p.WaitForExit(2000)) { p.Kill(); p.WaitForExit(6000); }
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
        }
        private static void StopManagedPluginProcesses()
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterDesktopWidgets"), pluginRoot = FullDirectory(Path.Combine(local, "Plugins")), jobs = Path.Combine(local, "PluginJobs");
            if (!Directory.Exists(jobs)) return;
            foreach (string file in Directory.GetFiles(jobs, "*.json")) try { IDictionary<string, object> job = ReadObject(file); int pid = Convert.ToInt32(job["pid"], CultureInfo.InvariantCulture); string entry = Path.GetFullPath(Text(job, "entry")); if (!entry.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase)) continue; using (Process p = Process.GetProcessById(pid)) { if (!Path.GetFullPath(p.MainModule.FileName).Equals(entry, StringComparison.OrdinalIgnoreCase)) continue; try { p.CloseMainWindow(); } catch { } if (!p.WaitForExit(3000)) p.Kill(); } job["state"] = "cancelled"; job["message"] = "Cancelled for host update"; job.Remove("pid"); job.Remove("entry"); job.Remove("process_started_at"); File.WriteAllText(file, Json.Serialize(job), new UTF8Encoding(false)); } catch { }
        }
        private static void RecoverInterruptedSkins(string skinsRoot)
        {
            foreach (string skin in new[] { "Todo", "Calendar" })
            {
                string target = Path.Combine(skinsRoot, skin); if (Directory.Exists(target)) continue;
                foreach (string tx in Directory.GetDirectories(skinsRoot, ".rainmeter-update-*" ).OrderByDescending(Directory.GetLastWriteTimeUtc)) { string backup = Path.Combine(tx, "backup", skin); if (Directory.Exists(backup)) { Directory.Move(backup, target); break; } }
            }
        }
        private sealed class FileSnapshot { public string Path; public bool Existed; public string Backup; }
        private static List<FileSnapshot> SnapshotPluginState(IEnumerable<string> ids, string tx)
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterDesktopWidgets"), root = Path.Combine(tx, "plugin-state"); Directory.CreateDirectory(root);
            var paths = new List<string> { Path.Combine(local, "migration-v2.json") };
            foreach (string id in ids) { paths.Add(Path.Combine(local, "Plugins", id, "current.json")); foreach (string name in new[] { "config.json", "secret.dat", "state.json" }) paths.Add(Path.Combine(local, "PluginData", id, name)); }
            var result = new List<FileSnapshot>(); int index = 0;
            foreach (string path in paths) { string backup = Path.Combine(root, "file-" + index++); bool exists = File.Exists(path); if (exists) File.Copy(path, backup, true); result.Add(new FileSnapshot { Path = path, Existed = exists, Backup = backup }); }
            return result;
        }
        private static void RestorePluginState(IEnumerable<FileSnapshot> snapshots) { if (snapshots == null) return; foreach (FileSnapshot item in snapshots) try { if (item.Existed) { Directory.CreateDirectory(Path.GetDirectoryName(item.Path)); File.Copy(item.Backup, item.Path, true); } else if (File.Exists(item.Path)) File.Delete(item.Path); } catch { } }
        private static void VerifyPreserved(string root, IDictionary<string, Dictionary<string, string>> all, string stagePrefix)
        {
            foreach (var skin in all) foreach (var file in skin.Value) { string path = stagePrefix == null ? Path.Combine(root, skin.Key, "@Resources", file.Key) : Path.Combine(root, stagePrefix, skin.Key, "@Resources", file.Key); if (!File.Exists(path) || !Sha256(path).Equals(file.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("用户数据校验失败：" + skin.Key + "/" + file.Key); }
        }
        private static void RollbackSkins(string skinsRoot, string tx, IList<string> swapped)
        {
            for (int i = swapped.Count - 1; i >= 0; i--) { string skin = swapped[i], target = Path.Combine(skinsRoot, skin), backup = Path.Combine(tx, "backup", skin); TryDeleteDirectory(target); if (Directory.Exists(backup)) Directory.Move(backup, target); }
        }
        private static void ValidateInstalledHosts(string skinsRoot, bool plugin)
        {
            var probes = new List<Tuple<string, string>> { Tuple.Create(Path.Combine(skinsRoot, "Todo", "@Resources", "TodoHost.exe"), "Render"), Tuple.Create(Path.Combine(skinsRoot, "Calendar", "@Resources", "CalendarHost.exe"), "Render") };
            if (plugin) probes.Insert(0, Tuple.Create(Path.Combine(skinsRoot, "Todo", "@Resources", "PluginHost.exe"), "SelfTest"));
            foreach (var probe in probes) { if (!File.Exists(probe.Item1)) throw new FileNotFoundException("安装后的宿主缺失。", probe.Item1); using (Process p = Process.Start(new ProcessStartInfo(probe.Item1, probe.Item2) { UseShellExecute = false, CreateNoWindow = true })) { if (!p.WaitForExit(30000)) { p.Kill(); throw new TimeoutException(Path.GetFileName(probe.Item1) + " 验证超时。"); } if (p.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(probe.Item1) + " 验证失败：" + p.ExitCode); } }
        }
        private static void StopRainmeter(string exe)
        {
            if (!File.Exists(exe)) return;
            TryStart(exe, "!Quit");
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && IsExactProcessRunning("Rainmeter", exe)) Thread.Sleep(250);
            if (!IsExactProcessRunning("Rainmeter", exe)) { Thread.Sleep(500); return; }
            // Some Rainmeter builds do not process !Quit while a skin/plugin
            // modal action is active.  Only force-close the process whose
            // executable path exactly matches the discovered installation.
            foreach (Process process in Process.GetProcessesByName("Rainmeter"))
            {
                try
                {
                    string path = "";
                    try { path = process.MainModule.FileName; } catch { }
                    if (path.Length == 0 || !Path.GetFullPath(path).Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) continue;
                    try { process.CloseMainWindow(); } catch { }
                    if (!process.WaitForExit(2000)) process.Kill();
                    process.WaitForExit(10000);
                }
                catch { }
                finally { process.Dispose(); }
            }
            if (IsExactProcessRunning("Rainmeter", exe)) throw new TimeoutException("Rainmeter 未能在退出超时后结束。");
            // The process is gone, but Windows releases the file handles a
            // moment later.  Give the file system time before the swap.
            Thread.Sleep(1000);
        }

        private static void MoveDirectoryWithRetry(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Exception last = null;
            DateTime until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until)
            {
                try { Directory.Move(source, destination); return; }
                catch (IOException ex) { last = ex; }
                catch (UnauthorizedAccessException ex) { last = ex; }
                Thread.Sleep(250);
            }
            throw new IOException("无法移动目录：" + source + " -> " + destination + "（" + (last == null ? "未知原因" : last.Message) + "）");
        }

        private static bool WaitForDirectoryUnlocked(string directory, int timeoutMilliseconds)
        {
            if (!Directory.Exists(directory)) return true;
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < until)
            {
                string probe = Path.Combine(directory, ".rw-unlock-probe-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(probe);
                    Directory.Delete(probe);
                    return true;
                }
                catch { try { if (Directory.Exists(probe)) Directory.Delete(probe, true); } catch { } }
                Thread.Sleep(250);
            }
            return false;
        }
        private static void StartAndRefreshRainmeter(string exe, bool activate)
        {
            if (!File.Exists(exe)) return;
            if (IsExactProcessRunning("Rainmeter", exe)) StopRainmeter(exe);
            TryStart(exe, ""); Thread.Sleep(1200); TryStart(exe, "!RefreshApp");
            if (activate) { Thread.Sleep(800); TryStart(exe, "!ActivateConfig \"Todo\" \"Todo.ini\""); TryStart(exe, "!ActivateConfig \"Calendar\" \"Calendar.ini\""); Thread.Sleep(800); TryStart(exe, "!SetWindowPosition \"100%\" \"0%\" \"100%\" \"0%\" \"Todo\""); }
        }
        private static bool IsExactProcessRunning(string name, string exe) { if (String.IsNullOrWhiteSpace(exe)) return false; foreach (Process p in Process.GetProcessesByName(name)) try { if (Path.GetFullPath(p.MainModule.FileName).Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return true; } catch { } finally { p.Dispose(); } return false; }
        private static void TryStart(string file, string args) { try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); } catch { } }

        private static void CopyDirectory(string source, string destination) { if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source); Directory.CreateDirectory(destination); foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, directory.Substring(source.Length).TrimStart('\\'))); foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) { string target = Path.Combine(destination, file.Substring(source.Length).TrimStart('\\')); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true); } }
        private static string Sha256(string path) { using (SHA256 hash = SHA256.Create()) using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static void CopyLimited(Stream input, Stream output, long maximum) { byte[] buffer = new byte[81920]; long total = 0; int read; while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { total += read; if (total > maximum) throw new InvalidDataException("数据超过允许大小。"); output.Write(buffer, 0, read); } }
        private static void TryDeleteDirectory(string path) { if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return; try { Directory.Delete(path, true); } catch { } }
        private static string FullDirectory(string path) { return Path.GetFullPath(path).TrimEnd('\\') + "\\"; }
        private static string SafeDestination(string root, string relative) { relative = (relative ?? "").Replace('/', '\\'); if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('\\').Any(x => x == "..")) throw new InvalidDataException("压缩包路径无效。"); string result = Path.GetFullPath(Path.Combine(root, relative)); if (!result.StartsWith(FullDirectory(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("压缩包路径越界。"); return result; }
        private static IDictionary<string, object> ReadObject(string path) { object value = Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)); var dictionary = value as IDictionary<string, object>; if (dictionary == null) throw new InvalidDataException("JSON 对象格式无效：" + path); return dictionary; }
        private static string Text(IDictionary<string, object> item, string key) { object value; return item != null && item.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static string NormalizeVersion(string value) { value = (value ?? "").Trim(); if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1); Match m = Regex.Match(value, @"\d+(?:\.\d+){0,3}"); return m.Success ? m.Value : value; }
        private static int CompareVersions(string left, string right) { int[] a = NormalizeVersion(left).Split('.').Select(ParseInt).ToArray(), b = NormalizeVersion(right).Split('.').Select(ParseInt).ToArray(); for (int i = 0; i < Math.Max(a.Length, b.Length); i++) { int av = i < a.Length ? a[i] : 0, bv = i < b.Length ? b[i] : 0; if (av != bv) return av.CompareTo(bv); } return 0; }
        private static int ParseInt(string value) { int result; return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0; }
        private static string BuildArguments(string mode, string package, string root, bool activate) { return "-Mode " + mode + " -PackageRoot \"" + package.Replace("\"", "\\\"") + "\" -RainmeterRoot \"" + (root ?? "").Replace("\"", "\\\"") + "\"" + (activate ? " -Activate" : ""); }

        private static void WaitForProcess(int pid) { if (pid <= 0) return; try { using (Process process = Process.GetProcessById(pid)) { if (!process.WaitForExit(30000)) throw new TimeoutException("更新器进程交接超时。"); } } catch (ArgumentException) { } }

        private sealed class Options
        {
            public string Mode = "InstallPackage", Repository = "kevendai/Rainmeter_todo", CurrentVersion = "", PackageRoot = "", RainmeterRoot = ""; public bool Activate, AssumeYes; public int WaitForProcessId, DelayMilliseconds;
            public static Options Parse(string[] args) { var o = new Options(); for (int i = 0; i < args.Length; i++) { string key = args[i].TrimStart('-', '/'); string value = i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[++i] : null; switch (key.ToLowerInvariant()) { case "mode": o.Mode = value ?? o.Mode; break; case "repository": o.Repository = value ?? o.Repository; break; case "currentversion": o.CurrentVersion = value ?? ""; break; case "packageroot": o.PackageRoot = value ?? ""; break; case "rainmeterroot": o.RainmeterRoot = value ?? ""; break; case "activate": o.Activate = true; if (value != null) i--; break; case "assumeyes": o.AssumeYes = true; if (value != null) i--; break; case "waitforprocessid": Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out o.WaitForProcessId); break; case "delaymilliseconds": Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out o.DelayMilliseconds); break; } } return o; }
        }

        private sealed class ZipEntryInfo { public string Name; public ushort Method, Flags; public uint Crc, Compressed, Uncompressed, LocalOffset, External; }
        private static class ZipExtractor
        {
            public static void Extract(string archive, string destination)
            {
                if (new FileInfo(archive).Length > MaxArchiveBytes) throw new InvalidDataException("压缩包超过大小限制。");
                Directory.CreateDirectory(destination);
                using (FileStream stream = File.OpenRead(archive))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    List<ZipEntryInfo> entries = ReadCentralDirectory(stream, reader);
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                    foreach (ZipEntryInfo entry in entries)
                    {
                        string normalized = entry.Name.Replace('/', '\\'); bool directory = normalized.EndsWith("\\", StringComparison.Ordinal);
                        if (!names.Add(normalized.TrimEnd('\\'))) throw new InvalidDataException("压缩包包含重复路径：" + entry.Name);
                        if ((entry.Flags & 1) != 0 || (entry.Method != 0 && entry.Method != 8)) throw new InvalidDataException("压缩包使用了不支持的加密或压缩方式。");
                        if (entry.Uncompressed > MaxEntryBytes || (total += entry.Uncompressed) > MaxArchiveBytes) throw new InvalidDataException("解压后文件超过大小限制。");
                        if (((entry.External >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("压缩包不允许符号链接。");
                        string target = SafeDestination(destination, normalized);
                        if (directory) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        stream.Position = entry.LocalOffset;
                        if (reader.ReadUInt32() != 0x04034b50) throw new InvalidDataException("ZIP 本地文件头无效。");
                        stream.Position += 22; ushort nameLength = reader.ReadUInt16(), extraLength = reader.ReadUInt16(); stream.Position += nameLength + extraLength;
                        using (var bounded = new BoundedStream(stream, entry.Compressed))
                        using (Stream input = entry.Method == 8 ? (Stream)new DeflateStream(bounded, CompressionMode.Decompress, true) : bounded)
                        using (FileStream output = File.Create(target))
                        {
                            var crc = new Crc32(); byte[] buffer = new byte[81920]; long written = 0; int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { written += read; if (written > entry.Uncompressed) throw new InvalidDataException("ZIP 条目长度无效。"); crc.Update(buffer, 0, read); output.Write(buffer, 0, read); }
                            if (written != entry.Uncompressed || crc.Value != entry.Crc) throw new InvalidDataException("ZIP 条目校验失败：" + entry.Name);
                        }
                    }
                }
            }
            private static List<ZipEntryInfo> ReadCentralDirectory(FileStream stream, BinaryReader reader)
            {
                long search = Math.Min(stream.Length, 65557); stream.Position = stream.Length - search; byte[] tail = reader.ReadBytes((int)search); int eocd = -1;
                for (int i = tail.Length - 22; i >= 0; i--) if (BitConverter.ToUInt32(tail, i) == 0x06054b50) { eocd = i; break; }
                if (eocd < 0) throw new InvalidDataException("ZIP 中央目录缺失。");
                ushort count = BitConverter.ToUInt16(tail, eocd + 10), comment = BitConverter.ToUInt16(tail, eocd + 20); uint size = BitConverter.ToUInt32(tail, eocd + 12), offset = BitConverter.ToUInt32(tail, eocd + 16);
                if (count > MaxEntries || eocd + 22 + comment > tail.Length || (long)offset + size > stream.Length) throw new InvalidDataException("ZIP 中央目录无效或过大。");
                stream.Position = offset; var entries = new List<ZipEntryInfo>(count);
                for (int i = 0; i < count; i++)
                {
                    if (reader.ReadUInt32() != 0x02014b50) throw new InvalidDataException("ZIP 中央目录条目无效。");
                    reader.ReadUInt16(); reader.ReadUInt16(); ushort flags = reader.ReadUInt16(), method = reader.ReadUInt16(); stream.Position += 4; uint crc = reader.ReadUInt32(), compressed = reader.ReadUInt32(), uncompressed = reader.ReadUInt32(); ushort nameLength = reader.ReadUInt16(), extraLength = reader.ReadUInt16(), commentLength = reader.ReadUInt16(); stream.Position += 4; uint external = reader.ReadUInt32(), local = reader.ReadUInt32(); byte[] nameBytes = reader.ReadBytes(nameLength); Encoding encoding = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(437); string name = encoding.GetString(nameBytes); stream.Position += extraLength + commentLength;
                    if (compressed == UInt32.MaxValue || uncompressed == UInt32.MaxValue || local == UInt32.MaxValue) throw new InvalidDataException("不支持 ZIP64 更新包。");
                    entries.Add(new ZipEntryInfo { Name = name, Method = method, Flags = flags, Crc = crc, Compressed = compressed, Uncompressed = uncompressed, LocalOffset = local, External = external });
                }
                return entries;
            }
        }
        private sealed class BoundedStream : Stream
        {
            private readonly Stream inner; private long remaining; public BoundedStream(Stream inner, long length) { this.inner = inner; remaining = length; }
            public override int Read(byte[] buffer, int offset, int count) { if (remaining <= 0) return 0; int read = inner.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read; }
            public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } } public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } } public override void Flush() { } public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); } public override void SetLength(long v) { throw new NotSupportedException(); } public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }
        private sealed class Crc32
        {
            private uint crc = 0xffffffff; private static readonly uint[] Table = Build(); public uint Value { get { return crc ^ 0xffffffff; } }
            public void Update(byte[] bytes, int offset, int count) { for (int i = offset; i < offset + count; i++) crc = Table[(crc ^ bytes[i]) & 0xff] ^ (crc >> 8); }
            private static uint[] Build() { var table = new uint[256]; for (uint i = 0; i < table.Length; i++) { uint value = i; for (int j = 0; j < 8; j++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; table[i] = value; } return table; }
        }
    }
}
