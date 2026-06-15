using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility.Documents;
using StreamJsonRpc;

namespace Codex.VisualStudio.Extension;

public sealed class WorkerBridge : ICodexWorkerObserver, IAsyncDisposable
{
    private readonly OutputChannel? log;
    private Process? process;
    private NamedPipeClientStream? pipe;
    private JsonRpc? rpc;
    private ProcessJobObject? jobObject;

    public WorkerBridge(OutputChannel? outputChannel = null)
    {
        log = outputChannel;
    }

    public event Func<WorkerStatus, Task>? StateChanged;

    public event Func<AccountStatus, Task>? AccountChanged;

    public event Func<ConversationEvent, Task>? ConversationEventReceived;

    public event Func<ApprovalRequest, Task>? ApprovalRequested;

    public event Func<string, Task>? ApprovalResolved;

    public async Task<WorkerStatus> ConnectAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("Worker connect invocation starting");
        if (rpc is null)
        {
            await StartWorkerAsync(cancellationToken).ConfigureAwait(false);
        }

        WorkerStatus result = await RequireRpc().InvokeWithCancellationAsync<WorkerStatus>(
            "worker/connect",
            new object[]
            {
                new WorkerOptions
                {
                    CodexPath = "codex",
                    WorkingDirectory = workingDirectory,
                    ExtensionVersion = "0.1.0",
                    ExperimentalApi = false,
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

    public Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
        => rpc!.InvokeWithCancellationAsync("worker/approval/resolve", new object[] { request }, cancellationToken);

    public Task OnStateChangedAsync(WorkerStatus status, CancellationToken cancellationToken)
    {
        if (status.State == WorkerConnectionState.Degraded)
            _ = log?.WriteLineAsync($"[CODEX] Worker degraded: {status.Message}");
        return StateChanged?.Invoke(status) ?? Task.CompletedTask;
    }

    public Task OnAccountChangedAsync(AccountStatus status, CancellationToken cancellationToken)
        => AccountChanged?.Invoke(status) ?? Task.CompletedTask;

    public Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
    {
        if (conversationEvent.Kind == ConversationEventKind.Error)
            _ = log?.WriteLineAsync($"[CODEX ERROR] {conversationEvent.Text}");
        return ConversationEventReceived?.Invoke(conversationEvent) ?? Task.CompletedTask;
    }

    public Task OnApprovalRequestedAsync(ApprovalRequest approval, CancellationToken cancellationToken)
    {
        _ = log?.WriteLineAsync($"[AUDIT] Approval requested: {approval.Risk} — {approval.DisplayText}");
        return ApprovalRequested?.Invoke(approval) ?? Task.CompletedTask;
    }

    public Task OnApprovalResolvedAsync(string requestId, CancellationToken cancellationToken)
        => ApprovalResolved?.Invoke(requestId) ?? Task.CompletedTask;

    public Task OnApprovalAuditAsync(ApprovalAuditRecord record, CancellationToken cancellationToken)
    {
        _ = log?.WriteLineAsync(
            $"[AUDIT] Approval {record.Action}: request={record.RequestId}, scope={record.Scope}, risk={record.Risk}, target={record.DisplayText}");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        rpc?.Dispose();
        pipe?.Dispose();
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    // Kill the worker together with its descendants (codex app-server and any
                    // cmd.exe processes it spawned) on the normal shutdown path. The job object
                    // assigned in StartWorkerAsync is the safety net for abnormal termination.
                    process.Kill(entireProcessTree: true);
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
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

        process?.Dispose();
        jobObject?.Dispose();
        jobObject = null;
    }

    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        string pipeName = $"Kkamegawa.CodexForVisualStudio.{Guid.NewGuid():N}";
        string assemblyDirectory = Path.GetDirectoryName(typeof(WorkerBridge).Assembly.Location) ?? string.Empty;
        string workerPath = Path.Combine(assemblyDirectory, "Worker", "Codex.VisualStudio.Worker.exe");
        ExtensionDiagnostics.Write($"Worker start requested exists={File.Exists(workerPath)}");
        process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = $"--pipe {pipeName}",
            UseShellExecute = false,

            // The worker keeps a console (instead of CREATE_NO_WINDOW) but it is hidden via
            // WindowStyle. This gives codex app-server - and the cmd.exe processes it spawns -
            // a console to inherit, so the OS does not allocate a new visible console window
            // for each of them. Codex.VisualStudio.Worker also hides its console window at
            // startup as a defensive measure (see HiddenConsole).
            CreateNoWindow = false,
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
        _ = ReadWorkerDiagnosticsAsync(process);
        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.Run(() => pipe.Connect(15_000), cancellationToken).ConfigureAwait(false);
        ExtensionDiagnostics.Write("Worker pipe connected");
        rpc = new JsonRpc(pipe);
        rpc.AddLocalRpcTarget<ICodexWorkerObserver>(this, null);
        rpc.StartListening();
        ExtensionDiagnostics.Write("Worker RPC listening");
    }

    private async Task ReadWorkerDiagnosticsAsync(Process source)
    {
        try
        {
            while (await source.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                await ExtensionDiagnostics.WriteOutputAsync(log, ExtensionDiagnostics.Sanitize(line)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Worker diagnostics stream ended", ex);
        }
    }

    private JsonRpc RequireRpc()
        => rpc ?? throw new InvalidOperationException("The Codex Worker RPC connection is unavailable.");
}
