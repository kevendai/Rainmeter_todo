using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RainmeterBackend;

internal static partial class TodoApp
{
    private const string GitHubRepository="kevendai/Rainmeter_todo";
    private static string ResourceDir="";
    private static string PluginDataDir="";
    private static string StatePath { get { return Path.Combine(PluginDataDir,"rss-tasks.json"); } }
    private static string IncludePath { get { return Path.Combine(ResourceDir,"Generated.inc"); } }
    private static string PaperCache { get { return Path.Combine(PluginDataDir,"cache"); } }
    // 2.1 起这份文件**只**放论文业务配置（Enabled / TranslateEnabled / Scoring / Rss）。它曾经还装着
    // DeepSeek Key 与文件服务器口令 —— 那两个已按 §9.2「复制不删除」搬到 ai-deepseek /
    // paper-snapshot-sync，arxiv 不再持有；旧文件里的同名段读到时直接忽略（不报错、不采用）。
    private static string PaperSyncSecret { get { return Path.Combine(PluginDataDir,"runtime-paper.secret"); } }
    // TodoUpdateService is shared with the host for translation helpers; this
    // plugin never launches the host updater.
    private static string UpdaterExecutable { get { return ""; } }
    private static readonly string AppVersion="2.1.0";
    private static Dictionary<string,object> PluginState;
    private sealed class EditorResult { public string Title,Target,Note,Available,Due;public List<string> Labels; }
    private delegate void LockedStateAction(Dictionary<string,object> state,ref bool refresh);

    private static int Main(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;
        PluginDataDir=Environment.GetEnvironmentVariable("RW_PLUGIN_DATA_DIR");if(String.IsNullOrWhiteSpace(PluginDataDir))PluginDataDir=Path.Combine(Path.GetTempPath(),"RainmeterArxivPluginData");Directory.CreateDirectory(PluginDataDir);Directory.CreateDirectory(PaperCache);
        if(args.Length>0&&args[0]=="PaperRssSelfTest")return RunPaperRssSelfTests();
        // 论文链路的共享逻辑（TodoPaperService，本体与插件同源）此前没有入口跑到，这里补上，
        // 覆盖致命错误快速失败与真实错误文案。
        if(args.Length>0&&args[0]=="PaperServiceSelfTest")return RunPaperSelfTests();
        // 消费者侧（arxiv 自己）的自检：服务解析、attention 流程、旧凭据段不再进入业务配置。
        if(args.Length>0&&args[0]=="PaperConsumerSelfTest")return RunPaperConsumerSelfTests();
        if(args.Length>0&&args[0]=="PaperRssServer"){ResourceDir=PluginDataDir;return RunPaperRssServer();}
        string requestId="";
        try
        {
            Dictionary<string,object> request=JsonUtil.Object(JsonUtil.Deserialize(Console.In.ReadLine()??""));
            requestId=JsonUtil.String(request,"request_id","");
            string action=JsonUtil.String(request,"action","");
            // `sync_with_ai` 是 §5.2 冻结的 resume action：用户在磁贴上确认付费后，宿主带着
            // input.allow_paid_ai=true 重开**同一条同步流程**。
            // 2.0.4 的 test_api / test_file_server / test_translation 三个动作随它们各自的客户端
            // 一起删除：连通性由 Provider 自己负责（§7.1、§7.2、§7.3），arxiv 不再替别人拨号。
            if(action!="sync"&&action!="rescore"&&action!="sync_with_ai"&&action!="validate_settings")return Emit(requestId,false,null,"不支持的 action");
            ResourceDir=Path.Combine(Path.GetTempPath(),"RainmeterArxivPlugin-"+requestId);
            Directory.CreateDirectory(ResourceDir);Directory.CreateDirectory(PaperCache);
            Dictionary<string,object> config=JsonUtil.Object(JsonUtil.Get(request,"config")),secret=JsonUtil.Object(JsonUtil.Get(request,"secret"));
            JsonUtil.WriteDpapiJson(PaperSyncSecret,BuildPaperSettings(config,secret));
            PluginState=new Dictionary<string,object>{{"version",3},{"meta",new Dictionary<string,object>()},{"tasks",new List<object>()}};
            string date=DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            string stateFile=Path.Combine(PluginDataDir,"state.json");
            Dictionary<string,object> persisted=File.Exists(stateFile)?JsonUtil.LoadObject(stateFile):new Dictionary<string,object>();
            Dictionary<string,object> context=JsonUtil.Object(JsonUtil.Get(request,"context"));
            Dictionary<string,object> input=JsonUtil.Object(JsonUtil.Get(request,"input"));
            ApplyJobContext(context,input);
            PaperSettings settings=LoadPaperSettings();
            if(action=="validate_settings"){ValidatePaperSettings(settings);return Emit(requestId,true,new Dictionary<string,object>{{"message","设置有效"}},null);}
            // 后台/定时同步（trigger=startup）今天已经跑过就不再走一遍 —— 沿用 2.0.4 的 startup 短路。
            bool manual=!String.Equals(JsonUtil.String(context,"trigger","manual"),"startup",StringComparison.OrdinalIgnoreCase);
            if(!manual&&JsonUtil.String(persisted,"last_sync_date","")==date)
                return Emit(requestId,true,new Dictionary<string,object>{{"tasks",new List<object>()},{"summary","今日论文已同步"}},null);
            // 让 RunPaperSyncFlow 里"今天已完成就不再走"这条判断能看到磁盘上的真相。
            Meta(PluginState)["last_arxiv_sync_date"]=JsonUtil.String(persisted,"last_sync_date","");
            Console.Out.WriteLine(JsonUtil.Serialize(new Dictionary<string,object>{{"type","progress"},{"request_id",requestId},{"current",0},{"total",1},{"message","正在获取和评分论文"}}));
            PaperSyncResult sync=RunPaperSyncFlow(PluginState,manual,date,action=="rescore");
            // attention 是**正式结果**（§4.3）：带 status=attention 正常退出，由宿主写进 job、
            // 等用户在磁贴上点按钮重开一次；这一轮 0 次 AI 调用、0 个 Todo。
            if(sync.Attention!=null)return EmitAttention(requestId,sync.Attention);
            if(!sync.Ok)return Emit(requestId,false,null,sync.Error);
            List<Dictionary<string,object>> drafts=new List<Dictionary<string,object>>();
            foreach(Dictionary<string,object> task in Tasks(PluginState))
            {
                string external=PaperExternalId(task);if(external=="")continue;
                drafts.Add(new Dictionary<string,object>{{"external_id",external},{"title",S(task,"title")},{"target",S(task,"target")},{"note",S(task,"note")},{"labels",Labels(task).Cast<object>().ToList()},{"available_from",JsonUtil.Get(task,"available_from")},{"due_at",JsonUtil.Get(task,"due_at")},{"policy",new Dictionary<string,object>{{"daily_rollover","auto_complete"},{"daily_boundary","06:00"},{"rollover_label","自动归档"},{"manual_complete_label","已读"},{"restore_resets_age",true}}}});
            }
            JsonUtil.SaveAtomic(stateFile,new Dictionary<string,object>{{"last_sync_date",date},{"updated_at",DateTimeOffset.Now.ToString("o")}});
            JsonUtil.SaveAtomic(StatePath,PluginState);if(settings.RssEnabled)EnsurePaperRssServer(false);
            string summary=JsonUtil.String(Meta(PluginState),"status","");if(summary=="")summary=sync.Summary==""?"论文同步完成":sync.Summary;
            return Emit(requestId,true,new Dictionary<string,object>{{"tasks",drafts.Cast<object>().ToList()},{"summary",summary}},null);
        }
        catch(Exception ex){return Emit(requestId,false,null,ex.Message);}
        finally{if(ResourceDir!="")try{Directory.Delete(ResourceDir,true);}catch{}}
    }
    private static int Emit(string id,bool ok,object payload,string error){Console.Out.WriteLine(JsonUtil.Serialize(new Dictionary<string,object>{{"type","result"},{"request_id",id},{"ok",ok},{"payload",payload??new Dictionary<string,object>()},{"error",error??""}}));return ok?0:1;}
    // §4.3 的 attention 信封：ok=true + status="attention"。宿主据此写 job（state/resume_action/
    // resume_input），**不**当作失败。
    private static int EmitAttention(string id,Dictionary<string,object> attention){Console.Out.WriteLine(JsonUtil.Serialize(new Dictionary<string,object>{{"type","result"},{"request_id",id},{"ok",true},{"status","attention"},{"payload",new Dictionary<string,object>{{"attention",attention}}},{"error",""}}));return 0;}

    // 本次 job 的上下文：服务解析结果（§3）与付费标记（§4.4、§5.6）。插件看不到绑定表 ——
    // 也正因为看不到，"按顺序挑 / 按 priority 挑 / 失败自动切换"这三条禁令在架构上无从违反（§8）。
    private static void ApplyJobContext(Dictionary<string,object> context,Dictionary<string,object> input)
    {
        Services=new PaperServices();
        Services.Ai=ServiceClient.Available(context,AiBindingKey);
        Services.Translation=ServiceClient.Available(context,TranslationBindingKey);
        Services.Snapshot=ServiceClient.Available(context,SnapshotBindingKey);
        Services.AiName=ServiceClient.ProviderName(context,AiBindingKey);
        Services.TranslationName=ServiceClient.ProviderName(context,TranslationBindingKey);
        Services.SnapshotName=ServiceClient.ProviderName(context,SnapshotBindingKey);
        // allow_paid_ai 能走到这里，说明宿主闸门已经放行（§5.6）：没有一次性同意标记的付费请求
        // 在 PluginHost 那层就被 provider_denied 挡掉，一个字节都不会写 job、一次都不会启动插件。
        PaidAiAllowed=JsonUtil.Bool(input,"allow_paid_ai",false);
        DeclinedToday=ServiceClient.DeclinedToday();
    }

    // 插件只保留论文业务配置 + translate_enabled 开关（§9.3）。DeepSeek / 文件服务器两段一律不再
    // 生成；旧 secret 里已有的同名段不读、不写、不删（§9.2 的"复制不删除"由迁移负责）。
    private static Dictionary<string,object> BuildPaperSettings(Dictionary<string,object> config,Dictionary<string,object> secret)
    {
        Dictionary<string,object> root=JsonUtil.Object(JsonUtil.Get(secret,"paper_settings"));if(root.Count==0)root=new Dictionary<string,object>{{"Version",2}};
        Dictionary<string,object> scoring=JsonUtil.Object(JsonUtil.Get(root,"Scoring")),rss=JsonUtil.Object(JsonUtil.Get(root,"Rss"));root["Scoring"]=scoring;root["Rss"]=rss;root.Remove("DeepSeek");root.Remove("FileServer");root["Version"]=3;
        Copy(config,"enabled",root,"Enabled");
        Copy(config,"translate_enabled",root,"TranslateEnabled");
        Copy(config,"categories",scoring,"Categories");Copy(config,"exclude_categories",scoring,"ExcludeCategories");Copy(config,"title_prompt",scoring,"TitlePrompt");Copy(config,"abstract_prompt",scoring,"AbstractPrompt");Copy(config,"title_threshold",scoring,"TitleThreshold");Copy(config,"title_batch_size",scoring,"TitleBatchSize");Copy(config,"abstract_batch_size",scoring,"AbstractBatchSize");Copy(config,"import_count",scoring,"ImportCount");Copy(config,"cache_days",scoring,"CacheDays");Copy(config,"rss_enabled",rss,"Enabled");rss["Address"]="127.0.0.1";rss["Port"]=18158;return root;
    }
    private static void Copy(Dictionary<string,object> source,string from,Dictionary<string,object> target,string to){object value=JsonUtil.Get(source,from);if(value!=null)target[to]=value;}

    // 消费者侧自检（Phase 9）：
    //   70 三家 provider 都没有 ⇒ 三个 Available 全 false，不抛、不猜、不自行造结果；
    //   71 context.services 是首选来源（环境变量只是兜底）；
    //   72 无 AI 无快照 ⇒ 老实说"今天没有可用的论文推荐结果"，**绝不**自行生成"最新 N 篇"；
    //   73 有快照插件但没命中 + 有 AI ⇒ attention，resume_action 冻结为 sync_with_ai，0 次 AI 调用；
    //   74 同一天已拒绝过 ⇒ 后台同步不再弹 attention（但磁贴入口仍在）；
    //   75 用户主动点（manual）⇒ 已拒绝过也照样询问（拒绝只影响"打扰"，不影响"入口"）；
    //   76 旧 secret 的 DeepSeek / FileServer 段不再进入业务配置。
    private static int RunPaperConsumerSelfTests()
    {
        string providerVariable=ServiceClient.EnvironmentName(TranslationBindingKey,"PROVIDER"),providerNameVariable=ServiceClient.EnvironmentName(TranslationBindingKey,"NAME");
        string previousData=Environment.GetEnvironmentVariable("RW_PLUGIN_DATA_DIR"),previousProvider=Environment.GetEnvironmentVariable(providerVariable),previousProviderName=Environment.GetEnvironmentVariable(providerNameVariable),previousDeclined=Environment.GetEnvironmentVariable(ServiceClient.DeclinedTodayVariable);
        string isolated=Path.Combine(Path.GetTempPath(),"RainmeterArxivConsumer-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(isolated);PluginDataDir=isolated;Directory.CreateDirectory(PaperCache);
            // 真实 job 路径也会先建好这份空状态再进流程（Main 里那一步），自检不能跳过它。
            PluginState=new Dictionary<string,object>{{"version",3},{"meta",new Dictionary<string,object>()},{"tasks",new List<object>()}};
            Environment.SetEnvironmentVariable("RW_PLUGIN_DATA_DIR",isolated);
            Environment.SetEnvironmentVariable(providerVariable,null);
            Environment.SetEnvironmentVariable(providerNameVariable,null);
            Environment.SetEnvironmentVariable(ServiceClient.DeclinedTodayVariable,null);
            Dictionary<string,object> empty=new Dictionary<string,object>();

            // 70：三家都没有。
            ApplyJobContext(empty,empty);
            if(Services.Ai||Services.Translation||Services.Snapshot||PaidAiAllowed||DeclinedToday)return 70;

            // 71：context.services 是首选来源。
            Dictionary<string,object> context=new Dictionary<string,object>{{"services",new Dictionary<string,object>{
                {"ai_provider",new Dictionary<string,object>{{"provider_id","io.github.test.ai"},{"provider_name","测试 AI"},{"available",true}}},
                {"paper_snapshot_provider",new Dictionary<string,object>{{"provider_id",null},{"provider_name",""},{"available",false},{"reason","not_installed"}}}}}};
            ApplyJobContext(context,new Dictionary<string,object>{{"allow_paid_ai",true}});
            if(!Services.Ai||Services.Snapshot||Services.Translation||!PaidAiAllowed)return 71;
            if(Services.AiName!="测试 AI")return 72;
            // 72：没有 context.services 时，RW_SERVICE_*_PROVIDER / _NAME 是等价兜底（§3 的两处呈现）。
            Environment.SetEnvironmentVariable(providerVariable,"io.github.test.translate");
            Environment.SetEnvironmentVariable(providerNameVariable,"测试翻译");
            ApplyJobContext(empty,empty);
            if(!Services.Translation||Services.TranslationName!="测试翻译")return 73;
            Environment.SetEnvironmentVariable(providerVariable,null);
            Environment.SetEnvironmentVariable(providerNameVariable,null);
            ApplyJobContext(empty,empty);
            if(Services.Ai||Services.Translation||Services.Snapshot)return 74;

            SavePaperSettings(new PaperSettings());
            string date=DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            // 75：无 AI 无快照 ⇒ 老实说"今天没有可用的论文推荐结果"，不是 attention、更不是自行造结果。
            PaperSyncResult none=RunPaperSyncFlow(PluginState,true,date,false);
            if(!none.Ok||none.Attention!=null||none.Summary.IndexOf("今天没有可用的论文推荐结果",StringComparison.Ordinal)<0)return 75;
            // 76-81：装了快照插件但没命中（这里连 Broker 都没有 ⇒ 走"请求失败"分支）+ 有 AI ⇒ attention。
            ApplyJobContext(context,empty);
            PaperSyncResult ask=RunPaperSyncFlow(PluginState,true,date,false);
            if(!ask.Ok||ask.Attention==null)return 76;
            if(JsonUtil.String(ask.Attention,"type","")!="paid_service_confirmation")return 77;
            if(JsonUtil.String(ask.Attention,"service","")!=AiService)return 78;
            if(JsonUtil.String(ask.Attention,"resume_action","")!=ResumeAction)return 79;
            if(!JsonUtil.Bool(JsonUtil.Object(JsonUtil.Get(ask.Attention,"resume_input")),"allow_paid_ai",false))return 80;
            if(JsonUtil.String(ask.Attention,"message","").IndexOf("测试 AI",StringComparison.Ordinal)<0)return 81;
            // 82-83：同一天已拒绝过 ⇒ 后台同步不再用 attention 打扰（磁贴上仍留着可点入口）。
            // 这个断言与时区/时段无关：窗口外会提前返回，窗口内走"已拒绝"分支，两条都不该出 attention。
            Environment.SetEnvironmentVariable(ServiceClient.DeclinedTodayVariable,"1");
            ApplyJobContext(context,empty);
            if(!DeclinedToday)return 82;
            if(RunPaperSyncFlow(PluginState,false,date,false).Attention!=null)return 83;
            // 84：用户主动点 —— 任何时段、任何拒绝记录都必须照样问（拒绝只影响"打扰"，不影响"入口"）。
            if(RunPaperSyncFlow(PluginState,true,date,false).Attention==null)return 84;
            // 85：跨天重置 —— 宿主把该变量置 0 之后，后台同步恢复询问。
            Environment.SetEnvironmentVariable(ServiceClient.DeclinedTodayVariable,null);
            ApplyJobContext(context,empty);
            if(DeclinedToday)return 85;

            // 86-89：旧凭据段被丢弃，业务配置与翻译开关保留，且旧口令一字不落地消失。
            Dictionary<string,object> legacy=new Dictionary<string,object>{{"paper_settings",new Dictionary<string,object>{
                {"Version",2},{"Enabled",true},
                {"DeepSeek",new Dictionary<string,object>{{"BaseUrl","http://example.invalid"},{"ApiKey","legacy-key"},{"Model","deepseek-chat"}}},
                {"FileServer",new Dictionary<string,object>{{"Enabled",true},{"BaseUrl","http://192.0.2.10:8900"},{"Password","legacy-password"}}},
                {"Scoring",new Dictionary<string,object>{{"Categories","cs.CV"}}}}}};
            Dictionary<string,object> merged=BuildPaperSettings(new Dictionary<string,object>{{"translate_enabled",true}},legacy);
            if(merged.ContainsKey("DeepSeek")||merged.ContainsKey("FileServer"))return 86;
            if(!JsonUtil.Bool(merged,"Enabled",false)||!JsonUtil.Bool(merged,"TranslateEnabled",false))return 87;
            if(JsonUtil.String(JsonUtil.Object(JsonUtil.Get(merged,"Scoring")),"Categories","")!="cs.CV")return 88;
            string text=JsonUtil.Serialize(merged);
            if(text.IndexOf("legacy-key",StringComparison.Ordinal)>=0||text.IndexOf("legacy-password",StringComparison.Ordinal)>=0||text.IndexOf("192.0.2.10",StringComparison.Ordinal)>=0)return 89;
            return 0;
        }
        // 异常一律落到 stderr 再去报 99：只回一个数字等于把诊断信息扔掉（套件里能看到 stderr）。
        catch(Exception ex){Console.Error.WriteLine("PaperConsumerSelfTest 异常：" + ex);return 99;}
        finally
        {
            Environment.SetEnvironmentVariable("RW_PLUGIN_DATA_DIR",previousData);
            Environment.SetEnvironmentVariable(providerVariable,previousProvider);
            Environment.SetEnvironmentVariable(providerNameVariable,previousProviderName);
            Environment.SetEnvironmentVariable(ServiceClient.DeclinedTodayVariable,previousDeclined);
            try{Directory.Delete(isolated,true);}catch{}
        }
    }

    private static string PaperExternalId(Dictionary<string,object> task){string note=S(task,"note");System.Text.RegularExpressions.Match m=System.Text.RegularExpressions.Regex.Match(note,@"arXiv ID[：:]\s*(\d{4}\.\d{4,5})");if(m.Success)return m.Groups[1].Value;m=System.Text.RegularExpressions.Regex.Match(S(task,"target"),@"/(\d{4}\.\d{4,5})");return m.Success?m.Groups[1].Value:"";}
    private static Dictionary<string,object> Meta(Dictionary<string,object> state){Dictionary<string,object> v=JsonUtil.Object(JsonUtil.Get(state,"meta"));state["meta"]=v;return v;}
    private static List<Dictionary<string,object>> Tasks(Dictionary<string,object> state){List<Dictionary<string,object>> v=JsonUtil.Array(JsonUtil.Get(state,"tasks")).Select(JsonUtil.Object).ToList();state["tasks"]=v;return v;}
    private static string S(Dictionary<string,object> v,string k){return JsonUtil.String(v,k,"");}
    private static bool B(Dictionary<string,object> v,string k){return JsonUtil.Bool(v,k,false);}
    private static List<string> Labels(Dictionary<string,object> t){return JsonUtil.Array(JsonUtil.Get(t,"labels")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct().ToList();}
    private static Dictionary<string,object> NewTask(EditorResult e,string source){return new Dictionary<string,object>{{"id",Guid.NewGuid().ToString("N")},{"title",e.Title},{"target",e.Target},{"note",e.Note},{"labels",e.Labels.Cast<object>().ToList()},{"completed",false},{"source",source},{"created_at",DateTimeOffset.Now.ToString("o")},{"completed_at",null},{"available_from",null},{"due_at",null}};}
    private static int WithLockedState(LockedStateAction action){bool refresh=false;action(PluginState,ref refresh);return 0;}
    private static void Commit(Dictionary<string,object> state){PluginState=state;}
    private static void Refresh(){}
}
