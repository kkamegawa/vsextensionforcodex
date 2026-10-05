using System.Collections.Concurrent;
using System.Text.Json;

long nextThread = 1;
long nextTurn = 1;
var threads = new List<object>();
string scenario = Environment.GetEnvironmentVariable("CODEX_FAKE_SCENARIO") ?? string.Empty;
var interactionResponses = new ConcurrentQueue<object>();
var interactionMethods = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
var clientMethods = new ConcurrentQueue<string>();
var outputGate = new SemaphoreSlim(1, 1);
JsonElement? initializeParams = null;
bool signedIn = false;

while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
{
    using JsonDocument document = JsonDocument.Parse(line);
    JsonElement root = document.RootElement;
    if (!root.TryGetProperty("method", out JsonElement methodElement))
    {
        if (root.TryGetProperty("id", out JsonElement responseId))
        {
            string responseKey = GetRequestKey(responseId);
            string? responseMethod = interactionMethods.TryRemove(responseKey, out string? recordedMethod) ? recordedMethod : null;
            interactionResponses.Enqueue(new
            {
                id = JsonSerializer.Deserialize<object>(responseId.GetRawText()),
                method = responseMethod,
                result = root.TryGetProperty("result", out JsonElement responseResult) ? JsonSerializer.Deserialize<object>(responseResult.GetRawText()) : null,
                error = root.TryGetProperty("error", out JsonElement responseError) ? JsonSerializer.Deserialize<object>(responseError.GetRawText()) : null,
            });
        }
        continue;
    }

    string method = methodElement.GetString() ?? string.Empty;
    clientMethods.Enqueue(method);
    if (method == "initialize" && root.TryGetProperty("params", out JsonElement initParams))
    {
        initializeParams = initParams.Clone();
    }
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
                        new { message = "SKILL.md front matter is not valid YAML.", path = RepoSkillPath("broken", "SKILL.md") },
                    },
                    skills = new object[]
                    {
                        new
                        {
                            name = "review-diff",
                            description = "Review the current diff for correctness and style issues.",
                            enabled = true,
                            path = RepoSkillPath("review-diff", "SKILL.md"),
                            scope = "repo",
                        },
                        new
                        {
                            name = "write-tests",
                            description = "Draft unit tests for the selected file.",
                            enabled = true,
                            path = RepoSkillPath("write-tests", "SKILL.md"),
                            scope = "repo",
                        },
                        new
                        {
                            name = "summarize-thread",
                            description = "Summarize the current conversation thread.",
                            enabled = true,
                            path = UserSkillPath("summarize-thread", "SKILL.md"),
                            scope = "user",
                        },
                        new
                        {
                            name = "legacy-formatter",
                            description = "Disabled by the user; kept for reference.",
                            enabled = false,
                            path = RepoSkillPath("legacy-formatter", "SKILL.md"),
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
        "account/gatewayOAuth/read" => new
        {
            providerId = "fake",
            providerName = "Fake provider",
            required = scenario == "interaction",
            status = scenario == "interaction" ? "notReady" : null,
        },
        "account/gatewayOAuth/login" => new { },
        "account/gatewayOAuth/cancel" => new { },
        "mcpServer/oauth/login" when scenario == "interaction" => new { authorizationUrl = "https://example.com/mcp-auth?state=fake" },
        "fake/interactions/start" when scenario == "interaction" => new { started = true },
        "fake/interactions/state" when scenario == "interaction" => new { responses = interactionResponses.ToArray(), pendingResponses = interactionMethods.Count, methods = clientMethods.ToArray(), initializeParams },
        "account/login/start" => StartLogin(),
        "account/logout" => Logout(),
        "account/rateLimits/read" => CreateRateLimits(),
        _ => new { },
    };
    if (method == "mcpServer/oauth/login" && scenario == "interaction")
    {
        await WriteAsync(new { method = "mcpServer/oauthLogin/completed", @params = new { name = "docs", threadId = "fake-thread-1", success = true } }).ConfigureAwait(false);
        await WriteAsync(new { method = "mcpServer/startupStatus/updated", @params = new { name = "docs", status = "failed", failureReason = "reauthenticationRequired" } }).ConfigureAwait(false);
    }

    if (method == "account/gatewayOAuth/login" && scenario == "interaction")
    {
        await WriteAsync(new { method = "account/gatewayOAuth/changed", @params = new { providerId = "fake", status = "started", authUrl = "https://example.com/fake-oauth" } }).ConfigureAwait(false);
        await WriteAsync(new { method = "account/gatewayOAuth/changed", @params = new { providerId = "fake", status = "succeeded", authUrl = (string?)null } }).ConfigureAwait(false);
    }

    await WriteAsync(new { id = JsonSerializer.Deserialize<object>(id.GetRawText()), result }).ConfigureAwait(false);
    if (method == "fake/interactions/start" && scenario == "interaction")
    {
        _ = Task.Run(EmitInteractionScenarioAsync);
    }
}

return;

object CreateThread()
{
    var thread = new { id = $"fake-thread-{nextThread++}", preview = "Fake conversation", cwd = Environment.CurrentDirectory };
    threads.Add(thread);
    return ThreadResponse(thread);
}

// Platform-native absolute paths (not hardcoded POSIX strings) so manual verification on
// Windows exercises the same path shapes CodexSessionService.NormalizeSkillPath sees from a
// real codex app-server, instead of masking Windows-specific path-handling issues.
static string RepoSkillPath(params string[] segments)
    => Path.Combine([Environment.CurrentDirectory, ".codex", "skills", .. segments]);

static string UserSkillPath(params string[] segments)
    => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills", .. segments]);

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

async Task WriteAsync(object value)
{
    await outputGate.WaitAsync().ConfigureAwait(false);
    try
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)).ConfigureAwait(false);
    }
    finally
    {
        outputGate.Release();
    }
}

Task EmitInteractionScenarioAsync()
    => Task.WhenAll(
        EmitRequestAsync(11, "mcpServer/elicitation/request", new
        {
            serverName = "docs", threadId = "fake-thread-1", mode = "form", message = "Supply typed values.",
            requestedSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["email"] = new { type = "string", format = "email", minLength = 5, maxLength = 64 },
                    ["ratio"] = new { type = "number", minimum = 0.5, maximum = 2.5 },
                    ["count"] = new { type = "integer", minimum = 1, maximum = 9 },
                    ["enabled"] = new { type = "boolean" },
                    ["tags"] = new { type = "array", items = new { anyOf = new[] { new { @const = "one", title = "One" }, new { @const = "two", title = "Two" }, new { @const = "three", title = "Three" } } }, minItems = 1, maxItems = 2 },
                    ["mode"] = new { type = "string", @enum = new List<string> { "fast", "safe" }, enumNames = new List<string> { "Fast", "Safe" } },
                },
                required = new List<string> { "email", "ratio", "count", "enabled", "mode" },
            },
        }),
        EmitRequestAsync("12", "item/tool/requestUserInput", new
        {
            threadId = "fake-thread-1", turnId = "fake-turn-1", itemId = "fake-item-question", isBlocking = false,
            questions = new[] { new { id = "language", header = "Language", question = "Choose or enter one.", isOther = true,
                options = new[] { new { label = "\u65e5\u672c\u8a9e", description = "Japanese" }, new { label = "English", description = "English" } } } },
        }),
        EmitRequestAsync(12, "item/tool/requestUserInput", new
        {
            threadId = "fake-thread-1", turnId = "fake-turn-1", itemId = "fake-item-question-numeric", isBlocking = true,
            questions = new[] { new { id = "topic", header = "Topic", question = "Choose a topic.", options = new[] { new { label = "Design", description = "Design" }, new { label = "Build", description = "Build" } } } },
        }),
        EmitRequestAsync(13, "item/tool/requestUserInput", new
        {
            threadId = "fake-thread-1", turnId = "fake-turn-1", itemId = "fake-item-secret", isBlocking = true,
            questions = new[] { new { id = "secret", header = "Credential", question = "secret test value", isSecret = true } },
        }),
        EmitRequestAsync(14, "item/permissions/requestApproval", new
        {
            cwd = Environment.CurrentDirectory, itemId = "fake-item-permission", threadId = "fake-thread-1", turnId = "fake-turn-1",
            startedAtMs = 1_800_000_000_000L,
            permissions = new { network = new { enabled = true }, fileSystem = new { read = new List<string> { "src" }, write = new List<string> { "out" } } },
            reason = "Need one additional network and file permission.",
        }),
        EmitRequestAsync(15, "item/commandExecution/requestApproval", new
        {
            itemId = "fake-item-command", startedAtMs = 1_800_000_000_000L, threadId = "fake-thread-1", turnId = "fake-turn-1",
            command = "write-item", cwd = Environment.CurrentDirectory, reason = "A concrete command needs review.",
            proposedExecpolicyAmendment = new List<string> { "write-item --safe" },
            proposedNetworkPolicyAmendments = new[] { new { action = "allow", host = "api.example.test" } },
        }),
        EmitRequestAsync(16, "mcpServer/elicitation/request", new
        {
            serverName = "docs", threadId = "fake-thread-1", mode = "url", elicitationId = "fake-elicitation-url",
            message = "Continue authentication in a browser.", url = "https://example.com/authorize?state=fake-state",
        }),
        EmitRequestAsync(17, "mcpServer/elicitation/request", new
        {
            serverName = "docs", threadId = "fake-thread-1", mode = "openai/form", message = "Unsupported extension form.",
            requestedSchema = new { type = "object" },
        }),
        EmitRequestAsync(18, "mcpServer/elicitation/request", new
        {
            serverName = "docs", threadId = "fake-thread-1", mode = "openai/userVerification",
            title = "Verify identity", description = "Untrusted challenge text must not be projected.", challenge = "fake-secret-challenge",
        })
    );

async Task EmitRequestAsync<TId>(TId id, string method, object parameters)
{
    JsonElement jsonId = JsonSerializer.SerializeToElement(id);
    interactionMethods[GetRequestKey(jsonId)] = method;
    await WriteAsync(new { id, method, @params = parameters }).ConfigureAwait(false);
}

static string GetRequestKey(JsonElement id) => id.ValueKind switch
{
    JsonValueKind.String => "s:" + id.GetString(),
    JsonValueKind.Number => "n:" + id.GetRawText(),
    _ => "invalid:" + id.GetRawText(),
};
