using Codex.VisualStudio.Contracts;
using StreamJsonRpc;

namespace Codex.VisualStudio.Worker;

public sealed class WorkerRpcService : ICodexWorkerClient, IAsyncDisposable
{
    private readonly ISecretRedactor redactor;
    private readonly ICodexProcessHost processHost;
    private readonly ICodexSessionService session;
    private WorkerOptions? options;
    private JsonRpc? clientRpc;
    private WorkerStatus status = new() { State = WorkerConnectionState.Disconnected, Message = "Worker is disconnected." };

    public WorkerRpcService(ISecretRedactor redactor, ICodexProcessHost processHost, ICodexSessionService session)
    {
        this.redactor = redactor;
        this.processHost = processHost;
        this.session = session;
        processHost.StandardErrorReceived += (_, text) => _ = PublishEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.Error,
            Text = text,
        }, CancellationToken.None);
        processHost.Exited += (_, exitCode) => _ = SetStatusAsync(
            WorkerConnectionState.Degraded,
            $"codex app-server exited with code {exitCode}.",
            CancellationToken.None);
        session.ConversationEventReceived += PublishEventAsync;
        session.ApprovalRequested += PublishApprovalAsync;
        session.ApprovalResolved += PublishApprovalResolvedAsync;
    }

    public void AttachClient(JsonRpc rpc)
    {
        clientRpc = rpc;
    }

    public async Task<WorkerStatus> ConnectAsync(WorkerOptions options, CancellationToken cancellationToken)
    {
        if (options.ContractVersion != ContractVersions.Current)
        {
            throw new InvalidOperationException($"Unsupported contract version {options.ContractVersion}.");
        }

        this.options = options;
        await SetStatusAsync(WorkerConnectionState.Connecting, "Starting codex app-server...", cancellationToken).ConfigureAwait(false);
        try
        {
            await processHost.StartAsync(options.CodexPath, options.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            await session.InitializeAsync(processHost.Connection!, options, cancellationToken).ConfigureAwait(false);
            return await SetStatusAsync(WorkerConnectionState.Ready, "Connected to codex app-server.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
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
}
