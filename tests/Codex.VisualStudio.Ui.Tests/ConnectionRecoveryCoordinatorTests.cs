using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ConnectionRecoveryCoordinatorTests
{
    [TestMethod]
    public async Task RunAsync_RetriesTransientFailuresWithBoundedBackoff_ThenReconnects()
    {
        int attempts = 0;
        var delays = new List<TimeSpan>();

        ConnectionRecoveryResult result = await ConnectionRecoveryCoordinator.RunAsync(
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(new WorkerStatus
                {
                    State = attempts == 3 ? WorkerConnectionState.Ready : WorkerConnectionState.Degraded,
                    RecoveryFailureKind = WorkerRecoveryFailureKind.PipeClosed,
                });
            },
            _ => Task.FromResult(true),
            CancellationToken.None,
            jitterSample: () => 0.5,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.AreEqual(ConnectionRecoveryOutcome.Reconnected, result.Outcome);
        Assert.AreEqual(3, result.Attempts);
        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            delays);
    }

    [TestMethod]
    public async Task RunAsync_StopsAfterFiveTransientFailures()
    {
        int attempts = 0;

        ConnectionRecoveryResult result = await ConnectionRecoveryCoordinator.RunAsync(
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(new WorkerStatus
                {
                    State = WorkerConnectionState.Degraded,
                    RecoveryFailureKind = WorkerRecoveryFailureKind.ServerUnavailable,
                });
            },
            _ => Task.FromResult(true),
            CancellationToken.None,
            jitterSample: () => 0.5,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(ConnectionRecoveryOutcome.Exhausted, result.Outcome);
        Assert.AreEqual(ConnectionRecoveryCoordinator.MaximumAttempts, result.Attempts);
        Assert.AreEqual(ConnectionRecoveryCoordinator.MaximumAttempts, attempts);
        Assert.AreEqual(WorkerRecoveryFailureKind.ServerUnavailable, result.LastFailureKind);
    }

    [TestMethod]
    public async Task RunAsync_DoesNotRetryTerminalFailureOrChangedTarget()
    {
        int attempts = 0;
        ConnectionRecoveryResult terminal = await ConnectionRecoveryCoordinator.RunAsync(
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(new WorkerStatus
                {
                    State = WorkerConnectionState.Degraded,
                    RecoveryFailureKind = WorkerRecoveryFailureKind.AuthenticationRejected,
                });
            },
            _ => Task.FromResult(true),
            CancellationToken.None,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(ConnectionRecoveryOutcome.Stopped, terminal.Outcome);
        Assert.AreEqual(1, attempts);

        ConnectionRecoveryResult changed = await ConnectionRecoveryCoordinator.RunAsync(
            (_, _) =>
            {
                Assert.Fail("A changed connection target must not be retried.");
                return Task.FromResult(new WorkerStatus());
            },
            _ => Task.FromResult(false),
            CancellationToken.None,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(ConnectionRecoveryOutcome.Stopped, changed.Outcome);
        Assert.AreEqual(0, changed.Attempts);
        Assert.AreEqual(WorkerRecoveryFailureKind.ProfileChanged, changed.LastFailureKind);
    }

    [TestMethod]
    public void GetRetryDelay_AppliesTwentyPercentJitter()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(800), ConnectionRecoveryCoordinator.GetRetryDelay(2, 0));
        Assert.AreEqual(TimeSpan.FromSeconds(1), ConnectionRecoveryCoordinator.GetRetryDelay(2, 0.5));
        Assert.AreEqual(TimeSpan.FromMilliseconds(1200), ConnectionRecoveryCoordinator.GetRetryDelay(2, 1));
        Assert.AreEqual(TimeSpan.FromSeconds(9.6), ConnectionRecoveryCoordinator.GetRetryDelay(5, 1));
    }
}
