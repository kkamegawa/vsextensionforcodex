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

            await ownerAConnection.EmitNotificationAsync("account/updated", new { authMode = "chatgpt" });
            Assert.AreEqual(ownerATarget.OwnerGeneration + 1, session.OwnerGeneration);
            Task<WorkerStatus> replacement = worker.ConnectAsync(Options(ownerBPath), CancellationToken.None);
            ownerAConnection.ReleaseTurnStart();

            JsonRpcConnectionClosedException turnFailure = await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(
                async () => await oldTurn.WaitAsync(TimeSpan.FromSeconds(5)));
            WorkerStatus ownerBStatus = await replacement.WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task UnsolicitedAccountChangeStaysDegradedUntilAnExplicitReconnect()
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
            private readonly ConcurrentDictionary<(string Partition, long Generation), TaskCompletionSource<WorkerNotification<WorkerStatus>>> readyNotifications = new();
            private readonly TaskCompletionSource<WorkerNotification<WorkerStatus>> busyNotification =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<WorkerNotification<WorkerStatus>> BusyNotification => busyNotification.Task;

            public IReadOnlyCollection<WorkerNotification<WorkerStatus>> Notifications => notifications.ToArray();

            public IReadOnlyCollection<long> RateLimitOwnerGenerations => rateLimitOwnerGenerations.ToArray();

            [JsonRpcMethod("observer/rateLimitsChanged", UseSingleObjectParameterDeserialization = true)]
            public void OnRateLimitsChanged(RateLimitsChangedArgs args)
            {
                if (args.Notification is { } notification)
                {
                    rateLimitOwnerGenerations.Enqueue(notification.OwnerGeneration);
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

        public Task StartAsync(string codexPath, string workingDirectory, CancellationToken cancellationToken)
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

        public Task StartAsync(string codexPath, string workingDirectory, CancellationToken cancellationToken)
        {
            connection = connections[next++];
            return Task.CompletedTask;
        }

        public Task StartRemoteAsync(RemoteConnectionRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            connection = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingConnection(string workingDirectory) : IJsonRpcConnection
    {
        private readonly ConcurrentQueue<string> methods = new();
        private readonly TaskCompletionSource turnStartSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseTurnStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DelayTurnStart { get; set; }

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

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            methods.Enqueue(method);
            if (method == "turn/start" && DelayTurnStart)
            {
                turnStartSeen.TrySetResult();
                await releaseTurnStart.Task.WaitAsync(cancellationToken);
            }

            JsonElement result = method switch
            {
                "thread/start" => JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = "thread-a", cwd = workingDirectory },
                }),
                "turn/start" => JsonSerializer.SerializeToElement(new { turn = new { id = "turn-a" } }),
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.1.0" }),
                "account/read" => JsonSerializer.SerializeToElement(new { account = (object?)null }),
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
    }
}
