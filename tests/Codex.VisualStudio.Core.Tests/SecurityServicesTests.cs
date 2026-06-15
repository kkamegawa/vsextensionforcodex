using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;
using System.Diagnostics;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class SecurityServicesTests
{
    [TestMethod]
    public void SecretRedactor_RemovesCommonCredentials()
    {
        var redactor = new SecretRedactor();

        string result = redactor.Redact("Authorization: Bearer abc123 token=secret password:hunter2 normal=value");

        Assert.IsFalse(result.Contains("abc123", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("hunter2", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("normal=value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PathAccessPolicy_NormalizesCaseAndParentSegments()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var policy = new PathAccessPolicy();

        PathAccessResult result = policy.Evaluate(Path.Combine(root, "src", "..", "README.md"), root.ToUpperInvariant());

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.IsWithinWorkspace);
        Assert.IsTrue(result.NormalizedPath.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PathAccessPolicy_DetectsWorkspaceOutside()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var policy = new PathAccessPolicy();

        PathAccessResult result = policy.Evaluate(Path.Combine(root, "..", "secret.txt"), root);

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.IsWithinWorkspace);
    }

    [TestMethod]
    public void PathAccessPolicy_ResolvesRelativePathsAgainstWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var policy = new PathAccessPolicy();

        PathAccessResult result = policy.Evaluate(Path.Combine("src", "file.cs"), root);

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.IsWithinWorkspace);
        Assert.AreEqual(Path.Combine(root, "src", "file.cs"), result.NormalizedPath, true);
    }

    [TestMethod]
    public void PathAccessPolicy_ResolvesSymbolicLinkOutsideWorkspace()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "CodexVsTests", Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(testRoot, "workspace");
        string outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        string link = Path.Combine(workspace, "linked");
        CreateDirectoryLink(link, outside);
        var policy = new PathAccessPolicy();

        PathAccessResult result = policy.Evaluate(Path.Combine(link, "secret.txt"), workspace);

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.IsWithinWorkspace);
        Directory.Delete(link);
        Directory.Delete(testRoot, recursive: true);
    }

    [TestMethod]
    public void ApprovalPolicy_ClassifiesDestructiveCommand()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var policy = new ApprovalPolicyEngine(new PathAccessPolicy());

        ApprovalPolicyResult result = policy.EvaluateCommand("git reset --hard", root, root, null, null);

        Assert.AreEqual(ApprovalRiskCategory.Destructive, result.Risk);
    }

    [TestMethod]
    public void ApprovalPolicy_ClassifiesNetworkByDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var policy = new ApprovalPolicyEngine(new PathAccessPolicy());

        ApprovalPolicyResult result = policy.EvaluateCommand(null, root, root, "api.example.com", 443);

        Assert.AreEqual(ApprovalRiskCategory.Network, result.Risk);
        Assert.AreEqual("network:api.example.com:443", result.RiskKey);
    }

    [TestMethod]
    public void ApprovalPolicy_ClassifiesCredentialValues()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var policy = new ApprovalPolicyEngine(new PathAccessPolicy());

        ApprovalPolicyResult result = policy.EvaluateCommand(
            "curl -H \"Authorization: Bearer abcdefghijklmnopqrstuvwxyz\" https://example.com",
            root,
            root,
            null,
            null);

        Assert.AreEqual(ApprovalRiskCategory.CredentialOAuth, result.Risk);
    }

    [TestMethod]
    public void ApprovalPolicy_BlocksFileWriteUnderProtectedDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var protectedPolicy = new ProtectedDirectoryPolicy(new[] { @"C:\Program Files" });
        var policy = new ApprovalPolicyEngine(new PathAccessPolicy(), protectedPolicy);

        ApprovalPolicyResult result = policy.EvaluateFile(@"C:\Program Files\PowerShell\7\hello.cs", root);

        Assert.IsTrue(result.IsBlocked);
        Assert.AreEqual(ApprovalRiskCategory.WorkspaceOutside, result.Risk);
    }

    [TestMethod]
    public void ApprovalPolicy_BlocksCommandWithCwdUnderProtectedDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var protectedPolicy = new ProtectedDirectoryPolicy(new[] { @"C:\Program Files" });
        var policy = new ApprovalPolicyEngine(new PathAccessPolicy(), protectedPolicy);

        ApprovalPolicyResult result = policy.EvaluateCommand("echo hi", @"C:\Program Files\PowerShell\7", root, null, null);

        Assert.IsTrue(result.IsBlocked);
        Assert.AreEqual(ApprovalRiskCategory.WorkspaceOutside, result.Risk);
    }

    [TestMethod]
    public void ApprovalPolicy_DoesNotBlockNonProtectedWorkspaceFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var protectedPolicy = new ProtectedDirectoryPolicy(new[] { @"C:\Program Files" });
        var policy = new ApprovalPolicyEngine(new PathAccessPolicy(), protectedPolicy);

        ApprovalPolicyResult result = policy.EvaluateFile(Path.Combine(root, "hello.cs"), root);

        Assert.IsFalse(result.IsBlocked);
        Assert.AreEqual(ApprovalRiskCategory.WorkspaceWrite, result.Risk);
    }

    [TestMethod]
    public void ApprovalGrantStore_EnforcesTurnThreadAndSessionScopes()
    {
        var store = new ApprovalGrantStore();
        var turnRequest = Request("risk", "thread-1", "turn-1");
        var sameThreadNextTurn = Request("risk", "thread-1", "turn-2");
        var otherThread = Request("risk", "thread-2", "turn-3");

        store.Add(turnRequest, ApprovalScope.Turn);
        Assert.IsTrue(store.IsApproved(turnRequest));
        Assert.IsFalse(store.IsApproved(sameThreadNextTurn));
        store.EndTurn("thread-1", "turn-1");
        Assert.IsFalse(store.IsApproved(turnRequest));

        store.Add(turnRequest, ApprovalScope.Thread);
        Assert.IsTrue(store.IsApproved(sameThreadNextTurn));
        Assert.IsFalse(store.IsApproved(otherThread));
        store.EndThread("thread-1");
        Assert.IsFalse(store.IsApproved(sameThreadNextTurn));

        store.Add(turnRequest, ApprovalScope.Session);
        Assert.IsTrue(store.IsApproved(otherThread));
        Assert.AreEqual(1, store.Snapshot().Count);
        store.Clear();
        Assert.AreEqual(0, store.Snapshot().Count);
    }

    private static ApprovalRequest Request(string riskKey, string threadId, string turnId) => new()
    {
        RiskKey = riskKey,
        ThreadId = threadId,
        TurnId = turnId,
    };

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "/c", "mklink", "/J", link, target },
        }) ?? throw new InvalidOperationException("Failed to start mklink.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }
}
