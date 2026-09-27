using System.IO.Pipelines;
using System.Text.Json;
using Codex.AppServer.Protocol;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class JsonLineRpcConnectionTests
{
    [TestMethod]
    public async Task RequestResponse_RoundTrips()
    {
        await using var harness = new RpcHarness();
        await harness.Connection.StartAsync(CancellationToken.None);
        Task<JsonElement> request = harness.Connection.SendRequestAsync("ping", new { value = 42 }, TimeSpan.FromSeconds(2), CancellationToken.None);

        string outgoing = await harness.ReadClientLineAsync();
        using JsonDocument document = JsonDocument.Parse(outgoing);
        long id = document.RootElement.GetProperty("id").GetInt64();
        await harness.WriteServerLineAsync(JsonSerializer.Serialize(new { id, result = new { ok = true } }));

        JsonElement result = await request;
        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public async Task NotificationHandler_CanAwaitRequestResponse()
    {
        await using var harness = new RpcHarness();
        var handled = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.NotificationReceived += async (_, cancellationToken) =>
        {
            JsonElement result = await harness.Connection.SendRequestAsync(
                "account/read",
                null,
                TimeSpan.FromSeconds(2),
                cancellationToken);
            handled.TrySetResult(result);
        };
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.WriteServerLineAsync("""{"method":"account/updated","params":{}}""");

        string outgoing = await harness.ReadClientLineAsync();
        using JsonDocument requestDocument = JsonDocument.Parse(outgoing);
        Assert.AreEqual("account/read", requestDocument.RootElement.GetProperty("method").GetString());
        long id = requestDocument.RootElement.GetProperty("id").GetInt64();
        await harness.WriteServerLineAsync(JsonSerializer.Serialize(new { id, result = new { account = "signed-in" } }));

        JsonElement result = await handled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("signed-in", result.GetProperty("account").GetString());
    }

    [TestMethod]
    public async Task ServerRequest_ReturnsHandlerResult()
    {
        await using var harness = new RpcHarness();
        harness.Connection.RequestReceived += (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { decision = "accept" }));
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.WriteServerLineAsync("""{"id":"approval-1","method":"item/fileChange/requestApproval","params":{}}""");

        string response = await harness.ReadClientLineAsync();
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.AreEqual("approval-1", document.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("accept", document.RootElement.GetProperty("result").GetProperty("decision").GetString());
    }

    [TestMethod]
    public async Task ServerRequest_PropagatesJsonRpcMethodError()
    {
        await using var harness = new RpcHarness();
        harness.Connection.RequestReceived += (_, _) =>
            throw new JsonRpcRemoteException(-32601, "unsupported");
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.WriteServerLineAsync("""{"id":"future-1","method":"future/request","params":{}}""");

        string response = await harness.ReadClientLineAsync();
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.AreEqual("future-1", document.RootElement.GetProperty("id").GetString());
        Assert.AreEqual(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task ServerRequest_PropagatesInvalidParamsErrorWithRequestId()
    {
        await using var harness = new RpcHarness();
        harness.Connection.RequestReceived += (_, _) =>
            throw new JsonRpcRemoteException(-32602, "invalid params");
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.WriteServerLineAsync("""{"id":"invalid-1","method":"item/fileChange/requestApproval","params":{}}""");

        string response = await harness.ReadClientLineAsync();
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.AreEqual("invalid-1", document.RootElement.GetProperty("id").GetString());
        Assert.AreEqual(-32602, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task DuplicateServerRequestId_InvokesHandlerOnce()
    {
        await using var harness = new RpcHarness();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int handlerCalls = 0;
        harness.Connection.RequestReceived += async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref handlerCalls);
            started.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return JsonSerializer.SerializeToElement(new { decision = "accept" });
        };
        await harness.Connection.StartAsync(CancellationToken.None);

        const string request = """{"id":"duplicate-1","method":"item/fileChange/requestApproval","params":{}}""";
        await harness.WriteServerLineAsync(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.WriteServerLineAsync(request);
        await Task.Delay(100);
        Assert.AreEqual(1, Volatile.Read(ref handlerCalls));

        release.TrySetResult(true);
        string response = await harness.ReadClientLineAsync();
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.AreEqual("duplicate-1", document.RootElement.GetProperty("id").GetString());
        await Task.Delay(100);
        Assert.AreEqual(1, Volatile.Read(ref handlerCalls));
    }

    [TestMethod]
    public async Task ImmediateServerRequests_CompleteWithoutLosingResponses()
    {
        await using var harness = new RpcHarness();
        int handlerCalls = 0;
        harness.Connection.RequestReceived += (_, _) =>
        {
            Interlocked.Increment(ref handlerCalls);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
        };
        await harness.Connection.StartAsync(CancellationToken.None);

        const int requestCount = 64;
        for (int i = 0; i < requestCount; i++)
        {
            await harness.WriteServerLineAsync($"{{\"id\":\"immediate-{i}\",\"method\":\"test/request\",\"params\":{{}}}}");
        }

        var responseIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < requestCount; i++)
        {
            using JsonDocument document = JsonDocument.Parse(await harness.ReadClientLineAsync());
            responseIds.Add(document.RootElement.GetProperty("id").GetString()!);
        }

        Assert.AreEqual(requestCount, responseIds.Count);
        Assert.AreEqual(requestCount, Volatile.Read(ref handlerCalls));
    }

    [TestMethod]
    public async Task Close_CancelsOutstandingServerRequestWithoutResponse()
    {
        await using var harness = new RpcHarness();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.RequestReceived += async (_, cancellationToken) =>
        {
            started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled.TrySetResult(true);
                throw;
            }

            return JsonSerializer.SerializeToElement(new { decision = "accept" });
        };
        await harness.Connection.StartAsync(CancellationToken.None);
        await harness.WriteServerLineAsync("""{"id":"close-1","method":"item/fileChange/requestApproval","params":{}}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await harness.Connection.DisposeAsync();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CanceledRequest_DoesNotCompleteFromLateResponse()
    {
        await using var harness = new RpcHarness();
        await harness.Connection.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<JsonElement> request = harness.Connection.SendRequestAsync("slow", null, TimeSpan.FromMinutes(1), cancellation.Token);
        string outgoing = await harness.ReadClientLineAsync();
        using JsonDocument document = JsonDocument.Parse(outgoing);
        long id = document.RootElement.GetProperty("id").GetInt64();

        cancellation.Cancel();
        try
        {
            await request;
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // expected — TaskCanceledException (a subclass) is also caught
        }
        await harness.WriteServerLineAsync(JsonSerializer.Serialize(new { id, result = new { late = true } }));
    }

    [TestMethod]
    public async Task WritePumpFailure_ClosesConnectionAndFailsPendingRequests()
    {
        var input = new Pipe();
        var connection = new JsonLineRpcConnection(input.Reader.AsStream(), new FailingWriteStream());
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += (_, exception) => closed.TrySetResult(exception);
        try
        {
            await connection.StartAsync(CancellationToken.None);

            Task<JsonElement> request = connection.SendRequestAsync("ping", null, TimeSpan.FromMinutes(1), CancellationToken.None);

            Exception? closeReason = await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsInstanceOfType<IOException>(closeReason);
            await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(() => request.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await connection.DisposeAsync();
            await input.Writer.CompleteAsync();
            await input.Reader.CompleteAsync();
        }
    }

    [TestMethod]
    public async Task NotificationHandlerTimeout_DoesNotStopLaterNotifications()
    {
        await using var harness = new RpcHarness();
        var received = new List<string>();
        var secondHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.NotificationReceived += async (message, cancellationToken) =>
        {
            received.Add(message.Method!);
            if (message.Method == "first")
            {
                // The fake server never answers, so the request times out with TaskCanceledException.
                await harness.Connection.SendRequestAsync("account/read", null, TimeSpan.FromMilliseconds(50), cancellationToken);
                return;
            }

            secondHandled.TrySetResult();
        };
        await harness.Connection.StartAsync(CancellationToken.None);

        await harness.WriteServerLineAsync("""{"method":"first","params":{}}""");
        await harness.WriteServerLineAsync("""{"method":"second","params":{}}""");

        await secondHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { "first", "second" }, received);
    }

    [TestMethod]
    public async Task Dispose_ToleratesAlreadyClosedStreams()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var connection = new JsonLineRpcConnection(input, output);
        await connection.StartAsync(CancellationToken.None);
        input.Dispose();
        output.Dispose();

        await connection.DisposeAsync();
    }

    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The pipe is broken.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("The pipe is broken."));
    }

    private sealed class RpcHarness : IAsyncDisposable
    {
        private readonly Pipe clientToServer = new();
        private readonly Pipe serverToClient = new();
        private readonly StreamReader serverReader;
        private readonly StreamWriter serverWriter;

        public RpcHarness()
        {
            Connection = new JsonLineRpcConnection(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
            serverReader = new StreamReader(clientToServer.Reader.AsStream());
            serverWriter = new StreamWriter(serverToClient.Writer.AsStream()) { AutoFlush = true, NewLine = "\n" };
        }

        public JsonLineRpcConnection Connection { get; }

        public async Task<string> ReadClientLineAsync()
            => await serverReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)) ?? throw new EndOfStreamException();

        public Task WriteServerLineAsync(string value) => serverWriter.WriteLineAsync(value);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            serverReader.Dispose();
            serverWriter.Dispose();
            await clientToServer.Reader.CompleteAsync();
            await clientToServer.Writer.CompleteAsync();
            await serverToClient.Reader.CompleteAsync();
            await serverToClient.Writer.CompleteAsync();
        }
    }
}
