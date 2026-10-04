using System.Text.Json;
using Codex.AppServer.Protocol;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class TransportPoliciesTests
{
    private static readonly string[] UnconditionalMethods =
    [
        "account/rateLimits/read",
        "thread/list",
        "thread/goal/get",
        "model/list",
        "permissionProfile/list",
        "mcpServerStatus/list",
    ];

    [TestMethod]
    public void Allowlist_ContainsExactlyTheTwelveReviewedReadOnlyMethods()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                "account/read", "account/rateLimits/read", "thread/list", "thread/goal/get",
                "model/list", "permissionProfile/list", "mcpServerStatus/list", "skills/list",
                "thread/read", "thread/turns/list", "thread/items/list", "thread/attachment/list",
            },
            ReadOnlyRequestAllowlist.Methods.ToArray());
    }

    [TestMethod]
    public void Allowlist_HistoryReadsRequireSafeThreadAndBoundedPageArguments()
    {
        Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/read",
            new { threadId = "thread-1", includeTurns = false }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/read",
            new { threadId = "thread-1", includeTurns = true }));
        Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/turns/list",
            new { threadId = "thread-1", cursor = (string?)null, limit = 50, itemsView = "summary" }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/turns/list",
            new { threadId = "thread-1", limit = 51 }));
        Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/items/list",
            new { threadId = "thread-1", turnId = (string?)null, cursor = (string?)null, limit = 100 }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/items/list",
            new { threadId = "thread-1", cursor = new { type = "item", itemId = "item-1" }, limit = 100 }));
        Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/items/list",
            new { threadId = "thread-1", turnId = "turn-1", cursor = new { type = "item", itemId = "item-1" }, limit = 100 }));
        Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/attachment/list",
            new { threadId = "thread-1", cursor = (string?)null, limit = 50 }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/attachment/list",
            new { threadId = "thread-1", limit = 51 }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable(
            "thread/attachment/list",
            new { threadId = "", limit = 50 }));
    }

    [TestMethod]
    public void Allowlist_UnconditionalMethodsAreRetryableWithAnyParameters()
    {
        foreach (string method in UnconditionalMethods)
        {
            Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable(method, new { }), method);
            Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable(method, null), method);
        }
    }

    [TestMethod]
    public void Allowlist_AccountReadRequiresExplicitFalseRefreshToken()
    {
        Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable("account/read", new { refreshToken = false }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("account/read", new { refreshToken = true }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("account/read", new { }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("account/read", null));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("account/read", new { refreshToken = "false" }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("account/read", new { refreshToken = 0 }));
    }

    [TestMethod]
    public void Allowlist_SkillsListRequiresExplicitFalseForceReload()
    {
        Assert.IsTrue(ReadOnlyRequestAllowlist.IsRetryable("skills/list", new { cwds = Array.Empty<string>(), forceReload = false }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("skills/list", new { cwds = Array.Empty<string>(), forceReload = true }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("skills/list", new { cwds = Array.Empty<string>() }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("skills/list", new { forceReload = (bool?)null }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("skills/list", new { forceReload = "false" }));
        Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable("skills/list", null));
    }

    [TestMethod]
    public void Allowlist_RejectsMutationsUnknownMethodsAndUnreviewedHistoryReads()
    {
        foreach (string method in new[]
        {
            "turn/start", "turn/steer", "turn/interrupt", "thread/start", "thread/resume", "thread/fork",
            "thread/goal/set", "thread/goal/clear", "thread/compact/start", "review/start", "feedback/upload",
            "account/login/start", "account/logout", "initialize", "thread/read", "thread/turns/list",
            "attachments/list", "unknown/method", "Account/Read",
        })
        {
            Assert.IsFalse(ReadOnlyRequestAllowlist.IsRetryable(method, new { refreshToken = false, forceReload = false }), method);
        }
    }

    [TestMethod]
    public void RetryPolicy_DelaysUseBaseScheduleWithBoundedJitter()
    {
        var low = new ReadOnlyRetryPolicy(jitterSource: static () => 0);
        var mid = new ReadOnlyRetryPolicy(jitterSource: static () => 0.5);
        var high = new ReadOnlyRetryPolicy(jitterSource: static () => 1);
        var invalid = new ReadOnlyRetryPolicy(jitterSource: static () => double.NaN);
        var outOfRange = new ReadOnlyRetryPolicy(jitterSource: static () => 7);
        double[] baseMs = [250, 500, 1000];
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(baseMs[i] * 0.8, low.GetDelay(i).TotalMilliseconds, 0.001);
            Assert.AreEqual(baseMs[i], mid.GetDelay(i).TotalMilliseconds, 0.001);
            Assert.AreEqual(baseMs[i] * 1.2, high.GetDelay(i).TotalMilliseconds, 0.001);
            Assert.AreEqual(baseMs[i], invalid.GetDelay(i).TotalMilliseconds, 0.001);
            Assert.AreEqual(baseMs[i] * 1.2, outOfRange.GetDelay(i).TotalMilliseconds, 0.001);
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => mid.GetDelay(3));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => mid.GetDelay(-1));
    }

    [TestMethod]
    public async Task ReadOnlyRequest_RetriesOverloadUpToFourSendsAndPreservesFinalError()
    {
        var connection = new ScriptedConnection(_ => throw new JsonRpcRemoteException(-32001, "overloaded"));
        var policy = new ReadOnlyRetryPolicy(jitterSource: static () => 0);

        JsonRpcRemoteException error = await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() =>
            connection.SendReadOnlyRequestAsync("model/list", new { }, TimeSpan.FromSeconds(30), CancellationToken.None, policy));

        Assert.AreEqual(-32001, error.Code);
        Assert.AreEqual(4, connection.Sends);
    }

    [TestMethod]
    public async Task ReadOnlyRequest_SucceedsAfterTransientOverload()
    {
        var connection = new ScriptedConnection(attempt => attempt < 3
            ? throw new JsonRpcRemoteException(-32001, "overloaded")
            : JsonSerializer.SerializeToElement(new { ok = true }));
        var policy = new ReadOnlyRetryPolicy(jitterSource: static () => 0);

        JsonElement result = await connection.SendReadOnlyRequestAsync(
            "account/read",
            new { refreshToken = false },
            TimeSpan.FromSeconds(30),
            CancellationToken.None,
            policy);

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.AreEqual(3, connection.Sends);
    }

    [TestMethod]
    public async Task ReadOnlyRequest_NonOverloadErrorsAndNonAllowlistedMethodsAreSentOnce()
    {
        foreach (int code in new[] { -32601, -32600, -32603, -32000 })
        {
            var connection = new ScriptedConnection(_ => throw new JsonRpcRemoteException(code, "error"));
            await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() =>
                connection.SendReadOnlyRequestAsync("model/list", new { }, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.AreEqual(1, connection.Sends, code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach ((string method, object parameters) in new (string, object)[]
        {
            ("turn/start", new { }),
            ("thread/goal/set", new { }),
            ("unknown/method", new { }),
            ("account/read", new { refreshToken = true }),
            ("account/read", new { }),
            ("skills/list", new { forceReload = true }),
            ("skills/list", new { }),
        })
        {
            var connection = new ScriptedConnection(_ => throw new JsonRpcRemoteException(-32001, "overloaded"));
            await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() =>
                connection.SendReadOnlyRequestAsync(method, parameters, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.AreEqual(1, connection.Sends, method);
        }
    }

    [TestMethod]
    public async Task ReadOnlyRequest_TransportFailuresAreNotRetried()
    {
        foreach (Func<int, JsonElement> failure in new Func<int, JsonElement>[]
        {
            _ => throw new JsonRpcConnectionClosedException("closed"),
            _ => throw new OperationCanceledException("timeout"),
            _ => throw new InvalidDataException("malformed"),
        })
        {
            var connection = new ScriptedConnection(failure);
            await Assert.ThrowsAsync<Exception>(() =>
                connection.SendReadOnlyRequestAsync("model/list", new { }, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.AreEqual(1, connection.Sends);
        }
    }

    [TestMethod]
    public async Task ReadOnlyRequest_SharedDeadlineStopsRetriesThatCannotFit()
    {
        var connection = new ScriptedConnection(_ => throw new JsonRpcRemoteException(-32001, "overloaded"));
        var policy = new ReadOnlyRetryPolicy(jitterSource: static () => 0);

        // 200 ms admits the first 200 ms (250 * 0.8) wait only if time remains; it does not, so
        // the original overload error is preserved after a single send.
        JsonRpcRemoteException error = await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() =>
            connection.SendReadOnlyRequestAsync("model/list", new { }, TimeSpan.FromMilliseconds(200), CancellationToken.None, policy));

        Assert.AreEqual(-32001, error.Code);
        Assert.AreEqual(1, connection.Sends);
        Assert.IsTrue(connection.Timeouts.All(static timeout => timeout <= TimeSpan.FromMilliseconds(200)));
    }

    [TestMethod]
    public async Task ReadOnlyRequest_EachAttemptReceivesOnlyTheRemainingTime()
    {
        var connection = new ScriptedConnection(attempt => attempt < 2
            ? throw new JsonRpcRemoteException(-32001, "overloaded")
            : JsonSerializer.SerializeToElement(new { }));
        var policy = new ReadOnlyRetryPolicy(jitterSource: static () => 0);

        await connection.SendReadOnlyRequestAsync("thread/list", new { }, TimeSpan.FromSeconds(10), CancellationToken.None, policy);

        Assert.AreEqual(2, connection.Timeouts.Count);
        Assert.IsTrue(connection.Timeouts[1] < connection.Timeouts[0]);
        // The 200 ms backoff is subtracted; allow for a timer that fires slightly early.
        Assert.IsTrue(connection.Timeouts[1] <= TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(150));
    }

    [TestMethod]
    public async Task ReadOnlyRequest_CancellationDuringBackoffStopsPromptly()
    {
        var connection = new ScriptedConnection(_ => throw new JsonRpcRemoteException(-32001, "overloaded"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var policy = new ReadOnlyRetryPolicy(jitterSource: static () => 1);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            connection.SendReadOnlyRequestAsync("model/list", new { }, TimeSpan.FromSeconds(30), cancellation.Token, policy));
        Assert.AreEqual(1, connection.Sends);
    }

    [TestMethod]
    public async Task ReadOnlyRequest_ConnectionCloseDuringBackoffStopsPromptly()
    {
        var connection = new ScriptedConnection(_ => throw new JsonRpcRemoteException(-32001, "overloaded"));
        var policy = new ReadOnlyRetryPolicy(jitterSource: static () => 1);

        Task<JsonElement> send = connection.SendReadOnlyRequestAsync("model/list", new { }, TimeSpan.FromSeconds(30), CancellationToken.None, policy);
        await connection.FirstSend.WaitAsync(TimeSpan.FromSeconds(5));
        connection.RaiseClosed();

        await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(() => send.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(1, connection.Sends);
    }

    [TestMethod]
    public async Task ReadOnlyRequest_CloseBeforeTheBackoffStartsStopsWithoutWaiting()
    {
        ScriptedConnection? connection = null;
        connection = new ScriptedConnection(_ =>
        {
            // The connection closes while the overloaded response is being delivered.
            connection!.RaiseClosed();
            throw new JsonRpcRemoteException(-32001, "overloaded");
        });
        var policy = new ReadOnlyRetryPolicy(jitterSource: static () => 1);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(() =>
            connection.SendReadOnlyRequestAsync("model/list", new { }, TimeSpan.FromSeconds(30), CancellationToken.None, policy));

        Assert.AreEqual(1, connection.Sends);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromMilliseconds(250), "The 300 ms backoff must not run after a close.");
    }

    [TestMethod]
    public void WebSocketPolicy_UsesSharedEndpointAndTokenRules()
    {
        string token = new('a', 32);
        Assert.IsFalse(WebSocketTransportSecurityPolicy.Validate(false, new Uri("ws://127.0.0.1:8080"), token).IsAllowed);
        Assert.IsTrue(WebSocketTransportSecurityPolicy.Validate(true, new Uri("wss://example.com"), token).IsAllowed);
        Assert.IsFalse(WebSocketTransportSecurityPolicy.Validate(true, new Uri("ws://localhost:8080"), "short").IsAllowed);
        Assert.IsTrue(WebSocketTransportSecurityPolicy.Validate(true, new Uri("ws://127.0.0.1:8080"), token).IsAllowed);
        Assert.IsFalse(WebSocketTransportSecurityPolicy.Validate(true, new Uri("ws://app-server.example"), token).IsAllowed);
        Assert.IsFalse(WebSocketTransportSecurityPolicy.Validate(true, new Uri("wss://user:pass@example.com"), token).IsAllowed);
        Assert.IsFalse(WebSocketTransportSecurityPolicy.Validate(true, new Uri("wss://example.com"), "has space " + token).IsAllowed);
    }

    [TestMethod]
    public async Task CloseHandlerCapturedDuringTheRequestToleratesLaterInvocation()
    {
        // A close that captured the retry's handler before it was unsubscribed may invoke it after
        // the call returned and disposed its cancellation source; that must not fault the close.
        EventHandler<Exception?>? captured = null;
        ScriptedConnection connection = null!;
        connection = new ScriptedConnection(_ =>
        {
            captured = connection.CaptureClosedHandlers();
            return JsonSerializer.SerializeToElement(new { });
        });

        await connection.SendReadOnlyRequestAsync("model/list", new { }, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.IsNotNull(captured);
        captured(connection, null);
    }

    private sealed class ScriptedConnection : IJsonRpcConnection
    {
        private readonly Func<int, JsonElement> handler;
        private readonly TaskCompletionSource firstSend = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScriptedConnection(Func<int, JsonElement> handler)
        {
            this.handler = handler;
        }

        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived { add { } remove { } }

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived { add { } remove { } }

        public event EventHandler<Exception?>? Closed;

        public int Sends { get; private set; }

        public List<TimeSpan> Timeouts { get; } = [];

        public Task FirstSend => firstSend.Task;

        public void RaiseClosed() => Closed?.Invoke(this, null);

        // The handlers a concurrent close would have captured at this moment.
        public EventHandler<Exception?>? CaptureClosedHandlers() => Closed;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Sends++;
            Timeouts.Add(timeout);
            firstSend.TrySetResult();
            try
            {
                return Task.FromResult(handler(Sends));
            }
            catch (Exception ex)
            {
                return Task.FromException<JsonElement>(ex);
            }
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
