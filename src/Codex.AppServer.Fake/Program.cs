using System.Text.Json;

long nextThread = 1;
long nextTurn = 1;
var threads = new List<object>();
bool signedIn = false;

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
        "model/list" => new
        {
            data = new[]
            {
                new { model = "gpt-5-codex", displayName = "GPT-5 Codex", isDefault = true, hidden = false },
                new { model = "gpt-5", displayName = "GPT-5", isDefault = false, hidden = false },
            },
            nextCursor = (string?)null,
        },
        "turn/start" => StartTurn(root),
        "turn/steer" => new { turnId = root.GetProperty("params").GetProperty("expectedTurnId").GetString() },
        "turn/interrupt" => new { },
        "account/read" => new
        {
            account = signedIn ? new { type = "chatgpt", planType = "plus" } : null,
            requiresOpenaiAuth = true,
        },
        "account/login/start" => StartLogin(),
        "account/logout" => Logout(),
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
    JsonElement parameters = request.GetProperty("params");
    string threadId = parameters.GetProperty("threadId").GetString() ?? string.Empty;
    string? model = GetOptionalString(parameters, "model");
    string? approvalPolicy = GetOptionalString(parameters, "approvalPolicy");
    string? sandboxMode = parameters.TryGetProperty("sandboxPolicy", out JsonElement sandbox)
        ? GetOptionalString(sandbox, "type")
        : null;
    string turnId = $"fake-turn-{nextTurn++}";
    Console.Error.WriteLine($"fake turn/start model={Sanitize(model) ?? "(default)"} approvalPolicy={Sanitize(approvalPolicy) ?? "(default)"} sandbox={Sanitize(sandboxMode) ?? "(default)"}");
    _ = Task.Run(async () =>
    {
        await Task.Delay(25).ConfigureAwait(false);
        await WriteAsync(new { method = "turn/started", @params = new { threadId, turnId, turn = new { id = turnId } } }).ConfigureAwait(false);
        await WriteAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = "agent-1", delta = "Hello from the fake app-server." } }).ConfigureAwait(false);
        await WriteAsync(new { method = "turn/completed", @params = new { threadId, turnId, turn = new { id = turnId, status = "completed" } } }).ConfigureAwait(false);
    });
    return new { turn = new { id = turnId, status = "inProgress" } };
}

object StartLogin()
{
    string loginId = $"fake-login-{Guid.NewGuid():N}";
    _ = Task.Run(async () =>
    {
        await Task.Delay(100).ConfigureAwait(false);
        signedIn = true;
        await WriteAsync(new { method = "account/login/completed", @params = new { loginId, success = true } }).ConfigureAwait(false);
        await WriteAsync(new { method = "account/updated", @params = new { authMode = "chatgpt", planType = "plus" } }).ConfigureAwait(false);
    });
    return new { type = "chatgpt", loginId, authUrl = "https://example.com/codex-login" };
}

object Logout()
{
    signedIn = false;
    return new { };
}

static string? GetOptionalString(JsonElement element, string name)
    => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

static string? Sanitize(string? value)
{
    if (string.IsNullOrEmpty(value))
    {
        return value;
    }

    return new string(value.Where(character => !char.IsControl(character)).Take(128).ToArray());
}

static Task WriteAsync(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
