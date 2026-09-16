using System;
using System.Collections.Generic;
using System.Globalization;
using RainmeterBackend;

// PaperBundle v1 的回归探针（规格 docs/V2.1-PROVIDER-INTERFACE.md §6，冻结版 rev2；
// 验收矩阵 #11 #12 #14 #19 #20）。三部分：
//   §A profile_hash 规范化与稳定性 —— 含两个与 Python 互证的跨语言黄金向量；
//   §B §6.2 字段校验全表 —— 三态判定（compatible / incompatible / error）；
//   §C 模型填充。
// 全程纯内存计算，不碰网络、不碰插件目录。
internal static class PaperBundleProbe
{
    private static int checks, failures;

    // Python 参考实现（sha256 of UTF-8）算出的黄金向量，用于钉死规范化算法：
    // {"abstract_batch_size":3,"abstract_prompt":"给这些论文的摘要打分：{PAPERS}",
    //  "categories":["cs.CV"],"exclude_categories":[],"import_count":5,
    //  "paper_bundle_schema_version":1,"scoring_version":1,"source":"arxiv",
    //  "title_batch_size":10,"title_prompt":"给这些论文的标题打分：{PAPERS}","title_threshold":7}
    private const string GoldenBaseline = "3ea731784b03fe8d3e52912285deaf04edd2241b973f53c2b79bfc39fdef8bde";
    // 同上，但 title_prompt="line1\nline2 \"quoted\" back\slash\ttab"（实际字符含 LF/引号/反斜杠/Tab）、abstract_prompt=""
    private const string GoldenEscapes = "d494b230fd4df2845e42fa08efe9ab55c8bebc5715dc20c830f98fdbe3ae197b";

    private static int Main(string[] args)
    {
        string localHash = PaperProfileHash.Compute(BaseProfile());
        try
        {
            HashSection(localHash);
            ValidatorSection(localHash);
            ModelSection(localHash);
        }
        catch (Exception ex) { Expect(false, "探针自身不应抛异常：" + ex.Message); }
        Console.WriteLine((failures == 0 ? "PASS" : "FAIL") + " paper bundle probe: checks=" + checks.ToString(CultureInfo.InvariantCulture) + " failures=" + failures.ToString(CultureInfo.InvariantCulture));
        return failures == 0 ? 0 : 1;
    }

    private static void Expect(bool condition, string description)
    {
        checks++;
        if (!condition)
        {
            failures++;
            Console.WriteLine("  FAIL: " + description);
        }
    }

    // ── §A profile_hash（§6.3）────────────────────────────────────────────────
    private static PaperProfile BaseProfile()
    {
        return new PaperProfile
        {
            Categories = new List<string> { "cs.CV" },
            ExcludeCategories = new List<string>(),
            TitlePrompt = "给这些论文的标题打分：{PAPERS}",
            AbstractPrompt = "给这些论文的摘要打分：{PAPERS}",
            TitleThreshold = 7,
            TitleBatchSize = 10,
            AbstractBatchSize = 3,
            ImportCount = 5
        };
    }

    private static void HashSection(string localHash)
    {
        Expect(IsHex64(localHash), "hash 是 64 位小写 hex");
        Expect(localHash == GoldenBaseline, "基准 profile 命中 Python 黄金向量（跨语言钉死规范化算法）");

        PaperProfile escaped = BaseProfile();
        escaped.TitlePrompt = "line1\nline2 \"quoted\" back\\slash\ttab";
        escaped.AbstractPrompt = "";
        string escapedHash = PaperProfileHash.Compute(escaped);
        Expect(escapedHash == GoldenEscapes, "含 LF/引号/反斜杠/Tab 的 prompt 命中第二个黄金向量（转义正确）");
        Expect(escapedHash != localHash, "prompt 不同则 hash 不同");

        // 同语义不同输入顺序必须同 hash（键字典序 + 数组去重升序）。
        PaperProfile shuffled = BaseProfile();
        shuffled.Categories = new List<string> { "cs.LG", "cs.CV", "cs.CV", " cs.AI " };
        PaperProfile sorted = BaseProfile();
        sorted.Categories = new List<string> { "cs.AI", "cs.CV", "cs.LG" };
        Expect(PaperProfileHash.Compute(shuffled) == PaperProfileHash.Compute(sorted), "分类乱序/重复/带空白 → 同 hash");

        PaperProfile excludedDup = BaseProfile();
        excludedDup.ExcludeCategories = new List<string> { "b.cat", "a.cat", "a.cat", " " };
        PaperProfile excludedClean = BaseProfile();
        excludedClean.ExcludeCategories = new List<string> { "a.cat", "b.cat" };
        Expect(PaperProfileHash.Compute(excludedDup) == PaperProfileHash.Compute(excludedClean), "排除分类去重升序 → 同 hash");

        PaperProfile crlf = BaseProfile();
        crlf.TitlePrompt = "  给这些论文的标题打分：{PAPERS}\r\n第二行  ";
        PaperProfile lf = BaseProfile();
        lf.TitlePrompt = "给这些论文的标题打分：{PAPERS}\n第二行";
        Expect(PaperProfileHash.Compute(crlf) == PaperProfileHash.Compute(lf), "CRLF→LF + Trim 规范化 → 同 hash");

        // 冻结决定（§13）：批大小纳入 hash。
        PaperProfile otherThreshold = BaseProfile(); otherThreshold.TitleThreshold = 8;
        Expect(PaperProfileHash.Compute(otherThreshold) != localHash, "title_threshold 变化 → hash 变化");
        PaperProfile otherTitleBatch = BaseProfile(); otherTitleBatch.TitleBatchSize = 11;
        Expect(PaperProfileHash.Compute(otherTitleBatch) != localHash, "title_batch_size 变化 → hash 变化（已冻结纳入）");
        PaperProfile otherAbstractBatch = BaseProfile(); otherAbstractBatch.AbstractBatchSize = 4;
        Expect(PaperProfileHash.Compute(otherAbstractBatch) != localHash, "abstract_batch_size 变化 → hash 变化（已冻结纳入）");
        PaperProfile otherImport = BaseProfile(); otherImport.ImportCount = 6;
        Expect(PaperProfileHash.Compute(otherImport) != localHash, "import_count 变化 → hash 变化");

        PaperProfile otherCategory = BaseProfile(); otherCategory.Categories = new List<string> { "cs.AI" };
        Expect(PaperProfileHash.Compute(otherCategory) != localHash, "分类集合变化 → hash 变化");
        PaperProfile otherPrompt = BaseProfile(); otherPrompt.AbstractPrompt = "换个摘要提示词：{PAPERS}";
        Expect(PaperProfileHash.Compute(otherPrompt) != localHash, "abstract_prompt 变化 → hash 变化");

        Expect(PaperProfileHash.Compute(BaseProfile()) == localHash, "同输入两次计算结果一致（确定性）");
        PaperProfile nullCategories = BaseProfile(); nullCategories.Categories = null;
        PaperProfile emptyCategories = BaseProfile(); emptyCategories.Categories = new List<string>();
        Expect(PaperProfileHash.Compute(nullCategories) == PaperProfileHash.Compute(emptyCategories), "null 与空分类列表等价");
    }

    // ── §B §6.2 字段校验全表─────────────────────────────────────────────────
    private static Dictionary<string, object> ValidBundle(string profileHash)
    {
        return new Dictionary<string, object>
        {
            { "schema_version", 1 },
            { "profile_hash_version", 1 },
            { "source", "arxiv" },
            { "date", "2026-09-16" },
            { "profile", new Dictionary<string, object>
                {
                    { "categories", new object[] { "cs.CV" } },
                    { "exclude_categories", new object[] { } },
                    { "profile_hash", profileHash }
                }
            },
            { "generator", new Dictionary<string, object>
                {
                    { "type", "ai" },
                    { "provider_id", "io.github.kevendai.ai-deepseek" },
                    { "model", "deepseek-chat" },
                    { "producer_version", "io.github.kevendai.arxiv 2.0.0" },
                    { "generated_at", "2026-09-16T08:10:00+08:00" }
                }
            },
            { "papers", new object[] { Paper("2609.01234", 8, 46, "这是一篇关于域适应目标检测的论文摘要。") } }
        };
    }

    private static Dictionary<string, object> Paper(string id, int titleScore, int abstractScore, string abstractText)
    {
        return new Dictionary<string, object>
        {
            { "id", id },
            { "title", "Original English Title " + id },
            { "translated_title", null },
            { "abstract", abstractText },
            { "authors", new object[] { "Alice Author", "Bob Author" } },
            { "categories", new object[] { "cs.CV" } },
            { "published_at", "2026-09-16T00:30:00+08:00" },
            { "url", "https://arxiv.org/abs/" + id },
            { "pdf_url", "https://arxiv.org/pdf/" + id },
            { "scores", new Dictionary<string, object> { { "title", titleScore }, { "abstract", abstractScore } } }
        };
    }

    private static PaperBundleCheckResult Check(Dictionary<string, object> bundle, string expectedHash)
    {
        return PaperBundleValidator.Check(JsonUtil.Serialize(bundle), expectedHash);
    }

    private static bool IsIncompatible(PaperBundleCheckResult result, string reasonFragment)
    {
        return result.Verdict == PaperBundleVerdict.Incompatible && result.Reason.IndexOf(reasonFragment, StringComparison.Ordinal) >= 0;
    }

    private static void ValidatorSection(string localHash)
    {
        string otherHash = PaperProfileHash.Compute(ChangedProfile());

        // 合法样本（验收 #20：title 9 / abstract 46 属正常量程）。
        Dictionary<string, object> normalScores = ValidBundle(localHash);
        normalScores["papers"] = new object[] { Paper("2609.01234", 9, 46, "abstract") };
        PaperBundleCheckResult normal = Check(normalScores, localHash);
        Expect(normal.Verdict == PaperBundleVerdict.Compatible, "title 9 / abstract 46 → compatible（#20 正常量程样本）");
        Expect(Check(ValidBundle(localHash), localHash).Verdict == PaperBundleVerdict.Compatible, "规格 §6.1 示例 → compatible");

        // schema_version（#12：未知版本是 incompatible，不是 error）。
        Dictionary<string, object> futureSchema = ValidBundle(localHash); futureSchema["schema_version"] = 2;
        Expect(IsIncompatible(Check(futureSchema, localHash), "schema_version"), "schema_version=2 → incompatible 且理由可读（#12）");
        Dictionary<string, object> stringSchema = ValidBundle(localHash); stringSchema["schema_version"] = "1";
        Expect(IsIncompatible(Check(stringSchema, localHash), "schema_version"), "schema_version 是字符串 → incompatible");
        Dictionary<string, object> missingSchema = ValidBundle(localHash); missingSchema.Remove("schema_version");
        Expect(IsIncompatible(Check(missingSchema, localHash), "schema_version"), "缺 schema_version → incompatible");

        Dictionary<string, object> hashVersion = ValidBundle(localHash); hashVersion["profile_hash_version"] = 2;
        Expect(IsIncompatible(Check(hashVersion, localHash), "profile_hash_version"), "profile_hash_version=2 → incompatible");

        Dictionary<string, object> source = ValidBundle(localHash); source["source"] = "ieee";
        Expect(IsIncompatible(Check(source, localHash), "source"), "source=ieee → incompatible");
        Dictionary<string, object> missingSource = ValidBundle(localHash); missingSource.Remove("source");
        Expect(IsIncompatible(Check(missingSource, localHash), "source"), "缺 source → incompatible");

        Dictionary<string, object> badDate = ValidBundle(localHash); badDate["date"] = "2026/09/16";
        Expect(IsIncompatible(Check(badDate, localHash), "date"), "date 用斜杠 → incompatible");
        Dictionary<string, object> impossibleDate = ValidBundle(localHash); impossibleDate["date"] = "2026-13-40";
        Expect(IsIncompatible(Check(impossibleDate, localHash), "date"), "date 非法日历日 → incompatible");
        Dictionary<string, object> shortDate = ValidBundle(localHash); shortDate["date"] = "2026-9-6";
        Expect(IsIncompatible(Check(shortDate, localHash), "date"), "date 非补零格式 → incompatible");

        Dictionary<string, object> badCategory = ValidBundle(localHash);
        badCategory["profile"] = new Dictionary<string, object> { { "categories", new object[] { "cs CV" } }, { "exclude_categories", new object[] { } }, { "profile_hash", localHash } };
        Expect(IsIncompatible(Check(badCategory, localHash), "cs CV"), "分类含空格 → incompatible");
        Dictionary<string, object> missingCategories = ValidBundle(localHash);
        missingCategories["profile"] = new Dictionary<string, object> { { "exclude_categories", new object[] { } }, { "profile_hash", localHash } };
        Expect(IsIncompatible(Check(missingCategories, localHash), "categories"), "缺 profile.categories → incompatible");
        Dictionary<string, object> missingExclude = ValidBundle(localHash);
        missingExclude["profile"] = new Dictionary<string, object> { { "categories", new object[] { "cs.CV" } }, { "profile_hash", localHash } };
        Expect(IsIncompatible(Check(missingExclude, localHash), "exclude_categories"), "缺 profile.exclude_categories → incompatible（空数组也必须显式给）");

        // profile_hash（#14：不匹配 = 视为无快照）。
        Expect(IsIncompatible(Check(ValidBundle(localHash), otherHash), "不匹配"), "profile_hash 不匹配 → incompatible 且理由写明两侧值");
        Dictionary<string, object> nonHexHash = ValidBundle(localHash);
        nonHexHash["profile"] = new Dictionary<string, object> { { "categories", new object[] { "cs.CV" } }, { "exclude_categories", new object[] { } }, { "profile_hash", "not-hex" } };
        Expect(IsIncompatible(Check(nonHexHash, localHash), "小写十六进制"), "profile_hash 非 64 位小写 hex → incompatible");
        Dictionary<string, object> upperHash = ValidBundle(localHash);
        upperHash["profile"] = new Dictionary<string, object> { { "categories", new object[] { "cs.CV" } }, { "exclude_categories", new object[] { } }, { "profile_hash", localHash.ToUpperInvariant() } };
        Expect(IsIncompatible(Check(upperHash, localHash), "小写十六进制"), "profile_hash 大写 → incompatible（只认小写）");

        Dictionary<string, object> missingGenerator = ValidBundle(localHash); missingGenerator.Remove("generator");
        Expect(IsIncompatible(Check(missingGenerator, localHash), "generator"), "缺 generator → incompatible");
        Dictionary<string, object> badGeneratorType = ValidBundle(localHash);
        badGeneratorType["generator"] = new Dictionary<string, object> { { "type", "quantum" } };
        Expect(IsIncompatible(Check(badGeneratorType, localHash), "ai / remote / manual"), "generator.type 未知 → incompatible");
        Dictionary<string, object> remoteGenerator = ValidBundle(localHash);
        remoteGenerator["generator"] = new Dictionary<string, object> { { "type", "remote" } };
        Expect(Check(remoteGenerator, localHash).Verdict == PaperBundleVerdict.Compatible, "generator.type=remote（预留值）→ compatible");

        // papers 数组。
        Dictionary<string, object> missingPapers = ValidBundle(localHash); missingPapers.Remove("papers");
        Expect(IsIncompatible(Check(missingPapers, localHash), "papers"), "缺 papers → incompatible");
        Dictionary<string, object> papersNotArray = ValidBundle(localHash); papersNotArray["papers"] = new Dictionary<string, object> { { "a", 1 } };
        Expect(IsIncompatible(Check(papersNotArray, localHash), "数组"), "papers 是对象 → incompatible");
        Dictionary<string, object> emptyPapers = ValidBundle(localHash); emptyPapers["papers"] = new object[] { };
        PaperBundleCheckResult emptyResult = Check(emptyPapers, localHash);
        Expect(emptyResult.Verdict == PaperBundleVerdict.Compatible && emptyResult.Bundle.Papers.Count == 0, "papers 空数组 → compatible（当天没有论文）");

        List<object> tooMany = new List<object>();
        for (int i = 0; i < 501; i++) tooMany.Add(Paper(i.ToString("0000", CultureInfo.InvariantCulture), 8, 46, "a"));
        Dictionary<string, object> overLimit = ValidBundle(localHash); overLimit["papers"] = tooMany.ToArray();
        Expect(IsIncompatible(Check(overLimit, localHash), "500"), "501 篇 → incompatible");

        List<object> duplicated = new List<object>();
        duplicated.Add(Paper("2609.01234", 8, 46, "a"));
        duplicated.Add(Paper("2609.01234", 9, 47, "b"));
        Dictionary<string, object> dupBundle = ValidBundle(localHash); dupBundle["papers"] = duplicated.ToArray();
        Expect(IsIncompatible(Check(dupBundle, localHash), "重复"), "id 重复 → incompatible");

        // 单篇字段。
        Dictionary<string, object> noId = ValidBundle(localHash); noId["papers"] = new object[] { Paper("", 8, 46, "a") };
        Expect(IsIncompatible(Check(noId, localHash), "id"), "空 id → incompatible");
        Dictionary<string, object> emptyTitle = ValidBundle(localHash);
        emptyTitle["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "title", "") };
        Expect(IsIncompatible(Check(emptyTitle, localHash), "title"), "空 title → incompatible");
        Dictionary<string, object> longTitle = ValidBundle(localHash);
        longTitle["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "title", new string('t', 1001)) };
        Expect(IsIncompatible(Check(longTitle, localHash), "1001"), "title 1001 字符 → incompatible");
        Dictionary<string, object> longAbstract = ValidBundle(localHash);
        longAbstract["papers"] = new object[] { Paper("2609.01234", 8, 46, new string('a', 20001)) };
        Expect(IsIncompatible(Check(longAbstract, localHash), "20001"), "abstract 20001 字符 → incompatible");

        // 分段量程（#19）。
        Dictionary<string, object> mixedUpScore = ValidBundle(localHash);
        mixedUpScore["papers"] = new object[] { Paper("2609.01234", 43, 46, "a") };
        PaperBundleCheckResult mixed = Check(mixedUpScore, localHash);
        Expect(mixed.Verdict == PaperBundleVerdict.Incompatible
            && mixed.Reason.IndexOf("0-10", StringComparison.Ordinal) >= 0
            && mixed.Reason.IndexOf("疑似", StringComparison.Ordinal) >= 0,
            "title=43 → incompatible 且理由点明量程混用（#19）");
        Dictionary<string, object> titleEleven = ValidBundle(localHash);
        titleEleven["papers"] = new object[] { Paper("2609.01234", 11, 46, "a") };
        Expect(IsIncompatible(Check(titleEleven, localHash), "0-10"), "title=11 → incompatible");
        Dictionary<string, object> negativeTitle = ValidBundle(localHash);
        negativeTitle["papers"] = new object[] { Paper("2609.01234", -1, 46, "a") };
        Expect(IsIncompatible(Check(negativeTitle, localHash), "负数"), "title=-1 → incompatible");
        Dictionary<string, object> abstractFiftyOne = ValidBundle(localHash);
        abstractFiftyOne["papers"] = new object[] { Paper("2609.01234", 8, 51, "a") };
        Expect(IsIncompatible(Check(abstractFiftyOne, localHash), "0-50"), "abstract=51 → incompatible");
        Dictionary<string, object> negativeAbstract = ValidBundle(localHash);
        negativeAbstract["papers"] = new object[] { Paper("2609.01234", 8, -2, "a") };
        Expect(IsIncompatible(Check(negativeAbstract, localHash), "负数"), "abstract=-2 → incompatible");
        Dictionary<string, object> stringScore = ValidBundle(localHash);
        stringScore["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "scores", new Dictionary<string, object> { { "title", "8" }, { "abstract", 46 } }) };
        Expect(IsIncompatible(Check(stringScore, localHash), "整数"), "title 是字符串数字 → incompatible（不可信输入只认真值）");
        Dictionary<string, object> fractionalScore = ValidBundle(localHash);
        fractionalScore["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "scores", new Dictionary<string, object> { { "title", 8.5 }, { "abstract", 46 } }) };
        Expect(IsIncompatible(Check(fractionalScore, localHash), "整数"), "title=8.5 非整数 → incompatible");
        Dictionary<string, object> noScores = ValidBundle(localHash);
        noScores["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "scores", null) };
        Expect(IsIncompatible(Check(noScores, localHash), "scores"), "缺 scores → incompatible");

        // URL。
        Dictionary<string, object> httpUrl = ValidBundle(localHash);
        httpUrl["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "url", "http://arxiv.org/abs/2609.01234") };
        Expect(IsIncompatible(Check(httpUrl, localHash), "https"), "http url → incompatible");
        Dictionary<string, object> evilUrl = ValidBundle(localHash);
        evilUrl["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "url", "https://evil.example.com/abs/2609.01234") };
        Expect(IsIncompatible(Check(evilUrl, localHash), "arxiv.org"), "host 白名单外 → incompatible");
        Dictionary<string, object> missingUrl = ValidBundle(localHash);
        missingUrl["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "url", null) };
        Expect(IsIncompatible(Check(missingUrl, localHash), "url"), "缺 url → incompatible");
        Dictionary<string, object> missingPdf = ValidBundle(localHash);
        missingPdf["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "pdf_url", null) };
        Expect(IsIncompatible(Check(missingPdf, localHash), "pdf_url"), "缺 pdf_url → incompatible");
        Dictionary<string, object> exportPdf = ValidBundle(localHash);
        exportPdf["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "pdf_url", "https://export.arxiv.org/pdf/2609.01234") };
        Expect(Check(exportPdf, localHash).Verdict == PaperBundleVerdict.Compatible, "export.arxiv.org 在白名单内 → compatible");

        // published_at。
        Dictionary<string, object> badTime = ValidBundle(localHash);
        badTime["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "published_at", "not-a-date") };
        Expect(IsIncompatible(Check(badTime, localHash), "published_at"), "published_at 不可解析 → incompatible");
        Dictionary<string, object> emptyTime = ValidBundle(localHash);
        emptyTime["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "published_at", "") };
        Expect(Check(emptyTime, localHash).Verdict == PaperBundleVerdict.Compatible, "published_at 空串（可选项）→ compatible");
        Dictionary<string, object> noTime = ValidBundle(localHash);
        noTime["papers"] = new object[] { MutatePaper("2609.01234", 8, 46, "published_at", null) };
        Expect(Check(noTime, localHash).Verdict == PaperBundleVerdict.Compatible, "published_at 缺省 → compatible");

        // 未知字段（前向兼容）。
        Dictionary<string, object> extra = ValidBundle(localHash);
        extra["future_field"] = new Dictionary<string, object> { { "x", 1 } };
        Expect(Check(extra, localHash).Verdict == PaperBundleVerdict.Compatible, "未知顶层字段 → 仍 compatible（加法式演进）");

        // 三态里的 error：解析失败 / 根非对象 / 空内容 / 调用方缺本地 hash。
        PaperBundleCheckResult broken = PaperBundleValidator.Check("{broken", localHash);
        Expect(broken.Verdict == PaperBundleVerdict.Error && broken.Reason.IndexOf("解析", StringComparison.Ordinal) >= 0, "坏 JSON → error 且理由可读");
        PaperBundleCheckResult arrayRoot = PaperBundleValidator.Check("[1,2,3]", localHash);
        Expect(arrayRoot.Verdict == PaperBundleVerdict.Error && arrayRoot.Reason.IndexOf("对象", StringComparison.Ordinal) >= 0, "根是数组 → error");
        Expect(PaperBundleValidator.Check("", localHash).Verdict == PaperBundleVerdict.Error, "空内容 → error");
        Expect(PaperBundleValidator.Check(JsonUtil.Serialize(ValidBundle(localHash)), "").Verdict == PaperBundleVerdict.Error, "本地 profile_hash 缺失 → error（调用方错误）");

        // 8 MB 字节上限（每篇字段都合法，靠体量触发）。
        List<object> heavy = new List<object>();
        for (int i = 0; i < 430; i++) heavy.Add(Paper(i.ToString("0000", CultureInfo.InvariantCulture), 8, 46, new string('a', 20000)));
        Dictionary<string, object> big = ValidBundle(localHash); big["papers"] = heavy.ToArray();
        PaperBundleCheckResult bigResult = PaperBundleValidator.Check(JsonUtil.Serialize(big), localHash);
        Expect(bigResult.Verdict == PaperBundleVerdict.Incompatible && bigResult.Reason.IndexOf("8 MB", StringComparison.Ordinal) >= 0, "超过 8 MB → incompatible");
    }

    private static PaperProfile ChangedProfile()
    {
        PaperProfile profile = BaseProfile();
        profile.Categories = new List<string> { "cs.AI" };
        return profile;
    }

    private static Dictionary<string, object> MutatePaper(string id, int titleScore, int abstractScore, string key, object value)
    {
        Dictionary<string, object> paper = Paper(id, titleScore, abstractScore, "a");
        if (value == null) paper.Remove(key);
        else paper[key] = value;
        return paper;
    }

    // ── §C 模型填充 ─────────────────────────────────────────────────────────
    private static void ModelSection(string localHash)
    {
        PaperBundleCheckResult result = Check(ValidBundle(localHash), localHash);
        Expect(result.Verdict == PaperBundleVerdict.Compatible && result.Bundle != null, "compatible 时模型可用");
        PaperBundle bundle = result.Bundle;
        Expect(bundle.SchemaVersion == 1 && bundle.ProfileHashVersion == 1, "版本字段填充");
        Expect(bundle.Source == "arxiv" && bundle.Date == "2026-09-16", "source / date 填充");
        Expect(bundle.ProfileHash == localHash && bundle.Categories.Count == 1 && bundle.Categories[0] == "cs.CV", "profile 字段填充");
        Expect(bundle.GeneratorType == "ai" && bundle.GeneratorProviderId == "io.github.kevendai.ai-deepseek"
            && bundle.GeneratorModel == "deepseek-chat" && bundle.GeneratorProducerVersion == "io.github.kevendai.arxiv 2.0.0", "generator 字段填充");
        Expect(bundle.Papers.Count == 1, "论文数量填充");
        PaperBundlePaper paper = bundle.Papers[0];
        Expect(paper.Id == "2609.01234" && paper.Title == "Original English Title 2609.01234", "id / title 填充");
        Expect(paper.TitleScore == 8 && paper.AbstractScore == 46, "分段分数填充（title 0-10 / abstract 0-50）");
        Expect(paper.AbstractText == "这是一篇关于域适应目标检测的论文摘要。", "摘要填充");
        Expect(paper.Authors.Count == 2 && paper.Authors[1] == "Bob Author" && paper.Categories.Count == 1, "authors / categories 填充");
        Expect(paper.TranslatedTitle == "" && paper.Url == "https://arxiv.org/abs/2609.01234" && paper.PdfUrl == "https://arxiv.org/pdf/2609.01234", "translated_title(空)/链接填充");
        Expect(paper.PublishedAt == "2026-09-16T00:30:00+08:00", "published_at 填充");
    }

    private static bool IsHex64(string value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok) return false;
        }
        return true;
    }
}
