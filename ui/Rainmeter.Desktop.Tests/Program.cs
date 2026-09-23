using System.Text.Json.Nodes;
using Rainmeter.Desktop;

var directory = Path.Combine(Path.GetTempPath(), "rainmeter-ui-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var repository = new TodoRepository(directory);
    repository.Save(new TodoDraft(null, "中文待办", "https://example.com", "一条备注", ["重要"], null, null));
    var path = Path.Combine(directory, "tasks.json");
    var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    var task = root["tasks"]!.AsArray()[0]!.AsObject();
    var id = task["id"]!.GetValue<string>();
    if (task["title"]!.GetValue<string>() != "中文待办") throw new Exception("新增待办失败");
    task["plugin_owned_extra"] = "必须保留";
    File.WriteAllText(path, root.ToJsonString());
    repository.Save(new TodoDraft(id, "修改后", "", "新备注", ["生活", "重要"], null, null));
    root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    task = root["tasks"]!.AsArray()[0]!.AsObject();
    if (task["plugin_owned_extra"]!.GetValue<string>() != "必须保留") throw new Exception("扩展字段丢失");
    if (task["title"]!.GetValue<string>() != "修改后") throw new Exception("编辑待办失败");
    bool rejected = false;
    try { repository.Save(new TodoDraft(id, "错误", "", "", [], "2026-09-23T20:00:00+08:00", "2026-09-23T19:00:00+08:00")); }
    catch (ArgumentException) { rejected = true; }
    if (!rejected) throw new Exception("无效时间未拒绝");
    repository.Delete(id);
    root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    if (root["tasks"]!.AsArray().Count != 0) throw new Exception("删除待办失败");
    Console.WriteLine("TodoRepository: PASS");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}
