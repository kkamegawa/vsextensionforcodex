using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using StreamJsonRpc;
using System.Diagnostics;

namespace Codex.VisualStudio.Worker;

public sealed class WorkerRpcService : ICodexWorkerClient, IAsyncDisposable
{
    private readonly ISecretRedactor redactor;
    private readonly ICodexProcessHost processHost;
    private readonly ICodexSessionService session;
    private readonly SemaphoreSlim connectionTransitionGate = new(1, 1);
    private WorkerOptions? options;
    private JsonRpc? clientRpc;
    private WorkerStatus status = new() { State = WorkerConnectionState.Disconnected, Message = "Worker is disconnected." };
    private AccountStatus accountStatus = new();
    private int networkFailureReported;
    // A remote app-server has no child process, so its loss is observed through the transport's
    // Closed event instead of ICodexProcessHost.Exited.
    private IJsonRpcConnection? observedRemoteConnection;
    // The observed connection whose close is waiting for the transition gate to be published.
    private IJsonRpcConnection? lostRemoteConnection;

    public WorkerRpcService(ISecretRedactor redactor, ICodexProcessHost processHost, ICodexSessionService session)
    {
        this.redactor = redactor;
        this.processHost = processHost;
        this.session = session;
        processHost.StandardErrorReceived += (_, text) => _ = OnStandardErrorReceivedAsync(text);
        processHost.Exited += (_, exitCode) => _ = OnProcessExitedAsync(exitCode);
        session.ConversationEventReceived += PublishEventAsync;
        session.ApprovalRequested += PublishApprovalAsync;
        session.ApprovalResolved += PublishApprovalResolvedAsync;
        session.AccountStatusChanged += PublishAccountStatusAsync;
        session.ApprovalAuditRecorded += PublishApprovalAuditAsync;
        session.UserInputRequested += PublishUserInputAsync;
        session.UserInputResolved += PublishUserInputResolvedAsync;
        session.ContextCompacted += PublishContextCompactedAsync;
        session.ReviewModeChanged += PublishReviewModeChangedAsync;
        session.ThreadGoalChanged += PublishThreadGoalChangedAsync;
        session.RateLimitsChanged += PublishRateLimitsChangedAsync;
        session.EffectiveApprovalStateChanged += PublishEffectiveApprovalStateAsync;
        session.SkillsChanged += PublishSkillsChangedAsync;
    }

    public void AttachClient(JsonRpc rpc)
    {
        clientRpc = rpc;
    }

    public async Task<WorkerStatus> ConnectAsync(WorkerOptions options, CancellationToken cancellationToken)
    {
        await connectionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConnectCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionTransitionGate.Release();
        }
    }

    private async Task<WorkerStatus> ConnectCoreAsync(WorkerOptions options, CancellationToken cancellationToken)
    {
        WorkerDiagnostics.Write("worker connect RPC received");
        if (options.ContractVersion != ContractVersions.Current)
        {
            throw new InvalidOperationException($"Unsupported contract version {options.ContractVersion}.");
        }

        bool remote = !string.IsNullOrWhiteSpace(options.RemoteEndpoint);
        bool hasLocalRoot = !string.IsNullOrWhiteSpace(options.LocalRoot);
        bool hasServerRoot = !string.IsNullOrWhiteSpace(options.ServerRoot);
        if (hasLocalRoot != hasServerRoot)
        {
            throw new InvalidOperationException("Remote path mapping requires both localRoot and serverRoot.");
        }

        this.options = options;
        Interlocked.Exchange(ref networkFailureReported, 0);
        ObserveRemoteConnection(null);
        await SetStatusAsync(
            WorkerConnectionState.Connecting,
            remote ? "Connecting to remote codex app-server..." : "Starting codex app-server...",
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (remote)
            {
                // Without both roots, local working-directory and attachment paths would reach the
                // remote server unmapped: they do not exist there and disclose the local layout.
                if (!hasLocalRoot || !hasServerRoot)
                {
                    throw new InvalidOperationException(
                        "Remote connections require both localRoot and serverRoot. Set both roots in the remote profile, then reconnect.");
                }

                if (string.IsNullOrWhiteSpace(options.RemoteTokenFilePath))
                {
                    throw new InvalidOperationException("A token file is required for a remote app-server connection.");
                }

                WorkerDiagnostics.Write("worker connecting to remote codex app-server");
                await processHost.StartRemoteAsync(
                    options.RemoteEndpoint!,
                    options.RemoteTokenFilePath,
                    cancellationToken).ConfigureAwait(false);
                ObserveRemoteConnection(processHost.Connection);
            }
            else
            {
                WorkerDiagnostics.Write("worker starting codex app-server");
                await processHost.StartAsync(options.CodexPath, options.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            }
            WorkerDiagnostics.Write("worker initializing codex app-server");
            await session.InitializeAsync(processHost.Connection!, options, cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write("worker reading account status");
            accountStatus = await session.GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write("worker connect completed");
            return await SetStatusAsync(
                WorkerConnectionState.Ready,
                remote ? "Connected to remote codex app-server." : "Connected to codex app-server.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("worker connect failed", ex);
            await PublishAccountStatusAsync(
                new AccountStatus { State = AccountState.Unavailable },
                CancellationToken.None).ConfigureAwait(false);
            return await SetStatusAsync(WorkerConnectionState.Degraded, redactor.Redact(ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<WorkerStatus> RestartAsync(CancellationToken cancellationToken)
    {
        await connectionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RestartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionTransitionGate.Release();
        }
    }

    private async Task<WorkerStatus> RestartCoreAsync(CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new InvalidOperationException("Connect must be called before restart.");
        }

        // An intentional stop is not a connection loss; detach before the transport closes.
        ObserveRemoteConnection(null);
        await processHost.StopAsync(cancellationToken).ConfigureAwait(false);
        return await ConnectCoreAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkerStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(CloneStatus());

    public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken)
        => Task.FromResult(CloneAccountStatus());

    public async Task<StartAccountLoginResult> StartAccountLoginAsync(CancellationToken cancellationToken)
    {
        WorkerDiagnostics.Write("worker login RPC received");
        try
        {
            StartAccountLoginResult result = await session.StartAccountLoginAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status.State != AccountState.SigningIn)
            {
                WorkerDiagnostics.Write($"worker login RPC completed state={result.Status.State}");
                return result;
            }

            try
            {
                WorkerDiagnostics.Write("default browser launch starting");
                Process.Start(new ProcessStartInfo
                {
                    FileName = result.AuthUrl!,
                    UseShellExecute = true,
                });
                WorkerDiagnostics.Write("default browser launch requested");
                return result;
            }
            catch (Exception ex)
            {
                WorkerDiagnostics.Write("default browser launch failed", ex);
                return await AccountLoginUnavailableAsync(
                    "Could not open the default browser.",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("worker login RPC failed", ex);
            return await AccountLoginUnavailableAsync(
                "Codex Worker could not start ChatGPT sign-in.",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<AccountStatus> LogoutAccountAsync(CancellationToken cancellationToken)
    {
        WorkerDiagnostics.Write("worker logout RPC received");
        AccountStatus result = await session.LogoutAccountAsync(cancellationToken).ConfigureAwait(false);
        WorkerDiagnostics.Write($"worker logout RPC completed state={result.State}");
        return result;
    }

    public async Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken)
    {
        ThreadSummary thread = await session.StartThreadAsync(cancellationToken).ConfigureAwait(false);
        UpdateSessionIds();
        return thread;
    }

    public async Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        ThreadSummary thread = await session.ResumeThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        UpdateSessionIds();
        return thread;
    }

    public Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken)
        => session.ListThreadsAsync(cursor, cancellationToken);

    public async Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        WorkerDiagnostics.Write("worker/models/list RPC received");
        try
        {
            ListModelsResult result = await session.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write($"worker/models/list RPC completed count={result.Models.Count}");
            return result;
        }
        catch (OperationCanceledException ex)
        {
            WorkerDiagnostics.Write("worker/models/list RPC canceled", ex);
            throw;
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("worker/models/list RPC failed", ex);
            throw;
        }
    }

    public Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(CancellationToken cancellationToken)
        => session.ListPermissionProfilesAsync(cancellationToken);

    public async Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
    {
        await SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken).ConfigureAwait(false);
        string turnId;
        try
        {
            turnId = await session.StartTurnAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AttachmentRejectedException ex)
        {
            await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
            throw new LocalRpcException(ex.Message) { ErrorCode = WorkerErrorCodes.AttachmentRejected };
        }
        catch
        {
            await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // The server may complete the turn before the turn/start response arrives. Publish the
        // post-response session state so a late response cannot leave the Worker Busy forever.
        if (session.ActiveTurnId is null)
        {
            await SetStatusAsync(WorkerConnectionState.Ready, "Turn completed.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken).ConfigureAwait(false);
        }
        return turnId;
    }

    public Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken)
        => session.SteerTurnAsync(request, cancellationToken);

    public Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken)
        => session.InterruptTurnAsync(request, cancellationToken);

    public async Task<CompactThreadResult> CompactThreadAsync(
        CompactThreadRequest request,
        CancellationToken cancellationToken)
    {
        CompactThreadResult result = await session.CompactThreadAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsSupported)
        {
            await SetStatusAsync(WorkerConnectionState.Busy, "Context compaction in progress.", cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<StartReviewResult> StartReviewAsync(
        StartReviewRequest request,
        CancellationToken cancellationToken)
    {
        StartReviewResult result = await session.StartReviewAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsSupported)
        {
            await SetStatusAsync(WorkerConnectionState.Busy, "Code review in progress.", cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<ForkThreadResult> ForkThreadAsync(
        ForkThreadRequest request,
        CancellationToken cancellationToken)
    {
        ForkThreadResult result = await session.ForkThreadAsync(request, cancellationToken).ConfigureAwait(false);
        UpdateSessionIds();
        return result;
    }

    public Task<ThreadGoalResult> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken)
        => session.GetThreadGoalAsync(threadId, cancellationToken);

    public Task<ThreadGoalResult> SetThreadGoalAsync(
        SetThreadGoalRequest request,
        CancellationToken cancellationToken)
        => session.SetThreadGoalAsync(request, cancellationToken);

    public Task<ThreadGoalResult> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken)
        => session.ClearThreadGoalAsync(threadId, cancellationToken);

    public Task<McpServerListResult> ListMcpServersAsync(string? threadId, CancellationToken cancellationToken)
        => session.ListMcpServersAsync(threadId, cancellationToken);

    public Task<ListSkillsResult> ListSkillsAsync(bool forceReload, CancellationToken cancellationToken)
        => session.ListSkillsAsync(forceReload, cancellationToken);

    private Task PublishSkillsChangedAsync(SkillsChangedEvent value, CancellationToken cancellationToken)
        => clientRpc is null
            ? Task.CompletedTask
            : clientRpc.NotifyWithParameterObjectAsync("observer/skillsChanged", new { value });

    public Task<UploadFeedbackResult> UploadFeedbackAsync(
        UploadFeedbackRequest request,
        CancellationToken cancellationToken)
        => session.UploadFeedbackAsync(request, cancellationToken);

    public Task<RateLimitsResult> GetRateLimitsAsync(CancellationToken cancellationToken)
        => session.GetRateLimitsAsync(cancellationToken);

    public Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
        => session.ResolveApprovalAsync(request, cancellationToken);

    public Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
        => session.ResolveUserInputAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await connectionTransitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObserveRemoteConnection(null);
            await session.DisposeAsync().ConfigureAwait(false);
            await processHost.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            connectionTransitionGate.Release();
            connectionTransitionGate.Dispose();
        }
    }

    private async Task<WorkerStatus> SetStatusAsync(WorkerConnectionState state, string message, CancellationToken cancellationToken)
    {
        status = new WorkerStatus
        {
            State = state,
            Message = message,
            ThreadId = session.ActiveThreadId,
            TurnId = session.ActiveTurnId,
            ProcessId = processHost.ProcessId,
            CodexVersion = ShouldIncludeCodexVersion(state) ? session.CodexVersion : null,
            EffectiveApprovalState = session.EffectiveApprovalState,
            EffectiveReasoningEffort = session.EffectiveReasoningEffort,
            EffectiveServiceTier = session.EffectiveServiceTier,
        };
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/stateChanged", new { status }).ConfigureAwait(false);
        }

        return CloneStatus();
    }

    private async Task OnStandardErrorReceivedAsync(string text)
    {
        // Forward the raw (already redacted) line to the transcript and output
        // log, preserving existing behavior.
        await PublishEventAsync(
            new ConversationEvent { Kind = ConversationEventKind.Error, Text = text },
            CancellationToken.None).ConfigureAwait(false);

        // On the first network/DNS failure, surface a single actionable message
        // and mark the connection degraded instead of flooding the transcript
        // with repeated low-level errors.
        if (CodexErrorClassifier.IsNetworkFailure(text)
            && Interlocked.CompareExchange(ref networkFailureReported, 1, 0) == 0)
        {
            await SetStatusAsync(
                WorkerConnectionState.Degraded,
                CodexErrorClassifier.NetworkFailureMessage,
                CancellationToken.None).ConfigureAwait(false);
            await PublishEventAsync(
                new ConversationEvent
                {
                    Kind = ConversationEventKind.Error,
                    Text = CodexErrorClassifier.NetworkFailureMessage,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PublishEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
    {
        if (conversationEvent.Kind == ConversationEventKind.TurnStarted)
        {
            await SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken).ConfigureAwait(false);
        }
        else if (conversationEvent.Kind == ConversationEventKind.TurnCompleted)
        {
            await SetStatusAsync(WorkerConnectionState.Ready, "Turn completed.", cancellationToken).ConfigureAwait(false);
        }
        else if (conversationEvent.Kind == ConversationEventKind.Error)
        {
            conversationEvent.Text = redactor.Redact(conversationEvent.Text);
        }

        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync(
                "observer/conversationEvent",
                new { conversationEvent }).ConfigureAwait(false);
        }
    }

    private async Task PublishEffectiveApprovalStateAsync(
        EffectiveApprovalState _,
        CancellationToken cancellationToken)
        => await SetStatusAsync(status.State, status.Message, cancellationToken).ConfigureAwait(false);

    private async Task PublishApprovalAsync(ApprovalRequest approval, CancellationToken cancellationToken)
    {
        await SetStatusAsync(WorkerConnectionState.WaitingForApproval, "Waiting for approval.", cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/approvalRequested", new { approval }).ConfigureAwait(false);
        }
    }

    private async Task PublishApprovalResolvedAsync(string requestId, CancellationToken cancellationToken)
    {
        await SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/approvalResolved", new { requestId }).ConfigureAwait(false);
        }
    }

    private async Task PublishUserInputAsync(UserInputRequest request, CancellationToken cancellationToken)
    {
        await SetStatusAsync(WorkerConnectionState.WaitingForApproval, "Waiting for input.", cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/userInputRequested", new { request }).ConfigureAwait(false);
        }
    }

    private async Task PublishUserInputResolvedAsync(string requestId, CancellationToken cancellationToken)
    {
        await SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/userInputResolved", new { requestId }).ConfigureAwait(false);
        }
    }

    private async Task PublishContextCompactedAsync(ContextCompactionEvent value, CancellationToken cancellationToken)
    {
        // thread/compact/start marks the worker Busy, but the app-server may report completion
        // only through this event instead of turn/completed. Restore Ready when no turn is
        // active so queued slash commands are not blocked behind a finished compaction.
        if (value.IsCompleted
            && status.State == WorkerConnectionState.Busy
            && session.ActiveTurnId is null)
        {
            await SetStatusAsync(WorkerConnectionState.Ready, "Context compaction completed.", cancellationToken).ConfigureAwait(false);
        }

        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/contextCompacted", new { value }).ConfigureAwait(false);
        }
    }

    private async Task PublishReviewModeChangedAsync(ReviewModeEvent value, CancellationToken cancellationToken)
    {
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/reviewModeChanged", new { value }).ConfigureAwait(false);
        }
    }

    private async Task PublishThreadGoalChangedAsync(ThreadGoalEvent value, CancellationToken cancellationToken)
    {
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/threadGoalChanged", new { value }).ConfigureAwait(false);
        }
    }

    private async Task PublishRateLimitsChangedAsync(RateLimitsResult value, CancellationToken cancellationToken)
    {
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/rateLimitsChanged", new { value }).ConfigureAwait(false);
        }
    }

    private async Task PublishAccountStatusAsync(AccountStatus value, CancellationToken cancellationToken)
    {
        accountStatus = value;
        if (clientRpc is not null)
        {
            try
            {
                await clientRpc.NotifyWithParameterObjectAsync("observer/accountChanged", new { status = value }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                WorkerDiagnostics.Write("account status observer notification failed", ex);
                // Account notifications are advisory and must never break chat or sign-in RPCs.
            }
        }
    }

    private async Task<StartAccountLoginResult> AccountLoginUnavailableAsync(string message, CancellationToken cancellationToken)
    {
        var unavailable = new AccountStatus
        {
            State = AccountState.Unavailable,
            Message = message,
        };
        await PublishAccountStatusAsync(unavailable, cancellationToken).ConfigureAwait(false);
        return new StartAccountLoginResult { Status = unavailable };
    }

    private async Task PublishApprovalAuditAsync(ApprovalAuditRecord record, CancellationToken cancellationToken)
    {
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/approvalAudit", new { record }).ConfigureAwait(false);
        }
    }

    // Always called under connectionTransitionGate (connect, restart, dispose).
    private void ObserveRemoteConnection(IJsonRpcConnection? connection)
    {
        IJsonRpcConnection? previous = Interlocked.Exchange(ref observedRemoteConnection, connection);

        // A newer transition supersedes any loss that has not been published yet.
        Volatile.Write(ref lostRemoteConnection, null);
        if (previous is not null)
        {
            previous.Closed -= OnRemoteConnectionClosed;
        }

        if (connection is not null)
        {
            connection.Closed += OnRemoteConnectionClosed;
        }
    }

    private void OnRemoteConnectionClosed(object? sender, Exception? exception)
    {
        if (sender is not IJsonRpcConnection connection)
        {
            return;
        }

        // Mark the loss before claiming the connection. If a transition swaps the observed
        // connection in between, the claim fails; if it runs after the claim, it clears the mark.
        Volatile.Write(ref lostRemoteConnection, connection);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref observedRemoteConnection, null, connection), connection))
        {
            Interlocked.CompareExchange(ref lostRemoteConnection, null, connection);
            return;
        }

        connection.Closed -= OnRemoteConnectionClosed;
        _ = OnRemoteConnectionLostAsync(connection, exception);
    }

    private async Task OnRemoteConnectionLostAsync(IJsonRpcConnection connection, Exception? exception)
    {
        WorkerDiagnostics.Write("remote codex app-server connection closed", exception);

        // Publish under the transition gate so the loss cannot overwrite the status of a connect,
        // restart, or dispose that started after the close; each of those clears the mark first.
        try
        {
            await connectionTransitionGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(Interlocked.CompareExchange(ref lostRemoteConnection, null, connection), connection))
            {
                return;
            }

            await PublishRemoteConnectionLossAsync(exception).ConfigureAwait(false);
        }
        finally
        {
            ReleaseTransitionGate();
        }
    }

    private void ReleaseTransitionGate()
    {
        try
        {
            connectionTransitionGate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task PublishRemoteConnectionLossAsync(Exception? exception)
    {
        string message = exception is null
            ? "The remote codex app-server connection closed. Reconnect to continue."
            : $"The remote codex app-server connection was lost: {redactor.Redact(exception.Message)} Reconnect to continue.";
        await PublishAccountStatusAsync(
            new AccountStatus { State = AccountState.Unavailable },
            CancellationToken.None).ConfigureAwait(false);
        await SetStatusAsync(WorkerConnectionState.Degraded, message, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task OnProcessExitedAsync(int exitCode)
    {
        await PublishAccountStatusAsync(
            new AccountStatus { State = AccountState.Unavailable },
            CancellationToken.None).ConfigureAwait(false);
        await SetStatusAsync(
            WorkerConnectionState.Degraded,
            $"codex app-server exited with code {exitCode}.",
            CancellationToken.None).ConfigureAwait(false);
    }

    private void UpdateSessionIds()
    {
        status.ThreadId = session.ActiveThreadId;
        status.TurnId = session.ActiveTurnId;
        status.ProcessId = processHost.ProcessId;
        status.CodexVersion = ShouldIncludeCodexVersion(status.State) ? session.CodexVersion : null;
        status.EffectiveApprovalState = session.EffectiveApprovalState;
        status.EffectiveReasoningEffort = session.EffectiveReasoningEffort;
        status.EffectiveServiceTier = session.EffectiveServiceTier;
    }

    private static bool ShouldIncludeCodexVersion(WorkerConnectionState state)
        => state is WorkerConnectionState.Ready
            or WorkerConnectionState.Busy
            or WorkerConnectionState.WaitingForApproval;

    private WorkerStatus CloneStatus() => new()
    {
        State = status.State,
        Message = status.Message,
        ThreadId = status.ThreadId,
        TurnId = status.TurnId,
        ProcessId = status.ProcessId,
        CodexVersion = status.CodexVersion,
        EffectiveApprovalState = status.EffectiveApprovalState,
        EffectiveReasoningEffort = status.EffectiveReasoningEffort,
        EffectiveServiceTier = status.EffectiveServiceTier,
    };

    private AccountStatus CloneAccountStatus() => new()
    {
        State = accountStatus.State,
        PlanType = accountStatus.PlanType,
        Message = accountStatus.Message,
    };
}
