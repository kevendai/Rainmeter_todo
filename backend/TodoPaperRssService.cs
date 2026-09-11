using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using RainmeterBackend;

internal static partial class TodoApp
{
    private const string PaperRssServerMutexName = @"Global\RainmeterTodoPaperRssServer";
    private const int PaperRssPort = 8891;
    private const string PaperRssAddress = "127.0.0.1";
    private static string PaperRssBaseUrl { get { return "http://" + PaperRssAddress + ":" + PaperRssPort.ToString(CultureInfo.InvariantCulture); } }
    private const string PaperRssNamespace = "https://example.com/rss/paper/1.0";
    private const string DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly TimeSpan PaperRssSyncStart = TimeSpan.FromHours(8);
    private static readonly TimeSpan PaperRssSyncEnd = TimeSpan.FromHours(20);
    private static readonly TimeSpan PaperRssSyncInterval = TimeSpan.FromMinutes(10);
    private static int paperRssSyncRunning;

    private static string PaperRssStatusPath { get { return Path.Combine(PaperCache, "paper-rss-status.json"); } }

    private sealed class PaperRssItem
    {
        public string Title;
        public string Link;
        public string Description;
        public string OriginalTitle;
        public List<string> Authors = new List<string>();
        public string ArxivId;
        public int TitleScore;
        public int AbstractScore;
        public string Abstract;
        public string AbstractUrl;
        public string PdfUrl;
        public int Score;
        public DateTimeOffset Published;
        public List<string> Categories = new List<string>();
    }

    private static bool IsPaperRssSyncWindow(DateTime now)
    {
        return now.TimeOfDay >= PaperRssSyncStart && now.TimeOfDay < PaperRssSyncEnd;
    }

    private static void EnsurePaperRssServer(bool waitForReady)
    {
        PaperSettings settings = LoadPaperSettings();
        if (!settings.Enabled || !settings.RssEnabled) return;
        if (IsPaperRssHealthy()) return;
        try { if (File.Exists(PaperRssStatusPath)) File.Delete(PaperRssStatusPath); } catch { }
        try
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, "PaperRssServer") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex) { throw new Exception("无法启动本地论文 RSS 服务：" + ex.Message); }
        if (!waitForReady) return;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            Thread.Sleep(100);
            if (IsPaperRssHealthy()) return;
        }
        string detail = "端口 " + PaperRssPort.ToString(CultureInfo.InvariantCulture) + " 可能已被其他程序占用";
        try
        {
            if (File.Exists(PaperRssStatusPath))
            {
                Dictionary<string, object> status = JsonUtil.LoadObject(PaperRssStatusPath);
                string error = JsonUtil.String(status, "error", "");
                if (error != "") detail = error;
            }
        }
        catch { }
        throw new Exception("本地论文 RSS 启用失败：" + detail + "。功能已保持关闭。");
    }

    private static bool IsPaperRssHealthy()
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + PaperRssPort + "/healthz");
            request.Method = "GET";
            request.Timeout = 350;
            request.ReadWriteTimeout = 350;
            request.Proxy = null;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return response.StatusCode == HttpStatusCode.OK && reader.ReadToEnd().Contains("RainmeterTodoPaperRss");
        }
        catch { return false; }
    }

    private static int RunPaperRssServer()
    {
        using (Mutex mutex = new Mutex(false, PaperRssServerMutexName))
        {
            bool held = false;
            TcpListener listener = null;
            try
            {
                held = mutex.WaitOne(0);
                if (!held) return 0;
                PaperSettings settings = LoadPaperSettings();
                if (!settings.Enabled || !settings.RssEnabled) return 0;
                listener = new TcpListener(IPAddress.Loopback, PaperRssPort);
                try { listener.Start(16); }
                catch (SocketException ex)
                {
                    WritePaperRssStatus("failed", "无法绑定 " + PaperRssAddress + ":" + PaperRssPort.ToString(CultureInfo.InvariantCulture) + "；端口可能已被占用（" + ex.SocketErrorCode + "）");
                    return 3;
                }
                WritePaperRssStatus("running", "");
                QueuePaperRssSync();
                DateTime nextSync = DateTime.Now.Add(PaperRssSyncInterval);
                DateTime nextSettingsCheck = DateTime.MinValue;
                DateTime settingsWriteTime = File.Exists(PaperSyncSecret) ? File.GetLastWriteTimeUtc(PaperSyncSecret) : DateTime.MinValue;
                while (true)
                {
                    DateTime now = DateTime.Now;
                    if (now >= nextSettingsCheck)
                    {
                        DateTime currentWriteTime = File.Exists(PaperSyncSecret) ? File.GetLastWriteTimeUtc(PaperSyncSecret) : DateTime.MinValue;
                        if (currentWriteTime != settingsWriteTime)
                        {
                            settings = LoadPaperSettings();
                            settingsWriteTime = currentWriteTime;
                            if (!settings.Enabled || !settings.RssEnabled) break;
                        }
                        nextSettingsCheck = now.AddSeconds(1);
                    }
                    if (now >= nextSync)
                    {
                        QueuePaperRssSync();
                        nextSync = now.Add(PaperRssSyncInterval);
                    }
                    if (!listener.Pending()) { Thread.Sleep(80); continue; }
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandlePaperRssClient(client); });
                }
                WritePaperRssStatus("stopped", "");
                return 0;
            }
            catch (Exception ex)
            {
                WritePaperRssStatus("failed", SafeStatusMessage(ex.Message));
                return 1;
            }
            finally
            {
                if (listener != null) try { listener.Stop(); } catch { }
                if (held) mutex.ReleaseMutex();
            }
        }
    }

    private static void QueuePaperRssSync()
    {
        if (!IsPaperRssSyncWindow(DateTime.Now)) return;
        if (Interlocked.CompareExchange(ref paperRssSyncRunning, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(delegate {
            try
            {
                WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
                    string before = JsonUtil.Serialize(state);
                    PaperSettings settings = LoadPaperSettings();
                    string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    List<Dictionary<string, object>> cached;
                    string finalPath = Path.Combine(PaperCache, date + "_papers.json");
                    if (!TryLoadPapers(finalPath, out cached) || !IsPaperFileComplete(cached, settings))
                        Meta(state)["last_arxiv_sync_date"] = "";
                    SyncArxiv(state, false, "");
                    if (before != JsonUtil.Serialize(state))
                    {
                        Commit(state);
                        refresh = true;
                    }
                });
            }
            finally { Interlocked.Exchange(ref paperRssSyncRunning, 0); }
        });
    }

    private static void WritePaperRssStatus(string state, string error)
    {
        try
        {
            Directory.CreateDirectory(PaperCache);
            JsonUtil.SaveAtomic(PaperRssStatusPath, new Dictionary<string, object> {
                {"service", "RainmeterTodoPaperRss"}, {"state", state}, {"error", error ?? ""},
                {"address", "127.0.0.1"}, {"port", PaperRssPort}, {"updated_at", RuntimeUtil.Iso(DateTimeOffset.Now)}
            });
        }
        catch { }
    }

    private static void HandlePaperRssClient(TcpClient client)
    {
        using (client)
        {
            NetworkStream stream = null;
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 5000;
                stream = client.GetStream();
                using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    string requestLine = reader.ReadLine();
                    if (String.IsNullOrWhiteSpace(requestLine)) return;
                    int headerBytes = requestLine.Length;
                    string line;
                    while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                    {
                        headerBytes += line.Length;
                        if (headerBytes > 16384) throw new Exception("HTTP 请求头过大");
                    }
                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        WritePaperRssResponse(stream, 405, "text/plain; charset=utf-8", "只支持 GET 请求");
                        return;
                    }
                    string target = parts[1];
                    int queryAt = target.IndexOf('?');
                    string path = queryAt < 0 ? target : target.Substring(0, queryAt);
                    string query = queryAt < 0 ? "" : target.Substring(queryAt + 1);
                    if (path == "/healthz")
                    {
                        PaperSettings settings = LoadPaperSettings();
                        string body = JsonUtil.Serialize(new Dictionary<string, object> {
                            {"service", "RainmeterTodoPaperRss"}, {"status", "ok"},
                            {"enabled", settings.Enabled && settings.RssEnabled},
                            {"address", "127.0.0.1"}, {"port", PaperRssPort},
                            {"in_window", IsPaperRssSyncWindow(DateTime.Now)}, {"time", RuntimeUtil.Iso(DateTimeOffset.Now)}
                        });
                        WritePaperRssResponse(stream, 200, "application/json; charset=utf-8", body);
                        return;
                    }
                    if (path != "/paper/rss")
                    {
                        WritePaperRssResponse(stream, 404, "text/plain; charset=utf-8", "未找到该端点");
                        return;
                    }
                    int minimumScore = ParsePaperRssQueryInt(query, "min_score", 0, 0, 50);
                    int limit = ParsePaperRssQueryInt(query, "limit", Int32.MaxValue, 1, 100);
                    string xml = BuildCurrentPaperRss(minimumScore, limit, DateTimeOffset.Now);
                    WritePaperRssResponse(stream, 200, "application/rss+xml; charset=utf-8", xml);
                }
            }
            catch (ArgumentException ex)
            {
                try { if (stream != null) WritePaperRssResponse(stream, 400, "text/plain; charset=utf-8", ex.Message); } catch { }
            }
            catch (Exception ex)
            {
                try { if (stream != null) WritePaperRssResponse(stream, 500, "text/plain; charset=utf-8", "RSS 生成失败：" + SafeStatusMessage(ex.Message)); } catch { }
            }
            finally { if (stream != null) try { stream.Dispose(); } catch { } }
        }
    }

    private static int ParsePaperRssQueryInt(string query, string name, int fallback, int minimum, int maximum)
    {
        if (String.IsNullOrEmpty(query)) return fallback;
        foreach (string pair in query.Split('&'))
        {
            string[] parts = pair.Split(new char[] {'='}, 2);
            string key = Uri.UnescapeDataString(parts[0].Replace("+", " "));
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            string raw = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace("+", " ")) : "";
            int value;
            if (!Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
                throw new ArgumentException(name + " 必须是 " + minimum + "–" + maximum + " 之间的整数");
            return value;
        }
        return fallback;
    }

    private static string BuildCurrentPaperRss(int minimumScore, int limit, DateTimeOffset now)
    {
        string date = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string cachePath = Path.Combine(PaperCache, date + "_papers.json");
        List<Dictionary<string, object>> papers;
        if (!TryLoadPapers(cachePath, out papers)) return BuildPaperRssXml(new List<PaperRssItem>());
        PaperSettings settings = LoadPaperSettings();
        using (Mutex mutex = new Mutex(false, @"Global\RainmeterTodoState"))
        {
            bool held = false;
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(15));
                if (!held) throw new Exception("待办数据正忙，请稍后重试");
                if (!File.Exists(StatePath)) throw new Exception("未找到 tasks.json");
                Dictionary<string, object> state = JsonUtil.LoadObject(StatePath);
                if (JsonUtil.Get(state, "tasks") == null) throw new Exception("tasks.json 缺少 tasks 数组");
                return BuildPaperRssXml(SelectPaperRssItems(Tasks(state), papers, settings.ImportCount, date, minimumScore, limit));
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
    }

    private static List<PaperRssItem> SelectPaperRssItems(List<Dictionary<string, object>> tasks, List<Dictionary<string, object>> papers, int importCount, string date, int minimumScore, int limit)
    {
        List<Dictionary<string, object>> ranked = papers
            .Where(p => JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "abstract") != null)
            .OrderByDescending(p => Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "abstract"), CultureInfo.InvariantCulture))
            .ThenByDescending(p => Convert.ToDouble(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(p, "score")), "title") ?? 0, CultureInfo.InvariantCulture))
            .Take(Clamp(importCount, 1, 20)).ToList();
        List<PaperRssItem> result = new List<PaperRssItem>();
        foreach (Dictionary<string, object> paper in ranked)
        {
            string arxiv = S(paper, "arxiv_id");
            Dictionary<string, object> score = JsonUtil.Object(JsonUtil.Get(paper, "score"));
            int abstractScore = Convert.ToInt32(JsonUtil.Get(score, "abstract"), CultureInfo.InvariantCulture);
            if (abstractScore < minimumScore) continue;
            Dictionary<string, object> task = tasks.FirstOrDefault(t =>
                !B(t, "completed") && S(t, "source").Equals("arxiv", StringComparison.OrdinalIgnoreCase) && Labels(t).Contains("论文") &&
                IsPaperTaskCreatedOnDate(t, date) && PaperTaskArxivId(t).Equals(arxiv, StringComparison.OrdinalIgnoreCase));
            if (task == null) continue;
            DateTimeOffset? created = RuntimeUtil.Date(task, "created_at");
            if (!created.HasValue) continue;
            List<string> categories = JsonUtil.Array(JsonUtil.Get(paper, "all_categories")).Select(Convert.ToString).Where(value => !String.IsNullOrWhiteSpace(value)).Distinct().ToList();
            if (categories.Count == 0) categories = JsonUtil.Array(JsonUtil.Get(paper, "category")).Select(Convert.ToString).Where(value => !String.IsNullOrWhiteSpace(value)).Distinct().ToList();
            int titleScore = Convert.ToInt32(JsonUtil.Get(score, "title") ?? 0, CultureInfo.InvariantCulture);
            string originalTitle = S(paper, "title").Trim();
            List<string> authors = S(paper, "authors").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value != "").ToList();
            string abstractUrl = S(paper, "abs_link").Trim();
            if (abstractUrl == "") abstractUrl = "https://arxiv.org/abs/" + arxiv;
            string pdfUrl = S(paper, "pdf_link").Trim();
            if (pdfUrl == "") pdfUrl = "https://arxiv.org/pdf/" + arxiv + ".pdf";
            string abstractText = S(paper, "abstract").Trim();
            List<string> descriptionParts = new List<string>();
            if (abstractText != "") descriptionParts.Add("<p>" + WebUtility.HtmlEncode(abstractText) + "</p>");
            if (originalTitle != "") descriptionParts.Add("<p><strong>" + WebUtility.HtmlEncode(originalTitle) + "</strong></p>");
            if (authors.Count > 0) descriptionParts.Add("<p>" + WebUtility.HtmlEncode(String.Join(", ", authors)) + "</p>");
            descriptionParts.Add("<p><strong>Score:</strong> " + abstractScore.ToString(CultureInfo.InvariantCulture) + "</p>");
            string link = S(task, "target");
            if (link == "") link = "https://arxiv.org/html/" + arxiv;
            result.Add(new PaperRssItem {
                Title = S(task, "title"), Link = link, Description = String.Join("", descriptionParts),
                OriginalTitle = originalTitle, Authors = authors, ArxivId = arxiv, TitleScore = titleScore,
                AbstractScore = abstractScore, Abstract = abstractText, AbstractUrl = abstractUrl, PdfUrl = pdfUrl,
                Score = abstractScore, Published = created.Value, Categories = categories
            });
            if (result.Count >= limit) break;
        }
        return result;
    }

    private static string PaperTaskArxivId(Dictionary<string, object> task)
    {
        Match target = Regex.Match(S(task, "target"), @"/(\d{4}\.\d{4,5})(?:v\d+)?(?:[/?#].*)?$", RegexOptions.IgnoreCase);
        if (target.Success) return target.Groups[1].Value;
        Match note = Regex.Match(S(task, "note"), @"arXiv ID[：:]\s*(\d{4}\.\d{4,5})", RegexOptions.IgnoreCase);
        return note.Success ? note.Groups[1].Value : "";
    }

    private static string BuildPaperRssXml(List<PaperRssItem> items)
    {
        using (MemoryStream buffer = new MemoryStream())
        {
            XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, OmitXmlDeclaration = false };
            using (XmlWriter writer = XmlWriter.Create(buffer, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("rss");
                writer.WriteAttributeString("version", "2.0");
                writer.WriteAttributeString("xmlns", "paper", null, PaperRssNamespace);
                writer.WriteAttributeString("xmlns", "dc", null, DublinCoreNamespace);
                writer.WriteStartElement("channel");
                writer.WriteElementString("title", "今日论文推荐");
                writer.WriteElementString("link", PaperRssBaseUrl + "/paper/rss");
                writer.WriteElementString("description", "长昼待办 · 今天的 arxiv 论文推荐");
                if (items.Count > 0) writer.WriteElementString("pubDate", FormatRssDate(items.Max(item => item.Published)));
                foreach (PaperRssItem item in items)
                {
                    writer.WriteStartElement("item");
                    writer.WriteElementString("title", item.Title ?? "");
                    writer.WriteElementString("link", item.Link ?? "");
                    writer.WriteStartElement("guid"); writer.WriteAttributeString("isPermaLink", "true"); writer.WriteString(item.Link ?? ""); writer.WriteEndElement();
                    writer.WriteElementString("paper", "originalTitle", PaperRssNamespace, item.OriginalTitle ?? "");
                    foreach (string author in item.Authors) writer.WriteElementString("paper", "author", PaperRssNamespace, author);
                    writer.WriteElementString("paper", "arxivId", PaperRssNamespace, item.ArxivId ?? "");
                    writer.WriteElementString("paper", "titleScore", PaperRssNamespace, item.TitleScore.ToString(CultureInfo.InvariantCulture));
                    writer.WriteElementString("paper", "abstractScore", PaperRssNamespace, item.AbstractScore.ToString(CultureInfo.InvariantCulture));
                    writer.WriteStartElement("paper", "abstract", PaperRssNamespace); WriteSafeCData(writer, item.Abstract); writer.WriteEndElement();
                    writer.WriteElementString("paper", "abstractUrl", PaperRssNamespace, item.AbstractUrl ?? "");
                    writer.WriteElementString("paper", "pdfUrl", PaperRssNamespace, item.PdfUrl ?? "");
                    writer.WriteStartElement("description"); WriteSafeCData(writer, item.Description); writer.WriteEndElement();
                    writer.WriteElementString("pubDate", FormatRssDate(item.Published));
                    writer.WriteElementString("category", "论文");
                    foreach (string category in item.Categories) writer.WriteElementString("category", category);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private static void WriteSafeCData(XmlWriter writer, string value)
    {
        string remaining = value ?? "";
        int marker;
        while ((marker = remaining.IndexOf("]]>", StringComparison.Ordinal)) >= 0)
        {
            writer.WriteCData(remaining.Substring(0, marker + 2));
            remaining = remaining.Substring(marker + 2);
        }
        writer.WriteCData(remaining);
    }

    private static string FormatRssDate(DateTimeOffset value)
    {
        string text = value.ToLocalTime().ToString("ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.GetCultureInfo("en-US"));
        return Regex.Replace(text, @"([+-]\d{2}):(\d{2})$", "$1$2");
    }

    private static void WritePaperRssResponse(Stream stream, int status, string contentType, string body)
    {
        byte[] content = Encoding.UTF8.GetBytes(body ?? "");
        string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : "Internal Server Error";
        string headers = "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + content.Length + "\r\nCache-Control: no-store, max-age=0\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(content, 0, content.Length);
        stream.Flush();
    }

    private static int RunPaperRssSelfTests()
    {
        try
        {
            string date = "2026-08-05";
            List<Dictionary<string, object>> papers = new List<Dictionary<string, object>> {
                new Dictionary<string, object>{{"arxiv_id","2608.00001"},{"title","A & B"},{"authors","Alice, Bob"},{"abstract","x < y & z ]]> tail"},{"pdf_link","https://arxiv.org/pdf/2608.00001.pdf"},{"abs_link","https://arxiv.org/abs/2608.00001"},{"all_categories",new List<object>{"cs.CV","cs.AI"}},{"score",new Dictionary<string,object>{{"title",8},{"abstract",44}}}},
                new Dictionary<string, object>{{"arxiv_id","2608.00002"},{"title","Other"},{"abstract","Other abstract"},{"score",new Dictionary<string,object>{{"title",7},{"abstract",38}}}}
            };
            List<Dictionary<string, object>> tasks = new List<Dictionary<string, object>> {
                new Dictionary<string, object>{{"title","(44) A & B < C"},{"target","https://arxiv.org/html/2608.00001"},{"note","note & <tag>"},{"labels",new List<object>{"论文"}},{"completed",false},{"source","arxiv"},{"created_at","2026-08-05T13:38:47+08:00"}},
                new Dictionary<string, object>{{"title","(38) Other"},{"target","https://arxiv.org/html/2608.00002"},{"note","other"},{"labels",new List<object>{"论文"}},{"completed",true},{"source","arxiv"},{"created_at","2026-08-05T13:39:00+08:00"}}
            };
            List<PaperRssItem> selected = SelectPaperRssItems(tasks, papers, 5, date, 40, 10);
            if (selected.Count != 1 || selected[0].Score != 44) return 61;
            string xml = BuildPaperRssXml(selected);
            XmlDocument document = new XmlDocument(); document.LoadXml(xml);
            XmlNamespaceManager namespaces = new XmlNamespaceManager(document.NameTable); namespaces.AddNamespace("paper", PaperRssNamespace);
            if (document.DocumentElement.GetAttribute("xmlns") != "" || document.DocumentElement.GetAttribute("xmlns:paper") != PaperRssNamespace || document.DocumentElement.GetAttribute("xmlns:dc") != DublinCoreNamespace) return 62;
            if (document.SelectNodes("/rss/channel/item").Count != 1) return 62;
            if (document.SelectSingleNode("/rss/channel/item/title").InnerText != "(44) A & B < C") return 63;
            string description = document.SelectSingleNode("/rss/channel/item/description").InnerText;
            if (!description.StartsWith("<p>x &lt; y &amp; z ]]&gt; tail</p>")) return 64;
            if (!description.Contains("<p><strong>A &amp; B</strong></p><p>Alice, Bob</p><p><strong>Score:</strong> 44</p>")) return 65;
            if (description.Contains("arXiv ID:") || description.Contains("abstract score:")) return 66;
            if (document.SelectSingleNode("/rss/channel/item/paper:originalTitle", namespaces).InnerText != "A & B") return 74;
            XmlNodeList authorNodes = document.SelectNodes("/rss/channel/item/paper:author", namespaces);
            if (authorNodes.Count != 2 || authorNodes[0].InnerText != "Alice" || authorNodes[1].InnerText != "Bob") return 75;
            if (document.SelectSingleNode("/rss/channel/item/paper:arxivId", namespaces).InnerText != "2608.00001") return 76;
            if (document.SelectSingleNode("/rss/channel/item/paper:titleScore", namespaces).InnerText != "8" || document.SelectSingleNode("/rss/channel/item/paper:abstractScore", namespaces).InnerText != "44") return 77;
            if (document.SelectSingleNode("/rss/channel/item/paper:abstract", namespaces).InnerText != "x < y & z ]]> tail") return 78;
            if (document.SelectSingleNode("/rss/channel/item/paper:abstractUrl", namespaces).InnerText != "https://arxiv.org/abs/2608.00001" || document.SelectSingleNode("/rss/channel/item/paper:pdfUrl", namespaces).InnerText != "https://arxiv.org/pdf/2608.00001.pdf") return 79;
            if (document.SelectNodes("/rss/channel/item/category").Count != 3) return 67;
            if (document.SelectSingleNode("/rss/channel/description").InnerText != "长昼待办 · 今天的 arxiv 论文推荐") return 68;
            if (document.SelectSingleNode("/rss/channel/item/pubDate").InnerText != "Wed, 05 Aug 2026 13:38:47 +0800") return 69;
            tasks[0]["completed"] = true;
            if (SelectPaperRssItems(tasks, papers, 5, date, 0, 10).Count != 0) return 70;
            string empty = BuildPaperRssXml(new List<PaperRssItem>());
            document.LoadXml(empty);
            if (document.SelectNodes("/rss/channel/item").Count != 0 || document.SelectSingleNode("/rss/channel") == null) return 71;
            if (IsPaperRssSyncWindow(new DateTime(2026, 8, 5, 7, 59, 59)) || !IsPaperRssSyncWindow(new DateTime(2026, 8, 5, 8, 0, 0)) || IsPaperRssSyncWindow(new DateTime(2026, 8, 5, 20, 0, 0))) return 72;
            return 0;
        }
        catch { return 73; }
    }
}
