using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using RainmeterBackend;

internal static partial class TodoApp
{
    private const string GitHubRepository="kevendai/Rainmeter_todo";
    // 本插件在 plugin.json 的 address_target 里声明的地址代管目标。本体不再替插件注入地址，
    // 由插件自己拿着这个名字去问地址插件（如 SSDP 服务器 IP 同步）要主机。
    private const string AddressTarget="arxiv.file_server";
    private static string ResourceDir="";
    private static string PluginDataDir="";
    private static string StatePath { get { return Path.Combine(PluginDataDir,"rss-tasks.json"); } }
    private static string IncludePath { get { return Path.Combine(ResourceDir,"Generated.inc"); } }
    private static string PaperCache { get { return Path.Combine(PluginDataDir,"cache"); } }
    private static string PaperSyncSecret { get { return Path.Combine(PluginDataDir,"runtime-paper.secret"); } }
    private static string TranslationSecret { get { return Path.Combine(PluginDataDir,"runtime-translation.secret"); } }
    // TodoUpdateService is shared with the host for translation helpers; this
    // plugin never launches the host updater.
    private static string UpdaterExecutable { get { return ""; } }
    private static readonly string AppVersion="2.0.0";
    private static Dictionary<string,object> PluginState;
    private sealed class EditorResult { public string Title,Target,Note,Available,Due;public List<string> Labels; }
    private delegate void LockedStateAction(Dictionary<string,object> state,ref bool refresh);

    private static int Main(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=Encoding.UTF8;
        PluginDataDir=Environment.GetEnvironmentVariable("RW_PLUGIN_DATA_DIR");if(String.IsNullOrWhiteSpace(PluginDataDir))PluginDataDir=Path.Combine(Path.GetTempPath(),"RainmeterArxivPluginData");Directory.CreateDirectory(PluginDataDir);Directory.CreateDirectory(PaperCache);
        if(args.Length>0&&args[0]=="PaperRssSelfTest")return RunPaperRssSelfTests();
        if(args.Length>0&&args[0]=="PaperSettingsSelfTest")return RunPaperSettingsSelfTests();
        if(args.Length>0&&args[0]=="AddressStatusSelfTest")return RunAddressStatusSelfTests();
        // 论文链路的共享逻辑（TodoPaperService，本体与插件同源）此前没有入口跑到，这里补上，
        // 覆盖致命错误快速失败与真实错误文案（401/402/403）。
        if(args.Length>0&&args[0]=="PaperServiceSelfTest")return RunPaperSelfTests();
        if(args.Length>0&&args[0]=="PaperRssServer"){ResourceDir=PluginDataDir;return RunPaperRssServer();}
        string requestId="";
        try
        {
            Dictionary<string,object> request=JsonUtil.Object(JsonUtil.Deserialize(Console.In.ReadLine()??""));
            requestId=JsonUtil.String(request,"request_id","");
            string action=JsonUtil.String(request,"action","");if(action!="sync"&&action!="rescore"&&action!="test_api"&&action!="test_file_server"&&action!="test_translation"&&action!="validate_settings")return Emit(requestId,false,null,"不支持的 action");
            ResourceDir=Path.Combine(Path.GetTempPath(),"RainmeterArxivPlugin-"+requestId);
            Directory.CreateDirectory(ResourceDir);Directory.CreateDirectory(PaperCache);
            Dictionary<string,object> config=JsonUtil.Object(JsonUtil.Get(request,"config")),secret=JsonUtil.Object(JsonUtil.Get(request,"secret"));
            Dictionary<string,object> paper=BuildPaperSettings(config,secret);
            JsonUtil.WriteDpapiJson(PaperSyncSecret,paper);
            Dictionary<string,object> translation=JsonUtil.Object(JsonUtil.Get(secret,"translation"));string translationId=JsonUtil.String(secret,"translation_secret_id",""),translationKey=JsonUtil.String(secret,"translation_secret_key","");if(translationId!=""||translationKey!="")translation=new Dictionary<string,object>{{"SecretId",translationId},{"SecretKey",translationKey}};if(translation.Count>0)JsonUtil.WriteDpapiJson(TranslationSecret,translation);
            PluginState=new Dictionary<string,object>{{"version",3},{"meta",new Dictionary<string,object>()},{"tasks",new List<object>()}};
            string date=DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            string stateFile=Path.Combine(PluginDataDir,"state.json");
            Dictionary<string,object> persisted=File.Exists(stateFile)?JsonUtil.LoadObject(stateFile):new Dictionary<string,object>();
            Dictionary<string,object> context=JsonUtil.Object(JsonUtil.Get(request,"context"));
            if(JsonUtil.String(context,"trigger","")=="startup"&&JsonUtil.String(persisted,"last_sync_date","")==date)
                return Emit(requestId,true,new Dictionary<string,object>{{"tasks",new List<object>()},{"summary","今日论文已同步"}},null);
            Console.Out.WriteLine(JsonUtil.Serialize(new Dictionary<string,object>{{"type","progress"},{"request_id",requestId},{"current",0},{"total",1},{"message","正在获取和评分论文"}}));
            PaperSettings settings=LoadPaperSettings();
            if(action=="validate_settings"){ValidatePaperSettings(settings);return Emit(requestId,true,new Dictionary<string,object>{{"message","设置有效"},{"address",AddressStatus(paper)}},null);}
            if(action=="test_api"){TestDeepSeekConnection(settings);return Emit(requestId,true,new Dictionary<string,object>{{"message","DeepSeek 连接成功"}},null);}
            if(action=="test_file_server"){TestFileServerConnection(settings);string provider=AddressProviderName();return Emit(requestId,true,new Dictionary<string,object>{{"message",provider==""?"文件服务器连接成功":"文件服务器连接成功（地址由“"+provider+"”提供）"}},null);}
            if(action=="test_translation"){return Emit(requestId,true,new Dictionary<string,object>{{"message",TestTranslationCredentials(JsonUtil.String(translation,"SecretId",""),JsonUtil.String(translation,"SecretKey",""))}},null);}
            List<Dictionary<string,object>> cached;
            string finalPath=Path.Combine(PaperCache,date+"_papers.json");
            if(action=="rescore"&&File.Exists(finalPath))File.Delete(finalPath);
            int code=0;
            if(TryLoadPapers(finalPath,out cached)&&IsPaperFileComplete(cached,settings))ImportPapers(PluginState,cached,date,settings);
            else code=RunPaperWorker(date);
            if(code!=0)return Emit(requestId,false,null,"论文同步失败，代码 "+code);
            List<Dictionary<string,object>> drafts=new List<Dictionary<string,object>>();
            foreach(Dictionary<string,object> task in Tasks(PluginState))
            {
                string external=PaperExternalId(task);if(external=="")continue;
                drafts.Add(new Dictionary<string,object>{{"external_id",external},{"title",S(task,"title")},{"target",S(task,"target")},{"note",S(task,"note")},{"labels",Labels(task).Cast<object>().ToList()},{"available_from",JsonUtil.Get(task,"available_from")},{"due_at",JsonUtil.Get(task,"due_at")},{"policy",new Dictionary<string,object>{{"daily_rollover","auto_complete"},{"daily_boundary","06:00"},{"rollover_label","自动归档"},{"manual_complete_label","已读"},{"restore_resets_age",true}}}});
            }
            JsonUtil.SaveAtomic(stateFile,new Dictionary<string,object>{{"last_sync_date",date},{"updated_at",DateTimeOffset.Now.ToString("o")}});
            JsonUtil.SaveAtomic(StatePath,PluginState);if(settings.RssEnabled)EnsurePaperRssServer(false);
            return Emit(requestId,true,new Dictionary<string,object>{{"tasks",drafts.Cast<object>().ToList()},{"summary",JsonUtil.String(Meta(PluginState),"status","论文同步完成")}},null);
        }
        catch(Exception ex){return Emit(requestId,false,null,ex.Message);}
        finally{if(ResourceDir!="")try{Directory.Delete(ResourceDir,true);}catch{}}
    }
    private static int Emit(string id,bool ok,object payload,string error){Console.Out.WriteLine(JsonUtil.Serialize(new Dictionary<string,object>{{"type","result"},{"request_id",id},{"ok",ok},{"payload",payload??new Dictionary<string,object>()},{"error",error??""}}));return ok?0:1;}
    private static Dictionary<string,object> BuildPaperSettings(Dictionary<string,object> config,Dictionary<string,object> secret)
    {
        Dictionary<string,object> root=JsonUtil.Object(JsonUtil.Get(secret,"paper_settings"));if(root.Count==0)root=new Dictionary<string,object>{{"Version",2}};
        Dictionary<string,object> api=JsonUtil.Object(JsonUtil.Get(root,"DeepSeek")),file=JsonUtil.Object(JsonUtil.Get(root,"FileServer")),scoring=JsonUtil.Object(JsonUtil.Get(root,"Scoring")),rss=JsonUtil.Object(JsonUtil.Get(root,"Rss"));root["DeepSeek"]=api;root["FileServer"]=file;root["Scoring"]=scoring;root["Rss"]=rss;
        Copy(config,"enabled",root,"Enabled");Copy(config,"api_url",api,"BaseUrl");Copy(config,"api_model",api,"Model");Copy(secret,"api_key",api,"ApiKey");Copy(config,"max_concurrency",api,"MaxConcurrency");Copy(config,"timeout_seconds",api,"TimeoutSeconds");
        Copy(config,"file_enabled",file,"Enabled");ApplyFileAddress(config,file);Copy(config,"file_account",file,"Account");Copy(secret,"file_password",file,"Password");
        Copy(config,"categories",scoring,"Categories");Copy(config,"exclude_categories",scoring,"ExcludeCategories");Copy(config,"title_prompt",scoring,"TitlePrompt");Copy(config,"abstract_prompt",scoring,"AbstractPrompt");Copy(config,"title_threshold",scoring,"TitleThreshold");Copy(config,"title_batch_size",scoring,"TitleBatchSize");Copy(config,"abstract_batch_size",scoring,"AbstractBatchSize");Copy(config,"import_count",scoring,"ImportCount");Copy(config,"cache_days",scoring,"CacheDays");Copy(config,"rss_enabled",rss,"Enabled");rss["Address"]="127.0.0.1";rss["Port"]=18158;return root;
    }
    private static void Copy(Dictionary<string,object> source,string from,Dictionary<string,object> target,string to){object value=JsonUtil.Get(source,from);if(value!=null)target[to]=value;}
    // 文件服务器地址不再由本体注入，改由插件自己向地址插件申请主机：
    //   · 用户自己填的完整地址（协议 / 端口 / 路径）取自 config 的 file_url，其次取 secret 里
    //     从旧版本迁移过来的 FileServer.BaseUrl；空串只代表「没有值」，绝不会把已有地址抹掉
    //     （要关同步请用「启用文件同步」开关）。
    //   · 装了声明了同一个 address_target 的地址插件（例如 SSDP 服务器 IP 同步）并且它已经拿到
    //     主机时，本插件就是「被接管」状态：只把地址里的主机换成插件给的主机，其余原样保留。
    //   · 被接管的事实会记进 AddressManaged / AddressManagedBy(Name) / AddressStoredBaseUrl，
    //     随 runtime-paper.secret 一起落盘 —— 插件因此明确知道自己被谁接管、用户原本填了什么，
    //     设置界面也能据此提示「在这里改 IP 没有意义」，避免用户徒劳修改。
    //   · 被接管时换出来的地址只用于这一次运行，绝不写回用户自己的 secret，插件一禁用就恢复原样。
    private static void ApplyFileAddress(Dictionary<string,object> config,Dictionary<string,object> file)
    {
        string stored=JsonUtil.String(config,"file_url","").Trim();
        if(stored=="")stored=JsonUtil.String(file,"BaseUrl","").Trim();
        // 先展开用户可能写的 {{plugin:...}} 占位符，再让地址插件决定主机。
        string resolved=DynamicPluginValues.Resolve(stored);
        AddressProviderBinding provider=DynamicPluginValues.AddressProvider(AddressTarget);
        bool managed=provider!=null&&!String.IsNullOrWhiteSpace(provider.Value);
        if(managed)
        {
            string bound=DynamicPluginValues.BindForTarget(resolved,AddressTarget);
            if(String.IsNullOrWhiteSpace(bound))bound=resolved;
            file["BaseUrl"]=bound;
            file["AddressManaged"]=true;
            file["AddressManagedBy"]=provider.PluginId;
            file["AddressManagedByName"]=provider.PluginName;
            if(stored!="")file["AddressStoredBaseUrl"]=stored;
        }
        else
        {
            if(resolved!="")file["BaseUrl"]=resolved;
            file["AddressManaged"]=false;
            file.Remove("AddressManagedBy");file.Remove("AddressManagedByName");file.Remove("AddressStoredBaseUrl");
        }
    }
    private static string AddressProviderName()
    {
        AddressProviderBinding provider=DynamicPluginValues.AddressProvider(AddressTarget);
        return provider==null||String.IsNullOrWhiteSpace(provider.Value)?"":provider.PluginName;
    }
    // 回归自检（51-54）：地址字段的空值不得清掉已有地址，有值必须覆盖，两边都没有时保持为空。
    // 这些用例在「没有地址插件」的环境里跑，所以同时验证了未被接管时用户地址原样保留。
    private static int RunPaperSettingsSelfTests()
    {
        string previousRoot=Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT"),isolated=Path.Combine(Path.GetTempPath(),"RainmeterArxivSelfTest-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(isolated);Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",isolated);
            if(PapersBaseUrl(BuildPaperSettings(new Dictionary<string,object>{{"file_url",""}},SettingsSecret("http://192.0.2.10:8900")))!="http://192.0.2.10:8900")return 51;
            if(PapersBaseUrl(BuildPaperSettings(new Dictionary<string,object>{{"file_url","http://192.0.2.11:8901"}},SettingsSecret("http://192.0.2.10:8900")))!="http://192.0.2.11:8901")return 52;
            if(PapersBaseUrl(BuildPaperSettings(new Dictionary<string,object>{{"file_url",""}},SettingsSecret("")))!="")return 53;
            if(PapersBaseUrl(BuildPaperSettings(new Dictionary<string,object>(),SettingsSecret("http://192.0.2.10:8900")))!="http://192.0.2.10:8900")return 54;
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",previousRoot);
            try{Directory.Delete(isolated,true);}catch{}
        }
    }
    // 回归自检（55-59）：地址插件接管 / 未接管 / 值为空 / 插件被禁用 四种情形下的地址与「是否被接管」标记。
    private static int RunAddressStatusSelfTests()
    {
        string previousRoot=Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT"),isolated=Path.Combine(Path.GetTempPath(),"RainmeterArxivAddressTest-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(isolated);Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",isolated);
            // 55：根本没有地址插件 → 不接管，用户地址原样保留。
            Dictionary<string,object> plain=BuildPaperSettings(new Dictionary<string,object>(),SettingsSecret("http://192.0.2.10:8900"));
            if(PapersBaseUrl(plain)!="http://192.0.2.10:8900"||IsAddressManaged(plain))return 55;
            // 56：地址插件已启用并给出主机 → 只换主机，保留端口与路径，并标记被谁接管。
            WriteFakeAddressProvider(isolated,true,"server_ip",AddressTarget,"203.0.113.9","Fake SSDP");
            Dictionary<string,object> managed=BuildPaperSettings(new Dictionary<string,object>(),SettingsSecret("http://192.0.2.10:8900/files"));
            if(PapersBaseUrl(managed)!="http://203.0.113.9:8900/files"||!IsAddressManaged(managed))return 56;
            if(AddressField(managed,"AddressManagedBy")!="io.github.test.fake-address"||AddressField(managed,"AddressManagedByName")!="Fake SSDP"||AddressField(managed,"AddressStoredBaseUrl")!="http://192.0.2.10:8900/files")return 57;
            // 58：地址插件已启用但还没拿到主机 → 视为未接管，用户地址仍然可用。
            WriteFakeAddressProvider(isolated,true,"server_ip",AddressTarget,"","Fake SSDP");
            Dictionary<string,object> emptyProvider=BuildPaperSettings(new Dictionary<string,object>(),SettingsSecret("http://192.0.2.10:8900"));
            if(PapersBaseUrl(emptyProvider)!="http://192.0.2.10:8900"||IsAddressManaged(emptyProvider))return 58;
            // 59：地址插件被禁用 → 不再接管（哪怕它上次拿到的 IP 还在）。
            WriteFakeAddressProvider(isolated,false,"server_ip",AddressTarget,"203.0.113.9","Fake SSDP");
            Dictionary<string,object> disabled=BuildPaperSettings(new Dictionary<string,object>(),SettingsSecret("http://192.0.2.10:8900"));
            if(PapersBaseUrl(disabled)!="http://192.0.2.10:8900"||IsAddressManaged(disabled))return 59;
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT",previousRoot);
            try{Directory.Delete(isolated,true);}catch{}
        }
    }
    private static void WriteFakeAddressProvider(string root,bool enabled,string valueKey,string target,string ip,string name)
    {
        string pluginRoot=Path.Combine(root,"Plugins","io.github.test.fake-address"),versionRoot=Path.Combine(pluginRoot,"versions","1.0.0");
        Directory.CreateDirectory(versionRoot);
        JsonUtil.SaveAtomic(Path.Combine(pluginRoot,"current.json"),new Dictionary<string,object>{{"version","1.0.0"},{"enabled",enabled}});
        JsonUtil.SaveAtomic(Path.Combine(versionRoot,"plugin.json"),new Dictionary<string,object>{{"id","io.github.test.fake-address"},{"name",name},{"capabilities",new List<object>{"value_provider"}},{"address_provider",new Dictionary<string,object>{{"priority",100},{"value",valueKey},{"targets",new List<object>{target}}}}});
        JsonUtil.SaveAtomic(Path.Combine(root,"PluginValues.json"),new Dictionary<string,object>{{"entries",new Dictionary<string,object>{{("Plugin_io_github_test_fake_address_"+valueKey),new Dictionary<string,object>{{"value",ip}}}}},{"providers",new Dictionary<string,object>()}});
    }
    private static bool IsAddressManaged(Dictionary<string,object> merged){return JsonUtil.Bool(JsonUtil.Object(JsonUtil.Get(merged,"FileServer")),"AddressManaged",false);}
    private static string AddressField(Dictionary<string,object> merged,string key){return JsonUtil.String(JsonUtil.Object(JsonUtil.Get(merged,"FileServer")),key,"");}
    // 插件对「当前地址是谁给的」的自述：设置界面/日志可据此提示用户。
    private static Dictionary<string,object> AddressStatus(Dictionary<string,object> merged)
    {
        return new Dictionary<string,object>{
            {"managed",IsAddressManaged(merged)},
            {"provider",AddressField(merged,"AddressManagedBy")},
            {"provider_name",AddressField(merged,"AddressManagedByName")},
            {"effective",PapersBaseUrl(merged)},
            {"stored",AddressField(merged,"AddressStoredBaseUrl")}};
    }
    private static Dictionary<string,object> SettingsSecret(string address)
    {
        return new Dictionary<string,object>{{"paper_settings",new Dictionary<string,object>{{"Version",2},{"Enabled",true},{"FileServer",new Dictionary<string,object>{{"Enabled",true},{"BaseUrl",address},{"Account","legacy"},{"Password","legacy"}}}}}};
    }
    private static string PapersBaseUrl(Dictionary<string,object> merged){return JsonUtil.String(JsonUtil.Object(JsonUtil.Get(merged,"FileServer")),"BaseUrl","");}
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
    private static string ShowPaperScoringConsent(string message){return "use";}
    private static bool ShowPaperRescoreConsent(){return true;}
    private static bool ShowPaperOverwriteConsent(string fileName){return false;}
}
