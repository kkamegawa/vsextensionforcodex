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
    public async Task OpenRejectsArtifactWhoseDirectoryLinkWasRetargetedOutsideCurrentRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "artifact-actions-link-root-" + Guid.NewGuid().ToString("N"));
        string outsideRoot = Path.Combine(Path.GetTempPath(), "artifact-actions-link-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideRoot);
        string insideTarget = Path.Combine(root, "inside-target");
        string outsideTarget = Path.Combine(outsideRoot, "outside-target");
        Directory.CreateDirectory(insideTarget);
        Directory.CreateDirectory(outsideTarget);
        string insideFile = Path.Combine(insideTarget, "artifact.txt");
        string outsideFile = Path.Combine(outsideTarget, "artifact.txt");
        string artifactLink = Path.Combine(root, "artifact-link");
        string artifactPath = Path.Combine(artifactLink, "artifact.txt");
        await File.WriteAllTextAsync(insideFile, "inside");
        await File.WriteAllTextAsync(outsideFile, "outside");
        try
        {
            CreateDirectoryLink(artifactLink, insideTarget);

            int opens = 0;
            var actions = new ArtifactFileActions((_, _) =>
            {
                opens++;
                return Task.CompletedTask;
            }, _ => { });
            var result = new ArtifactActionResult { Success = true, LocalPath = artifactPath };

            await actions.OpenAsync(result, LocalPath.Create(root), CancellationToken.None);
            Assert.AreEqual(1, opens, "An artifact that currently resolves inside the workspace should open.");

            Directory.Delete(artifactLink);
            CreateDirectoryLink(artifactLink, outsideTarget);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                actions.OpenAsync(result, LocalPath.Create(root), CancellationToken.None));
            Assert.AreEqual(1, opens, "The retargeted artifact must be rejected before opening the outside file.");
        }
        finally
        {
            if (Directory.Exists(artifactLink))
            {
                Directory.Delete(artifactLink);
            }

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

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Directory symbolic links and junctions are Windows-only.");
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or PlatformNotSupportedException
            or IOException
            or NotSupportedException)
        {
            // Junctions provide the same path redirection case without Developer Mode or elevation.
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
        {
            Assert.Inconclusive($"Directory symlinks and junctions are unavailable (exit code {process.ExitCode}).");
        }
    }
}
