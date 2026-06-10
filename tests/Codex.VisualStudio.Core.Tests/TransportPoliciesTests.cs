using Codex.AppServer.Protocol;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class TransportPoliciesTests
{
    [TestMethod]
    public void RetryPolicy_RetriesOnlyIdempotentOverloadResponses()
    {
        var policy = new JsonRpcRetryPolicy();

        RetryDecision retry = policy.Evaluate(new JsonRpcRemoteException(-32001, "overloaded"), 0, isIdempotent: true);
        RetryDecision mutation = policy.Evaluate(new JsonRpcRemoteException(-32001, "overloaded"), 0, isIdempotent: false);
        RetryDecision exhausted = policy.Evaluate(new JsonRpcRemoteException(-32001, "overloaded"), 3, isIdempotent: true);

        Assert.IsTrue(retry.ShouldRetry);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), retry.Delay);
        Assert.IsFalse(mutation.ShouldRetry);
        Assert.IsFalse(exhausted.ShouldRetry);
    }

    [TestMethod]
    public void WebSocketPolicy_RequiresExplicitLoopbackAndToken()
    {
        var policy = new WebSocketTransportSecurityPolicy();

        Assert.IsFalse(policy.Validate(false, new Uri("ws://127.0.0.1:8080"), new string('a', 32)).IsAllowed);
        Assert.IsFalse(policy.Validate(true, new Uri("wss://example.com"), new string('a', 32)).IsAllowed);
        Assert.IsFalse(policy.Validate(true, new Uri("ws://localhost:8080"), "short").IsAllowed);
        Assert.IsTrue(policy.Validate(true, new Uri("ws://127.0.0.1:8080"), new string('a', 32)).IsAllowed);
    }

    [TestMethod]
    public async Task RetryExtension_RetriesIdempotentRequestAfterOverload()
    {
        await using var connection = new OverloadedConnection();

        System.Text.Json.JsonElement result = await connection.SendIdempotentRequestAsync(
            "model/list",
            new { },
            TimeSpan.FromSeconds(1),
            new JsonRpcRetryPolicy(initialDelay: TimeSpan.Zero),
            CancellationToken.None);

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.AreEqual(3, connection.Attempts);
    }

    private sealed class OverloadedConnection : IJsonRpcConnection
    {
        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event Func<JsonRpcMessage, CancellationToken, Task<System.Text.Json.JsonElement>>? RequestReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<Exception?>? Closed
        {
            add { }
            remove { }
        }

        public int Attempts { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<System.Text.Json.JsonElement> SendRequestAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts < 3)
            {
                throw new JsonRpcRemoteException(JsonRpcRetryPolicy.ServerOverloadedCode, "overloaded");
            }

            return Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true }));
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
