using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility.Documents;
using StreamJsonRpc;

namespace Codex.VisualStudio.Extension;

internal interface IWorkerBridge : IAsyncDisposable
{
    event Func<WorkerStatus, Task>? StateChanged;

    event Func<AccountStatus, Task>? AccountChanged;

    event Func<ConversationEvent, Task>? ConversationEventReceived;

    event Func<ApprovalRequest, Task>? ApprovalRequested;

    event Func<string, Task>? ApprovalResolved;

    event Func<UserInputRequest, Task>? UserInputRequested;

    event Func<string, Task>? UserInputResolved;

    event Func<ContextCompactionEvent, Task>? ContextCompacted;

    event Func<ReviewModeEvent, Task>? ReviewModeChanged;

    event Func<ThreadGoalEvent, Task>? ThreadGoalChanged;

    event Func<RateLimitsResult, Task>? RateLimitsChanged;

    event Func<Task>? SkillsChanged;

    Task<WorkerStatus> ConnectAsync(string workingDirectory, bool experimentalApi, CancellationToken cancellationToken);

    Task<WorkerStatus> RestartAsync(CancellationToken cancellationToken);

    Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken);

    Task<StartAccountLoginResult> StartAccountLoginAsync(CancellationToken cancellationToken);

    Task<AccountStatus> LogoutAccountAsync(CancellationToken cancellationToken);

    Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken);

    Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken);

    Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(CancellationToken cancellationToken)
        => Task.FromResult(new ListPermissionProfilesResult
        {
            IsSupported = false,
            UnavailableReason = "Permission profiles are not available through this bridge.",
        });

    Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken);

    Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken);

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

    public WorkerBridge(OutputChannel? outputChannel = null)
    {
        log = outputChannel;
    }

    public event Func<WorkerStatus, Task>? StateChanged;

    public event Func<AccountStatus, Task>? AccountChanged;

    public event Func<ConversationEvent, Task>? ConversationEventReceived;

    public event Func<ApprovalRequest, Task>? ApprovalRequested;

    public event Func<string, Task>? ApprovalResolved;

    public event Func<UserInputRequest, Task>? UserInputRequested;

    public event Func<string, Task>? UserInputResolved;

    public event Func<ContextCompactionEvent, Task>? ContextCompacted;

    public event Func<ReviewModeEvent, Task>? ReviewModeChanged;

    public event Func<ThreadGoalEvent, Task>? ThreadGoalChanged;

    public event Func<RateLimitsResult, Task>? RateLimitsChanged;

    public event Func<Task>? SkillsChanged;

    public async Task<WorkerStatus> ConnectAsync(string workingDirectory, bool experimentalApi, CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("Worker connect invocation starting");
        await EnsureWorkerStartedAsync(cancellationToken).ConfigureAwait(false);

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
                },
            },
            cancellationToken).ConfigureAwait(false);
        ExtensionDiagnostics.Write($"Worker connect invocation completed state={result.State}");
        return result;
    }

    public Task<WorkerStatus> RestartAsync(CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<WorkerStatus>("worker/restart", Array.Empty<object>(), cancellationToken);

    public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<AccountStatus>("worker/account/status", Array.Empty<object>(), cancellationToken);

    public async Task<StartAccountLoginResult> StartAccountLoginAsync(CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("worker/account/login/start invocation starting");
        try
        {
            StartAccountLoginResult result = await RequireRpc().InvokeWithCancellationAsync<StartAccountLoginResult>(
                "worker/account/login/start",
                Array.Empty<object>(),
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

    public async Task<AccountStatus> LogoutAccountAsync(CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("worker/account/logout invocation starting");
        try
        {
            AccountStatus result = await RequireRpc().InvokeWithCancellationAsync<AccountStatus>(
                "worker/account/logout",
                Array.Empty<object>(),
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

    public Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<ThreadPage>("worker/thread/list", new object?[] { cursor }, cancellationToken);

    public async Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("worker/models/list invocation starting");
        using var timeout = new CancellationTokenSource(ModelListTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            ListModelsResult result = await RequireRpc().InvokeWithCancellationAsync<ListModelsResult>(
                "worker/models/list",
                Array.Empty<object>(),
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

    public Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ListPermissionProfilesResult>(
            "worker/permissionProfiles/list",
            Array.Empty<object>(),
            cancellationToken);

    public Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<ThreadSummary>("worker/thread/start", Array.Empty<object>(), cancellationToken);

    public Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync<ThreadSummary>("worker/thread/resume", new object[] { threadId }, cancellationToken);

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

    public Task<ListSkillsResult> ListSkillsAsync(bool forceReload, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ListSkillsResult>(
            "worker/skills/list",
            new object[] { forceReload },
            cancellationToken);

    public Task<ForkThreadResult> ForkThreadAsync(ForkThreadRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ForkThreadResult>(
            "worker/thread/fork",
            new object[] { request },
            cancellationToken);

    public Task<ThreadGoalResult> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadGoalResult>(
            "worker/thread/goal/get",
            new object[] { threadId },
            cancellationToken);

    public Task<ThreadGoalResult> SetThreadGoalAsync(SetThreadGoalRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadGoalResult>(
            "worker/thread/goal/set",
            new object[] { request },
            cancellationToken);

    public Task<ThreadGoalResult> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadGoalResult>(
            "worker/thread/goal/clear",
            new object[] { threadId },
            cancellationToken);

    public Task<McpServerListResult> ListMcpServersAsync(string? threadId, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<McpServerListResult>(
            "worker/mcp/list",
            new object?[] { threadId },
            cancellationToken);

    public Task<UploadFeedbackResult> UploadFeedbackAsync(UploadFeedbackRequest request, CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<UploadFeedbackResult>(
            "worker/feedback/upload",
            new object[] { request },
            cancellationToken);

    public Task<RateLimitsResult> GetRateLimitsAsync(CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<RateLimitsResult>(
            "worker/account/rateLimits",
            Array.Empty<object>(),
            cancellationToken);

    public Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync("worker/approval/resolve", new object[] { request }, cancellationToken);

    public Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync("worker/userInput/resolve", new object[] { request }, cancellationToken);

    public Task OnStateChangedAsync(WorkerStatus status, CancellationToken cancellationToken)
        => StateChanged?.Invoke(status) ?? Task.CompletedTask;

    public Task OnAccountChangedAsync(AccountStatus status, CancellationToken cancellationToken)
        => AccountChanged?.Invoke(status) ?? Task.CompletedTask;

    public Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
        => ConversationEventReceived?.Invoke(conversationEvent) ?? Task.CompletedTask;

    public Task OnApprovalRequestedAsync(ApprovalRequest approval, CancellationToken cancellationToken)
    {
        _ = log?.WriteLineAsync($"[AUDIT] Approval requested: {approval.Risk} — {approval.DisplayText}");
        return ApprovalRequested?.Invoke(approval) ?? Task.CompletedTask;
    }

    public Task OnApprovalResolvedAsync(string requestId, CancellationToken cancellationToken)
        => ApprovalResolved?.Invoke(requestId) ?? Task.CompletedTask;

    public Task OnUserInputRequestedAsync(UserInputRequest request, CancellationToken cancellationToken)
        => UserInputRequested?.Invoke(request) ?? Task.CompletedTask;

    public Task OnUserInputResolvedAsync(string requestId, CancellationToken cancellationToken)
        => UserInputResolved?.Invoke(requestId) ?? Task.CompletedTask;

    public Task OnContextCompactedAsync(ContextCompactionEvent value, CancellationToken cancellationToken)
        => ContextCompacted?.Invoke(value) ?? Task.CompletedTask;

    public Task OnReviewModeChangedAsync(ReviewModeEvent value, CancellationToken cancellationToken)
        => ReviewModeChanged?.Invoke(value) ?? Task.CompletedTask;

    public Task OnThreadGoalChangedAsync(ThreadGoalEvent value, CancellationToken cancellationToken)
        => ThreadGoalChanged?.Invoke(value) ?? Task.CompletedTask;

    public Task OnRateLimitsChangedAsync(RateLimitsResult value, CancellationToken cancellationToken)
        => RateLimitsChanged?.Invoke(value) ?? Task.CompletedTask;

    public Task OnSkillsChangedAsync(SkillsChangedEvent value, CancellationToken cancellationToken)
        => SkillsChanged?.Invoke() ?? Task.CompletedTask;

    public Task OnApprovalAuditAsync(ApprovalAuditRecord record, CancellationToken cancellationToken)
    {
        _ = log?.WriteLineAsync(
            $"[AUDIT] Approval {record.Action}: request={record.RequestId}, scope={record.Scope}, risk={record.Risk}, target={record.DisplayText}");
        return Task.CompletedTask;
    }

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
                await Task.Run(workerRpc.Dispose).ConfigureAwait(false);
            }

            if (workerPipe is not null)
            {
                await workerPipe.DisposeAsync().ConfigureAwait(false);
            }
            if (workerProcess is not null)
            {
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
        if (rpc is not null)
        {
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (rpc is null)
            {
                await StartWorkerAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        string pipeName = $"Kkamegawa.CodexForVisualStudio.{Guid.NewGuid():N}";
        string assemblyDirectory = Path.GetDirectoryName(typeof(WorkerBridge).Assembly.Location) ?? string.Empty;
        string workerPath = Path.Combine(assemblyDirectory, "Worker", "Codex.VisualStudio.Worker.exe");
        ExtensionDiagnostics.Write($"Worker start requested exists={File.Exists(workerPath)}");
        process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = $"--pipe {pipeName}",
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
        }) ?? throw new InvalidOperationException("Failed to start the Codex worker.");
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

    private JsonRpc RequireRpc()
        => rpc ?? throw new InvalidOperationException("The Codex Worker RPC connection is unavailable.");
}
