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
                    models = new[] { new { id = "gpt-5-codex" } },
                    defaultModel = "gpt-5-codex",
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

            public Task<WorkerStatus> TurnIdSeen => turnIdSeen.Task;

            [JsonRpcMethod("observer/stateChanged", UseSingleObjectParameterDeserialization = true)]
            public void OnStateChanged(StateChangedArgs args)
            {
                if (args.Status?.TurnId is not null)
                {
                    turnIdSeen.TrySetResult(args.Status);
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
        public event EventHandler<string>? StandardErrorReceived { add { } remove { } }

        public event EventHandler<int>? Exited { add { } remove { } }

        public int? ProcessId => 4242;

        public IJsonRpcConnection? Connection => null;

        public Task StartAsync(string codexPath, string workingDirectory, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubConnection : IJsonRpcConnection
    {
        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived { add { } remove { } }

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived { add { } remove { } }

        public event EventHandler<Exception?>? Closed { add { } remove { } }

        public Func<string, JsonElement> Handler { get; set; } = _ => JsonSerializer.SerializeToElement(new { });

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(Handler(method));

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
