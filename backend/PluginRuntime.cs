using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RainmeterBackend
{
    internal static class PluginPaths
    {
        public static readonly string Root = String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainmeterDesktopWidgets")
            : Path.GetFullPath(Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT"));
        public static string Plugins { get { return Path.Combine(Root, "Plugins"); } }
        public static string Data { get { return Path.Combine(Root, "PluginData"); } }
        public static string Jobs { get { return Path.Combine(Root, "PluginJobs"); } }
        public static string Logs { get { return Path.Combine(Root, "PluginLogs"); } }
        public static string Values { get { return Path.Combine(Root, "PluginValues.json"); } }
        public static string RegistryCache { get { return Path.Combine(Root, "registry-cache.json"); } }
        public static void Ensure() { foreach (string p in new[] { Root, Plugins, Data, Jobs, Logs }) Directory.CreateDirectory(p); }
        public static string PluginRoot(string id) { return Path.Combine(Plugins, id); }
        public static string VersionRoot(string id, string version) { return Path.Combine(PluginRoot(id), "versions", version); }
        public static string DataRoot(string id) { return Path.Combine(Data, id); }
    }

    internal sealed class PluginManifest
    {
        public string Id, Name, Version, MinHostVersion, Entry, SettingsSchema, Homepage, AddressValueKey, AddressTarget;
        public int ApiVersion, AddressPriority;
        public bool DefaultEnabled;
        public List<string> Capabilities = new List<string>();
        public List<string> Permissions = new List<string>();
        public List<string> AddressTargets = new List<string>();
        public List<Dictionary<string,object>> Actions = new List<Dictionary<string,object>>();

        public static PluginManifest Load(string root)
        {
            string path = Path.Combine(root, "plugin.json");
            if (!File.Exists(path)) throw new InvalidDataException("插件缺少 plugin.json");
            Dictionary<string, object> v = JsonUtil.LoadObject(path);
            PluginManifest m = new PluginManifest {
                Id=JsonUtil.String(v,"id",""), Name=JsonUtil.String(v,"name",""), Version=JsonUtil.String(v,"version",""),
                ApiVersion=JsonUtil.Int(v,"api_version",0), MinHostVersion=JsonUtil.String(v,"min_host_version",""),
                Entry=JsonUtil.String(v,"entry",""), SettingsSchema=JsonUtil.String(v,"settings_schema",""),
                Homepage=JsonUtil.String(v,"homepage",""), DefaultEnabled=JsonUtil.Bool(v,"default_enabled",false)
            };
            m.Capabilities=JsonUtil.Array(JsonUtil.Get(v,"capabilities")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).ToList();
            m.Permissions=JsonUtil.Array(JsonUtil.Get(v,"permissions")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).ToList();
            m.Actions=JsonUtil.Array(JsonUtil.Get(v,"actions")).Select(JsonUtil.Object).ToList();Dictionary<string,object> address=JsonUtil.Object(JsonUtil.Get(v,"address_provider"));m.AddressPriority=JsonUtil.Int(address,"priority",0);m.AddressValueKey=JsonUtil.String(address,"value","");m.AddressTargets=JsonUtil.Array(JsonUtil.Get(address,"targets")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            m.AddressTarget=JsonUtil.String(v,"address_target","").Trim();
            m.Validate(root); return m;
        }

        private void Validate(string root)
        {
            if(!Regex.IsMatch(Id??"",@"^[a-z0-9]+(?:[.-][a-z0-9]+)+$"))throw new InvalidDataException("插件 ID 格式无效");
            if(!Regex.IsMatch(Version??"",@"^\d+\.\d+\.\d+$"))throw new InvalidDataException("插件版本必须为 x.y.z");
            if(ApiVersion!=1)throw new InvalidDataException("不支持的插件 API 版本");
            if(!Regex.IsMatch(MinHostVersion??"",@"^\d+\.\d+\.\d+$")||CompareVersion(MinHostVersion,PluginRuntime.HostVersion)>0)throw new InvalidDataException("插件要求更高版本的宿主");
            if(String.IsNullOrWhiteSpace(Name))throw new InvalidDataException("插件名称不能为空");
            HashSet<string> allowed=new HashSet<string>(new[]{"todo_source","todo_transform","value_provider"},StringComparer.OrdinalIgnoreCase);
            if(Capabilities.Count==0||Capabilities.Any(x=>!allowed.Contains(x)))throw new InvalidDataException("插件 capability 无效");if(AddressTargets.Count>0&&(!Capabilities.Contains("value_provider")||!Regex.IsMatch(AddressValueKey??"",@"^[A-Za-z0-9_.-]{1,80}$")))throw new InvalidDataException("地址提供者声明无效");
            if(!String.IsNullOrEmpty(AddressTarget)&&!Regex.IsMatch(AddressTarget,@"^[a-z0-9]+(?:[._-][a-z0-9]+)+$"))throw new InvalidDataException("插件 address_target 格式无效");
            if(!File.Exists(SafeChildPath(root,Entry,"插件入口")))throw new InvalidDataException("插件入口不存在");
            if(!String.IsNullOrWhiteSpace(SettingsSchema)&&!File.Exists(SafeChildPath(root,SettingsSchema,"设置 Schema")))throw new InvalidDataException("设置 Schema 不存在");
        }
        private static int CompareVersion(string left,string right){int[] a=left.Split('.').Select(Int32.Parse).ToArray(),b=right.Split('.').Select(Int32.Parse).ToArray();for(int i=0;i<3;i++){int value=a[i].CompareTo(b[i]);if(value!=0)return value;}return 0;}

        public static string SafeChildPath(string root,string relative,string label)
        {
            if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))throw new InvalidDataException(label+"路径无效");
            string parent=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(Path.Combine(root,relative));
            if(!full.StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(label+"超出插件目录");
            return full;
        }
    }

    internal sealed class PluginCallResult
    {
        public bool Ok;
        public Dictionary<string,object> Payload=new Dictionary<string,object>();
        public string Error="";
    }

    internal static class PluginRuntime
    {
        public static readonly string HostVersion = LoadHostVersion();
        private const int MaxLineChars=1024*1024,MaxOutputChars=4*1024*1024,MaxLogChars=1024*1024;
        private static string LoadHostVersion(){string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"app-version.txt");if(File.Exists(path)){string value=File.ReadAllText(path,Encoding.UTF8).Trim();if(Regex.IsMatch(value,@"^\d+\.\d+\.\d+$"))return value;}return "0.0.0";}
        public static Dictionary<string,object> Current(string id){string p=Path.Combine(PluginPaths.PluginRoot(id),"current.json");return File.Exists(p)?JsonUtil.LoadObject(p):new Dictionary<string,object>();}
        public static PluginManifest Resolve(string id,bool enabled)
        {
            Dictionary<string,object> c=Current(id);if(c.Count==0)throw new InvalidOperationException("插件未安装："+id);
            if(enabled&&!JsonUtil.Bool(c,"enabled",false))throw new InvalidOperationException("插件未启用："+id);
            string version=JsonUtil.String(c,"version","");PluginManifest m=PluginManifest.Load(PluginPaths.VersionRoot(id,version));
            if(!m.Id.Equals(id,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("插件 ID 与安装目录不一致");
            return m;
        }

        // 插件在 plugin.json 的 address_target 里声明自己的地址由谁代管（例如 arxiv.file_server）。
        // 空串表示该插件不接受地址插件代管，本体不做任何地址改写。
        public static string AddressTargetOf(string id)
        {
            try
            {
                Dictionary<string,object> current=Current(id);if(current.Count==0)return "";
                PluginManifest manifest=PluginManifest.Load(PluginPaths.VersionRoot(id,JsonUtil.String(current,"version","")));
                return String.IsNullOrWhiteSpace(manifest.AddressTarget)?"":manifest.AddressTarget;
            }
            catch{return "";}
        }

        public static PluginCallResult Invoke(string id,string action,object input,string trigger,int timeoutSeconds,Action<Dictionary<string,object>> progress)
        {
            PluginPaths.Ensure();bool settingsAction=String.Equals(action,"validate_settings",StringComparison.OrdinalIgnoreCase)||String.Equals(action,"configure_account",StringComparison.OrdinalIgnoreCase)||String.Equals(action,"configure_discovery",StringComparison.OrdinalIgnoreCase);PluginManifest m=Resolve(id,!settingsAction);string root=PluginPaths.VersionRoot(id,m.Version);
            string entry=PluginManifest.SafeChildPath(root,m.Entry,"插件入口"),requestId=Guid.NewGuid().ToString("N");
            // 本体不向插件注入任何地址：声明了 address_target 的插件自己决定用哪个地址
            // （通过 DynamicPluginValues.AddressProvider 向地址插件申请，并自行记录是否被接管）。
            // 这里只做用户自己写的 {{plugin:...}} 占位符替换，不再替插件绑定/改写地址。
            Dictionary<string,object> resolvedConfig=ReadObject(Path.Combine(PluginPaths.DataRoot(id),"config.json"));DynamicPluginValues.ResolveObject(resolvedConfig);
            Dictionary<string,object> request=new Dictionary<string,object>{
                {"api_version",1},{"request_id",requestId},{"plugin_id",id},{"action",action},
                {"context",new Dictionary<string,object>{{"host_version",HostVersion},{"locale",CultureInfo.CurrentUICulture.Name},{"now",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)},{"trigger",trigger??"manual"}}},
                {"config",resolvedConfig},{"secret",ReadSecret(Path.Combine(PluginPaths.DataRoot(id),"secret.dat"))},{"input",input??new Dictionary<string,object>()}};
            List<string> lines=new List<string>();StringBuilder errors=new StringBuilder();object gate=new object();Exception progressError=null;
            ProcessStartInfo info=new ProcessStartInfo(entry){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            ApplyPluginEnvironment(info,PluginPaths.DataRoot(id),UiScale.Current.ToString("0.###",CultureInfo.InvariantCulture));
            using(Process p=new Process{StartInfo=info,EnableRaisingEvents=true})
            {
                int total=0;
                if(!p.Start())throw new InvalidOperationException("无法启动插件");if(progress!=null)progress(new Dictionary<string,object>{{"type","host_start"},{"pid",p.Id},{"entry",entry},{"process_started_at",p.StartTime.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture)},{"current",0},{"total",0},{"message","插件进程已启动"}});
                StreamReader stdout=new StreamReader(p.StandardOutput.BaseStream,Encoding.UTF8,false,4096),stderr=new StreamReader(p.StandardError.BaseStream,Encoding.UTF8,false,4096);
                Thread outputThread=new Thread(delegate(){string line;while((line=stdout.ReadLine())!=null){Dictionary<string,object> liveProgress=null;lock(gate){total+=line.Length;if(line.Length>MaxLineChars||total>MaxOutputChars)lines.Add("__OVERSIZE__");else{lines.Add(line);try{Dictionary<string,object> live=JsonUtil.Object(JsonUtil.Deserialize(line));if(JsonUtil.String(live,"type","")=="progress"&&JsonUtil.String(live,"request_id","")==requestId)liveProgress=live;}catch{}}}if(liveProgress!=null&&progress!=null)try{progress(liveProgress);}catch(Exception ex){lock(gate){if(progressError==null)progressError=ex;}}}}),errorThread=new Thread(delegate(){char[] buffer=new char[2048];int count;while((count=stderr.Read(buffer,0,buffer.Length))>0)lock(gate){if(errors.Length<MaxLogChars)errors.Append(buffer,0,Math.Min(count,MaxLogChars-errors.Length));}});outputThread.IsBackground=true;errorThread.IsBackground=true;outputThread.Start();errorThread.Start();
                byte[] requestBytes=Encoding.UTF8.GetBytes(JsonUtil.Serialize(request)+"\n");p.StandardInput.BaseStream.Write(requestBytes,0,requestBytes.Length);p.StandardInput.BaseStream.Close();
                if(!p.WaitForExit(Math.Max(1,timeoutSeconds)*1000)){try{p.Kill();}catch{}throw new TimeoutException("插件执行超时");}
                p.WaitForExit();outputThread.Join(2000);errorThread.Join(2000);File.WriteAllText(Path.Combine(PluginPaths.Logs,id+".log"),errors.ToString(),RuntimeUtil.Utf8NoBom);
                if(progressError!=null)throw new InvalidOperationException("插件进度处理失败",progressError);
                PluginCallResult result=null;
                foreach(string line in lines)
                {
                    if(line=="__OVERSIZE__")throw new InvalidDataException("插件输出超过限制");
                    Dictionary<string,object> msg;try{msg=JsonUtil.Object(JsonUtil.Deserialize(line));}catch{throw new InvalidDataException("插件 stdout 包含非法 JSON");}
                    if(JsonUtil.String(msg,"request_id","")!=requestId)throw new InvalidDataException("插件响应 request_id 不匹配");
                    string type=JsonUtil.String(msg,"type","");
                    if(type=="progress")continue;
                    if(type!="result")throw new InvalidDataException("未知插件响应类型");
                    if(result!=null)throw new InvalidDataException("插件返回了多个最终结果");
                    result=new PluginCallResult{Ok=JsonUtil.Bool(msg,"ok",false),Payload=JsonUtil.Object(JsonUtil.Get(msg,"payload")),Error=JsonUtil.String(msg,"error","")};
                }
                if(result==null)throw new InvalidDataException("插件未返回最终结果");
                if(result.Ok){ApplyConfigUpdates(id,result.Payload);ApplySecretUpdates(id,result.Payload);}
                if(p.ExitCode!=0&&result.Ok)throw new InvalidDataException("插件异常退出："+p.ExitCode.ToString(CultureInfo.InvariantCulture));
                return result;
            }
        }

        // 插件进程需要 RW_PLUGIN_DATA_DIR / RW_WINDOW_SCALE 两个环境变量。不能直接写
        // ProcessStartInfo.EnvironmentVariables：该属性懒加载时会把父进程环境块塞进
        // StringDictionary（内部把键转小写），而 Windows 的环境块允许同时存在大小写不同的
        // 同名变量（例如 Git Bash、部分安装器、CI 代理会额外注入一份 PATH），此时 Add 会抛
        // ArgumentException，导致所有插件一次也启动不了。这里自己构造一份「大小写去重 +
        // 追加 RW_*」的干净环境块，反射写入 ProcessStartInfo 的私有字段以绕开该实现缺陷；
        // 反射不可用时退回官方属性。
        internal static void ApplyPluginEnvironment(ProcessStartInfo info,string dataDir,string windowScale)
        {
            StringDictionary environment=BuildChildEnvironment(Environment.GetEnvironmentVariables(),dataDir,windowScale);
            if(environment!=null&&TryAssignEnvironment(info,environment))return;
            try
            {
                info.EnvironmentVariables["RW_PLUGIN_DATA_DIR"]=dataDir;
                info.EnvironmentVariables["RW_WINDOW_SCALE"]=windowScale;
            }
            catch(ArgumentException ex){throw new InvalidOperationException("无法为插件进程构造环境变量，宿主环境块中存在大小写重复的变量名",ex);}
        }

        // 同名不同大小写只保留最先出现的一项，与 Windows 解析环境变量的顺序一致，
        // 因此子进程拿到的值与直接继承父进程时完全相同。
        internal static StringDictionary BuildChildEnvironment(IDictionary source,string dataDir,string windowScale)
        {
            try
            {
                StringDictionary environment=new StringDictionary();HashSet<string> seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if(source!=null)foreach(DictionaryEntry entry in source)
                {
                    string key=entry.Key as string;if(String.IsNullOrEmpty(key)||!seen.Add(key))continue;
                    environment[key]=entry.Value as string??Convert.ToString(entry.Value,CultureInfo.InvariantCulture)??"";
                }
                if(!String.IsNullOrEmpty(dataDir))environment["RW_PLUGIN_DATA_DIR"]=dataDir;
                if(!String.IsNullOrEmpty(windowScale))environment["RW_WINDOW_SCALE"]=windowScale;
                return environment;
            }
            catch(Exception){return null;}
        }

        private static bool TryAssignEnvironment(ProcessStartInfo info,StringDictionary environment)
        {
            try
            {
                foreach(string name in new[]{"environmentVariables","_environmentVariables"})
                {
                    FieldInfo field=typeof(ProcessStartInfo).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
                    if(field==null||!field.FieldType.IsInstanceOfType(environment))continue;
                    field.SetValue(info,environment);return true;
                }
            }
            catch(Exception){}
            return false;
        }

        public static void BootstrapBundled(string bundledRoot)
        {
            PluginPaths.Ensure();if(!Directory.Exists(bundledRoot))return;
            foreach(string source in Directory.GetDirectories(bundledRoot))
            {
                PluginManifest m=PluginManifest.Load(source);string destination=PluginPaths.VersionRoot(m.Id,m.Version);
                if(!Directory.Exists(destination))CopyDirectory(source,destination);
                string current=Path.Combine(PluginPaths.PluginRoot(m.Id),"current.json");
                if(!File.Exists(current))JsonUtil.SaveAtomic(current,new Dictionary<string,object>{{"version",m.Version},{"enabled",m.DefaultEnabled}});
                else{Dictionary<string,object> installed=JsonUtil.LoadObject(current);string installedVersion=JsonUtil.String(installed,"version","0.0.0");if(CompareSemver(installedVersion,m.Version)<0){installed["version"]=m.Version;JsonUtil.SaveAtomic(current,installed);}}
                Directory.CreateDirectory(PluginPaths.DataRoot(m.Id));
                if(m.Id=="io.github.kevendai.arxiv")MigrateLegacyPaperSettings(Path.GetDirectoryName(bundledRoot),PluginPaths.DataRoot(m.Id));
            }
            RemoveObsoleteIpPluginPrograms();
        }
        private static int CompareSemver(string left,string right){int[] a,b;try{a=left.Split('.').Select(Int32.Parse).ToArray();b=right.Split('.').Select(Int32.Parse).ToArray();}catch{return -1;}for(int i=0;i<3;i++){int value=a[i].CompareTo(b[i]);if(value!=0)return value;}return 0;}
        public static void MigrateInstallation(string resourceRoot,string todoPath,string calendarStatePath)
        {
            PluginPaths.Ensure();string marker=Path.Combine(PluginPaths.Root,"migration-v2.json");if(File.Exists(marker))return;
            string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture),backup=Path.Combine(PluginPaths.Root,"MigrationBackups",stamp);Directory.CreateDirectory(backup);
            foreach(string path in new[]{todoPath,calendarStatePath,Path.Combine(resourceRoot,"paper-sync.secret"),Path.Combine(resourceRoot,"translation.secret")})if(!String.IsNullOrWhiteSpace(path)&&File.Exists(path))File.Copy(path,Path.Combine(backup,Path.GetFileName(path)),true);
            if(File.Exists(todoPath))
            {
                Dictionary<string,object> state=JsonUtil.LoadObject(todoPath);int before=JsonUtil.Array(JsonUtil.Get(state,"tasks")).Count;MigrateV3Candidate(state);string temporary=todoPath+".migration-"+Guid.NewGuid().ToString("N")+".tmp";
                try{File.WriteAllText(temporary,JsonUtil.Serialize(state),RuntimeUtil.Utf8NoBom);Dictionary<string,object> checkedState=JsonUtil.LoadObject(temporary);if(JsonUtil.Int(checkedState,"version",0)!=3||JsonUtil.Array(JsonUtil.Get(checkedState,"tasks")).Count!=before)throw new InvalidDataException("迁移后的 tasks.json 校验失败");try{File.Replace(temporary,todoPath,null);}catch{File.Copy(temporary,todoPath,true);File.Delete(temporary);}}finally{try{File.Delete(temporary);}catch{}}
            }
            JsonUtil.SaveAtomic(marker,new Dictionary<string,object>{{"version",2},{"completed_at",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)},{"backup",backup}});
        }
        private static void MigrateV3Candidate(Dictionary<string,object> state){TodoExternalImport.MigrateV3(state);}
        private static void MigrateLegacyPaperSettings(string resourceRoot,string dataRoot)
        {
            string target=Path.Combine(dataRoot,"secret.dat");if(File.Exists(target)||String.IsNullOrWhiteSpace(resourceRoot))return;
            string paper=Path.Combine(resourceRoot,"paper-sync.secret"),translation=Path.Combine(resourceRoot,"translation.secret");
            Dictionary<string,object> migrated=new Dictionary<string,object>();
            if(File.Exists(paper))migrated["paper_settings"]=JsonUtil.ReadDpapiJson(paper);
            if(File.Exists(translation))migrated["translation"]=JsonUtil.ReadDpapiJson(translation);
            if(migrated.Count>0)JsonUtil.WriteDpapiJson(target,migrated);
            string oldCache=Path.Combine(resourceRoot,"PaperCache"),newCache=Path.Combine(dataRoot,"cache");
            if(Directory.Exists(oldCache)&&!Directory.Exists(newCache))CopyDirectory(oldCache,newCache);
        }
        private static void CopyDirectory(string source,string destination){Directory.CreateDirectory(destination);foreach(string f in Directory.GetFiles(source))File.Copy(f,Path.Combine(destination,Path.GetFileName(f)),false);foreach(string d in Directory.GetDirectories(source))CopyDirectory(d,Path.Combine(destination,Path.GetFileName(d)));}
        private static Dictionary<string,object> ReadObject(string p){return File.Exists(p)?JsonUtil.LoadObject(p):new Dictionary<string,object>();}
        private static Dictionary<string,object> ReadSecret(string p){if(!File.Exists(p))return new Dictionary<string,object>();try{return JsonUtil.ReadDpapiJson(p);}catch{throw new InvalidDataException("插件敏感配置无法解密");}}
        private static void ApplyConfigUpdates(string id,Dictionary<string,object> payload)
        {
            Dictionary<string,object> updates=JsonUtil.Object(JsonUtil.Get(payload,"config_updates"));payload.Remove("config_updates");if(updates.Count==0)return;
            if(updates.Count>64||JsonUtil.Serialize(updates).Length>65536)throw new InvalidDataException("插件配置更新超过限制");
            foreach(KeyValuePair<string,object> pair in updates){if(!Regex.IsMatch(pair.Key??"",@"^[A-Za-z0-9_.-]{1,80}$"))throw new InvalidDataException("插件配置键无效");if(pair.Value is Dictionary<string,object>||pair.Value is object[])throw new InvalidDataException("插件配置值必须是字符串、数字或布尔值");}
            string data=PluginPaths.DataRoot(id),path=Path.Combine(data,"config.json");Directory.CreateDirectory(data);
            using(Mutex mutex=new Mutex(false,@"Global\RainmeterPluginConfig_"+Regex.Replace(id,@"[^A-Za-z0-9]","_")))
            {
                bool held=false;try{try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){held=true;}if(!held)throw new TimeoutException("插件配置正忙");Dictionary<string,object> config=ReadObject(path);foreach(KeyValuePair<string,object> pair in updates)config[pair.Key]=pair.Value;JsonUtil.SaveAtomic(path,config);}finally{if(held)mutex.ReleaseMutex();}
            }
        }
        private static void ApplySecretUpdates(string id,Dictionary<string,object> payload)
        {
            Dictionary<string,object> updates=JsonUtil.Object(JsonUtil.Get(payload,"secret_updates"));payload.Remove("secret_updates");if(updates.Count==0)return;
            if(updates.Count>64||JsonUtil.Serialize(updates).Length>65536)throw new InvalidDataException("插件敏感配置更新超过限制");
            foreach(string key in updates.Keys)if(!Regex.IsMatch(key??"",@"^[A-Za-z0-9_.-]{1,80}$"))throw new InvalidDataException("插件敏感配置键无效");
            string data=PluginPaths.DataRoot(id),path=Path.Combine(data,"secret.dat");Directory.CreateDirectory(data);
            using(Mutex mutex=new Mutex(false,@"Global\RainmeterPluginSecret_"+Regex.Replace(id,@"[^A-Za-z0-9]","_")))
            {
                bool held=false;try{try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){held=true;}if(!held)throw new TimeoutException("插件敏感配置正忙");Dictionary<string,object> secret=ReadSecret(path);foreach(KeyValuePair<string,object> pair in updates){string text=pair.Value as string;if(pair.Value==null||(text!=null&&text.Length==0))secret.Remove(pair.Key);else secret[pair.Key]=pair.Value;}JsonUtil.WriteDpapiJson(path,secret);}finally{if(held)mutex.ReleaseMutex();}
            }
        }
        private static void RemoveObsoleteIpPluginPrograms()
        {
            foreach(string oldId in new[]{"io.github.kevendai.network-ip","io.github.kevendai.xiaomi-router-wan-ip"})
            {
                string oldRoot=PluginPaths.PluginRoot(oldId);if(Directory.Exists(oldRoot))Directory.Delete(oldRoot,true);
            }
            if(File.Exists(PluginPaths.Values))
            {
                Dictionary<string,object> values=JsonUtil.LoadObject(PluginPaths.Values),entries=JsonUtil.Object(JsonUtil.Get(values,"entries")),providers=JsonUtil.Object(JsonUtil.Get(values,"providers"));
                foreach(string prefix in new[]{"Plugin_io_github_kevendai_network_ip","Plugin_io_github_kevendai_xiaomi_router_wan_ip"})foreach(string key in entries.Keys.Where(k=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToList())entries.Remove(key);
                providers.Remove("io.github.kevendai.network-ip");providers.Remove("io.github.kevendai.xiaomi-router-wan-ip");values["entries"]=entries;values["providers"]=providers;JsonUtil.SaveAtomic(PluginPaths.Values,values);
            }
        }
        public static void WritePluginTaskSnapshots(Dictionary<string,object> state)
        {
            List<Dictionary<string,object>> tasks=JsonUtil.Array(JsonUtil.Get(state,"tasks")).Select(JsonUtil.Object).ToList();foreach(string id in new[]{"io.github.kevendai.arxiv"})
            {
                List<object> own=tasks.Where(t=>JsonUtil.String(JsonUtil.Object(JsonUtil.Get(t,"origin")),"plugin_id","").Equals(id,StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList();string data=PluginPaths.DataRoot(id);Directory.CreateDirectory(data);JsonUtil.SaveAtomic(Path.Combine(data,"rss-tasks.json"),new Dictionary<string,object>{{"version",3},{"tasks",own}});
            }
        }
    }

    internal static class TodoExternalImport
    {
        public static Dictionary<string,object> Import(string todoPath,string pluginId,IEnumerable<Dictionary<string,object>> drafts)
        {
            using(Mutex mutex=new Mutex(false,@"Global\RainmeterTodoState"))
            {
                bool held=false;try
                {
                    try{held=mutex.WaitOne(TimeSpan.FromSeconds(15));}catch(AbandonedMutexException){held=true;}if(!held)throw new TimeoutException("待办数据正忙，请稍后重试");
                    Dictionary<string,object> state=File.Exists(todoPath)?JsonUtil.LoadObject(todoPath):new Dictionary<string,object>{{"version",3},{"meta",new Dictionary<string,object>{{"status","就绪"}}},{"tasks",new List<object>()}};
                    MigrateV3(state);List<Dictionary<string,object>> tasks=JsonUtil.Array(JsonUtil.Get(state,"tasks")).Select(JsonUtil.Object).ToList();state["tasks"]=tasks;
                    int created=0,skipped=0,invalid=0,count=0;string firstTaskId="";
                    foreach(Dictionary<string,object> d in drafts??Enumerable.Empty<Dictionary<string,object>>())
                    {
                        count++;if(count>500){invalid++;continue;}string external=JsonUtil.String(d,"external_id","").Trim(),title=JsonUtil.String(d,"title","").Trim();
                        if(external==""||title==""||external.Length>300||title.Length>500){invalid++;continue;}
                        bool exists=tasks.Any(t=>{Dictionary<string,object> o=JsonUtil.Object(JsonUtil.Get(t,"origin"));return JsonUtil.String(o,"plugin_id","").Equals(pluginId,StringComparison.OrdinalIgnoreCase)&&JsonUtil.String(o,"external_id","")==external;});
                        if(exists){skipped++;if(firstTaskId==""){Dictionary<string,object> existing=tasks.First(t=>{Dictionary<string,object> o=JsonUtil.Object(JsonUtil.Get(t,"origin"));return JsonUtil.String(o,"plugin_id","").Equals(pluginId,StringComparison.OrdinalIgnoreCase)&&JsonUtil.String(o,"external_id","")==external;});firstTaskId=JsonUtil.String(existing,"id","");}continue;}
                        List<object> labels=JsonUtil.Array(JsonUtil.Get(d,"labels")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct().Take(32).Cast<object>().ToList();
                        tasks.Add(new Dictionary<string,object>{{"id",Guid.NewGuid().ToString("N")},{"title",title},{"target",JsonUtil.String(d,"target","")},{"note",JsonUtil.String(d,"note","")},{"labels",labels},{"completed",false},{"source","plugin"},{"origin",new Dictionary<string,object>{{"plugin_id",pluginId},{"external_id",external}}},{"policy",JsonUtil.Object(JsonUtil.Get(d,"policy"))},{"created_at",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)},{"completed_at",null},{"available_from",EmptyNull(JsonUtil.Get(d,"available_from"))},{"due_at",EmptyNull(JsonUtil.Get(d,"due_at"))}});
                        if(firstTaskId=="")firstTaskId=JsonUtil.String(tasks[tasks.Count-1],"id","");created++;
                    }
                    JsonUtil.SaveAtomic(todoPath,state);PluginRuntime.WritePluginTaskSnapshots(state);return new Dictionary<string,object>{{"created",created},{"skipped",skipped},{"invalid",invalid},{"task_id",firstTaskId}};
                }finally{if(held)mutex.ReleaseMutex();}
            }
        }
        private static object EmptyNull(object v){return v==null||String.IsNullOrWhiteSpace(Convert.ToString(v,CultureInfo.InvariantCulture))?null:v;}
        public static void MigrateV3(Dictionary<string,object> state)
        {
            List<Dictionary<string,object>> tasks=JsonUtil.Array(JsonUtil.Get(state,"tasks")).Select(JsonUtil.Object).ToList();state["tasks"]=tasks;
            foreach(Dictionary<string,object> t in tasks)
            {
                if(JsonUtil.Get(t,"origin") is Dictionary<string,object>)continue;string source=JsonUtil.String(t,"source","");
                string plugin=source=="arxiv"?"io.github.kevendai.arxiv":source=="caldav"||source=="local-calendar"?"io.github.kevendai.calendar-to-todo":"";
                if(plugin=="")continue;string external=source=="arxiv"?ArxivId(t):JsonUtil.String(t,"calendar_occurrence_key","");
                if(external=="")external="legacy:"+JsonUtil.String(t,"id",Guid.NewGuid().ToString("N"));
                t["origin"]=new Dictionary<string,object>{{"plugin_id",plugin},{"external_id",external}};
                if(source=="arxiv")
                {
                    string original=JsonUtil.String(t,"title",""),translated=JsonUtil.String(t,"translated_title",""),score=JsonUtil.String(t,"abstract_score","");
                    if(t.ContainsKey("translated_title")||t.ContainsKey("abstract_score")||t.ContainsKey("arxiv_id"))
                    {
                        string display=translated==""?original:translated;
                        t["title"]=score==""?display:"("+score+") "+display;
                        string metadata="论文原标题："+original+(external.StartsWith("legacy:",StringComparison.Ordinal)?"":"\r\narXiv ID："+external);
                        string note=JsonUtil.String(t,"note","");
                        if(note.IndexOf("论文原标题：",StringComparison.Ordinal)<0)t["note"]=note==""?metadata:note+"\r\n\r\n"+metadata;
                        t.Remove("translated_title");t.Remove("abstract_score");t.Remove("arxiv_id");
                    }
                    AddLabel(t,"论文");
                    if(JsonUtil.Bool(t,"completed",false)&&!HasLabel(t,"已读")&&!HasLabel(t,"自动归档"))
                    {
                        DateTimeOffset completed;
                        if(DateTimeOffset.TryParse(JsonUtil.String(t,"completed_at",""),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out completed)&&completed.Hour==5&&completed.Minute==59)AddLabel(t,"自动归档");
                        else AddLabel(t,"已读");
                    }
                    t["policy"]=new Dictionary<string,object>{{"daily_rollover","auto_complete"},{"daily_boundary","06:00"},{"rollover_label","自动归档"},{"manual_complete_label","已读"},{"restore_resets_age",true}};
                }
                else AddLabel(t,"日程");
            }state["version"]=3;
        }
        private static void AddLabel(Dictionary<string,object> task,string label){List<string> labels=JsonUtil.Array(JsonUtil.Get(task,"labels")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct().ToList();if(!labels.Contains(label))labels.Add(label);task["labels"]=labels.Cast<object>().ToList();}
        private static bool HasLabel(Dictionary<string,object> task,string label){return JsonUtil.Array(JsonUtil.Get(task,"labels")).Select(Convert.ToString).Any(x=>String.Equals(x,label,StringComparison.Ordinal));}
        private static string ArxivId(Dictionary<string,object> t){string id=JsonUtil.String(t,"arxiv_id","");if(id!="")return id;Match m=Regex.Match(JsonUtil.String(t,"target",""),@"/(\d{4}\.\d{4,5})(?:v\d+)?(?:[/?#]|$)",RegexOptions.IgnoreCase);if(m.Success)return m.Groups[1].Value;m=Regex.Match(JsonUtil.String(t,"note",""),@"arXiv ID[：:]\s*(\d{4}\.\d{4,5})",RegexOptions.IgnoreCase);return m.Success?m.Groups[1].Value:"";}
    }
}
