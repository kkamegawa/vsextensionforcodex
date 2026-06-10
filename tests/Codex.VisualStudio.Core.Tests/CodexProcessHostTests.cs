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
    public void ResolverUsesExplicitExistingExecutable()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test process path is unavailable.");

        string resolved = CodexExecutableResolver.Resolve(executable);

        Assert.AreEqual(Path.GetFullPath(executable), resolved);
    }
}
