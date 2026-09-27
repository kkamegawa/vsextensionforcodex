using System.IO;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class WorkerBridgeTests
{
    [TestMethod]
    public void WorkerStartUsesCurrentRuntimeHostWithPackagedDll()
    {
        string root = Path.Combine(Path.GetTempPath(), $"worker host {Guid.NewGuid():N}");
        string runtimeRoot = Path.Combine(root, "runtime with spaces");
        string runtimeDirectory = Path.Combine(runtimeRoot, "shared", "Microsoft.NETCore.App", "8.0.31");
        string assemblyDirectory = Path.Combine(root, "extension with spaces");
        string workerDirectory = Path.Combine(assemblyDirectory, "Worker");
        string dotnetHost = Path.Combine(runtimeRoot, "dotnet.exe");
        string workerDll = Path.Combine(workerDirectory, "Codex.VisualStudio.Worker.dll");
        Directory.CreateDirectory(runtimeDirectory);
        Directory.CreateDirectory(workerDirectory);
        try
        {
            File.WriteAllBytes(dotnetHost, []);
            File.WriteAllBytes(workerDll, []);

            var startInfo = WorkerBridge.CreateWorkerStartInfo(assemblyDirectory, "test-pipe", runtimeDirectory);

            Assert.AreEqual(dotnetHost, startInfo.FileName);
            CollectionAssert.AreEqual(new[] { workerDll, "--pipe", "test-pipe" }, startInfo.ArgumentList.ToArray());
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.IsTrue(startInfo.CreateNoWindow);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
