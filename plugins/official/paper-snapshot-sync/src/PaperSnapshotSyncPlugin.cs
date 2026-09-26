using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RainmeterBackend;

// v2.1 官方 Provider：论文快照同步（规格 docs/V2.1-PROVIDER-INTERFACE.md §7.1）。
//
// 职责只有一件事：把现有论文数组存进文件服务器（File Browser）、按需取回来。
// 不负责 arXiv 抓取、AI 评分、prompt、翻译、Todo 导入 —— 那些分别属于 arxiv 与其他 provider。
//
// 远端布局：paper/<date>_papers.json，内容为论文数组。
//
// found:false（服务器明确回答「没有」）与 ok:false（请求失败）严格区分（§7.1）：
// HTTP 404 → found:false；401/403/5xx/超时/DNS → ok:false（Broker 记 provider_error）。
internal static class PaperSnapshotSyncPlugin
{
    // 本插件在 plugin.json 的 address_target 里声明的地址代管目标；地址插件（如 SSDP
    // 服务器 IP 同步）声明同一个 target 并已有主机值时，本插件处于「被接管」状态。
    private const string AddressTarget="paper_snapshot.file_server";
    private const string RemoteDirectory="paper";
    private const string PluginVersion="1.0.0";

    private static int Main(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;
        if(args.Length>0&&args[0]=="SnapshotSelfTest")return RunSelfTests();
        string requestId="";
        try
        {
            Dictionary<string,object> request=JsonUtil.Object(JsonUtil.Deserialize(Console.In.ReadLine()??""));
            requestId=JsonUtil.String(request,"request_id","");
            string action=JsonUtil.String(request,"action","");
            if(action!="get_snapshot"&&action!="put_snapshot"&&action!="test_connection"&&action!="validate_settings")
                return Emit(requestId,false,null,"不支持的 action");
            Dictionary<string,object> config=JsonUtil.Object(JsonUtil.Get(request,"config"));
            Dictionary<string,object> secret=JsonUtil.Object(JsonUtil.Get(request,"secret"));
            Dictionary<string,object> input=JsonUtil.Object(JsonUtil.Get(request,"input"));
            if(action=="get_snapshot")return GetSnapshot(requestId,config,secret,input);
            if(action=="put_snapshot")return PutSnapshot(requestId,config,secret,input);
            if(action=="test_connection")return TestConnection(requestId,config,secret);
            ValidateSettings(config);
            return Emit(requestId,true,new Dictionary<string,object>{{"message","设置有效"},{"address",AddressStatus(BuildServerConfig(config,secret))}},null);
        }
        catch(Exception ex){return Emit(requestId,false,null,ex.Message);}
    }

    private static int Emit(string id,bool ok,object payload,string error)
    {
        Console.Out.WriteLine(JsonUtil.Serialize(new Dictionary<string,object>{
            {"type","result"},{"request_id",id},{"ok",ok},{"status","ok"},
            {"payload",payload??new Dictionary<string,object>()},{"error",error??""}}));
        return ok?0:1;
    }

    // ── get_snapshot：input {source, date} → {found:true, papers} / {found:false} ──
    private static int GetSnapshot(string requestId,Dictionary<string,object> config,Dictionary<string,object> secret,Dictionary<string,object> input)
    {
        ServerConfig server=BuildServerConfig(config,secret);
        // 关闭开关走「没有快照」而不是错误：consumer 记 warning 后走自己的 fallback。
        if(!server.Enabled)return Emit(requestId,true,NotFound("插件未启用"),null);
        if(!server.AutoDownload)return Emit(requestId,true,NotFound("自动下载已关闭"),null);
        string source=JsonUtil.String(input,"source","").Trim();
        if(source!="arxiv")throw new InvalidDataException("input.source 仅支持 arxiv，实际为「"+source+"」");
        string date=JsonUtil.String(input,"date","").Trim();
        RequireBundleDate(date,"input.date");
        if(server.BaseUrl==""||server.Account=="")return Emit(requestId,true,NotFound("文件服务器未配置"),null);
        string token=Login(server);
        try
        {
            List<object> papers=DownloadPapers(server,token,SnapshotFileName(date));
            return Emit(requestId,true,new Dictionary<string,object>{{"found",true},{"papers",papers}},null);
        }
        catch(FileServerException ex)
        {
            if(ex.StatusCode==404)return Emit(requestId,true,NotFound(""),null);
            throw new IOException("论文文件服务器连接失败："+ex.Message,ex);
        }
    }

    // ── put_snapshot：input {date, papers:[…]} → {stored:true, remote_path} ──
    private static int PutSnapshot(string requestId,Dictionary<string,object> config,Dictionary<string,object> secret,Dictionary<string,object> input)
    {
        ServerConfig server=BuildServerConfig(config,secret);
        if(!server.Enabled)return Emit(requestId,true,new Dictionary<string,object>{{"stored",false},{"reason","插件未启用"}},null);
        List<object> papers=JsonUtil.Array(JsonUtil.Get(input,"papers"));
        if(papers.Count==0)throw new InvalidDataException("缺少 input.papers 或论文数组为空");
        string json=JsonUtil.Serialize(papers);
        if(Encoding.UTF8.GetByteCount(json)>PaperBundleValidator.MaxBundleBytes)
            throw new InvalidDataException("快照体积超过 8 MB 上限");
        // 文件名只由日期决定；论文数组的完整性由消费端校验。
        string date=JsonUtil.String(input,"date","").Trim();
        RequireBundleDate(date,"input.date");
        if(server.BaseUrl==""||server.Account=="")throw new InvalidDataException("文件服务器未配置，无法上传快照");
        string token=Login(server);
        EnsureRemoteDirectory(server,token);
        string name=SnapshotFileName(date);
        // 同一天只保留一个现有格式文件。
        HttpCall("POST",server.BaseUrl+"/api/resources/"+RemoteDirectory+"/"+name+"?override=true",json,
            new Dictionary<string,string>{{"X-Auth",token}},30000);
        return Emit(requestId,true,new Dictionary<string,object>{{"stored",true},{"remote_path",RemoteDirectory+"/"+name}},null);
    }

    // ── test_connection：设置页的 [测试连接] 按钮 ──
    private static int TestConnection(string requestId,Dictionary<string,object> config,Dictionary<string,object> secret)
    {
        ValidateSettings(config);
        ServerConfig server=BuildServerConfig(config,secret);
        if(!server.Enabled||server.BaseUrl==""||server.Account=="")throw new InvalidDataException("请先启用并填写完整的文件服务器配置");
        Login(server);
        string message=server.ManagedByName==""?"文件服务器连接成功":"文件服务器连接成功（地址由「"+server.ManagedByName+"」提供）";
        return Emit(requestId,true,new Dictionary<string,object>{{"message",message},{"address",AddressStatus(server)}},null);
    }

    private static void ValidateSettings(Dictionary<string,object> config)
    {
        string raw=JsonUtil.String(config,"file_url","").Trim();
        if(raw=="")return;
        Uri uri;
        if(!Uri.TryCreate(NormalizeHttpUrl(raw),UriKind.Absolute,out uri)
            ||(uri.Scheme!="http"&&uri.Scheme!="https"))
            throw new InvalidDataException("File Browser 地址必须是 http(s) URL");
    }

    // ── 配置组装与地址接管（与 arxiv ApplyFileAddress 同一套语义）─────────────────
    private sealed class ServerConfig
    {
        public bool Enabled,AutoDownload,Managed;
        public string BaseUrl="",Account="",Password="",ManagedBy="",ManagedByName="",StoredUrl="";
    }

    private static ServerConfig BuildServerConfig(Dictionary<string,object> config,Dictionary<string,object> secret)
    {
        ServerConfig server=new ServerConfig();
        server.Enabled=JsonUtil.Bool(config,"enabled",false);
        server.AutoDownload=JsonUtil.Bool(config,"auto_download",true);
        string stored=JsonUtil.String(config,"file_url","").Trim();
        // 宿主传来的 config 已经把 {{plugin:...}} 占位符展开过一次；这里再走一遍是幂等的。
        string source=JsonUtil.String(config,"address_source","");
        string resolved=source=="manual"?stored:DynamicPluginValues.Resolve(stored);
        // 装了声明同一 address_target 的地址插件（例如 SSDP 服务器 IP 同步）并已有主机值时，
        // 本插件就是「被接管」状态：只把地址里的主机换成插件给的主机，端口与路径原样保留。
        AddressProviderBinding provider=DynamicPluginValues.AddressProvider(AddressTarget);
        bool managed=source!="manual"&&provider!=null&&!String.IsNullOrWhiteSpace(provider.Value);
        if(managed)
        {
            string bound=DynamicPluginValues.BindForTarget(resolved,AddressTarget);
            if(!String.IsNullOrWhiteSpace(bound))resolved=bound;
            server.Managed=true;server.ManagedBy=provider.PluginId;server.ManagedByName=provider.PluginName;
            if(stored!="")server.StoredUrl=stored;
        }
        else if(source=="ssdp")resolved="";
        server.BaseUrl=NormalizeHttpUrl(resolved);
        server.Account=JsonUtil.String(config,"file_account","").Trim();
        server.Password=JsonUtil.String(secret,"file_password","");
        return server;
    }

    private static Dictionary<string,object> AddressStatus(ServerConfig server)
    {
        return new Dictionary<string,object>{
            {"managed",server.Managed},
            {"provider",server.ManagedBy},
            {"provider_name",server.ManagedByName},
            {"effective",server.BaseUrl},
            {"stored",server.StoredUrl}};
    }

    private static Dictionary<string,object> NotFound(string reason)
    {
        return new Dictionary<string,object>{{"found",false},{"reason",reason??"?"}};
    }

    // ── 命名与输入校验 ─────────────────────────────────────────────────────────
    private static string SnapshotFileName(string date)
    {
        return date+"_papers.json";
    }

    private static void RequireBundleDate(string value,string label)
    {
        DateTime parsed;
        if(value==null||value.Length!=10||!DateTime.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))
            throw new InvalidDataException(label+" 必须是 YYYY-MM-DD，实际为「"+(value??"")+"」");
    }

    private static string NormalizeHttpUrl(string value)
    {
        value=(value??"").Trim();
        if(value=="")return "";
        if(!value.StartsWith("http://",StringComparison.OrdinalIgnoreCase)&&!value.StartsWith("https://",StringComparison.OrdinalIgnoreCase))
            value="https://"+value;
        return value.TrimEnd('/');
    }

    // ── File Browser（文件服务器）HTTP 客户端 ─────────────────────────────────
    private sealed class FileServerException:Exception
    {
        public int StatusCode;
        public FileServerException(int statusCode,string message):base(message){StatusCode=statusCode;}
    }

    private static string HttpCall(string method,string url,string body,IDictionary<string,string> headers,int timeoutMs)
    {
        ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
        HttpWebRequest request=(HttpWebRequest)WebRequest.Create(url);
        request.Method=method;request.Timeout=timeoutMs;request.ReadWriteTimeout=timeoutMs;
        // File Browser 是局域网/本机服务，直连：不走系统代理（否则回环与内网地址会被
        // 代理客户端劫持，行为取决于代理配置，不可预测）。
        request.Proxy=null;
        request.UserAgent="RainmeterDesktopWidgets/"+PluginVersion;request.Accept="application/json, */*";
        if(headers!=null)foreach(KeyValuePair<string,string> header in headers)request.Headers[header.Key]=header.Value;
        if(body!=null)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(body);
            request.ContentType="application/json; charset=utf-8";
            request.ContentLength=bytes.Length;
            using(Stream stream=request.GetRequestStream())stream.Write(bytes,0,bytes.Length);
        }
        try
        {
            using(HttpWebResponse response=(HttpWebResponse)request.GetResponse())
            using(StreamReader reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))return reader.ReadToEnd();
        }
        catch(WebException ex)
        {
            HttpWebResponse response=ex.Response as HttpWebResponse;
            int code=response==null?0:(int)response.StatusCode;
            string message=ex.Message;
            if(response!=null){try{using(StreamReader reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))message=reader.ReadToEnd();}catch{}}
            throw new FileServerException(code,"HTTP "+code.ToString(CultureInfo.InvariantCulture)+" "+SafeText(message));
        }
    }

    private static string Login(ServerConfig server)
    {
        string body=JsonUtil.Serialize(new Dictionary<string,object>{{"username",server.Account},{"password",server.Password}});
        try{return HttpCall("POST",server.BaseUrl+"/api/login",body,null,10000).Trim().Trim('"');}
        catch(FileServerException ex)
        {
            if(ex.StatusCode==401||ex.StatusCode==403)
                throw new InvalidDataException("文件服务器账号或密码错误（HTTP "+ex.StatusCode.ToString(CultureInfo.InvariantCulture)+"）");
            throw new IOException("论文文件服务器连接失败："+ex.Message,ex);
        }
    }

    private static List<object> DownloadPapers(ServerConfig server,string token,string name)
    {
        string raw=HttpCall("GET",server.BaseUrl+"/api/resources/"+RemoteDirectory+"/"+name,null,
            new Dictionary<string,string>{{"X-Auth",token}},15000);
        // File Browser 把文件内容包在响应 JSON 的 content 字段里返回（字符串或对象都可能）。
        Dictionary<string,object> envelope=JsonUtil.Object(JsonUtil.Deserialize(raw));
        object content=JsonUtil.Get(envelope,"content");
        string json=content is string?(string)content:JsonUtil.Serialize(content??envelope);
        if(Encoding.UTF8.GetByteCount(json)>PaperBundleValidator.MaxBundleBytes)
            throw new InvalidDataException("远端快照体积超过 8 MB 上限");
        object parsed=JsonUtil.Deserialize(json);
        if(!(parsed is System.Collections.IList))throw new InvalidDataException("远端快照内容不是论文数组");
        List<object> papers=JsonUtil.Array(parsed);
        if(papers.Count==0)throw new InvalidDataException("远端论文数组为空");
        return papers;
    }

    private static void EnsureRemoteDirectory(ServerConfig server,string token)
    {
        try{HttpCall("GET",server.BaseUrl+"/api/resources/"+RemoteDirectory,null,new Dictionary<string,string>{{"X-Auth",token}},10000);}
        catch(FileServerException ex)
        {
            if(ex.StatusCode!=404)throw;
            HttpCall("POST",server.BaseUrl+"/api/resources/",
                JsonUtil.Serialize(new Dictionary<string,object>{{"name",RemoteDirectory},{"type","directory"}}),
                new Dictionary<string,string>{{"X-Auth",token}},10000);
        }
    }

    private static string SafeText(string value)
    {
        value=(value??"").Replace("\r"," ").Replace("\n"," ").Trim();
        return value.Length>180?value.Substring(0,180):value;
    }

    // ── 离线自检（不经网络；退出码 60 起）──────────────────────────────────────
    private static int RunSelfTests()
    {
        // 60：文件名沿用现有服务器格式。
        if(SnapshotFileName("2026-09-16")!="2026-09-16_papers.json")return 60;
        // 61：日期格式校验必须拒掉坏值。
        if(!Throws(delegate{RequireBundleDate("2026-9-16","input.date");})||!Throws(delegate{RequireBundleDate("2026-09-32","input.date");}))return 61;
        // 63：地址 URL 规范化：补 https 前缀、去尾部斜杠。
        if(NormalizeHttpUrl("192.0.2.10:8900/")!="https://192.0.2.10:8900")return 63;
        // 64-67：地址插件接管四情形（同 arxiv AddressStatusSelfTest 的 55-59）。
        string previousRoot=Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT"),isolated=Path.Combine(Path.GetTempPath(),"RainmeterSnapshotSelfTest-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(isolated);Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",isolated);
            // 64：没有地址插件 → 不接管，用户地址原样保留。
            ServerConfig plain=BuildServerConfig(PlainConfig("http://192.0.2.10:8900"),new Dictionary<string,object>());
            if(plain.BaseUrl!="http://192.0.2.10:8900"||plain.Managed)return 64;
            if(!plain.AutoDownload)return 65;
            // 66：地址插件已启用并给出主机 → 只换主机，标记被谁接管，记录用户原值。
            WriteFakeAddressProvider(isolated,true,"server_ip","203.0.113.9","Fake SSDP");
            ServerConfig managed=BuildServerConfig(PlainConfig("http://192.0.2.10:8900/files"),new Dictionary<string,object>());
            if(managed.BaseUrl!="http://203.0.113.9:8900/files"||!managed.Managed
                ||managed.ManagedBy!="io.github.test.fake-address"||managed.StoredUrl!="http://192.0.2.10:8900/files")return 66;
            Dictionary<string,object> manual=PlainConfig("http://192.0.2.10:9800/files");manual["address_source"]="manual";
            if(BuildServerConfig(manual,new Dictionary<string,object>()).BaseUrl!="http://192.0.2.10:9800/files")return 69;
            Dictionary<string,object> selected=PlainConfig("http://192.0.2.10:9800/files");selected["address_source"]="ssdp";
            if(BuildServerConfig(selected,new Dictionary<string,object>()).BaseUrl!="http://203.0.113.9:9800/files")return 70;
            // 67：地址插件已启用但还没拿到主机 / 被禁用 → 都视为未接管。
            WriteFakeAddressProvider(isolated,true,"server_ip","","Fake SSDP");
            if(BuildServerConfig(PlainConfig("http://192.0.2.10:8900"),new Dictionary<string,object>()).Managed)return 67;
            if(BuildServerConfig(selected,new Dictionary<string,object>()).BaseUrl!="")return 71;
            WriteFakeAddressProvider(isolated,false,"server_ip","203.0.113.9","Fake SSDP");
            if(BuildServerConfig(PlainConfig("http://192.0.2.10:8900"),new Dictionary<string,object>()).Managed)return 68;
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",previousRoot);
            try{Directory.Delete(isolated,true);}catch{}
        }
    }

    private static Dictionary<string,object> PlainConfig(string url)
    {
        return new Dictionary<string,object>{{"enabled",true},{"auto_download",true},{"file_url",url},{"file_account","probe"}};
    }

    private static void WriteFakeAddressProvider(string root,bool enabled,string valueKey,string ip,string name)
    {
        string pluginRoot=Path.Combine(root,"Plugins","io.github.test.fake-address"),versionRoot=Path.Combine(pluginRoot,"versions","1.0.0");
        Directory.CreateDirectory(versionRoot);
        JsonUtil.SaveAtomic(Path.Combine(pluginRoot,"current.json"),new Dictionary<string,object>{{"version","1.0.0"},{"enabled",enabled}});
        JsonUtil.SaveAtomic(Path.Combine(versionRoot,"plugin.json"),new Dictionary<string,object>{{"id","io.github.test.fake-address"},{"name",name},{"capabilities",new List<object>{"value_provider"}},{"address_provider",new Dictionary<string,object>{{"priority",100},{"value",valueKey},{"targets",new List<object>{AddressTarget}}}}});
        JsonUtil.SaveAtomic(Path.Combine(root,"PluginValues.json"),new Dictionary<string,object>{{"entries",new Dictionary<string,object>{{("Plugin_io_github_test_fake_address_"+valueKey),new Dictionary<string,object>{{"value",ip}}}}},{"providers",new Dictionary<string,object>()}});
    }

    private static bool Throws(Action action){try{action();return false;}catch{return true;}}
}
