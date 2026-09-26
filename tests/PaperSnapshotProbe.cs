using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RainmeterBackend;

// Paper Snapshot Sync Provider 的全链路离线探针（规格 §7.1 / §11）。
//
// 不依赖真实文件服务器：用 TcpListener 手写一个最小 File Browser（login + resources），
// 让「consumer 请求 → Broker(-Mode ServiceCall) → paper-snapshot-sync 插件 → 假服务器」
// 整条链路真正跑起来。规格 §12：CI 一律离线，不碰任何真实凭据。
internal static class PaperSnapshotProbe
{
    private static string build, pluginHost, root;
    private static int checks, failures;
    private const string PluginId = "io.github.kevendai.paper-snapshot-sync";
    private const string Service = "paper_snapshot_provider@1";
    private const string Consumer = "io.github.test.snapshot-consumer";
    private const string Date = "2026-09-16";
    private const string RemoteName = Date + "_papers.json";
    private static FakeFileBrowser server;

    private static int Main(string[] args)
    {
        build = AppDomain.CurrentDomain.BaseDirectory;
        pluginHost = Path.Combine(build, "PluginHost.exe");
        root = Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT") ?? "";
        Expect(File.Exists(pluginHost), "PluginHost.exe 在构建目录里");
        Expect(root != "", "RAINMETER_PLUGIN_ROOT 已设置");
        if (failures > 0) return Report();
        try
        {
            using (server = new FakeFileBrowser())
            {
                server.Start();
                ManifestSection();
                SetupSection();
                BrokerSection();
                ActionSection();
                AddressSection();
            }
        }
        catch (Exception ex) { Expect(false, "探针自身不应抛异常：" + ex); }
        return Report();
    }

    // ── manifest：真实 plugin.json 按 v2.1 语义加载 ───────────────────────────
    private static void ManifestSection()
    {
        PluginManifest manifest = PluginRuntime.Resolve(PluginId, false);
        Expect(manifest.Provides.Count == 1 && manifest.Provides[0] == Service, "manifest 声明 provides=paper_snapshot_provider@1");
        Expect(manifest.Billing == "free", "billing=free");
        Expect(manifest.AddressTarget == "paper_snapshot.file_server", "address_target=paper_snapshot.file_server");
        Expect(manifest.Capabilities.Count == 0, "纯 Provider：capabilities 为空也能加载");
    }

    // ── 启用 Bootstrap 装好的插件并写入配置与凭据 ────────────────────────────
    private static void SetupSection()
    {
        WritePluginConfig(server.Url, true, true, "probe-pass");
        // Bootstrap 默认装成禁用；探针显式启用。
        string current = Path.Combine(root, "Plugins", PluginId, "current.json");
        Dictionary<string, object> state = JsonUtil.LoadObject(current);
        state["enabled"] = true;
        JsonUtil.SaveAtomic(current, state);
        Expect(JsonUtil.Bool(JsonUtil.LoadObject(current), "enabled", false), "插件已启用");
    }

    private static void WritePluginConfig(string url, bool enabled, bool autoDownload, string password)
    {
        string data = PluginPaths.DataRoot(PluginId);
        Directory.CreateDirectory(data);
        JsonUtil.SaveAtomic(Path.Combine(data, "config.json"), new Dictionary<string, object>{
            {"enabled", enabled}, {"auto_download", autoDownload}, {"file_url", url}, {"file_account", "probe"}});
        JsonUtil.WriteDpapiJson(Path.Combine(data, "secret.dat"), new Dictionary<string, object>{{"file_password", password}});
    }

    // ── Broker 全链路：get / put / 语义区分 / 输入校验 / 故障分支 ───────────────
    private static void BrokerSection()
    {
        // 1) 未命中：HTTP 404 ⇒ found:false 且 ok:true（「服务器说没有」不是错误，§7.1）。
        Dictionary<string, object> missing = Broker(GetInput(), "get_snapshot");
        Dictionary<string, object> missingOut = Output(missing);
        Expect(JsonUtil.Bool(missing, "ok", false), "get_snapshot 未命中 ⇒ ok:true，实际 error=" + JsonUtil.String(missing, "error", ""));
        Expect(!JsonUtil.Bool(missingOut, "found", true), "get_snapshot 未命中 ⇒ found:false");
        Expect(JsonUtil.String(missing, "error_kind", "") == "", "未命中不是 error_kind");

        // 2) put：存储成功，沿用日期_papers.json 命名。
        Dictionary<string, object> stored = Broker(PutInput(), "put_snapshot");
        Dictionary<string, object> storedOut = Output(stored);
        Expect(JsonUtil.Bool(stored, "ok", false) && JsonUtil.Bool(storedOut, "stored", false), "put_snapshot 成功，实际 " + JsonUtil.String(stored, "error", ""));
        Expect(JsonUtil.String(storedOut, "remote_path", "") == "paper/" + RemoteName, "remote_path=paper/<date>_papers.json，实际 " + JsonUtil.String(storedOut, "remote_path", ""));
        Expect(server.LastUploadBody.StartsWith("[", StringComparison.Ordinal) && server.LastUploadBody.Contains("2609.01234"), "上传正文是论文数组 JSON");
        Expect(server.DirectoryCreated, "首次上传前创建了远端 paper 目录");

        // 3) 命中：论文数组原样回传。
        Dictionary<string, object> hit = Broker(GetInput(), "get_snapshot");
        Dictionary<string, object> hitOut = Output(hit);
        Expect(JsonUtil.Bool(hit, "ok", false) && JsonUtil.Bool(hitOut, "found", false), "put 后 get_snapshot 命中");
        List<object> papers = JsonUtil.Array(JsonUtil.Get(hitOut, "papers"));
        Dictionary<string, object> first = papers.Count > 0 ? JsonUtil.Object(papers[0]) : new Dictionary<string, object>();
        Dictionary<string, object> scores = JsonUtil.Object(JsonUtil.Get(first, "score"));
        Expect(JsonUtil.String(first, "arxiv_id", "") == "2609.01234", "论文 id 往返无损");
        Expect(JsonUtil.Int(scores, "title", -1) == 9 && JsonUtil.Int(scores, "abstract", -1) == 46, "分段分数往返无损（title 0-10 / abstract 0-50）");

        // 4) 输入校验：日期、来源、论文数组。
        ExpectRejected("坏 date 被拒", Broker(new Dictionary<string, object>{{"source", "arxiv"}, {"date", "2026-9-16"}}, "get_snapshot"), "date");
        ExpectRejected("非 arxiv source 被拒", Broker(new Dictionary<string, object>{{"source", "ieee"}, {"date", Date}}, "get_snapshot"), "source");
        ExpectRejected("缺 papers 被拒", Broker(new Dictionary<string, object>(), "put_snapshot"), "papers");
        ExpectRejected("坏上传日期被拒", Broker(new Dictionary<string, object>{{"date", "2026-9-16"}, {"papers", JsonUtil.Get(PutInput(), "papers")}}, "put_snapshot"), "date");

        // 5) 认证失败：401 ⇒ ok:false + 可读中文（严格区别于 found:false）。
        server.Mode = "auth_fail";
        Dictionary<string, object> authFail = Broker(GetInput(), "get_snapshot");
        Expect(!JsonUtil.Bool(authFail, "ok", true), "认证失败 ⇒ ok:false");
        Expect(JsonUtil.String(authFail, "error_kind", "") == "provider_error", "认证失败 ⇒ error_kind=provider_error");
        Expect(JsonUtil.String(authFail, "error", "").IndexOf("账号或密码", StringComparison.Ordinal) >= 0, "认证失败文案可读：" + JsonUtil.String(authFail, "error", ""));

        // 6) 服务器 5xx ⇒ provider_error，不是 found:false。
        server.Mode = "server_error";
        Dictionary<string, object> serverError = Broker(GetInput(), "get_snapshot");
        Expect(!JsonUtil.Bool(serverError, "ok", true) && JsonUtil.String(serverError, "error_kind", "") == "provider_error", "服务器 5xx ⇒ provider_error");
        server.Mode = "ok";

        // 7) 损坏内容：文件名对了但不是数组 ⇒ 错误（不是「没有」）。
        server.SetFile(RemoteName, "{\"bad\":true}");
        Dictionary<string, object> corrupt = Broker(GetInput(), "get_snapshot");
        Expect(!JsonUtil.Bool(corrupt, "ok", true), "损坏快照 ⇒ ok:false");
        Expect(JsonUtil.String(corrupt, "error", "").IndexOf("论文数组", StringComparison.Ordinal) >= 0, "损坏文案写明原因：" + JsonUtil.String(corrupt, "error", ""));
        server.SetFile(RemoteName, JsonUtil.Serialize(JsonUtil.Get(PutInput(), "papers")));

        // 8) 自动下载关闭 ⇒ found:false 且不碰服务器。
        int downloadsBefore = server.Downloads;
        WritePluginConfig(server.Url, true, false, "probe-pass");
        Dictionary<string, object> disabledDownload = Broker(GetInput(), "get_snapshot");
        Expect(JsonUtil.Bool(disabledDownload, "ok", false) && !JsonUtil.Bool(Output(disabledDownload), "found", true), "自动下载关闭 ⇒ found:false");
        Expect(server.Downloads == downloadsBefore, "自动下载关闭时不发请求");

        // 9) 配置开关关闭 ⇒ get 走 found:false、put 走 stored:false（都不是错误）。
        WritePluginConfig(server.Url, false, true, "probe-pass");
        Dictionary<string, object> disabledGet = Broker(GetInput(), "get_snapshot");
        Expect(JsonUtil.Bool(disabledGet, "ok", false) && !JsonUtil.Bool(Output(disabledGet), "found", true), "插件开关关闭 ⇒ get found:false");
        Dictionary<string, object> disabledPut = Broker(PutInput(), "put_snapshot");
        Expect(JsonUtil.Bool(disabledPut, "ok", false) && !JsonUtil.Bool(Output(disabledPut), "stored", false), "插件开关关闭 ⇒ put stored:false");
        WritePluginConfig(server.Url, true, true, "probe-pass");

        // 10) 审计：service-call.log 里记了本服务的一次调用（不含 input 正文）。
        string auditPath = Path.Combine(PluginPaths.Logs, "service-call.log");
        string audit = File.Exists(auditPath) ? File.ReadAllText(auditPath, Encoding.UTF8) : "";
        Expect(audit.IndexOf(Consumer + " | " + PluginId + " | " + Service, StringComparison.Ordinal) >= 0, "审计日志记录了 consumer|provider|service");
        Expect(audit.IndexOf("2609.01234", StringComparison.Ordinal) < 0, "审计日志不含论文内容");
    }

    // ── PluginAction：设置页 [测试连接] / validate_settings ────────────────────
    private static void ActionSection()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwsnap-act-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "in.json");
        JsonUtil.SaveAtomic(input, new Dictionary<string, object>());

        string output = Path.Combine(dir, "ok.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") == 0, "test_connection 成功退出 0");
        Dictionary<string, object> result = JsonUtil.LoadObject(output);
        Expect(JsonUtil.Bool(result, "ok", false), "test_connection ok");
        Expect(JsonUtil.String(JsonUtil.Object(JsonUtil.Get(result, "payload")), "message", "").IndexOf("文件服务器连接成功", StringComparison.Ordinal) >= 0, "test_connection 文案正确");

        // 密码错误 ⇒ 非零退出 + ok:false + 可读中文。
        WritePluginConfig(server.Url, true, true, "wrong-pass");
        output = Path.Combine(dir, "bad.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") != 0, "密码错误 ⇒ 非零退出");
        result = JsonUtil.LoadObject(output);
        Expect(!JsonUtil.Bool(result, "ok", true) && JsonUtil.String(result, "error", "").IndexOf("账号或密码", StringComparison.Ordinal) >= 0, "密码错误文案可读");

        // 未启用 ⇒ 提示先启用。
        WritePluginConfig(server.Url, false, true, "probe-pass");
        output = Path.Combine(dir, "disabled.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") != 0, "未启用 ⇒ 非零退出");
        result = JsonUtil.LoadObject(output);
        Expect(JsonUtil.String(result, "error", "").IndexOf("请先启用", StringComparison.Ordinal) >= 0, "未启用文案要求先启用");
        WritePluginConfig(server.Url, true, true, "probe-pass");

        // validate_settings：ok + 地址状态自述。
        output = Path.Combine(dir, "validate.json");
        Expect(RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"") == 0, "validate_settings 成功");
        result = JsonUtil.LoadObject(output);
        Dictionary<string, object> address = JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(result, "payload")), "address"));
        Expect(!JsonUtil.Bool(address, "managed", true) && JsonUtil.String(address, "effective", "") == server.Url, "validate_settings 报告未被接管的地址");
        try { Directory.Delete(dir, true); } catch { }
    }

    // ── 地址接管：SSDP 类地址插件代管 paper_snapshot.file_server ───────────────
    private static void AddressSection()
    {
        // 用户存的地址指向一个不存在的 IP（端口是真的假服务器端口）。
        string bogus = "http://10.99.99.99:" + server.Port.ToString(CultureInfo.InvariantCulture);
        WritePluginConfig(bogus, true, true, "probe-pass");

        // 没有地址插件 ⇒ 未接管，地址原样。
        string dir = Path.Combine(Path.GetTempPath(), "rwsnap-addr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "in.json");
        JsonUtil.SaveAtomic(input, new Dictionary<string, object>());
        string output = Path.Combine(dir, "plain.json");
        RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"");
        Dictionary<string, object> address = AddressOf(output);
        Expect(!JsonUtil.Bool(address, "managed", true) && JsonUtil.String(address, "effective", "") == bogus, "没有地址插件 ⇒ 未接管");

        // 装上假地址插件并给出主机 ⇒ 被接管：只换主机，端口保留。
        WriteFakeAddressProvider(true, "127.0.0.1");
        output = Path.Combine(dir, "managed.json");
        RunHost("PluginAction " + PluginId + " validate_settings \"" + input + "\" \"" + output + "\"");
        address = AddressOf(output);
        Expect(JsonUtil.Bool(address, "managed", false), "地址插件接管 paper_snapshot.file_server");
        Expect(JsonUtil.String(address, "effective", "") == server.Url, "只换主机、端口保留：" + JsonUtil.String(address, "effective", ""));
        Expect(JsonUtil.String(address, "provider_name", "") == "Fake Address" && JsonUtil.String(address, "stored", "") == bogus, "接管者与用户原值都被记录");

        // 全链路：被接管的地址也能真实连通（10.99.99.99 本不可达）。
        output = Path.Combine(dir, "connected.json");
        Expect(RunHost("PluginAction " + PluginId + " test_connection \"" + input + "\" \"" + output + "\"") == 0, "被接管地址全链路连通");
        Dictionary<string, object> result = JsonUtil.LoadObject(output);
        Expect(JsonUtil.String(JsonUtil.Object(JsonUtil.Get(result, "payload")), "message", "").IndexOf("地址由", StringComparison.Ordinal) >= 0, "连接成功文案注明地址由地址插件提供");

        // Broker 链路在被接管地址下 get/put 依旧成功。
        Dictionary<string, object> stored = Broker(PutInput(), "put_snapshot");
        Expect(JsonUtil.Bool(stored, "ok", false) && JsonUtil.Bool(Output(stored), "stored", false), "被接管地址下 put 成功");
        Dictionary<string, object> hit = Broker(GetInput(), "get_snapshot");
        Expect(JsonUtil.Bool(hit, "ok", false) && JsonUtil.Bool(Output(hit), "found", false), "被接管地址下 get 命中");

        // 收尾：地址插件禁用 + 配置还原，不影响后续用例。
        WriteFakeAddressProvider(false, "127.0.0.1");
        WritePluginConfig(server.Url, true, true, "probe-pass");
        try { Directory.Delete(dir, true); } catch { }
    }

    private static Dictionary<string, object> AddressOf(string outputPath)
    {
        Dictionary<string, object> result = File.Exists(outputPath) ? JsonUtil.LoadObject(outputPath) : new Dictionary<string, object>();
        return JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(result, "payload")), "address"));
    }

    private static void ExpectRejected(string label, Dictionary<string, object> response, string field)
    {
        Expect(!JsonUtil.Bool(response, "ok", true), label + " ⇒ ok:false");
        Expect(JsonUtil.String(response, "error_kind", "") == "provider_error", label + " ⇒ error_kind=provider_error");
        Expect(JsonUtil.String(response, "error", "").IndexOf(field, StringComparison.Ordinal) >= 0, label + " 文案点名 " + field + "：" + JsonUtil.String(response, "error", ""));
    }

    private static Dictionary<string, object> GetInput()
    {
        return new Dictionary<string, object>{{"source", "arxiv"}, {"date", Date}};
    }

    private static Dictionary<string, object> Output(Dictionary<string, object> response)
    {
        return JsonUtil.Object(JsonUtil.Get(response, "output"));
    }

    private static Dictionary<string, object> PutInput()
    {
        Dictionary<string, object> paper = new Dictionary<string, object>{
            {"id", 1}, {"arxiv_id", "2609.01234"}, {"title", "Fake paper title"},
            {"abstract", "Fake abstract"}, {"authors", "Example Author"},
            {"abs_link", "https://arxiv.org/abs/2609.01234"},
            {"pdf_link", "https://arxiv.org/pdf/2609.01234"},
            {"score", new Dictionary<string, object>{{"title", 9}, {"abstract", 46}}}};
        return new Dictionary<string, object>{{"date", Date}, {"papers", new List<object>{paper}}};
    }

    // 以一个未安装的 consumer 身份直接调用 Broker（§4.4：caller 身份只认环境变量）。
    private static Dictionary<string, object> Broker(Dictionary<string, object> input, string action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwsnap-broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string requestPath = Path.Combine(dir, "request.json"), outputPath = Path.Combine(dir, "output.json");
        JsonUtil.SaveAtomic(requestPath, new Dictionary<string, object>{
            {"protocol", 1}, {"request_id", "snap-" + Guid.NewGuid().ToString("N").Substring(0, 8)},
            {"service", Service}, {"action", action}, {"input", input}, {"timeout_seconds", 120}});
        ProcessStartInfo info = new ProcessStartInfo(pluginHost, "-Mode ServiceCall -RequestFile \"" + requestPath + "\" -OutputFile \"" + outputPath + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        Dictionary<string, string> extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        extra["RW_PLUGIN_ID"] = Consumer; extra["RW_PLUGIN_JOB_ID"] = ""; extra["RW_PLUGIN_HOST_EXE"] = pluginHost;
        PluginRuntime.ApplyPluginEnvironment(info, null, null, extra);
        using (Process broker = Process.Start(info))
        {
            broker.StandardOutput.ReadToEnd();
            broker.StandardError.ReadToEnd();
            broker.WaitForExit();
        }
        Dictionary<string, object> response = File.Exists(outputPath) ? JsonUtil.LoadObject(outputPath) : new Dictionary<string, object>();
        try { Directory.Delete(dir, true); } catch { }
        return response;
    }

    private static int RunHost(string arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo(pluginHost, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        PluginRuntime.ApplyPluginEnvironment(info, null, null, null);
        using (Process process = Process.Start(info))
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static void WriteFakeAddressProvider(bool enabled, string ip)
    {
        string pluginRoot = Path.Combine(root, "Plugins", "io.github.test.fake-address"), versionRoot = Path.Combine(pluginRoot, "versions", "1.0.0");
        Directory.CreateDirectory(versionRoot);
        JsonUtil.SaveAtomic(Path.Combine(pluginRoot, "current.json"), new Dictionary<string, object>{{"version", "1.0.0"}, {"enabled", enabled}});
        JsonUtil.SaveAtomic(Path.Combine(versionRoot, "plugin.json"), new Dictionary<string, object>{
            {"id", "io.github.test.fake-address"}, {"name", "Fake Address"},
            {"capabilities", new List<object>{"value_provider"}},
            {"address_provider", new Dictionary<string, object>{{"priority", 100}, {"value", "server_ip"}, {"targets", new List<object>{"paper_snapshot.file_server"}}}}});
        string valuesPath = Path.Combine(root, "PluginValues.json");
        Dictionary<string, object> values = File.Exists(valuesPath) ? JsonUtil.LoadObject(valuesPath) : new Dictionary<string, object>();
        Dictionary<string, object> entries = JsonUtil.Object(JsonUtil.Get(values, "entries"));
        values["entries"] = entries;
        entries["Plugin_io_github_test_fake_address_server_ip"] = new Dictionary<string, object>{{"value", ip}};
        JsonUtil.SaveAtomic(valuesPath, values);
    }

    private static void Expect(bool condition, string label)
    {
        checks++;
        if (!condition) { failures++; Console.Out.WriteLine("FAIL: " + label); }
    }

    private static int Report()
    {
        Console.Out.WriteLine("PaperSnapshotProbe: checks=" + checks.ToString(CultureInfo.InvariantCulture) + " failures=" + failures.ToString(CultureInfo.InvariantCulture));
        Console.Out.WriteLine(failures == 0 ? "PASS paper snapshot probe" : "FAIL paper snapshot probe");
        return failures == 0 ? 0 : 1;
    }
}

// 最小 File Browser：只实现 login 与 resources 的四个端点，够驱动真实插件。
// 探针侧的请求体全部是纯 ASCII（Content-Length 按字节计算），不处理多字节边界。
internal sealed class FakeFileBrowser : IDisposable
{
    private TcpListener listener;
    private Thread accept;
    private volatile bool running = true;
    private readonly object gate = new object();
    private readonly Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool directoryCreated;

    public string Mode = "ok";   // ok | auth_fail | server_error
    public string Token = "probe-token";
    public string Username = "probe", Password = "probe-pass";
    public string LastUploadBody = "";
    public int Downloads, Uploads;

    public int Port { get; private set; }
    public string Url { get { return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture); } }
    public bool DirectoryCreated { get { lock (gate) return directoryCreated; } }

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accept = new Thread(AcceptLoop);
        accept.IsBackground = true;
        accept.Start();
    }

    public void Dispose()
    {
        running = false;
        try { listener.Stop(); } catch { }
    }

    public void SetFile(string name, string content) { lock (gate) files[name] = content; }

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); } catch { break; }
            Thread worker = new Thread(delegate() { try { Handle(client); } catch { } finally { try { client.Close(); } catch { } } });
            worker.IsBackground = true;
            worker.Start();
        }
    }

    private void Handle(TcpClient client)
    {
        client.ReceiveTimeout = 15000;
        client.SendTimeout = 15000;
        using (NetworkStream stream = client.GetStream())
        {
            string requestLine = ReadLine(stream);
            if (requestLine == null) return;
            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2) { Respond(stream, 400, "{\"error\":\"bad request\"}"); return; }
            string method = parts[0], path = parts[1].Split('?')[0];
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int contentLength = 0;
            string line;
            while ((line = ReadLine(stream)) != null && line.Length > 0)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim(), value = line.Substring(colon + 1).Trim();
                headers[name] = value;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) Int32.TryParse(value, out contentLength);
            }
            string body = contentLength > 0 ? ReadExact(stream, contentLength) : "";
            Route(stream, method, path, body, headers);
        }
    }

    private void Route(NetworkStream stream, string method, string path, string body, Dictionary<string, string> headers)
    {
        string auth;
        headers.TryGetValue("X-Auth", out auth);
        if (path == "/api/login" && method == "POST")
        {
            Dictionary<string, object> credentials = JsonUtil.Object(JsonUtil.Deserialize(body));
            if (Mode == "auth_fail" || JsonUtil.String(credentials, "username", "") != Username || JsonUtil.String(credentials, "password", "") != Password)
            { Respond(stream, 401, "{\"error\":\"wrong credentials\"}"); return; }
            Respond(stream, 200, "\"" + Token + "\"");
            return;
        }
        if (!String.Equals(auth, Token, StringComparison.Ordinal)) { Respond(stream, 401, "{\"error\":\"unauthorized\"}"); return; }
        if (path == "/api/resources/paper" && method == "GET")
        {
            bool created;
            lock (gate) created = directoryCreated;
            if (!created) { Respond(stream, 404, "{\"error\":\"not found\"}"); return; }
            Respond(stream, 200, "{}");
            return;
        }
        if (path == "/api/resources/" && method == "POST")
        {
            lock (gate) directoryCreated = true;
            Respond(stream, 200, "{}");
            return;
        }
        if (path.StartsWith("/api/resources/paper/", StringComparison.Ordinal) && path.Length > "/api/resources/paper/".Length)
        {
            string name = path.Substring("/api/resources/paper/".Length);
            if (method == "GET")
            {
                string content = null;
                lock (gate) { Downloads++; files.TryGetValue(name, out content); }
                if (Mode == "server_error") { Respond(stream, 500, "{\"error\":\"boom\"}"); return; }
                if (content == null) { Respond(stream, 404, "{\"error\":\"not found\"}"); return; }
                Respond(stream, 200, JsonUtil.Serialize(new Dictionary<string, object>{{"content", content}}));
                return;
            }
            if (method == "POST")
            {
                lock (gate) { Uploads++; directoryCreated = true; files[name] = body; LastUploadBody = body; }
                Respond(stream, 200, "{}");
                return;
            }
        }
        Respond(stream, 404, "{\"error\":\"not found\"}");
    }

    private static void Respond(NetworkStream stream, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string head = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + Reason(status)
            + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length.ToString(CultureInfo.InvariantCulture)
            + "\r\nConnection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static string Reason(int status)
    {
        switch (status)
        {
            case 200: return "OK";
            case 400: return "Bad Request";
            case 401: return "Unauthorized";
            case 404: return "Not Found";
            case 500: return "Internal Server Error";
            default: return "Status";
        }
    }

    private static string ReadLine(NetworkStream stream)
    {
        StringBuilder builder = new StringBuilder(128);
        while (true)
        {
            int current = stream.ReadByte();
            if (current < 0) return builder.Length == 0 ? null : builder.ToString();
            if (current == '\n')
            {
                string text = builder.ToString();
                return text.EndsWith("\r", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
            }
            builder.Append((char)current);
        }
    }

    private static string ReadExact(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int block = stream.Read(buffer, read, count - read);
            if (block <= 0) break;
            read += block;
        }
        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}
