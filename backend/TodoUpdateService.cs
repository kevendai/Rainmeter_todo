using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RainmeterBackend;

internal static partial class TodoApp
{
    private static void StartExternalUpdater()
    {
        if (!File.Exists(UpdaterExecutable)) throw new Exception("未找到独立升级器：" + UpdaterExecutable);
        string arguments = "-Mode CheckAndInstall"
            + " -Repository " + QuoteArg(GitHubRepository)
            + " -CurrentVersion " + QuoteArg(AppVersion)
            + " -RainmeterRoot " + QuoteArg(CurrentRainmeterRoot())
            + " -Activate"
            + " -AssumeYes";
        // The updater swaps whole skin directories, and Windows cannot rename a
        // directory that is the current directory of a live process.  Rainmeter
        // starts this host with its working directory inside the skin folder, so
        // hand the updater a neutral one instead of letting the chain inherit it.
        Process.Start(new ProcessStartInfo(UpdaterExecutable, arguments) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Path.GetTempPath() });
    }

    private sealed class UpdateCheckResult
    {
        public string Tag;
        public bool IsNewer;
        public int CompareResult;
    }

    private static UpdateCheckResult CheckLatestUpdate()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        string tag = GitHubLatestReleaseTag();
        if (tag == "") throw new Exception("GitHub 上没有可用版本标签");
        int compare = CompareVersions(NormalizeVersion(tag), AppVersion);
        return new UpdateCheckResult { Tag = tag, CompareResult = compare, IsNewer = compare > 0 };
    }

    private static string GitHubLatestReleaseTag()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://github.com/" + GitHubRepository + "/releases/latest");
        request.Method = "HEAD";
        request.AllowAutoRedirect = true;
        request.Timeout = 10000;
        request.ReadWriteTimeout = 10000;
        request.UserAgent = "RainmeterDesktopWidgets/" + AppVersion;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        {
            string tag = response.ResponseUri.Segments.Last().Trim('/');
            return Uri.UnescapeDataString(tag);
        }
    }

    private static string LatestTag(string raw)
    {
        string best = "";
        foreach (object item in JsonUtil.Array(JsonUtil.Deserialize(raw)))
        {
            Dictionary<string, object> tag = JsonUtil.Object(item);
            string name = S(tag, "name");
            if (!Regex.IsMatch(NormalizeVersion(name), @"^\d")) continue;
            if (best == "" || CompareVersions(name, best) > 0) best = name;
        }
        return best;
    }

    private static string CurrentRainmeterRoot()
    {
        DirectoryInfo resources = new DirectoryInfo(ResourceDir);
        DirectoryInfo todo = resources.Parent;
        DirectoryInfo skins = todo == null ? null : todo.Parent;
        DirectoryInfo root = skins == null ? null : skins.Parent;
        if (root == null || skins == null || !skins.Name.Equals("Skins", StringComparison.OrdinalIgnoreCase)) throw new Exception("无法定位当前 Rainmeter 皮肤目录");
        if (!Directory.Exists(Path.Combine(root.FullName, "Skins"))) throw new Exception("无法定位当前 Rainmeter 皮肤目录");
        return root.FullName;
    }

    private static string QuoteArg(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }

    private static string NormalizeVersion(string value)
    {
        value = (value ?? "").Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
        Match match = Regex.Match(value, @"\d+(?:\.\d+){0,3}");
        return match.Success ? match.Value : value;
    }

    private static int CompareVersions(string left, string right)
    {
        int[] a = VersionParts(left), b = VersionParts(right);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int av = i < a.Length ? a[i] : 0, bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    private static int[] VersionParts(string value)
    {
        return NormalizeVersion(value).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => { int parsed; return Int32.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0; })
            .ToArray();
    }

    // 这里曾经住着 arxiv 自己的腾讯云机器翻译客户端（Http / ReadTranslationCredentials /
    // SaveTranslationCredentials / TestTranslationCredentials / TranslateWithCredentials / Translate）。
    // 2.1 起翻译由 translation_provider@1 提供（规格 §7.3），arxiv 侧只保留 `TranslateEnabled`
    // 这个开关 + 一次 `translate` 调用；本文件被 arxiv 插件直接链接编译，留着就等于插件里还躺着
    // 一份 Tencent API client，与 §9.3「arxiv 只允许持有论文业务配置 / PaperBundle / 绑定引用」
    // 直接冲突。凭据本身按 §9.2 仍以**复制不删除**的方式留在旧 secret / 备份里，迁移由
    // ProviderMigration 负责。
}
