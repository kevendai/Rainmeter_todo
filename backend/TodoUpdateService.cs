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
            + " -Flavor " + QuoteArg(AppFlavor)
            + " -FlavorName " + QuoteArg(AppFlavorName)
            + " -RainmeterRoot " + QuoteArg(CurrentRainmeterRoot())
            + " -Activate"
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
    private static Dictionary<string, object> ReadPaperSyncSettings()
    {
        if (!File.Exists(PaperSyncSecret)) return new Dictionary<string, object>();
        try { return JsonUtil.ReadDpapiJson(PaperSyncSecret); }
        catch { return new Dictionary<string, object>(); }
    }

    private static void SavePaperSyncSettings(string baseUrl, string account, string password)
    {
        baseUrl = (baseUrl ?? "").Trim();
        account = (account ?? "").Trim();
        password = (password ?? "").Trim();
        if (baseUrl == "" || account == "") throw new Exception("论文网页同步地址和账号不能为空");
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl;
        JsonUtil.WriteDpapiJson(PaperSyncSecret, new Dictionary<string, object>{{"BaseUrl", baseUrl.TrimEnd('/')}, {"Account", account}, {"Password", password}});
    }

    private static string LoginPaperSync(string baseUrl, string account, string password)
    {
        string login = JsonUtil.Serialize(new Dictionary<string, object>{{"username", account}, {"password", password}});
        return Http("POST", baseUrl.TrimEnd('/') + "/api/login", login, null, 5000).Trim().Trim('"');
    }

    private static void TestPaperSyncConnection(string baseUrl, string account, string password)
    {
        baseUrl = (baseUrl ?? "").Trim();
        account = (account ?? "").Trim();
        password = (password ?? "").Trim();
        if (baseUrl == "" || account == "") throw new Exception("论文网页同步地址和账号不能为空");
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl;
        LoginPaperSync(baseUrl, account, password);
    }

    private static bool DownloadPaper(string path, out string error)
    {
        error = ""; Dictionary<string, object> paperSync = ReadPaperSyncSettings(); string baseUrl = S(paperSync, "BaseUrl"), user = S(paperSync, "Account"), password = S(paperSync, "Password"); if(baseUrl==""||user==""){error="尚未配置论文网页同步";return false;} if(!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))baseUrl="http://"+baseUrl;
        try { string token=LoginPaperSync(baseUrl,user,password); string raw=Http("GET",baseUrl.TrimEnd('/')+"/api/resources/paper/"+Path.GetFileName(path),null,new Dictionary<string,string>{{"X-Auth",token}},5000); Dictionary<string,object> result=JsonUtil.Object(JsonUtil.Deserialize(raw)); object content=JsonUtil.Get(result,"content"); File.WriteAllText(path,content is string?(string)content:JsonUtil.Serialize(content??result),RuntimeUtil.Utf8NoBom); return true; }
        catch(WebException ex){HttpWebResponse response=ex.Response as HttpWebResponse;error=response!=null&&response.StatusCode==HttpStatusCode.NotFound?"远端暂无该日期的已评分论文数据":"论文数据服务连接失败";return false;} catch{error="论文数据服务连接失败";return false;}
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
    private static void SyncArxiv(Dictionary<string, object> state, bool manual, string paperDate)
    {
        Directory.CreateDirectory(PaperCache); foreach(string f in Directory.GetFiles(PaperCache,"*_papers.json"))if(File.GetLastWriteTime(f)<DateTime.Now.AddDays(-14))File.Delete(f);
        DateTime now=DateTime.Now; string today=String.IsNullOrEmpty(paperDate)?now.ToString("yyyy-MM-dd"):paperDate;
        if(!manual&&(now.TimeOfDay<TimeSpan.FromHours(8)||now.TimeOfDay>TimeSpan.FromHours(20))){Meta(state)["status"]="arXiv 自动检查时段为 08:00-20:00";return;}
        if(Tasks(state).Any(t=>!B(t,"completed")&&S(t,"source")=="arxiv")){Meta(state)["status"]="待办中已有论文，未重复添加";return;}
        if(!manual&&JsonUtil.String(Meta(state),"last_arxiv_sync_date","")==today){Meta(state)["status"]="今日 arXiv 已检查";return;}
        string name=today+"_papers.json",path=Path.Combine(PaperCache,name),error="";if(!File.Exists(path))DownloadPaper(path,out error);if(!File.Exists(path)){Meta(state)["status"]=error!=""?error:"暂无 "+today+" 已评分论文数据";return;}
        object parsed;try{string json=File.ReadAllText(path,Encoding.UTF8);while(json.StartsWith("[][") )json=json.Substring(2);parsed=JsonUtil.Deserialize(json);}catch{Meta(state)["status"]="今日论文 JSON 无法读取";return;}
        List<Dictionary<string,object>> ranked=JsonUtil.Array(parsed).Select(JsonUtil.Object).Where(p=>JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p,"score")),"abstract")!=null).OrderByDescending(p=>Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p,"score")),"abstract"),CultureInfo.InvariantCulture)).ThenByDescending(p=>Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p,"score")),"title")??0,CultureInfo.InvariantCulture)).Take(5).ToList();if(ranked.Count==0){Meta(state)["status"]="今日还没有完成摘要评分的论文";return;}
        int added=0,translated=0;foreach(Dictionary<string,object> p in ranked){string arxiv=S(p,"arxiv_id"),target="https://arxiv.org/html/"+arxiv;if(Tasks(state).Any(t=>S(t,"target")==target))continue;string original=S(p,"title"),translatedTitle=Translate(original);if(translatedTitle!=null){translated++;Thread.Sleep(220);}Dictionary<string,object> score=JsonUtil.Object(JsonUtil.Get(p,"score"));EditorResult e=new EditorResult{Title="("+Convert.ToString(JsonUtil.Get(score,"abstract"),CultureInfo.InvariantCulture)+") "+(translatedTitle??original),Target=target,Note="论文原标题："+original+"\r\narXiv ID："+arxiv,Labels=new List<string>{"论文"},Available="",Due=""};Tasks(state).Add(NewTask(e,"arxiv"));added++;}
        Meta(state)["last_arxiv_sync_date"]=today;Meta(state)["status"]=added>0?"已添加 "+today+" 共 "+added+" 篇，翻译 "+translated+" 篇":today+" 前五篇均已存在";
    }

}

