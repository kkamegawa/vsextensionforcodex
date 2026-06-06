using System.Collections.Concurrent;
using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public interface ICodexSessionService : IAsyncDisposable
{
    event Func<ConversationEvent, CancellationToken, Task>? ConversationEventReceived;

    event Func<ApprovalRequest, CancellationToken, Task>? ApprovalRequested;

    event Func<string, CancellationToken, Task>? ApprovalResolved;

    string? ActiveThreadId { get; }

    string? ActiveTurnId { get; }

    Task InitializeAsync(IJsonRpcConnection connection, WorkerOptions options, CancellationToken cancellationToken);

    Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken);

    Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken);

    Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken);

    Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken);

    Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken);

    Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken);

    Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken);
}

public sealed class CodexSessionService : ICodexSessionService, IAsyncDisposable
{
    private static readonly string[] ThreadSourceKinds = ["cli", "vscode", "appServer"];

    private readonly IApprovalPolicyEngine approvalPolicy;
    private readonly ISecretRedactor redactor;
    private readonly ConcurrentDictionary<string, PendingApproval> pendingApprovals = new();
    private readonly HashSet<string> sessionApprovals = new(StringComparer.OrdinalIgnoreCase);
    private IJsonRpcConnection? connection;
    private WorkerOptions options = new();
    private StreamingBuffer? streamingBuffer;

    public CodexSessionService(IApprovalPolicyEngine approvalPolicy, ISecretRedactor redactor)
    {
        this.approvalPolicy = approvalPolicy;
        this.redactor = redactor;
    }

    public event Func<ConversationEvent, CancellationToken, Task>? ConversationEventReceived;

    public event Func<ApprovalRequest, CancellationToken, Task>? ApprovalRequested;

    public event Func<string, CancellationToken, Task>? ApprovalResolved;

    public string? ActiveThreadId { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public async Task InitializeAsync(IJsonRpcConnection connection, WorkerOptions options, CancellationToken cancellationToken)
    {
        foreach (PendingApproval approval in pendingApprovals.Values)
        {
            approval.Completion.TrySetResult("cancel");
        }

        pendingApprovals.Clear();
        sessionApprovals.Clear();
        this.connection = connection;
        this.options = options;
        connection.NotificationReceived += OnNotificationAsync;
        connection.RequestReceived += OnServerRequestAsync;
        string overflowDirectory = Path.Combine(
            Path.GetTempPath(),
            "Kkamegawa.CodexForVisualStudio",
            Guid.NewGuid().ToString("N"));
        streamingBuffer = new StreamingBuffer(EmitAsync, overflowDirectory);

        JsonElement initResponse = await connection.SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "codex_visual_studio",
                    title = "Codex for Visual Studio",
                    version = options.ExtensionVersion,
                },
                capabilities = new { experimentalApi = options.ExperimentalApi },
            },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        await connection.SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);

        if (initResponse.TryGetProperty("serverInfo", out JsonElement serverInfo))
        {
            string? serverName = GetString(serverInfo, "name");
            string? serverVersion = GetString(serverInfo, "version");
            await EmitAsync(new ConversationEvent
            {
                Kind = ConversationEventKind.Unknown,
                Text = $"Connected to {serverName ?? "codex"} app-server v{serverVersion ?? "unknown"}.",
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await SendAsync("thread/start", new { cwd = options.WorkingDirectory }, cancellationToken).ConfigureAwait(false);
        ThreadSummary summary = ReadThread(result.GetProperty("thread"));
        ActiveThreadId = summary.Id;
        return summary;
    }

    public async Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        JsonElement result = await SendAsync("thread/resume", new { threadId }, cancellationToken).ConfigureAwait(false);
        ThreadSummary summary = ReadThread(result.GetProperty("thread"));
        ActiveThreadId = summary.Id;
        return summary;
    }

    public async Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken)
    {
        JsonElement result = await SendAsync(
            "thread/list",
            new { cursor, limit = 25, sourceKinds = ThreadSourceKinds },
            cancellationToken).ConfigureAwait(false);
        var threads = new List<ThreadSummary>();
        if (result.TryGetProperty("data", out JsonElement data))
        {
            foreach (JsonElement thread in data.EnumerateArray())
            {
                threads.Add(ReadThread(thread));
            }
        }

        return new ThreadPage
        {
            Threads = threads,
            NextCursor = GetString(result, "nextCursor"),
        };
    }

    public async Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
    {
        JsonElement result = await SendAsync(
            "turn/start",
            new { threadId = request.ThreadId, input = new[] { new { type = "text", text = request.Text } } },
            cancellationToken).ConfigureAwait(false);
        ActiveThreadId = request.ThreadId;
        ActiveTurnId = result.GetProperty("turn").GetProperty("id").GetString();
        return ActiveTurnId ?? string.Empty;
    }

    public async Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(ActiveTurnId, request.ExpectedTurnId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The expected turn is no longer active.");
        }

        JsonElement result = await SendAsync(
            "turn/steer",
            new
            {
                threadId = request.ThreadId,
                expectedTurnId = request.ExpectedTurnId,
                input = new[] { new { type = "text", text = request.Text } },
            },
            cancellationToken).ConfigureAwait(false);
        return GetString(result, "turnId") ?? request.ExpectedTurnId;
    }

    public async Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken)
    {
        await RequireConnection().SendRequestAsync(
            "turn/interrupt",
            new { threadId = request.ThreadId, turnId = request.TurnId },
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
    }

    public Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
    {
        if (!pendingApprovals.TryRemove(request.RequestId, out PendingApproval? pending))
        {
            return Task.CompletedTask;
        }

        if (request.Decision == ApprovalDecision.AcceptForSession)
        {
            sessionApprovals.Add(pending.Request.RiskKey);
        }

        pending.Completion.TrySetResult(ToWireDecision(request.Decision));
        return EmitApprovalResolvedAsync(request.RequestId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (streamingBuffer is not null)
        {
            await streamingBuffer.DisposeAsync().ConfigureAwait(false);
        }

        foreach (PendingApproval approval in pendingApprovals.Values)
        {
            approval.Completion.TrySetResult("cancel");
        }

        pendingApprovals.Clear();
    }

    private async Task<JsonElement> OnServerRequestAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        string requestId = message.GetIdKey() ?? Guid.NewGuid().ToString("N");
        JsonElement parameters = message.Params ?? JsonSerializer.SerializeToElement(new { });
        ApprovalRequest request = CreateApprovalRequest(requestId, message.Method ?? string.Empty, parameters);
        if (request.IsPolicyBlocked)
        {
            return ApprovalResponse("decline");
        }

        if (sessionApprovals.Contains(request.RiskKey))
        {
            return ApprovalResponse("accept");
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingApprovals[requestId] = new PendingApproval(request, completion);
        if (ApprovalRequested is not null)
        {
            await ApprovalRequested(request, cancellationToken).ConfigureAwait(false);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using CancellationTokenRegistration registration = linked.Token.Register(() => completion.TrySetResult("cancel"));
        string decision = await completion.Task.ConfigureAwait(false);
        pendingApprovals.TryRemove(requestId, out _);
        await EmitApprovalResolvedAsync(requestId, CancellationToken.None).ConfigureAwait(false);
        return ApprovalResponse(decision);
    }

    private async Task OnNotificationAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchNotificationAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EmitAsync(new ConversationEvent
            {
                Kind = ConversationEventKind.Error,
                Text = $"Unhandled notification '{message.Method}': {ex.Message}",
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task DispatchNotificationAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        string method = message.Method ?? string.Empty;
        JsonElement parameters = message.Params ?? JsonSerializer.SerializeToElement(new { });
        string? threadId = GetString(parameters, "threadId");
        string? turnId = GetString(parameters, "turnId");
        string? itemId = GetString(parameters, "itemId");
        if (method == "turn/started" && parameters.TryGetProperty("turn", out JsonElement startedTurn))
        {
            ActiveTurnId = GetString(startedTurn, "id");
            turnId = ActiveTurnId;
        }
        else if (method == "turn/completed")
        {
            ActiveTurnId = null;
        }

        if (method == "serverRequest/resolved")
        {
            string? requestId = GetString(parameters, "requestId");
            if (requestId is not null && pendingApprovals.TryRemove(requestId, out PendingApproval? pending))
            {
                pending.Completion.TrySetResult("cancel");
                await EmitApprovalResolvedAsync(requestId, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        ConversationEventKind kind = MapKind(method);
        var output = new ConversationEvent
        {
            Kind = kind,
            ThreadId = threadId,
            TurnId = turnId,
            ItemId = itemId,
            PayloadJson = redactor.Redact(parameters.GetRawText()),
        };
        string? delta = GetString(parameters, "delta") ?? GetString(parameters, "text");
        if (delta is not null && streamingBuffer is not null)
        {
            int limit = kind switch
            {
                ConversationEventKind.ReasoningSummaryDelta => StreamingBuffer.ReasoningLimit,
                ConversationEventKind.CommandOutputDelta => StreamingBuffer.CommandOutputLimit,
                ConversationEventKind.DiffUpdated => StreamingBuffer.DiffLimit,
                _ => StreamingBuffer.CommandOutputLimit,
            };
            streamingBuffer.Append($"{method}:{itemId ?? turnId ?? "global"}", output, redactor.Redact(delta), limit);
            return;
        }

        await EmitAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private ApprovalRequest CreateApprovalRequest(string requestId, string method, JsonElement parameters)
    {
        string? command = GetString(parameters, "command");
        string? cwd = GetString(parameters, "cwd");
        string? grantRoot = GetString(parameters, "grantRoot");
        string? networkHost = null;
        int? networkPort = null;
        if (parameters.TryGetProperty("networkApprovalContext", out JsonElement network))
        {
            networkHost = GetString(network, "host");
            if (network.TryGetProperty("port", out JsonElement port) && port.TryGetInt32(out int parsedPort))
            {
                networkPort = parsedPort;
            }
        }

        ApprovalPolicyResult policy = method.Contains("fileChange", StringComparison.Ordinal)
            ? approvalPolicy.EvaluateFile(grantRoot, options.WorkingDirectory)
            : approvalPolicy.EvaluateCommand(command, cwd, options.WorkingDirectory, networkHost, networkPort);
        return new ApprovalRequest
        {
            RequestId = requestId,
            Method = method,
            ThreadId = GetString(parameters, "threadId") ?? string.Empty,
            TurnId = GetString(parameters, "turnId") ?? string.Empty,
            ItemId = GetString(parameters, "itemId"),
            Risk = policy.Risk,
            RiskKey = policy.RiskKey,
            DisplayText = redactor.Redact(command ?? grantRoot ?? networkHost ?? method),
            Reason = redactor.Redact(GetString(parameters, "reason")),
            IsPolicyBlocked = policy.IsBlocked,
            PolicyBlockReason = policy.BlockReason,
            AvailableDecisions = policy.IsBlocked
                ? Array.Empty<ApprovalDecision>()
                : new[] { ApprovalDecision.Accept, ApprovalDecision.AcceptForSession, ApprovalDecision.Decline, ApprovalDecision.Cancel },
        };
    }

    private Task<JsonElement> SendAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        return RequireConnection().SendRequestAsync(method, parameters, TimeSpan.FromSeconds(60), cancellationToken);
    }

    private IJsonRpcConnection RequireConnection() => connection ?? throw new InvalidOperationException("The app-server is not initialized.");

    private Task EmitAsync(ConversationEvent value, CancellationToken cancellationToken)
        => ConversationEventReceived?.Invoke(value, cancellationToken) ?? Task.CompletedTask;

    private Task EmitApprovalResolvedAsync(string requestId, CancellationToken cancellationToken)
        => ApprovalResolved?.Invoke(requestId, cancellationToken) ?? Task.CompletedTask;

    private static ThreadSummary ReadThread(JsonElement thread) => new()
    {
        Id = GetString(thread, "id") ?? string.Empty,
        Preview = GetString(thread, "preview"),
        Cwd = GetString(thread, "cwd"),
        UpdatedAt = thread.TryGetProperty("updatedAt", out JsonElement updated) && updated.TryGetInt64(out long value) ? value : null,
    };

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static string ToWireDecision(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Accept => "accept",
        ApprovalDecision.AcceptForSession => "acceptForSession",
        ApprovalDecision.Decline => "decline",
        _ => "cancel",
    };

    private static JsonElement ApprovalResponse(string decision)
        => JsonSerializer.SerializeToElement(new { decision });

    private static ConversationEventKind MapKind(string method) => method switch
    {
        "item/started" => ConversationEventKind.ItemStarted,
        "item/completed" => ConversationEventKind.ItemCompleted,
        "item/agentMessage/delta" => ConversationEventKind.AgentMessageDelta,
        "item/reasoning/summaryTextDelta" => ConversationEventKind.ReasoningSummaryDelta,
        "item/commandExecution/outputDelta" => ConversationEventKind.CommandOutputDelta,
        "turn/diff/updated" => ConversationEventKind.DiffUpdated,
        "turn/plan/updated" => ConversationEventKind.PlanUpdated,
        "turn/started" => ConversationEventKind.TurnStarted,
        "turn/completed" => ConversationEventKind.TurnCompleted,
        "error" => ConversationEventKind.Error,
        _ => ConversationEventKind.Unknown,
    };

    private sealed record PendingApproval(ApprovalRequest Request, TaskCompletionSource<string> Completion);
}
