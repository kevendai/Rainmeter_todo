using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using RainmeterBackend;

internal static partial class TodoApp
{
    private sealed class EditorResult { public string Title, Target, Note, Available, Due; public List<string> Labels; }
    private static EditorResult ShowEditor(Dictionary<string, object> task)
    {
        bool editing = task != null;
        Form f = LightUi.Form(editing ? "修改待办" : "新增待办", 560, 840); int x = 28, w = 504;
        LightUi.Heading(f, editing ? "修改待办" : "新增待办", editing ? "调整待办事项，明确目标，高效执行" : "创建一项新的待办事项，明确目标，高效执行", "todo.svg");
        Button close = LightUi.Button("×", 500, 22, 34, DialogResult.Cancel); close.Height = 34; f.Controls.Add(close);

        TextBox title = Field(f, "标题 *", x, 112, w, editing ? S(task, "title") : "");
        TextBox target = FieldWithButton(f, "打开目标", x, 204, w, editing ? S(task, "target") : "", "浏览");
        TextBox available = DateField(f, "开始时间", x, 304, 230, RuntimeUtil.Date(task, "available_from"));
        TextBox due = DateField(f, "截止时间", 302, 304, 230, RuntimeUtil.Date(task, "due_at"));

        HashSet<string> selectedLabels = new HashSet<string>(editing ? Labels(task) : Enumerable.Empty<string>());
        Panel labelPanel = LabelSelector("标签", x, 400, w, CommonLabels(task), selectedLabels);
        f.Controls.Add(labelPanel);

        f.Controls.Add(LightUi.Label("备注", x, 500, w));
        Panel noteSurface = new Panel { Left = x, Top = 522, Width = w, Height = 144, BackColor = LightUi.Panel };
        LightUi.Round(noteSurface, 10);
        TextBox note = new TextBox { Left = 14, Top = 14, Width = w - 28, Height = 116, Text = editing ? S(task, "note") : "", Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, BorderStyle = BorderStyle.None, BackColor = LightUi.Panel, ForeColor = LightUi.Text, Font = new Font("Microsoft YaHei UI", 10F) };
        noteSurface.Controls.Add(note); f.Controls.Add(noteSurface);
        labelPanel.BringToFront();
        Label hint = LightUi.Label("标题为必填项。截止时间不能早于开始时间。", x, 682, 340); f.Controls.Add(hint);
        Button cancel = LightUi.Button("取消", 210, 722, 112, DialogResult.Cancel), save = LightUi.PrimaryButton(editing ? "+ 保存修改" : "+ 添加待办", 338, 722, 194, DialogResult.OK);
        f.Controls.Add(cancel); f.Controls.Add(save); f.AcceptButton = save; f.CancelButton = cancel;
        while (f.ShowDialog() == DialogResult.OK)
        {
            if (String.IsNullOrWhiteSpace(title.Text)) { LightUi.Error("标题不能为空"); continue; }
            DateTimeOffset a = default(DateTimeOffset), d = default(DateTimeOffset); string av = "", du = "";
            if (!String.IsNullOrWhiteSpace(available.Text) && !TryEditorDate(available.Text, out a)) { LightUi.Error("开始时间格式应为 YYYY-MM-DD HH:mm"); continue; } else if (!String.IsNullOrWhiteSpace(available.Text)) av = RuntimeUtil.Iso(a);
            if (!String.IsNullOrWhiteSpace(due.Text) && !TryEditorDate(due.Text, out d)) { LightUi.Error("截止时间格式应为 YYYY-MM-DD HH:mm"); continue; } else if (!String.IsNullOrWhiteSpace(due.Text)) du = RuntimeUtil.Iso(d);
            if (av != "" && du != "" && d < a) { LightUi.Error("截止时间不能早于开始时间"); continue; }
            return new EditorResult { Title = title.Text.Trim(), Target = target.Text.Trim(), Note = note.Text, Available = av, Due = du, Labels = selectedLabels.Where(v => v != "").Distinct().ToList() };
        }
        return null;
    }

    private static void ShowSettings()
    {
        Dictionary<string, object> credentials = ReadTranslationCredentials();
        PaperSettings settings = LoadPaperSettings();
        Form f = LightUi.Form("待办设置", 930, 760);
        LightUi.Heading(f, "待办设置", "配置论文推荐、DeepSeek 并发评分、文件同步、标题翻译和版本更新。", "settings.svg");
        Button close = LightUi.Button("×", 870, 22, 34, DialogResult.Cancel);
        close.Height = 34;
        f.Controls.Add(close);

        CheckBox enabled = new CheckBox { Left = 28, Top = 102, Width = 220, Height = 28, Text = "启用论文推荐", Checked = settings.Enabled, ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
        Label masterHint = LightUi.Label("关闭后启动和刷新都不会访问论文服务。", 250, 106, 500);
        f.Controls.AddRange(new Control[] { enabled, masterHint });

        string[] pageNames = { "论文推荐", "DeepSeek API", "筛选与评分", "文件同步", "标题翻译", "关于" };
        Panel navigation = new Panel { Left = 28, Top = 148, Width = 150, Height = 492, BackColor = Color.FromArgb(235, 245, 253) };
        LightUi.Round(navigation, 12);
        Panel content = new Panel { Left = 194, Top = 148, Width = 708, Height = 492, BackColor = Color.Transparent };
        f.Controls.AddRange(new Control[] { navigation, content });
        List<Button> tabs = new List<Button>();
        List<Panel> pages = new List<Panel>();
        for (int i = 0; i < pageNames.Length; i++)
        {
            Button tab = LightUi.Button(pageNames[i], 8, 8 + i * 56, 134, DialogResult.None);
            tab.Height = 46; tab.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            navigation.Controls.Add(tab); tabs.Add(tab);
            Panel page = new Panel { Left = 0, Top = 0, Width = 708, Height = 492, BackColor = Color.Transparent, AutoScroll = true, Visible = false };
            content.Controls.Add(page); pages.Add(page);
        }

        int w = 660;
        TextBox importCount = Field(pages[0], "每天导入论文数量（1-20）", 12, 12, 310, settings.ImportCount.ToString(CultureInfo.InvariantCulture));
        TextBox cacheDays = Field(pages[0], "缓存保留天数（1-90）", 342, 12, 306, settings.CacheDays.ToString(CultureInfo.InvariantCulture));
        Label jobState = new Label { Left = 12, Top = 126, Width = 636, Height = 76, Text = "当前状态：" + ReadPaperJobMessage("暂无后台评分任务"), ForeColor = LightUi.Text, BackColor = LightUi.Surface, Padding = new Padding(14), Font = new Font("Microsoft YaHei UI", 10F) };
        LightUi.Round(jobState, 10);
        Label generalHint = LightUi.Label("启动时仅在 08:00–20:00 读取本地或远端完整文件；只有手动刷新并确认后才会调用 DeepSeek。", 12, 222, 636);
        generalHint.Height = 48;
        pages[0].Controls.AddRange(new Control[] { jobState, generalHint });

        TextBox apiUrl = Field(pages[1], "Chat Completions 地址", 12, 12, w, settings.ApiBaseUrl);
        TextBox apiModel = Field(pages[1], "模型", 12, 106, w, settings.Model);
        TextBox apiKey = PasswordField(pages[1], "API Key", 12, 200, w, settings.ApiKey);
        TextBox concurrency = Field(pages[1], "最大并发（1-32，默认 8）", 12, 294, 310, settings.MaxConcurrency.ToString(CultureInfo.InvariantCulture));
        TextBox timeout = Field(pages[1], "请求超时秒数（30-600）", 342, 294, 330, settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Button testApi = LightUi.Button("测试 DeepSeek", 502, 408, 170, DialogResult.None);
        pages[1].Controls.Add(testApi);

        TextBox categories = Field(pages[2], "包含分类（逗号分隔）", 12, 12, w, settings.Categories);
        TextBox excludes = Field(pages[2], "排除分类（逗号分隔）", 12, 106, w, settings.ExcludeCategories);
        TextBox threshold = Field(pages[2], "标题进入摘要评分阈值（0-10）", 12, 200, 206, settings.TitleThreshold.ToString(CultureInfo.InvariantCulture));
        TextBox titleBatch = Field(pages[2], "标题批大小（1-50）", 230, 200, 206, settings.TitleBatchSize.ToString(CultureInfo.InvariantCulture));
        TextBox abstractBatch = Field(pages[2], "摘要批大小（1-20）", 448, 200, 224, settings.AbstractBatchSize.ToString(CultureInfo.InvariantCulture));
        TextBox titlePrompt = MultiLineField(pages[2], "标题评分提示词", 12, 294, w, 160, settings.TitlePrompt);
        TextBox abstractPrompt = MultiLineField(pages[2], "摘要评分提示词", 12, 490, w, 190, settings.AbstractPrompt);
        pages[2].AutoScrollMinSize = new Size(0, 710);

        CheckBox fileEnabled = new CheckBox { Left = 12, Top = 12, Width = 220, Height = 26, Text = "启用文件服务器同步", Checked = settings.FileServerEnabled, ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
        TextBox fileUrl = Field(pages[3], "File Browser 地址", 12, 58, w, settings.FileBaseUrl);
        TextBox fileAccount = Field(pages[3], "账号", 12, 152, w, settings.FileAccount);
        TextBox filePassword = PasswordField(pages[3], "密码", 12, 246, w, settings.FilePassword);
        Button testFile = LightUi.Button("测试文件服务器", 502, 374, 170, DialogResult.None);
        pages[3].Controls.AddRange(new Control[] { fileEnabled, testFile });

        TextBox secretId = Field(pages[4], "Tencent Cloud SecretId", 12, 12, w, S(credentials, "SecretId"));
        TextBox secretKey = PasswordField(pages[4], "Tencent Cloud SecretKey", 12, 106, w, S(credentials, "SecretKey"));
        Label translationStatus = LightUi.Label(File.Exists(TranslationSecret) ? "已保存翻译凭据" : "尚未配置翻译凭据；未配置时论文标题保留英文。", 12, 214, 636);
        Button clearTranslation = LightUi.DangerButton("清除翻译", 350, 266, 140, DialogResult.None);
        Button testTranslation = LightUi.Button("测试翻译", 502, 266, 140, DialogResult.None);
        pages[4].Controls.AddRange(new Control[] { translationStatus, clearTranslation, testTranslation });

        Label aboutTitle = new Label { Text = "Rainmeter Desktop Widgets", Left = 12, Top = 18, Width = 636, Height = 36, ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold) };
        Label aboutVersion = new Label { Text = "当前版本：" + AppVersion + "（" + AppEditionName + "）", Left = 12, Top = 72, Width = 636, Height = 26, ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
        Label aboutRepo = LightUi.Label("更新源：github.com/kevendai/Rainmeter_todo", 12, 112, 636);
        Label updateStatus = LightUi.Label("尚未检查更新", 12, 158, 420);
        Button checkUpdate = LightUi.PrimaryButton("检查更新", 502, 146, 146, DialogResult.None);
        pages[5].Controls.AddRange(new Control[] { aboutTitle, aboutVersion, aboutRepo, updateStatus, checkUpdate });

        Label saveStatus = LightUi.Label(File.Exists(PaperSyncSecret) ? "论文配置已加密保存" : "尚未保存论文配置", 194, 666, 470);
        saveStatus.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        Button clearPaper = LightUi.DangerButton("清除论文配置", 666, 654, 112, DialogResult.None);
        Button saveAll = LightUi.PrimaryButton("保存设置", 790, 654, 112, DialogResult.None);
        f.Controls.AddRange(new Control[] { saveStatus, clearPaper, saveAll });

        Action<int> showPage = delegate(int selected) {
            for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == selected; PaintTabButton(tabs[i], i == selected); }
        };
        for (int i = 0; i < tabs.Count; i++) { int selected = i; tabs[i].Click += delegate { showPage(selected); }; }
        showPage(0);

        Action updatePaperEnabled = delegate {
            for (int i = 0; i < 4; i++) SetChildrenEnabled(pages[i], enabled.Checked);
            enabled.Enabled = true;
        };
        enabled.CheckedChanged += delegate { updatePaperEnabled(); };
        updatePaperEnabled();

        Func<PaperSettings> collect = delegate {
            PaperSettings value = new PaperSettings();
            value.Enabled = enabled.Checked;
            value.ApiBaseUrl = apiUrl.Text;
            value.ApiKey = apiKey.Text;
            value.Model = apiModel.Text;
            value.MaxConcurrency = ParseSettingInt(concurrency.Text, 1, 32, "最大并发");
            value.TimeoutSeconds = ParseSettingInt(timeout.Text, 30, 600, "请求超时");
            value.FileServerEnabled = fileEnabled.Checked;
            value.FileBaseUrl = fileUrl.Text;
            value.FileAccount = fileAccount.Text;
            value.FilePassword = filePassword.Text;
            value.Categories = categories.Text;
            value.ExcludeCategories = excludes.Text;
            value.TitleThreshold = ParseSettingInt(threshold.Text, 0, 10, "标题阈值");
            value.TitleBatchSize = ParseSettingInt(titleBatch.Text, 1, 50, "标题批大小");
            value.AbstractBatchSize = ParseSettingInt(abstractBatch.Text, 1, 20, "摘要批大小");
            value.TitlePrompt = titlePrompt.Text;
            value.AbstractPrompt = abstractPrompt.Text;
            value.ImportCount = ParseSettingInt(importCount.Text, 1, 20, "导入数量");
            value.CacheDays = ParseSettingInt(cacheDays.Text, 1, 90, "缓存天数");
            return value;
        };

        saveAll.Click += delegate {
            try
            {
                PaperSettings value = collect();
                SavePaperSettings(value);
                if (!String.IsNullOrWhiteSpace(secretId.Text) || !String.IsNullOrWhiteSpace(secretKey.Text))
                    SaveTranslationCredentials(secretId.Text, secretKey.Text);
                saveStatus.Text = "设置已保存；论文凭据使用 Windows DPAPI CurrentUser 加密";
                saveStatus.ForeColor = Color.FromArgb(63, 178, 119);
            }
            catch (Exception ex) { saveStatus.Text = "保存失败"; saveStatus.ForeColor = LightUi.Danger; LightUi.Error(ex.Message); }
        };
        clearPaper.Click += delegate {
            try
            {
                if (File.Exists(PaperSyncSecret)) File.Delete(PaperSyncSecret);
                enabled.Checked = false; apiKey.Text = ""; fileUrl.Text = ""; fileAccount.Text = ""; filePassword.Text = ""; fileEnabled.Checked = false;
                saveStatus.Text = "论文配置已清除";
                saveStatus.ForeColor = LightUi.Muted;
            }
            catch (Exception ex) { LightUi.Error(ex.Message); }
        };
        testApi.Click += delegate {
            try { testApi.Enabled = false; testApi.Text = "测试中..."; Application.DoEvents(); TestDeepSeekConnection(collect()); saveStatus.Text = "DeepSeek 测试成功"; saveStatus.ForeColor = Color.FromArgb(63, 178, 119); }
            catch (Exception ex) { LightUi.Error("DeepSeek 测试失败：" + ex.Message); }
            finally { testApi.Enabled = true; testApi.Text = "测试 DeepSeek"; }
        };
        testFile.Click += delegate {
            try { TestFileServerConnection(collect()); saveStatus.Text = "文件服务器登录成功"; saveStatus.ForeColor = Color.FromArgb(63, 178, 119); }
            catch (Exception ex) { LightUi.Error("文件服务器测试失败：" + ex.Message); }
        };
        testTranslation.Click += delegate {
            try { string result = TestTranslationCredentials(secretId.Text, secretKey.Text); translationStatus.Text = "连接成功：" + result; translationStatus.ForeColor = Color.FromArgb(63, 178, 119); }
            catch (Exception ex) { LightUi.Error("翻译测试失败：" + ex.Message); }
        };
        clearTranslation.Click += delegate {
            try
            {
                if (File.Exists(TranslationSecret)) File.Delete(TranslationSecret);
                secretId.Text = "";
                secretKey.Text = "";
                translationStatus.Text = "尚未配置翻译凭据；论文标题将保留英文";
                translationStatus.ForeColor = LightUi.Muted;
            }
            catch (Exception ex) { LightUi.Error(ex.Message); }
        };

        checkUpdate.Click += delegate {
            try
            {
                checkUpdate.Enabled = false;
                updateStatus.Text = "正在检查 GitHub...";
                updateStatus.ForeColor = LightUi.Muted;
                updateStatus.Refresh();
                Application.DoEvents();
                UpdateCheckResult info = CheckLatestUpdate();
                if (!info.IsNewer)
                {
                    updateStatus.Text = info.CompareResult == 0 ? "已是最新版本：" + info.Tag : "当前版本高于最新标签：" + info.Tag;
                    updateStatus.ForeColor = Color.FromArgb(63, 178, 119);
                    return;
                }
                updateStatus.Text = "检测到新版本：" + info.Tag;
                updateStatus.ForeColor = LightUi.Accent;
                DialogResult update = MessageBox.Show(
                    "检测到新版本 " + info.Tag + "（统一版）。\r\n\r\n是否现在下载并自动部署？部署脚本会重启 Rainmeter。",
                    "检查更新",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (update != DialogResult.Yes)
                {
                    updateStatus.Text = "已取消更新：" + info.Tag;
                    updateStatus.ForeColor = LightUi.Muted;
                    return;
                }
                updateStatus.Text = "正在启动独立升级器...";
                updateStatus.ForeColor = LightUi.Muted;
                updateStatus.Refresh();
                Application.DoEvents();
                StartExternalUpdater();
                updateStatus.Text = "已启动独立升级器";
                updateStatus.ForeColor = Color.FromArgb(63, 178, 119);
                f.BeginInvoke(new Action(f.Close));
            }
            catch (Exception ex)
            {
                updateStatus.Text = "检查更新失败";
                updateStatus.ForeColor = LightUi.Danger;
                LightUi.Error("检查更新失败：" + ex.Message);
            }
            finally { checkUpdate.Enabled = true; }
        };

        f.CancelButton = close;
        f.ShowDialog();
    }

    private static int ParseSettingInt(string text, int minimum, int maximum, string name)
    {
        int value;
        if (!Int32.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
            throw new Exception(name + "必须是 " + minimum + "-" + maximum + " 的整数");
        return value;
    }

    private static void SetChildrenEnabled(Control parent, bool enabled)
    {
        foreach (Control child in parent.Controls)
        {
            child.Enabled = enabled;
            if (child.HasChildren) SetChildrenEnabled(child, enabled);
        }
    }

    private static TextBox MultiLineField(Control parent, string label, int x, int y, int width, int height, string text)
    {
        parent.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = height, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 12, Width = width - 28, Height = height - 24, Text = text ?? "", Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, BorderStyle = BorderStyle.None, BackColor = LightUi.Panel, ForeColor = LightUi.Text, Font = new Font("Microsoft YaHei UI", 9F) };
        surface.Controls.Add(box); parent.Controls.Add(surface);
        return box;
    }

    private static IEnumerable<string> CommonLabels(Dictionary<string, object> task)
    {
        string[] defaults = { "论文", "考试", "功能", "修复", "日程", "已读", "自动归档" };
        return defaults.Concat(task == null ? Enumerable.Empty<string>() : Labels(task)).Where(x => !String.IsNullOrWhiteSpace(x)).Distinct();
    }

    private static Panel LabelSelector(string title, int x, int y, int width, IEnumerable<string> options, HashSet<string> selected)
    {
        Panel panel = new Panel { Left = x, Top = y, Width = width, Height = 86, BackColor = Color.Transparent };
        panel.Controls.Add(LightUi.Label(title, 0, 0, width));
        Panel surface = new Panel { Left = 0, Top = 28, Width = width, Height = 56, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        panel.Controls.Add(surface);
        Button expand = LightUi.Button("展开", width - 66, 12, 52, DialogResult.None);
        expand.Height = 30;
        expand.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        expand.UseVisualStyleBackColor = false;
        expand.BackColor = LightUi.AccentFill;
        expand.ForeColor = Color.White;
        expand.FlatAppearance.BorderColor = Color.FromArgb(31, 103, 201);
        expand.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 116, 224);
        expand.FlatAppearance.MouseDownBackColor = Color.FromArgb(22, 88, 176);
        expand.MouseEnter += delegate { if (expand.Enabled) expand.BackColor = Color.FromArgb(31, 116, 224); };
        expand.MouseLeave += delegate { expand.BackColor = LightUi.AccentFill; };
        surface.Controls.Add(expand);
        int left = 12, top = 13;
        foreach (string label in options)
        {
            int buttonWidth = Math.Max(58, Math.Min(104, TextRenderer.MeasureText(label, new Font("Microsoft YaHei UI", 9F)).Width + 28));
            if (left + buttonWidth > width - 76) { left = 12; top += 32; }
            Button button = LightUi.Button(label, left, top, buttonWidth, DialogResult.None);
            button.Height = 28;
            button.Tag = label;
            button.Visible = top == 13;
            PaintLabelChoice(button, selected.Contains(label));
            button.Click += delegate(object sender, EventArgs e) {
                Button current = (Button)sender;
                string value = Convert.ToString(current.Tag);
                if (selected.Contains(value)) selected.Remove(value); else selected.Add(value);
                PaintLabelChoice(current, selected.Contains(value));
            };
            button.MouseEnter += delegate(object sender, EventArgs e) {
                Button current = (Button)sender;
                PaintLabelChoice(current, selected.Contains(Convert.ToString(current.Tag)));
            };
            button.MouseLeave += delegate(object sender, EventArgs e) {
                Button current = (Button)sender;
                PaintLabelChoice(current, selected.Contains(Convert.ToString(current.Tag)));
            };
            surface.Controls.Add(button);
            left += buttonWidth + 8;
        }
        bool expanded = false;
        expand.Click += delegate {
            expanded = !expanded;
            surface.Height = expanded ? 94 : 56;
            panel.Height = expanded ? 126 : 86;
            expand.Text = expanded ? "收起" : "展开";
            expand.BackColor = LightUi.AccentFill;
            expand.ForeColor = Color.White;
            if (expanded) panel.BringToFront();
            foreach (Control control in surface.Controls)
            {
                Button chip = control as Button;
                if (chip != null && chip != expand) chip.Visible = expanded || chip.Top == 13;
            }
        };
        return panel;
    }

    private static void PaintLabelChoice(Button button, bool active)
    {
        button.BackColor = active ? Color.FromArgb(220, 238, 255) : LightUi.Panel;
        button.ForeColor = active ? LightUi.Accent : LightUi.Text;
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
    }

    private static TextBox Field(Control f, string label, int x, int y, int width, string text)
    {
        f.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 28, Height = 24, AutoSize = false, Text = text ?? "", BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
        surface.Controls.Add(box);
        f.Controls.Add(surface);
        return box;
    }

    private static TextBox PasswordField(Control f, string label, int x, int y, int width, string text)
    {
        f.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 92, Height = 24, AutoSize = false, Text = text ?? "", UseSystemPasswordChar = true, BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
        Button reveal = LightUi.Button("显示", width - 70, 8, 56, DialogResult.None);
        reveal.Height = 34;
        reveal.Click += delegate {
            box.UseSystemPasswordChar = !box.UseSystemPasswordChar;
            reveal.Text = box.UseSystemPasswordChar ? "显示" : "隐藏";
        };
        surface.Controls.Add(box);
        surface.Controls.Add(reveal);
        f.Controls.Add(surface);
        return box;
    }

    private static TextBox FieldWithButton(Form f, string label, int x, int y, int width, string text, string buttonText)
    {
        f.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 98, Height = 24, AutoSize = false, Text = text ?? "", BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
        Button browse = LightUi.Button(buttonText, width - 78, 8, 64, DialogResult.None);
        browse.Height = 34;
        browse.Click += delegate {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择打开目标";
                dialog.CheckFileExists = false;
                dialog.CheckPathExists = true;
                dialog.Filter = "所有文件 (*.*)|*.*";
                if (dialog.ShowDialog() == DialogResult.OK) box.Text = dialog.FileName;
            }
        };
        surface.Controls.Add(box); surface.Controls.Add(browse); f.Controls.Add(surface);
        return box;
    }

    private static TextBox SearchField(Form f, int x, int y, int width)
    {
        Panel surface = new Panel { Left = x, Top = y, Width = width, Height = 42, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        Label icon = new Label { Left = 12, Top = 11, Width = 22, Height = 22, Text = "\xE721", Font = new Font("Segoe Fluent Icons", 9F), ForeColor = LightUi.Muted, BackColor = Color.Transparent };
        TextBox box = new TextBox { Left = 38, Top = 12, Width = width - 50, Height = 22, AutoSize = false, Text = "", BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 9F) };
        surface.Controls.Add(icon); surface.Controls.Add(box); f.Controls.Add(surface);
        return box;
    }

    private static TextBox DateField(Form f, string label, int x, int y, int width, DateTimeOffset? value)
    {
        f.Controls.Add(LightUi.Label(label, x, y, width));
        Panel surface = new Panel { Left = x, Top = y + 26, Width = width, Height = 50, BackColor = LightUi.Panel };
        LightUi.Round(surface, 10);
        TextBox box = new TextBox { Left = 14, Top = 15, Width = width - 58, Height = 24, AutoSize = false, Text = DateEdit(value), ReadOnly = true, BackColor = LightUi.Panel, ForeColor = LightUi.Text, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10F) };
        Button choose = LightUi.Button("\xE787", width - 42, 8, 30, DialogResult.None);
        choose.Height = 34;
        choose.Font = new Font("Segoe Fluent Icons", 9F);
        choose.Click += delegate {
            string picked = PickDateTime(box.Text);
            if (picked != null) box.Text = picked;
        };
        surface.Controls.Add(box); surface.Controls.Add(choose); f.Controls.Add(surface);
        return box;
    }

    private static string PickDateTime(string current)
    {
        DateTime initial;
        if (!DateTime.TryParseExact(current, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out initial)) initial = DateTime.Now;
        Form dialog = LightUi.Form("选择时间", 360, 210);
        LightUi.Heading(dialog, "选择时间", "选择日期和时间；清空表示不限制。");
        DateTimePicker picker = new DateTimePicker { Left = 26, Top = 92, Width = 308, Height = 32, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Value = initial, Font = new Font("Microsoft YaHei UI", 10F) };
        Button clear = LightUi.Button("清空", 82, 150, 76, DialogResult.Retry);
        Button cancel = LightUi.Button("取消", 168, 150, 76, DialogResult.Cancel);
        Button ok = LightUi.PrimaryButton("确定", 254, 150, 80, DialogResult.OK);
        dialog.Controls.AddRange(new Control[] { picker, clear, cancel, ok });
        DialogResult result = dialog.ShowDialog();
        if (result == DialogResult.OK) return picker.Value.ToString("yyyy-MM-dd HH:mm");
        if (result == DialogResult.Retry) return "";
        return null;
    }
    private static string DateEdit(DateTimeOffset? value) { return value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : ""; }
    private static bool TryEditorDate(string text, out DateTimeOffset result)
    {
        DateTime local; if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) { result = default(DateTimeOffset); return false; }
        result = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)); return true;
    }


    private static void Manage(Dictionary<string, object> state, ref bool refresh)
    {
        bool managerChanged = false;
        Form f = LightUi.Form("全部任务", 1120, 760); LightUi.Heading(f, "全部任务", "管理你的所有待办事项，支持批量操作", "all-tasks.svg");
        Button close = LightUi.Button("×", 1054, 22, 36, DialogResult.Cancel); close.Height = 34; f.Controls.Add(close);
        TextBox search = SearchField(f, 610, 38, 378);
        int filter = 0;
        CheckBox onlyOpen = new CheckBox { Left = 944, Top = 128, Width = 130, Height = 24, Text = "只看未完成", ForeColor = LightUi.Text, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F) };
        f.Controls.Add(onlyOpen);
        Button allTab = LightUi.Button("全部  0", 32, 118, 96, DialogResult.None), overdueTab = LightUi.Button("逾期  0", 138, 118, 96, DialogResult.None), futureTab = LightUi.Button("未开始  0", 244, 118, 104, DialogResult.None), pendingTab = LightUi.Button("待办  0", 358, 118, 96, DialogResult.None), doneTab = LightUi.Button("已办  0", 464, 118, 96, DialogResult.None);
        f.Controls.AddRange(new Control[]{allTab,overdueTab,futureTab,pendingTab,doneTab});
        Panel table = new Panel { Left = 32, Top = 180, Width = 1056, Height = 470, BackColor = Color.FromArgb(247, 251, 255), AutoScroll = true };
        LightUi.EnableDoubleBuffer(table);
        LightUi.Round(table, 12); f.Controls.Add(table);
        Panel footer = new Panel { Left = 32, Top = 682, Width = 1056, Height = 54, BackColor = Color.FromArgb(245, 251, 255) };
        LightUi.EnableDoubleBuffer(footer);
        LightUi.Round(footer, 12); f.Controls.Add(footer);
        Label selectionHint = LightUi.Label("已选择 0 项", 18, 16, 240); footer.Controls.Add(selectionHint);
        Button edit = LightUi.Button("修改选中项", 604, 8, 112, DialogResult.None), toggle = LightUi.Button("批量完成", 728, 8, 112, DialogResult.None), delete = LightUi.DangerButton("删除", 852, 8, 76, DialogResult.None), add = LightUi.PrimaryButton("+ 新建待办", 940, 8, 100, DialogResult.None);
        footer.Controls.AddRange(new Control[]{edit,toggle,delete,add}); f.CancelButton = close;
        List<CheckBox> rowChecks = new List<CheckBox>();
        Dictionary<string, Panel> rowPanels = new Dictionary<string, Panel>();
        string selectedId = "";
        Action paintTabs = delegate {
            Button[] tabs = { allTab, overdueTab, futureTab, pendingTab, doneTab };
            for (int i = 0; i < tabs.Length; i++) PaintTabButton(tabs[i], filter == i);
        };
        Action paintRows = delegate {
            foreach (KeyValuePair<string, Panel> pair in rowPanels)
                pair.Value.BackColor = pair.Key == selectedId ? Color.FromArgb(232, 244, 255) : Color.FromArgb(247, 251, 255);
        };
        MouseEventHandler selectRow = delegate(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            Control control = sender as Control;
            while (control != null && !(control.Tag is string)) control = control.Parent;
            if (control == null) return;
            selectedId = Convert.ToString(control.Tag);
            paintRows();
        };
        Action<bool> reload = null;
        reload = delegate(bool preserveScroll) {
            int previousScrollY = preserveScroll ? Math.Max(0, -table.AutoScrollPosition.Y) : 0;
            // Clearing rows while the panel is still scrolled leaves its negative display
            // offset in the next layout pass. Reset first, then restore the clamped offset
            // after the rebuilt controls have established the new scroll range.
            table.AutoScrollPosition = Point.Empty;
            List<Dictionary<string, object>> all = Tasks(state);
            DateTimeOffset now = DateTimeOffset.Now;
            allTab.Text = "全部  " + all.Count;
            overdueTab.Text = "逾期  " + all.Count(t => !B(t, "completed") && RuntimeUtil.Date(t, "due_at").HasValue && now > RuntimeUtil.Date(t, "due_at").Value);
            futureTab.Text = "未开始  " + all.Count(t => !B(t, "completed") && RuntimeUtil.Date(t, "available_from").HasValue && now < RuntimeUtil.Date(t, "available_from").Value);
            pendingTab.Text = "待办  " + all.Count(t => !B(t, "completed") && (!RuntimeUtil.Date(t, "due_at").HasValue || now <= RuntimeUtil.Date(t, "due_at").Value) && (!RuntimeUtil.Date(t, "available_from").HasValue || now >= RuntimeUtil.Date(t, "available_from").Value));
            doneTab.Text = "已办  " + all.Count(t => B(t, "completed"));
            string query = search.Text.Trim();
            table.SuspendLayout(); LightUi.SetRedraw(table, false); table.Controls.Clear(); rowChecks.Clear(); rowPanels.Clear();
            AddCellLabel(table, "状态", 50, 14, 70, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "标题", 128, 14, 350, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "标签", 486, 14, 140, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "开始时间", 640, 14, 132, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "截止时间", 788, 14, 132, LightUi.Muted, FontStyle.Bold);
            AddCellLabel(table, "操作", 936, 14, 86, LightUi.Muted, FontStyle.Bold);
            int y = 42;
            foreach (Dictionary<string, object> t in all.Where(t => TaskMatchesFilter(t, filter, now)).Where(t => !onlyOpen.Checked || !B(t, "completed")).Where(t => query == "" || S(t, "title").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || String.Join("、", Labels(t)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(t => B(t,"completed") ? 3 : (RuntimeUtil.Date(t,"due_at").HasValue && now > RuntimeUtil.Date(t,"due_at").Value ? 0 : RuntimeUtil.Date(t,"available_from").HasValue && now < RuntimeUtil.Date(t,"available_from").Value ? 1 : 2)).ThenByDescending(t => RuntimeUtil.Date(t,"created_at") ?? DateTimeOffset.MinValue)) {
                string id = S(t, "id");
                Panel row = new Panel { Left = 12, Top = y, Width = 1016, Height = 42, BackColor = Color.FromArgb(247, 251, 255), Tag = id, Cursor = Cursors.Hand };
                LightUi.EnableDoubleBuffer(row);
                LightUi.Round(row, 8);
                row.MouseDown += selectRow;
                CheckBox check = new CheckBox { Left = 6, Top = 11, Width = 20, Height = 20, BackColor = Color.Transparent, Tag = id };
                check.CheckedChanged += delegate { selectionHint.Text = "已选择 " + rowChecks.Count(c => c.Checked) + " 项"; };
                row.Controls.Add(check); rowChecks.Add(check);
                AddCellLabel(row, TaskStatusText(t, now), 36, 11, 70, TaskStatusColor(t, now), FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, S(t,"title"), 114, 11, 350, LightUi.Text, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, String.Join("  ", Labels(t)), 472, 11, 140, LightUi.Accent, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, DateEdit(RuntimeUtil.Date(t,"available_from")) == "" ? "一" : DateEdit(RuntimeUtil.Date(t,"available_from")), 626, 11, 132, LightUi.Text, FontStyle.Regular).MouseDown += selectRow;
                AddCellLabel(row, DateEdit(RuntimeUtil.Date(t,"due_at")) == "" ? "一" : DateEdit(RuntimeUtil.Date(t,"due_at")), 774, 11, 132, LightUi.Text, FontStyle.Regular).MouseDown += selectRow;
                Button openBtn = RowIcon("\xE72A", 914, 5);
                Button editBtn = RowIcon("\xE70F", 948, 5);
                Button deleteBtn = RowIcon("\xE74D", 982, 5);
                openBtn.Click += delegate { bool changed=false; Open(state, id, ref changed); managerChanged |= changed; if (changed) reload(true); };
                editBtn.Click += delegate { bool changed=false; Edit(state, id, ref changed); managerChanged |= changed; if (changed) reload(true); };
                deleteBtn.Click += delegate { bool changed=false; Delete(state, id, ref changed); managerChanged |= changed; if (changed) reload(true); };
                row.Controls.Add(openBtn); row.Controls.Add(editBtn); row.Controls.Add(deleteBtn);
                table.Controls.Add(row); rowPanels[id] = row; y += 42;
            }
            table.ResumeLayout();
            int maxScrollY = Math.Max(0, table.DisplayRectangle.Height - table.ClientSize.Height);
            if (previousScrollY > 0) table.AutoScrollPosition = new Point(0, Math.Min(previousScrollY, maxScrollY));
            LightUi.SetRedraw(table, true); paintTabs(); paintRows(); selectionHint.Text = "已选择 " + rowChecks.Count(c => c.Checked) + " 项";
        };
        allTab.Click += delegate { filter = 0; reload(false); };
        overdueTab.Click += delegate { filter = 1; reload(false); };
        futureTab.Click += delegate { filter = 2; reload(false); };
        pendingTab.Click += delegate { filter = 3; reload(false); };
        doneTab.Click += delegate { filter = 4; reload(false); };
        search.TextChanged += delegate { reload(false); };
        onlyOpen.CheckedChanged += delegate { reload(false); };
        search.Parent.BringToFront();
        search.BringToFront();
        close.BringToFront();
        reload(false);
        edit.Click += delegate { if (selectedId == "") { selectionHint.Text="请先选中一项需要修改的任务。"; selectionHint.ForeColor=LightUi.Danger; return; } bool changed=false; Edit(state, selectedId, ref changed); managerChanged |= changed; selectionHint.ForeColor=LightUi.Muted; if (changed) reload(true); };
        toggle.Click += delegate { List<string> selected=rowChecks.Where(c=>c.Checked).Select(c=>Convert.ToString(c.Tag)).ToList(); if(selected.Count==0){selectionHint.Text="请先勾选需要完成或恢复的任务。";selectionHint.ForeColor=LightUi.Danger;return;} foreach (string id in selected) { bool changed=false; Toggle(state,id,ref changed); managerChanged |= changed; } selectionHint.ForeColor=LightUi.Muted; reload(true); };
        delete.Click += delegate { List<string> selected=rowChecks.Where(c=>c.Checked).Select(c=>Convert.ToString(c.Tag)).ToList(); if(selected.Count==0){selectionHint.Text="请先勾选需要删除的任务。";selectionHint.ForeColor=LightUi.Danger;return;} if(!LightUi.Confirm("确定删除勾选的 "+selected.Count+" 项任务？","批量删除"))return; foreach (string id in selected) Tasks(state).RemoveAll(t => S(t, "id") == id); Meta(state)["status"]="已批量删除";Commit(state);managerChanged=true;selectionHint.ForeColor=LightUi.Muted;reload(true); };
        add.Click += delegate { bool changed=false; Add(state, ref changed); managerChanged |= changed; if (changed) reload(false); };
        table.DoubleClick += delegate { edit.PerformClick(); }; f.ShowDialog(); refresh |= managerChanged;
    }

    private static Label AddCellLabel(Control parent, string text, int x, int y, int width, Color color, FontStyle style)
    {
        Label label = new Label { Left = x, Top = y, Width = width, Height = 22, Text = text, ForeColor = color, BackColor = Color.Transparent, AutoEllipsis = true, Font = new Font("Microsoft YaHei UI", 9F, style) };
        parent.Controls.Add(label);
        return label;
    }

    private static Button RowIcon(string text, int x, int y)
    {
        Button button = LightUi.Button(text, x, y, 28, DialogResult.None);
        button.Height = 30;
        button.Font = new Font("Segoe Fluent Icons", 9F);
        button.BackColor = Color.FromArgb(247, 251, 255);
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private static void PaintTabButton(Button button, bool active)
    {
        button.BackColor = active ? Color.FromArgb(220, 238, 255) : LightUi.Panel;
        button.ForeColor = active ? LightUi.Accent : LightUi.Text;
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
    }

    private static string TaskStatusText(Dictionary<string, object> task, DateTimeOffset now)
    {
        bool completed = B(task, "completed");
        bool overdue = !completed && RuntimeUtil.Date(task, "due_at").HasValue && now > RuntimeUtil.Date(task, "due_at").Value;
        bool future = !completed && RuntimeUtil.Date(task, "available_from").HasValue && now < RuntimeUtil.Date(task, "available_from").Value;
        return completed ? "已办" : overdue ? "逾期" : future ? "未开始" : "待办";
    }

    private static Color TaskStatusColor(Dictionary<string, object> task, DateTimeOffset now)
    {
        string status = TaskStatusText(task, now);
        if (status == "已办") return Color.FromArgb(28, 145, 82);
        if (status == "逾期") return LightUi.Danger;
        if (status == "未开始") return Color.FromArgb(145, 96, 28);
        return LightUi.Accent;
    }

}

