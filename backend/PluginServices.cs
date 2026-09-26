using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using RainmeterBackend;

internal static class PluginMarketCache
{
    internal static string Decode(byte[] raw)
    {
        string json=new UTF8Encoding(false,true).GetString(raw);
        JsonUtil.Deserialize(json);
        return json;
    }

    internal static void Save(string path,string json)
    {
        JsonUtil.Deserialize(json);
        string directory=Path.GetDirectoryName(path);
        if(!String.IsNullOrWhiteSpace(directory))Directory.CreateDirectory(directory);
        string temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary,json,RuntimeUtil.Utf8NoBom);
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }
        finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch{}}
    }

    internal static string LoadLocal(string cachePath,string bundledPath,out string sourcePath,out string sourceLabel)
    {
        sourcePath=cachePath;sourceLabel="使用本地市场缓存；";
        if(File.Exists(cachePath))try
        {
            string cached=File.ReadAllText(cachePath,RuntimeUtil.Utf8NoBom);JsonUtil.Deserialize(cached);return cached;
        }
        catch{try{File.Delete(cachePath);}catch{}}
        sourcePath=bundledPath;sourceLabel="使用内置官方索引；";
        if(!File.Exists(bundledPath))throw new FileNotFoundException("找不到本地插件市场索引。",bundledPath);
        string bundled=File.ReadAllText(bundledPath,RuntimeUtil.Utf8NoBom);JsonUtil.Deserialize(bundled);return bundled;
    }
}

internal static partial class TodoApp
{
    private const string PluginRegistryUrl="https://kevendai.github.io/Rainmeter_todo-plugin-registry/index-v1.json";
    private static void EnableTls12(){ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;}

    private static string PluginDisplayStatus(Dictionary<string,object> state)
    {
        string path=Path.Combine(PluginPaths.Jobs,"io.github.kevendai.arxiv.json");
        if(!File.Exists(path))return JsonUtil.String(JsonUtil.Object(JsonUtil.Get(state,"meta")),"status","就绪");
        try
        {
            Dictionary<string,object> job=JsonUtil.LoadObject(path);
            string value=JsonUtil.String(job,"message","");
            return PluginNames.Humanize(value==""?"插件状态："+JsonUtil.String(job,"state","未知"):value);
        }
        catch{return "插件状态暂不可读";}
    }

    private static object PluginSettingValue(string pluginId,string key,Dictionary<string,object> direct,Dictionary<string,object> secrets,Dictionary<string,object> property)
    {
        object value=JsonUtil.Get(direct,key);if(value!=null)return value;if(pluginId!="io.github.kevendai.arxiv")return JsonUtil.Get(property,"default");
        Dictionary<string,object> root=JsonUtil.Object(JsonUtil.Get(secrets,"paper_settings")),translation=JsonUtil.Object(JsonUtil.Get(secrets,"translation"));Dictionary<string,object> section=root;string legacy=key;
        if(key=="api_url"||key=="api_model"||key=="api_key"||key=="max_concurrency"||key=="timeout_seconds"){section=JsonUtil.Object(JsonUtil.Get(root,"DeepSeek"));legacy=key=="api_url"?"BaseUrl":key=="api_model"?"Model":key=="api_key"?"ApiKey":key=="max_concurrency"?"MaxConcurrency":"TimeoutSeconds";}
        else if(key.StartsWith("file_",StringComparison.Ordinal)){section=JsonUtil.Object(JsonUtil.Get(root,"FileServer"));legacy=key=="file_enabled"?"Enabled":key=="file_url"?"BaseUrl":key=="file_account"?"Account":"Password";}
        else if(key=="rss_enabled"){section=JsonUtil.Object(JsonUtil.Get(root,"Rss"));legacy="Enabled";}
        else if(key=="translate_enabled"){section=root;legacy="TranslateEnabled";}
        else if(key=="translation_secret_id"||key=="translation_secret_key"){section=translation;legacy=key.EndsWith("_id",StringComparison.Ordinal)?"SecretId":"SecretKey";}
        else if(key!="enabled"){section=JsonUtil.Object(JsonUtil.Get(root,"Scoring"));Dictionary<string,string> names=new Dictionary<string,string>{{"categories","Categories"},{"exclude_categories","ExcludeCategories"},{"title_prompt","TitlePrompt"},{"abstract_prompt","AbstractPrompt"},{"title_threshold","TitleThreshold"},{"title_batch_size","TitleBatchSize"},{"abstract_batch_size","AbstractBatchSize"},{"import_count","ImportCount"},{"cache_days","CacheDays"}};if(names.ContainsKey(key))legacy=names[key];}
        else legacy="Enabled";
        value=JsonUtil.Get(section,legacy);return value??JsonUtil.Get(property,"default");
    }

    private static int UiMarketRefresh(string resultPath)
    {
        try
        {
            EnableTls12();
            using(WebClient web=new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent]="RainmeterDesktopWidgets/"+AppVersion;
                PluginMarketCache.Save(PluginPaths.RegistryCache,PluginMarketCache.Decode(web.DownloadData(PluginRegistryUrl)));
            }
            JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",true}});return 0;
        }
        catch(Exception ex){try{JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",false},{"error",ex.Message}});}catch{}return 1;}
    }

    private static int UiMarketInstall(string pluginId,string resultPath)
    {
        try
        {
            string sourcePath,sourceLabel;
            string bundledPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"plugin-registry-v1.json");
            string json=PluginMarketCache.LoadLocal(PluginPaths.RegistryCache,bundledPath,out sourcePath,out sourceLabel);
            Dictionary<string,object> index=JsonUtil.Object(JsonUtil.Deserialize(json));
            Dictionary<string,object> record=JsonUtil.Array(JsonUtil.Get(index,"plugins")).Select(JsonUtil.Object)
                .FirstOrDefault(p=>JsonUtil.String(p,"id","")==pluginId&&JsonUtil.Bool(p,"official",false));
            if(record==null)throw new Exception("本地市场中找不到该官方插件，请先刷新市场。");
            string required;
            if(!MarketCompatible(record,out required))throw new Exception("该插件需要主程序 "+required+" 或更高版本。");
            string url=JsonUtil.String(record,"download",""),sha=JsonUtil.String(record,"sha256","");Uri uri;
            if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||!uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||uri.AbsolutePath.IndexOf("/releases/download/",StringComparison.OrdinalIgnoreCase)<0)
                throw new Exception("市场下载地址必须是 GitHub HTTPS Release。");
            if(!System.Text.RegularExpressions.Regex.IsMatch(sha,@"^[a-fA-F0-9]{64}$"))throw new Exception("市场条目缺少有效 SHA256。");
            EnableTls12();string temporary=Path.Combine(Path.GetTempPath(),"rw-market-"+Guid.NewGuid().ToString("N")+".rwplugin");
            try
            {
                using(WebClient web=new WebClient()){web.Headers[HttpRequestHeader.UserAgent]="RainmeterDesktopWidgets/"+AppVersion;web.DownloadFile(uri,temporary);}
                PluginPackageInstaller.Install(temporary,PluginPaths.Plugins,AppVersion,sha);
            }
            finally{try{File.Delete(temporary);}catch{}}
            JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",true}});return 0;
        }
        catch(Exception ex){try{JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",false},{"error",ex.Message}});}catch{}return 1;}
    }

    private static bool MarketCompatible(Dictionary<string,object> record,out string required)
    {
        required=record==null?"":JsonUtil.String(record,"min_host_version","");Version host,min;
        return Version.TryParse(AppVersion,out host)&&Version.TryParse(required,out min)&&host.CompareTo(min)>=0;
    }
}
