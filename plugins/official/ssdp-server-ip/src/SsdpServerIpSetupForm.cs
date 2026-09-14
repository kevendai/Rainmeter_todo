using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class SsdpServerIpSetupForm : Form
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024};
    private readonly Dictionary<string,object> _config;
    private readonly TextBox _scanIp=new TextBox(),_mask=new TextBox(),_filter=new TextBox();
    private readonly ComboBox _devices=new ComboBox();
    private readonly Label _status=new Label(),_details=new Label(),_progressText=new Label();
    private readonly ProgressBar _progress=new ProgressBar();
    private readonly Button _search=new Button(),_save=new Button(),_cancel=new Button();
    private readonly ToolTip _toolTip=new ToolTip();
    private readonly object _processGate=new object();
    private readonly List<SsdpDevice> _allDevices=new List<SsdpDevice>();
    private Process _scanProcess;
    public Dictionary<string,object> Payload{get;private set;}

    private SsdpServerIpSetupForm(Dictionary<string,object> config)
    {
        _config=config;Payload=new Dictionary<string,object>();Text="搜索并选择 SSDP 服务器";ClientSize=new Size(700,600);StartPosition=FormStartPosition.CenterScreen;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=true;BackColor=Color.FromArgb(244,249,253);Font=new Font("Microsoft YaHei UI",10F);CancelButton=_cancel;
        Controls.Add(NewLabel("SSDP 服务器 IP 同步",28,22,620,34,16F,FontStyle.Bold));Label hint=NewLabel("首次完整扫描配置范围；保存后以完整 USN 识别设备，IP 改变不会认错服务器。",30,60,640,46,9F,FontStyle.Regular);hint.ForeColor=Color.FromArgb(75,98,120);Controls.Add(hint);
        Controls.Add(NewLabel("扫描范围中的 IP",30,118,260,24,10F,FontStyle.Regular));_scanIp.SetBounds(30,146,290,32);_scanIp.Text=S(config,"scan_ip","");Controls.Add(_scanIp);
        Controls.Add(NewLabel("子网掩码",350,118,200,24,10F,FontStyle.Regular));_mask.SetBounds(350,146,200,32);_mask.Text=S(config,"subnet_mask","255.255.255.0");Controls.Add(_mask);
        _search.Text="全量搜索";_search.SetBounds(566,142,104,38);StylePrimary(_search);_search.Click+=delegate{StartSearch();};Controls.Add(_search);AcceptButton=_search;
        Controls.Add(NewLabel("筛选设备（IP、系统、SERVER 或 USN）",30,204,620,24,10F,FontStyle.Regular));_filter.SetBounds(30,232,640,32);_filter.TextChanged+=delegate{ApplyFilter();};Controls.Add(_filter);
        Controls.Add(NewLabel("选择设备（IP · 系统 · 设备标识）",30,274,620,24,10F,FontStyle.Regular));_devices.SetBounds(30,302,640,34);_devices.DropDownStyle=ComboBoxStyle.DropDownList;_devices.SelectedIndexChanged+=delegate{ShowSelectedDetails();};Controls.Add(_devices);
        _details.SetBounds(30,344,640,46);_details.ForeColor=Color.FromArgb(75,98,120);_details.Font=new Font("Microsoft YaHei UI",8.5F);_details.AutoEllipsis=true;Controls.Add(_details);
        _status.SetBounds(30,396,640,42);_status.ForeColor=Color.FromArgb(75,98,120);_status.Text="填写任意网内 IP 和子网掩码后点击全量搜索。/16 最多扫描 65534 个地址。";Controls.Add(_status);
        _progress.SetBounds(30,448,640,18);_progress.Minimum=0;_progress.Maximum=100;_progress.Style=ProgressBarStyle.Continuous;_progress.Visible=false;Controls.Add(_progress);
        _progressText.SetBounds(30,470,640,24);_progressText.ForeColor=Color.FromArgb(75,98,120);_progressText.TextAlign=ContentAlignment.MiddleLeft;_progressText.Visible=false;Controls.Add(_progressText);
        _cancel.Text="取消";_cancel.DialogResult=DialogResult.Cancel;_cancel.SetBounds(420,530,118,38);StyleButton(_cancel);Controls.Add(_cancel);
        _save.Text="保存所选设备";_save.SetBounds(552,530,118,38);StylePrimary(_save);_save.Enabled=false;_save.Click+=delegate{SaveSelection();};Controls.Add(_save);
        FormClosing+=delegate{StopScanChild();};FormClosed+=delegate{_toolTip.Dispose();};
        float scale;if(Single.TryParse(Environment.GetEnvironmentVariable("RW_WINDOW_SCALE"),NumberStyles.Float,CultureInfo.InvariantCulture,out scale)&&Math.Abs(scale-1F)>0.001F){AutoScaleMode=AutoScaleMode.None;Scale(new SizeF(scale,scale));}
        if(Environment.GetEnvironmentVariable("RAINMETER_UI_SMOKE")=="1")Shown+=delegate{BeginInvoke(new Action(Close));};
    }
    public static int Run(string requestId,Dictionary<string,object> config)
    {
        using(SsdpServerIpSetupForm form=new SsdpServerIpSetupForm(config)){if(form.ShowDialog()!=DialogResult.OK)return SsdpServerIpPlugin.Result(requestId,true,new Dictionary<string,object>{{"summary","未更改 SSDP 服务器设置"}},null);return SsdpServerIpPlugin.Result(requestId,true,form.Payload,null);}
    }
    private void StartSearch()
    {
        NetworkRange range;try{range=NetworkRange.Parse(_scanIp.Text.Trim(),_mask.Text.Trim());}catch(Exception ex){ShowError(ex.Message);return;}
        string scanIp=_scanIp.Text.Trim(),mask=_mask.Text.Trim();int wait=SsdpServerIpPlugin.Clamp(SsdpServerIpPlugin.Int(_config,"scan_wait_ms",1500),300,10000);
        _allDevices.Clear();_devices.Items.Clear();_details.Text="";SetBusy(true,"扫描在独立子进程中运行，窗口仍可移动和关闭。");UpdateProgress(0,range.Count,"准备扫描 0 / "+range.Count);
        BackgroundWorker worker=new BackgroundWorker();worker.DoWork+=delegate(object sender,DoWorkEventArgs e){e.Result=RunScanChild(scanIp,mask,wait);};worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e)
        {
            worker.Dispose();if(IsDisposed)return;SetBusy(false,"");if(e.Error!=null){ShowError(e.Error.Message);return;}ScanResult result=(ScanResult)e.Result;if(result.Error!=""){ShowError(result.Error);return;}
            _allDevices.AddRange(result.Devices);ApplyFilter();
            _status.ForeColor=Color.FromArgb(75,98,120);_status.Text=_allDevices.Count==0?"没有收到带 SERVER 和 USN 的 SSDP 应答。请检查扫描范围和网络转发。":"找到 "+_allDevices.Count+" 个设备。可按 IP、系统、SERVER 或 USN 筛选。";
            UpdateProgress(100,100,"扫描完成");
        };worker.RunWorkerAsync();
    }
    private ScanResult RunScanChild(string scanIp,string mask,int wait)
    {
        ScanResult result=new ScanResult();Process process=null;
        try
        {
            ProcessStartInfo info=new ProcessStartInfo(Application.ExecutablePath,"--scan-child"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            process=new Process{StartInfo=info};if(!process.Start())throw new IOException("无法启动扫描子进程");lock(_processGate)_scanProcess=process;
            process.StandardInput.WriteLine(Json.Serialize(new Dictionary<string,object>{{"scan_ip",scanIp},{"subnet_mask",mask},{"scan_wait_ms",wait}}));process.StandardInput.Close();
            string line;bool finalSeen=false;while((line=process.StandardOutput.ReadLine())!=null)
            {
                Dictionary<string,object> message=Json.DeserializeObject(line) as Dictionary<string,object>;if(message==null)continue;string type=SsdpServerIpPlugin.Str(message,"type","");
                if(type=="progress"){int current=SsdpServerIpPlugin.Int(message,"current",0),total=SsdpServerIpPlugin.Int(message,"total",1);ReportProgress(current,total,SsdpServerIpPlugin.Str(message,"message","正在扫描"));continue;}
                if(type=="result"){finalSeen=true;if(!Convert.ToBoolean(SsdpServerIpPlugin.Get(message,"ok")??false,CultureInfo.InvariantCulture)){result.Error=SsdpServerIpPlugin.Str(message,"error","扫描失败");continue;}foreach(object raw in SsdpServerIpPlugin.Get(message,"devices") as object[]??new object[0]){Dictionary<string,object> d=raw as Dictionary<string,object>;if(d==null)continue;result.Devices.Add(new SsdpDevice{Ip=SsdpServerIpPlugin.Str(d,"ip",""),Server=SsdpServerIpPlugin.Str(d,"server",""),Usn=SsdpServerIpPlugin.Str(d,"usn",""),Location=SsdpServerIpPlugin.Str(d,"location",""),St=SsdpServerIpPlugin.Str(d,"st","")});}}
            }
            process.WaitForExit();string stderr=process.StandardError.ReadToEnd().Trim();if(!finalSeen&&result.Error=="")result.Error=stderr==""?"扫描子进程没有返回结果":stderr;if(process.ExitCode!=0&&result.Error=="")result.Error=stderr==""?"扫描子进程异常退出":stderr;
        }
        finally{lock(_processGate){if(Object.ReferenceEquals(_scanProcess,process))_scanProcess=null;}if(process!=null)process.Dispose();}
        return result;
    }
    private void ReportProgress(int current,int total,string message)
    {
        try{BeginInvoke(new Action(delegate{UpdateProgress(current,total,message);}));}catch(ObjectDisposedException){}catch(InvalidOperationException){}
    }
    private void UpdateProgress(int current,int total,string message)
    {
        if(IsDisposed)return;int percent=total<=0?0:(int)Math.Max(0,Math.Min(100,(long)current*100/total));_progress.Value=percent;_progressText.Text=percent+"%  ·  "+message;
    }
    private void ApplyFilter()
    {
        string query=_filter.Text.Trim();SsdpDevice selected=_devices.SelectedItem as SsdpDevice;_devices.BeginUpdate();try{_devices.Items.Clear();foreach(SsdpDevice device in _allDevices){string haystack=device.Ip+"\n"+device.SystemName()+"\n"+device.ShortId()+"\n"+device.Server+"\n"+device.Usn;if(query==""||haystack.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)_devices.Items.Add(device);}if(selected!=null&&_devices.Items.Contains(selected))_devices.SelectedItem=selected;else if(_devices.Items.Count>0)_devices.SelectedIndex=0;}finally{_devices.EndUpdate();}_save.Enabled=_devices.Items.Count>0;if(_allDevices.Count>0)_status.Text="显示 "+_devices.Items.Count+" / "+_allDevices.Count+" 个设备";
    }
    private void ShowSelectedDetails()
    {
        SsdpDevice choice=_devices.SelectedItem as SsdpDevice;if(choice==null){_details.Text="";_toolTip.SetToolTip(_details,"");return;}_details.Text="SERVER："+choice.Server+Environment.NewLine+"USN："+choice.Usn;_details.Tag=choice;_toolTip.SetToolTip(_details,_details.Text);
    }
    private void StopScanChild(){lock(_processGate){if(_scanProcess!=null){try{if(!_scanProcess.HasExited)_scanProcess.Kill();}catch{}}}}
    private void SaveSelection()
    {
        SsdpDevice choice=_devices.SelectedItem as SsdpDevice;if(choice==null){ShowError("请选择设备");return;}
        int ttl=SsdpServerIpPlugin.Clamp(SsdpServerIpPlugin.Int(_config,"ttl",60),30,86400);
        Payload=new Dictionary<string,object>{{"summary","已选择 SSDP 服务器："+choice.Ip},{"values",new Dictionary<string,object>{{"server_ip",choice.Ip},{"server",choice.Server},{"usn",choice.Usn},{"location",choice.Location}}},{"ttl",ttl},{"config_updates",new Dictionary<string,object>{{"scan_ip",_scanIp.Text.Trim()},{"subnet_mask",_mask.Text.Trim()},{"selected_usn",choice.Usn},{"selected_server",choice.Server},{"last_ip",choice.Ip},{"probe_timeout_ms",SsdpServerIpPlugin.Int(_config,"probe_timeout_ms",800)},{"scan_wait_ms",SsdpServerIpPlugin.Int(_config,"scan_wait_ms",1500)},{"ttl",ttl}}}};
        MessageBox.Show(this,"已锁定设备：\r\n"+choice.Ip+" · "+choice.SystemName()+" · "+choice.ShortId()+"\r\n\r\n当前 IP："+choice.Ip,"SSDP 服务器已保存",MessageBoxButtons.OK,MessageBoxIcon.Information);DialogResult=DialogResult.OK;Close();
    }
    private void SetBusy(bool busy,string message){UseWaitCursor=false;_search.Enabled=!busy;_save.Enabled=!busy&&_devices.Items.Count>0;_scanIp.Enabled=_mask.Enabled=!busy;_filter.Enabled=!busy;_progress.Visible=_progressText.Visible=busy||_progress.Value>0;if(message!=""){_status.ForeColor=Color.FromArgb(75,98,120);_status.Text=message;}}
    private void ShowError(string message){UseWaitCursor=false;_status.ForeColor=Color.FromArgb(198,52,60);_status.Text=message;MessageBox.Show(this,message,"SSDP 搜索",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    private static Label NewLabel(string text,int x,int y,int width,int height,float size,FontStyle style){return new Label{Text=text,Left=x,Top=y,Width=width,Height=height,Font=new Font("Microsoft YaHei UI",size,style),ForeColor=Color.FromArgb(18,46,73),BackColor=Color.Transparent};}
    private static void StyleButton(Button b){b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Color.FromArgb(184,207,226);b.BackColor=Color.White;b.ForeColor=Color.FromArgb(18,46,73);b.Cursor=Cursors.Hand;}
    private static void StylePrimary(Button b){StyleButton(b);b.BackColor=Color.FromArgb(32,112,214);b.ForeColor=Color.White;b.FlatAppearance.BorderSize=0;}
    private static string S(Dictionary<string,object> value,string key,string fallback){return SsdpServerIpPlugin.Str(value,key,fallback);}
    private sealed class ScanResult{public readonly List<SsdpDevice> Devices=new List<SsdpDevice>();public string Error="";}
}