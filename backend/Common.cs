using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Rectangle { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public int Size; public Rectangle Monitor; public Rectangle Work; public uint Flags; }
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        public static void EnableDpiAwareness()
        {
            try { SetProcessDPIAware(); }
            catch { }
        }

        private static string ConfigPathFor(string fileName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string local = Path.Combine(baseDir, fileName);
            if (File.Exists(local) || !String.Equals(new DirectoryInfo(baseDir).Name, "@Resources", StringComparison.OrdinalIgnoreCase))
                return local;
            DirectoryInfo skin = Directory.GetParent(baseDir);
            DirectoryInfo skins = skin == null ? null : skin.Parent;
            // Calendar currently shares Todo's scale files by the fixed sibling skin names below. Keep this coupling explicit until both skins persist and update the settings together.
            if (skin != null && skins != null && String.Equals(skin.Name, "Calendar", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(skins.FullName, "Todo", "@Resources", fileName);
            return local;
        }

        private static string ConfigPath { get { return ConfigPathFor("ui-scale.txt"); } }
        private static string WindowConfigPath { get { return ConfigPathFor("ui-window-scale.txt"); } }

        private static string ReadMode(string path, string fallback)
        {
            try
            {
                if (File.Exists(path))
                {
                    string value = File.ReadAllText(path, Encoding.UTF8).Trim().ToLowerInvariant();
                    if (value == AutoMode) return AutoMode;
                    float parsed;
                    if (Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        return Clamp(parsed).ToString("0.00", CultureInfo.InvariantCulture);
                }
            }
            catch { }
            return fallback;
        }

        public static string Mode { get { return ReadMode(ConfigPath, AutoMode); } }

        public static string WindowMode
        {
            get
            {
                // Preserve pre-v2 behavior until the user explicitly saves a separate window scale.
                string fallback = Mode == AutoMode ? AutoMode : "1.00";
                return ReadMode(WindowConfigPath, fallback);
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
                string mode = WindowMode;
                float manual;
                if (mode != AutoMode && Single.TryParse(mode, NumberStyles.Float, CultureInfo.InvariantCulture, out manual))
                    return Clamp(manual);
                return AutoScale();
            }
        }

        public static float TileCurrent
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
                return AutoScale();
            }
        }

        private static float AutoScale()
        {
            int width = 2560, height = 1440;
            try
            {
                Point cursor;
                if (GetCursorPos(out cursor))
                {
                    IntPtr monitor = MonitorFromPoint(cursor, 2);
                    MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                    if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    {
                        width = info.Monitor.Right - info.Monitor.Left;
                        height = info.Monitor.Bottom - info.Monitor.Top;
                    }
                }
                if (width <= 0 || height <= 0)
                {
                    width = GetSystemMetrics(0);
                    height = GetSystemMetrics(1);
                }
            }
            catch { }
            float scale = Math.Min(width / BaseWidth, height / BaseHeight);
            scale = (float)Math.Round(scale * 20F, MidpointRounding.AwayFromZero) / 20F;
            return Clamp(scale);
        }

        public static int Percent { get { return (int)Math.Round(Current * 100F); } }
        public static int TilePercent { get { return (int)Math.Round(TileCurrent * 100F); } }

        public static void SaveMode(string mode)
        {
            SaveModeFile(ConfigPath, mode);
        }

        public static void SaveWindowMode(string mode)
        {
            SaveModeFile(WindowConfigPath, mode);
        }

        private static void SaveModeFile(string path, string mode)
        {
            string normalized = String.IsNullOrWhiteSpace(mode) ? AutoMode : mode.Trim().ToLowerInvariant();
            if (normalized != AutoMode)
            {
                float parsed;
                if (!Single.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    throw new ArgumentException("无效的界面缩放比例");
                normalized = Clamp(parsed).ToString("0.00", CultureInfo.InvariantCulture);
            }
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, normalized, new UTF8Encoding(false));
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
            if (option.StartsWith("Padding=", StringComparison.OrdinalIgnoreCase))
            {
                string[] values = option.Substring(8).Split(',');
                for (int i = 0; i < values.Length; i++)
                {
                    double value;
                    if (Double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) values[i] = (value * scale).ToString("0.###", CultureInfo.InvariantCulture);
                }
                return "Padding=" + String.Join(",", values);
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


        private static float Clamp(float value)
        {
            return Math.Max(MinimumScale, Math.Min(MaximumScale, value));
        }
    }

    internal static class UiTheme
    {
        public const string Classic = "classic";
        public const string Mica = "mica";
        public const string Acrylic = "acrylic";

        private static string ConfigPathFor(string fileName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string local = Path.Combine(baseDir, fileName);
            if (File.Exists(local) || !String.Equals(new DirectoryInfo(baseDir).Name, "@Resources", StringComparison.OrdinalIgnoreCase)) return local;
            DirectoryInfo skin = Directory.GetParent(baseDir);
            DirectoryInfo skins = skin == null ? null : skin.Parent;
            if (skin != null && skins != null && String.Equals(skin.Name, "Calendar", StringComparison.OrdinalIgnoreCase)) return Path.Combine(skins.FullName, "Todo", "@Resources", fileName);
            return local;
        }

        private static string ConfigPath { get { return ConfigPathFor("ui-theme.txt"); } }

        public static string Current
        {
            get
            {
                string overrideValue = Environment.GetEnvironmentVariable("RAINMETER_UI_THEME_OVERRIDE");
                if (!String.IsNullOrWhiteSpace(overrideValue)) return Normalize(overrideValue);
                try { if (File.Exists(ConfigPath)) return Normalize(File.ReadAllText(ConfigPath, Encoding.UTF8)); }
                catch { }
                return Classic;
            }
        }

        public static string Normalize(string mode)
        {
            string value = String.IsNullOrWhiteSpace(mode) ? Classic : mode.Trim().ToLowerInvariant();
            return value == Mica || value == Acrylic ? value : Classic;
        }

        public static string DisplayName(string mode)
        {
            string value = Normalize(mode);
            return value == Mica ? "云母" : value == Acrylic ? "亚克力" : "经典";
        }

        public static void Save(string mode)
        {
            string normalized = Normalize(mode);
            string directory = Path.GetDirectoryName(ConfigPath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(ConfigPath, normalized, new UTF8Encoding(false));
        }

        public static bool WriteRainmeterTheme(string resourceDirectory)
        {
            if (String.IsNullOrWhiteSpace(resourceDirectory)) throw new ArgumentException("主题资源目录不能为空");
            return RuntimeUtil.WriteUtf16IfChanged(Path.Combine(resourceDirectory, "Theme.inc"), RainmeterVariables(Current));
        }

        internal static string RainmeterVariables(string mode)
        {
            string value = Normalize(mode);
            List<string> lines = new List<string>();
            lines.Add("; This file is generated from ui-theme.txt. Change the style in 外观与备份.");
            lines.Add("[Variables]");
            lines.Add("ThemeMode=" + value);
            if (value == Mica)
                AddPalette(lines, "28,33,43,255", "92,99,112,255", "62,92,214,255", "32,122,82,255", "196,54,63,255", "207,214,225,230", "102,110,124,255", "62,92,214,255", "242,245,250,252", "255,255,255,235", "240,248,244,238", "255,255,255,185", "207,214,225,190", "0");
            else if (value == Acrylic)
                AddPalette(lines, "25,34,47,255", "83,101,123,255", "25,108,212,255", "20,118,66,255", "198,52,60,255", "255,255,255,172", "75,91,113,255", "32,112,214,245", "226,239,250,205", "250,253,255,185", "232,247,240,190", "255,255,255,205", "255,255,255,145", "1");
            else
                AddPalette(lines, "21,32,48,255", "92,108,130,255", "25,108,212,255", "20,118,66,255", "198,52,60,255", "198,216,232,210", "75,91,113,255", "32,112,214,255", "239,248,255,248", "247,251,255,242", "239,249,244,238", "255,255,255,180", "202,218,232,170", "0");
            return String.Join("\r\n", lines) + "\r\n";
        }

        private static void AddPalette(List<string> lines, string text, string muted, string accent, string done, string danger, string border, string subtle, string accentFill, string panel, string card, string doneCard, string highlight, string divider, string blur)
        {
            lines.Add("TextColor=" + text); lines.Add("MutedColor=" + muted); lines.Add("AccentColor=" + accent);
            lines.Add("DoneColor=" + done); lines.Add("OngoingColor=" + done); lines.Add("DangerColor=" + danger); lines.Add("ConflictColor=" + danger);
            lines.Add("BorderColor=" + border); lines.Add("SubtleColor=" + subtle); lines.Add("AccentFill=" + accentFill);
            lines.Add("PanelColor=" + panel); lines.Add("CardColor=" + card); lines.Add("DoneCardColor=" + doneCard);
            lines.Add("PanelHighlightColor=" + highlight); lines.Add("DividerColor=" + divider); lines.Add("ThemeBlur=" + blur);
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
            WriteAtomicText(path, Serialize(value));
        }

        private static void WriteAtomicText(string path, string content)
        {
            string fullPath = Path.GetFullPath(path);
            string key;
            using (SHA256 sha = SHA256.Create())
                key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()))).Replace("-", "");
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(false, @"Global\RainmeterAtomic_" + key))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                    catch (System.Threading.AbandonedMutexException) { held = true; }
                    if (!held) throw new TimeoutException("文件写入正忙：" + Path.GetFileName(path));
                    WriteAtomicTextLocked(fullPath, content);
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        private static void WriteAtomicTextLocked(string path, string content)
        {
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else
                {
                    try { File.Move(temporary, path); }
                    catch (IOException) { if (!File.Exists(path)) throw; File.Replace(temporary, path, null); }
                }
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
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
            WriteAtomicText(path, Convert.ToBase64String(cipher));
        }
    }

    internal sealed class AddressProviderBinding
    {
        public string PluginId="",PluginName="",ValueKey="",Value="";public int Priority;
    }

    internal static class DynamicPluginValues
    {
        public const string SsdpPluginId = "io.github.kevendai.ssdp-server-ip";
        private static string Root { get { string configured=Environment.GetEnvironmentVariable("RAINMETER_PLUGIN_ROOT");return String.IsNullOrWhiteSpace(configured)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RainmeterDesktopWidgets"):Path.GetFullPath(configured); } }
        private static string ValuesPath { get { return Path.Combine(Root,"PluginValues.json"); } }
        private static string SsdpConfigPath { get { return Path.Combine(Root,"PluginData",SsdpPluginId,"config.json"); } }
        public static AddressProviderBinding AddressProvider(string target)
        {
            string plugins=Path.Combine(Root,"Plugins");if(String.IsNullOrWhiteSpace(target)||!Directory.Exists(plugins))return null;Dictionary<string,object> entries=ReadEntries();List<AddressProviderBinding> candidates=new List<AddressProviderBinding>();
            foreach(string pluginRoot in Directory.GetDirectories(plugins))try
            {
                string id=Path.GetFileName(pluginRoot),currentPath=Path.Combine(pluginRoot,"current.json");if(!File.Exists(currentPath))continue;Dictionary<string,object> current=JsonUtil.LoadObject(currentPath);if(!JsonUtil.Bool(current,"enabled",false))continue;string version=JsonUtil.String(current,"version","");string manifestPath=Path.Combine(pluginRoot,"versions",version,"plugin.json");if(!File.Exists(manifestPath))continue;Dictionary<string,object> manifest=JsonUtil.LoadObject(manifestPath),address=JsonUtil.Object(JsonUtil.Get(manifest,"address_provider"));List<string> targets=JsonUtil.Array(JsonUtil.Get(address,"targets")).Select(Convert.ToString).Where(x=>!String.IsNullOrWhiteSpace(x)).ToList();if(!targets.Contains(target,StringComparer.OrdinalIgnoreCase))continue;string key=JsonUtil.String(address,"value","");if(key=="")continue;
                candidates.Add(new AddressProviderBinding{PluginId=id,PluginName=JsonUtil.String(manifest,"name",id),ValueKey=key,Priority=JsonUtil.Int(address,"priority",0),Value=EntryValue(entries,id,key)});
            }catch{}
            return candidates.OrderByDescending(x=>x.Priority).ThenBy(x=>x.PluginId,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        public static string BindForTarget(string value,string target){AddressProviderBinding provider=AddressProvider(target);return provider==null||String.IsNullOrWhiteSpace(provider.Value)?Resolve(value):ReplaceAnyHost(value,provider.Value);}
        public static string BindSelected(string value,string target,string source)
        {
            if(String.Equals(source,"manual",StringComparison.OrdinalIgnoreCase))return value;
            if(String.Equals(source,"ssdp",StringComparison.OrdinalIgnoreCase)){AddressProviderBinding selected=AddressProvider(target);return selected==null||String.IsNullOrWhiteSpace(selected.Value)?"":ReplaceAnyHost(value,selected.Value);}
            return BindForTarget(value,target);
        }
        // 设置页显示的是接管后的实际地址，但保存时只能接收用户对协议、端口、路径、查询参数的修改。
        // 原始主机继续留在配置里，运行时再由地址 Provider 替换；这样禁用 Provider 后仍能回到用户自己的地址。
        public static string MergeAddressEdit(string stored,string edited,string target)
        {
            AddressProviderBinding provider=AddressProvider(target);if(provider==null||String.IsNullOrWhiteSpace(provider.Value))return edited;
            Uri editedUri,storedUri;if(!Uri.TryCreate(edited,UriKind.Absolute,out editedUri))return edited;
            if(!Uri.TryCreate(stored,UriKind.Absolute,out storedUri))return edited;
            try
            {
                bool slash=edited.EndsWith("/",StringComparison.Ordinal);UriBuilder builder=new UriBuilder(editedUri);builder.Host=storedUri.Host;
                string merged=builder.Uri.AbsoluteUri;return slash?merged:merged.TrimEnd('/');
            }
            catch{return edited;}
        }
        public static string Resolve(string value)
        {
            if(String.IsNullOrEmpty(value))return value;Dictionary<string,object> entries=ReadEntries();
            string resolved=System.Text.RegularExpressions.Regex.Replace(value,@"\{\{plugin:([a-z0-9.-]+):([A-Za-z0-9_.-]+)\}\}",delegate(System.Text.RegularExpressions.Match match){string replacement=JsonUtil.String(JsonUtil.Object(JsonUtil.Get(entries,VariableName(match.Groups[1].Value,match.Groups[2].Value))),"value","");return replacement==""?match.Value:replacement;},System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string current=EntryValue(entries,SsdpPluginId,"server_ip");if(current=="")return resolved;Dictionary<string,object> config=File.Exists(SsdpConfigPath)?SafeLoad(SsdpConfigPath):new Dictionary<string,object>();
            foreach(string old in new[]{JsonUtil.String(config,"previous_ip",""),JsonUtil.String(config,"last_ip","")}.Where(x=>x!=""&&x!=current).Distinct(StringComparer.OrdinalIgnoreCase))resolved=ReplaceHost(resolved,old,current);return resolved;
        }
        public static void ResolveObject(Dictionary<string,object> value){foreach(string key in value.Keys.ToList()){string text=value[key] as string;if(text!=null)value[key]=Resolve(text);else{Dictionary<string,object> nested=value[key] as Dictionary<string,object>;if(nested!=null)ResolveObject(nested);}}}
        public static string CurrentSsdpIp(){return EntryValue(ReadEntries(),SsdpPluginId,"server_ip");}
        public static bool UsesSsdp(string value)
        {
            if(String.IsNullOrEmpty(value))return false;if(value.IndexOf("{{plugin:"+SsdpPluginId+":server_ip}}",StringComparison.OrdinalIgnoreCase)>=0)return true;Dictionary<string,object> config=File.Exists(SsdpConfigPath)?SafeLoad(SsdpConfigPath):new Dictionary<string,object>();
            return new[]{JsonUtil.String(config,"previous_ip",""),JsonUtil.String(config,"last_ip","")}.Any(ip=>ip!=""&&value.IndexOf(ip,StringComparison.OrdinalIgnoreCase)>=0);
        }
        private static Dictionary<string,object> ReadEntries(){try{return File.Exists(ValuesPath)?JsonUtil.Object(JsonUtil.Get(JsonUtil.LoadObject(ValuesPath),"entries")):new Dictionary<string,object>();}catch{return new Dictionary<string,object>();}}
        private static Dictionary<string,object> SafeLoad(string path){try{return JsonUtil.LoadObject(path);}catch{return new Dictionary<string,object>();}}
        private static string EntryValue(Dictionary<string,object> entries,string pluginId,string key){return JsonUtil.String(JsonUtil.Object(JsonUtil.Get(entries,VariableName(pluginId,key))),"value","");}
        private static string VariableName(string pluginId,string key){return System.Text.RegularExpressions.Regex.Replace("Plugin_"+pluginId+"_"+key,@"[^A-Za-z0-9_]","_");}
        private static string ReplaceAnyHost(string value,string newIp)
        {
            if(String.IsNullOrWhiteSpace(value))return value;IPAddress address;if(IPAddress.TryParse(value,out address))return newIp;Uri uri;if(Uri.TryCreate(value,UriKind.Absolute,out uri)){bool slash=value.EndsWith("/",StringComparison.Ordinal);UriBuilder builder=new UriBuilder(uri);builder.Host=newIp;string changed=builder.Uri.AbsoluteUri;return slash?changed:changed.TrimEnd('/');}return value;
        }
        private static string ReplaceHost(string value,string oldIp,string newIp)
        {
            if(String.Equals(value,oldIp,StringComparison.OrdinalIgnoreCase))return newIp;Uri uri;if(Uri.TryCreate(value,UriKind.Absolute,out uri)&&String.Equals(uri.Host,oldIp,StringComparison.OrdinalIgnoreCase)){bool slash=value.EndsWith("/",StringComparison.Ordinal);UriBuilder builder=new UriBuilder(uri);builder.Host=newIp;string changed=builder.Uri.AbsoluteUri;return slash?changed:changed.TrimEnd('/');}return value;
        }
    }
    internal static class RuntimeUtil
    {
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

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
            string temporary = path + ".tmp-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                int lastError = 0;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (MoveFileEx(temporary, path, MoveFileReplaceExisting | MoveFileWriteThrough))
                    {
                        lastError = 0;
                        break;
                    }
                    lastError = Marshal.GetLastWin32Error();
                    if (attempt < 9) System.Threading.Thread.Sleep(15);
                }
                if (lastError != 0) throw new Win32Exception(lastError, "Unable to atomically replace " + path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }

        public static string FindRainmeter()
        {
            foreach (Process process in Process.GetProcessesByName("Rainmeter"))
            {
                try
                {
                    string running = process.MainModule == null ? "" : process.MainModule.FileName;
                    if (File.Exists(running)) return running;
                }
                catch { }
                finally { process.Dispose(); }
            }
            string besidePortableSkins = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Rainmeter.exe"));
            string[] candidates = {
                besidePortableSkins,
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
            if (Environment.GetEnvironmentVariable("RAINMETER_COMMANDS_DISABLED") == "1") return;
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

        public static void RefreshAll()
        {
            if (Environment.GetEnvironmentVariable("RAINMETER_COMMANDS_DISABLED") == "1") return;
            string exe = FindRainmeter();
            if (!File.Exists(exe)) return;
            Process process = Process.Start(new ProcessStartInfo(exe, "!RefreshApp") { UseShellExecute = false, CreateNoWindow = true });
            if (process != null && !process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
            }
        }

        public static void SetMeterText(string config, string meter, string text)
        {
            if (Environment.GetEnvironmentVariable("RAINMETER_COMMANDS_DISABLED") == "1") return;
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

    internal static class TileIconFont
    {
        public static readonly string Name = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        private static bool HasFont(string name)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey fonts = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"))
                    return fonts != null && fonts.GetValueNames().Any(value => value.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }
    }
}
