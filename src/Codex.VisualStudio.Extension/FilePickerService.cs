using System.IO;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Shell.FileDialog;

namespace Codex.VisualStudio.Extension;

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> PickFilesAsync(string? initialDirectory, CancellationToken cancellationToken);
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
