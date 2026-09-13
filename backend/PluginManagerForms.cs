using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using RainmeterBackend;

internal static partial class TodoApp
{
    private const string PluginRegistryUrl = "https://kevendai.github.io/Rainmeter_todo-plugin-registry/index-v1.json";
    private static string PluginDisplayStatus(Dictionary<string,object> state)
    {
        string path=Path.Combine(PluginPaths.Jobs,"io.github.kevendai.arxiv.json");if(!File.Exists(path))return JsonUtil.String(JsonUtil.Object(JsonUtil.Get(state,"meta")),"status","就绪");
        try{Dictionary<string,object> job=JsonUtil.LoadObject(path);string value=JsonUtil.String(job,"message","");return value==""?"插件状态："+JsonUtil.String(job,"state","未知"):value;}catch{return "插件状态暂不可读";}
    }
    private static void ShowLegacySettings()
    {
        Form f=LightUi.Form("外观、备份与更新",700,500);LightUi.Heading(f,"外观、备份与更新","宿主级设置；插件业务配置请在插件页打开。","settings.svg");
        Label scaleLabel=LightUi.Label("桌面磁贴缩放",28,106,240);f.Controls.Add(scaleLabel);ComboBox scale=new ComboBox{Left=28,Top=136,Width=220,DropDownStyle=ComboBoxStyle.DropDownList,Font=new Font("Microsoft YaHei UI",10F)};string[] labels={"自动","75%","80%","90%","100%","110%","125%"},values={"auto","0.75","0.80","0.90","1.00","1.10","1.25"};scale.Items.AddRange(labels);int selected=Array.FindIndex(values,x=>String.Equals(x,UiScale.Mode,StringComparison.OrdinalIgnoreCase));scale.SelectedIndex=selected<0?0:selected;f.Controls.Add(scale);
        Button apply=LightUi.PrimaryButton("应用缩放",268,134,126,DialogResult.None);f.Controls.Add(apply);apply.Click+=delegate{try{UiScale.SaveMode(values[scale.SelectedIndex]);RenderUiScaleSkins();}catch(Exception ex){LightUi.Error(ex.Message);}};
        Label backup=LightUi.Label("加密用户配置备份（rwbackup 2.0，可导入 1.0）",28,214,520);f.Controls.Add(backup);Button export=LightUi.PrimaryButton("导出用户配置",28,250,150,DialogResult.None),import=LightUi.Button("导入用户配置",190,250,150,DialogResult.None);f.Controls.AddRange(new Control[]{export,import});
        export.Click+=delegate{try{string path=ExportUserBackupInteractive();if(path!="")MessageBox.Show("备份已保存：\r\n"+path,"导出完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){LightUi.Error(ex.Message);}};
        import.Click+=delegate{try{string result=ImportUserBackupInteractive();if(result!=""){RenderUiScaleSkins();MessageBox.Show(result,"导入完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}}catch(Exception ex){LightUi.Error(ex.Message);}};
        Label about=LightUi.Label("当前版本："+AppVersion+" · Plugin API v1",28,334,420);f.Controls.Add(about);Button update=LightUi.Button("检查主程序更新",28,372,160,DialogResult.None);f.Controls.Add(update);update.Click+=delegate{try{UpdateCheckResult info=CheckLatestUpdate();if(!info.IsNewer)MessageBox.Show("已是最新版本："+info.Tag,"检查更新");else if(MessageBox.Show("发现 "+info.Tag+"，现在启动升级器？","检查更新",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){StartExternalUpdater();f.Close();}}catch(Exception ex){LightUi.Error(ex.Message);}};
        f.ShowDialog();
    }

    private static void ShowSettings()
    {
        PluginPaths.Ensure();
        Form form=LightUi.Form("待办设置",920,720);
        LightUi.Heading(form,"待办设置","管理独立进程插件、外观、备份与更新。","settings.svg");
        Button close=LightUi.Button("×",860,22,34,DialogResult.Cancel);close.Height=34;form.Controls.Add(close);
        TabControl tabs=new TabControl{Left=28,Top=104,Width=864,Height=530,Font=new Font("Microsoft YaHei UI",10F)};
        TabPage installed=new TabPage("已安装"),market=new TabPage("插件市场");tabs.TabPages.Add(installed);tabs.TabPages.Add(market);form.Controls.Add(tabs);
        ListView list=PluginListView();installed.Controls.Add(list);
        Label status=LightUi.Label("",18,448,610);installed.Controls.Add(status);
        Button toggle=LightUi.PrimaryButton("启用 / 禁用",18,398,132,DialogResult.None);
        Button configure=LightUi.Button("配置",160,398,96,DialogResult.None);
        Button run=LightUi.Button("立即运行",266,398,112,DialogResult.None);
        Button actions=LightUi.Button("插件操作",388,398,112,DialogResult.None);
        Button uninstall=LightUi.DangerButton("卸载程序",510,398,112,DialogResult.None);
        Button local=LightUi.Button("从本地安装",628,398,180,DialogResult.None);
        installed.Controls.AddRange(new Control[]{toggle,configure,run,actions,uninstall,local});
        Action reload=delegate{ReloadPlugins(list,status);};reload();
        System.Windows.Forms.Timer jobTimer=new System.Windows.Forms.Timer{Interval=500};jobTimer.Tick+=delegate{if(list.SelectedItems.Count!=1)return;string path=Path.Combine(PluginPaths.Jobs,list.SelectedItems[0].Name+".json");try{if(File.Exists(path)){Dictionary<string,object> job=JsonUtil.LoadObject(path);string state=JsonUtil.String(job,"state",""),message=JsonUtil.String(job,"message","");int current=JsonUtil.Int(job,"current",0),total=JsonUtil.Int(job,"total",0);status.Text=state+(total>0?" "+current+"/"+total:"")+(message==""?"":" · "+message);}}catch{}};jobTimer.Start();form.FormClosed+=delegate{jobTimer.Stop();jobTimer.Dispose();};
        toggle.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);Dictionary<string,object> c=PluginRuntime.Current(m.Id);c["enabled"]=!JsonUtil.Bool(c,"enabled",false);JsonUtil.SaveAtomic(Path.Combine(PluginPaths.PluginRoot(m.Id),"current.json"),c);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        configure.Click+=delegate{try{ShowPluginConfig(SelectedPlugin(list));reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        run.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);string verb=m.Capabilities.Contains("value_provider")?"Values":m.Capabilities.Contains("todo_source")?"Sync":"";if(verb=="")throw new Exception("此插件由日历按需调用。 ");StartPluginCommand(verb,m.Id);status.Text="已启动 "+m.Name;}catch(Exception ex){LightUi.Error(ex.Message);}};
        actions.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);ContextMenuStrip menu=new ContextMenuStrip();ToolStripItem cancel=menu.Items.Add("取消当前任务");cancel.Click+=delegate{Process.Start(new ProcessStartInfo(PluginHostPath,"Cancel "+m.Id){UseShellExecute=false,CreateNoWindow=true});};ToolStripItem clear=menu.Items.Add("清除该插件创建的待办");clear.Click+=delegate{Process.Start(new ProcessStartInfo(Application.ExecutablePath,"PluginClearTasks "+m.Id){UseShellExecute=false,CreateNoWindow=true});};ToolStripItem log=menu.Items.Add("查看最近错误");log.Click+=delegate{string path=Path.Combine(PluginPaths.Logs,m.Id+".log");MessageBox.Show(File.Exists(path)?File.ReadAllText(path,RuntimeUtil.Utf8NoBom):"暂无插件日志",m.Name+" 日志",MessageBoxButtons.OK,MessageBoxIcon.Information);};if(!String.IsNullOrWhiteSpace(m.Homepage)){ToolStripItem home=menu.Items.Add("打开主页");home.Click+=delegate{RuntimeUtil.Run(m.Homepage);};}if(m.Actions.Count>0)menu.Items.Add(new ToolStripSeparator());foreach(Dictionary<string,object> action in m.Actions){ToolStripItem item=menu.Items.Add(JsonUtil.String(action,"name",JsonUtil.String(action,"id","操作")));item.Tag=action;item.Click+=delegate(object sender,EventArgs ignored){Dictionary<string,object> selectedAction=(Dictionary<string,object>)((ToolStripItem)sender).Tag;string warning=JsonUtil.String(selectedAction,"risk","");if(JsonUtil.Bool(selectedAction,"confirm",false)&&!LightUi.Confirm((warning==""?"确定执行此操作？":warning),"插件操作"))return;Process.Start(new ProcessStartInfo(PluginHostPath,"PluginAction "+m.Id+" "+JsonUtil.String(selectedAction,"id","") ){UseShellExecute=false,CreateNoWindow=true});};}menu.Show(actions,new Point(0,actions.Height));}catch(Exception ex){LightUi.Error(ex.Message);}};
        uninstall.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);if(!LightUi.Confirm("卸载 "+m.Name+" 的程序版本？插件数据默认保留。","卸载插件"))return;Directory.Delete(PluginPaths.PluginRoot(m.Id),true);if(Directory.Exists(PluginPaths.DataRoot(m.Id))&&LightUi.Confirm("程序已卸载。是否同时永久删除该插件的配置、secret、状态和缓存？","删除插件数据"))Directory.Delete(PluginPaths.DataRoot(m.Id),true);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        local.Click+=delegate{try{InstallLocalPlugin(form);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};

        ListView marketList=PluginListView();marketList.Height=390;market.Controls.Add(marketList);
        Label marketStatus=LightUi.Label("尚未加载市场",18,448,610);market.Controls.Add(marketStatus);
        Button refresh=LightUi.PrimaryButton("刷新市场",18,398,132,DialogResult.None);market.Controls.Add(refresh);
        Button installMarket=LightUi.Button("安装 / 更新",160,398,132,DialogResult.None);market.Controls.Add(installMarket);
        refresh.Click+=delegate{try{LoadMarket(marketList,marketStatus);}catch(Exception ex){marketStatus.Text="市场不可用："+ex.Message;marketStatus.ForeColor=LightUi.Danger;}};
        installMarket.Click+=delegate{try{if(marketList.SelectedItems.Count!=1)throw new Exception("请先选择一个市场插件。");InstallMarketPlugin(marketList.SelectedItems[0]);reload();LoadMarket(marketList,marketStatus);}catch(Exception ex){LightUi.Error(ex.Message);}};
        Button legacy=LightUi.Button("外观、备份与兼容设置",28,650,230,DialogResult.None);legacy.Click+=delegate{ShowLegacySettings();};form.Controls.Add(legacy);
        Label version=LightUi.Label("Rainmeter Desktop Widgets "+AppVersion+" · Plugin API v1",470,657,420);form.Controls.Add(version);
        form.ShowDialog();
    }

    private static ListView PluginListView()
    {
        ListView v=new ListView{Left=18,Top=18,Width=790,Height=360,View=View.Details,FullRowSelect=true,GridLines=true,HideSelection=false};
        v.Columns.Add("插件",250);v.Columns.Add("版本",90);v.Columns.Add("状态",90);v.Columns.Add("能力",190);v.Columns.Add("权限",140);return v;
    }
    private static void ReloadPlugins(ListView view,Label status)
    {
        view.Items.Clear();int invalid=0;
        foreach(string root in Directory.Exists(PluginPaths.Plugins)?Directory.GetDirectories(PluginPaths.Plugins):new string[0])try
        {
            string id=Path.GetFileName(root);PluginManifest m=PluginRuntime.Resolve(id,false);Dictionary<string,object> c=PluginRuntime.Current(id);
            ListViewItem item=new ListViewItem(m.Name);item.Name=m.Id;item.Tag=m;item.SubItems.Add(m.Version);item.SubItems.Add(JsonUtil.Bool(c,"enabled",false)?"已启用":"已禁用");item.SubItems.Add(String.Join(", ",m.Capabilities.ToArray()));item.SubItems.Add(String.Join(", ",m.Permissions.ToArray()));view.Items.Add(item);
        }catch{invalid++;}
        status.Text="已安装 "+view.Items.Count.ToString(CultureInfo.InvariantCulture)+" 个插件"+(invalid>0?"；"+invalid+" 个安装损坏":"");
    }
    private static PluginManifest SelectedPlugin(ListView view){if(view.SelectedItems.Count!=1)throw new Exception("请先选择一个插件。");return (PluginManifest)view.SelectedItems[0].Tag;}

    private static void ShowPluginConfig(PluginManifest manifest)
    {
        string root=PluginPaths.VersionRoot(manifest.Id,manifest.Version);if(String.IsNullOrWhiteSpace(manifest.SettingsSchema))throw new Exception("该插件没有可配置项。");
        Dictionary<string,object> schema=JsonUtil.LoadObject(PluginManifest.SafeChildPath(root,manifest.SettingsSchema,"设置 Schema"));Dictionary<string,object> props=JsonUtil.Object(JsonUtil.Get(schema,"properties"));
        string data=PluginPaths.DataRoot(manifest.Id);Directory.CreateDirectory(data);string configPath=Path.Combine(data,"config.json"),secretPath=Path.Combine(data,"secret.dat");
        Dictionary<string,object> config=File.Exists(configPath)?JsonUtil.LoadObject(configPath):new Dictionary<string,object>();Dictionary<string,object> secret=File.Exists(secretPath)?JsonUtil.ReadDpapiJson(secretPath):new Dictionary<string,object>();
        string oldConfig=JsonUtil.Serialize(config),oldSecret=JsonUtil.Serialize(secret);HashSet<string> required=new HashSet<string>(JsonUtil.Array(JsonUtil.Get(schema,"required")).Select(Convert.ToString),StringComparer.OrdinalIgnoreCase);
        Form f=LightUi.Form(manifest.Name+" 设置",680,Math.Min(820,190+props.Count*82));LightUi.Heading(f,manifest.Name,"敏感项使用当前 Windows 用户 DPAPI 加密。","settings.svg");
        Panel panel=new Panel{Left=24,Top=102,Width=630,Height=f.ClientSize.Height-180,AutoScroll=true};f.Controls.Add(panel);Dictionary<string,Control> controls=new Dictionary<string,Control>();int y=8;
        foreach(KeyValuePair<string,object> pair in props.OrderBy(x=>JsonUtil.Int(JsonUtil.Object(x.Value),"x-order",999)))
        {
            Dictionary<string,object> p=JsonUtil.Object(pair.Value);string type=JsonUtil.String(p,"type","string"),title=JsonUtil.String(p,"title",pair.Key);bool isSecret=JsonUtil.Bool(p,"x-secret",false)||type=="password";object existing=PluginSettingValue(manifest.Id,pair.Key,isSecret?secret:config,secret,p);
            Label label=LightUi.Label(title,8,y,590);panel.Controls.Add(label);Control control;
            if(type=="boolean")control=new CheckBox{Left=8,Top=y+28,Width=590,Checked=existing!=null&&Convert.ToBoolean(existing,CultureInfo.InvariantCulture),Text="启用",Font=new Font("Microsoft YaHei UI",10F)};
            else if(type=="enum") { ComboBox box=new ComboBox{Left=8,Top=y+28,Width=590,DropDownStyle=ComboBoxStyle.DropDownList};foreach(object option in JsonUtil.Array(JsonUtil.Get(p,"enum")))box.Items.Add(Convert.ToString(option));box.SelectedItem=Convert.ToString(existing);control=box; }
            else control=new TextBox{Left=8,Top=y+28,Width=590,Height=type=="multiline"?86:28,Multiline=type=="multiline",ScrollBars=type=="multiline"?ScrollBars.Vertical:ScrollBars.None,Text=existing==null?"":Convert.ToString(existing,CultureInfo.InvariantCulture),UseSystemPasswordChar=isSecret};
            panel.Controls.Add(control);controls[pair.Key]=control;y+=type=="multiline"?136:78;
        }
        panel.AutoScrollMinSize=new Size(0,y+10);Button save=LightUi.PrimaryButton("保存",532,f.ClientSize.Height-60,120,DialogResult.OK);f.Controls.Add(save);
        if(f.ShowDialog()!=DialogResult.OK)return;
        foreach(KeyValuePair<string,Control> pair in controls)
        {
            Dictionary<string,object> p=JsonUtil.Object(props[pair.Key]);string type=JsonUtil.String(p,"type","string");bool isSecret=JsonUtil.Bool(p,"x-secret",false)||type=="password";object value;
            if(type=="boolean")value=((CheckBox)pair.Value).Checked;else if(type=="integer"){int number;if(!Int32.TryParse(pair.Value.Text,out number))throw new Exception(JsonUtil.String(p,"title",pair.Key)+"必须是整数。");int min=JsonUtil.Int(p,"minimum",Int32.MinValue),max=JsonUtil.Int(p,"maximum",Int32.MaxValue);if(number<min||number>max)throw new Exception(JsonUtil.String(p,"title",pair.Key)+"超出允许范围。");value=number;}else value=pair.Value.Text;if(required.Contains(pair.Key)&&String.IsNullOrWhiteSpace(Convert.ToString(value,CultureInfo.InvariantCulture)))throw new Exception(JsonUtil.String(p,"title",pair.Key)+"为必填项。");
            (isSecret?secret:config)[pair.Key]=value;
        }
        JsonUtil.SaveAtomic(configPath,config);JsonUtil.WriteDpapiJson(secretPath,secret);
        try{using(Process validation=Process.Start(new ProcessStartInfo(PluginHostPath,"PluginAction "+manifest.Id+" validate_settings"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){if(validation==null||!validation.WaitForExit(35000)||validation.ExitCode!=0)throw new Exception("插件拒绝了当前设置。");}}catch{JsonUtil.SaveAtomic(configPath,JsonUtil.Object(JsonUtil.Deserialize(oldConfig)));JsonUtil.WriteDpapiJson(secretPath,JsonUtil.Object(JsonUtil.Deserialize(oldSecret)));throw;}
    }

    private static object PluginSettingValue(string pluginId,string key,Dictionary<string,object> direct,Dictionary<string,object> secrets,Dictionary<string,object> property)
    {
        object value=JsonUtil.Get(direct,key);if(value!=null)return value;if(pluginId!="io.github.kevendai.arxiv")return JsonUtil.Get(property,"default");
        Dictionary<string,object> root=JsonUtil.Object(JsonUtil.Get(secrets,"paper_settings")),translation=JsonUtil.Object(JsonUtil.Get(secrets,"translation"));Dictionary<string,object> section=root;string legacy=key;
        if(key=="api_url"||key=="api_model"||key=="api_key"||key=="max_concurrency"||key=="timeout_seconds"){section=JsonUtil.Object(JsonUtil.Get(root,"DeepSeek"));legacy=key=="api_url"?"BaseUrl":key=="api_model"?"Model":key=="api_key"?"ApiKey":key=="max_concurrency"?"MaxConcurrency":"TimeoutSeconds";}
        else if(key.StartsWith("file_",StringComparison.Ordinal)){section=JsonUtil.Object(JsonUtil.Get(root,"FileServer"));legacy=key=="file_enabled"?"Enabled":key=="file_url"?"BaseUrl":key=="file_account"?"Account":"Password";}
        else if(key=="rss_enabled"){section=JsonUtil.Object(JsonUtil.Get(root,"Rss"));legacy="Enabled";}
        else if(key=="translation_secret_id"||key=="translation_secret_key"){section=translation;legacy=key.EndsWith("_id",StringComparison.Ordinal)?"SecretId":"SecretKey";}
        else if(key!="enabled"){section=JsonUtil.Object(JsonUtil.Get(root,"Scoring"));Dictionary<string,string> names=new Dictionary<string,string>{{"categories","Categories"},{"exclude_categories","ExcludeCategories"},{"title_prompt","TitlePrompt"},{"abstract_prompt","AbstractPrompt"},{"title_threshold","TitleThreshold"},{"title_batch_size","TitleBatchSize"},{"abstract_batch_size","AbstractBatchSize"},{"import_count","ImportCount"},{"cache_days","CacheDays"}};if(names.ContainsKey(key))legacy=names[key];}
        else legacy="Enabled";
        value=JsonUtil.Get(section,legacy);return value??JsonUtil.Get(property,"default");
    }

    private static void InstallLocalPlugin(Form owner)
    {
        OpenFileDialog open=new OpenFileDialog{Filter="Rainmeter 插件 (*.rwplugin)|*.rwplugin",CheckFileExists=true};if(open.ShowDialog(owner)!=DialogResult.OK)return;
        if(!LightUi.Confirm("插件是普通第三方程序，权限声明不能限制其系统访问。仅安装你信任的文件。继续吗？","第三方插件警告"))return;
        string script=Path.Combine(ResourceDir,"PluginInstaller.ps1");if(!File.Exists(script))throw new Exception("缺少 PluginInstaller.ps1。");
        RunPluginInstaller(open.FileName,"");MessageBox.Show("插件安装成功。默认保持禁用，请检查权限后手动启用。","插件安装",MessageBoxButtons.OK,MessageBoxIcon.Information);
    }

    private static void RunPluginInstaller(string package,string expectedSha)
    {
        string script=Path.Combine(ResourceDir,"PluginInstaller.ps1");if(!File.Exists(script))throw new Exception("缺少 PluginInstaller.ps1。");string sha=expectedSha==""?"":" -ExpectedSha256 \""+expectedSha+"\"";
        Process p=Process.Start(new ProcessStartInfo("pwsh.exe","-NoLogo -NoProfile -ExecutionPolicy Bypass -File \""+script+"\" -Package \""+package.Replace("\"","\"\"")+"\" -PluginRoot \""+PluginPaths.Plugins+"\""+sha){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true});string output=p.StandardOutput.ReadToEnd(),error=p.StandardError.ReadToEnd();p.WaitForExit();if(p.ExitCode!=0)throw new Exception(error==""?output:error);
    }
    private static void InstallMarketPlugin(ListViewItem item)
    {
        Dictionary<string,object> record=item.Tag as Dictionary<string,object>;if(record==null||!JsonUtil.Bool(record,"official",false))throw new Exception("仅允许安装官方市场条目。");string url=JsonUtil.String(record,"download",""),sha=JsonUtil.String(record,"sha256","");Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||!uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase))throw new Exception("市场下载地址必须是 GitHub HTTPS Release。");if(!System.Text.RegularExpressions.Regex.IsMatch(sha,@"^[a-fA-F0-9]{64}$"))throw new Exception("市场条目缺少有效 SHA256。");
        string temporary=Path.Combine(Path.GetTempPath(),"rw-market-"+Guid.NewGuid().ToString("N")+".rwplugin");try{using(WebClient web=new WebClient()){web.Headers[HttpRequestHeader.UserAgent]="RainmeterDesktopWidgets/"+AppVersion;web.DownloadFile(uri,temporary);}RunPluginInstaller(temporary,sha);}finally{try{File.Delete(temporary);}catch{}}
    }

    private static void LoadMarket(ListView view,Label status)
    {
        string json="";bool cached=false;try{using(WebClient web=new WebClient()){web.Headers[HttpRequestHeader.UserAgent]="RainmeterDesktopWidgets/"+AppVersion;json=web.DownloadString(PluginRegistryUrl);File.WriteAllText(PluginPaths.RegistryCache,json,RuntimeUtil.Utf8NoBom);}}catch{if(File.Exists(PluginPaths.RegistryCache)){json=File.ReadAllText(PluginPaths.RegistryCache,RuntimeUtil.Utf8NoBom);cached=true;}else throw;}
        Dictionary<string,object> index=JsonUtil.Object(JsonUtil.Deserialize(json));view.Items.Clear();foreach(object raw in JsonUtil.Array(JsonUtil.Get(index,"plugins"))){Dictionary<string,object> p=JsonUtil.Object(raw);if(!JsonUtil.Bool(p,"official",false))continue;string id=JsonUtil.String(p,"id","");ListViewItem item=new ListViewItem(JsonUtil.String(p,"name",id));item.Name=id;item.Tag=p;item.SubItems.Add(JsonUtil.String(p,"version",""));item.SubItems.Add(Directory.Exists(PluginPaths.PluginRoot(id))?"已安装":"可安装");item.SubItems.Add(JsonUtil.String(p,"capability",""));item.SubItems.Add(JsonUtil.String(p,"permissions",""));view.Items.Add(item);}DateTime cacheTime=File.Exists(PluginPaths.RegistryCache)?File.GetLastWriteTime(PluginPaths.RegistryCache):DateTime.Now;status.Text=(cached?"离线：使用上次成功缓存；":"官方市场已加载；")+"缓存时间 "+cacheTime.ToString("yyyy-MM-dd HH:mm");
    }
}
