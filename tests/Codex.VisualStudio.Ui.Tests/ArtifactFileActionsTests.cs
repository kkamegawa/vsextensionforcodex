using System.Diagnostics;
using System.IO;
using Codex.VisualStudio.Extension;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ArtifactFileActionsTests
{
    [TestMethod]
    public async Task OpenValidatesCurrentFileAndStartsOnlyThatPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "artifact-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "result.txt");
        await File.WriteAllTextAsync(file, "data");
        try
        {
            ProcessStartInfo? started = null;
            Uri? opened = null;
            var actions = new ArtifactFileActions((uri, _) =>
            {
                opened = uri;
                return Task.CompletedTask;
            }, info => started = info);
            await actions.OpenAsync(new ArtifactActionResult { Success = true, LocalPath = file }, LocalPath.Create(root), CancellationToken.None);

            Assert.IsNotNull(opened);
            Assert.AreEqual(new Uri(file, UriKind.Absolute), opened);
            Assert.IsNull(started, "Opening a document must not launch the OS file association.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OpenRejectsStaleOrOutsidePathsWithoutStartingProcess()
    {
        string root = Path.Combine(Path.GetTempPath(), "artifact-actions-" + Guid.NewGuid().ToString("N"));
        string outsideRoot = Path.Combine(Path.GetTempPath(), "artifact-actions-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideRoot);
        string outsideFile = Path.Combine(outsideRoot, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "data");
        try
        {
            int starts = 0;
            var actions = new ArtifactFileActions((_, _) => Task.CompletedTask, _ => starts++);
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.OpenAsync(
                new ArtifactActionResult { Success = true, LocalPath = outsideFile }, LocalPath.Create(root), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.OpenAsync(
                new ArtifactActionResult { Success = true, LocalPath = Path.Combine(root, "gone.txt") }, LocalPath.Create(root), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.OpenAsync(
                new ArtifactActionResult { Success = false, LocalPath = outsideFile }, LocalPath.Create(root), CancellationToken.None));
            Assert.AreEqual(0, starts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RevealPassesOneArgumentListEntryWithoutCommandLineInterpolation()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Explorer reveal is a Windows-only action.");
        }

        string root = Path.Combine(Path.GetTempPath(), "artifact-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "name with spaces.txt");
        await File.WriteAllTextAsync(file, "data");
        try
        {
            ProcessStartInfo? started = null;
            var actions = new ArtifactFileActions((_, _) => Task.CompletedTask, info => started = info);
            await actions.RevealAsync(new ArtifactActionResult { Success = true, LocalPath = file }, LocalPath.Create(root), CancellationToken.None);

            Assert.IsNotNull(started);
            Assert.AreEqual("explorer.exe", started.FileName);
            Assert.IsFalse(started.UseShellExecute);
            Assert.AreEqual(1, started.ArgumentList.Count);
            Assert.AreEqual("/select," + file, started.ArgumentList[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
