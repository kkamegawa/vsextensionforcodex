using System.Text.Json;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class SupportedCodexVersionsTests
{
    [TestMethod]
    public void CompiledVersionsMatchTheContractManifest()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(FindManifest()));
        JsonElement root = manifest.RootElement;

        Assert.AreEqual(5, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(SupportedCodexVersions.Minimum, root.GetProperty("minimumVersion").GetString());
        Assert.AreEqual(SupportedCodexVersions.Target, root.GetProperty("targetVersion").GetString());
        Assert.AreEqual("0.170.0", SupportedCodexVersions.Tier170);
        Assert.AreEqual(SupportedCodexVersions.Tier171, root.GetProperty("targetVersion").GetString());
        CollectionAssert.AreEqual(
            new[] { "0.169.1", "0.170.0" },
            root.GetProperty("regressionVersions").EnumerateArray().Select(version => version.GetString()).ToArray());
    }

    [TestMethod]
    [DataRow("0.168.0-alpha.1", CodexFeatureTier.Unsupported)]
    [DataRow("0.168.0", CodexFeatureTier.Unsupported)]
    [DataRow("0.169.0-alpha.1", CodexFeatureTier.Unsupported)]
    [DataRow("0.169.0+build.7", CodexFeatureTier.Baseline)]
    [DataRow("0.169.0", CodexFeatureTier.Baseline)]
    [DataRow("0.169.1-alpha.1", CodexFeatureTier.Baseline)]
    [DataRow("0.170.0-alpha.1", CodexFeatureTier.Baseline)]
    [DataRow("0.170.0+build.7", CodexFeatureTier.Tier170)]
    [DataRow("0.170.0", CodexFeatureTier.Tier170)]
    [DataRow("0.171.0-alpha.1", CodexFeatureTier.Tier170)]
    [DataRow("0.171.0+build.7", CodexFeatureTier.Tier171)]
    [DataRow("0.171.0", CodexFeatureTier.Tier171)]
    public void GetFeatureTierUsesTheSupportedVersionBoundaries(string version, CodexFeatureTier expected)
        => Assert.AreEqual(expected, CodexCliVersion.GetFeatureTier(CodexCliVersion.Parse(version)));

    [TestMethod]
    public void CompareToUsesSemVerPrereleaseAndBuildMetadataPrecedence()
    {
        CodexCliVersion prerelease = CodexCliVersion.Parse("0.171.0-alpha.1");
        CodexCliVersion release = CodexCliVersion.Parse("0.171.0");

        Assert.IsTrue(prerelease.CompareTo(release) < 0);
        Assert.AreEqual(
            0,
            CodexCliVersion.Parse("0.171.0+build.1").CompareTo(CodexCliVersion.Parse("0.171.0+build.2")));
    }

    [TestMethod]
    [DataRow("0.171.0-alpha.1", false)]
    [DataRow("0.171.0+build.7", false)]
    [DataRow("0.171.0", false)]
    [DataRow("0.171.1-alpha.1", true)]
    [DataRow("0.171.1", true)]
    [DataRow("0.172.0", true)]
    public void IsNewerThanTargetUsesSemVerPrecedence(string version, bool expected)
        => Assert.AreEqual(expected, CodexCliVersion.Parse(version).IsNewerThanTarget);

    private static string FindManifest()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "app-server-contract.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not find app-server-contract.json above the test output directory.");
    }
}
