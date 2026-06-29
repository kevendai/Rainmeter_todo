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
    private static string CleanTitle(string t){return Regex.Replace(t??"",@"^\s*\[(?:待办|代办)\]\s*","").Trim();}
    private static bool IsLocalPath(string value){return Regex.IsMatch((value??"").Trim(),@"^(?:[A-Za-z]:[\\/]|\\\\)[^\r\n<>""|?*]+$");}
    private static string TrimTarget(string value){return (value??"").Trim().TrimEnd(')',']','}','，','。','；',';');}
    private static string DisplayLink(string value){Uri uri;if(Uri.TryCreate((value??"").Trim(),UriKind.Absolute,out uri)&&uri.IsFile)return uri.LocalPath;return value??"";}
    private static bool IsWebLink(string value){Uri uri;return Uri.TryCreate((value??"").Trim(),UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https"||uri.Scheme=="wemeet");}
    private static string Target(Dictionary<string,object>e){string direct=TrimTarget(S(e,"url"));if(direct!=""){if(IsLocalPath(direct))return direct;Uri uri;if(Uri.TryCreate(direct,UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https"||uri.Scheme=="wemeet"||uri.IsFile))return uri.IsFile?uri.LocalPath:direct;}foreach(string k in new[]{"location","description"}){string text=S(e,k);Match m=Regex.Match(text,@"(?i)(?:https?://|wemeet://|file:///)[^\s<>\""'，。；;]+");if(m.Success)return DisplayLink(TrimTarget(m.Value));m=Regex.Match(text,@"(?i)(?:[A-Z]:\\|\\\\)[^\r\n<>""|?*]+");if(m.Success)return TrimTarget(m.Value);}return "";}
    private static string FullTime(Dictionary<string,object>e){DateTimeOffset?start=RuntimeUtil.Date(e,"start_at"),end=RuntimeUtil.Date(e,"end_at");if(!start.HasValue)return"";if(B(e,"all_day")){DateTimeOffset last=end.HasValue?end.Value.AddDays(-1):start.Value;return last.Date==start.Value.Date?start.Value.ToString("yyyy年M月d日 全天"):start.Value.ToString("yyyy年M月d日")+"–"+last.ToString("yyyy年M月d日")+" 全天";}if(!end.HasValue||end<=start)return start.Value.ToString("yyyy年M月d日 HH:mm");return end.Value.Date==start.Value.Date?start.Value.ToString("yyyy年M月d日 HH:mm")+"–"+end.Value.ToString("HH:mm"):start.Value.ToString("yyyy年M月d日 HH:mm")+" → "+end.Value.ToString("yyyy年M月d日 HH:mm");}
    private static bool AddTask(Dictionary<string,object>e,Dictionary<string,object>state,string mode,bool hide){using(Mutex m=new Mutex(false,@"Global\RainmeterTodoState")){bool held=m.WaitOne(TimeSpan.FromSeconds(15));if(!held)throw new Exception("待办数据正忙，请稍后重试");try{if(!File.Exists(TodoPath))throw new Exception("未找到待办数据");Dictionary<string,object>todo=JsonUtil.LoadObject(TodoPath);List<Dictionary<string,object>>tasks=List(todo,"tasks");Dictionary<string,object>task=tasks.FirstOrDefault(t=>S(t,"calendar_occurrence_key")==S(e,"occurrence_key"));if(task!=null){if(!Conversions(state).Any(c=>S(c,"occurrence_key")==S(e,"occurrence_key")))Conversions(state).Add(Conversion(e,task,mode,hide));return false;}Conversions(state).RemoveAll(c=>S(c,"occurrence_key")==S(e,"occurrence_key"));DateTimeOffset?start=RuntimeUtil.Date(e,"start_at"),end=RuntimeUtil.Date(e,"end_at"),reminder=RuntimeUtil.Date(e,"reminder_at");DateTimeOffset?available=start.HasValue&&reminder.HasValue?(start.Value<=reminder.Value?start:reminder):(reminder.HasValue?reminder:start);if(B(e,"all_day")&&end.HasValue)end=end.Value.AddMinutes(-1);List<string>notes=new List<string>{"来自 CalDAV 日程","日程时间："+FullTime(e)};if(reminder.HasValue)notes.Add("最早提醒："+reminder.Value.ToString("yyyy年M月d日 HH:mm"));if(S(e,"location")!="")notes.Add("地点："+S(e,"location"));if(S(e,"description")!="")notes.Add("日程备注："+S(e,"description"));task=new Dictionary<string,object>{{"id",Guid.NewGuid().ToString("N")},{"title","（日程）"+CleanTitle(S(e,"title"))},{"target",Target(e)},{"note",String.Join("\r\n",notes)},{"labels",new List<object>{"日程"}},{"completed",false},{"source","caldav"},{"created_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"completed_at",null},{"available_from",available.HasValue?RuntimeUtil.Iso(available.Value):null},{"due_at",end.HasValue?RuntimeUtil.Iso(end.Value):null},{"calendar_uid",S(e,"uid")},{"calendar_occurrence_key",S(e,"occurrence_key")}};tasks.Add(task);Save(TodoPath,todo);Conversions(state).Add(Conversion(e,task,mode,hide));return true;}finally{m.ReleaseMutex();}}}
    private static Dictionary<string,object> Conversion(Dictionary<string,object>e,Dictionary<string,object>task,string mode,bool hide){return new Dictionary<string,object>{{"occurrence_key",S(e,"occurrence_key")},{"uid",S(e,"uid")},{"recurrence_id",S(e,"recurrence_id")},{"task_id",S(task,"id")},{"converted_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"mode",mode},{"hide_event",hide}};}
    private static bool OccursOn(Dictionary<string,object>e,DateTime date){DateTimeOffset?start=RuntimeUtil.Date(e,"start_at"),end=RuntimeUtil.Date(e,"end_at");if(!start.HasValue||!end.HasValue)return false;DateTimeOffset ds=new DateTimeOffset(date,TimeZoneInfo.Local.GetUtcOffset(date)),de=ds.AddDays(1);return start.Value<de&&end.Value>ds;}
    private static bool AutoConvertDue(Dictionary<string,object> e,DateTime today){DateTimeOffset? reminder=RuntimeUtil.Date(e,"reminder_at");return OccursOn(e,today)||(reminder.HasValue&&reminder.Value.Date<=today);}
    private static bool AutoConvert(Dictionary<string,object>cache,Dictionary<string,object>state){bool changed=false;DateTime today=DateTime.Now.Date;foreach(Dictionary<string,object>e in Events(cache).Where(e=>AutoConvertDue(e,today))){Dictionary<string,object>rule=Rules(state).FirstOrDefault(r=>S(r,"uid")==S(e,"uid"));if(Regex.IsMatch(S(e,"title"),@"^\s*\[(?:待办|代办)\]")&&rule==null){rule=new Dictionary<string,object>{{"uid",S(e,"uid")},{"title",CleanTitle(S(e,"title"))},{"effective_from",S(e,"start_at")},{"created_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"reason","title-tag"},{"hide_event",true}};Rules(state).Add(rule);changed=true;}if(rule!=null&&AddTask(e,state,"series",JsonUtil.Bool(rule,"hide_event",true)))changed=true;}return changed;}
    private sealed class Choice{public string Mode;public bool Hide;}
    private static Choice Choose(Dictionary<string,object> e)
    {
        Form f = LightUi.Form("转为待办", 540, 340);
        LightUi.Heading(f, "转为带时间待办", B(e, "recurring") ? "选择仅转换这一期，或让后续周期自动进入待办。" : "开始、结束和提醒时间会一并带入待办。");
        Label eventTitle = new Label { Text = CleanTitle(S(e, "title")), Left = 26, Top = 100, Width = 488, Height = 48, BackColor = LightUi.Surface, ForeColor = LightUi.Text, Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold), Padding = new Padding(14, 13, 14, 8) };
        LightUi.Round(eventTitle, 10);
        f.Controls.Add(eventTitle);
        CheckBox hide = new CheckBox { Text = "转换后从今日日程磁贴隐藏", Checked = true, Left = 28, Top = 168, Width = 360, Height = 28, ForeColor = LightUi.Text, BackColor = Color.Transparent, FlatStyle = FlatStyle.Flat };
        f.Controls.Add(hide); f.Controls.Add(LightUi.Label("原事件仍保留在 CalDAV 和手机日历中。", 50, 199, 400));
        Button cancel = LightUi.Button("取消", 222, 270, 86, DialogResult.Cancel);
        Button once = LightUi.Button("仅本次", 318, 270, 86, DialogResult.OK);
        Button series = LightUi.PrimaryButton("本次及今后", 414, 270, 100, DialogResult.Yes);
        if (!B(e, "recurring")) series.Visible = false;
        f.Controls.AddRange(new Control[] { cancel, once, series }); f.CancelButton = cancel;
        DialogResult result = f.ShowDialog();
        if (result != DialogResult.OK && result != DialogResult.Yes) return null;
        return new Choice { Mode = result == DialogResult.Yes ? "Series" : "Once", Hide = hide.Checked };
    }
    private static bool ConvertInteractive(Dictionary<string,object>e,Dictionary<string,object>state,Dictionary<string,object>cache){Choice c=Choose(e);if(c==null)return false;if(c.Mode=="Series"){Dictionary<string,object>rule=Rules(state).FirstOrDefault(r=>S(r,"uid")==S(e,"uid"));if(rule==null){rule=new Dictionary<string,object>{{"uid",S(e,"uid")},{"title",CleanTitle(S(e,"title"))},{"effective_from",S(e,"start_at")},{"created_at",RuntimeUtil.Iso(DateTimeOffset.Now)},{"reason","manual"}};Rules(state).Add(rule);}rule["hide_event"]=c.Hide;}bool added=AddTask(e,state,c.Mode=="Series"?"series":"single",c.Hide);if(added)cache["status"]=c.Hide?"已转为待办并从日程隐藏":"已转为待办，日程继续显示";return added;}
    private static DialogResult ShowDetails(Dictionary<string,object> e, bool converted)
    {
        Form f = LightUi.Form("日程详情", 680, 630);
        LightUi.Heading(f, CleanTitle(S(e, "title")), "日程详情");
        DateTimeOffset? reminder = RuntimeUtil.Date(e, "reminder_at");
        Panel facts = new Panel { Left = 24, Top = 92, Width = 632, Height = 112, BackColor = LightUi.Surface };
        LightUi.Round(facts, 10);
        facts.Controls.Add(LightUi.Label("时间", 16, 12, 64)); facts.Controls.Add(new Label { Text = FullTime(e), Left = 88, Top = 12, Width = 520, Height = 22, ForeColor = LightUi.Text, BackColor = Color.Transparent });
        facts.Controls.Add(LightUi.Label("提醒", 16, 44, 64)); facts.Controls.Add(new Label { Text = reminder.HasValue ? reminder.Value.ToString("yyyy年M月d日 HH:mm") : "未设置", Left = 88, Top = 44, Width = 520, Height = 22, ForeColor = LightUi.Text, BackColor = Color.Transparent });
        facts.Controls.Add(LightUi.Label("地点", 16, 76, 64)); facts.Controls.Add(new Label { Text = S(e, "location") == "" ? "未设置" : S(e, "location"), Left = 88, Top = 76, Width = 520, Height = 22, ForeColor = LightUi.Text, BackColor = Color.Transparent });
        f.Controls.Add(facts); f.Controls.Add(LightUi.Label("备注", 25, 222, 200));
        TextBox note = LightUi.TextBox(24, 246, 632, S(e, "description") == "" ? "（没有备注）" : S(e, "description")); note.Multiline = true; note.ReadOnly = true; note.Height = 270; note.ScrollBars = ScrollBars.Vertical; f.Controls.Add(note);
        Button open = LightUi.Button("打开链接 / 路径", 0, 552, 126, DialogResult.None);
        Button edit = LightUi.Button("编辑", 0, 552, 84, DialogResult.Yes);
        Button convert = LightUi.PrimaryButton(converted ? "已转为待办" : "转为待办", 0, 552, 116, DialogResult.OK);
        Button close = LightUi.Button("关闭", 0, 552, 118, DialogResult.Cancel);
        open.Visible = Target(e) != ""; open.Click += delegate { RuntimeUtil.Run(Target(e)); }; convert.Visible = !converted;
        Action layoutActions = delegate {
            List<Button> buttons = new List<Button>();
            if (open.Visible) buttons.Add(open);
            buttons.Add(edit); if (convert.Visible) buttons.Add(convert); buttons.Add(close);
            int gap = 10, total = buttons.Sum(b => b.Width) + gap * (buttons.Count - 1), x = f.ClientSize.Width - 24 - total;
            foreach (Button button in buttons) { button.Left = x; button.Top = 552; x += button.Width + gap; }
        };
        layoutActions();
        f.Controls.AddRange(new Control[] { open, edit, convert, close }); f.CancelButton = close;
        return f.ShowDialog();
    }
}
