using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;
using RainmeterBackend;

internal static partial class TodoApp
{
    private const string PluginRegistryUrl = "https://kevendai.github.io/Rainmeter_todo-plugin-registry/index-v1.json";
    private static void EnableTls12()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
    }
    private static string PluginDisplayStatus(Dictionary<string,object> state)
    {
        string path=Path.Combine(PluginPaths.Jobs,"io.github.kevendai.arxiv.json");if(!File.Exists(path))return JsonUtil.String(JsonUtil.Object(JsonUtil.Get(state,"meta")),"status","就绪");
        try{Dictionary<string,object> job=JsonUtil.LoadObject(path);string value=JsonUtil.String(job,"message","");return value==""?"插件状态："+JsonUtil.String(job,"state","未知"):value;}catch{return "插件状态暂不可读";}
    }
    private static readonly Font PluginNavFont = new Font("Microsoft YaHei UI", 10F);
    private static readonly Font PluginNavGlyphFont = new Font(LightUi.IconFontName, 12F);
    private static readonly Font PluginCaptionFont = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
    private static readonly Font PluginPageTitleFont = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
    private static readonly Font PluginRowTitleFont = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    private static readonly Font PluginRowSubFont = new Font("Microsoft YaHei UI", 9F);
    private static readonly Font PluginBadgeFont = new Font("Microsoft YaHei UI", 8.5F);
    private static readonly Font PluginVersionFont = new Font("Microsoft YaHei UI", 8F);
    private static readonly Color PluginDoneGreen = LightUi.Done;
    private static readonly Color PluginSelectedBack = LightUi.Selected;
    private static readonly Color PluginHoverBack = Color.FromArgb(235, 246, 255);
    private static readonly Color PluginRowBack = Color.FromArgb(247, 251, 255);
    private static readonly Color PluginBadgeBackOff = Color.FromArgb(236, 241, 246);
    private static readonly Color PluginBadgeBackOn = Color.FromArgb(229, 245, 236);

    private sealed class PluginRow
    {
        public string Title = "", Subtitle = "", Badge = "";
        public Color BadgeFore, BadgeBack;
        public bool Dimmed;
        public object Tag;
    }

    // 自绘行列表：圆角卡片行 + 状态胶囊，替代原生 ListView，与全局浅色风格一致。
    // 支持悬停、单选、双击激活、键盘上下选择、滚轮滚动；绘制按 UiScale 做矢量缩放。
    private sealed class PluginListControl : Control
    {
        private const int RowHeight = 56, RowGap = 8, RowPad = 14;
        public readonly List<PluginRow> Rows = new List<PluginRow>();
        public int SelectedIndex = -1;
        public string EmptyText = "暂无内容";
        public event EventHandler SelectionChanged;
        public event EventHandler RowActivated;
        private int hoverIndex = -1, scrollOffset = 0;

        public PluginListControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; TabStop = true;
        }

        public PluginRow SelectedRow { get { return SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null; } }
        private float ViewScale { get { float s = UiScale.For(this); return s > 0.01F ? s : 1F; } }
        private int DesignHeight { get { return (int)Math.Ceiling(Height / ViewScale); } }
        private int ContentHeight { get { return Rows.Count == 0 ? 0 : Rows.Count * (RowHeight + RowGap) + RowGap; } }
        private int MaxScroll { get { return Math.Max(0, ContentHeight - DesignHeight); } }
        private bool Scrollable { get { return ContentHeight > DesignHeight; } }

        public void AfterReload()
        {
            if (SelectedIndex >= Rows.Count) SelectedIndex = Rows.Count - 1;
            scrollOffset = Math.Min(scrollOffset, MaxScroll);
            Invalidate();
        }

        private int RowAt(int designY)
        {
            int rel = designY + scrollOffset - RowGap;
            if (rel < 0) return -1;
            int index = rel / (RowHeight + RowGap);
            if (index < 0 || index >= Rows.Count) return -1;
            return rel % (RowHeight + RowGap) <= RowHeight ? index : -1;
        }

        private void EnsureVisible(int index)
        {
            int top = RowGap + index * (RowHeight + RowGap), bottom = top + RowHeight;
            if (top - RowGap < scrollOffset) scrollOffset = Math.Max(0, top - RowGap);
            else if (bottom + RowGap - scrollOffset > DesignHeight) scrollOffset = Math.Min(MaxScroll, bottom + RowGap - DesignHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = ViewScale; int designWidth = (int)Math.Ceiling(Width / scale), designHeight = DesignHeight;
            if (Math.Abs(scale - 1F) > 0.001F) g.ScaleTransform(scale, scale);
            if (Rows.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, PluginRowSubFont, new Rectangle(0, 0, designWidth, Math.Min(designHeight, 120)), LightUi.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int rowWidth = designWidth - 2 - (Scrollable ? 10 : 0), y = RowGap - scrollOffset;
            for (int i = 0; i < Rows.Count; i++)
            {
                Rectangle bounds = new Rectangle(1, y, rowWidth, RowHeight);
                if (bounds.Bottom >= 0 && bounds.Top <= designHeight)
                {
                    PluginRow row = Rows[i]; bool selected = i == SelectedIndex, hover = i == hoverIndex;
                    Color fill = selected ? PluginSelectedBack : hover ? PluginHoverBack : PluginRowBack;
                    Color edge = selected ? LightUi.Accent : LightUi.Border;
                    using (GraphicsPath path = LightUi.RoundedPath(bounds, 10))
                    {
                        using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                        using (Pen pen = new Pen(edge, selected ? 1.6F : 1F)) g.DrawPath(pen, path);
                    }
                    if (selected) using (GraphicsPath barPath = LightUi.RoundedPath(new Rectangle(bounds.Left + 6, bounds.Top + 14, 4, RowHeight - 28), 2))
                    {
                        using (SolidBrush bar = new SolidBrush(LightUi.Accent)) g.FillPath(bar, barPath);
                    }
                    Color titleColor = row.Dimmed ? LightUi.Muted : LightUi.Text;
                    TextRenderer.DrawText(g, row.Title, PluginRowTitleFont, new Rectangle(bounds.Left + 16, bounds.Top + 8, bounds.Width - 180, 22), titleColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, row.Subtitle, PluginRowSubFont, new Rectangle(bounds.Left + 16, bounds.Top + 31, bounds.Width - 180, 18), LightUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    if (row.Badge != "")
                    {
                        Size size = TextRenderer.MeasureText(g, row.Badge, PluginBadgeFont, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding);
                        int chipWidth = size.Width + 18, chipHeight = 22;
                        Rectangle chip = new Rectangle(bounds.Right - RowPad - chipWidth, bounds.Top + (RowHeight - chipHeight) / 2, chipWidth, chipHeight);
                        using (GraphicsPath chipPath = LightUi.RoundedPath(chip, 11))
                        {
                            using (SolidBrush chipBrush = new SolidBrush(row.BadgeBack)) g.FillPath(chipBrush, chipPath);
                        }
                        TextRenderer.DrawText(g, row.Badge, PluginBadgeFont, chip, row.BadgeFore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                }
                y += RowHeight + RowGap;
            }
            if (Scrollable)
            {
                int trackHeight = designHeight - 8, thumbHeight = Math.Max(32, (int)((double)designHeight / ContentHeight * trackHeight));
                int thumbTop = 4 + (MaxScroll == 0 ? 0 : (int)((double)scrollOffset / MaxScroll * (trackHeight - thumbHeight)));
                using (GraphicsPath thumbPath = LightUi.RoundedPath(new Rectangle(designWidth - 6, thumbTop, 4, thumbHeight), 2))
                {
                    using (SolidBrush thumbBrush = new SolidBrush(Color.FromArgb(140, 140, 155, 175))) g.FillPath(thumbBrush, thumbPath);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = RowAt((int)(e.Y / ViewScale));
            if (index != hoverIndex) { hoverIndex = index; Invalidate(); }
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            int index = RowAt((int)(e.Y / ViewScale));
            if (index != SelectedIndex) { SelectedIndex = index; Invalidate(); RaiseSelectionChanged(); }
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (RowAt((int)(e.Y / ViewScale)) >= 0 && RowActivated != null) RowActivated(this, EventArgs.Empty);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Scrollable) return;
            scrollOffset = Math.Max(0, Math.Min(MaxScroll, scrollOffset - Math.Sign(e.Delta) * (RowHeight + RowGap) * 2));
            Invalidate();
        }
        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Rows.Count == 0) return;
            int next = SelectedIndex;
            if (e.KeyCode == Keys.Down) next = SelectedIndex < 0 ? 0 : Math.Min(Rows.Count - 1, SelectedIndex + 1);
            else if (e.KeyCode == Keys.Up) next = SelectedIndex < 0 ? 0 : Math.Max(0, SelectedIndex - 1);
            else return;
            if (next != SelectedIndex) { SelectedIndex = next; EnsureVisible(next); Invalidate(); RaiseSelectionChanged(); }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        private void RaiseSelectionChanged() { if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }
    }

    // 侧边栏导航项：图标 + 文本，选中高亮 + Accent 竖条，支持鼠标与键盘（Enter/Space）。
    private sealed class SettingsNavItem : Control
    {
        public string Glyph = "", Title = "";
        public bool Selected;
        public event EventHandler Activated;
        private bool hover;
        public SettingsNavItem()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; Height = 40; Cursor = Cursors.Hand; TabStop = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = UiScale.For(this); if (scale < 0.01F) scale = 1F;
            int w = (int)Math.Ceiling(Width / scale), h = (int)Math.Ceiling(Height / scale);
            if (Math.Abs(scale - 1F) > 0.001F) g.ScaleTransform(scale, scale);
            Rectangle bounds = new Rectangle(0, 0, w - 1, h - 1);
            if (Selected || hover) using (GraphicsPath path = LightUi.RoundedPath(bounds, 10))
            {
                using (SolidBrush brush = new SolidBrush(Selected ? PluginSelectedBack : PluginHoverBack)) g.FillPath(brush, path);
            }
            if (Selected) using (GraphicsPath barPath = LightUi.RoundedPath(new Rectangle(4, 10, 4, h - 20), 2))
            {
                using (SolidBrush bar = new SolidBrush(LightUi.Accent)) g.FillPath(bar, barPath);
            }
            Color fore = Selected ? LightUi.Accent : hover ? LightUi.Text : LightUi.Muted;
            if (Glyph != "") TextRenderer.DrawText(g, Glyph, PluginNavGlyphFont, new Rectangle(10, 0, 32, h), fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Title, PluginNavFont, new Rectangle(48, 0, w - 56, h), fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) Activate(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Activate(); }
        private void Activate() { if (Activated != null) Activated(this, EventArgs.Empty); }
    }

    private static SettingsNavItem NewNavItem(string glyph, string title, int top)
    {
        return new SettingsNavItem { Glyph = glyph, Title = title, Text = title, Left = 12, Top = top, Width = 186, Height = 40 };
    }

    // 设置页内容卡片：圆角浅色底 + 细描边，承载一组相关设置项。
    private static Panel SettingsCard(Control parent, int x, int y, int width, int height)
    {
        Panel card = new Panel { Left = x, Top = y, Width = width, Height = height, BackColor = PluginRowBack };
        LightUi.Round(card, 10);
        card.Paint += delegate(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int radius = Math.Max(1, (int)Math.Round(10F * UiScale.For(card)));
            using (GraphicsPath path = LightUi.RoundedPath(new Rectangle(0, 0, card.Width - 1, card.Height - 1), radius))
            using (Pen pen = new Pen(LightUi.Border, 1F)) e.Graphics.DrawPath(pen, path);
        };
        parent.Controls.Add(card); return card;
    }

    private static void ShowSettings()
    {
        PluginPaths.Ensure();
        Form form=LightUi.Form("待办设置",920,720);
        Panel sidebar=new Panel{Left=0,Top=0,Width=210,Height=720,BackColor=Color.FromArgb(232,241,249)};
        sidebar.Paint+=delegate(object sender,PaintEventArgs e){using(Pen pen=new Pen(LightUi.Border,1F))e.Graphics.DrawLine(pen,sidebar.Width-1,0,sidebar.Width-1,sidebar.Height);};
        sidebar.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
        form.Controls.Add(sidebar);
        Label caption=new Label{Text="待办设置",Left=20,Top=22,Width=170,Height=28,ForeColor=LightUi.Text,BackColor=Color.Transparent,Font=PluginCaptionFont};
        caption.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
        sidebar.Controls.Add(caption);
        Label version=new Label{Text="Rainmeter Desktop Widgets "+AppVersion+"\r\nPlugin API v1",Left=20,Top=648,Width=176,Height=40,ForeColor=LightUi.Muted,BackColor=Color.Transparent,Font=PluginVersionFont};
        sidebar.Controls.Add(version);
        Panel content=new Panel{Left=210,Top=0,Width=710,Height=720,BackColor=Color.Transparent};
        content.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
        form.Controls.Add(content);
        Func<string,Panel> newPage=delegate(string title)
        {
            Panel page=new Panel{Left=0,Top=0,Width=710,Height=720,BackColor=Color.Transparent,Visible=false};
            page.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
            Label pageTitle=new Label{Text=title,Left=28,Top=26,Width=620,Height=30,ForeColor=LightUi.Text,BackColor=Color.Transparent,Font=PluginPageTitleFont};
            pageTitle.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
            page.Controls.Add(pageTitle);content.Controls.Add(page);
            return page;
        };
        Panel installedPage=newPage("已安装插件");
        PluginListControl list=new PluginListControl{Left=28,Top=92,Width=654,Height=400,EmptyText="还没有安装任何插件。切换到「插件市场」浏览官方插件，或「从本地安装」。"};
        Label status=LightUi.Label("",28,552,654);
        Button toggle=LightUi.PrimaryButton("启用 / 禁用",28,506,120,DialogResult.None);
        Button configure=LightUi.Button("配置",156,506,88,DialogResult.None);
        Button run=LightUi.Button("立即运行",252,506,100,DialogResult.None);
        Button actions=LightUi.Button("插件操作",360,506,100,DialogResult.None);
        Button uninstall=LightUi.DangerButton("卸载程序",468,506,100,DialogResult.None);
        Button local=LightUi.Button("从本地安装",576,506,118,DialogResult.None);
        installedPage.Controls.Add(list);installedPage.Controls.Add(status);
        installedPage.Controls.AddRange(new Control[]{toggle,configure,run,actions,uninstall,local});
        Action updateInstalledButtons=delegate{bool has=list.SelectedRow!=null;toggle.Enabled=configure.Enabled=run.Enabled=actions.Enabled=uninstall.Enabled=has;};
        list.SelectionChanged+=delegate{updateInstalledButtons();};
        list.RowActivated+=delegate{if(configure.Enabled)configure.PerformClick();};
        Action reload=delegate{ReloadPlugins(list,status);updateInstalledButtons();};reload();
        System.Windows.Forms.Timer jobTimer=new System.Windows.Forms.Timer{Interval=500};jobTimer.Tick+=delegate{PluginRow selectedRow=list.SelectedRow;if(selectedRow==null)return;PluginManifest selectedManifest=selectedRow.Tag as PluginManifest;if(selectedManifest==null)return;string path=Path.Combine(PluginPaths.Jobs,selectedManifest.Id+".json");try{if(File.Exists(path)){Dictionary<string,object> job=JsonUtil.LoadObject(path);string state=JsonUtil.String(job,"state",""),message=JsonUtil.String(job,"message","");int current=JsonUtil.Int(job,"current",0),total=JsonUtil.Int(job,"total",0);status.Text=state+(total>0?" "+current+"/"+total:"")+(message==""?"":" · "+message);}}catch{}};jobTimer.Start();form.FormClosed+=delegate{jobTimer.Stop();jobTimer.Dispose();};
        toggle.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);Dictionary<string,object> c=PluginRuntime.Current(m.Id);c["enabled"]=!JsonUtil.Bool(c,"enabled",false);JsonUtil.SaveAtomic(Path.Combine(PluginPaths.PluginRoot(m.Id),"current.json"),c);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        configure.Click+=delegate{try{ShowPluginConfig(SelectedPlugin(list));reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        run.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);string verb=m.Capabilities.Contains("value_provider")?"Values":m.Capabilities.Contains("todo_source")?"Sync":"";if(verb=="")throw new Exception("此插件由日历按需调用。 ");StartPluginCommand(verb,m.Id);status.Text="已启动 "+m.Name;}catch(Exception ex){LightUi.Error(ex.Message);}};
        actions.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);ContextMenuStrip menu=new ContextMenuStrip();ToolStripItem cancel=menu.Items.Add("取消当前任务");cancel.Click+=delegate{Process.Start(new ProcessStartInfo(PluginHostPath,"Cancel "+m.Id){UseShellExecute=false,CreateNoWindow=true});};ToolStripItem clear=menu.Items.Add("清除该插件创建的待办");clear.Click+=delegate{Process.Start(new ProcessStartInfo(Application.ExecutablePath,"PluginClearTasks "+m.Id){UseShellExecute=false,CreateNoWindow=true});};ToolStripItem log=menu.Items.Add("查看最近错误");log.Click+=delegate{string path=Path.Combine(PluginPaths.Logs,m.Id+".log");MessageBox.Show(File.Exists(path)?File.ReadAllText(path,RuntimeUtil.Utf8NoBom):"暂无插件日志",m.Name+" 日志",MessageBoxButtons.OK,MessageBoxIcon.Information);};if(!String.IsNullOrWhiteSpace(m.Homepage)){ToolStripItem home=menu.Items.Add("打开主页");home.Click+=delegate{RuntimeUtil.Run(m.Homepage);};}if(m.Actions.Count>0)menu.Items.Add(new ToolStripSeparator());foreach(Dictionary<string,object> action in m.Actions){ToolStripItem item=menu.Items.Add(JsonUtil.String(action,"name",JsonUtil.String(action,"id","操作")));item.Tag=action;item.Click+=delegate(object sender,EventArgs ignored){Dictionary<string,object> selectedAction=(Dictionary<string,object>)((ToolStripItem)sender).Tag;string warning=JsonUtil.String(selectedAction,"risk","");if(JsonUtil.Bool(selectedAction,"confirm",false)&&!LightUi.Confirm((warning==""?"确定执行此操作？":warning),"插件操作"))return;Process.Start(new ProcessStartInfo(PluginHostPath,"PluginAction "+m.Id+" "+JsonUtil.String(selectedAction,"id","") ){UseShellExecute=false,CreateNoWindow=true});};}menu.Show(actions,new Point(0,actions.Height));}catch(Exception ex){LightUi.Error(ex.Message);}};
        uninstall.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);if(!LightUi.Confirm("卸载 "+m.Name+" 的程序版本？插件数据默认保留。","卸载插件"))return;Directory.Delete(PluginPaths.PluginRoot(m.Id),true);if(Directory.Exists(PluginPaths.DataRoot(m.Id))&&LightUi.Confirm("程序已卸载。是否同时永久删除该插件的配置、secret、状态和缓存？","删除插件数据"))Directory.Delete(PluginPaths.DataRoot(m.Id),true);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        local.Click+=delegate{try{InstallLocalPlugin(form);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};

        Panel marketPage=newPage("插件市场");
        PluginListControl marketList=new PluginListControl{Left=28,Top=92,Width=654,Height=400,EmptyText="点击「刷新市场」加载官方插件列表。"};
        Label marketStatus=LightUi.Label("尚未加载市场",28,552,654);
        Button refresh=LightUi.PrimaryButton("刷新市场",28,506,120,DialogResult.None);
        Button installMarket=LightUi.Button("安装 / 更新",156,506,120,DialogResult.None);
        marketPage.Controls.Add(marketList);marketPage.Controls.Add(marketStatus);
        marketPage.Controls.AddRange(new Control[]{refresh,installMarket});
        installMarket.Enabled=false;
        marketList.SelectionChanged+=delegate{string required;installMarket.Enabled=marketList.SelectedRow!=null&&MarketCompatible(marketList.SelectedRow.Tag as Dictionary<string,object>,out required);};
        marketList.RowActivated+=delegate{if(installMarket.Enabled)installMarket.PerformClick();};
        refresh.Click+=delegate{try{LoadMarket(marketList,marketStatus);}catch(Exception ex){marketStatus.Text="市场不可用："+ex.Message;marketStatus.ForeColor=LightUi.Danger;}};
        installMarket.Click+=delegate{try{PluginRow selectedMarket=marketList.SelectedRow;if(selectedMarket==null)throw new Exception("请先选择一个市场插件。");InstallMarketPlugin(selectedMarket);reload();LoadMarket(marketList,marketStatus);}catch(Exception ex){LightUi.Error(ex.Message);}};
        Panel appearancePage=newPage("外观与备份");
        Panel scaleCard=SettingsCard(appearancePage,28,92,654,106);
        string[] labels={"自动","75%","80%","90%","100%","110%","125%"},values={"auto","0.75","0.80","0.90","1.00","1.10","1.25"};
        Label scaleLabel=LightUi.Label("桌面磁贴缩放",16,14,220);scaleCard.Controls.Add(scaleLabel);
        ComboBox scale=new ComboBox{Left=16,Top=40,Width=220,DropDownStyle=ComboBoxStyle.DropDownList,Font=PluginNavFont};scale.Items.AddRange(labels);int selected=Array.FindIndex(values,x=>String.Equals(x,UiScale.Mode,StringComparison.OrdinalIgnoreCase));scale.SelectedIndex=selected<0?0:selected;scaleCard.Controls.Add(scale);
        Label windowScaleLabel=LightUi.Label("管理与编辑窗口缩放",252,14,220);scaleCard.Controls.Add(windowScaleLabel);
        ComboBox windowScale=new ComboBox{Left=252,Top=40,Width=220,DropDownStyle=ComboBoxStyle.DropDownList,Font=PluginNavFont};windowScale.Items.AddRange(labels);int windowSelected=Array.FindIndex(values,x=>String.Equals(x,UiScale.WindowMode,StringComparison.OrdinalIgnoreCase));windowScale.SelectedIndex=windowSelected<0?0:windowSelected;scaleCard.Controls.Add(windowScale);
        Button apply=LightUi.PrimaryButton("应用缩放",488,38,130,DialogResult.None);scaleCard.Controls.Add(apply);
        apply.Click+=delegate{try{UiScale.SaveMode(values[scale.SelectedIndex]);UiScale.SaveWindowMode(values[windowScale.SelectedIndex]);RenderUiScaleSkins();MessageBox.Show("磁贴缩放已应用；窗口缩放将在下次打开窗口时生效。","缩放设置");}catch(Exception ex){LightUi.Error(ex.Message);}};

        Panel backupCard=SettingsCard(appearancePage,28,214,654,150);
        Label backupTitle=new Label{Text="加密用户配置备份",Left=16,Top=14,Width=400,Height=22,ForeColor=LightUi.Text,BackColor=Color.Transparent,Font=PluginNavFont};backupCard.Controls.Add(backupTitle);

        Button export=LightUi.PrimaryButton("导出用户配置",16,84,150,DialogResult.None),import=LightUi.Button("导入用户配置",178,84,150,DialogResult.None);
        backupCard.Controls.AddRange(new Control[]{export,import});
        export.Click+=delegate{try{string path=ExportUserBackupInteractive();if(path!="")MessageBox.Show("备份已保存：\r\n"+path,"导出完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){LightUi.Error(ex.Message);}};
        import.Click+=delegate{try{string result=ImportUserBackupInteractive();if(result!=""){RenderUiScaleSkins();MessageBox.Show(result,"导入完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}}catch(Exception ex){LightUi.Error(ex.Message);}};
        Panel aboutPage=newPage("关于与更新");
        Panel aboutCard=SettingsCard(aboutPage,28,92,654,180);
        Label appName=new Label{Text="Rainmeter Desktop Widgets",Left=16,Top=16,Width=400,Height=28,ForeColor=LightUi.Text,BackColor=Color.Transparent,Font=PluginCaptionFont};
        Label aboutVersion=new Label{Text="版本 "+AppVersion+" · Plugin API v1",Left=16,Top=52,Width=400,Height=20,ForeColor=LightUi.Muted,BackColor=Color.Transparent,Font=PluginRowSubFont};
        Button update=LightUi.Button("检查主程序更新",16,104,160,DialogResult.None);
        aboutCard.Controls.AddRange(new Control[]{appName,aboutVersion,update});
        update.Click+=delegate{try{UpdateCheckResult info=CheckLatestUpdate();if(!info.IsNewer)MessageBox.Show("已是最新版本："+info.Tag,"检查更新");else if(MessageBox.Show("发现 "+info.Tag+"，现在启动升级器？","检查更新",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){StartExternalUpdater();form.Close();}}catch(Exception ex){LightUi.Error(ex.Message);}};
        SettingsNavItem navInstalled=NewNavItem("\xE74C","已安装插件",92),navMarket=NewNavItem("\xE719","插件市场",136),navAppearance=NewNavItem("\xE790","外观与备份",180),navAbout=NewNavItem("\xE946","关于与更新",224);
        sidebar.Controls.AddRange(new Control[]{navInstalled,navMarket,navAppearance,navAbout});
        Panel[] pages={installedPage,marketPage,appearancePage,aboutPage};
        SettingsNavItem[] navs={navInstalled,navMarket,navAppearance,navAbout};
        Action<int> selectPage=delegate(int index){for(int i=0;i<pages.Length;i++){pages[i].Visible=i==index;navs[i].Selected=i==index;navs[i].Invalidate();}};
        for(int i=0;i<navs.Length;i++){int index=i;navs[index].Activated+=delegate{selectPage(index);};}
        selectPage(0);
        Button close=LightUi.CloseButton(form);form.Controls.Add(close);close.BringToFront();
        form.ShowDialog();
    }

    private static void ReloadPlugins(PluginListControl view,Label status)
    {
        view.Rows.Clear();int invalid=0;
        foreach(string root in Directory.Exists(PluginPaths.Plugins)?Directory.GetDirectories(PluginPaths.Plugins):new string[0])try
        {
            string id=Path.GetFileName(root);PluginManifest m=PluginRuntime.Resolve(id,false);Dictionary<string,object> c=PluginRuntime.Current(id);
            bool enabled=JsonUtil.Bool(c,"enabled",false);
            List<string> facts=new List<string>();facts.Add("v"+m.Version);
            if(m.Capabilities.Count>0)facts.Add(String.Join(", ",m.Capabilities.ToArray()));
            if(m.Permissions.Count>0)facts.Add("权限 "+String.Join(", ",m.Permissions.ToArray()));
            string runtimeStatus=PluginRuntimeStatus(id,m);if(runtimeStatus!="")facts.Add(runtimeStatus);
            PluginRow row=new PluginRow();row.Title=m.Name;row.Subtitle=String.Join("  ·  ",facts.ToArray());
            row.Badge=enabled?"已启用":"已禁用";
            row.BadgeFore=enabled?PluginDoneGreen:LightUi.Muted;
            row.BadgeBack=enabled?PluginBadgeBackOn:PluginBadgeBackOff;
            row.Tag=m;view.Rows.Add(row);
        }catch{invalid++;}
        view.AfterReload();
        status.ForeColor=LightUi.Muted;
        status.Text="已安装 "+view.Rows.Count.ToString(CultureInfo.InvariantCulture)+" 个插件"+(invalid>0?"；"+invalid+" 个安装损坏":"");
    }
    private static string PluginRuntimeStatus(string id,PluginManifest manifest)
    {
        if(!manifest.Capabilities.Contains("value_provider"))return "";
        try
        {
            string variable="Plugin_"+System.Text.RegularExpressions.Regex.Replace(id,@"[^A-Za-z0-9_]","_")+"_server_ip";
            if(File.Exists(PluginPaths.Values))
            {
                Dictionary<string,object> values=JsonUtil.LoadObject(PluginPaths.Values),entries=JsonUtil.Object(JsonUtil.Get(values,"entries")),entry=JsonUtil.Object(JsonUtil.Get(entries,variable));
                string ip=JsonUtil.String(entry,"value","");if(ip!="")return "IP "+ip+(JsonUtil.Bool(entry,"stale",false)?"（上次成功，当前已失效）":"");
            }
            string jobPath=Path.Combine(PluginPaths.Jobs,id+".json");if(File.Exists(jobPath)){Dictionary<string,object> job=JsonUtil.LoadObject(jobPath);string state=JsonUtil.String(job,"state","");if(state=="failed"||state=="attention")return "失败："+JsonUtil.String(job,"message","未知错误");}
        }
        catch{}
        return "尚未获取 IP";
    }
    private static PluginManifest SelectedPlugin(PluginListControl view){PluginRow row=view.SelectedRow;if(row==null)throw new Exception("请先选择一个插件。");return (PluginManifest)row.Tag;}

    private static void ShowPluginConfig(PluginManifest manifest)
    {
        string root=PluginPaths.VersionRoot(manifest.Id,manifest.Version);if(String.IsNullOrWhiteSpace(manifest.SettingsSchema))throw new Exception("该插件没有可配置项。");
        Dictionary<string,object> settingsAction=manifest.Actions.FirstOrDefault(a=>JsonUtil.Bool(a,"settings_ui",false));
        if(settingsAction!=null){StartPluginCommand("PluginAction",manifest.Id+" "+JsonUtil.String(settingsAction,"id",""));return;}
        Dictionary<string,object> schema=JsonUtil.LoadObject(PluginManifest.SafeChildPath(root,manifest.SettingsSchema,"设置 Schema"));Dictionary<string,object> props=JsonUtil.Object(JsonUtil.Get(schema,"properties"));
        string data=PluginPaths.DataRoot(manifest.Id);Directory.CreateDirectory(data);string configPath=Path.Combine(data,"config.json"),secretPath=Path.Combine(data,"secret.dat");
        Dictionary<string,object> config=File.Exists(configPath)?JsonUtil.LoadObject(configPath):new Dictionary<string,object>();Dictionary<string,object> secret=File.Exists(secretPath)?JsonUtil.ReadDpapiJson(secretPath):new Dictionary<string,object>();
        string oldConfig=JsonUtil.Serialize(config),oldSecret=JsonUtil.Serialize(secret);HashSet<string> required=new HashSet<string>(JsonUtil.Array(JsonUtil.Get(schema,"required")).Select(Convert.ToString),StringComparer.OrdinalIgnoreCase);AddressProviderBinding claimedProvider=manifest.Id=="io.github.kevendai.arxiv"?DynamicPluginValues.AddressProvider("arxiv.file_server"):null;
        Form f=LightUi.Form(manifest.Name+" 设置",680,Math.Min(820,190+props.Count*82));LightUi.Heading(f,manifest.Name,"敏感项使用当前 Windows 用户 DPAPI 加密。","settings.svg");
        Panel panel=new Panel{Left=24,Top=102,Width=630,Height=f.ClientSize.Height-180,AutoScroll=true};f.Controls.Add(panel);Dictionary<string,Control> controls=new Dictionary<string,Control>();int y=8;
        foreach(KeyValuePair<string,object> pair in props.OrderBy(x=>JsonUtil.Int(JsonUtil.Object(x.Value),"x-order",999)))
        {
            Dictionary<string,object> p=JsonUtil.Object(pair.Value);string type=JsonUtil.String(p,"type","string"),title=JsonUtil.String(p,"title",pair.Key);bool claimedAddress=claimedProvider!=null&&pair.Key=="file_url";bool isSecret=JsonUtil.Bool(p,"x-secret",false)||type=="password";object existing=PluginSettingValue(manifest.Id,pair.Key,isSecret?secret:config,secret,p);if(claimedAddress)existing=DynamicPluginValues.BindForTarget(Convert.ToString(existing??"",CultureInfo.InvariantCulture),"arxiv.file_server");
            Label label=LightUi.Label(title,8,y,590);panel.Controls.Add(label);Control control;
            if(type=="boolean")control=new CheckBox{Left=8,Top=y+28,Width=590,Checked=existing!=null&&Convert.ToBoolean(existing,CultureInfo.InvariantCulture),Text="启用",Font=LightUi.UiFont(10F)};
            else if(type=="enum") { ComboBox box=new ComboBox{Left=8,Top=y+28,Width=590,DropDownStyle=ComboBoxStyle.DropDownList};foreach(object option in JsonUtil.Array(JsonUtil.Get(p,"enum")))box.Items.Add(Convert.ToString(option));box.SelectedItem=Convert.ToString(existing);control=box; }
            else control=new TextBox{Left=8,Top=y+28,Width=590,Height=type=="multiline"?86:28,Multiline=type=="multiline",ScrollBars=type=="multiline"?ScrollBars.Vertical:ScrollBars.None,Text=existing==null?"":Convert.ToString(existing,CultureInfo.InvariantCulture),UseSystemPasswordChar=isSecret};
            if(claimedAddress){TextBox claimedBox=control as TextBox;if(claimedBox!=null){claimedBox.ReadOnly=true;claimedBox.BackColor=Color.FromArgb(232,238,244);claimedBox.Cursor=Cursors.Hand;claimedBox.Click+=delegate{MessageBox.Show("文件服务器地址当前由“"+claimedProvider.PluginName+"”接管。系统只替换发送地址中的主机/IP，保留协议、端口和路径。\r\n\r\n如需手动修改，请先在已安装插件中禁用该地址插件，再重新打开设置。","地址由插件接管",MessageBoxButtons.OK,MessageBoxIcon.Information);};}}panel.Controls.Add(control);controls[pair.Key]=control;y+=type=="multiline"?136:78;
        }
        panel.AutoScrollMinSize=new Size(0,y+10);Button cancel=LightUi.Button("取消",400,f.ClientSize.Height-60,120,DialogResult.Cancel);Button save=LightUi.PrimaryButton("保存",532,f.ClientSize.Height-60,120,DialogResult.OK);f.Controls.AddRange(new Control[]{cancel,save});f.CancelButton=cancel;
        if(f.ShowDialog()!=DialogResult.OK)return;
        foreach(KeyValuePair<string,Control> pair in controls)
        {
            Dictionary<string,object> p=JsonUtil.Object(props[pair.Key]);string type=JsonUtil.String(p,"type","string");if(claimedProvider!=null&&pair.Key=="file_url")continue;bool isSecret=JsonUtil.Bool(p,"x-secret",false)||type=="password";object value;
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
        Process p=Process.Start(new ProcessStartInfo("pwsh.exe","-NoLogo -NoProfile -ExecutionPolicy Bypass -File \""+script+"\" -Package \""+package.Replace("\"","\"\"")+"\" -PluginRoot \""+PluginPaths.Plugins+"\" -HostVersion \""+AppVersion+"\""+sha){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true});string output=p.StandardOutput.ReadToEnd(),error=p.StandardError.ReadToEnd();p.WaitForExit();if(p.ExitCode!=0)throw new Exception(error==""?output:error);
    }
    private static void InstallMarketPlugin(PluginRow row)
    {
        Dictionary<string,object> record=row.Tag as Dictionary<string,object>;if(record==null||!JsonUtil.Bool(record,"official",false))throw new Exception("仅允许安装官方市场条目。");string required;if(!MarketCompatible(record,out required))throw new Exception("该插件需要主程序 "+required+" 或更高版本。");string url=JsonUtil.String(record,"download",""),sha=JsonUtil.String(record,"sha256","");Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||!uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||uri.AbsolutePath.IndexOf("/releases/download/",StringComparison.OrdinalIgnoreCase)<0)throw new Exception("市场下载地址必须是 GitHub HTTPS Release。");if(!System.Text.RegularExpressions.Regex.IsMatch(sha,@"^[a-fA-F0-9]{64}$"))throw new Exception("市场条目缺少有效 SHA256。");
        EnableTls12();string temporary=Path.Combine(Path.GetTempPath(),"rw-market-"+Guid.NewGuid().ToString("N")+".rwplugin");try{using(WebClient web=new WebClient()){web.Headers[HttpRequestHeader.UserAgent]="RainmeterDesktopWidgets/"+AppVersion;web.DownloadFile(uri,temporary);}RunPluginInstaller(temporary,sha);}finally{try{File.Delete(temporary);}catch{}}
    }

    private static void LoadMarket(PluginListControl view,Label status)
    {
        EnableTls12();
        string json = "", sourcePath = PluginPaths.RegistryCache, sourceLabel = "官方市场已加载；";
        try
        {
            using (WebClient web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "RainmeterDesktopWidgets/" + AppVersion;
                // Download raw bytes and decode as UTF-8 explicitly.  WebClient.DownloadString
                // can fall back to the system's default code page on some machines, which turns
                // Chinese text in the registry into mojibake and later causes JSON parse errors.
                byte[] raw = web.DownloadData(PluginRegistryUrl);
                json = Encoding.UTF8.GetString(raw);
                // Only cache the response after it parses as valid JSON.
                JsonUtil.Deserialize(json);
                File.WriteAllText(PluginPaths.RegistryCache, json, RuntimeUtil.Utf8NoBom);
            }
        }
        catch
        {
            bool usable = false;
            if (File.Exists(PluginPaths.RegistryCache))
            {
                try
                {
                    json = File.ReadAllText(PluginPaths.RegistryCache, RuntimeUtil.Utf8NoBom);
                    JsonUtil.Deserialize(json);
                    sourceLabel = "离线：使用上次成功缓存；";
                    usable = true;
                }
                catch
                {
                    // Cache is corrupt; remove it so the next refresh starts clean.
                    try { File.Delete(PluginPaths.RegistryCache); } catch { }
                }
            }
            if (!usable)
            {
                sourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugin-registry-v1.json");
                if (!File.Exists(sourcePath)) throw;
                json = File.ReadAllText(sourcePath, RuntimeUtil.Utf8NoBom);
                sourceLabel = "离线：使用内置官方索引；";
            }
        }
        Dictionary<string,object> index=JsonUtil.Object(JsonUtil.Deserialize(json));view.Rows.Clear();
        foreach(object raw in JsonUtil.Array(JsonUtil.Get(index,"plugins")))
        {
            Dictionary<string,object> p=JsonUtil.Object(raw);if(!JsonUtil.Bool(p,"official",false))continue;
            string id=JsonUtil.String(p,"id","");if(id=="io.github.kevendai.network-ip")continue;string required;bool compatible=MarketCompatible(p,out required),installedPlugin=Directory.Exists(PluginPaths.PluginRoot(id));
            PluginRow row=new PluginRow();row.Title=JsonUtil.String(p,"name",id);row.Tag=p;
            string permissions=JsonUtil.String(p,"permissions",""),description=JsonUtil.String(p,"description","");
            row.Subtitle="v"+JsonUtil.String(p,"version","")+(description==""?"":"  ·  "+description)+"  ·  "+JsonUtil.String(p,"capability","")+(permissions==""?"":"  ·  权限 "+permissions);
            if(!compatible){row.Badge="需要主程序 "+required;row.BadgeFore=LightUi.Muted;row.BadgeBack=PluginBadgeBackOff;row.Dimmed=true;}
            else if(installedPlugin){row.Badge="已安装";row.BadgeFore=PluginDoneGreen;row.BadgeBack=PluginBadgeBackOn;}
            else{row.Badge="可安装";row.BadgeFore=LightUi.Accent;row.BadgeBack=Color.FromArgb(232,244,255);}
            view.Rows.Add(row);
        }
        view.AfterReload();
        DateTime cacheTime = File.Exists(sourcePath) ? File.GetLastWriteTime(sourcePath) : DateTime.Now;
        status.ForeColor = LightUi.Muted;
        status.Text = sourceLabel + "索引时间 " + cacheTime.ToString("yyyy-MM-dd HH:mm");
    }
    private static bool MarketCompatible(Dictionary<string,object> record,out string required){required=record==null?"":JsonUtil.String(record,"min_host_version","");Version host,min;return Version.TryParse(AppVersion,out host)&&Version.TryParse(required,out min)&&host.CompareTo(min)>=0;}
}
