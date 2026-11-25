using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;
using StreamJsonRpc;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class WorkerRpcOwnerBoundaryTests
{
    [TestMethod]
    public async Task SimultaneousWorkerInstancesInOneWorkspaceKeepMutationsAndNotificationsPartitioned()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-shared-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var connectionA = new RecordingConnection(workspace);
            var connectionB = new RecordingConnection(workspace);
            var sessionA = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            var sessionB = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var workerA = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(connectionA), sessionA);
            await using var workerB = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(connectionB), sessionB);
            await using var clientA = new RpcClientChannel(workerA);
            await using var clientB = new RpcClientChannel(workerB);

            WorkerStatus statusA = await workerA.ConnectAsync(Options(workspace), CancellationToken.None);
            WorkerStatus statusB = await workerB.ConnectAsync(Options(workspace), CancellationToken.None);
            ConnectionTargetSnapshot targetA = statusA.Target!;
            ConnectionTargetSnapshot targetB = statusB.Target!;
            await clientA.WaitForReadyNotificationAsync(targetA).WaitAsync(TimeSpan.FromSeconds(5));
            await clientB.WaitForReadyNotificationAsync(targetB).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreNotEqual(targetA.StatePartitionFingerprint, targetB.StatePartitionFingerprint);

            ThreadSummary threadA = await workerA.StartThreadAsync(Request<StartThreadRequest>(targetA), CancellationToken.None);
            ThreadSummary threadB = await workerB.StartThreadAsync(Request<StartThreadRequest>(targetB), CancellationToken.None);
            await workerA.StartTurnAsync(new StartTurnRequest
            {
                ThreadId = threadA.Id,
                Text = "instance A turn marker",
                StatePartitionFingerprint = targetA.StatePartitionFingerprint,
                OwnerGeneration = targetA.OwnerGeneration,
                ConnectionGeneration = targetA.Generation,
            }, CancellationToken.None);
            await workerB.StartTurnAsync(new StartTurnRequest
            {
                ThreadId = threadB.Id,
                Text = "instance B turn marker",
                StatePartitionFingerprint = targetB.StatePartitionFingerprint,
                OwnerGeneration = targetB.OwnerGeneration,
                ConnectionGeneration = targetB.Generation,
            }, CancellationToken.None);

            AssertMutationRequests(connectionA, threadA.Id, "instance A turn marker");
            AssertMutationRequests(connectionB, threadB.Id, "instance B turn marker");

            await connectionA.EmitNotificationAsync(
                "account/rateLimits/updated",
                new { rateLimits = new { primary = new { usedPercent = 10 } } });
            await clientA.WaitForRateLimitsAsync(targetA.OwnerGeneration).WaitAsync(TimeSpan.FromSeconds(5));
            WorkerNotification<RateLimitsResult> notificationA = clientA.RateLimitNotifications.Single();
            Assert.AreEqual(targetA.StatePartitionFingerprint, notificationA.StatePartitionFingerprint);
            Assert.AreEqual(targetA.OwnerGeneration, notificationA.OwnerGeneration);
            Assert.AreEqual(0, clientB.RateLimitNotifications.Count,
                "A notification from instance A must not be published through instance B's client channel.");
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DelayedTurnStartResponseCannotCompleteAcrossOwnerInvalidation()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), $"worker-owner-boundary-{Guid.NewGuid():N}");
        string ownerAPath = Path.Combine(testRoot, "owner-a");
        string ownerBPath = Path.Combine(testRoot, "owner-b");
        Directory.CreateDirectory(ownerAPath);
        Directory.CreateDirectory(ownerBPath);

        try
        {
            var ownerAConnection = new RecordingConnection(ownerAPath);
            var ownerBConnection = new RecordingConnection(ownerBPath);
            ownerAConnection.DelayTurnStart = true;
            var processHost = new SwitchingProcessHost(ownerAConnection, ownerBConnection);
            var session = new CodexSessionService(
                new ApprovalPolicyEngine(new PathAccessPolicy()),
                new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), processHost, session);
            await using var client = new RpcClientChannel(worker);

            WorkerStatus ownerAStatus = await worker.ConnectAsync(Options(ownerAPath), CancellationToken.None);
            ConnectionTargetSnapshot ownerATarget = ownerAStatus.Target!;
            await client.WaitForReadyNotificationAsync(ownerATarget).WaitAsync(TimeSpan.FromSeconds(5));
            ThreadSummary thread = await worker.StartThreadAsync(
                Request<StartThreadRequest>(ownerATarget),
                CancellationToken.None);
            int initialOwnerAReadyCount = client.Notifications.Count(notification =>
                notification.Value.State == WorkerConnectionState.Ready
                && notification.StatePartitionFingerprint == ownerATarget.StatePartitionFingerprint
                && notification.OwnerGeneration == ownerATarget.OwnerGeneration);

            var turnRequest = new StartTurnRequest
            {
                ThreadId = thread.Id,
                Text = "owner-a prompt",
                StatePartitionFingerprint = ownerATarget.StatePartitionFingerprint,
                OwnerGeneration = ownerATarget.OwnerGeneration,
                ConnectionGeneration = ownerATarget.Generation,
            };

            Task<string> oldTurn = worker.StartTurnAsync(turnRequest, CancellationToken.None);
            await ownerAConnection.TurnStartSeen.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(oldTurn.IsCompleted, "The StartTurn operation completed before its delayed app-server response was released.");
            Assert.AreEqual(1, ownerAConnection.MethodCount("turn/start"));

            ownerAConnection.AccountEmail = "other@example.test";
            await ownerAConnection.EmitNotificationAsync("account/updated", new { authMode = "chatgpt" });
            Assert.AreEqual(ownerATarget.OwnerGeneration + 1, session.OwnerGeneration);
            Task<WorkerStatus> replacement = worker.ConnectAsync(Options(ownerBPath), CancellationToken.None);
            ownerAConnection.ReleaseTurnStart();

            LocalRpcException turnFailure = await Assert.ThrowsExactlyAsync<LocalRpcException>(
                async () => await oldTurn.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(WorkerErrorCodes.UpstreamOperationFailed, turnFailure.ErrorCode);
            WorkerStatus ownerBStatus = await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            await client.WaitForReadyNotificationAsync(ownerBStatus.Target!).WaitAsync(TimeSpan.FromSeconds(5));
            WorkerNotification<WorkerStatus> busy = await client.BusyNotification.WaitAsync(TimeSpan.FromSeconds(5));

            StringAssert.Contains(turnFailure.Message, "generation");
            Assert.AreEqual(1, ownerAConnection.MethodCount("turn/start"));
            Assert.AreEqual(0, ownerBConnection.MethodCount("turn/start"));
            Assert.AreEqual(WorkerConnectionState.Busy, busy.Value.State);
            Assert.AreEqual(ownerATarget.StatePartitionFingerprint, busy.StatePartitionFingerprint);
            Assert.AreEqual(ownerATarget.OwnerGeneration, busy.OwnerGeneration);
            Assert.AreEqual(ownerATarget.StatePartitionFingerprint, busy.Value.Target?.StatePartitionFingerprint);
            Assert.AreEqual(ownerATarget.OwnerGeneration, busy.Value.Target?.OwnerGeneration);
            Assert.IsTrue(ReferenceEquals(ownerBConnection, processHost.Connection));
            Assert.AreNotEqual(ownerATarget.StatePartitionFingerprint, ownerBStatus.Target?.StatePartitionFingerprint);
            Assert.AreEqual(initialOwnerAReadyCount, client.Notifications.Count(notification =>
                notification.Value.State == WorkerConnectionState.Ready
                && notification.StatePartitionFingerprint == ownerATarget.StatePartitionFingerprint
                && notification.OwnerGeneration == ownerATarget.OwnerGeneration));
            Assert.AreEqual(1, client.Notifications.Count(notification =>
                notification.Value.State == WorkerConnectionState.Ready
                && notification.StatePartitionFingerprint == ownerBStatus.Target?.StatePartitionFingerprint
                && notification.OwnerGeneration == ownerBStatus.Target?.OwnerGeneration),
                "Only owner B's Connect operation may publish its Ready state.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task OwnerLogoutConnectsANewIsolatedOwnerAutomatically()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-logout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var first = new RecordingConnection(workspace);
            var second = new RecordingConnection(workspace);
            var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(first, second), session);
            await using var client = new RpcClientChannel(worker);
            ConnectionTargetSnapshot ownerA = (await worker.ConnectAsync(Options(workspace), CancellationToken.None)).Target!;

            AccountStatus result = await worker.LogoutAccountAsync(Request<LogoutAccountRequest>(ownerA), CancellationToken.None);

            WorkerNotification<WorkerStatus> ready = await client.WaitForAsync(notification =>
                notification.Value.State == WorkerConnectionState.Ready
                && notification.OwnerGeneration > ownerA.OwnerGeneration).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(AccountState.SignedOut, result.State);
            Assert.AreNotEqual(ownerA.StatePartitionFingerprint, ready.StatePartitionFingerprint);
            Assert.AreEqual(1, second.MethodCount("initialize"));
            Assert.AreEqual(0, second.MethodCount("account/logout"));
            Assert.IsFalse(client.Notifications.Any(notification => notification.Value.State == WorkerConnectionState.Degraded));
            Assert.IsTrue(client.Notifications.Any(notification =>
                notification.Value.State == WorkerConnectionState.Connecting
                && notification.Value.Message?.Contains("account changed", StringComparison.OrdinalIgnoreCase) == true));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task OwnerLogoutWhoseNotificationArrivesFirstStillConnectsANewOwner()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-logout-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new RecordingConnection(workspace)
            {
                AccountEmail = "a@example.test",
                EmitAccountUpdatedDuringLogout = true,
                LogoutResponseGate = releaseResponse,
            };
            var second = new RecordingConnection(workspace);
            var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(first, second), session);
            await using var client = new RpcClientChannel(worker);
            ConnectionTargetSnapshot ownerA = (await worker.ConnectAsync(Options(workspace), CancellationToken.None)).Target!;

            Task<AccountStatus> logout = worker.LogoutAccountAsync(Request<LogoutAccountRequest>(ownerA), CancellationToken.None);
            WorkerNotification<WorkerStatus> ready;
            try
            {
                ready = await client.WaitForAsync(notification =>
                    notification.Value.State == WorkerConnectionState.Ready
                    && notification.OwnerGeneration > ownerA.OwnerGeneration).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                releaseResponse.TrySetResult();
            }

            AccountStatus result = await logout.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(AccountState.SignedOut, result.State);
            Assert.AreNotEqual(ownerA.StatePartitionFingerprint, ready.StatePartitionFingerprint);
            Assert.IsFalse(client.Notifications.Any(notification => notification.Value.State == WorkerConnectionState.Degraded));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConfirmedLogoutBeforeOwnerInvalidationReturnsSignedOutWhenConnectionCloses()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-logout-confirmed-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var notificationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new RecordingConnection(workspace)
            {
                AccountEmail = "a@example.test",
                EmitAccountUpdatedDuringLogout = true,
                LogoutNotificationGate = notificationGate,
                LogoutResponseGate = responseGate,
            };
            await using var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await session.InitializeAsync(connection, Options(workspace), CancellationToken.None);

            Task<AccountStatus> logout = session.LogoutAccountAsync(CancellationToken.None);
            await connection.LogoutRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Confirm logout through the real account/updated notification, then close the connection before its RPC response.
            notificationGate.TrySetResult();
            await connection.LogoutNotificationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            responseGate.TrySetException(new JsonRpcConnectionClosedException("The fake connection closed before owner invalidation."));

            AccountStatus result = await logout.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(AccountState.SignedOut, result.State);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailedOverlappingLogoutDoesNotHideConfirmedLogout()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-logout-overlap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var requestBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFailureGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Keep the response pending so owner invalidation stops the transport before the RPC replies.
            var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new RecordingConnection(workspace)
            {
                AccountEmail = "a@example.test",
                EmitAccountUpdatedDuringLogout = true,
                LogoutRequestBarrier = requestBarrier,
                FailLogoutRequestCall = 1,
                LogoutFailureGate = firstFailureGate,
                EmitAccountUpdatedDuringLogoutCall = 2,
                LogoutNotificationGate = notificationGate,
                LogoutResponseGate = responseGate,
            };
            var second = new RecordingConnection(workspace);
            var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(first, second), session);
            await using var client = new RpcClientChannel(worker);
            ConnectionTargetSnapshot ownerA = (await worker.ConnectAsync(Options(workspace), CancellationToken.None)).Target!;

            Task<AccountStatus> firstLogout = worker.LogoutAccountAsync(Request<LogoutAccountRequest>(ownerA), CancellationToken.None);
            Task<AccountStatus> secondLogout = worker.LogoutAccountAsync(Request<LogoutAccountRequest>(ownerA), CancellationToken.None);
            await first.LogoutRequestsSynchronized.Task.WaitAsync(TimeSpan.FromSeconds(5));
            requestBarrier.TrySetResult();

            firstFailureGate.TrySetResult();
            Task<AccountStatus> failedLogout = await Task.WhenAny(firstLogout, secondLogout)
                .WaitAsync(TimeSpan.FromSeconds(5));
            AccountStatus failedResult = await failedLogout.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(AccountState.Unavailable, failedResult.State);
            Task<AccountStatus> confirmedLogout = ReferenceEquals(failedLogout, firstLogout)
                ? secondLogout
                : firstLogout;

            notificationGate.TrySetResult();
            AccountStatus secondResult = await confirmedLogout.WaitAsync(TimeSpan.FromSeconds(5));
            WorkerNotification<WorkerStatus> ready = await client.WaitForAsync(notification =>
                notification.Value.State == WorkerConnectionState.Ready
                && notification.OwnerGeneration > ownerA.OwnerGeneration).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(AccountState.SignedOut, secondResult.State);
            Assert.AreNotEqual(ownerA.StatePartitionFingerprint, ready.StatePartitionFingerprint);
            Assert.AreEqual(1, second.MethodCount("initialize"));
            Assert.AreEqual(0, second.MethodCount("account/logout"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task OverlappingLogoutFailureAfterConfirmationDoesNotPublishUnavailable()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-logout-confirmed-overlap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var requestBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failureGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new RecordingConnection(workspace)
            {
                AccountEmail = "a@example.test",
                EmitAccountUpdatedDuringLogout = true,
                LogoutRequestBarrier = requestBarrier,
                LogoutNotificationGate = notificationGate,
                EmitAccountUpdatedDuringLogoutCall = 1,
                FailLogoutRequestAfterNotificationCall = 2,
                LogoutFailureGate = failureGate,
                LogoutResponseGate = responseGate,
            };
            var accountStates = new ConcurrentQueue<AccountState>();
            await using var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            session.AccountStatusChanged += (status, _) =>
            {
                accountStates.Enqueue(status.State);
                return Task.CompletedTask;
            };
            await session.InitializeAsync(connection, Options(workspace), CancellationToken.None);
            Assert.AreEqual(AccountState.SignedIn, (await session.GetAccountStatusAsync(CancellationToken.None)).State);

            Task<AccountStatus> firstLogout = session.LogoutAccountAsync(CancellationToken.None);
            Task<AccountStatus> secondLogout = session.LogoutAccountAsync(CancellationToken.None);
            await connection.LogoutRequestsSynchronized.Task.WaitAsync(TimeSpan.FromSeconds(5));
            requestBarrier.TrySetResult();
            notificationGate.TrySetResult();
            await connection.LogoutNotificationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            failureGate.TrySetResult();
            Task<AccountStatus> failedAfterConfirmation = await Task.WhenAny(firstLogout, secondLogout)
                .WaitAsync(TimeSpan.FromSeconds(5));
            AccountStatus failureResult = await failedAfterConfirmation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(AccountState.SignedOut, failureResult.State);
            Assert.IsFalse(accountStates.Contains(AccountState.Unavailable),
                "A late failure from the retired owner must not publish Unavailable over the confirmed sign-out.");

            Task<AccountStatus> notificationLogout = ReferenceEquals(failedAfterConfirmation, firstLogout)
                ? secondLogout
                : firstLogout;
            responseGate.TrySetResult();
            AccountStatus notificationResult = await notificationLogout.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(AccountState.SignedOut, notificationResult.State);
            Assert.IsFalse(accountStates.Contains(AccountState.Unavailable));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task AuthenticatedAccountUpdateDuringLogoutIsNotConfirmedAsSignOut()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-logout-authenticated-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new RecordingConnection(workspace)
            {
                AccountEmail = "before@example.test",
                LogoutResponseGate = responseGate,
            };
            await using var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await session.InitializeAsync(connection, Options(workspace), CancellationToken.None);
            Assert.AreEqual(AccountState.SignedIn, (await session.GetAccountStatusAsync(CancellationToken.None)).State);

            Task<AccountStatus> logout = session.LogoutAccountAsync(CancellationToken.None);
            await connection.LogoutRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            connection.AccountEmail = "after@example.test";
            await connection.EmitNotificationAsync("account/updated", new { authMode = "chatgpt", planType = "plus" });

            Assert.AreEqual(AccountState.Unavailable, session.InvalidatedAccountState);
            Assert.IsFalse(session.InvalidatedByOwnerAction);
            connection.Stop();
            await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(() => logout.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoginCompletionDuringLogoutIsNotConfirmedAsSignOut()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-logout-login-completed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new RecordingConnection(workspace)
            {
                AccountEmail = "before@example.test",
                LogoutResponseGate = responseGate,
            };
            await using var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await session.InitializeAsync(connection, Options(workspace), CancellationToken.None);
            await session.GetAccountStatusAsync(CancellationToken.None);
            StartAccountLoginResult login = await session.StartAccountLoginAsync(CancellationToken.None);

            Task<AccountStatus> logout = session.LogoutAccountAsync(CancellationToken.None);
            await connection.LogoutRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await connection.EmitNotificationAsync("account/login/completed", new { loginId = login.LoginId, success = true });

            Assert.AreEqual(AccountState.Unavailable, session.InvalidatedAccountState);
            Assert.IsTrue(session.InvalidatedByOwnerAction);
            connection.Stop();
            await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(() => logout.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task SuccessfulLogoutResponseRetiresContextBeforeDelayedSignOutNotification()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-logout-response-first-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var connection = new RecordingConnection(workspace) { AccountEmail = "before@example.test" };
            var accountStates = new ConcurrentQueue<AccountState>();
            await using var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            session.AccountStatusChanged += (status, _) =>
            {
                accountStates.Enqueue(status.State);
                return Task.CompletedTask;
            };
            await session.InitializeAsync(connection, Options(workspace), CancellationToken.None);
            await session.GetAccountStatusAsync(CancellationToken.None);

            AccountStatus result = await session.LogoutAccountAsync(CancellationToken.None);
            Assert.AreEqual(AccountState.SignedOut, result.State);
            Assert.IsFalse(session.IsConnectionActive);
            Assert.AreEqual(AccountState.SignedOut, session.InvalidatedAccountState);

            await connection.EmitNotificationAsync("account/updated", new { authMode = (string?)null });

            Assert.AreEqual(AccountState.SignedOut, session.InvalidatedAccountState);
            Assert.IsFalse(accountStates.Contains(AccountState.Unavailable),
                "A delayed notification from the retired connection must not replace the successful logout state.");
            Assert.AreEqual(AccountState.SignedOut, accountStates.Last());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task OwnerSignInCompletionConnectsANewIsolatedOwnerAutomatically()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-login-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var first = new RecordingConnection(workspace);
            var second = new RecordingConnection(workspace);
            var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(first, second), session);
            await using var client = new RpcClientChannel(worker);
            ConnectionTargetSnapshot ownerA = (await worker.ConnectAsync(Options(workspace), CancellationToken.None)).Target!;

            // The session call avoids launching a browser; it records the owner's sign-in id.
            StartAccountLoginResult login = await session.StartAccountLoginAsync(CancellationToken.None);
            await first.EmitNotificationAsync("account/login/completed", new { loginId = login.LoginId, success = true });

            WorkerNotification<WorkerStatus> ready = await client.WaitForAsync(notification =>
                notification.Value.State == WorkerConnectionState.Ready
                && notification.OwnerGeneration > ownerA.OwnerGeneration).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreNotEqual(ownerA.StatePartitionFingerprint, ready.StatePartitionFingerprint);
            Assert.AreEqual(1, second.MethodCount("initialize"));
            Assert.AreEqual(1, second.MethodCount("account/read"));
            Assert.AreEqual(0, second.MethodCount("account/login/start"));
            Assert.IsFalse(client.Notifications.Any(notification => notification.Value.State == WorkerConnectionState.Degraded));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangedAccountStaysDegradedUntilAnExplicitReconnect()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-updated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var first = new RecordingConnection(workspace);
            var second = new RecordingConnection(workspace);
            var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(first, second), session);
            await using var client = new RpcClientChannel(worker);
            await worker.ConnectAsync(Options(workspace), CancellationToken.None);

            first.AccountEmail = "other@example.test";
            await first.EmitNotificationAsync("account/updated", new { authMode = "chatgpt", planType = "pro" });

            WorkerNotification<WorkerStatus> degraded = await client.WaitForAsync(notification =>
                notification.Value.State == WorkerConnectionState.Degraded).WaitAsync(TimeSpan.FromSeconds(5));
            StringAssert.Contains(degraded.Value.Message, "Reconnect");
            Assert.AreEqual(0, second.MethodCount("initialize"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task AccountUpdatedDuringStartupForTheSameAccountKeepsTheConnectionReady()
    {
        // The CLI may send account/updated shortly after startup without any account change.
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var first = new RecordingConnection(workspace)
            {
                AccountEmail = "a@example.test",
                EmitAccountUpdatedDuringRead = true,
            };
            var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(first), session);
            await using var client = new RpcClientChannel(worker);

            WorkerStatus status = await worker.ConnectAsync(Options(workspace), CancellationToken.None);
            await first.EmitNotificationAsync("account/updated", new { authMode = "chatgpt", planType = "plus" });

            Assert.AreEqual(WorkerConnectionState.Ready, status.State);
            Assert.IsTrue(session.IsConnectionActive);
            Assert.AreEqual(status.Target!.OwnerGeneration, session.OwnerGeneration);
            Assert.IsFalse(client.Notifications.Any(notification => notification.Value.State == WorkerConnectionState.Degraded));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task RateLimitPushFromAReplacedOwnerIsNotPublished()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"worker-owner-ratelimit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var first = new RecordingConnection(workspace);
            var second = new RecordingConnection(workspace);
            var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), new SequenceProcessHost(first, second), session);
            await using var client = new RpcClientChannel(worker);
            ConnectionTargetSnapshot ownerA = (await worker.ConnectAsync(Options(workspace), CancellationToken.None)).Target!;
            object rateLimits = new { rateLimits = new { primary = new { usedPercent = 10 } } };
            await first.EmitNotificationAsync("account/rateLimits/updated", rateLimits);
            await client.WaitForRateLimitsAsync(ownerA.OwnerGeneration).WaitAsync(TimeSpan.FromSeconds(5));

            ConnectionTargetSnapshot ownerB = (await worker.ConnectAsync(Options(workspace), CancellationToken.None)).Target!;
            await first.EmitNotificationAsync("account/rateLimits/updated", rateLimits);
            await second.EmitNotificationAsync("account/rateLimits/updated", rateLimits);
            await client.WaitForRateLimitsAsync(ownerB.OwnerGeneration).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, client.RateLimitOwnerGenerations.Count(generation => generation == ownerA.OwnerGeneration));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static TRequest Request<TRequest>(ConnectionTargetSnapshot target)
        where TRequest : OwnerScopedRequest, new()
        => new()
        {
            StatePartitionFingerprint = target.StatePartitionFingerprint,
            OwnerGeneration = target.OwnerGeneration,
            ConnectionGeneration = target.Generation,
        };

    private static WorkerOptions Options(string workspace) => new()
    {
        WorkingDirectory = workspace,
        ExtensionVersion = "test",
    };

    private static void AssertMutationRequests(RecordingConnection connection, string threadId, string turnMarker)
    {
        (string Method, JsonElement Parameters)[] mutations = connection.Requests
            .Where(request => request.Method is "thread/start" or "turn/start")
            .ToArray();
        CollectionAssert.AreEqual(new[] { "thread/start", "turn/start" }, mutations.Select(request => request.Method).ToArray());
        Assert.AreEqual(threadId, mutations[1].Parameters.GetProperty("threadId").GetString());
        StringAssert.Contains(mutations[1].Parameters.GetRawText(), turnMarker);
    }

    private sealed class RpcClientChannel : IAsyncDisposable
    {
        private readonly JsonRpc workerRpc;
        private readonly JsonRpc clientRpc;
        private readonly Observer observer = new();

        public RpcClientChannel(WorkerRpcService worker)
        {
            var clientToWorker = new Pipe();
            var workerToClient = new Pipe();
            workerRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                workerToClient.Writer.AsStream(),
                clientToWorker.Reader.AsStream(),
                new SystemTextJsonFormatter()));
            clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                clientToWorker.Writer.AsStream(),
                workerToClient.Reader.AsStream(),
                new SystemTextJsonFormatter()));
            clientRpc.AddLocalRpcTarget(observer);
            worker.AttachClient(workerRpc);
            workerRpc.StartListening();
            clientRpc.StartListening();
        }

        public Task<WorkerNotification<WorkerStatus>> BusyNotification => observer.BusyNotification;

        public IReadOnlyCollection<WorkerNotification<WorkerStatus>> Notifications => observer.Notifications;

        public Task<WorkerNotification<WorkerStatus>> WaitForReadyNotificationAsync(ConnectionTargetSnapshot target)
            => observer.WaitForReadyNotificationAsync(target);

        public IReadOnlyCollection<long> RateLimitOwnerGenerations => observer.RateLimitOwnerGenerations;

        public IReadOnlyCollection<WorkerNotification<RateLimitsResult>> RateLimitNotifications => observer.RateLimitNotifications;

        public async Task<WorkerNotification<WorkerStatus>> WaitForAsync(Func<WorkerNotification<WorkerStatus>, bool> predicate)
        {
            while (true)
            {
                WorkerNotification<WorkerStatus>? match = observer.Notifications.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(10);
            }
        }

        public async Task WaitForRateLimitsAsync(long ownerGeneration)
        {
            while (!observer.RateLimitOwnerGenerations.Contains(ownerGeneration))
            {
                await Task.Delay(10);
            }
        }

        public ValueTask DisposeAsync()
        {
            workerRpc.Dispose();
            clientRpc.Dispose();
            return ValueTask.CompletedTask;
        }

        private sealed class Observer
        {
            private readonly ConcurrentQueue<WorkerNotification<WorkerStatus>> notifications = new();
            private readonly ConcurrentQueue<long> rateLimitOwnerGenerations = new();
            private readonly ConcurrentQueue<WorkerNotification<RateLimitsResult>> rateLimitNotifications = new();
            private readonly ConcurrentDictionary<(string Partition, long Generation), TaskCompletionSource<WorkerNotification<WorkerStatus>>> readyNotifications = new();
            private readonly TaskCompletionSource<WorkerNotification<WorkerStatus>> busyNotification =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<WorkerNotification<WorkerStatus>> BusyNotification => busyNotification.Task;

            public IReadOnlyCollection<WorkerNotification<WorkerStatus>> Notifications => notifications.ToArray();

            public IReadOnlyCollection<long> RateLimitOwnerGenerations => rateLimitOwnerGenerations.ToArray();

            public IReadOnlyCollection<WorkerNotification<RateLimitsResult>> RateLimitNotifications => rateLimitNotifications.ToArray();

            [JsonRpcMethod("observer/rateLimitsChanged", UseSingleObjectParameterDeserialization = true)]
            public void OnRateLimitsChanged(RateLimitsChangedArgs args)
            {
                if (args.Notification is { } notification)
                {
                    rateLimitOwnerGenerations.Enqueue(notification.OwnerGeneration);
                    rateLimitNotifications.Enqueue(notification);
                }
            }

            public Task<WorkerNotification<WorkerStatus>> WaitForReadyNotificationAsync(ConnectionTargetSnapshot target)
            {
                var key = (Partition: target.StatePartitionFingerprint!, Generation: target.OwnerGeneration);
                WorkerNotification<WorkerStatus>? existing = notifications.FirstOrDefault(notification =>
                    notification.Value.State == WorkerConnectionState.Ready
                    && notification.StatePartitionFingerprint == key.Partition
                    && notification.OwnerGeneration == key.Generation);
                if (existing is not null)
                {
                    return Task.FromResult(existing);
                }

                TaskCompletionSource<WorkerNotification<WorkerStatus>> waiter = readyNotifications.GetOrAdd(
                    key,
                    static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                existing = notifications.FirstOrDefault(notification =>
                    notification.Value.State == WorkerConnectionState.Ready
                    && notification.StatePartitionFingerprint == key.Partition
                    && notification.OwnerGeneration == key.Generation);
                if (existing is not null)
                {
                    waiter.TrySetResult(existing);
                }

                return waiter.Task;
            }

            [JsonRpcMethod("observer/stateChanged", UseSingleObjectParameterDeserialization = true)]
            public void OnStateChanged(StateChangedArgs args)
            {
                if (args.Notification is { } notification)
                {
                    notifications.Enqueue(notification);
                    if (notification.Value.State == WorkerConnectionState.Ready)
                    {
                        readyNotifications.TryGetValue(
                            (notification.StatePartitionFingerprint!, notification.OwnerGeneration),
                            out TaskCompletionSource<WorkerNotification<WorkerStatus>>? waiter);
                        waiter?.TrySetResult(notification);
                    }
                }

                WorkerStatus? status = args.Notification?.Value;
                if (status?.State == WorkerConnectionState.Busy && status.TurnId is null)
                {
                    busyNotification.TrySetResult(args.Notification!);
                }
            }
        }

        private sealed class StateChangedArgs
        {
            [JsonPropertyName("notification")]
            public WorkerNotification<WorkerStatus>? Notification { get; set; }
        }

        private sealed class RateLimitsChangedArgs
        {
            [JsonPropertyName("notification")]
            public WorkerNotification<RateLimitsResult>? Notification { get; set; }
        }
    }

    private sealed class SwitchingProcessHost(RecordingConnection ownerA, RecordingConnection ownerB) : ICodexProcessHost
    {
        private IJsonRpcConnection? connection;

        public event EventHandler<string>? StandardErrorReceived { add { } remove { } }

        public event EventHandler<int>? Exited { add { } remove { } }

        public int? ProcessId => 100;

        public IJsonRpcConnection? Connection => connection;

        public Task StartAsync(string codexPath, string workingDirectory, WindowsSandboxBackendPreference sandboxBackend, CancellationToken cancellationToken)
        {
            connection = string.Equals(workingDirectory, ownerA.WorkingDirectory, StringComparison.Ordinal)
                ? ownerA
                : ownerB;
            return Task.CompletedTask;
        }

        public Task StartRemoteAsync(RemoteConnectionRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            (connection as RecordingConnection)?.Stop();
            connection = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Each start hands out the next connection, so a reconnect for the same options gets a new one.
    private sealed class SequenceProcessHost(params RecordingConnection[] connections) : ICodexProcessHost
    {
        private int next;
        private IJsonRpcConnection? connection;

        public event EventHandler<string>? StandardErrorReceived { add { } remove { } }

        public event EventHandler<int>? Exited { add { } remove { } }

        public int? ProcessId => 100;

        public IJsonRpcConnection? Connection => connection;

        public Task StartAsync(string codexPath, string workingDirectory, WindowsSandboxBackendPreference sandboxBackend, CancellationToken cancellationToken)
        {
            connection = connections[next++];
            return Task.CompletedTask;
        }

        public Task StartRemoteAsync(RemoteConnectionRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            (connection as RecordingConnection)?.Stop();
            connection = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingConnection(string workingDirectory) : IJsonRpcConnection
    {
        private readonly ConcurrentQueue<string> methods = new();
        private readonly ConcurrentQueue<(string Method, JsonElement Parameters)> requests = new();
        private readonly TaskCompletionSource turnStartSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseTurnStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource stoppedTokenSource = new();
        private int logoutCallCount;
        private int stopped;

        public bool DelayTurnStart { get; set; }

        public string? AccountEmail { get; set; }

        public bool EmitAccountUpdatedDuringRead { get; set; }

        public bool EmitAccountUpdatedDuringLogout { get; set; }

        public TaskCompletionSource? LogoutResponseGate { get; set; }

        public TaskCompletionSource LogoutRequestSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LogoutNotificationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? LogoutRequestBarrier { get; set; }

        public TaskCompletionSource? LogoutFailureGate { get; set; }

        public TaskCompletionSource? LogoutNotificationGate { get; set; }

        public int FailLogoutRequestCall { get; set; }

        public int FailLogoutRequestAfterNotificationCall { get; set; }

        public int EmitAccountUpdatedDuringLogoutCall { get; set; } = 1;

        public TaskCompletionSource LogoutRequestsSynchronized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TurnStartSeen => turnStartSeen.Task;

        public void ReleaseTurnStart() => releaseTurnStart.TrySetResult();

        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<Exception?>? Closed
        {
            add { }
            remove { }
        }

        public string WorkingDirectory => workingDirectory;

        public int MethodCount(string method) => methods.Count(item => string.Equals(item, method, StringComparison.Ordinal));

        public IReadOnlyCollection<(string Method, JsonElement Parameters)> Requests => requests.ToArray();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            methods.Enqueue(method);
            requests.Enqueue((method, parameters is null
                ? JsonDocument.Parse("{}").RootElement.Clone()
                : JsonSerializer.SerializeToElement(parameters)));
            if (method == "turn/start" && DelayTurnStart)
            {
                turnStartSeen.TrySetResult();
                await releaseTurnStart.Task.WaitAsync(cancellationToken);
            }

            if (method == "account/read" && EmitAccountUpdatedDuringRead)
            {
                EmitAccountUpdatedDuringRead = false;
                await EmitNotificationAsync("account/updated", new { authMode = "chatgpt", planType = "plus" });
            }

            if (method == "account/logout")
            {
                int call = Interlocked.Increment(ref logoutCallCount);
                LogoutRequestSeen.TrySetResult();
                if (call >= 2)
                {
                    LogoutRequestsSynchronized.TrySetResult();
                }

                if (EmitAccountUpdatedDuringLogout && LogoutRequestBarrier is not null)
                {
                    await WaitForPendingRequestAsync(LogoutRequestBarrier.Task, cancellationToken);
                }

                if (EmitAccountUpdatedDuringLogout && call == FailLogoutRequestCall)
                {
                    if (LogoutFailureGate is not null)
                    {
                        await WaitForPendingRequestAsync(LogoutFailureGate.Task, cancellationToken);
                    }

                    throw new InvalidOperationException("The first overlapping logout failed before its notification.");
                }

                if (EmitAccountUpdatedDuringLogout && call == FailLogoutRequestAfterNotificationCall)
                {
                    await WaitForPendingRequestAsync(LogoutNotificationCompleted.Task, cancellationToken);
                    if (LogoutFailureGate is not null)
                    {
                        await WaitForPendingRequestAsync(LogoutFailureGate.Task, cancellationToken);
                    }

                    throw new InvalidOperationException("The overlapping logout failed after its notification.");
                }

                if (EmitAccountUpdatedDuringLogout && call == EmitAccountUpdatedDuringLogoutCall)
                {
                    if (LogoutNotificationGate is not null)
                    {
                        await WaitForPendingRequestAsync(LogoutNotificationGate.Task, cancellationToken);
                    }

                    AccountEmail = null;
                    await EmitNotificationAsync("account/updated", new { authMode = (string?)null });
                    LogoutNotificationCompleted.TrySetResult();
                }

                if (LogoutResponseGate is not null)
                {
                    await WaitForPendingRequestAsync(LogoutResponseGate.Task, cancellationToken);
                }

                if (Volatile.Read(ref stopped) != 0)
                {
                    throw new JsonRpcConnectionClosedException("The fake connection stopped before the response arrived.");
                }
            }

            JsonElement result = method switch
            {
                "thread/start" => JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = "thread-a", cwd = workingDirectory, model = "gpt-5-codex" },
                }),
                "model/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { model = "gpt-5-codex", isDefault = true } },
                    nextCursor = (string?)null,
                }),
                "turn/start" => JsonSerializer.SerializeToElement(new { turn = new { id = "turn-a" } }),
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Target}" }),
                "account/gatewayOAuth/read" => JsonSerializer.SerializeToElement(new
                {
                    providerId = "test-provider",
                    providerName = "Test provider",
                    required = false,
                    status = (string?)null,
                }),
                "account/read" => JsonSerializer.SerializeToElement(new
                {
                    account = AccountEmail is null
                        ? null
                        : (object)new { type = "chatgpt", email = AccountEmail, planType = "plus" },
                }),
                "account/login/start" => JsonSerializer.SerializeToElement(new
                {
                    type = "chatgpt",
                    loginId = "login-1",
                    authUrl = "https://auth.example.test/start",
                }),
                _ => JsonSerializer.SerializeToElement(new { }),
            };
            return result;
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task EmitNotificationAsync(string method, object parameters)
            => NotificationReceived?.Invoke(
                new JsonRpcMessage
                {
                    Method = method,
                    Params = JsonSerializer.SerializeToElement(parameters),
                },
                CancellationToken.None) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Stop()
        {
            Interlocked.Exchange(ref stopped, 1);
            stoppedTokenSource.Cancel();
        }

        private async Task WaitForPendingRequestAsync(Task task, CancellationToken cancellationToken)
        {
            try
            {
                await task.WaitAsync(cancellationToken).WaitAsync(stoppedTokenSource.Token);
            }
            catch (OperationCanceledException) when (stoppedTokenSource.IsCancellationRequested)
            {
                throw new JsonRpcConnectionClosedException("The fake connection stopped before the response arrived.");
            }
        }
    }
}
