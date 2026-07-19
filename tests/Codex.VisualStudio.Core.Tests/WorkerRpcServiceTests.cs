using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;
using StreamJsonRpc;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class WorkerRpcServiceTests
{
    [TestMethod]
    public async Task StartTurn_PublishesBusyStatusCarryingTheTurnId()
    {
        // The interrupt button binds to IsTurnActive (Status.TurnId is not null). The worker must
        // publish a Busy status AFTER the turn id is known; otherwise the client only ever receives
        // Busy with TurnId = null and the interrupt button never appears while the turn runs.
        var connection = new StubConnection
        {
            Handler = method => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };

        // The worker owns disposal of the session, so it is not disposed separately here.
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await session.InitializeAsync(connection, Options(), CancellationToken.None);

        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(), session);
        await using var client = new ClientChannel(worker);

        await worker.StartTurnAsync(new StartTurnRequest { ThreadId = "thread-1", Text = "hello" }, CancellationToken.None);

        WorkerStatus published = await client.TurnIdSeen.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WorkerConnectionState.Busy, published.State);
        Assert.AreEqual("turn-1", published.TurnId);
    }

    [TestMethod]
    public async Task ListModels_DelegatesToSession()
    {
        var connection = new StubConnection
        {
            Handler = method => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { model = "gpt-5-codex", isDefault = true } },
                    nextCursor = (string?)null,
                })
                : JsonSerializer.SerializeToElement(new { }),
        };

        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await session.InitializeAsync(connection, Options(), CancellationToken.None);

        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(), session);

        ListModelsResult result = await worker.ListModelsAsync(CancellationToken.None);

        Assert.AreEqual(1, result.Models.Count);
        Assert.AreEqual("gpt-5-codex", result.Models[0].Id);
        Assert.AreEqual("gpt-5-codex", result.DefaultModel);
    }

    [TestMethod]
    public async Task UnsupportedSlashOperationDoesNotDegradeConnection()
    {
        var connection = new StubConnection
        {
            Handler = method => method switch
            {
                "thread/compact/start" => throw new JsonRpcRemoteException(-32601, "Method not found"),
                "account/read" => JsonSerializer.SerializeToElement(new { account = (object?)null }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(
            new SecretRedactor(),
            new FakeProcessHost(connection),
            session);

        WorkerStatus connected = await worker.ConnectAsync(Options(), CancellationToken.None);
        CompactThreadResult result = await worker.CompactThreadAsync(
            new CompactThreadRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        WorkerStatus afterOperation = await worker.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Ready, connected.State);
        Assert.IsFalse(result.IsSupported);
        Assert.AreEqual(WorkerConnectionState.Ready, afterOperation.State);
    }

    [TestMethod]
    public async Task CompactionCompletionRestoresReadyWhenNoTurnIsActive()
    {
        // thread/compact/start marks the worker Busy, but the app-server may report completion
        // only through the context/compacted notification instead of turn/completed. The worker
        // must return to Ready so queued slash commands are not blocked forever.
        var connection = new StubConnection
        {
            Handler = method => method == "account/read"
                ? JsonSerializer.SerializeToElement(new { account = (object?)null })
                : JsonSerializer.SerializeToElement(new { }),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(
            new SecretRedactor(),
            new FakeProcessHost(connection),
            session);

        await worker.ConnectAsync(Options(), CancellationToken.None);
        CompactThreadResult result = await worker.CompactThreadAsync(
            new CompactThreadRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        WorkerStatus during = await worker.GetStatusAsync(CancellationToken.None);

        await connection.EmitNotificationAsync(
            "context/compacted",
            new { threadId = "thread-1", turnId = "turn-9" });
        WorkerStatus after = await worker.GetStatusAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSupported);
        Assert.AreEqual(WorkerConnectionState.Busy, during.State);
        Assert.AreEqual(WorkerConnectionState.Ready, after.State);
    }

    [TestMethod]
    public async Task ConnectionStatusPropagatesVersionAndClearsItAfterInitializationFailure()
    {
        bool failInitialization = false;
        var connection = new StubConnection
        {
            Handler = method => method switch
            {
                "initialize" when failInitialization => throw new InvalidOperationException("initialization failed"),
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/3.4.5" }),
                "account/read" => JsonSerializer.SerializeToElement(new { account = (object?)null }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(
            new SecretRedactor(),
            new FakeProcessHost(connection),
            session);

        WorkerStatus ready = await worker.ConnectAsync(Options(), CancellationToken.None);
        Assert.AreEqual(WorkerConnectionState.Ready, ready.State);
        Assert.AreEqual("3.4.5", ready.CodexVersion);

        failInitialization = true;
        WorkerStatus degraded = await worker.ConnectAsync(Options(), CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Degraded, degraded.State);
        Assert.IsNull(degraded.CodexVersion);
        Assert.IsNull((await worker.GetStatusAsync(CancellationToken.None)).CodexVersion);
    }

    [TestMethod]
    public async Task ConnectedVersionSurvivesBusyApprovalAndReadyTransitions()
    {
        var connection = new StubConnection
        {
            Handler = method => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/4.5.6" }),
                "account/read" => JsonSerializer.SerializeToElement(new { account = (object?)null }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(
            new SecretRedactor(),
            new FakeProcessHost(connection),
            session);
        var approvalSeen = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ApprovalRequested += (request, _) =>
        {
            approvalSeen.TrySetResult(request);
            return Task.CompletedTask;
        };

        WorkerStatus initialReady = await worker.ConnectAsync(Options(), CancellationToken.None);
        await worker.CompactThreadAsync(
            new CompactThreadRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        WorkerStatus busy = await worker.GetStatusAsync(CancellationToken.None);

        Task<JsonElement> approvalTask = connection.EmitRequestAsync(
            "approval-1",
            "item/commandExecution/requestApproval",
            new { command = "dotnet build", cwd = Options().WorkingDirectory, threadId = "thread-1", turnId = "turn-1" });
        ApprovalRequest approval = await approvalSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        WorkerStatus waiting = await worker.GetStatusAsync(CancellationToken.None);

        await session.ResolveApprovalAsync(
            new ResolveApprovalRequest { RequestId = approval.RequestId, Decision = ApprovalDecision.Decline },
            CancellationToken.None);
        await approvalTask;
        await connection.EmitNotificationAsync(
            "context/compacted",
            new { threadId = "thread-1", turnId = "turn-1" });
        WorkerStatus finalReady = await worker.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Ready, initialReady.State);
        Assert.AreEqual("4.5.6", initialReady.CodexVersion);
        Assert.AreEqual(WorkerConnectionState.Busy, busy.State);
        Assert.AreEqual("4.5.6", busy.CodexVersion);
        Assert.AreEqual(WorkerConnectionState.WaitingForApproval, waiting.State);
        Assert.AreEqual("4.5.6", waiting.CodexVersion);
        Assert.AreEqual(WorkerConnectionState.Ready, finalReady.State);
        Assert.AreEqual("4.5.6", finalReady.CodexVersion);
    }

    [TestMethod]
    public async Task ThreadSettingsUpdatePublishesEffectiveApprovalStateAcrossWorkerContract()
    {
        var connection = new StubConnection
        {
            Handler = method => method == "thread/start"
                ? JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = "thread-1" },
                    approvalPolicy = "on-request",
                    approvalsReviewer = "user",
                    sandbox = new { type = "workspaceWrite" },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await session.InitializeAsync(connection, Options(), CancellationToken.None);
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(), session);
        await worker.StartThreadAsync(CancellationToken.None);
        await using var client = new ClientChannel(worker);

        await connection.EmitNotificationAsync(
            "thread/settings/updated",
            new
            {
                threadId = "thread-1",
                threadSettings = new
                {
                    activePermissionProfile = new { id = "review" },
                    approvalPolicy = "on-request",
                    approvalsReviewer = "auto_review",
                    sandboxPolicy = new { type = "workspaceWrite" },
                },
            });

        WorkerStatus published = await client.EffectiveStateSeen.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("review", published.EffectiveApprovalState!.ActivePermissionProfile);
        Assert.AreEqual("on-request", published.EffectiveApprovalState.ApprovalPolicy);
        Assert.AreEqual("auto_review", published.EffectiveApprovalState.ApprovalsReviewer);
        Assert.AreEqual("workspaceWrite", published.EffectiveApprovalState.SandboxMode);
    }

    private static WorkerOptions Options() => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        ExtensionVersion = "test",
    };

    // Captures observer/stateChanged notifications published by the worker over a real StreamJsonRpc
    // duplex so the test asserts the actual client-facing contract, not just internal state.
    private sealed class ClientChannel : IAsyncDisposable
    {
        private readonly JsonRpc workerRpc;
        private readonly JsonRpc clientRpc;
        private readonly Observer observer = new();

        public ClientChannel(WorkerRpcService worker)
        {
            var clientToWorker = new Pipe();
            var workerToClient = new Pipe();

            workerRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                workerToClient.Writer, clientToWorker.Reader, new SystemTextJsonFormatter()));
            clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                clientToWorker.Writer, workerToClient.Reader, new SystemTextJsonFormatter()));
            clientRpc.AddLocalRpcTarget(observer);

            worker.AttachClient(workerRpc);
            workerRpc.StartListening();
            clientRpc.StartListening();
        }

        public Task<WorkerStatus> TurnIdSeen => observer.TurnIdSeen;

        public Task<WorkerStatus> EffectiveStateSeen => observer.EffectiveStateSeen;

        public ValueTask DisposeAsync()
        {
            workerRpc.Dispose();
            clientRpc.Dispose();
            return ValueTask.CompletedTask;
        }

        private sealed class Observer
        {
            private readonly TaskCompletionSource<WorkerStatus> turnIdSeen =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<WorkerStatus> effectiveStateSeen =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<WorkerStatus> TurnIdSeen => turnIdSeen.Task;

            public Task<WorkerStatus> EffectiveStateSeen => effectiveStateSeen.Task;

            [JsonRpcMethod("observer/stateChanged", UseSingleObjectParameterDeserialization = true)]
            public void OnStateChanged(StateChangedArgs args)
            {
                if (args.Status?.TurnId is not null)
                {
                    turnIdSeen.TrySetResult(args.Status);
                }

                if (args.Status?.EffectiveApprovalState is not null)
                {
                    effectiveStateSeen.TrySetResult(args.Status);
                }
            }
        }

        private sealed class StateChangedArgs
        {
            [JsonPropertyName("status")]
            public WorkerStatus? Status { get; set; }
        }
    }

    private sealed class FakeProcessHost : ICodexProcessHost
    {
        private readonly IJsonRpcConnection? connection;

        public FakeProcessHost(IJsonRpcConnection? connection = null)
        {
            this.connection = connection;
        }

        public event EventHandler<string>? StandardErrorReceived { add { } remove { } }

        public event EventHandler<int>? Exited { add { } remove { } }

        public int? ProcessId => 4242;

        public IJsonRpcConnection? Connection => connection;

        public Task StartAsync(string codexPath, string workingDirectory, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubConnection : IJsonRpcConnection
    {
        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

        public event EventHandler<Exception?>? Closed { add { } remove { } }

        public Func<string, JsonElement> Handler { get; set; } = _ => JsonSerializer.SerializeToElement(new { });

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(Handler(method));

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

        public Task<JsonElement> EmitRequestAsync(string id, string method, object parameters)
            => RequestReceived?.Invoke(
                new JsonRpcMessage
                {
                    Id = JsonSerializer.SerializeToElement(id),
                    Method = method,
                    Params = JsonSerializer.SerializeToElement(parameters),
                },
                CancellationToken.None)
                ?? Task.FromResult(JsonSerializer.SerializeToElement(new { }));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
