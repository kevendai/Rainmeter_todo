using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RainmeterBackend;

internal static class PluginHostApp
{
    private static string ActivePluginId="";
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
            if (action == "Cancel") return CancelPlugin(args.Length>1?args[1]:"");
            if (args.Length < 2) throw new ArgumentException("缺少插件 ID");
            string id = args[1];
            ActivePluginId=id;
            bool custom=action=="PluginAction";string inputPath = args.Length > (custom?3:2) ? args[custom?3:2] : "";
            string outputPath = args.Length > (custom?4:3) ? args[custom?4:3] : "";
            Dictionary<string, object> input = inputPath != "" && File.Exists(inputPath) ? JsonUtil.LoadObject(inputPath) : new Dictionary<string, object>();
            bool sync=action=="Sync"||action=="SyncAuto";
            int timeout = sync||custom ? 1800 : id==DynamicPluginValues.SsdpPluginId ? 90 : 30;
            string pluginAction = custom?(args.Length>2?args[2]:""):sync ? "sync" : action == "Transform" ? "transform" : action == "Values" ? "get_values" : action;
            WriteJobState(id,"running",0,0,"正在运行");
            PluginCallResult result = InvokeLocked(id,pluginAction,input,action == "SyncAuto" ? "startup" : sync ? "manual" : "host",timeout,sync,ThrottledProgress(id,baseDir));
            string addressTarget=PluginRuntime.AddressTargetOf(id);AddressProviderBinding addressProvider=sync&&addressTarget!=""?DynamicPluginValues.AddressProvider(addressTarget):null;if(addressProvider!=null&&!result.Ok){PluginCallResult refreshed=RefreshAddressProvider(baseDir,addressProvider);if(refreshed.Ok)result=InvokeLocked(id,pluginAction,input,action=="SyncAuto"?"startup":"manual",timeout,sync,ThrottledProgress(id,baseDir));else result.Error=result.Error+"；"+addressProvider.PluginName+" 重新发现失败："+refreshed.Error+"。服务器可能已关机或故障。";}
            Dictionary<string, object> response = new Dictionary<string, object>{{"ok",result.Ok},{"payload",result.Payload},{"error",result.Error}};
            if (action == "Values" || (custom && pluginAction == "configure_discovery" && JsonUtil.Object(JsonUtil.Get(result.Payload,"values")).Count>0)) { UpdateValues(baseDir, id, result); if(result.Ok&&custom&&pluginAction=="configure_discovery")SetPluginEnabled(id,true); }
            bool importsTasks=sync||(custom&&pluginAction=="rescore");
            if (result.Ok && (importsTasks || action == "Transform"))
            {
                IEnumerable<Dictionary<string, object>> drafts;
                if (action == "Transform" && JsonUtil.Get(result.Payload, "task") is Dictionary<string, object>)
                    drafts = new[] { JsonUtil.Object(JsonUtil.Get(result.Payload, "task")) };
                else
                    drafts = JsonUtil.Array(JsonUtil.Get(result.Payload, "tasks")).Select(JsonUtil.Object);
                response["import"] = TodoExternalImport.Import(Path.Combine(baseDir, "tasks.json"), id, drafts);
                if (importsTasks) RefreshTodo(baseDir);
            }
            if (outputPath != "") JsonUtil.SaveAtomic(outputPath, response); else Console.Out.WriteLine(JsonUtil.Serialize(response));
            WriteJobState(id,result.Ok?"completed":"failed",result.Ok?1:0,1,result.Ok?JsonUtil.String(result.Payload,"summary","已完成"):result.Error);
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex) { try {if(ActivePluginId!="")WriteFailureUnlessCancelled(ActivePluginId,ex.Message);Console.Error.WriteLine(ex.ToString()); } catch { } return 1; }
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
                PluginManifest manifest=PluginRuntime.Resolve(id,true);if(!manifest.Capabilities.Contains("todo_source"))continue;ActivePluginId=id;WriteJobState(id,"running",0,0,"正在运行");
                PluginCallResult result=InvokeLocked(id,"sync",new Dictionary<string,object>(),trigger,1800,true,ThrottledProgress(id,baseDir));string addressTarget=PluginRuntime.AddressTargetOf(id);AddressProviderBinding provider=addressTarget!=""?DynamicPluginValues.AddressProvider(addressTarget):null;
                if(provider!=null&&!result.Ok){PluginCallResult refreshed=RefreshAddressProvider(baseDir,provider);if(refreshed.Ok)result=InvokeLocked(id,"sync",new Dictionary<string,object>(),trigger,1800,true,ThrottledProgress(id,baseDir));else result.Error=result.Error+"；"+provider.PluginName+" 重新发现失败："+refreshed.Error+"。服务器可能已关机或故障。";}
                if(!result.Ok){failures++;WriteJobState(id,"failed",0,1,result.Error);continue;}IEnumerable<Dictionary<string,object>> drafts=JsonUtil.Array(JsonUtil.Get(result.Payload,"tasks")).Select(JsonUtil.Object);TodoExternalImport.Import(Path.Combine(baseDir,"tasks.json"),id,drafts);WriteJobState(id,"completed",1,1,JsonUtil.String(result.Payload,"summary","已完成"));
            }catch(Exception ex){failures++;WriteJobState(id,"failed",0,1,ex.Message);}
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

    private static PluginCallResult InvokeLocked(string id,string action,object input,string trigger,int timeout,bool exclusive,Action<Dictionary<string,object>> progress)
    {
        if(!exclusive)return PluginRuntime.Invoke(id,action,input,trigger,timeout,progress);
        using(System.Threading.Mutex mutex=new System.Threading.Mutex(false,@"Global\RainmeterPluginSync_"+System.Text.RegularExpressions.Regex.Replace(id,@"[^A-Za-z0-9]","_")))
        {
            bool held=false;try{try{held=mutex.WaitOne(TimeSpan.Zero);}catch(System.Threading.AbandonedMutexException){held=true;}if(!held)throw new InvalidOperationException("该插件已有同步任务正在运行");return PluginRuntime.Invoke(id,action,input,trigger,timeout,progress);}finally{if(held)mutex.ReleaseMutex();}
        }
    }

    private static void WriteJob(string id, Dictionary<string, object> progress)
    {
        using(System.Threading.Mutex mutex=new System.Threading.Mutex(false,@"Global\RainmeterPluginJob_"+System.Text.RegularExpressions.Regex.Replace(id,@"[^A-Za-z0-9]","_"))){bool held=false;try{try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(System.Threading.AbandonedMutexException){held=true;}if(!held)throw new TimeoutException("插件任务状态正忙");string path=Path.Combine(PluginPaths.Jobs,id+".json");Dictionary<string,object> job=File.Exists(path)?JsonUtil.LoadObject(path):new Dictionary<string,object>();foreach(KeyValuePair<string,object> pair in progress)job[pair.Key]=pair.Value;if(!progress.ContainsKey("state"))job["state"]="running";string state=JsonUtil.String(job,"state","");if(state!="running"){job.Remove("pid");job.Remove("entry");job.Remove("process_started_at");}job["plugin_id"]=id;job["updated_at"]=DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture);JsonUtil.SaveAtomic(path,job);}finally{if(held)mutex.ReleaseMutex();}}
    }
    private static void WriteJobState(string id,string state,int current,int total,string message){WriteJob(id,new Dictionary<string,object>{{"state",state},{"current",current},{"total",total},{"message",message??""}});}
    private static bool JobHasState(string id,string state){string path=Path.Combine(PluginPaths.Jobs,id+".json");if(!File.Exists(path))return false;try{return JsonUtil.String(JsonUtil.LoadObject(path),"state","")==state;}catch{return false;}}
    private static void WriteFailureUnlessCancelled(string id,string message)
    {
        using(System.Threading.Mutex mutex=new System.Threading.Mutex(false,@"Global\RainmeterPluginJob_"+System.Text.RegularExpressions.Regex.Replace(id,@"[^A-Za-z0-9]","_"))){bool held=false;try{try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(System.Threading.AbandonedMutexException){held=true;}if(!held)return;string path=Path.Combine(PluginPaths.Jobs,id+".json");Dictionary<string,object> job=File.Exists(path)?JsonUtil.LoadObject(path):new Dictionary<string,object>();if(JsonUtil.String(job,"state","")=="cancelled")return;job["state"]="failed";job["current"]=0;job["total"]=1;job["message"]=message??"";job["plugin_id"]=id;job["updated_at"]=DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture);job.Remove("pid");job.Remove("entry");job.Remove("process_started_at");JsonUtil.SaveAtomic(path,job);}finally{if(held)mutex.ReleaseMutex();}}
    }
    private static int CancelPlugin(string id)
    {
        if(id=="")return 2;string path=Path.Combine(PluginPaths.Jobs,id+".json");if(!File.Exists(path))return 1;Dictionary<string,object> job=JsonUtil.LoadObject(path);int pid=JsonUtil.Int(job,"pid",0);string entry=JsonUtil.String(job,"entry","");if(pid<=0||entry=="")return 1;
        try{using(System.Diagnostics.Process process=System.Diagnostics.Process.GetProcessById(pid)){string actual=process.MainModule.FileName;if(!Path.GetFullPath(actual).Equals(Path.GetFullPath(entry),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("进程身份已变化，拒绝终止");try{process.CloseMainWindow();}catch{}if(!process.WaitForExit(3000))process.Kill();}}catch(ArgumentException){return 1;}WriteJobState(id,"cancelled",0,1,"已取消");return 0;
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
