using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RainmeterBackend
{
    // ── v2.1 PaperBundle v1（规格 docs/V2.1-PROVIDER-INTERFACE.md §6，冻结版 rev2）────
    //
    // 三个职责，全部不依赖网络与 TodoPaperService：
    //   1. PaperProfileHash   —— §6.3 profile_hash 的规范化与 SHA-256；
    //   2. PaperBundle        —— §6.1 的数据模型；
    //   3. PaperBundleValidator —— §6.2 的全表校验，输出三态判定
    //      (compatible / incompatible / error)，incompatible 携带可读中文理由。
    //
    // 远端 JSON 一律视为不可信输入；incompatible 的语义是「结构合法但不是给我们用的」，
    // 不得当作 error 重试。

    /// <summary>判定三态（§6.2）。compatible = 可直接导入；incompatible = 结构合法但不是
    /// 给我们用的（记 warning、走 AI fallback，不得导入）；error = 取不到/解析失败。</summary>
    public enum PaperBundleVerdict { Compatible, Incompatible, Error }

    /// <summary>profile_hash 的语义输入（§6.3 冻结字段表：只有下面这些进 hash）。
    /// cache_days 与所有 api_* / file_* / translate_* 凭据、UI 偏好一律不进。</summary>
    public sealed class PaperProfile
    {
        public List<string> Categories = new List<string>();
        public List<string> ExcludeCategories = new List<string>();
        public string TitlePrompt = "";
        public string AbstractPrompt = "";
        public int TitleThreshold = 7;
        public int TitleBatchSize = 10;
        public int AbstractBatchSize = 3;
        public int ImportCount = 5;
    }

    public static class PaperProfileHash
    {
        /// <summary>paper_bundle_schema_version，进 hash。</summary>
        public const int BundleSchemaVersion = 1;
        /// <summary>profile_hash_version，写在 bundle 里，不进 hash 本身。</summary>
        public const int ProfileHashVersion = 1;
        /// <summary>scoring_version：显式递增的评分算法版本；将来只改算法不改 prompt 时用它失效旧快照。</summary>
        public const int ScoringVersion = 1;
        /// <summary>快照来源标识，进 hash 且必须与 bundle 的 source 一致。</summary>
        public const string SourceId = "arxiv";

        /// <summary>对规范化 JSON（键字典序、数组去重升序、字符串 trim、数字最简形式）的
        /// UTF-8 字节取 SHA-256 小写 hex。同语义不同输入顺序必须得到同一 hash。</summary>
        public static string Compute(PaperProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            StringBuilder canonical = new StringBuilder(512);
            canonical.Append('{');
            AppendKey(canonical, "abstract_batch_size");
            canonical.Append(profile.AbstractBatchSize.ToString(CultureInfo.InvariantCulture));
            canonical.Append(',');
            AppendKey(canonical, "abstract_prompt");
            AppendString(canonical, NormalizePrompt(profile.AbstractPrompt));
            canonical.Append(',');
            AppendKey(canonical, "categories");
            AppendStringArray(canonical, NormalizeCategories(profile.Categories));
            canonical.Append(',');
            AppendKey(canonical, "exclude_categories");
            AppendStringArray(canonical, NormalizeCategories(profile.ExcludeCategories));
            canonical.Append(',');
            AppendKey(canonical, "import_count");
            canonical.Append(profile.ImportCount.ToString(CultureInfo.InvariantCulture));
            canonical.Append(',');
            AppendKey(canonical, "paper_bundle_schema_version");
            canonical.Append(BundleSchemaVersion.ToString(CultureInfo.InvariantCulture));
            canonical.Append(',');
            AppendKey(canonical, "scoring_version");
            canonical.Append(ScoringVersion.ToString(CultureInfo.InvariantCulture));
            canonical.Append(',');
            AppendKey(canonical, "source");
            AppendString(canonical, SourceId);
            canonical.Append(',');
            AppendKey(canonical, "title_batch_size");
            canonical.Append(profile.TitleBatchSize.ToString(CultureInfo.InvariantCulture));
            canonical.Append(',');
            AppendKey(canonical, "title_prompt");
            AppendString(canonical, NormalizePrompt(profile.TitlePrompt));
            canonical.Append(',');
            AppendKey(canonical, "title_threshold");
            canonical.Append(profile.TitleThreshold.ToString(CultureInfo.InvariantCulture));
            canonical.Append('}');

            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                StringBuilder hex = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++) hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        /// <summary>Prompt 规范化 = CRLF → LF，然后 Trim()（与 TodoPaperService 的
        /// settings.TitlePrompt.Trim() 语义一致）。</summary>
        internal static string NormalizePrompt(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return value.Replace("\r\n", "\n").Trim();
        }

        /// <summary>分类数组规范化 = 元素 trim → 丢空 → 去重(Ordinal) → 升序(Ordinal)。
        /// 比较器已冻结为 Ordinal；将来要改必须递增 profile_hash_version。</summary>
        internal static List<string> NormalizeCategories(IEnumerable<string> values)
        {
            List<string> cleaned = new List<string>();
            if (values != null)
            {
                foreach (string raw in values)
                {
                    string item = (raw ?? "").Trim();
                    if (item.Length == 0) continue;
                    if (!cleaned.Contains(item)) cleaned.Add(item);
                }
            }
            cleaned.Sort(StringComparer.Ordinal);
            return cleaned;
        }

        private static void AppendKey(StringBuilder builder, string key)
        {
            builder.Append('"').Append(key).Append("\":");
        }

        private static void AppendString(StringBuilder builder, string value)
        {
            builder.Append('"');
            AppendEscaped(builder, value ?? "");
            builder.Append('"');
        }

        private static void AppendStringArray(StringBuilder builder, List<string> values)
        {
            builder.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) builder.Append(',');
                AppendString(builder, values[i]);
            }
            builder.Append(']');
        }

        // 只转义 JSON 必须转义的字符；非 ASCII 原样输出（hash 取的是 UTF-8 字节，
        // 不允许 \uXXXX 转义，否则同一个中文 prompt 会因序列化器不同而 hash 不同）。
        private static void AppendEscaped(StringBuilder builder, string value)
        {
            foreach (char c in value)
            {
                if (c == '"') builder.Append("\\\"");
                else if (c == '\\') builder.Append("\\\\");
                else if (c == '\n') builder.Append("\\n");
                else if (c == '\r') builder.Append("\\r");
                else if (c == '\t') builder.Append("\\t");
                else if (c == '\b') builder.Append("\\b");
                else if (c == '\f') builder.Append("\\f");
                else if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else builder.Append(c);
            }
        }
    }

    public sealed class PaperBundlePaper
    {
        public string Id = "";
        public string Title = "";
        public string TranslatedTitle = "";
        public string AbstractText = "";
        public string PublishedAt = "";
        public string Url = "";
        public string PdfUrl = "";
        public List<string> Authors = new List<string>();
        public List<string> Categories = new List<string>();
        public int TitleScore;
        public int AbstractScore;
    }

    public sealed class PaperBundle
    {
        public int SchemaVersion;
        public int ProfileHashVersion;
        public string Source = "";
        public string Date = "";
        public string ProfileHash = "";
        public List<string> Categories = new List<string>();
        public List<string> ExcludeCategories = new List<string>();
        public string GeneratorType = "";
        public string GeneratorProviderId = "";
        public string GeneratorModel = "";
        public string GeneratorProducerVersion = "";
        public string GeneratedAt = "";
        public List<PaperBundlePaper> Papers = new List<PaperBundlePaper>();
    }

    public sealed class PaperBundleCheckResult
    {
        public PaperBundleVerdict Verdict;
        /// <summary>incompatible / error 时的可读中文原因；compatible 时为空串。</summary>
        public string Reason = "";
        /// <summary>仅 compatible 时非 null。</summary>
        public PaperBundle Bundle;
    }

    public static class PaperBundleValidator
    {
        /// <summary>整个 bundle 的字节上限（与 PluginPackageInstaller 风格一致）。</summary>
        public const long MaxBundleBytes = 8L * 1024 * 1024;
        /// <summary>papers 数量上限。</summary>
        public const int MaxPapers = 500;
        public const int MaxTitleLength = 1000;
        public const int MaxAbstractLength = 20000;
        /// <summary>标题阶段量程 0-10（TodoPaperService.cs:26,727）；摘要阶段 0-50（:46,733）。</summary>
        public const int TitleScoreMax = 10;
        public const int AbstractScoreMax = 50;

        private static readonly string[] AllowedHosts = { "arxiv.org", "www.arxiv.org", "export.arxiv.org" };
        private static readonly string[] AllowedGeneratorTypes = { "ai", "remote", "manual" };

        /// <summary>校验一段 bundle JSON。expectedProfileHash 是本地按当前设置现算的
        /// profile_hash；为空视为调用方错误（error 而不是 incompatible）。</summary>
        public static PaperBundleCheckResult Check(string json, string expectedProfileHash)
        {
            if (String.IsNullOrEmpty(json)) return Error("bundle 内容为空");
            if (String.IsNullOrEmpty(expectedProfileHash)) return Error("本地 profile_hash 缺失，无法校验快照");

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaxBundleBytes)
                return Incompatible("bundle 体积 " + bytes.Length.ToString(CultureInfo.InvariantCulture) + " 字节，超过 8 MB 上限");

            object root;
            try { root = JsonUtil.Deserialize(json); }
            catch (Exception ex) { return Error("bundle JSON 无法解析：" + ex.Message); }
            Dictionary<string, object> map = root as Dictionary<string, object>;
            if (map == null) return Error("bundle 根节点必须是 JSON 对象");

            int schemaVersion;
            if (!TryGetInteger(map, "schema_version", out schemaVersion))
                return Incompatible("schema_version 必须是整数");
            if (schemaVersion != PaperProfileHash.BundleSchemaVersion)
                return Incompatible("bundle schema_version=" + schemaVersion.ToString(CultureInfo.InvariantCulture)
                    + "，仅支持 " + PaperProfileHash.BundleSchemaVersion.ToString(CultureInfo.InvariantCulture) + "（不是错误，不做重试）");

            int hashVersion;
            if (!TryGetInteger(map, "profile_hash_version", out hashVersion))
                return Incompatible("profile_hash_version 必须是整数");
            if (hashVersion != PaperProfileHash.ProfileHashVersion)
                return Incompatible("profile_hash_version=" + hashVersion.ToString(CultureInfo.InvariantCulture)
                    + "，仅支持 " + PaperProfileHash.ProfileHashVersion.ToString(CultureInfo.InvariantCulture));

            string source = JsonUtil.String(map, "source", null);
            if (String.IsNullOrEmpty(source)) return Incompatible("缺少 source");
            if (source != PaperProfileHash.SourceId)
                return Incompatible("source=" + source + "，仅支持 " + PaperProfileHash.SourceId);

            string date = JsonUtil.String(map, "date", null);
            if (String.IsNullOrEmpty(date)) return Incompatible("缺少 date");
            if (!IsValidBundleDate(date)) return Incompatible("date=" + date + " 必须是合法的 YYYY-MM-DD（按 Asia/Shanghai 解释）");

            Dictionary<string, object> profile = JsonUtil.Object(JsonUtil.Get(map, "profile"));
            if (profile.Count == 0) return Incompatible("缺少 profile 对象");

            List<string> categories;
            string reason = ReadCategoryList(profile, "categories", out categories);
            if (reason != null) return Incompatible(reason);
            List<string> excludeCategories;
            reason = ReadCategoryList(profile, "exclude_categories", out excludeCategories);
            if (reason != null) return Incompatible(reason);

            string remoteHash = JsonUtil.String(profile, "profile_hash", null);
            if (String.IsNullOrEmpty(remoteHash)) return Incompatible("缺少 profile.profile_hash");
            if (!IsLowerCaseHex64(remoteHash)) return Incompatible("profile_hash 必须是 64 位小写十六进制");
            if (!String.Equals(remoteHash, expectedProfileHash, StringComparison.Ordinal))
                return Incompatible("profile_hash 不匹配：远端 " + remoteHash + " ≠ 本地 " + expectedProfileHash
                    + "（快照属于另一份配置，不得导入）");

            Dictionary<string, object> generator = JsonUtil.Object(JsonUtil.Get(map, "generator"));
            if (generator.Count == 0) return Incompatible("缺少 generator 对象");
            string generatorType = JsonUtil.String(generator, "type", null);
            if (String.IsNullOrEmpty(generatorType)) return Incompatible("缺少 generator.type");
            if (Array.IndexOf(AllowedGeneratorTypes, generatorType) < 0)
                return Incompatible("generator.type=" + generatorType + "，仅支持 ai / remote / manual");

            object papersRaw;
            if (!map.TryGetValue("papers", out papersRaw) || papersRaw == null)
                return Incompatible("缺少 papers 数组");
            // JsonUtil.Array 对「非数组」与「空数组」都返回空表，这里必须先确认真的是数组：
            // 空数组是合法的（当天没有论文），非数组才是 incompatible。
            if (!(papersRaw is object[]) && !(papersRaw is System.Collections.ArrayList))
                return Incompatible("papers 必须是数组");
            List<object> papersRawList = JsonUtil.Array(papersRaw);
            if (papersRawList.Count > MaxPapers)
                return Incompatible("论文数量 " + papersRawList.Count.ToString(CultureInfo.InvariantCulture)
                    + " 超过上限 " + MaxPapers.ToString(CultureInfo.InvariantCulture));

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            List<PaperBundlePaper> papers = new List<PaperBundlePaper>();
            for (int i = 0; i < papersRawList.Count; i++)
            {
                Dictionary<string, object> paperMap = papersRawList[i] as Dictionary<string, object>;
                if (paperMap == null) return Incompatible("papers[" + i.ToString(CultureInfo.InvariantCulture) + "] 必须是 JSON 对象");

                string where = "papers[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                string id = JsonUtil.String(paperMap, "id", null);
                if (String.IsNullOrEmpty(id)) return Incompatible(where + " 缺少非空 id");
                where += "（id=" + id + "）";
                if (!seenIds.Add(id)) return Incompatible("id 重复：" + id + "（论文 id 必须全表唯一）");

                string title = JsonUtil.String(paperMap, "title", null);
                if (String.IsNullOrEmpty(title)) return Incompatible(where + " 缺少非空 title");
                if (title.Length > MaxTitleLength) return Incompatible(where + " 标题长度 " + title.Length.ToString(CultureInfo.InvariantCulture) + " 超过上限 " + MaxTitleLength.ToString(CultureInfo.InvariantCulture));

                string abstractText = JsonUtil.String(paperMap, "abstract", "");
                if (abstractText.Length > MaxAbstractLength)
                    return Incompatible(where + " 摘要长度 " + abstractText.Length.ToString(CultureInfo.InvariantCulture) + " 超过上限 " + MaxAbstractLength.ToString(CultureInfo.InvariantCulture));

                Dictionary<string, object> scores = JsonUtil.Object(JsonUtil.Get(paperMap, "scores"));
                if (scores.Count == 0) return Incompatible(where + " 缺少 scores 对象");
                int titleScore, abstractScore;
                if (!TryGetInteger(scores, "title", out titleScore))
                    return Incompatible(where + " 标题分必须是 0-" + TitleScoreMax.ToString(CultureInfo.InvariantCulture) + " 的整数");
                if (titleScore > TitleScoreMax)
                    return Incompatible(where + " 标题分 " + titleScore.ToString(CultureInfo.InvariantCulture)
                        + " 超出 0-" + TitleScoreMax.ToString(CultureInfo.InvariantCulture)
                        + " 量程（疑似把 0-" + AbstractScoreMax.ToString(CultureInfo.InvariantCulture) + " 的摘要分写进了 title）");
                if (titleScore < 0) return Incompatible(where + " 标题分为负数：" + titleScore.ToString(CultureInfo.InvariantCulture));
                if (!TryGetInteger(scores, "abstract", out abstractScore))
                    return Incompatible(where + " 摘要分必须是 0-" + AbstractScoreMax.ToString(CultureInfo.InvariantCulture) + " 的整数");
                if (abstractScore > AbstractScoreMax)
                    return Incompatible(where + " 摘要分 " + abstractScore.ToString(CultureInfo.InvariantCulture)
                        + " 超出 0-" + AbstractScoreMax.ToString(CultureInfo.InvariantCulture) + " 量程");
                if (abstractScore < 0) return Incompatible(where + " 摘要分为负数：" + abstractScore.ToString(CultureInfo.InvariantCulture));

                string url = JsonUtil.String(paperMap, "url", null);
                string pdfUrl = JsonUtil.String(paperMap, "pdf_url", null);
                if (!IsAllowedPaperUrl(url)) return Incompatible(where + " url 必须是 https 且 host 属于 arxiv.org");
                if (!IsAllowedPaperUrl(pdfUrl)) return Incompatible(where + " pdf_url 必须是 https 且 host 属于 arxiv.org");

                string publishedAt = JsonUtil.String(paperMap, "published_at", "");
                if (publishedAt.Length > 0 && !IsParseableTimestamp(publishedAt))
                    return Incompatible(where + " published_at 无法解析：" + publishedAt);

                papers.Add(new PaperBundlePaper
                {
                    Id = id,
                    Title = title,
                    TranslatedTitle = JsonUtil.String(paperMap, "translated_title", ""),
                    AbstractText = abstractText,
                    PublishedAt = publishedAt,
                    Url = url,
                    PdfUrl = pdfUrl,
                    Authors = ReadStringList(paperMap, "authors"),
                    Categories = ReadStringList(paperMap, "categories"),
                    TitleScore = titleScore,
                    AbstractScore = abstractScore
                });
            }

            return new PaperBundleCheckResult
            {
                Verdict = PaperBundleVerdict.Compatible,
                Reason = "",
                Bundle = new PaperBundle
                {
                    SchemaVersion = schemaVersion,
                    ProfileHashVersion = hashVersion,
                    Source = source,
                    Date = date,
                    ProfileHash = remoteHash,
                    Categories = categories,
                    ExcludeCategories = excludeCategories,
                    GeneratorType = generatorType,
                    GeneratorProviderId = JsonUtil.String(generator, "provider_id", ""),
                    GeneratorModel = JsonUtil.String(generator, "model", ""),
                    GeneratorProducerVersion = JsonUtil.String(generator, "producer_version", ""),
                    GeneratedAt = JsonUtil.String(generator, "generated_at", ""),
                    Papers = papers
                }
            };
        }

        private static PaperBundleCheckResult Incompatible(string reason) { return new PaperBundleCheckResult { Verdict = PaperBundleVerdict.Incompatible, Reason = reason }; }
        private static PaperBundleCheckResult Error(string reason) { return new PaperBundleCheckResult { Verdict = PaperBundleVerdict.Error, Reason = reason }; }

        /// <summary>严格整数读取：只认 JSON 数值（不接受字符串数字），且必须可落在 int 内。</summary>
        private static bool TryGetInteger(Dictionary<string, object> map, string key, out int value)
        {
            value = 0;
            object raw = JsonUtil.Get(map, key);
            if (raw == null) return false;
            if (raw is int) { value = (int)raw; return true; }
            if (raw is long)
            {
                long narrow = (long)raw;
                if (narrow < Int32.MinValue || narrow > Int32.MaxValue) return false;
                value = (int)narrow;
                return true;
            }
            if (raw is decimal)
            {
                decimal precise = (decimal)raw;
                if (precise != decimal.Truncate(precise)) return false;
                if (precise < Int32.MinValue || precise > Int32.MaxValue) return false;
                value = (int)precise;
                return true;
            }
            if (raw is double)
            {
                double loose = (double)raw;
                if (loose != Math.Floor(loose)) return false;
                if (loose < Int32.MinValue || loose > Int32.MaxValue) return false;
                value = (int)loose;
                return true;
            }
            return false;
        }

        private static string ReadCategoryList(Dictionary<string, object> profile, string key, out List<string> values)
        {
            values = new List<string>();
            object raw;
            if (!profile.TryGetValue(key, out raw) || raw == null) return "缺少 profile." + key;
            List<object> items = JsonUtil.Array(raw);
            for (int i = 0; i < items.Count; i++)
            {
                string item = items[i] == null ? null : Convert.ToString(items[i], CultureInfo.InvariantCulture);
                if (String.IsNullOrEmpty(item)) return "profile." + key + " 的元素必须是字符串";
                if (!IsValidCategoryToken(item)) return "profile." + key + " 的元素 " + item + " 不匹配 ^[A-Za-z0-9._-]+$";
                values.Add(item);
            }
            return null;
        }

        private static List<string> ReadStringList(Dictionary<string, object> map, string key)
        {
            List<string> values = new List<string>();
            List<object> items = JsonUtil.Array(JsonUtil.Get(map, key));
            foreach (object item in items)
            {
                string text = item == null ? null : Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!String.IsNullOrEmpty(text)) values.Add(text);
            }
            return values;
        }

        private static bool IsValidCategoryToken(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
            {
                bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!allowed) return false;
            }
            return true;
        }

        private static bool IsValidBundleDate(string value)
        {
            if (value == null || value.Length != 10) return false;
            DateTime parsed;
            return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
        }

        private static bool IsParseableTimestamp(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed);
        }

        private static bool IsLowerCaseHex64(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
            {
                bool allowed = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!allowed) return false;
            }
            return true;
        }

        private static bool IsAllowedPaperUrl(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            foreach (string allowed in AllowedHosts)
            {
                if (String.Equals(uri.Host, allowed, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
