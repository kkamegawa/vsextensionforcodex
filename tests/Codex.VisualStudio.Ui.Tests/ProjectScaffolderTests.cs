using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ProjectScaffolderTests
{
    private const string EmptySolutionContents = "<Solution>\r\n</Solution>\r\n";

    [TestMethod]
    public async Task CreateEmptySolution_WritesOnlyRootLevelSlnxWithExactEncodingAndContents()
    {
        string directory = CreateTemporaryDirectory("Sample-App");
        try
        {
            ProjectScaffolder.CreateEmptySolution(directory);

            string solutionPath = Path.Combine(directory, "Sample_App.slnx");
            Assert.IsTrue(File.Exists(solutionPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "src")));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "Program.cs")));
            Assert.AreEqual(0, Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories).Count());

            byte[] preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
            byte[] expected = [.. preamble, .. Encoding.UTF8.GetBytes(EmptySolutionContents)];
            CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(solutionPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task CreateEmptySolution_DoesNotOverwriteExistingSolution()
    {
        string directory = CreateTemporaryDirectory("Existing");
        string solutionPath = Path.Combine(directory, "Existing.slnx");
        byte[] original = Encoding.UTF8.GetBytes("keep");
        await File.WriteAllBytesAsync(solutionPath, original);
        try
        {
            ProjectScaffolder.CreateEmptySolution(directory);

            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(solutionPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public void CreateEmptySolution_DoesNotFollowDanglingSolutionSymlink()
    {
        string directory = CreateTemporaryDirectory("SymlinkSafe");
        string testDirectory = Directory.GetParent(directory)!.FullName;
        string solutionPath = Path.Combine(directory, "SymlinkSafe.slnx");
        string outsidePath = Path.Combine(testDirectory, "outside.slnx");
        try
        {
            try
            {
                File.CreateSymbolicLink(solutionPath, outsidePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Inconclusive($"Symbolic links are unavailable in this test environment: {ex.Message}");
            }

            ProjectScaffolder.CreateEmptySolution(directory);

            Assert.IsFalse(File.Exists(outsidePath));
            Assert.IsTrue(File.GetAttributes(solutionPath).HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task CreateEmptySolution_ConcurrentCallsProduceOneExactSolution()
    {
        const int concurrentCallCount = 16;
        string directory = CreateTemporaryDirectory("Concurrent");
        try
        {
            using var startGate = new Barrier(concurrentCallCount);
            Task[] calls = Enumerable.Range(0, concurrentCallCount)
                .Select(_ => Task.Run(() =>
                {
                    startGate.SignalAndWait();
                    ProjectScaffolder.CreateEmptySolution(directory);
                }))
                .ToArray();

            await Task.WhenAll(calls);

            string[] entries = Directory.GetFileSystemEntries(directory);
            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(Path.Combine(directory, "Concurrent.slnx"), entries[0]);

            byte[] preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
            byte[] expected = [.. preamble, .. Encoding.UTF8.GetBytes(EmptySolutionContents)];
            CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(entries[0]));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public void CreateEmptySolution_ProducesValidEmptySolutionXml()
    {
        string directory = CreateTemporaryDirectory("XmlCompatible");
        try
        {
            ProjectScaffolder.CreateEmptySolution(directory);

            XDocument document = XDocument.Load(Path.Combine(directory, "XmlCompatible.slnx"));
            Assert.IsNotNull(document.Root);
            Assert.AreEqual("Solution", document.Root.Name.LocalName);
            Assert.AreEqual(0, document.Root.Elements().Count());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task CreateEmptySolution_IsAcceptedByDotNetSlnCommand()
    {
        string directory = CreateTemporaryDirectory("DotNetCompatible");
        try
        {
            ProjectScaffolder.CreateEmptySolution(directory);
            string solutionPath = Path.Combine(directory, "DotNetCompatible.slnx");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.ArgumentList.Add("sln");
            process.StartInfo.ArgumentList.Add(solutionPath);
            process.StartInfo.ArgumentList.Add("list");

            Assert.IsTrue(process.Start());
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }

            Assert.AreEqual(
                0,
                process.ExitCode,
                $"dotnet sln rejected the generated file. Output: {await standardOutput} Error: {await standardError}");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task CreateFileBasedApp_RemainsSolutionAndProjectFree()
    {
        string directory = CreateTemporaryDirectory("FileBased");
        try
        {
            ProjectScaffolder.CreateFileBasedApp(directory);

            Assert.AreEqual("Console.WriteLine(\"Hello from Codex!\");\r\n", await File.ReadAllTextAsync(Path.Combine(directory, "Program.cs")));
            Assert.AreEqual(0, Directory.EnumerateFiles(directory, "*.sln*", SearchOption.AllDirectories).Count());
            Assert.AreEqual(0, Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories).Count());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory(string leafName)
    {
        string path = Path.Combine(Path.GetTempPath(), "CodexVsTests", Guid.NewGuid().ToString("N"), leafName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        string? testDirectory = Directory.GetParent(directory)?.FullName;
        Assert.IsNotNull(testDirectory);
        Directory.Delete(testDirectory, recursive: true);
    }
}
