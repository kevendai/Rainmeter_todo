using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using RainmeterBackend;

internal static partial class TodoApp
{
    private static void StartExternalUpdater()
    {
        if (!File.Exists(UpdaterScript)) throw new Exception("未找到独立升级器：" + UpdaterScript);
        string arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArg(UpdaterScript)
            + " -Mode CheckAndInstall"
            + " -Repository " + QuoteArg(GitHubRepository)
            + " -CurrentVersion " + QuoteArg(AppVersion)
            + " -RainmeterRoot " + QuoteArg(CurrentRainmeterRoot())
            + " -Activate"
            + " -AssumeYes"
            + " -WaitForProcessId " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        Process.Start(new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = false, CreateNoWindow = false });
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
        string raw = GitHubGet("https://api.github.com/repos/" + GitHubRepository + "/tags");
        string tag = LatestTag(raw);
        if (tag == "") throw new Exception("GitHub 上没有可用版本标签");
        int compare = CompareVersions(NormalizeVersion(tag), AppVersion);
        return new UpdateCheckResult { Tag = tag, CompareResult = compare, IsNewer = compare > 0 };
    }

    private static string GitHubGet(string url)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Timeout = 10000;
        request.ReadWriteTimeout = 10000;
        request.UserAgent = "RainmeterDesktopWidgets/" + AppVersion;
        request.Accept = "application/vnd.github+json";
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
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

    private static string Http(string method, string url, string body, IDictionary<string,string> headers, int timeout)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url); request.Method = method; request.Timeout = timeout; request.ReadWriteTimeout = timeout; request.KeepAlive = false; request.ContentType = "application/json; charset=utf-8";
        if (headers != null) foreach (KeyValuePair<string,string> header in headers) { if (header.Key.Equals("Host",StringComparison.OrdinalIgnoreCase)) request.Host=header.Value; else request.Headers[header.Key]=header.Value; }
        if (body != null) { byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length; using(Stream s=request.GetRequestStream()) s.Write(bytes,0,bytes.Length); }
        using (HttpWebResponse response=(HttpWebResponse)request.GetResponse()) using(StreamReader reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) return reader.ReadToEnd();
    }
    private static Dictionary<string, object> ReadTranslationCredentials()
    {
        if (!File.Exists(TranslationSecret)) return new Dictionary<string, object>();
        try { return JsonUtil.ReadDpapiJson(TranslationSecret); }
        catch { return new Dictionary<string, object>(); }
    }

    private static void SaveTranslationCredentials(string secretId, string secretKey)
    {
        secretId = (secretId ?? "").Trim();
        secretKey = (secretKey ?? "").Trim();
        if (secretId == "" || secretKey == "") throw new Exception("SecretId 和 SecretKey 不能为空");
        JsonUtil.WriteDpapiJson(TranslationSecret, new Dictionary<string, object>{{"SecretId", secretId}, {"SecretKey", secretKey}});
    }

    private static string TestTranslationCredentials(string secretId, string secretKey)
    {
        Dictionary<string, object> credentials = new Dictionary<string, object>{{"SecretId", (secretId ?? "").Trim()}, {"SecretKey", (secretKey ?? "").Trim()}};
        string result = TranslateWithCredentials(credentials, "hello");
        return result == "" ? "翻译服务可用" : result;
    }

    private static string TranslateWithCredentials(Dictionary<string, object> credentials, string text)
    {
        string id = S(credentials, "SecretId"), key = S(credentials, "SecretKey");
        if (id == "" || key == "") throw new Exception("SecretId 和 SecretKey 不能为空");
        const string service = "tmt", host = "tmt.tencentcloudapi.com", action = "TextTranslate";
        long timestamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        string date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");
        string payload = JsonUtil.Serialize(new Dictionary<string, object>{{"SourceText", text}, {"Source", "en"}, {"Target", "zh"}, {"ProjectId", 0}});
        string canonicalHeaders = "content-type:application/json; charset=utf-8\nhost:" + host + "\nx-tc-action:texttranslate\n";
        string signed = "content-type;host;x-tc-action";
        string request = "POST\n/\n\n" + canonicalHeaders + "\n" + signed + "\n" + RuntimeUtil.Sha256Hex(payload);
        string scope = date + "/" + service + "/tc3_request";
        string toSign = "TC3-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + RuntimeUtil.Sha256Hex(request);
        byte[] secretDate = RuntimeUtil.Hmac(Encoding.UTF8.GetBytes("TC3" + key), date);
        byte[] secretService = RuntimeUtil.Hmac(secretDate, service);
        byte[] secretSigning = RuntimeUtil.Hmac(secretService, "tc3_request");
        string signature = BitConverter.ToString(RuntimeUtil.Hmac(secretSigning, toSign)).Replace("-", "").ToLowerInvariant();
        Dictionary<string, string> headers = new Dictionary<string, string>{{"Authorization", "TC3-HMAC-SHA256 Credential=" + id + "/" + scope + ", SignedHeaders=" + signed + ", Signature=" + signature}, {"Host", host}, {"X-TC-Action", action}, {"X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture)}, {"X-TC-Version", "2018-03-21"}, {"X-TC-Region", "ap-guangzhou"}};
        Dictionary<string, object> root = JsonUtil.Object(JsonUtil.Deserialize(Http("POST", "https://" + host, payload, headers, 15000)));
        Dictionary<string, object> response = JsonUtil.Object(JsonUtil.Get(root, "Response"));
        Dictionary<string, object> error = JsonUtil.Object(JsonUtil.Get(response, "Error"));
        string message = JsonUtil.String(error, "Message", "");
        if (message != "") throw new Exception(message);
        string translated = JsonUtil.String(response, "TargetText", "");
        if (translated == "") throw new Exception("腾讯云未返回翻译结果");
        return translated;
    }

    private static string Translate(string text)
    {
        if (!File.Exists(TranslationSecret)) return null;
        try { return TranslateWithCredentials(JsonUtil.ReadDpapiJson(TranslationSecret), text); }
        catch { return null; }
    }
}

