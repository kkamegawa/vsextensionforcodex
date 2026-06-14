using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class CodexProcessHostTests
{
    [TestMethod]
    public async Task FailedStartDoesNotLeaveUnsafeProcessReference()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-codex.exe");
        await using var host = new CodexProcessHost(new SecretRedactor());

        bool threw = false;
        try
        {
            await host.StartAsync(missing, Path.GetTempPath(), CancellationToken.None);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Expected the missing executable start to fail.");
        Assert.IsNull(host.ProcessId);
    }

    [TestMethod]
    public void PopulateChildEnvironmentForwardsProxyVariables()
    {
        const string proxyName = "HTTPS_PROXY";
        string? original = Environment.GetEnvironmentVariable(proxyName);
        try
        {
            Environment.SetEnvironmentVariable(proxyName, "http://proxy.example:8080");
            var target = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            CodexProcessHost.PopulateChildEnvironment(target);

            Assert.IsTrue(target.TryGetValue(proxyName, out string? value));
            Assert.AreEqual("http://proxy.example:8080", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proxyName, original);
        }
    }

    [TestMethod]
    public void PopulateChildEnvironmentDoesNotForwardArbitrarySecrets()
    {
        const string secretName = "SOME_UNRELATED_SECRET";
        string? original = Environment.GetEnvironmentVariable(secretName);
        try
        {
            Environment.SetEnvironmentVariable(secretName, "super-secret-value");
            var target = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            CodexProcessHost.PopulateChildEnvironment(target);

            Assert.IsFalse(target.ContainsKey(secretName), "Non-allow-listed variables must not be forwarded.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, original);
        }
    }

    [TestMethod]
    public void ResolverUsesExplicitExistingExecutable()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test process path is unavailable.");

        string resolved = CodexExecutableResolver.Resolve(executable);

        Assert.AreEqual(Path.GetFullPath(executable), resolved);
    }
}
