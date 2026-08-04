using System.Collections.Concurrent;
using System.Text;
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

    event Func<ContextCompactionEvent, CancellationToken, Task>? ContextCompacted;

    event Func<ReviewModeEvent, CancellationToken, Task>? ReviewModeChanged;

    event Func<ThreadGoalEvent, CancellationToken, Task>? ThreadGoalChanged;

    event Func<RateLimitsResult, CancellationToken, Task>? RateLimitsChanged;

    event Func<EffectiveApprovalState, CancellationToken, Task>? EffectiveApprovalStateChanged;

    string? ActiveThreadId { get; }

    string? ActiveTurnId { get; }

    string? CodexVersion { get; }

    EffectiveApprovalState? EffectiveApprovalState { get; }

    string? EffectiveReasoningEffort { get; }

    string? EffectiveServiceTier { get; }

    Task InitializeAsync(IJsonRpcConnection connection, WorkerOptions options, CancellationToken cancellationToken);

    Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken);

    Task<StartAccountLoginResult> StartAccountLoginAsync(CancellationToken cancellationToken);

    Task<AccountStatus> LogoutAccountAsync(CancellationToken cancellationToken);

    Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken);

    Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken);

    Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken);

    Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken);

    Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(CancellationToken cancellationToken);

    Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken);

    Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken);

    Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken);

    Task<CompactThreadResult> CompactThreadAsync(CompactThreadRequest request, CancellationToken cancellationToken);

    Task<StartReviewResult> StartReviewAsync(StartReviewRequest request, CancellationToken cancellationToken);

    Task<ForkThreadResult> ForkThreadAsync(ForkThreadRequest request, CancellationToken cancellationToken);

    Task<ThreadGoalResult> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken);

    Task<ThreadGoalResult> SetThreadGoalAsync(SetThreadGoalRequest request, CancellationToken cancellationToken);

    Task<ThreadGoalResult> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken);

    Task<McpServerListResult> ListMcpServersAsync(string? threadId, CancellationToken cancellationToken);

    Task<ListSkillsResult> ListSkillsAsync(bool forceReload, CancellationToken cancellationToken);

    Task<UploadFeedbackResult> UploadFeedbackAsync(UploadFeedbackRequest request, CancellationToken cancellationToken);

    Task<RateLimitsResult> GetRateLimitsAsync(CancellationToken cancellationToken);

    Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken);

    Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken);
}

public sealed class CodexSessionService : ICodexSessionService, IAsyncDisposable
{
    private static readonly string[] ThreadSourceKinds = ["cli", "vscode", "appServer"];
    private const int PermissionProfilePageSize = 100;
    private const int MaxPermissionProfilePages = 10;
    private const int MaxPermissionProfiles = 500;
    private const int MaxPermissionProfileIdLength = 256;

    // skills/list has no pagination cursor (unlike model/list and permissionProfile/list), so the
    // array is server-supplied and unbounded. Cap it instead of trusting the server to be small.
    private const int MaxSkills = 200;
    private const int MaxSkillErrors = 50;
    private const int MaxSkillNameLength = 128;
    private const int MaxSkillTextLength = 512;

    // Not the legacy Windows MAX_PATH (260): that limit only applies without the long-paths
    // opt-in and would silently drop valid skills under deep workspaces or long user-profile
    // paths. 1024 is a generous display/memory bound while still rejecting pathological input.
    private const int MaxSkillPathLength = 1024;

    private readonly IApprovalPolicyEngine approvalPolicy;
    private readonly ISecretRedactor redactor;
    private readonly IPathAccessPolicy pathAccessPolicy;
    private readonly IProtectedDirectoryPolicy protectedDirectoryPolicy;
    private readonly ConcurrentDictionary<string, PendingApproval> pendingApprovals = new();
    private readonly ConcurrentDictionary<string, PendingUserInput> pendingUserInputs = new();
    private readonly ApprovalGrantStore approvalGrants = new();
    private readonly object unsupportedMethodsLock = new();
    private readonly HashSet<string> unsupportedMethods = new(StringComparer.Ordinal);
    private IJsonRpcConnection? connection;
    private WorkerOptions options = new();
    private StreamingBuffer? streamingBuffer;

    public CodexSessionService(
        IApprovalPolicyEngine approvalPolicy,
        ISecretRedactor redactor,
        IPathAccessPolicy? pathAccessPolicy = null,
        IProtectedDirectoryPolicy? protectedDirectoryPolicy = null)
    {
        this.approvalPolicy = approvalPolicy;
        this.redactor = redactor;
        this.pathAccessPolicy = pathAccessPolicy ?? new PathAccessPolicy();
        this.protectedDirectoryPolicy = protectedDirectoryPolicy ?? new ProtectedDirectoryPolicy();
    }

    public event Func<ConversationEvent, CancellationToken, Task>? ConversationEventReceived;

    public event Func<ApprovalRequest, CancellationToken, Task>? ApprovalRequested;

    public event Func<string, CancellationToken, Task>? ApprovalResolved;

    public event Func<AccountStatus, CancellationToken, Task>? AccountStatusChanged;

    public event Func<ApprovalAuditRecord, CancellationToken, Task>? ApprovalAuditRecorded;

    public event Func<UserInputRequest, CancellationToken, Task>? UserInputRequested;

    public event Func<string, CancellationToken, Task>? UserInputResolved;

    public event Func<ContextCompactionEvent, CancellationToken, Task>? ContextCompacted;

    public event Func<ReviewModeEvent, CancellationToken, Task>? ReviewModeChanged;

    public event Func<ThreadGoalEvent, CancellationToken, Task>? ThreadGoalChanged;

    public event Func<RateLimitsResult, CancellationToken, Task>? RateLimitsChanged;

    public event Func<EffectiveApprovalState, CancellationToken, Task>? EffectiveApprovalStateChanged;

    public string? ActiveThreadId { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public string? CodexVersion { get; private set; }

    public EffectiveApprovalState? EffectiveApprovalState { get; private set; }

    public string? EffectiveReasoningEffort { get; private set; }

    public string? EffectiveServiceTier { get; private set; }

    public async Task InitializeAsync(IJsonRpcConnection connection, WorkerOptions options, CancellationToken cancellationToken)
    {
        CodexVersion = null;
        EffectiveApprovalState = null;
        EffectiveReasoningEffort = null;
        EffectiveServiceTier = null;
        foreach (PendingApproval approval in pendingApprovals.Values)
        {
            approval.Completion.TrySetResult("cancel");
        }

        pendingApprovals.Clear();
        approvalGrants.Clear();
        lock (unsupportedMethodsLock)
        {
            unsupportedMethods.Clear();
        }

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

        CodexVersion = ReadCodexVersion(initResponse);

        string? serverName = null;
        if (initResponse.TryGetProperty("serverInfo", out JsonElement serverInfo)
            && serverInfo.ValueKind == JsonValueKind.Object)
        {
            serverName = GetString(serverInfo, "name");
        }

        if (CodexVersion is not null || serverName is not null)
        {
            await EmitAsync(new ConversationEvent
            {
                Kind = ConversationEventKind.Unknown,
                Text = $"Connected to {serverName ?? "codex"} app-server v{CodexVersion ?? "unknown"}.",
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string? ReadCodexVersion(JsonElement initResponse)
    {
        string? userAgent = GetString(initResponse, "userAgent");
        if (userAgent is not null)
        {
            int separator = userAgent.IndexOf(' ');
            ReadOnlySpan<char> firstProduct = separator >= 0
                ? userAgent.AsSpan(0, separator)
                : userAgent.AsSpan();
            int slash = firstProduct.IndexOf('/');
            if (slash > 0)
            {
                ReadOnlySpan<char> version = firstProduct[(slash + 1)..];
                if (IsValidCodexVersion(version))
                {
                    return version.ToString();
                }
            }
        }

        if (initResponse.TryGetProperty("serverInfo", out JsonElement serverInfo)
            && serverInfo.ValueKind == JsonValueKind.Object)
        {
            string? legacyVersion = GetString(serverInfo, "version");
            if (legacyVersion is not null && IsValidCodexVersion(legacyVersion.AsSpan()))
            {
                return legacyVersion;
            }
        }

        return null;
    }

    private static bool IsValidCodexVersion(ReadOnlySpan<char> version)
    {
        if (version.IsEmpty || version.Length > 64)
        {
            return false;
        }

        int buildSeparator = version.IndexOf('+');
        ReadOnlySpan<char> withoutBuild = buildSeparator >= 0 ? version[..buildSeparator] : version;
        if (buildSeparator >= 0
            && (!IsValidIdentifierList(version[(buildSeparator + 1)..], allowNumericLeadingZero: true)
                || version[(buildSeparator + 1)..].Contains('+')))
        {
            return false;
        }

        int prereleaseSeparator = withoutBuild.IndexOf('-');
        ReadOnlySpan<char> core = prereleaseSeparator >= 0 ? withoutBuild[..prereleaseSeparator] : withoutBuild;
        if (prereleaseSeparator >= 0
            && !IsValidIdentifierList(withoutBuild[(prereleaseSeparator + 1)..], allowNumericLeadingZero: false))
        {
            return false;
        }

        int firstDot = core.IndexOf('.');
        if (firstDot <= 0)
        {
            return false;
        }

        int secondDotOffset = core[(firstDot + 1)..].IndexOf('.');
        if (secondDotOffset <= 0)
        {
            return false;
        }

        int secondDot = firstDot + 1 + secondDotOffset;
        return !core[(secondDot + 1)..].Contains('.')
            && IsValidCoreComponent(core[..firstDot])
            && IsValidCoreComponent(core[(firstDot + 1)..secondDot])
            && IsValidCoreComponent(core[(secondDot + 1)..]);
    }

    private static bool IsValidIdentifierList(ReadOnlySpan<char> identifiers, bool allowNumericLeadingZero)
    {
        if (identifiers.IsEmpty)
        {
            return false;
        }

        while (true)
        {
            int dot = identifiers.IndexOf('.');
            ReadOnlySpan<char> identifier = dot >= 0 ? identifiers[..dot] : identifiers;
            if (identifier.IsEmpty)
            {
                return false;
            }

            bool numeric = true;
            foreach (char value in identifier)
            {
                bool isDigit = value is >= '0' and <= '9';
                bool isLetter = value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
                if (!isDigit && !isLetter && value != '-')
                {
                    return false;
                }

                numeric &= isDigit;
            }

            if (!allowNumericLeadingZero && numeric && HasLeadingZero(identifier))
            {
                return false;
            }

            if (dot < 0)
            {
                return true;
            }

            identifiers = identifiers[(dot + 1)..];
        }
    }

    private static bool IsValidCoreComponent(ReadOnlySpan<char> value)
        => IsAsciiDigits(value) && !HasLeadingZero(value);

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasLeadingZero(ReadOnlySpan<char> value) => value.Length > 1 && value[0] == '0';

    public async Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await SendAsync("thread/start", new { cwd = options.WorkingDirectory }, cancellationToken).ConfigureAwait(false);
        EffectiveApprovalState = ReadEffectiveApprovalState(result);
        ReadEffectiveTurnSettings(result, out string? reasoningEffort, out string? serviceTier);
        EffectiveReasoningEffort = reasoningEffort;
        EffectiveServiceTier = serviceTier;
        ThreadSummary summary = ReadThread(
            result.GetProperty("thread"),
            EffectiveApprovalState,
            EffectiveReasoningEffort,
            EffectiveServiceTier);
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
        EffectiveApprovalState = ReadEffectiveApprovalState(result);
        ReadEffectiveTurnSettings(result, out string? reasoningEffort, out string? serviceTier);
        EffectiveReasoningEffort = reasoningEffort;
        EffectiveServiceTier = serviceTier;
        ThreadSummary summary = ReadThread(
            result.GetProperty("thread"),
            EffectiveApprovalState,
            EffectiveReasoningEffort,
            EffectiveServiceTier);
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
        WorkerDiagnostics.Write("app-server model list request starting");
        try
        {
            JsonElement result = await RequireConnection().SendRequestAsync(
                "model/list",
                // Include hidden models so the catalog default (which may be a hidden preset and
                // is otherwise filtered out server-side) can still be surfaced in the picker.
                new { includeHidden = true },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            ListModelsResult models = ReadModelsResult(result);
            WorkerDiagnostics.Write($"app-server model list request completed count={models.Models.Count}");
            return models;
        }
        catch (OperationCanceledException ex)
        {
            WorkerDiagnostics.Write("app-server model list request canceled", ex);
            throw;
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("app-server model list request failed; keeping fallback models", ex);
            return new ListModelsResult();
        }
    }

    public async Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(CancellationToken cancellationToken)
    {
        const string method = "permissionProfile/list";
        if (!options.ExperimentalApi)
        {
            return Unsupported<ListPermissionProfilesResult>(
                "Permission profiles require the experimental app-server API.");
        }

        var profiles = new List<PermissionProfileInfo>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        bool truncated = false;

        for (int page = 0; page < MaxPermissionProfilePages; page++)
        {
            OperationCallResult call = await TrySendOperationAsync(
                method,
                new { cwd = options.WorkingDirectory, cursor, limit = PermissionProfilePageSize },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            if (!call.IsSupported)
            {
                return Unsupported<ListPermissionProfilesResult>(
                    "Permission profiles are not supported by this app-server.");
            }

            bool pageWasTruncated = ReadPermissionProfiles(call.Result, profiles, seenIds);
            if (profiles.Count >= MaxPermissionProfiles)
            {
                truncated = pageWasTruncated || GetString(call.Result, "nextCursor") is not null;
                break;
            }

            string? rawNextCursor = GetString(call.Result, "nextCursor");
            if (rawNextCursor is null)
            {
                break;
            }

            string? nextCursor = NormalizeCursor(rawNextCursor);
            if (nextCursor is null)
            {
                truncated = true;
                break;
            }

            if (!seenCursors.Add(nextCursor))
            {
                truncated = true;
                break;
            }

            cursor = nextCursor;
            if (page == MaxPermissionProfilePages - 1)
            {
                truncated = true;
            }
        }

        return new ListPermissionProfilesResult
        {
            Profiles = profiles,
            IsTruncated = truncated,
        };
    }

    public async Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
    {
        ValidateTurnApprovalOverrides(request);
        List<object> input = BuildTurnInput(request);
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = request.ThreadId,
            ["input"] = input,
        };
        AddOptional(parameters, "model", request.Model);
        AddOptional(parameters, "approvalPolicy", request.ApprovalPolicy);
        AddOptional(parameters, "approvalsReviewer", request.ApprovalsReviewer);
        if (request.SandboxMode is not null)
        {
            parameters["sandboxPolicy"] = new { type = request.SandboxMode };
        }

        AddOptional(parameters, "permissions", request.Permissions);
        if (request.HasEffort)
        {
            parameters["effort"] = request.Effort;
        }

        AddOptional(parameters, "personality", request.Personality);
        if (request.HasServiceTier)
        {
            parameters["serviceTier"] = request.ServiceTier;
        }

        if (request.CollaborationMode is not null)
        {
            var collaborationSettings = new Dictionary<string, object?>
            {
                ["model"] = request.CollaborationMode.Model,
            };
            if (request.HasEffort)
            {
                collaborationSettings["reasoning_effort"] = request.CollaborationMode.ReasoningEffort;
            }

            AddOptional(
                collaborationSettings,
                "developer_instructions",
                request.CollaborationMode.DeveloperInstructions);
            parameters["collaborationMode"] = new Dictionary<string, object?>
            {
                ["mode"] = request.CollaborationMode.Mode,
                ["settings"] = collaborationSettings,
            };
        }

        JsonElement result = await SendAsync(
            "turn/start",
            parameters,
            cancellationToken).ConfigureAwait(false);
        ActiveThreadId = request.ThreadId;
        ActiveTurnId = result.GetProperty("turn").GetProperty("id").GetString();
        if (request.HasEffort)
        {
            EffectiveReasoningEffort = request.Effort;
        }

        if (request.HasServiceTier)
        {
            EffectiveServiceTier = request.ServiceTier;
        }

        return ActiveTurnId ?? string.Empty;
    }

    private static void AddOptional(Dictionary<string, object?> values, string name, object? value)
    {
        if (value is not null)
        {
            values[name] = value;
        }
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

    public async Task<CompactThreadResult> CompactThreadAsync(
        CompactThreadRequest request,
        CancellationToken cancellationToken)
    {
        OperationCallResult call = await TrySendOperationAsync(
            "thread/compact/start",
            new { threadId = request.ThreadId },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<CompactThreadResult>("Manual context compaction is not supported by this app-server.");
        }

        ActiveThreadId = request.ThreadId;
        return new CompactThreadResult();
    }

    public async Task<StartReviewResult> StartReviewAsync(
        StartReviewRequest request,
        CancellationToken cancellationToken)
    {
        object target = CreateReviewTarget(request.Target);
        OperationCallResult call = await TrySendOperationAsync(
            "review/start",
            new
            {
                threadId = request.ThreadId,
                target,
                delivery = request.Delivery == ReviewDelivery.Detached ? "detached" : "inline",
            },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<StartReviewResult>("Code review is not supported by this app-server.");
        }

        string? reviewThreadId = GetString(call.Result, "reviewThreadId");
        string? turnId = call.Result.TryGetProperty("turn", out JsonElement turn)
            ? GetString(turn, "id")
            : null;
        ActiveThreadId = reviewThreadId ?? request.ThreadId;
        ActiveTurnId = turnId;
        return new StartReviewResult
        {
            ReviewThreadId = reviewThreadId,
            TurnId = turnId,
        };
    }

    public async Task<ForkThreadResult> ForkThreadAsync(
        ForkThreadRequest request,
        CancellationToken cancellationToken)
    {
        OperationCallResult call = await TrySendOperationAsync(
            "thread/fork",
            new { threadId = request.ThreadId },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<ForkThreadResult>("Thread forking is not supported by this app-server.");
        }

        EffectiveApprovalState = ReadEffectiveApprovalState(call.Result);
        ReadEffectiveTurnSettings(call.Result, out string? reasoningEffort, out string? serviceTier);
        EffectiveReasoningEffort = reasoningEffort;
        EffectiveServiceTier = serviceTier;
        ThreadSummary? thread = call.Result.TryGetProperty("thread", out JsonElement threadElement)
            ? ReadThread(
                threadElement,
                EffectiveApprovalState,
                EffectiveReasoningEffort,
                EffectiveServiceTier)
            : null;
        ActiveThreadId = thread?.Id ?? ActiveThreadId;
        ActiveTurnId = null;
        return new ForkThreadResult { Thread = thread };
    }

    public async Task<ThreadGoalResult> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken)
    {
        OperationCallResult call = await TrySendOperationAsync(
            "thread/goal/get",
            new { threadId },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<ThreadGoalResult>("Thread goals are not supported by this app-server.");
        }

        return new ThreadGoalResult { Goal = ReadOptionalGoal(call.Result) };
    }

    public async Task<ThreadGoalResult> SetThreadGoalAsync(
        SetThreadGoalRequest request,
        CancellationToken cancellationToken)
    {
        ValidateGoalRequest(request);
        OperationCallResult call = await TrySendOperationAsync(
            "thread/goal/set",
            new
            {
                threadId = request.ThreadId,
                objective = request.Objective,
                status = request.Status.HasValue ? ToWireGoalStatus(request.Status.Value) : null,
                tokenBudget = request.TokenBudget,
            },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<ThreadGoalResult>("Thread goals are not supported by this app-server.");
        }

        return new ThreadGoalResult { Goal = ReadOptionalGoal(call.Result) };
    }

    public async Task<ThreadGoalResult> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken)
    {
        OperationCallResult call = await TrySendOperationAsync(
            "thread/goal/clear",
            new { threadId },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<ThreadGoalResult>("Thread goals are not supported by this app-server.");
        }

        return new ThreadGoalResult
        {
            Cleared = GetBool(call.Result, "cleared") == true,
        };
    }

    public async Task<McpServerListResult> ListMcpServersAsync(
        string? threadId,
        CancellationToken cancellationToken)
    {
        OperationCallResult call = await TrySendOperationAsync(
            "mcpServerStatus/list",
            new { cursor = (string?)null, limit = 100, detail = "toolsAndAuthOnly", threadId },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<McpServerListResult>("MCP server status is not supported by this app-server.");
        }

        return new McpServerListResult { Servers = ReadMcpServers(call.Result) };
    }

    public async Task<ListSkillsResult> ListSkillsAsync(bool forceReload, CancellationToken cancellationToken)
    {
        // No experimentalApi gate: nothing in the app-server protocol marks skills/list as
        // experimental (unlike permissionProfile/list), and that gate's failure mode is sticky
        // for the rest of the session. Rely purely on the -32601 capability probe below.
        OperationCallResult call = await TrySendOperationAsync(
            "skills/list",
            new { cwds = Array.Empty<string>(), forceReload },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<ListSkillsResult>("Skills are not supported by this app-server.");
        }

        return ReadSkills(call.Result);
    }

    public async Task<UploadFeedbackResult> UploadFeedbackAsync(
        UploadFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        ValidateFeedbackRequest(request);
        OperationCallResult call = await TrySendOperationAsync(
            "feedback/upload",
            new
            {
                classification = request.Classification,
                reason = request.Reason,
                includeLogs = request.IncludeLogs,
                threadId = request.ThreadId,
                tags = request.Tags.Count == 0 ? null : request.Tags,
                extraLogFiles = (string[]?)null,
            },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<UploadFeedbackResult>("Feedback upload is not supported by this app-server.");
        }

        return new UploadFeedbackResult { ThreadId = GetString(call.Result, "threadId") };
    }

    public async Task<RateLimitsResult> GetRateLimitsAsync(CancellationToken cancellationToken)
    {
        OperationCallResult call = await TrySendOperationAsync(
            "account/rateLimits/read",
            new { },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (!call.IsSupported)
        {
            return Unsupported<RateLimitsResult>("Rate-limit status is not supported by this app-server.");
        }

        return ReadRateLimitsResult(call.Result);
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

    private List<object> BuildTurnInput(StartTurnRequest request)
    {
        var input = new List<object>
        {
            new { type = "text", text = request.Text },
        };

        var includedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int attachmentCount = 0;
        foreach (AttachmentInfo attachment in request.Attachments)
        {
            if (attachmentCount == 10)
            {
                break;
            }

            bool isImage = string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase);
            bool isMention = string.Equals(attachment.Kind, "mention", StringComparison.OrdinalIgnoreCase);
            if ((!isImage && !isMention)
                || !TryNormalizeReadableFile(attachment.Path, allowOutsideWorkspace: true, out string normalizedPath)
                || !includedPaths.Add(normalizedPath))
            {
                continue;
            }

            attachmentCount++;

            if (isImage)
            {
                input.Add(new
                {
                    type = "localImage",
                    path = normalizedPath,
                });
            }
            else
            {
                input.Add(new
                {
                    type = "mention",
                    name = Path.GetFileName(normalizedPath),
                    path = normalizedPath,
                });
            }
        }

        if (request.IdeContext is null)
        {
            return input;
        }

        string? activeDocumentPath = request.IdeContext.ActiveDocumentPath;
        if (TryNormalizeReadableFile(activeDocumentPath, allowOutsideWorkspace: false, out string normalizedActivePath)
            && includedPaths.Add(normalizedActivePath))
        {
            input.Add(new
            {
                type = "mention",
                name = Path.GetFileName(normalizedActivePath),
                path = normalizedActivePath,
            });
        }

        foreach (string path in request.IdeContext.ReferencedFilePaths.Take(10))
        {
            if (!TryNormalizeReadableFile(path, allowOutsideWorkspace: false, out string normalizedPath)
                || !includedPaths.Add(normalizedPath))
            {
                continue;
            }

            input.Add(new
            {
                type = "mention",
                name = Path.GetFileName(normalizedPath),
                path = normalizedPath,
            });
        }

        string? selection = LimitUtf8(request.IdeContext.SelectionText, 32 * 1024);
        if (!string.IsNullOrEmpty(selection))
        {
            string? selectionPath = request.IdeContext.SelectionFilePath;
            string header = IsWorkspacePath(selectionPath)
                ? $"IDE selection from {selectionPath}:"
                : "IDE selection:";
            input.Add(new
            {
                type = "text",
                text = $"{header}{Environment.NewLine}{selection}",
            });
        }

        return input;
    }

    private bool IsWorkspacePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string workspace = Path.GetFullPath(options.WorkingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            return candidate.StartsWith(workspace, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool TryNormalizeReadableFile(string? path, bool allowOutsideWorkspace, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        PathAccessResult result = pathAccessPolicy.Evaluate(path, options.WorkingDirectory);
        if (!result.IsValid
            || (!allowOutsideWorkspace && !result.IsWithinWorkspace)
            || protectedDirectoryPolicy.IsProtected(result.NormalizedPath)
            || !File.Exists(result.NormalizedPath))
        {
            return false;
        }

        normalizedPath = result.NormalizedPath;
        return true;
    }

    private static string? LimitUtf8(string? value, int maximumBytes)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        int low = 0;
        int high = value.Length;
        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= maximumBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0 && char.IsHighSurrogate(value[low - 1]))
        {
            low--;
        }

        return value[..low];
    }

    private static object CreateReviewTarget(ReviewTarget target)
    {
        string? value = string.IsNullOrWhiteSpace(target.Value) ? null : target.Value.Trim();
        return target.Kind switch
        {
            ReviewTargetKind.UncommittedChanges => new { type = "uncommittedChanges" },
            ReviewTargetKind.BaseBranch when value is not null => new { type = "baseBranch", branch = value },
            ReviewTargetKind.Commit when value is not null => new { type = "commit", sha = value, title = target.Title },
            ReviewTargetKind.Custom when value is not null => new { type = "custom", instructions = value },
            _ => throw new ArgumentException("The selected review target requires a value.", nameof(target)),
        };
    }

    private static void ValidateGoalRequest(SetThreadGoalRequest request)
    {
        ValidateGoalObjective(request.Objective);
        ValidateGoalTokenBudget(request.TokenBudget);
    }

    private static void ValidateFeedbackRequest(UploadFeedbackRequest request)
    {
        ValidateFeedbackClassification(request.Classification);
        ValidateFeedbackReason(request.Reason);
        ValidateFeedbackTags(request.Tags);
    }

    private static void ValidateGoalObjective(string? objective)
    {
        if (objective is not null && (string.IsNullOrWhiteSpace(objective) || objective.Length > 4_000))
        {
            throw new ArgumentOutOfRangeException(
                nameof(objective),
                "A goal objective must contain between 1 and 4,000 characters.");
        }
    }

    private static void ValidateGoalTokenBudget(long? tokenBudget)
    {
        if (tokenBudget is not null && tokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), "A token budget must be greater than zero.");
        }
    }

    private static void ValidateFeedbackClassification(string classification)
    {
        if (string.IsNullOrWhiteSpace(classification) || classification.Length > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(classification),
                "A feedback classification must contain between 1 and 64 characters.");
        }
    }

    private static void ValidateFeedbackReason(string? reason)
    {
        if (reason?.Length > 4_000)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "Feedback text cannot exceed 4,000 characters.");
        }
    }

    private static void ValidateFeedbackTags(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.Count > 20
            || tags.Any(pair => pair.Key.Length > 128 || pair.Value.Length > 128))
        {
            throw new ArgumentOutOfRangeException(nameof(tags), "Feedback tags exceed the supported limits.");
        }
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

        if (method == "thread/settings/updated")
        {
            bool isActiveThread = ActiveThreadId is not null
                && string.Equals(threadId, ActiveThreadId, StringComparison.Ordinal);
            bool hasSettings = (parameters.TryGetProperty("threadSettings", out JsonElement threadSettings)
                    || parameters.TryGetProperty("settings", out threadSettings))
                && threadSettings.ValueKind == JsonValueKind.Object;
            if (isActiveThread && hasSettings)
            {
                EffectiveApprovalState = ReadEffectiveApprovalState(threadSettings);
                ReadEffectiveTurnSettings(threadSettings, out string? reasoningEffort, out string? serviceTier);
                EffectiveReasoningEffort = reasoningEffort;
                EffectiveServiceTier = serviceTier;
                if (EffectiveApprovalStateChanged is not null)
                {
                    await EffectiveApprovalStateChanged(EffectiveApprovalState, cancellationToken).ConfigureAwait(false);
                }

            }

            return;
        }

        if (method == "account/rateLimits/updated"
            && parameters.TryGetProperty("rateLimits", out JsonElement updatedRateLimits))
        {
            await EmitRateLimitsChangedAsync(
                new RateLimitsResult { RateLimits = ReadRateLimit(updatedRateLimits) },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "thread/goal/updated")
        {
            await EmitThreadGoalChangedAsync(
                new ThreadGoalEvent
                {
                    ThreadId = threadId ?? string.Empty,
                    TurnId = turnId,
                    Goal = parameters.TryGetProperty("goal", out JsonElement goal)
                        ? ReadGoal(goal)
                        : null,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "thread/goal/cleared")
        {
            await EmitThreadGoalChangedAsync(
                new ThreadGoalEvent
                {
                    ThreadId = threadId ?? string.Empty,
                    TurnId = turnId,
                    IsCleared = true,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "context/compacted")
        {
            await EmitContextCompactedAsync(
                new ContextCompactionEvent
                {
                    ThreadId = threadId ?? string.Empty,
                    TurnId = turnId ?? string.Empty,
                    IsCompleted = true,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if ((method is "item/started" or "item/completed")
            && parameters.TryGetProperty("item", out JsonElement specialItem)
            && await TryEmitSpecialItemAsync(
                specialItem,
                threadId,
                turnId,
                method == "item/completed",
                cancellationToken).ConfigureAwait(false))
        {
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

    private async Task<OperationCallResult> TrySendOperationAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        lock (unsupportedMethodsLock)
        {
            if (unsupportedMethods.Contains(method))
            {
                return OperationCallResult.Unsupported;
            }
        }

        try
        {
            JsonElement result = await RequireConnection().SendRequestAsync(
                method,
                parameters,
                timeout,
                cancellationToken).ConfigureAwait(false);
            return new OperationCallResult(true, result);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == -32601)
        {
            lock (unsupportedMethodsLock)
            {
                unsupportedMethods.Add(method);
            }

            WorkerDiagnostics.Write($"app-server method disabled for this session method={method}", ex);
            return OperationCallResult.Unsupported;
        }
    }

    private static T Unsupported<T>(string reason)
        where T : AppServerOperationResult, new()
        => new()
        {
            IsSupported = false,
            UnavailableReason = reason,
        };

    private async Task<bool> TryEmitSpecialItemAsync(
        JsonElement item,
        string? threadId,
        string? turnId,
        bool isCompleted,
        CancellationToken cancellationToken)
    {
        string? itemType = GetString(item, "type");
        string? itemId = GetString(item, "id");
        if (itemType == "contextCompaction")
        {
            await EmitContextCompactedAsync(
                new ContextCompactionEvent
                {
                    ThreadId = threadId ?? string.Empty,
                    TurnId = turnId ?? string.Empty,
                    ItemId = itemId,
                    IsCompleted = isCompleted,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (itemType is "enteredReviewMode" or "exitedReviewMode")
        {
            await EmitReviewModeChangedAsync(
                new ReviewModeEvent
                {
                    ThreadId = threadId ?? string.Empty,
                    TurnId = turnId ?? string.Empty,
                    ItemId = itemId,
                    ChangeKind = itemType == "enteredReviewMode"
                        ? ReviewModeChangeKind.Entered
                        : ReviewModeChangeKind.Exited,
                    Review = redactor.Redact(GetString(item, "review")),
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private IJsonRpcConnection RequireConnection() => connection ?? throw new InvalidOperationException("The app-server is not initialized.");

    private Task EmitAsync(ConversationEvent value, CancellationToken cancellationToken)
        => ConversationEventReceived?.Invoke(value, cancellationToken) ?? Task.CompletedTask;

    private Task EmitApprovalResolvedAsync(string requestId, CancellationToken cancellationToken)
        => ApprovalResolved?.Invoke(requestId, cancellationToken) ?? Task.CompletedTask;

    private Task EmitUserInputResolvedAsync(string requestId, CancellationToken cancellationToken)
        => UserInputResolved?.Invoke(requestId, cancellationToken) ?? Task.CompletedTask;

    private Task EmitContextCompactedAsync(ContextCompactionEvent value, CancellationToken cancellationToken)
        => ContextCompacted?.Invoke(value, cancellationToken) ?? Task.CompletedTask;

    private Task EmitReviewModeChangedAsync(ReviewModeEvent value, CancellationToken cancellationToken)
        => ReviewModeChanged?.Invoke(value, cancellationToken) ?? Task.CompletedTask;

    private Task EmitThreadGoalChangedAsync(ThreadGoalEvent value, CancellationToken cancellationToken)
        => ThreadGoalChanged?.Invoke(value, cancellationToken) ?? Task.CompletedTask;

    private Task EmitRateLimitsChangedAsync(RateLimitsResult value, CancellationToken cancellationToken)
        => RateLimitsChanged?.Invoke(value, cancellationToken) ?? Task.CompletedTask;

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

    private static ThreadSummary ReadThread(
        JsonElement thread,
        EffectiveApprovalState? effectiveApprovalState = null,
        string? effectiveReasoningEffort = null,
        string? effectiveServiceTier = null) => new()
        {
            Id = GetString(thread, "id") ?? string.Empty,
            Preview = GetString(thread, "preview"),
            Cwd = GetString(thread, "cwd"),
            UpdatedAt = thread.TryGetProperty("updatedAt", out JsonElement updated) && updated.TryGetInt64(out long value) ? value : null,
            EffectiveApprovalState = effectiveApprovalState,
            EffectiveReasoningEffort = effectiveReasoningEffort,
            EffectiveServiceTier = effectiveServiceTier,
        };

    private static void ReadEffectiveTurnSettings(
        JsonElement value,
        out string? reasoningEffort,
        out string? serviceTier)
    {
        JsonElement settings = value;
        if ((value.TryGetProperty("threadSettings", out JsonElement nested)
                || value.TryGetProperty("settings", out nested))
            && nested.ValueKind == JsonValueKind.Object)
        {
            settings = nested;
        }
        else if (value.TryGetProperty("thread", out JsonElement thread)
            && thread.ValueKind == JsonValueKind.Object)
        {
            settings = thread;
            if ((thread.TryGetProperty("threadSettings", out nested)
                    || thread.TryGetProperty("settings", out nested))
                && nested.ValueKind == JsonValueKind.Object)
            {
                settings = nested;
            }
        }

        reasoningEffort = NormalizeWireIdentifier(
            GetString(settings, "effort")
            ?? GetString(settings, "reasoningEffort")
            ?? GetString(settings, "reasoning_effort")
            ?? GetString(value, "effort")
            ?? GetString(value, "reasoningEffort")
            ?? GetString(value, "reasoning_effort"));
        serviceTier = NormalizeWireIdentifier(
            GetString(settings, "serviceTier")
            ?? GetString(settings, "service_tier")
            ?? GetString(value, "serviceTier")
            ?? GetString(value, "service_tier"));
    }

    private static EffectiveApprovalState ReadEffectiveApprovalState(JsonElement value)
    {
        string? activePermissionProfile = null;
        if (value.TryGetProperty("activePermissionProfile", out JsonElement activeProfile))
        {
            activePermissionProfile = activeProfile.ValueKind switch
            {
                JsonValueKind.Object => NormalizeEffectiveIdentifier(GetString(activeProfile, "id")),
                JsonValueKind.String => NormalizeEffectiveIdentifier(activeProfile.GetString()),
                _ => null,
            };
        }

        JsonElement sandbox = default;
        bool hasSandbox = (value.TryGetProperty("sandbox", out sandbox)
                || value.TryGetProperty("sandboxPolicy", out sandbox))
            && sandbox.ValueKind == JsonValueKind.Object;
        return new EffectiveApprovalState
        {
            ActivePermissionProfile = activePermissionProfile,
            ApprovalPolicy = NormalizeWireIdentifier(GetString(value, "approvalPolicy")),
            ApprovalsReviewer = NormalizeWireIdentifier(GetString(value, "approvalsReviewer")),
            SandboxMode = hasSandbox ? NormalizeWireIdentifier(GetString(sandbox, "type")) : null,
        };
    }

    private bool ReadPermissionProfiles(
        JsonElement result,
        List<PermissionProfileInfo> profiles,
        HashSet<string> seenIds)
    {
        if (!result.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement profile in data.EnumerateArray())
        {
            if (profiles.Count >= MaxPermissionProfiles)
            {
                return true;
            }

            if (profile.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = NormalizePermissionProfileId(GetString(profile, "id"));
            if (id is null || !seenIds.Add(id))
            {
                continue;
            }

            profiles.Add(new PermissionProfileInfo
            {
                Id = id,
                Description = SanitizePermissionProfileDescription(GetString(profile, "description")),
                Allowed = GetBool(profile, "allowed") == true,
            });
        }

        return false;
    }

    private string? SanitizePermissionProfileDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string redacted = redactor.Redact(value);
        string sanitized = new(redacted.Where(character => !char.IsControl(character)).Take(512).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized.Trim();
    }

    private static string? NormalizePermissionProfileId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= MaxPermissionProfileIdLength
            && trimmed.All(character => !char.IsControl(character))
                ? trimmed
                : null;
    }

    private static string? NormalizeEffectiveIdentifier(string? value)
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

    private static string? NormalizeCursor(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length <= 512
            && value.All(character => !char.IsControl(character))
                ? value
                : null;

    private static void ValidateTurnApprovalOverrides(StartTurnRequest request)
    {
        if (request.Permissions is not null)
        {
            if (request.ApprovalPolicy is not null
                || request.ApprovalsReviewer is not null
                || request.SandboxMode is not null)
            {
                throw new ArgumentException(
                    "A permissions profile cannot be combined with approval, reviewer, or sandbox overrides.",
                    nameof(request));
            }

            string? normalizedProfile = NormalizePermissionProfileId(request.Permissions);
            if (!string.Equals(normalizedProfile, request.Permissions, StringComparison.Ordinal))
            {
                throw new ArgumentException("The permissions profile id is invalid.", nameof(request));
            }
        }

        if (request.ApprovalPolicy is not null
            && request.ApprovalPolicy is not ("untrusted" or "on-request" or "never"))
        {
            throw new ArgumentException("The approval policy override is invalid.", nameof(request));
        }

        if (request.ApprovalsReviewer is not null
            && request.ApprovalsReviewer is not ("user" or "auto_review"))
        {
            throw new ArgumentException("The approvals reviewer override is invalid.", nameof(request));
        }

        if (request.SandboxMode is not null
            && request.SandboxMode is not ("readOnly" or "workspaceWrite" or "dangerFullAccess"))
        {
            throw new ArgumentException("The sandbox override is invalid.", nameof(request));
        }
    }

    private ListModelsResult ReadModelsResult(JsonElement result)
    {
        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var modelInfoById = new Dictionary<string, ModelInfo>(StringComparer.Ordinal);
        string? defaultModel = null;
        ModelInfo? defaultModelInfo = null;

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

                // The "model" field carries the slug used for turn/start; fall back to "id" for older shapes.
                string? id = NormalizeModelId(GetString(model, "model") ?? GetString(model, "id"));
                if (id is null)
                {
                    continue;
                }

                // Capture the catalog default even when it is hidden, so the picker can still offer it.
                if (defaultModel is null && GetBool(model, "isDefault") == true)
                {
                    defaultModel = id;
                }

                ModelInfo modelInfo = ReadModelInfo(model, id);
                modelInfoById.TryAdd(id, modelInfo);
                if (GetBool(model, "isDefault") == true)
                {
                    defaultModelInfo = modelInfo;
                }

                if (GetBool(model, "hidden") == true)
                {
                    continue;
                }

                if (!seen.Add(id))
                {
                    continue;
                }

                models.Add(modelInfo);
            }
        }

        // Fall back to a top-level "defaultModel" key when no entry was flagged as default.
        if (defaultModel is null)
        {
            string? topLevelDefault = NormalizeModelId(GetString(result, "defaultModel"));
            if (topLevelDefault is not null && modelInfoById.TryGetValue(topLevelDefault, out ModelInfo? modelInfo))
            {
                defaultModel = topLevelDefault;
                defaultModelInfo = modelInfo;
            }
        }

        WorkerDiagnostics.Write(
            $"app-server model list parsed; count={models.Count} default={redactor.Redact(defaultModel ?? "(none)")}");

        return new ListModelsResult
        {
            Models = models,
            DefaultModel = defaultModel,
            DefaultModelInfo = defaultModelInfo,
        };
    }

    private ModelInfo ReadModelInfo(JsonElement model, string id)
    {
        string? displayName = GetString(model, "displayName");
        return new ModelInfo
        {
            Id = id,
            DisplayName = displayName is null ? null : redactor.Redact(displayName),
            DefaultReasoningEffort = NormalizeWireIdentifier(GetString(model, "defaultReasoningEffort")),
            SupportedReasoningEfforts = ReadReasoningEfforts(model),
            SupportsPersonality = GetBool(model, "supportsPersonality") == true,
            DefaultServiceTier = NormalizeWireIdentifier(GetString(model, "defaultServiceTier")),
            ServiceTiers = ReadServiceTiers(model),
        };
    }

    private IReadOnlyList<ReasoningEffortInfo> ReadReasoningEfforts(JsonElement model)
    {
        if (!model.TryGetProperty("supportedReasoningEfforts", out JsonElement efforts)
            || efforts.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ReasoningEffortInfo>();
        }

        var result = new List<ReasoningEffortInfo>();
        foreach (JsonElement effort in efforts.EnumerateArray())
        {
            string? id = NormalizeWireIdentifier(GetString(effort, "reasoningEffort"));
            if (id is null)
            {
                continue;
            }

            result.Add(new ReasoningEffortInfo
            {
                Id = id,
                Description = redactor.Redact(GetString(effort, "description")),
            });
        }

        return result;
    }

    private IReadOnlyList<ServiceTierInfo> ReadServiceTiers(JsonElement model)
    {
        if (!model.TryGetProperty("serviceTiers", out JsonElement serviceTiers)
            || serviceTiers.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ServiceTierInfo>();
        }

        var result = new List<ServiceTierInfo>();
        foreach (JsonElement serviceTier in serviceTiers.EnumerateArray())
        {
            string? id = NormalizeWireIdentifier(GetString(serviceTier, "id"));
            if (id is null)
            {
                continue;
            }

            result.Add(new ServiceTierInfo
            {
                Id = id,
                Name = redactor.Redact(GetString(serviceTier, "name")),
                Description = redactor.Redact(GetString(serviceTier, "description")),
            });
        }

        return result;
    }

    private ThreadGoalInfo? ReadOptionalGoal(JsonElement result)
        => result.TryGetProperty("goal", out JsonElement goal)
        && goal.ValueKind == JsonValueKind.Object
            ? ReadGoal(goal)
            : null;

    private ThreadGoalInfo ReadGoal(JsonElement goal) => new()
    {
        ThreadId = GetString(goal, "threadId") ?? string.Empty,
        Objective = redactor.Redact(GetString(goal, "objective")) ?? string.Empty,
        Status = FromWireGoalStatus(GetString(goal, "status")),
        TokenBudget = GetInt64(goal, "tokenBudget"),
        TokensUsed = GetInt64(goal, "tokensUsed") ?? 0,
        TimeUsedSeconds = GetInt64(goal, "timeUsedSeconds") ?? 0,
        CreatedAt = GetInt64(goal, "createdAt") ?? 0,
        UpdatedAt = GetInt64(goal, "updatedAt") ?? 0,
    };

    // SkillsListResponse.data is SkillsListEntry[] (one entry per requested cwd). skills/list is
    // always called with cwds: [] in v1, so the app-server returns exactly one entry, but this
    // still walks the array defensively: the Fake app-server's unmatched-method fallback (`_ =>
    // new { }`) has no "data" property at all, and a naive result.GetProperty("data") would throw
    // KeyNotFoundException outside the -32601 capability probe in TrySendOperationAsync.
    private ListSkillsResult ReadSkills(JsonElement result)
    {
        var skills = new List<SkillInfo>();
        var errors = new List<SkillLoadError>();
        bool truncated = false;

        if (result.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (skills.Count >= MaxSkills && errors.Count >= MaxSkillErrors)
                {
                    // Both caps were already reached by an earlier entry. skills/list is
                    // untrusted and unbounded, so stop enumerating remaining server-supplied
                    // entries entirely rather than paying per-entry sanitization and
                    // property-lookup cost on data that would be dropped anyway.
                    truncated = true;
                    break;
                }

                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? cwd = SanitizeSkillText(GetString(entry, "cwd"));

                if (entry.TryGetProperty("errors", out JsonElement errorArray)
                    && errorArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement error in errorArray.EnumerateArray())
                    {
                        if (error.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (errors.Count >= MaxSkillErrors)
                        {
                            truncated = true;
                            break;
                        }

                        string? message = SanitizeSkillText(GetString(error, "message"));
                        if (message is null)
                        {
                            continue;
                        }

                        errors.Add(new SkillLoadError
                        {
                            Cwd = cwd,
                            Path = SanitizeSkillText(GetString(error, "path"), MaxSkillPathLength),
                            Message = message,
                        });
                    }
                }

                if (entry.TryGetProperty("skills", out JsonElement skillArray)
                    && skillArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement skill in skillArray.EnumerateArray())
                    {
                        if (skill.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (skills.Count >= MaxSkills)
                        {
                            truncated = true;
                            break;
                        }

                        SkillInfo? info = ReadSkill(skill, cwd);
                        if (info is not null)
                        {
                            skills.Add(info);
                        }
                    }
                }
            }
        }

        return new ListSkillsResult
        {
            Skills = skills,
            Errors = errors,
            IsTruncated = truncated,
        };
    }

    private SkillInfo? ReadSkill(JsonElement skill, string? cwd)
    {
        // name/path/scope/enabled/description are all required by SkillMetadata
        // (schemas/v2/SkillsListResponse.json). A response missing any of them is treated as
        // malformed for that entry and the skill is dropped rather than surfaced half-populated.
        string? name = NormalizeSkillName(GetString(skill, "name"));
        string? path = NormalizeSkillPath(GetString(skill, "path"));
        string? scope = NormalizeWireIdentifier(GetString(skill, "scope"));
        bool? enabled = GetBool(skill, "enabled");
        string? rawDescription = GetString(skill, "description");
        if (name is null || path is null || scope is null || enabled is null || rawDescription is null)
        {
            return null;
        }

        string? shortDescription = SanitizeSkillText(GetString(skill, "shortDescription"));
        string? displayName = null;
        if (skill.TryGetProperty("interface", out JsonElement skillInterface)
            && skillInterface.ValueKind == JsonValueKind.Object)
        {
            // interface.brandColor, interface.iconSmall/iconLarge, and interface.defaultPrompt are
            // deliberately not read here: brandColor is a free-form attacker-supplied string that
            // would have to be interpreted as a WPF brush, the icon paths are AbsolutePathBuf values
            // that would bind an Image.Source to a file the app-server chose, and defaultPrompt is
            // attacker-controlled text destined for the composer (a prompt-injection vector). None
            // of the three are exposed by this contract; see doc/adr.md.
            displayName = SanitizeSkillText(GetString(skillInterface, "displayName"));
            shortDescription ??= SanitizeSkillText(GetString(skillInterface, "shortDescription"));
        }

        return new SkillInfo
        {
            Name = name,
            Description = SanitizeSkillText(rawDescription) ?? string.Empty,
            ShortDescription = shortDescription,
            DisplayName = displayName,
            Scope = scope,
            Path = path,
            Enabled = enabled.Value,
            Cwd = cwd,
        };
    }

    private string? SanitizeSkillText(string? value, int maxLength = MaxSkillTextLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string redacted = redactor.Redact(value);
        string sanitized = new(redacted.Where(character => !char.IsControl(character)).Take(maxLength).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized.Trim();
    }

    private static string? NormalizeSkillName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Unlike NormalizeWireIdentifier, this does not restrict to [A-Za-z0-9_-]: real skill
        // names may legitimately contain other characters (spaces, dots, etc.), and applying that
        // narrower charset here would silently drop otherwise-valid skills.
        string trimmed = value.Trim();
        return trimmed.Length <= MaxSkillNameLength && trimmed.All(character => !char.IsControl(character))
            ? trimmed
            : null;
    }

    private static string? NormalizeSkillPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxSkillPathLength || trimmed.Any(char.IsControl))
        {
            return null;
        }

        // No File.Exists / workspace-containment check: this path is the app-server's own
        // skills/list output, not user input, and scope: "user"/"system"/"admin" skills routinely
        // live outside the workspace (or are directories). Only structural validity is enforced.
        return Path.IsPathRooted(trimmed) ? trimmed : null;
    }

    private IReadOnlyList<McpServerStatusInfo> ReadMcpServers(JsonElement result)
    {
        if (!result.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<McpServerStatusInfo>();
        }

        var servers = new List<McpServerStatusInfo>();
        foreach (JsonElement server in data.EnumerateArray())
        {
            if (server.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tools = new List<string>();
            if (server.TryGetProperty("tools", out JsonElement toolMap)
                && toolMap.ValueKind == JsonValueKind.Object)
            {
                tools.AddRange(toolMap.EnumerateObject().Select(tool => redactor.Redact(tool.Name)));
            }

            string? displayName = null;
            if (server.TryGetProperty("serverInfo", out JsonElement serverInfo)
                && serverInfo.ValueKind == JsonValueKind.Object)
            {
                displayName = GetString(serverInfo, "title") ?? GetString(serverInfo, "name");
            }

            servers.Add(new McpServerStatusInfo
            {
                Name = redactor.Redact(GetString(server, "name")) ?? string.Empty,
                DisplayName = redactor.Redact(displayName),
                AuthStatus = NormalizeWireIdentifier(GetString(server, "authStatus")) ?? string.Empty,
                ToolNames = tools,
                ResourceCount = GetArrayLength(server, "resources"),
                ResourceTemplateCount = GetArrayLength(server, "resourceTemplates"),
            });
        }

        return servers;
    }

    private RateLimitsResult ReadRateLimitsResult(JsonElement result)
    {
        var byLimitId = new Dictionary<string, RateLimitInfo>(StringComparer.Ordinal);
        if (result.TryGetProperty("rateLimitsByLimitId", out JsonElement map)
            && map.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in map.EnumerateObject())
            {
                byLimitId[redactor.Redact(property.Name)] = ReadRateLimit(property.Value);
            }
        }

        return new RateLimitsResult
        {
            RateLimits = result.TryGetProperty("rateLimits", out JsonElement rateLimits)
                && rateLimits.ValueKind == JsonValueKind.Object
                    ? ReadRateLimit(rateLimits)
                    : null,
            RateLimitsByLimitId = byLimitId,
        };
    }

    private RateLimitInfo ReadRateLimit(JsonElement value) => new()
    {
        LimitId = redactor.Redact(GetString(value, "limitId")),
        LimitName = redactor.Redact(GetString(value, "limitName")),
        PlanType = NormalizeWireIdentifier(GetString(value, "planType")),
        ReachedType = NormalizeWireIdentifier(GetString(value, "rateLimitReachedType")),
        Primary = ReadRateLimitWindow(value, "primary"),
        Secondary = ReadRateLimitWindow(value, "secondary"),
        Credits = ReadCredits(value),
    };

    private static RateLimitWindowInfo? ReadRateLimitWindow(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement window)
            || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new RateLimitWindowInfo
        {
            UsedPercent = GetInt32(window, "usedPercent"),
            ResetsAt = GetInt64(window, "resetsAt"),
            WindowDurationMinutes = GetInt64(window, "windowDurationMins"),
        };
    }

    private CreditsInfo? ReadCredits(JsonElement value)
    {
        if (!value.TryGetProperty("credits", out JsonElement credits)
            || credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CreditsInfo
        {
            HasCredits = GetBool(credits, "hasCredits") == true,
            Unlimited = GetBool(credits, "unlimited") == true,
            Balance = redactor.Redact(GetString(credits, "balance")),
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

    private static string? NormalizeWireIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 128
            && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
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

    private static long? GetInt64(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out long value)
                ? value
                : null;

    private static int? GetInt32(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out int value)
                ? value
                : null;

    private static int GetArrayLength(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Array
                ? property.GetArrayLength()
                : 0;

    private static string ToWireDecision(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Accept => "accept",
        ApprovalDecision.AcceptForTurn => "accept",
        ApprovalDecision.AcceptForThread => "accept",
        ApprovalDecision.AcceptForSession => "acceptForSession",
        ApprovalDecision.Decline => "decline",
        _ => "cancel",
    };

    private static string ToWireGoalStatus(ThreadGoalStatus status) => status switch
    {
        ThreadGoalStatus.Active => "active",
        ThreadGoalStatus.Paused => "paused",
        ThreadGoalStatus.Blocked => "blocked",
        ThreadGoalStatus.UsageLimited => "usageLimited",
        ThreadGoalStatus.BudgetLimited => "budgetLimited",
        ThreadGoalStatus.Complete => "complete",
        _ => "active",
    };

    private static ThreadGoalStatus FromWireGoalStatus(string? status) => status switch
    {
        "paused" => ThreadGoalStatus.Paused,
        "blocked" => ThreadGoalStatus.Blocked,
        "usageLimited" => ThreadGoalStatus.UsageLimited,
        "budgetLimited" => ThreadGoalStatus.BudgetLimited,
        "complete" => ThreadGoalStatus.Complete,
        _ => ThreadGoalStatus.Active,
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

    private readonly record struct OperationCallResult(bool IsSupported, JsonElement Result)
    {
        public static OperationCallResult Unsupported { get; } = new(false, default);
    }
}
