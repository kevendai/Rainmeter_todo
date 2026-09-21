using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace RainmeterBackend
{
    // 一次跨插件服务调用的结果。字段与规格 §4.3 的响应信封一一对应。
    //
    // 关键语义（consumer 必须按这些分支，不许看 error 文本）：
    //   · Attention = ok && status=="attention"：这是**正式结果**，不是错误（§5 的付费确认）。
    //   · ErrorKind=="no_provider"：绑定不存在 / 已卸载 / 已禁用 / 多候选待选 —— 这是
    //     "没有该 provider"，**不是失败**。
    //   · 其余 error_kind 才是真错误，见 §4.3 的表。
    internal sealed class ServiceCallResult
    {
        public bool Ok, Attention, Fatal;
        public string Status = "", Error = "", ErrorKind = "";
        public Dictionary<string, object> Output = new Dictionary<string, object>();

        public bool KindIs(string kind) { return String.Equals(ErrorKind, kind ?? "", StringComparison.Ordinal); }
        public bool NoProvider { get { return !Ok && KindIs(ServiceClient.KindNoProvider); } }

        public static ServiceCallResult Failure(string kind, string message)
        {
            return new ServiceCallResult { Ok = false, Status = "error", ErrorKind = kind ?? "", Error = message ?? "" };
        }
    }

    // 插件侧（consumer）唯一的跨插件动作：把请求写成文件，交给宿主 Broker 转发（规格 §4）。
    //
    // 三条不许越过的边界：
    //   1. 只能在**插件进程内**用 —— 宿主靠 RW_PLUGIN_ID 认人，请求 JSON 里禁止出现 provider。
    //   2. provider 由宿主按绑定解析，插件无从"偷偷换 provider"（§8 的三条禁令因此天然成立）。
    //   3. provider 进程拿不到 RW_PLUGIN_HOST_EXE，物理上无法二级转发（depth = 1，§4.4）。
    internal static class ServiceClient
    {
        public const string KindNoProvider = "no_provider";
        public const string KindProtocol = "protocol_error";
        public const string KindUnavailable = "provider_crashed";

        private const string HostExeVariable = "RW_PLUGIN_HOST_EXE";
        private const string DepthVariable = "RW_SERVICE_CALL_DEPTH";
        // 宿主把上限压到 1800s（§4.3）；这里给 broker 留出它自己的收尾时间，别比它先放弃。
        private const int BrokerOverheadSeconds = 60;

        public static string BrokerEntry()
        {
            return EnvironmentValue(HostExeVariable);
        }

        // 宿主没注入 Broker 入口 = 这个宿主根本没有跨插件调用能力 ⇒ 按"没有该 provider"处理
        // （让 consumer 走同一条分支，不会把"没装宿主基础设施"误报成插件失败）。
        public static bool HasBroker() { return BrokerEntry() != ""; }

        // 环境变量口径见规格 §3：RW_SERVICE_<BINDING_KEY 大写>_PROVIDER / _NAME。
        // 注意 binding_key 本身就以 `_provider` 结尾（`ai_provider` → RW_SERVICE_AI_PROVIDER_PROVIDER），
        // 看起来啰嗦但**必须与宿主 ServiceRegistry.ToEnvironment 一字不差**，否则兜底形同虚设。
        // 公开出来是为了让自检不必手抄这串名字（抄错一次就白测）。
        public static string EnvironmentName(string bindingKey, string suffix)
        {
            if (String.IsNullOrWhiteSpace(bindingKey)) return "";
            string name = "RW_SERVICE_" + bindingKey.Trim().ToUpperInvariant() + "_" + suffix;
            for (int index = 0; index < name.Length; index++)
            {
                char c = name[index];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') continue;
                name = name.Substring(0, index) + '_' + name.Substring(index + 1);
            }
            return name;
        }

        public static string ProviderFromEnvironment(string bindingKey)
        {
            return EnvironmentValue(EnvironmentName(bindingKey, "PROVIDER"));
        }

        // 插件不调 Broker 就能判断"有没有"：优先用请求里的 context.services（本次 job 的真实解析
        // 结果），环境变量作为兜底。两者口径一致（§3 明说是同一件事的两处呈现）。
        public static bool Available(Dictionary<string, object> context, string bindingKey)
        {
            Dictionary<string, object> services = JsonUtil.Object(JsonUtil.Get(context, "services"));
            Dictionary<string, object> entry = JsonUtil.Object(JsonUtil.Get(services, bindingKey));
            if (entry.Count > 0) return JsonUtil.Bool(entry, "available", false);
            return ProviderFromEnvironment(bindingKey) != "";
        }

        public static string ProviderName(Dictionary<string, object> context, string bindingKey)
        {
            Dictionary<string, object> services = JsonUtil.Object(JsonUtil.Get(context, "services"));
            Dictionary<string, object> entry = JsonUtil.Object(JsonUtil.Get(services, bindingKey));
            string name = JsonUtil.String(entry, "provider_name", "");
            // context.services 与 RW_SERVICE_*_NAME 是同一件事的两处呈现（§3），
            // 少了任何一处都要能拿到名字（界面文案不该因为宿主换了呈现方式就变成空串）。
            return name == "" ? EnvironmentValue(EnvironmentName(bindingKey, "NAME")) : name;
        }

        // 宿主注入的"这次 job 的上下文"里跟服务无关的那部分（§4.4）。目前只有一项：
        // 同一天内用户是否已经拒绝过付费 AI（后台同步据此不再打扰，§4.6-6 第 2 条）。
        public const string DeclinedTodayVariable = "RW_PLUGIN_DECLINED_TODAY";

        public static bool DeclinedToday()
        {
            return EnvironmentValue(DeclinedTodayVariable) == "1";
        }

        // Windows 环境块允许同名不同大小写并存，读的时候别只试一种写法。
        public static string EnvironmentValue(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (String.IsNullOrWhiteSpace(value))
            {
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    if (!String.Equals(Convert.ToString(entry.Key), name, StringComparison.OrdinalIgnoreCase)) continue;
                    value = Convert.ToString(entry.Value);
                    break;
                }
            }
            return String.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        // 向某个服务发一次调用。任何异常都转成 ServiceCallResult，调用方不必 try/catch。
        public static ServiceCallResult Call(string service, string action, Dictionary<string, object> input, int timeoutSeconds)
        {
            string broker = BrokerEntry();
            if (broker == "") return ServiceCallResult.Failure(KindNoProvider, "宿主未提供跨插件调用入口");
            if (String.IsNullOrWhiteSpace(service)) return ServiceCallResult.Failure(KindProtocol, "缺少 service 名");
            if (String.IsNullOrWhiteSpace(action)) return ServiceCallResult.Failure(KindProtocol, "缺少 action 名");

            int timeout = timeoutSeconds <= 0 ? 600 : timeoutSeconds;
            string directory = Path.Combine(Path.GetTempPath(), "rwsvc-" + Guid.NewGuid().ToString("N"));
            string requestPath = Path.Combine(directory, "request.json");
            string outputPath = Path.Combine(directory, "output.json");
            try
            {
                Directory.CreateDirectory(directory);
                // 顶层键白名单（§4.3）：多一个键 Broker 直接拒。这里**只**放这六个。
                Dictionary<string, object> request = new Dictionary<string, object> {
                    {"protocol", 1},
                    {"request_id", Guid.NewGuid().ToString("N")},
                    {"service", service.Trim()},
                    {"action", action.Trim()},
                    {"input", input ?? new Dictionary<string, object>()},
                    {"timeout_seconds", timeout}
                };
                File.WriteAllText(requestPath, JsonUtil.Serialize(request), RuntimeUtil.Utf8NoBom);

                ProcessStartInfo info = new ProcessStartInfo(broker,
                    "-Mode ServiceCall -RequestFile \"" + requestPath + "\" -OutputFile \"" + outputPath + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardError = true;
                info.RedirectStandardOutput = true;
                string stderr;
                int exitCode = 0;
                using (Process process = new Process { StartInfo = info })
                {
                    if (!process.Start()) return ServiceCallResult.Failure(KindUnavailable, "无法启动宿主 Broker");
                    // 父子进程自保（§4.6-4）：Broker 会盯着我们；我们也要读干净输出避免管道写满。
                    stderr = process.StandardError.ReadToEnd();
                    string stdout = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit((timeout + BrokerOverheadSeconds) * 1000))
                    {
                        try { process.Kill(); } catch { }
                        return ServiceCallResult.Failure("provider_timeout", "服务调用超过 " + timeout.ToString(CultureInfo.InvariantCulture) + " 秒没有返回");
                    }
                    exitCode = process.ExitCode;
                    if (!File.Exists(outputPath) && !String.IsNullOrWhiteSpace(stdout))
                        return Parse(stdout);
                }
                if (!File.Exists(outputPath))
                    return ServiceCallResult.Failure(KindUnavailable,
                        "宿主 Broker 没有返回结果（exit " + exitCode.ToString(CultureInfo.InvariantCulture) + (stderr.Trim() == "" ? "" : "：" + Trim(stderr)) + "）");
                return Parse(File.ReadAllText(outputPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                return ServiceCallResult.Failure(KindUnavailable, "服务调用失败：" + Trim(ex.Message));
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        // 响应信封（§4.3）。结构不符一律 protocol_error —— 绝不猜、绝不把半截结构当好结果用。
        public static ServiceCallResult Parse(string text)
        {
            Dictionary<string, object> envelope;
            try { envelope = JsonUtil.Object(JsonUtil.Deserialize((text ?? "").Trim())); }
            catch (Exception ex) { return ServiceCallResult.Failure(KindProtocol, "服务调用返回的不是合法 JSON：" + Trim(ex.Message)); }
            if (envelope.Count == 0) return ServiceCallResult.Failure(KindProtocol, "服务调用返回了空结果");
            if (JsonUtil.Int(envelope, "protocol", 0) != 1) return ServiceCallResult.Failure(KindProtocol, "不支持的 Broker 协议版本");
            ServiceCallResult result = new ServiceCallResult();
            result.Ok = JsonUtil.Bool(envelope, "ok", false);
            result.Status = JsonUtil.String(envelope, "status", "ok").Trim();
            if (result.Status == "") result.Status = "ok";
            result.Output = JsonUtil.Object(JsonUtil.Get(envelope, "output"));
            result.Error = JsonUtil.String(envelope, "error", "");
            result.ErrorKind = JsonUtil.String(envelope, "error_kind", "").Trim();
            result.Fatal = JsonUtil.Bool(envelope, "fatal", false);
            result.Attention = result.Ok && result.Status == "attention";
            if (!result.Ok && result.ErrorKind == "") result.ErrorKind = "provider_error";
            return result;
        }

        // 失败原因只用来写日志/状态文案，必须先去换行并截断（沿用 2.0.4 的 SafeStatusMessage 口径）。
        public static string Trim(string value)
        {
            value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 180 ? value.Substring(0, 180) : value;
        }
    }
}
