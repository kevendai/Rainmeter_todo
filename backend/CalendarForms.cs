using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using RainmeterBackend;


internal static partial class CalendarApp
{
    private static bool EditInteractive(Dictionary<string,object> original,Dictionary<string,object> state,Dictionary<string,object> cache)
    {
        bool hasCalDav=File.Exists(SecretPath), isNew=original==null, originalCalDav=!isNew&&S(original,"source")=="caldav";
        Form f=LightUi.Form(isNew?"新建日程":"编辑日程",640,800);LightUi.Heading(f,isNew?"新建日程":"编辑日程",isNew?"创建本地日程或同步到 CalDAV 日历。":"编辑日程信息，删除入口位于窗口底部。",isNew?"new-calendar.svg":null);
        Func<string,int,int,int,int,string,TextBox> addField = delegate(string label,int x,int y,int width,int height,string text) {
            f.Controls.Add(new Label{Text=label,Left=x,Top=y-26,Width=width,Height=22,BackColor=Color.Transparent,ForeColor=LightUi.Text,Font=new System.Drawing.Font("Microsoft YaHei UI",9.5F,System.Drawing.FontStyle.Bold)});
            Panel box=RoundedPanel(x,y,width,height,Color.FromArgb(252,254,255),Color.FromArgb(220,230,241),11);
            TextBox input=new TextBox{Left=14,Top=Math.Max(8,(height-22)/2),Width=width-28,Height=height-16,AutoSize=false,BorderStyle=BorderStyle.None,BackColor=Color.FromArgb(252,254,255),ForeColor=LightUi.Text,Font=new System.Drawing.Font("Microsoft YaHei UI",10F),Text=text??""};
            input.GotFocus+=delegate{box.BackColor=Color.White;box.Invalidate();};
            input.LostFocus+=delegate{box.BackColor=Color.FromArgb(252,254,255);box.Invalidate();};
            box.Controls.Add(input);f.Controls.Add(box);return input;
        };
        Func<string,int,int,int,Button> addPicker = delegate(string text,int x,int y,int width) {
            Button b=LightUi.Button(text,x,y,width,DialogResult.None);b.Height=40;b.TextAlign=ContentAlignment.MiddleLeft;b.Padding=new Padding(12,0,8,0);b.BackColor=Color.FromArgb(252,254,255);b.FlatAppearance.BorderSize=1;b.FlatAppearance.BorderColor=Color.FromArgb(220,230,241);b.FlatAppearance.MouseOverBackColor=Color.White;return b;
        };
        f.Controls.Add(new Label{Text="日历",Left=26,Top=92,Width=120,Height=22,BackColor=Color.Transparent,ForeColor=LightUi.Text,Font=new System.Drawing.Font("Microsoft YaHei UI",9.5F,System.Drawing.FontStyle.Bold)});
        string selectedSource=isNew&&hasCalDav?"caldav":originalCalDav&&hasCalDav?"caldav":"local";
        bool allowSourceChange=isNew||(!originalCalDav&&hasCalDav);
        Button localSource=LightUi.Button("本地日历",26,118,110,DialogResult.None),caldavSource=LightUi.Button("CalDAV 日历",144,118,124,DialogResult.None);
        localSource.Height=caldavSource.Height=36;localSource.TextAlign=caldavSource.TextAlign=ContentAlignment.MiddleCenter;caldavSource.Visible=hasCalDav;localSource.Enabled=caldavSource.Enabled=allowSourceChange;
        Action paintSource=delegate{Button[] sourceButtons=new[]{localSource,caldavSource};foreach(Button b in sourceButtons){bool active=(b==localSource&&selectedSource=="local")||(b==caldavSource&&selectedSource=="caldav");b.BackColor=active?LightUi.AccentFill:Color.FromArgb(246,251,255);b.ForeColor=active?Color.White:LightUi.Text;b.FlatAppearance.BorderSize=0;b.FlatAppearance.BorderColor=b.BackColor;b.FlatAppearance.MouseOverBackColor=active?Color.FromArgb(38,118,222):Color.White;b.FlatAppearance.MouseDownBackColor=active?Color.FromArgb(25,94,185):Color.FromArgb(235,245,253);b.Font=new System.Drawing.Font("Microsoft YaHei UI",9F,active?System.Drawing.FontStyle.Bold:System.Drawing.FontStyle.Regular);}};localSource.MouseEnter+=delegate{paintSource();};caldavSource.MouseEnter+=delegate{paintSource();};localSource.MouseLeave+=delegate{paintSource();};caldavSource.MouseLeave+=delegate{paintSource();};localSource.Click+=delegate{selectedSource="local";paintSource();};caldavSource.Click+=delegate{selectedSource="caldav";paintSource();};paintSource();f.Controls.AddRange(new Control[]{localSource,caldavSource});
        TextBox title=addField("标题 *",26,178,588,38,isNew?"":CleanTitle(S(original,"title")));
        f.Controls.Add(new Label{Text="日期与时间",Left=26,Top=240,Width=160,Height=22,BackColor=Color.Transparent,ForeColor=LightUi.Text,Font=new System.Drawing.Font("Microsoft YaHei UI",9.5F,System.Drawing.FontStyle.Bold)});
        DateTimeOffset s=isNew?DateTimeOffset.Now:RuntimeUtil.Date(original,"start_at")??DateTimeOffset.Now, en=isNew?s.AddHours(1):RuntimeUtil.Date(original,"end_at")??s.AddHours(1);
        DateTime selectedDate=s.DateTime.Date;TimeSpan selectedStart=new TimeSpan(s.Hour,s.Minute,0),selectedEnd=new TimeSpan(en.Hour,en.Minute,0);bool allDaySelected=!isNew&&B(original,"all_day");
        Button prevDate=LightUi.Button("‹",26,268,38,DialogResult.None),dateButton=LightUi.Button(selectedDate.ToString("yyyy-MM-dd"),70,268,150,DialogResult.None),nextDate=LightUi.Button("›",226,268,38,DialogResult.None);
        prevDate.Height=dateButton.Height=nextDate.Height=36;dateButton.TextAlign=ContentAlignment.MiddleCenter;Button allDay=LightUi.Button("全天",282,268,82,DialogResult.None);allDay.Height=36;allDay.TextAlign=ContentAlignment.MiddleCenter;f.Controls.AddRange(new Control[]{prevDate,dateButton,nextDate,allDay});
        Panel timeGroup=new Panel{Left=26,Top=314,Width=588,Height=66,BackColor=Color.Transparent};
        Label startLabel=new Label{Text="开始时间  "+selectedStart.ToString(@"hh\:mm"),Left=0,Top=0,Width=240,Height=20,BackColor=Color.Transparent,ForeColor=LightUi.Muted,Font=new System.Drawing.Font("Microsoft YaHei UI",9F)};
        Label endLabel=new Label{Text="结束时间  "+selectedEnd.ToString(@"hh\:mm"),Left=294,Top=0,Width=240,Height=20,BackColor=Color.Transparent,ForeColor=LightUi.Muted,Font=new System.Drawing.Font("Microsoft YaHei UI",9F)};
        TimeSlider startSlider=new TimeSlider{Left=0,Top=24,Width=260,Value=Math.Min(95,Math.Max(0,(int)selectedStart.TotalMinutes/15))};
        TimeSlider endSlider=new TimeSlider{Left=294,Top=24,Width=260,Value=Math.Min(95,Math.Max(0,(int)selectedEnd.TotalMinutes/15))};
        timeGroup.Controls.AddRange(new Control[]{startLabel,endLabel,startSlider,endSlider});f.Controls.Add(timeGroup);
        Action updateTimeLabels=delegate{selectedStart=TimeSpan.FromMinutes(startSlider.Value*15);selectedEnd=TimeSpan.FromMinutes(endSlider.Value*15);startLabel.Text="开始时间  "+selectedStart.ToString(@"hh\:mm");endLabel.Text="结束时间  "+selectedEnd.ToString(@"hh\:mm");};
        startSlider.ValueChanged+=delegate{updateTimeLabels();};endSlider.ValueChanged+=delegate{updateTimeLabels();};updateTimeLabels();
        Action updateAllDay=delegate{Color back=allDaySelected?LightUi.AccentFill:Color.FromArgb(235,245,253);allDay.BackColor=back;allDay.ForeColor=allDaySelected?Color.White:LightUi.Text;allDay.FlatAppearance.MouseOverBackColor=allDaySelected?Color.FromArgb(38,118,222):Color.White;allDay.FlatAppearance.MouseDownBackColor=allDaySelected?Color.FromArgb(25,94,185):Color.FromArgb(218,236,251);startSlider.Enabled=endSlider.Enabled=!allDaySelected;startLabel.ForeColor=endLabel.ForeColor=allDaySelected?Color.FromArgb(150,165,185):LightUi.Muted;startSlider.Invalidate();endSlider.Invalidate();};
        allDay.MouseEnter+=delegate{updateAllDay();};allDay.MouseLeave+=delegate{updateAllDay();};
        allDay.Click+=delegate{allDaySelected=!allDaySelected;updateAllDay();};updateAllDay();
        Action refreshDate=delegate{dateButton.Text=selectedDate.ToString("yyyy-MM-dd");};prevDate.Click+=delegate{selectedDate=selectedDate.AddDays(-1);refreshDate();};nextDate.Click+=delegate{selectedDate=selectedDate.AddDays(1);refreshDate();};dateButton.Click+=delegate{selectedDate=DateTime.Now.Date;refreshDate();};
        TextBox location=addField("地点",26,548,588,38,isNew?"":S(original,"location"));location.Text=location.Text==""?"添加地点":location.Text;location.ForeColor=S(original,"location")==""?Color.FromArgb(150,165,185):LightUi.Text;location.GotFocus+=delegate{if(location.Text=="添加地点"){location.Text="";location.ForeColor=LightUi.Text;}};location.LostFocus+=delegate{if(location.Text.Trim()==""){location.Text="添加地点";location.ForeColor=Color.FromArgb(150,165,185);}};
        TextBox url=addField("链接",26,612,588,38,isNew?"":S(original,"url"));url.Text=url.Text==""?"添加会议链接、网页或本地路径":url.Text;url.ForeColor=S(original,"url")==""?Color.FromArgb(150,165,185):LightUi.Text;url.GotFocus+=delegate{if(url.Text=="添加会议链接、网页或本地路径"){url.Text="";url.ForeColor=LightUi.Text;}};url.LostFocus+=delegate{if(url.Text.Trim()==""){url.Text="添加会议链接、网页或本地路径";url.ForeColor=Color.FromArgb(150,165,185);}};
        f.Controls.Add(new Label{Text="提醒",Left=26,Top=404,Width=120,Height=22,BackColor=Color.Transparent,ForeColor=LightUi.Text,Font=new System.Drawing.Font("Microsoft YaHei UI",9.5F,System.Drawing.FontStyle.Bold)});
        List<int> reminderMinutes=isNew?new List<int>():Reminders(original);
        List<int> customStartReminders=new List<int>();
        HashSet<int> disabledCustomStartReminders=new HashSet<int>();
        List<string> customAlarms=isNew?new List<string>():CustomAlarms(original);
        int originalCustomAlarmCount=customAlarms.Count;
        bool reminderExpanded=false;
        Panel reminderPanel=RoundedPanel(26,428,588,94,Color.FromArgb(248,252,255),Color.FromArgb(224,233,244),12);
        Label bell=new Label{Text="\xE7ED",Left=16,Top=14,Width=26,Height=28,BackColor=Color.Transparent,ForeColor=LightUi.Accent,Font=new System.Drawing.Font("Segoe Fluent Icons",13F),TextAlign=ContentAlignment.MiddleCenter};
        Label reminderText=new Label{Text="日程开始前提醒我",Left=50,Top=16,Width=172,Height=24,BackColor=Color.Transparent,ForeColor=LightUi.Text,Font=new System.Drawing.Font("Microsoft YaHei UI",10F)};
        Button addReminder=LightUi.Button("+ 添加提醒",392,10,98,DialogResult.None);addReminder.Height=34;addReminder.TextAlign=ContentAlignment.MiddleCenter;addReminder.ForeColor=LightUi.Accent;addReminder.BackColor=Color.FromArgb(235,245,253);addReminder.UseVisualStyleBackColor=false;
        Button expandReminder=LightUi.Button("展开",500,10,66,DialogResult.None);expandReminder.Height=34;expandReminder.TextAlign=ContentAlignment.MiddleCenter;expandReminder.ForeColor=LightUi.Muted;expandReminder.BackColor=Color.FromArgb(235,245,253);
        expandReminder.UseVisualStyleBackColor=false;
        reminderPanel.Controls.AddRange(new Control[]{bell,reminderText,addReminder,expandReminder});
        FlowLayoutPanel reminderChips=new FlowLayoutPanel{Left=16,Top=50,Width=550,Height=34,BackColor=Color.Transparent,FlowDirection=FlowDirection.LeftToRight,WrapContents=false};
        reminderPanel.Controls.Add(reminderChips);f.Controls.Add(reminderPanel);
        Panel reminderDrop=RoundedPanel(26,521,588,48,Color.FromArgb(248,252,255),Color.FromArgb(213,229,244),12);
        FlowLayoutPanel extraReminderChips=new FlowLayoutPanel{Left=14,Top=10,Width=560,Height=30,BackColor=Color.Transparent,FlowDirection=FlowDirection.LeftToRight,WrapContents=true,Visible=true};
        reminderDrop.Controls.Add(extraReminderChips);reminderDrop.Visible=false;f.Controls.Add(reminderDrop);
        Func<int,string> reminderLabel=delegate(int m){if(m>=1440&&m%1440==0)return (m/1440)+" 天前";if(m>=60&&m%60==0)return (m/60)+" 小时前";return m+" 分钟前";};
        int[] quickReminders=new[]{5,15,30,60,300,1440};
        Action renderReminders=null;
        Action showAddReminderDialog=delegate{Form rf=LightUi.Form("添加提醒",360,210);LightUi.Heading(rf,"添加提醒","只能添加日程开始前的提醒。");TextBox num=new TextBox{Left=36,Top=94,Width=110,Height=30,Text="15",Font=new System.Drawing.Font("Microsoft YaHei UI",11F)};ComboBox unit=new ComboBox{Left=158,Top=92,Width=100,Height=34,DropDownStyle=ComboBoxStyle.DropDownList,Font=new System.Drawing.Font("Microsoft YaHei UI",10F)};unit.Items.AddRange(new object[]{"分钟","小时","天"});unit.SelectedIndex=0;Button ok=LightUi.PrimaryButton("添加",204,150,72,DialogResult.OK);Button dialogCancel=LightUi.Button("取消",282,150,56,DialogResult.Cancel);rf.Controls.AddRange(new Control[]{num,unit,ok,dialogCancel});rf.AcceptButton=ok;rf.CancelButton=dialogCancel;if(rf.ShowDialog()==DialogResult.OK){int value;if(Int32.TryParse(num.Text.Trim(),out value)&&value>0){int minutes=value*(unit.SelectedIndex==2?1440:unit.SelectedIndex==1?60:1);if(quickReminders.Contains(minutes)){if(!reminderMinutes.Contains(minutes))reminderMinutes.Add(minutes);}else{if(!customStartReminders.Contains(minutes))customStartReminders.Add(minutes);disabledCustomStartReminders.Remove(minutes);}renderReminders();}else LightUi.Error("请输入有效数字");}};
        Action<Button,Color,Color,Color> stabilizeChip=delegate(Button chip,Color back,Color fore,Color hover){chip.UseVisualStyleBackColor=false;chip.BackColor=back;chip.ForeColor=fore;chip.FlatAppearance.BorderSize=1;chip.FlatAppearance.BorderColor=Color.FromArgb(220,230,241);chip.FlatAppearance.MouseOverBackColor=hover;chip.FlatAppearance.MouseDownBackColor=hover;chip.MouseEnter+=delegate{chip.BackColor=hover;};chip.MouseLeave+=delegate{chip.BackColor=back;chip.ForeColor=fore;};};
        renderReminders=delegate{reminderChips.Controls.Clear();extraReminderChips.Controls.Clear();foreach(int m in quickReminders){Button chip=LightUi.Button(reminderLabel(m),0,0,m>=1440?72:86,DialogResult.None);chip.Height=30;chip.Margin=new Padding(0,0,8,0);chip.Tag=m;bool active=reminderMinutes.Contains(m);Color back=active?LightUi.AccentFill:Color.FromArgb(252,254,255),fore=active?Color.White:LightUi.Accent,hover=active?Color.FromArgb(38,118,222):Color.FromArgb(235,245,253);stabilizeChip(chip,back,fore,hover);chip.Click+=delegate(object sender,EventArgs args){int value=(int)((Control)sender).Tag;if(reminderMinutes.Contains(value))reminderMinutes.Remove(value);else reminderMinutes.Add(value);renderReminders();};reminderChips.Controls.Add(chip);}int extraCount=0;foreach(int m in customStartReminders.ToList()){int labelWidth=Math.Max(78,Math.Min(118,TextRenderer.MeasureText(reminderLabel(m),new System.Drawing.Font("Microsoft YaHei UI",9F)).Width+24));Panel customWrap=new Panel{Width=labelWidth+30,Height=30,Margin=new Padding(0,0,8,8),BackColor=Color.Transparent,Tag=m};Button customStart=LightUi.Button(reminderLabel(m),0,0,labelWidth,DialogResult.None);customStart.Height=30;customStart.Tag=m;bool active=!disabledCustomStartReminders.Contains(m);Color back=active?LightUi.AccentFill:Color.FromArgb(252,254,255),fore=active?Color.White:LightUi.Accent,hover=active?Color.FromArgb(38,118,222):Color.FromArgb(235,245,253);stabilizeChip(customStart,back,fore,hover);customStart.Click+=delegate(object sender,EventArgs args){int value=(int)((Control)sender).Tag;if(disabledCustomStartReminders.Contains(value))disabledCustomStartReminders.Remove(value);else disabledCustomStartReminders.Add(value);renderReminders();};Button removeCustom=LightUi.Button("×",labelWidth+2,0,26,DialogResult.None);removeCustom.Height=30;removeCustom.Tag=m;stabilizeChip(removeCustom,Color.FromArgb(255,246,246),Color.FromArgb(205,70,70),Color.FromArgb(255,238,238));removeCustom.Click+=delegate(object sender,EventArgs args){int value=(int)((Control)sender).Tag;customStartReminders.Remove(value);disabledCustomStartReminders.Remove(value);renderReminders();};customWrap.Controls.Add(customStart);customWrap.Controls.Add(removeCustom);extraReminderChips.Controls.Add(customWrap);extraCount++;}foreach(string raw in customAlarms.ToList()){Button custom=LightUi.Button("CalDAV提醒 ×",0,0,106,DialogResult.None);custom.Height=30;custom.Margin=new Padding(0,0,8,8);custom.Tag=raw;stabilizeChip(custom,Color.FromArgb(255,246,246),Color.FromArgb(205,70,70),Color.FromArgb(255,238,238));custom.Click+=delegate(object sender,EventArgs args){customAlarms.Remove(Convert.ToString(((Control)sender).Tag));renderReminders();};extraReminderChips.Controls.Add(custom);extraCount++;}int rows=Math.Max(1,(int)Math.Ceiling(extraCount/4.0));reminderDrop.Height=18+rows*38;extraReminderChips.Height=reminderDrop.Height-16;reminderDrop.Visible=reminderExpanded;reminderDrop.BringToFront();expandReminder.Text=reminderExpanded?"收起":"展开";};
        addReminder.Click+=delegate{showAddReminderDialog();};
        expandReminder.Click+=delegate{reminderExpanded=!reminderExpanded;renderReminders();};
        renderReminders();
        TextBox description=addField("备注",26,674,588,42,isNew?"":S(original,"description"));description.Multiline=true;description.ScrollBars=ScrollBars.None;description.Text=description.Text==""?"添加备注":description.Text;description.ForeColor=S(original,"description")==""?Color.FromArgb(150,165,185):LightUi.Text;description.GotFocus+=delegate{if(description.Text=="添加备注"){description.Text="";description.ForeColor=LightUi.Text;}};description.LostFocus+=delegate{if(description.Text.Trim()==""){description.Text="添加备注";description.ForeColor=Color.FromArgb(150,165,185);}};
        bool recurringCalDav=!isNew&&originalCalDav&&B(original,"recurring");
        Panel footer=RoundedPanel(18,730,604,48,Color.FromArgb(248,252,255),Color.FromArgb(224,233,244),14);
        Label hint=LightUi.Label(recurringCalDav?"保存会改写整个 CalDAV 周期日程。":hasCalDav?"CalDAV 已配置，可同步到远程日历。":"未填写 CalDAV 凭据时只创建本地日历。",128,17,270);footer.Controls.Add(hint);
        Button delete=LightUi.DangerButton("删除日程",18,5,96,DialogResult.None);delete.Visible=!isNew;Button cancel=LightUi.Button("取消",416,5,74,DialogResult.Cancel);Button save=LightUi.PrimaryButton("保存",500,5,74,DialogResult.None);footer.Controls.AddRange(new Control[]{delete,cancel,save});f.Controls.Add(footer);f.CancelButton=cancel;
        delete.BringToFront();cancel.BringToFront();save.BringToFront();
        bool deleted=false;
        bool saved=false;
        Func<bool> saveDraft=delegate{
            string cleanTitle=title.Text.Trim();if(cleanTitle==""){LightUi.Error("标题不能为空");return false;}
            DateTime day=selectedDate.Date;DateTimeOffset start=allDaySelected?new DateTimeOffset(day,TimeZoneInfo.Local.GetUtcOffset(day)):new DateTimeOffset(day.Year,day.Month,day.Day,selectedStart.Hours,selectedStart.Minutes,0,TimeZoneInfo.Local.GetUtcOffset(day));
            DateTimeOffset end=allDaySelected?start.AddDays(1):new DateTimeOffset(day.Year,day.Month,day.Day,selectedEnd.Hours,selectedEnd.Minutes,0,TimeZoneInfo.Local.GetUtcOffset(day));if(end<=start){LightUi.Error("结束时间不能早于开始时间");return false;}
            if(recurringCalDav){DialogResult confirm=MessageBox.Show("这会修改整个周期日程，所有后续重复项都会一起更新。确定继续吗？","改写周期日程",MessageBoxButtons.YesNo,MessageBoxIcon.Warning);if(confirm!=DialogResult.Yes)return false;}
            if(originalCustomAlarmCount>customAlarms.Count){DialogResult confirmCustom=MessageBox.Show("你删除了 CalDAV 额外提醒。保存后这些提醒会从远端日程删除，之后本界面也不支持重新创建这种格式的提醒。确定继续吗？","删除 CalDAV 额外提醒",MessageBoxButtons.YesNo,MessageBoxIcon.Warning);if(confirmCustom!=DialogResult.Yes)return false;}
            string locationText=location.Text.Trim()=="添加地点"?"":location.Text.Trim(),urlText=url.Text.Trim()=="添加会议链接、网页或本地路径"?"":url.Text.Trim(),descriptionText=description.Text.Trim()=="添加备注"?"":description.Text.Trim();
            List<int> editableReminders=reminderMinutes.Concat(customStartReminders.Where(x=>!disabledCustomStartReminders.Contains(x))).Distinct().OrderBy(x=>x).ToList();
            string selected=selectedSource=="caldav"?"caldav":"local";Dictionary<string,object> draft=DraftEvent(original,selected,cleanTitle,start,end,allDaySelected,locationText,urlText,descriptionText,editableReminders,customAlarms);
            try{
                if(selected=="caldav"){
                    if(recurringCalDav)SaveCalDavSeriesEvent(draft,cache);else SaveCalDavEvent(draft,cache);
                    if(!isNew&&!originalCalDav)DeleteLocalEvent(original,state);
                }else{
                    SaveLocalEvent(draft,state);cache["status"]="已保存到本地日历";
                }
                return true;
            }catch(Exception ex){
                if(selected=="caldav"&&isNew){
                    Dictionary<string,object> localDraft=DraftEvent(original,"local",cleanTitle,start,end,allDaySelected,locationText,urlText,descriptionText,editableReminders,customAlarms);
                    SaveLocalEvent(localDraft,state);
                    selectedSource="local";caldavSource.Enabled=false;hint.Text="CalDAV 连接失败，已保存到本地日历。";paintSource();
                    cache["status"]="CalDAV 保存失败，已改为本地日历："+ex.Message;
                    LightUi.Error("CalDAV 连接超时或保存失败，内容已先保存为本地日程。之后可以在日程管理中编辑它并切换到 CalDAV。");
                    return true;
                }
                LightUi.Error(ex.Message);return false;
            }
        };
        delete.Click+=delegate{string mode="series";if(B(original,"recurring")){DialogResult choice=MessageBox.Show("这是周期日程。选择“是”删除整组，选择“否”只在本机隐藏本次。","删除周期日程",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Warning);if(choice==DialogResult.Cancel)return;mode=choice==DialogResult.Yes?"series":"once";}else if(!LightUi.Confirm("确定删除这个日程吗？","删除日程"))return;try{if(originalCalDav)DeleteCalDavEvent(original,cache,state,mode);else DeleteLocalEvent(original,state);deleted=true;f.DialogResult=DialogResult.OK;f.Close();}catch(Exception ex){LightUi.Error(ex.Message);}};
        save.Click+=delegate{if(saveDraft()){saved=true;f.DialogResult=DialogResult.OK;f.Close();}};
        if(f.ShowDialog()!=DialogResult.OK)return false;if(deleted||saved)return true;return false;
    }

    private static Dictionary<string,object> ReadCredentials()
    {
        if (!File.Exists(SecretPath)) return new Dictionary<string,object>();
        try { return JsonUtil.ReadDpapiJson(SecretPath); }
        catch { return new Dictionary<string,object>(); }
    }

    private static void SaveCredentials(TextBox server, TextBox username, TextBox password, Dictionary<string,object> cache)
    {
        string s = server.Text.Trim(), u = username.Text.Trim(), p = password.Text;
        if (s == "") throw new Exception("CalDAV 地址不能为空");
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
        if (u == "" || p == "") throw new Exception("账号和密码不能为空");
        s = s.TrimEnd('/');
        JsonUtil.WriteDpapiJson(SecretPath, new Dictionary<string,object>{{"Server",s},{"Username",u},{"Password",p}});
        cache["calendar_url"] = "";
        cache["events"] = new List<object>();
        cache["fetched_at"] = "";
        cache["status"] = "CalDAV 凭据已保存";
        Save(CachePath, cache);
    }

    private static Dictionary<string,object> CredentialsFromFields(TextBox server, TextBox username, TextBox password)
    {
        string s = server.Text.Trim(), u = username.Text.Trim(), p = password.Text;
        if (s == "") throw new Exception("CalDAV 地址不能为空");
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
        if (u == "" || p == "") throw new Exception("账号和密码不能为空");
        return new Dictionary<string,object>{{"Server",s},{"Username",u},{"Password",p}};
    }

    private static string TestCredentials(TextBox server, TextBox username, TextBox password)
    {
        CalendarInfo calendar = Discover(CredentialsFromFields(server, username, password));
        return "连接成功：" + (calendar.Name == "" ? calendar.Uri : calendar.Name);
    }

    private static void ClearCredentials(Dictionary<string,object> cache)
    {
        if (File.Exists(SecretPath)) File.Delete(SecretPath);
        cache["events"] = new List<object>();
        cache["calendar_url"] = "";
        cache["fetched_at"] = "";
        cache["status"] = "CalDAV 未连接";
        Save(CachePath, cache);
    }

    private static void FillRules(ListBox list, Dictionary<string,object> state)
    {
        list.Items.Clear();
        foreach (Dictionary<string,object> rule in Rules(state)) list.Items.Add(new KeyValuePair<Dictionary<string,object>,string>(rule, S(rule, "title") == "" ? "周期日程" : S(rule, "title")));
        if (list.Items.Count == 0) list.Items.Add(new KeyValuePair<Dictionary<string,object>,string>(null, "还没有周期自动转入规则"));
    }

    private static void StyleRuleList(ListBox list)
    {
        list.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.IntegralHeight = false;
        list.ItemHeight = Math.Max(32, list.Font.Height + 14);
        list.DrawItem += delegate(object sender, DrawItemEventArgs e) {
            if (e.Index < 0) return;
            ListBox source = (ListBox)sender;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color back = selected ? Color.FromArgb(11, 128, 214) : source.BackColor;
            Color fore = selected ? Color.White : source.ForeColor;
            using (SolidBrush background = new SolidBrush(back)) e.Graphics.FillRectangle(background, e.Bounds);
            string text = source.GetItemText(source.Items[e.Index]);
            Rectangle textBounds = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, source.Font, textBounds, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
    }

    private static void StyleTab(TabControl tabs)
    {
        tabs.Appearance = TabAppearance.FlatButtons;
        tabs.ItemSize = new Size(118, 34);
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
    }

    private static void DrawRound(Graphics graphics, Pen pen, float x, float y, float width, float height, float radius)
    {
        using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            float d = radius * 2F;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            graphics.DrawPath(pen, path);
        }
    }

    private static Panel IconPanel(string kind, int x, int y, int size, Color color)
    {
        Panel icon = new Panel { Left = x, Top = y, Width = size, Height = size, BackColor = Color.Transparent, Tag = kind };
        icon.Paint += delegate(object sender, PaintEventArgs e) {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, Math.Max(2F, size / 15F)))
            using (SolidBrush brush = new SolidBrush(color))
            {
                float w = size, h = size;
                string currentKind = Convert.ToString(((Control)sender).Tag, CultureInfo.InvariantCulture);
                if (currentKind == "calendar")
                {
                    DrawRound(e.Graphics, pen, 3, 5, w - 6, h - 8, 4);
                    e.Graphics.DrawLine(pen, 6, 14, w - 6, 14);
                    e.Graphics.FillRectangle(brush, 9, 1, 4, 10);
                    e.Graphics.FillRectangle(brush, w - 13, 1, 4, 10);
                    float cell = Math.Max(3F, size / 8F);
                    e.Graphics.FillRectangle(brush, w * 0.25F, h * 0.53F, cell, cell);
                    e.Graphics.FillRectangle(brush, w * 0.47F, h * 0.53F, cell, cell);
                    e.Graphics.FillRectangle(brush, w * 0.69F, h * 0.53F, cell, cell);
                    e.Graphics.FillRectangle(brush, w * 0.25F, h * 0.72F, cell, cell);
                    e.Graphics.FillRectangle(brush, w * 0.47F, h * 0.72F, cell, cell);
                    e.Graphics.FillRectangle(brush, w * 0.69F, h * 0.72F, cell, cell);
                }
                else if (currentKind == "globe")
                {
                    e.Graphics.DrawEllipse(pen, 4, 4, w - 8, h - 8);
                    e.Graphics.DrawLine(pen, w / 2, 5, w / 2, h - 5);
                    e.Graphics.DrawArc(pen, 11, 4, w - 22, h - 8, 90, 180);
                    e.Graphics.DrawArc(pen, 11, 4, w - 22, h - 8, -90, 180);
                    e.Graphics.DrawLine(pen, 6, h / 2, w - 6, h / 2);
                }
                else if (currentKind == "user")
                {
                    e.Graphics.DrawEllipse(pen, w / 2 - 7, 5, 14, 14);
                    e.Graphics.DrawArc(pen, 7, 21, w - 14, h - 18, 200, 140);
                    e.Graphics.DrawLine(pen, 11, h - 6, w - 11, h - 6);
                }
                else if (currentKind == "lock")
                {
                    e.Graphics.DrawArc(pen, w / 2 - 10, 5, 20, 20, 180, 180);
                    DrawRound(e.Graphics, pen, 8, 18, w - 16, h - 21, 4);
                    e.Graphics.FillEllipse(brush, w / 2 - 2, 27, 4, 4);
                }
                else if (currentKind == "shield")
                {
                    using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.StartFigure();
                        path.AddBezier(w / 2, 4, w / 2 + 7, 7, w - 8, 8, w - 7, 8);
                        path.AddBezier(w - 7, 8, w - 7, h - 8, w / 2 + 5, h - 4, w / 2, h - 3);
                        path.AddBezier(w / 2, h - 3, w / 2 - 5, h - 4, 7, h - 8, 7, 8);
                        path.AddBezier(7, 8, 8, 8, w / 2 - 7, 7, w / 2, 4);
                        path.CloseFigure();
                        e.Graphics.DrawPath(pen, path);
                    }
                    e.Graphics.DrawLine(pen, w / 2 - 6, h / 2, w / 2 - 1, h / 2 + 5);
                    e.Graphics.DrawLine(pen, w / 2 - 1, h / 2 + 5, w / 2 + 8, h / 2 - 5);
                }
                else if (currentKind == "eye" || currentKind == "eye-off")
                {
                    using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.StartFigure();
                        path.AddBezier(4, h / 2, w / 4, 6, w * 3 / 4, 6, w - 4, h / 2);
                        path.AddBezier(w - 4, h / 2, w * 3 / 4, h - 6, w / 4, h - 6, 4, h / 2);
                        path.CloseFigure();
                        e.Graphics.DrawPath(pen, path);
                    }
                    e.Graphics.DrawEllipse(pen, w / 2 - 5, h / 2 - 5, 10, 10);
                    using (SolidBrush pupil = new SolidBrush(color)) e.Graphics.FillEllipse(pupil, w / 2 - 2, h / 2 - 2, 4, 4);
                    if (currentKind == "eye-off") e.Graphics.DrawLine(pen, 5, h - 5, w - 5, 5);
                }
                else if (currentKind == "link")
                {
                    e.Graphics.DrawArc(pen, 7, 10, 18, 18, 120, 230);
                    e.Graphics.DrawArc(pen, w - 25, h - 28, 18, 18, -60, 230);
                    e.Graphics.DrawLine(pen, 18, 25, w - 18, 13);
                }
                else if (currentKind == "trash")
                {
                    e.Graphics.DrawLine(pen, 10, 12, w - 10, 12);
                    e.Graphics.DrawRectangle(pen, 13, 15, w - 26, h - 21);
                    e.Graphics.DrawLine(pen, w / 2 - 7, 8, w / 2 + 7, 8);
                }
                else if (currentKind == "save")
                {
                    DrawRound(e.Graphics, pen, 8, 6, w - 16, h - 12, 3);
                    e.Graphics.DrawRectangle(pen, 14, 7, w - 28, 10);
                    e.Graphics.DrawRectangle(pen, 15, h - 17, w - 30, 10);
                }
            }
        };
        return icon;
    }

    private static void AddHeaderSvgIcon(Panel host, string fileName, string fallbackKind, int left, int top, int size)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", fileName);
        if (!File.Exists(path))
        {
            host.Controls.Add(IconPanel(fallbackKind, left, top, size, LightUi.Accent));
            return;
        }

        WebBrowser browser = new WebBrowser { Left = left, Top = top, Width = size, Height = size, ScrollBarsEnabled = false, IsWebBrowserContextMenuEnabled = false, AllowWebBrowserDrop = false, WebBrowserShortcutsEnabled = false };
        browser.DocumentText = "<html><head><meta http-equiv='X-UA-Compatible' content='IE=edge'></head><body style='margin:0;overflow:hidden;background:#eef5fc;'><img src='file:///" + path.Replace("\\", "/") + "' style='width:100%;height:100%;display:block;'/></body></html>";
        host.Controls.Add(browser);
    }

    private static Panel RoundedPanel(int x, int y, int width, int height, Color fill, Color border, int radius)
    {
        Panel panel = new Panel { Left = x, Top = y, Width = width, Height = height, BackColor = fill };
        LightUi.Round(panel, radius);
        panel.Paint += delegate(object sender, PaintEventArgs e) {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(border, 1F))
                DrawRound(e.Graphics, pen, 0, 0, panel.Width - 1, panel.Height - 1, radius);
        };
        return panel;
    }

    private sealed class TimeSlider : Control
    {
        public int Value;
        public event EventHandler ValueChanged;
        private bool dragging;
        public TimeSlider(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint,true);Height=34;Cursor=Cursors.Hand;}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;int pad=10,trackY=Height/2,trackW=Math.Max(1,Width-pad*2),x=pad+(int)Math.Round(trackW*(Value/95.0));Color accent=Enabled?LightUi.Accent:Color.FromArgb(165,181,202),track=Enabled?Color.FromArgb(213,228,241):Color.FromArgb(226,235,243);using(Pen bg=new Pen(track,6)){bg.StartCap=bg.EndCap=System.Drawing.Drawing2D.LineCap.Round;e.Graphics.DrawLine(bg,pad,trackY,Width-pad,trackY);}using(Pen fg=new Pen(accent,6)){fg.StartCap=fg.EndCap=System.Drawing.Drawing2D.LineCap.Round;e.Graphics.DrawLine(fg,pad,trackY,x,trackY);}using(SolidBrush shadow=new SolidBrush(Enabled?Color.FromArgb(50,47,132,235):Color.FromArgb(35,120,135,155)))e.Graphics.FillEllipse(shadow,x-9,trackY-8,18,18);using(SolidBrush knob=new SolidBrush(Color.White))e.Graphics.FillEllipse(knob,x-8,trackY-9,16,16);using(Pen pen=new Pen(accent,2))e.Graphics.DrawEllipse(pen,x-8,trackY-9,16,16);}
        private void SetFromX(int mouseX){int pad=10,trackW=Math.Max(1,Width-pad*2);int next=(int)Math.Round(Math.Max(0,Math.Min(trackW,mouseX-pad))/(trackW/95.0));if(next==Value)return;Value=next;Invalidate();if(ValueChanged!=null)ValueChanged(this,EventArgs.Empty);}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);dragging=true;Capture=true;SetFromX(e.X);}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging)SetFromX(e.X);}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;}
    }

    private static TextBox AddCredentialField(Panel parent, string icon, string label, int y, string text, bool password, out Panel reveal)
    {
        parent.Controls.Add(IconPanel(icon, 34, y + 13, 28, LightUi.Accent));
        parent.Controls.Add(new Label { Text = label, Left = 84, Top = y + 13, Width = 180, Height = 24, ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F) });
        Panel box = RoundedPanel(84, y + 44, 560, 44, Color.FromArgb(252, 254, 255), Color.FromArgb(220, 230, 241), 10);
        TextBox input = new TextBox { Left = 14, Top = 10, Width = password ? 480 : 532, Height = 24, AutoSize = false, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(252, 254, 255), ForeColor = Color.FromArgb(5, 16, 34), Font = new System.Drawing.Font("Microsoft YaHei UI", 10F), Text = text ?? "" };
        input.UseSystemPasswordChar = password;
        box.Controls.Add(input);
        parent.Controls.Add(box);
        reveal = null;
        if (password)
        {
            reveal = IconPanel("eye-off", 516, 8, 28, Color.FromArgb(94, 111, 150));
            reveal.Cursor = Cursors.Hand;
            box.Controls.Add(reveal);
        }
        return input;
    }

    private static void Settings(Dictionary<string,object> state, Dictionary<string,object> cache, ref bool refreshTodo)
    {
        Dictionary<string,object> credentials = ReadCredentials();
        bool syncChangedTodo = false;
        Form f = LightUi.Form("日程设置", 720, 560);
        Panel headerIcon = RoundedPanel(38, 34, 48, 48, Color.FromArgb(238,245,252), Color.FromArgb(205,224,241), 14);
        AddHeaderSvgIcon(headerIcon, "settings.svg", "shield", 8, 8, 32);
        Label title = new Label { Text = "日程设置", Left = 112, Top = 34, Width = 240, Height = 36, BackColor = Color.Transparent, ForeColor = LightUi.Text, Font = new System.Drawing.Font("Microsoft YaHei UI", 18F, System.Drawing.FontStyle.Bold) };
        Label subtitle = new Label { Text = "管理 CalDAV 同步账号和周期自动转入规则。", Left = 42, Top = 92, Width = 540, Height = 26, BackColor = Color.Transparent, ForeColor = Color.FromArgb(76, 94, 132), Font = new System.Drawing.Font("Microsoft YaHei UI", 10F) };
        Button closeTop = LightUi.Button("×", 648, 38, 42, DialogResult.Cancel);
        closeTop.Height = 42;
        closeTop.Font = new System.Drawing.Font("Microsoft YaHei UI", 18F);
        closeTop.ForeColor = Color.FromArgb(37, 52, 82);
        f.Controls.AddRange(new Control[] { headerIcon, title, subtitle, closeTop });
        title.BringToFront();
        closeTop.BringToFront();

        Panel tabRail = RoundedPanel(42, 138, 272, 44, Color.FromArgb(235, 245, 253), Color.FromArgb(235, 245, 253), 11);
        Button tabAccount = LightUi.Button("同步账号", 0, 0, 136, DialogResult.None);
        Button tabRules = LightUi.Button("自动转入", 136, 0, 136, DialogResult.None);
        tabAccount.Height = tabRules.Height = 44;
        tabAccount.Font = tabRules.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold);
        tabRail.Controls.AddRange(new Control[] { tabAccount, tabRules });
        f.Controls.Add(tabRail);

        Panel accountPage = RoundedPanel(32, 196, 656, 286, Color.FromArgb(248, 252, 255), Color.FromArgb(224, 233, 244), 18);
        Panel rulePage = RoundedPanel(32, 196, 656, 286, Color.FromArgb(248, 252, 255), Color.FromArgb(224, 233, 244), 18);
        rulePage.Visible = false;
        f.Controls.AddRange(new Control[] { accountPage, rulePage });

        Action<bool> showAccount = null;
        tabAccount.Click += delegate { showAccount(true); };
        tabRules.Click += delegate { showAccount(false); };

        Panel reveal;
        TextBox server = AddCredentialField(accountPage, "globe", "CalDAV 地址", 10, S(credentials, "Server"), false, out reveal);
        TextBox username = AddCredentialField(accountPage, "user", "账号", 92, S(credentials, "Username"), false, out reveal);
        TextBox password = AddCredentialField(accountPage, "lock", "密码", 174, S(credentials, "Password"), true, out reveal);
        if (reveal != null) reveal.Click += delegate { password.UseSystemPasswordChar = !password.UseSystemPasswordChar; reveal.Tag = password.UseSystemPasswordChar ? "eye-off" : "eye"; reveal.Invalidate(); };

        ListBox list = new ListBox { Left = 34, Top = 28, Width = 588, Height = 176, BackColor = Color.FromArgb(252, 254, 255), ForeColor = LightUi.Text, DisplayMember = "Value", BorderStyle = BorderStyle.None };
        StyleRuleList(list);
        LightUi.Round(list, 12);
        FillRules(list, state);
        rulePage.Controls.Add(list);
        rulePage.Controls.Add(new Label { Text = "停止规则只影响未来周期，已经生成的待办保持不变。", Left = 34, Top = 232, Width = 420, Height = 24, ForeColor = Color.FromArgb(76, 94, 132), BackColor = Color.Transparent, Font = new System.Drawing.Font("Microsoft YaHei UI", 9F) });
        Button stop = LightUi.DangerButton("清除规则", 506, 224, 116, DialogResult.None);
        stop.Height = 40;
        stop.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
        rulePage.Controls.Add(stop);

        Label saveStatus = new Label { Text = S(cache, "status"), Left = 64, Top = 502, Width = 280, Height = 28, BackColor = Color.Transparent, ForeColor = S(cache, "status").Contains("成功") || S(cache, "status").Contains("已同步") ? Color.FromArgb(63, 178, 119) : Color.FromArgb(76, 94, 132), Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold) };
        Button clearAccount = LightUi.DangerButton("清除设置", 364, 494, 98, DialogResult.None);
        Button testAccount = LightUi.Button("测试连接", 476, 494, 98, DialogResult.None);
        Button saveAccount = LightUi.PrimaryButton("保存凭据", 588, 494, 100, DialogResult.None);
        clearAccount.Height = testAccount.Height = saveAccount.Height = 38;
        clearAccount.Font = testAccount.Font = saveAccount.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
        f.Controls.AddRange(new Control[] { saveStatus, clearAccount, testAccount, saveAccount });
        showAccount = delegate(bool account) {
            accountPage.Visible = account; accountPage.Enabled = account;
            rulePage.Visible = !account; rulePage.Enabled = !account;
            saveStatus.Visible = clearAccount.Visible = testAccount.Visible = saveAccount.Visible = account;
            clearAccount.Enabled = testAccount.Enabled = saveAccount.Enabled = account;
            tabAccount.BackColor = account ? Color.FromArgb(248, 252, 255) : Color.FromArgb(235, 245, 253);
            tabAccount.ForeColor = account ? LightUi.Accent : LightUi.Text;
            tabRules.BackColor = account ? Color.FromArgb(235, 245, 253) : Color.FromArgb(248, 252, 255);
            tabRules.ForeColor = account ? LightUi.Text : LightUi.Accent;
            if(account)accountPage.BringToFront();else rulePage.BringToFront();
            tabRail.BringToFront(); closeTop.BringToFront();
        };
        showAccount(true);

        testAccount.Click += delegate {
            try { saveStatus.Text = "正在测试…"; saveStatus.ForeColor = Color.FromArgb(76, 94, 132); saveStatus.Refresh(); saveStatus.Text = TestCredentials(server, username, password); saveStatus.ForeColor = Color.FromArgb(63, 178, 119); }
            catch (Exception ex) { LightUi.Error("连接失败：" + ex.Message); saveStatus.Text = "连接失败"; saveStatus.ForeColor = LightUi.Danger; }
        };
        saveAccount.Click += delegate {
            try { SaveCredentials(server, username, password, cache); saveStatus.Text = "已保存"; saveStatus.ForeColor = Color.FromArgb(63, 178, 119); }
            catch (Exception ex) { LightUi.Error(ex.Message); }
        };
        clearAccount.Click += delegate {
            try { ClearCredentials(cache); server.Text = ""; username.Text = ""; password.Text = ""; saveStatus.Text = "CalDAV 未连接"; saveStatus.ForeColor = Color.FromArgb(76, 94, 132); }
            catch (Exception ex) { LightUi.Error(ex.Message); }
        };
        stop.Click += delegate {
            if (list.SelectedItem == null) return;
            Dictionary<string,object> selected = ((KeyValuePair<Dictionary<string,object>,string>)list.SelectedItem).Key;
            if (selected == null) return;
            Rules(state).Remove(selected);
            Save(StatePath, state);
            cache["status"] = "已停止该系列的未来自动转入";
            Save(CachePath, cache);
            FillRules(list, state);
            saveStatus.Text = "已停止该系列的未来自动转入";
            saveStatus.ForeColor = Color.FromArgb(76, 94, 132);
        };
        closeTop.Click += delegate { f.Close(); };
        f.CancelButton = closeTop;
        f.ShowDialog();
        if (syncChangedTodo) refreshTodo = true;
    }

    private static bool ManageEvents(Dictionary<string,object> state, Dictionary<string,object> cache, ref bool refreshTodo)
    {
        bool managerRefreshTodo = false, changed = false;
        bool showLocal = true, showCalDav = true;
        DateTime selectedDate = DateTime.Now.Date;
        Action reload = null, renderCalendar = null;
        Form f = LightUi.Form("日程管理", 1180, 760);
        Panel headerIcon = RoundedPanel(30, 26, 42, 42, Color.FromArgb(238,245,252), Color.FromArgb(205,224,241), 12);
        AddHeaderSvgIcon(headerIcon, "calendar.svg", "calendar", 7, 7, 28);
        Label title = new Label { Text = "日程管理", Left = 90, Top = 24, Width = 220, Height = 38, BackColor = Color.Transparent, ForeColor = LightUi.Text, Font = new System.Drawing.Font("Microsoft YaHei UI", 18F, System.Drawing.FontStyle.Bold) };
        Label subtitle = new Label { Text = "查看、编辑和同步你的本地日历与 CalDAV 日历。", Left = 92, Top = 70, Width = 520, Height = 22, BackColor = Color.Transparent, ForeColor = LightUi.Muted, Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F) };
        Panel searchBox = RoundedPanel(760, 30, 300, 42, Color.FromArgb(252,254,255), Color.FromArgb(220,230,241), 13);
        Label searchIcon = new Label { Text = "\xE721", Left = 14, Top = 10, Width = 22, Height = 22, BackColor = Color.Transparent, ForeColor = LightUi.Muted, Font = new System.Drawing.Font("Segoe Fluent Icons", 10F), TextAlign = ContentAlignment.MiddleCenter };
        TextBox search = new TextBox { Left = 44, Top = 10, Width = 238, Height = 22, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(252,254,255), ForeColor = LightUi.Text, Font = new System.Drawing.Font("Microsoft YaHei UI", 10F) };
        searchBox.Controls.Add(searchIcon); searchBox.Controls.Add(search);
        Button close = LightUi.Button("×", 1100, 30, 42, DialogResult.Cancel); close.Height = 42; close.Font = new System.Drawing.Font("Segoe UI Symbol", 14F, System.Drawing.FontStyle.Bold); close.TextAlign = ContentAlignment.MiddleCenter; close.Padding = new Padding(0, 0, 0, 2);
        f.Controls.AddRange(new Control[] { headerIcon, title, subtitle, searchBox, close });

        Panel left = RoundedPanel(28, 112, 330, 540, Color.FromArgb(248,252,255), Color.FromArgb(224,233,244), 18);
        Button prevMonth = LightUi.Button("‹", 22, 22, 38, DialogResult.None);
        Button nextMonth = LightUi.Button("›", 270, 22, 38, DialogResult.None);
        Label monthTitle = new Label { Left = 70, Top = 29, Width = 190, Height = 24, BackColor = Color.Transparent, ForeColor = LightUi.Text, TextAlign = ContentAlignment.MiddleCenter, Font = new System.Drawing.Font("Microsoft YaHei UI", 12F, System.Drawing.FontStyle.Bold) };
        Button todayButton = LightUi.Button("今天", 238, 66, 70, DialogResult.None); todayButton.Height = 32;
        Panel calendarGrid = new Panel { Left = 22, Top = 102, Width = 286, Height = 250, BackColor = Color.Transparent };
        left.Controls.AddRange(new Control[] { prevMonth, nextMonth, monthTitle, todayButton, calendarGrid });
        Panel filters = RoundedPanel(20, 370, 288, 88, Color.FromArgb(252,254,255), Color.FromArgb(224,233,244), 14);
        Label filterTitle = new Label { Text = "日历筛选", Left = 18, Top = 14, Width = 160, Height = 24, BackColor = Color.Transparent, ForeColor = LightUi.Text, Font = new System.Drawing.Font("Microsoft YaHei UI", 10.5F, System.Drawing.FontStyle.Bold) };
        Button localFilter = LightUi.Button("✓  本地日历", 18, 48, 120, DialogResult.None);
        Button caldavFilter = LightUi.Button("✓  CalDAV 日历", 150, 48, 120, DialogResult.None);
        localFilter.Height = caldavFilter.Height = 28;
        localFilter.TextAlign = caldavFilter.TextAlign = ContentAlignment.MiddleCenter;
        filters.Controls.AddRange(new Control[] { filterTitle, localFilter, caldavFilter });
        left.Controls.Add(filters); f.Controls.Add(left);

        Panel main = RoundedPanel(382, 112, 760, 540, Color.FromArgb(248,252,255), Color.FromArgb(224,233,244), 18);
        Label dayHeader = new Label { Left = 22, Top = 24, Width = 430, Height = 30, BackColor = Color.Transparent, ForeColor = LightUi.Text, Font = new System.Drawing.Font("Microsoft YaHei UI", 12F, System.Drawing.FontStyle.Bold) };
        int timeMode = 0;
        Panel timeTabs = RoundedPanel(498, 20, 224, 36, Color.FromArgb(235,245,253), Color.FromArgb(218,232,246), 12);
        Button todayTab = LightUi.Button("今天", 2, 2, 62, DialogResult.None);
        Button weekTab = LightUi.Button("未来7天", 66, 2, 76, DialogResult.None);
        Button allTimeTab = LightUi.Button("全部时间", 144, 2, 78, DialogResult.None);
        foreach(Button tab in new[]{todayTab,weekTab,allTimeTab}){tab.Height=32;tab.Font=new System.Drawing.Font("Microsoft YaHei UI",8.5F,System.Drawing.FontStyle.Bold);tab.TextAlign=ContentAlignment.MiddleCenter;tab.FlatAppearance.BorderSize=0;tab.UseVisualStyleBackColor=false;}
        timeTabs.Controls.AddRange(new Control[]{todayTab,weekTab,allTimeTab});
        FlowLayoutPanel list = new FlowLayoutPanel { Left = 20, Top = 72, Width = 720, Height = 446, BackColor = Color.Transparent, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        main.Controls.AddRange(new Control[] { dayHeader, timeTabs, list }); f.Controls.Add(main);

        Panel footer = RoundedPanel(28, 674, 1114, 58, Color.FromArgb(248, 252, 255), Color.FromArgb(224, 233, 244), 16);
        Label footerSummary = LightUi.Label("当前日期：" + selectedDate.ToString("yyyy/M/d") + " · 0 项日程", 22, 18, 460); footer.Controls.Add(footerSummary);
        Button settings = LightUi.Button("设置", 758, 10, 92, DialogResult.None);
        Button sync = LightUi.Button("刷新同步", 858, 10, 112, DialogResult.None);
        Button add = LightUi.PrimaryButton("新建日程", 978, 10, 112, DialogResult.None);
        settings.Font = sync.Font = add.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
        settings.TextAlign = sync.TextAlign = add.TextAlign = ContentAlignment.MiddleCenter;
        Action<Button> paintFooterButton = delegate(Button b) {
            b.UseVisualStyleBackColor = false;
            b.BackColor = Color.FromArgb(235,245,253);
            b.ForeColor = LightUi.Accent;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(221,237,255);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(205,224,241);
            b.FlatAppearance.BorderColor = Color.FromArgb(205,224,241);
            b.MouseEnter += delegate { if(b.Enabled)b.BackColor=Color.FromArgb(221,237,255); };
            b.MouseLeave += delegate { b.BackColor=Color.FromArgb(235,245,253); };
        };
        paintFooterButton(settings); paintFooterButton(sync);
        footer.Controls.AddRange(new Control[] { settings, sync, add }); f.Controls.Add(footer);
        Action paintTimeTabs = delegate {
            Button[] tabs = new[]{todayTab,weekTab,allTimeTab};
            for(int i=0;i<tabs.Length;i++){
                bool active = i == timeMode;
                tabs[i].BackColor = active ? Color.FromArgb(252,254,255) : Color.FromArgb(235,245,253);
                tabs[i].ForeColor = active ? LightUi.Accent : LightUi.Muted;
                tabs[i].UseVisualStyleBackColor = false;
                tabs[i].FlatAppearance.MouseOverBackColor = active ? Color.FromArgb(252,254,255) : Color.FromArgb(226,239,250);
                tabs[i].FlatAppearance.MouseDownBackColor = active ? Color.FromArgb(252,254,255) : Color.FromArgb(214,231,247);
            }
        };
        foreach(Button tab in new[]{todayTab,weekTab,allTimeTab}){tab.MouseEnter+=delegate{paintTimeTabs();};tab.MouseLeave+=delegate{paintTimeTabs();};}

        Func<Dictionary<string,object>,bool> isVisibleSource = delegate(Dictionary<string,object> e) { return (showLocal && S(e,"source")=="local") || (showCalDav && S(e,"source")=="caldav"); };
        Func<IEnumerable<Dictionary<string,object>>> visibleEvents = delegate {
            HashSet<string> hidden = new HashSet<string>(Conversions(state).Where(c => JsonUtil.Bool(c, "hide_event", true)).Select(c => S(c, "occurrence_key")));
            foreach(Dictionary<string,object> h in HiddenEvents(state)) hidden.Add(S(h,"occurrence_key"));
            IEnumerable<Dictionary<string,object>> query=AllEvents(cache,state).Where(e=>RuntimeUtil.Date(e,"start_at").HasValue&&RuntimeUtil.Date(e,"end_at").HasValue&&!hidden.Contains(S(e,"occurrence_key"))&&isVisibleSource(e));
            if(search.Text.Trim()!=""){string q=search.Text.Trim();query=query.Where(e=>CleanTitle(S(e,"title")).IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0||S(e,"location").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0||S(e,"description").IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0);}
            return query;
        };
        Func<DateTime,List<string>> sourcesOnDate = delegate(DateTime date) {
            DateTimeOffset ds=new DateTimeOffset(date,TimeZoneInfo.Local.GetUtcOffset(date)),de=ds.AddDays(1);
            return visibleEvents().Where(e=>RuntimeUtil.Date(e,"start_at").Value<de&&RuntimeUtil.Date(e,"end_at").Value>ds)
                .Select(e=>S(e,"source")).Distinct().OrderBy(s=>s=="caldav"?0:1).ToList();
        };
        Action<Button,bool> paintFilter = delegate(Button b,bool active) {
            Color back = active ? LightUi.AccentFill : Color.FromArgb(235,245,253);
            b.Tag = active;
            b.BackColor = back;
            b.ForeColor = active ? Color.White : LightUi.Text;
            b.FlatAppearance.MouseOverBackColor = back;
            b.FlatAppearance.MouseDownBackColor = active ? Color.FromArgb(38,118,222) : Color.FromArgb(218,236,251);
            b.Text=(active?"✓  ":"   ")+Regex.Replace(b.Text,@"^[✓ ]+\s*","");
        };
        Action<Button> keepFilterHover = delegate(Button b) {
            b.MouseEnter += delegate { paintFilter(b, b.Tag is bool && (bool)b.Tag); };
            b.MouseLeave += delegate { paintFilter(b, b.Tag is bool && (bool)b.Tag); };
        };
        keepFilterHover(localFilter); keepFilterHover(caldavFilter);
        Action updateFilters = delegate {
            int localCount=AllEvents(cache,state).Count(e=>S(e,"source")=="local"), caldavCount=AllEvents(cache,state).Count(e=>S(e,"source")=="caldav");
            localFilter.Text=(showLocal?"✓  ":"")+"本地  "+localCount;
            caldavFilter.Text=(showCalDav?"✓  ":"")+"CalDAV  "+caldavCount;
            paintFilter(localFilter,showLocal);paintFilter(caldavFilter,showCalDav);
        };
        renderCalendar = delegate {
            calendarGrid.Controls.Clear();
            monthTitle.Text=selectedDate.ToString("yyyy 年 M 月",CultureInfo.GetCultureInfo("zh-CN"));
            string[] names={"一","二","三","四","五","六","日"};
            for(int i=0;i<7;i++)calendarGrid.Controls.Add(new Label{Text=names[i],Left=i*40,Top=0,Width=34,Height=24,TextAlign=ContentAlignment.MiddleCenter,BackColor=Color.Transparent,ForeColor=LightUi.Muted,Font=new System.Drawing.Font("Microsoft YaHei UI",9F,System.Drawing.FontStyle.Bold)});
            DateTime first=new DateTime(selectedDate.Year,selectedDate.Month,1);
            int offset=((int)first.DayOfWeek+6)%7;
            DateTime cursor=first.AddDays(-offset);
            DateTime today=DateTime.Now.Date;
            for(int cell=0;cell<42;cell++){
                DateTime d=cursor.AddDays(cell);bool inMonth=d.Month==selectedDate.Month,selected=d.Date==selectedDate.Date,isToday=d.Date==today;List<string> daySources=sourcesOnDate(d);
                Color dayBack=selected?LightUi.AccentFill:(isToday?Color.FromArgb(232,244,255):Color.Transparent);
                Label day=new Label{Text=d.Day.ToString(CultureInfo.InvariantCulture),Left=(cell%7)*40+2,Top=28+(cell/7)*35,Width=30,Height=30,TextAlign=ContentAlignment.MiddleCenter,BackColor=dayBack,ForeColor=selected?Color.White:(isToday?LightUi.Accent:inMonth?LightUi.Text:Color.FromArgb(170,185,205)),Font=new System.Drawing.Font("Microsoft YaHei UI",10F,(selected||isToday)?System.Drawing.FontStyle.Bold:System.Drawing.FontStyle.Regular),Tag=d};
                LightUi.Round(day,15);day.Cursor=Cursors.Hand;day.Click+=delegate(object sender,EventArgs args){selectedDate=((DateTime)((Control)sender).Tag).Date;reload();};calendarGrid.Controls.Add(day);
                int dotCount=daySources.Count;int baseLeft=(cell%7)*40+17-(dotCount*8-2)/2, dotTop=day.Top+30;
                for(int dotIndex=0;dotIndex<dotCount;dotIndex++){string source=daySources[dotIndex];Color dotColor=source=="caldav"?(selected?Color.White:LightUi.Accent):Color.FromArgb(63,178,119);Panel dot=new Panel{Left=baseLeft+dotIndex*8,Top=dotTop,Width=6,Height=6,BackColor=dotColor};LightUi.Round(dot,3);calendarGrid.Controls.Add(dot);dot.BringToFront();}
            }
        };

        reload = delegate {
            list.Controls.Clear();
            DateTimeOffset now=DateTimeOffset.Now, dayStart=new DateTimeOffset(selectedDate,TimeZoneInfo.Local.GetUtcOffset(selectedDate)), dayEnd=dayStart.AddDays(1);
            IEnumerable<Dictionary<string,object>> query=visibleEvents();
            if(timeMode==0)query=query.Where(e=>RuntimeUtil.Date(e,"start_at").Value<dayEnd&&RuntimeUtil.Date(e,"end_at").Value>dayStart);
            else if(timeMode==1){DateTimeOffset weekEnd=dayStart.AddDays(7);query=query.Where(e=>RuntimeUtil.Date(e,"start_at").Value<weekEnd&&RuntimeUtil.Date(e,"end_at").Value>dayStart);}
            List<Dictionary<string,object>> rows=query.OrderBy(e=>RuntimeUtil.Date(e,"start_at")).ThenBy(e=>RuntimeUtil.Date(e,"end_at")).ThenBy(e=>CleanTitle(S(e,"title")),StringComparer.CurrentCulture).ToList();
            if(timeMode==2){dayHeader.Text="全部日程 · "+rows.Count+" 项日程";footerSummary.Text="全部时间 · "+rows.Count+" 项日程";}
            else if(timeMode==1){DateTime rangeEnd=selectedDate.AddDays(6);dayHeader.Text=selectedDate.ToString("yyyy年M月d日",CultureInfo.GetCultureInfo("zh-CN"))+" - "+rangeEnd.ToString("M月d日",CultureInfo.GetCultureInfo("zh-CN"))+" · "+rows.Count+" 项日程";footerSummary.Text="未来7天："+selectedDate.ToString("yyyy/M/d")+" - "+rangeEnd.ToString("yyyy/M/d")+" · "+rows.Count+" 项日程";}
            else{dayHeader.Text=selectedDate.ToString("yyyy年M月d日 dddd",CultureInfo.GetCultureInfo("zh-CN"))+" · "+rows.Count+" 项日程";footerSummary.Text="当前日期："+selectedDate.ToString("yyyy/M/d")+" · "+rows.Count+" 项日程";}
            updateFilters();paintTimeTabs();renderCalendar();
            foreach(Dictionary<string,object> e in rows)
            {
                bool caldav=S(e,"source")=="caldav";
                Color stripe=caldav?LightUi.Accent:Color.FromArgb(63,178,119);
                Panel row=RoundedPanel(0,0,694,86,Color.FromArgb(252,254,255),Color.FromArgb(224,233,244),12);
                row.Margin=new Padding(0,0,0,10);row.Tag=S(e,"id");row.Cursor=Cursors.Hand;
                Panel mark=new Panel{Left=16,Top=16,Width=4,Height=row.Height-32,BackColor=stripe};LightUi.Round(mark,2);row.Controls.Add(mark);
                DateTimeOffset es=RuntimeUtil.Date(e,"start_at").Value,ee=RuntimeUtil.Date(e,"end_at").Value;
                bool showEventDate=timeMode!=0;
                string timeText=B(e,"all_day")?"全天":es.ToString("HH:mm")+" - "+ee.ToString("HH:mm");
                if(showEventDate)timeText=es.ToString("M/d ddd",CultureInfo.GetCultureInfo("zh-CN"))+"\r\n"+timeText;
                Label time=new Label{Text=timeText,Left=36,Top=showEventDate?20:30,Width=128,Height=showEventDate?46:24,BackColor=Color.Transparent,ForeColor=LightUi.Text,Font=new System.Drawing.Font("Microsoft YaHei UI",showEventDate?9F:10F,System.Drawing.FontStyle.Bold)};
                Label name=new Label{Text=CleanTitle(S(e,"title")),Left=172,Top=16,Width=310,Height=24,BackColor=Color.Transparent,ForeColor=LightUi.Text,Font=new System.Drawing.Font("Microsoft YaHei UI",11F,System.Drawing.FontStyle.Bold)};
                bool noLocation=S(e,"location")=="";
                Label loc=new Label{Text=noLocation?"无地点":S(e,"location"),Left=172,Top=43,Width=310,Height=18,BackColor=Color.Transparent,ForeColor=noLocation?Color.FromArgb(165,181,202):LightUi.Muted,Font=new System.Drawing.Font("Microsoft YaHei UI",noLocation?8F:9F)};
                Label badge=new Label{Text=caldav?"CalDAV 日历":"本地日历",Left=172,Top=60,Width=118,Height=22,BackColor=caldav?Color.FromArgb(221,237,255):Color.FromArgb(218,246,231),ForeColor=caldav?LightUi.Accent:Color.FromArgb(35,145,89),TextAlign=ContentAlignment.MiddleCenter,Font=new System.Drawing.Font("Microsoft YaHei UI",9F)};
                LightUi.Round(badge,8);
                Button editRow=LightUi.Button("",610,24,42,DialogResult.None);editRow.Font=new System.Drawing.Font("Segoe Fluent Icons",10F);editRow.Tag=S(e,"id");
                row.Controls.AddRange(new Control[]{time,name,loc,badge,editRow});
                EventHandler editEvent=delegate(object sender,EventArgs args){Dictionary<string,object> ev=FindEvent(cache,state,Convert.ToString(((Control)editRow).Tag));if(ev!=null&&EditInteractive(ev,state,cache)){Save(StatePath,state);Save(CachePath,cache);changed=true;reload();}};
                row.DoubleClick+=editEvent;editRow.Click+=editEvent;
                list.Controls.Add(row);
            }
            if(rows.Count==0){Panel empty=RoundedPanel(0,0,694,86,Color.FromArgb(252,254,255),Color.FromArgb(224,233,244),14);empty.Controls.Add(new Label{Text="这一天没有日程安排",Left=230,Top=30,Width=240,Height=24,TextAlign=ContentAlignment.MiddleCenter,BackColor=Color.Transparent,ForeColor=LightUi.Muted,Font=new System.Drawing.Font("Microsoft YaHei UI",10F)});list.Controls.Add(empty);}
        };
        prevMonth.Click += delegate { selectedDate=selectedDate.AddMonths(-1); reload(); };
        nextMonth.Click += delegate { selectedDate=selectedDate.AddMonths(1); reload(); };
        todayButton.Click += delegate { selectedDate=DateTime.Now.Date; reload(); };
        localFilter.Click += delegate { showLocal=!showLocal;if(!showLocal&&!showCalDav)showCalDav=true;reload(); };
        caldavFilter.Click += delegate { showCalDav=!showCalDav;if(!showLocal&&!showCalDav)showLocal=true;reload(); };
        search.TextChanged += delegate { reload(); };
        todayTab.Click += delegate { timeMode=0;reload(); };
        weekTab.Click += delegate { timeMode=1;reload(); };
        allTimeTab.Click += delegate { timeMode=2;reload(); };
        add.Click += delegate { if(EditInteractive(null,state,cache)){Save(StatePath,state);Save(CachePath,cache);changed=true;reload();} };
        sync.Click += delegate {
            System.Diagnostics.Process syncProcess=StartBackgroundSync();
            if(syncProcess==null){sync.Text="刷新同步";return;}
            sync.Enabled=false;sync.Text="后台同步中";
            cache["status"]="正在后台同步";Save(CachePath,cache);Render(cache,state);reload();
            System.Windows.Forms.Timer syncTimer=new System.Windows.Forms.Timer{Interval=700};
            syncTimer.Tick+=delegate{
                if(!syncProcess.HasExited)return;
                syncTimer.Stop();syncTimer.Dispose();syncProcess.Dispose();
                cache=Load(CachePath,NewCache());state=Load(StatePath,NewState());Shape(cache,state);
                sync.Enabled=true;sync.Text="刷新同步";reload();
            };
            syncTimer.Start();
        };
        settings.Click += delegate { Settings(state,cache,ref managerRefreshTodo); Save(StatePath,state); Save(CachePath,cache); changed=true; reload(); };
        close.Click += delegate { f.Close(); }; f.CancelButton = close;
        reload();
        f.ShowDialog();
        if (managerRefreshTodo) refreshTodo = true;
        return changed;
    }

    private static void Manage(Dictionary<string,object> state, Dictionary<string,object> cache)
    {
        while (true)
        {
            Form f = LightUi.Form("周期自动转入规则", 620, 500);
            LightUi.Heading(f, "周期自动转入规则", "停止规则只影响未来周期，已经生成的待办保持不变。");
            ListBox list = new ListBox { Left = 24, Top = 96, Width = 572, Height = 292, BackColor = LightUi.Surface, ForeColor = LightUi.Text, DisplayMember = "Value", BorderStyle = BorderStyle.FixedSingle };
            StyleRuleList(list);
            LightUi.Round(list, 10);
            foreach (Dictionary<string,object> rule in Rules(state)) list.Items.Add(new KeyValuePair<Dictionary<string,object>,string>(rule, S(rule, "title") == "" ? "周期日程" : S(rule, "title")));
            if (list.Items.Count == 0) list.Items.Add(new KeyValuePair<Dictionary<string,object>,string>(null, "还没有周期自动转入规则"));
            f.Controls.Add(list); f.Controls.Add(LightUi.Label("选择一条规则后可以停止未来自动转入。", 25, 404, 360));
            Button stop = LightUi.DangerButton("停止自动转入", 374, 430, 130, DialogResult.OK), close = LightUi.Button("关闭", 514, 430, 82, DialogResult.Cancel);
            f.Controls.AddRange(new Control[] { stop, close }); f.CancelButton = close;
            if (f.ShowDialog() != DialogResult.OK || list.SelectedItem == null) break;
            Dictionary<string,object> selected = ((KeyValuePair<Dictionary<string,object>,string>)list.SelectedItem).Key;
            if (selected == null) continue;
            Rules(state).Remove(selected); Save(StatePath, state); cache["status"] = "已停止该系列的未来自动转入"; Save(CachePath, cache);
        }
    }

}
