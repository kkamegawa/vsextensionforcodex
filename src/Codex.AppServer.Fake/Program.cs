using System.Text.Json;

long nextThread = 1;
long nextTurn = 1;
var threads = new List<object>();

while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
{
    using JsonDocument document = JsonDocument.Parse(line);
    JsonElement root = document.RootElement;
    string method = root.GetProperty("method").GetString() ?? string.Empty;
    if (!root.TryGetProperty("id", out JsonElement id))
    {
        continue;
    }

    object result = method switch
    {
        "initialize" => new { userAgent = "codex-app-server-fake/0.1.0" },
        "thread/start" => CreateThread(),
        "thread/resume" => new { thread = new { id = root.GetProperty("params").GetProperty("threadId").GetString(), preview = "Resumed fake thread" } },
        "thread/list" => new { data = threads, nextCursor = (string?)null },
        "turn/start" => StartTurn(root),
        "turn/steer" => new { turnId = root.GetProperty("params").GetProperty("expectedTurnId").GetString() },
        "turn/interrupt" => new { },
        _ => new { },
    };
    await WriteAsync(new { id = JsonSerializer.Deserialize<object>(id.GetRawText()), result }).ConfigureAwait(false);
}

return;

object CreateThread()
{
    var thread = new { id = $"fake-thread-{nextThread++}", preview = "Fake conversation", cwd = Environment.CurrentDirectory };
    threads.Add(thread);
    return new { thread };
}

object StartTurn(JsonElement request)
{
    string threadId = request.GetProperty("params").GetProperty("threadId").GetString() ?? string.Empty;
    string turnId = $"fake-turn-{nextTurn++}";
    _ = Task.Run(async () =>
    {
        await Task.Delay(25).ConfigureAwait(false);
        await WriteAsync(new { method = "turn/started", @params = new { threadId, turnId, turn = new { id = turnId } } }).ConfigureAwait(false);
        await WriteAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = "agent-1", delta = "Hello from the fake app-server." } }).ConfigureAwait(false);
        await WriteAsync(new { method = "turn/completed", @params = new { threadId, turnId, turn = new { id = turnId, status = "completed" } } }).ConfigureAwait(false);
    });
    return new { turn = new { id = turnId, status = "inProgress" } };
}

static Task WriteAsync(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
