using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

internal sealed class CallbackScope(Action callback) : IDisposable
{
    private Action? release = callback;

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

public interface ICodexSessionService : IAsyncDisposable
{
    AppServerInitializationMetadata? InitializationMetadata { get; }
    string? StatePartitionFingerprint => null;
    long OwnerGeneration => 0;
    string? EmittingStatePartitionFingerprint => StatePartitionFingerprint;
    long EmittingOwnerGeneration => OwnerGeneration;
    AccountState? InvalidatedAccountState => null;
    bool InvalidatedByOwnerAction => false;
    IDisposable SuppressEmissionOwnerContext() => new CallbackScope(static () => { });
    bool CanPersistOwnerState => false;
    bool IsConnectionActive => true;
    bool GatewayOAuthRequired => false;
    void BeginOwnerPartition(WorkerOptions options, string workerInstanceId, long ownerGeneration, string? credentialFingerprint)
    {
    }

    event Func<IJsonRpcConnection, CancellationToken, Task>? OwnerInvalidated
    {
        add { }
        remove { }
    }
    event Func<ConversationEvent, CancellationToken, Task>? ConversationEventReceived;

    event Func<ApprovalRequest, CancellationToken, Task>? ApprovalRequested;

    event Func<string, CancellationToken, Task>? ApprovalResolved;

    event Func<AccountStatus, CancellationToken, Task>? AccountStatusChanged;

    event Func<ApprovalAuditRecord, CancellationToken, Task>? ApprovalAuditRecorded;

    event Func<UserInputRequest, CancellationToken, Task>? UserInputRequested;

    event Func<string, CancellationToken, Task>? UserInputResolved;

    event Func<PermissionRequest, CancellationToken, Task>? PermissionRequested
    {
        add { }
        remove { }
    }

    event Func<string, CancellationToken, Task>? PermissionResolved
    {
        add { }
        remove { }
    }

    event Func<McpElicitationRequest, CancellationToken, Task>? McpElicitationRequested
    {
        add { }
        remove { }
    }

    event Func<string, CancellationToken, Task>? McpElicitationResolved
    {
        add { }
        remove { }
    }

    event Func<UnsupportedInteractionNotice, CancellationToken, Task>? UnsupportedInteraction
    {
        add { }
        remove { }
    }

    event Func<InteractionAuthStatus, CancellationToken, Task>? InteractionAuthStatusChanged
    {
        add { }
        remove { }
    }

    event Func<ContextCompactionEvent, CancellationToken, Task>? ContextCompacted;

    event Func<ReviewModeEvent, CancellationToken, Task>? ReviewModeChanged;

    event Func<ThreadGoalEvent, CancellationToken, Task>? ThreadGoalChanged;

    event Func<RateLimitsResult, CancellationToken, Task>? RateLimitsChanged;

    event Func<EffectiveApprovalState, CancellationToken, Task>? EffectiveApprovalStateChanged;

    event Func<SkillsChangedEvent, CancellationToken, Task>? SkillsChanged;

    event Func<ThreadAttachmentUpdatedEvent, CancellationToken, Task>? ThreadAttachmentUpdated;

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

    Task<ThreadSummary> ReadThreadAsync(string threadId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<ThreadTurnsPage> ListThreadTurnsAsync(string threadId, string? cursor, int limit, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<ThreadItemsPage> ListThreadItemsAsync(
        string threadId,
        string? turnId,
        ThreadItemCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<ThreadAttachmentsPage> ListThreadAttachmentsAsync(
        string threadId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

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

    Task ResolvePermissionSelectionAsync(ResolvePermissionSelectionRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task ResolveMcpElicitationAsync(ResolveMcpElicitationRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<InteractionAuthStatus> ReadGatewayOAuthAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<InteractionAuthStatus> LoginGatewayOAuthAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<InteractionAuthStatus> CancelGatewayOAuthAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task OpenAuthorizationUrlAsync(OpenAuthorizationUrlRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<McpOAuthLoginStatus> StartMcpOAuthLoginAsync(StartMcpOAuthLoginRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task DismissMcpOAuthLoginAsync(DismissMcpOAuthLoginRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

/// <summary>An explicit turn attachment that the connected app-server cannot read.</summary>
public sealed class AttachmentRejectedException : InvalidOperationException
{
    public AttachmentRejectedException(string message)
        : base(message)
    {
    }
}

public sealed class SkillInvocationRejectedException : InvalidOperationException
{
    public SkillInvocationRejectedException(string message)
        : base(message)
    {
    }
}

public sealed class TurnStartOutcomeUnknownException : Exception
{
    public TurnStartOutcomeUnknownException(Exception innerException)
        : base("The turn/start request was dispatched, but its result could not be confirmed.", innerException)
    {
    }
}

public sealed record AppServerInitializationMetadata(
    string? CodexHome,
    string? PlatformFamily,
    string? PlatformOs,
    string? UserAgent);

public sealed class CodexSessionService : ICodexSessionService, IAsyncDisposable
{
    private const int MaxHistoryPageTurns = 50;
    private const int MaxHistoryPageItems = 100;
    private const int MaxAttachmentPageSize = 50;
    private const int MaxAttachmentsPerThread = 100;
    private const int MaxAttachmentPayloadBytes = 64 * 1024;
    private const int MaxAttachmentIdentityBytes = 256;
    private const int MaxThreadPreviewBytes = 8 * 1024;
    private const int MaxHistoryTextBytes = 64 * 1024;

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
    private const int MaxSkillDefaultPromptLength = 4096;
    private const int MaxSkillDependencyCount = 16;
    private const int MaxSkillDependencyTypeLength = 64;
    private const int MaxSkillDependencyValueLength = 256;
    private const int MaxSkillDependencyDescriptionLength = 512;

    // Not the legacy Windows MAX_PATH (260): that limit only applies without the long-paths
    // opt-in and would silently drop valid skills under deep workspaces or long user-profile
    // paths. 1024 is a generous display/memory bound while still rejecting pathological input.
    private const int MaxSkillPathLength = 1024;

    private readonly IApprovalPolicyEngine approvalPolicy;
    private readonly ISecretRedactor redactor;
    private readonly IPathAccessPolicy pathAccessPolicy;
    private readonly ILocalPathBoundary localPathBoundary;
    private readonly IProtectedDirectoryPolicy protectedDirectoryPolicy;
    private readonly PendingInteractionRegistry pendingInteractions;
    private readonly ProtectedAuthorizationUrlStore authorizationUrls = new();
    private readonly AsyncLocal<ConnectionContext?> emittingContext = new();
    private readonly object turnStateLock = new();
    private readonly HashSet<TurnKey> completedTurnIds = new();
    // Thread whose turn/start request is in flight. Its turn notifications can arrive before the
    // response updates ActiveThreadId, so they must not be treated as another thread's events.
    private string? pendingTurnThreadId;
    // Stop requests keyed by turn, used only to log how long a turn took to end after the request.
    private readonly Dictionary<TurnKey, long> interruptRequestedAt = new();
    private long connectionGeneration;
    private readonly SemaphoreSlim skillsCacheGate = new(1, 1);
    private readonly TimeProvider timeProvider;
    private readonly ISkillCatalogStore skillCatalogStore;
    private readonly Action<Uri> authorizationUrlOpener;
    private readonly object skillsBackgroundRefreshLock = new();
    private CancellationTokenSource skillsBackgroundRefreshCancellation = new();
    private Task? skillsBackgroundRefreshTask;
    private ListSkillsResult? skillsSnapshot;
    private DateTimeOffset skillsSnapshotExpiresAt;
    private long skillsGeneration;
    private ConnectionContext? connectionContext;
    private WorkerOptions options = new();
    private RemotePathMapper? remotePathMapper;
    private StreamingBuffer? streamingBuffer;
    private string workerInstanceId = Guid.NewGuid().ToString("N");
    private string? credentialFingerprint;
    private string? statePartitionFingerprint;
    private long ownerGeneration;
    private bool ownerPartitionPrepared;
    private AccountState? invalidatedAccountState;
    private bool invalidatedByOwnerAction;
    private const int MaxTurnAttachments = 10;
    public const string RemoteWorkingDirectoryLabel = "(remote working directory)";

    public CodexSessionService(
        IApprovalPolicyEngine approvalPolicy,
        ISecretRedactor redactor,
        IPathAccessPolicy? pathAccessPolicy = null,
        IProtectedDirectoryPolicy? protectedDirectoryPolicy = null,
        TimeProvider? timeProvider = null)
        : this(approvalPolicy, redactor, pathAccessPolicy, protectedDirectoryPolicy, timeProvider, null, null)
    {
    }

    internal CodexSessionService(
        IApprovalPolicyEngine approvalPolicy,
        ISecretRedactor redactor,
        IPathAccessPolicy? pathAccessPolicy,
        IProtectedDirectoryPolicy? protectedDirectoryPolicy,
        TimeProvider? timeProvider,
        ISkillCatalogStore? skillCatalogStore,
        Action<Uri>? authorizationUrlOpener = null)
    {
        this.approvalPolicy = approvalPolicy;
        this.redactor = redactor;
        this.pathAccessPolicy = pathAccessPolicy ?? new PathAccessPolicy();
        this.localPathBoundary = new LocalPathBoundary(this.pathAccessPolicy);
        this.protectedDirectoryPolicy = protectedDirectoryPolicy ?? new ProtectedDirectoryPolicy();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.skillCatalogStore = skillCatalogStore ?? new FileSkillCatalogStore(redactor);
        this.authorizationUrlOpener = authorizationUrlOpener ?? OpenWithDefaultBrowser;
        pendingInteractions = new PendingInteractionRegistry(this.timeProvider);
    }

    public event Func<ConversationEvent, CancellationToken, Task>? ConversationEventReceived;

    public event Func<ApprovalRequest, CancellationToken, Task>? ApprovalRequested;

    public event Func<string, CancellationToken, Task>? ApprovalResolved;

    public event Func<AccountStatus, CancellationToken, Task>? AccountStatusChanged;

    public event Func<ApprovalAuditRecord, CancellationToken, Task>? ApprovalAuditRecorded;

    public event Func<UserInputRequest, CancellationToken, Task>? UserInputRequested;

    public event Func<string, CancellationToken, Task>? UserInputResolved;

    public event Func<PermissionRequest, CancellationToken, Task>? PermissionRequested;

    public event Func<string, CancellationToken, Task>? PermissionResolved;

    public event Func<McpElicitationRequest, CancellationToken, Task>? McpElicitationRequested;

    public event Func<string, CancellationToken, Task>? McpElicitationResolved;

    public event Func<UnsupportedInteractionNotice, CancellationToken, Task>? UnsupportedInteraction;

    public event Func<InteractionAuthStatus, CancellationToken, Task>? InteractionAuthStatusChanged;

    public bool GatewayOAuthRequired => Volatile.Read(ref connectionContext)?.GatewayOAuthRequired ?? false;

    public event Func<ContextCompactionEvent, CancellationToken, Task>? ContextCompacted;

    public event Func<ReviewModeEvent, CancellationToken, Task>? ReviewModeChanged;

    public event Func<ThreadGoalEvent, CancellationToken, Task>? ThreadGoalChanged;

    public event Func<RateLimitsResult, CancellationToken, Task>? RateLimitsChanged;

    public event Func<EffectiveApprovalState, CancellationToken, Task>? EffectiveApprovalStateChanged;

    public event Func<SkillsChangedEvent, CancellationToken, Task>? SkillsChanged;

    public event Func<ThreadAttachmentUpdatedEvent, CancellationToken, Task>? ThreadAttachmentUpdated;

    public string? ActiveThreadId { get; private set; }

    public string? ActiveTurnId { get; private set; }

    public string? CodexVersion { get; private set; }

    public EffectiveApprovalState? EffectiveApprovalState { get; private set; }

    public string? EffectiveReasoningEffort { get; private set; }

    public string? EffectiveServiceTier { get; private set; }

    public AppServerInitializationMetadata? InitializationMetadata { get; private set; }

    public string? StatePartitionFingerprint => Volatile.Read(ref statePartitionFingerprint);

    public long OwnerGeneration => Interlocked.Read(ref ownerGeneration);

    public string? EmittingStatePartitionFingerprint
        => emittingContext.Value?.StatePartitionFingerprint ?? StatePartitionFingerprint;

    public long EmittingOwnerGeneration => emittingContext.Value?.OwnerGeneration ?? OwnerGeneration;
    public AccountState? InvalidatedAccountState => invalidatedAccountState;

    // True when the current owner itself requested the change (its own logout or the completion of
    // the sign-in it started), so the Worker may activate a new owner without a manual reconnect.
    public bool InvalidatedByOwnerAction => invalidatedByOwnerAction;

    public IDisposable SuppressEmissionOwnerContext()
    {
        ConnectionContext? previous = emittingContext.Value;
        emittingContext.Value = null;
        return new CallbackScope(() => emittingContext.Value = previous);
    }

    // The pinned account/read contract does not identify an account authoritatively. No state
    // partition is eligible for cross-instance disk-cache reuse.
    public bool CanPersistOwnerState => false;

    public bool IsConnectionActive => Volatile.Read(ref connectionContext) is not null;

    public event Func<IJsonRpcConnection, CancellationToken, Task>? OwnerInvalidated;

    public void BeginOwnerPartition(
        WorkerOptions options,
        string workerInstanceId,
        long ownerGeneration,
        string? credentialFingerprint)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.workerInstanceId = workerInstanceId;
        this.credentialFingerprint = credentialFingerprint;
        this.ownerGeneration = ownerGeneration;
        invalidatedAccountState = null;
        invalidatedByOwnerAction = false;
        statePartitionFingerprint = ConnectionStatePartition.Create(
            options,
            workerInstanceId,
            ownerGeneration,
            credentialFingerprint);
        ownerPartitionPrepared = true;
    }

    public async Task InitializeAsync(IJsonRpcConnection connection, WorkerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        if (!ownerPartitionPrepared)
        {
            BeginOwnerPartition(options, workerInstanceId, Interlocked.Increment(ref ownerGeneration), credentialFingerprint);
        }

        ownerPartitionPrepared = false;

        ConnectionContext? previous = Interlocked.Exchange(ref connectionContext, null);
        if (previous is not null)
        {
            previous.Detach();
            CancelPending(previous.Generation);
            pendingInteractions.RetireGeneration(previous.Generation);
            authorizationUrls.RetireGeneration(previous.Generation);
            await RetireStreamingBufferAsync().ConfigureAwait(false);
        }
        await ResetSkillsBackgroundRefreshAsync().ConfigureAwait(false);
        InvalidateSkillsCache();
        CodexVersion = null;
        EffectiveApprovalState = null;
        EffectiveReasoningEffort = null;
        EffectiveServiceTier = null;
        InitializationMetadata = null;
        CancelPending(null);
        this.options = options;
        LocalPath? parsedLocalRoot = LocalPath.TryCreate(options.LocalRoot, out LocalPath localPath)
            ? localPath
            : null;
        ServerPath? parsedServerRoot = ServerPath.TryCreate(options.ServerRoot, out ServerPath serverPath)
            ? serverPath
            : null;
        if (!string.IsNullOrWhiteSpace(options.RemoteEndpoint)
            && (parsedLocalRoot is null || parsedServerRoot is null))
        {
            throw new InvalidOperationException("Remote connection roots must be valid absolute local and server paths.");
        }

        remotePathMapper = parsedLocalRoot is not null && parsedServerRoot is not null
                ? new RemotePathMapper(parsedLocalRoot, parsedServerRoot)
                : null;
        long generation = Interlocked.Increment(ref connectionGeneration);
        var context = new ConnectionContext(
            this,
            connection,
            generation,
            StatePartitionFingerprint,
            OwnerGeneration,
            string.IsNullOrWhiteSpace(options.RemoteEndpoint));
        connectionContext = context;
        connection.NotificationReceived += context.NotificationHandler;
        connection.RequestReceived += context.RequestHandler;
        connection.Closed += context.ClosedHandler;
        lock (turnStateLock)
        {
            completedTurnIds.Clear();
            interruptRequestedAt.Clear();
            pendingTurnThreadId = null;
            ActiveThreadId = null;
            ActiveTurnId = null;
        }
        string overflowDirectory = Path.Combine(
            Path.GetTempPath(),
            "Kkamegawa.CodexForVisualStudio",
            Guid.NewGuid().ToString("N"));
        streamingBuffer = new StreamingBuffer(
            (value, token) => EmitForContextAsync(context, value, token),
            overflowDirectory);

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
                capabilities = CreateClientCapabilities(options),
            },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(context))
        {
            return;
        }
        await connection.SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(context))
        {
            return;
        }

        CodexVersion = ReadCodexVersion(initResponse);
        InitializationMetadata = new(
            GetString(initResponse, "codexHome"),
            GetString(initResponse, "platformFamily"),
            GetString(initResponse, "platformOs"),
            GetString(initResponse, "userAgent"));

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

        // Read the effective gateway policy after initialized on every connection. A malformed
        // or failed response leaves the connection recoverable but gates credential-dependent RPCs.
        await ReadGatewayOAuthCoreAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, object> CreateClientCapabilities(WorkerOptions options)
    {
        var capabilities = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["experimentalApi"] = options.ExperimentalApi,
        };
        if (string.IsNullOrWhiteSpace(options.RemoteEndpoint))
        {
            capabilities["explicitGatewayOauth"] = true;
        }

        return capabilities;
    }

    public async Task<InteractionAuthStatus> ReadGatewayOAuthAsync(CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireContext();
        await ReadGatewayOAuthCoreAsync(context, cancellationToken).ConfigureAwait(false);
        return CreateInteractionAuthStatus(context);
    }

    public async Task<InteractionAuthStatus> LoginGatewayOAuthAsync(CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        if (!context.IsLocal)
        {
            throw new InvalidOperationException("Gateway OAuth login is available only for a local app-server connection.");
        }

        if (!context.GatewayOAuthReadSucceeded || !context.GatewayOAuthRequired)
        {
            throw new InvalidOperationException("Gateway OAuth status must be read successfully before login.");
        }

        RevokeGatewayAuthorizationAction(context);
        context.GatewayOAuthReady = false;
        context.GatewayOAuthMessage = "Waiting for gateway authorization.";
        string attemptId = Guid.NewGuid().ToString("N");
        context.GatewayLoginAttemptId = attemptId;
        SetGatewayState(context, InteractionAuthState.LoginPending, context.GatewayOAuthMessage);
        context.GatewayLoginTask = CompleteGatewayOAuthLoginAsync(context, attemptId);
        await EmitInteractionAuthStatusAsync(context, cancellationToken).ConfigureAwait(false);
        return CreateInteractionAuthStatus(context);
    }

    private async Task CompleteGatewayOAuthLoginAsync(ConnectionContext context, string attemptId)
    {
        try
        {
            JsonElement result = await context.Connection.SendRequestAsync(
                "account/gatewayOAuth/login",
                new { },
                TimeSpan.FromMinutes(10),
                context.Lifetime.Token).ConfigureAwait(false);
            EnsureCurrent(context);
            if (result.ValueKind != JsonValueKind.Object)
            {
                throw new JsonRpcRemoteException(-32603, "Gateway login returned an invalid response.");
            }

            if (string.Equals(context.GatewayLoginAttemptId, attemptId, StringComparison.Ordinal)
                && context.GatewayOAuthState == InteractionAuthState.LoginPending)
            {
                context.GatewayOAuthMessage = "Gateway authorization completed. Refresh the status before continuing.";
                SetGatewayState(context, InteractionAuthState.Authenticated, context.GatewayOAuthMessage);
                context.GatewayOAuthReady = true;
                context.GatewayLoginAttemptId = null;
                await EmitInteractionAuthStatusAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (IsCurrent(context)
                && string.Equals(context.GatewayLoginAttemptId, attemptId, StringComparison.Ordinal)
                && context.GatewayOAuthState == InteractionAuthState.LoginPending)
            {
                context.GatewayOAuthReady = false;
                context.GatewayOAuthMessage = "Gateway authorization did not complete. Check the status and try again.";
                SetGatewayState(context, InteractionAuthState.Failed, context.GatewayOAuthMessage);
                context.GatewayLoginAttemptId = null;
                await EmitInteractionAuthStatusAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<InteractionAuthStatus> CancelGatewayOAuthAsync(CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        if (!context.IsLocal)
        {
            throw new InvalidOperationException("Gateway OAuth cancellation is available only for a local app-server connection.");
        }

        if (!context.GatewayOAuthReadSucceeded || !context.GatewayOAuthRequired)
        {
            throw new InvalidOperationException("Gateway OAuth status must be read successfully before cancellation.");
        }

        RevokeGatewayAuthorizationAction(context);
        context.GatewayLoginAttemptId = null;
        try
        {
            await context.Connection.SendRequestAsync(
                "account/gatewayOAuth/cancel",
                new { },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            context.GatewayOAuthReady = false;
            context.GatewayOAuthMessage = "Gateway authorization was canceled.";
            SetGatewayState(context, InteractionAuthState.Failed, context.GatewayOAuthMessage);
            await EmitInteractionAuthStatusAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            context.GatewayOAuthReady = false;
            context.GatewayOAuthMessage = "Gateway authorization could not be canceled. Read the status before continuing.";
            SetGatewayState(context, InteractionAuthState.Unavailable, context.GatewayOAuthMessage);
            await EmitInteractionAuthStatusAsync(context, CancellationToken.None).ConfigureAwait(false);
        }

        return CreateInteractionAuthStatus(context);
    }

    public Task OpenAuthorizationUrlAsync(OpenAuthorizationUrlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.AuthorizationActionOwners.TryRemove(request.ActionId, out _)
            || !authorizationUrls.TryTake(context.Generation, request.ActionId, out Uri? uri)
            || uri is null)
        {
            throw new InvalidOperationException("This authorization link is no longer available. Read the authentication status and try again.");
        }

        try
        {
            authorizationUrlOpener(uri);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The default browser could not be opened. You can retry from the current authorization status.");
        }

        return Task.CompletedTask;
    }

    private static void OpenWithDefaultBrowser(Uri uri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
    }

    public async Task<McpOAuthLoginStatus> StartMcpOAuthLoginAsync(StartMcpOAuthLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string serverName = request.ServerName.Trim();
        if (serverName.Length is 0 or > 256 || serverName.Any(char.IsControl))
        {
            throw new ArgumentException("The MCP server name is invalid.", nameof(request));
        }

        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        string? previousOperationId = context.McpOAuthOperationsByServer.GetValueOrDefault(serverName);
        if (previousOperationId is not null && context.McpOAuthOperations.TryRemove(previousOperationId, out McpOAuthOperation? previous))
        {
            RevokeAuthorizationAction(context, previous.ActionId);
        }

        var operation = new McpOAuthOperation(Guid.NewGuid().ToString("N"), serverName, request.ThreadId);
        context.McpOAuthOperations[operation.OperationId] = operation;
        context.McpOAuthOperationsByServer[serverName] = operation.OperationId;
        context.McpAuthStatuses[serverName] = new McpServerAuthStatus
        {
            ServerName = redactor.Redact(serverName) ?? string.Empty,
            State = InteractionAuthState.LoginPending,
            Message = "MCP server authorization is starting.",
        };

        JsonElement result;
        try
        {
            result = await SendAsync(
                "mcpServer/oauth/login",
                new { name = serverName, threadId = request.ThreadId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (context.McpOAuthOperations.TryGetValue(operation.OperationId, out McpOAuthOperation? active))
            {
                active.RequestCanceled = true;
            }

            throw;
        }
        catch (Exception)
        {
            if (IsCurrent(context) && context.McpOAuthOperations.TryGetValue(operation.OperationId, out McpOAuthOperation? active))
            {
                context.McpAuthStatuses[serverName] = new McpServerAuthStatus
                {
                    ServerName = redactor.Redact(serverName) ?? string.Empty,
                    State = InteractionAuthState.Failed,
                    Message = "MCP server authorization could not be started. Check the server status before retrying.",
                };
                RevokeAuthorizationAction(context, active.ActionId);
            }

            await EmitInteractionAuthStatusAsync(context, CancellationToken.None).ConfigureAwait(false);
            return GetMcpOAuthLoginStatus(context, operation);
        }

        EnsureCurrent(context);
        if (!context.McpOAuthOperations.TryGetValue(operation.OperationId, out McpOAuthOperation? current)
            || current.Completed || current.RequestCanceled)
        {
            return GetMcpOAuthLoginStatus(context, operation);
        }

        string? authorizationUrl = GetString(result, "authorizationUrl") ?? GetString(result, "authUrl");

        string message;
        string? originDisplay = null;
        string? actionId = null;
        if (authorizationUrls.TryStore(context.Generation, authorizationUrl, out ProtectedAuthorizationUrlInfo urlInfo))
        {
            actionId = urlInfo.ActionId;
            originDisplay = redactor.Redact(urlInfo.OriginDisplay);
            operation.ActionId = actionId;
            context.AuthorizationActionOwners[actionId] = operation.OperationId;
            message = "MCP server authorization is waiting for completion.";
        }
        else
        {
            message = "The MCP server did not provide a valid authorization link. Check the server status and retry.";
        }

        context.McpAuthStatuses.TryGetValue(serverName, out McpServerAuthStatus? previousStatus);
        var status = new McpServerAuthStatus
        {
            ServerName = redactor.Redact(serverName) ?? string.Empty,
            State = current.NotificationObserved && previousStatus is not null
                ? previousStatus.State
                : actionId is null ? InteractionAuthState.Failed : InteractionAuthState.LoginPending,
            Message = current.NotificationObserved && previousStatus is not null ? previousStatus.Message : message,
            OriginDisplay = originDisplay,
            OpenAuthorizationActionId = actionId,
        };
        if (context.McpOAuthOperations.TryGetValue(operation.OperationId, out current) && !current.Completed && !current.RequestCanceled)
        {
            context.McpAuthStatuses[serverName] = status;
        }
        else
        {
            RevokeAuthorizationAction(context, actionId);
            return GetMcpOAuthLoginStatus(context, operation);
        }

        await EmitInteractionAuthStatusAsync(context, cancellationToken).ConfigureAwait(false);
        return new McpOAuthLoginStatus
        {
            OperationId = operation.OperationId,
            ServerName = status.ServerName,
            State = status.State,
            Message = status.Message,
            OriginDisplay = status.OriginDisplay,
            OpenAuthorizationActionId = status.OpenAuthorizationActionId,
        };
    }

    private static McpOAuthLoginStatus GetMcpOAuthLoginStatus(ConnectionContext context, McpOAuthOperation operation)
    {
        if (context.McpAuthStatuses.TryGetValue(operation.ServerName, out McpServerAuthStatus? status))
        {
            return new McpOAuthLoginStatus
            {
                OperationId = operation.OperationId,
                ServerName = status.ServerName,
                State = status.State,
                Message = status.Message,
                OriginDisplay = status.OriginDisplay,
                OpenAuthorizationActionId = status.OpenAuthorizationActionId,
            };
        }

        return new McpOAuthLoginStatus
        {
            OperationId = operation.OperationId,
            ServerName = operation.ServerName,
            State = InteractionAuthState.Unavailable,
            Message = "MCP server authorization status is unavailable.",
        };
    }

    public async Task DismissMcpOAuthLoginAsync(DismissMcpOAuthLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        if (!context.McpOAuthOperations.TryGetValue(request.OperationId, out McpOAuthOperation? operation))
        {
            return;
        }

        operation.Dismissed = true;
        RevokeAuthorizationAction(context, operation.ActionId);
        operation.ActionId = null;
        string serverName = operation.ServerName;
        if (context.McpAuthStatuses.TryGetValue(serverName, out McpServerAuthStatus? status))
        {
            context.McpAuthStatuses[serverName] = new McpServerAuthStatus
            {
                ServerName = status.ServerName,
                State = status.State,
                Message = "The authorization wait was dismissed. Server authentication may still be in progress.",
            };
            await EmitInteractionAuthStatusAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadGatewayOAuthCoreAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        EnsureCurrent(context);
        RevokeGatewayAuthorizationAction(context);
        context.GatewayOAuthReadSucceeded = false;
        context.GatewayOAuthReady = false;
        context.GatewayOAuthRequired = true;
        context.GatewayOAuthProviderId = null;
        context.GatewayOAuthProviderName = null;
        context.GatewayOAuthMessage = "Gateway authorization status is unavailable. Retry the status check before continuing.";
        SetGatewayState(context, InteractionAuthState.Unavailable, context.GatewayOAuthMessage);

        try
        {
            JsonElement result = await context.Connection.SendRequestAsync(
                "account/gatewayOAuth/read",
                new { },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            if (!TryReadGatewayOAuthResponse(result, out string? providerId, out string? providerName, out bool required, out string? wireStatus, out string? safeError))
            {
                WorkerDiagnostics.Write("gateway OAuth status response rejected");
                context.GatewayOAuthMessage = "Gateway authorization status is invalid. Retry the status check before continuing.";
                SetGatewayState(context, InteractionAuthState.Unavailable, context.GatewayOAuthMessage);
                await EmitInteractionAuthStatusAsync(context, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            context.GatewayOAuthReadSucceeded = true;
            context.GatewayOAuthProviderId = redactor.Redact(providerId);
            context.GatewayOAuthProviderName = redactor.Redact(providerName);
            context.GatewayOAuthRequired = required;
            context.GatewayOAuthMessage = safeError is null ? null : "Gateway authorization needs attention.";
            context.GatewayOAuthReady = safeError is null
                && (!required || string.Equals(wireStatus, "succeeded", StringComparison.Ordinal));
            SetGatewayState(context, MapGatewayOAuthState(required, wireStatus, safeError), context.GatewayOAuthMessage);
            WorkerDiagnostics.Write($"gateway OAuth status read completed required={required} state={wireStatus ?? "none"}");
            await EmitInteractionAuthStatusAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (IsCurrent(context))
            {
                WorkerDiagnostics.Write("gateway OAuth status read failed");
                context.GatewayOAuthMessage = "Gateway authorization status is unavailable. Retry the status check before continuing.";
                SetGatewayState(context, InteractionAuthState.Unavailable, context.GatewayOAuthMessage);
                await EmitInteractionAuthStatusAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static bool TryReadGatewayOAuthResponse(
        JsonElement result,
        out string? providerId,
        out string? providerName,
        out bool required,
        out string? status,
        out string? safeError)
    {
        providerId = GetBoundedString(result, "providerId", 256);
        providerName = GetBoundedString(result, "providerName", 256);
        required = false;
        status = null;
        safeError = null;
        if (result.ValueKind != JsonValueKind.Object
            || providerId is null
            || providerName is null
            || !HasBoolean(result, "required"))
        {
            return false;
        }

        required = result.GetProperty("required").GetBoolean();
        if (result.TryGetProperty("error", out JsonElement error))
        {
            if (error.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                return false;
            }

            if (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
            {
                safeError = "error";
            }
        }

        if (!result.TryGetProperty("status", out JsonElement statusElement)
            || statusElement.ValueKind == JsonValueKind.Null)
        {
            return !required;
        }

        if (statusElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        status = statusElement.GetString();
        return required && status is "notReady" or "started" or "succeeded" or "failed";
    }

    private static InteractionAuthState MapGatewayOAuthState(bool required, string? status, string? error)
    {
        if (error is not null)
        {
            return InteractionAuthState.Failed;
        }

        if (!required)
        {
            return InteractionAuthState.Ready;
        }

        if (status == "failed")
        {
            return InteractionAuthState.Failed;
        }

        return status switch
        {
            "started" => InteractionAuthState.LoginPending,
            "succeeded" => InteractionAuthState.Authenticated,
            _ => InteractionAuthState.ReauthenticationRequired,
        };
    }

    private static void SetGatewayState(ConnectionContext context, InteractionAuthState state, string? message)
    {
        lock (context.InteractionAuthLock)
        {
            context.GatewayOAuthState = state;
            context.GatewayOAuthMessage = message;
        }
    }

    private void RevokeGatewayAuthorizationAction(ConnectionContext context)
    {
        string? actionId = context.GatewayOAuthActionId;
        context.GatewayOAuthActionId = null;
        RevokeAuthorizationAction(context, actionId);
    }

    private void RevokeAuthorizationAction(ConnectionContext context, string? actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return;
        }

        context.AuthorizationActionOwners.TryRemove(actionId, out _);
        authorizationUrls.Remove(context.Generation, actionId);
    }

    private void RevokeAuthorizationActionsForOwner(ConnectionContext context, string ownerId)
    {
        foreach ((string actionId, string owner) in context.AuthorizationActionOwners)
        {
            if (string.Equals(owner, ownerId, StringComparison.Ordinal))
            {
                RevokeAuthorizationAction(context, actionId);
            }
        }
    }

    private static InteractionAuthStatus CreateInteractionAuthStatus(ConnectionContext context)
    {
        lock (context.InteractionAuthLock)
        {
            return new InteractionAuthStatus
            {
                IsLocal = context.IsLocal,
                IsSupported = context.GatewayOAuthReadSucceeded,
                State = context.GatewayOAuthState,
                Message = context.GatewayOAuthMessage,
                OriginDisplay = context.GatewayOAuthOriginDisplay,
                OpenAuthorizationActionId = context.GatewayOAuthActionId,
                McpServers = context.McpAuthStatuses.Values
                    .OrderBy(item => item.ServerName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
        }
    }

    private async Task EmitInteractionAuthStatusAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        if (IsCurrent(context) && InteractionAuthStatusChanged is not null)
        {
            await InteractionAuthStatusChanged(CreateInteractionAuthStatus(context), cancellationToken).ConfigureAwait(false);
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
        JsonElement result = await SendAsync("thread/start", new { cwd = MapLocalPathForServer(options.WorkingDirectory) }, cancellationToken).ConfigureAwait(false);
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
            ConnectionContext context = RequireContext();
            JsonElement result = await context.Connection.SendReadOnlyRequestAsync(
                "account/read",
                new { refreshToken = false },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            AccountStatus status = ReadAccountStatus(result);
            WorkerDiagnostics.Write($"account status read completed state={status.State} plan={status.PlanType ?? "none"}");

            // The owner's first read records its account fingerprint. A later read that returns a
            // different account is an owner boundary: the new account's status is never published
            // under the old owner.
            string fingerprint = ComputeAccountFingerprint(result);
            string? recorded = context.AccountFingerprint;
            if (recorded is null)
            {
                context.AccountFingerprint = fingerprint;
            }
            else if (!string.Equals(recorded, fingerprint, StringComparison.Ordinal))
            {
                WorkerDiagnostics.Write("account identity changed; retiring the owner");
                bool ownLogout = context.LogoutRequested;
                invalidatedAccountState = ownLogout ? AccountState.SignedOut : AccountState.Unavailable;
                invalidatedByOwnerAction = ownLogout;
                await InvalidateOwnerPartitionAsync(context, cancellationToken).ConfigureAwait(false);
                var changed = new AccountStatus
                {
                    State = AccountState.Unavailable,
                    Message = "The account changed. Reconnect to confirm the active account.",
                };
                await EmitAccountStatusAsync(changed, CancellationToken.None).ConfigureAwait(false);
                return changed;
            }

            await EmitAccountStatusAsync(status, cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch (JsonRpcConnectionClosedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A request timeout also surfaces as OperationCanceledException; only the caller's own
            // cancellation may leave the status at Checking.
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
            ConnectionContext context = RequireContext();
            JsonElement result = await context.Connection.SendRequestAsync(
                "account/login/start",
                new { type = "chatgpt" },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
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

            context.PendingLoginId = loginId;
            WorkerDiagnostics.Write("app-server login response accepted");
            return new StartAccountLoginResult
            {
                Status = signingIn,
                LoginId = loginId,
                AuthUrl = authUrl,
            };
        }
        catch (JsonRpcConnectionClosedException)
        {
            throw;
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
        ConnectionContext? logoutContext = null;
        try
        {
            ConnectionContext context = RequireContext();
            logoutContext = context;

            // An account notification for this logout can be processed before the response. Marking
            // the request first lets that path retire the owner as the owner's own logout.
            context.LogoutRequested = true;
            await context.Connection.SendRequestAsync(
                "account/logout",
                new { },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(context) && context.OwnerInvalidated && invalidatedByOwnerAction)
            {
                WorkerDiagnostics.Write("app-server logout request completed after its notification");
                return new AccountStatus { State = AccountState.SignedOut };
            }

            EnsureCurrent(context);
            invalidatedAccountState = AccountState.SignedOut;
            invalidatedByOwnerAction = true;
            await InvalidateOwnerPartitionAsync(context, cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write("app-server logout request completed");
            var signedOut = new AccountStatus { State = AccountState.SignedOut };
            await EmitAccountStatusAsync(signedOut, CancellationToken.None).ConfigureAwait(false);
            return signedOut;
        }
        catch (JsonRpcConnectionClosedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logoutContext is not null)
            {
                logoutContext.LogoutRequested = false;
            }

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
        JsonElement result = await SendAsync("thread/resume", new { threadId, excludeTurns = true }, cancellationToken).ConfigureAwait(false);
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
        JsonElement result = await SendReadOnlyAsync(
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

    public async Task<ThreadSummary> ReadThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        ValidateHistoryId(threadId, nameof(threadId));
        JsonElement result = await SendReadOnlyAsync(
            "thread/read",
            new { threadId, includeTurns = false },
            cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("thread", out JsonElement thread) || thread.ValueKind != JsonValueKind.Object)
        {
            throw InvalidHistoryResponse();
        }

        ThreadSummary summary = ReadThread(thread);
        if (!string.Equals(summary.Id, threadId, StringComparison.Ordinal))
        {
            throw InvalidHistoryResponse();
        }

        return summary;
    }

    public async Task<ThreadTurnsPage> ListThreadTurnsAsync(
        string threadId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateHistoryId(threadId, nameof(threadId));
        int boundedLimit = Math.Clamp(limit, 1, MaxHistoryPageTurns);
        JsonElement result = await SendReadOnlyAsync(
            "thread/turns/list",
            new { threadId, cursor, limit = boundedLimit, itemsView = "summary", sortDirection = "desc" },
            cancellationToken).ConfigureAwait(false);
        JsonElement data = RequireArray(result, "data");
        var turns = new List<ThreadTurnSummary>(Math.Min(data.GetArrayLength(), boundedLimit));
        foreach (JsonElement turn in data.EnumerateArray())
        {
            string? id = GetBoundedString(turn, "id", MaxAttachmentIdentityBytes);
            string? status = GetBoundedString(turn, "status", 64);
            if (id is null || status is null)
            {
                continue;
            }

            turns.Add(new ThreadTurnSummary
            {
                Id = id,
                Status = status,
                StartedAt = GetInt64(turn, "startedAt"),
                CompletedAt = GetInt64(turn, "completedAt"),
            });
            if (turns.Count == boundedLimit)
            {
                break;
            }
        }

        return new ThreadTurnsPage
        {
            Turns = turns,
            NextCursor = ReadOptionalCursor(result),
        };
    }

    public async Task<ThreadItemsPage> ListThreadItemsAsync(
        string threadId,
        string? turnId,
        ThreadItemCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateHistoryId(threadId, nameof(threadId));
        int boundedLimit = Math.Clamp(limit, 1, MaxHistoryPageItems);
        object? wireCursor = cursor?.Kind switch
        {
            null => null,
            ThreadItemCursorKind.Opaque => ValidateCursor(cursor.Value),
            ThreadItemCursorKind.ExclusiveItem => CreateItemAnchor(cursor, turnId),
            _ => throw new ArgumentOutOfRangeException(nameof(cursor)),
        };
        if (cursor?.Kind == ThreadItemCursorKind.ExclusiveItem
            && (string.IsNullOrWhiteSpace(turnId) || !string.Equals(cursor.TurnId, turnId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("An item anchor requires the matching turnId.", nameof(cursor));
        }

        JsonElement result = await SendReadOnlyAsync(
            "thread/items/list",
            new { threadId, turnId, cursor = wireCursor, limit = boundedLimit, sortDirection = "desc" },
            cancellationToken).ConfigureAwait(false);
        JsonElement data = RequireArray(result, "data");
        var items = new List<ThreadHistoryItem>(Math.Min(data.GetArrayLength(), boundedLimit));
        foreach (JsonElement entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("item", out JsonElement item)
                || item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = GetBoundedString(item, "id", MaxAttachmentIdentityBytes);
            string? itemType = GetBoundedString(item, "type", 64);
            string? itemTurnId = GetBoundedString(entry, "turnId", MaxAttachmentIdentityBytes);
            if (id is null || itemType is null || itemTurnId is null)
            {
                continue;
            }

            // A per-turn page must only contain items for the requested turn. Mismatched
            // entries are untrusted server data and must not leak into another turn.
            if (turnId is not null && !string.Equals(itemTurnId, turnId, StringComparison.Ordinal))
            {
                continue;
            }

            items.Add(new ThreadHistoryItem
            {
                Id = id,
                TurnId = itemTurnId,
                Type = itemType,
                Text = ReadHistoryText(item, itemType),
                StartedAtMs = GetInt64(entry, "startedAtMs"),
                CompletedAtMs = GetInt64(entry, "completedAtMs"),
            });
            if (items.Count == boundedLimit)
            {
                break;
            }
        }

        return new ThreadItemsPage
        {
            Items = items,
            NextCursor = ReadOptionalCursor(result),
        };
    }

    public async Task<ThreadAttachmentsPage> ListThreadAttachmentsAsync(
        string threadId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateHistoryId(threadId, nameof(threadId));
        int boundedLimit = Math.Clamp(limit, 1, MaxAttachmentPageSize);
        JsonElement result = await SendReadOnlyAsync(
            "thread/attachment/list",
            new { threadId, cursor, limit = boundedLimit },
            cancellationToken).ConfigureAwait(false);
        JsonElement data = RequireArray(result, "data");
        var attachments = new List<ThreadAttachmentMetadata>(Math.Min(data.GetArrayLength(), boundedLimit));
        int rejectedEntryCount = 0;
        foreach (JsonElement attachment in data.EnumerateArray())
        {
            if (attachment.ValueKind != JsonValueKind.Object)
            {
                rejectedEntryCount++;
                continue;
            }

            string? id = GetBoundedString(attachment, "id", MaxAttachmentIdentityBytes);
            string? attachmentType = GetBoundedString(attachment, "attachmentType", MaxAttachmentIdentityBytes);
            string? identityKey = GetBoundedString(attachment, "identityKey", MaxAttachmentIdentityBytes);
            long? createdAt = GetInt64(attachment, "createdAt");
            if (id is null || attachmentType is null || identityKey is null || createdAt is null
                || !attachment.TryGetProperty("payload", out JsonElement payload))
            {
                rejectedEntryCount++;
                continue;
            }

            int payloadBytes = Encoding.UTF8.GetByteCount(payload.GetRawText());
            attachments.Add(new ThreadAttachmentMetadata
            {
                Id = id,
                AttachmentType = attachmentType,
                IdentityKey = identityKey,
                CreatedAt = createdAt.Value,
                UnavailableReason = payloadBytes > MaxAttachmentPayloadBytes
                    ? "Attachment payload exceeds the metadata validation limit."
                    : "Attachment payload is not interpreted by the metadata-only recovery API.",
            });
            if (attachments.Count == boundedLimit || attachments.Count == MaxAttachmentsPerThread)
            {
                break;
            }
        }

        return new ThreadAttachmentsPage
        {
            Attachments = attachments,
            NextCursor = ReadOptionalCursor(result),
            RejectedEntryCount = rejectedEntryCount,
        };
    }

    public async Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        WorkerDiagnostics.Write("app-server model list request starting");
        try
        {
            ConnectionContext context = RequireContext();
            EnsureCredentialReady(context);
            JsonElement result = await context.Connection.SendReadOnlyRequestAsync(
                "model/list",
                // Include hidden models so the catalog default (which may be a hidden preset and
                // is otherwise filtered out server-side) can still be surfaced in the picker.
                new { includeHidden = true },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            ListModelsResult models = ReadModelsResult(result);
            WorkerDiagnostics.Write($"app-server model list request completed count={models.Models.Count}");
            return models;
        }
        catch (OperationCanceledException ex)
        {
            WorkerDiagnostics.Write("app-server model list request canceled", ex);
            throw;
        }
        catch (JsonRpcConnectionClosedException)
        {
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
                new { cwd = MapLocalPathForServer(options.WorkingDirectory), cursor, limit = PermissionProfilePageSize },
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
        ConnectionContext context = RequireContext();
        EnsureCredentialReady(context);
        ValidateTurnApprovalOverrides(request);
        if (request.Skill is not null)
        {
            await ValidateSkillInvocationAsync(request.Skill, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
        }

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

        lock (turnStateLock)
        {
            pendingTurnThreadId = request.ThreadId;
        }

        string? startedTurnId;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            JsonElement result = await context.Connection.SendRequestAsync(
                "turn/start",
                parameters,
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(context))
            {
                throw new JsonRpcConnectionClosedException("The app-server connection generation changed while starting the turn.");
            }

            startedTurnId = result.GetProperty("turn").GetProperty("id").GetString();
            lock (turnStateLock)
            {
                if (!IsCurrent(context))
                {
                    throw new JsonRpcConnectionClosedException("The app-server connection generation changed while starting the turn.");
                }

                ActiveThreadId = request.ThreadId;
                // A completion notification may legally race the response to turn/start.
                // Never resurrect a turn that the server has already completed.
                if (startedTurnId is not null && !completedTurnIds.Contains(new TurnKey(context.Generation, request.ThreadId, startedTurnId)))
                {
                    ActiveTurnId = startedTurnId;
                }
                else
                {
                    ActiveTurnId = null;
                }
            }
        }
        catch (Exception ex)
        {
            throw new TurnStartOutcomeUnknownException(ex);
        }
        finally
        {
            lock (turnStateLock)
            {
                if (string.Equals(pendingTurnThreadId, request.ThreadId, StringComparison.Ordinal))
                {
                    pendingTurnThreadId = null;
                }
            }
        }

        if (request.HasEffort)
        {
            EffectiveReasoningEffort = request.Effort;
        }

        if (request.HasServiceTier)
        {
            EffectiveServiceTier = request.ServiceTier;
        }

        return startedTurnId ?? string.Empty;
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
        ConnectionContext context = RequireContext();
        EnsureCredentialReady(context);

        // Record when the user asked to stop, so the diagnostics log shows how long the server took
        // to acknowledge the request and to actually end the turn.
        long requestedAt = timeProvider.GetTimestamp();
        var key = new TurnKey(context.Generation, request.ThreadId, request.TurnId);
        lock (turnStateLock)
        {
            interruptRequestedAt[key] = requestedAt;
        }

        WorkerDiagnostics.Write($"turn/interrupt requested thread={request.ThreadId} turn={request.TurnId}");
        try
        {
            await context.Connection.SendRequestAsync(
                "turn/interrupt",
                new { threadId = request.ThreadId, turnId = request.TurnId },
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write(
                $"turn/interrupt failed turn={request.TurnId} elapsedMs={ElapsedMilliseconds(requestedAt)}",
                ex);
            throw;
        }

        WorkerDiagnostics.Write(
            $"turn/interrupt acknowledged turn={request.TurnId} elapsedMs={ElapsedMilliseconds(requestedAt)}");
        EnsureCurrent(context);
    }

    private long ElapsedMilliseconds(long startTimestamp)
        => (long)timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;

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
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!forceReload && skillsSnapshot is not null && now < skillsSnapshotExpiresAt)
        {
            return CloneSkillsResult(skillsSnapshot);
        }

        bool startBackgroundRefresh = false;
        long backgroundGeneration = 0;
        ListSkillsResult result;
        await skillsCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = timeProvider.GetUtcNow();
            if (!forceReload && skillsSnapshot is not null && now < skillsSnapshotExpiresAt)
            {
                result = CloneSkillsResult(skillsSnapshot);
            }
            else if (CanPersistOwnerState
                && !string.IsNullOrWhiteSpace(StatePartitionFingerprint)
                && !forceReload
                && await skillCatalogStore.TryReadAsync(
                    options.WorkingDirectory,
                    StatePartitionFingerprint,
                    GetSkillsCacheIdentity(),
                    now,
                    cancellationToken).ConfigureAwait(false) is { } persisted)
            {
                backgroundGeneration = Volatile.Read(ref skillsGeneration);
                persisted.Generation = backgroundGeneration;
                persisted.IsStale = true;
                skillsSnapshot = CloneSkillsResult(persisted);
                skillsSnapshotExpiresAt = now.AddSeconds(60);
                result = CloneSkillsResult(persisted);
                startBackgroundRefresh = true;
            }
            else
            {
                result = await LoadLiveSkillsAsync(forceReload, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            skillsCacheGate.Release();
        }

        if (startBackgroundRefresh)
        {
            StartSkillsBackgroundRefresh(backgroundGeneration);
        }

        return result;
    }

    private async Task<ListSkillsResult> LoadLiveSkillsAsync(bool forceReload, CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            long generation = Volatile.Read(ref skillsGeneration);
            ListSkillsResult loaded = await ReadSkillsFromServerAsync(
                forceReload || attempt > 0,
                cancellationToken).ConfigureAwait(false);
            loaded = CloneSkillsResult(loaded, isStale: false, generation);
            if (generation != Volatile.Read(ref skillsGeneration))
            {
                continue;
            }

            if (loaded.IsSupported && CanPersistOwnerState && !string.IsNullOrWhiteSpace(StatePartitionFingerprint))
            {
                try
                {
                    await skillCatalogStore.WriteAsync(
                        options.WorkingDirectory,
                        StatePartitionFingerprint,
                        GetSkillsCacheIdentity(),
                        loaded,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    WorkerDiagnostics.Write("skill catalog cache write failed", ex);
                }
            }

            if (generation != Volatile.Read(ref skillsGeneration))
            {
                await DeletePersistedSkillsAsync(CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            skillsSnapshot = CloneSkillsResult(loaded);
            skillsSnapshotExpiresAt = timeProvider.GetUtcNow().AddSeconds(60);
            return CloneSkillsResult(loaded);
        }

        throw new InvalidOperationException("The skill catalog changed repeatedly while it was loading.");
    }

    private void StartSkillsBackgroundRefresh(long expectedGeneration)
    {
        lock (skillsBackgroundRefreshLock)
        {
            if (skillsBackgroundRefreshTask is { IsCompleted: false })
            {
                return;
            }

            skillsBackgroundRefreshTask = RefreshSkillsInBackgroundAsync(
                expectedGeneration,
                skillsBackgroundRefreshCancellation.Token);
        }
    }

    private async Task RefreshSkillsInBackgroundAsync(long expectedGeneration, CancellationToken cancellationToken)
    {
        try
        {
            // Do not let an in-memory/test connection that completes synchronously turn the
            // display-only cache path back into a blocking live scan.
            await Task.Yield();
            await skillsCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ListSkillsResult loaded;
            try
            {
                if (expectedGeneration != Volatile.Read(ref skillsGeneration))
                {
                    return;
                }

                loaded = await LoadLiveSkillsAsync(forceReload: false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                skillsCacheGate.Release();
            }

            if (SkillsChanged is not null)
            {
                await SkillsChanged(
                    new SkillsChangedEvent { Generation = loaded.Generation },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("skill catalog background refresh failed", ex);
        }
    }

    private async Task<ListSkillsResult> ReadSkillsFromServerAsync(bool forceReload, CancellationToken cancellationToken)
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
        ConnectionContext? context = Volatile.Read(ref connectionContext);
        ConnectionContext? previous = emittingContext.Value;
        emittingContext.Value = context;
        try
        {
            await ResolveApprovalCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            emittingContext.Value = previous;
        }
    }

    private async Task ResolveApprovalCoreAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref connectionContext) is not { } context
            || !pendingInteractions.TryGetByInteractionId(context.Generation, request.RequestId, out PendingInteractionEntry entry)
            || entry.Method is not ("item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
            || !context.CommandChoices.TryGetValue(entry.InteractionId, out OfferedCommandChoiceSet? choices))
        {
            return;
        }

        JsonElement response;
        if (!string.IsNullOrWhiteSpace(request.ChoiceId))
        {
            if (!choices.TryResolve(request.ChoiceId, out response))
            {
                throw new ArgumentException("The selected command-approval option is invalid.", nameof(request));
            }
        }
        else
        {
            if (request.Decision is not (ApprovalDecision.Accept or ApprovalDecision.AcceptForSession or ApprovalDecision.Decline or ApprovalDecision.Cancel))
            {
                throw new ArgumentException("A scoped approval requires an exact offered ChoiceId.", nameof(request));
            }

            if (!choices.TryResolveDecision(ToWireDecision(request.Decision), out response))
            {
                throw new ArgumentException("The selected command-approval decision was not offered by the server.", nameof(request));
            }
        }

        if (!pendingInteractions.TryComplete(entry.Key, entry, response))
        {
            return;
        }

        if (TryReadApprovalDecision(response, out string? wireDecision))
        {
            if (wireDecision == "acceptForSession" && CanReuseSessionApproval(entry.Parameters))
            {
                ApprovalRequest approval = CreateApprovalRequest(entry.InteractionId, entry.Method, entry.Parameters);
                ApprovalScope grantScope = ApprovalScope.Session;
                context.ApprovalGrants.Add(approval, grantScope);
                await EmitApprovalAuditAsync(approval, ApprovalAuditAction.GrantCreated, grantScope, cancellationToken).ConfigureAwait(false);
            }
        }

        context.CommandChoices.TryRemove(entry.InteractionId, out _);
    }

    public async Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
    {
        ConnectionContext? context = Volatile.Read(ref connectionContext);
        ConnectionContext? previous = emittingContext.Value;
        emittingContext.Value = context;
        try
        {
            await ResolveUserInputCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            emittingContext.Value = previous;
        }
    }

    private async Task ResolveUserInputCoreAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref connectionContext) is not { } context
            || !pendingInteractions.TryGetByInteractionId(context.Generation, request.RequestId, out PendingInteractionEntry entry)
            || entry.Method != "item/tool/requestUserInput")
        {
            return;
        }

        if (!context.UserInputProjections.TryGetValue(entry.InteractionId, out UserInputProjection? projection))
        {
            return;
        }
        UserInputRequest projected = projection.Request;
        JsonElement response;
        if (request.Action == UserInputAction.Cancel)
        {
            if (request.Answers.Count != 0)
            {
                throw new ArgumentException("A canceled user-input request cannot contain answers.", nameof(request));
            }

            response = UserInputResponse(new Dictionary<string, UserInputAnswer>(StringComparer.Ordinal));
        }
        else if (request.Action == UserInputAction.Submit)
        {
            response = ValidateAnswers(projection, request.Answers);
        }
        else
        {
            throw new ArgumentException("The user-input action is invalid.", nameof(request));
        }
        if (!pendingInteractions.TryComplete(entry.Key, entry, response))
        {
            return;
        }

        context.UserInputProjections.TryRemove(entry.InteractionId, out _);
    }

    public async Task ResolvePermissionSelectionAsync(ResolvePermissionSelectionRequest request, CancellationToken cancellationToken)
    {
        ConnectionContext? context = Volatile.Read(ref connectionContext);
        if (context is null
            || !pendingInteractions.TryGetByInteractionId(context.Generation, request.RequestId, out PendingInteractionEntry entry)
            || entry.Method != "item/permissions/requestApproval"
            || !context.PermissionSelections.TryGetValue(entry.InteractionId, out PermissionSelectionSet? selection)
            || !selection.TryBuild(request.SelectedPermissionIds, request.Scope, out JsonElement response, out string safeError))
        {
            throw new ArgumentException("The permission selection is invalid or no longer active.", nameof(request));
        }

        pendingInteractions.TryComplete(entry.Key, entry, response);
    }

    public async Task ResolveMcpElicitationAsync(ResolveMcpElicitationRequest request, CancellationToken cancellationToken)
    {
        ConnectionContext? context = Volatile.Read(ref connectionContext);
        if (context is null
            || !pendingInteractions.TryGetByInteractionId(context.Generation, request.RequestId, out PendingInteractionEntry entry)
            || entry.Method != "mcpServer/elicitation/request"
            || !context.McpElicitations.TryGetValue(entry.InteractionId, out McpElicitationForm? form))
        {
            return;
        }

        if (request.Values.Count != 0 && (form.Request.Kind == McpElicitationKind.Url
            || request.Action is McpElicitationAction.Decline or McpElicitationAction.Cancel))
        {
            throw new ArgumentException("Values are not allowed for this MCP elicitation response.", nameof(request));
        }

        JsonElement response;
        switch (request.Action)
        {
            case McpElicitationAction.Accept when form.Request.Kind == McpElicitationKind.Form:
                if (!form.TryValidateValues(request.Values, out JsonElement content, out string safeError))
                {
                    throw new ArgumentException(safeError, nameof(request));
                }

                response = JsonSerializer.SerializeToElement(new { action = "accept", content });
                break;
            case McpElicitationAction.Accept when form.Request.Kind == McpElicitationKind.Url:
                response = JsonSerializer.SerializeToElement(new { action = "accept" });
                break;
            case McpElicitationAction.Decline:
                response = JsonSerializer.SerializeToElement(new { action = "decline" });
                break;
            case McpElicitationAction.Cancel:
                response = JsonSerializer.SerializeToElement(new { action = "cancel" });
                break;
            default:
                throw new ArgumentException("The MCP elicitation response is invalid.", nameof(request));
        }

        if (pendingInteractions.TryComplete(entry.Key, entry, response))
        {
            RevokeAuthorizationActionsForOwner(context, entry.InteractionId);
        }
    }

    private async Task<JsonElement> HandleUserInputRequestAsync(ConnectionContext context, JsonElement originalRequestId, string requestId, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.GetProperty("questions").EnumerateArray().Any(question => GetBoolean(question, "isSecret") == true))
        {
            await EmitUnsupportedInteractionAsync(new UnsupportedInteractionNotice
            {
                Kind = UnsupportedInteractionKind.SecretInput,
                Message = "This request asks for secret input, which this extension cannot collect safely.",
                ThreadId = GetString(parameters, "threadId"),
                TurnId = GetString(parameters, "turnId"),
            }, cancellationToken).ConfigureAwait(false);
            return UserInputResponse(new Dictionary<string, UserInputAnswer>(StringComparer.Ordinal));
        }

        var key = new PendingInteractionKey(context.Generation, requestId);
        JsonElement timeoutResponse = UserInputResponse(new Dictionary<string, UserInputAnswer>(StringComparer.Ordinal));
        if (!pendingInteractions.TryAdd(key, "item/tool/requestUserInput", originalRequestId, parameters,
                TimeSpan.FromMinutes(5), timeoutResponse, out PendingInteractionEntry entry))
        {
            throw new JsonRpcRemoteException(-32600, "The user-input request is no longer active.");
        }

        if (entry.Completion.IsCompleted)
        {
            PendingInteractionResult earlyResult = await entry.Completion.ConfigureAwait(false);
            if (earlyResult.Kind is PendingInteractionCompletionKind.ExternallyResolved or PendingInteractionCompletionKind.GenerationRetired)
            {
                throw new JsonRpcRequestResolvedException();
            }

            return earlyResult.Response;
        }

        UserInputRequest request = CreateUserInputRequest(entry.InteractionId, parameters, out Dictionary<string, Dictionary<string, string>> optionLabels);
        context.UserInputProjections[entry.InteractionId] = new UserInputProjection(request, optionLabels);
        if (UserInputRequested is not null)
        {
            await UserInputRequested(request, cancellationToken).ConfigureAwait(false);
        }

        PendingInteractionResult result = await entry.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        context.UserInputProjections.TryRemove(entry.InteractionId, out _);
        RevokeAuthorizationActionsForOwner(context, entry.InteractionId);
        if (ShouldNotifyPendingResolution(context))
        {
            await EmitUserInputResolvedAsync(entry.InteractionId, CancellationToken.None).ConfigureAwait(false);
        }

        if (result.Kind is PendingInteractionCompletionKind.ExternallyResolved or PendingInteractionCompletionKind.GenerationRetired)
        {
            throw new JsonRpcRequestResolvedException();
        }

        return result.Response;
    }

    private async Task<JsonElement> HandlePermissionSelectionRequestAsync(
        ConnectionContext context,
        JsonElement originalRequestId,
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        ValidateApprovalParameters("item/permissions/requestApproval", parameters);
        ApprovalRequest policyRequest = CreateApprovalRequest(requestId, "item/permissions/requestApproval", parameters);
        if (policyRequest.IsPolicyBlocked
            || !parameters.TryGetProperty("permissions", out JsonElement requestedPermissions)
            || !PermissionSelectionBuilder.TryCreate(requestedPermissions, value => redactor.Redact(value) ?? string.Empty,
                static () => Guid.NewGuid().ToString("N"), out PermissionSelectionSet? selection)
            || selection is null)
        {
            return JsonSerializer.SerializeToElement(new { permissions = new { }, scope = "turn" });
        }

        var key = new PendingInteractionKey(context.Generation, requestId);
        JsonElement timeoutResponse = JsonSerializer.SerializeToElement(new { permissions = new { }, scope = "turn" });
        if (!pendingInteractions.TryAdd(key, "item/permissions/requestApproval", originalRequestId, parameters,
                TimeSpan.FromMinutes(5), timeoutResponse, out PendingInteractionEntry entry))
        {
            return timeoutResponse;
        }

        if (entry.Completion.IsCompleted)
        {
            PendingInteractionResult earlyResult = await entry.Completion.ConfigureAwait(false);
            if (earlyResult.Kind is PendingInteractionCompletionKind.ExternallyResolved or PendingInteractionCompletionKind.GenerationRetired)
            {
                throw new JsonRpcRequestResolvedException();
            }

            return earlyResult.Response;
        }

        context.PermissionSelections[entry.InteractionId] = selection;
        var request = new PermissionRequest
        {
            RequestId = entry.InteractionId,
            ThreadId = GetString(parameters, "threadId") ?? string.Empty,
            TurnId = GetString(parameters, "turnId") ?? string.Empty,
            ItemId = GetString(parameters, "itemId"),
            Reason = redactor.Redact(GetString(parameters, "reason")),
            RequestedPermissions = selection.RequestedPermissions,
        };
        if (PermissionRequested is not null)
        {
            await PermissionRequested(request, cancellationToken).ConfigureAwait(false);
        }

        PendingInteractionResult result = await entry.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        context.PermissionSelections.TryRemove(entry.InteractionId, out _);
        if (ShouldNotifyPendingResolution(context))
        {
            await EmitPermissionResolvedAsync(entry.InteractionId, CancellationToken.None).ConfigureAwait(false);
        }

        if (result.Kind is PendingInteractionCompletionKind.ExternallyResolved or PendingInteractionCompletionKind.GenerationRetired)
        {
            throw new JsonRpcRequestResolvedException();
        }

        return result.Response;
    }

    private async Task<JsonElement> HandleMcpElicitationRequestAsync(
        ConnectionContext context,
        JsonElement originalRequestId,
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        ProtectedAuthorizationUrlInfo? protectedUrlInfo = null;
        if (!McpElicitationFormParser.TryParse(parameters, requestId, static () => Guid.NewGuid().ToString("N"),
                url => authorizationUrls.TryStore(context.Generation, url, out ProtectedAuthorizationUrlInfo info)
                    ? protectedUrlInfo = info
                    : null,
                out McpElicitationForm? form, out McpElicitationParseRefusal refusal)
            || form is null)
        {
            if (refusal.Status == McpElicitationParseStatus.Invalid)
            {
                throw new JsonRpcRemoteException(-32602, "The MCP elicitation request is invalid.");
            }

            await EmitUnsupportedInteractionAsync(new UnsupportedInteractionNotice
            {
                Kind = refusal.UnsupportedKind ?? UnsupportedInteractionKind.McpElicitationSchema,
                Message = refusal.SafeReason,
                ThreadId = GetString(parameters, "threadId"),
                TurnId = GetString(parameters, "turnId"),
            }, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new { action = "cancel" });
        }

        var key = new PendingInteractionKey(context.Generation, requestId);
        JsonElement timeoutResponse = JsonSerializer.SerializeToElement(new { action = "cancel" });
        if (!pendingInteractions.TryAdd(key, "mcpServer/elicitation/request", originalRequestId, parameters,
                TimeSpan.FromMinutes(5), timeoutResponse, out PendingInteractionEntry entry))
        {
            if (protectedUrlInfo is not null)
            {
                authorizationUrls.Remove(context.Generation, protectedUrlInfo.ActionId);
            }

            return timeoutResponse;
        }

        if (entry.Completion.IsCompleted)
        {
            if (protectedUrlInfo is not null)
            {
                authorizationUrls.Remove(context.Generation, protectedUrlInfo.ActionId);
            }

            PendingInteractionResult earlyResult = await entry.Completion.ConfigureAwait(false);
            if (earlyResult.Kind is PendingInteractionCompletionKind.ExternallyResolved or PendingInteractionCompletionKind.GenerationRetired)
            {
                throw new JsonRpcRequestResolvedException();
            }

            return earlyResult.Response;
        }

        form.Request.RequestId = entry.InteractionId;
        context.McpElicitations[entry.InteractionId] = form;
        if (protectedUrlInfo is not null)
        {
            context.AuthorizationActionOwners[protectedUrlInfo.ActionId] = entry.InteractionId;
        }
        if (McpElicitationRequested is not null)
        {
            await McpElicitationRequested(form.Request, cancellationToken).ConfigureAwait(false);
        }

        PendingInteractionResult result = await entry.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        context.McpElicitations.TryRemove(entry.InteractionId, out _);
        RevokeAuthorizationActionsForOwner(context, entry.InteractionId);
        if (ShouldNotifyPendingResolution(context))
        {
            await EmitMcpElicitationResolvedAsync(entry.InteractionId, CancellationToken.None).ConfigureAwait(false);
        }

        if (result.Kind is PendingInteractionCompletionKind.ExternallyResolved or PendingInteractionCompletionKind.GenerationRetired)
        {
            throw new JsonRpcRequestResolvedException();
        }

        return result.Response;
    }

    private UserInputRequest CreateUserInputRequest(
        string requestId,
        JsonElement parameters,
        out Dictionary<string, Dictionary<string, string>> optionLabelsByQuestion)
    {
        var questions = new List<UserInputQuestion>();
        optionLabelsByQuestion = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
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
                var optionLabels = new Dictionary<string, string>(StringComparer.Ordinal);
                if (question.TryGetProperty("options", out JsonElement optionArray)
                    && optionArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement option in optionArray.EnumerateArray())
                    {
                        if (option.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string optionId = Guid.NewGuid().ToString("N");
                        string rawLabel = GetString(option, "label") ?? string.Empty;
                        optionLabels[optionId] = rawLabel;
                        options.Add(new UserInputOption
                        {
                            OptionId = optionId,
                            Label = redactor.Redact(rawLabel) ?? string.Empty,
                            Description = redactor.Redact(GetString(option, "description")),
                        });
                    }
                }

                string questionId = GetString(question, "id") ?? string.Empty;
                optionLabelsByQuestion[questionId] = optionLabels;
                questions.Add(new UserInputQuestion
                {
                    Id = questionId,
                    Header = redactor.Redact(GetString(question, "header")),
                    Question = redactor.Redact(GetString(question, "question")),
                    IsOther = GetBoolean(question, "isOther") == true,
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
            IsBlocking = GetBoolean(parameters, "isBlocking") == true,
            Questions = questions,
        };
    }

    private static JsonElement ValidateAnswers(
        UserInputProjection projection,
        IDictionary<string, UserInputAnswer> answers)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (answers.Keys.Any(key => !projection.Request.Questions.Any(question => string.Equals(question.Id, key, StringComparison.Ordinal)))
            || projection.Request.IsBlocking && projection.Request.Questions.Any(question => !answers.ContainsKey(question.Id)))
        {
            throw new ArgumentException("The submitted answers do not match this request.", nameof(answers));
        }

        foreach ((string questionId, UserInputAnswer? answer) in answers)
        {
            UserInputQuestion question = projection.Request.Questions.Single(question => string.Equals(question.Id, questionId, StringComparison.Ordinal));
            Dictionary<string, string> optionLabels = projection.OptionLabelsByQuestion[question.Id];
            if (answer is null)
            {
                throw new ArgumentException("An answer is invalid.", nameof(answers));
            }

            if (answer.Kind == UserInputAnswerKind.FreeText && question.Options.Count == 0 && !question.IsOther
                && answer.OptionIds.Count == 0 && answer.Text is not null && answer.Text.Length <= 65536)
            {
                result[question.Id] = new { answers = new[] { answer.Text } };
            }
            else if (answer.Kind == UserInputAnswerKind.Other && question.IsOther
                && answer.OptionIds.Count == 0 && answer.Text is not null && answer.Text.Length <= 65536)
            {
                result[question.Id] = new { answers = new[] { answer.Text } };
            }
            else if (answer.Kind == UserInputAnswerKind.SelectedOptions && answer.OptionIds.Count == 1
                && answer.Text is null
                && optionLabels.TryGetValue(answer.OptionIds[0], out string? rawLabel))
            {
                result[question.Id] = new { answers = new[] { rawLabel } };
            }
            else
            {
                throw new ArgumentException("An answer does not match the question options.", nameof(answers));
            }
        }

        return JsonSerializer.SerializeToElement(new { answers = result });
    }

    private List<object> BuildTurnInput(StartTurnRequest request)
    {
        var input = new List<object>
        {
            new { type = "text", text = request.Text },
        };

        if (request.Skill is not null)
        {
            input.Add(new
            {
                type = "skill",
                name = request.Skill.Name,
                path = request.Skill.Path,
            });
        }

        var includedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int attachmentCount = 0;
        foreach (AttachmentInfo attachment in request.Attachments)
        {
            bool isImage = string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase);
            bool isMention = string.Equals(attachment.Kind, "mention", StringComparison.OrdinalIgnoreCase);
            if (!isImage && !isMention)
            {
                continue;
            }

            // A partial attachment list must never be sent: a missing, unreadable, or protected
            // file rejects the whole turn, naming only the file.
            if (!TryNormalizeReadableFile(attachment.Path, allowOutsideWorkspace: true, out string normalizedPath))
            {
                throw new AttachmentRejectedException(
                    $"The attachment '{SafeAttachmentName(attachment.Path)}' is missing, unreadable, or protected. "
                    + "Remove it or attach a readable file.");
            }

            // An explicit attachment the remote server cannot see must never be dropped silently:
            // the user would believe the model received it. Reject the turn with a fixable reason.
            if (!TryMapLocalPathForServer(normalizedPath, out string serverPath))
            {
                throw new AttachmentRejectedException(
                    $"The attachment '{Path.GetFileName(normalizedPath)}' is outside the remote profile's local root, "
                    + "so the remote app-server cannot read it. Remove it or move it under the mapped local root.");
            }

            if (!includedPaths.Add(normalizedPath))
            {
                continue;
            }

            // Every entry is validated; an excess attachment rejects the turn instead of being
            // dropped from the list.
            if (++attachmentCount > MaxTurnAttachments)
            {
                throw new AttachmentRejectedException(
                    $"At most {MaxTurnAttachments} attachments can be sent in one turn. Remove some, then send again.");
            }

            if (isImage)
            {
                input.Add(new
                {
                    type = "localImage",
                    path = serverPath,
                });
            }
            else
            {
                input.Add(new
                {
                    type = "mention",
                    name = Path.GetFileName(normalizedPath),
                    path = serverPath,
                });
            }
        }

        if (request.IdeContext is null)
        {
            return input;
        }

        string? activeDocumentPath = request.IdeContext.ActiveDocumentPath;
        if (TryNormalizeReadableFile(activeDocumentPath, allowOutsideWorkspace: false, out string normalizedActivePath)
            && TryMapLocalPathForServer(normalizedActivePath, out string activeServerPath)
            && includedPaths.Add(normalizedActivePath))
        {
            input.Add(new
            {
                type = "mention",
                name = Path.GetFileName(normalizedActivePath),
                path = activeServerPath,
            });
        }

        foreach (string path in request.IdeContext.ReferencedFilePaths.Take(10))
        {
            if (!TryNormalizeReadableFile(path, allowOutsideWorkspace: false, out string normalizedPath)
                || !TryMapLocalPathForServer(normalizedPath, out string serverPath)
                || !includedPaths.Add(normalizedPath))
            {
                continue;
            }

            input.Add(new
            {
                type = "mention",
                name = Path.GetFileName(normalizedPath),
                path = serverPath,
            });
        }

        string? selection = LimitUtf8(request.IdeContext.SelectionText, 32 * 1024);
        if (!string.IsNullOrEmpty(selection))
        {
            string? selectionPath = request.IdeContext.SelectionFilePath;
            string? serverSelectionPath = selectionPath is not null
                && TryMapLocalPathForServer(selectionPath, out string mappedSelectionPath)
                ? mappedSelectionPath
                : null;
            string header = IsWorkspacePath(selectionPath)
                ? $"IDE selection from {serverSelectionPath ?? "the active document"}:"
                : "IDE selection:";
            input.Add(new
            {
                type = "text",
                text = $"{header}{Environment.NewLine}{selection}",
            });
        }

        return input;
    }

    private async Task ValidateSkillInvocationAsync(
        SkillInvocationInfo invocation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invocation.Name)
            || string.IsNullOrWhiteSpace(invocation.Scope)
            || string.IsNullOrWhiteSpace(invocation.Path))
        {
            throw new SkillInvocationRejectedException("The selected skill identity is incomplete.");
        }

        ListSkillsResult catalog = await ListSkillsAsync(forceReload: true, cancellationToken).ConfigureAwait(false);
        bool exactEnabled = catalog.IsSupported
            && !catalog.IsStale
            && catalog.Skills.Any(skill =>
                skill.Enabled
                && string.Equals(skill.Name, invocation.Name, StringComparison.Ordinal)
                && string.Equals(skill.Scope, invocation.Scope, StringComparison.Ordinal)
                && string.Equals(skill.Path, invocation.Path, StringComparison.Ordinal));
        if (!exactEnabled)
        {
            throw new SkillInvocationRejectedException("The selected skill is stale, disabled, or unavailable.");
        }
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

    private string MapLocalPathForServer(string localPath)
    {
        if (remotePathMapper is null)
        {
            return localPath;
        }

        if (LocalPath.TryCreate(localPath, out LocalPath parsed)
            && remotePathMapper.TryMapLocalToServer(
                parsed,
                localPathBoundary,
                out ServerPath serverPath,
                out _))
        {
            return serverPath.Value;
        }

        throw new InvalidOperationException("The local path cannot be safely mapped to the remote working root.");
    }

    private bool TryMapLocalPathForServer(string localPath, out string serverPath)
    {
        if (remotePathMapper is null)
        {
            serverPath = localPath;
            return true;
        }

        serverPath = string.Empty;
        if (!LocalPath.TryCreate(localPath, out LocalPath parsed)
            || !remotePathMapper.TryMapLocalToServer(
                parsed,
                localPathBoundary,
                out ServerPath mapped,
                out _))
        {
            return false;
        }

        serverPath = mapped.Value;
        return true;
    }

    private string? MapServerPathToLocal(string? serverPath)
    {
        if (string.IsNullOrWhiteSpace(serverPath) || remotePathMapper is null)
        {
            return serverPath;
        }

        return ServerPath.TryCreate(serverPath, out ServerPath parsed)
            && remotePathMapper.TryMapServerToLocal(
                parsed,
                localPathBoundary,
                out LocalPath localPath,
                out _)
                ? localPath.Value
            : null;
    }

    // A remote thread's working directory is a server path. It is shown only as its mapped local
    // path; an unmappable value becomes a fixed label so the raw server layout is not displayed.
    private string? DisplayThreadWorkingDirectory(string? serverCwd)
    {
        if (remotePathMapper is null || string.IsNullOrWhiteSpace(serverCwd))
        {
            return serverCwd;
        }

        return MapServerPathToLocal(serverCwd) ?? RemoteWorkingDirectoryLabel;
    }

    private static string SafeAttachmentName(string? path)
    {
        string name;
        try
        {
            name = Path.GetFileName(path ?? string.Empty);
        }
        catch (ArgumentException)
        {
            name = string.Empty;
        }

        name = new string(name.Where(static c => !char.IsControl(c)).Take(128).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
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
        ConnectionContext? context = Interlocked.Exchange(ref connectionContext, null);
        if (context is not null)
        {
            context.Detach();
            CancelPending(context.Generation);
        }
        await ResetSkillsBackgroundRefreshAsync().ConfigureAwait(false);
        skillsBackgroundRefreshCancellation.Cancel();
        skillsBackgroundRefreshCancellation.Dispose();
        await RetireStreamingBufferAsync().ConfigureAwait(false);

        pendingInteractions.Dispose();
        authorizationUrls.Dispose();
        skillsCacheGate.Dispose();
    }

    private async Task<JsonElement> OnServerRequestAsync(ConnectionContext context, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        ConnectionContext? previousContext = emittingContext.Value;
        emittingContext.Value = context;
        try
        {
            return await OnServerRequestCoreAsync(context, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            emittingContext.Value = previousContext;
        }
    }

    private async Task<JsonElement> OnServerRequestCoreAsync(ConnectionContext context, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        if (!IsCurrent(context))
        {
            throw new JsonRpcRemoteException(-32000, "The app-server connection generation is no longer active.");
        }
        JsonElement originalRequestId = message.Id
            ?? throw new JsonRpcRemoteException(-32600, "The server request has no JSON-RPC id.");
        if (!JsonRpcRequestId.TryGetKey(originalRequestId, out string requestId))
        {
            throw new JsonRpcRemoteException(-32600, "The server request id is invalid.");
        }
        JsonElement parameters = message.Params ?? JsonSerializer.SerializeToElement(new { });
        string method = message.Method ?? string.Empty;

        if (method == "item/tool/requestUserInput")
        {
            ValidateUserInputParameters(parameters);
            return await HandleUserInputRequestAsync(context, originalRequestId, requestId, parameters, cancellationToken).ConfigureAwait(false);
        }

        if (method == "item/permissions/requestApproval")
        {
            return await HandlePermissionSelectionRequestAsync(context, originalRequestId, requestId, parameters, cancellationToken).ConfigureAwait(false);
        }

        if (method == "mcpServer/elicitation/request")
        {
            return await HandleMcpElicitationRequestAsync(context, originalRequestId, requestId, parameters, cancellationToken).ConfigureAwait(false);
        }

        // Only known approval requests may enter the approval policy and grant store.  App
        // server requests are untrusted input; routing an unknown request to this path could
        // accidentally grant permissions for a future method with different semantics.
        if (!IsApprovalRequestMethod(method))
        {
            throw new JsonRpcRemoteException(-32601, $"Unsupported server request method '{method}'.");
        }

        ValidateApprovalParameters(method, parameters);

        ApprovalRequest request = CreateApprovalRequest(requestId, method, parameters);
        if (request.IsPolicyBlocked)
        {
            return ApprovalResponse("decline");
        }

        if (!OfferedCommandChoiceSet.TryCreate(parameters, value => redactor.Redact(value) ?? string.Empty,
                static () => Guid.NewGuid().ToString("N"), out OfferedCommandChoiceSet? choices)
            || choices is null)
        {
            throw new JsonRpcRemoteException(-32602, "The command-approval choices are invalid.");
        }

        request.Choices = choices.Choices;
        if (CanReuseSessionApproval(parameters)
            && context.ApprovalGrants.FindApproval(request) is { Scope: ApprovalScope.Session } sessionGrant
            && choices.TryResolveDecision("acceptForSession", out JsonElement automaticResponse))
        {
            await EmitApprovalAuditAsync(request, ApprovalAuditAction.AutoApproved, sessionGrant.Scope, cancellationToken).ConfigureAwait(false);
            return automaticResponse;
        }

        var key = new PendingInteractionKey(context.Generation, requestId);
        if (!pendingInteractions.TryAdd(key, method, originalRequestId, parameters, TimeSpan.FromMinutes(5),
                ApprovalResponse("cancel"), out PendingInteractionEntry entry))
        {
            throw new JsonRpcRemoteException(-32600, "The approval request is no longer active.");
        }

        if (entry.Completion.IsCompleted)
        {
            PendingInteractionResult earlyResult = await entry.Completion.ConfigureAwait(false);
            if (earlyResult.Kind is PendingInteractionCompletionKind.ExternallyResolved or PendingInteractionCompletionKind.GenerationRetired)
            {
                throw new JsonRpcRequestResolvedException();
            }

            return earlyResult.Response;
        }

        request.RequestId = entry.InteractionId;
        context.CommandChoices[entry.InteractionId] = choices;
        if (ApprovalRequested is not null)
        {
            await ApprovalRequested(request, cancellationToken).ConfigureAwait(false);
        }

        PendingInteractionResult result = await entry.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        context.CommandChoices.TryRemove(entry.InteractionId, out _);
        if (result.Kind is PendingInteractionCompletionKind.ExternallyResolved or PendingInteractionCompletionKind.GenerationRetired)
        {
            throw new JsonRpcRequestResolvedException();
        }

        if (ShouldNotifyPendingResolution(context))
        {
            await EmitApprovalResolvedAsync(entry.InteractionId, CancellationToken.None).ConfigureAwait(false);
        }

        return result.Response;
    }

    private async Task OnNotificationAsync(ConnectionContext context, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        ConnectionContext? previousContext = emittingContext.Value;
        emittingContext.Value = context;
        try
        {
            await OnNotificationCoreAsync(context, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            emittingContext.Value = previousContext;
        }
    }

    private async Task OnNotificationCoreAsync(ConnectionContext context, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        if (!IsCurrent(context))
        {
            return;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.Lifetime.Token);
        try
        {
            await DispatchNotificationAsync(context, message, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !linked.Token.IsCancellationRequested)
        {
            // Report request timeouts too; stay silent only when the notification or connection was canceled.
            await EmitAsync(new ConversationEvent
            {
                Kind = ConversationEventKind.Error,
                Text = $"Unhandled notification '{redactor.Redact(message.Method)}': {redactor.Redact(ex.Message)}",
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task DispatchNotificationAsync(ConnectionContext context, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        string method = message.Method ?? string.Empty;
        JsonElement parameters = message.Params ?? JsonSerializer.SerializeToElement(new { });
        EnsureCurrent(context);
        if (method == "account/gatewayOAuth/changed")
        {
            await HandleGatewayOAuthChangedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "mcpServer/oauthLogin/completed")
        {
            await HandleMcpOAuthCompletedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "mcpServer/startupStatus/updated")
        {
            await HandleMcpStartupStatusAsync(context, parameters, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "skills/changed")
        {
            long generation = InvalidateSkillsCache();
            await DeletePersistedSkillsAsync(cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            if (SkillsChanged is not null)
            {
                await SkillsChanged(new SkillsChangedEvent { Generation = generation }, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        string? threadId = GetString(parameters, "threadId");
        if (method == "thread/attachment/updated")
        {
            ThreadAttachmentUpdatedEvent? attachmentEvent = ReadThreadAttachmentUpdated(parameters);
            if (attachmentEvent is not null && ThreadAttachmentUpdated is not null)
            {
                await ThreadAttachmentUpdated(attachmentEvent, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        string? turnId = GetString(parameters, "turnId");
        string? itemId = GetString(parameters, "itemId");
        bool suppressTurnEvent = false;
        if (method == "turn/started" && parameters.TryGetProperty("turn", out JsonElement startedTurn))
        {
            threadId ??= GetString(startedTurn, "threadId");
            string? startedTurnId = GetString(startedTurn, "id");
            lock (turnStateLock)
            {
                // A late turn/started notification must not revive a completed turn.
                if (startedTurnId is not null
                    && IsTrackedTurnThreadLocked(threadId)
                    && !completedTurnIds.Contains(new TurnKey(context.Generation, threadId, startedTurnId)))
                {
                    ActiveTurnId = startedTurnId;
                    turnId = startedTurnId;
                }
                else
                {
                    suppressTurnEvent = true;
                }
            }
        }
        else if (method == "turn/completed")
        {
            // The wire shape is { threadId, turn: { id } }; a top-level turnId is accepted only as
            // a fallback so the (generation, thread, turn) key is never lost for a real completion.
            string? completedId = turnId
                ?? (parameters.TryGetProperty("turn", out JsonElement completedTurn)
                    ? GetString(completedTurn, "id")
                    : null);
            bool otherThread;
            lock (turnStateLock)
            {
                otherThread = !IsTrackedTurnThreadLocked(threadId);
                completedId ??= otherThread ? null : ActiveTurnId;
                if (completedId is not null)
                {
                    completedTurnIds.Add(new TurnKey(context.Generation, threadId, completedId));
                }

                if (!otherThread
                    && (completedId is null || string.Equals(ActiveTurnId, completedId, StringComparison.Ordinal)))
                {
                    ActiveTurnId = null;
                }
            }

            turnId = completedId;
            LogInterruptedTurnCompletion(context, threadId, completedId, parameters);
            context.ApprovalGrants.EndTurn(threadId, completedId);
            if (otherThread)
            {
                return;
            }
        }
        else if (method == "thread/closed")
        {
            context.ApprovalGrants.EndThread(threadId);
        }

        if (method == "serverRequest/resolved")
        {
            if (parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("requestId", out JsonElement resolvedId)
                && JsonRpcRequestId.TryGetKey(resolvedId, out string requestId))
            {
                var key = new PendingInteractionKey(context.Generation, requestId);
                pendingInteractions.TryGet(key, out PendingInteractionEntry? entry);
                if (pendingInteractions.TryResolveExternally(key, entry))
                {
                    if (entry is not null && ShouldNotifyPendingResolution(context))
                    {
                        await EmitInteractionResolvedAsync(entry, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            return;
        }

        if (method is "account/login/completed" or "account/updated")
        {
            // The pinned notification carries only auth mode and plan, so it cannot prove that
            // the authenticated owner is unchanged. Retire the old generation before accepting
            // any later account-scoped result, even when email and plan appear unchanged.
            // Only a completion for the sign-in this owner started counts as an owner action. Any
            // other notification is verified by reading the account again: the app-server also
            // sends account/updated without an account change (for example shortly after startup).
            string? completedLoginId = method == "account/login/completed" ? GetString(parameters, "loginId") : null;
            bool loginCompleted = completedLoginId is not null
                && string.Equals(completedLoginId, context.PendingLoginId, StringComparison.Ordinal);
            if (context.LogoutRequested)
            {
                invalidatedAccountState = AccountState.SignedOut;
                invalidatedByOwnerAction = true;
                await InvalidateOwnerPartitionAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!loginCompleted)
            {
                EnsureCurrent(context);

                // Before the owner's first account read completes, that read covers the change.
                if (context.AccountFingerprint is not null)
                {
                    await GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            invalidatedAccountState = AccountState.Unavailable;
            invalidatedByOwnerAction = true;
            await InvalidateOwnerPartitionAsync(context, cancellationToken).ConfigureAwait(false);
            await EmitAccountStatusAsync(
                new AccountStatus
                {
                    State = AccountState.Unavailable,
                    Message = "The account changed. Reconnect to confirm the active account.",
                },
                CancellationToken.None).ConfigureAwait(false);
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
                EnsureCurrent(context);
                EffectiveApprovalState = ReadEffectiveApprovalState(threadSettings);
                ReadEffectiveTurnSettings(threadSettings, out string? reasoningEffort, out string? serviceTier);
                EffectiveReasoningEffort = reasoningEffort;
                EffectiveServiceTier = serviceTier;
                if (EffectiveApprovalStateChanged is not null)
                {
                    await EffectiveApprovalStateChanged(EffectiveApprovalState, cancellationToken).ConfigureAwait(false);
                    EnsureCurrent(context);
                }

            }

            return;
        }

        if (method == "account/rateLimits/updated"
            && parameters.TryGetProperty("rateLimits", out JsonElement updatedRateLimits))
        {
            EnsureCurrent(context);
            await EmitRateLimitsChangedAsync(
                new RateLimitsResult { RateLimits = ReadRateLimit(updatedRateLimits) },
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            return;
        }

        if (method == "thread/goal/updated")
        {
            EnsureCurrent(context);
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
            EnsureCurrent(context);
            return;
        }

        if (method == "thread/goal/cleared")
        {
            EnsureCurrent(context);
            await EmitThreadGoalChangedAsync(
                new ThreadGoalEvent
                {
                    ThreadId = threadId ?? string.Empty,
                    TurnId = turnId,
                    IsCleared = true,
                },
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            return;
        }

        if (method == "thread/compacted")
        {
            EnsureCurrent(context);
            await EmitContextCompactedAsync(
                new ContextCompactionEvent
                {
                    ThreadId = threadId ?? string.Empty,
                    TurnId = turnId ?? string.Empty,
                    IsCompleted = true,
                },
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
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
            EnsureCurrent(context);
            return;
        }

        ConversationEventKind kind = MapKind(method);
        if (kind == ConversationEventKind.Unknown)
        {
            WorkerDiagnostics.Write($"Ignoring unsupported app-server notification method={redactor.Redact(method)}");
            return;
        }

        if (suppressTurnEvent)
        {
            return;
        }
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

        EnsureCurrent(context);
        await EmitAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleGatewayOAuthChangedAsync(
        ConnectionContext context,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!context.IsLocal
            || !HasString(parameters, "providerId")
            || !HasString(parameters, "status")
            || !string.Equals(GetString(parameters, "providerId"), context.GatewayOAuthProviderId, StringComparison.Ordinal))
        {
            return;
        }

        string status = GetString(parameters, "status")!;
        if (status is not ("notReady" or "started" or "succeeded" or "failed"))
        {
            context.GatewayOAuthReady = false;
            context.GatewayOAuthMessage = "Gateway authorization status is invalid. Refresh the status before continuing.";
            SetGatewayState(context, InteractionAuthState.Unavailable, context.GatewayOAuthMessage);
            await EmitInteractionAuthStatusAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        RevokeGatewayAuthorizationAction(context);
        string? error = GetString(parameters, "error");
        string? authUrl = GetString(parameters, "authUrl");
        if (status == "started" && authUrl is not null
            && authorizationUrls.TryStore(context.Generation, authUrl, out ProtectedAuthorizationUrlInfo urlInfo))
        {
            context.GatewayOAuthActionId = urlInfo.ActionId;
            context.GatewayOAuthOriginDisplay = redactor.Redact(urlInfo.OriginDisplay);
            context.AuthorizationActionOwners[urlInfo.ActionId] = "gateway";
        }
        else
        {
            context.GatewayOAuthOriginDisplay = null;
        }

        context.GatewayOAuthReady = status == "succeeded" && string.IsNullOrWhiteSpace(error);
        if (status is "succeeded" or "failed" or "notReady")
        {
            context.GatewayLoginAttemptId = null;
        }
        context.GatewayOAuthMessage = status switch
        {
            "notReady" => "Gateway authorization is required before using this app-server.",
            "started" => "Complete gateway authorization in the browser.",
            "succeeded" when context.GatewayOAuthReady => null,
            "succeeded" => "Gateway authorization needs attention. Refresh the status before continuing.",
            _ => "Gateway authorization failed. Retry the status check before continuing.",
        };
        SetGatewayState(context, status == "succeeded" && context.GatewayOAuthReady
            ? InteractionAuthState.Authenticated
            : MapGatewayOAuthState(required: true, status, error is null ? null : "error"), context.GatewayOAuthMessage);
        await EmitInteractionAuthStatusAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMcpOAuthCompletedAsync(
        ConnectionContext context,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        string? rawName = GetString(parameters, "name");
        if (string.IsNullOrWhiteSpace(rawName) || !HasBoolean(parameters, "success"))
        {
            return;
        }

        bool succeeded = GetBoolean(parameters, "success") == true;
        MarkMcpOAuthOperationsCompleted(context, rawName);
        string serverName = redactor.Redact(rawName) ?? string.Empty;
        var status = new McpServerAuthStatus
        {
            ServerName = serverName,
            State = succeeded ? InteractionAuthState.Authenticated : InteractionAuthState.Failed,
            Message = succeeded ? "MCP server authorization completed." : "MCP server authorization did not complete. Retry from the server status.",
        };
        context.McpAuthStatuses[rawName] = status;
        if (!succeeded)
        {
            RetireMcpElicitations(context, rawName);
        }

        await EmitInteractionAuthStatusAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMcpStartupStatusAsync(
        ConnectionContext context,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        string? rawName = GetString(parameters, "name");
        string? wireStatus = GetString(parameters, "status");
        if (string.IsNullOrWhiteSpace(rawName) || wireStatus is not ("starting" or "ready" or "failed" or "cancelled"))
        {
            return;
        }

        string? failureReason = GetString(parameters, "failureReason");
        bool needsReauthentication = string.Equals(failureReason, "reauthenticationRequired", StringComparison.Ordinal);
        InteractionAuthState state = needsReauthentication
            ? InteractionAuthState.ReauthenticationRequired
            : wireStatus switch
            {
                "ready" => InteractionAuthState.Authenticated,
                "starting" => InteractionAuthState.Checking,
                _ => InteractionAuthState.Failed,
            };
        string? message = needsReauthentication
            ? "MCP server authorization expired. Sign in again to continue."
            : wireStatus switch
            {
                "ready" => null,
                "starting" => "MCP server is starting.",
                "cancelled" => "MCP server startup was canceled.",
                _ => "MCP server is unavailable. Check its status before retrying.",
            };
        context.McpAuthStatuses[rawName] = new McpServerAuthStatus
        {
            ServerName = redactor.Redact(rawName) ?? string.Empty,
            State = state,
            Message = message,
        };
        if (context.McpOAuthOperationsByServer.TryGetValue(rawName, out string? operationId)
            && context.McpOAuthOperations.TryGetValue(operationId, out McpOAuthOperation? operation))
        {
            operation.NotificationObserved = true;
        }
        if (needsReauthentication || wireStatus is "failed" or "cancelled")
        {
            RetireMcpElicitations(context, rawName);
        }

        await EmitInteractionAuthStatusAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private void MarkMcpOAuthOperationsCompleted(ConnectionContext context, string serverName)
    {
        foreach ((string operationId, McpOAuthOperation operation) in context.McpOAuthOperations)
        {
            if (string.Equals(operation.ServerName, serverName, StringComparison.Ordinal))
            {
                operation.Completed = true;
                RevokeAuthorizationAction(context, operation.ActionId);
                operation.ActionId = null;
                context.McpOAuthOperations.TryRemove(operationId, out _);
                context.McpOAuthOperationsByServer.TryRemove(serverName, out _);
            }
        }
    }

    private void RetireMcpElicitations(ConnectionContext context, string serverName)
    {
        foreach ((string interactionId, McpElicitationForm form) in context.McpElicitations)
        {
            if (string.Equals(form.Request.ServerName, serverName, StringComparison.Ordinal)
                && pendingInteractions.TryGetByInteractionId(context.Generation, interactionId, out PendingInteractionEntry entry))
            {
                RevokeAuthorizationActionsForOwner(context, interactionId);
                pendingInteractions.TryComplete(
                    entry.Key,
                    entry,
                    JsonSerializer.SerializeToElement(new { action = "cancel" }));
            }
        }
    }

    private ApprovalRequest CreateApprovalRequest(string requestId, string method, JsonElement parameters)
    {
        string? command = GetString(parameters, "command");
        string? serverCwd = GetString(parameters, "cwd");
        string? serverGrantRoot = GetString(parameters, "grantRoot");
        string? cwd = MapServerPathToLocal(serverCwd);
        string? grantRoot = MapServerPathToLocal(serverGrantRoot);
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

        bool unmappablePath = remotePathMapper is not null
            && ((serverCwd is not null && cwd is null) || (serverGrantRoot is not null && grantRoot is null));
        ApprovalPolicyResult policy = unmappablePath
            ? new ApprovalPolicyResult(
                ApprovalRiskCategory.WorkspaceOutside,
                "remote-path-unmappable",
                true,
                "The remote path is outside the configured local root.")
            : method.Contains("fileChange", StringComparison.Ordinal)
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

    // Logs the completion of a turn the user asked to stop: the final status (normally
    // "interrupted") and the time from the stop request to the end of the turn.
    private void LogInterruptedTurnCompletion(
        ConnectionContext context,
        string? threadId,
        string? completedId,
        JsonElement parameters)
    {
        if (completedId is null)
        {
            return;
        }

        long requestedAt;
        lock (turnStateLock)
        {
            if (!interruptRequestedAt.Remove(new TurnKey(context.Generation, threadId, completedId), out requestedAt))
            {
                return;
            }
        }

        string status = parameters.TryGetProperty("turn", out JsonElement turn)
            ? NormalizeWireIdentifier(GetString(turn, "status")) ?? "unknown"
            : "unknown";
        WorkerDiagnostics.Write(
            $"turn completed after interrupt request turn={completedId} status={status} elapsedMs={ElapsedMilliseconds(requestedAt)}");
    }

    // A turn event belongs to the tracked conversation when it names no thread, when no thread is
    // tracked yet, or when it names the active thread or the thread whose turn/start is in flight.
    private bool IsTrackedTurnThreadLocked(string? threadId)
        => threadId is null
            || (ActiveThreadId is null && pendingTurnThreadId is null)
            || string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal)
            || string.Equals(pendingTurnThreadId, threadId, StringComparison.Ordinal);

    private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireContext();
        if (method is not ("account/read" or "account/login/start" or "account/logout"))
        {
            EnsureCredentialReady(context);
        }
        JsonElement result = await context.Connection.SendRequestAsync(
            method,
            parameters,
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        return result;
    }

    private async Task<JsonElement> SendReadOnlyAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireContext();
        if (method != "account/read")
        {
            EnsureCredentialReady(context);
        }
        JsonElement result = await context.Connection.SendReadOnlyRequestAsync(
            method,
            parameters,
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        return result;
    }

    private void EnsureCurrent(ConnectionContext context)
    {
        if (!IsCurrent(context))
        {
            throw new JsonRpcConnectionClosedException("The app-server connection generation changed.");
        }
    }

    private bool IsCurrent(ConnectionContext context)
        => ReferenceEquals(Volatile.Read(ref connectionContext), context);

    private void OnConnectionClosed(ConnectionContext context)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref connectionContext, null, context), context))
        {
            context.NotifyPendingResolution = true;
            context.Detach();
            CancelPending(context.Generation);
        }
    }

    private bool ShouldNotifyPendingResolution(ConnectionContext context)
        => IsCurrent(context)
            || (Volatile.Read(ref connectionContext) is null && context.NotifyPendingResolution);

    private void CancelPending(long? generation)
    {
        if (generation is long currentGeneration)
        {
            pendingInteractions.RetireGeneration(currentGeneration);
            authorizationUrls.RetireGeneration(currentGeneration);
        }
        else if (Volatile.Read(ref connectionContext) is { } context)
        {
            pendingInteractions.RetireGeneration(context.Generation);
            authorizationUrls.RetireGeneration(context.Generation);
        }
    }

    private async Task<OperationCallResult> TrySendOperationAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireContext();
        if (method != "account/read")
        {
            EnsureCredentialReady(context);
        }
        lock (context.UnsupportedMethodsLock)
        {
            if (context.UnsupportedMethods.Contains(method))
            {
                return OperationCallResult.Unsupported;
            }
        }

        try
        {
            // SendReadOnlyRequestAsync owns retry eligibility through its exact method
            // allowlist; every other method (including all mutations) is sent once.
            JsonElement result = await context.Connection.SendReadOnlyRequestAsync(
                method,
                parameters,
                timeout,
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            return new OperationCallResult(true, result);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == -32601)
        {
            lock (context.UnsupportedMethodsLock)
            {
                if (IsCurrent(context))
                {
                    context.UnsupportedMethods.Add(method);
                }
            }

            WorkerDiagnostics.Write($"app-server method disabled for this session method={method}", ex);
            return OperationCallResult.Unsupported;
        }
    }

    private static void EnsureCredentialReady(ConnectionContext context)
    {
        if (!context.GatewayOAuthReadSucceeded || !context.GatewayOAuthReady)
        {
            throw new InvalidOperationException(
                context.GatewayOAuthReadSucceeded
                    ? "Complete gateway authorization or cancel it before using this app-server operation."
                    : "Gateway authorization status is unavailable. Retry the status check before using this app-server operation.");
        }
    }

    private long InvalidateSkillsCache()
    {
        long generation = Interlocked.Increment(ref skillsGeneration);
        skillsSnapshot = null;
        skillsSnapshotExpiresAt = default;
        return generation;
    }

    private async Task InvalidateOwnerPartitionAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        if (!IsCurrent(context)
            || !ReferenceEquals(Interlocked.CompareExchange(ref connectionContext, null, context), context))
        {
            return;
        }

        context.OwnerInvalidated = true;
        context.NotifyPendingResolution = false;
        context.Detach();
        context.ApprovalGrants.Clear();
        await RetireStreamingBufferAsync().ConfigureAwait(false);
        CancelPending(context.Generation);
        await ResetSkillsBackgroundRefreshAsync().ConfigureAwait(false);
        await skillsCacheGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            InvalidateSkillsCache();
        }
        finally
        {
            skillsCacheGate.Release();
        }
        CodexVersion = null;
        InitializationMetadata = null;
        EffectiveApprovalState = null;
        EffectiveReasoningEffort = null;
        EffectiveServiceTier = null;
        lock (turnStateLock)
        {
            completedTurnIds.Clear();
            interruptRequestedAt.Clear();
            pendingTurnThreadId = null;
            ActiveThreadId = null;
            ActiveTurnId = null;
        }

        long nextOwnerGeneration = Interlocked.Increment(ref ownerGeneration);
        statePartitionFingerprint = ConnectionStatePartition.Create(
            options,
            workerInstanceId,
            nextOwnerGeneration,
            credentialFingerprint);

        if (OwnerInvalidated is { } handlers)
        {
            foreach (Func<IJsonRpcConnection, CancellationToken, Task> handler in handlers.GetInvocationList())
            {
                try
                {
                    await handler(context.Connection, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    WorkerDiagnostics.Write("owner invalidation observer failed", ex);
                }
            }
        }
    }

    private async Task EmitForContextAsync(
        ConnectionContext context,
        ConversationEvent value,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(context))
        {
            return;
        }

        ConnectionContext? previousContext = emittingContext.Value;
        emittingContext.Value = context;
        try
        {
            if (IsCurrent(context))
            {
                await EmitAsync(value, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            emittingContext.Value = previousContext;
        }
    }

    private async Task RetireStreamingBufferAsync()
    {
        StreamingBuffer? retired = streamingBuffer;
        streamingBuffer = null;
        if (retired is not null)
        {
            await retired.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string? GetSkillsCacheIdentity()
    {
        if (!CanPersistOwnerState || string.IsNullOrWhiteSpace(CodexVersion))
        {
            return null;
        }

        // A skill catalog is server-owned data. Include a stable hash of the endpoint and
        // mapping roots so a remote profile can never consume another server's catalog while
        // keeping sensitive endpoint/path values out of the persisted cache metadata.
        string identity = string.Join("\n", StatePartitionFingerprint, options.RemoteEndpoint, options.LocalRoot, options.ServerRoot);
        byte[] digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{CodexVersion}:{Convert.ToHexString(digest)}";
    }

    private async Task DeletePersistedSkillsAsync(CancellationToken cancellationToken)
    {
        if (!CanPersistOwnerState || string.IsNullOrWhiteSpace(StatePartitionFingerprint))
        {
            return;
        }

        try
        {
            await skillCatalogStore.DeleteAsync(options.WorkingDirectory, StatePartitionFingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WorkerDiagnostics.Write("skill catalog cache invalidation failed", ex);
        }
    }

    private async Task ResetSkillsBackgroundRefreshAsync()
    {
        Task? pending;
        CancellationTokenSource previousCancellation;
        lock (skillsBackgroundRefreshLock)
        {
            pending = skillsBackgroundRefreshTask;
            skillsBackgroundRefreshTask = null;
            previousCancellation = skillsBackgroundRefreshCancellation;
            skillsBackgroundRefreshCancellation = new CancellationTokenSource();
        }

        previousCancellation.Cancel();
        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (previousCancellation.IsCancellationRequested)
            {
            }
        }

        previousCancellation.Dispose();
    }

    private static ListSkillsResult CloneSkillsResult(
        ListSkillsResult source,
        bool? isStale = null,
        long? generation = null)
        => new()
        {
            IsSupported = source.IsSupported,
            UnavailableReason = source.UnavailableReason,
            IsTruncated = source.IsTruncated,
            IsStale = isStale ?? source.IsStale,
            Generation = generation ?? source.Generation,
            Skills = source.Skills.Select(skill => new SkillInfo
            {
                Name = skill.Name,
                Description = skill.Description,
                ShortDescription = skill.ShortDescription,
                DisplayName = skill.DisplayName,
                Scope = skill.Scope,
                Path = skill.Path,
                Enabled = skill.Enabled,
                Cwd = skill.Cwd,
                BrandColor = skill.BrandColor,
                DefaultPrompt = skill.DefaultPrompt,
                HasIconSmall = skill.HasIconSmall,
                ToolDependencies = skill.ToolDependencies.Select(dependency => new SkillToolDependencyInfo
                {
                    Type = dependency.Type,
                    Value = dependency.Value,
                    Description = dependency.Description,
                }).ToArray(),
            }).ToArray(),
            Errors = source.Errors.Select(error => new SkillLoadError
            {
                Cwd = error.Cwd,
                Path = error.Path,
                Message = error.Message,
            }).ToArray(),
        };

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

    private ConnectionContext RequireContext()
        => Volatile.Read(ref connectionContext) ?? throw new InvalidOperationException("The app-server is not initialized.");

    private Task EmitAsync(ConversationEvent value, CancellationToken cancellationToken)
        => ConversationEventReceived?.Invoke(value, cancellationToken) ?? Task.CompletedTask;

    private Task EmitApprovalResolvedAsync(string requestId, CancellationToken cancellationToken)
        => ApprovalResolved?.Invoke(requestId, cancellationToken) ?? Task.CompletedTask;

    private Task EmitUserInputResolvedAsync(string requestId, CancellationToken cancellationToken)
        => UserInputResolved?.Invoke(requestId, cancellationToken) ?? Task.CompletedTask;

    private Task EmitPermissionResolvedAsync(string requestId, CancellationToken cancellationToken)
        => PermissionResolved?.Invoke(requestId, cancellationToken) ?? Task.CompletedTask;

    private Task EmitMcpElicitationResolvedAsync(string requestId, CancellationToken cancellationToken)
        => McpElicitationResolved?.Invoke(requestId, cancellationToken) ?? Task.CompletedTask;

    private Task EmitUnsupportedInteractionAsync(UnsupportedInteractionNotice notice, CancellationToken cancellationToken)
        => UnsupportedInteraction?.Invoke(notice, cancellationToken) ?? Task.CompletedTask;

    private Task EmitInteractionResolvedAsync(PendingInteractionEntry entry, CancellationToken cancellationToken)
        => entry.Method switch
        {
            "item/tool/requestUserInput" => EmitUserInputResolvedAsync(entry.InteractionId, cancellationToken),
            "item/permissions/requestApproval" => EmitPermissionResolvedAsync(entry.InteractionId, cancellationToken),
            "mcpServer/elicitation/request" => EmitMcpElicitationResolvedAsync(entry.InteractionId, cancellationToken),
            _ => EmitApprovalResolvedAsync(entry.InteractionId, cancellationToken),
        };

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

    // A Worker-only digest of the account identity fields. It never leaves the Worker.
    private static string ComputeAccountFingerprint(JsonElement result)
    {
        if (!result.TryGetProperty("account", out JsonElement account)
            || account.ValueKind != JsonValueKind.Object)
        {
            return "signed-out";
        }

        var builder = new StringBuilder();
        foreach (string? field in new[]
        {
            GetString(account, "type"),
            GetString(account, "email"),
            GetString(account, "planType") ?? GetString(account, "chatgptPlanType"),
        })
        {
            string value = field ?? string.Empty;
            builder.Append(value.Length).Append(':').Append(value).Append('\0');
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

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

    private ThreadSummary ReadThread(
        JsonElement thread,
        EffectiveApprovalState? effectiveApprovalState = null,
        string? effectiveReasoningEffort = null,
        string? effectiveServiceTier = null)
    {
        string? threadId = GetBoundedString(thread, "id", MaxAttachmentIdentityBytes);
        if (threadId is null)
        {
            throw InvalidHistoryResponse();
        }

        string? preview = GetBoundedString(thread, "preview", MaxThreadPreviewBytes);
        return new ThreadSummary
        {
            Id = threadId,
            Preview = preview,
            Cwd = DisplayThreadWorkingDirectory(GetString(thread, "cwd")),
            UpdatedAt = thread.TryGetProperty("updatedAt", out JsonElement updated) && updated.TryGetInt64(out long value) ? value : null,
            EffectiveApprovalState = effectiveApprovalState,
            EffectiveReasoningEffort = effectiveReasoningEffort,
            EffectiveServiceTier = effectiveServiceTier,
        };
    }

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

                // cwd is a filesystem path, not prose; cap it like path fields (MaxSkillPathLength)
                // rather than the shorter MaxSkillTextLength used for descriptions and messages,
                // or a long-but-valid working directory would be truncated to null.
                string? cwd = SanitizeSkillText(GetString(entry, "cwd"), MaxSkillPathLength);

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
                            // Same rooted-path validation as SkillInfo.Path (NormalizeSkillPath),
                            // not just length/control-character sanitization, so a malformed or
                            // relative error path is dropped rather than forwarded over the
                            // contract as if it were a real absolute path.
                            Path = NormalizeSkillPath(GetString(error, "path")),
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
            displayName = SanitizeSkillText(GetString(skillInterface, "displayName"));
            shortDescription ??= SanitizeSkillText(GetString(skillInterface, "shortDescription"));
        }

        string? brandColor = null;
        string? defaultPrompt = null;
        bool hasIconSmall = false;
        var dependencies = new List<SkillToolDependencyInfo>();
        if (skill.TryGetProperty("interface", out skillInterface)
            && skillInterface.ValueKind == JsonValueKind.Object)
        {
            string? candidateColor = GetString(skillInterface, "brandColor");
            if (candidateColor is not null
                && candidateColor.Length == 7
                && candidateColor[0] == '#'
                && candidateColor.Skip(1).All(c => Uri.IsHexDigit(c)))
            {
                brandColor = candidateColor.ToUpperInvariant();
            }

            defaultPrompt = SanitizeSkillText(
                GetString(skillInterface, "defaultPrompt"),
                MaxSkillDefaultPromptLength);
            hasIconSmall = skillInterface.TryGetProperty("iconSmall", out JsonElement icon)
                && icon.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(icon.GetString());
        }

        if (skill.TryGetProperty("dependencies", out JsonElement dependenciesElement)
            && dependenciesElement.ValueKind == JsonValueKind.Object
            && dependenciesElement.TryGetProperty("tools", out JsonElement tools)
            && tools.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tool in tools.EnumerateArray().Take(MaxSkillDependencyCount))
            {
                if (tool.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? type = SanitizeSkillText(GetString(tool, "type"), MaxSkillDependencyTypeLength);
                string? value = SanitizeSkillText(GetString(tool, "value"), MaxSkillDependencyValueLength);
                if (type is null || value is null)
                {
                    continue;
                }

                dependencies.Add(new SkillToolDependencyInfo
                {
                    Type = type,
                    Value = value,
                    Description = SanitizeSkillText(GetString(tool, "description"), MaxSkillDependencyDescriptionLength),
                });
            }
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
            BrandColor = brandColor,
            DefaultPrompt = defaultPrompt,
            HasIconSmall = hasIconSmall,
            ToolDependencies = dependencies,
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
        if (string.IsNullOrEmpty(value)
            || value.Length > MaxSkillPathLength
            || value.Any(char.IsControl)
            || !ServerPath.TryCreate(value, out _))
        {
            return null;
        }

        // No File.Exists / workspace-containment check: this path is the app-server's own
        // skills/list output, not user input, and scope: "user"/"system"/"admin" skills routinely
        // live outside the workspace (or are directories). Only structural validity is enforced.
        return value;
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

    private static bool? GetBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : null;

    private static string? GetBoundedString(JsonElement element, string name, int maximumUtf8Bytes)
    {
        string? value = GetString(element, name);
        return !string.IsNullOrWhiteSpace(value)
            && Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes
                ? value
                : null;
    }

    private static void ValidateHistoryId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > MaxAttachmentIdentityBytes)
        {
            throw new ArgumentException("A non-empty history identifier within the byte limit is required.", parameterName);
        }
    }

    private static string ValidateCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || Encoding.UTF8.GetByteCount(cursor) > 4096)
        {
            throw new ArgumentException("The history cursor is invalid.", nameof(cursor));
        }

        return cursor;
    }

    private static object CreateItemAnchor(ThreadItemCursor cursor, string? turnId)
    {
        ValidateHistoryId(turnId ?? string.Empty, nameof(turnId));
        if (string.IsNullOrWhiteSpace(cursor.ItemId)
            || Encoding.UTF8.GetByteCount(cursor.ItemId) > MaxAttachmentIdentityBytes
            || !string.Equals(cursor.TurnId, turnId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The exclusive item anchor is invalid.", nameof(cursor));
        }

        return new { type = "item", itemId = cursor.ItemId };
    }

    private static JsonElement RequireArray(JsonElement result, string name)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw InvalidHistoryResponse();
        }

        return property;
    }

    private static string? ReadOptionalCursor(JsonElement result)
    {
        if (!result.TryGetProperty("nextCursor", out JsonElement cursor)
            || cursor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (cursor.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(cursor.GetString())
            || Encoding.UTF8.GetByteCount(cursor.GetString()!) > 4096)
        {
            throw InvalidHistoryResponse();
        }

        return cursor.GetString();
    }

    private static string? ReadHistoryText(JsonElement item, string itemType)
    {
        var text = new StringBuilder();
        if (itemType == "agentMessage" && GetString(item, "text") is { } agentText)
        {
            AppendBoundedHistoryText(text, agentText);
        }
        else if (itemType == "userMessage"
            && item.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (GetString(part, "type") == "inputText" && GetString(part, "text") is { } userText)
                {
                    AppendBoundedHistoryText(text, userText);
                    if (Encoding.UTF8.GetByteCount(text.ToString()) >= MaxHistoryTextBytes)
                    {
                        break;
                    }
                }
            }
        }

        string value = text.ToString();
        return Encoding.UTF8.GetByteCount(value) <= MaxHistoryTextBytes ? value : null;
    }

    private static void AppendBoundedHistoryText(StringBuilder target, string value)
    {
        if (Encoding.UTF8.GetByteCount(target.ToString()) + Encoding.UTF8.GetByteCount(value) <= MaxHistoryTextBytes)
        {
            target.Append(value);
        }
    }

    private static ThreadAttachmentUpdatedEvent? ReadThreadAttachmentUpdated(JsonElement parameters)
    {
        string? threadId = GetBoundedString(parameters, "threadId", MaxAttachmentIdentityBytes);
        string? attachmentId = GetBoundedString(parameters, "attachmentId", MaxAttachmentIdentityBytes);
        string? attachmentType = GetBoundedString(parameters, "attachmentType", MaxAttachmentIdentityBytes);
        string? identityKey = GetBoundedString(parameters, "identityKey", MaxAttachmentIdentityBytes);
        string? operation = GetString(parameters, "operation");
        if (threadId is null || attachmentId is null || attachmentType is null || identityKey is null)
        {
            return null;
        }

        ThreadAttachmentOperation? parsedOperation = operation switch
        {
            "created" => ThreadAttachmentOperation.Created,
            "deleted" => ThreadAttachmentOperation.Deleted,
            _ => null,
        };
        return parsedOperation is null
            ? null
            : new ThreadAttachmentUpdatedEvent
            {
                ThreadId = threadId,
                AttachmentId = attachmentId,
                AttachmentType = attachmentType,
                IdentityKey = identityKey,
                Operation = parsedOperation.Value,
            };
    }

    private static InvalidDataException InvalidHistoryResponse()
        => new("The app-server returned an invalid history response.");

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
        ApprovalDecision.Decline => "decline",
        ApprovalDecision.Cancel => "cancel",
        _ => string.Empty,
    };

    private static bool TryReadApprovalDecision(JsonElement response, out string? decision)
    {
        decision = response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("decision", out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        return decision is not null;
    }

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

    private static void ValidateApprovalParameters(string method, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !HasString(parameters, "threadId")
            || !HasString(parameters, "turnId")
            || !HasString(parameters, "itemId"))
        {
            throw new JsonRpcRemoteException(-32602, "Approval request has invalid required fields.");
        }

        if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
        {
            if (!HasNumber(parameters, "startedAtMs"))
            {
                throw new JsonRpcRemoteException(-32602, "Approval request requires startedAtMs.");
            }
        }
        else if (method == "item/permissions/requestApproval"
            && (!HasString(parameters, "cwd")
                || !HasObject(parameters, "permissions")
                || !HasNumber(parameters, "startedAtMs")))
        {
            throw new JsonRpcRemoteException(-32602, "Permission approval request has invalid required fields.");
        }
    }

    private static void ValidateUserInputParameters(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !HasString(parameters, "threadId")
            || !HasString(parameters, "turnId")
            || !HasString(parameters, "itemId")
            || !HasBoolean(parameters, "isBlocking")
            || !HasArray(parameters, "questions"))
        {
            throw new JsonRpcRemoteException(-32602, "User-input request has invalid required fields.");
        }

        foreach (JsonElement question in parameters.GetProperty("questions").EnumerateArray())
        {
            if (question.ValueKind != JsonValueKind.Object
                || !HasString(question, "id")
                || !HasString(question, "header")
                || !HasString(question, "question"))
            {
                throw new JsonRpcRemoteException(-32602, "User-input question has invalid required fields.");
            }

            if (question.TryGetProperty("options", out JsonElement options)
                && options.ValueKind != JsonValueKind.Null
                && options.ValueKind != JsonValueKind.Array)
            {
                throw new JsonRpcRemoteException(-32602, "User-input options must be an array or null.");
            }

            if (options.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement option in options.EnumerateArray())
                {
                    if (option.ValueKind != JsonValueKind.Object
                        || !HasString(option, "label")
                        || !HasString(option, "description"))
                    {
                        throw new JsonRpcRemoteException(-32602, "User-input option has invalid required fields.");
                    }
                }
            }
        }
    }

    private static bool HasString(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String;

    private static bool HasNumber(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Number;

    private static bool HasBoolean(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property)
            && (property.ValueKind is JsonValueKind.True or JsonValueKind.False);

    private static bool HasObject(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Object;

    private static bool HasArray(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Array;

    private static bool IsApprovalRequestMethod(string method)
        => method is "item/commandExecution/requestApproval"
            or "item/fileChange/requestApproval"
            or "item/permissions/requestApproval";

    private static bool CanReuseSessionApproval(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (string name in new[] { "additionalPermissions", "proposedExecpolicyAmendment", "proposedNetworkPolicyAmendments" })
        {
            if (parameters.TryGetProperty(name, out JsonElement value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0))
            {
                return false;
            }
        }

        return true;
    }

    // Shapes the result per ToolRequestUserInputResponse: { answers: { <id>: { answers: [...] } } }.
    private static JsonElement UserInputResponse(IReadOnlyDictionary<string, UserInputAnswer> answers)
        => JsonSerializer.SerializeToElement(new { answers = new Dictionary<string, object>(StringComparer.Ordinal) });

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

    private sealed class McpOAuthOperation(string operationId, string serverName, string? threadId)
    {
        public string OperationId { get; } = operationId;

        public string ServerName { get; } = serverName;

        public string? ThreadId { get; } = threadId;

        public string? ActionId { get; set; }

        public bool Dismissed { get; set; }

        public bool Completed { get; set; }

        public bool RequestCanceled { get; set; }

        public bool NotificationObserved { get; set; }
    }

    private sealed record UserInputProjection(
        UserInputRequest Request,
        IReadOnlyDictionary<string, Dictionary<string, string>> OptionLabelsByQuestion);

    private readonly record struct TurnKey(long Generation, string? ThreadId, string TurnId);

    private sealed class ConnectionContext
    {
        private readonly CodexSessionService owner;

        public ConnectionContext(
            CodexSessionService owner,
            IJsonRpcConnection connection,
            long generation,
            string? statePartitionFingerprint,
            long ownerGeneration,
            bool isLocal)
        {
            this.owner = owner;
            Connection = connection;
            Generation = generation;
            StatePartitionFingerprint = statePartitionFingerprint;
            OwnerGeneration = ownerGeneration;
            IsLocal = isLocal;
            NotificationHandler = (message, token) => owner.OnNotificationAsync(this, message, token);
            RequestHandler = (message, token) => owner.OnServerRequestAsync(this, message, token);
            ClosedHandler = (_, _) => owner.OnConnectionClosed(this);
        }

        public IJsonRpcConnection Connection { get; }
        public long Generation { get; }
        public string? StatePartitionFingerprint { get; }
        public long OwnerGeneration { get; }
        public ApprovalGrantStore ApprovalGrants { get; } = new();
        public object UnsupportedMethodsLock { get; } = new();
        public HashSet<string> UnsupportedMethods { get; } = new(StringComparer.Ordinal);
        public bool NotifyPendingResolution { get; set; }
        public bool OwnerInvalidated { get; set; }
        public string? PendingLoginId { get; set; }
        public string? AccountFingerprint { get; set; }
        public bool LogoutRequested { get; set; }
        public bool GatewayOAuthRequired { get; set; }
        public bool GatewayOAuthReady { get; set; }
        public bool GatewayOAuthReadSucceeded { get; set; }
        public bool IsLocal { get; set; }
        public string? GatewayOAuthProviderId { get; set; }
        public string? GatewayOAuthProviderName { get; set; }
        public string? GatewayOAuthMessage { get; set; }
        public string? GatewayOAuthActionId { get; set; }
        public string? GatewayOAuthOriginDisplay { get; set; }
        public string? GatewayLoginAttemptId { get; set; }
        public InteractionAuthState GatewayOAuthState { get; set; } = InteractionAuthState.Checking;
        public object InteractionAuthLock { get; } = new();
        public ConcurrentDictionary<string, McpServerAuthStatus> McpAuthStatuses { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, McpOAuthOperation> McpOAuthOperations { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> McpOAuthOperationsByServer { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, OfferedCommandChoiceSet> CommandChoices { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, PermissionSelectionSet> PermissionSelections { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, McpElicitationForm> McpElicitations { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, UserInputProjection> UserInputProjections { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> AuthorizationActionOwners { get; } = new(StringComparer.Ordinal);
        public Task? GatewayLoginTask { get; set; }
        public CancellationTokenSource Lifetime { get; } = new();
        public Func<JsonRpcMessage, CancellationToken, Task> NotificationHandler { get; }
        public Func<JsonRpcMessage, CancellationToken, Task<JsonElement>> RequestHandler { get; }
        public EventHandler<Exception?> ClosedHandler { get; }

        public void Detach()
        {
            Lifetime.Cancel();
            Connection.NotificationReceived -= NotificationHandler;
            Connection.RequestReceived -= RequestHandler;
            Connection.Closed -= ClosedHandler;
        }
    }

    private readonly record struct OperationCallResult(bool IsSupported, JsonElement Result)
    {
        public static OperationCallResult Unsupported { get; } = new(false, default);
    }
}
