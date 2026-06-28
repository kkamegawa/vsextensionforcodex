using System.Diagnostics;
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
    public void PopulateChildEnvironmentForwardsPathExt()
    {
        // PATHEXT must be forwarded so Windows can resolve script-based launchers (.cmd/.bat)
        // such as the mise shim that codex is invoked through; without it the launcher fails
        // with "cannot find binary path" and the app-server exits with code 1.
        const string pathExtName = "PATHEXT";
        string? original = Environment.GetEnvironmentVariable(pathExtName);
        try
        {
            Environment.SetEnvironmentVariable(pathExtName, ".COM;.EXE;.BAT;.CMD");
            var target = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            CodexProcessHost.PopulateChildEnvironment(target);

            Assert.IsTrue(target.TryGetValue(pathExtName, out string? value), "PATHEXT must be forwarded to the child environment.");
            Assert.AreEqual(".COM;.EXE;.BAT;.CMD", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(pathExtName, original);
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

    [TestMethod]
    public void ResolverFindsPathLauncherWrapperWhenExeIsUnavailable()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "codex-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string launcherPath = Path.Combine(tempDirectory, "codex.cmd");
        File.WriteAllText(launcherPath, "@echo off");

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        string? originalCodexPath = Environment.GetEnvironmentVariable("CODEX_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_PATH", null);
            Environment.SetEnvironmentVariable("PATH", tempDirectory);

            string resolved = CodexExecutableResolver.Resolve("codex");

            Assert.AreEqual(Path.GetFullPath(launcherPath), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("CODEX_PATH", originalCodexPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void CreateStartInfoUsesHiddenCmdStartupForScriptLauncher()
    {
        string wrapperPath = @"C:\tools\codex.cmd";

        ProcessStartInfo info = CodexProcessHost.CreateStartInfo(wrapperPath, Path.GetTempPath());

        Assert.IsTrue(info.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(4, info.ArgumentList.Count);
        Assert.AreEqual("/d", info.ArgumentList[0]);
        Assert.AreEqual("/s", info.ArgumentList[1]);
        Assert.AreEqual("/c", info.ArgumentList[2]);
        Assert.AreEqual($"\"\"{wrapperPath}\" app-server\"", info.ArgumentList[3]);
        Assert.IsTrue(info.CreateNoWindow);
    }

    [TestMethod]
    public void CreateStartInfoKeepsInheritedHiddenConsoleBehaviorForExecutable()
    {
        string executablePath = @"C:\tools\codex.exe";

        ProcessStartInfo info = CodexProcessHost.CreateStartInfo(executablePath, Path.GetTempPath());

        Assert.AreEqual(executablePath, info.FileName);
        CollectionAssert.AreEqual(new[] { "app-server" }, info.ArgumentList.ToArray());
        Assert.IsFalse(info.CreateNoWindow);
    }
}
