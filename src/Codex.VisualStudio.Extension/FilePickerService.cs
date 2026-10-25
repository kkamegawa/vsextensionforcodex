using System.IO;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Shell.FileDialog;

namespace Codex.VisualStudio.Extension;

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> PickFilesAsync(string? initialDirectory, CancellationToken cancellationToken);

    // Returns the chosen full path, or null when the dialog is canceled.
    Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}

public sealed class FilePickerService : IFilePickerService
{
    private const int MaximumFiles = 10;

    private readonly VisualStudioExtensibility? extensibility;
    private readonly IProtectedDirectoryPolicy protectedDirectoryPolicy;

    public FilePickerService(
        VisualStudioExtensibility? extensibility,
        IProtectedDirectoryPolicy? protectedDirectoryPolicy = null)
    {
        this.extensibility = extensibility;
        this.protectedDirectoryPolicy = protectedDirectoryPolicy ?? new ProtectedDirectoryPolicy();
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(
        string? initialDirectory,
        CancellationToken cancellationToken)
    {
        if (extensibility is null)
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<string>? selectedPaths = await extensibility.Shell().ShowOpenMultipleFilesDialogAsync(
            new FileDialogOptions
            {
                Title = "Attach Files",
                InitialDirectory = NormalizeInitialDirectory(initialDirectory),
            },
            cancellationToken).ConfigureAwait(false);

        return (selectedPaths ?? Array.Empty<string>())
            .Select(TryNormalizeFile)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumFiles)
            .ToArray();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        if (extensibility is null)
        {
            return null;
        }

        string? selected = await extensibility.Shell().ShowSaveAsFileDialogAsync(
            new FileDialogOptions
            {
                Title = "Save Diagnostic ZIP",
                InitialDirectory = NormalizeInitialDirectory(null),
                InitialFileName = suggestedFileName,
                Filters = new DialogFilters(new DialogFilter("ZIP archive", "*.zip")),
            },
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(selected);
            return protectedDirectoryPolicy.IsProtected(fullPath) ? null : fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private string? TryNormalizeFile(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) && !protectedDirectoryPolicy.IsProtected(fullPath)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeInitialDirectory(string? initialDirectory)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
                ? Path.GetFullPath(initialDirectory)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
}
