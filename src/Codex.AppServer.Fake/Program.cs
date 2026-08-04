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
        "thread/resume" => ThreadResponse(
            new { id = root.GetProperty("params").GetProperty("threadId").GetString(), preview = "Resumed fake thread" }),
        "thread/fork" => ThreadResponse(
            new { id = $"fake-thread-{nextThread++}", preview = "Forked fake thread", cwd = Environment.CurrentDirectory }),
        "thread/list" => new { data = threads, nextCursor = (string?)null },
        "model/list" => new
        {
            data = new object[]
            {
                new
                {
                    model = "gpt-5-codex",
                    displayName = "GPT-5 Codex",
                    isDefault = false,
                    hidden = false,
                    defaultReasoningEffort = "medium",
                    supportedReasoningEfforts = new[]
                    {
                        new { reasoningEffort = "low", description = "Faster responses with lighter reasoning." },
                        new { reasoningEffort = "medium", description = "Balanced reasoning for everyday work." },
                        new { reasoningEffort = "high", description = "Deeper reasoning for complex work." },
                    },
                    defaultServiceTier = "standard",
                    serviceTiers = new[]
                    {
                        new { id = "standard", name = "Standard", description = "Standard Codex service." },
                        new { id = "fast", name = "Fast", description = "Prioritize lower latency." },
                    },
                },
                new
                {
                    model = "gpt-5",
                    displayName = "GPT-5",
                    isDefault = false,
                    hidden = false,
                    defaultReasoningEffort = "medium",
                    supportedReasoningEfforts = new[]
                    {
                        new { reasoningEffort = "medium", description = "Balanced reasoning." },
                        new { reasoningEffort = "high", description = "Deeper reasoning." },
                    },
                },
                // Hidden catalog default: filtered from the picker server-side but surfaced via isDefault.
                new
                {
                    model = "gpt-5.1-codex-max",
                    displayName = "GPT-5.1 Codex Max",
                    isDefault = true,
                    hidden = true,
                    defaultReasoningEffort = "high",
                    supportedReasoningEfforts = new[]
                    {
                        new { reasoningEffort = "medium", description = "Balanced reasoning." },
                        new { reasoningEffort = "high", description = "Deeper reasoning." },
                        new { reasoningEffort = "xhigh", description = "Maximum reasoning depth." },
                    },
                    defaultServiceTier = "standard",
                    serviceTiers = new[]
                    {
                        new { id = "standard", name = "Standard", description = "Standard Codex service." },
                        new { id = "fast", name = "Fast", description = "Prioritize lower latency." },
                    },
                },
            },
            nextCursor = (string?)null,
        },
        "permissionProfile/list" => new
        {
            data = new[]
            {
                new { id = "review", description = "Review commands before workspace changes.", allowed = true },
                new { id = "managed", description = "Managed by organization policy.", allowed = false },
            },
            nextCursor = (string?)null,
        },
        "skills/list" => new
        {
            data = new object[]
            {
                new
                {
                    cwd = Environment.CurrentDirectory,
                    errors = new object[]
                    {
                        new { message = "SKILL.md front matter is not valid YAML.", path = "/repo/.codex/skills/broken/SKILL.md" },
                    },
                    skills = new object[]
                    {
                        new
                        {
                            name = "review-diff",
                            description = "Review the current diff for correctness and style issues.",
                            enabled = true,
                            path = "/repo/.codex/skills/review-diff",
                            scope = "repo",
                        },
                        new
                        {
                            name = "write-tests",
                            description = "Draft unit tests for the selected file.",
                            enabled = true,
                            path = "/repo/.codex/skills/write-tests",
                            scope = "repo",
                        },
                        new
                        {
                            name = "summarize-thread",
                            description = "Summarize the current conversation thread.",
                            enabled = true,
                            path = "/home/fake-user/.codex/skills/summarize-thread",
                            scope = "user",
                        },
                        new
                        {
                            name = "legacy-formatter",
                            description = "Disabled by the user; kept for reference.",
                            enabled = false,
                            path = "/repo/.codex/skills/legacy-formatter",
                            scope = "repo",
                        },
                    },
                },
            },
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
        "account/rateLimits/read" => CreateRateLimits(),
        _ => new { },
    };
    await WriteAsync(new { id = JsonSerializer.Deserialize<object>(id.GetRawText()), result }).ConfigureAwait(false);
}

return;

object CreateThread()
{
    var thread = new { id = $"fake-thread-{nextThread++}", preview = "Fake conversation", cwd = Environment.CurrentDirectory };
    threads.Add(thread);
    return ThreadResponse(thread);
}

static object ThreadResponse(object thread) => new
{
    thread,
    activePermissionProfile = new { id = ":workspace" },
    approvalPolicy = "on-request",
    approvalsReviewer = "user",
    sandbox = new { type = "workspaceWrite" },
    effort = "medium",
    serviceTier = "standard",
};

object StartTurn(JsonElement request)
{
    JsonElement parameters = request.GetProperty("params");
    string threadId = parameters.GetProperty("threadId").GetString() ?? string.Empty;
    string? model = GetOptionalString(parameters, "model");
    string? approvalPolicy = GetOptionalString(parameters, "approvalPolicy");
    string? approvalsReviewer = GetOptionalString(parameters, "approvalsReviewer");
    string? permissions = GetOptionalString(parameters, "permissions");
    string? effort = GetOptionalString(parameters, "effort");
    string? serviceTier = GetOptionalString(parameters, "serviceTier");
    string? sandboxMode = parameters.TryGetProperty("sandboxPolicy", out JsonElement sandbox)
        ? GetOptionalString(sandbox, "type")
        : null;
    string turnId = $"fake-turn-{nextTurn++}";
    Console.Error.WriteLine($"fake turn/start model={Sanitize(model) ?? "(default)"} approvalPolicy={Sanitize(approvalPolicy) ?? "(default)"} approvalsReviewer={Sanitize(approvalsReviewer) ?? "(default)"} sandbox={Sanitize(sandboxMode) ?? "(default)"} permissions={Sanitize(permissions) ?? "(default)"} effort={Sanitize(effort) ?? "(default)"} serviceTier={Sanitize(serviceTier) ?? "(default)"}");
    _ = Task.Run(async () =>
    {
        await Task.Delay(25).ConfigureAwait(false);
        await WriteAsync(new { method = "turn/started", @params = new { threadId, turnId, turn = new { id = turnId } } }).ConfigureAwait(false);
        await WriteAsync(new
        {
            method = "thread/settings/updated",
            @params = new
            {
                threadId,
                threadSettings = new
                {
                    activePermissionProfile = permissions is null ? null : new { id = permissions },
                    approvalPolicy = approvalPolicy ?? "on-request",
                    approvalsReviewer = approvalsReviewer ?? "user",
                    sandboxPolicy = new { type = sandboxMode ?? "workspaceWrite" },
                    effort = effort ?? "medium",
                    serviceTier = serviceTier ?? "standard",
                },
            },
        }).ConfigureAwait(false);
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
        await WriteAsync(new { method = "account/rateLimits/updated", @params = CreateRateLimits() }).ConfigureAwait(false);
    });
    return new { type = "chatgpt", loginId, authUrl = "https://example.com/codex-login" };
}

object Logout()
{
    signedIn = false;
    return new { };
}

static object CreateRateLimits() => new
{
    rateLimits = new
    {
        limitId = "codex",
        primary = new { usedPercent = 20, resetsAt = 1_800_000_000L, windowDurationMins = 300L },
        secondary = new { usedPercent = 50, resetsAt = 1_800_000_000L, windowDurationMins = 10_080L },
        credits = new { hasCredits = true, unlimited = false, balance = "10" },
    },
};

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
