using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class WorkerBridgeTests
{
    [TestMethod]
    public async Task DisposeAsync_IsIdempotentAndPreventsReconnect()
    {
        var bridge = new WorkerBridge();

        await bridge.DisposeAsync();
        await bridge.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            bridge.ConnectAsync(Environment.CurrentDirectory, experimentalApi: false, CancellationToken.None));
    }
}
