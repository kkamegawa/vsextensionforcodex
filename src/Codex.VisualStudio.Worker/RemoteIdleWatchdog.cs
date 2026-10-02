using Codex.AppServer.Protocol;

namespace Codex.VisualStudio.Worker;

public sealed record RemoteWatchdogTiming(TimeSpan IdleWindow, TimeSpan ProbeTimeout)
{
    public static RemoteWatchdogTiming Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
}

/// <summary>
/// Detects a silent remote peer for one connection generation. WebSocket keep-alive PONG frames
/// are unsolicited and prove nothing about the peer, so liveness comes only from valid parsed
/// inbound JSON-RPC traffic. Once a full idle window has passed since the last inbound message
/// (or since start, when nothing has arrived yet) it sends at most
/// two <c>account/read(refreshToken: false)</c> probes, each sent once without overload retry.
/// Only two consecutive silent probes report the peer as unresponsive. One activity baseline
/// covers the whole probe episode, and the owner revalidates it before retiring the socket, so a
/// message that arrives between or after the probes restarts the idle window instead. It never
/// reconnects and never sends a mutation.
/// </summary>
internal sealed class RemoteIdleWatchdog : IDisposable
{
    private readonly IJsonRpcConnection connection;
    private readonly IInboundActivitySource activity;
    private readonly RemoteWatchdogTiming timing;
    private readonly TimeProvider timeProvider;
    private readonly UnresponsiveHandler onUnresponsive;
    private readonly CancellationTokenSource lifetime = new();
    private long lastActivityTimestamp;
    private Task? loop;

    public RemoteIdleWatchdog(
        IJsonRpcConnection connection,
        IInboundActivitySource activity,
        RemoteWatchdogTiming timing,
        TimeProvider timeProvider,
        UnresponsiveHandler onUnresponsive)
    {
        this.connection = connection;
        this.activity = activity;
        this.timing = timing;
        this.timeProvider = timeProvider;
        this.onUnresponsive = onUnresponsive;
        lastActivityTimestamp = timeProvider.GetTimestamp();
        activity.InboundActivity += OnInboundActivity;
    }

    /// <summary>
    /// Called after two silent probes. <paramref name="isStillSilent"/> reports whether no inbound
    /// activity arrived since the probe episode began; the owner checks it under its transition
    /// gate immediately before retiring the socket. Returns <see langword="true"/> to keep watching
    /// (activity arrived, so the socket was kept) and <see langword="false"/> to stop.
    /// </summary>
    internal delegate Task<bool> UnresponsiveHandler(Func<bool> isStillSilent, CancellationToken cancellationToken);

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
            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait only until a full idle window after the last inbound message, so probing
                // starts once the peer has been silent for exactly that window.
                TimeSpan silence = timeProvider.GetElapsedTime(Interlocked.Read(ref lastActivityTimestamp));
                if (silence < timing.IdleWindow)
                {
                    await Task.Delay(timing.IdleWindow - silence, timeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // One baseline for the whole episode: activity between the probes, or while the
                // owner waits for its gate, still counts as life.
                long episode = activity.InboundActivitySequence;
                if (await ProbeShowsLifeAsync(episode, cancellationToken).ConfigureAwait(false)
                    || await ProbeShowsLifeAsync(episode, cancellationToken).ConfigureAwait(false))
                {
                    MarkActivity();
                    continue;
                }

                bool IsStillSilent() => activity.InboundActivitySequence == episode;
                if (await onUnresponsive(IsStillSilent, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

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
            activity.InboundActivity -= OnInboundActivity;
            lifetime.Dispose();
        }
    }

    private void OnInboundActivity(object? sender, EventArgs e) => MarkActivity();

    private void MarkActivity() => Interlocked.Exchange(ref lastActivityTimestamp, timeProvider.GetTimestamp());

    // True when the peer answered (any response, including a JSON-RPC error or SignedOut), or
    // when other inbound traffic arrived since the probe episode began.
    private async Task<bool> ProbeShowsLifeAsync(long episode, CancellationToken cancellationToken)
    {
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
            return activity.InboundActivitySequence != episode;
        }
    }
}
