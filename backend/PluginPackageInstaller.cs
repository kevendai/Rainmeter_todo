using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RainmeterBackend
{
    internal sealed class InstalledPluginInfo
    {
        public string Id = "", Version = "", Sha256 = "";
    }

    // 第三方插件包（.rwplugin）的校验与安装，全部在主程序内完成：
    // 不依赖 PowerShell（老实现调用 pwsh.exe，机器上没装 PowerShell 7 时
    // 直接抛 WinError 2「系统找不到指定的文件。」），也不依赖 System.IO.Compression
    // 的 ZipArchive（.NET 4.5 才有），解压走 ZipArchiveReader。
    //
    // 校验强度与原 Install-RwPlugin.ps1 完全一致：
    // 包大小 / SHA256 / 条目数与解压体积 / 路径越界 / 符号链接 / ZIP64 / 重复路径 /
    // manifest 字段（id、version、api_version、min_host_version、capabilities、entry）。
    internal static class PluginPackageInstaller
    {
        private const long MaxPackageBytes = 64L * 1024 * 1024;
        private const long MaxEntryBytes = 32L * 1024 * 1024;
        private const long MaxExpandedBytes = 256L * 1024 * 1024;
        private const int MaxEntries = 4096;
        private static readonly string[] AllowedCapabilities = { "todo_source", "todo_transform", "value_provider" };

        public static InstalledPluginInfo Install(string package, string pluginRoot, string hostVersion, string expectedSha256)
        {
            if (String.IsNullOrWhiteSpace(package)) throw new InvalidDataException("插件包路径为空。");
            string packagePath = Path.GetFullPath(package);
            if (!File.Exists(packagePath)) throw new InvalidDataException("插件包不存在。");
            if (new FileInfo(packagePath).Length > MaxPackageBytes) throw new InvalidDataException("插件包超过 64 MB 限制。");
            string actual = Sha256File(packagePath);
            if (!String.IsNullOrWhiteSpace(expectedSha256) && !actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件包 SHA256 不匹配。");
            if (!Regex.IsMatch(hostVersion ?? "", @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("宿主版本必须为 x.y.z。");

            string root = Path.GetFullPath(pluginRoot);
            string stage = Path.Combine(Path.GetTempPath(), "rwplugin-" + Guid.NewGuid().ToString("N"));
            try
            {
                ZipArchiveReader.Extract(packagePath, stage, MaxPackageBytes, MaxEntryBytes, MaxExpandedBytes, MaxEntries);
                string manifestPath = Path.Combine(stage, "plugin.json");
                if (!File.Exists(manifestPath)) throw new InvalidDataException("插件缺少 plugin.json。");
                Dictionary<string, object> manifest = JsonUtil.LoadObject(manifestPath);
                string id = JsonUtil.String(manifest, "id", ""), version = JsonUtil.String(manifest, "version", "");
                if (!Regex.IsMatch(id, @"^[a-z0-9]+(?:[.-][a-z0-9]+)+$")) throw new InvalidDataException("插件 ID 格式无效。");
                if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("插件版本必须为 x.y.z。");
                if (JsonUtil.Int(manifest, "api_version", 0) != 1) throw new InvalidDataException("不支持的 Plugin API 版本。");
                string minHost = JsonUtil.String(manifest, "min_host_version", "");
                if (!Regex.IsMatch(minHost, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("插件最低宿主版本无效。");
                if (new Version(minHost).CompareTo(new Version(hostVersion)) > 0) throw new InvalidDataException("插件要求主程序 " + minHost + " 或更高版本。");
                List<object> capabilities = JsonUtil.Array(JsonUtil.Get(manifest, "capabilities"));
                if (capabilities.Count == 0) throw new InvalidDataException("插件 capability 无效。");
                foreach (object capability in capabilities)
                {
                    string name = Convert.ToString(capability, CultureInfo.InvariantCulture) ?? "";
                    if (Array.IndexOf(AllowedCapabilities, name) < 0) throw new InvalidDataException("插件 capability 无效。");
                }
                string entry = JsonUtil.String(manifest, "entry", "");
                if (entry.Length == 0 || Path.IsPathRooted(entry) || Regex.IsMatch(entry, @"(^|[\\/])\.\.([\\/]|$)")) throw new InvalidDataException("插件入口路径无效。");
                string entryPath;
                try { entryPath = ZipArchiveReader.SafeDestination(stage, entry); }
                catch (InvalidDataException) { throw new InvalidDataException("插件入口不存在或超出插件目录。"); }
                if (!File.Exists(entryPath)) throw new InvalidDataException("插件入口不存在或超出插件目录。");

                string pluginDirectory = Path.Combine(root, id), versions = Path.Combine(pluginDirectory, "versions"), destination = Path.Combine(versions, version);
                Directory.CreateDirectory(versions);
                if (Directory.Exists(destination)) Directory.Delete(stage, true);
                else MoveDirectory(stage, destination);
                stage = null;

                string currentPath = Path.Combine(pluginDirectory, "current.json");
                bool wasEnabled = false;
                if (File.Exists(currentPath)) { try { wasEnabled = JsonUtil.Bool(JsonUtil.LoadObject(currentPath), "enabled", false); } catch { wasEnabled = false; } }
                Dictionary<string, object> current = new Dictionary<string, object>();
                current["version"] = version; current["enabled"] = wasEnabled;
                JsonUtil.SaveAtomic(currentPath, current);
                return new InstalledPluginInfo { Id = id, Version = version, Sha256 = actual };
            }
            finally
            {
                if (stage != null) { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { } }
            }
        }

        // 暂存目录在 %TEMP%，插件根可能位于另一个卷（用户把 TEMP 或 LOCALAPPDATA 重定向过）。
        // Directory.Move 跨卷必然失败，这种情况下退化为复制 + 删除。
        private static void MoveDirectory(string source, string destination)
        {
            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(source)), destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));
            if (String.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase)) { Directory.Move(source, destination); return; }
            CopyTree(source, destination);
            Directory.Delete(source, true);
        }

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, directory.Substring(source.Length).TrimStart('\\')));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, file.Substring(source.Length).TrimStart('\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static string Sha256File(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
