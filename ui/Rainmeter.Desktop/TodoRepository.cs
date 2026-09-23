using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rainmeter.Desktop;

internal sealed record TodoDraft(
    string? Id,
    string Title,
    string Target,
    string Note,
    string[] Labels,
    string? AvailableFrom,
    string? DueAt);

internal sealed class TodoRepository(string resourceDirectory)
{
    private readonly string path = Path.Combine(resourceDirectory, "tasks.json");
    private const string MutexName = @"Global\RainmeterTodoState";

    internal TodoDraft? Find(string id)
    {
        var state = ReadSnapshot();
        if (state is null) return null;
        var task = state["tasks"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(item => item["id"]?.GetValue<string>() == id);
        if (task is null) return null;
        return new TodoDraft(
            id,
            String(task, "title"),
            String(task, "target"),
            String(task, "note"),
            task["labels"]?.AsArray().Select(v => v?.GetValue<string>() ?? "").Where(v => v != "").ToArray() ?? [],
            String(task, "available_from") is { Length: > 0 } available ? available : null,
            String(task, "due_at") is { Length: > 0 } due ? due : null);
    }

    internal void Save(TodoDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new ArgumentException("标题不能为空。");
        if (draft.AvailableFrom is not null && draft.DueAt is not null
            && DateTimeOffset.Parse(draft.DueAt) < DateTimeOffset.Parse(draft.AvailableFrom))
            throw new ArgumentException("截止时间不能早于开始时间。");
        Mutate(state =>
        {
            var tasks = EnsureTasks(state);
            var task = draft.Id is null ? null : tasks.OfType<JsonObject>()
                .FirstOrDefault(item => item["id"]?.GetValue<string>() == draft.Id);
            if (draft.Id is not null && task is null) throw new InvalidOperationException("这条待办已不存在，请刷新后重试。");
            bool isNew = task is null;
            if (task is null)
            {
                task = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["completed"] = false,
                    ["source"] = "manual",
                    ["created_at"] = DateTimeOffset.Now.ToString("O"),
                    ["completed_at"] = null
                };
                tasks.Add(task);
            }
            task["title"] = draft.Title.Trim();
            task["target"] = draft.Target.Trim();
            task["note"] = draft.Note;
            task["labels"] = new JsonArray(draft.Labels.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => (JsonNode?)JsonValue.Create(x.Trim())).ToArray());
            task["available_from"] = draft.AvailableFrom;
            task["due_at"] = draft.DueAt;
            EnsureMeta(state)["status"] = isNew ? "已新增待办" : "已修改待办";
        });
    }

    internal void Delete(string id)
    {
        Mutate(state =>
        {
            var tasks = EnsureTasks(state);
            var task = tasks.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<string>() == id);
            if (task is null) return;
            tasks.Remove(task);
            EnsureMeta(state)["status"] = "已删除待办";
        });
    }

    private JsonObject? ReadSnapshot()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("待办数据根节点无效，原文件未修改。");
        }
        catch (JsonException) { throw new InvalidDataException("待办数据无法解析，原文件未修改。"); }
    }

    private void Mutate(Action<JsonObject> action)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var gate = new Mutex(false, MutexName);
        bool held;
        try { held = gate.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new TimeoutException("待办正在被另一个程序使用，请稍后重试。");
        try
        {
            var state = ReadSnapshot() ?? new JsonObject
            {
                ["version"] = 3,
                ["meta"] = new JsonObject { ["status"] = "就绪" },
                ["tasks"] = new JsonArray()
            };
            action(state);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.ReleaseMutex(); }
    }

    private static JsonArray EnsureTasks(JsonObject state)
    {
        if (state["tasks"] is JsonArray tasks) return tasks;
        if (state.ContainsKey("tasks"))
            throw new InvalidDataException("待办列表格式无效，原文件未修改。");
        else
        {
            tasks = [];
            state["tasks"] = tasks;
        }
        return tasks;
    }

    private static JsonObject EnsureMeta(JsonObject state)
    {
        if (state["meta"] is not JsonObject meta)
        {
            meta = [];
            state["meta"] = meta;
        }
        return meta;
    }

    private static string String(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : "";
}
