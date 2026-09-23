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
        string directory=Path.GetDirectoryName(path);if(!String.IsNullOrWhiteSpace(directory))Directory.CreateDirectory(directory);
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
    private const string PluginRegistryUrl = "https://kevendai.github.io/Rainmeter_todo-plugin-registry/index-v1.json";
    private static void EnableTls12()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
    }
    private static string PluginDisplayStatus(Dictionary<string,object> state)
    {
        string path=Path.Combine(PluginPaths.Jobs,"io.github.kevendai.arxiv.json");if(!File.Exists(path))return JsonUtil.String(JsonUtil.Object(JsonUtil.Get(state,"meta")),"status","就绪");
        try{Dictionary<string,object> job=JsonUtil.LoadObject(path);string value=JsonUtil.String(job,"message","");return PluginNames.Humanize(value==""?"插件状态："+JsonUtil.String(job,"state","未知"):value);}catch{return "插件状态暂不可读";}
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
    private static Color PluginSelectedBack { get { return SettingsSelected; } }
    private static Color PluginHoverBack { get { return SettingsCardHover; } }
    private static Color PluginRowBack { get { return SettingsCardBack; } }
    private static Color PluginBadgeBackOff { get { return SettingsDark ? Color.FromArgb(58, 60, 69) : Color.FromArgb(236, 241, 246); } }
    private static Color PluginBadgeBackOn { get { return SettingsDark ? Color.FromArgb(38, 78, 59) : Color.FromArgb(229, 245, 236); } }

    // 设置中心拥有独立的 Fluent 视觉层。经典模式保持原来的明亮配色；
    // 云母与亚克力使用不同的深色材质色阶，而不是只给旧界面换一个背景色。
    private static bool SettingsDark { get { return !String.Equals(UiTheme.Current, UiTheme.Classic, StringComparison.OrdinalIgnoreCase); } }
    private static bool SettingsAcrylic { get { return String.Equals(UiTheme.Current, UiTheme.Acrylic, StringComparison.OrdinalIgnoreCase); } }
    private static Color SettingsBack { get { return SettingsDark ? (SettingsAcrylic ? Color.FromArgb(24, 29, 42) : Color.FromArgb(31, 32, 38)) : Color.FromArgb(244, 247, 251); } }
    private static Color SettingsSidebar { get { return SettingsDark ? (SettingsAcrylic ? Color.FromArgb(28, 34, 49) : Color.FromArgb(25, 26, 31)) : Color.FromArgb(235, 240, 247); } }
    private static Color SettingsCardBack { get { return SettingsDark ? (SettingsAcrylic ? Color.FromArgb(45, 53, 72) : Color.FromArgb(45, 46, 54)) : Color.White; } }
    private static Color SettingsCardHover { get { return SettingsDark ? (SettingsAcrylic ? Color.FromArgb(55, 65, 87) : Color.FromArgb(54, 55, 64)) : Color.FromArgb(249, 251, 254); } }
    private static Color SettingsBorder { get { return SettingsDark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(218, 224, 233); } }
    private static Color SettingsText { get { return SettingsDark ? Color.FromArgb(244, 244, 247) : Color.FromArgb(25, 29, 36); } }
    private static Color SettingsMuted { get { return SettingsDark ? Color.FromArgb(176, 180, 192) : Color.FromArgb(100, 108, 121); } }
    private static Color SettingsAccent { get { return SettingsDark ? Color.FromArgb(220, 76, 178) : Color.FromArgb(116, 69, 205); } }
    private static Color SettingsSelected { get { return SettingsDark ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(226, 219, 246); } }

    private sealed class PluginRow
    {
        public string Title = "", Subtitle = "", Badge = "", Description = "", Category = "", Version = "", Glyph = "";
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
                TextRenderer.DrawText(g, EmptyText, PluginRowSubFont, new Rectangle(0, 0, designWidth, Math.Min(designHeight, 120)), SettingsMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
                    Color edge = selected ? SettingsAccent : SettingsBorder;
                    using (GraphicsPath path = LightUi.RoundedPath(bounds, 10))
                    {
                        using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                        using (Pen pen = new Pen(edge, selected ? 1.6F : 1F)) g.DrawPath(pen, path);
                    }
                    if (selected) using (GraphicsPath barPath = LightUi.RoundedPath(new Rectangle(bounds.Left + 6, bounds.Top + 14, 4, RowHeight - 28), 2))
                    {
                        using (SolidBrush bar = new SolidBrush(SettingsAccent)) g.FillPath(bar, barPath);
                    }
                    Color titleColor = row.Dimmed ? SettingsMuted : SettingsText;
                    TextRenderer.DrawText(g, row.Title, PluginRowTitleFont, new Rectangle(bounds.Left + 16, bounds.Top + 8, bounds.Width - 180, 22), titleColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, row.Subtitle, PluginRowSubFont, new Rectangle(bounds.Left + 16, bounds.Top + 31, bounds.Width - 180, 18), SettingsMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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

    // 插件市场使用独立的双列应用卡片，已安装页继续保留原有紧凑列表。
    private sealed class PluginMarketControl : Control
    {
        private const int CardHeight = 148, CardGap = 10, OuterPad = 1;
        public readonly List<PluginRow> AllRows = new List<PluginRow>();
        public readonly List<PluginRow> Rows = new List<PluginRow>();
        public int SelectedIndex = -1;
        public string EmptyText = "暂无匹配插件";
        public event EventHandler SelectionChanged;
        public event EventHandler RowActivated;
        public event EventHandler InstallRequested;
        private int hoverIndex = -1, scrollOffset = 0;
        private string query = "", category = "全部";
        private static readonly Font CardTitleFont = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
        private static readonly Font CardTextFont = new Font("Microsoft YaHei UI", 8.5F);
        private static readonly Font CardMetaFont = new Font("Microsoft YaHei UI", 8F);
        private static readonly Font CardIconFont = new Font(LightUi.IconFontName, 17F);

        public PluginMarketControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; TabStop = true;
        }

        public PluginRow SelectedRow { get { return SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null; } }
        private float ViewScale { get { float s = UiScale.For(this); return s > 0.01F ? s : 1F; } }
        private int DesignWidth { get { return (int)Math.Ceiling(Width / ViewScale); } }
        private int DesignHeight { get { return (int)Math.Ceiling(Height / ViewScale); } }
        private int Columns { get { return DesignWidth >= 520 ? 2 : 1; } }
        private int RowCount { get { return Rows.Count == 0 ? 0 : (Rows.Count + Columns - 1) / Columns; } }
        private int ContentHeight { get { return RowCount == 0 ? 0 : RowCount * (CardHeight + CardGap) + CardGap; } }
        private int MaxScroll { get { return Math.Max(0, ContentHeight - DesignHeight); } }
        private bool Scrollable { get { return ContentHeight > DesignHeight; } }

        public void SetRows(IEnumerable<PluginRow> rows) { AllRows.Clear(); AllRows.AddRange(rows); ApplyFilter(query, category); }
        public void ApplyFilter(string searchText, string selectedCategory)
        {
            PluginRow previous = SelectedRow; query = (searchText ?? "").Trim(); category = String.IsNullOrWhiteSpace(selectedCategory) ? "全部" : selectedCategory; Rows.Clear();
            foreach (PluginRow row in AllRows)
            {
                bool categoryMatch = category == "全部" || String.Equals(row.Category, category, StringComparison.OrdinalIgnoreCase);
                string haystack = row.Title + " " + row.Description + " " + row.Subtitle + " " + row.Category;
                if (categoryMatch && (query == "" || haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) Rows.Add(row);
            }
            SelectedIndex = previous == null ? -1 : Rows.IndexOf(previous); hoverIndex = -1; scrollOffset = 0; Invalidate(); RaiseSelectionChanged();
        }

        private Rectangle CardBounds(int index)
        {
            int columns = Columns, usable = DesignWidth - (Scrollable ? 10 : 0), cardWidth = (usable - CardGap * (columns - 1) - OuterPad * 2) / columns;
            int column = index % columns, row = index / columns;
            return new Rectangle(OuterPad + column * (cardWidth + CardGap), CardGap + row * (CardHeight + CardGap) - scrollOffset, cardWidth, CardHeight);
        }
        private Rectangle InstallBounds(Rectangle card) { return new Rectangle(card.Left + 14, card.Bottom - 34, 92, 24); }
        private int RowAt(int designX, int designY) { for (int i = 0; i < Rows.Count; i++) if (CardBounds(i).Contains(designX, designY)) return i; return -1; }
        private void EnsureVisible(int index) { Rectangle bounds = CardBounds(index); if (bounds.Top < CardGap) scrollOffset = Math.Max(0, scrollOffset + bounds.Top - CardGap); else if (bounds.Bottom > DesignHeight - CardGap) scrollOffset = Math.Min(MaxScroll, scrollOffset + bounds.Bottom - DesignHeight + CardGap); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; float scale = ViewScale; if (Math.Abs(scale - 1F) > 0.001F) g.ScaleTransform(scale, scale);
            if (Rows.Count == 0) { TextRenderer.DrawText(g, EmptyText, PluginRowSubFont, new Rectangle(0, 0, DesignWidth, Math.Min(DesignHeight, 120)), SettingsMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            for (int i = 0; i < Rows.Count; i++)
            {
                Rectangle bounds = CardBounds(i); if (bounds.Bottom < 0 || bounds.Top > DesignHeight) continue; PluginRow row = Rows[i]; bool selected = i == SelectedIndex, hover = i == hoverIndex;
                Color fill = selected ? PluginSelectedBack : hover ? PluginHoverBack : SettingsCardBack, edge = selected ? SettingsAccent : SettingsBorder;
                using (GraphicsPath path = LightUi.RoundedPath(bounds, 12)) { using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path); using (Pen pen = new Pen(edge, selected ? 1.8F : 1F)) g.DrawPath(pen, path); }
                Rectangle icon = new Rectangle(bounds.Left + 14, bounds.Top + 14, 42, 42);
                using (GraphicsPath iconPath = LightUi.RoundedPath(icon, 11)) using (SolidBrush iconBrush = new SolidBrush(SettingsDark ? Color.FromArgb(56, 58, 68) : selected ? Color.FromArgb(220, 237, 255) : Color.FromArgb(232, 244, 255))) g.FillPath(iconBrush, iconPath);
                TextRenderer.DrawText(g, row.Glyph, CardIconFont, icon, SettingsAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, row.Title, CardTitleFont, new Rectangle(bounds.Left + 66, bounds.Top + 12, bounds.Width - 80, 24), row.Dimmed ? SettingsMuted : SettingsText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, "v" + row.Version + "  ·  " + row.Category, CardMetaFont, new Rectangle(bounds.Left + 66, bounds.Top + 37, bounds.Width - 80, 18), SettingsMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, row.Description, CardTextFont, new Rectangle(bounds.Left + 14, bounds.Top + 64, bounds.Width - 28, 36), SettingsMuted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                Rectangle install = InstallBounds(bounds);
                using (GraphicsPath installPath = LightUi.RoundedPath(install, 7)) using (SolidBrush installBrush = new SolidBrush(SettingsAccent)) g.FillPath(installBrush, installPath);
                TextRenderer.DrawText(g, "安装 / 更新", PluginBadgeFont, install, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (row.Badge != "")
                {
                    Size size = TextRenderer.MeasureText(g, row.Badge, PluginBadgeFont, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding); int chipWidth = Math.Min(bounds.Width - 28, size.Width + 18); Rectangle chip = new Rectangle(bounds.Right - chipWidth - 14, bounds.Bottom - 28, chipWidth, 20);
                    using (GraphicsPath chipPath = LightUi.RoundedPath(chip, 10)) using (SolidBrush chipBrush = new SolidBrush(row.BadgeBack)) g.FillPath(chipBrush, chipPath);
                    TextRenderer.DrawText(g, row.Badge, PluginBadgeFont, chip, row.BadgeFore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
            }
            if (Scrollable) { int trackHeight = DesignHeight - 8, thumbHeight = Math.Max(32, (int)((double)DesignHeight / ContentHeight * trackHeight)); int thumbTop = 4 + (MaxScroll == 0 ? 0 : (int)((double)scrollOffset / MaxScroll * (trackHeight - thumbHeight))); using (GraphicsPath thumbPath = LightUi.RoundedPath(new Rectangle(DesignWidth - 6, thumbTop, 4, thumbHeight), 2)) using (SolidBrush thumbBrush = new SolidBrush(Color.FromArgb(140, 140, 155, 175))) g.FillPath(thumbBrush, thumbPath); }
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int index = RowAt((int)(e.X / ViewScale), (int)(e.Y / ViewScale)); if (index != hoverIndex) { hoverIndex = index; Invalidate(); } Cursor = index >= 0 ? Cursors.Hand : Cursors.Default; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); int x=(int)(e.X/ViewScale),y=(int)(e.Y/ViewScale),index=RowAt(x,y); if(index<0)return; if(index!=SelectedIndex){SelectedIndex=index;Invalidate();RaiseSelectionChanged();} if(InstallBounds(CardBounds(index)).Contains(x,y)&&InstallRequested!=null)InstallRequested(this,EventArgs.Empty); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (RowAt((int)(e.X / ViewScale), (int)(e.Y / ViewScale)) >= 0 && RowActivated != null) RowActivated(this, EventArgs.Empty); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (!Scrollable) return; scrollOffset = Math.Max(0, Math.Min(MaxScroll, scrollOffset - Math.Sign(e.Delta) * (CardHeight + CardGap))); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { if (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Left || keyData == Keys.Right) return true; return base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (Rows.Count == 0) return; int next = SelectedIndex < 0 ? 0 : SelectedIndex; if (e.KeyCode == Keys.Right) next++; else if (e.KeyCode == Keys.Left) next--; else if (e.KeyCode == Keys.Down) next += Columns; else if (e.KeyCode == Keys.Up) next -= Columns; else return; next = Math.Max(0, Math.Min(Rows.Count - 1, next)); if (next != SelectedIndex) { SelectedIndex = next; EnsureVisible(next); Invalidate(); RaiseSelectionChanged(); } }
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
            BackColor = Color.Transparent; Height = 44; Cursor = Cursors.Hand; TabStop = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = UiScale.For(this); if (scale < 0.01F) scale = 1F;
            int w = (int)Math.Ceiling(Width / scale), h = (int)Math.Ceiling(Height / scale);
            if (Math.Abs(scale - 1F) > 0.001F) g.ScaleTransform(scale, scale);
            Rectangle bounds = new Rectangle(0, 0, w - 1, h - 1);
            if (Selected || hover) using (GraphicsPath path = LightUi.RoundedPath(bounds, 7))
            {
                using (SolidBrush brush = new SolidBrush(Selected ? SettingsSelected : SettingsCardHover)) g.FillPath(brush, path);
            }
            if (Selected) using (GraphicsPath barPath = LightUi.RoundedPath(new Rectangle(4, 10, 4, h - 20), 2))
            {
                using (SolidBrush bar = new SolidBrush(SettingsAccent)) g.FillPath(bar, barPath);
            }
            Color fore = Selected ? SettingsText : hover ? SettingsText : SettingsMuted;
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
        return new SettingsNavItem { Glyph = glyph, Title = title, Text = title, Left = 14, Top = top, Width = 208, Height = 44 };
    }

    // 设置页内容卡片：圆角浅色底 + 细描边，承载一组相关设置项。
    private static Panel SettingsCard(Control parent, int x, int y, int width, int height)
    {
        Panel card = new Panel { Left = x, Top = y, Width = width, Height = height, BackColor = SettingsCardBack };
        LightUi.Round(card, 8);
        card.Paint += delegate(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int radius = Math.Max(1, (int)Math.Round(8F * UiScale.For(card)));
            using (GraphicsPath path = LightUi.RoundedPath(new Rectangle(0, 0, card.Width - 1, card.Height - 1), radius))
            using (Pen pen = new Pen(SettingsBorder, 1F)) e.Graphics.DrawPath(pen, path);
        };
        parent.Controls.Add(card); return card;
    }

    private static void ShowSettings()
    {
        ShowSettings(0);
    }

    // initialPage：0=已安装插件，1=插件市场（服务行上的「安装」按钮直接落到市场页）。
    private static void ShowSettings(int initialPage)
    {
        PluginPaths.Ensure();
        Form form=LightUi.Form("待办设置",1120,800);
        form.BackColor=SettingsBack;form.ForeColor=SettingsText;
        form.Paint+=delegate(object sender,PaintEventArgs e)
        {
            Rectangle bounds=new Rectangle(0,0,form.ClientSize.Width,form.ClientSize.Height);
            Color end=SettingsAcrylic?Color.FromArgb(37,45,65):SettingsBack;
            using(LinearGradientBrush brush=new LinearGradientBrush(bounds,SettingsBack,end,LinearGradientMode.ForwardDiagonal))e.Graphics.FillRectangle(brush,bounds);
        };
        Panel topBar=new Panel{Left=0,Top=0,Width=1120,Height=56,BackColor=SettingsSidebar};
        topBar.Paint+=delegate(object sender,PaintEventArgs e){using(Pen pen=new Pen(SettingsBorder,1F))e.Graphics.DrawLine(pen,0,topBar.Height-1,topBar.Width,topBar.Height-1);};
        topBar.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
        Label product=new Label{Text="RAINMETER DESKTOP",Left=20,Top=0,Width=240,Height=56,ForeColor=SettingsText,BackColor=Color.Transparent,Font=new Font("Microsoft YaHei UI",10F,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft};
        TextBox globalSearch=new TextBox{Left=376,Top=12,Width=368,Height=34,AutoSize=false,BorderStyle=BorderStyle.FixedSingle,BackColor=SettingsCardBack,ForeColor=SettingsText,Font=PluginNavFont};
        LightUi.SetCue(globalSearch,"搜索设置或插件");
        topBar.Controls.AddRange(new Control[]{product,globalSearch});form.Controls.Add(topBar);
        Panel sidebar=new Panel{Left=0,Top=56,Width=236,Height=744,BackColor=SettingsSidebar};
        sidebar.Paint+=delegate(object sender,PaintEventArgs e){using(Pen pen=new Pen(SettingsBorder,1F))e.Graphics.DrawLine(pen,sidebar.Width-1,0,sidebar.Width-1,sidebar.Height);};
        sidebar.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
        form.Controls.Add(sidebar);
        Label caption=new Label{Text="设置中心",Left=20,Top=22,Width=190,Height=34,ForeColor=SettingsText,BackColor=Color.Transparent,Font=new Font("Microsoft YaHei UI",18F,FontStyle.Bold)};
        caption.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
        sidebar.Controls.Add(caption);
        Label version=new Label{Text="Rainmeter Desktop Widgets\r\n"+AppVersion+" · Plugin API v1",Left=20,Top=678,Width=200,Height=42,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginVersionFont};
        sidebar.Controls.Add(version);
        Panel content=new Panel{Left=236,Top=56,Width=884,Height=744,BackColor=SettingsBack};
        content.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
        form.Controls.Add(content);
        Func<string,Panel> newPage=delegate(string title)
        {
            Panel page=new Panel{Left=0,Top=0,Width=884,Height=744,BackColor=SettingsBack,Visible=false};
            page.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
            Label pageTitle=new Label{Text=title,Left=32,Top=24,Width=620,Height=46,ForeColor=SettingsText,BackColor=Color.Transparent,Font=new Font("Microsoft YaHei UI",22F,FontStyle.Bold)};
            pageTitle.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)LightUi.BeginDrag(form);};
            Label pageSubtitle=new Label{Text=title=="已安装插件"?"管理扩展、运行状态和插件数据。":title=="插件市场"?"从本地索引浏览；刷新时才连接远端。":title=="外观"?"分别调整磁贴、窗口与材质风格。":title=="数据与维护"?"备份个人配置并维护主程序。":"版本、组件与项目信息。",Left=34,Top=86,Width=610,Height=24,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginRowSubFont};
            page.Controls.AddRange(new Control[]{pageTitle,pageSubtitle});content.Controls.Add(page);
            return page;
        };
        Panel installedPage=newPage("已安装插件");
        PluginListControl list=new PluginListControl{Left=32,Top=122,Width=514,Height=556,EmptyText="还没有安装插件。可从插件市场或本地程序包添加。"};
        Panel pluginDetail=SettingsCard(installedPage,566,122,286,556);
        Label detailEyebrow=new Label{Text="插件详情",Left=20,Top=18,Width=220,Height=20,ForeColor=SettingsAccent,BackColor=Color.Transparent,Font=PluginBadgeFont};
        Label detailTitle=new Label{Text="选择一个插件",Left=20,Top=44,Width=246,Height=58,ForeColor=SettingsText,BackColor=Color.Transparent,Font=PluginCaptionFont};
        Label status=new Label{Text="从左侧选择插件后，可在这里管理。",Left=20,Top=108,Width=246,Height=66,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginRowSubFont};
        Button toggle=LightUi.PrimaryButton("启用或禁用",20,194,246,DialogResult.None);
        Button configure=LightUi.Button("打开配置",20,242,118,DialogResult.None);
        Button run=LightUi.Button("立即运行",148,242,118,DialogResult.None);
        Button actions=LightUi.Button("更多操作",20,290,246,DialogResult.None);
        Button uninstall=LightUi.DangerButton("卸载插件程序",20,338,246,DialogResult.None);
        Button local=LightUi.Button("从本地安装",700,34,152,DialogResult.None);
        foreach(Button b in new Button[]{toggle,configure,run,actions,uninstall}){b.BackColor=SettingsCardHover;b.ForeColor=SettingsText;}
        pluginDetail.Controls.AddRange(new Control[]{detailEyebrow,detailTitle,status,toggle,configure,run,actions,uninstall});
        installedPage.Controls.AddRange(new Control[]{list,local});
        Action updateInstalledButtons=delegate{PluginRow chosen=list.SelectedRow;PluginManifest manifest=chosen==null?null:chosen.Tag as PluginManifest;bool has=manifest!=null;bool runnable=has&&!manifest.HostTooOld;toggle.Enabled=configure.Enabled=run.Enabled=runnable;actions.Enabled=uninstall.Enabled=has;detailTitle.Text=chosen==null?"选择一个插件":chosen.Title;status.Text=chosen==null?"从左侧选择插件后，可在这里管理。":chosen.Subtitle;};
        list.SelectionChanged+=delegate{updateInstalledButtons();};
        list.RowActivated+=delegate{if(configure.Enabled)configure.PerformClick();};
        Action reload=delegate{ReloadPlugins(list,status);updateInstalledButtons();};reload();
        HashSet<string> refreshedTerminalJobs=new HashSet<string>(StringComparer.Ordinal);
        System.Windows.Forms.Timer jobTimer=new System.Windows.Forms.Timer{Interval=500};
        jobTimer.Tick+=delegate
        {
            PluginRow selectedRow=list.SelectedRow;if(selectedRow==null)return;
            PluginManifest selectedManifest=selectedRow.Tag as PluginManifest;if(selectedManifest==null)return;
            try
            {
                if(!JsonUtil.Bool(PluginRuntime.Current(selectedManifest.Id),"enabled",false)){status.Text="插件已禁用";status.ForeColor=SettingsMuted;return;}
                string path=Path.Combine(PluginPaths.Jobs,selectedManifest.Id+".json");if(!File.Exists(path))return;
                Dictionary<string,object> job=JsonUtil.LoadObject(path);string state=JsonUtil.String(job,"state",""),message=PluginNames.Humanize(JsonUtil.String(job,"message",""));
                int current=JsonUtil.Int(job,"current",0),total=JsonUtil.Int(job,"total",0);
                string stateText=state=="failed"?"失败":state=="attention"?"需要确认":state=="cancelled"?"已取消":state=="running"?"运行中":state=="completed"?"已完成":state;
                string statusText=stateText+(total>0?" "+current+"/"+total:"")+(message==""?"":" · "+message);
                // 任务可能在一个定时器周期内直接从旧失败变成完成。底部状态以前会更新，
                // 但列表行仍保留打开页面时的旧文案。每个任务终态只重建一次列表，随后恢复最新状态文本。
                if(RememberTerminalJobRefresh(refreshedTerminalJobs,selectedManifest.Id,job))reload();
                status.Text=statusText;
            }
            catch{}
        };
        jobTimer.Start();form.FormClosed+=delegate{jobTimer.Stop();jobTimer.Dispose();};
        toggle.Click+=delegate{try{PluginManifest m=SelectedRunnablePlugin(list);Dictionary<string,object> c=PluginRuntime.Current(m.Id);c["enabled"]=!JsonUtil.Bool(c,"enabled",false);JsonUtil.SaveAtomic(Path.Combine(PluginPaths.PluginRoot(m.Id),"current.json"),c);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        configure.Click+=delegate{try{ShowPluginConfig(SelectedRunnablePlugin(list));reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        run.Click+=delegate{try{PluginManifest m=SelectedRunnablePlugin(list);string verb=m.Capabilities.Contains("value_provider")?"Values":m.Capabilities.Contains("todo_source")?"Sync":"";if(verb=="")throw new Exception("此插件由日历按需调用。 ");StartPluginCommand(verb,m.Id);status.Text="已启动 "+m.Name;}catch(Exception ex){LightUi.Error(ex.Message);}};
        actions.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);ContextMenuStrip menu=new ContextMenuStrip();ToolStripItem cancel=menu.Items.Add("取消当前任务");cancel.Click+=delegate{Process.Start(new ProcessStartInfo(PluginHostPath,"Cancel "+m.Id){UseShellExecute=false,CreateNoWindow=true});};ToolStripItem clear=menu.Items.Add("清除该插件创建的待办");clear.Click+=delegate{Process.Start(new ProcessStartInfo(Application.ExecutablePath,"PluginClearTasks "+m.Id){UseShellExecute=false,CreateNoWindow=true});};ToolStripItem log=menu.Items.Add("查看最近错误");log.Click+=delegate{string path=Path.Combine(PluginPaths.Logs,m.Id+".log");MessageBox.Show(File.Exists(path)?File.ReadAllText(path,RuntimeUtil.Utf8NoBom):"暂无插件日志",m.Name+" 日志",MessageBoxButtons.OK,MessageBoxIcon.Information);};
        // 规格 §5.3/§5.5：attention 的处理入口与磁贴按钮走**同一条**路径（都是 TodoHost 的确认框），
        // 免得长出第二套付费确认实现 —— 付费确认只允许有一个产生"用户已同意"的地方。
        if(PluginAttentionResumable(m.Id)){ToolStripItem resume=menu.Items.Add("处理待确认…");resume.Click+=delegate{Process.Start(new ProcessStartInfo(Application.ExecutablePath,"PluginConfirmAttention "+m.Id){UseShellExecute=false,CreateNoWindow=true});};}
        if(!String.IsNullOrWhiteSpace(m.Homepage)){ToolStripItem home=menu.Items.Add("打开主页");home.Click+=delegate{RuntimeUtil.Run(m.Homepage);};}if(m.Actions.Count>0)menu.Items.Add(new ToolStripSeparator());foreach(Dictionary<string,object> action in m.Actions){ToolStripItem item=menu.Items.Add(JsonUtil.String(action,"name",JsonUtil.String(action,"id","操作")));item.Tag=action;item.Click+=delegate(object sender,EventArgs ignored){Dictionary<string,object> selectedAction=(Dictionary<string,object>)((ToolStripItem)sender).Tag;string warning=JsonUtil.String(selectedAction,"risk","");if(JsonUtil.Bool(selectedAction,"confirm",false)&&!LightUi.Confirm((warning==""?"确定执行此操作？":warning),"插件操作"))return;Process.Start(new ProcessStartInfo(PluginHostPath,"PluginAction "+m.Id+" "+JsonUtil.String(selectedAction,"id","") ){UseShellExecute=false,CreateNoWindow=true});};}menu.Show(actions,new Point(0,actions.Height));}catch(Exception ex){LightUi.Error(ex.Message);}};
        uninstall.Click+=delegate{try{PluginManifest m=SelectedPlugin(list);if(!LightUi.Confirm("卸载 "+m.Name+" 的程序版本？插件数据默认保留。","卸载插件"))return;Directory.Delete(PluginPaths.PluginRoot(m.Id),true);if(Directory.Exists(PluginPaths.DataRoot(m.Id))&&LightUi.Confirm("程序已卸载。是否同时永久删除该插件的配置、secret、状态和缓存？","删除插件数据"))Directory.Delete(PluginPaths.DataRoot(m.Id),true);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};
        local.Click+=delegate{try{InstallLocalPlugin(form);reload();}catch(Exception ex){LightUi.Error(ex.Message);}};

        Panel marketPage=newPage("插件市场");
        TextBox marketSearch=new TextBox{Left=32,Top=116,Width=300,Height=36,AutoSize=false,BorderStyle=BorderStyle.FixedSingle,Font=PluginNavFont,BackColor=SettingsCardBack,ForeColor=SettingsText};
        Label searchHint=LightUi.Label("搜索名称、说明或类别",46,124,220);searchHint.BackColor=SettingsCardBack;searchHint.ForeColor=SettingsMuted;searchHint.Cursor=Cursors.IBeam;
        string selectedMarketCategory="全部";
        string[] categories={"全部","内容","服务","工具","其他"};
        List<Button> categoryButtons=new List<Button>();
        for(int i=0;i<categories.Length;i++){string category=categories[i];Button chip=LightUi.Button(category,344+i*68,116,64,DialogResult.None);chip.Height=36;chip.BackColor=category=="全部"?SettingsAccent:SettingsCardBack;chip.ForeColor=category=="全部"?Color.White:SettingsText;categoryButtons.Add(chip);}
        Button refresh=LightUi.PrimaryButton("刷新市场",694,114,158,DialogResult.None);refresh.Height=38;
        PluginMarketControl marketList=new PluginMarketControl{Left=32,Top=170,Width=820,Height=478,EmptyText="本地市场暂无匹配插件。点击「刷新市场」可更新索引。"};
        Label marketStatus=new Label{Text="正在读取本地市场",Left=34,Top=656,Width=630,Height=24,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginRowSubFont};
        Button installMarket=LightUi.PrimaryButton("安装或更新所选插件",674,650,178,DialogResult.None);
        LightUi.SetCue(marketSearch,"搜索插件");
        marketPage.Controls.AddRange(new Control[]{marketSearch,refresh,marketList,marketStatus,installMarket});foreach(Button chip in categoryButtons)marketPage.Controls.Add(chip);
        installMarket.Enabled=false;
        Action applyMarketFilter=delegate{marketList.ApplyFilter(marketSearch.Text,selectedMarketCategory);searchHint.Visible=marketSearch.TextLength==0&&!marketSearch.Focused;};
        marketSearch.TextChanged+=delegate{applyMarketFilter();};marketSearch.Enter+=delegate{searchHint.Visible=false;};marketSearch.Leave+=delegate{searchHint.Visible=marketSearch.TextLength==0;};searchHint.Click+=delegate{marketSearch.Focus();};
        Action paintCategories=delegate{for(int j=0;j<categoryButtons.Count;j++){categoryButtons[j].BackColor=categories[j]==selectedMarketCategory?SettingsAccent:SettingsCardBack;categoryButtons[j].ForeColor=categories[j]==selectedMarketCategory?Color.White:SettingsText;}};
        for(int i=0;i<categoryButtons.Count;i++){int index=i;categoryButtons[index].Click+=delegate{selectedMarketCategory=categories[index];paintCategories();applyMarketFilter();};categoryButtons[index].MouseEnter+=delegate{paintCategories();};categoryButtons[index].MouseLeave+=delegate{paintCategories();};}
        marketList.SelectionChanged+=delegate{string required;installMarket.Enabled=marketList.SelectedRow!=null&&MarketCompatible(marketList.SelectedRow.Tag as Dictionary<string,object>,out required);};
        marketList.RowActivated+=delegate{if(installMarket.Enabled)installMarket.PerformClick();};
        marketList.InstallRequested+=delegate{if(installMarket.Enabled)installMarket.PerformClick();};
        refresh.Click+=delegate{try{LoadMarket(marketList,marketStatus,true);}catch(Exception ex){marketStatus.Text="市场不可用："+ex.Message;marketStatus.ForeColor=LightUi.Danger;}};
        installMarket.Click+=delegate{try{PluginRow selectedMarket=marketList.SelectedRow;if(selectedMarket==null)throw new Exception("请先选择一个市场插件。");InstallMarketPlugin(selectedMarket);reload();LoadMarket(marketList,marketStatus,false);}catch(Exception ex){LightUi.Error(ex.Message);}};
        try{LoadMarket(marketList,marketStatus,false);}catch(Exception ex){marketStatus.Text="本地市场不可用："+ex.Message;marketStatus.ForeColor=LightUi.Danger;}
        Panel appearancePage=newPage("外观");
        Panel scaleCard=SettingsCard(appearancePage,32,120,820,116);
        string[] labels={"自动","75%","80%","90%","100%","110%","125%"},values={"auto","0.75","0.80","0.90","1.00","1.10","1.25"};
        Label scaleLabel=new Label{Text="桌面磁贴缩放",Left=22,Top=18,Width=210,Height=22,ForeColor=SettingsText,BackColor=Color.Transparent,Font=PluginNavFont};scaleCard.Controls.Add(scaleLabel);
        ComboBox scale=new ComboBox{Left=22,Top=52,Width=210,DropDownStyle=ComboBoxStyle.DropDownList,Font=PluginNavFont};scale.Items.AddRange(labels);int selected=Array.FindIndex(values,x=>String.Equals(x,UiScale.Mode,StringComparison.OrdinalIgnoreCase));scale.SelectedIndex=selected<0?0:selected;scaleCard.Controls.Add(scale);
        Label windowScaleLabel=new Label{Text="管理与编辑窗口缩放",Left=260,Top=18,Width=230,Height=22,ForeColor=SettingsText,BackColor=Color.Transparent,Font=PluginNavFont};scaleCard.Controls.Add(windowScaleLabel);
        ComboBox windowScale=new ComboBox{Left=260,Top=52,Width=230,DropDownStyle=ComboBoxStyle.DropDownList,Font=PluginNavFont};windowScale.Items.AddRange(labels);int windowSelected=Array.FindIndex(values,x=>String.Equals(x,UiScale.WindowMode,StringComparison.OrdinalIgnoreCase));windowScale.SelectedIndex=windowSelected<0?0:windowSelected;scaleCard.Controls.Add(windowScale);
        Button apply=LightUi.PrimaryButton("应用缩放",658,48,136,DialogResult.None);scaleCard.Controls.Add(apply);
        apply.Click+=delegate{try{UiScale.SaveMode(values[scale.SelectedIndex]);UiScale.SaveWindowMode(values[windowScale.SelectedIndex]);RenderUiScaleSkins();MessageBox.Show("磁贴缩放已应用；窗口缩放将在下次打开窗口时生效。","缩放设置");}catch(Exception ex){LightUi.Error(ex.Message);}};

        Panel themeCard=SettingsCard(appearancePage,32,252,820,112);
        Label themeLabel=new Label{Text="界面材质风格",Left=22,Top=18,Width=200,Height=22,ForeColor=SettingsText,BackColor=Color.Transparent,Font=PluginNavFont};themeCard.Controls.Add(themeLabel);
        string[] themeLabels={"经典（保留原版）","云母","亚克力"},themeValues={UiTheme.Classic,UiTheme.Mica,UiTheme.Acrylic};
        ComboBox theme=new ComboBox{Left=22,Top=50,Width=238,DropDownStyle=ComboBoxStyle.DropDownList,Font=PluginNavFont};theme.Items.AddRange(themeLabels);int themeSelected=Array.FindIndex(themeValues,x=>String.Equals(x,UiTheme.Current,StringComparison.OrdinalIgnoreCase));theme.SelectedIndex=themeSelected<0?0:themeSelected;themeCard.Controls.Add(theme);
        Label themeHint=new Label{Text="经典保留原版；云母沉稳，亚克力更通透。设置中心与磁贴会使用同一套设计语言。",Left=286,Top=24,Width=334,Height=56,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginRowSubFont};themeCard.Controls.Add(themeHint);
        Button applyTheme=LightUi.PrimaryButton("应用风格",658,46,136,DialogResult.None);themeCard.Controls.Add(applyTheme);
        applyTheme.Click+=delegate{try{UiTheme.Save(themeValues[theme.SelectedIndex]);RenderUiScaleSkins();MessageBox.Show("已应用"+UiTheme.DisplayName(themeValues[theme.SelectedIndex])+"风格。","外观设置");}catch(Exception ex){LightUi.Error(ex.Message);}};

        Panel maintenancePage=newPage("数据与维护");
        Panel backupCard=SettingsCard(maintenancePage,32,120,820,152);
        Label backupTitle=new Label{Text="加密用户配置备份",Left=22,Top=20,Width=400,Height=24,ForeColor=SettingsText,BackColor=Color.Transparent,Font=PluginNavFont};backupCard.Controls.Add(backupTitle);
        Label backupHint=new Label{Text="迁移或重装前导出配置；导入时会验证备份内容。",Left=22,Top=50,Width=500,Height=24,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginRowSubFont};backupCard.Controls.Add(backupHint);
        Button export=LightUi.PrimaryButton("导出用户配置",22,94,154,DialogResult.None),import=LightUi.Button("导入用户配置",188,94,154,DialogResult.None);
        backupCard.Controls.AddRange(new Control[]{export,import});
        export.Click+=delegate{try{string path=ExportUserBackupInteractive();if(path!="")MessageBox.Show("备份已保存：\r\n"+path,"导出完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){LightUi.Error(ex.Message);}};
        import.Click+=delegate{try{string result=ImportUserBackupInteractive();if(result!=""){RenderUiScaleSkins();MessageBox.Show(result,"导入完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}}catch(Exception ex){LightUi.Error(ex.Message);}};
        Panel updateCard=SettingsCard(maintenancePage,32,288,820,112);
        Label updateTitle=new Label{Text="主程序更新",Left=22,Top=20,Width=300,Height=24,ForeColor=SettingsText,BackColor=Color.Transparent,Font=PluginNavFont};
        Label updateHint=new Label{Text="检查新版本，并在确认后启动独立升级器。",Left=22,Top=52,Width=460,Height=24,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginRowSubFont};
        Button update=LightUi.Button("检查主程序更新",622,38,172,DialogResult.None);
        updateCard.Controls.AddRange(new Control[]{updateTitle,updateHint,update});
        update.Click+=delegate{try{UpdateCheckResult info=CheckLatestUpdate();if(!info.IsNewer)MessageBox.Show("已是最新版本："+info.Tag,"检查更新");else if(MessageBox.Show("发现 "+info.Tag+"，现在启动升级器？","检查更新",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){StartExternalUpdater();form.Close();}}catch(Exception ex){LightUi.Error(ex.Message);}};
        Panel aboutPage=newPage("关于");
        Panel aboutCard=SettingsCard(aboutPage,32,120,820,210);
        Label appName=new Label{Text="Rainmeter Desktop Widgets",Left=28,Top=28,Width=520,Height=36,ForeColor=SettingsText,BackColor=Color.Transparent,Font=new Font("Microsoft YaHei UI",17F,FontStyle.Bold)};
        Label aboutVersion=new Label{Text="版本 "+AppVersion+"  ·  Plugin API v1",Left=28,Top=76,Width=500,Height=24,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginRowSubFont};
        Label aboutCopy=new Label{Text="桌面待办、日程与扩展服务的统一管理工具。\r\n界面重构不会改变磁贴数据与插件兼容边界。",Left=28,Top=116,Width=630,Height=58,ForeColor=SettingsMuted,BackColor=Color.Transparent,Font=PluginRowSubFont};
        aboutCard.Controls.AddRange(new Control[]{appName,aboutVersion,aboutCopy});
        SettingsNavItem navInstalled=NewNavItem("\xE74C","已安装插件",92),navMarket=NewNavItem("\xE719","插件市场",142),navAppearance=NewNavItem("\xE790","外观",192),navMaintenance=NewNavItem("\xE713","数据与维护",242),navAbout=NewNavItem("\xE946","关于",292);
        sidebar.Controls.AddRange(new Control[]{navInstalled,navMarket,navAppearance,navMaintenance,navAbout});
        Panel[] pages={installedPage,marketPage,appearancePage,maintenancePage,aboutPage};
        SettingsNavItem[] navs={navInstalled,navMarket,navAppearance,navMaintenance,navAbout};
        Action<int> selectPage=delegate(int index){for(int i=0;i<pages.Length;i++){pages[i].Visible=i==index;navs[i].Selected=i==index;navs[i].Invalidate();}};
        globalSearch.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter){selectPage(1);marketSearch.Text=globalSearch.Text;marketSearch.Focus();e.SuppressKeyPress=true;}};
        for(int i=0;i<navs.Length;i++){int index=i;navs[index].Activated+=delegate{selectPage(index);};}
        selectPage(initialPage<0||initialPage>=pages.Length?0:initialPage);
        Button close=LightUi.CloseButton(form);form.Controls.Add(close);close.BringToFront();
        form.ShowDialog();
    }

    private static void ReloadPlugins(PluginListControl view,Label status)
    {
        view.Rows.Clear();int invalid=0,staleHost=0;
        foreach(string root in Directory.Exists(PluginPaths.Plugins)?Directory.GetDirectories(PluginPaths.Plugins):new string[0])
        {
            string id=Path.GetFileName(root);PluginManifest m=null;
            try{m=PluginRuntime.Resolve(id,false);}
            catch
            {
                // §10 回滚保护第 1 条：本体被降级（例如回退到 2.0.4）后，arxiv 2.0.0 这类插件会被
                // min_host_version 拒跑。这种插件**必须看得见**并说明原因，绝不能静默算成"安装损坏"。
                try{PluginManifest stale=PluginRuntime.ResolveForStatus(id);if(stale.HostTooOld)m=stale;}catch{}
            }
            if(m==null){invalid++;continue;}
            try
            {
                Dictionary<string,object> c=PluginRuntime.Current(id);
                bool enabled=JsonUtil.Bool(c,"enabled",false);
                List<string> facts=new List<string>();facts.Add("v"+m.Version);
                if(m.HostTooOld)facts.Add("需要主程序 "+m.MinHostVersion+" 或更高版本（当前 "+AppVersion+"）");
                else
                {
                    if(m.Capabilities.Count>0)facts.Add(String.Join(", ",m.Capabilities.ToArray()));
                    if(m.Permissions.Count>0)facts.Add("权限 "+String.Join(", ",m.Permissions.ToArray()));
                    string runtimeStatus=PluginRuntimeStatus(id,m,enabled);if(runtimeStatus!="")facts.Add(runtimeStatus);
                }
                PluginRow row=new PluginRow();row.Title=PluginNames.Display(id,m.Name);row.Subtitle=String.Join("  ·  ",facts.ToArray());
                if(m.HostTooOld)
                {
                    staleHost++;
                    row.Badge="版本不匹配";row.BadgeFore=LightUi.Danger;row.BadgeBack=PluginBadgeBackOff;
                }
                else
                {
                    row.Badge=enabled?"已启用":"已禁用";
                    row.BadgeFore=enabled?PluginDoneGreen:LightUi.Muted;
                    row.BadgeBack=enabled?PluginBadgeBackOn:PluginBadgeBackOff;
                }
                row.Tag=m;view.Rows.Add(row);
            }
            catch{invalid++;}
        }
        view.AfterReload();
        status.ForeColor=staleHost>0?LightUi.Danger:LightUi.Muted;
        status.Text="已安装 "+view.Rows.Count.ToString(CultureInfo.InvariantCulture)+" 个插件"
            +(staleHost>0?"；"+staleHost+" 个因主程序版本过低已暂停，请升级主程序":"")
            +(invalid>0?"；"+invalid+" 个安装损坏":"");
    }
    private static string PluginRuntimeStatus(string id,PluginManifest manifest,bool enabled)
    {
        // job 状态优先，且必须区分 attention / cancelled 与真正的失败（规格 §5.4、§11-#25）。
        if(!enabled)return "";
        // 原来这一段被 `value_provider` 短路保护着，arxiv（todo_source）根本走不到。
        string jobStatus=PluginJobStatus(id);
        if(jobStatus!="")return jobStatus;
        if(!manifest.Capabilities.Contains("value_provider"))return "";
        try
        {
            string variable="Plugin_"+System.Text.RegularExpressions.Regex.Replace(id,@"[^A-Za-z0-9_]","_")+"_server_ip";
            if(File.Exists(PluginPaths.Values))
            {
                Dictionary<string,object> values=JsonUtil.LoadObject(PluginPaths.Values),entries=JsonUtil.Object(JsonUtil.Get(values,"entries")),entry=JsonUtil.Object(JsonUtil.Get(entries,variable));
                string ip=JsonUtil.String(entry,"value","");if(ip!="")return "IP "+ip+(JsonUtil.Bool(entry,"stale",false)?"（上次成功，当前已失效）":"");
            }
        }
        catch{}
        return "尚未获取 IP";
    }
    // 有「可重新启动一次」的确认入口：attention 中等用户决定，或今天已经被拒绝过
    // （规格 §4.6-6 第 2 条要求入口必须留着）。两者都由 resume_action 撑着。
    private static bool PluginAttentionResumable(string id)
    {
        try
        {
            string path=Path.Combine(PluginPaths.Jobs,id+".json");if(!File.Exists(path))return false;
            Dictionary<string,object> job=JsonUtil.LoadObject(path);
            if(JsonUtil.String(job,"resume_action","")=="")return false;
            return JsonUtil.String(job,"state","")=="attention"||PluginRuntime.PaidDeclinedToday(id);
        }
        catch{return false;}
    }
    private static bool RememberTerminalJobRefresh(HashSet<string> refreshed,string id,Dictionary<string,object> job)
    {
        string state=JsonUtil.String(job,"state","");
        if(state!="completed"&&state!="failed"&&state!="cancelled"&&state!="attention")return false;
        string fingerprint=id+"|"+JsonUtil.String(job,"job_id","")+"|"+state+"|"+JsonUtil.String(job,"updated_at","")+"|"+JsonUtil.String(job,"message","");
        return refreshed.Add(fingerprint);
    }
    private static string PluginJobStatus(string id)
    {
        try
        {
            string path=Path.Combine(PluginPaths.Jobs,id+".json");if(!File.Exists(path))return "";
            Dictionary<string,object> job=JsonUtil.LoadObject(path);string state=JsonUtil.String(job,"state",""),message=JsonUtil.String(job,"message","");
            message=PluginNames.Humanize(message);
            if(state=="attention")return "需要确认："+(message==""?"请到磁贴处理":message);
            if(state=="cancelled")return message==""?"已取消":message;if(state=="failed")return "失败："+(message==""?"未知错误":message);
            return "";
        }
        catch{return "";}
    }
    private static PluginManifest SelectedPlugin(PluginListControl view){PluginRow row=view.SelectedRow;if(row==null)throw new Exception("请先选择一个插件。");return (PluginManifest)row.Tag;}
    // 版本不匹配的插件可以被看见、可以被选中（这是 §10 要求的可见提示），但不能被启用/配置/运行
    // ——它根本跑不起来，放行只会得到一串看不懂的异常。
    private static PluginManifest SelectedRunnablePlugin(PluginListControl view)
    {
        PluginManifest m=SelectedPlugin(view);
        if(m.HostTooOld)throw new Exception("「"+m.Name+"」需要主程序 "+m.MinHostVersion+" 或更高版本（当前 "+AppVersion+"），请先升级主程序。");
        return m;
    }

    // 设置页里的一个排版单元：一行（标签 + 控件 [+ 状态 + 按钮]），或一个分组标题。
    // 位置统一由 LayoutSettings 计算，因为「高级设置」折叠/展开时要整页重排（规格 §7.4）。
    private sealed class SettingsBlock
    {
        public Control[] Controls = new Control[0];
        public int[] Offsets = new int[0];
        public int Height;
        public bool Advanced, Header;
        public SettingsBlock(Control[] controls, int[] offsets, int height) { Controls = controls; Offsets = offsets; Height = height; }
    }

    // 按可见性重排整页；返回内容总高。分组标题行永远可见（「高级设置」标题本身就是折叠开关）。
    private static int LayoutSettings(Panel panel, List<SettingsBlock> blocks, bool showAdvanced)
    {
        int y = 8;
        foreach (SettingsBlock block in blocks)
        {
            bool visible = block.Header || showAdvanced || !block.Advanced;
            for (int i = 0; i < block.Controls.Length; i++)
            {
                block.Controls[i].Visible = visible;
                if (visible) block.Controls[i].Top = y + block.Offsets[i];
            }
            if (visible) y += block.Height;
        }
        panel.AutoScrollMinSize = new Size(0, y + 10);
        return y;
    }

    private static void ShowPluginConfig(PluginManifest manifest)
    {
        string root=PluginPaths.VersionRoot(manifest.Id,manifest.Version);if(String.IsNullOrWhiteSpace(manifest.SettingsSchema))throw new Exception("该插件没有可配置项。");
        Dictionary<string,object> settingsAction=manifest.Actions.FirstOrDefault(a=>JsonUtil.Bool(a,"settings_ui",false));
        if(settingsAction!=null){StartPluginCommand("PluginAction",manifest.Id+" "+JsonUtil.String(settingsAction,"id",""));return;}
        Dictionary<string,object> schema=JsonUtil.LoadObject(PluginManifest.SafeChildPath(root,manifest.SettingsSchema,"设置 Schema"));Dictionary<string,object> props=JsonUtil.Object(JsonUtil.Get(schema,"properties"));
        string data=PluginPaths.DataRoot(manifest.Id);Directory.CreateDirectory(data);string configPath=Path.Combine(data,"config.json"),secretPath=Path.Combine(data,"secret.dat");
        Dictionary<string,object> config=File.Exists(configPath)?JsonUtil.LoadObject(configPath):new Dictionary<string,object>();Dictionary<string,object> secret=File.Exists(secretPath)?JsonUtil.ReadDpapiJson(secretPath):new Dictionary<string,object>();
        string oldConfig=JsonUtil.Serialize(config),oldSecret=JsonUtil.Serialize(secret);HashSet<string> required=new HashSet<string>(JsonUtil.Array(JsonUtil.Get(schema,"required")).Select(Convert.ToString),StringComparer.OrdinalIgnoreCase);AddressProviderBinding claimedProvider=String.IsNullOrWhiteSpace(manifest.AddressTarget)?null:DynamicPluginValues.AddressProvider(manifest.AddressTarget);
        Dictionary<string,string> addressStored=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        List<SettingsField> fields=PluginSettingsLayout.Parse(props);List<SettingsSection> sections=PluginSettingsLayout.Group(fields);
        // 服务行状态一次算好：x-service 行的下拉框、x-requires 的置灰都读它。
        Dictionary<string,ServiceRowState> services=new Dictionary<string,ServiceRowState>(StringComparer.OrdinalIgnoreCase);
        foreach(SettingsField candidate in fields)
        {
            string service=PluginSettingsLayout.IsServiceField(candidate)?candidate.Service:candidate.Requires;
            if(String.IsNullOrWhiteSpace(service)||services.ContainsKey(service))continue;
            SettingsField row=fields.FirstOrDefault(x=>PluginSettingsLayout.IsServiceField(x)&&String.Equals(x.Service,service,StringComparison.OrdinalIgnoreCase));
            services[service]=PluginSettingsLayout.Inspect(manifest,row==null?new SettingsField{Service=service}:row);
        }
        List<Action> refreshServices=new List<Action>();
        Func<string,string> fixHint=delegate(string reason){if(reason==ServiceRegistry.ReasonDisabled)return "点此启用";if(reason==ServiceRegistry.ReasonNotInstalled)return "点此安装";return "点此处理";};
        Action<string> fixService=delegate(string service)
        {
            ServiceRowState state;if(!services.TryGetValue(service,out state)||state==null)return;
            try
            {
                if(state.NeedsEnable)
                {
                    Dictionary<string,object> current=PluginRuntime.Current(state.DisabledProviderId);
                    if(current.Count==0)throw new Exception("找不到插件 "+state.DisabledProviderId+"。");
                    current["enabled"]=true;JsonUtil.SaveAtomic(Path.Combine(PluginPaths.PluginRoot(state.DisabledProviderId),"current.json"),current);
                    ServiceRegistry.Audit("settings-enable "+state.DisabledProviderId+" (from "+manifest.Id+" settings)");
                }
                else ShowSettings(1);   // 没装 ⇒ 直接去插件市场；装完回来就地刷新
                foreach(Action refresh in refreshServices)refresh();
            }
            catch(Exception ex){LightUi.Error(ex.Message);}
        };
        string displayName=PluginNames.Display(manifest.Id,manifest.Name);
        Form f=LightUi.Form(displayName+" 设置",680,Math.Max(360,Math.Min(820,190+fields.Count*84)));LightUi.Heading(f,displayName,"敏感项使用当前 Windows 用户 DPAPI 加密。","settings.svg");
        Panel panel=new Panel{Left=24,Top=102,Width=630,Height=200,AutoScroll=true};f.Controls.Add(panel);
        List<SettingsBlock> blocks=new List<SettingsBlock>();Dictionary<string,Control> controls=new Dictionary<string,Control>();Dictionary<string,Func<string>> serviceValue=new Dictionary<string,Func<string>>();
        bool advancedExpanded=false;Label advancedToggle=null;
        Action<bool> setAdvanced=delegate(bool expanded)
        {
            advancedExpanded=expanded;
            if(advancedToggle!=null)advancedToggle.Text=(expanded?"\u25BE ":"\u25B8 ")+PluginSettingsLayout.AdvancedSection;
            int content=LayoutSettings(panel,blocks,expanded);
            f.Height=Math.Min(820,Math.Max(360,content+200));panel.Height=f.ClientSize.Height-180;
            LayoutSettings(panel,blocks,expanded);
        };
        int plainSections=sections.Count(x=>!x.Advanced);
        foreach(SettingsSection section in sections)
        {
            if(section.Advanced)
            {
                advancedToggle=LightUi.Label("\u25B8 "+PluginSettingsLayout.AdvancedSection,8,0,590);
                advancedToggle.Font=LightUi.UiFont(10F,FontStyle.Bold);advancedToggle.ForeColor=LightUi.Accent;advancedToggle.Cursor=Cursors.Hand;advancedToggle.Height=24;
                advancedToggle.Click+=delegate{setAdvanced(!advancedExpanded);};
                panel.Controls.Add(advancedToggle);blocks.Add(new SettingsBlock(new Control[]{advancedToggle},new int[]{0},36){Header=true});
            }
            else if(plainSections>1)
            {
                Label header=LightUi.Label(section.Name,8,0,590);header.Font=LightUi.UiFont(10F,FontStyle.Bold);header.ForeColor=LightUi.Text;header.Height=24;
                panel.Controls.Add(header);blocks.Add(new SettingsBlock(new Control[]{header},new int[]{0},36));
            }
            foreach(SettingsField field in section.Fields)
            {
                bool claimedAddress=claimedProvider!=null&&field.Address;
                object existing=PluginSettingValue(manifest.Id,field.Key,field.Secret?secret:config,secret,field.Property);if(claimedAddress)addressStored[field.Key]=Convert.ToString(existing??"",CultureInfo.InvariantCulture);
                if(claimedAddress)existing=DynamicPluginValues.BindForTarget(addressStored[field.Key],manifest.AddressTarget);
                string requiresReason="";bool requiresOk=field.Requires==""||PluginSettingsLayout.RequiresSatisfied(manifest,field.Requires,out requiresReason);
                string title=field.Title;
                if(!requiresOk)title=title+"（需要「"+PluginSettingsLayout.ServiceLabel(fields,field.Requires)+"」，"+fixHint(requiresReason)+"）";
                if(claimedAddress)title=title+"（主机由“"+claimedProvider.PluginName+"”提供；端口和路径可修改）";
                Label label=LightUi.Label(title,8,0,590);panel.Controls.Add(label);Control control;
                if(PluginSettingsLayout.IsServiceField(field))
                {
                    ComboBox box=new ComboBox{Left=8,Top=0,Width=396,DropDownStyle=ComboBoxStyle.DropDownList};
                    Label hint=LightUi.Label("",412,5,134);hint.Cursor=Cursors.Hand;
                    Button fix=LightUi.Button("",552,0,78,DialogResult.None);fix.Height=28;fix.Visible=false;
                    // 一行上的三个动作都走同一个入口：要么启用已装未启用的 provider，要么去市场装。
                    // 用户点「安装 / 启用」之后本页就地刷新，不需要关掉重开（规格 §8 的设置页行为）。
                    fix.Click+=delegate{fixService(field.Service);};
                    hint.Click+=delegate{if(fix.Visible)fixService(field.Service);};
                    List<string> ids=new List<string>();bool populated=false;
                    Action refresh=delegate
                    {
                        SettingsField rowField=fields.FirstOrDefault(x=>PluginSettingsLayout.IsServiceField(x)&&String.Equals(x.Service,field.Service,StringComparison.OrdinalIgnoreCase));
                        ServiceRowState fresh=PluginSettingsLayout.Inspect(manifest,rowField==null?field:rowField);services[field.Service]=fresh;
                        string keep=populated&&ids.Count>0&&box.SelectedIndex>=0&&box.SelectedIndex<ids.Count?ids[box.SelectedIndex]:null;
                        // 空选择回落到刚解析出来的 provider：用户点了「安装 / 启用」之后，这一行必须显示
                        // "现在真的会用哪个"，否则界面上还停在「不使用」，与宿主实际解析结果不一致。
                        string wanted=String.IsNullOrEmpty(keep)?fresh.Bound:keep;
                        ids.Clear();box.Items.Clear();ids.Add("");box.Items.Add("（不使用）");
                        if(fresh.Bound!=""&&!fresh.Candidates.Any(x=>String.Equals(x.Id,fresh.Bound,StringComparison.OrdinalIgnoreCase)))
                        {ids.Add(fresh.Bound);box.Items.Add("已失效："+(fresh.BoundName==""?fresh.Bound:fresh.BoundName)+"（"+(fresh.ReasonText==""?"不可用":fresh.ReasonText)+"）");}
                        foreach(ServiceCandidate candidate in fresh.Candidates)
                        {
                            string text=candidate.Name==""?candidate.Id:candidate.Name;
                            if(String.Equals(candidate.Billing,"may_charge",StringComparison.OrdinalIgnoreCase))text=text+"（可能收费）";
                            ids.Add(candidate.Id);box.Items.Add(text);
                        }
                        int index=ids.FindIndex(x=>String.Equals(x,wanted==null?"":wanted,StringComparison.OrdinalIgnoreCase));box.SelectedIndex=index<0?0:index;populated=true;
                        if(!fresh.Declared){box.Enabled=false;hint.Text="插件未声明该依赖";hint.ForeColor=LightUi.Danger;fix.Visible=false;return;}
                        box.Enabled=true;fix.Visible=fresh.InstallNeeded||fresh.NeedsEnable;fix.Text=fresh.NeedsEnable?"启用":"安装";
                        if(fresh.Ambiguous)hint.Text="有多个候选，请选择";
                        else hint.Text=fresh.Available?(fresh.Optional?"已启用":"已启用（必需）"):(fresh.ReasonText==""?"请选择":fresh.ReasonText);
                        hint.ForeColor=fresh.Available?LightUi.Done:LightUi.Danger;
                    };
                    refreshServices.Add(refresh);refresh();
                    serviceValue[field.Key]=delegate{return ids.Count>0&&box.SelectedIndex>=0&&box.SelectedIndex<ids.Count&&services[field.Service].Declared?ids[box.SelectedIndex]:"";};
                    panel.Controls.Add(box);panel.Controls.Add(hint);panel.Controls.Add(fix);
                    blocks.Add(new SettingsBlock(new Control[]{label,box,hint,fix},new int[]{0,28,33,28},84){Advanced=field.Advanced});
                    controls[field.Key]=box;continue;
                }
                if(field.Type=="boolean")control=new CheckBox{Left=8,Top=0,Width=590,Checked=existing!=null&&Convert.ToBoolean(existing,CultureInfo.InvariantCulture),Text="启用",Font=LightUi.UiFont(10F)};
                else if(field.Type=="enum"){ComboBox enumBox=new ComboBox{Left=8,Top=0,Width=590,DropDownStyle=ComboBoxStyle.DropDownList};foreach(object option in JsonUtil.Array(JsonUtil.Get(field.Property,"enum")))enumBox.Items.Add(Convert.ToString(option));enumBox.SelectedItem=Convert.ToString(existing);control=enumBox;}
                else control=new TextBox{Left=8,Top=0,Width=590,Height=field.Type=="multiline"?86:28,Multiline=field.Type=="multiline",ScrollBars=field.Type=="multiline"?ScrollBars.Vertical:ScrollBars.None,Text=existing==null?"":Convert.ToString(existing,CultureInfo.InvariantCulture),UseSystemPasswordChar=field.Secret};
                if(claimedAddress){TextBox claimedBox=control as TextBox;if(claimedBox!=null){claimedBox.BackColor=Color.FromArgb(244,249,253);}}
                if(!requiresOk)
                {
                    // 无可用 provider ⇒ 控件置灰，但**绝不改存储值**（规格 §11-#7/#8：卸载 provider 后开关必须保持原样）。
                    control.Enabled=false;TextBox disabledText=control as TextBox;if(disabledText!=null){disabledText.ReadOnly=true;disabledText.BackColor=Color.FromArgb(238,238,238);}
                    label.ForeColor=LightUi.Accent;label.Cursor=Cursors.Hand;label.Click+=delegate{fixService(field.Requires);};
                }
                panel.Controls.Add(control);
                int controlHeight=field.Type=="multiline"?86:28;
                blocks.Add(new SettingsBlock(new Control[]{label,control},new int[]{0,28},28+controlHeight+28){Advanced=field.Advanced});
                controls[field.Key]=control;
            }
        }
        if(blocks.Count==0)throw new Exception("该插件没有可配置项。");
        int contentHeight=LayoutSettings(panel,blocks,false);
        f.Height=Math.Min(820,Math.Max(360,contentHeight+200));panel.Height=f.ClientSize.Height-180;
        LayoutSettings(panel,blocks,false);
        Dictionary<string,SettingsField> fieldsByKey=new Dictionary<string,SettingsField>(StringComparer.OrdinalIgnoreCase);
        foreach(SettingsField indexed in fields)fieldsByKey[indexed.Key]=indexed;
        Button cancel=LightUi.Button("取消",400,f.ClientSize.Height-60,120,DialogResult.Cancel);Button save=LightUi.PrimaryButton("保存",532,f.ClientSize.Height-60,120,DialogResult.OK);f.Controls.AddRange(new Control[]{cancel,save});f.CancelButton=cancel;
        if(f.ShowDialog()!=DialogResult.OK)return;
        List<KeyValuePair<string,string>> selections=new List<KeyValuePair<string,string>>();
        foreach(KeyValuePair<string,Control> pair in controls)
        {
            SettingsField field=fieldsByKey[pair.Key];Dictionary<string,object> p=field.Property;
            if(PluginSettingsLayout.IsServiceField(field))
            {
                // 服务行的值属于绑定表，**不写 config**（规格 §3：绑定表由宿主独占写入）。插件没声明的依赖连绑定都不写。
                if(!PluginSettingsLayout.DeclaresService(manifest,field.Service))continue;
                Func<string> read;if(serviceValue.TryGetValue(pair.Key,out read))selections.Add(new KeyValuePair<string,string>(field.Service,read()));
                continue;
            }
            object value;
            if(field.Type=="boolean")value=((CheckBox)pair.Value).Checked;
            else if(field.Type=="integer"){int number;if(!Int32.TryParse(pair.Value.Text,out number))throw new Exception(field.Title+"必须是整数。");int min=JsonUtil.Int(p,"minimum",Int32.MinValue),max=JsonUtil.Int(p,"maximum",Int32.MaxValue);if(number<min||number>max)throw new Exception(field.Title+"超出允许范围。");value=number;}
            else value=pair.Value.Text;
            // 置灰的 x-requires 行原样写回：卸载 provider 不得把用户的开关悄悄改成 false（规格 §11-#8）。
            if(required.Contains(pair.Key)&&String.IsNullOrWhiteSpace(Convert.ToString(value,CultureInfo.InvariantCulture)))throw new Exception(field.Title+"为必填项。");
            if(claimedProvider!=null&&field.Address)value=DynamicPluginValues.MergeAddressEdit(addressStored.ContainsKey(field.Key)?addressStored[field.Key]:"",Convert.ToString(value,CultureInfo.InvariantCulture),manifest.AddressTarget);
            (field.Secret?secret:config)[pair.Key]=value;
        }
        JsonUtil.SaveAtomic(configPath,config);JsonUtil.WriteDpapiJson(secretPath,secret);
        // config 与绑定表必须一起成功或一起失败，否则会留下"设置没保存、绑定却生效"的半截状态。
        List<KeyValuePair<string,string>> previousBindings=new List<KeyValuePair<string,string>>();
        foreach(KeyValuePair<string,string> selection in selections)previousBindings.Add(new KeyValuePair<string,string>(selection.Key,ServiceRegistry.BoundProvider(manifest.Id,selection.Key)));
        int boundChanges=PluginSettingsLayout.ApplyBindings(manifest.Id,selections);
        try{using(Process validation=Process.Start(new ProcessStartInfo(PluginHostPath,"PluginAction "+manifest.Id+" validate_settings"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){if(validation==null||!validation.WaitForExit(35000)||validation.ExitCode!=0)throw new Exception("插件拒绝了当前设置。");}}catch{JsonUtil.SaveAtomic(configPath,JsonUtil.Object(JsonUtil.Deserialize(oldConfig)));JsonUtil.WriteDpapiJson(secretPath,JsonUtil.Object(JsonUtil.Deserialize(oldSecret)));if(boundChanges>0)PluginSettingsLayout.ApplyBindings(manifest.Id,previousBindings);throw;}
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
        RunPluginInstaller(open.FileName,"");MessageBox.Show("插件安装成功。默认保持禁用，请检查权限后手动启用。","插件安装",MessageBoxButtons.OK,MessageBoxIcon.Information);
    }

    private static void RunPluginInstaller(string package,string expectedSha)
    {
        PluginPackageInstaller.Install(package,PluginPaths.Plugins,AppVersion,expectedSha);
    }
    private static void InstallMarketPlugin(PluginRow row)
    {
        Dictionary<string,object> record=row.Tag as Dictionary<string,object>;if(record==null||!JsonUtil.Bool(record,"official",false))throw new Exception("仅允许安装官方市场条目。");string required;if(!MarketCompatible(record,out required))throw new Exception("该插件需要主程序 "+required+" 或更高版本。");string url=JsonUtil.String(record,"download",""),sha=JsonUtil.String(record,"sha256","");Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||!uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||uri.AbsolutePath.IndexOf("/releases/download/",StringComparison.OrdinalIgnoreCase)<0)throw new Exception("市场下载地址必须是 GitHub HTTPS Release。");if(!System.Text.RegularExpressions.Regex.IsMatch(sha,@"^[a-fA-F0-9]{64}$"))throw new Exception("市场条目缺少有效 SHA256。");
        EnableTls12();string temporary=Path.Combine(Path.GetTempPath(),"rw-market-"+Guid.NewGuid().ToString("N")+".rwplugin");try{using(WebClient web=new WebClient()){web.Headers[HttpRequestHeader.UserAgent]="RainmeterDesktopWidgets/"+AppVersion;web.DownloadFile(uri,temporary);}RunPluginInstaller(temporary,sha);}finally{try{File.Delete(temporary);}catch{}}
    }

    private static void LoadMarket(PluginMarketControl view,Label status,bool refreshRemote)
    {
        string bundledPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"plugin-registry-v1.json");
        Exception refreshError=null;
        if(refreshRemote)
        {
            try
            {
                EnableTls12();
                using(WebClient web=new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent]="RainmeterDesktopWidgets/"+AppVersion;
                    string downloaded=PluginMarketCache.Decode(web.DownloadData(PluginRegistryUrl));
                    PluginMarketCache.Save(PluginPaths.RegistryCache,downloaded);
                }
            }
            catch(Exception ex){refreshError=ex;}
        }
        string sourcePath,sourceLabel;
        string json=PluginMarketCache.LoadLocal(PluginPaths.RegistryCache,bundledPath,out sourcePath,out sourceLabel);
        Dictionary<string,object> index=JsonUtil.Object(JsonUtil.Deserialize(json));List<PluginRow> marketRows=new List<PluginRow>();
        foreach(object raw in JsonUtil.Array(JsonUtil.Get(index,"plugins")))
        {
            Dictionary<string,object> p=JsonUtil.Object(raw);if(!JsonUtil.Bool(p,"official",false))continue;
            string id=JsonUtil.String(p,"id","");if(id=="io.github.kevendai.network-ip")continue;string required;bool compatible=MarketCompatible(p,out required),installedPlugin=Directory.Exists(PluginPaths.PluginRoot(id));
            PluginRow row=new PluginRow();row.Title=PluginNames.Display(id,JsonUtil.String(p,"name",id));row.Tag=p;
            string permissions=JsonUtil.String(p,"permissions",""),description=JsonUtil.String(p,"description","");
            row.Version=JsonUtil.String(p,"version","");row.Description=MarketDescription(id,description);row.Category=MarketCategory(id,JsonUtil.String(p,"capability",""));row.Glyph=MarketGlyph(row.Category);row.Subtitle=JsonUtil.String(p,"capability","")+(permissions==""?"":"  ·  权限 "+permissions);
            if(!compatible){row.Badge="需要主程序 "+required;row.BadgeFore=LightUi.Muted;row.BadgeBack=PluginBadgeBackOff;row.Dimmed=true;}
            else if(installedPlugin){row.Badge="已安装";row.BadgeFore=PluginDoneGreen;row.BadgeBack=PluginBadgeBackOn;}
            else{row.Badge="可安装";row.BadgeFore=LightUi.Accent;row.BadgeBack=Color.FromArgb(232,244,255);}
            marketRows.Add(row);
        }
        view.SetRows(marketRows);
        DateTime cacheTime=File.Exists(sourcePath)?File.GetLastWriteTime(sourcePath):DateTime.Now;
        status.ForeColor=refreshError==null?LightUi.Muted:LightUi.Danger;
        status.Text=(refreshError==null?(refreshRemote?"已从远端更新；":""):"远端刷新失败，")+sourceLabel+"索引时间 "+cacheTime.ToString("yyyy-MM-dd HH:mm")+(refreshError==null?"":"；"+refreshError.Message);
    }
    private static string MarketDescription(string id,string fallback)
    {
        if(id=="io.github.kevendai.arxiv")return "按关键词抓取并筛选 arXiv 论文，生成每日推荐。";
        if(id=="io.github.kevendai.calendar-to-todo")return "把日程按规则转换为待办，保留来源与时间信息。";
        if(id=="io.github.kevendai.paper-snapshot-sync")return "把论文推荐快照同步到文件服务器，支持多台设备共享。";
        if(id=="io.github.kevendai.ai-deepseek")return "提供 DeepSeek AI 评分服务，供论文等插件生成结构化结果。";
        if(id=="io.github.kevendai.translate-tencent")return "提供腾讯云机器翻译服务，支持其他插件批量翻译文本。";
        if(id=="io.github.kevendai.ssdp-server-ip")return "自动发现服务器 IP，同时保留用户手动设置的端口。";
        return String.IsNullOrWhiteSpace(fallback)?"提供扩展功能与桌面联动。":fallback;
    }

    private static int UiMarketRefresh(string resultPath)
    {
        try
        {
            EnableTls12();
            using(WebClient web=new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent]="RainmeterDesktopWidgets/"+AppVersion;
                string downloaded=PluginMarketCache.Decode(web.DownloadData(PluginRegistryUrl));
                PluginMarketCache.Save(PluginPaths.RegistryCache,downloaded);
            }
            JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",true}});
            return 0;
        }
        catch(Exception ex)
        {
            try{JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",false},{"error",ex.Message}});}catch{}
            return 1;
        }
    }

    private static int UiMarketInstall(string pluginId,string resultPath)
    {
        try
        {
            string sourcePath,sourceLabel;
            string bundledPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"plugin-registry-v1.json");
            string json=PluginMarketCache.LoadLocal(PluginPaths.RegistryCache,bundledPath,out sourcePath,out sourceLabel);
            Dictionary<string,object> index=JsonUtil.Object(JsonUtil.Deserialize(json));
            Dictionary<string,object> record=JsonUtil.Array(JsonUtil.Get(index,"plugins"))
                .Select(JsonUtil.Object).FirstOrDefault(p=>JsonUtil.String(p,"id","")==pluginId&&JsonUtil.Bool(p,"official",false));
            if(record==null)throw new Exception("本地市场中找不到该官方插件，请先刷新市场。");
            PluginRow row=new PluginRow();row.Tag=record;
            InstallMarketPlugin(row);
            JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",true}});
            return 0;
        }
        catch(Exception ex)
        {
            try{JsonUtil.SaveAtomic(resultPath,new Dictionary<string,object>{{"ok",false},{"error",ex.Message}});}catch{}
            return 1;
        }
    }
    private static string MarketCategory(string id,string capability)
    {
        string value=(id+" "+capability).ToLowerInvariant();
        if(value.Contains("arxiv")||value.Contains("calendar")||value.Contains("todo_source"))return "内容";
        if(value.Contains("ai")||value.Contains("translate")||value.Contains("snapshot")||value.Contains("service"))return "服务";
        if(value.Contains("ssdp")||value.Contains("network")||value.Contains("value_provider"))return "工具";
        return "其他";
    }
    private static string MarketGlyph(string category){return category=="内容"?"\xE8A5":category=="服务"?"\xE950":category=="工具"?"\xE713":"\xE719";}
    private static bool MarketCompatible(Dictionary<string,object> record,out string required){required=record==null?"":JsonUtil.String(record,"min_host_version","");Version host,min;return Version.TryParse(AppVersion,out host)&&Version.TryParse(required,out min)&&host.CompareTo(min)>=0;}
}
