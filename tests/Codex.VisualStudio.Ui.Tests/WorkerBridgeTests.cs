using System.IO;
using Codex.VisualStudio.Contracts;
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

    [TestMethod]
    public async Task NewInteractionNotifications_AreForwardedToExtensionSubscribers()
    {
        var bridge = new WorkerBridge();
        PermissionRequest? permission = null;
        string? permissionResolved = null;
        McpElicitationRequest? elicitation = null;
        string? elicitationResolved = null;
        UnsupportedInteractionNotice? unsupported = null;
        InteractionAuthStatus? authStatus = null;
        bridge.PermissionRequested += notification =>
        {
            permission = notification.Value;
            return Task.CompletedTask;
        };
        bridge.PermissionResolved += notification =>
        {
            permissionResolved = notification.Value;
            return Task.CompletedTask;
        };
        bridge.McpElicitationRequested += notification =>
        {
            elicitation = notification.Value;
            return Task.CompletedTask;
        };
        bridge.McpElicitationResolved += notification =>
        {
            elicitationResolved = notification.Value;
            return Task.CompletedTask;
        };
        bridge.UnsupportedInteractionReceived += notification =>
        {
            unsupported = notification.Value;
            return Task.CompletedTask;
        };
        bridge.InteractionAuthStatusChanged += notification =>
        {
            authStatus = notification.Value;
            return Task.CompletedTask;
        };

        var permissionValue = new PermissionRequest { RequestId = "permission-1" };
        var elicitationValue = new McpElicitationRequest { RequestId = "elicitation-1" };
        var unsupportedValue = new UnsupportedInteractionNotice { Kind = UnsupportedInteractionKind.SecretInput };
        var authValue = new InteractionAuthStatus { IsLocal = true, IsSupported = true };
        await bridge.OnPermissionRequestedAsync(Notification(permissionValue), CancellationToken.None);
        await bridge.OnPermissionResolvedAsync(Notification("permission-1"), CancellationToken.None);
        await bridge.OnMcpElicitationRequestedAsync(Notification(elicitationValue), CancellationToken.None);
        await bridge.OnMcpElicitationResolvedAsync(Notification("elicitation-1"), CancellationToken.None);
        await bridge.OnUnsupportedInteractionAsync(Notification(unsupportedValue), CancellationToken.None);
        await bridge.OnInteractionAuthStatusChangedAsync(Notification(authValue), CancellationToken.None);

        Assert.AreSame(permissionValue, permission);
        Assert.AreEqual("permission-1", permissionResolved);
        Assert.AreSame(elicitationValue, elicitation);
        Assert.AreEqual("elicitation-1", elicitationResolved);
        Assert.AreSame(unsupportedValue, unsupported);
        Assert.AreSame(authValue, authStatus);
    }

    private static WorkerNotification<T> Notification<T>(T value)
        => new() { Value = value };
}
