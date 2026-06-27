using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace RainmeterBackend
{
    internal static class JsonUtil
    {
        private static JavaScriptSerializer NewSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        public static Dictionary<string, object> Object(object value)
        {
            return value as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public static List<object> Array(object value)
        {
            object[] array = value as object[];
            if (array != null) return array.ToList();
            ArrayList list = value as ArrayList;
            if (list != null) return list.Cast<object>().ToList();
            IEnumerable<object> enumerable = value as IEnumerable<object>;
            return enumerable == null ? new List<object>() : enumerable.ToList();
        }

        public static object Get(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result : null;
        }

        public static string String(Dictionary<string, object> value, string key, string fallback)
        {
            object result = Get(value, key);
            return result == null ? fallback : Convert.ToString(result, CultureInfo.InvariantCulture) ?? fallback;
        }

        public static bool Bool(Dictionary<string, object> value, string key, bool fallback)
        {
            object result = Get(value, key);
            if (result is bool) return (bool)result;
            bool parsed;
            return result != null && Boolean.TryParse(Convert.ToString(result), out parsed) ? parsed : fallback;
        }

        public static int Int(Dictionary<string, object> value, string key, int fallback)
        {
            object result = Get(value, key);
            int parsed;
            return result != null && Int32.TryParse(Convert.ToString(result, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        public static Dictionary<string, object> LoadObject(string path)
        {
            return Object(NewSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)));
        }

        public static object Deserialize(string json)
        {
            return NewSerializer().DeserializeObject(json);
        }

        public static string Serialize(object value)
        {
            return NewSerializer().Serialize(value);
        }

        public static void SaveAtomic(string path, object value)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public static Dictionary<string, object> ReadDpapiJson(string path)
        {
            byte[] cipher = Convert.FromBase64String(File.ReadAllText(path).Trim());
            byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Object(Deserialize(Encoding.UTF8.GetString(plain)));
        }

        public static void WriteDpapiJson(string path, object value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!System.String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            byte[] plain = Encoding.UTF8.GetBytes(Serialize(value));
            byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllText(path, Convert.ToBase64String(cipher), new UTF8Encoding(false));
        }
    }

    internal static class RuntimeUtil
    {
        public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static DateTimeOffset? Date(Dictionary<string, object> value, string key)
        {
            DateTimeOffset parsed;
            string text = JsonUtil.String(value, key, "");
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed) ? parsed : (DateTimeOffset?)null;
        }

        public static string Iso(DateTimeOffset value) { return value.ToString("o", CultureInfo.InvariantCulture); }

        public static string CleanRainmeter(string value)
        {
            return (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'").Replace("#", "﹟").Trim();
        }

        public static bool WriteUtf16IfChanged(string path, string text)
        {
            UnicodeEncoding encoding = new UnicodeEncoding(false, true);
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(text);
            byte[] bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) return false;
            File.WriteAllBytes(path, bytes);
            return true;
        }

        public static string FindRainmeter()
        {
            string[] candidates = {
                @"D:\Program Files (x86)\Rainmeter\Rainmeter.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rainmeter", "Rainmeter.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Rainmeter", "Rainmeter.exe")
            };
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }

        public static void Run(string target)
        {
            if (String.IsNullOrWhiteSpace(target)) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch { }
        }

        public static void Refresh(string config)
        {
            string exe = FindRainmeter();
            if (!File.Exists(exe)) return;
            // Rainmeter parses the bang from the raw command line. Quoting the
            // bang itself makes portable instances treat it as a normal launch,
            // leaving a second headless Rainmeter process instead of forwarding
            // the command to the visible instance.
            Process process = Process.Start(new ProcessStartInfo(exe, "!Refresh \"" + config + "\"") { UseShellExecute = false, CreateNoWindow = true });
            if (process != null && !process.WaitForExit(2000))
            {
                // A forwarded command exits immediately. Never allow a failed
                // refresh command to accumulate as another Rainmeter instance.
                try { process.Kill(); } catch { }
            }
        }

        public static string Sha256Hex(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        public static byte[] Hmac(byte[] key, string value)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key)) return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        }
    }

    internal static class LightUi
    {
        public static readonly Color Back = Color.FromArgb(230, 242, 252);
        public static readonly Color Panel = Color.FromArgb(246, 251, 255);
        public static readonly Color Surface = Color.FromArgb(238, 247, 254);
        public static readonly Color Border = Color.FromArgb(198, 216, 232);
        public static readonly Color Text = Color.FromArgb(21, 32, 48);
        public static readonly Color Muted = Color.FromArgb(92, 108, 130);
        public static readonly Color Accent = Color.FromArgb(47, 132, 235);
        public static readonly Color AccentFill = Color.FromArgb(50, 136, 236);
        public static readonly Color Danger = Color.FromArgb(238, 69, 78);

        private sealed class StyledForm : Form
        {
            public StyledForm()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                UpdateStyles();
            }

            protected override CreateParams CreateParams
            {
                get { CreateParams value = base.CreateParams; value.ClassStyle |= 0x00020000; value.ExStyle |= 0x02000000; return value; }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left) BeginDrag(this);
            }
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

        public static void EnableDoubleBuffer(Control control)
        {
            if (control == null) return;
            try
            {
                PropertyInfo property = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (property != null) property.SetValue(control, true, null);
            }
            catch { }
        }

        public static void SetRedraw(Control control, bool enabled)
        {
            if (control == null || !control.IsHandleCreated) return;
            SendMessage(control.Handle, 0x000B, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            if (enabled) control.Invalidate(true);
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void ApplyRoundedRegion(Form form)
        {
            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, form.Width, form.Height), 14))
            {
                Region previous = form.Region;
                form.Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        public static void Round(Control control, int radius)
        {
            Action apply = delegate {
                if (control.Width <= 1 || control.Height <= 1) return;
                using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, control.Width, control.Height), radius))
                {
                    Region previous = control.Region;
                    control.Region = new Region(path);
                    if (previous != null) previous.Dispose();
                }
            };
            control.HandleCreated += delegate { apply(); };
            control.Resize += delegate { apply(); };
            if (control.IsHandleCreated) apply();
        }

        public static void BeginDrag(Form form)
        {
            ReleaseCapture();
            SendMessage(form.Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
        }

        public static Form Form(string title, int width, int height)
        {
            Form form = new StyledForm();
            form.Text = title;
            form.Width = width;
            form.Height = height;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.BackColor = Back;
            form.ForeColor = Text;
            form.Font = new Font("Microsoft YaHei UI", 9F);
            form.FormBorderStyle = FormBorderStyle.None;
            form.MaximizeBox = false;
            form.MinimizeBox = true;
            form.ShowInTaskbar = true;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.Padding = new Padding(1);
            form.Opacity = 0D;
            form.Shown += delegate {
                form.BeginInvoke(new Action(delegate {
                    if (form.IsDisposed) return;
                    form.Refresh();
                    form.Opacity = 1D;
                }));
            };
            form.Shown += delegate { ApplyRoundedRegion(form); };
            if (Environment.GetEnvironmentVariable("RAINMETER_UI_SMOKE") == "1")
                form.Shown += delegate { form.BeginInvoke(new Action(form.Close)); };
            form.Resize += delegate { ApplyRoundedRegion(form); };
            form.Paint += delegate(object sender, PaintEventArgs e) {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, form.Width - 1, form.Height - 1);
                using (GraphicsPath path = RoundedPath(bounds, 18))
                using (LinearGradientBrush brush = new LinearGradientBrush(bounds, Color.FromArgb(247, 252, 255), Color.FromArgb(226, 242, 253), LinearGradientMode.ForwardDiagonal))
                using (Pen pen = new Pen(Color.FromArgb(235, 246, 255), 1F))
                {
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(pen, path);
                }
            };
            return form;
        }

        private static Control SvgIcon(string iconFile, int x, int y, int size)
        {
            Panel box = new Panel { Left = x, Top = y, Width = size, Height = size, BackColor = Color.FromArgb(238, 245, 252) };
            Round(box, 10);
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", iconFile ?? "");
            if (File.Exists(path))
            {
                WebBrowser browser = new WebBrowser { Left = 7, Top = 7, Width = size - 14, Height = size - 14, ScrollBarsEnabled = false, IsWebBrowserContextMenuEnabled = false, AllowWebBrowserDrop = false, WebBrowserShortcutsEnabled = false };
                browser.TabStop = false;
                browser.DocumentText = "<html><head><meta http-equiv='X-UA-Compatible' content='IE=edge'></head><body style='margin:0;overflow:hidden;background:#eef5fc;'><img src='file:///" + path.Replace("\\", "/") + "' style='width:100%;height:100%;display:block;'/></body></html>";
                box.Controls.Add(browser);
            }
            else
            {
                Label fallback = new Label { Text = "□", Left = 0, Top = 0, Width = size, Height = size, BackColor = Color.Transparent, ForeColor = Accent, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold) };
                box.Controls.Add(fallback);
            }
            return box;
        }

        public static void Heading(Form form, string title, string subtitle)
        {
            Heading(form, title, subtitle, null);
        }

        public static void Heading(Form form, string title, string subtitle, string iconFile)
        {
            Control icon;
            if (!String.IsNullOrEmpty(iconFile)) icon = SvgIcon(iconFile, 24, 22, 34);
            else
            {
                string glyph = HeadingGlyph(title, subtitle);
                Font iconFont = glyph == "✓" ? new Font("Microsoft YaHei UI", 14F, FontStyle.Bold) : new Font("Segoe Fluent Icons", 12F);
                icon = new Label { Text = glyph, Left = 24, Top = 22, Width = 34, Height = 34, ForeColor = Color.White, BackColor = AccentFill, Font = iconFont, TextAlign = ContentAlignment.MiddleCenter };
                Round(icon, 9);
            }
            Label heading = new Label { Text = title, Left = 68, Top = 22, Width = form.ClientSize.Width - 120, Height = 30, ForeColor = Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold) };
            Label sub = new Label { Text = subtitle, Left = 25, Top = 62, Width = form.ClientSize.Width - 50, Height = 22, ForeColor = Muted, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F) };
            icon.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) BeginDrag(form); };
            heading.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) BeginDrag(form); };
            sub.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) BeginDrag(form); };
            form.Controls.Add(icon); form.Controls.Add(heading); form.Controls.Add(sub);
        }

        private static string HeadingGlyph(string title, string subtitle)
        {
            string text = (title ?? "") + " " + (subtitle ?? "");
            if (text.Contains("日程") || text.Contains("时间")) return "";
            if (text.Contains("全部") || text.Contains("管理") || text.Contains("规则")) return "";
            if (text.Contains("删除") || text.Contains("未完成")) return "";
            if (text.Contains("待办") || text.Contains("任务")) return "✓";
            return "";
        }

        public static Label Label(string text, int x, int y, int width)
        {
            return new Label { Text = text, Left = x, Top = y, Width = width, Height = 22, ForeColor = Muted, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F) };
        }

        public static TextBox TextBox(int x, int y, int width, string text)
        {
            TextBox box = new TextBox { Left = x, Top = y, Width = width, Height = 36, AutoSize = false, Text = text ?? "", BackColor = Panel, ForeColor = Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
            Round(box, 9); return box;
        }

        public static Button Button(string text, int x, int y, int width, DialogResult result)
        {
            Button button = new Button { Text = text, Left = x, Top = y, Width = width, Height = 38, DialogResult = result, FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = Text, Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 9F) };
            button.FlatAppearance.BorderColor = Panel; button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(218, 236, 251);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 246, 255);
            button.MouseEnter += delegate { if (button.Enabled) button.BackColor = Color.FromArgb(235, 246, 255); };
            button.MouseLeave += delegate { button.BackColor = Panel; };
            Round(button, 9);
            return button;
        }

        public static Button PrimaryButton(string text, int x, int y, int width, DialogResult result)
        {
            Button button = Button(text, x, y, width, result);
            button.BackColor = AccentFill; button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = AccentFill;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 118, 222);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(25, 94, 185);
            button.MouseEnter += delegate { if (button.Enabled) button.BackColor = Color.FromArgb(38, 118, 222); };
            button.MouseLeave += delegate { button.BackColor = AccentFill; };
            return button;
        }

        public static Button DangerButton(string text, int x, int y, int width, DialogResult result)
        {
            Button button = Button(text, x, y, width, result);
            button.ForeColor = Danger;
            button.BackColor = Color.FromArgb(255, 246, 246);
            button.FlatAppearance.BorderColor = button.BackColor;
            return button;
        }

        public static void StyleList(ListView list)
        {
            list.BackColor = Color.FromArgb(247, 251, 255); list.ForeColor = Text; list.BorderStyle = BorderStyle.FixedSingle;
            list.Font = new Font("Microsoft YaHei UI", 9F); list.FullRowSelect = true; list.HideSelection = false;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable; list.GridLines = true;
            Round(list, 10);
        }

        public static bool Confirm(string text, string title)
        {
            Form form = Form(title, 480, 250); Heading(form, title, "此操作无法撤销。");
            Label message = new Label { Text = text, Left = 26, Top = 98, Width = 428, Height = 56, ForeColor = Text, BackColor = Surface, Padding = new Padding(14, 14, 14, 8), Font = new Font("Microsoft YaHei UI", 10F) };
            Round(message, 10); form.Controls.Add(message);
            Button cancel = Button("取消", 278, 184, 84, DialogResult.Cancel), confirm = DangerButton("确认删除", 372, 184, 82, DialogResult.Yes);
            form.Controls.AddRange(new Control[] { cancel, confirm }); form.CancelButton = cancel;
            return form.ShowDialog() == DialogResult.Yes;
        }

        public static void Error(string text)
        {
            Form form = Form("操作未完成", 480, 240); Heading(form, "操作未完成", "请检查输入后再试一次。");
            Label message = new Label { Text = text, Left = 26, Top = 98, Width = 428, Height = 52, ForeColor = Text, BackColor = Surface, Padding = new Padding(14, 13, 14, 8), Font = new Font("Microsoft YaHei UI", 10F) };
            Round(message, 10); form.Controls.Add(message);
            Button close = PrimaryButton("知道了", 370, 174, 84, DialogResult.OK); form.Controls.Add(close); form.AcceptButton = close; form.CancelButton = close;
            form.ShowDialog();
        }
    }
}
