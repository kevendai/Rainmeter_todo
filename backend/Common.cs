using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace RainmeterBackend
{
    internal static class UiScale
    {
        private const float BaseWidth = 2560F;
        private const float BaseHeight = 1440F;
        private const float MinimumScale = 0.70F;
        private const float MaximumScale = 1.25F;
        private const string AutoMode = "auto";
        private sealed class FormScaleState { public float Scale = 1F; public bool Applied; }
        private sealed class ControlScaleState { public bool Scaled; public bool Watching; }
        private static readonly ConditionalWeakTable<Form, FormScaleState> FormScales = new ConditionalWeakTable<Form, FormScaleState>();
        private static readonly ConditionalWeakTable<Control, ControlScaleState> ControlScales = new ConditionalWeakTable<Control, ControlScaleState>();

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static void EnableDpiAwareness()
        {
            try { SetProcessDPIAware(); }
            catch { }
        }

        private static string ConfigPath
        {
            get
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                string local = Path.Combine(baseDir, "ui-scale.txt");
                if (File.Exists(local) || !String.Equals(new DirectoryInfo(baseDir).Name, "@Resources", StringComparison.OrdinalIgnoreCase))
                    return local;
                DirectoryInfo skin = Directory.GetParent(baseDir);
                DirectoryInfo skins = skin == null ? null : skin.Parent;
                if (skin != null && skins != null && String.Equals(skin.Name, "Calendar", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(skins.FullName, "Todo", "@Resources", "ui-scale.txt");
                return local;
            }
        }

        public static string Mode
        {
            get
            {
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        string value = File.ReadAllText(ConfigPath, Encoding.UTF8).Trim().ToLowerInvariant();
                        if (value == AutoMode) return AutoMode;
                        float parsed;
                        if (Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                            return Clamp(parsed).ToString("0.00", CultureInfo.InvariantCulture);
                    }
                }
                catch { }
                return AutoMode;
            }
        }

        public static float Current
        {
            get
            {
                string overrideText = Environment.GetEnvironmentVariable("RAINMETER_UI_SCALE_OVERRIDE");
                float overrideValue;
                if (!String.IsNullOrWhiteSpace(overrideText) && Single.TryParse(overrideText, NumberStyles.Float, CultureInfo.InvariantCulture, out overrideValue))
                    return Clamp(overrideValue);
                string mode = Mode;
                float manual;
                if (mode != AutoMode && Single.TryParse(mode, NumberStyles.Float, CultureInfo.InvariantCulture, out manual))
                    return Clamp(manual);
                Rectangle bounds = Screen.PrimaryScreen == null ? new Rectangle(0, 0, 2560, 1440) : Screen.PrimaryScreen.Bounds;
                float scale = Math.Min(bounds.Width / BaseWidth, bounds.Height / BaseHeight);
                scale = (float)Math.Round(scale * 20F, MidpointRounding.AwayFromZero) / 20F;
                return Clamp(scale);
            }
        }

        public static int Percent { get { return (int)Math.Round(Current * 100F); } }

        public static void SaveMode(string mode)
        {
            string normalized = String.IsNullOrWhiteSpace(mode) ? AutoMode : mode.Trim().ToLowerInvariant();
            if (normalized != AutoMode)
            {
                float parsed;
                if (!Single.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    throw new ArgumentException("无效的界面缩放比例");
                normalized = Clamp(parsed).ToString("0.00", CultureInfo.InvariantCulture);
            }
            string directory = Path.GetDirectoryName(ConfigPath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(ConfigPath, normalized, new UTF8Encoding(false));
        }

        public static string RainmeterOption(string option, float scale)
        {
            if (String.IsNullOrEmpty(option)) return option;
            string[] numeric = { "X=", "Y=", "W=", "H=", "FontSize=" };
            foreach (string prefix in numeric)
            {
                if (!option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                double value;
                if (Double.TryParse(option.Substring(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return prefix + (value * scale).ToString("0.###", CultureInfo.InvariantCulture);
                return option;
            }
            if (!option.StartsWith("Shape=", StringComparison.OrdinalIgnoreCase)) return option;
            int pipe = option.IndexOf('|');
            string geometry = pipe < 0 ? option : option.Substring(0, pipe);
            string styling = pipe < 0 ? "" : option.Substring(pipe);
            geometry = System.Text.RegularExpressions.Regex.Replace(geometry, @"(?<![A-Za-z#])[-+]?\d+(?:\.\d+)?", delegate(System.Text.RegularExpressions.Match match) {
                double value;
                return Double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? (value * scale).ToString("0.###", CultureInfo.InvariantCulture) : match.Value;
            });
            styling = System.Text.RegularExpressions.Regex.Replace(styling, @"(?i)(StrokeWidth\s+)(\d+(?:\.\d+)?)", delegate(System.Text.RegularExpressions.Match match) {
                double value;
                return Double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? match.Groups[1].Value + (value * scale).ToString("0.###", CultureInfo.InvariantCulture) : match.Value;
            });
            return geometry + styling;
        }

        public static void ApplyTo(Form form)
        {
            float scale = Current;
            FormScaleState formState = FormScales.GetOrCreateValue(form);
            formState.Scale = scale;
            formState.Applied = true;
            form.SuspendLayout();
            if (Math.Abs(scale - 1F) >= 0.001F)
            {
                form.Scale(new SizeF(scale, scale));
                form.Font = ScaleFont(form.Font, scale);
                ScaleFontsAndSpecialControls(form, scale);
            }
            form.ResumeLayout(true);
            MarkScaledTree(form);
            InstallDynamicScaling(form);
            if (Math.Abs(scale - 1F) >= 0.001F)
            {
                Screen screen = Screen.FromControl(form);
                Rectangle area = screen.WorkingArea;
                form.Left = area.Left + Math.Max(0, (area.Width - form.Width) / 2);
                form.Top = area.Top + Math.Max(0, (area.Height - form.Height) / 2);
            }
        }

        public static float For(Control control)
        {
            if (control == null) return 1F;
            Form form = control as Form ?? control.FindForm();
            FormScaleState state;
            return form != null && FormScales.TryGetValue(form, out state) && state.Applied ? state.Scale : 1F;
        }

        public static int Logical(Control control, int value)
        {
            return Math.Max(0, (int)Math.Round(value * For(control), MidpointRounding.AwayFromZero));
        }

        private static void ScaleAddedControl(Control control)
        {
            if (control == null) return;
            ControlScaleState controlState = ControlScales.GetOrCreateValue(control);
            if (controlState.Scaled) { InstallDynamicScaling(control); return; }
            Form form = control.FindForm();
            FormScaleState formState;
            if (form == null || !FormScales.TryGetValue(form, out formState) || !formState.Applied) return;
            if (Math.Abs(formState.Scale - 1F) >= 0.001F)
            {
                control.Scale(new SizeF(formState.Scale, formState.Scale));
                ScaleControlTreeFontsAndSpecials(control, formState.Scale);
            }
            MarkScaledTree(control);
            InstallDynamicScaling(control);
        }

        private static void MarkScaledTree(Control control)
        {
            ControlScales.GetOrCreateValue(control).Scaled = true;
            foreach (Control child in control.Controls) MarkScaledTree(child);
        }

        private static void InstallDynamicScaling(Control parent)
        {
            ControlScaleState state = ControlScales.GetOrCreateValue(parent);
            if (!state.Watching)
            {
                parent.ControlAdded += delegate(object sender, ControlEventArgs args) { ScaleAddedControl(args.Control); };
                state.Watching = true;
            }
            foreach (Control child in parent.Controls) InstallDynamicScaling(child);
        }

        private static void ScaleFontsAndSpecialControls(Control parent, float scale)
        {
            foreach (Control control in parent.Controls)
            {
                ScaleFontAndSpecialControl(control, scale);
                ScaleFontsAndSpecialControls(control, scale);
            }
        }

        private static void ScaleControlTreeFontsAndSpecials(Control control, float scale)
        {
            ScaleFontAndSpecialControl(control, scale);
            foreach (Control child in control.Controls) ScaleControlTreeFontsAndSpecials(child, scale);
        }

        private static void ScaleFontAndSpecialControl(Control control, float scale)
        {
            PropertyDescriptor fontProperty = TypeDescriptor.GetProperties(control)["Font"];
            if (control.Font != null && fontProperty != null && fontProperty.ShouldSerializeValue(control))
                control.Font = ScaleFont(control.Font, scale);
            ListView list = control as ListView;
            if (list != null)
                foreach (ColumnHeader column in list.Columns) column.Width = Math.Max(24, (int)Math.Round(column.Width * scale));
            TabControl tabs = control as TabControl;
            if (tabs != null && tabs.SizeMode == TabSizeMode.Fixed)
                tabs.ItemSize = new Size(Math.Max(24, (int)Math.Round(tabs.ItemSize.Width * scale)), Math.Max(20, (int)Math.Round(tabs.ItemSize.Height * scale)));
            ListBox listBox = control as ListBox;
            if (listBox != null && listBox.DrawMode != DrawMode.Normal)
                listBox.ItemHeight = Math.Max(16, (int)Math.Round(listBox.ItemHeight * scale));
            ScrollableControl scrollable = control as ScrollableControl;
            if (scrollable != null && scrollable.AutoScrollMinSize != Size.Empty)
                scrollable.AutoScrollMinSize = new Size(Math.Max(0, (int)Math.Round(scrollable.AutoScrollMinSize.Width * scale)), Math.Max(0, (int)Math.Round(scrollable.AutoScrollMinSize.Height * scale)));
            CheckBox check = control as CheckBox;
            if (check != null)
            {
                int centerY = check.Top + check.Height / 2;
                int minimumHeight = String.IsNullOrEmpty(check.Text) ? 18 : 20;
                if (check.Height < minimumHeight)
                {
                    check.Height = minimumHeight;
                    check.Top = centerY - check.Height / 2;
                }
                if (String.IsNullOrEmpty(check.Text) && check.Width < 18) check.Width = 18;
            }
            ComboBox combo = control as ComboBox;
            if (combo != null && combo.DropDownStyle == ComboBoxStyle.DropDownList)
                combo.Height = combo.PreferredHeight;
        }

        private static Font ScaleFont(Font font, float scale)
        {
            return new Font(font.FontFamily, Math.Max(6F, font.Size * scale), font.Style, font.Unit);
        }

        private static float Clamp(float value)
        {
            return Math.Max(MinimumScale, Math.Min(MaximumScale, value));
        }
    }

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

        public static void SetMeterText(string config, string meter, string text)
        {
            string exe = FindRainmeter();
            if (!File.Exists(exe)) return;
            string safe = CleanRainmeter(text);
            RunRainmeterBang(exe, "!SetOption \"" + meter + "\" \"Text\" \"" + safe + "\" \"" + config + "\"");
            RunRainmeterBang(exe, "!UpdateMeter \"" + meter + "\" \"" + config + "\"");
            RunRainmeterBang(exe, "!Redraw \"" + config + "\"");
        }

        private static void RunRainmeterBang(string exe, string arguments)
        {
            try
            {
                Process process = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true });
                if (process != null && !process.WaitForExit(1500))
                {
                    try { process.Kill(); } catch { }
                }
            }
            catch { }
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
            int safeRadius = Math.Max(1, Math.Min(radius, Math.Max(1, Math.Min(bounds.Width, bounds.Height) / 2)));
            int diameter = safeRadius * 2;
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
            int radius = Math.Max(1, (int)Math.Round(14F * UiScale.For(form)));
            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, form.Width, form.Height), radius))
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
                int scaledRadius = Math.Max(1, (int)Math.Round(radius * UiScale.For(control), MidpointRounding.AwayFromZero));
                using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, control.Width, control.Height), scaledRadius))
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
            form.Shown += delegate { UiScale.ApplyTo(form); };
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
                int radius = Math.Max(1, (int)Math.Round(18F * UiScale.For(form)));
                using (GraphicsPath path = RoundedPath(bounds, radius))
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
