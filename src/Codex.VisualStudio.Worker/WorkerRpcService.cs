using Codex.VisualStudio.Contracts;
using StreamJsonRpc;
using System.Diagnostics;

namespace Codex.VisualStudio.Worker;

public sealed class WorkerRpcService : ICodexWorkerClient, IAsyncDisposable
{
    private readonly ISecretRedactor redactor;
    private readonly ICodexProcessHost processHost;
    private readonly ICodexSessionService session;
    private WorkerOptions? options;
    private JsonRpc? clientRpc;
    private WorkerStatus status = new() { State = WorkerConnectionState.Disconnected, Message = "Worker is disconnected." };
    private AccountStatus accountStatus = new();
    private int networkFailureReported;

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
    }

    public void AttachClient(JsonRpc rpc)
    {
        clientRpc = rpc;
    }

    public async Task<WorkerStatus> ConnectAsync(WorkerOptions options, CancellationToken cancellationToken)
    {
        WorkerDiagnostics.Write("worker connect RPC received");
        if (options.ContractVersion != ContractVersions.Current)
        {
            throw new InvalidOperationException($"Unsupported contract version {options.ContractVersion}.");
        }

        this.options = options;
        Interlocked.Exchange(ref networkFailureReported, 0);
        await SetStatusAsync(WorkerConnectionState.Connecting, "Starting codex app-server...", cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerDiagnostics.Write("worker starting codex app-server");
            await processHost.StartAsync(options.CodexPath, options.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write("worker initializing codex app-server");
            await session.InitializeAsync(processHost.Connection!, options, cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write("worker reading account status");
            accountStatus = await session.GetAccountStatusAsync(cancellationToken).ConfigureAwait(false);
            WorkerDiagnostics.Write("worker connect completed");
            return await SetStatusAsync(WorkerConnectionState.Ready, "Connected to codex app-server.", cancellationToken).ConfigureAwait(false);
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
        if (options is null)
        {
            throw new InvalidOperationException("Connect must be called before restart.");
        }

        await processHost.StopAsync(cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(options, cancellationToken).ConfigureAwait(false);
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

    public async Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
    {
        await SetStatusAsync(WorkerConnectionState.Busy, "Turn in progress.", cancellationToken).ConfigureAwait(false);
        string turnId = await session.StartTurnAsync(request, cancellationToken).ConfigureAwait(false);
        UpdateSessionIds();
        return turnId;
    }

    public Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken)
        => session.SteerTurnAsync(request, cancellationToken);

    public Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken)
        => session.InterruptTurnAsync(request, cancellationToken);

    public Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
        => session.ResolveApprovalAsync(request, cancellationToken);

    public Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
        => session.ResolveUserInputAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await session.DisposeAsync().ConfigureAwait(false);
        await processHost.DisposeAsync().ConfigureAwait(false);
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
        if (conversationEvent.Kind == ConversationEventKind.TurnCompleted)
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
    }

    private WorkerStatus CloneStatus() => new()
    {
        State = status.State,
        Message = status.Message,
        ThreadId = status.ThreadId,
        TurnId = status.TurnId,
        ProcessId = status.ProcessId,
    };

    private AccountStatus CloneAccountStatus() => new()
    {
        State = accountStatus.State,
        PlanType = accountStatus.PlanType,
        Message = accountStatus.Message,
    };
}
