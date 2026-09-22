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
        // 跨插件服务绑定表（宿主独占写入，插件不可见）。
        public static string Bindings { get { return Path.Combine(Root, "plugin-bindings.json"); } }
        public static void Ensure() { foreach (string p in new[] { Root, Plugins, Data, Jobs, Logs }) Directory.CreateDirectory(p); }
        public static string PluginRoot(string id) { return Path.Combine(Plugins, id); }
        public static string VersionRoot(string id, string version) { return Path.Combine(PluginRoot(id), "versions", version); }
        public static string DataRoot(string id) { return Path.Combine(Data, id); }
    }

    internal sealed class PluginManifest
    {
        public string Id, Name, Version, MinHostVersion, Entry, SettingsSchema, Homepage, AddressValueKey, AddressTarget, Billing;
        public int ApiVersion, AddressPriority;
        public bool DefaultEnabled;
        public List<string> Capabilities = new List<string>();
        public List<string> Permissions = new List<string>();
        public List<string> AddressTargets = new List<string>();
        public List<Dictionary<string,object>> Actions = new List<Dictionary<string,object>>();
        // v2.1：插件能提供的服务（provides）与它需要的服务（uses）。与 capabilities 语义不同：
        // capabilities 是"宿主可以主动执行的入口"，provides 是"给别的插件用的服务"。
        public List<string> Provides = new List<string>();
        public List<ServiceUse> Uses = new List<ServiceUse>();
        // v2.1 回滚保护（§10）：插件要求的主程序版本高于当前主程序。只有 LoadForStatus 会把它置位，
        // 正常 Load 遇到这种情况仍然抛异常拒跑。UI 靠它给出**可见**提示而不是静默算成"安装损坏"。
        public bool HostTooOld;

        public static PluginManifest Load(string root)
        {
            return Parse(root, true);
        }

        // v2.1 回滚保护（§10 第 1 条）：本体被降级到 2.0.4 之后，arxiv 2.0.0 这类插件会因
        // min_host_version 被拒跑。插件列表必须**看得见**它并说明原因，所以这里不因"宿主太旧"
        // 而抛异常，只把 HostTooOld 置位；其余校验照旧（真的坏了还是要抛）。
        public static PluginManifest LoadForStatus(string root)
        {
            return Parse(root, false);
        }

        private static PluginManifest Parse(string root, bool enforceHostVersion)
        {
            string path = Path.Combine(root, "plugin.json");
            if (!File.Exists(path)) throw new InvalidDataException("插件缺少 plugin.json");
            Dictionary<string, object> v = JsonUtil.LoadObject(path);
            PluginManifest m = new PluginManifest {
                Id=JsonUtil.String(v,"id",""), Name=JsonUtil.String(v,"name",""), Version=JsonUtil.String(v,"version",""),
                ApiVersion=JsonUtil.Int(v,"api_version",0), MinHostVersion=JsonUtil.String(v,"min_host_version",""),
                Entry=JsonUtil.String(v,"entry",""), SettingsSchema=JsonUtil.String(v,"settings_schema",""),
                Homepage=JsonUtil.String(v,"homepage",""), DefaultEnabled=JsonUtil.Bool(v,"default_enabled",false),
                Billing=JsonUtil.String(v,"billing","")
            };
            m.Capabilities=JsonUtil.Array(JsonUtil.Get(v,"capabilities")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).ToList();
            m.Permissions=JsonUtil.Array(JsonUtil.Get(v,"permissions")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).ToList();
            m.Provides=JsonUtil.Array(JsonUtil.Get(v,"provides")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach(object raw in JsonUtil.Array(JsonUtil.Get(v,"uses")))
            {
                Dictionary<string,object> item=JsonUtil.Object(raw);if(item.Count==0)continue;
                string service=JsonUtil.String(item,"service","").Trim();
                m.Uses.Add(new ServiceUse{Service=service,BindingKey=JsonUtil.String(item,"binding_key",ServiceRegistry.KeyOf(service)).Trim(),Optional=JsonUtil.Bool(item,"optional",true)});
            }
            m.Actions=JsonUtil.Array(JsonUtil.Get(v,"actions")).Select(JsonUtil.Object).ToList();Dictionary<string,object> address=JsonUtil.Object(JsonUtil.Get(v,"address_provider"));m.AddressPriority=JsonUtil.Int(address,"priority",0);m.AddressValueKey=JsonUtil.String(address,"value","");m.AddressTargets=JsonUtil.Array(JsonUtil.Get(address,"targets")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            m.AddressTarget=JsonUtil.String(v,"address_target","").Trim();
            m.Validate(root,enforceHostVersion);m.Name=PluginNames.Display(m.Id,m.Name);return m;
        }

        private void Validate(string root,bool enforceHostVersion)
        {
            if(!Regex.IsMatch(Id??"",@"^[a-z0-9]+(?:[.-][a-z0-9]+)+$"))throw new InvalidDataException("插件 ID 格式无效");
            if(!Regex.IsMatch(Version??"",@"^\d+\.\d+\.\d+$"))throw new InvalidDataException("插件版本必须为 x.y.z");
            if(ApiVersion!=1)throw new InvalidDataException("不支持的插件 API 版本");
            if(!Regex.IsMatch(MinHostVersion??"",@"^\d+\.\d+\.\d+$"))throw new InvalidDataException("插件最低宿主版本无效");
            if(CompareVersion(MinHostVersion,PluginRuntime.HostVersion)>0)
            {
                if(enforceHostVersion)throw new InvalidDataException("插件要求更高版本的宿主");
                HostTooOld=true;
            }
            if(String.IsNullOrWhiteSpace(Name))throw new InvalidDataException("插件名称不能为空");
            HashSet<string> allowed=new HashSet<string>(new[]{"todo_source","todo_transform","value_provider"},StringComparer.OrdinalIgnoreCase);
            // capabilities 可以为空（纯 Provider 插件），但"能被宿主调用"与"能提供服务"至少要有一样。
            if(Capabilities.Count==0&&Provides.Count==0)throw new InvalidDataException("插件既无 capability 也不提供任何服务");
            if(Capabilities.Any(x=>!allowed.Contains(x)))throw new InvalidDataException("插件 capability 无效");
            if(Provides.Any(x=>!ServiceRegistry.ValidService(x)))throw new InvalidDataException("插件 provides 声明无效");
            foreach(ServiceUse use in Uses)
            {
                if(!ServiceRegistry.ValidService(use.Service))throw new InvalidDataException("插件 uses 声明无效");
                if(!ServiceRegistry.ValidBindingKey(use.BindingKey))throw new InvalidDataException("插件 uses 的 binding_key 无效");
            }
            if(Uses.Select(x=>x.BindingKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Uses.Count)throw new InvalidDataException("插件 uses 的 binding_key 重复");
            if(AddressTargets.Count>0&&(!Capabilities.Contains("value_provider")||!Regex.IsMatch(AddressValueKey??"",@"^[A-Za-z0-9_.-]{1,80}$")))throw new InvalidDataException("地址提供者声明无效");
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

    // 面向用户统一显示中文插件名。已安装的旧版本清单仍可能保留英文名，
    // 所以不能只依赖更新后的 plugin.json；内置插件 ID 在宿主侧也必须有稳定映射。
    internal static class PluginNames
    {
        private static readonly Dictionary<string,string> BuiltIn=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            {"io.github.kevendai.arxiv","arXiv 论文推荐"},
            {"io.github.kevendai.calendar-to-todo","日程转待办"},
            {"io.github.kevendai.paper-snapshot-sync","论文快照同步"},
            {"io.github.kevendai.ai-deepseek","DeepSeek AI 评分"},
            {"io.github.kevendai.translate-tencent","腾讯云翻译"},
            {"io.github.kevendai.ssdp-server-ip","SSDP 服务器 IP 同步"}
        };

        public static string Display(string id,string fallback)
        {
            string known;if(BuiltIn.TryGetValue(id??"",out known))return known;
            if(!String.IsNullOrWhiteSpace(fallback)&&!String.Equals(fallback,id,StringComparison.OrdinalIgnoreCase))return fallback;
            try
            {
                PluginManifest manifest=PluginRuntime.ResolveForStatus(id);
                if(manifest!=null&&!String.IsNullOrWhiteSpace(manifest.Name))return manifest.Name;
            }
            catch{}
            return String.IsNullOrWhiteSpace(fallback)?(id??""):fallback;
        }

        public static string Humanize(string text)
        {
            if(String.IsNullOrWhiteSpace(text))return text??"";
            return Regex.Replace(text,@"io\.github\.[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)+",delegate(Match match){return Display(match.Value,match.Value);},RegexOptions.IgnoreCase);
        }
    }

    internal sealed class PluginCallResult
    {
        public bool Ok;
        // "ok"（缺省即旧插件语义）| "attention"（正式协议结果：等用户决定，不是失败，见规格 §4.3/§5）。
        public string Status="ok";
        public Dictionary<string,object> Payload=new Dictionary<string,object>();
        public string Error="";
    }

    // 单飞锁拒绝第二个同步时抛这个。调用方**不得**把它渲染成失败（规格 §4.7）：
    // 用户手快双击时正在跑的那个 job 必须保持 running。
    internal sealed class PluginBusyException : Exception
    {
        public PluginBusyException(string message) : base(message) { }
    }

    internal static class PluginRuntime
    {
        public static readonly string HostVersion = LoadHostVersion();

        // 宿主→插件上下文变量。规格 §4.4：Caller identity 只来自这些变量，请求 JSON 里不许出现 provider_id。
        public const string PluginIdVariable="RW_PLUGIN_ID";
        public const string JobIdVariable="RW_PLUGIN_JOB_ID";
        public const string HostExeVariable="RW_PLUGIN_HOST_EXE";
        public const string ParentPidVariable="RW_PLUGIN_PID";
        public const string DepthVariable="RW_SERVICE_CALL_DEPTH";
        // 一次性付费同意标记（规格 §5.6「不允许自动同意」）。只有 TodoHost 的确认框会在**自己进程**里
        // 设它，再由它启动的 PluginHost 读得到；插件子进程的环境块里一律被剔除，
        // 所以插件既看不到、也伪造不出"用户已同意"（与 RW_PLUGIN_HOST_EXE 同一手法：机制而非约定）。
        public const string PaidConsentVariable="RW_PAID_CONSENT";
        // 让 consumer 插件不查宿主文件就知道"同一天内已经被用户拒绝过"（规格 §4.6-6 第 2 条）。
        public const string DeclinedTodayVariable="RW_PLUGIN_DECLINED_TODAY";

        public static bool HasPaidConsent(){return !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PaidConsentVariable));}

        // 本机日期。规格 §4.6-6 的"同一天内已拒绝过"按它判定，跨天自然失效。
        public static string LocalDate(){return DateTimeOffset.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);}

        // 只看**粘性**字段 paid_declined_date：后续同步会把 job 的 state 从 cancelled 改成
        // running/attention，若按 state+cancel_reason 现算，用户刚拒绝完就被下一次同步"忘记"了。
        // 放在 PluginRuntime 是因为 PluginHost（写）与 TodoHost（渲染/注入环境）都要用。
        public static bool PaidDeclinedToday(string id)
        {
            try{string path=Path.Combine(PluginPaths.Jobs,id+".json");if(!File.Exists(path))return false;return JsonUtil.String(JsonUtil.LoadObject(path),"paid_declined_date","")==LocalDate();}
            catch{return false;}
        }

        // 本次 job 的 uuid，由 PluginHostApp 在起 job 时赋值；空 = 该次调用不参与取消传播。
        public static string CurrentJobId="";

        // Broker 入口（跨插件调用的唯一通道）。PluginRuntime 只跑在 PluginHost.exe 里，
        // 所以这永远是宿主目录下的 PluginHost.exe；插件拿到全路径后即可回调宿主。
        public static string BrokerHostExe { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PluginHost.exe"); } }

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

        // 列表页专用：宿主太旧的插件也返回（HostTooOld=true），其余错误照旧抛出交给 UI 计"安装损坏"。
        public static PluginManifest ResolveForStatus(string id)
        {
            Dictionary<string,object> c=Current(id);if(c.Count==0)throw new InvalidOperationException("插件未安装："+id);
            string version=JsonUtil.String(c,"version","");PluginManifest m=PluginManifest.LoadForStatus(PluginPaths.VersionRoot(id,version));
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
            return Invoke(id,action,input,trigger,timeoutSeconds,progress,null);
        }

        // extraEnvironment：Broker 调用 provider 时用来"剔除 RW_PLUGIN_HOST_EXE + 注入 depth"，
        // 值传 null 表示把该键从子环境块里删掉（否则会随环境块继承下去）。
        public static PluginCallResult Invoke(string id,string action,object input,string trigger,int timeoutSeconds,Action<Dictionary<string,object>> progress,Dictionary<string,string> extraEnvironment)
        {
            PluginPaths.Ensure();bool settingsAction=String.Equals(action,"validate_settings",StringComparison.OrdinalIgnoreCase)||String.Equals(action,"configure_account",StringComparison.OrdinalIgnoreCase)||String.Equals(action,"configure_discovery",StringComparison.OrdinalIgnoreCase);PluginManifest m=Resolve(id,!settingsAction);string root=PluginPaths.VersionRoot(id,m.Version);
            string entry=PluginManifest.SafeChildPath(root,m.Entry,"插件入口"),requestId=Guid.NewGuid().ToString("N");
            // 本体不向插件注入任何地址：声明了 address_target 的插件自己决定用哪个地址
            // （通过 DynamicPluginValues.AddressProvider 向地址插件申请，并自行记录是否被接管）。
            // 这里只做用户自己写的 {{plugin:...}} 占位符替换，不再替插件绑定/改写地址。
            Dictionary<string,object> resolvedConfig=ReadObject(Path.Combine(PluginPaths.DataRoot(id),"config.json"));DynamicPluginValues.ResolveObject(resolvedConfig);
            // 跨插件服务解析（规格 §3）：唯一候选自动绑定并落 plugin-bindings.json，
            // 绑定失效/多候选一律视为"没有该 provider"（不回落、不按顺序挑）。
            List<ServiceResolution> services=ServiceRegistry.ResolveAll(id,m.Uses,true);
            Dictionary<string,object> context=new Dictionary<string,object>{{"host_version",HostVersion},{"locale",CultureInfo.CurrentUICulture.Name},{"now",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)},{"trigger",trigger??"manual"}};
            if(services.Count>0)context["services"]=ServiceRegistry.ToContextServices(services);
            Dictionary<string,object> request=new Dictionary<string,object>{
                {"api_version",1},{"request_id",requestId},{"plugin_id",id},{"action",action},
                {"context",context},
                {"config",resolvedConfig},{"secret",ReadSecret(Path.Combine(PluginPaths.DataRoot(id),"secret.dat"))},{"input",input??new Dictionary<string,object>()}};
            List<string> lines=new List<string>();StringBuilder errors=new StringBuilder();object gate=new object();Exception progressError=null;
            ProcessStartInfo info=new ProcessStartInfo(entry){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            Dictionary<string,string> environment=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
                {PluginIdVariable,id},{JobIdVariable,CurrentJobId??""},{HostExeVariable,BrokerHostExe},{ParentPidVariable,Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)},
                // 显式剔除（值传 null）：付费同意标记绝不下发给插件 —— 否则插件只要自己再起一个
                // PluginHost 就能把"同意"传染给二级调用（规格 §5.6）。
                {PaidConsentVariable,null}};
            foreach(KeyValuePair<string,string> pair in ServiceRegistry.ToEnvironment(services))environment[pair.Key]=pair.Value;
            if(extraEnvironment!=null)foreach(KeyValuePair<string,string> pair in extraEnvironment)environment[pair.Key]=pair.Value;
            ApplyPluginEnvironment(info,PluginPaths.DataRoot(id),UiScale.Current.ToString("0.###",CultureInfo.InvariantCulture),environment);

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
                    string status=JsonUtil.String(msg,"status","ok").Trim();
                    result=new PluginCallResult{Ok=JsonUtil.Bool(msg,"ok",false),Status=status==""?"ok":status,Payload=JsonUtil.Object(JsonUtil.Get(msg,"payload")),Error=JsonUtil.String(msg,"error","")};
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
            ApplyPluginEnvironment(info,dataDir,windowScale,null);
        }

        internal static void ApplyPluginEnvironment(ProcessStartInfo info,string dataDir,string windowScale,Dictionary<string,string> extra)
        {
            StringDictionary environment=BuildChildEnvironment(Environment.GetEnvironmentVariables(),dataDir,windowScale,extra);
            if(environment!=null&&TryAssignEnvironment(info,environment))return;
            try
            {
                info.EnvironmentVariables["RW_PLUGIN_DATA_DIR"]=dataDir;
                info.EnvironmentVariables["RW_WINDOW_SCALE"]=windowScale;
                if(extra!=null)foreach(KeyValuePair<string,string> pair in extra)
                {
                    if(pair.Value==null)info.EnvironmentVariables.Remove(pair.Key);else info.EnvironmentVariables[pair.Key]=pair.Value;
                }
            }
            catch(ArgumentException ex){throw new InvalidOperationException("无法为插件进程构造环境变量，宿主环境块中存在大小写重复的变量名",ex);}
        }

        // 同名不同大小写只保留最先出现的一项，与 Windows 解析环境变量的顺序一致，
        // 因此子进程拿到的值与直接继承父进程时完全相同。
        internal static StringDictionary BuildChildEnvironment(IDictionary source,string dataDir,string windowScale)
        {
            return BuildChildEnvironment(source,dataDir,windowScale,null);
        }

        // extra 里值为 null = 显式剔除该键。Broker 用它把 RW_PLUGIN_HOST_EXE 从 provider 的环境块里拿掉，
        // 让 provider 物理上找不到宿主入口（规格 §4.4 的 depth=1 机制保障）。
        internal static StringDictionary BuildChildEnvironment(IDictionary source,string dataDir,string windowScale,Dictionary<string,string> extra)
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
                if(extra!=null)foreach(KeyValuePair<string,string> pair in extra)
                {
                    if(String.IsNullOrEmpty(pair.Key))continue;
                    if(pair.Value==null)environment.Remove(pair.Key);else environment[pair.Key]=pair.Value;
                }
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
