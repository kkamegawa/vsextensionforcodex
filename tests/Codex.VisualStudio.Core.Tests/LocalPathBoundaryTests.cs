using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class LocalPathBoundaryTests
{
    private string temporaryRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        temporaryRoot = Path.Combine(Path.GetTempPath(), $"local-path-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public void AcceptsRootAndExistingDescendantAndRejectsSibling()
    {
        string rootPath = CreateDirectory("root");
        string childPath = CreateDirectory(Path.Combine("root", "child"));
        string siblingPath = CreateDirectory("root-sibling");
        var boundary = new LocalPathBoundary();
        LocalPath root = LocalPath.Create(rootPath);

        Assert.IsTrue(boundary.IsWithinRoot(root, root));
        Assert.IsTrue(boundary.IsWithinRoot(root, LocalPath.Create(childPath)));
        Assert.IsFalse(boundary.IsWithinRoot(root, LocalPath.Create(siblingPath)));
    }

    [TestMethod]
    public void RejectsDifferentPathFamilies()
    {
        var boundary = new LocalPathBoundary();
        LocalPath windowsRoot = LocalPath.Create(@"C:\workspace");
        LocalPath posixCandidate = LocalPath.Create("/workspace/file.txt");

        Assert.IsFalse(boundary.IsWithinRoot(windowsRoot, posixCandidate));
    }

    [TestMethod]
    public void ResolvesInRootAndEscapingDirectoryLinks()
    {
        string rootPath = CreateDirectory("link-root");
        string insideTarget = CreateDirectory(Path.Combine("link-root", "real"));
        string outsideTarget = CreateDirectory("outside");
        string insideLink = Path.Combine(rootPath, "inside-link");
        string outsideLink = Path.Combine(rootPath, "outside-link");
        RequireDirectorySymlink(insideLink, insideTarget);
        RequireDirectorySymlink(outsideLink, outsideTarget);
        var boundary = new LocalPathBoundary();
        LocalPath root = LocalPath.Create(rootPath);

        Assert.IsTrue(boundary.IsWithinRoot(root, LocalPath.Create(Path.Combine(insideLink, "child"))));
        Assert.IsFalse(boundary.IsWithinRoot(root, LocalPath.Create(Path.Combine(outsideLink, "child"))));
    }

    [TestMethod]
    public void ResolvesConfiguredRootThatIsAnInRootLink()
    {
        string actualRoot = CreateDirectory("actual-root");
        string configuredRoot = Path.Combine(temporaryRoot, "configured-root");
        RequireDirectorySymlink(configuredRoot, actualRoot);
        string candidate = CreateDirectory(Path.Combine("actual-root", "child"));

        Assert.IsTrue(new LocalPathBoundary().IsWithinRoot(
            LocalPath.Create(configuredRoot),
            LocalPath.Create(candidate)));
    }

    [TestMethod]
    public void RejectsLinkWhoseTargetContainsEscapingAncestorLink()
    {
        string rootPath = CreateDirectory("nested-link-root");
        string outsidePath = CreateDirectory("nested-link-outside");
        string outsideChild = CreateDirectory(Path.Combine("nested-link-outside", "sub"));
        string ancestorAlias = Path.Combine(rootPath, "escape-alias");
        RequireDirectorySymlink(ancestorAlias, outsidePath);
        string candidateLink = Path.Combine(rootPath, "candidate-link");
        RequireDirectorySymlink(candidateLink, Path.Combine(ancestorAlias, "sub"));

        Assert.IsFalse(new LocalPathBoundary().IsWithinRoot(
            LocalPath.Create(rootPath),
            LocalPath.Create(candidateLink)));
        Assert.IsTrue(Directory.Exists(outsideChild));
    }

    [TestMethod]
    public void RejectsBrokenAndCyclicDirectoryLinks()
    {
        string rootPath = CreateDirectory("broken-root");
        string missingLink = Path.Combine(rootPath, "missing-link");
        RequireDirectorySymlink(missingLink, Path.Combine(temporaryRoot, "missing-target"));
        string firstLoop = Path.Combine(rootPath, "loop-a");
        string secondLoop = Path.Combine(rootPath, "loop-b");
        RequireDirectorySymlink(firstLoop, secondLoop);
        RequireDirectorySymlink(secondLoop, firstLoop);
        var boundary = new LocalPathBoundary();
        LocalPath root = LocalPath.Create(rootPath);

        Assert.IsFalse(boundary.IsWithinRoot(root, LocalPath.Create(Path.Combine(missingLink, "child.txt"))));
        Assert.IsFalse(boundary.IsWithinRoot(root, LocalPath.Create(firstLoop)));
    }

    private string CreateDirectory(string relativePath)
    {
        string path = Path.Combine(temporaryRoot, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RequireDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or PlatformNotSupportedException
            or IOException
            or NotSupportedException)
        {
            Assert.Inconclusive($"Directory symbolic links are unavailable: {exception.GetType().Name}.");
        }
    }
}
