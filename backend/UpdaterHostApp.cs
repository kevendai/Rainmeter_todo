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
using Microsoft.Win32;
using RainmeterBackend;

namespace RainmeterUpdater
{
    internal static class Program
    {
        private const string UpdaterVersion = "2.1";
        private const long MaxArchiveBytes = 512L * 1024 * 1024;
        private const long MaxEntryBytes = 256L * 1024 * 1024;
        private const int MaxEntries = 8192;
        // A parent updater hands this process the package it downloaded and then
        // waits for us to exit.  Give it a moment to disappear on its own (that
        // is what the pre-2.0.2 parents do) before we take over separately.
        private const int HandoffGraceMilliseconds = 5000;
        private const string WorkerDirectoryPrefix = "RainmeterDesktopWidgetsWorker-";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 };
        private static readonly string LogRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterDesktopWidgets", "Updater");
        private static readonly string ErrorLog = Path.Combine(LogRoot, "last-error.log");
        // Unattended runs (and the automated update-chain tests) must never block
        // on a modal dialog; the switch and the environment variable are both
        // honoured because an older parent cannot forward a flag it never knew.
        private static bool Quiet;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                ServicePointManager.Expect100Continue = false;
                Options options = Options.Parse(args);
                Quiet = options.Quiet || IsTruthy(Environment.GetEnvironmentVariable("RW_UPDATER_QUIET"));
                RelocateWorkingDirectory(options);
                if (options.DelayMilliseconds > 0) Thread.Sleep(Math.Min(options.DelayMilliseconds, 10000));
                if (options.Mode.Equals("SelfTest", StringComparison.OrdinalIgnoreCase)) return SelfTest();
                if (options.Mode.Equals("UpdateUpdater", StringComparison.OrdinalIgnoreCase)) { UpdateUpdater(options); return 0; }
                if (options.Mode.Equals("InstallPackage", StringComparison.OrdinalIgnoreCase))
                {
                    // Returns true when the work was handed to a detached copy of
                    // this process; this one then exits so the parent can finish.
                    if (DetachFromWaitingParent(options)) return 0;
                    try { InstallPackage(options.PackageRoot, options.RainmeterRoot, options.Activate, true); }
                    finally { TryDeleteWorkerPackage(options.PackageRoot); }
                    return 0;
                }
                if (options.Mode.Equals("CheckAndInstall", StringComparison.OrdinalIgnoreCase)) { CheckAndInstall(options); return 0; }
                throw new InvalidOperationException("Unknown updater mode: " + options.Mode);
            }
            catch (Exception ex)
            {
                try { Directory.CreateDirectory(LogRoot); File.WriteAllText(ErrorLog, DateTimeOffset.Now.ToString("o") + "\r\n" + ex + "\r\n", new UTF8Encoding(false)); } catch { }
                if (!Quiet) try { Console.Error.WriteLine("更新失败：" + ex.Message + "；错误日志：" + ErrorLog); } catch { }
                return 1;
            }
        }

        // The update chain is started by the skin host, which Rainmeter launches
        // with its working directory inside the skin folder, and every process
        // in the chain inherits it.  Windows refuses to rename a directory that
        // is the current directory of a live process, so the skin swap would
        // always fail with "access denied" no matter when Rainmeter exits.  Move
        // our own working directory out of the skin tree before doing any work.
        private static void RelocateWorkingDirectory(Options options)
        {
            // Resolve relative arguments while the original working directory
            // is still in effect.
            try { if (!String.IsNullOrWhiteSpace(options.PackageRoot)) options.PackageRoot = Path.GetFullPath(options.PackageRoot.Trim('"')); } catch { }
            try { if (!String.IsNullOrWhiteSpace(options.RainmeterRoot)) options.RainmeterRoot = Path.GetFullPath(options.RainmeterRoot.Trim('"')); } catch { }
            try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }
        }

        private static bool IsTruthy(string value)
        {
            value = (value ?? "").Trim();
            return value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        // Non-throwing "is that process still alive" probe: a pid that no longer
        // exists simply means the handoff is already over.
        private static bool ParentStillRunning(int pid, int milliseconds)
        {
            if (pid <= 0) return false;
            try { using (Process process = Process.GetProcessById(pid)) return !process.WaitForExit(milliseconds); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // Every released parent from 2.0.2 on waits for this process to exit (that
        // is how it knows the install finished) and removes the download directory
        // it unpacked us into afterwards.  Waiting for *it* deadlocks the pair until
        // the handoff times out, which is what made a 2.0.2 install reject every
        // 2.0.3 update.  Break the cycle instead: move our package out of the
        // parent's directory, continue the install in a detached copy of ourselves,
        // and exit so the parent can clean up and go away.  Parent versions that do
        // not wait (1.4.x, 1.5.x, 2.0.0, 2.0.1 and the PowerShell launcher) are gone
        // within the grace period, so nothing is detached for them.
        private static bool DetachFromWaitingParent(Options options)
        {
            if (options.WaitForProcessId <= 0) return false;
            if (String.IsNullOrWhiteSpace(options.PackageRoot) || !Directory.Exists(options.PackageRoot)) return false;
            if (!File.Exists(Path.Combine(options.PackageRoot, "Updater", "UpdaterHost.exe"))) return false;
            if (!ParentStillRunning(options.WaitForProcessId, HandoffGraceMilliseconds)) return false;
            try
            {
                string work = Path.Combine(Path.GetTempPath(), WorkerDirectoryPrefix + Guid.NewGuid().ToString("N"));
                string package = Path.Combine(work, "package");
                Directory.CreateDirectory(work);
                bool relocated = false;
                if (IsInsideTemp(options.PackageRoot))
                {
                    // Same volume: a rename is instant and lets the parent delete its
                    // own download directory without tripping over our running image.
                    try { Directory.Move(options.PackageRoot.TrimEnd('\\'), package); relocated = true; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (!relocated) CopyDirectory(options.PackageRoot, package);
                if (relocated) options.PackageRoot = package;
                string worker = Path.Combine(package, "Updater", "UpdaterHost.exe");
                if (!File.Exists(worker)) throw new InvalidDataException("更新包的 Updater 目录不完整。");
                Process.Start(new ProcessStartInfo(worker, BuildArguments("InstallPackage", package, options.RainmeterRoot, options.Activate)) { UseShellExecute = true, WorkingDirectory = Path.GetTempPath() });
                return true;
            }
            catch { return false; }
        }

        // Cleanup helper for the working copy DetachFromWaitingParent creates.  It
        // refuses to touch anything that is not exactly that working copy, so a
        // package root the user pointed us at can never be deleted by accident.
        private static void TryDeleteWorkerPackage(string packageRoot)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(packageRoot)) return;
                string root = Path.GetFullPath(packageRoot).TrimEnd('\\');
                if (!Path.GetFileName(root).Equals("package", StringComparison.OrdinalIgnoreCase)) return;
                string owner = Path.GetDirectoryName(root);
                if (String.IsNullOrEmpty(owner)) return;
                if (!Path.GetFileName(owner).StartsWith(WorkerDirectoryPrefix, StringComparison.OrdinalIgnoreCase)) return;
                if (!IsInsideTemp(owner)) return;
                TryDeleteDirectory(Path.Combine(root, "Skins"));
                TryDeleteDirectory(owner);
            }
            catch { }
        }

        private static bool IsInsideTemp(string path)
        {
            try { return FullDirectory(path).StartsWith(FullDirectory(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static int SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "rw-updater-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                if (CompareVersions("2.0.3", "2.0.2") <= 0 || NormalizeVersion("v2.0.3") != "2.0.3") return 2;
                string candidate = ZipArchiveReader.SafeDestination(root, "folder/file.txt");
                if (!candidate.StartsWith(FullDirectory(root), StringComparison.OrdinalIgnoreCase)) return 3;
                try { ZipArchiveReader.SafeDestination(root, "../escape"); return 4; } catch (InvalidDataException) { }
                if (ParentStillRunning(0, 10)) return 5;
                if (!ParentStillRunning(Process.GetCurrentProcess().Id, 10)) return 6;
                // The worker cleanup must never touch a package root we were pointed
                // at, but must remove the working copy we created for ourselves.
                string foreign = Path.Combine(root, "not-a-worker", "package");
                Directory.CreateDirectory(foreign);
                File.WriteAllText(Path.Combine(foreign, "keep.txt"), "keep", Encoding.UTF8);
                TryDeleteWorkerPackage(foreign);
                if (!File.Exists(Path.Combine(foreign, "keep.txt"))) return 7;
                string worker = Path.Combine(root, WorkerDirectoryPrefix + "probe", "package");
                Directory.CreateDirectory(worker);
                File.WriteAllText(Path.Combine(worker, "gone.txt"), "gone", Encoding.UTF8);
                TryDeleteWorkerPackage(worker);
                if (Directory.Exists(Path.GetDirectoryName(worker))) return 8;
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
                if (!Quiet) Console.WriteLine(comparison == 0 ? "已经是最新版本：" + latestTag : "当前版本比最新发布版更新。");
                return;
            }
            // WinUI confirms the update before launching this worker. A direct
            // invocation without consent only reports availability; it never installs.
            if (!options.AssumeYes)
            {
                if (!Quiet) Console.WriteLine("发现新版本 " + latestTag + "；请在设置界面确认更新。");
                return;
            }
            string asset = "rainmeter-desktop-widgets-" + latestVersion + ".zip";
            string temp = Path.Combine(Path.GetTempPath(), "RainmeterDesktopWidgetsUpdate-" + Guid.NewGuid().ToString("N"));
            string zip = Path.Combine(temp, asset);
            string extract = Path.Combine(temp, "package");
            Directory.CreateDirectory(temp);
            try
            {
                DownloadVerifiedReleaseAsset(options.Repository, latestTag, asset, zip);
                ZipArchiveReader.Extract(zip, extract, MaxArchiveBytes, MaxEntryBytes, MaxArchiveBytes, MaxEntries);
                string packageRoot = FindPackageRoot(extract);
                string updater = Path.Combine(packageRoot, "Updater", "UpdaterHost.exe");
                if (!File.Exists(updater)) throw new InvalidDataException("更新包中缺少 UpdaterHost.exe。");
                using (Process child = Process.Start(new ProcessStartInfo(updater, BuildArguments("InstallPackage", packageRoot, options.RainmeterRoot, options.Activate) + " -WaitForProcessId " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = temp }))
                {
                    if (child == null) throw new InvalidOperationException("无法启动新版本更新器。");
                    if (!child.WaitForExit(120000)) throw new TimeoutException("新版本更新器未在 2 分钟内完成。");
                    if (child.ExitCode != 0) throw new InvalidOperationException("新版本更新器返回错误：" + child.ExitCode);
                }
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
                    ZipArchiveReader.Extract(zip, extract, MaxArchiveBytes, MaxEntryBytes, MaxArchiveBytes, MaxEntries);
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
                    string paperCache = Path.Combine(target, "@Resources", "PaperCache");
                    if (Directory.Exists(paperCache)) CopyDirectory(paperCache, Path.Combine(stage, "@Resources", "PaperCache"));
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
                        throw new TimeoutException(skin + " 目录无法重命名，它仍是某个进程的当前工作目录或被其占用。请关闭 Rainmeter 及皮肤宿主进程后重试。");
                }

                foreach (string skin in new[] { "Todo", "Calendar" })
                {
                    string target = Path.Combine(roots.SkinsRoot, skin), stage = Path.Combine(transaction, "stage", skin), backup = Path.Combine(transaction, "backup", skin);
                    MoveDirectoryWithRetry(target, backup);
                    swapped.Add(skin);
                    MoveDirectoryWithRetry(stage, target);
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
                // A failed update must never leave the user without tiles: the
                // swap stopped Rainmeter, so bring the previous skins back up.
                if (rainmeterWasRunning && File.Exists(rainmeterExe)) { try { StartAndRefreshRainmeter(rainmeterExe, activate); } catch { } }
                throw;
            }
        }

        private static readonly string[] Preserve = { "tasks.json", "Generated.inc", "PluginValues.inc", "ui-scale.txt", "ui-window-scale.txt", "ui-theme.txt", "calendar-cache.json", "calendar-state.json", "caldav.secret", "translation.secret", "paper-sync.secret" };
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
            if (CompareVersions(version, "2.2.0") >= 0)
            {
                string desktop = Path.Combine(root, "Skins", "Todo", "@Resources", "DesktopUI");
                foreach (string file in new[] { "Rainmeter.Desktop.exe", "Rainmeter.Desktop.dll", "Rainmeter.Desktop.runtimeconfig.json", "coreclr.dll", "Microsoft.UI.Xaml.dll" })
                    if (!File.Exists(Path.Combine(desktop, file))) throw new InvalidDataException("更新包缺少桌面界面运行时：" + file);
            }
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
                // 皮肤目录一律问 Rainmeter 自己，绝不写死 Documents 或程序目录：
                // 标准版把 SkinPath 写在 %APPDATA%\Rainmeter\Rainmeter.ini（通常是
                // Documents\Rainmeter\Skins），便携版把 Rainmeter.ini 放在程序目录、
                // 皮肤放在其下的 Skins。顺序：程序目录里的 ini → 程序目录下的 Skins →
                // %APPDATA% 的 ini → 退回程序目录下的 Skins。
                string portableIni = Path.Combine(value, "Rainmeter.ini"), portableSkins = Path.Combine(value, "Skins");
                string skinPath = File.Exists(portableIni) ? ReadSkinPath(portableIni) : null;
                if (skinPath == null && Directory.Exists(portableSkins)) skinPath = portableSkins;
                if (skinPath == null)
                {
                    string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rainmeter", "Rainmeter.ini");
                    if (File.Exists(ini)) skinPath = ReadSkinPath(ini);
                }
                if (skinPath != null) skins = skinPath;
            }
            Directory.CreateDirectory(skins);
            return new Roots { RainmeterRoot = rainmeter, SkinsRoot = skins };
        }
        // 读 Rainmeter.ini 的 SkinPath。相对路径按 ini 所在目录解析，不能靠当前工作目录
        //（更新器已经把工作目录挪到了 %TEMP%）。
        private static string ReadSkinPath(string ini)
        {
            try
            {
                Match m = Regex.Match(File.ReadAllText(ini), @"(?m)^SkinPath=(.+)$");
                if (!m.Success) return null;
                string path = Environment.ExpandEnvironmentVariables(m.Groups[1].Value.Trim().Trim('"'));
                if (path.Length == 0) return null;
                if (!Path.IsPathRooted(path)) path = Path.Combine(Path.GetDirectoryName(ini), path);
                return Path.GetFullPath(path);
            }
            catch { return null; }
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
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(skinsRoot, "Todo", "@Resources", "TodoHost.exe"), Path.Combine(skinsRoot, "Calendar", "@Resources", "CalendarHost.exe"), Path.Combine(skinsRoot, "Todo", "@Resources", "PluginHost.exe"), Path.Combine(skinsRoot, "Todo", "@Resources", "DesktopUI", "Rainmeter.Desktop.exe") };
            foreach (string name in new[] { "TodoHost", "CalendarHost", "PluginHost", "Rainmeter.Desktop" })
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
            string local = PluginStateRoot(), pluginRoot = FullDirectory(Path.Combine(local, "Plugins")), jobs = Path.Combine(local, "PluginJobs");
            if (!Directory.Exists(jobs)) return;
            foreach (string file in Directory.GetFiles(jobs, "*.json")) try { IDictionary<string, object> job = ReadObject(file); int pid = Convert.ToInt32(job["pid"], CultureInfo.InvariantCulture); string entry = Path.GetFullPath(Text(job, "entry")); if (!entry.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase)) continue; using (Process p = Process.GetProcessById(pid)) { if (!Path.GetFullPath(p.MainModule.FileName).Equals(entry, StringComparison.OrdinalIgnoreCase)) continue; try { p.CloseMainWindow(); } catch { } if (!p.WaitForExit(3000)) p.Kill(); } job["state"] = "cancelled"; job["message"] = "Cancelled for host update"; job.Remove("pid"); job.Remove("entry"); job.Remove("process_started_at"); File.WriteAllText(file, Json.Serialize(job), new UTF8Encoding(false)); } catch { }
        }
        private static void RecoverInterruptedSkins(string skinsRoot)
        {
            foreach (string skin in new[] { "Todo", "Calendar" })
            {
                string target = Path.Combine(skinsRoot, skin); if (Directory.Exists(target)) continue;
                foreach (string tx in Directory.GetDirectories(skinsRoot, ".rainmeter-update-*" ).OrderByDescending(Directory.GetLastWriteTimeUtc)) { string backup = Path.Combine(tx, "backup", skin); if (Directory.Exists(backup)) { Directory.Move(backup, target); break; } }
                // The unlock probe momentarily renames the skin directory; put it
                // back if a previous run was killed in that window.
                if (Directory.Exists(target)) continue;
                foreach (string probe in Directory.GetDirectories(skinsRoot, skin + ".rw-unlock-probe-*")) { try { Directory.Move(probe, target); break; } catch { } }
            }
        }
        private sealed class FileSnapshot { public string Path; public bool Existed; public string Backup; }
        private static string PluginStateRoot()
        {
            string configured = Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT");
            return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterDesktopWidgets") : Path.GetFullPath(configured);
        }
        private static List<FileSnapshot> SnapshotPluginState(IEnumerable<string> ids, string tx)
        {
            string local = PluginStateRoot(), root = Path.Combine(tx, "plugin-state"); Directory.CreateDirectory(root);
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
            while (true)
            {
                // Creating a probe file inside the directory succeeds even while
                // the directory itself is locked, so the only reliable check is
                // the rename we are about to perform.  Rename it away and back:
                // a directory that is the current directory of a live process,
                // or that still holds an open file, refuses both operations.
                string probe = directory.TrimEnd('\\') + ".rw-unlock-probe-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.Move(directory, probe);
                    Directory.Move(probe, directory);
                    return true;
                }
                catch { try { if (Directory.Exists(probe) && !Directory.Exists(directory)) Directory.Move(probe, directory); } catch { } }
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(250);
            }
        }
        private static void StartAndRefreshRainmeter(string exe, bool activate)
        {
            if (!File.Exists(exe)) return;
            if (IsExactProcessRunning("Rainmeter", exe)) StopRainmeter(exe);
            TryStart(exe, ""); Thread.Sleep(1200); TryStart(exe, "!RefreshApp");
            if (activate) { Thread.Sleep(800); TryStart(exe, "!ActivateConfig \"Todo\" \"Todo.ini\""); TryStart(exe, "!ActivateConfig \"Calendar\" \"Calendar.ini\""); Thread.Sleep(800); TryStart(exe, "!SetWindowPosition \"100%\" \"0%\" \"100%\" \"0%\" \"Todo\""); }
        }
        private static bool IsExactProcessRunning(string name, string exe) { if (String.IsNullOrWhiteSpace(exe)) return false; foreach (Process p in Process.GetProcessesByName(name)) try { if (Path.GetFullPath(p.MainModule.FileName).Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return true; } catch { } finally { p.Dispose(); } return false; }
        private static void TryStart(string file, string args) { try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = Path.GetTempPath() }); } catch { } }

        private static void CopyDirectory(string source, string destination) { if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source); Directory.CreateDirectory(destination); foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, directory.Substring(source.Length).TrimStart('\\'))); foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) { string target = Path.Combine(destination, file.Substring(source.Length).TrimStart('\\')); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true); } }
        private static string Sha256(string path) { using (SHA256 hash = SHA256.Create()) using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static void CopyLimited(Stream input, Stream output, long maximum) { byte[] buffer = new byte[81920]; long total = 0; int read; while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { total += read; if (total > maximum) throw new InvalidDataException("数据超过允许大小。"); output.Write(buffer, 0, read); } }
        private static void TryDeleteDirectory(string path) { if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return; try { Directory.Delete(path, true); } catch { } }
        private static string FullDirectory(string path) { return Path.GetFullPath(path).TrimEnd('\\') + "\\"; }
        private static IDictionary<string, object> ReadObject(string path) { object value = Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)); var dictionary = value as IDictionary<string, object>; if (dictionary == null) throw new InvalidDataException("JSON 对象格式无效：" + path); return dictionary; }
        private static string Text(IDictionary<string, object> item, string key) { object value; return item != null && item.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static string NormalizeVersion(string value) { value = (value ?? "").Trim(); if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1); Match m = Regex.Match(value, @"\d+(?:\.\d+){0,3}"); return m.Success ? m.Value : value; }
        private static int CompareVersions(string left, string right) { int[] a = NormalizeVersion(left).Split('.').Select(ParseInt).ToArray(), b = NormalizeVersion(right).Split('.').Select(ParseInt).ToArray(); for (int i = 0; i < Math.Max(a.Length, b.Length); i++) { int av = i < a.Length ? a[i] : 0, bv = i < b.Length ? b[i] : 0; if (av != bv) return av.CompareTo(bv); } return 0; }
        private static int ParseInt(string value) { int result; return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0; }
        private static string BuildArguments(string mode, string package, string root, bool activate) { return "-Mode " + mode + " -PackageRoot \"" + package.Replace("\"", "\\\"") + "\" -RainmeterRoot \"" + (root ?? "").Replace("\"", "\\\"") + "\"" + (activate ? " -Activate" : "") + (Quiet ? " -Quiet" : ""); }

        private sealed class Options
        {
            public string Mode = "InstallPackage", Repository = "kevendai/Rainmeter_todo", CurrentVersion = "", PackageRoot = "", RainmeterRoot = ""; public bool Activate, AssumeYes, Quiet; public int WaitForProcessId, DelayMilliseconds;
            public static Options Parse(string[] args) { var o = new Options(); for (int i = 0; i < args.Length; i++) { string key = args[i].TrimStart('-', '/'); string value = i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[++i] : null; switch (key.ToLowerInvariant()) { case "mode": o.Mode = value ?? o.Mode; break; case "repository": o.Repository = value ?? o.Repository; break; case "currentversion": o.CurrentVersion = value ?? ""; break; case "packageroot": o.PackageRoot = value ?? ""; break; case "rainmeterroot": o.RainmeterRoot = value ?? ""; break; case "activate": o.Activate = true; if (value != null) i--; break; case "assumeyes": o.AssumeYes = true; if (value != null) i--; break; case "quiet": o.Quiet = true; if (value != null) i--; break; case "waitforprocessid": Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out o.WaitForProcessId); break; case "delaymilliseconds": Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out o.DelayMilliseconds); break; } } return o; }
        }

    }
}
