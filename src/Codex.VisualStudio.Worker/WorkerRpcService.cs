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
    private readonly IRemoteConnectionDiagnostics? diagnostics;
    private readonly RemoteStartupLimits limits;
    private readonly RemoteWatchdogTiming watchdogTiming;
    private readonly TimeProvider timeProvider;
    private readonly string workerInstanceId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim connectionTransitionGate = new(1, 1);

    // Canceled first on dispose so an owner-change reconnect cannot delay Worker shutdown.
    private readonly CancellationTokenSource lifetime = new();
    private readonly object targetGate = new();
    private readonly AsyncLocal<ConnectionTargetSnapshot?> processEmissionTarget = new();
    private readonly AsyncLocal<ConnectionTargetSnapshot?> requestEmissionTarget = new();
    private readonly SortedDictionary<long, ConnectionTargetSnapshot> ownerTargetHistory = new();
    // Queued close and watchdog callbacks. They run outside the transition gate and are drained
    // after the gate is released during disposal.
    private readonly List<Task> trackedCallbacks = [];
    private readonly object trackedCallbacksGate = new();
    // Cleared under trackedCallbacksGate when DisposeAsync snapshots the callbacks to drain.
    private bool acceptingCallbacks = true;
    private WorkerOptions? options;
    private JsonRpc? clientRpc;
    private WorkerStatus status = new() { State = WorkerConnectionState.Disconnected, Message = "Worker is disconnected." };
    private AccountStatus accountStatus = new();
    private ConnectionTargetSnapshot target = new() { Kind = ConnectionTargetKind.Local };
    private long connectionGeneration;
    private long ownerGeneration;
    private int networkFailureReported;
    private WorkerRecoveryFailureKind recoveryFailureKind;
    // A remote app-server has no child process, so its loss is observed through the transport's
    // Closed event instead of ICodexProcessHost.Exited.
    private IJsonRpcConnection? observedRemoteConnection;
    // The observed connection whose close is waiting for the transition gate to be published.
    private IJsonRpcConnection? lostRemoteConnection;
    private RemoteIdleWatchdog? watchdog;
    private EventHandler<string>? standardErrorHandler;
    private EventHandler<int>? processExitedHandler;

    public WorkerRpcService(
        ISecretRedactor redactor,
        ICodexProcessHost processHost,
        ICodexSessionService session,
        IRemoteConnectionDiagnostics? diagnostics = null,
        RemoteStartupLimits? limits = null,
        RemoteWatchdogTiming? watchdogTiming = null,
        TimeProvider? timeProvider = null)
    {
        this.redactor = redactor;
        this.processHost = processHost;
        this.session = session;
        this.diagnostics = diagnostics;
        this.limits = limits ?? RemoteStartupLimits.Default;
        this.watchdogTiming = watchdogTiming ?? RemoteWatchdogTiming.Default;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        session.ConversationEventReceived += PublishEventAsync;
        session.ApprovalRequested += PublishApprovalAsync;
        session.ApprovalResolved += PublishApprovalResolvedAsync;
        session.AccountStatusChanged += PublishAccountStatusAsync;
        session.ApprovalAuditRecorded += PublishApprovalAuditAsync;
        session.UserInputRequested += PublishUserInputAsync;
        session.UserInputResolved += PublishUserInputResolvedAsync;
        session.PermissionRequested += PublishPermissionAsync;
        session.PermissionResolved += PublishPermissionResolvedAsync;
        session.McpElicitationRequested += PublishMcpElicitationAsync;
        session.McpElicitationResolved += PublishMcpElicitationResolvedAsync;
        session.UnsupportedInteraction += PublishUnsupportedInteractionAsync;
        session.InteractionAuthStatusChanged += PublishInteractionAuthStatusAsync;
        session.ContextCompacted += PublishContextCompactedAsync;
        session.ReviewModeChanged += PublishReviewModeChangedAsync;
        session.ThreadGoalChanged += PublishThreadGoalChangedAsync;
        session.RateLimitsChanged += PublishRateLimitsChangedAsync;
        session.EffectiveApprovalStateChanged += PublishEffectiveApprovalStateAsync;
        session.SkillsChanged += PublishSkillsChangedAsync;
        session.ThreadAttachmentUpdated += OnThreadAttachmentUpdatedAsync;
        session.WindowsSandboxSetupCompleted += PublishWindowsSandboxSetupChangedAsync;
        session.OwnerInvalidated += OnOwnerInvalidatedAsync;
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

    private async Task<WorkerStatus> ConnectCoreAsync(
        WorkerOptions options,
        CancellationToken cancellationToken,
        string? connectingMessage = null)
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

        if (remote
            && (!LocalPath.TryCreate(options.LocalRoot, out _)
                || !ServerPath.TryCreate(options.ServerRoot, out _)))
        {
            throw new InvalidOperationException("Remote connection roots must be valid absolute local and server paths.");
        }

        this.options = options;
        Interlocked.Exchange(ref networkFailureReported, 0);
        recoveryFailureKind = WorkerRecoveryFailureKind.None;
        StopWatchdog();
        ObserveRemoteConnection(null);
        DetachProcessObservers();

        // Every attempt gets a new generation. Its snapshot reports the intended target while
        // connecting or degraded, and the confirmed target once Ready.
        long generation = Interlocked.Increment(ref connectionGeneration);
        long nextOwnerGeneration = Math.Max(
            Interlocked.Read(ref ownerGeneration),
            session.OwnerGeneration) + 1;
        Interlocked.Exchange(ref ownerGeneration, nextOwnerGeneration);
        session.BeginOwnerPartition(options, workerInstanceId, nextOwnerGeneration, null);
        string statePartitionFingerprint = session.StatePartitionFingerprint
            ?? ConnectionStatePartition.Create(options, workerInstanceId, nextOwnerGeneration, null);
        ConnectionTargetSnapshot newTarget = remote
            ? new ConnectionTargetSnapshot
            {
                Kind = ConnectionTargetKind.Remote,
                DisplayName = options.RemoteProfileName,
                Fingerprint = options.RemoteProfileFingerprint,
                Generation = generation,
                StatePartitionFingerprint = statePartitionFingerprint,
                OwnerGeneration = nextOwnerGeneration,
            }
            : new ConnectionTargetSnapshot
            {
                Kind = ConnectionTargetKind.Local,
                Generation = generation,
                StatePartitionFingerprint = statePartitionFingerprint,
                OwnerGeneration = nextOwnerGeneration,
            };
        lock (targetGate)
        {
            target = newTarget;
            SaveTargetSnapshotLocked();
        }
        await SetStatusAsync(
            WorkerConnectionState.Connecting,
            connectingMessage ?? (remote ? "Connecting to remote codex app-server..." : "Starting codex app-server..."),
            cancellationToken).ConfigureAwait(false);
        return remote
            ? await ConnectRemoteCoreAsync(options, generation, cancellationToken).ConfigureAwait(false)
            : await ConnectLocalCoreAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerStatus> ConnectLocalCoreAsync(WorkerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            WorkerDiagnostics.Write("worker starting codex app-server");
            AttachProcessObservers(SnapshotTarget());
            await processHost.StartAsync(options.CodexPath, options.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            BeginSessionOwnerPartition(options, processHost.CredentialFingerprint);
            WorkerDiagnostics.Write("worker initializing codex app-server");
            await session.InitializeAsync(processHost.Connection!, options, cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write("worker reading account status");
            ConnectionTargetSnapshot accountReadTarget = SnapshotTarget();
            AccountStatus initialAccountStatus = await session.GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
            EnsureSessionStillActive();
            CommitAccountStatus(accountReadTarget, initialAccountStatus);
            WorkerDiagnostics.Write("worker connect completed");
            return await SetStatusAsync(
                WorkerConnectionState.Ready,
                "Connected to codex app-server.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation (including a recovery attempt deadline) stays cancellation so
            // the caller can classify it; the partially started local process is stopped first.
            WorkerDiagnostics.Write("worker connect canceled");
            DetachProcessObservers();
            try
            {
                await processHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stopException)
            {
                WorkerDiagnostics.Write("canceled local connect cleanup failed", stopException);
            }

            await PublishRemoteFailureAsync(
                "The connection attempt was canceled.",
                WorkerRecoveryFailureKind.Cancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("worker connect failed", ex);
            await PublishAccountStatusAsync(
                new AccountStatus { State = AccountState.Unavailable },
                CancellationToken.None).ConfigureAwait(false);
            return await SetStatusAsync(
                WorkerConnectionState.Degraded,
                redactor.Redact(ex.Message),
                cancellationToken,
                ClassifyRecoveryFailure(ex)).ConfigureAwait(false);
        }
    }

    // One monotonic budget covers token read, handshake, initialize, and the startup account
    // read. Each stage is also capped. A failure after the candidate socket exists retires it.
    private async Task<WorkerStatus> ConnectRemoteCoreAsync(
        WorkerOptions options,
        long generation,
        CancellationToken cancellationToken)
    {
        using var overall = new CancellationTokenSource(limits.Overall, timeProvider);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overall.Token);
        RemoteConnectionFailure stageFailure = RemoteConnectionFailure.None;
        try
        {
            // Without both roots, local working-directory and attachment paths would reach the
            // remote server unmapped: they do not exist there and disclose the local layout.
            if (string.IsNullOrWhiteSpace(options.LocalRoot) || string.IsNullOrWhiteSpace(options.ServerRoot))
            {
                throw new InvalidOperationException(
                    "Remote connections require both localRoot and serverRoot. Set both roots in the remote profile, then reconnect.");
            }

            if (string.IsNullOrWhiteSpace(options.RemoteTokenFilePath))
            {
                throw new InvalidOperationException("A token file is required for a remote app-server connection.");
            }

            WorkerDiagnostics.Write("worker connecting to remote codex app-server");
            DetachProcessObservers();
            await processHost.StartRemoteAsync(
                new RemoteConnectionRequest(options.RemoteEndpoint!, options.RemoteTokenFilePath),
                startup.Token).ConfigureAwait(false);
            IJsonRpcConnection connection = processHost.Connection!;
            BeginSessionOwnerPartition(options, processHost.CredentialFingerprint);
            ObserveRemoteConnection(connection);

            WorkerDiagnostics.Write("worker initializing remote codex app-server");
            stageFailure = RemoteConnectionFailure.InitializeFailed;
            await CodexProcessHost.RunStageAsync(
                async stage =>
                {
                    await session.InitializeAsync(connection, options, stage).ConfigureAwait(false);
                    return true;
                },
                limits.Initialize,
                startup.Token).ConfigureAwait(false);

            WorkerDiagnostics.Write("worker reading remote account status");
            stageFailure = RemoteConnectionFailure.AccountReadFailed;
            ConnectionTargetSnapshot accountReadTarget = SnapshotTarget();
            try
            {
                AccountStatus initialAccountStatus = await CodexProcessHost.RunStageAsync(
                    stage => session.GetAccountStatusAsync(stage),
                    limits.AccountRead,
                    startup.Token).ConfigureAwait(false);
                EnsureSessionStillActive();
                CommitAccountStatus(accountReadTarget, initialAccountStatus);
            }
            catch (RemoteConnectionException ex) when (ex.Failure == RemoteConnectionFailure.Timeout)
            {
                // A slow account read alone does not degrade an initialized RPC connection.
                await PublishAccountStatusAsync(
                    new AccountStatus { State = AccountState.Unavailable, Message = "Codex could not read the account status." },
                    CancellationToken.None).ConfigureAwait(false);
            }

            WorkerDiagnostics.Write("worker remote connect completed");
            WorkerStatus ready = await SetStatusAsync(
                WorkerConnectionState.Ready,
                "Connected to remote codex app-server.",
                cancellationToken).ConfigureAwait(false);
            StartWatchdog(connection, generation);
            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation stays cancellation; the candidate is retired first.
            await RetireCandidateAsync().ConfigureAwait(false);
            await PublishRemoteFailureAsync(
                "The remote connection attempt was canceled.",
                WorkerRecoveryFailureKind.Cancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Before the handshake stage the only failures are this method's own fixed
            // validation messages; afterwards the stage decides the category. The overall
            // startup deadline surfaces as a Timeout.
            RemoteConnectionFailure category = ex switch
            {
                RemoteConnectionException remote => remote.Failure,
                OperationCanceledException => RemoteConnectionFailure.Timeout,
                _ => stageFailure,
            };
            WorkerDiagnostics.Write($"worker remote connect failed category={category}", ex);
            string message = category == RemoteConnectionFailure.None
                ? redactor.Redact(ex.Message)
                : RemoteConnectionException.Describe(category);
            await RetireCandidateAsync().ConfigureAwait(false);
            return await PublishRemoteFailureAsync(message, ToRecoveryFailureKind(category)).ConfigureAwait(false);
        }
    }

    // Retires the candidate connection: stop observing it, then dispose it (completing pending
    // client and server requests) before its token lease is released by the host.
    private async Task RetireCandidateAsync()
    {
        StopWatchdog();
        ObserveRemoteConnection(null);
        DetachProcessObservers();
        try
        {
            await processHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("remote candidate cleanup failed", ex);
        }
    }

    private async Task<WorkerStatus> PublishRemoteFailureAsync(
        string message,
        WorkerRecoveryFailureKind failureKind = WorkerRecoveryFailureKind.Unknown)
    {
        await PublishAccountStatusAsync(
            new AccountStatus { State = AccountState.Unavailable },
            CancellationToken.None).ConfigureAwait(false);
        return await SetStatusAsync(WorkerConnectionState.Degraded, message, CancellationToken.None, failureKind).ConfigureAwait(false);
    }

    private void BeginSessionOwnerPartition(WorkerOptions options, string? credentialFingerprint)
    {
        lock (targetGate)
        {
            session.BeginOwnerPartition(options, workerInstanceId, target.OwnerGeneration, credentialFingerprint);
            target.StatePartitionFingerprint = session.StatePartitionFingerprint
                ?? ConnectionStatePartition.Create(options, workerInstanceId, target.OwnerGeneration, credentialFingerprint);
            target.OwnerGeneration = Math.Max(target.OwnerGeneration, session.OwnerGeneration);
            SaveTargetSnapshotLocked();
        }
    }

    private void EnsureSessionStillActive()
    {
        if (!session.IsConnectionActive)
        {
            throw new InvalidOperationException("The account owner changed during connection startup. Reconnect to continue.");
        }
    }

    private Task OnOwnerInvalidatedAsync(IJsonRpcConnection connection, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(processHost.Connection, connection))
        {
            return Task.CompletedTask;
        }

        long expectedGeneration;
        lock (targetGate)
        {
            target.StatePartitionFingerprint = session.StatePartitionFingerprint;
            target.OwnerGeneration = session.OwnerGeneration;
            accountStatus = new AccountStatus
            {
                State = AccountState.Unavailable,
                Message = "The account changed. Reconnect to check the new account.",
            };
            expectedGeneration = target.Generation;
            SaveTargetSnapshotLocked();
        }

        using (SuppressWorkerEmissionContext())
        using (session.SuppressEmissionOwnerContext())
        {
            TrackCallback(RetireInvalidatedOwnerAsync(connection, expectedGeneration));
        }

        return Task.CompletedTask;
    }

    private void AttachProcessObservers(ConnectionTargetSnapshot source)
    {
        DetachProcessObservers();
        ConnectionTargetSnapshot captured = source.Clone();
        standardErrorHandler = (_, text) => TrackCallback(InvokeProcessCallbackAsync(
            captured,
            () => OnStandardErrorReceivedAsync(text)));
        processExitedHandler = (_, exitCode) => TrackCallback(InvokeProcessCallbackAsync(
            captured,
            () => OnProcessExitedAsync(exitCode)));
        processHost.StandardErrorReceived += standardErrorHandler;
        processHost.Exited += processExitedHandler;
    }

    private CallbackScope SuppressWorkerEmissionContext()
    {
        ConnectionTargetSnapshot? previousProcess = processEmissionTarget.Value;
        ConnectionTargetSnapshot? previousRequest = requestEmissionTarget.Value;
        processEmissionTarget.Value = null;
        requestEmissionTarget.Value = null;
        return new CallbackScope(() =>
        {
            processEmissionTarget.Value = previousProcess;
            requestEmissionTarget.Value = previousRequest;
        });
    }

    private void DetachProcessObservers()
    {
        if (standardErrorHandler is not null)
        {
            processHost.StandardErrorReceived -= standardErrorHandler;
            standardErrorHandler = null;
        }

        if (processExitedHandler is not null)
        {
            processHost.Exited -= processExitedHandler;
            processExitedHandler = null;
        }
    }

    private async Task InvokeProcessCallbackAsync(ConnectionTargetSnapshot source, Func<Task> callback)
    {
        ConnectionTargetSnapshot current = SnapshotTarget();
        if (source.Generation != current.Generation
            || source.OwnerGeneration != current.OwnerGeneration
            || !string.Equals(source.StatePartitionFingerprint, current.StatePartitionFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        ConnectionTargetSnapshot? previous = processEmissionTarget.Value;
        processEmissionTarget.Value = source;
        try
        {
            await callback().ConfigureAwait(false);
        }
        finally
        {
            processEmissionTarget.Value = previous;
        }
    }

    private async Task RetireInvalidatedOwnerAsync(IJsonRpcConnection connection, long expectedGeneration)
    {
        // The notification callback may be running on the transport receive loop. Let it unwind
        // before StopAsync disposes and joins that transport.
        await Task.Yield();
        await connectionTransitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (SnapshotTarget().Generation != expectedGeneration || !ReferenceEquals(processHost.Connection, connection))
            {
                return;
            }

            StopWatchdog();
            ObserveRemoteConnection(null);
            DetachProcessObservers();
            try
            {
                await processHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WorkerDiagnostics.Write("owner-change connection cleanup failed", ex);
            }

            // The owner's own logout or sign-in activates a new owner with the same bound options.
            // Only the connect sequence runs; no request of the retired owner is replayed.
            WorkerOptions? bound = options;
            if (session.InvalidatedByOwnerAction && bound is not null)
            {
                WorkerDiagnostics.Write("owner-initiated account change; connecting a new owner");
                try
                {
                    await ConnectCoreAsync(
                        bound,
                        lifetime.Token,
                        "The account changed. Starting a new isolated session...").ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    WorkerDiagnostics.Write("owner-change connect canceled by worker shutdown");
                }
                catch (Exception ex)
                {
                    WorkerDiagnostics.Write("owner-change connect failed", ex);
                    await PublishAccountStatusAsync(
                        new AccountStatus { State = AccountState.Unavailable },
                        CancellationToken.None).ConfigureAwait(false);
                    await SetStatusAsync(
                        WorkerConnectionState.Degraded,
                        redactor.Redact(ex.Message),
                        CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }

            await SetStatusAsync(
                WorkerConnectionState.Degraded,
                "The account changed. Reconnect to establish a new isolated session.",
                CancellationToken.None,
                WorkerRecoveryFailureKind.OwnerChanged).ConfigureAwait(false);
            await PublishAccountStatusAsync(
                new AccountStatus
                {
                    State = session.InvalidatedAccountState ?? AccountState.Unavailable,
                    Message = session.InvalidatedAccountState == AccountState.SignedOut
                        ? null
                        : "The account changed. Reconnect to check the new account.",
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            connectionTransitionGate.Release();
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

        // Restart owns and replaces a local child process. A remote target has none, so the
        // request is refused before anything is stopped or sent.
        if (!string.IsNullOrWhiteSpace(options.RemoteEndpoint))
        {
            throw Rejected(ConnectionOperationRejectionReason.LocalProcessRequired);
        }

        // An intentional stop is not a connection loss; detach before the transport closes.
        ObserveRemoteConnection(null);
        DetachProcessObservers();
        await processHost.StopAsync(cancellationToken).ConfigureAwait(false);
        return await ConnectCoreAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkerStatus> ReconnectAsync(RemoteReconnectRequest request, CancellationToken cancellationToken)
    {
        await connectionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerOptions bound = ValidateReconnect(request);

            // Close only the Worker-owned socket. The external server is never stopped; the token
            // file is read again so rotated contents take effect on this explicit reconnect.
            StopWatchdog();
            ObserveRemoteConnection(null);
            DetachProcessObservers();
            await processHost.StopAsync(cancellationToken).ConfigureAwait(false);
            return await ConnectCoreAsync(bound, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionTransitionGate.Release();
        }
    }

    // Revalidates the immutable request against the options bound to the current generation
    // before any token read, socket stop, or send.
    private WorkerOptions ValidateReconnect(RemoteReconnectRequest request)
    {
        WorkerOptions? bound = options;
        if (bound is null || string.IsNullOrWhiteSpace(bound.RemoteEndpoint))
        {
            throw Rejected(ConnectionOperationRejectionReason.RemoteConnectionRequired);
        }

        if (string.IsNullOrEmpty(bound.RemoteProfileName) || string.IsNullOrEmpty(bound.RemoteProfileFingerprint))
        {
            throw Rejected(ConnectionOperationRejectionReason.ProfileUnavailable);
        }

        string boundFingerprint = RemoteProfileFingerprint.Compute(
            bound.RemoteProfileName,
            bound.RemoteEndpoint,
            bound.RemoteTokenFilePath,
            bound.LocalRoot,
            bound.ServerRoot,
            enabled: true);
        if (!string.Equals(request.ProfileName, bound.RemoteProfileName, StringComparison.Ordinal)
            || !string.Equals(request.Fingerprint, bound.RemoteProfileFingerprint, StringComparison.Ordinal)
            || !string.Equals(boundFingerprint, bound.RemoteProfileFingerprint, StringComparison.Ordinal))
        {
            throw Rejected(ConnectionOperationRejectionReason.ProfileChanged);
        }

        if (request.ExpectedGeneration != Interlocked.Read(ref connectionGeneration))
        {
            throw Rejected(ConnectionOperationRejectionReason.StaleGeneration);
        }

        return bound;
    }

    internal static LocalRpcException Rejected(ConnectionOperationRejectionReason reason)
        => new(reason.ToString())
        {
            ErrorCode = WorkerErrorCodes.ConnectionOperationRejected,
            ErrorData = reason.ToString(),
        };

    private ConnectionTargetSnapshot ValidateOwnerScope(OwnerScopedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionTargetSnapshot snapshot = SnapshotTarget();
        if (request.OwnerGeneration != snapshot.OwnerGeneration
            || request.ConnectionGeneration != snapshot.Generation
            || string.IsNullOrWhiteSpace(request.StatePartitionFingerprint)
            || !string.Equals(
                request.StatePartitionFingerprint,
                snapshot.StatePartitionFingerprint,
                StringComparison.Ordinal))
        {
            throw Rejected(ConnectionOperationRejectionReason.StaleGeneration);
        }

        if (status.State is WorkerConnectionState.Disconnected
            or WorkerConnectionState.Connecting
            or WorkerConnectionState.Degraded)
        {
            throw Rejected(ConnectionOperationRejectionReason.StaleGeneration);
        }

        return snapshot;
    }

    private bool IsOwnerScopeCurrent(OwnerScopedRequest request)
    {
        ConnectionTargetSnapshot snapshot = SnapshotTarget();
        return request.OwnerGeneration == snapshot.OwnerGeneration
            && request.ConnectionGeneration == snapshot.Generation
            && string.Equals(
                request.StatePartitionFingerprint,
                snapshot.StatePartitionFingerprint,
                StringComparison.Ordinal);
    }

    private void CommitAccountStatus(ConnectionTargetSnapshot source, AccountStatus value)
    {
        lock (targetGate)
        {
            if (source.Generation != target.Generation
                || source.OwnerGeneration != target.OwnerGeneration
                || !string.Equals(source.StatePartitionFingerprint, target.StatePartitionFingerprint, StringComparison.Ordinal))
            {
                throw Rejected(ConnectionOperationRejectionReason.StaleGeneration);
            }

            accountStatus = value;
        }
    }

    // The gate is held only while the request owner is validated against the current target, so a
    // transition cannot interleave with the check. It is released before the awaited operation:
    // StartTurn can wait for an approval or user-input answer that arrives through Resolve* calls,
    // and owner retirement and the watchdog need the gate while a long app-server call is pending.
    // Operations revalidate the owner after each await, and the session rejects stale contexts.
    private async Task<ConnectionTargetSnapshot> ValidateOwnerScopeUnderGateAsync(
        OwnerScopedRequest request,
        CancellationToken cancellationToken)
    {
        await connectionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ValidateOwnerScope(request);
        }
        finally
        {
            ReleaseTransitionGate();
        }
    }

    private async Task<T> ExecuteOwnerScopedAsync<T>(
        OwnerScopedRequest request,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ConnectionTargetSnapshot validatedTarget = await ValidateOwnerScopeUnderGateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ConnectionTargetSnapshot? previous = requestEmissionTarget.Value;
        requestEmissionTarget.Value = validatedTarget;
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            requestEmissionTarget.Value = previous;
        }
    }

    private async Task ExecuteOwnerScopedAsync(
        OwnerScopedRequest request,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        ConnectionTargetSnapshot validatedTarget = await ValidateOwnerScopeUnderGateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ConnectionTargetSnapshot? previous = requestEmissionTarget.Value;
        requestEmissionTarget.Value = validatedTarget;
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            requestEmissionTarget.Value = previous;
        }
    }

    public async Task<ConnectionDiagnosticsResult> DiagnoseConnectionAsync(
        ConnectionDiagnosticsRequest request,
        CancellationToken cancellationToken)
    {
        // Diagnosis is independent of the RPC connection: it takes no transition gate, never
        // reads a token, and cannot change Worker connection state.
        if (diagnostics is null)
        {
            return new ConnectionDiagnosticsResult
            {
                ProfileName = request.ProfileName,
                ObservedAt = timeProvider.GetUtcNow(),
                RejectionReason = "Health diagnosis is not available in this Worker.",
            };
        }

        WorkerDiagnostics.Write("worker connection diagnosis requested");
        ConnectionDiagnosticsResult result = await diagnostics.DiagnoseAsync(request, cancellationToken).ConfigureAwait(false);
        WorkerDiagnostics.Write($"worker connection diagnosis completed health={result.Health.State} ready={result.Ready.State}");
        return result;
    }

    public Task<WorkerStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(CloneStatus());

    public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken)
        => Task.FromResult(CloneAccountStatus());

    public Task<StartAccountLoginResult> StartAccountLoginAsync(
        StartAccountLoginRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => StartAccountLoginCoreAsync(request, cancellationToken), cancellationToken);

    private async Task<StartAccountLoginResult> StartAccountLoginCoreAsync(
        StartAccountLoginRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOwnerScope(request);
        WorkerDiagnostics.Write("worker login RPC received");
        try
        {
            StartAccountLoginResult result = await session.StartAccountLoginAsync(cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
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
                ValidateOwnerScope(request);
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
            if (!IsOwnerScopeCurrent(request))
            {
                throw;
            }

            WorkerDiagnostics.Write("worker login RPC failed", ex);
            return await AccountLoginUnavailableAsync(
                "Codex Worker could not start ChatGPT sign-in.",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task<AccountStatus> LogoutAccountAsync(
        LogoutAccountRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => LogoutAccountCoreAsync(request, cancellationToken), cancellationToken);

    private async Task<AccountStatus> LogoutAccountCoreAsync(
        LogoutAccountRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOwnerScope(request);
        WorkerDiagnostics.Write("worker logout RPC received");
        AccountStatus result = await session.LogoutAccountAsync(cancellationToken).ConfigureAwait(false);
        WorkerDiagnostics.Write($"worker logout RPC completed state={result.State}");
        return result;
    }

    public Task<ThreadSummary> StartThreadAsync(StartThreadRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ThreadSummary thread = await session.StartThreadAsync(cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            UpdateSessionIds();
            return thread;
        }, cancellationToken);

    public Task<ThreadSummary> ResumeThreadAsync(ResumeThreadRequest request, CancellationToken cancellationToken)
    {
        if (!request.UserConfirmed)
        {
            throw new InvalidOperationException("Joining a thread requires explicit user confirmation.");
        }

        return ExecuteOwnerScopedAsync(request, async () =>
        {
            ThreadSummary thread = await session.ResumeThreadAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            UpdateSessionIds();
            return thread;
        }, cancellationToken);
    }

    private async Task<T> ExecuteOwnerScopedGenerationAsync<T>(
        OwnerScopedRequest request,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
        where T : WorkerGenerationResult
    {
        ConnectionTargetSnapshot captured = await ValidateOwnerScopeUnderGateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ConnectionTargetSnapshot? previous = requestEmissionTarget.Value;
        requestEmissionTarget.Value = captured;
        try
        {
            T result = await operation().ConfigureAwait(false);
            ConnectionTargetSnapshot current = await ValidateOwnerScopeUnderGateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            result.StatePartitionFingerprint = current.StatePartitionFingerprint;
            result.OwnerGeneration = current.OwnerGeneration;
            result.ConnectionGeneration = current.Generation;
            return result;
        }
        finally
        {
            requestEmissionTarget.Value = previous;
        }
    }

    public Task<ThreadPage> ListThreadsAsync(ListThreadsRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedGenerationAsync(
            request,
            () => session.ListThreadsAsync(request.Cursor, cancellationToken),
            cancellationToken);

    public Task<ThreadReadResult> ReadThreadAsync(ReadThreadRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedGenerationAsync(
            request,
            async () => new ThreadReadResult
            {
                Thread = await session.ReadThreadAsync(request.ThreadId, cancellationToken).ConfigureAwait(false),
            },
            cancellationToken);

    public Task<ThreadTurnsPage> ListThreadTurnsAsync(ListThreadTurnsRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedGenerationAsync(
            request,
            () => session.ListThreadTurnsAsync(request.ThreadId, request.Cursor, request.Limit, cancellationToken),
            cancellationToken);

    public Task<ThreadItemsPage> ListThreadItemsAsync(ListThreadItemsRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedGenerationAsync(
            request,
            () => session.ListThreadItemsAsync(request.ThreadId, request.TurnId, request.Cursor, request.Limit, cancellationToken),
            cancellationToken);

    public Task<ThreadAttachmentsPage> ListThreadAttachmentsAsync(ListThreadAttachmentsRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedGenerationAsync(
            request,
            () => session.ListThreadAttachmentsAsync(request.ThreadId, request.Cursor, request.Limit, cancellationToken),
            cancellationToken);

    public Task<ArtifactActionResult> ResolveArtifactActionAsync(
        ArtifactActionRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ValidateOwnerScope(request);
            ArtifactActionResult result = await session.ResolveArtifactActionAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            return result;
        }, cancellationToken);

    public Task<ShellCommandPrepareResult> PrepareShellCommandAsync(
        ShellCommandPrepareRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ValidateOwnerScope(request);
            ShellCommandPrepareResult result = await session.PrepareShellCommandAsync(
                request,
                SnapshotTarget().DisplayName,
                cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            return result;
        }, cancellationToken);

    public Task<ShellCommandExecuteResult> ExecuteShellCommandAsync(
        ShellCommandExecuteRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ValidateOwnerScope(request);
            ShellCommandExecuteResult result = await session.ExecuteShellCommandAsync(
                request,
                SnapshotTarget().DisplayName,
                cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            return result;
        }, cancellationToken);

    public Task<SavedAttachmentMutationResult> AddSavedAttachmentAsync(
        SavedAttachmentAddRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ValidateOwnerScope(request);
            SavedAttachmentMutationResult result = await session.AddSavedAttachmentAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            return result;
        }, cancellationToken);

    public Task<SavedAttachmentMutationResult> RemoveSavedAttachmentAsync(
        SavedAttachmentRemoveRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ValidateOwnerScope(request);
            SavedAttachmentMutationResult result = await session.RemoveSavedAttachmentAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            return result;
        }, cancellationToken);

    public Task<WindowsSandboxReadinessResult> GetWindowsSandboxReadinessAsync(
        WindowsSandboxReadinessRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ValidateOwnerScope(request);
            WindowsSandboxReadinessResult result = await session.GetWindowsSandboxReadinessAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            return result;
        }, cancellationToken);

    public Task<WindowsSandboxSetupStartResult> StartWindowsSandboxSetupAsync(
        WindowsSandboxSetupStartRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ValidateOwnerScope(request);
            WindowsSandboxSetupStartResult result = await session.StartWindowsSandboxSetupAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            return result;
        }, cancellationToken);

    public Task<ListModelsResult> ListModelsAsync(ListModelsRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => ListModelsCoreAsync(cancellationToken), cancellationToken);

    private async Task<ListModelsResult> ListModelsCoreAsync(CancellationToken cancellationToken)
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

    public Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(
        ListPermissionProfilesRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ListPermissionProfilesAsync(cancellationToken), cancellationToken);

    public Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            await SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken).ConfigureAwait(false);
            string turnId;
            bool turnStartReturned = false;
            try
            {
                ValidateOwnerScope(request);
                turnId = await session.StartTurnAsync(request, cancellationToken).ConfigureAwait(false);
                turnStartReturned = true;
                ValidateOwnerScope(request);
            }
            catch (AttachmentRejectedException ex)
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw new LocalRpcException(ex.Message) { ErrorCode = WorkerErrorCodes.AttachmentRejected };
            }
            catch (SkillInvocationRejectedException ex)
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw new LocalRpcException(ex.Message) { ErrorCode = WorkerErrorCodes.SkillRejected };
            }
            catch (LocalRpcException ex) when (turnStartReturned)
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw new LocalRpcException(redactor.Redact(ex.Message))
                {
                    ErrorCode = WorkerErrorCodes.UpstreamOperationFailed,
                };
            }
            catch (TurnStartOutcomeUnknownException ex)
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw new LocalRpcException(redactor.Redact(ex.InnerException?.Message ?? ex.Message))
                {
                    ErrorCode = WorkerErrorCodes.UpstreamOperationFailed,
                };
            }
            catch (RemoteInvocationException ex)
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw new LocalRpcException(redactor.Redact(ex.Message))
                {
                    ErrorCode = WorkerErrorCodes.PreDispatchRejected,
                };
            }
            catch (OperationCanceledException ex)
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw new LocalRpcException(redactor.Redact(ex.Message))
                {
                    ErrorCode = WorkerErrorCodes.PreDispatchRejected,
                };
            }
            catch (ArgumentException ex)
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw new LocalRpcException(redactor.Redact(ex.Message))
                {
                    ErrorCode = WorkerErrorCodes.PreDispatchRejected,
                };
            }
            catch (InvalidOperationException ex)
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw new LocalRpcException(redactor.Redact(ex.Message))
                {
                    ErrorCode = WorkerErrorCodes.PreDispatchRejected,
                };
            }
            catch
            {
                if (IsOwnerScopeCurrent(request))
                {
                    await SetStatusAsync(WorkerConnectionState.Ready, "Ready.", CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }

            if (session.ActiveTurnId is null)
            {
                await SetStatusAsync(WorkerConnectionState.Ready, "Turn completed.", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken).ConfigureAwait(false);
            }

            return turnId;
        }, cancellationToken);

    public Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.SteerTurnAsync(request, cancellationToken), cancellationToken);

    public Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.InterruptTurnAsync(request, cancellationToken), cancellationToken);

    public Task<CompactThreadResult> CompactThreadAsync(
        CompactThreadRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            CompactThreadResult result = await session.CompactThreadAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            if (result.IsSupported)
            {
                await SetStatusAsync(WorkerConnectionState.Busy, "Context compaction in progress.", cancellationToken).ConfigureAwait(false);
            }

            return result;
        }, cancellationToken);

    public Task<StartReviewResult> StartReviewAsync(
        StartReviewRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            StartReviewResult result = await session.StartReviewAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            if (result.IsSupported)
            {
                await SetStatusAsync(WorkerConnectionState.Busy, "Code review in progress.", cancellationToken).ConfigureAwait(false);
            }

            return result;
        }, cancellationToken);

    public Task<ForkThreadResult> ForkThreadAsync(
        ForkThreadRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            ForkThreadResult result = await session.ForkThreadAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateOwnerScope(request);
            UpdateSessionIds();
            return result;
        }, cancellationToken);

    public Task<ThreadGoalResult> GetThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.GetThreadGoalAsync(request.ThreadId, cancellationToken), cancellationToken);

    public Task<ThreadGoalResult> SetThreadGoalAsync(
        SetThreadGoalRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.SetThreadGoalAsync(request, cancellationToken), cancellationToken);

    public Task<ThreadGoalResult> ClearThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ClearThreadGoalAsync(request.ThreadId, cancellationToken), cancellationToken);

    public Task<McpServerListResult> ListMcpServersAsync(ListMcpServersRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ListMcpServersAsync(request.ThreadId, cancellationToken), cancellationToken);

    public Task<ListSkillsResult> ListSkillsAsync(ListSkillsRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ListSkillsAsync(request.ForceReload, cancellationToken), cancellationToken);

    private Task PublishSkillsChangedAsync(SkillsChangedEvent value, CancellationToken cancellationToken)
        => clientRpc is null
            ? Task.CompletedTask
            : clientRpc.NotifyWithParameterObjectAsync("observer/skillsChanged", new { notification = Stamp(value) });

    private Task OnThreadAttachmentUpdatedAsync(ThreadAttachmentUpdatedEvent value, CancellationToken cancellationToken)
        => clientRpc is null
            ? Task.CompletedTask
            : clientRpc.NotifyWithParameterObjectAsync(
                "observer/threadAttachmentUpdated",
                new { notification = Stamp(value) });

    private Task PublishWindowsSandboxSetupChangedAsync(WindowsSandboxSetupCompletedEvent value, CancellationToken cancellationToken)
        => !IsCurrentEmission() || clientRpc is null
            ? Task.CompletedTask
            : clientRpc.NotifyWithParameterObjectAsync(
                "observer/windowsSandboxSetupChanged",
                new { notification = Stamp(value) });

    public Task<UploadFeedbackResult> UploadFeedbackAsync(
        UploadFeedbackRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.UploadFeedbackAsync(request, cancellationToken), cancellationToken);

    public Task<RateLimitsResult> GetRateLimitsAsync(GetRateLimitsRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.GetRateLimitsAsync(cancellationToken), cancellationToken);

    public Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ResolveApprovalAsync(request, cancellationToken), cancellationToken);

    public Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ResolveUserInputAsync(request, cancellationToken), cancellationToken);

    public Task ResolvePermissionSelectionAsync(ResolvePermissionSelectionRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ResolvePermissionSelectionAsync(request, cancellationToken), cancellationToken);

    public Task ResolveMcpElicitationAsync(ResolveMcpElicitationRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ResolveMcpElicitationAsync(request, cancellationToken), cancellationToken);

    public Task<InteractionAuthStatus> ReadGatewayOAuthAsync(ReadGatewayOAuthRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.ReadGatewayOAuthAsync(cancellationToken), cancellationToken);

    public Task<InteractionAuthStatus> LoginGatewayOAuthAsync(LoginGatewayOAuthRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.LoginGatewayOAuthAsync(cancellationToken), cancellationToken);

    public Task<InteractionAuthStatus> CancelGatewayOAuthAsync(CancelGatewayOAuthRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.CancelGatewayOAuthAsync(cancellationToken), cancellationToken);

    public Task OpenAuthorizationUrlAsync(OpenAuthorizationUrlRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.OpenAuthorizationUrlAsync(request, cancellationToken), cancellationToken);

    public Task<McpOAuthLoginStatus> StartMcpOAuthLoginAsync(StartMcpOAuthLoginRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.StartMcpOAuthLoginAsync(request, cancellationToken), cancellationToken);

    public Task DismissMcpOAuthLoginAsync(DismissMcpOAuthLoginRequest request, CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, () => session.DismissMcpOAuthLoginAsync(request, cancellationToken), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await connectionTransitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopWatchdog();
            ObserveRemoteConnection(null);
            DetachProcessObservers();
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            connectionTransitionGate.Release();
        }

        // Queued close and watchdog callbacks may wait for the gate; drain them only after it is
        // released so cleanup cannot deadlock. They find nothing current and return.
        Task[] callbacks;
        lock (trackedCallbacksGate)
        {
            acceptingCallbacks = false;
            callbacks = [.. trackedCallbacks];
            trackedCallbacks.Clear();
        }

        try
        {
            await Task.WhenAll(callbacks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("worker callback drain failed", ex);
        }

        // Only then dispose the host, which retires the socket and releases its token lease.
        await connectionTransitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await processHost.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            connectionTransitionGate.Release();
        }

        connectionTransitionGate.Dispose();
        lifetime.Dispose();
    }

    private async Task<WorkerStatus> SetStatusAsync(
        WorkerConnectionState state,
        string message,
        CancellationToken cancellationToken,
        WorkerRecoveryFailureKind failureKind = WorkerRecoveryFailureKind.None)
    {
        recoveryFailureKind = state == WorkerConnectionState.Degraded
            ? failureKind
            : WorkerRecoveryFailureKind.None;
        ConnectionTargetSnapshot targetSnapshot;
        lock (targetGate)
        {
            if (!IsCurrentEmissionLocked(target))
            {
                return CloneStatus();
            }

            if (session.OwnerGeneration >= target.OwnerGeneration
                && session.StatePartitionFingerprint is { } ownerPartition)
            {
                target.StatePartitionFingerprint = ownerPartition;
                target.OwnerGeneration = session.OwnerGeneration;
            }

            targetSnapshot = target.Clone();
            status = new WorkerStatus
            {
                State = state,
                Message = message,
                ThreadId = session.ActiveThreadId,
                TurnId = session.ActiveTurnId,
                ProcessId = targetSnapshot.Kind == ConnectionTargetKind.Local ? processHost.ProcessId : null,
                CodexVersion = ShouldIncludeCodexVersion(state) ? session.CodexVersion : null,
                EffectiveApprovalState = session.EffectiveApprovalState,
                EffectiveReasoningEffort = session.EffectiveReasoningEffort,
                EffectiveServiceTier = session.EffectiveServiceTier,
                EffectiveModelId = session.EffectiveModelId,
                Target = targetSnapshot,
                RecoveryFailureKind = recoveryFailureKind,
                GatewayOAuthRequired = session.GatewayOAuthRequired,
            };
        }
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync(
                "observer/stateChanged",
                new { notification = Stamp(CloneStatus(), targetSnapshot, useEmissionOwner: false) }).ConfigureAwait(false);
        }

        return CloneStatus();
    }

    private WorkerNotification<T> Stamp<T>(T value) => Stamp(value, SnapshotTarget());

    private bool IsCurrentEmission()
    {
        ConnectionTargetSnapshot snapshot = SnapshotTarget();
        return IsCurrentEmissionLocked(snapshot);
    }

    private bool IsCurrentEmissionLocked(ConnectionTargetSnapshot snapshot)
    {
        ConnectionTargetSnapshot? processSource = processEmissionTarget.Value;
        if (processSource is not null)
        {
            return processSource.Generation == snapshot.Generation
                && processSource.OwnerGeneration == snapshot.OwnerGeneration
                && string.Equals(
                    processSource.StatePartitionFingerprint,
                    snapshot.StatePartitionFingerprint,
                    StringComparison.Ordinal);
        }

        ConnectionTargetSnapshot? requestSource = requestEmissionTarget.Value;
        if (requestSource is not null)
        {
            return requestSource.Generation == snapshot.Generation
                && requestSource.OwnerGeneration == snapshot.OwnerGeneration
                && string.Equals(
                    requestSource.StatePartitionFingerprint,
                    snapshot.StatePartitionFingerprint,
                    StringComparison.Ordinal);
        }

        return session.EmittingOwnerGeneration == snapshot.OwnerGeneration
            && string.Equals(
                session.EmittingStatePartitionFingerprint,
                snapshot.StatePartitionFingerprint,
                StringComparison.Ordinal);
    }

    private WorkerNotification<T> Stamp<T>(
        T value,
        ConnectionTargetSnapshot snapshot,
        bool useEmissionOwner = true)
    {
        ConnectionTargetSnapshot? processSource = useEmissionOwner ? processEmissionTarget.Value : null;
        ConnectionTargetSnapshot? requestSource = useEmissionOwner ? requestEmissionTarget.Value : null;
        long owner = useEmissionOwner
            ? processSource?.OwnerGeneration ?? requestSource?.OwnerGeneration ?? session.EmittingOwnerGeneration
            : snapshot.OwnerGeneration;
        string? partition = useEmissionOwner
            ? processSource?.StatePartitionFingerprint
                ?? requestSource?.StatePartitionFingerprint
                ?? session.EmittingStatePartitionFingerprint
                ?? snapshot.StatePartitionFingerprint
            : snapshot.StatePartitionFingerprint;
        if (useEmissionOwner && processSource is null && requestSource is null)
        {
            lock (targetGate)
            {
                if (ownerTargetHistory.TryGetValue(owner, out ConnectionTargetSnapshot? sourceTarget))
                {
                    snapshot = sourceTarget.Clone();
                }
            }
        }
        else if (processSource is not null)
        {
            snapshot = processSource.Clone();
        }
        else if (requestSource is not null)
        {
            snapshot = requestSource.Clone();
        }
        if (value is WorkerStatus workerStatus)
        {
            workerStatus.Target = snapshot.Clone();
            workerStatus.Target.StatePartitionFingerprint = partition;
            workerStatus.Target.OwnerGeneration = owner;
        }

        return new WorkerNotification<T>
        {
            Value = value,
            StatePartitionFingerprint = partition,
            OwnerGeneration = owner,
            ConnectionGeneration = snapshot.Generation,
        };
    }

    private ConnectionTargetSnapshot SnapshotTarget()
    {
        lock (targetGate)
        {
            return target.Clone();
        }
    }

    private void SaveTargetSnapshotLocked()
    {
        ownerTargetHistory[target.OwnerGeneration] = target.Clone();
        while (ownerTargetHistory.Count > 8)
        {
            ownerTargetHistory.Remove(ownerTargetHistory.Keys.First());
        }
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
                CancellationToken.None,
                WorkerRecoveryFailureKind.ServerUnavailable).ConfigureAwait(false);
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
        if (!IsCurrentEmission())
        {
            return;
        }

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
                new { notification = Stamp(conversationEvent) }).ConfigureAwait(false);
        }
    }

    private async Task PublishEffectiveApprovalStateAsync(
        EffectiveApprovalState _,
        CancellationToken cancellationToken)
        => await SetStatusAsync(status.State, status.Message, cancellationToken).ConfigureAwait(false);

    private async Task PublishApprovalAsync(ApprovalRequest approval, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        await SetStatusAsync(WorkerConnectionState.WaitingForApproval, "Waiting for approval.", cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/approvalRequested", new { notification = Stamp(approval) }).ConfigureAwait(false);
        }
    }

    private async Task PublishApprovalResolvedAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        await SetStatusAfterInteractionResolvedAsync(cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/approvalResolved", new { notification = Stamp(requestId) }).ConfigureAwait(false);
        }
    }

    private async Task PublishUserInputAsync(UserInputRequest request, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        await SetStatusAsync(WorkerConnectionState.WaitingForApproval, "Waiting for input.", cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/userInputRequested", new { notification = Stamp(request) }).ConfigureAwait(false);
        }
    }

    private async Task PublishUserInputResolvedAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        await SetStatusAfterInteractionResolvedAsync(cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/userInputResolved", new { notification = Stamp(requestId) }).ConfigureAwait(false);
        }
    }

    // Independent interaction cards resolve one at a time; stay in WaitingForApproval until the
    // last pending interaction of the current generation is resolved, timed out, or retired.
    private Task<WorkerStatus> SetStatusAfterInteractionResolvedAsync(CancellationToken cancellationToken)
        => session.PendingInteractionCount > 0
            ? SetStatusAsync(WorkerConnectionState.WaitingForApproval, "Waiting for approval.", cancellationToken)
            : SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken);

    private async Task PublishPermissionAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        await SetStatusAsync(WorkerConnectionState.WaitingForApproval, "Waiting for permission selection.", cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/permissionRequested", new { notification = Stamp(request) }).ConfigureAwait(false);
        }
    }

    private async Task PublishPermissionResolvedAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        await SetStatusAfterInteractionResolvedAsync(cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/permissionResolved", new { notification = Stamp(requestId) }).ConfigureAwait(false);
        }
    }

    private async Task PublishMcpElicitationAsync(McpElicitationRequest request, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        if (request.Kind == McpElicitationKind.Form)
        {
            await SetStatusAsync(WorkerConnectionState.WaitingForApproval, "Waiting for MCP input.", cancellationToken).ConfigureAwait(false);
        }

        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/mcpElicitationRequested", new { notification = Stamp(request) }).ConfigureAwait(false);
        }
    }

    private async Task PublishMcpElicitationResolvedAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        await SetStatusAfterInteractionResolvedAsync(cancellationToken).ConfigureAwait(false);
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/mcpElicitationResolved", new { notification = Stamp(requestId) }).ConfigureAwait(false);
        }
    }

    private async Task PublishUnsupportedInteractionAsync(UnsupportedInteractionNotice notice, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission() || clientRpc is null)
        {
            return;
        }

        notice.Message = redactor.Redact(notice.Message);
        notice.ServerName = redactor.Redact(notice.ServerName);
        await clientRpc.NotifyWithParameterObjectAsync("observer/unsupportedInteraction", new { notification = Stamp(notice) }).ConfigureAwait(false);
    }

    private async Task PublishInteractionAuthStatusAsync(InteractionAuthStatus authStatus, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/interactionAuthStatusChanged", new { notification = Stamp(authStatus) }).ConfigureAwait(false);
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
            await clientRpc.NotifyWithParameterObjectAsync("observer/contextCompacted", new { notification = Stamp(value) }).ConfigureAwait(false);
        }
    }

    private async Task PublishReviewModeChangedAsync(ReviewModeEvent value, CancellationToken cancellationToken)
    {
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/reviewModeChanged", new { notification = Stamp(value) }).ConfigureAwait(false);
        }
    }

    private async Task PublishThreadGoalChangedAsync(ThreadGoalEvent value, CancellationToken cancellationToken)
    {
        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/threadGoalChanged", new { notification = Stamp(value) }).ConfigureAwait(false);
        }
    }

    private async Task PublishRateLimitsChangedAsync(RateLimitsResult value, CancellationToken cancellationToken)
    {
        if (!IsCurrentEmission())
        {
            return;
        }

        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/rateLimitsChanged", new { notification = Stamp(value) }).ConfigureAwait(false);
        }
    }

    private async Task PublishAccountStatusAsync(AccountStatus value, CancellationToken cancellationToken)
    {
        lock (targetGate)
        {
            if (!IsCurrentEmissionLocked(target))
            {
                return;
            }

            accountStatus = value;
        }

        if (clientRpc is not null)
        {
            try
            {
                await clientRpc.NotifyWithParameterObjectAsync("observer/accountChanged", new { notification = Stamp(value) }).ConfigureAwait(false);
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
        if (!IsCurrentEmission())
        {
            return;
        }

        if (clientRpc is not null)
        {
            await clientRpc.NotifyWithParameterObjectAsync("observer/approvalAudit", new { notification = Stamp(record) }).ConfigureAwait(false);
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

        // Admit the handler before logging. DisposeAsync drains every admitted handler before it
        // disposes the host and releases the token lease; once disposal has taken its snapshot the
        // handler is suppressed, so the exception is never logged after the lease is gone.
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryTrackCallback(handled.Task))
        {
            return;
        }

        Task lost = Task.CompletedTask;
        try
        {
            // Produce the diagnostic and the categorical status text now, while the token lease is
            // still held, so the queued publication carries no raw exception text.
            WorkerDiagnostics.Write("remote codex app-server connection closed", exception);
            lost = OnRemoteConnectionLostAsync(connection);
        }
        finally
        {
            lost.ContinueWith(
                static (completed, state) =>
                {
                    var source = (TaskCompletionSource)state!;
                    if (completed.Exception is { } failure)
                    {
                        source.TrySetException(failure.InnerExceptions);
                    }
                    else
                    {
                        source.TrySetResult();
                    }
                },
                handled,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task OnRemoteConnectionLostAsync(IJsonRpcConnection connection)
    {

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

            StopWatchdog();
            await PublishRemoteFailureAsync(
                RemoteConnectionException.Describe(RemoteConnectionFailure.ConnectionLost),
                WorkerRecoveryFailureKind.TransportClosed).ConfigureAwait(false);
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

    private async Task OnProcessExitedAsync(int exitCode)
    {
        await PublishAccountStatusAsync(
            new AccountStatus { State = AccountState.Unavailable },
            CancellationToken.None).ConfigureAwait(false);
        await SetStatusAsync(
            WorkerConnectionState.Degraded,
            $"codex app-server exited with code {exitCode}.",
            CancellationToken.None,
            WorkerRecoveryFailureKind.WorkerProcessExit).ConfigureAwait(false);
    }

    private void UpdateSessionIds()
    {
        status.ThreadId = session.ActiveThreadId;
        status.TurnId = session.ActiveTurnId;
        status.ProcessId = OwnedProcessId();
        status.CodexVersion = ShouldIncludeCodexVersion(status.State) ? session.CodexVersion : null;
        status.EffectiveApprovalState = session.EffectiveApprovalState;
        status.EffectiveReasoningEffort = session.EffectiveReasoningEffort;
        status.EffectiveServiceTier = session.EffectiveServiceTier;
        status.GatewayOAuthRequired = session.GatewayOAuthRequired;
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
        EffectiveModelId = status.EffectiveModelId,
        Target = status.Target?.Clone(),
        RecoveryFailureKind = status.RecoveryFailureKind,
        GatewayOAuthRequired = status.GatewayOAuthRequired,
    };

    private static WorkerRecoveryFailureKind ClassifyRecoveryFailure(Exception exception)
        => exception switch
        {
            RemoteConnectionException remote => ToRecoveryFailureKind(remote.Failure),
            OperationCanceledException => WorkerRecoveryFailureKind.Cancelled,
            _ => WorkerRecoveryFailureKind.Unknown,
        };

    private static WorkerRecoveryFailureKind ToRecoveryFailureKind(RemoteConnectionFailure failure)
        => failure switch
        {
            RemoteConnectionFailure.ProfileChanged or RemoteConnectionFailure.ProfileUnavailable
                => WorkerRecoveryFailureKind.ProfileChanged,
            RemoteConnectionFailure.InvalidEndpoint or RemoteConnectionFailure.TokenFileMissing
                or RemoteConnectionFailure.TokenFileUnreadable or RemoteConnectionFailure.TokenFileInvalid
                or RemoteConnectionFailure.UpgradeRejected or RemoteConnectionFailure.InitializeFailed
                => WorkerRecoveryFailureKind.ConfigurationChanged,
            RemoteConnectionFailure.AuthenticationRejected => WorkerRecoveryFailureKind.AuthenticationRejected,
            RemoteConnectionFailure.CertificateRejected => WorkerRecoveryFailureKind.TlsRejected,
            RemoteConnectionFailure.NetworkFailure or RemoteConnectionFailure.Timeout
                or RemoteConnectionFailure.AccountReadFailed or RemoteConnectionFailure.ConnectionLost
                => WorkerRecoveryFailureKind.ServerUnavailable,
            RemoteConnectionFailure.PeerUnresponsive => WorkerRecoveryFailureKind.PeerUnresponsive,
            _ => WorkerRecoveryFailureKind.Unknown,
        };

    // A PID is reported only for a Worker-owned local process, never for a remote target.
    private int? OwnedProcessId() => SnapshotTarget().Kind == ConnectionTargetKind.Local ? processHost.ProcessId : null;

    private void TrackCallback(Task callback) => TryTrackCallback(callback);

    // False once DisposeAsync has snapshotted the callbacks; the caller must then do nothing that
    // depends on the token lease or the host.
    private bool TryTrackCallback(Task callback)
    {
        lock (trackedCallbacksGate)
        {
            if (!acceptingCallbacks)
            {
                return false;
            }

            trackedCallbacks.RemoveAll(static task => task.IsCompleted);
            trackedCallbacks.Add(callback);
            return true;
        }
    }

    // Always called under connectionTransitionGate.
    private void StartWatchdog(IJsonRpcConnection connection, long generation)
    {
        if (connection is not IInboundActivitySource activity)
        {
            return;
        }

        var started = new RemoteIdleWatchdog(
            connection,
            activity,
            watchdogTiming,
            timeProvider,
            (isStillSilent, cancellationToken) => OnRemotePeerUnresponsiveAsync(connection, generation, isStillSilent, cancellationToken));
        watchdog = started;
        started.Start();
        TrackCallback(started.Completion);
    }

    // Always called under connectionTransitionGate. Cancels without awaiting; the watchdog may be
    // waiting for the gate the caller holds. DisposeAsync drains it after releasing the gate.
    private void StopWatchdog()
    {
        Interlocked.Exchange(ref watchdog, null)?.Cancel();
    }

    // Returns true when the watchdog should keep watching because inbound activity arrived after
    // the probes; false once the socket is retired or the generation was superseded.
    private async Task<bool> OnRemotePeerUnresponsiveAsync(
        IJsonRpcConnection connection,
        long generation,
        Func<bool> isStillSilent,
        CancellationToken cancellationToken)
    {
        await connectionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only the captured socket of the still-current generation is retired. A newer
            // connect, reconnect, or restart supersedes this result.
            if (Interlocked.Read(ref connectionGeneration) != generation
                || !ReferenceEquals(Volatile.Read(ref observedRemoteConnection), connection)
                || !ReferenceEquals(processHost.Connection, connection))
            {
                return false;
            }

            // Revalidated under the gate: a message that arrived between or after the probes
            // proves the peer is alive, so the idle window restarts instead.
            if (!isStillSilent())
            {
                return true;
            }

            WorkerDiagnostics.Write("remote codex app-server did not answer two liveness probes");
            watchdog = null;
            ObserveRemoteConnection(null);
            DetachProcessObservers();
            await processHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishRemoteFailureAsync(
                RemoteConnectionException.Describe(RemoteConnectionFailure.PeerUnresponsive),
                WorkerRecoveryFailureKind.PeerUnresponsive).ConfigureAwait(false);
            return false;
        }
        finally
        {
            ReleaseTransitionGate();
        }
    }

    private AccountStatus CloneAccountStatus() => new()
    {
        State = accountStatus.State,
        PlanType = accountStatus.PlanType,
        Message = accountStatus.Message,
    };
}
