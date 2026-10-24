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

        Assert.AreEqual(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(SupportedCodexVersions.Minimum, root.GetProperty("minimumVersion").GetString());
        Assert.AreEqual(SupportedCodexVersions.Target, root.GetProperty("targetVersion").GetString());
        CollectionAssert.Contains(
            root.GetProperty("regressionVersions").EnumerateArray().Select(version => version.GetString()).ToArray(),
            SupportedCodexVersions.Minimum);
    }

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
