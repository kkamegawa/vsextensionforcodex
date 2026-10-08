using System.Diagnostics;
using System.IO;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;

namespace Codex.VisualStudio.Extension;

public interface IArtifactFileActions
{
    Task OpenAsync(ArtifactActionResult result, LocalPath currentLocalRoot, CancellationToken cancellationToken);

    Task RevealAsync(ArtifactActionResult result, LocalPath currentLocalRoot, CancellationToken cancellationToken);
}

/// <summary>Opens or reveals only Worker-mapped files that remain physically inside the current local root.</summary>
public sealed class ArtifactFileActions : IArtifactFileActions
{
    private readonly Func<Uri, CancellationToken, Task> openDocument;
    private readonly Action<ProcessStartInfo> startProcess;

    public ArtifactFileActions()
        : this(null)
    {
    }

    public ArtifactFileActions(VisualStudioExtensibility? extensibility)
        : this(
            extensibility is null
                ? static (_, _) => Task.FromException(new InvalidOperationException("Visual Studio document services are unavailable."))
                : (uri, cancellationToken) => extensibility.Documents().OpenDocumentAsync(uri, cancellationToken),
            StartProcess)
    {
    }

    internal ArtifactFileActions(
        Func<Uri, CancellationToken, Task> openDocument,
        Action<ProcessStartInfo> startProcess)
    {
        this.openDocument = openDocument ?? throw new ArgumentNullException(nameof(openDocument));
        this.startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
    }

    public Task OpenAsync(ArtifactActionResult result, LocalPath currentLocalRoot, CancellationToken cancellationToken)
    {
        string path = ValidateResultPath(result, currentLocalRoot);
        return openDocument(new Uri(path, UriKind.Absolute), cancellationToken);
    }

    public Task RevealAsync(ArtifactActionResult result, LocalPath currentLocalRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("File reveal is available only on Windows.");
        }

        string path = ValidateResultPath(result, currentLocalRoot);
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add("/select," + path);
        startProcess(startInfo);
        return Task.CompletedTask;
    }

    private static string ValidateResultPath(ArtifactActionResult result, LocalPath currentLocalRoot)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(currentLocalRoot);
        if (!result.Success || !LocalPath.TryCreate(result.LocalPath, out LocalPath candidate)
            || !IsHostPath(currentLocalRoot) || !IsHostPath(candidate)
            || !File.Exists(candidate.Value)
            || !IsPhysicallyWithinRoot(currentLocalRoot.Value, candidate.Value))
        {
            throw new InvalidOperationException("The artifact is no longer an available file inside the current workspace.");
        }

        return candidate.Value;
    }

    private static bool IsHostPath(LocalPath path)
        => OperatingSystem.IsWindows()
            ? path.Family is PathFamily.WindowsDrive or PathFamily.WindowsUnc
            : path.Family == PathFamily.Posix;

    private static bool IsPhysicallyWithinRoot(string rootPath, string candidatePath)
    {
        try
        {
            if (!Directory.Exists(rootPath))
            {
                return false;
            }

            string physicalRoot = ResolvePhysicalPath(rootPath);
            string physicalCandidate = ResolvePhysicalPath(candidatePath);
            string rootPrefix = Path.TrimEndingDirectorySeparator(physicalRoot);
            if (!rootPrefix.EndsWith(Path.DirectorySeparatorChar)
                && !rootPrefix.EndsWith(Path.AltDirectorySeparatorChar))
            {
                rootPrefix += Path.DirectorySeparatorChar;
            }

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return physicalCandidate.Equals(Path.TrimEndingDirectorySeparator(physicalRoot), comparison)
                || physicalCandidate.StartsWith(rootPrefix, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string ResolvePhysicalPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string volumeRoot = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The artifact path has no filesystem root.");
        string remainder = fullPath.Substring(volumeRoot.Length);
        string current = volumeRoot;
        foreach (string segment in remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            bool isReparsePoint = (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0;
            if (isReparsePoint && info?.LinkTarget is null)
            {
                throw new IOException("The artifact path contains a reparse point.");
            }

            if (isReparsePoint)
            {
                current = (info ?? throw new IOException("The artifact path is no longer available.")).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("The artifact path contains an unresolved link.");
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo);
    }
}
