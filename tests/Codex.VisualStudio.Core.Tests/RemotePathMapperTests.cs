using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class RemotePathMapperTests
{
    [TestMethod]
    public void MapsOnlyWithinRootBoundary()
    {
        var mapper = new RemotePathMapper(
            Path.Combine(Path.GetTempPath(), "workspace"),
            "/srv/workspace");

        string localRoot = Path.Combine(Path.GetTempPath(), "workspace");
        Assert.IsTrue(mapper.TryMapLocalToServer(Path.Combine(localRoot, "src", "main.cs"), out string serverPath));
        Assert.AreEqual("/srv/workspace/src/main.cs", serverPath);
        Assert.IsFalse(mapper.TryMapLocalToServer(Path.Combine(Path.GetTempPath(), "workspace2", "main.cs"), out _));
    }

    [TestMethod]
    public void RejectsTraversalWhenMappingBackToLocal()
    {
        var mapper = new RemotePathMapper(Path.GetTempPath(), "/srv/workspace");

        Assert.IsFalse(mapper.TryMapServerToLocal("/srv/workspace/../secrets.txt", out _));
        Assert.IsTrue(mapper.TryMapServerToLocal("/srv/workspace/src/main.cs", out string localPath));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "src", "main.cs")), localPath);
    }
}
