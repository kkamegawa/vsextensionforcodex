using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class ProtectedDirectoryPolicyTests
{
    [TestMethod]
    [DataRow(@"C:\Program Files\PowerShell\7")]
    [DataRow(@"C:\Program Files\PowerShell\7\hello.cs")]
    [DataRow(@"C:\Windows\System32")]
    [DataRow(@"C:\")]
    public void IsProtected_ReturnsTrueForOsProtectedAndDriveRootPaths(string path)
    {
        var policy = new ProtectedDirectoryPolicy(new[] { @"C:\Program Files", @"C:\Windows" });

        Assert.IsTrue(policy.IsProtected(path));
    }

    [TestMethod]
    public void IsProtected_ReturnsFalseForOrdinaryUserPath()
    {
        string allowed = Path.Combine(Path.GetTempPath(), "CodexVsTests", "Workspace");
        var policy = new ProtectedDirectoryPolicy(new[] { @"C:\Program Files", @"C:\Windows" });

        Assert.IsFalse(policy.IsProtected(allowed));
    }

    [TestMethod]
    public void IsProtected_TreatsEmptyOrUnresolvablePathAsProtected()
    {
        var policy = new ProtectedDirectoryPolicy(Array.Empty<string>());

        Assert.IsTrue(policy.IsProtected(string.Empty));
        Assert.IsTrue(policy.IsProtected("   "));
    }

    [TestMethod]
    public void IsProtected_IsCaseInsensitiveAndTrailingSeparatorTolerant()
    {
        var policy = new ProtectedDirectoryPolicy(new[] { @"C:\Program Files" });

        Assert.IsTrue(policy.IsProtected(@"c:\program files\"));
        Assert.IsTrue(policy.IsProtected(@"C:\PROGRAM FILES\Sub\file.cs"));
    }

    [TestMethod]
    public void IsProtected_StripsExtendedLengthPrefixes()
    {
        var policy = new ProtectedDirectoryPolicy(new[] { @"C:\Windows" });

        Assert.IsTrue(policy.IsProtected(@"\\?\C:\Windows\System32"));
        Assert.IsTrue(policy.IsProtected(@"\\?\C:\Windows"));
    }

    [TestMethod]
    public void GetDefaultProtectedRoots_IncludesWindowsAndProgramFiles()
    {
        string[] roots = ProtectedDirectoryPolicy.GetDefaultProtectedRoots().ToArray();

        Assert.IsTrue(roots.Contains(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.IsTrue(roots.Contains(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    }
}
