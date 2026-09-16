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
            if(action=="validate_settings"){ValidatePaperSettings(settings);return Emit(requestId,true,new Dictionary<string,object>{{"message","设置有效"}},null);}
            if(action=="test_api"){TestDeepSeekConnection(settings);return Emit(requestId,true,new Dictionary<string,object>{{"message","DeepSeek 连接成功"}},null);}
            if(action=="test_file_server"){TestFileServerConnection(settings);return Emit(requestId,true,new Dictionary<string,object>{{"message","文件服务器连接成功"}},null);}
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
        Copy(config,"file_enabled",file,"Enabled");CopyAddress(config,"file_url",file,"BaseUrl");Copy(config,"file_account",file,"Account");Copy(secret,"file_password",file,"Password");
        Copy(config,"categories",scoring,"Categories");Copy(config,"exclude_categories",scoring,"ExcludeCategories");Copy(config,"title_prompt",scoring,"TitlePrompt");Copy(config,"abstract_prompt",scoring,"AbstractPrompt");Copy(config,"title_threshold",scoring,"TitleThreshold");Copy(config,"title_batch_size",scoring,"TitleBatchSize");Copy(config,"abstract_batch_size",scoring,"AbstractBatchSize");Copy(config,"import_count",scoring,"ImportCount");Copy(config,"cache_days",scoring,"CacheDays");Copy(config,"rss_enabled",rss,"Enabled");rss["Address"]="127.0.0.1";rss["Port"]=18158;return root;
    }
    private static void Copy(Dictionary<string,object> source,string from,Dictionary<string,object> target,string to){object value=JsonUtil.Get(source,from);if(value!=null)target[to]=value;}
    // 服务器地址这一项由宿主代管：装了地址插件（SSDP）时，宿主会把已保存的地址改写主机后塞回 file_url，
    // 同时在设置界面隐藏该项、不把它写进 config.json。宿主没有值的时候仍然会塞一个空串进来——那个空串
    // 只代表「宿主这边没有值」，不代表「用户清空了地址」。若照抄进 BaseUrl，从旧版本迁移过来的地址就会被
    // 抹掉，随后「启用中 + 地址为空」会让设置校验直接报错，远端拉取、上传、状态检查全部停用。
    // 所以要关闭文件同步请用「启用文件同步」开关，清空地址一律视为未设置。
    private static void CopyAddress(Dictionary<string,object> source,string from,Dictionary<string,object> target,string to)
    {
        string value=JsonUtil.String(source,from,"").Trim();if(value=="")return;target[to]=value;
    }
    // 回归自检：宿主注入的 file_url 为空串时不得清掉已有地址，有值时必须覆盖，两边都没有时保持为空。
    private static int RunPaperSettingsSelfTests()
    {
        if(PapersBaseUrl(BuildPaperSettings(new Dictionary<string,object>{{"file_url",""}},SettingsSecret("http://192.0.2.10:8900")))!="http://192.0.2.10:8900")return 51;
        if(PapersBaseUrl(BuildPaperSettings(new Dictionary<string,object>{{"file_url","http://192.0.2.11:8901"}},SettingsSecret("http://192.0.2.10:8900")))!="http://192.0.2.11:8901")return 52;
        if(PapersBaseUrl(BuildPaperSettings(new Dictionary<string,object>{{"file_url",""}},SettingsSecret("")))!="")return 53;
        if(PapersBaseUrl(BuildPaperSettings(new Dictionary<string,object>(),SettingsSecret("http://192.0.2.10:8900")))!="http://192.0.2.10:8900")return 54;
        return 0;
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
