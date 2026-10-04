using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility.Documents;
using StreamJsonRpc;

namespace Codex.VisualStudio.Extension;

internal interface IWorkerBridge : IAsyncDisposable
{
    event Action<WorkerRecoveryFailureKind>? ConnectionLost;

    event Func<WorkerNotification<WorkerStatus>, Task>? StateChanged;

    event Func<WorkerNotification<AccountStatus>, Task>? AccountChanged;

    event Func<WorkerNotification<ConversationEvent>, Task>? ConversationEventReceived;

    event Func<WorkerNotification<ThreadAttachmentUpdatedEvent>, Task>? ThreadAttachmentUpdated;

    event Func<WorkerNotification<ApprovalRequest>, Task>? ApprovalRequested;

    event Func<WorkerNotification<string>, Task>? ApprovalResolved;

    event Func<WorkerNotification<UserInputRequest>, Task>? UserInputRequested;

    event Func<WorkerNotification<string>, Task>? UserInputResolved;

    event Func<WorkerNotification<ContextCompactionEvent>, Task>? ContextCompacted;

    event Func<WorkerNotification<ReviewModeEvent>, Task>? ReviewModeChanged;

    event Func<WorkerNotification<ThreadGoalEvent>, Task>? ThreadGoalChanged;

    event Func<WorkerNotification<RateLimitsResult>, Task>? RateLimitsChanged;

    event Func<WorkerNotification<SkillsChangedEvent>, Task>? SkillsChanged;

    event Func<WorkerNotification<ApprovalAuditRecord>, Task>? ApprovalAuditReceived;

    Task<WorkerStatus> ConnectAsync(string workingDirectory, bool experimentalApi, CancellationToken cancellationToken);

    Task<WorkerStatus> RestartAsync(CancellationToken cancellationToken);

    Task<WorkerStatus> ReconnectAsync(RemoteReconnectRequest request, CancellationToken cancellationToken);

    Task<ConnectionDiagnosticsResult> DiagnoseConnectionAsync(ConnectionDiagnosticsRequest request, CancellationToken cancellationToken);

    Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken);

    Task<StartAccountLoginResult> StartAccountLoginAsync(StartAccountLoginRequest request, CancellationToken cancellationToken);

    Task<AccountStatus> LogoutAccountAsync(LogoutAccountRequest request, CancellationToken cancellationToken);

    Task<ThreadPage> ListThreadsAsync(ListThreadsRequest request, CancellationToken cancellationToken);

    Task<ThreadReadResult> ReadThreadAsync(ReadThreadRequest request, CancellationToken cancellationToken);

    Task<ThreadTurnsPage> ListThreadTurnsAsync(ListThreadTurnsRequest request, CancellationToken cancellationToken);

    Task<ThreadItemsPage> ListThreadItemsAsync(ListThreadItemsRequest request, CancellationToken cancellationToken);

    Task<ThreadAttachmentsPage> ListThreadAttachmentsAsync(ListThreadAttachmentsRequest request, CancellationToken cancellationToken);

    Task<ListModelsResult> ListModelsAsync(ListModelsRequest request, CancellationToken cancellationToken);

    Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(ListPermissionProfilesRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ListPermissionProfilesResult
        {
            IsSupported = false,
            UnavailableReason = "Permission profiles are not available through this bridge.",
        });

    Task<ThreadSummary> StartThreadAsync(StartThreadRequest request, CancellationToken cancellationToken);

    Task<ThreadSummary> ResumeThreadAsync(ResumeThreadRequest request, CancellationToken cancellationToken);

    Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken);

    Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken);

    Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken);

    Task<CompactThreadResult> CompactThreadAsync(CompactThreadRequest request, CancellationToken cancellationToken);

    Task<StartReviewResult> StartReviewAsync(StartReviewRequest request, CancellationToken cancellationToken);

    Task<ForkThreadResult> ForkThreadAsync(ForkThreadRequest request, CancellationToken cancellationToken);

    Task<ThreadGoalResult> GetThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken);

    Task<ThreadGoalResult> SetThreadGoalAsync(SetThreadGoalRequest request, CancellationToken cancellationToken);

    Task<ThreadGoalResult> ClearThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken);

    Task<McpServerListResult> ListMcpServersAsync(ListMcpServersRequest request, CancellationToken cancellationToken);

    Task<ListSkillsResult> ListSkillsAsync(ListSkillsRequest request, CancellationToken cancellationToken);

    Task<UploadFeedbackResult> UploadFeedbackAsync(UploadFeedbackRequest request, CancellationToken cancellationToken);

    Task<RateLimitsResult> GetRateLimitsAsync(GetRateLimitsRequest request, CancellationToken cancellationToken);

    Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken);

    Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken);
}

public sealed class WorkerBridge : IWorkerBridge, ICodexWorkerObserver
{
    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(20);

    private readonly OutputChannel? log;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private Process? process;
    private NamedPipeClientStream? pipe;
    private JsonRpc? rpc;
    private ProcessJobObject? jobObject;
    private CancellationTokenSource? diagnosticsCancellation;
    private Task? diagnosticsTask;
    private int disposed;
    private int stopping;
    private int connectionLossReported;

    public WorkerBridge(OutputChannel? outputChannel = null)
    {
        log = outputChannel;
    }

    public event Action<WorkerRecoveryFailureKind>? ConnectionLost;

    public event Func<WorkerNotification<WorkerStatus>, Task>? StateChanged;

    public event Func<WorkerNotification<AccountStatus>, Task>? AccountChanged;

    public event Func<WorkerNotification<ConversationEvent>, Task>? ConversationEventReceived;

    public event Func<WorkerNotification<ThreadAttachmentUpdatedEvent>, Task>? ThreadAttachmentUpdated;

    public event Func<WorkerNotification<ApprovalRequest>, Task>? ApprovalRequested;

    public event Func<WorkerNotification<string>, Task>? ApprovalResolved;

    public event Func<WorkerNotification<UserInputRequest>, Task>? UserInputRequested;

    public event Func<WorkerNotification<string>, Task>? UserInputResolved;

    public event Func<WorkerNotification<ContextCompactionEvent>, Task>? ContextCompacted;

    public event Func<WorkerNotification<ReviewModeEvent>, Task>? ReviewModeChanged;

    public event Func<WorkerNotification<ThreadGoalEvent>, Task>? ThreadGoalChanged;

    public event Func<WorkerNotification<RateLimitsResult>, Task>? RateLimitsChanged;

    public event Func<WorkerNotification<SkillsChangedEvent>, Task>? SkillsChanged;

    public event Func<WorkerNotification<ApprovalAuditRecord>, Task>? ApprovalAuditReceived;

    public async Task<WorkerStatus> ConnectAsync(string workingDirectory, bool experimentalApi, CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("Worker connect invocation starting");
        await EnsureWorkerStartedAsync(cancellationToken).ConfigureAwait(false);

        ExtensionSettings settings = ExtensionSettings.Load();
        string[] duplicateNames = settings.RemoteProfiles
            .Where(static profile => profile.Enabled)
            .GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateNames.Length > 0)
        {
            throw new InvalidOperationException("Enabled remote profile names must be unique.");
        }

        RemoteConnectionProfile? remote = settings.RemoteProfiles
            .FirstOrDefault(profile => profile.Enabled
                && string.Equals(profile.Name, settings.SelectedRemoteProfileName, StringComparison.Ordinal));

        WorkerStatus result = await RequireRpc().InvokeWithCancellationAsync<WorkerStatus>(
            "worker/connect",
            new object[]
            {
                new WorkerOptions
                {
                    CodexPath = "codex",
                    WorkingDirectory = workingDirectory,
                    ExtensionVersion = "0.1.0",
                    ExperimentalApi = experimentalApi,
                    RemoteEndpoint = remote?.Endpoint,
                    RemoteTokenFilePath = remote?.TokenFilePath,
                    LocalRoot = remote?.LocalRoot,
                    ServerRoot = remote?.ServerRoot,
                    RemoteProfileName = remote?.Name,
                    RemoteProfileFingerprint = remote?.ComputeFingerprint(),
                },
            },
            cancellationToken).ConfigureAwait(false);
        ExtensionDiagnostics.Write($"Worker connect invocation completed state={result.State}");
        return result;
    }

    public Task<WorkerStatus> RestartAsync(CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<WorkerStatus>("worker/restart", Array.Empty<object>(), cancellationToken);

    public Task<WorkerStatus> ReconnectAsync(RemoteReconnectRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<WorkerStatus>("worker/reconnect", new object[] { request }, cancellationToken);

    // Health diagnosis is available before the first connection, so it starts the Worker if
    // needed. It never connects the app-server or reads a token.
    public async Task<ConnectionDiagnosticsResult> DiagnoseConnectionAsync(
        ConnectionDiagnosticsRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureWorkerStartedAsync(cancellationToken).ConfigureAwait(false);
        return await RequireRpc().InvokeWithCancellationAsync<ConnectionDiagnosticsResult>(
            "worker/connection/diagnose",
            new object[] { request },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<AccountStatus>("worker/account/status", Array.Empty<object>(), cancellationToken);

    public async Task<StartAccountLoginResult> StartAccountLoginAsync(StartAccountLoginRequest request, CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("worker/account/login/start invocation starting");
        try
        {
            StartAccountLoginResult result = await RequireRpc().InvokeWithCancellationAsync<StartAccountLoginResult>(
                "worker/account/login/start",
                new object[] { request },
                cancellationToken).ConfigureAwait(false);
            ExtensionDiagnostics.Write($"worker/account/login/start invocation completed state={result.Status.State}");
            return result;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("worker/account/login/start invocation failed", ex);
            throw;
        }
    }

    public async Task<AccountStatus> LogoutAccountAsync(LogoutAccountRequest request, CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("worker/account/logout invocation starting");
        try
        {
            AccountStatus result = await RequireRpc().InvokeWithCancellationAsync<AccountStatus>(
                "worker/account/logout",
                new object[] { request },
                cancellationToken).ConfigureAwait(false);
            ExtensionDiagnostics.Write($"worker/account/logout invocation completed state={result.State}");
            return result;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("worker/account/logout invocation failed", ex);
            throw;
        }
    }

    public Task<ThreadPage> ListThreadsAsync(ListThreadsRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<ThreadPage>("worker/thread/list", new object[] { request }, cancellationToken);

    public Task<ThreadReadResult> ReadThreadAsync(ReadThreadRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadReadResult>(
            "worker/thread/read",
            new object[] { request },
            cancellationToken);

    public Task<ThreadTurnsPage> ListThreadTurnsAsync(ListThreadTurnsRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadTurnsPage>(
            "worker/thread/turns/list",
            new object[] { request },
            cancellationToken);

    public Task<ThreadItemsPage> ListThreadItemsAsync(ListThreadItemsRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadItemsPage>(
            "worker/thread/items/list",
            new object[] { request },
            cancellationToken);

    public Task<ThreadAttachmentsPage> ListThreadAttachmentsAsync(ListThreadAttachmentsRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadAttachmentsPage>(
            "worker/thread/attachments/list",
            new object[] { request },
            cancellationToken);

    public async Task<ListModelsResult> ListModelsAsync(ListModelsRequest request, CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("worker/models/list invocation starting");
        using var timeout = new CancellationTokenSource(ModelListTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            ListModelsResult result = await RequireRpc().InvokeWithCancellationAsync<ListModelsResult>(
                "worker/models/list",
                new object[] { request },
                linked.Token).ConfigureAwait(false);
            ExtensionDiagnostics.Write($"worker/models/list invocation completed count={result.Models.Count}");
            return result;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ExtensionDiagnostics.Write("worker/models/list invocation timed out", ex);
            throw new TimeoutException("The Codex Worker did not complete model discovery in time.", ex);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            ExtensionDiagnostics.Write("worker/models/list invocation canceled", ex);
            throw;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("worker/models/list invocation failed", ex);
            throw;
        }
    }

    public Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(ListPermissionProfilesRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ListPermissionProfilesResult>(
            "worker/permissionProfiles/list",
            new object[] { request },
            cancellationToken);

    public Task<ThreadSummary> StartThreadAsync(StartThreadRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<ThreadSummary>("worker/thread/start", new object[] { request }, cancellationToken);

    public Task<ThreadSummary> ResumeThreadAsync(ResumeThreadRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<ThreadSummary>("worker/thread/resume", new object[] { request }, cancellationToken);

    public Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<string>("worker/turn/start", new object[] { request }, cancellationToken);

    public Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<string>("worker/turn/steer", new object[] { request }, cancellationToken);

    public Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync("worker/turn/interrupt", new object[] { request }, cancellationToken);

    public Task<CompactThreadResult> CompactThreadAsync(CompactThreadRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<CompactThreadResult>(
            "worker/thread/compact",
            new object[] { request },
            cancellationToken);

    public Task<StartReviewResult> StartReviewAsync(StartReviewRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<StartReviewResult>(
            "worker/review/start",
            new object[] { request },
            cancellationToken);

    public Task<ListSkillsResult> ListSkillsAsync(ListSkillsRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ListSkillsResult>(
            "worker/skills/list",
            new object[] { request },
            cancellationToken);

    public Task<ForkThreadResult> ForkThreadAsync(ForkThreadRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ForkThreadResult>(
            "worker/thread/fork",
            new object[] { request },
            cancellationToken);

    public Task<ThreadGoalResult> GetThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadGoalResult>(
            "worker/thread/goal/get",
            new object[] { request },
            cancellationToken);

    public Task<ThreadGoalResult> SetThreadGoalAsync(SetThreadGoalRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadGoalResult>(
            "worker/thread/goal/set",
            new object[] { request },
            cancellationToken);

    public Task<ThreadGoalResult> ClearThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadGoalResult>(
            "worker/thread/goal/clear",
            new object[] { request },
            cancellationToken);

    public Task<McpServerListResult> ListMcpServersAsync(ListMcpServersRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<McpServerListResult>(
            "worker/mcp/list",
            new object[] { request },
            cancellationToken);

    public Task<UploadFeedbackResult> UploadFeedbackAsync(UploadFeedbackRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<UploadFeedbackResult>(
            "worker/feedback/upload",
            new object[] { request },
            cancellationToken);

    public Task<RateLimitsResult> GetRateLimitsAsync(GetRateLimitsRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<RateLimitsResult>(
            "worker/account/rateLimits",
            new object[] { request },
            cancellationToken);

    public Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync("worker/approval/resolve", new object[] { request }, cancellationToken);

    public Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync("worker/userInput/resolve", new object[] { request }, cancellationToken);

    public Task OnStateChangedAsync(WorkerNotification<WorkerStatus> notification, CancellationToken cancellationToken)
        => StateChanged?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnAccountChangedAsync(WorkerNotification<AccountStatus> notification, CancellationToken cancellationToken)
        => AccountChanged?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnConversationEventAsync(WorkerNotification<ConversationEvent> notification, CancellationToken cancellationToken)
        => ConversationEventReceived?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnThreadAttachmentUpdatedAsync(
        WorkerNotification<ThreadAttachmentUpdatedEvent> notification,
        CancellationToken cancellationToken)
        => ThreadAttachmentUpdated?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnApprovalRequestedAsync(WorkerNotification<ApprovalRequest> notification, CancellationToken cancellationToken)
    {
        return ApprovalRequested?.Invoke(notification) ?? Task.CompletedTask;
    }

    public Task OnApprovalResolvedAsync(WorkerNotification<string> notification, CancellationToken cancellationToken)
        => ApprovalResolved?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnUserInputRequestedAsync(WorkerNotification<UserInputRequest> notification, CancellationToken cancellationToken)
        => UserInputRequested?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnUserInputResolvedAsync(WorkerNotification<string> notification, CancellationToken cancellationToken)
        => UserInputResolved?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnContextCompactedAsync(WorkerNotification<ContextCompactionEvent> notification, CancellationToken cancellationToken)
        => ContextCompacted?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnReviewModeChangedAsync(WorkerNotification<ReviewModeEvent> notification, CancellationToken cancellationToken)
        => ReviewModeChanged?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnThreadGoalChangedAsync(WorkerNotification<ThreadGoalEvent> notification, CancellationToken cancellationToken)
        => ThreadGoalChanged?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnRateLimitsChangedAsync(WorkerNotification<RateLimitsResult> notification, CancellationToken cancellationToken)
        => RateLimitsChanged?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnSkillsChangedAsync(WorkerNotification<SkillsChangedEvent> notification, CancellationToken cancellationToken)
        => SkillsChanged?.Invoke(notification) ?? Task.CompletedTask;

    public Task OnApprovalAuditAsync(WorkerNotification<ApprovalAuditRecord> notification, CancellationToken cancellationToken)
        => ApprovalAuditReceived?.Invoke(notification) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopWorkerCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task StopWorkerCoreAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        CancellationTokenSource? diagnosticsLifetime = Interlocked.Exchange(ref diagnosticsCancellation, null);
        Task? diagnosticsReader = Interlocked.Exchange(ref diagnosticsTask, null);
        JsonRpc? workerRpc = Interlocked.Exchange(ref rpc, null);
        NamedPipeClientStream? workerPipe = Interlocked.Exchange(ref pipe, null);
        Process? workerProcess = Interlocked.Exchange(ref process, null);
        ProcessJobObject? workerJobObject = Interlocked.Exchange(ref jobObject, null);

        try
        {
            if (diagnosticsLifetime is not null)
            {
                await diagnosticsLifetime.CancelAsync().ConfigureAwait(false);
            }

            if (workerRpc is not null)
            {
                workerRpc.Disconnected -= OnRpcDisconnected;
                await Task.Run(workerRpc.Dispose).ConfigureAwait(false);
            }

            if (workerPipe is not null)
            {
                await workerPipe.DisposeAsync().ConfigureAwait(false);
            }
            if (workerProcess is not null)
            {
                workerProcess.Exited -= OnWorkerProcessExited;
                try
                {
                    if (!workerProcess.HasExited)
                    {
                        // Kill the worker together with its descendants (codex app-server and any
                        // cmd.exe processes it spawned) on the normal shutdown path. The job object
                        // assigned in StartWorkerAsync is the safety net for abnormal termination.
                        workerProcess.Kill(entireProcessTree: true);
                        await Task.Run(workerProcess.WaitForExit).ConfigureAwait(false);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    // The process exited between the HasExited check and Kill/WaitForExit.
                    ExtensionDiagnostics.Write("Worker process already exited during disposal", ex);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Kill failed (e.g. the process was already terminating). Disposal must
                    // continue so the process handle and job object are still released.
                    ExtensionDiagnostics.Write("Failed to kill worker process during disposal", ex);
                }
            }

            if (diagnosticsReader is not null)
            {
                await diagnosticsReader.ConfigureAwait(false);
            }
        }
        finally
        {
            diagnosticsLifetime?.Dispose();
            workerProcess?.Dispose();
            workerJobObject?.Dispose();
        }
    }

    private async Task EnsureWorkerStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (IsWorkerUsable())
        {
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (IsWorkerUsable())
            {
                return;
            }

            if (rpc is not null)
            {
                // The Worker process exited or its RPC transport closed. Retire the dead
                // transport so an explicit Connect starts a new Worker instead of invoking
                // the stale proxy.
                ExtensionDiagnostics.Write("Retiring stale Worker transport before restart");
                await StopWorkerCoreAsync().ConfigureAwait(false);
            }

            await StartWorkerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interlocked.Exchange(ref stopping, 0);
        Interlocked.Exchange(ref connectionLossReported, 0);
        string pipeName = $"Kkamegawa.CodexForVisualStudio.{Guid.NewGuid():N}";
        string assemblyDirectory = Path.GetDirectoryName(typeof(WorkerBridge).Assembly.Location) ?? string.Empty;
        ProcessStartInfo startInfo = CreateWorkerStartInfo(
            assemblyDirectory,
            pipeName,
            RuntimeEnvironment.GetRuntimeDirectory());
        ExtensionDiagnostics.Write($"Worker start requested launcher={Path.GetFileName(startInfo.FileName)} exists={File.Exists(startInfo.FileName)}");
        process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the Codex worker.");
        process.EnableRaisingEvents = true;
        process.Exited += OnWorkerProcessExited;
        ExtensionDiagnostics.Write($"Worker process started pid={process.Id}");

        // Assign the worker (and, implicitly, every descendant process it spawns - codex
        // app-server and any cmd.exe processes) to a job object with
        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. This guarantees the whole process tree is
        // terminated when the extension process exits or is force-closed, even if the
        // graceful shutdown in DisposeAsync never runs.
        jobObject = ProcessJobObject.CreateKillOnCloseJob();
        if (jobObject is not null && !jobObject.Assign(process))
        {
            ExtensionDiagnostics.Write("Failed to assign worker process to job object");
            jobObject.Dispose();
            jobObject = null;
        }
        diagnosticsCancellation = new CancellationTokenSource();
        diagnosticsTask = ReadWorkerDiagnosticsAsync(process, diagnosticsCancellation.Token);
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.Run(() => pipe.Connect(15_000), cancellationToken).ConfigureAwait(false);
            ExtensionDiagnostics.Write("Worker pipe connected");
            rpc = new JsonRpc(pipe);
            rpc.AddLocalRpcTarget<ICodexWorkerObserver>(this, null);
            rpc.Disconnected += OnRpcDisconnected;
            rpc.StartListening();
            ExtensionDiagnostics.Write("Worker RPC listening");
        }
        catch (Exception ex)
        {
            // Connecting to the worker or starting RPC failed after the process (and its
            // job object) were already created. Tear down the partially-started worker so
            // it doesn't linger, then surface the failure to the caller.
            ExtensionDiagnostics.Write("Worker pipe/RPC setup failed; disposing partially-started worker", ex);
            await StopWorkerCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo CreateWorkerStartInfo(
        string assemblyDirectory,
        string pipeName,
        string runtimeDirectory)
    {
        string workerDirectory = Path.Combine(assemblyDirectory, "Worker");
        string workerDll = Path.Combine(workerDirectory, "Codex.VisualStudio.Worker.dll");
        string workerAppHost = Path.Combine(workerDirectory, "Codex.VisualStudio.Worker.exe");
        string dotnetHost = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", "dotnet.exe"));
        bool useDotnetHost = File.Exists(dotnetHost) && File.Exists(workerDll);
        var startInfo = new ProcessStartInfo
        {
            FileName = useDotnetHost ? dotnetHost : workerAppHost,
            UseShellExecute = false,

            // CREATE_NO_WINDOW gives the worker a console that has no window at all (rather
            // than allocating a visible console and hiding it afterwards, which can flash or
            // linger if the hide runs late). codex app-server - and the cmd.exe processes it
            // spawns to run shell commands - inherit this windowless console, so the OS never
            // allocates a new visible console window for any of them. Codex.VisualStudio.Worker
            // also hides its console window at startup as a defensive measure (see HiddenConsole).
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
        };
        if (useDotnetHost)
        {
            startInfo.ArgumentList.Add(workerDll);
        }

        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        return startInfo;
    }

    private async Task ReadWorkerDiagnosticsAsync(Process source, CancellationToken cancellationToken)
    {
        try
        {
            while (await source.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExtensionDiagnostics.WriteOutputAsync(log, ExtensionDiagnostics.Sanitize(line)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ExtensionDiagnostics.Write("Worker diagnostics stream canceled");
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Worker diagnostics stream ended", ex);
        }
    }

    private bool IsWorkerUsable()
        => rpc is { } current
            && !current.Completion.IsCompleted
            && Volatile.Read(ref connectionLossReported) == 0;

    private JsonRpc RequireRpc()
        => rpc ?? throw new InvalidOperationException("The Codex Worker RPC connection is unavailable.");

    private void OnWorkerProcessExited(object? sender, EventArgs eventArgs)
        => ReportConnectionLost(WorkerRecoveryFailureKind.WorkerProcessExit);

    private void OnRpcDisconnected(object? sender, EventArgs eventArgs)
        => ReportConnectionLost(WorkerRecoveryFailureKind.TransportClosed);

    private void ReportConnectionLost(WorkerRecoveryFailureKind failureKind)
    {
        if (Volatile.Read(ref stopping) != 0 || Interlocked.Exchange(ref connectionLossReported, 1) != 0)
        {
            return;
        }

        ExtensionDiagnostics.Write($"Worker connection lost kind={failureKind}");
        try
        {
            ConnectionLost?.Invoke(failureKind);
        }
        catch (Exception)
        {
            // Connection recovery is best effort; an event consumer must not escape onto
            // the process or JSON-RPC callback thread.
            ExtensionDiagnostics.Write("Worker connection-loss event handler failed");
        }
    }
}
