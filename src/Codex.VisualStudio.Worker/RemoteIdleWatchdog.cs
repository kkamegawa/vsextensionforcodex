using Codex.AppServer.Protocol;

namespace Codex.VisualStudio.Worker;

public sealed record RemoteWatchdogTiming(TimeSpan IdleWindow, TimeSpan ProbeTimeout)
{
    public static RemoteWatchdogTiming Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
}

/// <summary>
/// Detects a silent remote peer for one connection generation. WebSocket keep-alive PONG frames
/// are unsolicited and prove nothing about the peer, so liveness comes only from valid parsed
/// inbound JSON-RPC traffic. After a full idle window without inbound activity it sends at most
/// two <c>account/read(refreshToken: false)</c> probes, each sent once without overload retry.
/// Only two consecutive silent probes report the peer as unresponsive. It never reconnects and
/// never sends a mutation.
/// </summary>
internal sealed class RemoteIdleWatchdog : IDisposable
{
    private readonly IJsonRpcConnection connection;
    private readonly IInboundActivitySource activity;
    private readonly RemoteWatchdogTiming timing;
    private readonly TimeProvider timeProvider;
    private readonly Func<CancellationToken, Task> onUnresponsive;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;

    public RemoteIdleWatchdog(
        IJsonRpcConnection connection,
        IInboundActivitySource activity,
        RemoteWatchdogTiming timing,
        TimeProvider timeProvider,
        Func<CancellationToken, Task> onUnresponsive)
    {
        this.connection = connection;
        this.activity = activity;
        this.timing = timing;
        this.timeProvider = timeProvider;
        this.onUnresponsive = onUnresponsive;
    }

    public Task Completion => loop ?? Task.CompletedTask;

    public void Start() => loop = Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None);

    // Cancels without waiting, so a caller holding the connection-transition gate never awaits a
    // watchdog that may itself be waiting for that gate. Owners drain Completion afterwards.
    public void Dispose() => Cancel();

    public void Cancel()
    {
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            long observed = activity.InboundActivitySequence;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(timing.IdleWindow, timeProvider, cancellationToken).ConfigureAwait(false);
                long current = activity.InboundActivitySequence;
                if (current != observed)
                {
                    observed = current;
                    continue;
                }

                if (await ProbeShowsLifeAsync(cancellationToken).ConfigureAwait(false)
                    || await ProbeShowsLifeAsync(cancellationToken).ConfigureAwait(false))
                {
                    observed = activity.InboundActivitySequence;
                    continue;
                }

                await onUnresponsive(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (JsonRpcConnectionClosedException)
        {
            // The transport's Closed event reports the loss; the watchdog just stops.
        }
        catch (Exception ex)
        {
            WorkerDiagnostics.Write("remote idle watchdog stopped", ex);
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    // True when the peer answered (any response, including a JSON-RPC error or SignedOut), or
    // when other inbound traffic arrived while the probe was outstanding.
    private async Task<bool> ProbeShowsLifeAsync(CancellationToken cancellationToken)
    {
        long before = activity.InboundActivitySequence;
        try
        {
            await connection.SendRequestAsync(
                "account/read",
                new { refreshToken = false },
                timing.ProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (JsonRpcRemoteException)
        {
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return activity.InboundActivitySequence != before;
        }
    }
}
