using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using RainmeterBackend;

// Phase 7 离线探针：设置页的分组/高级项（规格 §7.4）、服务绑定的选择规则（§8）、
// 以及「启用标题翻译」这类依赖行在无 provider 时必须置灰且**不改存储值**（§11-#7/#8）。
//
// 两段覆盖：
//   1) 纯模型层：Parse / Group / Inspect / RequiresSatisfied / ApplyBindings；
//   2) 真对话框：用反射调 TodoApp.ShowPluginConfig，靠一个 Timer 在窗体上做断言，
//      最后直接把 DialogResult 置成 OK，让**保存路径**也在无人值守下真跑一遍。
//      （宿主保存后会用本进程的副本当插件的 validate_settings 桩，所以 Main 带参时必须立即返回 0。）
internal static class PluginSettingsProbe
{
    private const string ConsumerId = "io.github.test.consumer";
    private const string ProviderA = "io.github.test.provider-a";
    private const string ProviderB = "io.github.test.provider-b";
    private const string ProviderOff = "io.github.test.provider-off";
    private const string ProviderCache = "io.github.test.provider-cache";

    private static int checks, failures;
    private static string root = "";

    [STAThread]
    private static int Main(string[] args)
    {
        // 宿主保存设置后会拿本进程的副本当插件入口跑 `validate_settings`：动作走 stdin 的请求 JSON，
        // **不是**命令行参数，所以只能靠宿主注入的 RW_PLUGIN_ID 判断"我现在是插件子进程"。
        if (args != null && args.Length > 0) return ValidateSettingsStub();
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable(PluginRuntime.PluginIdVariable))) return ValidateSettingsStub();
        root = Path.Combine(Path.GetTempPath(), "rwps-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("RAINMETER_PLUGIN_ROOT", root);
        try
        {
            Application.EnableVisualStyles();
            Fixture();
            PluginManifest manifest = PluginManifest.Load(PluginPaths.VersionRoot(ConsumerId, "1.0.0"));
            LayoutSection(manifest);
            ServiceSection(manifest);
            BindingSection(manifest);
            UiSection(manifest);
        }
        catch (Exception ex)
        {
            checks++; failures++;
            Console.Error.WriteLine("FAIL: unexpected " + ex.GetType().FullName + ": " + ex.Message);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("RAINMETER_PROBE_KEEP") == "1") Console.Error.WriteLine("ROOT: " + root);
            else try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine("PASS plugin settings probe: checks=" + checks + " failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    // 宿主保存设置后会拿本进程的副本当插件入口，跑 `validate_settings`：必须回一行 request_id
    // 相同的 result，宿主才不会判"插件未返回最终结果 / request_id 不匹配"。
    private static int ValidateSettingsStub()
    {
        string requestId = "";
        try
        {
            string line = Console.In.ReadLine();
            if (!String.IsNullOrWhiteSpace(line))
                requestId = JsonUtil.String(JsonUtil.Object(JsonUtil.Deserialize(line)), "request_id", "");
        }
        catch { }
        Console.WriteLine("{\"type\":\"result\",\"request_id\":\"" + requestId + "\",\"ok\":true,\"payload\":{},\"error\":\"\"}");
        return 0;
    }

    // ---------- 断言 ----------

    private static void Expect(bool condition, string label)
    {
        checks++;
        if (condition) return;
        failures++;
        Console.Error.WriteLine("FAIL: " + label);
    }

    private static void Equal(string expected, string actual, string label)
    {
        checks++;
        if (String.Equals(expected, actual, StringComparison.Ordinal)) return;
        failures++;
        Console.Error.WriteLine("FAIL: " + label + " (expected '" + expected + "', got '" + actual + "')");
    }

    // ---------- 夹具 ----------

    private const string ConsumerSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""enabled"": {""type"":""boolean"",""title"":""启用"",""default"":true,""x-order"":1},
    ""paper_snapshot"": {""type"":""string"",""title"":""论文同步"",""x-service"":""paper_snapshot_provider@1"",""x-order"":2},
    ""ai_service"": {""type"":""string"",""title"":""AI 服务"",""x-service"":""ai_provider@1"",""x-order"":3},
    ""translate_enabled"": {""type"":""boolean"",""title"":""启用标题翻译"",""default"":false,""x-order"":4,""x-requires"":""translation_provider@1""},
    ""translation_service"": {""type"":""string"",""title"":""翻译服务"",""x-service"":""translation_provider@1"",""x-order"":5},
    ""cache_service"": {""type"":""string"",""title"":""缓存服务"",""x-service"":""cache_provider@1"",""x-order"":6},
    ""undeclared"": {""type"":""string"",""title"":""未声明的依赖"",""x-service"":""mystery_provider@1"",""x-order"":7},
    ""title_prompt"": {""type"":""multiline"",""title"":""标题提示词"",""default"":"""",""x-order"":10,""x-section"":""评分"",""x-advanced"":true},
    ""title_threshold"": {""type"":""integer"",""title"":""阈值"",""minimum"":0,""maximum"":10,""default"":7,""x-order"":11,""x-section"":""评分""},
    ""batch"": {""type"":""integer"",""title"":""批大小"",""minimum"":1,""maximum"":50,""default"":10,""x-order"":12,""x-advanced"":true}
  }
}";

    private const string ConsumerManifest = @"{
  ""id"": ""io.github.test.consumer"", ""name"": ""Settings fixture"", ""version"": ""1.0.0"",
  ""api_version"": 1, ""min_host_version"": ""2.0.0"", ""entry"": ""bin/Consumer.exe"",
  ""capabilities"": [""todo_source""], ""settings_schema"": ""settings.schema.json"",
  ""uses"": [
    {""service"":""translation_provider@1""},
    {""service"":""ai_provider@1""},
    {""service"":""paper_snapshot_provider@1""},
    {""service"":""cache_provider@1"", ""optional"": false}
  ]
}";

    private static string ProviderManifest(string id, string name, string service, string billing)
    {
        return "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"version\":\"1.0.0\",\"api_version\":1,"
            + "\"min_host_version\":\"2.0.0\",\"entry\":\"bin/Provider.exe\",\"capabilities\":[],"
            + "\"provides\":[\"" + service + "\"],\"billing\":\"" + billing + "\"}";
    }

    private static void Fixture()
    {
        PluginPaths.Ensure();
        Directory.CreateDirectory(PluginPaths.DataRoot(ConsumerId));
        WritePlugin(ConsumerId, ConsumerManifest, true, "Consumer.exe");
        File.WriteAllText(Path.Combine(PluginPaths.VersionRoot(ConsumerId, "1.0.0"), "settings.schema.json"), ConsumerSchema, RuntimeUtil.Utf8NoBom);
        File.WriteAllText(Path.Combine(PluginPaths.DataRoot(ConsumerId), "config.json"), "{\"enabled\":true,\"translate_enabled\":true}", RuntimeUtil.Utf8NoBom);
        WritePlugin(ProviderA, ProviderManifest(ProviderA, "Provider A", "translation_provider@1", "may_charge"), true, "Provider.exe");
        WritePlugin(ProviderB, ProviderManifest(ProviderB, "Provider B", "translation_provider@1", "free"), true, "Provider.exe");
        WritePlugin(ProviderCache, ProviderManifest(ProviderCache, "Provider Cache", "cache_provider@1", "free"), true, "Provider.exe");
        // 装了但没启用：界面该给"启用"而不是"安装"。
        WritePlugin(ProviderOff, ProviderManifest(ProviderOff, "Provider OFF", "ai_provider@1", "free"), false, "Provider.exe");
    }

    private static void WritePlugin(string id, string manifest, bool enabled, string exeName)
    {
        string versionRoot = PluginPaths.VersionRoot(id, "1.0.0");
        Directory.CreateDirectory(Path.Combine(versionRoot, "bin"));
        // 消费方插件的入口放**本探针自己的副本**：宿主保存设置后会调
        // `PluginHost.exe PluginAction <id> validate_settings`，那个子进程就是本探针（Main 带参 ⇒ 立即返回 0）。
        if (id == ConsumerId) File.Copy(Assembly.GetEntryAssembly().Location, Path.Combine(versionRoot, "bin", exeName), true);
        else File.WriteAllText(Path.Combine(versionRoot, "bin", exeName), "", RuntimeUtil.Utf8NoBom);
        File.WriteAllText(Path.Combine(versionRoot, "plugin.json"), manifest, RuntimeUtil.Utf8NoBom);
        File.WriteAllText(Path.Combine(PluginPaths.PluginRoot(id), "current.json"),
            "{\"version\":\"1.0.0\",\"enabled\":" + (enabled ? "true" : "false") + "}", RuntimeUtil.Utf8NoBom);
    }

    private static void SetEnabled(string id, bool enabled)
    {
        File.WriteAllText(Path.Combine(PluginPaths.PluginRoot(id), "current.json"),
            "{\"version\":\"1.0.0\",\"enabled\":" + (enabled ? "true" : "false") + "}", RuntimeUtil.Utf8NoBom);
    }

    private static List<SettingsField> Fields()
    {
        Dictionary<string, object> schema = JsonUtil.LoadObject(Path.Combine(PluginPaths.VersionRoot(ConsumerId, "1.0.0"), "settings.schema.json"));
        return PluginSettingsLayout.Parse(JsonUtil.Object(JsonUtil.Get(schema, "properties")));
    }

    private static SettingsField Field(string key)
    {
        return Fields().First(x => x.Key == key);
    }

    // ---------- 1) 分组与高级项（规格 §7.4） ----------

    private static void LayoutSection(PluginManifest manifest)
    {
        List<SettingsField> fields = Fields();
        List<SettingsSection> sections = PluginSettingsLayout.Group(fields);

        checks++;
        if (sections.Count != 3) { failures++; Console.Error.WriteLine("FAIL: 分组数应为 3，实际 " + sections.Count); }
        else
        {
            Equal("基本", sections[0].Name, "缺省落「基本」组");
            Equal("评分", sections[1].Name, "x-section 建组");
            Equal("高级设置", sections[2].Name, "高级项合并成一个折叠组");
        }
        Expect(!sections[0].Advanced && !sections[1].Advanced && sections[2].Advanced, "只有高级组带折叠标记");
        Equal("enabled,paper_snapshot,ai_service,translate_enabled,translation_service,cache_service,undeclared",
            String.Join(",", sections[0].Fields.Select(x => x.Key).ToArray()), "组内按 x-order 排序");
        Equal("title_threshold", String.Join(",", sections[1].Fields.Select(x => x.Key).ToArray()), "评分组只收非高级项");
        Equal("title_prompt,batch", String.Join(",", sections[2].Fields.Select(x => x.Key).ToArray()), "高级组按 x-order 排序");

        // 组顺序 = 组内最小 x-order：基本(1) → 评分(10) → 高级设置。故意让评分组的 order 大于高级组，
        // 分组后仍然排在高级组前面（高级组永远最后）。
        Expect(PluginSettingsLayout.IsServiceField(Field("ai_service")) && !PluginSettingsLayout.IsServiceField(Field("enabled")), "x-service 行被识别");
        Equal("translation_provider@1", Field("translate_enabled").Requires, "x-requires 被解析");
        Expect(Field("title_prompt").Advanced && !Field("title_threshold").Advanced, "x-advanced 只作用于自己那一行");
        Equal("翻译服务", PluginSettingsLayout.ServiceLabel(fields, "translation_provider@1"), "服务显示名取自声明它的那一行");
        Equal("paper_snapshot_provider@2", PluginSettingsLayout.ServiceLabel(fields, "paper_snapshot_provider@2"), "没有对应行时退回服务名");
    }

    // ---------- 2) 服务行状态与选择规则（规格 §8） ----------

    private static void ServiceSection(PluginManifest manifest)
    {
        ServiceRowState uninstalled = PluginSettingsLayout.Inspect(manifest, Field("paper_snapshot"));
        Expect(uninstalled.InstallNeeded && !uninstalled.Available, "没装 ⇒ 给「安装」入口");
        Equal(ServiceRegistry.ReasonNotInstalled, uninstalled.Reason, "未安装的 reason");
        Equal("未安装", uninstalled.ReasonText, "未安装的界面文案");
        Expect(uninstalled.Declared, "声明的依赖被认出来");

        ServiceRowState disabled = PluginSettingsLayout.Inspect(manifest, Field("ai_service"));
        Expect(disabled.NeedsEnable && !disabled.InstallNeeded, "装了没启用 ⇒ 给「启用」而不是「安装」");
        Equal(ProviderOff, disabled.DisabledProviderId, "指出是哪个插件没启用");

        ServiceRowState ambiguous = PluginSettingsLayout.Inspect(manifest, Field("translation_service"));
        Expect(ambiguous.Ambiguous && ambiguous.Candidates.Count == 2 && !ambiguous.Available, "两个候选 ⇒ 歧义、不自动选（§8 第 2 行）");
        Equal("may_charge", ambiguous.Candidates.First(x => x.Id == ProviderA).Billing, "候选取自已启用且声明 provides 的插件");

        ServiceRowState unique = PluginSettingsLayout.Inspect(manifest, Field("cache_service"));
        Expect(unique.Available && !unique.Optional, "唯一候选 ⇒ 可用；optional:false 被认出来");
        Expect(ServiceRegistry.BoundProvider(ConsumerId, "cache_provider@1") == ProviderCache, "唯一候选被自动绑定并落表（§8 第 1 行）");
        Expect(File.ReadAllText(Path.Combine(PluginPaths.Logs, "service-call.log"), RuntimeUtil.Utf8NoBom)
            .Contains("auto-bind " + ConsumerId + " cache_provider@1 -> " + ProviderCache), "自动绑定记进 service-call.log");

        ServiceRowState undeclared = PluginSettingsLayout.Inspect(manifest, Field("undeclared"));
        Expect(!undeclared.Declared, "uses 里没声明的 x-service 行被标记出来");

        string reason = "";
        Expect(PluginSettingsLayout.RequiresSatisfied(manifest, "", out reason), "没有 x-requires ⇒ 永不放灰");
        Expect(!PluginSettingsLayout.RequiresSatisfied(manifest, "paper_snapshot_provider@1", out reason) && reason == ServiceRegistry.ReasonNotInstalled, "依赖未装 ⇒ 不满足");
        Expect(!PluginSettingsLayout.RequiresSatisfied(manifest, "translation_provider@1", out reason) && reason == ServiceRegistry.ReasonAmbiguous, "依赖歧义 ⇒ 不满足");
        Expect(PluginSettingsLayout.RequiresSatisfied(manifest, "cache_provider@1", out reason), "依赖可用 ⇒ 满足");

        // 绑定到已禁用的 provider：解析按"视为没有 provider"处理，**不回落**到别的候选（§3 第 3 条）。
        ServiceRegistry.SetBinding(ConsumerId, "ai_provider@1", ProviderOff);
        ServiceRowState stillDisabled = PluginSettingsLayout.Inspect(manifest, Field("ai_service"));
        Expect(stillDisabled.NeedsEnable && stillDisabled.Bound == ProviderOff, "绑定失效时不自动改绑（§3 第 2 条）");
        ServiceRegistry.SetBinding(ConsumerId, "ai_provider@1", "");
    }

    // ---------- 3) 绑定表的写入语义 ----------

    private static void BindingSection(PluginManifest manifest)
    {
        List<KeyValuePair<string, string>> selections = new List<KeyValuePair<string, string>>();
        selections.Add(new KeyValuePair<string, string>("translation_provider@1", ProviderA));
        Expect(PluginSettingsLayout.ApplyBindings(ConsumerId, selections) == 1, "改了才写");
        Equal(ProviderA, ServiceRegistry.BoundProvider(ConsumerId, "translation_provider@1"), "写进去的就是选中的 provider");
        Expect(PluginSettingsLayout.ApplyBindings(ConsumerId, selections) == 0, "没变就不重复写（少一次加锁落盘）");

        selections[0] = new KeyValuePair<string, string>("translation_provider@1", "");
        Expect(PluginSettingsLayout.ApplyBindings(ConsumerId, selections) == 1, "选「不使用」⇒ 解除绑定");
        Equal("", ServiceRegistry.BoundProvider(ConsumerId, "translation_provider@1"), "解除后绑定为空");

        List<KeyValuePair<string, string>> bogus = new List<KeyValuePair<string, string>>();
        bogus.Add(new KeyValuePair<string, string>("Not A Service", ProviderA));
        Expect(PluginSettingsLayout.ApplyBindings(ConsumerId, bogus) == 0, "非法 service 名被跳过");

        Expect(File.ReadAllText(Path.Combine(PluginPaths.Logs, "service-call.log"), RuntimeUtil.Utf8NoBom)
            .Contains("settings-bind " + ConsumerId + " translation_provider@1 -> " + ProviderA), "手工绑定也记进 service-call.log（§3）");
        Expect(!File.Exists(Path.Combine(PluginPaths.Root, "plugin-bindings.json"))
            || !File.ReadAllText(Path.Combine(PluginPaths.Root, "plugin-bindings.json"), RuntimeUtil.Utf8NoBom).Contains("Not A Service"), "非法绑定没被写进文件");
    }

    // ---------- 4) 真对话框（含保存路径与置灰行） ----------

    private static void UiSection(PluginManifest manifest)
    {
        Exception failure = RunDialog(manifest, delegate(Form form)
        {
            Label translateLabel = Labels(form).First(x => x.Text.StartsWith("启用标题翻译", StringComparison.Ordinal));
            Expect(translateLabel.Text.Contains("翻译服务") && translateLabel.Text.Contains("点此"), "置灰行的标题写明缺哪个服务、点哪里修");
            CheckBox translateBox = RowControl<CheckBox>(form, translateLabel);
            Expect(translateBox != null && !translateBox.Enabled, "没有可用 provider 时「启用标题翻译」置灰（§11-#7）");
            Expect(translateBox != null && translateBox.Checked, "置灰不改值：仍然是用户存过的 true（§11-#8）");
            Expect(Labels(form).Count(x => x.Text == "基本") == 1 && Labels(form).Count(x => x.Text == "评分") == 1, "多组时渲染分组标题（§7.4）");

            ComboBox translation = RowControl<ComboBox>(form, Labels(form).First(x => x.Text == "翻译服务"));
            Expect(translation != null && translation.Items.Count == 3, "候选与「不使用」都在下拉里");
            Expect(translation != null && translation.Items.Cast<object>().Any(x => Convert.ToString(x) == "Provider A（可能收费）"), "收费 provider 在下拉里标出费用（§6.4）");
            Expect(translation != null && translation.SelectedIndex == 0, "多候选且用户没选 ⇒ 下拉停在「不使用」（§8/#15）");

            Button enable = Buttons(form).First(x => x.Visible && x.Text == "启用");
            Label aiLabel = Labels(form).First(x => x.Text == "AI 服务");
            enable.PerformClick();
            ComboBox ai = RowControl<ComboBox>(form, aiLabel);
            Expect(ai != null && Convert.ToString(ai.SelectedItem) == "Provider OFF", "点「启用」后就地刷新并选中该 provider");
            Button aiFix = RowFixButton(form, aiLabel);
            Expect(aiFix == null || !aiFix.Visible, "这一行可用了就不再显示修复入口");

            Label advanced = Labels(form).First(x => x.Text.StartsWith("\u25B8 " + PluginSettingsLayout.AdvancedSection, StringComparison.Ordinal));
            Label prompt = Labels(form).First(x => x.Text == "标题提示词");
            Expect(!prompt.Visible, "高级项默认收起（§7.4）");
            ControlOnClick(advanced);
            Expect(prompt.Visible, "点开「高级设置」后高级项出现");
            ControlOnClick(advanced);
            Expect(!prompt.Visible, "再点一次收回去");
        });
        Expect(failure == null, "第一轮配置对话框：" + (failure == null ? "" : failure.Message));

        Dictionary<string, object> config = JsonUtil.LoadObject(Path.Combine(PluginPaths.DataRoot(ConsumerId), "config.json"));
        Expect(JsonUtil.Bool(config, "translate_enabled", false), "保存后开关仍是 true：置灰逻辑没有改写存储值（§11-#8）");
        Expect(JsonUtil.Int(config, "title_threshold", -1) == 7, "保存真的落盘");
        Equal("", ServiceRegistry.BoundProvider(ConsumerId, "translation_provider@1"), "多候选且未选 ⇒ 一个字节都不写（§8/#15）");
        Equal(ProviderOff, ServiceRegistry.BoundProvider(ConsumerId, "ai_provider@1"), "界面上启用的 provider 被记进绑定表");

        // 第二轮：只留一个候选 ⇒ 自动绑定，且**设置页明确显示**（§8 第 1 行）。
        SetEnabled(ProviderB, false);
        failure = RunDialog(manifest, delegate(Form form)
        {
            CheckBox translateBox = RowControl<CheckBox>(form, Labels(form).First(x => x.Text.StartsWith("启用标题翻译", StringComparison.Ordinal)));
            Expect(translateBox != null && translateBox.Enabled, "有可用 provider 时开关可用（§11-#7）");
            Expect(translateBox != null && translateBox.Checked, "值仍然没被改过");
            ComboBox translation = RowControl<ComboBox>(form, Labels(form).First(x => x.Text == "翻译服务"));
            Expect(translation != null && translation.Items.Count == 2 && Convert.ToString(translation.SelectedItem) == "Provider A（可能收费）", "唯一候选自动绑定并在界面上显示出来（§8 第 1 行）");
            Expect(Labels(form).Any(x => x.Text == "已启用"), "状态提示写明已启用");
        });
        Expect(failure == null, "第二轮配置对话框：" + (failure == null ? "" : failure.Message));
        Equal(ProviderA, ServiceRegistry.BoundProvider(ConsumerId, "translation_provider@1"), "设置页把绑定写进 plugin-bindings.json");
        Expect(File.ReadAllText(Path.Combine(PluginPaths.Logs, "service-call.log"), RuntimeUtil.Utf8NoBom)
            .Contains("settings-bind " + ConsumerId + " translation_provider@1 -> " + ProviderA), "设置页写入也记进 service-call.log");
    }

    // ---------- 驱动配置对话框 ----------

    private static Exception RunDialog(PluginManifest manifest, Action<Form> inspect)
    {
        Exception failure = null;
        bool reached = false;
        Timer timer = new Timer { Interval = 100 };
        timer.Tick += delegate
        {
            Form form = null;
            foreach (Form open in Application.OpenForms) if (open.Text == manifest.Name + " 设置") form = open;
            if (form == null) return;
            reached = true;
            timer.Stop();
            try { inspect(form); }
            catch (Exception ex) { failure = ex; }
            finally { form.DialogResult = DialogResult.OK; }
        };
        timer.Start();
        try
        {
            Method("ShowPluginConfig").Invoke(null, new object[] { manifest });
        }
        catch (TargetInvocationException ex) { failure = ex.InnerException == null ? ex : ex.InnerException; }
        finally { timer.Stop(); timer.Dispose(); }
        if (!reached && failure == null) failure = new Exception("设置对话框没有出现");
        return failure;
    }

    private static MethodInfo Method(string name)
    {
        return typeof(TodoApp).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(x => x.Name == name);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static List<Label> Labels(Form form) { return Descendants(form).OfType<Label>().ToList(); }
    private static List<Button> Buttons(Form form) { return Descendants(form).OfType<Button>().ToList(); }

    // 一行里的控件：同一个容器里、与标题左对齐、且位置在标题下方的第一个该类型控件。
    // 不比对精确 Top —— 窗体 Shown 时 UiScale 会按 DPI 重排（28 会被缩放取整），精确匹配在缩放机器上必挂。
    private static T RowControl<T>(Form form, Label label) where T : Control
    {
        if (label == null || label.Parent == null) return null;
        T best = null;
        int bestTop = Int32.MaxValue;
        foreach (Control child in label.Parent.Controls)
        {
            if (!(child is T) || child.Left > label.Left + 40 || child.Top <= label.Top) continue;
            if (child.Top >= bestTop) continue;
            best = (T)child; bestTop = child.Top;
        }
        return best;
    }

    // 服务行右侧的修复按钮（Left 靠右，不能按左对齐规则找）。
    private static Button RowFixButton(Form form, Label label)
    {
        if (label == null || label.Parent == null) return null;
        Button best = null;
        int bestTop = Int32.MaxValue;
        foreach (Control child in label.Parent.Controls)
        {
            if (!(child is Button) || child.Left <= label.Left + 40) continue;
            if (child.Top <= label.Top || child.Top > label.Top + 80) continue;
            if (child.Top >= bestTop) continue;
            best = (Button)child; bestTop = child.Top;
        }
        return best;
    }

    // Label 没有 PerformClick，用 OnClick 直接触发它挂的处理器。
    private static void ControlOnClick(Control control)
    {
        typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] { EventArgs.Empty });
    }
}
