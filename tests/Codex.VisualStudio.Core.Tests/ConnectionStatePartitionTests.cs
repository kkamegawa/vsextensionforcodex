using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class ConnectionStatePartitionTests
{
    [TestMethod]
    public void EquivalentWindowsRootSpellingsProduceSamePartition()
    {
        WorkerOptions first = CreateOptions();
        WorkerOptions equivalent = CreateOptions();
        equivalent.LocalRoot = @"c:/WORKSPACE/";
        equivalent.WorkingDirectory = @"c:/WORKSPACE/project-a/.";
        equivalent.ServerRoot = "/srv/codex/workspace/";

        Assert.AreEqual(LocalPath.Create(first.LocalRoot!).IdentityValue, LocalPath.Create(equivalent.LocalRoot!).IdentityValue);
        Assert.AreEqual(LocalPath.Create(first.WorkingDirectory).IdentityValue, LocalPath.Create(equivalent.WorkingDirectory).IdentityValue);
        Assert.AreEqual(ServerPath.Create(first.ServerRoot!).IdentityValue, ServerPath.Create(equivalent.ServerRoot!).IdentityValue);

        string firstKey = ConnectionStatePartition.Create(first, "worker-a", 7, "credential-a");
        string equivalentKey = ConnectionStatePartition.Create(equivalent, "worker-a", 7, "credential-a");

        Assert.AreEqual(firstKey, equivalentKey);
    }

    [TestMethod]
    public void PartitionChangesForEndpointProfileWorkspaceAndOwnerAttempt()
    {
        WorkerOptions baseline = CreateOptions();
        string baselineKey = Create(baseline);

        WorkerOptions endpoint = CreateOptions();
        endpoint.RemoteEndpoint = "https://other.example/api";
        Assert.AreNotEqual(baselineKey, Create(endpoint));

        WorkerOptions profile = CreateOptions();
        profile.RemoteProfileName = "Production";
        Assert.AreNotEqual(baselineKey, Create(profile));

        WorkerOptions profileFingerprint = CreateOptions();
        profileFingerprint.RemoteProfileFingerprint = "different-profile-fingerprint";
        Assert.AreNotEqual(baselineKey, Create(profileFingerprint));

        WorkerOptions localRoot = CreateOptions();
        localRoot.LocalRoot = @"C:\other-workspace";
        Assert.AreNotEqual(baselineKey, Create(localRoot));

        WorkerOptions serverRoot = CreateOptions();
        serverRoot.ServerRoot = "/srv/other-workspace";
        Assert.AreNotEqual(baselineKey, Create(serverRoot));

        WorkerOptions workingDirectory = CreateOptions();
        workingDirectory.WorkingDirectory = @"C:\workspace\project-b";
        Assert.AreNotEqual(baselineKey, Create(workingDirectory));

        Assert.AreNotEqual(baselineKey, ConnectionStatePartition.Create(baseline, "worker-b", 7, "credential-a"));
        Assert.AreNotEqual(baselineKey, ConnectionStatePartition.Create(baseline, "worker-a", 8, "credential-a"));
        Assert.AreNotEqual(baselineKey, ConnectionStatePartition.Create(baseline, "worker-a", 7, "credential-b"));
    }

    [TestMethod]
    public void PosixRootIdentityPreservesCase()
    {
        WorkerOptions upper = CreateOptions();
        upper.LocalRoot = "/srv/Workspace";
        upper.WorkingDirectory = "/srv/Workspace/project";
        WorkerOptions lower = CreateOptions();
        lower.LocalRoot = "/srv/workspace";
        lower.WorkingDirectory = "/srv/workspace/project";

        Assert.AreNotEqual(Create(upper), Create(lower));
    }

    [TestMethod]
    public void PosixRootIdentityPreservesTrailingSpace()
    {
        WorkerOptions withoutTrailingSpace = CreateOptions();
        withoutTrailingSpace.LocalRoot = "/srv/repo";
        withoutTrailingSpace.WorkingDirectory = "/srv/repo/workspace";
        WorkerOptions withTrailingSpace = CreateOptions();
        withTrailingSpace.LocalRoot = "/srv/repo ";
        withTrailingSpace.WorkingDirectory = "/srv/repo /workspace";

        Assert.AreNotEqual(Create(withoutTrailingSpace), Create(withTrailingSpace));
    }

    [TestMethod]
    public void PartitionIsOpaqueAndContainsNoConnectionMetadata()
    {
        WorkerOptions options = CreateOptions();
        string partition = Create(options);

        Assert.AreEqual(64, partition.Length);
        Assert.IsTrue(partition.All(Uri.IsHexDigit));
        Assert.IsFalse(partition.Contains("private-profile", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(partition.Contains("example", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(partition.Contains("credential-a", StringComparison.Ordinal));
        Assert.IsFalse(partition.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(partition.Contains("token.txt", StringComparison.OrdinalIgnoreCase));
    }

    private static string Create(WorkerOptions options)
        => ConnectionStatePartition.Create(options, "worker-a", 7, "credential-a");

    private static WorkerOptions CreateOptions() => new()
    {
        WorkingDirectory = @"C:\workspace\project-a",
        RemoteEndpoint = "https://example.test/api",
        RemoteTokenFilePath = @"C:\Users\user\token.txt",
        LocalRoot = @"C:\workspace",
        ServerRoot = "/srv/codex/workspace",
        RemoteProfileName = "private-profile",
        RemoteProfileFingerprint = "profile-fingerprint",
    };
}
