using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Codex.AppServer.Protocol;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class WebSocketJsonRpcConnectionTests
{
    [TestMethod]
    public async Task Notifications_AreDeliveredOneAtATimeInWireOrder()
    {
        await using var harness = new SocketHarness();
        const int count = 40;
        var received = new List<int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        bool overlapped = false;
        harness.Connection.NotificationReceived += async (message, cancellationToken) =>
        {
            if (Interlocked.Increment(ref active) > 1)
            {
                overlapped = true;
            }

            int index = message.Params!.Value.GetProperty("index").GetInt32();
            // Uneven handler latency would reorder concurrently dispatched notifications.
            await Task.Delay(index % 3 == 0 ? 5 : 0, cancellationToken);
            received.Add(index);
            Interlocked.Decrement(ref active);
            if (received.Count == count)
            {
                done.TrySetResult();
            }
        };
        await harness.Connection.StartAsync(CancellationToken.None);

        for (int i = 0; i < count; i++)
        {
            await harness.SendServerTextAsync($"{{\"method\":\"item/agentMessage/delta\",\"params\":{{\"index\":{i}}}}}");
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(overlapped);
        CollectionAssert.AreEqual(Enumerable.Range(0, count).ToList(), received);
    }

    [TestMethod]
    public async Task ServerRequest_StartsOnlyAfterEarlierNotificationsWereHandled()
    {
        await using var harness = new SocketHarness();
        var notificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool notificationHandled = false;
        bool requestSawNotification = false;
        harness.Connection.NotificationReceived += async (_, cancellationToken) =>
        {
            notificationStarted.TrySetResult();
            await releaseNotification.Task.WaitAsync(cancellationToken);
            Volatile.Write(ref notificationHandled, true);
        };
        harness.Connection.RequestReceived += (_, _) =>
        {
            requestSawNotification = Volatile.Read(ref notificationHandled);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { decision = "accept" }));
        };
        await harness.Connection.StartAsync(CancellationToken.None);

        // An approval for an item must not be handled before the item/started that precedes it.
        await harness.SendServerTextAsync("""{"method":"item/started","params":{}}""");
        await notificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.SendServerTextAsync("""{"id":3,"method":"item/commandExecution/requestApproval","params":{}}""");
        await Task.Delay(50);
        releaseNotification.TrySetResult();

        using JsonDocument response = JsonDocument.Parse(await harness.ReceiveServerTextAsync());
        Assert.AreEqual(3, response.RootElement.GetProperty("id").GetInt64());
        Assert.IsTrue(requestSawNotification);
    }

    [TestMethod]
    public async Task Response_ResolvesWhileNotificationHandlerAwaitsIt()
    {
        await using var harness = new SocketHarness();
        var handlerResult = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.NotificationReceived += async (_, cancellationToken) =>
        {
            JsonElement result = await harness.Connection.SendRequestAsync(
                "account/read",
                new { refreshToken = false },
                TimeSpan.FromSeconds(5),
                cancellationToken);
            handlerResult.TrySetResult(result);
        };
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.SendServerTextAsync("""{"method":"account/updated","params":{}}""");
        using JsonDocument request = JsonDocument.Parse(await harness.ReceiveServerTextAsync());
        Assert.AreEqual("account/read", request.RootElement.GetProperty("method").GetString());
        long id = request.RootElement.GetProperty("id").GetInt64();
        await harness.SendServerTextAsync($"{{\"id\":{id},\"result\":{{\"account\":null}}}}");

        JsonElement result = await handlerResult.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("account").ValueKind);
    }

    [TestMethod]
    public async Task ServerRequest_ReturnsHandlerResultAndIgnoresDuplicateOutstandingId()
    {
        await using var harness = new SocketHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        harness.Connection.RequestReceived += async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return JsonSerializer.SerializeToElement(new { decision = "accept" });
        };
        await harness.Connection.StartAsync(CancellationToken.None);

        const string request = """{"id":9,"method":"item/fileChange/requestApproval","params":{}}""";
        await harness.SendServerTextAsync(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.SendServerTextAsync(request);
        await Task.Delay(50);
        release.TrySetResult();

        using JsonDocument response = JsonDocument.Parse(await harness.ReceiveServerTextAsync());
        Assert.AreEqual(9, response.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual("accept", response.RootElement.GetProperty("result").GetProperty("decision").GetString());
        Assert.AreEqual(1, Volatile.Read(ref calls));
    }

    [TestMethod]
    public async Task ServerRequest_PropagatesJsonRpcErrorCode()
    {
        await using var harness = new SocketHarness();
        harness.Connection.RequestReceived += (_, _) => throw new JsonRpcRemoteException(-32601, "unsupported");
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.SendServerTextAsync("""{"id":"future-1","method":"future/request","params":{}}""");

        using JsonDocument response = JsonDocument.Parse(await harness.ReceiveServerTextAsync());
        Assert.AreEqual("future-1", response.RootElement.GetProperty("id").GetString());
        Assert.AreEqual(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task MalformedFrames_AreToleratedUntilThreshold()
    {
        await using var harness = new SocketHarness();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.NotificationReceived += (_, _) =>
        {
            delivered.TrySetResult();
            return Task.CompletedTask;
        };
        harness.Connection.Closed += (_, exception) => closed.TrySetResult(exception);
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.SendServerTextAsync("{not json");
        await harness.SendServerTextAsync("""{"method":"skills/changed","params":{}}""");
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(closed.Task.IsCompleted);

        for (int i = 0; i < 3; i++)
        {
            await harness.SendServerTextAsync("{not json");
        }

        Exception? failure = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsInstanceOfType<InvalidDataException>(failure);
    }

    [TestMethod]
    public async Task OversizedMessage_ClosesConnectionAndFailsPendingRequests()
    {
        await using var harness = new SocketHarness(maxMessageBytes: 64);
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.Closed += (_, exception) => closed.TrySetResult(exception);
        await harness.Connection.StartAsync(CancellationToken.None);
        Task<JsonElement> pending = harness.Connection.SendRequestAsync("thread/list", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        _ = await harness.ReceiveServerTextAsync();

        await harness.SendServerTextAsync("{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"" + new string('x', 128) + "\"}}");

        Assert.IsInstanceOfType<InvalidDataException>(await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(() => pending);
    }

    [TestMethod]
    public async Task ServerClose_RaisesClosedAndRejectsLaterRequests()
    {
        await using var harness = new SocketHarness();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.Closed += (_, _) => closed.TrySetResult();
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.CloseServerAsync();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(() =>
            harness.Connection.SendRequestAsync("thread/list", null, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [TestMethod]
    public void Constructor_RejectsPlainWebSocketToRemoteHost()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WebSocketJsonRpcConnection(new Uri("ws://app-server.example.invalid"), new string('a', 32)));
    }

    // Connects the connection under test to an in-memory server WebSocket so framing is real
    // without opening a network listener.
    private sealed class SocketHarness : IAsyncDisposable
    {
        private readonly WebSocket server;

        public SocketHarness(int maxMessageBytes = JsonLineRpcConnection.DefaultMaxLineBytes)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            WebSocket client = WebSocket.CreateFromStream(
                new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream()),
                isServer: false,
                subProtocol: null,
                keepAliveInterval: Timeout.InfiniteTimeSpan);
            server = WebSocket.CreateFromStream(
                new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
                isServer: true,
                subProtocol: null,
                keepAliveInterval: Timeout.InfiniteTimeSpan);
            Connection = new WebSocketJsonRpcConnection(client, maxMessageBytes);
        }

        public WebSocketJsonRpcConnection Connection { get; }

        public Task SendServerTextAsync(string text)
            => server.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        public async Task<string> ReceiveServerTextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var message = new MemoryStream();
            byte[] buffer = new byte[4096];
            ValueWebSocketReceiveResult result;
            do
            {
                result = await server.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(message.ToArray());
        }

        public Task CloseServerAsync()
            => server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            server.Abort();
            server.Dispose();
        }
    }

    private sealed class DuplexStream : Stream
    {
        private readonly Stream input;
        private readonly Stream output;

        public DuplexStream(Stream input, Stream output)
        {
            this.input = input;
            this.output = output;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            output.Write(buffer, offset, count);
            output.Flush();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await output.WriteAsync(buffer, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
