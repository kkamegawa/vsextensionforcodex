using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class CodexSessionServiceTests
{
    [TestMethod]
    public async Task ThreadListUsesPagingAndAllSupportedSources()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = Array.Empty<object>(),
                    nextCursor = "next",
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadPage page = await service.ListThreadsAsync("cursor", CancellationToken.None);

        var matching = connection.Requests.Where(item => item.Method == "thread/list").ToList();
        Assert.AreEqual(1, matching.Count);
        RecordedRequest request = matching[0];
        JsonElement parameters = JsonSerializer.SerializeToElement(request.Parameters);
        Assert.AreEqual(25, parameters.GetProperty("limit").GetInt32());
        Assert.AreEqual("cursor", parameters.GetProperty("cursor").GetString());
        CollectionAssert.AreEqual(
            new[] { "cli", "vscode", "appServer" },
            parameters.GetProperty("sourceKinds").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.AreEqual("next", page.NextCursor);
    }

    [TestMethod]
    public async Task SteerRejectsStaleExpectedTurn()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.StartTurnAsync(new StartTurnRequest { ThreadId = "thread-1", Text = "hello" }, CancellationToken.None);

        bool threw = false;
        try
        {
            await service.SteerTurnAsync(
                new SteerTurnRequest { ThreadId = "thread-1", ExpectedTurnId = "stale", Text = "more" },
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Expected InvalidOperationException for stale turn.");
    }

    private static CodexSessionService CreateService()
        => new(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());

    private static WorkerOptions Options() => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        ExtensionVersion = "test",
    };

    private sealed class RecordingConnection : IJsonRpcConnection
    {
        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived
        {
            add { }
            remove { }
        }

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

        public Func<string, object?, JsonElement> Handler { get; set; } = (_, _) => JsonSerializer.SerializeToElement(new { });

        public List<RecordedRequest> Requests { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(method, parameters));
            return Task.FromResult(Handler(method, parameters));
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record RecordedRequest(string Method, object? Parameters);
}
