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

    event Func<AccountStatus, CancellationToken, Task>? AccountStatusChanged;

    event Func<ApprovalAuditRecord, CancellationToken, Task>? ApprovalAuditRecorded;

    event Func<UserInputRequest, CancellationToken, Task>? UserInputRequested;

    event Func<string, CancellationToken, Task>? UserInputResolved;

    string? ActiveThreadId { get; }

    string? ActiveTurnId { get; }

    Task InitializeAsync(IJsonRpcConnection connection, WorkerOptions options, CancellationToken cancellationToken);

    Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken);

    Task<StartAccountLoginResult> StartAccountLoginAsync(CancellationToken cancellationToken);

    Task<AccountStatus> LogoutAccountAsync(CancellationToken cancellationToken);

    Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken);

    Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken);

    Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken);

    Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken);

    Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken);

    Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken);

    Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken);

    Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken);

    Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken);
}

public sealed class CodexSessionService : ICodexSessionService, IAsyncDisposable
{
    private static readonly string[] ThreadSourceKinds = ["cli", "vscode", "appServer"];

    private readonly IApprovalPolicyEngine approvalPolicy;
    private readonly ISecretRedactor redactor;
    private readonly ConcurrentDictionary<string, PendingApproval> pendingApprovals = new();
    private readonly ConcurrentDictionary<string, PendingUserInput> pendingUserInputs = new();
    private readonly ApprovalGrantStore approvalGrants = new();
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

    public event Func<AccountStatus, CancellationToken, Task>? AccountStatusChanged;

    public event Func<ApprovalAuditRecord, CancellationToken, Task>? ApprovalAuditRecorded;

    public event Func<UserInputRequest, CancellationToken, Task>? UserInputRequested;

    public event Func<string, CancellationToken, Task>? UserInputResolved;

    public string? ActiveThreadId { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public async Task InitializeAsync(IJsonRpcConnection connection, WorkerOptions options, CancellationToken cancellationToken)
    {
        foreach (PendingApproval approval in pendingApprovals.Values)
        {
            approval.Completion.TrySetResult("cancel");
        }

        pendingApprovals.Clear();
        approvalGrants.Clear();
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

    public async Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken)
    {
        var checking = new AccountStatus { State = AccountState.Checking };
        await EmitAccountStatusAsync(checking, cancellationToken).ConfigureAwait(false);
        try
        {
            JsonElement result = await RequireConnection().SendRequestAsync(
                "account/read",
                new { refreshToken = false },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            AccountStatus status = ReadAccountStatus(result);
            WorkerDiagnostics.Write($"account status read completed state={status.State} plan={status.PlanType ?? "none"}");
            await EmitAccountStatusAsync(status, cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var status = new AccountStatus
            {
                State = AccountState.Unavailable,
                Message = "Codex could not read the account status.",
            };
            await EmitAccountStatusAsync(status, CancellationToken.None).ConfigureAwait(false);
            return status;
        }
    }

    public async Task<StartAccountLoginResult> StartAccountLoginAsync(CancellationToken cancellationToken)
    {
        WorkerDiagnostics.Write("app-server login request starting");
        var signingIn = new AccountStatus { State = AccountState.SigningIn };
        await EmitAccountStatusAsync(signingIn, cancellationToken).ConfigureAwait(false);
        try
        {
            JsonElement result = await RequireConnection().SendRequestAsync(
                "account/login/start",
                new { type = "chatgpt" },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            string? loginId = GetString(result, "loginId");
            string? authUrl = GetString(result, "authUrl");
            if (!string.Equals(GetString(result, "type"), "chatgpt", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(loginId)
                || !IsSecureAbsoluteUrl(authUrl))
            {
                var unavailable = new AccountStatus
                {
                    State = AccountState.Unavailable,
                    Message = "Codex returned an invalid ChatGPT sign-in response.",
                };
                await EmitAccountStatusAsync(unavailable, CancellationToken.None).ConfigureAwait(false);
                WorkerDiagnostics.Write("app-server login response rejected");
                return new StartAccountLoginResult { Status = unavailable };
            }

            WorkerDiagnostics.Write("app-server login response accepted");
            return new StartAccountLoginResult
            {
                Status = signingIn,
                LoginId = loginId,
                AuthUrl = authUrl,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WorkerDiagnostics.Write("app-server login request failed", ex);
            var unavailable = new AccountStatus
            {
                State = AccountState.Unavailable,
                Message = "Codex could not start ChatGPT sign-in.",
            };
            await EmitAccountStatusAsync(unavailable, CancellationToken.None).ConfigureAwait(false);
            return new StartAccountLoginResult { Status = unavailable };
        }
    }

    public async Task<AccountStatus> LogoutAccountAsync(CancellationToken cancellationToken)
    {
        WorkerDiagnostics.Write("app-server logout request starting");
        try
        {
            await RequireConnection().SendRequestAsync(
                "account/logout",
                new { },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write("app-server logout request completed");
            return await GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WorkerDiagnostics.Write("app-server logout request failed", ex);
            var unavailable = new AccountStatus
            {
                State = AccountState.Unavailable,
                Message = "Codex could not sign out.",
            };
            await EmitAccountStatusAsync(unavailable, CancellationToken.None).ConfigureAwait(false);
            return unavailable;
        }
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

    public async Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonElement result = await RequireConnection().SendRequestAsync(
                "model/list",
                new { },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            return ReadModelsResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("app-server model list request failed; keeping fallback models", ex);
            return new ListModelsResult();
        }
    }

    public async Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
    {
        JsonElement result = await SendAsync(
            "turn/start",
            new
            {
                threadId = request.ThreadId,
                input = new[] { new { type = "text", text = request.Text } },
                model = request.Model,
                approvalPolicy = request.ApprovalPolicy,
                sandboxPolicy = request.SandboxMode is null ? null : new { type = request.SandboxMode },
            },
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

    public async Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
    {
        if (!pendingApprovals.TryRemove(request.RequestId, out PendingApproval? pending))
        {
            return;
        }

        ApprovalScope scope = request.Decision switch
        {
            ApprovalDecision.AcceptForTurn => ApprovalScope.Turn,
            ApprovalDecision.AcceptForThread => ApprovalScope.Thread,
            ApprovalDecision.AcceptForSession => ApprovalScope.Session,
            _ => ApprovalScope.Once,
        };
        approvalGrants.Add(pending.Request, scope);
        if (scope != ApprovalScope.Once)
        {
            await EmitApprovalAuditAsync(pending.Request, ApprovalAuditAction.GrantCreated, scope, cancellationToken).ConfigureAwait(false);
        }

        pending.Completion.TrySetResult(ToWireDecision(request.Decision));
        await EmitApprovalResolvedAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
    {
        if (!pendingUserInputs.TryRemove(request.RequestId, out PendingUserInput? pending))
        {
            return;
        }

        Dictionary<string, string[]> validated = ValidateAnswers(pending.Request, request.Answers);
        pending.Completion.TrySetResult(validated);
        await EmitUserInputResolvedAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> HandleUserInputRequestAsync(string requestId, JsonElement parameters, CancellationToken cancellationToken)
    {
        UserInputRequest request = CreateUserInputRequest(requestId, parameters);
        var completion = new TaskCompletionSource<IReadOnlyDictionary<string, string[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingUserInputs[requestId] = new PendingUserInput(request, completion);
        if (UserInputRequested is not null)
        {
            await UserInputRequested(request, cancellationToken).ConfigureAwait(false);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using CancellationTokenRegistration registration = linked.Token.Register(() => completion.TrySetResult(EmptyAnswers));
        IReadOnlyDictionary<string, string[]> answers = await completion.Task.ConfigureAwait(false);
        pendingUserInputs.TryRemove(requestId, out _);
        await EmitUserInputResolvedAsync(requestId, CancellationToken.None).ConfigureAwait(false);
        return UserInputResponse(answers);
    }

    private UserInputRequest CreateUserInputRequest(string requestId, JsonElement parameters)
    {
        var questions = new List<UserInputQuestion>();
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("questions", out JsonElement questionArray)
            && questionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement question in questionArray.EnumerateArray())
            {
                if (question.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var options = new List<UserInputOption>();
                if (question.TryGetProperty("options", out JsonElement optionArray)
                    && optionArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement option in optionArray.EnumerateArray())
                    {
                        if (option.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        options.Add(new UserInputOption
                        {
                            // The label is echoed back to the app-server verbatim, so it must stay
                            // unredacted; the extension sanitizes it for display via SafeMarkdownService.
                            Label = GetString(option, "label") ?? string.Empty,
                            Description = redactor.Redact(GetString(option, "description")),
                        });
                    }
                }

                questions.Add(new UserInputQuestion
                {
                    Id = GetString(question, "id") ?? string.Empty,
                    Header = redactor.Redact(GetString(question, "header")),
                    Question = redactor.Redact(GetString(question, "question")),
                    Options = options,
                });
            }
        }

        return new UserInputRequest
        {
            RequestId = requestId,
            ThreadId = GetString(parameters, "threadId") ?? string.Empty,
            TurnId = GetString(parameters, "turnId") ?? string.Empty,
            ItemId = GetString(parameters, "itemId"),
            Questions = questions,
        };
    }

    // Only labels the server actually offered are echoed back; single-select keeps at most one.
    // Questions without options (free text / secret) are out of scope and never answered.
    private static Dictionary<string, string[]> ValidateAnswers(
        UserInputRequest request,
        IDictionary<string, string[]> answers)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (UserInputQuestion question in request.Questions)
        {
            if (question.Options.Count == 0
                || !answers.TryGetValue(question.Id, out string[]? selected)
                || selected is null)
            {
                continue;
            }

            var allowed = new HashSet<string>(question.Options.Select(option => option.Label), StringComparer.Ordinal);
            string[] valid = selected.Where(allowed.Contains).Take(1).ToArray();
            if (valid.Length > 0)
            {
                result[question.Id] = valid;
            }
        }

        return result;
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

        foreach (PendingUserInput userInput in pendingUserInputs.Values)
        {
            userInput.Completion.TrySetResult(EmptyAnswers);
        }

        pendingUserInputs.Clear();
    }

    private async Task<JsonElement> OnServerRequestAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        string requestId = message.GetIdKey() ?? Guid.NewGuid().ToString("N");
        JsonElement parameters = message.Params ?? JsonSerializer.SerializeToElement(new { });
        string method = message.Method ?? string.Empty;

        // Interactive choice prompts (request_user_input) are a distinct server request that
        // carries questions/options and expects selected answers — not an approval decision.
        if (IsUserInputRequest(method, parameters))
        {
            return await HandleUserInputRequestAsync(requestId, parameters, cancellationToken).ConfigureAwait(false);
        }

        ApprovalRequest request = CreateApprovalRequest(requestId, method, parameters);
        if (request.IsPolicyBlocked)
        {
            return ApprovalResponse("decline");
        }

        if (approvalGrants.FindApproval(request) is { } grant)
        {
            await EmitApprovalAuditAsync(request, ApprovalAuditAction.AutoApproved, grant.Scope, cancellationToken).ConfigureAwait(false);
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
            approvalGrants.EndTurn(threadId, ActiveTurnId ?? turnId);
            ActiveTurnId = null;
        }
        else if (method == "thread/closed")
        {
            approvalGrants.EndThread(threadId);
        }

        if (method == "serverRequest/resolved")
        {
            string? requestId = GetString(parameters, "requestId");
            if (requestId is not null && pendingApprovals.TryRemove(requestId, out PendingApproval? pending))
            {
                pending.Completion.TrySetResult("cancel");
                await EmitApprovalResolvedAsync(requestId, cancellationToken).ConfigureAwait(false);
            }
            else if (requestId is not null && pendingUserInputs.TryRemove(requestId, out PendingUserInput? pendingInput))
            {
                pendingInput.Completion.TrySetResult(EmptyAnswers);
                await EmitUserInputResolvedAsync(requestId, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (method is "account/login/completed" or "account/updated")
        {
            await GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
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
                :
                [
                    ApprovalDecision.Accept,
                    ApprovalDecision.AcceptForTurn,
                    ApprovalDecision.AcceptForThread,
                    ApprovalDecision.AcceptForSession,
                    ApprovalDecision.Decline,
                    ApprovalDecision.Cancel,
                ],
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

    private Task EmitUserInputResolvedAsync(string requestId, CancellationToken cancellationToken)
        => UserInputResolved?.Invoke(requestId, cancellationToken) ?? Task.CompletedTask;

    private async Task EmitAccountStatusAsync(AccountStatus status, CancellationToken cancellationToken)
    {
        if (AccountStatusChanged is null)
        {
            return;
        }

        try
        {
            await AccountStatusChanged(status, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("account status observer failed", ex);
        }
    }

    private Task EmitApprovalAuditAsync(
        ApprovalRequest request,
        ApprovalAuditAction action,
        ApprovalScope scope,
        CancellationToken cancellationToken)
        => ApprovalAuditRecorded?.Invoke(
            new ApprovalAuditRecord
            {
                RequestId = request.RequestId,
                Action = action,
                Risk = request.Risk,
                Scope = scope,
                DisplayText = request.DisplayText,
                ThreadId = request.ThreadId,
                TurnId = request.TurnId,
            },
            cancellationToken) ?? Task.CompletedTask;

    private static AccountStatus ReadAccountStatus(JsonElement result)
    {
        if (!result.TryGetProperty("account", out JsonElement account)
            || account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new AccountStatus { State = AccountState.SignedOut };
        }

        string? planType = NormalizePlanType(GetString(account, "planType")
            ?? GetString(account, "chatgptPlanType")
            ?? GetString(result, "planType")
            ?? GetString(result, "chatgptPlanType"));
        return new AccountStatus
        {
            State = AccountState.SignedIn,
            PlanType = planType,
        };
    }

    private static bool IsSecureAbsoluteUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizePlanType(string? value)
        => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? value
            : null;

    private static ThreadSummary ReadThread(JsonElement thread) => new()
    {
        Id = GetString(thread, "id") ?? string.Empty,
        Preview = GetString(thread, "preview"),
        Cwd = GetString(thread, "cwd"),
        UpdatedAt = thread.TryGetProperty("updatedAt", out JsonElement updated) && updated.TryGetInt64(out long value) ? value : null,
    };

    private ListModelsResult ReadModelsResult(JsonElement result)
    {
        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? defaultModel = null;

        // The codex app-server model/list response uses "data"; tolerate a legacy "models" key as well.
        if ((result.TryGetProperty("data", out JsonElement modelArray)
                || result.TryGetProperty("models", out modelArray))
            && modelArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement model in modelArray.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (GetBool(model, "hidden") == true)
                {
                    continue;
                }

                // The "model" field carries the slug used for turn/start; fall back to "id" for older shapes.
                string? id = NormalizeModelId(GetString(model, "model") ?? GetString(model, "id"));
                if (id is null || !seen.Add(id))
                {
                    continue;
                }

                string? displayName = GetString(model, "displayName");
                models.Add(new ModelInfo
                {
                    Id = id,
                    DisplayName = displayName is null ? null : redactor.Redact(displayName),
                });

                if (GetBool(model, "isDefault") == true)
                {
                    defaultModel = id;
                }
            }
        }

        // Fall back to a top-level "defaultModel" key when no entry was flagged as default.
        if (defaultModel is null)
        {
            string? topLevelDefault = NormalizeModelId(GetString(result, "defaultModel"));
            if (topLevelDefault is not null && seen.Contains(topLevelDefault))
            {
                defaultModel = topLevelDefault;
            }
        }

        WorkerDiagnostics.Write($"app-server model list parsed; count={models.Count} default={defaultModel ?? "(none)"}");

        return new ListModelsResult
        {
            Models = models,
            DefaultModel = defaultModel,
        };
    }

    private static string? NormalizeModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 128 && trimmed.All(character => !char.IsControl(character))
            ? trimmed
            : null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static bool? GetBool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
                ? property.GetBoolean()
                : null;

    private static string ToWireDecision(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Accept => "accept",
        ApprovalDecision.AcceptForTurn => "accept",
        ApprovalDecision.AcceptForThread => "accept",
        ApprovalDecision.AcceptForSession => "acceptForSession",
        ApprovalDecision.Decline => "decline",
        _ => "cancel",
    };

    private static JsonElement ApprovalResponse(string decision)
        => JsonSerializer.SerializeToElement(new { decision });

    private static readonly IReadOnlyDictionary<string, string[]> EmptyAnswers =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    private static bool IsUserInputRequest(string method, JsonElement parameters)
        => method.Contains("requestUserInput", StringComparison.OrdinalIgnoreCase)
        || method.Contains("request_user_input", StringComparison.OrdinalIgnoreCase)
        || (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("questions", out JsonElement questions)
            && questions.ValueKind == JsonValueKind.Array);

    // Shapes the result per ToolRequestUserInputResponse: { answers: { <id>: { answers: [...] } } }.
    private static JsonElement UserInputResponse(IReadOnlyDictionary<string, string[]> answers)
    {
        var map = answers.ToDictionary(
            pair => pair.Key,
            pair => (object)new { answers = pair.Value },
            StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(new { answers = map });
    }

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

    private sealed record PendingUserInput(
        UserInputRequest Request,
        TaskCompletionSource<IReadOnlyDictionary<string, string[]>> Completion);
}
