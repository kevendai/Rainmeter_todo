using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RainmeterBackend;

internal static class PluginHostApp
{
    private static string ActivePluginId="";

    // Broker 请求文件的顶层键白名单：多一个都拒（规格 §4.4）。请求里**不许**出现
    // provider_id / consumer / binding_key —— 插件只能报 service 名，provider 由宿主按绑定解析。
    private static readonly HashSet<string> ServiceRequestKeys=new HashSet<string>(new[]{"protocol","request_id","service","action","input","timeout_seconds"},StringComparer.OrdinalIgnoreCase);
    private const long MaxServiceRequestBytes=1024L*1024L;
    private const int DefaultServiceTimeout=600,MinServiceTimeout=2,MaxServiceTimeout=1800;

    private static int Main(string[] args)
    {
        try
        {
            string action = args.Length == 0 ? "SelfTest" : args[0];
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            PluginPaths.Ensure();
            PluginRuntime.BootstrapBundled(Path.Combine(baseDir, "BundledPlugins"));
            if (action == "Bootstrap") { PluginRuntime.BootstrapBundled(Path.Combine(baseDir, "BundledPlugins")); return 0; }
            if (action == "SelfTest") return SelfTest();
            if (action == "QueryTasks") return QueryTasks(baseDir,args);
            if (action == "SyncAll") return SyncAll(baseDir,args.Length>1?args[1]:"manual");
            if (action == "ValuesAll") return ValuesAll(baseDir,args.Length>1?args[1]:"startup");
            if (action == "Cancel") return CancelPlugin(args.Length>1?args[1]:"",args.Length>2?args[2]:"user_cancelled");
            // 两种写法都收：`ServiceCall <requestFile> <outputFile>` 与规格 §4.1 的
            // `-Mode ServiceCall -RequestFile <path> [-OutputFile <path>]`。
            if (action == "ServiceCall" || String.Equals(action,"-Mode",StringComparison.OrdinalIgnoreCase)) return ServiceCallCommand(args);
            if (args.Length < 2) throw new ArgumentException("缺少插件 ID");
            string id = args[1];
            ActivePluginId=id;
            bool custom=action=="PluginAction";string inputPath = args.Length > (custom?3:2) ? args[custom?3:2] : "";
            string outputPath = args.Length > (custom?4:3) ? args[custom?4:3] : "";
            Dictionary<string, object> input = inputPath != "" && File.Exists(inputPath) ? JsonUtil.LoadObject(inputPath) : new Dictionary<string, object>();
            bool sync=action=="Sync"||action=="SyncAuto";
            int timeout = sync||custom ? 1800 : id==DynamicPluginValues.SsdpPluginId ? 90 : 30;
            string pluginAction = custom?(args.Length>2?args[2]:""):sync ? "sync" : action == "Transform" ? "transform" : action == "Values" ? "get_values" : action;
            string jobId=Guid.NewGuid().ToString("N");
            PluginCallResult result;
            try
            {
                result=InvokeLocked(id,pluginAction,input,action == "SyncAuto" ? "startup" : sync ? "manual" : "host",timeout,sync,ThrottledProgress(id,baseDir),delegate{StartJob(id,jobId);});
            }
            catch(PluginBusyException)
            {
                // 规格 §4.7：单飞锁拒绝第二个请求时，正在跑的那个 job 必须保持 running。
                // 这里**一个字节都不写** job 文件（连 message 也不改），避免踩坏在跑的任务。
                Console.Error.WriteLine("该插件已有同步任务正在运行");
                return 0;
            }
            string addressTarget=PluginRuntime.AddressTargetOf(id);AddressProviderBinding addressProvider=sync&&addressTarget!=""?DynamicPluginValues.AddressProvider(addressTarget):null;if(addressProvider!=null&&!result.Ok){PluginCallResult refreshed=RefreshAddressProvider(baseDir,addressProvider);if(refreshed.Ok)result=InvokeLocked(id,pluginAction,input,action=="SyncAuto"?"startup":"manual",timeout,sync,ThrottledProgress(id,baseDir),null);else result.Error=result.Error+"；"+addressProvider.PluginName+" 重新发现失败："+refreshed.Error+"。服务器可能已关机或故障。";}
            Dictionary<string, object> response = new Dictionary<string, object>{{"ok",result.Ok},{"status",result.Status},{"payload",result.Payload},{"error",result.Error}};
            Dictionary<string,object> attention=AttentionOf(result);
            if(JobHasState(id,"cancelled"))
            {
                // 规格 §4.6-6「取消后不得续跑」：插件被杀的瞬间可能已经把结果写出来了，
                // 这里必须整体丢弃 —— 不导入 Todo、不写终态（否则会把 CancelPlugin 写的
                // cancelled 覆盖成 completed，用户看到"取消失败"）。
                Console.Error.WriteLine("任务已被取消，忽略本轮结果");
                response["cancelled"]=true;
            }
            else if(attention!=null)
            {
                // 规格 §5.2：插件带着 attention 正常退出，宿主把它写进 job，等用户在磁贴上点按钮。
                // 这一轮 0 次外部 API 调用、0 个 Todo。
                WriteJob(id,new Dictionary<string,object>{
                    {"state","attention"},{"attention",attention},
                    {"message",JsonUtil.String(attention,"message","需要你的确认")},
                    {"current",0},{"total",0},
                    {"resume_action",JsonUtil.String(attention,"resume_action","")},
                    {"resume_input",JsonUtil.Object(JsonUtil.Get(attention,"resume_input"))}});
            }
            else
            {
                if (action == "Values" || (custom && pluginAction == "configure_discovery" && JsonUtil.Object(JsonUtil.Get(result.Payload,"values")).Count>0)) { UpdateValues(baseDir, id, result); if(result.Ok&&custom&&pluginAction=="configure_discovery")SetPluginEnabled(id,true); }
                bool importsTasks=sync||ActionImportsTasks(id,pluginAction);
                if (result.Ok && !JobHasState(id,"cancelled") && (importsTasks || action == "Transform"))
                {
                    IEnumerable<Dictionary<string, object>> drafts;
                    if (action == "Transform" && JsonUtil.Get(result.Payload, "task") is Dictionary<string, object>)
                        drafts = new[] { JsonUtil.Object(JsonUtil.Get(result.Payload, "task")) };
                    else
                        drafts = JsonUtil.Array(JsonUtil.Get(result.Payload, "tasks")).Select(JsonUtil.Object);
                    response["import"] = TodoExternalImport.Import(Path.Combine(baseDir, "tasks.json"), id, drafts);
                    if (importsTasks) RefreshTodo(baseDir);
                }
                WriteTerminalState(id,result.Ok?"completed":"failed",result.Ok?1:0,1,result.Ok?JsonUtil.String(result.Payload,"summary","已完成"):result.Error);
            }
            if (outputPath != "") JsonUtil.SaveAtomic(outputPath, response); else Console.Out.WriteLine(JsonUtil.Serialize(response));
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex) { try {if(ActivePluginId!="")WriteFailureUnlessCancelled(ActivePluginId,ex.Message);Console.Error.WriteLine(ex.ToString()); } catch { } return 1; }
    }

    // 哪些 action 的结果要导入 Todo。原来硬编码 "rescore"（2.0.4 及更早），现在改成数据驱动：
    // manifest 的 action 里标 "imports_tasks": true 即生效（旧值保留以兼容已发布的插件）。
    private static bool ActionImportsTasks(string id,string actionId)
    {
        if(String.IsNullOrWhiteSpace(actionId))return false;
        if(String.Equals(actionId,"rescore",StringComparison.OrdinalIgnoreCase))return true;
        try{return PluginRuntime.Resolve(id,false).Actions.Any(a=>String.Equals(JsonUtil.String(a,"id",""),actionId,StringComparison.OrdinalIgnoreCase)&&JsonUtil.Bool(a,"imports_tasks",false));}
        catch{return false;}
    }

    // status == "attention" 是**正式协议结果**，不是失败（规格 §4.3/§5.4）。
    private static Dictionary<string,object> AttentionOf(PluginCallResult result)
    {
        if(result==null||!result.Ok)return null;
        if(!String.Equals(result.Status,"attention",StringComparison.OrdinalIgnoreCase))return null;
        Dictionary<string,object> attention=JsonUtil.Object(JsonUtil.Get(result.Payload,"attention"));
        if(attention.Count==0)attention=new Dictionary<string,object>{{"type","paid_service_confirmation"},{"message",JsonUtil.String(result.Payload,"summary","需要你的确认")}};
        return attention;
    }

    private static void SetPluginEnabled(string id,bool enabled){Dictionary<string,object> current=PluginRuntime.Current(id);current["enabled"]=enabled;JsonUtil.SaveAtomic(Path.Combine(PluginPaths.PluginRoot(id),"current.json"),current);}

    private static PluginCallResult RefreshAddressProvider(string baseDir,AddressProviderBinding provider)
    {
        try{PluginCallResult result=PluginRuntime.Invoke(provider.PluginId,"get_values",new Dictionary<string,object>(),"dependency_retry",90,ThrottledProgress(provider.PluginId,baseDir));UpdateValues(baseDir,provider.PluginId,result);WriteJobState(provider.PluginId,result.Ok?"completed":"failed",result.Ok?1:0,1,result.Ok?JsonUtil.String(result.Payload,"summary","地址已更新"):result.Error);return result;}catch(Exception ex){PluginCallResult failed=new PluginCallResult{Ok=false,Error=ex.Message};UpdateValues(baseDir,provider.PluginId,failed);return failed;}
    }
    private static int SyncAll(string baseDir,string trigger)
    {
        int failures=0;if(!Directory.Exists(PluginPaths.Plugins))return 0;foreach(string root in Directory.GetDirectories(PluginPaths.Plugins))
        {
            string id=Path.GetFileName(root);try
            {
                PluginManifest manifest=PluginRuntime.Resolve(id,true);if(!manifest.Capabilities.Contains("todo_source"))continue;ActivePluginId=id;
                string jobId=Guid.NewGuid().ToString("N");PluginCallResult result;
                try{result=InvokeLocked(id,"sync",new Dictionary<string,object>(),trigger,1800,true,ThrottledProgress(id,baseDir),delegate{StartJob(id,jobId);});}
                catch(PluginBusyException){continue;}
                string addressTarget=PluginRuntime.AddressTargetOf(id);AddressProviderBinding provider=addressTarget!=""?DynamicPluginValues.AddressProvider(addressTarget):null;
                if(provider!=null&&!result.Ok){PluginCallResult refreshed=RefreshAddressProvider(baseDir,provider);if(refreshed.Ok)result=InvokeLocked(id,"sync",new Dictionary<string,object>(),trigger,1800,true,ThrottledProgress(id,baseDir),null);else result.Error=result.Error+"；"+provider.PluginName+" 重新发现失败："+refreshed.Error+"。服务器可能已关机或故障。";}
                if(JobHasState(id,"cancelled"))continue;
                Dictionary<string,object> attention=AttentionOf(result);
                if(attention!=null)
                {
                    // 后台同步**不弹模态**（规格 §5.2 / §11-#9）：只置 attention + 磁贴提示，等用户主动点。
                    WriteJob(id,new Dictionary<string,object>{{"state","attention"},{"attention",attention},{"message",JsonUtil.String(attention,"message","需要你的确认")},{"current",0},{"total",0},{"resume_action",JsonUtil.String(attention,"resume_action","")},{"resume_input",JsonUtil.Object(JsonUtil.Get(attention,"resume_input"))}});continue;
                }
                if(!result.Ok){failures++;WriteTerminalState(id,"failed",0,1,result.Error);continue;}
                if(JobHasState(id,"cancelled"))continue;
                IEnumerable<Dictionary<string,object>> drafts=JsonUtil.Array(JsonUtil.Get(result.Payload,"tasks")).Select(JsonUtil.Object);TodoExternalImport.Import(Path.Combine(baseDir,"tasks.json"),id,drafts);WriteTerminalState(id,"completed",1,1,JsonUtil.String(result.Payload,"summary","已完成"));
            }catch(Exception ex){failures++;WriteTerminalState(id,"failed",0,1,ex.Message);}
        }
        RefreshTodo(baseDir);return failures==0?0:1;
    }    private static int ValuesAll(string baseDir,string trigger)
    {
        int failures=0;if(!Directory.Exists(PluginPaths.Plugins))return 0;foreach(string root in Directory.GetDirectories(PluginPaths.Plugins))
        {
            string id=Path.GetFileName(root);try{PluginManifest manifest=PluginRuntime.Resolve(id,true);if(!manifest.Capabilities.Contains("value_provider")||!ValueDue(id))continue;PluginCallResult result=PluginRuntime.Invoke(id,"get_values",new Dictionary<string,object>(),trigger,30,ThrottledProgress(id,baseDir));UpdateValues(baseDir,id,result);if(!result.Ok)failures++;}catch(Exception ex){failures++;UpdateValues(baseDir,id,new PluginCallResult{Ok=false,Error=ex.Message});}
        }RefreshTodo(baseDir);return failures==0?0:1;
    }
    private static bool ValueDue(string id){if(!File.Exists(PluginPaths.Values))return true;try{Dictionary<string,object> root=JsonUtil.LoadObject(PluginPaths.Values),providers=JsonUtil.Object(JsonUtil.Get(root,"providers")),provider=JsonUtil.Object(JsonUtil.Get(providers,id));DateTimeOffset expires;return !DateTimeOffset.TryParse(JsonUtil.String(provider,"expires_at",""),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out expires)||DateTimeOffset.Now>=expires;}catch{return true;}}
    private static Action<Dictionary<string,object>> ThrottledProgress(string id,string baseDir){DateTime last=DateTime.MinValue;return delegate(Dictionary<string,object> progress){DateTime now=DateTime.UtcNow;int current=JsonUtil.Int(progress,"current",0),total=JsonUtil.Int(progress,"total",0);if(JsonUtil.String(progress,"type","")=="host_start"){WriteJob(id,progress);RefreshTodo(baseDir);return;}if(last!=DateTime.MinValue&&(now-last).TotalMilliseconds<500&&!(total>0&&current>=total))return;last=now;WriteJob(id,progress);RefreshTodo(baseDir);};}
    private static void RefreshTodo(string baseDir){try{File.WriteAllText(Path.Combine(baseDir,".refresh-guard"),RuntimeUtil.Iso(DateTimeOffset.Now),RuntimeUtil.Utf8NoBom);}catch{return;}RuntimeUtil.Refresh("Todo");}

    private static int QueryTasks(string baseDir,string[] args)
    {
        if(args.Length<3)throw new ArgumentException("QueryTasks 缺少输入或输出路径");Dictionary<string,object> request=JsonUtil.LoadObject(args[1]);HashSet<string> wanted=new HashSet<string>(JsonUtil.Array(JsonUtil.Get(request,"task_ids")).Select(Convert.ToString),StringComparer.OrdinalIgnoreCase);List<object> existing=new List<object>();
        using(System.Threading.Mutex mutex=new System.Threading.Mutex(false,@"Global\RainmeterTodoState")){bool held=false;try{try{held=mutex.WaitOne(TimeSpan.FromSeconds(15));}catch(System.Threading.AbandonedMutexException){held=true;}if(!held)throw new TimeoutException("待办数据正忙");string path=Path.Combine(baseDir,"tasks.json");if(File.Exists(path)){Dictionary<string,object> state=JsonUtil.LoadObject(path);foreach(object raw in JsonUtil.Array(JsonUtil.Get(state,"tasks"))){string id=JsonUtil.String(JsonUtil.Object(raw),"id","");if(wanted.Contains(id))existing.Add(id);}}}finally{if(held)mutex.ReleaseMutex();}}
        JsonUtil.SaveAtomic(args[2],new Dictionary<string,object>{{"ok",true},{"task_ids",existing}});return 0;
    }

    private static PluginCallResult InvokeLocked(string id,string action,object input,string trigger,int timeout,bool exclusive,Action<Dictionary<string,object>> progress,Action onStart)
    {
        if(!exclusive){if(onStart!=null)onStart();return PluginRuntime.Invoke(id,action,input,trigger,timeout,progress);}
        using(System.Threading.Mutex mutex=new System.Threading.Mutex(false,@"Global\RainmeterPluginSync_"+Regex.Replace(id,@"[^A-Za-z0-9]","_")))
        {
            bool held=false;try{try{held=mutex.WaitOne(TimeSpan.Zero);}catch(System.Threading.AbandonedMutexException){held=true;}if(!held)throw new PluginBusyException("该插件已有同步任务正在运行");
            // 拿到锁之后才写 job：否则第二个请求会先把在跑的 job 的进度打回 0/0。
            if(onStart!=null)onStart();
            return PluginRuntime.Invoke(id,action,input,trigger,timeout,progress);}finally{if(held)mutex.ReleaseMutex();}
        }
    }

    // 起一个 job：写 job_id 与空 children[]，并把 job_id 交给 PluginRuntime 注入插件环境。
    private static void StartJob(string id,string jobId)
    {
        PluginRuntime.CurrentJobId=jobId;
        WriteJob(id,new Dictionary<string,object>{
            {"job_id",jobId},{"state","running"},{"current",0},{"total",0},{"message","正在运行"},
            {"started_at",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)},{"children",new List<object>()}});
    }

    private static void WriteJob(string id, Dictionary<string, object> progress)
    {
        using(System.Threading.Mutex mutex=new System.Threading.Mutex(false,@"Global\RainmeterPluginJob_"+Regex.Replace(id,@"[^A-Za-z0-9]","_"))){bool held=false;try{try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(System.Threading.AbandonedMutexException){held=true;}if(!held)throw new TimeoutException("插件任务状态正忙");string path=Path.Combine(PluginPaths.Jobs,id+".json");Dictionary<string,object> job=File.Exists(path)?JsonUtil.LoadObject(path):new Dictionary<string,object>();foreach(KeyValuePair<string,object> pair in progress)job[pair.Key]=pair.Value;if(!progress.ContainsKey("state"))job["state"]="running";
            // 规格 §4.6-1：pid/entry 在整个 job 生命周期里保留（不再像 2.0.4 那样收尾即删），
            // 否则 job 一收尾就再也取消不了；误杀风险由 CancelPlugin 的进程身份核对兜住。
            job["plugin_id"]=id;job["updated_at"]=DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture);JsonUtil.SaveAtomic(path,job);}finally{if(held)mutex.ReleaseMutex();}}
    }
    private static void WriteJobState(string id,string state,int current,int total,string message){WriteJob(id,new Dictionary<string,object>{{"state",state},{"current",current},{"total",total},{"message",message??""}});}
    private static bool JobHasState(string id,string state){string path=Path.Combine(PluginPaths.Jobs,id+".json");if(!File.Exists(path))return false;try{return JsonUtil.String(JsonUtil.LoadObject(path),"state","")==state;}catch{return false;}}
    private static void WriteFailureUnlessCancelled(string id,string message){WriteTerminalState(id,"failed",0,1,message);}

    // 写终态。**取消优先**：CancelPlugin 已经标了 cancelled 就绝不覆盖（规格 §4.6-6）。
    // 判定与写入必须在同一把 job 锁里完成 —— 否则 CancelPlugin 恰好插在两者之间时，
    // 用户会看到"取消成功"，紧接着状态又变回 completed。
    private static void WriteTerminalState(string id,string state,int current,int total,string message)
    {
        using(System.Threading.Mutex mutex=new System.Threading.Mutex(false,@"Global\RainmeterPluginJob_"+Regex.Replace(id,@"[^A-Za-z0-9]","_")))
        {
            bool held=false;
            try
            {
                try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(System.Threading.AbandonedMutexException){held=true;}
                if(!held)return;
                string path=Path.Combine(PluginPaths.Jobs,id+".json");
                Dictionary<string,object> job=File.Exists(path)?JsonUtil.LoadObject(path):new Dictionary<string,object>();
                if(JsonUtil.String(job,"state","")=="cancelled")return;
                job["state"]=state;job["current"]=current;job["total"]=total;job["message"]=message??"";
                job["plugin_id"]=id;job["updated_at"]=DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture);
                JsonUtil.SaveAtomic(path,job);
            }
            finally{if(held)mutex.ReleaseMutex();}
        }
    }

    // 取消传播（规格 §4.6）：先 provider（最深）→ 再 broker → 最后 consumer。
    // 顺序反了会留下孤儿请求进程；每一步都用登记路径核对进程身份，PID 被复用则跳过而不是误杀。
    private static int CancelPlugin(string id,string reason)
    {
        if(id=="")return 2;string path=Path.Combine(PluginPaths.Jobs,id+".json");if(!File.Exists(path))return 1;
        Dictionary<string,object> job;try{job=JsonUtil.LoadObject(path);}catch{return 1;}
        string state=JsonUtil.String(job,"state","");bool killed=false;
        List<Dictionary<string,object>> children=JsonUtil.Array(JsonUtil.Get(job,"children")).Select(JsonUtil.Object).ToList();
        for(int index=children.Count-1;index>=0;index--)
        {
            Dictionary<string,object> child=children[index];
            if(KillRegisteredProcess(JsonUtil.Int(child,"broker_pid",0),JsonUtil.String(child,"broker_entry","")))killed=true;
            if(KillRegisteredProcess(JsonUtil.Int(child,"pid",0),JsonUtil.String(child,"entry","")))killed=true;
        }
        if(KillRegisteredProcess(JsonUtil.Int(job,"pid",0),JsonUtil.String(job,"entry","")))killed=true;
        if(state=="completed"||state=="failed"||state=="cancelled")return 1;
        if(!killed&&state!="running"&&state!="attention")return 1;
        string cancelReason=String.IsNullOrWhiteSpace(reason)?"user_cancelled":reason.Trim();
        WriteJob(id,new Dictionary<string,object>{{"state","cancelled"},{"current",0},{"total",1},{"message",CancelMessage(cancelReason)},{"cancel_reason",cancelReason},{"cancelled_at",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)}});
        return 0;
    }

    // 取消 ≠ 失败：文案由 cancel_reason 决定（规格 §4.6-6）。
    private static string CancelMessage(string reason)
    {
        if(reason=="user_declined")return "已取消：你选择了不使用 AI 评分";
        if(reason=="provider_denied")return "已取消：没有人确认这次付费调用";
        return "已取消";
    }

    private static bool KillRegisteredProcess(int pid,string entry)
    {
        if(pid<=0||String.IsNullOrWhiteSpace(entry))return false;
        try{if(pid==Process.GetCurrentProcess().Id)return false;}catch{}
        try
        {
            using(Process process=Process.GetProcessById(pid))
            {
                string actual;try{actual=process.MainModule.FileName;}catch{return false;}
                if(!Path.GetFullPath(actual).Equals(Path.GetFullPath(entry),StringComparison.OrdinalIgnoreCase))return false;
                try{process.CloseMainWindow();}catch{}
                if(!process.WaitForExit(3000))process.Kill();
                return true;
            }
        }
        catch(ArgumentException){return false;}
        catch(Exception){return false;}
    }

    // Broker 把 provider 进程登记进消费者 job 的 children[]（规格 §4.6-2）。
    // 只在「job 存在 && job_id 与本次调用一致 && job 尚未收尾」时登记。否则会把别的任务的
    // children 留在已完成/别代的 job 里，取消时只得拿死 PID 一个个核对（判定也就不可信了）。
    private static void AppendJobChild(string consumerId,string jobId,Dictionary<string,object> child)
    {
        if(String.IsNullOrWhiteSpace(consumerId)||String.IsNullOrWhiteSpace(jobId)||JsonUtil.Int(child,"pid",0)<=0)return;
        using(System.Threading.Mutex mutex=new System.Threading.Mutex(false,@"Global\RainmeterPluginJob_"+Regex.Replace(consumerId,@"[^A-Za-z0-9]","_")))
        {
            bool held=false;
            try
            {
                try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){held=true;}
                if(!held)return;
                string path=Path.Combine(PluginPaths.Jobs,consumerId+".json");
                if(!File.Exists(path))return;
                Dictionary<string,object> job=JsonUtil.LoadObject(path);
                if(!String.Equals(JsonUtil.String(job,"job_id",""),jobId,StringComparison.OrdinalIgnoreCase))return;
                string state=JsonUtil.String(job,"state","");
                if(state=="completed"||state=="failed"||state=="cancelled")return;
                List<object> children=JsonUtil.Array(JsonUtil.Get(job,"children")).ToList();
                int pid=JsonUtil.Int(child,"pid",0);
                if(children.Select(JsonUtil.Object).Any(x=>JsonUtil.Int(x,"pid",0)==pid))return;
                children.Add(child);job["children"]=children;
                job["plugin_id"]=consumerId;job["updated_at"]=DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture);
                JsonUtil.SaveAtomic(path,job);
            }
            catch{}
            finally{if(held)mutex.ReleaseMutex();}
        }
    }

    private static string CurrentProcessEntry()
    {
        try{return Process.GetCurrentProcess().MainModule.FileName;}catch{return "";}
    }

    private sealed class ServiceCallTrace
    {
        public DateTimeOffset StartedAt;
        public string RequestId="",Consumer="",Service="",Action="",ProviderId="",Billing="",Status="",ErrorKind="",Error="";
        public long RequestBytes;public int InputKeys;public bool Ok;
    }

    // 跨插件服务调用（Broker）。规格 §4：这是插件**唯一**被允许的跨插件动作。
    // 链：Host(Sync) → consumer 插件 → 本进程 → provider 插件。Broker depth 写死 1。
    private static int ServiceCallCommand(string[] args)
    {
        string requestPath=null,outputPath="";
        for(int index=1;index<args.Length;index++)
        {
            string arg=args[index];
            if(String.Equals(arg,"-Mode",StringComparison.OrdinalIgnoreCase)||String.Equals(arg,"ServiceCall",StringComparison.OrdinalIgnoreCase))continue;
            if(String.Equals(arg,"-RequestFile",StringComparison.OrdinalIgnoreCase)){if(index+1<args.Length)requestPath=args[++index];continue;}
            if(String.Equals(arg,"-OutputFile",StringComparison.OrdinalIgnoreCase)){if(index+1<args.Length)outputPath=args[++index];continue;}
            if(requestPath==null)requestPath=arg;else if(outputPath=="")outputPath=arg;
        }
        return ServiceCall(requestPath??"",outputPath);
    }

    private static int ServiceCall(string requestPath,string outputPath)
    {
        ServiceCallTrace trace=new ServiceCallTrace{StartedAt=DateTimeOffset.Now};
        Dictionary<string,object> response;
        try{response=RunServiceCall(requestPath,trace);}
        catch(Exception ex){response=Fail(trace,"broker_error","Broker 内部错误："+ex.Message);}
        try{if(outputPath!="")JsonUtil.SaveAtomic(outputPath,response);else Console.Out.WriteLine(JsonUtil.Serialize(response));}catch{}
        long elapsed=(long)(DateTimeOffset.Now-trace.StartedAt).TotalMilliseconds;
        ServiceRegistry.Audit((trace.Consumer==""?"-":trace.Consumer)+" | "+(trace.ProviderId==""?"-":trace.ProviderId)+" | "+(trace.Service==""?"-":trace.Service)+" | "+(trace.Action==""?"-":trace.Action)+" | "+elapsed.ToString(CultureInfo.InvariantCulture)+"ms | "+(trace.Ok?"ok/"+trace.Status:"error/"+trace.ErrorKind)+" | "+(trace.Billing==""?"-":trace.Billing)+" | request="+trace.RequestBytes.ToString(CultureInfo.InvariantCulture)+"B input_keys="+trace.InputKeys.ToString(CultureInfo.InvariantCulture));
        return JsonUtil.Bool(response,"ok",false)?0:1;
    }

    private static Dictionary<string,object> RunServiceCall(string requestPath,ServiceCallTrace trace)
    {
        string consumer=Environment.GetEnvironmentVariable(PluginRuntime.PluginIdVariable)??"";trace.Consumer=consumer;
        string jobId=Environment.GetEnvironmentVariable(PluginRuntime.JobIdVariable)??"";
        // 双保险（规格 §4.4）：Broker 自身环境块里已经有 depth 标记 ⇒ 这是二级转发，直接拒。
        if(!String.IsNullOrEmpty(Environment.GetEnvironmentVariable(PluginRuntime.DepthVariable)))
            return Fail(trace,"broker_depth_exceeded","服务调用不允许二级转发（Broker depth = 1）。");
        if(String.IsNullOrWhiteSpace(consumer))
            return Fail(trace,"protocol_error","缺少 "+PluginRuntime.PluginIdVariable+"：跨插件调用只能从插件内部发起。");
        WatchConsumer(consumer,jobId);
        if(String.IsNullOrWhiteSpace(requestPath)||!File.Exists(requestPath))
            return Fail(trace,"input_invalid","缺少 Broker 请求文件。");
        FileInfo file=new FileInfo(requestPath);trace.RequestBytes=file.Length;
        if(file.Length>MaxServiceRequestBytes)return Fail(trace,"input_invalid","Broker 请求文件超过 1 MB 限制。");
        Dictionary<string,object> request;
        try{request=JsonUtil.LoadObject(requestPath);}catch(Exception ex){return Fail(trace,"input_invalid","Broker 请求文件不是合法 JSON："+ex.Message);}
        string offending=request.Keys.FirstOrDefault(key=>!ServiceRequestKeys.Contains(key));
        if(offending!=null)return Fail(trace,"protocol_error","请求文件出现非法顶层键："+offending+"（插件只能报 service，provider 由宿主按绑定解析）。");
        if(JsonUtil.Int(request,"protocol",0)!=1)return Fail(trace,"protocol_error","不支持的 Broker 协议版本。");
        trace.RequestId=JsonUtil.String(request,"request_id","");
        string service=JsonUtil.String(request,"service","").Trim();trace.Service=service;
        string callAction=JsonUtil.String(request,"action","").Trim();trace.Action=callAction;
        if(!ServiceRegistry.ValidService(service))return Fail(trace,"protocol_error","service 格式无效（应为 name@version）。");
        if(!Regex.IsMatch(callAction??"",@"^[a-z0-9_]{1,60}$"))return Fail(trace,"protocol_error","action 格式无效。");
        object input=JsonUtil.Get(request,"input");if(input==null)input=new Dictionary<string,object>();
        Dictionary<string,object> inputObject=input as Dictionary<string,object>;trace.InputKeys=inputObject==null?0:inputObject.Count;
        int timeout=JsonUtil.Int(request,"timeout_seconds",DefaultServiceTimeout);if(timeout<MinServiceTimeout)timeout=MinServiceTimeout;if(timeout>MaxServiceTimeout)timeout=MaxServiceTimeout;
        string providerId=ServiceRegistry.ProviderFor(consumer,service);trace.ProviderId=providerId;
        if(providerId=="")return Fail(trace,"no_provider","没有可用的服务提供者（"+ServiceRegistry.ReasonFor(consumer,service)+"）。");
        trace.Billing=ServiceRegistry.BillingOf(providerId);
        Dictionary<string,string> environment=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
            {PluginRuntime.HostExeVariable,null},            // 显式剔除：provider 物理上找不到 Broker 入口
            {PluginRuntime.DepthVariable,"1"},
            {PluginRuntime.PluginIdVariable,providerId}};    // provider 看到的是自己的 id
        Action<Dictionary<string,object>> onProgress=delegate(Dictionary<string,object> progress)
        {
            if(JsonUtil.String(progress,"type","")!="host_start")return;
            AppendJobChild(consumer,jobId,new Dictionary<string,object>{
                {"job_id",jobId},
                {"role","provider"},{"plugin_id",providerId},
                {"pid",JsonUtil.Int(progress,"pid",0)},{"entry",JsonUtil.String(progress,"entry","")},
                {"broker_pid",Process.GetCurrentProcess().Id},{"broker_entry",CurrentProcessEntry()},
                {"started_at",DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture)}});
        };
        PluginCallResult result;
        try{result=PluginRuntime.Invoke(providerId,callAction,input,"service_call",timeout,onProgress,environment);}
        catch(TimeoutException){return Fail(trace,"provider_timeout","服务提供者超过 "+timeout.ToString(CultureInfo.InvariantCulture)+" 秒没有返回。");}
        catch(PluginBusyException ex){return Fail(trace,"provider_denied",ex.Message);}
        catch(Exception ex){return Fail(trace,"provider_crashed","服务提供者执行失败："+ex.Message);}
        if(result.Ok)
        {
            trace.Ok=true;trace.Status=String.IsNullOrWhiteSpace(result.Status)?"ok":result.Status;
            return ServiceEnvelope(trace.RequestId,true,trace.Status,result.Payload,"","",false);
        }
        return Fail(trace,"provider_error",String.IsNullOrWhiteSpace(result.Error)?"服务提供者返回失败。":result.Error,JsonUtil.Bool(result.Payload,"fatal",false));
    }

    // consumer 插件进程消失 ⇒ Broker 自行退出，避免留下后台请求（规格 §4.6-4）。
    // consumer 的 pid 取自它自己的 job 文件（用 job_id 核对，防止读到下一代任务的 pid）。
    private static void WatchConsumer(string consumerId,string jobId)
    {
        // 没有 job 就没有"可以被取消的东西"：此时不监视（否则会盯上一个与本调用无关的 PID）。
        if(String.IsNullOrWhiteSpace(consumerId)||String.IsNullOrWhiteSpace(jobId))return;
        int pid=0;
        try
        {
            string path=Path.Combine(PluginPaths.Jobs,consumerId+".json");
            if(!File.Exists(path))return;
            Dictionary<string,object> job=JsonUtil.LoadObject(path);
            if(!String.Equals(JsonUtil.String(job,"job_id",""),jobId,StringComparison.OrdinalIgnoreCase))return;
            pid=JsonUtil.Int(job,"pid",0);
        }
        catch{return;}
        if(pid<=0)return;
        try{if(pid==Process.GetCurrentProcess().Id)return;}catch{}
        Thread thread=new Thread(delegate()
        {
            try{using(Process parent=Process.GetProcessById(pid)){parent.WaitForExit();}Console.Error.WriteLine("consumer 插件已退出，Broker 自行结束");}
            catch(ArgumentException){Console.Error.WriteLine("consumer 插件已不存在，Broker 自行结束");}
            catch(Exception){return;}
            try{Environment.Exit(1);}catch{}
        });
        thread.IsBackground=true;thread.Start();
    }

    private static Dictionary<string,object> Fail(ServiceCallTrace trace,string kind,string message){return Fail(trace,kind,message,false);}

    private static Dictionary<string,object> Fail(ServiceCallTrace trace,string kind,string message,bool fatal)
    {
        trace.Ok=false;trace.Status="error";trace.ErrorKind=kind;trace.Error=message;
        return ServiceEnvelope(trace.RequestId,false,"error",null,message,kind,fatal);
    }

    private static Dictionary<string,object> ServiceEnvelope(string requestId,bool ok,string status,object output,string error,string errorKind,bool fatal)
    {
        return new Dictionary<string,object>{
            {"protocol",1},{"request_id",requestId??""},{"ok",ok},
            {"status",String.IsNullOrWhiteSpace(status)?"ok":status},
            {"output",output},{"error",error??""},{"error_kind",errorKind??""},
            {"fatal",fatal},{"warnings",new List<object>()}};
    }

    private static void UpdateValues(string baseDir,string pluginId,PluginCallResult result)
    {
        Dictionary<string,object> root=File.Exists(PluginPaths.Values)?JsonUtil.LoadObject(PluginPaths.Values):new Dictionary<string,object>();
        Dictionary<string,object> entries=JsonUtil.Object(JsonUtil.Get(root,"entries"));root["entries"]=entries;
        Dictionary<string,object> providers=JsonUtil.Object(JsonUtil.Get(root,"providers"));root["providers"]=providers;
        string prefix=VariableName(pluginId,""),now=DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture);
        foreach(string key in entries.Keys.Where(k=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToList())JsonUtil.Object(entries[key])["stale"]=!result.Ok;
        if(result.Ok)
        {
            int ttl=Math.Max(30,Math.Min(86400,JsonUtil.Int(result.Payload,"ttl",300)));providers[pluginId]=new Dictionary<string,object>{{"ttl",ttl},{"updated_at",now},{"expires_at",DateTimeOffset.Now.AddSeconds(ttl).ToString("o",CultureInfo.InvariantCulture)}};
            Dictionary<string,object> values=JsonUtil.Object(JsonUtil.Get(result.Payload,"values"));
            foreach(KeyValuePair<string,object> pair in values)
            {
                if(pair.Value is Dictionary<string,object>||pair.Value is object[])continue;
                string name=VariableName(pluginId,pair.Key);
                entries[name]=new Dictionary<string,object>{{"value",pair.Value==null?"":Convert.ToString(pair.Value,CultureInfo.InvariantCulture)},{"stale",false},{"updated_at",now}};
            }
        }
        JsonUtil.SaveAtomic(PluginPaths.Values,root);
        List<string> lines=new List<string>{"[Variables]"};
        foreach(string key in entries.Keys.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string,object> item=JsonUtil.Object(entries[key]);string value=JsonUtil.String(item,"value","").Replace("\r"," ").Replace("\n"," ");
            lines.Add(key+"="+value);lines.Add(key+"_Stale="+(JsonUtil.Bool(item,"stale",true)?"1":"0"));lines.Add(key+"_UpdatedAt="+JsonUtil.String(item,"updated_at",""));
        }
        RuntimeUtil.WriteUtf16IfChanged(Path.Combine(baseDir,"PluginValues.inc"),String.Join("\r\n",lines)+"\r\n");
    }
    private static string VariableName(string pluginId,string key){string value="Plugin_"+pluginId+(key==""?"":"_"+key);return System.Text.RegularExpressions.Regex.Replace(value,@"[^A-Za-z0-9_]","_");}

    private static int SelfTest()
    {
        Dictionary<string, object> state = new Dictionary<string, object>{{"version",2},{"meta",new Dictionary<string,object>()},{"tasks",new List<object>{new Dictionary<string,object>{{"id","a"},{"source","arxiv"},{"target","https://arxiv.org/abs/2609.01234"}}}}};
        TodoExternalImport.MigrateV3(state);
        Dictionary<string, object> task = JsonUtil.Object(JsonUtil.Array(JsonUtil.Get(state, "tasks"))[0]);
        Dictionary<string, object> origin = JsonUtil.Object(JsonUtil.Get(task, "origin"));
        if (JsonUtil.Int(state, "version", 0) != 3 || JsonUtil.String(origin, "external_id", "") != "2609.01234") return 20;

        string testDir = Path.Combine(Path.GetTempPath(), "RainmeterPluginHostSelfTest-" + Guid.NewGuid().ToString("N"));
        string include = Path.Combine(testDir, "Generated.inc");
        string first = "[MeterA]\r\nMeter=String\r\nText=" + new String('A', 32768) + "\r\n";
        string second = "[MeterB]\r\nMeter=String\r\nText=" + new String('B', 32768) + "\r\n";
        try
        {
            Directory.CreateDirectory(testDir);
            RuntimeUtil.WriteUtf16IfChanged(include, first);
            byte[] firstBytes = File.ReadAllBytes(include);
            RuntimeUtil.WriteUtf16IfChanged(include, second);
            byte[] secondBytes = File.ReadAllBytes(include);
            RuntimeUtil.WriteUtf16IfChanged(include, first);
            int running = 1;
            Exception writerError = null;
            Thread one = new Thread(new ThreadStart(delegate { try { for (int i = 0; i < 80; i++) RuntimeUtil.WriteUtf16IfChanged(include, (i % 2 == 0) ? second : first); } catch (Exception ex) { writerError = ex; } finally { Interlocked.Decrement(ref running); } }));
            one.Start();
            while (Interlocked.CompareExchange(ref running, 0, 0) > 0)
            {
                byte[] observed;
                using (FileStream stream = new FileStream(include, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (MemoryStream copy = new MemoryStream()) { stream.CopyTo(copy); observed = copy.ToArray(); }
                if (!observed.SequenceEqual(firstBytes) && !observed.SequenceEqual(secondBytes)) return 21;
            }
            one.Join();
            if (writerError != null) return 22;
            if (Directory.GetFiles(testDir, "*.tmp-*").Length != 0) return 23;
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }
}
