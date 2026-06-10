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
