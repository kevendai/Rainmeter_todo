using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using RainmeterBackend;

// v2.1 官方 Provider：腾讯云机器翻译 TMT（规格 docs/V2.1-PROVIDER-INTERFACE.md §7.3）。
//
// 职责边界：**只负责"把一批文本翻过去"** —— 腾讯云的 TC3 签名、接口地址、密钥、语言代码换算、
// 单次请求的长度切分、请求频率节流、厂商错误码解析、结果缓存。
// 它**不知道**什么是 arXiv、不知道这些字符串是论文标题、不知道"只在最终 Top-N 确定后才翻译"
// （那是消费方的规则）。⇒ 换掉本插件不会改变任何筛选/排序算法。
//
// 三条必须守住的契约（§7.3）：
//   ① **必须批量**：一次调用翻译一批，绝不"每篇启一次 provider"。
//      input.texts 超过单次请求长度时，切分发生在**插件内部**，消费方仍然只看到一次调用。
//   ② `translations.length` 必须 == `texts.length`，否则是**协议错误**：
//      长度不符时**整批结果丢弃**并报错，绝不返回一个"短一截"的数组让消费方错位对齐。
//   ③ 结果按 `sha256(source|target|text)` 本地缓存 ⇒ 重评不重复付费。
//
// 关于 error_kind：Broker 对所有"插件自报失败"统一填 `provider_error`（§4.3 的枚举是宿主
// 的词汇表，插件不能自定），所以 §7.3 里"否则 protocol_error"指的是**语义等级**——长度不符属于
// "响应结构不符 ⇒ 记错误、不重试"。本插件把 `protocol_error` 字样写进错误文案，消费方与
// service-call.log 据此识别；同时消费方自己也要防御（见 §7.3 的 Phase 6 落地说明）。
internal static class TranslateTencentPlugin
{
    private const string PluginId="io.github.kevendai.translate-tencent";
    private const string PluginVersion="1.0.0";
    private const string DefaultEndpoint="https://tmt.tencentcloudapi.com";
    private const string ApiVersion="2018-03-21";
    private const string ApiAction="TextTranslateBatch";
    private const string Tc3Service="tmt";
    private const string Tc3Algorithm="TC3-HMAC-SHA256";
    // 腾讯云 v3 签名只覆盖这三个头（官方文档示例的 SignedHeaders 就是它）。
    private const string Tc3SignedHeaders="content-type;host;x-tc-action";
    private const string ContentType="application/json; charset=utf-8";
    private const string DefaultRegion="ap-guangzhou";
    private const string DefaultSourceLanguage="auto";
    private const string DefaultTargetLanguage="zh";
    private const int DefaultTimeoutSeconds=60;
    private const int DefaultMaxTexts=200;
    private const int MaxTextsHardLimit=500;
    // 腾讯云硬限制：单次请求文本长度总和必须**低于** 2000（UnsupportedOperation.TextTooLong）。
    private const int MaxCharsPerText=2000;
    // 切分预算是 1800 而不是 2000：官方口径是"低于 2000"，留 10% 余量免得踩边界。
    private const int ChunkCharBudget=1800;
    // 一次调用最多 40k 字符（≈23 个腾讯请求）；超过就要求消费方分批，避免一次调用跑到超时。
    private const int MaxTotalChars=40000;
    // 默认接口频率限制 5 次/秒 ⇒ 相邻请求起点至少间隔 250ms。
    private const int RequestIntervalMs=250;
    private const int CacheVersion=1;
    private const int MaxCacheEntries=5000;
    // 重试节奏：比 ai-deepseek 快一档（翻译单批很快，且限流是"每秒"级别的窗口）。
    private static readonly int[] RetryDelays={1000,2000,4000};

    private static int Main(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;
        if(args.Length>0&&args[0]=="TranslationProviderSelfTest")return RunSelfTests();
        string requestId="";
        try
        {
            Dictionary<string,object> request=JsonUtil.Object(JsonUtil.Deserialize(Console.In.ReadLine()??""));
            requestId=JsonUtil.String(request,"request_id","");
            string action=JsonUtil.String(request,"action","");
            if(action!="translate"&&action!="test_connection"&&action!="validate_settings")
                return Fail(requestId,"不支持的 action",false);
            Dictionary<string,object> config=JsonUtil.Object(JsonUtil.Get(request,"config"));
            Dictionary<string,object> secret=JsonUtil.Object(JsonUtil.Get(request,"secret"));
            Dictionary<string,object> input=JsonUtil.Object(JsonUtil.Get(request,"input"));
            if(action=="translate")return Translate(requestId,config,secret,input);
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

    // ── translate：唯一真正做事的 action ─────────────────────────────────────
    // input {texts:[…], source_language:"en", target_language:"zh-CN"}
    // output {translations:[…], source_language, target_language, usage:{translated_chars,requests,cached}}
    private static int Translate(string requestId,Dictionary<string,object> config,Dictionary<string,object> secret,Dictionary<string,object> input)
    {
        Client client=BuildClient(config,secret);
        RequireCredentials(client);
        List<string> texts=ReadTexts(input,client.MaxTexts);
        string source=ResolveLanguage(FirstNonEmpty(JsonUtil.String(input,"source_language",""),client.SourceLanguage),true);
        string target=ResolveLanguage(FirstNonEmpty(JsonUtil.String(input,"target_language",""),client.TargetLanguage),false);

        // 源=目标时翻译没有意义，直接原样返回：既不花钱也不该假装配了一次。
        if(source!="auto"&&String.Equals(source,target,StringComparison.OrdinalIgnoreCase))
        {
            List<object> unchanged=new List<object>();
            foreach(string text in texts)unchanged.Add(text);
            return Emit(requestId,true,new Dictionary<string,object>{
                {"translations",unchanged},
                {"source_language",source},{"target_language",target},
                {"usage",new Dictionary<string,object>{{"translated_chars",0},{"requests",0},{"cached",0}}},
                {"notes",new List<object>{"源语言与目标语言相同，未调用腾讯云接口（不产生费用）"}}},null);
        }

        Dictionary<string,object> cache=client.UseCache?LoadCache():null;
        List<string> results=new List<string>(new string[texts.Count]);
        List<int> pending=new List<int>();
        int cachedCount=0;
        for(int index=0;index<texts.Count;index++)
        {
            string hit=cache==null?null:CacheGet(cache,source,target,texts[index]);
            if(hit!=null){results[index]=hit;cachedCount++;}else pending.Add(index);
        }
        foreach(List<int> chunk in SplitChunks(texts,pending,ChunkCharBudget))
        {
            List<string> payload=new List<string>();
            foreach(int index in chunk)payload.Add(texts[index]);
            List<string> translated=TranslateChunk(client,payload,source,target);
            if(translated.Count!=payload.Count)
                throw new Failure("协议错误（protocol_error）：腾讯云返回 "+translated.Count.ToString(CultureInfo.InvariantCulture)
                    +" 条译文，但本次请求了 "+payload.Count.ToString(CultureInfo.InvariantCulture)+" 条，长度不一致，已丢弃这一批结果。",false);
            for(int offset=0;offset<chunk.Count;offset++)
            {
                results[chunk[offset]]=translated[offset];
                if(cache!=null)CacheSet(cache,source,target,texts[chunk[offset]],translated[offset]);
            }
        }
        if(cache!=null)SaveCache(cache);

        int translatedChars=0;
        List<object> translations=new List<object>();
        foreach(string text in results){translatedChars+=text.Length;translations.Add(text);}
        return Emit(requestId,true,new Dictionary<string,object>{
            {"translations",translations},
            {"source_language",source},{"target_language",target},
            {"usage",new Dictionary<string,object>{
                {"translated_chars",translatedChars},
                {"requests",client.Requests},
                {"cached",cachedCount}}},
            {"limits",new Dictionary<string,object>{
                {"chunk_char_budget",ChunkCharBudget},
                {"request_interval_ms",RequestIntervalMs}}}},null);
    }

    // ── test_connection：设置页的 [测试连接] 按钮 ────────────────────────────
    // 刻意**不走缓存**：按钮的意义就是真的打一次接口，缓存命中会让用户以为密钥是对的。
    private static int TestConnection(string requestId,Dictionary<string,object> config,Dictionary<string,object> secret)
    {
        Client client=BuildClient(config,secret);
        RequireCredentials(client);
        string source=ResolveLanguage(client.SourceLanguage,true);
        string target=ResolveLanguage(client.TargetLanguage,false);
        string probe="translation service check";
        List<string> translated=TranslateChunk(client,new List<string>{probe},source,target);
        if(translated.Count!=1||translated[0].Trim()=="")
            throw new Failure("腾讯云返回的译文为空，请检查语言设置（源语言 / 目标语言）。",false);
        return Emit(requestId,true,new Dictionary<string,object>{
            {"message","翻译服务可用"},
            {"source_language",source},{"target_language",target},
            {"sample",new Dictionary<string,object>{
                {"text",probe},{"translation",translated[0]}}},
            {"region",client.Region},
            {"limits",new Dictionary<string,object>{
                {"chunk_char_budget",ChunkCharBudget},
                {"request_interval_ms",RequestIntervalMs},
                {"max_texts_per_call",client.MaxTexts}}}},null);
    }

    // ── validate_settings：宿主保存设置后会调用它，必须能 exit 0 ──────────────
    // 刻意**不**要求密钥已填：用户可能想先改语言再填密钥；缺密钥由实际调用时报 fatal。
    private static int ValidateSettings(string requestId,Dictionary<string,object> config)
    {
        Client client=BuildClient(config,new Dictionary<string,object>());
        Uri uri;
        if(!Uri.TryCreate(client.Endpoint,UriKind.Absolute,out uri)||(uri.Scheme!="http"&&uri.Scheme!="https"))
            throw new Failure("接口地址必须是 http(s) URL，实际为「"+client.Endpoint+"」",false);
        string source=ResolveLanguage(client.SourceLanguage,true);
        string target=ResolveLanguage(client.TargetLanguage,false);
        List<object> notes=new List<object>();
        if(uri.Query!="")notes.Add("接口地址带查询参数：TC3 签名不覆盖查询串（官方接口不使用查询参数），请确认你的网关不需要它参与签名。");
        if(source=="auto")notes.Add("源语言为 auto：由腾讯云自动识别，个别短标题可能识别错语种；固定为 en 可以避免。");
        return Emit(requestId,true,new Dictionary<string,object>{
            {"message","设置有效"},
            {"endpoint",client.Endpoint},
            {"region",client.Region},
            {"source_language",source},
            {"target_language",target},
            {"cache_enabled",client.UseCache},
            {"cache_entries",CountCache()},
            {"limits",new Dictionary<string,object>{
                {"chunk_char_budget",ChunkCharBudget},
                {"max_texts_per_call",client.MaxTexts},
                {"timeout_seconds",client.TimeoutSeconds}}},
            {"notes",notes}},null);
    }

    // ── 输入校验 ─────────────────────────────────────────────────────────────
    private static List<string> ReadTexts(Dictionary<string,object> input,int maxTexts)
    {
        List<object> raw=JsonUtil.Array(JsonUtil.Get(input,"texts"));
        if(raw.Count==0)throw new Failure("input.texts 必须是非空数组",false);
        if(raw.Count>maxTexts)
            throw new Failure("input.texts 有 "+raw.Count.ToString(CultureInfo.InvariantCulture)+" 条，超过单次调用上限 "
                +maxTexts.ToString(CultureInfo.InvariantCulture)+" 条（可在插件高级设置里调整）；请分批调用。",false);
        List<string> texts=new List<string>();
        int total=0;
        for(int index=0;index<raw.Count;index++)
        {
            string text=raw[index] as string;
            if(text==null)throw new Failure("input.texts["+index.ToString(CultureInfo.InvariantCulture)+"] 必须是字符串",false);
            if(text.Trim()=="")throw new Failure("input.texts["+index.ToString(CultureInfo.InvariantCulture)+"] 不能是空字符串",false);
            if(text.Length>MaxCharsPerText)
                throw new Failure("input.texts["+index.ToString(CultureInfo.InvariantCulture)+"] 有 "+text.Length.ToString(CultureInfo.InvariantCulture)
                    +" 个字符，超过腾讯云单条 "+MaxCharsPerText.ToString(CultureInfo.InvariantCulture)+" 字符上限。",false);
            total+=text.Length;
            texts.Add(text);
        }
        if(total>MaxTotalChars)
            throw new Failure("input.texts 合计 "+total.ToString(CultureInfo.InvariantCulture)+" 个字符，超过单次调用上限 "
                +MaxTotalChars.ToString(CultureInfo.InvariantCulture)+" 字符；请分批调用。",false);
        return texts;
    }

    // 按"每批字符数不超过预算"切分，**保持原顺序**；单条超预算的文本在 ReadTexts 已被拒。
    private static List<List<int>> SplitChunks(List<string> texts,List<int> indices,int budget)
    {
        List<List<int>> chunks=new List<List<int>>();
        List<int> current=new List<int>();
        int length=0;
        foreach(int index in indices)
        {
            int cost=texts[index].Length;
            if(current.Count>0&&length+cost>budget){chunks.Add(current);current=new List<int>();length=0;}
            current.Add(index);length+=cost;
        }
        if(current.Count>0)chunks.Add(current);
        return chunks;
    }

    private static string FirstNonEmpty(string preferred,string fallback)
    {
        return String.IsNullOrWhiteSpace(preferred)?fallback:preferred;
    }

    // ── 语言代码：消费方用 BCP-47（en / zh-CN / zh-TW），腾讯云用短码（en / zh / zh-TW）──
    // 换算表是 Provider 的职责：消费方不该知道任何一家厂商的语言代码。
    private static readonly Dictionary<string,string> LanguageAliases=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
        {"zh","zh"},{"zh-cn","zh"},{"zh-hans","zh"},{"zh-chs","zh"},{"zh-sg","zh"},{"cn","zh"},{"chs","zh"},{"chinese","zh"},
        {"zh-tw","zh-TW"},{"zh-hk","zh-TW"},{"zh-mo","zh-TW"},{"zh-hant","zh-TW"},{"zh-cht","zh-TW"},{"cht","zh-TW"},
        {"en","en"},{"english","en"},{"ja","ja"},{"jp","ja"},{"japanese","ja"},
        {"ko","ko"},{"kr","ko"},{"korean","ko"},{"fr","fr"},{"es","es"},{"it","it"},{"de","de"},
        {"tr","tr"},{"ru","ru"},{"pt","pt"},{"vi","vi"},{"id","id"},{"th","th"},{"ms","ms"},{"ar","ar"},{"hi","hi"},
        {"auto","auto"},{"detect","auto"}};

    // 腾讯云机器翻译支持的目标语言（也支持作为源语言）；auto 只能当源语言。
    private static readonly HashSet<string> SupportedTargets=new HashSet<string>(
        new string[]{"zh","zh-TW","en","ja","ko","fr","es","it","de","tr","ru","pt","vi","id","th","ms","ar","hi"},
        StringComparer.OrdinalIgnoreCase);

    private static string ResolveLanguage(string code,bool source)
    {
        string key=(code??"").Trim().ToLowerInvariant();
        if(key=="")
        {
            if(source)return "auto";
            throw new Failure("目标语言不能为空，请在「腾讯云翻译」插件设置里指定语言。",true);
        }
        string value;
        if(!LanguageAliases.TryGetValue(key,out value))
        {
            // 带地区后缀的（en-US / pt-BR / ja-JP）退回主语言子标签再查一次。
            int dash=key.IndexOf('-');
            string primary=dash<0?key:key.Substring(0,dash);
            if(primary==""||!LanguageAliases.TryGetValue(primary,out value))value=null;
        }
        if(value==null||value=="")
            throw new Failure((source?"源语言":"目标语言")+"「"+(code??"")+"」不受腾讯云机器翻译支持，请改成一个受支持的语言代码。",true);
        if(value=="auto")
        {
            if(source)return "auto";
            throw new Failure("目标语言不能是 auto，请在「腾讯云翻译」插件设置里指定一个具体语言。",true);
        }
        if(!SupportedTargets.Contains(value))
            throw new Failure((source?"源语言":"目标语言")+"「"+(code??"")+"」不受腾讯云机器翻译支持，请改成一个受支持的语言代码。",true);
        return value;
    }

    private sealed class Failure:Exception
    {
        public bool Fatal;
        public Failure(string message,bool fatal):base(message){Fatal=fatal;}
    }

    // ── 配置组装 ─────────────────────────────────────────────────────────────
    private sealed class Client
    {
        public string Endpoint=DefaultEndpoint,Region=DefaultRegion;
        public string SecretId="",SecretKey="";
        public string SourceLanguage=DefaultSourceLanguage,TargetLanguage=DefaultTargetLanguage;
        public bool UseCache=true;
        public int TimeoutSeconds=DefaultTimeoutSeconds,MaxTexts=DefaultMaxTexts;
        public int Requests;
        private DateTimeOffset lastRequest=DateTimeOffset.MinValue;

        // 默认接口频率限制 5 次/秒 ⇒ 相邻请求起点至少间隔 250ms。串行请求 + 节流是必须的：
        // 消费方可能一次塞进来上千条，全速打过去只会换来一片 RequestLimitExceeded。
        public void Throttle()
        {
            if(Requests>0)
            {
                long elapsed=(long)(DateTimeOffset.Now-lastRequest).TotalMilliseconds;
                long wait=RequestIntervalMs-elapsed;
                if(wait>0)Thread.Sleep((int)wait);
            }
            lastRequest=DateTimeOffset.Now;
        }
    }

    private static Client BuildClient(Dictionary<string,object> config,Dictionary<string,object> secret)
    {
        Client client=new Client();
        // 宿主传下来的 config 已经展开过一次 {{plugin:...}}；这里再走一遍是幂等的。
        string endpoint=DynamicPluginValues.Resolve(JsonUtil.String(config,"api_endpoint",DefaultEndpoint)).Trim();
        client.Endpoint=NormalizeHttpUrl(endpoint==""?DefaultEndpoint:endpoint);
        client.Region=JsonUtil.String(config,"region",DefaultRegion).Trim();
        if(client.Region=="")client.Region=DefaultRegion;
        client.SourceLanguage=JsonUtil.String(config,"source_language",DefaultSourceLanguage).Trim();
        if(client.SourceLanguage=="")client.SourceLanguage=DefaultSourceLanguage;
        client.TargetLanguage=JsonUtil.String(config,"target_language",DefaultTargetLanguage).Trim();
        if(client.TargetLanguage=="")client.TargetLanguage=DefaultTargetLanguage;
        client.UseCache=JsonUtil.Bool(config,"use_local_cache",true);
        client.SecretId=JsonUtil.String(secret,"secret_id","").Trim();
        client.SecretKey=JsonUtil.String(secret,"secret_key","").Trim();
        client.TimeoutSeconds=Clamp(JsonUtil.Int(config,"timeout_seconds",DefaultTimeoutSeconds),10,600);
        client.MaxTexts=Clamp(JsonUtil.Int(config,"max_texts_per_call",DefaultMaxTexts),1,MaxTextsHardLimit);
        return client;
    }

    private static int Clamp(int value,int minimum,int maximum)
    {
        if(value<minimum)return minimum;
        if(value>maximum)return maximum;
        return value;
    }

    private static void RequireCredentials(Client client)
    {
        if(client.SecretId==""||client.SecretKey=="")
            throw new Failure("请先在「腾讯云翻译」插件设置里填写 Secret ID 与 Secret Key",true);
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
    // 与 ai-deepseek 同一个理由：内网地址被代理客户端劫持后行为不可预测；
    // 顺带让"把接口地址指向局域网网关"和"离线探针指着 127.0.0.1"这两种用法都能工作。
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

    // ── TC3-HMAC-SHA256 签名（腾讯云 API 3.0「签名方法 v3」）─────────────────
    // 官方文档：https://cloud.tencent.com/document/product/598/38504
    // 三个"看起来无所谓但一定会踩"的点，都在下面固化了：
    //   ① Date 必须由时间戳取 **UTC** 日期（东八区凌晨会算错成第二天 ⇒ 全部签名失败）；
    //   ② CanonicalHeaders 里的 action 要**小写**，且只签 content-type / host / x-tc-action；
    //   ③ CanonicalHeader 末尾本身带 \n，与 SignedHeaders 之间因此会有一个**空行**。
    private static string Hex(byte[] value)
    {
        return BitConverter.ToString(value).Replace("-","").ToLowerInvariant();
    }

    private static long UnixSeconds()
    {
        return (long)(DateTime.UtcNow-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
    }

    private static byte[] Unhex(string value)
    {
        if(value==null)value="";
        byte[] bytes=new byte[value.Length/2];
        for(int index=0;index<bytes.Length;index++)bytes[index]=Convert.ToByte(value.Substring(index*2,2),16);
        return bytes;
    }

    internal static string Tc3Date(long timestamp)
    {
        return new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(timestamp).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    }

    internal static string CanonicalRequest(string method,string canonicalUri,string canonicalQuery,string host,string contentType,string action,string hashedPayload)
    {
        string canonicalHeaders="content-type:"+contentType+"\n"+"host:"+host+"\n"+"x-tc-action:"+action.ToLowerInvariant()+"\n";
        return method+"\n"+canonicalUri+"\n"+canonicalQuery+"\n"+canonicalHeaders+"\n"+Tc3SignedHeaders+"\n"+hashedPayload;
    }

    internal static string StringToSign(long timestamp,string date,string hashedCanonicalRequest)
    {
        return StringToSign(timestamp,date,Tc3Service,hashedCanonicalRequest);
    }

    // service 显式传参只为自检：腾讯云官方示例用的是 cvm，而本插件的 scope 是 tmt。
    internal static string StringToSign(long timestamp,string date,string service,string hashedCanonicalRequest)
    {
        return Tc3Algorithm+"\n"+timestamp.ToString(CultureInfo.InvariantCulture)+"\n"
            +date+"/"+service+"/tc3_request"+"\n"+hashedCanonicalRequest;
    }

    internal static string Sign(string secretId,string secretKey,long timestamp,string canonicalUri,string host,string contentType,string action,string body)
    {
        string date=Tc3Date(timestamp);
        string canonicalRequest=CanonicalRequest("POST",canonicalUri,"",host,contentType,action,RuntimeUtil.Sha256Hex(body));
        string stringToSign=StringToSign(timestamp,date,RuntimeUtil.Sha256Hex(canonicalRequest));
        // 派生密钥链：kDate → kService → kSigning，中间值都是**二进制**（不是 hex 字符串）。
        byte[] kDate=RuntimeUtil.Hmac(Encoding.UTF8.GetBytes("TC3"+secretKey),date);
        byte[] kService=RuntimeUtil.Hmac(kDate,Tc3Service);
        byte[] kSigning=RuntimeUtil.Hmac(kService,"tc3_request");
        string signature=Hex(RuntimeUtil.Hmac(kSigning,stringToSign));
        return Tc3Algorithm+" Credential="+secretId+"/"+date+"/"+Tc3Service+"/tc3_request, SignedHeaders="+Tc3SignedHeaders+", Signature="+signature;
    }

    // ── 请求体与 HTTP ────────────────────────────────────────────────────────
    private static string BuildBody(List<string> texts,string source,string target)
    {
        List<object> wire=new List<object>();
        foreach(string text in texts)wire.Add(text);
        Dictionary<string,object> body=new Dictionary<string,object>();
        body["SourceTextList"]=wire;
        body["Source"]=source;
        body["Target"]=target;
        body["ProjectId"]=0;
        return JsonUtil.Serialize(body);
    }

    private sealed class HttpFailure:Exception
    {
        public int StatusCode;
        public string Body;
        public HttpFailure(int statusCode,string body):base("HTTP "+statusCode.ToString(CultureInfo.InvariantCulture)+" "+body){StatusCode=statusCode;Body=body;}
    }

    // 厂商错误码（HTTP 200 + Response.Error）。分两级：fatal（重试没意义，要人去控制台/改设置）
    // 与 transient（等一会儿再试就可能有结果）。分级照搬腾讯云机器翻译的错误码文档。
    private sealed class ApiFailure:Exception
    {
        public string Code;
        public bool Fatal;
        public bool Transient;
        public ApiFailure(string code,string message):base(message)
        {
            Code=code;Fatal=IsFatalCode(code);Transient=IsTransientCode(code);
        }
    }

    private static bool IsFatalCode(string code)
    {
        if(String.IsNullOrEmpty(code))return false;
        if(code.StartsWith("AuthFailure",StringComparison.Ordinal))return true;
        if(code.StartsWith("UnauthorizedOperation",StringComparison.Ordinal))return true;
        if(code=="FailedOperation.NoFreeAmount"||code=="FailedOperation.ServiceIsolate"||code=="FailedOperation.StopUsing"
            ||code=="FailedOperation.UserNotRegistered"||code=="FailedOperation.ErrorUserArea")return true;
        if(code.StartsWith("UnsupportedOperation.Unsupported",StringComparison.Ordinal)
            ||code=="UnsupportedOperation.UnSupportedTargetLanguage"||code=="UnsupportedOperation")return true;
        return false;
    }

    private static bool IsTransientCode(string code)
    {
        if(String.IsNullOrEmpty(code))return false;
        if(code.StartsWith("InternalError",StringComparison.Ordinal))return true;
        if(code.StartsWith("RequestLimitExceeded",StringComparison.Ordinal))return true;
        if(code.StartsWith("LimitExceeded",StringComparison.Ordinal))return true;
        if(code=="FailedOperation"||code=="FailedOperation.RequestAiLabErr"||code=="FailedOperation.LanguageRecognitionErr")return true;
        if(code=="ServiceUnavailable")return true;
        return false;
    }

    private static List<string> TranslateChunk(Client client,List<string> texts,string source,string target)
    {
        string body=BuildBody(texts,source,target);
        for(int attempt=0;;attempt++)
        {
            try{return ParseTargets(HttpPost(client,body));}
            catch(HttpFailure ex)
            {
                // 腾讯云的业务错误一律 HTTP 200，所以走到这里的是真的传输/网关层错误。
                if(ex.StatusCode==401||ex.StatusCode==403)throw new Failure(DescribeHttp(ex.StatusCode,ex.Body),true);
                bool transient=ex.StatusCode==429||ex.StatusCode==500||ex.StatusCode==502||ex.StatusCode==503||ex.StatusCode==504;
                if(!transient||attempt>=RetryDelays.Length)throw new Failure(DescribeHttp(ex.StatusCode,ex.Body),false);
            }
            catch(ApiFailure ex)
            {
                if(ex.Fatal)throw new Failure(ex.Message,true);
                if(!ex.Transient||attempt>=RetryDelays.Length)throw new Failure(ex.Message,false);
            }
            Thread.Sleep(RetryDelays[attempt]+new Random(unchecked(Environment.TickCount*31+Thread.CurrentThread.ManagedThreadId)).Next(150,600));
        }
    }

    // 每次尝试都重新取时间戳并重新签名（TC3 的 Timestamp 参与签名，且与服务器时间偏差 >5 分钟必失败）。
    private static string HttpPost(Client client,string body)
    {
        Uri uri=new Uri(client.Endpoint);
        string host=uri.IsDefaultPort?uri.Host:uri.Host+":"+uri.Port.ToString(CultureInfo.InvariantCulture);
        long timestamp=UnixSeconds();
        client.Throttle();
        ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
        HttpWebRequest request=(HttpWebRequest)WebRequest.Create(uri);
        request.Method="POST";
        request.Timeout=client.TimeoutSeconds*1000;
        request.ReadWriteTimeout=client.TimeoutSeconds*1000;
        request.KeepAlive=true;
        // 公网 API 沿用系统代理；回环/内网直连（见 IsLocalEndpoint 的说明）。
        if(IsLocalEndpoint(uri))request.Proxy=null;
        request.UserAgent="RainmeterDesktopWidgets/"+PluginVersion;
        request.Accept="application/json";
        request.ContentType=ContentType;
        // 签的 host 必须与实际发出去的 Host 头逐字节一致，所以这里显式写死。
        request.Host=host;
        request.Headers["X-TC-Action"]=ApiAction;
        request.Headers["X-TC-Version"]=ApiVersion;
        request.Headers["X-TC-Timestamp"]=timestamp.ToString(CultureInfo.InvariantCulture);
        if(client.Region!="")request.Headers["X-TC-Region"]=client.Region;
        request.Headers["Authorization"]=Sign(client.SecretId,client.SecretKey,timestamp,uri.AbsolutePath,host,ContentType,ApiAction,body);
        byte[] bytes=Encoding.UTF8.GetBytes(body);
        request.ContentLength=bytes.Length;
        using(Stream stream=request.GetRequestStream())stream.Write(bytes,0,bytes.Length);
        client.Requests++;
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
    private static List<string> ParseTargets(string raw)
    {
        Dictionary<string,object> root;
        try{root=JsonUtil.Object(JsonUtil.Deserialize(raw));}
        catch(Exception ex){throw new Failure("腾讯云返回的不是合法 JSON："+SafeText(ex.Message),false);}
        Dictionary<string,object> response=JsonUtil.Object(JsonUtil.Get(root,"Response"));
        if(response.Count==0)
            throw new Failure("腾讯云返回的 JSON 里没有 Response 对象（云 API 的正常响应与业务错误都放在 Response 里）。",false);
        Dictionary<string,object> error=JsonUtil.Object(JsonUtil.Get(response,"Error"));
        string code=JsonUtil.String(error,"Code","");
        if(code!="")throw new ApiFailure(code,DescribeCode(code,JsonUtil.String(error,"Message","")));
        object list=JsonUtil.Get(response,"TargetTextList");
        if(list==null)throw new Failure("腾讯云返回缺少 TargetTextList，无法确认译文顺序。",false);
        List<object> targets=JsonUtil.Array(list);
        List<string> translations=new List<string>();
        foreach(object item in targets)translations.Add(item as string??Convert.ToString(item,CultureInfo.InvariantCulture)??"");
        return translations;
    }

    private static string DescribeCode(string code,string message)
    {
        if(code=="AuthFailure.SecretIdNotFound")return "腾讯云 Secret ID 不存在或已被禁用（"+code+"），请在「腾讯云翻译」插件设置里检查 Secret ID。";
        if(code=="AuthFailure.InvalidSecretId")return "腾讯云 Secret ID 不是有效的云 API 密钥（"+code+"），请检查是否填成了别的字符串。";
        if(code=="AuthFailure.SignatureFailure")return "腾讯云签名校验失败（"+code+"），通常是 Secret Key 填错；请重新复制 Secret Key（注意首尾有没有多余空格）。";
        if(code=="AuthFailure.SignatureExpire")return "腾讯云签名已过期（"+code+"）：本机时间与标准时间相差超过 5 分钟，请先同步系统时间再试。";
        if(code=="AuthFailure.TokenFailure"||code=="AuthFailure.InvalidAuthorization")return "腾讯云鉴权失败（"+code+"），请重新填写密钥。";
        if(code.StartsWith("AuthFailure",StringComparison.Ordinal))return "腾讯云鉴权失败（"+code+"）："+SafeText(message);
        if(code.StartsWith("UnauthorizedOperation",StringComparison.Ordinal))
            return "腾讯云未授权本次调用（"+code+"）：请在腾讯云控制台确认这个密钥已开通机器翻译（TMT）并有 CAM 权限。";
        if(code=="FailedOperation.NoFreeAmount")return "腾讯云机器翻译本月免费额度已用完（"+code+"），如需继续使用请在腾讯云机器翻译控制台升级为付费。";
        if(code=="FailedOperation.ServiceIsolate")return "腾讯云账号因为欠费已停止机器翻译服务（"+code+"），请先充值。";
        if(code=="FailedOperation.StopUsing")return "腾讯云账号的机器翻译服务已停服（"+code+"），请检查账号状态。";
        if(code=="FailedOperation.UserNotRegistered")return "腾讯云机器翻译服务未开通（"+code+"），请先在官网机器翻译控制台开通。";
        if(code=="FailedOperation.ErrorUserArea")return "腾讯云用户地域与请求服务地域不一致（"+code+"），请检查插件的地域设置。";
        if(code=="UnsupportedOperation.UnsupportedSourceLanguage")return "腾讯云不支持这个源语言（"+code+"），请在插件设置里改源语言。";
        if(code=="UnsupportedOperation.UnSupportedTargetLanguage"||code=="UnsupportedOperation.UnsupportedTargetLanguage")return "腾讯云不支持这个目标语言（"+code+"），请在插件设置里改目标语言。";
        if(code=="UnsupportedOperation.TextTooLong")return "本次请求的文本长度超过腾讯云 2000 字符上限（"+code+"），这是插件内部分批的错误，请反馈。";
        if(code.StartsWith("RequestLimitExceeded",StringComparison.Ordinal)||code.StartsWith("LimitExceeded",StringComparison.Ordinal))
            return "腾讯云翻译请求过于频繁（"+code+"），默认限制是每秒 5 次，稍后重试。";
        if(code.StartsWith("InternalError",StringComparison.Ordinal))return "腾讯云翻译服务内部错误（"+code+"），稍后重试。";
        return "腾讯云翻译失败（"+code+"）："+SafeText(message);
    }

    private static string DescribeHttp(int statusCode,string body)
    {
        if(statusCode==401||statusCode==403)return "腾讯云拒绝了这次请求（HTTP "+statusCode.ToString(CultureInfo.InvariantCulture)+"），请检查 Secret ID / Secret Key 与账号权限。";
        return "腾讯云请求失败（HTTP "+statusCode.ToString(CultureInfo.InvariantCulture)+"）："+SafeText(body);
    }

    private static string Describe(Exception ex)
    {
        Failure failure=ex as Failure;
        if(failure!=null)return failure.Message;
        ApiFailure api=ex as ApiFailure;
        if(api!=null)return api.Message;
        HttpFailure http=ex as HttpFailure;
        if(http!=null)return DescribeHttp(http.StatusCode,http.Body);
        WebException web=ex as WebException;
        if(web!=null)return "连接腾讯云翻译失败："+SafeText(web.Message);
        return SafeText(ex==null?"":ex.Message);
    }

    // Parallel/TPL 会把内层异常包成 AggregateException：先拍平再挑最有信息量的那个，
    // 免得界面只剩「发生一个或多个错误。」。
    private static Exception Unwrap(Exception ex)
    {
        AggregateException aggregate=ex as AggregateException;
        if(aggregate==null)return ex;
        List<Exception> flat=new List<Exception>(aggregate.Flatten().InnerExceptions);
        foreach(Exception item in flat){if(item is Failure)return item;}
        foreach(Exception item in flat){if(item is ApiFailure)return item;}
        foreach(Exception item in flat){if(item is HttpFailure)return item;}
        return flat.Count>0?flat[0]:ex;
    }

    private static string SafeText(string value)
    {
        value=(value??"").Replace("\r"," ").Replace("\n"," ").Trim();
        return value.Length>200?value.Substring(0,200):value;
    }

    // ── 本地翻译缓存（§7.3：结果按 sha256(title) 本地缓存，重评不重复付费）──────
    // 键里额外带上了 source / target：同一个标题翻成不同语言是两份不同的结果，
    // 只用 sha256(title) 会串味（规格原话是 sha256(title)，这里按"更安全"实现，见 §7.3 落地说明）。
    private static string CacheFile()
    {
        string data=Environment.GetEnvironmentVariable("RW_PLUGIN_DATA_DIR");
        if(String.IsNullOrWhiteSpace(data))
        {
            string root=Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT");
            if(String.IsNullOrWhiteSpace(root))
                root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RainmeterDesktopWidgets");
            data=Path.Combine(root,"PluginData",PluginId);
        }
        return Path.Combine(data,"translation-cache.json");
    }

    internal static string CacheKey(string source,string target,string text)
    {
        return RuntimeUtil.Sha256Hex("v"+CacheVersion.ToString(CultureInfo.InvariantCulture)+"\n"+source+"\n"+target+"\n"+text);
    }

    // 缓存读写在任何情况下都不许影响翻译本身：读失败就当没有缓存，写失败只记一行 stderr。
    private static Dictionary<string,object> LoadCache()
    {
        try
        {
            string path=CacheFile();
            if(!File.Exists(path))return NewCache();
            Dictionary<string,object> root=JsonUtil.LoadObject(path);
            Dictionary<string,object> entries=JsonUtil.Object(JsonUtil.Get(root,"entries"));
            Dictionary<string,object> cache=new Dictionary<string,object>();
            cache["version"]=CacheVersion;cache["entries"]=entries;
            return cache;
        }
        catch(Exception ex)
        {
            Warn("读取翻译缓存失败，本次不使用缓存："+SafeText(ex.Message));
            QuarantineCache();
            // 返回**空缓存**而不是 null：这样本次调用结束时仍会把新结果写回磁盘，
            // 缓存只需要"重翻一次"就能恢复；返回 null 的话连着两次都要白花钱。
            return NewCache();
        }
    }

    // 坏掉的缓存文件必须**挪走**，不能留着：留着的话之后每次调用都会读失败 ⇒ 缓存永久失效 ⇒
    // 「重评不重复付费」（§7.3）直接作废，同一批标题会被反复送去付费翻译。
    // 挪走而不是删掉，是为了保留现场（用户反馈"怎么又收费了"时还能看到坏在哪）。
    private static void QuarantineCache()
    {
        try
        {
            string path=CacheFile();
            if(!File.Exists(path))return;
            string quarantine=path+".corrupt";
            try{File.Delete(quarantine);}catch{}
            File.Move(path,quarantine);
            Warn("翻译缓存文件已损坏，已挪到 "+Path.GetFileName(quarantine)+"，下次调用会重建缓存。");
        }
        catch(Exception ex){Warn("隔离损坏的翻译缓存失败："+SafeText(ex.Message));}
    }

    private static Dictionary<string,object> NewCache()
    {
        return new Dictionary<string,object>{{"version",CacheVersion},{"entries",new Dictionary<string,object>()}};
    }

    private static int CountCache()
    {
        Dictionary<string,object> cache=LoadCache();
        return cache==null?0:JsonUtil.Object(JsonUtil.Get(cache,"entries")).Count;
    }

    private static string CacheGet(Dictionary<string,object> cache,string source,string target,string text)
    {
        Dictionary<string,object> entries=JsonUtil.Object(JsonUtil.Get(cache,"entries"));
        Dictionary<string,object> entry=JsonUtil.Object(JsonUtil.Get(entries,CacheKey(source,target,text)));
        if(entry.Count==0)return null;
        string translation=JsonUtil.String(entry,"translation","");
        return translation==""?null:translation;
    }

    private static void CacheSet(Dictionary<string,object> cache,string source,string target,string text,string translation)
    {
        Dictionary<string,object> entries=JsonUtil.Object(JsonUtil.Get(cache,"entries"));
        entries[CacheKey(source,target,text)]=new Dictionary<string,object>{
            {"translation",translation},
            {"source",source},{"target",target},
            {"updated_at",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)}};
    }

    // 缓存只留最近 MaxCacheEntries 条（按 updated_at 淘汰最旧的），避免文件无限膨胀。
    private static void SaveCache(Dictionary<string,object> cache)
    {
        try
        {
            Dictionary<string,object> entries=JsonUtil.Object(JsonUtil.Get(cache,"entries"));
            if(entries.Count>MaxCacheEntries)
            {
                List<string> keys=new List<string>(entries.Keys);
                keys.Sort(delegate(string left,string right){return EntryTime(entries,left).CompareTo(EntryTime(entries,right));});
                for(int index=0;index<keys.Count-MaxCacheEntries;index++)entries.Remove(keys[index]);
            }
            string path=CacheFile();
            string directory=Path.GetDirectoryName(path);
            if(!String.IsNullOrEmpty(directory))Directory.CreateDirectory(directory);
            JsonUtil.SaveAtomic(path,new Dictionary<string,object>{{"version",CacheVersion},{"entries",entries}});
        }
        catch(Exception ex)
        {
            Warn("写入翻译缓存失败："+SafeText(ex.Message));
        }
    }

    private static DateTime EntryTime(Dictionary<string,object> entries,string key)
    {
        DateTimeOffset parsed;
        if(DateTimeOffset.TryParse(JsonUtil.String(JsonUtil.Object(JsonUtil.Get(entries,key)),"updated_at",""),CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))
            return parsed.UtcDateTime;
        return DateTime.MaxValue;
    }

    // ── 离线自检（不联网；退出码 70 起）──────────────────────────────────────
    private static int RunSelfTests()
    {
        quietDiagnostics=true;
        // 70：TC3 签名的**已知答案**——全部取自腾讯云官方文档的示例，逐字节比对。
        //     这是本插件最值得钉死的地方：签名拼错时接口只会回一个 SignatureFailure，
        //     没有已知答案就只能靠"线上试试看"。
        string docPayload="{\"Limit\": 1, \"Filters\": [{\"Values\": [\"\\u672a\\u547d\\u540d\"], \"Name\": \"instance-name\"}]}";
        string payloadHash=RuntimeUtil.Sha256Hex(docPayload);
        if(payloadHash!="35e9c5b0e3ae67532d3c9f17ead6c90222632e5b1ff7f6e89887f1398934f064"){Diag("70.1 payload hash="+payloadHash);return 70;}
        string docCanonical=CanonicalRequest("POST","/","","cvm.tencentcloudapi.com","application/json; charset=utf-8","DescribeInstances",payloadHash);
        string canonicalHash=RuntimeUtil.Sha256Hex(docCanonical);
        if(canonicalHash!="7019a55be8395899b900fb5564e4200d984910f34794a27cb3fb7d10ff6a1e84"){Diag("70.2 canonical hash="+canonicalHash+"\n["+docCanonical+"]");return 70;}
        // 官方示例的 CredentialScope 是 2019-02-25/cvm/tc3_request（接口是 CVM 的 DescribeInstances），
        // 所以这里显式传 cvm —— 这个已知答案校验的是"拼装规则 + HMAC 十六进制输出"，
        // 与本插件自己的 scope（tmt）无关；本插件的 scope 由 70.11 单独钉。
        string docStringToSign=StringToSign(1551113065,"2019-02-25","cvm",canonicalHash);
        string docSignature=Hex(RuntimeUtil.Hmac(Unhex("b596b923aad85185e2d1f6659d2a062e0a86731226e021e61bfe06f7ed05f5af"),docStringToSign));
        if(docSignature!="10b1a37a7301a02ca19a647ad722d5e43b4b3cff309d421d85b46093f6ab6c4f"){Diag("70.3 signature="+docSignature+"\n["+docStringToSign+"]");return 70;}
        if(StringToSign(1551113065,"2019-02-25",canonicalHash).IndexOf("/tmt/tc3_request",StringComparison.Ordinal)<0){Diag("70.11 scope 不是 tmt");return 70;}
        // Date 必须取 UTC 日期：1551113065 在东八区是 2019-02-26 00:44:25，但签名里的 Date 是 2019-02-25。
        if(Tc3Date(1551113065)!="2019-02-25"){Diag("70.4 date="+Tc3Date(1551113065));return 70;}
        // Authorization 头的形状（Credential / SignedHeaders / 64 位小写 hex 签名）。
        string authorization=Sign("AKIDexample","secret",1551113065,"/","tmt.tencentcloudapi.com",ContentType,ApiAction,"{}");
        if(!authorization.StartsWith("TC3-HMAC-SHA256 Credential=AKIDexample/",StringComparison.Ordinal)){Diag("70.5 authorization="+authorization);return 70;}
        if(authorization.IndexOf("/tmt/tc3_request, SignedHeaders=content-type;host;x-tc-action, Signature=",StringComparison.Ordinal)<0){Diag("70.6 authorization="+authorization);return 70;}
        string signature=authorization.Substring(authorization.LastIndexOf('=')+1);
        if(signature.Length!=64){Diag("70.7 signature length="+signature.Length.ToString(CultureInfo.InvariantCulture));return 70;}
        foreach(char character in signature)
            if(!((character>='0'&&character<='9')||(character>='a'&&character<='f'))){Diag("70.8 signature="+signature);return 70;}
        // 时间戳变了签名必须跟着变（否则就是"忘了把 timestamp 带进 StringToSign"）。
        if(Sign("AKIDexample","secret",1551113066,"/","tmt.tencentcloudapi.com",ContentType,ApiAction,"{}")==authorization){Diag("70.9 时间戳未参与签名");return 70;}
        // 同一条请求换个 body，签名也必须变（否则就是"忘了签 payload 哈希"）。
        if(Sign("AKIDexample","secret",1551113065,"/","tmt.tencentcloudapi.com",ContentType,ApiAction,"{\"a\":1}")==authorization){Diag("70.10 payload 未参与签名");return 70;}

        // 71：语言代码换算（消费方 BCP-47 → 腾讯云短码）。
        if(ResolveLanguage("en",true)!="en"||ResolveLanguage("en",false)!="en")return 71;
        if(ResolveLanguage("zh-CN",false)!="zh"||ResolveLanguage("zh-Hans",false)!="zh"||ResolveLanguage("zh",false)!="zh")return 71;
        if(ResolveLanguage("zh-TW",false)!="zh-TW"||ResolveLanguage("zh-HK",false)!="zh-TW")return 71;
        if(ResolveLanguage("jp",false)!="ja"||ResolveLanguage("ja-JP",false)!="ja")return 71;
        if(ResolveLanguage("kr",false)!="ko"||ResolveLanguage("pt-BR",false)!="pt"||ResolveLanguage("en-US",true)!="en")return 71;
        if(ResolveLanguage("auto",true)!="auto")return 71;
        if(ResolveLanguage("",true)!="auto")return 71;                                     // 源语言留空 = auto
        if(ResolveLanguage("  en  ",true)!="en")return 71;                                 // 首尾空格要吃掉
        if(!Throws(delegate{ResolveLanguage("auto",false);}))return 71;                    // 目标语言不许 auto
        if(!Throws(delegate{ResolveLanguage("",false);}))return 71;                        // 目标语言不许空
        if(!Throws(delegate{ResolveLanguage("klingon",false);}))return 71;                 // 不支持的语言
        if(!Throws(delegate{ResolveLanguage("xx-YY",false);}))return 71;                   // 不支持的主语言子标签

        // 72：文本输入校验：空数组 / 非数组 / 项非字符串 / 空串 / 单条超 2000 / 条数超上限 / 合计超上限。
        if(!Throws(delegate{ReadTexts(new Dictionary<string,object>(),10);}))return 72;
        if(!Throws(delegate{ReadTexts(new Dictionary<string,object>{{"texts","nope"}},10);}))return 72;
        if(!Throws(delegate{ReadTexts(new Dictionary<string,object>{{"texts",new List<object>{1,2}}},10);}))return 72;
        if(!Throws(delegate{ReadTexts(new Dictionary<string,object>{{"texts",new List<object>{"   "}}},10);}))return 72;
        if(!Throws(delegate{ReadTexts(new Dictionary<string,object>{{"texts",new List<object>{new string('a',MaxCharsPerText+1)}}},10);}))return 72;
        if(!Throws(delegate{ReadTexts(ManyTexts(5),2);}))return 72;
        if(!Throws(delegate{ReadTexts(ManyTexts(MaxTotalChars/MaxCharsPerText+1),MaxTextsHardLimit);}))return 72;
        if(ReadTexts(new Dictionary<string,object>{{"texts",new List<object>{"a","b"}}},10).Count!=2)return 72;

        // 73：切分——保持顺序、每组不超预算、边界不切碎。
        List<string> texts=new List<string>{"aaaa","bbbb","cccc"};
        List<int> all=new List<int>{0,1,2};
        if(SplitChunks(texts,all,8).Count!=2)return 73;
        if(SplitChunks(texts,all,12).Count!=1)return 73;
        if(SplitChunks(texts,all,4).Count!=3)return 73;
        List<List<int>> chunks=SplitChunks(texts,all,8);
        if(chunks[0][0]!=0||chunks[0][1]!=1||chunks[1][0]!=2)return 73;
        if(SplitChunks(texts,new List<int>{1,2},100).Count!=1)return 73;                  // 只有未命中的才发请求
        if(SplitChunks(texts,new List<int>(),100).Count!=0)return 73;                     // 全部命中缓存 ⇒ 一次请求都不发
        if(SplitChunks(new List<string>{new string('x',ChunkCharBudget)},new List<int>{0},ChunkCharBudget).Count!=1)return 73;

        // 74：响应解析与厂商错误码分级。
        List<string> parsed=ParseTargets("{\"Response\":{\"TargetTextList\":[\"甲\",\"乙\"],\"RequestId\":\"r\"}}");
        if(parsed.Count!=2||parsed[0]!="甲"||parsed[1]!="乙")return 74;
        if(!Throws(delegate{ParseTargets("not json");}))return 74;
        if(!Throws(delegate{ParseTargets("{\"foo\":1}");}))return 74;
        if(!Throws(delegate{ParseTargets("{\"Response\":{\"RequestId\":\"r\"}}");}))return 74;   // 缺 TargetTextList
        if(!Throws(delegate{ParseTargets("{\"Response\":{\"Error\":{\"Code\":\"AuthFailure.SignatureFailure\",\"Message\":\"x\"}}}");}))return 74;
        if(!IsFatalCode("AuthFailure.SignatureExpire")||!IsFatalCode("AuthFailure.SecretIdNotFound"))return 74;
        if(!IsFatalCode("FailedOperation.NoFreeAmount")||!IsFatalCode("FailedOperation.ServiceIsolate"))return 74;
        if(!IsFatalCode("FailedOperation.UserNotRegistered")||!IsFatalCode("UnauthorizedOperation.ActionNotFound"))return 74;
        if(!IsFatalCode("UnsupportedOperation.UnsupportedSourceLanguage"))return 74;
        if(IsFatalCode("RequestLimitExceeded")||IsFatalCode("InternalError.BackendTimeout"))return 74;
        if(IsFatalCode("InvalidParameter")||IsFatalCode("UnsupportedOperation.TextTooLong"))return 74;
        if(!IsTransientCode("RequestLimitExceeded.UinLimitExceeded")||!IsTransientCode("LimitExceeded.LimitedAccessFrequency"))return 74;
        if(!IsTransientCode("InternalError.ErrorGetRoute")||!IsTransientCode("FailedOperation.RequestAiLabErr"))return 74;
        if(IsTransientCode("InvalidParameter")||IsTransientCode("FailedOperation.NoFreeAmount"))return 74;
        // 错误文案必须点名状态码，且余额不足那一条不能被任何别的错误码复用。
        if(DescribeCode("FailedOperation.NoFreeAmount","x").IndexOf("免费额度",StringComparison.Ordinal)<0)return 74;
        if(DescribeCode("AuthFailure.SignatureExpire","x").IndexOf("系统时间",StringComparison.Ordinal)<0)return 74;
        if(DescribeCode("FailedOperation.NoFreeAmount","x").IndexOf("InvalidParameter",StringComparison.Ordinal)>=0)return 74;
        if(DescribeCode("InvalidParameter","参数错误").IndexOf("腾讯云翻译失败",StringComparison.Ordinal)<0)return 74;

        // 75：缓存键把 source / target / 文本都编进去；同文本不同目标语言不得串味；落盘可读回。
        if(CacheKey("en","zh","paper")==CacheKey("en","zh-TW","paper"))return 75;
        if(CacheKey("en","zh","paper")==CacheKey("en","zh","paper2"))return 75;
        if(CacheKey("en","zh","paper")==CacheKey("fr","zh","paper"))return 75;
        if(CacheKey("en","zh","paper").Length!=64)return 75;
        string previousDataDir=Environment.GetEnvironmentVariable("RW_PLUGIN_DATA_DIR");
        string temp=Path.Combine(Path.GetTempPath(),"rwtt-cache-"+Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("RW_PLUGIN_DATA_DIR",temp);
            Dictionary<string,object> cache=NewCache();
            CacheSet(cache,"en","zh","paper","论文");
            SaveCache(cache);
            if(!File.Exists(CacheFile()))return 75;
            Dictionary<string,object> reloaded=LoadCache();
            if(reloaded==null||CacheGet(reloaded,"en","zh","paper")!="论文")return 75;
            if(CacheGet(reloaded,"en","zh-TW","paper")!=null)return 75;
            if(CacheGet(reloaded,"en","zh","other")!=null)return 75;
            if(CountCache()!=1)return 75;
            // 坏文件不能把翻译带崩：读失败 ⇒ 当没有缓存（返回空缓存），而不是抛异常。
            File.WriteAllText(CacheFile(),"{ this is not json",RuntimeUtil.Utf8NoBom);
            if(LoadCache()==null)return 75;                       // 不许把异常抛给翻译
            if(CountCache()!=0)return 75;                         // 但要当它是空的
            // 也不能让缓存**永久**失效：坏文件必须被挪走，下一次调用就能重新建起来。
            if(File.Exists(CacheFile()))return 75;
            if(!File.Exists(CacheFile()+".corrupt"))return 75;
        }
        finally
        {
            Environment.SetEnvironmentVariable("RW_PLUGIN_DATA_DIR",previousDataDir);
            try{Directory.Delete(temp,true);}catch{}
        }

        // 76：配置夹取与地址规范化。
        Client client=BuildClient(new Dictionary<string,object>(),new Dictionary<string,object>());
        if(client.Endpoint!=DefaultEndpoint||client.Region!=DefaultRegion||client.TimeoutSeconds!=DefaultTimeoutSeconds)return 76;
        if(client.SourceLanguage!=DefaultSourceLanguage||client.TargetLanguage!=DefaultTargetLanguage)return 76;
        if(client.MaxTexts!=DefaultMaxTexts||!client.UseCache)return 76;
        Client clamped=BuildClient(new Dictionary<string,object>{{"timeout_seconds",1},{"max_texts_per_call",100000}},new Dictionary<string,object>());
        if(clamped.TimeoutSeconds!=10||clamped.MaxTexts!=MaxTextsHardLimit)return 76;
        if(BuildClient(new Dictionary<string,object>{{"use_local_cache",false}},new Dictionary<string,object>()).UseCache)return 76;
        if(NormalizeHttpUrl("tmt.tencentcloudapi.com")!="https://tmt.tencentcloudapi.com")return 76;
        if(BuildClient(new Dictionary<string,object>(),new Dictionary<string,object>{{"secret_id","id"},{"secret_key","key"}}).SecretKey!="key")return 76;
        if(SafeText("a\r\nb").IndexOf('\n')>=0)return 76;
        // 密钥绝不能出现在错误文案里（错误文案会进 service-call.log 之外的插件日志与界面）。
        Client keyed=BuildClient(new Dictionary<string,object>(),new Dictionary<string,object>{{"secret_id","AKID1234567890"},{"secret_key","topsecret"}});
        try{RequireCredentials(new Client());return 76;}
        catch(Failure failure){if(!failure.Fatal)return 76;if(failure.Message.IndexOf(keyed.SecretKey,StringComparison.Ordinal)>=0)return 76;}
        return 0;
    }

    // 自检失败时把实际值写到 stderr：退出码只能告诉"哪一节挂了"，签名类问题必须看到实际字符串。
    private static void Diag(string message)
    {
        Console.Error.WriteLine("TranslateTencentPlugin self-test: "+message);
    }

    // 自检里有"故意把缓存文件写坏"的用例，那条用例会触发 LoadCache 的正常告警。
    // 自检期间把这类**预期内**的告警静音，免得套件把一行红字当成异常（真正的失败仍然靠退出码）。
    private static bool quietDiagnostics;

    private static void Warn(string message)
    {
        if(!quietDiagnostics)Console.Error.WriteLine(message);
    }

    private static Dictionary<string,object> ManyTexts(int count)
    {
        List<object> texts=new List<object>();
        for(int index=0;index<count;index++)texts.Add(new string('a',MaxCharsPerText));
        return new Dictionary<string,object>{{"texts",texts}};
    }

    private static bool Throws(Action action)
    {
        try{action();return false;}catch{return true;}
    }
}
