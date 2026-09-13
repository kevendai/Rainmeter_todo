using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using RainmeterBackend;

internal static partial class TodoApp
{
    private const int BackupFormatVersion = 1;
    private const string BackupConfigVersion = "2.0";
    private const int BackupKdfIterations = 310000;
    private const int MaxBackupBytes = 64 * 1024 * 1024;
    private const int MaxBackupPlainBytes = 32 * 1024 * 1024;
    private static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("RWBACKUP");

    private sealed class BackupPasswordResult
    {
        public string Password;
        public bool FullBackup;
    }

    private sealed class BackupImportChoice
    {
        public bool Configuration;
        public bool Data;
    }

    private static string CalendarResourceDir
    {
        get { return Path.GetFullPath(Path.Combine(ResourceDir, "..", "..", "Calendar", "@Resources")); }
    }

    private static string CalendarStatePath
    {
        get { return Path.Combine(CalendarResourceDir, "calendar-state.json"); }
    }

    private static string CalDavSecret
    {
        get { return Path.Combine(ResourceDir, "caldav.secret"); }
    }

    private static string UiScalePath
    {
        get { return Path.Combine(ResourceDir, "ui-scale.txt"); }
    }

    private static string ExportUserBackupInteractive()
    {
        BackupPasswordResult request = ShowBackupPasswordDialog(true);
        if (request == null) return "";
        SaveFileDialog save = new SaveFileDialog {
            Title = "导出 Rainmeter 用户配置",
            Filter = "Rainmeter 加密备份 (*.rwbackup)|*.rwbackup",
            DefaultExt = "rwbackup",
            AddExtension = true,
            FileName = "Rainmeter-用户备份-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".rwbackup"
        };
        if (save.ShowDialog() != DialogResult.OK) return "";
        Dictionary<string, object> payload = BuildBackupPayload(request.FullBackup);
        byte[] encrypted = EncryptBackup(JsonUtil.Serialize(payload), request.Password, BackupKdfIterations);
        WriteBytesAtomic(save.FileName, encrypted);
        return save.FileName;
    }

    private static string ImportUserBackupInteractive()
    {
        OpenFileDialog open = new OpenFileDialog {
            Title = "导入 Rainmeter 用户配置",
            Filter = "Rainmeter 加密备份 (*.rwbackup)|*.rwbackup|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (open.ShowDialog() != DialogResult.OK) return "";
        BackupPasswordResult password = ShowBackupPasswordDialog(false);
        if (password == null) return "";
        byte[] encrypted = File.ReadAllBytes(open.FileName);
        Dictionary<string, object> payload = JsonUtil.Object(JsonUtil.Deserialize(DecryptBackup(encrypted, password.Password)));
        UpgradeBackupPayload(payload);
        ValidateBackupPayload(payload);
        BackupImportChoice choice = ShowBackupImportPreview(payload);
        if (choice == null || (!choice.Configuration && !choice.Data)) return "";
        WithGlobalStateLocks(delegate { ApplyBackupPayload(payload, choice.Configuration, choice.Data); });
        return "备份导入成功。\r\n\r\n敏感配置已使用当前 Windows 用户的 DPAPI 重新加密。";
    }

    private static Dictionary<string, object> BuildBackupPayload(bool fullBackup)
    {
        Dictionary<string, object> components = new Dictionary<string, object>();
        if (File.Exists(PaperSyncSecret)) components["paper_settings"] = ReadSecretForBackup(PaperSyncSecret, "论文设置");
        if (File.Exists(TranslationSecret)) components["translation"] = ReadSecretForBackup(TranslationSecret, "翻译凭据");
        if (File.Exists(CalDavSecret)) components["caldav"] = ReadSecretForBackup(CalDavSecret, "CalDAV 凭据");
        components["ui_scale"] = ReadBackupUiScale();
        components["plugins"] = BuildPluginBackup();

        Dictionary<string, object> calendarState = LoadOptionalObject(CalendarStatePath, NewBackupCalendarState());
        components["calendar_rules"] = JsonUtil.Array(JsonUtil.Get(calendarState, "series_rules"));
        if (fullBackup)
        {
            components["tasks"] = LoadOptionalObject(StatePath, NewState());
            components["calendar_state"] = calendarState;
        }

        return new Dictionary<string, object> {
            {"format_version", BackupFormatVersion},
            {"config_version", BackupConfigVersion},
            {"app_version", AppVersion},
            {"exported_at", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)},
            {"full_backup", fullBackup},
            {"components", components}
        };
    }

    private static List<object> BuildPluginBackup()
    {
        PluginPaths.Ensure();HashSet<string> ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(Directory.Exists(PluginPaths.Plugins))foreach(string path in Directory.GetDirectories(PluginPaths.Plugins))ids.Add(Path.GetFileName(path));
        if(Directory.Exists(PluginPaths.Data))foreach(string path in Directory.GetDirectories(PluginPaths.Data))ids.Add(Path.GetFileName(path));
        List<object> result=new List<object>();foreach(string id in ids.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string,object> current=PluginRuntime.Current(id),record=new Dictionary<string,object>{{"id",id},{"enabled",JsonUtil.Bool(current,"enabled",false)},{"version",JsonUtil.String(current,"version","")}};
            string data=PluginPaths.DataRoot(id),config=Path.Combine(data,"config.json"),secret=Path.Combine(data,"secret.dat");
            record["config"]=File.Exists(config)?JsonUtil.LoadObject(config):new Dictionary<string,object>();
            record["secret"]=File.Exists(secret)?ReadSecretForBackup(secret,"插件 "+id+" 的敏感设置"):new Dictionary<string,object>();result.Add(record);
        }return result;
    }

    private static Dictionary<string, object> ReadSecretForBackup(string path, string label)
    {
        try { return JsonUtil.ReadDpapiJson(path); }
        catch (Exception ex) { throw new Exception(label + "无法由当前 Windows 用户解密，未生成不完整备份。", ex); }
    }

    private static Dictionary<string, object> LoadOptionalObject(string path, Dictionary<string, object> fallback)
    {
        if (!File.Exists(path)) return fallback;
        try { return JsonUtil.LoadObject(path); }
        catch (Exception ex) { throw new Exception("配置文件损坏，无法导出：" + Path.GetFileName(path), ex); }
    }

    private static Dictionary<string, object> NewBackupCalendarState()
    {
        return new Dictionary<string, object> {
            {"version", 1},
            {"series_rules", new List<object>()},
            {"conversions", new List<object>()},
            {"local_events", new List<object>()},
            {"hidden_events", new List<object>()}
        };
    }

    private static void ValidateBackupPayload(Dictionary<string, object> payload)
    {
        if (JsonUtil.Int(payload, "format_version", 0) != BackupFormatVersion)
            throw new Exception("此备份格式版本暂不受支持。");
        if (JsonUtil.String(payload, "config_version", "") != BackupConfigVersion)
            throw new Exception("此用户配置版本暂不受支持。");
        Dictionary<string, object> components = JsonUtil.Object(JsonUtil.Get(payload, "components"));
        if (components.Count == 0) throw new Exception("备份中没有可导入的配置。");
        foreach (string secretName in new[] { "paper_settings", "translation", "caldav" })
        {
            object secret = JsonUtil.Get(components, secretName);
            if (secret != null && !(secret is Dictionary<string, object>))
                throw new Exception("备份中的敏感配置格式无效：" + secretName + "。");
        }
        ValidateArrayLimit(JsonUtil.Get(components, "calendar_rules"), "日历规则");
        ValidateArrayLimit(JsonUtil.Get(components, "plugins"), "插件配置");
        foreach(object raw in JsonUtil.Array(JsonUtil.Get(components,"plugins")))
        {
            Dictionary<string,object> plugin=JsonUtil.Object(raw);string id=JsonUtil.String(plugin,"id","");
            if(!Regex.IsMatch(id,@"^[a-z0-9]+(?:[.-][a-z0-9]+)+$"))throw new Exception("备份中的插件 ID 无效。");
            if(!(JsonUtil.Get(plugin,"config") is Dictionary<string,object>)||!(JsonUtil.Get(plugin,"secret") is Dictionary<string,object>))throw new Exception("备份中的插件配置格式无效："+id+"。");
        }
        Dictionary<string, object> tasks = JsonUtil.Get(components, "tasks") == null ? null : JsonUtil.Object(JsonUtil.Get(components, "tasks"));
        if (tasks != null)
        {
            if (JsonUtil.Get(tasks, "tasks") == null) throw new Exception("备份中的待办数据格式无效。");
            ValidateArrayLimit(JsonUtil.Get(tasks, "tasks"), "待办");
        }
        Dictionary<string, object> calendar = JsonUtil.Get(components, "calendar_state") == null ? null : JsonUtil.Object(JsonUtil.Get(components, "calendar_state"));
        if (calendar != null)
        {
            foreach (string key in new[] { "series_rules", "conversions", "local_events", "hidden_events" })
            {
                if (JsonUtil.Get(calendar, key) == null) throw new Exception("备份中的日历状态格式无效。");
                ValidateArrayLimit(JsonUtil.Get(calendar, key), "日历状态");
            }
        }
    }

    private static void UpgradeBackupPayload(Dictionary<string, object> payload)
    {
        string version = JsonUtil.String(payload, "config_version", "");
        switch (version)
        {
            case BackupConfigVersion:
                return;
            case "1.0":
                Dictionary<string,object> components=JsonUtil.Object(JsonUtil.Get(payload,"components"));Dictionary<string,object> secret=new Dictionary<string,object>();
                if(JsonUtil.Get(components,"paper_settings") is Dictionary<string,object>)secret["paper_settings"]=JsonUtil.Object(JsonUtil.Get(components,"paper_settings"));
                if(JsonUtil.Get(components,"translation") is Dictionary<string,object>)secret["translation"]=JsonUtil.Object(JsonUtil.Get(components,"translation"));
                components["plugins"]=secret.Count==0?new List<object>():new List<object>{new Dictionary<string,object>{{"id","io.github.kevendai.arxiv"},{"version",""},{"enabled",false},{"config",new Dictionary<string,object>()},{"secret",secret}}};
                payload["config_version"]=BackupConfigVersion;return;
            default:
                throw new Exception("无法导入用户配置版本 " + (version == "" ? "未知" : version) + "。当前支持版本：" + BackupConfigVersion + "。");
        }
    }

    private static void ValidateArrayLimit(object value, string label)
    {
        if (value != null && JsonUtil.Array(value).Count > 100000)
            throw new Exception(label + "数量异常，已拒绝导入。");
    }

    private static void ApplyBackupPayload(Dictionary<string, object> payload, bool importConfiguration, bool importData)
    {
        Dictionary<string, object> components = JsonUtil.Object(JsonUtil.Get(payload, "components"));
        List<string> paths = new List<string>{ PaperSyncSecret, TranslationSecret, CalDavSecret, UiScalePath, StatePath, CalendarStatePath };
        foreach(object raw in JsonUtil.Array(JsonUtil.Get(components,"plugins"))){string id=JsonUtil.String(JsonUtil.Object(raw),"id","");string data=PluginPaths.DataRoot(id);paths.Add(Path.Combine(data,"config.json"));paths.Add(Path.Combine(data,"secret.dat"));paths.Add(Path.Combine(PluginPaths.PluginRoot(id),"current.json"));}
        string rollback = Path.Combine(ResourceDir, ".import-rollback-" + Guid.NewGuid().ToString("N"));
        Dictionary<string, string> saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(rollback);
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                if (!File.Exists(paths[i])) continue;
                string copy = Path.Combine(rollback, i.ToString(CultureInfo.InvariantCulture) + ".bak");
                File.Copy(paths[i], copy, true);
                saved[paths[i]] = copy;
            }

            Dictionary<string, object> currentCalendar = LoadOptionalObject(CalendarStatePath, NewBackupCalendarState());
            List<object> currentRules = JsonUtil.Array(JsonUtil.Get(currentCalendar, "series_rules"));

            if (importConfiguration)
            {
                WriteSecretComponent(components, "paper_settings", PaperSyncSecret);
                WriteSecretComponent(components, "translation", TranslationSecret);
                WriteSecretComponent(components, "caldav", CalDavSecret);
                ApplyPluginBackup(components);
                object scale = JsonUtil.Get(components, "ui_scale");
                if (scale != null) WriteBackupUiScale(Convert.ToString(scale, CultureInfo.InvariantCulture));
                object rules = JsonUtil.Get(components, "calendar_rules");
                if (rules != null)
                {
                    currentCalendar["series_rules"] = JsonUtil.Array(rules);
                    JsonUtil.SaveAtomic(CalendarStatePath, currentCalendar);
                }
            }

            if (importData)
            {
                object tasks = JsonUtil.Get(components, "tasks");
                object calendar = JsonUtil.Get(components, "calendar_state");
                if (tasks == null || calendar == null) throw new Exception("该备份不包含完整用户数据。");
                JsonUtil.SaveAtomic(StatePath, JsonUtil.Object(tasks));
                Dictionary<string, object> importedCalendar = JsonUtil.Object(calendar);
                if (!importConfiguration) importedCalendar["series_rules"] = currentRules;
                JsonUtil.SaveAtomic(CalendarStatePath, importedCalendar);
            }
        }
        catch
        {
            foreach (string path in paths)
            {
                string copy;
                if (saved.TryGetValue(path, out copy)) File.Copy(copy, path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            throw;
        }
        finally
        {
            try { Directory.Delete(rollback, true); } catch { }
        }
    }

    private static void ApplyPluginBackup(Dictionary<string,object> components)
    {
        foreach(object raw in JsonUtil.Array(JsonUtil.Get(components,"plugins")))
        {
            Dictionary<string,object> record=JsonUtil.Object(raw);string id=JsonUtil.String(record,"id","");if(!Regex.IsMatch(id,@"^[a-z0-9]+(?:[.-][a-z0-9]+)+$"))throw new Exception("插件 ID 无效。");
            string data=PluginPaths.DataRoot(id);Directory.CreateDirectory(data);JsonUtil.SaveAtomic(Path.Combine(data,"config.json"),JsonUtil.Object(JsonUtil.Get(record,"config")));JsonUtil.WriteDpapiJson(Path.Combine(data,"secret.dat"),JsonUtil.Object(JsonUtil.Get(record,"secret")));
            string currentPath=Path.Combine(PluginPaths.PluginRoot(id),"current.json");if(File.Exists(currentPath)){Dictionary<string,object> current=JsonUtil.LoadObject(currentPath);current["enabled"]=JsonUtil.Bool(record,"enabled",false);JsonUtil.SaveAtomic(currentPath,current);}
        }
    }

    private static void WriteSecretComponent(Dictionary<string, object> components, string name, string path)
    {
        object value = JsonUtil.Get(components, name);
        if (value != null) JsonUtil.WriteDpapiJson(path, JsonUtil.Object(value));
    }

    private static string ReadBackupUiScale()
    {
        if (!File.Exists(UiScalePath)) return "auto";
        string value = File.ReadAllText(UiScalePath, Encoding.UTF8).Trim().ToLowerInvariant();
        return NormalizeBackupUiScale(value);
    }

    private static void WriteBackupUiScale(string value)
    {
        string normalized = NormalizeBackupUiScale(value);
        string temporary = UiScalePath + ".tmp";
        File.WriteAllText(temporary, normalized, new UTF8Encoding(false));
        if (File.Exists(UiScalePath)) File.Replace(temporary, UiScalePath, null);
        else File.Move(temporary, UiScalePath);
    }

    private static string NormalizeBackupUiScale(string value)
    {
        value = String.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
        if (value == "auto") return value;
        float parsed;
        if (!Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || parsed < 0.70F || parsed > 1.25F)
            throw new Exception("备份中的界面缩放比例无效。");
        return parsed.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static BackupPasswordResult ShowBackupPasswordDialog(bool exporting)
    {
        Form form = LightUi.Form(exporting ? "导出加密备份" : "打开加密备份", 520, exporting ? 390 : 310);
        LightUi.Heading(form, exporting ? "设置备份密码" : "输入备份密码", exporting ? "该密码用于在其他电脑上解密，无法找回。" : "密码只用于解密此备份，不会保存。");
        TextBox password = PasswordField(form, "备份密码", 28, 104, 464, "");
        TextBox confirm = null;
        CheckBox full = null;
        int buttonTop;
        if (exporting)
        {
            confirm = PasswordField(form, "确认密码", 28, 198, 464, "");
            full = new CheckBox { Left = 28, Top = 292, Width = 360, Height = 28, Text = "完整备份：同时包含待办和本地日程", ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9.5F) };
            form.Controls.Add(full);
            buttonTop = 330;
        }
        else buttonTop = 250;
        Button cancel = LightUi.Button("取消", 294, buttonTop, 92, DialogResult.Cancel);
        Button ok = LightUi.PrimaryButton(exporting ? "继续导出" : "打开备份", 398, buttonTop, 94, DialogResult.None);
        form.Controls.AddRange(new Control[] { cancel, ok });
        form.CancelButton = cancel;
        BackupPasswordResult result = null;
        ok.Click += delegate {
            if (password.Text.Length < 10) { LightUi.Error("备份密码至少需要 10 个字符。"); return; }
            if (exporting && password.Text != confirm.Text) { LightUi.Error("两次输入的备份密码不一致。"); return; }
            result = new BackupPasswordResult { Password = password.Text, FullBackup = exporting && full.Checked };
            form.DialogResult = DialogResult.OK;
            form.Close();
        };
        form.ShowDialog();
        return result;
    }

    private static BackupImportChoice ShowBackupImportPreview(Dictionary<string, object> payload)
    {
        Dictionary<string, object> components = JsonUtil.Object(JsonUtil.Get(payload, "components"));
        bool hasData = JsonUtil.Get(components, "tasks") != null && JsonUtil.Get(components, "calendar_state") != null;
        int taskCount = hasData ? JsonUtil.Array(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(components, "tasks")), "tasks")).Count : 0;
        Dictionary<string, object> calendar = hasData ? JsonUtil.Object(JsonUtil.Get(components, "calendar_state")) : new Dictionary<string, object>();
        int eventCount = hasData ? JsonUtil.Array(JsonUtil.Get(calendar, "local_events")).Count : 0;
        int ruleCount = JsonUtil.Array(JsonUtil.Get(components, "calendar_rules")).Count;
        string sourceVersion = JsonUtil.String(payload, "app_version", "未知");
        string configVersion = JsonUtil.String(payload, "config_version", "未知");

        Form form = LightUi.Form("确认导入内容", 560, 360);
        LightUi.Heading(form, "确认导入内容", "应用版本 " + sourceVersion + "；配置版本 " + configVersion + "；导入前会建立临时回滚副本。");
        CheckBox configuration = new CheckBox { Left = 32, Top = 112, Width = 490, Height = 54, Checked = true, Text = "配置与凭据\r\nCalDAV、论文、翻译、缩放和 " + ruleCount + " 条自动转入规则", ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9.5F) };
        CheckBox data = new CheckBox { Left = 32, Top = 180, Width = 490, Height = 54, Checked = hasData, Enabled = hasData, Text = hasData ? "完整用户数据\r\n" + taskCount + " 条待办、" + eventCount + " 条本地日程及转换记录（覆盖现有数据）" : "此备份不包含待办和本地日程", ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9.5F) };
        Label warning = LightUi.Label("导入的类别会覆盖当前对应内容；未勾选的类别保持不变。", 32, 246, 490);
        Button cancel = LightUi.Button("取消", 326, 300, 96, DialogResult.Cancel);
        Button import = LightUi.PrimaryButton("确认导入", 434, 300, 98, DialogResult.None);
        form.Controls.AddRange(new Control[] { configuration, data, warning, cancel, import });
        form.CancelButton = cancel;
        BackupImportChoice result = null;
        import.Click += delegate {
            if (!configuration.Checked && !data.Checked) { LightUi.Error("请至少选择一类要导入的内容。"); return; }
            string text = data.Checked ? "完整用户数据将覆盖当前待办和本地日程。确定继续吗？" : "所选配置将覆盖当前对应设置。确定继续吗？";
            if (MessageBox.Show(text, "确认导入", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            result = new BackupImportChoice { Configuration = configuration.Checked, Data = data.Checked };
            form.DialogResult = DialogResult.OK;
            form.Close();
        };
        form.ShowDialog();
        return result;
    }

    private static byte[] EncryptBackup(string json, string password, int iterations)
    {
        byte[] salt = RandomBytes(32), iv = RandomBytes(16), keys = null;
        try
        {
            keys = Pbkdf2Sha256(password, salt, iterations, 64);
            byte[] plain = Compress(Encoding.UTF8.GetBytes(json));
            byte[] cipher;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = keys.Take(32).ToArray();
                aes.IV = iv;
                using (ICryptoTransform encryptor = aes.CreateEncryptor()) cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            }
            byte[] authenticated;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(BackupMagic);
                writer.Write(BackupFormatVersion);
                writer.Write(iterations);
                writer.Write(salt.Length);
                writer.Write(iv.Length);
                writer.Write(cipher.Length);
                writer.Write(salt);
                writer.Write(iv);
                writer.Write(cipher);
                writer.Flush();
                authenticated = stream.ToArray();
            }
            byte[] mac;
            using (HMACSHA256 hmac = new HMACSHA256(keys.Skip(32).Take(32).ToArray())) mac = hmac.ComputeHash(authenticated);
            return authenticated.Concat(mac).ToArray();
        }
        finally { if (keys != null) Array.Clear(keys, 0, keys.Length); }
    }

    private static string DecryptBackup(byte[] file, string password)
    {
        if (file == null || file.Length < 100 || file.Length > MaxBackupBytes) throw new Exception("备份文件大小无效。");
        byte[] salt, iv, cipher, storedMac, keys = null;
        int iterations, authenticatedLength;
        using (MemoryStream stream = new MemoryStream(file, false))
        using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
        {
            byte[] magic = reader.ReadBytes(BackupMagic.Length);
            if (!magic.SequenceEqual(BackupMagic)) throw new Exception("这不是受支持的 Rainmeter 加密备份。");
            if (reader.ReadInt32() != BackupFormatVersion) throw new Exception("此备份加密格式版本暂不受支持。");
            iterations = reader.ReadInt32();
            int saltLength = reader.ReadInt32(), ivLength = reader.ReadInt32(), cipherLength = reader.ReadInt32();
            if (iterations < 100000 || iterations > 5000000 || saltLength < 16 || saltLength > 64 || ivLength != 16 || cipherLength <= 0 || cipherLength > MaxBackupBytes)
                throw new Exception("备份加密参数无效。");
            long expected = stream.Position + saltLength + ivLength + cipherLength + 32L;
            if (expected != file.Length) throw new Exception("备份文件不完整或已损坏。");
            salt = reader.ReadBytes(saltLength);
            iv = reader.ReadBytes(ivLength);
            cipher = reader.ReadBytes(cipherLength);
            authenticatedLength = (int)stream.Position;
            storedMac = reader.ReadBytes(32);
        }
        try
        {
            keys = Pbkdf2Sha256(password, salt, iterations, 64);
            byte[] calculated;
            using (HMACSHA256 hmac = new HMACSHA256(keys.Skip(32).Take(32).ToArray())) calculated = hmac.ComputeHash(file, 0, authenticatedLength);
            if (!FixedTimeEquals(calculated, storedMac)) throw new Exception("备份密码错误，或文件已被修改。");
            byte[] compressed;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = keys.Take(32).ToArray();
                aes.IV = iv;
                using (ICryptoTransform decryptor = aes.CreateDecryptor()) compressed = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            }
            return Encoding.UTF8.GetString(Decompress(compressed));
        }
        catch (CryptographicException) { throw new Exception("备份密码错误，或文件已损坏。"); }
        finally { if (keys != null) Array.Clear(keys, 0, keys.Length); }
    }

    private static byte[] Pbkdf2Sha256(string password, byte[] salt, int iterations, int length)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password ?? ""), result = new byte[length];
        try
        {
            using (HMACSHA256 hmac = new HMACSHA256(passwordBytes))
            {
                int offset = 0;
                for (int block = 1; offset < length; block++)
                {
                    byte[] input = new byte[salt.Length + 4];
                    Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
                    input[input.Length - 4] = (byte)(block >> 24);
                    input[input.Length - 3] = (byte)(block >> 16);
                    input[input.Length - 2] = (byte)(block >> 8);
                    input[input.Length - 1] = (byte)block;
                    byte[] u = hmac.ComputeHash(input), t = (byte[])u.Clone();
                    for (int i = 1; i < iterations; i++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
                    }
                    int take = Math.Min(t.Length, length - offset);
                    Buffer.BlockCopy(t, 0, result, offset, take);
                    offset += take;
                }
            }
            return result;
        }
        finally { Array.Clear(passwordBytes, 0, passwordBytes.Length); }
    }

    private static byte[] RandomBytes(int length)
    {
        byte[] value = new byte[length];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(value);
        return value;
    }

    private static byte[] Compress(byte[] plain)
    {
        using (MemoryStream output = new MemoryStream())
        {
            using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(plain, 0, plain.Length);
            return output.ToArray();
        }
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using (MemoryStream input = new MemoryStream(compressed, false))
        using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
        using (MemoryStream output = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxBackupPlainBytes) throw new Exception("备份解压后的内容过大。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        int difference = 0;
        for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
        return difference == 0;
    }

    private static void WriteBytesAtomic(string path, byte[] value)
    {
        string directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, value);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private static int RunBackupSelfTests()
    {
        string originalResourceDir = ResourceDir;
        string testRoot = Path.Combine(Path.GetTempPath(), "RainmeterBackupTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            string original = JsonUtil.Serialize(new Dictionary<string, object> {
                {"format_version", BackupFormatVersion},
                {"config_version", BackupConfigVersion},
                {"text", "跨电脑配置测试"},
                {"secret", "not-plain-text"}
            });
            byte[] encrypted = EncryptBackup(original, "correct horse battery staple", 100000);
            if (Encoding.UTF8.GetString(encrypted).Contains("not-plain-text")) return 61;
            if (DecryptBackup(encrypted, "correct horse battery staple") != original) return 62;
            try { DecryptBackup(encrypted, "wrong password"); return 63; } catch { }
            byte[] tampered = (byte[])encrypted.Clone();
            tampered[tampered.Length / 2] ^= 0x40;
            try { DecryptBackup(tampered, "correct horse battery staple"); return 64; } catch { }

            string todo = Path.Combine(testRoot, "Skins", "Todo", "@Resources");
            string calendar = Path.Combine(testRoot, "Skins", "Calendar", "@Resources");
            Directory.CreateDirectory(todo);
            Directory.CreateDirectory(calendar);
            ResourceDir = todo;
            Dictionary<string, object> paper = new Dictionary<string, object> {
                {"Version", 2},
                {"Enabled", false},
                {"DeepSeek", new Dictionary<string, object>{{"ApiKey", "portable-api-key"}}}
            };
            Dictionary<string, object> translation = new Dictionary<string, object>{{"SecretId", "id"},{"SecretKey", "key"}};
            Dictionary<string, object> caldav = new Dictionary<string, object>{{"Server", "https://calendar.invalid"},{"Username", "user"},{"Password", "pass"}};
            JsonUtil.WriteDpapiJson(PaperSyncSecret, paper);
            JsonUtil.WriteDpapiJson(TranslationSecret, translation);
            JsonUtil.WriteDpapiJson(CalDavSecret, caldav);
            WriteBackupUiScale("0.90");
            Dictionary<string, object> todoState = NewState();
            todoState["tasks"] = new List<object>{new Dictionary<string, object>{{"id", "task-1"},{"title", "portable task"}}};
            JsonUtil.SaveAtomic(StatePath, todoState);
            Dictionary<string, object> calendarState = NewBackupCalendarState();
            calendarState["series_rules"] = new List<object>{new Dictionary<string, object>{{"uid", "rule-1"}}};
            calendarState["local_events"] = new List<object>{new Dictionary<string, object>{{"id", "event-1"}}};
            JsonUtil.SaveAtomic(CalendarStatePath, calendarState);

            Dictionary<string, object> portable = BuildBackupPayload(true);
            if (JsonUtil.String(portable, "config_version", "") != BackupConfigVersion) return 65;
            Dictionary<string,object> legacy=new Dictionary<string,object>{{"format_version",1},{"config_version","1.0"},{"components",new Dictionary<string,object>{{"paper_settings",paper},{"translation",translation},{"caldav",caldav},{"ui_scale","0.90"},{"calendar_rules",new List<object>()}}}};
            UpgradeBackupPayload(legacy);ValidateBackupPayload(legacy);List<object> upgradedPlugins=JsonUtil.Array(JsonUtil.Get(JsonUtil.Object(JsonUtil.Get(legacy,"components")),"plugins"));if(JsonUtil.String(legacy,"config_version","")!="2.0"||upgradedPlugins.Count!=1||JsonUtil.String(JsonUtil.Object(upgradedPlugins[0]),"id","")!="io.github.kevendai.arxiv")return 73;
            byte[] portableEncrypted = EncryptBackup(JsonUtil.Serialize(portable), "portable backup password", 100000);
            Dictionary<string, object> reopened = JsonUtil.Object(JsonUtil.Deserialize(DecryptBackup(portableEncrypted, "portable backup password")));
            UpgradeBackupPayload(reopened);
            ValidateBackupPayload(reopened);
            File.Delete(PaperSyncSecret);
            File.Delete(TranslationSecret);
            File.Delete(CalDavSecret);
            File.Delete(StatePath);
            File.Delete(CalendarStatePath);
            ApplyBackupPayload(reopened, true, true);
            if (JsonUtil.String(JsonUtil.ReadDpapiJson(PaperSyncSecret), "Version", "") != "2") return 66;
            if (JsonUtil.String(JsonUtil.ReadDpapiJson(TranslationSecret), "SecretKey", "") != "key") return 67;
            if (JsonUtil.String(JsonUtil.ReadDpapiJson(CalDavSecret), "Password", "") != "pass") return 68;
            if (JsonUtil.Array(JsonUtil.Get(JsonUtil.LoadObject(StatePath), "tasks")).Count != 1) return 69;
            if (JsonUtil.Array(JsonUtil.Get(JsonUtil.LoadObject(CalendarStatePath), "local_events")).Count != 1) return 70;

            byte[] paperBeforeFailure = File.ReadAllBytes(PaperSyncSecret);
            Dictionary<string, object> broken = BuildBackupPayload(false);
            Dictionary<string, object> brokenComponents = JsonUtil.Object(JsonUtil.Get(broken, "components"));
            brokenComponents["paper_settings"] = new Dictionary<string, object>{{"Version", 999}};
            brokenComponents["ui_scale"] = "invalid-scale";
            try { ApplyBackupPayload(broken, true, false); return 71; } catch { }
            if (!File.ReadAllBytes(PaperSyncSecret).SequenceEqual(paperBeforeFailure)) return 72;
            return 0;
        }
        catch(Exception ex) { try{Console.Error.WriteLine(ex.ToString());File.WriteAllText(Path.Combine(testRoot,"backup-selftest-error.txt"),ex.ToString(),RuntimeUtil.Utf8NoBom);}catch{}return 60; }
        finally
        {
            ResourceDir = originalResourceDir;
            try { Directory.Delete(testRoot, true); } catch { }
        }
    }
}
