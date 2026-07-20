using System.IO.Compression;
using System.IO;
using System.Xml.Linq;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ExtensionIdentityPackagingTests
{
    private const string ExtensionProjectDirectory = "Codex.VisualStudio.Extension";
    private static readonly XNamespace VsixNamespace = "http://schemas.microsoft.com/developer/vsx-schema/2011";

    [TestMethod]
    public void ExtensionConfiguration_UsesCentralIdentity()
    {
        var metadata = new CodexExtension().ExtensionConfiguration.Metadata;

        Assert.IsNotNull(metadata);
        Assert.AreEqual(ExtensionIdentity.Id, metadata.Id);
        Assert.AreEqual(ExtensionIdentity.PublisherName, metadata.PublisherName);
        Assert.AreEqual(ExtensionIdentity.DisplayName, metadata.DisplayName);
        Assert.AreEqual(ExtensionIdentity.Description, metadata.Description);
        Assert.AreEqual(ExtensionIdentity.AssemblyVersion, metadata.Version);
    }

    // The Extensibility SDK generates extension.vsixmanifest from ExtensionConfiguration, so the
    // packaged manifest is the only manifest that exists; there is no source.extension.vsixmanifest.
    [TestMethod]
    public void PackagedVsixManifest_MatchesCentralIdentity()
    {
        string repositoryRoot = FindRepositoryRoot();
        string packagedVsixPath = FindPackagedVsix(repositoryRoot);
        using var archive = ZipFile.OpenRead(packagedVsixPath);
        ZipArchiveEntry manifestEntry = archive.GetEntry("extension.vsixmanifest")
            ?? throw new AssertFailedException("The packaged VSIX does not contain extension.vsixmanifest.");
        using Stream manifestStream = manifestEntry.Open();
        AssertManifestIdentity(XDocument.Load(manifestStream), manifestEntry.FullName);
    }

    [TestMethod]
    public void LegacyIdentity_IsAbsentFromProductionMetadataSourcesAndPackage()
    {
        string legacyIdentity = string.Concat("CodexForVisualStudio", ".kkamegawa");
        string repositoryRoot = FindRepositoryRoot();
        string extensionProjectRoot = Path.Combine(repositoryRoot, "src", ExtensionProjectDirectory);
        string[] sourcePaths =
        [
            Path.Combine(extensionProjectRoot, "CodexExtension.cs"),
            Path.Combine(extensionProjectRoot, "ToolWindows", "CodexToolWindow.cs"),
        ];

        foreach (string sourcePath in sourcePaths)
        {
            string sourceText = File.ReadAllText(sourcePath);
            Assert.IsFalse(
                sourceText.Contains(legacyIdentity, StringComparison.Ordinal),
                $"Legacy extension identity found in {Path.GetFileName(sourcePath)}.");
        }

        string packagedVsixPath = FindPackagedVsix(repositoryRoot);
        using var archive = ZipFile.OpenRead(packagedVsixPath);
        IEnumerable<ZipArchiveEntry> manifestEntries = archive.Entries.Where(
            entry => entry.FullName.EndsWith(".vsixmanifest", StringComparison.OrdinalIgnoreCase));

        foreach (ZipArchiveEntry manifestEntry in manifestEntries)
        {
            using StreamReader reader = new(manifestEntry.Open());
            string manifestText = reader.ReadToEnd();
            Assert.IsFalse(
                manifestText.Contains(legacyIdentity, StringComparison.Ordinal),
                $"Legacy extension identity found in packaged entry {manifestEntry.FullName}.");
        }
    }

    // Bundled documents must stay English only, and the manifest License path must resolve to a
    // file that is actually packaged.
    [TestMethod]
    public void PackagedVsix_BundlesEnglishLicenseOnly()
    {
        string packagedVsixPath = FindPackagedVsix(FindRepositoryRoot());
        using var archive = ZipFile.OpenRead(packagedVsixPath);

        ZipArchiveEntry manifestEntry = archive.GetEntry("extension.vsixmanifest")
            ?? throw new AssertFailedException("The packaged VSIX does not contain extension.vsixmanifest.");
        using Stream manifestStream = manifestEntry.Open();
        XDocument manifest = XDocument.Load(manifestStream);
        XElement metadata = manifest.Root?.Element(VsixNamespace + "Metadata")
            ?? throw new AssertFailedException("extension.vsixmanifest does not contain Metadata.");
        string? license = metadata.Element(VsixNamespace + "License")?.Value;

        Assert.AreEqual(ExtensionIdentity.License, license);
        Assert.IsNotNull(archive.GetEntry(ExtensionIdentity.License), $"{ExtensionIdentity.License} is not packaged.");

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            Assert.IsFalse(
                entry.FullName.EndsWith("_ja.md", StringComparison.OrdinalIgnoreCase),
                $"A Japanese document is packaged: {entry.FullName}.");
        }
    }

    private static void AssertManifestIdentity(XDocument manifest, string manifestName)
    {
        XElement metadata = manifest.Root?.Element(VsixNamespace + "Metadata")
            ?? throw new AssertFailedException($"{manifestName} does not contain Metadata.");
        XElement identity = metadata.Element(VsixNamespace + "Identity")
            ?? throw new AssertFailedException($"{manifestName} does not contain Identity.");
        string versionText = identity.Attribute("Version")?.Value
            ?? throw new AssertFailedException($"{manifestName} does not contain an identity version.");

        Assert.AreEqual(ExtensionIdentity.Id, identity.Attribute("Id")?.Value, manifestName);
        Assert.AreEqual(ExtensionIdentity.PublisherName, identity.Attribute("Publisher")?.Value, manifestName);
        Assert.AreEqual(
            ExtensionIdentity.DisplayName,
            metadata.Element(VsixNamespace + "DisplayName")?.Value,
            manifestName);
        Assert.AreEqual(ExtensionIdentity.AssemblyVersion, NormalizeVersion(Version.Parse(versionText)), manifestName);
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexForVisualStudio.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new AssertFailedException("Could not locate the repository root.");
    }

    private static string FindPackagedVsix(string repositoryRoot)
    {
        string configuration = FindBuildConfiguration();
        string outputDirectory = Path.Combine(
            repositoryRoot,
            "src",
            ExtensionProjectDirectory,
            "bin",
            configuration);
        string? vsixPath = Directory
            .EnumerateFiles(outputDirectory, "*.vsix", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return vsixPath
            ?? throw new AssertFailedException($"No packaged VSIX was found under {outputDirectory}.");
    }

    private static string FindBuildConfiguration()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (string.Equals(directory.Name, "Debug", StringComparison.OrdinalIgnoreCase)
                || string.Equals(directory.Name, "Release", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Name;
            }
        }

        throw new AssertFailedException("Could not determine the test build configuration.");
    }
}
