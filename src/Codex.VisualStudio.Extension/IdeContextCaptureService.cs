using System.Text;
using System.IO;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Codex.VisualStudio.Extension;

internal static class IdeContextCaptureService
{
    public const int MaximumReferencedFiles = 10;
    public const int MaximumSelectionBytes = 32 * 1024;

    public static async Task<IdeContextInfo?> CaptureAsync(
        string? workspaceRoot,
        IClientContext? clientContext,
        CancellationToken cancellationToken)
    {
        if (clientContext is null || !TryGetWorkspaceRoot(workspaceRoot, out string? normalizedRoot))
        {
            return null;
        }

        ITextViewSnapshot? activeView;
        Uri? selectedPath;
        try
        {
            activeView = await clientContext.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            selectedPath = await clientContext.GetSelectedPathAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("IDE context capture failed", ex);
            return null;
        }

        string? selectionText = null;
        if (activeView is not null && !activeView.Selection.IsEmpty)
        {
            selectionText = activeView.Selection.Extent.ToString();
        }

        return Build(
            normalizedRoot,
            activeView?.FilePath,
            selectedPath?.IsFile == true ? selectedPath.LocalPath : null,
            selectionText);
    }

    internal static IdeContextInfo? Build(
        string workspaceRoot,
        string? activeDocumentPath,
        string? selectedFilePath,
        string? selectionText)
    {
        string? activePath = NormalizeWorkspaceFile(workspaceRoot, activeDocumentPath);
        string? selectedPath = NormalizeWorkspaceFile(workspaceRoot, selectedFilePath);
        string[] references = selectedPath is not null
            && !string.Equals(selectedPath, activePath, StringComparison.OrdinalIgnoreCase)
                ? [selectedPath]
                : [];
        string? boundedSelection = activePath is null || string.IsNullOrEmpty(selectionText)
            ? null
            : TruncateUtf8(selectionText, MaximumSelectionBytes);

        if (activePath is null && references.Length == 0 && boundedSelection is null)
        {
            return null;
        }

        return new IdeContextInfo
        {
            ActiveDocumentPath = activePath,
            ReferencedFilePaths = references.Take(MaximumReferencedFiles).ToArray(),
            SelectionFilePath = boundedSelection is null ? null : activePath,
            SelectionText = boundedSelection,
        };
    }

    internal static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        int bytes = 0;
        int characters = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maximumBytes)
            {
                break;
            }

            bytes += runeBytes;
            characters += rune.Utf16SequenceLength;
        }

        return value[..characters];
    }

    private static bool TryGetWorkspaceRoot(string? workspaceRoot, out string normalizedRoot)
    {
        normalizedRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return false;
        }

        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            return Directory.Exists(normalizedRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? NormalizeWorkspaceFile(string workspaceRoot, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(candidatePath);
            string relativePath = Path.GetRelativePath(workspaceRoot, fullPath);
            bool outsideWorkspace = Path.IsPathRooted(relativePath)
                || string.Equals(relativePath, "..", StringComparison.Ordinal)
                || relativePath.StartsWith(string.Concat("..", Path.DirectorySeparatorChar), StringComparison.Ordinal)
                || relativePath.StartsWith(string.Concat("..", Path.AltDirectorySeparatorChar), StringComparison.Ordinal);
            return outsideWorkspace ? null : fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
