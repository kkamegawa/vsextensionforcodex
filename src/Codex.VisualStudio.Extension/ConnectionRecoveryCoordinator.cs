using System.Diagnostics;
using System.IO;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

internal enum ConnectionRecoveryOutcome
{
    Reconnected,
    Exhausted,
    Stopped,
    Cancelled,
}

internal sealed record ConnectionRecoveryResult(
    ConnectionRecoveryOutcome Outcome,
    int Attempts,
    WorkerRecoveryFailureKind LastFailureKind);

/// <summary>
/// Applies the bounded retry policy for transient app-server connection loss.
/// The caller owns target validation, bridge replacement, UI state, and operation handling.
/// </summary>
internal static class ConnectionRecoveryCoordinator
{
    internal const int MaximumAttempts = 5;

    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(45);

    internal static readonly TimeSpan EpisodeTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    ];

    internal static async Task<ConnectionRecoveryResult> RunAsync(
        Func<int, CancellationToken, Task<WorkerStatus>> attemptAsync,
        Func<CancellationToken, Task<bool>> targetStillValidAsync,
        CancellationToken cancellationToken,
        Func<double>? jitterSample = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(attemptAsync);
        ArgumentNullException.ThrowIfNull(targetStillValidAsync);
        jitterSample ??= static () => Random.Shared.NextDouble();
        delayAsync ??= static (duration, token) => Task.Delay(duration, token);

        var elapsed = Stopwatch.StartNew();
        WorkerRecoveryFailureKind lastFailure = WorkerRecoveryFailureKind.None;
        int attempts = 0;

        for (int attemptNumber = 1; attemptNumber <= MaximumAttempts; attemptNumber++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Cancelled, attempts, WorkerRecoveryFailureKind.Cancelled);
            }

            if (elapsed.Elapsed >= EpisodeTimeout)
            {
                return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Exhausted, attempts, lastFailure);
            }

            bool targetValid;
            try
            {
                targetValid = await targetStillValidAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Cancelled, attempts, WorkerRecoveryFailureKind.Cancelled);
            }

            if (!targetValid)
            {
                return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Stopped, attempts, WorkerRecoveryFailureKind.ProfileChanged);
            }

            if (attemptNumber > 1)
            {
                TimeSpan remaining = EpisodeTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Exhausted, attempts, lastFailure);
                }

                TimeSpan requestedDelay = GetRetryDelay(attemptNumber, jitterSample());
                if (requestedDelay >= remaining)
                {
                    return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Exhausted, attempts, lastFailure);
                }

                try
                {
                    await delayAsync(requestedDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Cancelled, attempts, WorkerRecoveryFailureKind.Cancelled);
                }

                bool remainsValid;
                try
                {
                    remainsValid = await targetStillValidAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Cancelled, attempts, WorkerRecoveryFailureKind.Cancelled);
                }

                if (!remainsValid)
                {
                    return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Stopped, attempts, WorkerRecoveryFailureKind.ProfileChanged);
                }
            }

            TimeSpan attemptRemaining = EpisodeTimeout - elapsed.Elapsed;
            if (attemptRemaining <= TimeSpan.Zero)
            {
                return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Exhausted, attempts, lastFailure);
            }

            using var attemptTimeout = new CancellationTokenSource(
                attemptRemaining < AttemptTimeout ? attemptRemaining : AttemptTimeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                attemptTimeout.Token);
            attempts++;
            try
            {
                WorkerStatus status = await attemptAsync(attemptNumber, linkedCancellation.Token).ConfigureAwait(false);
                if (status.State == WorkerConnectionState.Ready)
                {
                    return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Reconnected, attempts, WorkerRecoveryFailureKind.None);
                }

                lastFailure = status.RecoveryFailureKind;
                if (!IsTransient(lastFailure))
                {
                    return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Stopped, attempts, lastFailure);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Cancelled, attempts, WorkerRecoveryFailureKind.Cancelled);
            }
            catch (OperationCanceledException) when (attemptTimeout.IsCancellationRequested)
            {
                lastFailure = WorkerRecoveryFailureKind.PeerUnresponsive;
            }
            catch (Exception ex) when (IsTransientException(ex))
            {
                lastFailure = WorkerRecoveryFailureKind.PeerUnresponsive;
            }
            catch
            {
                return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Stopped, attempts, WorkerRecoveryFailureKind.Unknown);
            }
        }

        return new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Exhausted, attempts, lastFailure);
    }

    internal static bool IsTransient(WorkerRecoveryFailureKind failureKind)
        => failureKind is WorkerRecoveryFailureKind.WorkerProcessExit
            or WorkerRecoveryFailureKind.PipeClosed
            or WorkerRecoveryFailureKind.TransportClosed
            or WorkerRecoveryFailureKind.PeerUnresponsive
            or WorkerRecoveryFailureKind.ServerUnavailable;

    internal static TimeSpan GetRetryDelay(int attemptNumber, double jitterSample)
    {
        if (attemptNumber is < 2 or > MaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        if (jitterSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterSample));
        }

        double jitter = (jitterSample * 0.4) - 0.2;
        double seconds = RetryDelays[attemptNumber - 2].TotalSeconds * (1 + jitter);
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsTransientException(Exception exception)
        => exception is IOException or TimeoutException or ObjectDisposedException;
}
