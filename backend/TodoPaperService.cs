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
    private static string paperSettingsLoadError = "";
    private const string PaperListPlaceholder = "<INSERT_PAPER_LIST_HERE>";
    private const string PaperWorkerMutexName = @"Global\RainmeterTodoPaperWorker";
    // 各类调用的建议超时（秒）。Provider 自己的超时在它那边配；这里给的是"这一次跨插件调用愿意等多久"
    // ——宿主会把它夹到上限内（§4.3：缺省 600、最大 1800）。
    private const int ArxivFetchTimeoutSeconds = 180;
    private const int AiCallTimeoutSeconds = 900;
    private const int SnapshotCallTimeoutSeconds = 120;
    private const int TranslationCallTimeoutSeconds = 180;
    // 同时打出去的评批次数的上限。2.0.4 里这是用户可配的 MaxConcurrency（1..32，默认 8），
    // 2.1 起并发属于 Provider 的内部实现（§7.2 明确 endpoint/key/model/timeout/retry/concurrency
    // 全部搬进 ai-deepseek），arxiv 只保留一个固定的批次并发度，取值与 2.0.4 的默认一致。
    private const int AiBatchParallelism = 8;
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

    // 三个官方 Provider 的**服务名**（规格 §7）。arxiv 只认这三个名字，不知道任何厂商：
    // 换掉 DeepSeek / 腾讯云 / File Browser 都不需要改这里一行。
    private const string AiService = "ai_provider@1";
    private const string TranslationService = "translation_provider@1";
    private const string SnapshotService = "paper_snapshot_provider@1";
    private const string AiBindingKey = "ai_provider";
    private const string TranslationBindingKey = "translation_provider";
    private const string SnapshotBindingKey = "paper_snapshot_provider";
    // attention 之后重新开的那一次 job 的 action id（规格 §5.2 冻结值）。
    private const string ResumeAction = "sync_with_ai";

    // 本次 job 的服务解析结果。由 ArxivPluginApp 读完请求后填好 —— 插件看不到绑定表（也不许看），
    // 只知道"有没有可用的 provider"。这正好让 §8 的三条禁令（不按顺序挑、不按 priority 挑、
    // 失败不自动切换）在架构上无从违反。
    private sealed class PaperServices
    {
        public bool Ai, Translation, Snapshot;
        public string AiName = "", TranslationName = "", SnapshotName = "";
    }

    private static PaperServices Services = new PaperServices();
    // 本次 job 拿到的"用户已同意付费 AI"。同一个词在 §5.6 有两层含义，别混：**同意标记**
    // （RW_PAID_CONSENT）绝不下发给插件，由宿主闸门在起 job 之前就拦掉没标记的付费请求；
    // 插件看到的是闸门放行后才传进来的 input.allow_paid_ai。
    private static bool PaidAiAllowed;
    // 宿主注入的"同一天内用户已拒绝过付费 AI"（§4.6-6 第 2 条）：后台同步不再用 attention 打扰。
    private static bool DeclinedToday;

    private sealed class PaperSettings
    {
        public bool Enabled = true;
        // 标题翻译。设置页**始终**显示这一项；没有 translation provider 时置灰，但存储值一个字节
        // 都不改（§11-#7/#8）—— 卸载 Provider 不该悄悄关掉用户的开关。
        public bool TranslateEnabled;
        public string Categories = "";
        public string ExcludeCategories = "";
        public string TitlePrompt = DefaultTitlePrompt;
        public string AbstractPrompt = DefaultAbstractPrompt;
        public int TitleThreshold = 7;
        public int TitleBatchSize = 10;
        public int AbstractBatchSize = 3;
        public int ImportCount = 5;
        public int CacheDays = 14;
        public bool RssEnabled;
    }

    // 一次快照请求的结果。`Found=false`（服务器明确回答"没有"）与 `Failed=true`（请求失败）
    // 必须分开：前者可以收工，后者要走 AI fallback（§7.1、§九）。
    private sealed class SnapshotResult
    {
        public bool Found, Failed;
        public string Error = "";
        public List<Dictionary<string, object>> Papers;
    }

    // AI 侧"重试和拆批都救不回来"的错误（Key 无效 / 余额不足 / 被拒绝 / 没有 provider）。
    // 一旦出现就立刻中止整轮评分，而不是对每一批都白跑一遍。2.0.4 的判据是 HTTP 码 401/402/403，
    // Provider 化之后厂商错误语义已经搬进 ai-deepseek，这里只认信封上的 `fatal` 与 `no_provider`
    // 两个标志（§4.3、§7.2）——arxiv 不再自己解释 HTTP 状态码。
    private sealed class PaperAiFatalException : Exception
    {
        public PaperAiFatalException(string message) : base(message) { }
    }

    // arXiv RSS 抓取失败。**只服务于 arXiv 自己那两次 HTTP**（feed 与 pdf），与 AI / 翻译 /
    // 快照三家 Provider 无关 —— 那些错误一律以 ServiceCallResult 的形式回来，不抛异常。
    private sealed class PaperHttpException : Exception
    {
        public int StatusCode;
        public PaperHttpException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
    }

    // Provider 回传的 error 已经是面向用户的中文（§7.2 的厂商错误映射在 ai-deepseek 里做），
    // 这里只做长度和换行的消毒，不再二次翻译 HTTP 语义。
    private static string DescribePaperFailure(Exception ex)
    {
        return SafeStatusMessage(ex == null ? "" : ex.Message);
    }

    // Parallel.ForEach 会把并行体里的异常包成 AggregateException，直接取 Message 只会得到
    // 「发生一个或多个错误。」这类无信息量的文案，真实原因（例如余额不足）就丢了。
    private static Exception UnwrapAggregate(Exception ex)
    {
        AggregateException aggregate = ex as AggregateException;
        if (aggregate == null) return ex;
        List<Exception> flat = aggregate.Flatten().InnerExceptions.ToList();
        Exception fatal = flat.FirstOrDefault(e => e is PaperAiFatalException);
        if (fatal != null) return fatal;
        Exception http = flat.FirstOrDefault(e => e is PaperHttpException);
        if (http != null) return http;
        return flat.Count > 0 ? flat[0] : ex;
    }

    private static string PaperJobPath { get { return Path.Combine(PaperCache, "paper-job.json"); } }
    private static string PaperRescorePath(string date) { return Path.Combine(PaperCache, date + "_papers.rescore"); }

    private static PaperSettings LoadPaperSettings()
    {
        PaperSettings settings = new PaperSettings();
        paperSettingsLoadError = "";
        if (!File.Exists(PaperSyncSecret)) return settings;
        try
        {
            Dictionary<string, object> root = JsonUtil.ReadDpapiJson(PaperSyncSecret);
            Dictionary<string, object> scoring = JsonUtil.Object(JsonUtil.Get(root, "Scoring"));
            Dictionary<string, object> rss = JsonUtil.Object(JsonUtil.Get(root, "Rss"));
            // 2.0.0 起这份文件**只**放论文业务配置：AI / 翻译 / 文件服务器三家的凭据都不再属于 arxiv
            // （规格 §九：arxiv 只允许持有论文业务配置 + PaperBundle + Provider 绑定引用）。旧文件里
            // 的 DeepSeek / FileServer 段读到时直接忽略 —— §9.2 说的是"复制，不删除"，不是"继续用"。
            settings.Enabled = JsonUtil.Bool(root, "Enabled", true);
            settings.TranslateEnabled = JsonUtil.Bool(root, "TranslateEnabled", false);
            settings.Categories = JsonUtil.String(scoring, "Categories", settings.Categories);
            settings.ExcludeCategories = JsonUtil.String(scoring, "ExcludeCategories", settings.ExcludeCategories);
            settings.TitlePrompt = EnsurePaperPlaceholder(JsonUtil.String(scoring, "TitlePrompt", settings.TitlePrompt), DefaultTitlePrompt);
            settings.AbstractPrompt = EnsurePaperPlaceholder(JsonUtil.String(scoring, "AbstractPrompt", settings.AbstractPrompt), DefaultAbstractPrompt);
            settings.TitleThreshold = Clamp(JsonUtil.Int(scoring, "TitleThreshold", settings.TitleThreshold), 0, 10);
            settings.TitleBatchSize = Clamp(JsonUtil.Int(scoring, "TitleBatchSize", settings.TitleBatchSize), 1, 50);
            settings.AbstractBatchSize = Clamp(JsonUtil.Int(scoring, "AbstractBatchSize", settings.AbstractBatchSize), 1, 20);
            settings.ImportCount = Clamp(JsonUtil.Int(scoring, "ImportCount", settings.ImportCount), 1, 20);
            settings.CacheDays = Clamp(JsonUtil.Int(scoring, "CacheDays", settings.CacheDays), 1, 90);
            settings.RssEnabled = JsonUtil.Bool(rss, "Enabled", false);
        }
        catch (Exception ex) { paperSettingsLoadError = "论文设置无法解密或已损坏：" + SafeStatusMessage(ex.Message); }
        return settings;
    }

    private static void SavePaperSettings(PaperSettings settings)
    {
        ValidatePaperSettings(settings);
        Dictionary<string, object> root = new Dictionary<string, object> {
            {"Version", 3},
            {"Enabled", settings.Enabled},
            {"TranslateEnabled", settings.TranslateEnabled},
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
            }},
            {"Rss", new Dictionary<string, object> {
                {"Enabled", settings.RssEnabled},
                {"Address", PaperRssAddress},
                {"Port", PaperRssPort}
            }}
        };
        JsonUtil.WriteDpapiJson(PaperSyncSecret, root);
    }

    private static void ValidatePaperSettings(PaperSettings settings)
    {
        if (!settings.Enabled) return;
        if (String.IsNullOrWhiteSpace(settings.TitlePrompt) || String.IsNullOrWhiteSpace(settings.AbstractPrompt))
            throw new Exception("标题和摘要评分提示词不能为空");
        if (!settings.TitlePrompt.Contains(PaperListPlaceholder) || !settings.AbstractPrompt.Contains(PaperListPlaceholder))
            throw new Exception("标题和摘要评分提示词都必须包含论文插入占位符 " + PaperListPlaceholder);
    }

    private static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
    // "能不能评分" 现在只取决于**有没有可用的 ai_provider**（规格 §7.2：endpoint / key / model /
    // timeout / retry / max concurrency 全部搬进了 Provider，arxiv 侧一个字节都不留）。
    private static bool AiAvailable(PaperSettings settings)
    {
        return settings.Enabled && Services.Ai;
    }

    private static string EnsurePaperPlaceholder(string prompt, string fallback)
    {
        prompt = String.IsNullOrWhiteSpace(prompt) ? fallback : prompt.Trim();
        return prompt.Contains(PaperListPlaceholder) ? prompt : prompt + "\r\n\r\nPapers to evaluate:\r\n" + PaperListPlaceholder;
    }

    // 有没有"远端快照"这条捷径。与 arxiv 自己的开关无关：快照插件自己决定要不要下载
    // （关掉时它回 found:false，走的是同一分支）。
    private static bool SnapshotAvailable(PaperSettings settings)
    {
        return settings.Enabled && Services.Snapshot;
    }

    private static string ProviderLabel(string name, string fallback)
    {
        return String.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    // ── AI Provider（ai_provider@1）────────────────────────────────────────────────
    // 只负责"把这段 prompt 打成一次结构化补全"。prompt 的业务含义、两阶段评分、阈值与 Top-N
    // 全部留在 arxiv —— 这是 §7.2 说的"换 provider 时算法不变"。
    private static Dictionary<int, int> CallAiBatch(List<Dictionary<string, object>> batch, string stage, int minimum, int maximum, PaperSettings settings)
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
        Dictionary<string, object> call = new Dictionary<string, object> {
            {"messages", new List<object> {
                new Dictionary<string, object>{{"role", "system"}, {"content", "Follow the scoring instructions exactly and return valid JSON only."}},
                new Dictionary<string, object>{{"role", "user"}, {"content", resolvedPrompt}}
            }},
            {"response_schema", new Dictionary<string, object> {
                {"type", "object"},
                {"properties", new Dictionary<string, object>{{"scores", new Dictionary<string, object>{{"type", "object"}}}}},
                {"required", new List<object>{"scores"}}
            }},
            {"purpose", stage == "title" ? "arxiv_title_scoring" : "arxiv_abstract_scoring"}
        };
        ServiceCallResult response = ServiceClient.Call(AiService, "structured_complete", call, AiCallTimeoutSeconds);
        if (!response.Ok)
        {
            string message = "AI 评分失败（" + response.ErrorKind + "）：" + ServiceClient.Trim(response.Error);
            // fatal / 没有 provider 都属于"这一轮不必再试了"：前者是 Key 无效、余额不足这类
            // 重试也没用的错误，后者是根本没装 —— 都不该对每一批白跑一遍（§4.3）。
            if (response.Fatal || response.NoProvider) throw new PaperAiFatalException(message);
            throw new Exception(message);
        }
        Dictionary<string, object> json = JsonUtil.Object(JsonUtil.Get(response.Output, "json"));
        if (json.Count == 0) throw new Exception("AI Provider 没有返回结构化结果");
        return ParseAiScores(JsonUtil.Serialize(json), batch, minimum, maximum);
    }

    // ── Paper Snapshot Provider（paper_snapshot_provider@1）────────────────────────
    private static string ProfileHashOf(PaperSettings settings)
    {
        PaperProfile profile = new PaperProfile {
            Categories = CsvSet(settings.Categories).ToList(),
            ExcludeCategories = CsvSet(settings.ExcludeCategories).ToList(),
            TitlePrompt = settings.TitlePrompt,
            AbstractPrompt = settings.AbstractPrompt,
            TitleThreshold = settings.TitleThreshold,
            TitleBatchSize = settings.TitleBatchSize,
            AbstractBatchSize = settings.AbstractBatchSize,
            ImportCount = settings.ImportCount
        };
        return PaperProfileHash.Compute(profile);
    }

    // 远端快照 → 内部论文表。结构合法性由 PaperBundleValidator 负责（远端 JSON 一律不可信），
    // 这里只做形状转换。
    private static SnapshotResult FetchSnapshot(PaperSettings settings, string date)
    {
        string hash = ProfileHashOf(settings);
        Dictionary<string, object> call = new Dictionary<string, object> {
            {"source", PaperProfileHash.SourceId},
            {"date", date},
            {"profile_hash", hash}
        };
        ServiceCallResult response = ServiceClient.Call(SnapshotService, "get_snapshot", call, SnapshotCallTimeoutSeconds);
        if (!response.Ok)
            return new SnapshotResult { Failed = true, Error = "远端论文同步失败：" + ServiceClient.Trim(response.Error) };
        if (!JsonUtil.Bool(response.Output, "found", false)) return new SnapshotResult();
        // 快照以**原始 JSON 文本**回传：校验器要按字节判体积、按原样判结构，插件不该替它做形状假设。
        string json = JsonUtil.Serialize(JsonUtil.Object(JsonUtil.Get(response.Output, "snapshot")));
        PaperBundleCheckResult check = PaperBundleValidator.Check(json, hash);
        if (check.Verdict == PaperBundleVerdict.Compatible)
            return new SnapshotResult { Found = true, Papers = PapersFromBundle(check.Bundle, settings) };
        // incompatible（结构合法但不是给我们用的）与 error（取不到/解析失败）都要**走 AI 询问**，
        // 但绝不能导入 —— 把理由原样带出去给用户看（§6.2）。
        return new SnapshotResult { Failed = true, Error = "远端快照不可用：" + check.Reason };
    }

    private static List<Dictionary<string, object>> PapersFromBundle(PaperBundle bundle, PaperSettings settings)
    {
        List<Dictionary<string, object>> papers = new List<Dictionary<string, object>>();
        foreach (PaperBundlePaper paper in bundle.Papers)
        {
            papers.Add(new Dictionary<string, object> {
                {"id", papers.Count + 1},
                {"arxiv_id", paper.Id},
                {"title", paper.Title},
                {"translated_title", paper.TranslatedTitle},
                {"abstract", paper.AbstractText},
                {"authors", paper.Authors.Count == 0 ? "Unknown" : String.Join(", ", paper.Authors.ToArray())},
                {"pdf_link", paper.PdfUrl},
                {"abs_link", paper.Url},
                {"published_at", paper.PublishedAt},
                {"category", paper.Categories.Cast<object>().ToList()},
                {"all_categories", paper.Categories.Cast<object>().ToList()},
                {"score", new Dictionary<string, object>{{"title", paper.TitleScore}, {"abstract", paper.AbstractScore}}},
                {"status", "done"}
            });
        }
        return papers;
    }

    // 内部论文表 → PaperBundle（§6.1）。只放"已经拿到分数的"论文：没通过标题阈值的候选不属于
    // 今日推荐结果，写进快照会让别的机器导入一份被截断的清单。
    private static Dictionary<string, object> BundleFromPapers(List<Dictionary<string, object>> papers, string date, PaperSettings settings)
    {
        List<object> items = new List<object>();
        foreach (Dictionary<string, object> paper in papers)
        {
            Dictionary<string, object> score = JsonUtil.Object(JsonUtil.Get(paper, "score"));
            if (JsonUtil.Get(score, "abstract") == null) continue;
            string id = S(paper, "arxiv_id");
            if (id == "") continue;
            List<string> categories = JsonUtil.Array(JsonUtil.Get(paper, "all_categories")).Select(Convert.ToString).Where(v => !String.IsNullOrWhiteSpace(v)).ToList();
            if (categories.Count == 0) categories = JsonUtil.Array(JsonUtil.Get(paper, "category")).Select(Convert.ToString).Where(v => !String.IsNullOrWhiteSpace(v)).ToList();
            List<string> authors = S(paper, "authors").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v != "").ToList();
            items.Add(new Dictionary<string, object> {
                {"id", id},
                {"title", S(paper, "title")},
                // 翻译是"enrichment"（§十五）：没翻就留空，绝不因此让快照或导入失败。
                {"translated_title", S(paper, "translated_title")},
                {"abstract", S(paper, "abstract")},
                {"authors", authors.Cast<object>().ToList()},
                {"categories", categories.Cast<object>().ToList()},
                {"published_at", S(paper, "published_at")},
                {"url", S(paper, "abs_link")},
                {"pdf_url", S(paper, "pdf_link")},
                {"scores", new Dictionary<string, object>{
                    {"title", Convert.ToInt32(JsonUtil.Get(score, "title") ?? 0, CultureInfo.InvariantCulture)},
                    {"abstract", Convert.ToInt32(JsonUtil.Get(score, "abstract"), CultureInfo.InvariantCulture)}}}
            });
        }
        return new Dictionary<string, object> {
            {"schema_version", PaperProfileHash.BundleSchemaVersion},
            {"profile_hash_version", PaperProfileHash.ProfileHashVersion},
            {"source", PaperProfileHash.SourceId},
            {"date", date},
            {"profile", new Dictionary<string, object> {
                {"categories", CsvSet(settings.Categories).ToList().Cast<object>().ToList()},
                {"exclude_categories", CsvSet(settings.ExcludeCategories).ToList().Cast<object>().ToList()},
                {"profile_hash", ProfileHashOf(settings)}
            }},
            {"generator", new Dictionary<string, object> {
                {"type", "ai"},
                {"provider_id", ServiceClient.ProviderFromEnvironment(AiBindingKey)},
                {"model", ""},
                {"producer_version", "io.github.kevendai.arxiv " + AppVersion},
                {"generated_at", RuntimeUtil.Iso(DateTimeOffset.Now)}
            }},
            {"papers", items}
        };
    }

    // 回传快照。**失败绝不让本轮同步失败**（§7.1 / 计划 §十）：AI 已经花过钱了，Todo 该照常导入，
    // 只是别的机器这次拿不到结果。
    private static string StoreSnapshot(List<Dictionary<string, object>> papers, string date, PaperSettings settings)
    {
        Dictionary<string, object> call = new Dictionary<string, object> { { "snapshot", BundleFromPapers(papers, date, settings) } };
        ServiceCallResult response = ServiceClient.Call(SnapshotService, "put_snapshot", call, SnapshotCallTimeoutSeconds);
        if (!response.Ok) return "失败：" + ServiceClient.Trim(response.Error);
        if (!JsonUtil.Bool(response.Output, "stored", false)) return "未存储：" + JsonUtil.String(response.Output, "reason", "提供者未存储该快照");
        return "ok";
    }

    // ── Translation Provider（translation_provider@1）──────────────────────────────
    // 只在**最终 Top-N 已确定**之后翻译（计划 §十四）：绝不为了省事先把当天所有候选标题翻一遍。
    // 返回一句可拼进状态的说明；**翻译失败绝不让导入失败**（§十五：翻译是 enrichment），
    // 失败时标题留空，调用方回落到英文原标题。
    private static string TranslateTitles(List<Dictionary<string, object>> papers, PaperSettings settings)
    {
        if (!settings.TranslateEnabled) return "";
        if (!Services.Translation) return "未安装翻译插件，已使用英文标题";
        List<Dictionary<string, object>> pending = papers.Where(p => S(p, "translated_title") == "").ToList();
        if (pending.Count == 0) return "";
        Dictionary<string, object> call = new Dictionary<string, object> {
            {"texts", pending.Select(p => (object)S(p, "title")).ToList()},
            {"source_language", "en-US"},
            {"target_language", "zh-CN"}
        };
        ServiceCallResult response = ServiceClient.Call(TranslationService, "translate", call, TranslationCallTimeoutSeconds);
        if (!response.Ok) return "标题翻译失败，已使用英文标题";
        List<object> translations = JsonUtil.Array(JsonUtil.Get(response.Output, "translations"));
        // 等长保序是 Provider 侧的责任（§7.3）；消费方仍自查一次，不许让短数组造成错位标题。
        if (translations.Count != pending.Count) return "标题翻译结果条数不符，已使用英文标题";
        for (int index = 0; index < pending.Count; index++)
        {
            string text = Convert.ToString(translations[index], CultureInfo.InvariantCulture);
            if (!String.IsNullOrWhiteSpace(text)) pending[index]["translated_title"] = text;
        }
        return "";
    }

    // 同步主流程（计划 §九～§十二）。**这里没有任何 UI**：付费确认不是插件里的模态框，而是
    // "带着 attention 正常退出，等用户在磁贴上点按钮重开一次 job"（§5.1、§5.2）。
    //
    // 顺序固定：本地有效缓存 → 远端快照 → （有 AI 才）询问 → 抓 arXiv 评分。
    // manual 只影响两件事：后台同步有 8:00–20:00 窗口与"今天已完成就不再走"；同一天已拒绝过
    // 付费 AI 时后台不再用 attention 打扰（§4.6-6 第 2 条），但用户在界面上主动点永远有效。
    private sealed class PaperSyncResult
    {
        public bool Ok = true;
        public string Summary = "";
        public string Error = "";
        public Dictionary<string, object> Attention;
    }

    private static PaperSyncResult RunPaperSyncFlow(Dictionary<string, object> state, bool manual, string paperDate, bool replaceToday)
    {
        PaperSyncResult result = new PaperSyncResult();
        PaperSettings settings = LoadPaperSettings();
        string today = String.IsNullOrWhiteSpace(paperDate) ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : paperDate;
        if (!settings.Enabled)
        {
            result.Summary = "论文推荐已关闭";
            return result;
        }
        if (!manual)
        {
            DateTime now = DateTime.Now;
            if (now.TimeOfDay < TimeSpan.FromHours(8) || now.TimeOfDay > TimeSpan.FromHours(20)) { result.Summary = "不在同步时段"; return result; }
            if (JsonUtil.String(Meta(state), "last_arxiv_sync_date", "") == today) { result.Summary = "今日论文已同步"; return result; }
        }
        Directory.CreateDirectory(PaperCache);
        CleanupPaperCache(settings);
        // `rescore`（"重新爬取并打分"）的语义 = 强制重新生成今天的推荐结果：先扔掉本地缓存，
        // 再让导入路径替换掉今天由 arxiv 建的那批待办。AI 路径的替换由下面那个 rescore 记号
        // 在 RunPaperWorker 里完成（那里还要照顾事务回滚）。
        if (replaceToday)
        {
            string cached = Path.Combine(PaperCache, today + "_papers.json");
            if (File.Exists(cached)) { try { File.Delete(cached); } catch { } }
        }

        // ① 本地有效缓存：命中就一个外部调用都不发。
        List<Dictionary<string, object>> papers;
        if (TryUseLocalCache(today, settings, out papers))
        {
            ImportPapers(state, papers, today, settings, replaceToday);
            result.Summary = JsonUtil.String(Meta(state), "status", "已导入本地缓存的今日论文");
            return result;
        }

        // ② 远端快照：命中直接导入，**0 次 AI 调用**（计划 §九：不得因为装了 AI Provider 再评分一次）。
        string remoteError;
        if (SnapshotAvailable(settings))
        {
            SnapshotResult snapshot = FetchSnapshot(settings, today);
            if (snapshot.Found)
            {
                papers = snapshot.Papers;
                JsonUtil.SaveAtomic(Path.Combine(PaperCache, today + "_papers.json"), papers);
                ImportPapers(state, papers, today, settings, replaceToday);
                result.Summary = JsonUtil.String(Meta(state), "status", "已从远端快照导入今日论文");
                return result;
            }
            remoteError = snapshot.Failed ? snapshot.Error : "远端暂无 " + today + " 的推荐结果";
        }
        else remoteError = "未安装论文同步插件";

        // ③ 有 AI Provider 才谈 fallback；没有就按计划 §十一 老实说"今天没有可用结果"，
        //    绝不自行生成"最新 N 篇"。
        if (!AiAvailable(settings))
        {
            Meta(state)["status"] = "今天没有可用的论文推荐结果";
            result.Summary = "今天没有可用的论文推荐结果。\r\n\r\n你可以：\r\n• 安装论文同步插件，从远端获取已经生成的推荐结果；\r\n• 安装 AI Provider，在需要时手动生成推荐结果。";
            return result;
        }
        if (!PaidAiAllowed)
        {
            if (!manual && DeclinedToday)
            {
                Meta(state)["status"] = remoteError + "；今天已拒绝过 AI 评分";
                result.Summary = JsonUtil.String(Meta(state), "status", "论文推荐需要确认");
                return result;
            }
            result.Attention = new Dictionary<string, object> {
                {"type", "paid_service_confirmation"},
                {"service", AiService},
                {"message", remoteError + "。\r\n\r\n是否使用「" + ProviderLabel(Services.AiName, "AI Provider") + "」重新获取并评分今日 arXiv 论文？\r\n\r\n此操作将调用外部 AI API，可能产生费用。"},
                {"resume_action", ResumeAction},
                {"resume_input", new Dictionary<string, object>{{"allow_paid_ai", true}}}
            };
            Meta(state)["status"] = "论文推荐需要确认";
            result.Summary = "论文推荐需要确认";
            return result;
        }

        // ④ 用户已经明确同意这一次调用：抓 arXiv → 两阶段评分 → 回传快照 → 导入。
        //    落一个 rescore 记号：AI 路径的语义就是"按当前设置重新生成今天的推荐结果"，
        //    所以导入时替换掉今天由 arxiv 建的那批待办，而不是在旧列表后面再追加一批。
        try { File.WriteAllText(PaperRescorePath(today), RuntimeUtil.Iso(DateTimeOffset.Now), RuntimeUtil.Utf8NoBom); }
        catch { }
        WritePaperJob("queued", "正在准备 " + today + " 的论文", 0, 0);
        int code = RunPaperWorker(today);
        if (code != 0)
        {
            result.Ok = false;
            result.Error = ReadPaperJobMessage("论文评分失败");
            return result;
        }
        result.Summary = JsonUtil.String(Meta(state), "status", "论文同步完成");
        return result;
    }

    private static bool TryUseLocalCache(string date, PaperSettings settings, out List<Dictionary<string, object>> papers)
    {
        papers = null;
        string finalPath = Path.Combine(PaperCache, date + "_papers.json");
        if (!TryLoadPapers(finalPath, out papers) || !IsPaperFileComplete(papers, settings)) { papers = null; return false; }
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
                if (!AiAvailable(settings)) { WritePaperJob("failed", "没有可用的 AI Provider，无法生成推荐结果", 0, 0); return 2; }
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
                // 评分完成后回传快照。失败只记 warning —— AI 的钱已经花过，Todo 必须照常导入（§7.1）。
                string snapshotStatus = SnapshotAvailable(settings) ? StoreSnapshot(papers, date, settings) : "disabled";
                WritePaperJob("importing", "评分完成，正在导入本地待办", papers.Count, papers.Count);
                bool replaceToday = File.Exists(PaperRescorePath(date));
                int result = WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
                    List<Dictionary<string, object>> removed = replaceToday ? Tasks(state).Where(t => IsPaperTaskCreatedOnDate(t, date)).ToList() : new List<Dictionary<string, object>>();
                    try
                    {
                        if (replaceToday) Tasks(state).RemoveAll(t => IsPaperTaskCreatedOnDate(t, date));
                        ImportPapers(state, papers, date, settings, false);
                        if (snapshotStatus.StartsWith("失败", StringComparison.Ordinal)) Meta(state)["status"] += "；快照回传失败";
                        else if (snapshotStatus.StartsWith("未存储", StringComparison.Ordinal)) Meta(state)["status"] += "；快照未存储";
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
                if (result == 0 && snapshotStatus.StartsWith("失败", StringComparison.Ordinal)) completion += "；远端快照回传失败";
                else if (result == 0 && snapshotStatus.StartsWith("未存储", StringComparison.Ordinal)) completion += "；远端未存储快照";
                WritePaperJob(result == 0 ? "completed" : "failed", completion, papers.Count, papers.Count);
                return result;
            }
            catch (Exception ex)
            {
                string reason = DescribePaperFailure(UnwrapAggregate(ex));
                WritePaperJob("failed", "论文评分失败：" + reason, 0, 0);
                UpdatePaperStatus("论文评分失败：" + reason, true);
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
        XmlDocument document = FetchArxivXml(feedPath, ArxivFetchTimeoutSeconds * 1000,
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
        try
        {
        Parallel.ForEach(batches, new ParallelOptions { MaxDegreeOfParallelism = AiBatchParallelism }, delegate(List<Dictionary<string, object>> batch) {
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
        catch (AggregateException ex) { throw UnwrapAggregate(ex); }
    }

    // 单批的网络重试、退避、拆批兜底全部留在 arxiv 这一层：Provider 只保证"一次调用"的语义
    // （§7.2）。致命错误的判定也已经上移到 CallAiBatch —— 它抛 PaperAiFatalException，这里必须
    // 原样上抛，绝不能当成"普通失败"去拆成两半重试（那只会把同一笔钱再花一遍）。
    private static Dictionary<int, int> ScoreBatchWithRecovery(List<Dictionary<string, object>> batch, string stage, int minimum, int maximum, PaperSettings settings)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { return CallAiBatch(batch, stage, minimum, maximum, settings); }
            catch (PaperAiFatalException) { throw; }
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

    // 解析 Provider 的结构化结果。**形状**是 arxiv 定的（§7.2 的 response_schema），所以
    // 越界分数、非整数 ID、批次不一致这些校验必须留在消费方 —— Provider 无权替我们放宽。
    private static Dictionary<int, int> ParseAiScores(string content, List<Dictionary<string, object>> batch, int minimum, int maximum)
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
                throw new Exception("AI Provider 返回了非整数 ID 或分数");
            if (score < minimum || score > maximum) throw new Exception("AI Provider 返回分数超出 " + minimum + "-" + maximum);
            result[id] = score;
        }
        if (!expected.SetEquals(result.Keys)) throw new Exception("AI Provider 返回的论文 ID 与请求批次不一致");
        return result;
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

    // 通用 JSON HTTP 客户端（PaperHttp）随 DeepSeek / 文件服务器客户端一起删除了：2.1 之后
    // arxiv 只剩 arXiv RSS 这一个外部 HTTP 目标，走的是下面专用的 PaperXml。

    private static int ToPaperProgressInt(long value)
    {
        if (value <= 0) return 0;
        return value >= Int32.MaxValue ? Int32.MaxValue : (int)value;
    }

    private static string FormatPaperMegabytes(long value)
    {
        return (Math.Max(0L, value) / (1024D * 1024D)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
    }

    // 旧有的 DeepSeek 连通性自检、文件服务器登录/探测/目录创建/上传（TestDeepSeekConnection、
    // LoginFileServer、TestFileServerConnection、DownloadRemotePaper、SyncLocalPaperToRemoteIfMissing、
    // UploadScoredPaper、CheckRemotePaper、UploadRemotePaperWithToken、EnsureRemotePaperDirectory）
    // 在 2.1 里**整体删除**：它们分别变成了 ai_provider@1 的 `structured_complete`（Provider 自己
    // 负责连通性）与 paper_snapshot_provider@1 的 `get_snapshot` / `put_snapshot`（§7.1、§7.2）。
    // 连带消失的还有"远端已有同名文件，是否覆盖？"这个模态框 —— 快照提供者用 date+profile_hash
    // 作为天然去重键，冲突由它自己按策略处理，不再需要让用户在同步流程中间做决定。

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

    // replaceToday：`rescore` 走缓存/快照分支时用来替换今天由 arxiv 建的那批待办（AI 分支的
    // 替换在 RunPaperWorker 里做，它还要照顾事务回滚，所以那一路传 false，免得删两遍）。
    private static void ImportPapers(Dictionary<string, object> state, List<Dictionary<string, object>> papers, string date, PaperSettings settings, bool replaceToday)
    {
        if (replaceToday) Tasks(state).RemoveAll(t => IsPaperTaskCreatedOnDate(t, date));
        List<Dictionary<string, object>> ranked = papers.Where(p => JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "abstract") != null)
            .OrderByDescending(p => Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "abstract"), CultureInfo.InvariantCulture))
            .ThenByDescending(p => Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "title") ?? 0, CultureInfo.InvariantCulture))
            .Take(settings.ImportCount).ToList();
        if (ranked.Count == 0) { Meta(state)["status"] = date + " 没有通过摘要评分的论文"; return; }
        // 翻译放在这里、只翻**最终要落地的这几篇**：Top-N 之前一个字的翻译成本都不花（计划 §十四）。
        // 快照导入的论文本来就带 translated_title，TranslateTitles 会自动跳过（§6.1）。
        string translateWarning = TranslateTitles(ranked, settings);
        int added = 0, translated = 0;
        foreach (Dictionary<string, object> paper in ranked)
        {
            string arxiv = S(paper, "arxiv_id");
            string target = "https://arxiv.org/html/" + arxiv;
            if (Tasks(state).Any(t => S(t, "target").Equals(target, StringComparison.OrdinalIgnoreCase) || S(t, "note").Contains("arXiv ID：" + arxiv))) continue;
            string original = S(paper, "title");
            string translatedTitle = S(paper, "translated_title");
            if (translatedTitle != "") translated++;
            Dictionary<string, object> score = JsonUtil.Object(JsonUtil.Get(paper, "score"));
            EditorResult editor = new EditorResult {
                Title = "(" + Convert.ToString(JsonUtil.Get(score, "abstract"), CultureInfo.InvariantCulture) + ") " + (translatedTitle != "" ? translatedTitle : original),
                Target = target,
                Note = "论文原标题：" + original + "\r\narXiv ID：" + arxiv,
                Labels = new List<string>{"论文"}, Available = "", Due = ""
            };
            Tasks(state).Add(NewTask(editor, "arxiv"));
            added++;
        }
        Meta(state)["last_arxiv_sync_date"] = date;
        Meta(state)["status"] = added > 0 ? "已添加 " + date + " 共 " + added + " 篇，翻译 " + translated + " 篇" : date + " 推荐论文均已存在";
        if (translateWarning != "") Meta(state)["status"] += "；" + translateWarning;
    }

    private static string SafeStatusMessage(string value)
    {
        value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > 180 ? value.Substring(0, 180) : value;
    }

    private static int RunPaperSelfTests()
    {
        string originalResourceDir = ResourceDir;
        string testRoot = Path.Combine(Path.GetTempPath(), "RainmeterPaperTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testRoot);
            ResourceDir = testRoot;
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
            Dictionary<int, int> parsed = ParseAiScores("{\"scores\":{\"1\":8,\"2\":5}}", papers, 0, 10);
            if (parsed.Count != 2 || parsed[1] != 8 || parsed[2] != 5) return 35;
            try { ParseAiScores("{\"scores\":{\"1\":8}}", papers, 0, 10); return 36; }
            catch { }
            // §9.2 的"复制，不删除"：旧文件里那些 DeepSeek / 文件服务器段仍然存在，arxiv 读到
            // 只当没看见 —— 不报错、不采用、也不因为读不懂而退回默认值。
            JsonUtil.WriteDpapiJson(PaperSyncSecret, new Dictionary<string, object>{
                {"Version",3},{"Enabled",true},
                {"BaseUrl","http://example.invalid"},{"Account","legacy"},{"Password","secret"},
                {"Scoring", new Dictionary<string, object>{{"Categories","cs.CV"},{"TitleThreshold",9}}}});
            PaperSettings migrated = LoadPaperSettings();
            if (!migrated.Enabled || migrated.Categories != "cs.CV" || migrated.TitleThreshold != 9 || paperSettingsLoadError != "") return 37;
            PaperSettings disabled = new PaperSettings { Enabled = false, TitlePrompt = "", AbstractPrompt = "" };
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
            // AI 致命错误现在是 Provider 通过信封的 fatal / no_provider 报出来的（§4.3），
            // arxiv 不再自己解释 401/402/403 —— 只负责"立刻上抛、不再对每一批白跑"。
            Exception unwrapped = UnwrapAggregate(new AggregateException(new Exception("发生一个或多个错误。"), new PaperAiFatalException("DeepSeek 账户余额不足（HTTP 402），请充值后重新同步论文")));
            if (!(unwrapped is PaperAiFatalException) || DescribePaperFailure(unwrapped).IndexOf("余额不足", StringComparison.Ordinal) < 0) return 47;
            Exception nested = UnwrapAggregate(new AggregateException(new AggregateException(new PaperHttpException(429, "HTTP 429 busy"))));
            if (!(nested is PaperHttpException) || ((PaperHttpException)nested).StatusCode != 429) return 48;
            // Provider 的 error 文案原样透传（翻译/映射在 Provider 侧做），这里只消毒。
            if (DescribePaperFailure(new PaperHttpException(0, "HTTP 0 nope")).IndexOf("HTTP 0", StringComparison.Ordinal) < 0) return 49;
            // 服务可用性判据：业务开关 + 宿主解析出的绑定，两者缺一不可。
            PaperServices saved = Services;
            try
            {
                Services = new PaperServices();
                if (AiAvailable(settings) || SnapshotAvailable(settings)) return 50;
                Services.Ai = true;
                if (!AiAvailable(settings)) return 51;
                if (AiAvailable(new PaperSettings { Enabled = false })) return 52;
            }
            finally { Services = saved; }
            return 0;
        }
        finally
        {
            ResourceDir = originalResourceDir;
            try { Directory.Delete(testRoot, true); } catch { }
        }
    }
}
