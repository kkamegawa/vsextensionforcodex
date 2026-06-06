using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

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
}
