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
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using RainmeterBackend;

internal static partial class TodoApp
{
    private const string PaperListPlaceholder = "<INSERT_PAPER_LIST_HERE>";
    private const string PaperWorkerMutexName = @"Global\RainmeterTodoPaperWorker";
    private static DateTime lastPaperDesktopUpdate = DateTime.MinValue;
    private const string DefaultTitlePrompt =
        "You are given a list of paper titles, each associated with a unique ID.\r\n\r\n"
        + "Input format:\r\nEach line contains one paper in the format:\r\nID: <paper title>\r\n\r\n"
        + "Now evaluate the following papers:\r\n" + PaperListPlaceholder + "\r\n\r\n"
        + "Assign an integer score from 0 to 10 to every paper.\r\n\r\n"
        + "Evaluation criteria:\r\n"
        + "1. Likely relevance and usefulness to an active research workflow.\r\n"
        + "2. Methodological novelty and potential scientific contribution.\r\n"
        + "3. Transferability of the main idea to related research problems.\r\n\r\n"
        + "Score standard (absolute):\r\n"
        + "- 9-10: exceptional potential value, novelty and broad research impact.\r\n"
        + "- 7-8: clearly valuable, with a useful or transferable method.\r\n"
        + "- 5-6: potentially useful but uncertain, incremental or narrowly relevant.\r\n"
        + "- 0-4: low expected relevance or research value.\r\n\r\n"
        + "Guidelines:\r\n"
        + "- Judge the underlying methodology rather than keywords.\r\n"
        + "- Prioritize reusable scientific ideas over superficial application similarity.\r\n"
        + "- Use the full scale meaningfully and avoid assigning the same score to most papers.\r\n\r\n"
        + "Output requirements (STRICT):\r\n"
        + "Return only one valid JSON object in this exact shape: {\"scores\":{\"1\":8,\"2\":6}}.\r\n"
        + "Include all and only the supplied IDs. Do not add explanations or Markdown.";
    private const string DefaultAbstractPrompt =
        "You are given a list of papers with titles and abstracts.\r\n\r\n"
        + "Evaluate the following papers:\r\n" + PaperListPlaceholder + "\r\n\r\n"
        + "Assign an integer score from 0 to 50 to every paper.\r\n\r\n"
        + "Scoring rules:\r\n"
        + "0-10:\r\n- Weakly motivated, low relevance, or little identifiable technical contribution.\r\n\r\n"
        + "10-20:\r\n- Narrow or incremental work with limited transferability or evidence.\r\n\r\n"
        + "20-30:\r\n- Solid research with some useful ideas, but moderate novelty or impact.\r\n\r\n"
        + "30-40:\r\n- Strong method, clear contribution and convincing evidence.\r\n\r\n"
        + "40-50:\r\n- Exceptional novelty, relevance, methodological value and likely research impact.\r\n\r\n"
        + "Adjustments:\r\n- Reward available code, strong experiments, robustness and broad transferability without exceeding 50.\r\n\r\n"
        + "Use the full range and avoid clustering scores.\r\n\r\n"
        + "Output requirements (STRICT):\r\n"
        + "Return only one valid JSON object in this exact shape: {\"scores\":{\"1\":42,\"2\":26}}.\r\n"
        + "Include all and only the supplied IDs. Do not add explanations or Markdown.";

    private sealed class PaperSettings
    {
        public bool Enabled;
        public string ApiBaseUrl = "https://api.deepseek.com/chat/completions";
        public string ApiKey = "";
        public string Model = "deepseek-v4-flash";
        public int MaxConcurrency = 8;
        public int TimeoutSeconds = 180;
        public bool FileServerEnabled;
        public string FileBaseUrl = "";
        public string FileAccount = "";
        public string FilePassword = "";
        public string Categories = "";
        public string ExcludeCategories = "";
        public string TitlePrompt = DefaultTitlePrompt;
        public string AbstractPrompt = DefaultAbstractPrompt;
        public int TitleThreshold = 7;
        public int TitleBatchSize = 10;
        public int AbstractBatchSize = 3;
        public int ImportCount = 5;
        public int CacheDays = 14;
    }

    private sealed class RemotePaperResult
    {
        public string Status;
        public string Error;
    }

    private sealed class PaperHttpException : Exception
    {
        public int StatusCode;
        public PaperHttpException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
    }

    private static string PaperJobPath { get { return Path.Combine(PaperCache, "paper-job.json"); } }
    private static string PaperRescorePath(string date) { return Path.Combine(PaperCache, date + "_papers.rescore"); }

    private static PaperSettings LoadPaperSettings()
    {
        PaperSettings settings = new PaperSettings();
        if (!File.Exists(PaperSyncSecret)) return settings;
        try
        {
            Dictionary<string, object> root = JsonUtil.ReadDpapiJson(PaperSyncSecret);
            Dictionary<string, object> api = JsonUtil.Object(JsonUtil.Get(root, "DeepSeek"));
            Dictionary<string, object> file = JsonUtil.Object(JsonUtil.Get(root, "FileServer"));
            Dictionary<string, object> scoring = JsonUtil.Object(JsonUtil.Get(root, "Scoring"));
            bool legacy = api.Count == 0 && file.Count == 0 && scoring.Count == 0;
            settings.Enabled = JsonUtil.Bool(root, "Enabled", legacy);
            if (legacy)
            {
                settings.FileBaseUrl = S(root, "BaseUrl");
                settings.FileAccount = S(root, "Account");
                settings.FilePassword = S(root, "Password");
                settings.FileServerEnabled = settings.FileBaseUrl != "" || settings.FileAccount != "";
                return settings;
            }
            settings.ApiBaseUrl = JsonUtil.String(api, "BaseUrl", settings.ApiBaseUrl);
            settings.ApiKey = JsonUtil.String(api, "ApiKey", "");
            settings.Model = JsonUtil.String(api, "Model", settings.Model);
            settings.MaxConcurrency = Clamp(JsonUtil.Int(api, "MaxConcurrency", settings.MaxConcurrency), 1, 32);
            settings.TimeoutSeconds = Clamp(JsonUtil.Int(api, "TimeoutSeconds", settings.TimeoutSeconds), 30, 600);
            settings.FileServerEnabled = JsonUtil.Bool(file, "Enabled", false);
            settings.FileBaseUrl = JsonUtil.String(file, "BaseUrl", "");
            settings.FileAccount = JsonUtil.String(file, "Account", "");
            settings.FilePassword = JsonUtil.String(file, "Password", "");
            settings.Categories = JsonUtil.String(scoring, "Categories", settings.Categories);
            settings.ExcludeCategories = JsonUtil.String(scoring, "ExcludeCategories", settings.ExcludeCategories);
            settings.TitlePrompt = EnsurePaperPlaceholder(JsonUtil.String(scoring, "TitlePrompt", settings.TitlePrompt), DefaultTitlePrompt);
            settings.AbstractPrompt = EnsurePaperPlaceholder(JsonUtil.String(scoring, "AbstractPrompt", settings.AbstractPrompt), DefaultAbstractPrompt);
            settings.TitleThreshold = Clamp(JsonUtil.Int(scoring, "TitleThreshold", settings.TitleThreshold), 0, 10);
            settings.TitleBatchSize = Clamp(JsonUtil.Int(scoring, "TitleBatchSize", settings.TitleBatchSize), 1, 50);
            settings.AbstractBatchSize = Clamp(JsonUtil.Int(scoring, "AbstractBatchSize", settings.AbstractBatchSize), 1, 20);
            settings.ImportCount = Clamp(JsonUtil.Int(scoring, "ImportCount", settings.ImportCount), 1, 20);
            settings.CacheDays = Clamp(JsonUtil.Int(scoring, "CacheDays", settings.CacheDays), 1, 90);
        }
        catch { }
        return settings;
    }

    private static void SavePaperSettings(PaperSettings settings)
    {
        ValidatePaperSettings(settings);
        Dictionary<string, object> root = new Dictionary<string, object> {
            {"Version", 2},
            {"Enabled", settings.Enabled},
            {"DeepSeek", new Dictionary<string, object> {
                {"BaseUrl", NormalizeHttpUrl(settings.ApiBaseUrl)},
                {"ApiKey", settings.ApiKey.Trim()},
                {"Model", settings.Model.Trim()},
                {"MaxConcurrency", Clamp(settings.MaxConcurrency, 1, 32)},
                {"TimeoutSeconds", Clamp(settings.TimeoutSeconds, 30, 600)}
            }},
            {"FileServer", new Dictionary<string, object> {
                {"Enabled", settings.FileServerEnabled},
                {"BaseUrl", NormalizeHttpUrl(settings.FileBaseUrl)},
                {"Account", settings.FileAccount.Trim()},
                {"Password", settings.FilePassword}
            }},
            {"Scoring", new Dictionary<string, object> {
                {"Categories", settings.Categories.Trim()},
                {"ExcludeCategories", settings.ExcludeCategories.Trim()},
                {"TitlePrompt", settings.TitlePrompt.Trim()},
                {"AbstractPrompt", settings.AbstractPrompt.Trim()},
                {"TitleThreshold", Clamp(settings.TitleThreshold, 0, 10)},
                {"TitleBatchSize", Clamp(settings.TitleBatchSize, 1, 50)},
                {"AbstractBatchSize", Clamp(settings.AbstractBatchSize, 1, 20)},
                {"ImportCount", Clamp(settings.ImportCount, 1, 20)},
                {"CacheDays", Clamp(settings.CacheDays, 1, 90)}
            }}
        };
        JsonUtil.WriteDpapiJson(PaperSyncSecret, root);
    }

    private static void ValidatePaperSettings(PaperSettings settings)
    {
        if (!settings.Enabled) return;
        bool anyApi = !String.IsNullOrWhiteSpace(settings.ApiKey);
        if (anyApi && (String.IsNullOrWhiteSpace(settings.ApiBaseUrl) || String.IsNullOrWhiteSpace(settings.Model)))
            throw new Exception("DeepSeek API 地址、API Key 和模型必须同时填写，或全部留空");
        if (settings.FileServerEnabled && (String.IsNullOrWhiteSpace(settings.FileBaseUrl) || String.IsNullOrWhiteSpace(settings.FileAccount)))
            throw new Exception("启用文件同步时，服务器地址和账号不能为空");
        if (String.IsNullOrWhiteSpace(settings.TitlePrompt) || String.IsNullOrWhiteSpace(settings.AbstractPrompt))
            throw new Exception("标题和摘要评分提示词不能为空");
        if (!settings.TitlePrompt.Contains(PaperListPlaceholder) || !settings.AbstractPrompt.Contains(PaperListPlaceholder))
            throw new Exception("标题和摘要评分提示词都必须包含论文插入占位符 " + PaperListPlaceholder);
    }

    private static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
    private static string NormalizeHttpUrl(string value)
    {
        value = (value ?? "").Trim();
        if (value == "") return "";
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value;
        return value.TrimEnd('/');
    }
    private static bool HasScoringApi(PaperSettings settings)
    {
        return settings.Enabled && settings.ApiBaseUrl.Trim() != "" && settings.ApiKey.Trim() != "" && settings.Model.Trim() != "";
    }

    private static string EnsurePaperPlaceholder(string prompt, string fallback)
    {
        prompt = String.IsNullOrWhiteSpace(prompt) ? fallback : prompt.Trim();
        return prompt.Contains(PaperListPlaceholder) ? prompt : prompt + "\r\n\r\nPapers to evaluate:\r\n" + PaperListPlaceholder;
    }
    private static bool HasFileServer(PaperSettings settings)
    {
        return settings.Enabled && settings.FileServerEnabled && settings.FileBaseUrl.Trim() != "" && settings.FileAccount.Trim() != "";
    }

    private static void SyncArxiv(Dictionary<string, object> state, bool manual, string paperDate)
    {
        PaperSettings settings = LoadPaperSettings();
        string today = String.IsNullOrWhiteSpace(paperDate) ? DateTime.Now.ToString("yyyy-MM-dd") : paperDate;
        if (!settings.Enabled)
        {
            if (manual) Meta(state)["status"] = "论文推荐已关闭";
            return;
        }
        DateTime now = DateTime.Now;
        if (!manual && (now.TimeOfDay < TimeSpan.FromHours(8) || now.TimeOfDay > TimeSpan.FromHours(20))) return;
        if (IsPaperJobRunning())
        {
            Meta(state)["status"] = ReadPaperJobMessage("论文后台任务正在运行");
            return;
        }
        if (!manual && JsonUtil.String(Meta(state), "last_arxiv_sync_date", "") == today) return;
        Directory.CreateDirectory(PaperCache);
        CleanupPaperCache(settings);
        string finalPath = Path.Combine(PaperCache, today + "_papers.json");

        List<Dictionary<string, object>> local;
        if (TryLoadPapers(finalPath, out local) && IsPaperFileComplete(local, settings))
        {
            string remoteSync = HasFileServer(settings) ? SyncLocalPaperToRemoteIfMissing(settings, finalPath) : "disabled";
            ImportPapers(state, local, today, settings);
            if (remoteSync == "uploaded") Meta(state)["status"] += "；已补传到文件服务器";
            else if (remoteSync == "exists") Meta(state)["status"] += "；远端已有同名文件，保留本地结果";
            else if (remoteSync == "failed") Meta(state)["status"] += "；远端状态检查失败";
            return;
        }

        RemotePaperResult remote = null;
        if (HasFileServer(settings))
        {
            remote = DownloadRemotePaper(settings, finalPath);
            if (remote.Status == "found")
            {
                List<Dictionary<string, object>> downloaded;
                if (TryLoadPapers(finalPath, out downloaded))
                {
                    ImportPapers(state, downloaded, today, settings);
                    return;
                }
                remote.Status = "error";
                remote.Error = "远端论文 JSON 无法读取";
            }
        }

        if (!manual)
        {
            Meta(state)["status"] = remote == null ? "本地暂无 " + today + " 已评分论文" :
                remote.Status == "notfound" ? "远端暂无 " + today + " 已评分论文" : remote.Error;
            return;
        }
        if (JsonUtil.String(Meta(state), "paper_api_skip_date", "") == today)
        {
            Meta(state)["status"] = "今日不再使用 DeepSeek；仍会在刷新时检查远端";
            return;
        }
        if (!HasScoringApi(settings))
        {
            Meta(state)["status"] = "未配置 DeepSeek 评分 API";
            return;
        }

        string prompt;
        if (remote != null && remote.Status == "error")
            prompt = "无法确认远端论文文件状态，继续可能产生重复评分费用。\r\n\r\n是否仍在本机抓取 arXiv 并调用 DeepSeek 评分？";
        else if (remote != null && remote.Status == "notfound")
            prompt = "远端暂无 " + today + " 的论文文件。\r\n\r\n是否在本机抓取 arXiv 并调用 DeepSeek 评分？";
        else
            prompt = "本地暂无 " + today + " 的完整论文缓存。\r\n\r\n是否抓取 arXiv 并调用 DeepSeek 评分？";
        string consent = ShowPaperScoringConsent(prompt);
        if (consent == "skip_today")
        {
            Meta(state)["paper_api_skip_date"] = today;
            Meta(state)["status"] = "今日不再使用 DeepSeek；仍会在刷新时检查远端";
            return;
        }
        if (consent != "use")
        {
            Meta(state)["status"] = "已取消本地论文评分";
            return;
        }
        WritePaperJob("queued", "已启动后台评分，正在准备 " + today + " 的论文", 0, 0);
        StartPaperWorker(today);
        Meta(state)["status"] = "已启动后台论文评分";
    }

    private static void StartPaperWorker(string date)
    {
        string arguments = "PaperWorker " + QuoteArg(date);
        Process.Start(new ProcessStartInfo(Application.ExecutablePath, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
    }

    private static bool StartPaperRescore(PaperSettings settings)
    {
        ValidatePaperSettings(settings);
        if (!settings.Enabled) throw new Exception("请先启用论文推荐");
        if (!HasScoringApi(settings)) throw new Exception("请先填写完整的 DeepSeek API 配置");
        if (IsPaperJobRunning()) throw new Exception(ReadPaperJobMessage("论文后台任务正在运行，请等待完成"));
        if (!ShowPaperRescoreConsent()) return false;

        SavePaperSettings(settings);
        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(PaperCache);
        foreach (string path in new[] {
            Path.Combine(PaperCache, date + "_papers.json"),
            Path.Combine(PaperCache, date + "_papers.partial.json"),
            PaperJobPath
        })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { throw new Exception("无法清除今天的论文缓存：" + ex.Message); }
        }
        File.WriteAllText(PaperRescorePath(date), RuntimeUtil.Iso(DateTimeOffset.Now), RuntimeUtil.Utf8NoBom);
        WritePaperJob("queued", "正在准备重新爬取 " + date + " 的论文", 0, 0);
        int reset = WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
            Meta(state)["last_arxiv_sync_date"] = "";
            Meta(state)["paper_api_skip_date"] = "";
            Meta(state)["status"] = "已按当前设置启动重新评分";
            Commit(state);
            refresh = true;
        });
        if (reset != 0)
        {
            try { File.Delete(PaperRescorePath(date)); } catch { }
            WritePaperJob("failed", "重新评分未启动：无法更新待办状态", 0, 0);
            throw new Exception("无法更新待办状态，请稍后重试");
        }
        try { StartPaperWorker(date); }
        catch
        {
            try { File.Delete(PaperRescorePath(date)); } catch { }
            WritePaperJob("failed", "重新评分未启动：无法启动后台任务", 0, 0);
            throw;
        }
        return true;
    }

    private static int RunPaperWorker(string date)
    {
        using (Mutex mutex = new Mutex(false, PaperWorkerMutexName))
        {
            bool held = false;
            try
            {
                held = mutex.WaitOne(0);
                if (!held) return 0;
                PaperSettings settings = LoadPaperSettings();
                if (!HasScoringApi(settings)) { WritePaperJob("failed", "DeepSeek 评分 API 未配置完整", 0, 0); return 2; }
                ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, settings.MaxConcurrency + 4);
                ServicePointManager.Expect100Continue = false;
                Directory.CreateDirectory(PaperCache);
                CleanupPaperCache(settings);
                string finalPath = Path.Combine(PaperCache, date + "_papers.json");
                string partialPath = Path.Combine(PaperCache, date + "_papers.partial.json");
                List<Dictionary<string, object>> papers;
                if (!TryLoadPapers(partialPath, out papers))
                {
                    WritePaperJob("fetching", "正在从 arXiv 获取 " + date + " 的论文", 0, 0);
                    papers = FetchArxivPapers(settings, date);
                    if (papers.Count == 0)
                    {
                        WritePaperJob("completed", date + " 没有符合条件的新论文", 0, 0);
                        UpdatePaperStatus(date + " 没有符合条件的新论文", false);
                        return 0;
                    }
                    JsonUtil.SaveAtomic(partialPath, papers);
                }
                ScorePapers(papers, settings, partialPath);
                if (!IsPaperFileComplete(papers, settings)) throw new Exception("论文评分未完整完成");
                JsonUtil.SaveAtomic(finalPath, papers);
                if (File.Exists(partialPath)) File.Delete(partialPath);
                string uploadStatus = !HasFileServer(settings) ? "disabled" : UploadScoredPaper(settings, finalPath, papers.Count);
                WritePaperJob("importing", "评分完成，正在导入本地待办", papers.Count, papers.Count);
                bool replaceToday = File.Exists(PaperRescorePath(date));
                int result = WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
                    List<Dictionary<string, object>> removed = replaceToday ? Tasks(state).Where(t => IsPaperTaskCreatedOnDate(t, date)).ToList() : new List<Dictionary<string, object>>();
                    try
                    {
                        if (replaceToday) Tasks(state).RemoveAll(t => IsPaperTaskCreatedOnDate(t, date));
                        ImportPapers(state, papers, date, settings);
                        if (uploadStatus == "failed") Meta(state)["status"] += "；文件服务器上传失败";
                        else if (uploadStatus == "skipped") Meta(state)["status"] += "；已保留远端原文件";
                        Commit(state);
                        refresh = true;
                    }
                    catch
                    {
                        if (replaceToday) Tasks(state).AddRange(removed);
                        throw;
                    }
                });
                if (result == 0 && replaceToday)
                {
                    try { File.Delete(PaperRescorePath(date)); } catch { }
                }
                string completion = result == 0 ? "论文评分和待办同步完成" : "评分完成，但待办同步失败";
                if (result == 0 && uploadStatus == "skipped") completion += "；远端文件未覆盖";
                else if (result == 0 && uploadStatus == "failed") completion += "；远端上传失败";
                WritePaperJob(result == 0 ? "completed" : "failed", completion, papers.Count, papers.Count);
                return result;
            }
            catch (Exception ex)
            {
                WritePaperJob("failed", "论文评分失败：" + SafeStatusMessage(ex.Message), 0, 0);
                UpdatePaperStatus("论文评分失败：" + SafeStatusMessage(ex.Message), true);
                return 1;
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
    }

    private static void UpdatePaperStatus(string message, bool refreshSkin)
    {
        WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
            Meta(state)["status"] = message;
            Commit(state);
            refresh = refreshSkin;
        });
    }

    private static bool IsPaperJobRunning()
    {
        if (IsPaperWorkerMutexLocked()) return true;
        if (!File.Exists(PaperJobPath)) return false;
        try
        {
            Dictionary<string, object> job = JsonUtil.LoadObject(PaperJobPath);
            string state = JsonUtil.String(job, "state", "");
            DateTimeOffset updated;
            if (!DateTimeOffset.TryParse(JsonUtil.String(job, "updated_at", ""), out updated)) return false;
            return state == "queued" && DateTimeOffset.Now - updated < TimeSpan.FromMinutes(1);
        }
        catch { return false; }
    }

    private static bool IsPaperWorkerMutexLocked()
    {
        using (Mutex mutex = new Mutex(false, PaperWorkerMutexName))
        {
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                return !acquired;
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }
    }

    private static string ReadPaperJobMessage(string fallback)
    {
        if (!File.Exists(PaperJobPath)) return fallback;
        try { return JsonUtil.String(JsonUtil.LoadObject(PaperJobPath), "message", fallback); }
        catch { return fallback; }
    }

    private static string ReadPaperJobDesktopMessage(string fallback)
    {
        if (!File.Exists(PaperJobPath)) return fallback;
        try
        {
            Dictionary<string, object> job = JsonUtil.LoadObject(PaperJobPath);
            return JsonUtil.String(job, "desktop_message", JsonUtil.String(job, "message", fallback));
        }
        catch { return fallback; }
    }

    private static string PaperDisplayStatus(Dictionary<string, object> state)
    {
        return IsPaperJobRunning() ? ReadPaperJobDesktopMessage("论文后台评分正在运行") : JsonUtil.String(Meta(state), "status", "就绪");
    }

    private static void WritePaperJob(string state, string message, int completed, int total)
    {
        WritePaperJob(state, message, message, completed, total, true);
    }

    private static void WritePaperJob(string state, string message, int completed, int total, bool refreshSkin)
    {
        WritePaperJob(state, message, message, completed, total, refreshSkin);
    }

    private static void WritePaperJob(string state, string message, string desktopMessage, int completed, int total, bool refreshSkin)
    {
        Directory.CreateDirectory(PaperCache);
        JsonUtil.SaveAtomic(PaperJobPath, new Dictionary<string, object> {
            {"state", state}, {"message", message}, {"desktop_message", desktopMessage}, {"completed", completed}, {"total", total},
            {"updated_at", RuntimeUtil.Iso(DateTimeOffset.Now)}
        });
        if (refreshSkin) RuntimeUtil.Refresh("Todo");
        else UpdatePaperDesktopStatus(desktopMessage, total > 0 && completed >= total);
    }

    private static void UpdatePaperDesktopStatus(string message, bool force)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now - lastPaperDesktopUpdate < TimeSpan.FromSeconds(2)) return;
        lastPaperDesktopUpdate = now;
        RuntimeUtil.SetMeterText("Todo", "Status", message);
    }

    private static void CleanupPaperCache(PaperSettings settings)
    {
        Directory.CreateDirectory(PaperCache);
        DateTime cutoff = DateTime.Now.AddDays(-settings.CacheDays);
        foreach (string file in Directory.GetFiles(PaperCache))
        {
            string name = Path.GetFileName(file);
            if ((name.EndsWith("_papers.json", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("_papers.partial.json", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("_papers.rescore", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("paper-job.json", StringComparison.OrdinalIgnoreCase)) &&
                File.GetLastWriteTime(file) < cutoff)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private static List<Dictionary<string, object>> FetchArxivPapers(PaperSettings settings, string date)
    {
        string shortDate = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MM-dd", CultureInfo.InvariantCulture);
        string feedPath = BuildArxivFeedPath(settings.Categories);
        DateTime lastProgressWrite = DateTime.MinValue;
        int lastPercent = -1;
        XmlDocument document = FetchArxivXml(feedPath, settings.TimeoutSeconds * 1000,
            delegate(long received, long total) {
                int percent = total > 0 ? (int)Math.Min(100L, received * 100L / total) : 0;
                DateTime now = DateTime.UtcNow;
                bool final = total > 0 && received >= total;
                if (!final && percent == lastPercent && now - lastProgressWrite < TimeSpan.FromMilliseconds(500)) return;
                if (!final && now - lastProgressWrite < TimeSpan.FromMilliseconds(500)) return;
                lastPercent = percent;
                lastProgressWrite = now;
                string shortMessage = "正在获取 " + shortDate + " 的论文";
                string desktopMessage = shortMessage;
                if (total > 0) desktopMessage += " · 已下载 " + FormatPaperMegabytes(received) + " / " + FormatPaperMegabytes(total);
                WritePaperJob("fetching", shortMessage, desktopMessage, ToPaperProgressInt(received), ToPaperProgressInt(total), false);
            });
        DateTime target = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date;
        HashSet<string> include = CsvSet(settings.Categories);
        HashSet<string> exclude = CsvSet(settings.ExcludeCategories);
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<Dictionary<string, object>> papers = new List<Dictionary<string, object>>();
        XmlNodeList items = document.SelectNodes("//*[local-name()='item' or local-name()='entry']");
        foreach (XmlNode item in items)
        {
            string link = NodeText(item, "link");
            if (link == "")
            {
                XmlNode linkNode = item.SelectSingleNode("./*[local-name()='link']");
                if (linkNode != null && linkNode.Attributes != null && linkNode.Attributes["href"] != null) link = linkNode.Attributes["href"].Value;
            }
            MatchResult idResult = ParseArxivId(link);
            if (!idResult.Valid || !seen.Add(idResult.Value)) continue;
            DateTimeOffset published;
            if (!TryGetPaperPublished(item, out published)) continue;
            if (published.ToOffset(TimeSpan.FromHours(8)).Date != target) continue;
            HashSet<string> categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in item.SelectNodes("./*[local-name()='category' or local-name()='subject']"))
            {
                string value = node.InnerText.Trim();
                if (node.Attributes != null && node.Attributes["term"] != null) value = node.Attributes["term"].Value.Trim();
                foreach (string part in value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)) categories.Add(part.Trim());
            }
            if (include.Count > 0 && !categories.Any(include.Contains)) continue;
            if (categories.Any(exclude.Contains)) continue;
            string title = CleanPaperText(NodeText(item, "title"));
            string summary = NodeText(item, "description");
            if (summary == "") summary = NodeText(item, "summary");
            summary = CleanPaperText(Regex.Replace(summary, @"^arXiv:\S+\s+Announce Type:\s*\w+\s*", "", RegexOptions.IgnoreCase));
            string authors = String.Join(", ", item.SelectNodes("./*[local-name()='author' or local-name()='creator']").Cast<XmlNode>().Select(n => CleanPaperText(NodeText(n, "name") == "" ? n.InnerText : NodeText(n, "name"))).Where(v => v != ""));
            papers.Add(new Dictionary<string, object> {
                {"id", papers.Count + 1}, {"arxiv_id", idResult.Value}, {"title", title},
                {"authors", authors == "" ? "Unknown" : authors}, {"abstract", summary},
                {"pdf_link", "https://arxiv.org/pdf/" + idResult.Value + ".pdf"},
                {"abs_link", "https://arxiv.org/abs/" + idResult.Value},
                {"category", categories.Cast<object>().ToList()},
                {"all_categories", categories.Cast<object>().ToList()},
                {"score", new Dictionary<string, object>{{"title", null}, {"abstract", null}}},
                {"status", "idle"}, {"current_task", null}
            });
        }
        return papers;
    }

    private static string BuildArxivFeedPath(string categories)
    {
        string feedCategories = String.Join("+", CsvSet(categories).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).Select(Uri.EscapeDataString));
        return "/rss/" + (feedCategories == "" ? "cs" : feedCategories);
    }

    private static XmlDocument FetchArxivXml(string feedPath, int timeout, Action<long, long> progress)
    {
        string[] hosts = { "https://export.arxiv.org", "https://rss.arxiv.org", "https://export.arxiv.org" };
        Exception last = null;
        for (int i = 0; i < hosts.Length; i++)
        {
            try { return PaperXml(hosts[i] + feedPath, timeout, progress); }
            catch (PaperHttpException ex)
            {
                last = ex;
                bool networkFailure = ex.StatusCode == 0;
                bool transientHttp = ex.StatusCode == 429 || ex.StatusCode == 500 || ex.StatusCode == 502 || ex.StatusCode == 503 || ex.StatusCode == 504;
                if (!networkFailure && !transientHttp) throw;
                if (i + 1 < hosts.Length) Thread.Sleep(800 + i * 700);
            }
        }
        throw new Exception("无法连接 arXiv，请检查网络或 DNS 后重新刷新" + (last == null ? "" : "：" + SafeStatusMessage(last.Message)));
    }

    private static bool TryGetPaperPublished(XmlNode item, out DateTimeOffset published)
    {
        foreach (string field in new[] { "date", "published", "updated", "pubDate" })
        {
            string value = NodeText(item, field);
            if (value != "" && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out published)) return true;
        }
        published = default(DateTimeOffset);
        return false;
    }

    private sealed class MatchResult { public bool Valid; public string Value; }
    private static MatchResult ParseArxivId(string link)
    {
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(link ?? "", @"/(?:abs|pdf)/([^/?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string value = match.Success ? match.Groups[1].Value : "";
        if (value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4);
        value = System.Text.RegularExpressions.Regex.Replace(value, @"v\d+$", "");
        return new MatchResult { Valid = value != "", Value = value };
    }
    private static string NodeText(XmlNode node, string localName)
    {
        XmlNode child = node.SelectSingleNode("./*[local-name()='" + localName + "']");
        return child == null ? "" : child.InnerText.Trim();
    }
    private static string CleanPaperText(string value)
    {
        if (String.IsNullOrWhiteSpace(value)) return "";
        return WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " ")).Replace("\r", " ").Replace("\n", " ").Trim();
    }
    private static HashSet<string> CsvSet(string value)
    {
        return new HashSet<string>((value ?? "").Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v != ""), StringComparer.OrdinalIgnoreCase);
    }

    private static void ScorePapers(List<Dictionary<string, object>> papers, PaperSettings settings, string partialPath)
    {
        ScoreStage(papers.Where(p => JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "title") == null).ToList(), "title", settings.TitleBatchSize, 0, 10, settings, partialPath, papers);
        List<Dictionary<string, object>> abstracts = papers.Where(p => {
            Dictionary<string, object> score = JsonUtil.Object(JsonUtil.Get(p, "score"));
            object title = JsonUtil.Get(score, "title");
            return title != null && Convert.ToInt32(title, CultureInfo.InvariantCulture) >= settings.TitleThreshold && JsonUtil.Get(score, "abstract") == null;
        }).ToList();
        ScoreStage(abstracts, "abstract", settings.AbstractBatchSize, 0, 50, settings, partialPath, papers);
    }

    private static void ScoreStage(List<Dictionary<string, object>> stagePapers, string stage, int batchSize, int minimum, int maximum, PaperSettings settings, string partialPath, List<Dictionary<string, object>> allPapers)
    {
        List<List<Dictionary<string, object>>> batches = new List<List<Dictionary<string, object>>>();
        for (int i = 0; i < stagePapers.Count; i += batchSize) batches.Add(stagePapers.Skip(i).Take(batchSize).ToList());
        if (batches.Count == 0) return;
        object saveLock = new object();
        int completed = 0;
        WritePaperJob(stage, stage == "title" ? "正在并发进行标题评分" : "正在并发进行摘要评分", 0, stagePapers.Count);
        Parallel.ForEach(batches, new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrency }, delegate(List<Dictionary<string, object>> batch) {
            Dictionary<int, int> scores = ScoreBatchWithRecovery(batch, stage, minimum, maximum, settings);
            lock (saveLock)
            {
                foreach (Dictionary<string, object> paper in batch)
                {
                    int id = Convert.ToInt32(JsonUtil.Get(paper, "id"), CultureInfo.InvariantCulture);
                    Dictionary<string, object> score = JsonUtil.Object(JsonUtil.Get(paper, "score"));
                    score[stage] = scores[id];
                    paper["score"] = score;
                    if (stage == "title" && scores[id] < settings.TitleThreshold) paper["status"] = "filtered";
                    else if (stage == "abstract") paper["status"] = "done";
                    else paper["status"] = "idle";
                }
                completed += batch.Count;
                JsonUtil.SaveAtomic(partialPath, allPapers);
                WritePaperJob(stage, (stage == "title" ? "标题评分 " : "摘要评分 ") + completed + "/" + stagePapers.Count, completed, stagePapers.Count);
            }
        });
    }

    private static Dictionary<int, int> ScoreBatchWithRecovery(List<Dictionary<string, object>> batch, string stage, int minimum, int maximum, PaperSettings settings)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { return CallDeepSeekBatch(batch, stage, minimum, maximum, settings); }
            catch (Exception ex) { last = ex; }
        }
        if (batch.Count > 1)
        {
            int split = batch.Count / 2;
            Dictionary<int, int> left = ScoreBatchWithRecovery(batch.Take(split).ToList(), stage, minimum, maximum, settings);
            Dictionary<int, int> right = ScoreBatchWithRecovery(batch.Skip(split).ToList(), stage, minimum, maximum, settings);
            foreach (KeyValuePair<int, int> pair in right) left[pair.Key] = pair.Value;
            return left;
        }
        throw new Exception("论文 " + Convert.ToString(JsonUtil.Get(batch[0], "id"), CultureInfo.InvariantCulture) + " 评分失败：" + (last == null ? "未知错误" : last.Message));
    }

    private static Dictionary<int, int> CallDeepSeekBatch(List<Dictionary<string, object>> batch, string stage, int minimum, int maximum, PaperSettings settings)
    {
        string prompt = stage == "title" ? settings.TitlePrompt : settings.AbstractPrompt;
        StringBuilder input = new StringBuilder();
        foreach (Dictionary<string, object> paper in batch)
        {
            input.Append(Convert.ToString(JsonUtil.Get(paper, "id"), CultureInfo.InvariantCulture)).Append(": ").Append(S(paper, "title")).AppendLine();
            if (stage == "abstract") input.Append("Abstract: ").Append(S(paper, "abstract")).AppendLine().AppendLine();
        }
        string resolvedPrompt = EnsurePaperPlaceholder(prompt, stage == "title" ? DefaultTitlePrompt : DefaultAbstractPrompt)
            .Replace(PaperListPlaceholder, input.ToString().TrimEnd());
        Dictionary<string, object> body = new Dictionary<string, object> {
            {"model", settings.Model},
            {"messages", new object[] {
                new Dictionary<string, object>{{"role", "system"}, {"content", "Follow the scoring instructions exactly and return valid JSON only."}},
                new Dictionary<string, object>{{"role", "user"}, {"content", resolvedPrompt}}
            }},
            {"thinking", new Dictionary<string, object>{{"type", "enabled"}}},
            {"response_format", new Dictionary<string, object>{{"type", "json_object"}}},
            {"stream", false}
        };
        string raw = DeepSeekRequest(settings, JsonUtil.Serialize(body));
        Dictionary<string, object> root = JsonUtil.Object(JsonUtil.Deserialize(raw.Trim()));
        List<object> choices = JsonUtil.Array(JsonUtil.Get(root, "choices"));
        if (choices.Count == 0) throw new Exception("DeepSeek 未返回 choices");
        Dictionary<string, object> message = JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(choices[0]), "message"));
        string content = JsonUtil.String(message, "content", "").Trim();
        return ParseDeepSeekScores(content, batch, minimum, maximum);
    }

    private static Dictionary<int, int> ParseDeepSeekScores(string content, List<Dictionary<string, object>> batch, int minimum, int maximum)
    {
        Dictionary<string, object> parsed = JsonUtil.Object(JsonUtil.Deserialize(content));
        Dictionary<string, object> values = JsonUtil.Object(JsonUtil.Get(parsed, "scores"));
        if (values.Count == 0) values = parsed;
        HashSet<int> expected = new HashSet<int>(batch.Select(p => Convert.ToInt32(JsonUtil.Get(p, "id"), CultureInfo.InvariantCulture)));
        Dictionary<int, int> result = new Dictionary<int, int>();
        foreach (KeyValuePair<string, object> pair in values)
        {
            int id, score;
            if (!Int32.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ||
                !Int32.TryParse(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out score))
                throw new Exception("DeepSeek 返回了非整数 ID 或分数");
            if (score < minimum || score > maximum) throw new Exception("DeepSeek 返回分数超出 " + minimum + "-" + maximum);
            result[id] = score;
        }
        if (!expected.SetEquals(result.Keys)) throw new Exception("DeepSeek 返回的论文 ID 与请求批次不一致");
        return result;
    }

    private static string DeepSeekRequest(PaperSettings settings, string body)
    {
        int[] delays = { 2000, 4000, 8000 };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return PaperHttp("POST", NormalizeHttpUrl(settings.ApiBaseUrl), body,
                    new Dictionary<string, string>{{"Authorization", "Bearer " + settings.ApiKey.Trim()}}, settings.TimeoutSeconds * 1000);
            }
            catch (PaperHttpException ex)
            {
                bool transient = ex.StatusCode == 429 || ex.StatusCode == 500 || ex.StatusCode == 503;
                if (!transient || attempt >= delays.Length) throw;
                Thread.Sleep(delays[attempt] + new Random(unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId)).Next(200, 900));
            }
        }
    }

    private sealed class PaperProgressStream : Stream
    {
        private readonly Stream inner;
        private readonly long total;
        private readonly Action<long, long> progress;
        public long BytesRead { get; private set; }

        public PaperProgressStream(Stream inner, long total, Action<long, long> progress)
        {
            this.inner = inner;
            this.total = total;
            this.progress = progress;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { return BytesRead; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                BytesRead += read;
                if (progress != null) progress(BytesRead, total);
            }
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    private static XmlDocument PaperXml(string url, int timeout, Action<long, long> progress)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        request.KeepAlive = true;
        request.UserAgent = "RainmeterDesktopWidgets/" + AppVersion;
        request.Accept = "application/rss+xml, application/atom+xml, application/xml, text/xml, */*";
        try
        {
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            {
                long total = response.ContentLength;
                if (progress != null) progress(0, total);
                using (PaperProgressStream stream = new PaperProgressStream(responseStream, total, progress))
                {
                    XmlDocument document = new XmlDocument();
                    document.XmlResolver = null;
                    document.Load(stream);
                    if (progress != null) progress(stream.BytesRead, total > 0 ? total : stream.BytesRead);
                    return document;
                }
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            int code = response == null ? 0 : (int)response.StatusCode;
            throw new PaperHttpException(code, "HTTP " + code + " " + SafeStatusMessage(ex.Message));
        }
    }

    private static string PaperHttp(string method, string url, string body, IDictionary<string, string> headers, int timeout)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = method;
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        request.KeepAlive = true;
        request.UserAgent = "RainmeterDesktopWidgets/" + AppVersion;
        request.Accept = "application/json, application/xml, text/xml, */*";
        if (headers != null) foreach (KeyValuePair<string, string> header in headers) request.Headers[header.Key] = header.Value;
        if (body != null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = bytes.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }
        try
        {
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            int code = response == null ? 0 : (int)response.StatusCode;
            string message = ex.Message;
            if (response != null)
            {
                try { using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) message = reader.ReadToEnd(); } catch { }
            }
            throw new PaperHttpException(code, "HTTP " + code + " " + SafeStatusMessage(message));
        }
    }

    private static int ToPaperProgressInt(long value)
    {
        if (value <= 0) return 0;
        return value >= Int32.MaxValue ? Int32.MaxValue : (int)value;
    }

    private static string FormatPaperMegabytes(long value)
    {
        return (Math.Max(0L, value) / (1024D * 1024D)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
    }

    private static void TestDeepSeekConnection(PaperSettings settings)
    {
        ValidatePaperSettings(settings);
        if (!HasScoringApi(settings)) throw new Exception("请先填写完整的 DeepSeek API 配置");
        Dictionary<string, object> paper = new Dictionary<string, object>{{"id", 1}, {"title", "Test paper"}, {"abstract", "A test abstract."}};
        CallDeepSeekBatch(new List<Dictionary<string, object>>{paper}, "title", 0, 10, settings);
    }

    private static string LoginFileServer(PaperSettings settings)
    {
        string body = JsonUtil.Serialize(new Dictionary<string, object>{{"username", settings.FileAccount.Trim()}, {"password", settings.FilePassword}});
        return PaperHttp("POST", NormalizeHttpUrl(settings.FileBaseUrl) + "/api/login", body, null, 10000).Trim().Trim('"');
    }

    private static void TestFileServerConnection(PaperSettings settings)
    {
        ValidatePaperSettings(settings);
        if (!HasFileServer(settings)) throw new Exception("请先启用并填写完整的文件服务器配置");
        LoginFileServer(settings);
    }

    private static RemotePaperResult DownloadRemotePaper(PaperSettings settings, string path)
    {
        try
        {
            string token = LoginFileServer(settings);
            string raw = PaperHttp("GET", NormalizeHttpUrl(settings.FileBaseUrl) + "/api/resources/paper/" + Path.GetFileName(path), null,
                new Dictionary<string, string>{{"X-Auth", token}}, 15000);
            Dictionary<string, object> result = JsonUtil.Object(JsonUtil.Deserialize(raw));
            object content = JsonUtil.Get(result, "content");
            string json = content is string ? (string)content : JsonUtil.Serialize(content ?? result);
            object parsed = JsonUtil.Deserialize(json);
            JsonUtil.SaveAtomic(path, parsed);
            return new RemotePaperResult { Status = "found", Error = "" };
        }
        catch (PaperHttpException ex)
        {
            return new RemotePaperResult { Status = ex.StatusCode == 404 ? "notfound" : "error", Error = ex.StatusCode == 404 ? "" : "论文文件服务器连接失败" };
        }
        catch { return new RemotePaperResult { Status = "error", Error = "论文文件服务器连接失败" }; }
    }

    private static string SyncLocalPaperToRemoteIfMissing(PaperSettings settings, string path)
    {
        if (!File.Exists(path)) return "failed";
        try
        {
            string token = LoginFileServer(settings);
            EnsureRemotePaperDirectory(settings, token);
            RemotePaperResult remote = CheckRemotePaper(settings, token, path);
            if (remote.Status == "found") return "exists";
            if (remote.Status != "notfound") return "failed";
            return UploadRemotePaperWithToken(settings, token, path, false) ? "uploaded" : "failed";
        }
        catch { return "failed"; }
    }

    private static string UploadScoredPaper(PaperSettings settings, string path, int paperCount)
    {
        if (!File.Exists(path)) return "failed";
        try
        {
            string token = LoginFileServer(settings);
            EnsureRemotePaperDirectory(settings, token);
            RemotePaperResult remote = CheckRemotePaper(settings, token, path);
            if (remote.Status == "error") return "failed";
            bool overwrite = false;
            if (remote.Status == "found")
            {
                WritePaperJob("upload_wait", "远端已有同名论文文件，等待确认是否覆盖", paperCount, paperCount);
                if (!ShowPaperOverwriteConsent(Path.GetFileName(path))) return "skipped";
                overwrite = true;
            }
            WritePaperJob("uploading", overwrite ? "正在覆盖远端论文文件" : "正在同步论文到文件服务器", paperCount, paperCount);
            return UploadRemotePaperWithToken(settings, token, path, overwrite) ? "uploaded" : "failed";
        }
        catch { return "failed"; }
    }

    private static RemotePaperResult CheckRemotePaper(PaperSettings settings, string token, string path)
    {
        try
        {
            PaperHttp("GET", NormalizeHttpUrl(settings.FileBaseUrl) + "/api/resources/paper/" + Path.GetFileName(path), null,
                new Dictionary<string, string>{{"X-Auth", token}}, 15000);
            return new RemotePaperResult { Status = "found", Error = "" };
        }
        catch (PaperHttpException ex)
        {
            return new RemotePaperResult {
                Status = ex.StatusCode == 404 ? "notfound" : "error",
                Error = ex.StatusCode == 404 ? "" : "无法确认远端论文文件状态"
            };
        }
        catch { return new RemotePaperResult { Status = "error", Error = "无法确认远端论文文件状态" }; }
    }

    private static bool UploadRemotePaperWithToken(PaperSettings settings, string token, string path, bool overwrite)
    {
        byte[] data = File.ReadAllBytes(path);
        string url = NormalizeHttpUrl(settings.FileBaseUrl) + "/api/resources/paper/" + Path.GetFileName(path) + "?override=" + (overwrite ? "true" : "false");
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "POST";
        request.Timeout = 30000;
        request.ReadWriteTimeout = 30000;
        request.ContentLength = data.Length;
        request.Headers["X-Auth"] = token;
        using (Stream stream = request.GetRequestStream()) stream.Write(data, 0, data.Length);
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            return (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
    }

    private static void EnsureRemotePaperDirectory(PaperSettings settings, string token)
    {
        string baseUrl = NormalizeHttpUrl(settings.FileBaseUrl);
        try
        {
            PaperHttp("GET", baseUrl + "/api/resources/paper", null, new Dictionary<string, string>{{"X-Auth", token}}, 10000);
        }
        catch (PaperHttpException ex)
        {
            if (ex.StatusCode != 404) throw;
            string body = JsonUtil.Serialize(new Dictionary<string, object>{{"name", "paper"}, {"type", "directory"}});
            PaperHttp("POST", baseUrl + "/api/resources/", body, new Dictionary<string, string>{{"X-Auth", token}}, 10000);
        }
    }

    private static bool TryLoadPapers(string path, out List<Dictionary<string, object>> papers)
    {
        papers = new List<Dictionary<string, object>>();
        if (!File.Exists(path)) return false;
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8).Trim();
            while (json.StartsWith("[][", StringComparison.Ordinal)) json = json.Substring(2);
            papers = JsonUtil.Array(JsonUtil.Deserialize(json)).Select(JsonUtil.Object).Where(p => p.Count > 0).ToList();
            return papers.Count > 0;
        }
        catch { return false; }
    }

    private static bool IsPaperFileComplete(List<Dictionary<string, object>> papers, PaperSettings settings)
    {
        if (papers == null || papers.Count == 0) return false;
        foreach (Dictionary<string, object> paper in papers)
        {
            Dictionary<string, object> score = JsonUtil.Object(JsonUtil.Get(paper, "score"));
            object title = JsonUtil.Get(score, "title");
            if (title == null) return false;
            int titleScore;
            if (!Int32.TryParse(Convert.ToString(title, CultureInfo.InvariantCulture), out titleScore)) return false;
            if (titleScore >= settings.TitleThreshold && JsonUtil.Get(score, "abstract") == null) return false;
        }
        return true;
    }

    private static bool IsPaperTaskCreatedOnDate(Dictionary<string, object> task, string date)
    {
        if (!S(task, "source").Equals("arxiv", StringComparison.OrdinalIgnoreCase)) return false;
        DateTimeOffset? created = RuntimeUtil.Date(task, "created_at");
        return created.HasValue && created.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == date;
    }

    private static void ImportPapers(Dictionary<string, object> state, List<Dictionary<string, object>> papers, string date, PaperSettings settings)
    {
        List<Dictionary<string, object>> ranked = papers.Where(p => JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "abstract") != null)
            .OrderByDescending(p => Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "abstract"), CultureInfo.InvariantCulture))
            .ThenByDescending(p => Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "title") ?? 0, CultureInfo.InvariantCulture))
            .Take(settings.ImportCount).ToList();
        if (ranked.Count == 0) { Meta(state)["status"] = date + " 没有通过摘要评分的论文"; return; }
        int added = 0, translated = 0;
        foreach (Dictionary<string, object> paper in ranked)
        {
            string arxiv = S(paper, "arxiv_id");
            string target = "https://arxiv.org/html/" + arxiv;
            if (Tasks(state).Any(t => S(t, "target").Equals(target, StringComparison.OrdinalIgnoreCase) || S(t, "note").Contains("arXiv ID：" + arxiv))) continue;
            string original = S(paper, "title");
            string translatedTitle = Translate(original);
            if (translatedTitle != null) { translated++; Thread.Sleep(220); }
            Dictionary<string, object> score = JsonUtil.Object(JsonUtil.Get(paper, "score"));
            EditorResult editor = new EditorResult {
                Title = "(" + Convert.ToString(JsonUtil.Get(score, "abstract"), CultureInfo.InvariantCulture) + ") " + (translatedTitle ?? original),
                Target = target,
                Note = "论文原标题：" + original + "\r\narXiv ID：" + arxiv,
                Labels = new List<string>{"论文"}, Available = "", Due = ""
            };
            Tasks(state).Add(NewTask(editor, "arxiv"));
            added++;
        }
        Meta(state)["last_arxiv_sync_date"] = date;
        Meta(state)["status"] = added > 0 ? "已添加 " + date + " 共 " + added + " 篇，翻译 " + translated + " 篇" : date + " 推荐论文均已存在";
    }

    private static string SafeStatusMessage(string value)
    {
        value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > 180 ? value.Substring(0, 180) : value;
    }

    private static int RunPaperSelfTests()
    {
        byte[] original = File.Exists(PaperSyncSecret) ? File.ReadAllBytes(PaperSyncSecret) : null;
        try
        {
            PaperSettings settings = new PaperSettings();
            List<Dictionary<string, object>> papers = new List<Dictionary<string, object>> {
                new Dictionary<string, object>{{"id",1},{"score",new Dictionary<string,object>{{"title",8},{"abstract",40}}}},
                new Dictionary<string, object>{{"id",2},{"score",new Dictionary<string,object>{{"title",5},{"abstract",null}}}}
            };
            if (!IsPaperFileComplete(papers, settings)) return 31;
            JsonUtil.Object(JsonUtil.Get(papers[0], "score"))["abstract"] = null;
            if (IsPaperFileComplete(papers, settings)) return 32;
            if (!ParseArxivId("https://arxiv.org/abs/2601.00001v2").Valid || ParseArxivId("https://arxiv.org/abs/2601.00001v2").Value != "2601.00001") return 33;
            if (Clamp(99, 1, 32) != 32 || Clamp(-1, 1, 32) != 1) return 34;
            Dictionary<int, int> parsed = ParseDeepSeekScores("{\"scores\":{\"1\":8,\"2\":5}}", papers, 0, 10);
            if (parsed.Count != 2 || parsed[1] != 8 || parsed[2] != 5) return 35;
            try { ParseDeepSeekScores("{\"scores\":{\"1\":8}}", papers, 0, 10); return 36; }
            catch { }
            JsonUtil.WriteDpapiJson(PaperSyncSecret, new Dictionary<string, object>{{"BaseUrl","http://example.invalid"},{"Account","legacy"},{"Password","secret"}});
            PaperSettings migrated = LoadPaperSettings();
            if (!migrated.Enabled || !migrated.FileServerEnabled || migrated.FileAccount != "legacy") return 37;
            PaperSettings disabled = new PaperSettings { Enabled = false, ApiBaseUrl = "", Model = "", TitlePrompt = "", AbstractPrompt = "" };
            SavePaperSettings(disabled);
            if (LoadPaperSettings().Enabled) return 38;
            if (!DefaultTitlePrompt.Contains(PaperListPlaceholder) || !DefaultAbstractPrompt.Contains(PaperListPlaceholder)) return 39;
            string inserted = EnsurePaperPlaceholder("Score these papers", DefaultTitlePrompt).Replace(PaperListPlaceholder, "1: Test");
            if (!inserted.Contains("1: Test") || inserted.Contains(PaperListPlaceholder)) return 40;
            XmlDocument rss = new XmlDocument();
            rss.LoadXml("<item><pubDate>Thu, 16 Jul 2026 00:00:00 -0400</pubDate></item>");
            DateTimeOffset published;
            if (!TryGetPaperPublished(rss.DocumentElement, out published) || published.ToOffset(TimeSpan.FromHours(8)).Date != new DateTime(2026, 7, 16)) return 41;
            string feed = BuildArxivFeedPath("cs.CV,cs.AI");
            if (!feed.Contains("cs.CV") || !feed.Contains("cs.AI") || feed.Equals("/rss/cs", StringComparison.OrdinalIgnoreCase)) return 42;
            if (settings.Categories != "" || settings.ExcludeCategories != "" || BuildArxivFeedPath(settings.Categories) != "/rss/cs") return 44;
            Dictionary<string, object> todayTask = new Dictionary<string, object>{{"source","arxiv"},{"created_at","2026-07-16T10:00:00+08:00"}};
            Dictionary<string, object> oldTask = new Dictionary<string, object>{{"source","arxiv"},{"created_at","2026-07-15T10:00:00+08:00"}};
            if (!IsPaperTaskCreatedOnDate(todayTask, "2026-07-16") || IsPaperTaskCreatedOnDate(oldTask, "2026-07-16")) return 43;
            return 0;
        }
        finally
        {
            if (original == null) { try { File.Delete(PaperSyncSecret); } catch { } }
            else File.WriteAllBytes(PaperSyncSecret, original);
        }
    }
}
