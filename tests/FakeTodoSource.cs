using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// PaidConsentProbe 专用的最小 todo_source 假插件（规格 §5.2/§5.6）。
//
// 判据只有一条：**拿到 input.allow_paid_ai=true 才会"用 AI 评分"，否则一律带 attention 正常退出**。
// 每次调用都往 RW_PLUGIN_DATA_DIR\calls.log 追加一行，把三件事记下来供探针断言：
//   allow_paid_ai  —— 宿主到底把付费请求放行了没有；
//   declined_today —— 宿主注入的"今天已经拒绝过"事实（规格 §4.6-6）；
//   consent_env    —— 插件**自己**能不能看到一次性同意标记（答案必须永远是不能）。
internal static class FakeTodoSource
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    private static int Main()
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = Encoding.UTF8;
        Dictionary<string, object> request = (Dictionary<string, object>)Json.DeserializeObject(Console.In.ReadLine() ?? "");
        string id = Convert.ToString(request["request_id"]), action = Convert.ToString(request["action"]);
        Dictionary<string, object> input = request.ContainsKey("input") ? request["input"] as Dictionary<string, object> : null;
        if (input == null) input = new Dictionary<string, object>();
        bool paidAi = input.ContainsKey("allow_paid_ai") && Convert.ToBoolean(input["allow_paid_ai"]);
        AppendCall(action, paidAi, Environment.GetEnvironmentVariable("RW_PLUGIN_DECLINED_TODAY") ?? "", Environment.GetEnvironmentVariable("RW_PAID_CONSENT") ?? "");
        Dictionary<string, object> result = new Dictionary<string, object> {
            {"type","result"},{"request_id",id},{"ok",true},{"status","ok"},{"payload",new Dictionary<string, object>()},{"error",""}};
        if (action == "sync" && !paidAi)
        {
            // 没有用户的明确同意 ⇒ 不调任何外部 API，带着 attention 正常退出（exit 0）。
            result["status"] = "attention";
            result["payload"] = new Dictionary<string, object> {{"attention", new Dictionary<string, object> {
                {"type","paid_service_confirmation"},{"service","ai_provider@1"},
                {"message","远端论文同步失败，是否使用 DeepSeek AI 重新评分？"},
                {"resume_action","sync_with_ai"},
                {"resume_input",new Dictionary<string, object>{{"allow_paid_ai",true}}}}}};
        }
        else
        {
            result["payload"] = new Dictionary<string, object> {
                {"summary",paidAi?"已用 AI 评分":"已同步"},{"tasks",new List<object>()}};
        }
        Console.WriteLine(Json.Serialize(result));
        return 0;
    }

    private static void AppendCall(string action, bool paidAi, string declinedToday, string consent)
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable("RW_PLUGIN_DATA_DIR");
            if (String.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "calls.log"),
                action + "|allow_paid_ai=" + (paidAi ? "1" : "0") + "|declined_today=" + declinedToday + "|consent_env=" + (consent == "" ? "0" : "1") + "\r\n",
                new UTF8Encoding(false));
        }
        catch { }
    }
}
