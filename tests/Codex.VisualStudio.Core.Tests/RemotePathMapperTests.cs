using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class RemotePathMapperTests
{
    [TestMethod]
    public void MapsWindowsLocalRootToPosixServerRootBySegments()
    {
        var mapper = new RemotePathMapper(LocalPath.Create(@"C:\Repo"), ServerPath.Create("/srv/workspace"));

        Assert.IsTrue(mapper.TryMapLocalToServer(LocalPath.Create("c:/repo/src/./main.cs"), out ServerPath serverPath));
        Assert.AreEqual("/srv/workspace/src/main.cs", serverPath.Value);
        Assert.IsTrue(mapper.TryMapLocalToServer(LocalPath.Create(@"C:\Repo"), out ServerPath mappedRoot));
        Assert.AreEqual("/srv/workspace", mappedRoot.Value);
        Assert.IsFalse(mapper.TryMapLocalToServer(LocalPath.Create(@"C:\Repo2\main.cs"), out _));
        Assert.IsFalse(mapper.TryMapLocalToServer(LocalPath.Create(@"D:\Repo\main.cs"), out _));
    }

    [TestMethod]
    public void MapsToWindowsServerRootAndNormalizesMixedSeparators()
    {
        var mapper = new RemotePathMapper(LocalPath.Create(@"C:\Work"), ServerPath.Create("D:/Build/Repo/"));

        Assert.IsTrue(mapper.TryMapLocalToServer(LocalPath.Create("C:/Work/src\\main.cs"), out ServerPath serverPath));
        Assert.AreEqual(@"D:\Build\Repo\src\main.cs", serverPath.Value);
        Assert.IsTrue(mapper.TryMapServerToLocal(ServerPath.Create("d:/build/repo/src\\main.cs"), out LocalPath localPath));
        Assert.AreEqual(@"C:\Work\src\main.cs", localPath.Value);
        Assert.IsFalse(mapper.TryMapServerToLocal(ServerPath.Create(@"E:\Build\Repo\src\main.cs"), out _));
    }

    [TestMethod]
    public void SupportsUncRootsAndLongPathPrefixes()
    {
        var uncMapper = new RemotePathMapper(
            LocalPath.Create(@"\\fileserver\share\repo"),
            ServerPath.Create(@"\\buildserver\source\repo"));
        Assert.IsTrue(uncMapper.TryMapLocalToServer(
            LocalPath.Create(@"\\FILESERVER\SHARE\REPO\src\main.cs"), out ServerPath uncPath));
        Assert.AreEqual(@"\\buildserver\source\repo\src\main.cs", uncPath.Value);
        Assert.IsTrue(uncMapper.TryMapLocalToServer(
            LocalPath.Create(@"\\fileserver\\share\repo\\src\main.cs"), out ServerPath repeatedSeparators));
        Assert.AreEqual(@"\\buildserver\source\repo\src\main.cs", repeatedSeparators.Value);
        Assert.IsFalse(uncMapper.TryMapLocalToServer(LocalPath.Create(@"\\fileserver\other\repo\src\main.cs"), out _));

        var longPathMapper = new RemotePathMapper(LocalPath.Create(@"C:\repo"), ServerPath.Create("/repo"));
        string longLocalPath = @"\\?\C:\repo\" + new string('a', 300) + ".cs";
        Assert.IsTrue(longPathMapper.TryMapLocalToServer(LocalPath.Create(longLocalPath), out ServerPath longServerPath));
        Assert.AreEqual("/repo/" + new string('a', 300) + ".cs", longServerPath.Value);
    }

    [TestMethod]
    public void NormalizesDotSegmentsButRejectsTraversalOutsideFilesystemRoot()
    {
        var mapper = new RemotePathMapper(LocalPath.Create(@"C:\repo"), ServerPath.Create("/srv/repo"));

        Assert.IsTrue(mapper.TryMapServerToLocal(ServerPath.Create("/srv/repo/src/../readme.md"), out LocalPath normalized));
        Assert.AreEqual(@"C:\repo\readme.md", normalized.Value);
        Assert.IsTrue(ServerPath.TryCreate("/srv/repo/../../secrets.txt", out ServerPath escapedServerPath));
        Assert.IsFalse(mapper.TryMapServerToLocal(escapedServerPath, out _));
        Assert.IsTrue(ServerPath.TryCreate("/srv/repo/../srv/repo/returned.txt", out ServerPath reenteredServerPath));
        Assert.IsFalse(mapper.TryMapServerToLocal(reenteredServerPath, out _));
        Assert.IsFalse(LocalPath.TryCreate(@"C:\..\secrets.txt", out _));
    }

    [TestMethod]
    public void WindowsPathsCompareWithoutCaseAndPosixPathsRemainCaseSensitive()
    {
        Assert.IsTrue(LocalPath.Create(@"C:\Work\Ärea").Equals(LocalPath.Create(@"c:\work\äREA")));
        Assert.AreEqual(LocalPath.Create(@"C:\Work\Ärea").IdentityValue, LocalPath.Create(@"c:\work\äREA").IdentityValue);
        Assert.IsFalse(ServerPath.Create("/srv/Work").Equals(ServerPath.Create("/srv/work")));
        Assert.IsFalse(ServerPath.Create("/srv/é").Equals(ServerPath.Create("/srv/e\u0301")));

        var posixMapper = new RemotePathMapper(LocalPath.Create(@"C:\repo"), ServerPath.Create("/srv/Repo"));
        Assert.IsFalse(posixMapper.TryMapServerToLocal(ServerPath.Create("/srv/repo/file.txt"), out _));
        Assert.IsFalse(posixMapper.TryMapServerToLocal(ServerPath.Create("/srv/Repo/part\\name.txt"), out _));
    }

    [TestMethod]
    public void PosixPathsPreserveColonAndUnicodeComponents()
    {
        var mapper = new RemotePathMapper(LocalPath.Create("/workspace/repo"), ServerPath.Create("/srv/repo"));
        Assert.IsTrue(LocalPath.TryCreate("/workspace/repo/file:metadata/é.txt", out LocalPath localPath));
        Assert.IsTrue(mapper.TryMapLocalToServer(localPath, out ServerPath serverPath));
        Assert.AreEqual("/srv/repo/file:metadata/é.txt", serverPath.Value);
        Assert.IsTrue(mapper.TryMapServerToLocal(serverPath, out LocalPath roundTripped));
        Assert.AreEqual(localPath, roundTripped);
    }

    [TestMethod]
    public void RejectsRelativeUriDeviceAndAlternateDataStreamPaths()
    {
        Assert.IsFalse(LocalPath.TryCreate("repo/src/file.cs", out _));
        Assert.IsFalse(ServerPath.TryCreate("https://server/repo/file.cs", out _));
        Assert.IsFalse(LocalPath.TryCreate(@"\\.\PhysicalDrive0", out _));
        Assert.IsFalse(LocalPath.TryCreate(@"C:\repo\file.txt:secret", out _));
        Assert.IsFalse(LocalPath.TryCreate(@"C:\repo\CON.txt", out _));
        Assert.IsFalse(LocalPath.TryCreate(@"C:\repo\CON .txt", out _));
        Assert.IsFalse(LocalPath.TryCreate(@"C:\COM¹\repo", out _));
        Assert.IsFalse(LocalPath.TryCreate(@"C:\repo\LPT².txt", out _));
        Assert.IsFalse(LocalPath.TryCreate(@"C:\repo\file. ", out _));
        Assert.IsFalse(LocalPath.TryCreate("Å:\\repo", out _));
        Assert.IsFalse(ServerPath.TryCreate(@"\\server\.\repo", out _));
        Assert.IsFalse(ServerPath.TryCreate(@"\\server\..\repo", out _));
        Assert.IsFalse(ServerPath.TryCreate("C:relative\\file.txt", out _));

        var windowsMapper = new RemotePathMapper(LocalPath.Create(@"C:\repo"), ServerPath.Create("/srv/repo"));
        Assert.IsTrue(ServerPath.TryCreate("/srv/repo/file:metadata", out ServerPath posixPathWithColon));
        Assert.IsFalse(windowsMapper.TryMapServerToLocal(posixPathWithColon, out _));
    }

    [TestMethod]
    public void CheckedMappingsRejectLocalPhysicalBoundaryEscapes()
    {
        var mapper = new RemotePathMapper(LocalPath.Create(@"C:\repo"), ServerPath.Create("/srv/repo"));
        var boundary = new TestLocalPathBoundary { Allow = false };

        Assert.IsFalse(mapper.TryMapLocalToServer(
            LocalPath.Create(@"C:\repo\linked\file.txt"), boundary, out _, out RemotePathMappingFailure localFailure));
        Assert.AreEqual(RemotePathMappingFailure.LocalBoundaryViolation, localFailure);
        Assert.IsFalse(mapper.TryMapServerToLocal(
            ServerPath.Create("/srv/repo/linked/file.txt"), boundary, out _, out RemotePathMappingFailure serverFailure));
        Assert.AreEqual(RemotePathMappingFailure.LocalBoundaryViolation, serverFailure);

        boundary.Allow = true;
        Assert.IsTrue(mapper.TryMapServerToLocal(
            ServerPath.Create("/srv/repo/file.txt"), boundary, out LocalPath mapped, out RemotePathMappingFailure success));
        Assert.AreEqual(RemotePathMappingFailure.None, success);
        Assert.AreEqual(@"C:\repo\file.txt", mapped.Value);
    }

    [TestMethod]
    public void MapsHostTempAndDriveRootsWithTypedValues()
    {
        LocalPath tempRoot = LocalPath.Create(Path.Combine(Path.GetTempPath(), "workspace"));
        var tempMapper = new RemotePathMapper(tempRoot, ServerPath.Create("/srv/workspace"));
        Assert.IsTrue(tempMapper.TryMapLocalToServer(
            LocalPath.Create(Path.Combine(tempRoot.Value, "src", "main.cs")), out ServerPath serverPath));
        Assert.AreEqual("/srv/workspace/src/main.cs", serverPath.Value);
        Assert.IsFalse(tempMapper.TryMapLocalToServer(
            LocalPath.Create(Path.Combine(Path.GetTempPath(), "workspace2", "main.cs")), out _));

        LocalPath localRoot = LocalPath.Create(Path.GetPathRoot(Environment.SystemDirectory)!);
        var driveMapper = new RemotePathMapper(localRoot, ServerPath.Create("/srv/root"));
        Assert.IsTrue(driveMapper.TryMapServerToLocal(ServerPath.Create("/srv/root"), out LocalPath mappedRoot));
        Assert.AreEqual(localRoot, mappedRoot);
        Assert.IsTrue(driveMapper.TryMapServerToLocal(ServerPath.Create("/srv/root/src/main.cs"), out LocalPath mappedFile));
        Assert.AreEqual(LocalPath.Create(Path.Combine(localRoot.Value, "src", "main.cs")), mappedFile);
    }

    private sealed class TestLocalPathBoundary : ILocalPathBoundary
    {
        public bool Allow { get; set; }

        public bool IsWithinRoot(LocalPath root, LocalPath candidate) => Allow;
    }
}
