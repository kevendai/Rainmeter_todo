using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RainmeterBackend;

// v2.1 官方 Provider：DeepSeek AI（规格 docs/V2.1-PROVIDER-INTERFACE.md §7.2）。
//
// 职责边界（规格原文）：**只负责模型调用** —— endpoint / key / model / timeout / retry /
// max concurrency / 请求与返回格式 / 厂商错误解析。
// 它**不知道**什么是 arXiv、不知道 prompt 的业务含义、不知道"两阶段评分"规则；
// 标题批量 → 阈值筛选 → 摘要 → 排序 → Top-N 全部留在消费方（arxiv）。
//
// 错误语义是从 2.0.4 的本体「搬」进来的，不是重写：
//   · 401/402/403（以及"没配 API Key"）→ ok:false + payload.fatal=true + 可读中文。
//     Broker 会把它翻成 error_kind=provider_error + fatal:true，消费方据此**立即中止整轮**，
//     不再对每批白跑一遍（2.0.4 之前 33 批 × 重试 ≈ 1400 次注定失败的请求）。
//   · 429/500/503 → 内部有限重试（2s/4s/8s + 抖动，共 3 次），与本体 DeepSeekRequest 一致。
//   · AggregateException 拍平后取消息，避免界面只剩「发生一个或多个错误。」。
//
// 与 paper-snapshot-sync 的关键差异：那个连**局域网**服务所以 request.Proxy=null；
// 本插件连**公网** api.deepseek.com，**必须沿用系统代理**，因此不动 Proxy。
internal static class AiDeepSeekPlugin
{
    private const string PluginVersion="1.0.0";
    private const string DefaultApiUrl="https://api.deepseek.com/chat/completions";
    private const string DefaultModel="deepseek-v4-flash";
    private const int DefaultTimeoutSeconds=180;
    private const int DefaultMaxConcurrency=8;
    private const int MaxMessages=64;
    // 内部重试节奏：与 2.0.4 的 TodoPaperService.DeepSeekRequest 完全相同。
    private static readonly int[] RetryDelays={2000,4000,8000};

    private static int Main(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;
        if(args.Length>0&&args[0]=="AiProviderSelfTest")return RunSelfTests();
        string requestId="";
        try
        {
            Dictionary<string,object> request=JsonUtil.Object(JsonUtil.Deserialize(Console.In.ReadLine()??""));
            requestId=JsonUtil.String(request,"request_id","");
            string action=JsonUtil.String(request,"action","");
            if(action!="structured_complete"&&action!="test_connection"&&action!="validate_settings")
                return Fail(requestId,"不支持的 action",false);
            Dictionary<string,object> config=JsonUtil.Object(JsonUtil.Get(request,"config"));
            Dictionary<string,object> secret=JsonUtil.Object(JsonUtil.Get(request,"secret"));
            Dictionary<string,object> input=JsonUtil.Object(JsonUtil.Get(request,"input"));
            if(action=="structured_complete")return StructuredComplete(requestId,config,secret,input);
            if(action=="test_connection")return TestConnection(requestId,config,secret);
            return ValidateSettings(requestId,config);
        }
        catch(Exception ex)
        {
            Exception inner=Unwrap(ex);
            Failure failure=inner as Failure;
            return Fail(requestId,Describe(inner),failure!=null&&failure.Fatal);
        }
    }

    // 失败时的 payload 里带 fatal，Broker 读 payload.fatal 决定消费方是否立即中止整轮（§4.3）。
    private static int Emit(string id,bool ok,object payload,string error)
    {
        Console.Out.WriteLine(JsonUtil.Serialize(new Dictionary<string,object>{
            {"type","result"},{"request_id",id},{"ok",ok},{"status","ok"},
            {"payload",payload??new Dictionary<string,object>()},{"error",error??""}}));
        return ok?0:1;
    }

    private static int Fail(string id,string message,bool fatal)
    {
        return Emit(id,false,new Dictionary<string,object>{{"fatal",fatal}},message);
    }

    // ── structured_complete：唯一真正做事的 action ────────────────────────────
    // input {messages:[{role,content}], response_schema:{…}, purpose:"arxiv_title_scoring"}
    // output {json:{…}, usage:{prompt_tokens,completion_tokens}}
    private static int StructuredComplete(string requestId,Dictionary<string,object> config,Dictionary<string,object> secret,Dictionary<string,object> input)
    {
        Client client=BuildClient(config,secret);
        List<Dictionary<string,object>> messages=ReadMessages(input);
        string purpose=ReadPurpose(input);
        Dictionary<string,object> schema=JsonUtil.Object(JsonUtil.Get(input,"response_schema"));
        RequireApiKey(client);
        Dictionary<string,object> completion=ParseCompletion(Post(client,BuildBody(client,messages,purpose)));
        CheckSchema(JsonUtil.Object(JsonUtil.Get(completion,"json")),schema);
        return Emit(requestId,true,completion,null);
    }

    // ── test_connection：设置页的 [测试连接] 按钮 ─────────────────────────────
    private static int TestConnection(string requestId,Dictionary<string,object> config,Dictionary<string,object> secret)
    {
        Client client=BuildClient(config,secret);
        RequireApiKey(client);
        List<Dictionary<string,object>> messages=new List<Dictionary<string,object>>();
        messages.Add(new Dictionary<string,object>{{"role","system"},{"content","You reply with JSON only."}});
        messages.Add(new Dictionary<string,object>{{"role","user"},{"content","Reply with exactly {\"pong\": true}."}});
        Dictionary<string,object> completion=ParseCompletion(Post(client,BuildBody(client,messages,"connection_test")));
        return Emit(requestId,true,new Dictionary<string,object>{
            {"message","模型可用"},
            {"model",client.Model},
            {"reply",JsonUtil.Object(JsonUtil.Get(completion,"json"))},
            {"usage",JsonUtil.Object(JsonUtil.Get(completion,"usage"))},
            {"limits",Limits(client)}},null);
    }

    // ── validate_settings：宿主保存设置后会调用它，必须能 exit 0 ──────────────
    // 刻意**不**要求 API Key 已填：用户可能想先改模型再填 Key；缺 Key 由实际调用时报 fatal。
    private static int ValidateSettings(string requestId,Dictionary<string,object> config)
    {
        Client client=BuildClient(config,new Dictionary<string,object>());
        Uri uri;
        if(!Uri.TryCreate(client.ApiUrl,UriKind.Absolute,out uri)||(uri.Scheme!="http"&&uri.Scheme!="https"))
            throw new Failure("API 地址必须是 http(s) URL，实际为「"+client.ApiUrl+"」",false);
        return Emit(requestId,true,new Dictionary<string,object>{
            {"message","设置有效"},
            {"api_url",client.ApiUrl},
            {"model",client.Model},
            {"limits",Limits(client)}},null);
    }

    // ── 输入校验 ─────────────────────────────────────────────────────────────
    private static List<Dictionary<string,object>> ReadMessages(Dictionary<string,object> input)
    {
        List<object> raw=JsonUtil.Array(JsonUtil.Get(input,"messages"));
        if(raw.Count==0)throw new Failure("input.messages 必须是非空数组",false);
        if(raw.Count>MaxMessages)throw new Failure("input.messages 超过 "+MaxMessages.ToString(CultureInfo.InvariantCulture)+" 条上限",false);
        List<Dictionary<string,object>> messages=new List<Dictionary<string,object>>();
        foreach(object item in raw)
        {
            Dictionary<string,object> message=item as Dictionary<string,object>;
            if(message==null)
                throw new Failure("input.messages 的每一项都必须是 {role,content} 对象",false);
            string role=JsonUtil.String(message,"role","").Trim();
            if(role!="system"&&role!="user"&&role!="assistant")
                throw new Failure("input.messages[].role 只能是 system/user/assistant，实际为「"+role+"」",false);
            string content=JsonUtil.String(message,"content","");
            if(content.Trim()=="")
                throw new Failure("input.messages[].content 不能为空",false);
            messages.Add(new Dictionary<string,object>{{"role",role},{"content",content}});
        }
        return messages;
    }

    // purpose 只是日志标签，本插件不解释它；限制成机器可读的形状，避免被塞进任意长文本。
    private static string ReadPurpose(Dictionary<string,object> input)
    {
        string purpose=JsonUtil.String(input,"purpose","").Trim();
        if(purpose=="")throw new Failure("input.purpose 不能为空",false);
        if(!Regex.IsMatch(purpose,@"^[a-z0-9_]{1,64}$"))
            throw new Failure("input.purpose 只能是小写字母/数字/下划线且不超过 64 字符，实际为「"+purpose+"」",false);
        return purpose;
    }

    private static void RequireApiKey(Client client)
    {
        if(client.ApiKey=="")
            throw new Failure("请先在「DeepSeek AI」插件设置里填写 API Key",true);
    }

    // response_schema 只做格式级检查：声明了顶层 required 就必须都有，不看 properties 的业务含义。
    private static void CheckSchema(Dictionary<string,object> json,Dictionary<string,object> schema)
    {
        if(schema==null||schema.Count==0)return;
        List<string> missing=new List<string>();
        foreach(object name in JsonUtil.Array(JsonUtil.Get(schema,"required")))
        {
            string key=Convert.ToString(name,CultureInfo.InvariantCulture);
            if(String.IsNullOrEmpty(key))continue;
            if(JsonUtil.Get(json,key)==null)missing.Add(key);
        }
        if(missing.Count>0)
            throw new Failure("DeepSeek 返回的 JSON 缺少 response_schema 要求的字段："+String.Join("、",missing.ToArray()),false);
    }

    private sealed class Failure:Exception
    {
        public bool Fatal;
        public Failure(string message,bool fatal):base(message){Fatal=fatal;}
    }

    // ── 配置组装 ─────────────────────────────────────────────────────────────
    private sealed class Client
    {
        public string ApiUrl="",ApiKey="",Model="";
        public int TimeoutSeconds=DefaultTimeoutSeconds,MaxConcurrency=DefaultMaxConcurrency;
    }

    // 键名刻意与 arxiv 的旧字段同名（api_url / api_key / api_model / timeout_seconds /
    // max_concurrency），这样 §9.1/§9.2 的迁移就是一次直拷。
    private static Client BuildClient(Dictionary<string,object> config,Dictionary<string,object> secret)
    {
        Client client=new Client();
        // 宿主传下来的 config 已经展开过一次 {{plugin:...}}；这里再走一遍是幂等的。
        string url=DynamicPluginValues.Resolve(JsonUtil.String(config,"api_url",DefaultApiUrl)).Trim();
        client.ApiUrl=NormalizeHttpUrl(url==""?DefaultApiUrl:url);
        client.Model=JsonUtil.String(config,"api_model",DefaultModel).Trim();
        if(client.Model=="")client.Model=DefaultModel;
        client.ApiKey=JsonUtil.String(secret,"api_key","").Trim();
        client.TimeoutSeconds=Clamp(JsonUtil.Int(config,"timeout_seconds",DefaultTimeoutSeconds),10,1800);
        client.MaxConcurrency=Clamp(JsonUtil.Int(config,"max_concurrency",DefaultMaxConcurrency),1,32);
        return client;
    }

    // 消费方开并发批次前读一次即可拿到——provider 拥有 max_concurrency，但批次是消费方开的。
    private static Dictionary<string,object> Limits(Client client)
    {
        return new Dictionary<string,object>{
            {"max_concurrency",client.MaxConcurrency},
            {"timeout_seconds",client.TimeoutSeconds}};
    }

    private static int Clamp(int value,int minimum,int maximum)
    {
        if(value<minimum)return minimum;
        if(value>maximum)return maximum;
        return value;
    }

    private static string NormalizeHttpUrl(string value)
    {
        value=(value??"").Trim();
        if(value=="")return "";
        if(!value.StartsWith("http://",StringComparison.OrdinalIgnoreCase)&&!value.StartsWith("https://",StringComparison.OrdinalIgnoreCase))
            value="https://"+value;
        return value.TrimEnd('/');
    }

    // 回环 / RFC1918 内网 / IPv6 link-local 一律直连（不走系统代理）。
    private static bool IsLocalEndpoint(Uri uri)
    {
        if(uri==null)return false;
        if(uri.IsLoopback)return true;
        IPAddress address;
        if(!IPAddress.TryParse(uri.Host,out address))return false;
        if(IPAddress.IsLoopback(address))return true;
        if(address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal||address.IsIPv6SiteLocal;
        byte[] bytes=address.GetAddressBytes();
        if(bytes.Length!=4)return false;
        if(bytes[0]==10)return true;
        if(bytes[0]==192&&bytes[1]==168)return true;
        if(bytes[0]==172&&bytes[1]>=16&&bytes[1]<=31)return true;
        return false;
    }

    // ── 请求体与 HTTP ────────────────────────────────────────────────────────
    // thinking / response_format 与 2.0.4 保持一致（搬进来，不改行为）。
    private static string BuildBody(Client client,List<Dictionary<string,object>> messages,string purpose)
    {
        List<object> wire=new List<object>();
        foreach(Dictionary<string,object> message in messages)wire.Add(message);
        Dictionary<string,object> body=new Dictionary<string,object>();
        body["model"]=client.Model;
        body["messages"]=wire;
        body["thinking"]=new Dictionary<string,object>{{"type","enabled"}};
        body["response_format"]=new Dictionary<string,object>{{"type","json_object"}};
        body["stream"]=false;
        return JsonUtil.Serialize(body);
    }

    private sealed class HttpFailure:Exception
    {
        public int StatusCode;
        public string Body;
        public HttpFailure(int statusCode,string body):base("HTTP "+statusCode.ToString(CultureInfo.InvariantCulture)+" "+body){StatusCode=statusCode;Body=body;}
    }

    // 429/5xx 才重试（与本体一致）；401/402/403 立刻上抛成 fatal，重试没有意义。
    private static string Post(Client client,string body)
    {
        for(int attempt=0;;attempt++)
        {
            try{return HttpPost(client,body);}
            catch(HttpFailure ex)
            {
                if(ex.StatusCode==401||ex.StatusCode==402||ex.StatusCode==403)
                    throw new Failure(DescribeStatus(ex.StatusCode,ex.Body),true);
                bool transient=ex.StatusCode==429||ex.StatusCode==500||ex.StatusCode==503;
                if(!transient||attempt>=RetryDelays.Length)
                    throw new Failure(DescribeStatus(ex.StatusCode,ex.Body),false);
                Thread.Sleep(RetryDelays[attempt]+new Random(unchecked(Environment.TickCount*31+Thread.CurrentThread.ManagedThreadId)).Next(200,900));
            }
        }
    }

    private static string HttpPost(Client client,string body)
    {
        ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
        HttpWebRequest request=(HttpWebRequest)WebRequest.Create(client.ApiUrl);
        request.Method="POST";
        request.Timeout=client.TimeoutSeconds*1000;
        request.ReadWriteTimeout=client.TimeoutSeconds*1000;
        request.KeepAlive=true;
        // 公网 API 沿用系统代理；但回环/内网地址必须直连，否则会被代理客户端劫持
        // （与本项目 paper-snapshot-sync 的 Proxy=null 同一个理由，只是这里要分情况）。
        // 这也让"把 api_url 指向局域网里的 OpenAI 兼容服务"这种用法能正常工作。
        if(IsLocalEndpoint(request.Address))request.Proxy=null;
        request.UserAgent="RainmeterDesktopWidgets/"+PluginVersion;
        request.Accept="application/json";
        request.Headers["Authorization"]="Bearer "+client.ApiKey;
        byte[] bytes=Encoding.UTF8.GetBytes(body);
        request.ContentType="application/json; charset=utf-8";
        request.ContentLength=bytes.Length;
        using(Stream stream=request.GetRequestStream())stream.Write(bytes,0,bytes.Length);
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
            throw new HttpFailure(code,message);
        }
    }

    // ── 返回解析 ─────────────────────────────────────────────────────────────
    private static Dictionary<string,object> ParseCompletion(string raw)
    {
        Dictionary<string,object> root=JsonUtil.Object(JsonUtil.Deserialize(raw));
        if(root.Count==0)throw new Failure("DeepSeek 返回的不是 JSON 对象",false);
        List<object> choices=JsonUtil.Array(JsonUtil.Get(root,"choices"));
        if(choices.Count==0)throw new Failure("DeepSeek 未返回 choices",false);
        Dictionary<string,object> message=JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(choices[0]),"message"));
        string content=JsonUtil.String(message,"content","").Trim();
        if(content=="")throw new Failure("DeepSeek 返回的 message.content 为空",false);
        Dictionary<string,object> json;
        try{json=JsonUtil.Object(JsonUtil.Deserialize(UnwrapJsonFence(content)));}
        catch(Exception ex){throw new Failure("DeepSeek 返回的 content 不是合法 JSON："+SafeText(ex.Message),false);}
        if(json.Count==0)throw new Failure("DeepSeek 返回的 content 不是 JSON 对象",false);
        return new Dictionary<string,object>{{"json",json},{"usage",ReadUsage(root)}};
    }

    private static Dictionary<string,object> ReadUsage(Dictionary<string,object> root)
    {
        Dictionary<string,object> usage=JsonUtil.Object(JsonUtil.Get(root,"usage"));
        return new Dictionary<string,object>{
            {"prompt_tokens",JsonUtil.Int(usage,"prompt_tokens",0)},
            {"completion_tokens",JsonUtil.Int(usage,"completion_tokens",0)}};
    }

    // 格式层容错：模型偶尔仍会套 ```json 代码块（响应格式是提示而非保证）。
    private static string UnwrapJsonFence(string value)
    {
        string text=(value??"").Trim();
        if(!text.StartsWith("```",StringComparison.Ordinal))return text;
        int firstBreak=text.IndexOf('\n');
        if(firstBreak<0)return text;
        int lastFence=text.LastIndexOf("```",StringComparison.Ordinal);
        if(lastFence<=firstBreak)return text;
        return text.Substring(firstBreak+1,lastFence-firstBreak-1).Trim();
    }

    // ── 错误文案（照搬 2.0.4 的中文口径）───────────────────────────────────
    private static string DescribeStatus(int statusCode,string body)
    {
        if(statusCode==402)return "DeepSeek 账户余额不足（HTTP 402），请充值后重新同步论文";
        if(statusCode==401)return "DeepSeek API Key 无效或已被撤销（HTTP 401），请在「DeepSeek AI」插件设置里检查 API Key";
        if(statusCode==403)return "DeepSeek 拒绝了本次请求（HTTP 403），请检查账号权限或所在地区限制";
        return "DeepSeek 请求失败（HTTP "+statusCode.ToString(CultureInfo.InvariantCulture)+"）："+SafeText(body);
    }

    private static string Describe(Exception ex)
    {
        Failure failure=ex as Failure;
        if(failure!=null)return failure.Message;
        HttpFailure http=ex as HttpFailure;
        if(http!=null)return DescribeStatus(http.StatusCode,http.Body);
        WebException web=ex as WebException;
        if(web!=null)return "DeepSeek 连接失败："+SafeText(web.Message);
        return SafeText(ex==null?"":ex.Message);
    }

    // Parallel/TPL 会把内层异常包成 AggregateException，直接取 Message 只剩
    // 「发生一个或多个错误。」⇒ 先拍平再挑最有信息量的那个（照搬 2.0.4 的 UnwrapAggregate）。
    private static Exception Unwrap(Exception ex)
    {
        AggregateException aggregate=ex as AggregateException;
        if(aggregate==null)return ex;
        List<Exception> flat=new List<Exception>(aggregate.Flatten().InnerExceptions);
        foreach(Exception item in flat){if(item is Failure)return item;}
        foreach(Exception item in flat){if(item is HttpFailure)return item;}
        return flat.Count>0?flat[0]:ex;
    }

    private static string SafeText(string value)
    {
        value=(value??"").Replace("\r"," ").Replace("\n"," ").Trim();
        return value.Length>200?value.Substring(0,200):value;
    }

    // ── 离线自检（不联网；退出码 70 起）──────────────────────────────────────
    private static int RunSelfTests()
    {
        // 70：请求体按 2.0.4 的形态构造（model / messages / thinking / response_format / stream）。
        Client client=BuildClient(new Dictionary<string,object>(),new Dictionary<string,object>());
        if(client.ApiUrl!=DefaultApiUrl||client.Model!=DefaultModel
            ||client.TimeoutSeconds!=DefaultTimeoutSeconds||client.MaxConcurrency!=DefaultMaxConcurrency)return 70;
        List<Dictionary<string,object>> messages=new List<Dictionary<string,object>>();
        messages.Add(new Dictionary<string,object>{{"role","system"},{"content","sys"}});
        messages.Add(new Dictionary<string,object>{{"role","user"},{"content","user"}});
        Dictionary<string,object> body=JsonUtil.Object(JsonUtil.Deserialize(BuildBody(client,messages,"arxiv_title_scoring")));
        if(JsonUtil.String(body,"model","")!=DefaultModel||JsonUtil.Array(JsonUtil.Get(body,"messages")).Count!=2
            ||JsonUtil.Bool(body,"stream",true)
            ||JsonUtil.String(JsonUtil.Object(JsonUtil.Get(body,"thinking")),"type","")!="enabled"
            ||JsonUtil.String(JsonUtil.Object(JsonUtil.Get(body,"response_format")),"type","")!="json_object")return 70;
        if(JsonUtil.String(JsonUtil.Object(JsonUtil.Array(JsonUtil.Get(body,"messages"))[0]),"role","")!="system")return 70;

        // 71：messages 校验必须拒掉空数组 / 坏结构 / 坏 role / 空 content / 超量。
        if(!Throws(delegate{ReadMessages(new Dictionary<string,object>());}))return 71;
        if(!Throws(delegate{ReadMessages(new Dictionary<string,object>{{"messages",new List<object>{"plain"}}});}))return 71;
        if(!Throws(delegate{ReadMessages(Message("tool","x"));}))return 71;
        if(!Throws(delegate{ReadMessages(Message("user","   "));}))return 71;
        List<object> tooMany=new List<object>();
        for(int i=0;i<MaxMessages+1;i++)tooMany.Add(MessageItem("user","x"));
        if(!Throws(delegate{ReadMessages(new Dictionary<string,object>{{"messages",tooMany}});}))return 71;
        if(ReadMessages(Message("assistant","ok")).Count!=1)return 71;

        // 72：purpose 形状校验（空 / 大写 / 超长都拒）。
        if(!Throws(delegate{ReadPurpose(new Dictionary<string,object>());}))return 72;
        if(!Throws(delegate{ReadPurpose(new Dictionary<string,object>{{"purpose","Arxiv_Title"}});}))return 72;
        if(ReadPurpose(new Dictionary<string,object>{{"purpose","arxiv_title_scoring"}})!="arxiv_title_scoring")return 72;

        // 73：未配置 API Key 必须是 fatal（消费方要立刻中止整轮，不能对每批白跑）。
        Client noKey=new Client();
        try{RequireApiKey(noKey);return 73;}
        catch(Failure failure){if(!failure.Fatal)return 73;}

        // 74：401/402/403 的中文文案；其他状态码走通用文案（不得误报成余额不足）。
        if(DescribeStatus(402,"x").IndexOf("余额不足",StringComparison.Ordinal)<0)return 74;
        if(DescribeStatus(401,"x").IndexOf("API Key",StringComparison.Ordinal)<0)return 74;
        if(DescribeStatus(403,"x").IndexOf("拒绝",StringComparison.Ordinal)<0)return 74;
        if(DescribeStatus(500,"boom").IndexOf("HTTP 500",StringComparison.Ordinal)<0)return 74;
        if(DescribeStatus(500,"boom").IndexOf("余额不足",StringComparison.Ordinal)>=0)return 74;

        // 75：usage 缺失回零；choices 缺失 / content 非 JSON / content 为空都拒。
        Dictionary<string,object> empty=ReadUsage(new Dictionary<string,object>());
        if(JsonUtil.Int(empty,"prompt_tokens",-1)!=0||JsonUtil.Int(empty,"completion_tokens",-1)!=0)return 75;
        if(JsonUtil.Int(ReadUsage(JsonUtil.Object(JsonUtil.Deserialize("{\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":22}}"))),"completion_tokens",-1)!=22)return 75;
        if(!Throws(delegate{ParseCompletion("{\"choices\":[]}");}))return 75;
        if(!Throws(delegate{ParseCompletion("{\"choices\":[{\"message\":{\"content\":\"not json\"}}]}");}))return 75;
        if(!Throws(delegate{ParseCompletion("{\"choices\":[{\"message\":{\"content\":\"\"}}]}");}))return 75;

        // 76：正常 content 解析出 json；```json 代码块要先剥壳。
        Dictionary<string,object> parsed=ParseCompletion("{\"choices\":[{\"message\":{\"content\":\"{\\\"scores\\\":{\\\"1\\\":9}}\"}}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":6}}");
        if(JsonUtil.Int(JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(parsed,"json")),"scores")),"1",-1)!=9)return 76;
        if(JsonUtil.Int(JsonUtil.Object(JsonUtil.Get(parsed,"usage")),"completion_tokens",-1)!=6)return 76;
        string fenced="{\"choices\":[{\"message\":{\"content\":\"```json\\n{\\\"scores\\\":{\\\"2\\\":7}}\\n```\"}}]}";
        if(JsonUtil.Int(JsonUtil.Object(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(ParseCompletion(fenced),"json")),"scores")),"2",-1)!=7)return 76;

        // 77：response_schema 只做 required 的格式级检查。
        Dictionary<string,object> good=new Dictionary<string,object>{{"scores",new Dictionary<string,object>()}};
        CheckSchema(good,new Dictionary<string,object>{{"required",new List<object>{"scores"}}});
        try{CheckSchema(good,new Dictionary<string,object>{{"required",new List<object>{"scores","notes"}}});return 77;}
        catch(Failure failure){if(failure.Message.IndexOf("notes",StringComparison.Ordinal)<0)return 77;}

        // 78：超时/并发夹取 + URL 规范化。
        Client clamped=BuildClient(new Dictionary<string,object>{{"timeout_seconds",5},{"max_concurrency",99}},new Dictionary<string,object>());
        if(clamped.TimeoutSeconds!=10||clamped.MaxConcurrency!=32)return 78;
        if(NormalizeHttpUrl("api.deepseek.com/chat/completions")!="https://api.deepseek.com/chat/completions")return 78;
        if(BuildClient(new Dictionary<string,object>(),new Dictionary<string,object>{{"api_key","k"}}).ApiKey!="k")return 78;

        // 79：AggregateException 拍平后应取回内层 Failure，而不是「发生一个或多个错误。」。
        Exception wrapped=Unwrap(new AggregateException(new Exception("outer"),new Failure("余额不足",true)));
        if(!(wrapped is Failure)||((Failure)wrapped).Message!="余额不足"||!((Failure)wrapped).Fatal)return 79;
        if(Describe(wrapped).IndexOf("余额不足",StringComparison.Ordinal)<0)return 79;

        // 80：只有回环/内网直连（不走系统代理），公网地址仍沿用代理。
        if(!IsLocalEndpoint(new Uri("http://127.0.0.1:1234/chat/completions")))return 80;
        if(!IsLocalEndpoint(new Uri("http://localhost:1234/chat/completions")))return 80;
        if(!IsLocalEndpoint(new Uri("http://192.168.31.4:8000/v1/chat/completions")))return 80;
        if(!IsLocalEndpoint(new Uri("http://10.150.179.74:8000/v1/chat/completions")))return 80;
        if(!IsLocalEndpoint(new Uri("http://172.16.0.9:8000/v1/chat/completions")))return 80;
        if(IsLocalEndpoint(new Uri("https://api.deepseek.com/chat/completions")))return 80;
        if(IsLocalEndpoint(new Uri("http://172.32.0.9:8000/v1")))return 80;
        if(IsLocalEndpoint(new Uri("http://203.0.113.9:8000/v1")))return 80;
        return 0;
    }

    private static Dictionary<string,object> Message(string role,string content)
    {
        return new Dictionary<string,object>{{"messages",new List<object>{MessageItem(role,content)}}};
    }

    private static object MessageItem(string role,string content)
    {
        return new Dictionary<string,object>{{"role",role},{"content",content}};
    }

    private static bool Throws(Action action){try{action();return false;}catch{return true;}}
}
