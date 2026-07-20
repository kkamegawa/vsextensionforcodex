using System.IO;
using System.Text;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// The choices presented when the resolved working directory has no solution or project yet.
/// <see cref="None"/> is returned when the user dismisses the prompt without choosing.
/// </summary>
public enum ScaffoldChoice
{
    None,
    EmptySolution,
    FileBasedApp,
    DontCreate,
}

/// <summary>
/// Offers to scaffold an empty solution (or a file-based app) when Codex's resolved
/// working directory does not yet contain one.
/// </summary>
public interface IProjectScaffolder
{
    /// <summary>
    /// If <paramref name="rootDirectory"/> has no solution or project, asks the user whether to
    /// create one and, if so, creates it. Does nothing if a solution/project already exists.
    /// </summary>
    Task EnsureScaffoldAsync(string rootDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Creates a root-level empty solution (<c>ROOT/&lt;Name&gt;.slnx</c>) or a file-based app
/// (no solution), based on the user's choice. Never overwrites existing files.
/// </summary>
public sealed class ProjectScaffolder : IProjectScaffolder
{
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    private readonly VisualStudioExtensibility? extensibility;

    public ProjectScaffolder(VisualStudioExtensibility? extensibility)
    {
        this.extensibility = extensibility;
    }

    public async Task EnsureScaffoldAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        if (extensibility is null || HasExistingSolutionOrProject(rootDirectory))
        {
            return;
        }

        ScaffoldChoice choice = await extensibility.Shell().ShowPromptAsync(
            "This folder has no solution or project yet. Set one up for Codex?",
            new PromptOptions<ScaffoldChoice>
            {
                Choices =
                {
                    { "Create an empty solution", ScaffoldChoice.EmptySolution },
                    { "Use a file-based app (no solution)", ScaffoldChoice.FileBasedApp },
                    { "Don't create anything", ScaffoldChoice.DontCreate },
                },
                DefaultChoiceIndex = 0,
                DismissedReturns = ScaffoldChoice.None,
                Title = "Set Up Codex Project",
            },
            cancellationToken).ConfigureAwait(false);

        switch (choice)
        {
            case ScaffoldChoice.EmptySolution:
                CreateEmptySolution(rootDirectory);
                break;
            case ScaffoldChoice.FileBasedApp:
                CreateFileBasedApp(rootDirectory);
                break;
            case ScaffoldChoice.DontCreate:
            case ScaffoldChoice.None:
            default:
                break;
        }
    }

    /// <summary>
    /// Looks for an existing <c>.sln</c>/<c>.slnx</c>/<c>.csproj</c> directly under
    /// <paramref name="rootDirectory"/> or under its <c>src</c> subfolder (the layout this
    /// scaffolder creates).
    /// </summary>
    internal static bool HasExistingSolutionOrProject(string rootDirectory)
    {
        try
        {
            if (HasMatchingFile(rootDirectory, "*.slnx") || HasMatchingFile(rootDirectory, "*.sln") || HasMatchingFile(rootDirectory, "*.csproj"))
            {
                return true;
            }

            string srcDirectory = Path.Combine(rootDirectory, "src");
            return HasMatchingFile(srcDirectory, "*.slnx") || HasMatchingFile(srcDirectory, "*.sln") || HasMatchingFile(srcDirectory, "*.csproj");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the folder can't be inspected, don't offer to scaffold over unknown content.
            return true;
        }
    }

    private static bool HasMatchingFile(string directory, string searchPattern)
        => Directory.Exists(directory) && Directory.EnumerateFiles(directory, searchPattern).Any();

    internal static void CreateEmptySolution(string rootDirectory)
    {
        string name = GetProjectName(rootDirectory);
        WriteFileIfMissing(
            Path.Combine(rootDirectory, $"{name}.slnx"),
            "<Solution>\r\n</Solution>\r\n");
    }

    internal static void CreateFileBasedApp(string rootDirectory)
    {
        // A file-based app (requirement 4) skips solution/project creation entirely; just seed a
        // starter file so Codex has something to work with.
        WriteFileIfMissing(
            Path.Combine(rootDirectory, "Program.cs"),
            "Console.WriteLine(\"Hello from Codex!\");\r\n");
    }

    private static void WriteFileIfMissing(string path, string contents)
    {
        if (PathEntryExists(path))
        {
            // Covers a dangling symlink whose target does not exist yet: on Windows,
            // FileStream(..., FileMode.CreateNew) transparently follows a reparse point, so
            // without this upfront check it would silently create contents at the link's target
            // instead of leaving the pre-existing leaf entry alone.
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                bufferSize: 1024,
                leaveOpen: false);
            writer.Write(contents);
        }
        catch (IOException ex) when (IsExistingPathException(ex))
        {
            // FileMode.CreateNew makes the non-overwrite guarantee atomic for the race between
            // the check above and this open call.
        }
        catch (UnauthorizedAccessException) when (PathEntryExists(path))
        {
            // Windows can report access denied when the leaf entry is an existing directory or
            // directory reparse point. Ignore only a leaf entry whose attributes can be read.
        }
    }

    private static bool IsExistingPathException(IOException exception)
        => (exception.HResult & 0xFFFF) is ErrorFileExists or ErrorAlreadyExists;

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException
            or UnauthorizedAccessException
            or IOException)
        {
            // File.GetAttributes resolves a symlink before reading attributes, so it throws for a
            // dangling link whose target does not exist. FileInfo.LinkTarget instead reads the
            // reparse point's own data without following it, so it still reports the leaf entry
            // as present when the link itself exists but is broken.
            return new FileInfo(path).LinkTarget is not null;
        }
    }

    /// <summary>
    /// Derives a project name from the working directory's folder name, falling back to
    /// <c>"App"</c> if the folder name is empty or not a valid identifier-ish name.
    /// </summary>
    private static string GetProjectName(string rootDirectory)
    {
        string folderName = new DirectoryInfo(rootDirectory).Name;
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "App";
        }

        char[] chars = folderName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        string sanitized = new(chars);
        if (!sanitized.Any(char.IsLetterOrDigit))
        {
            return "App";
        }

        return char.IsLetter(sanitized[0]) || sanitized[0] == '_'
            ? sanitized
            : $"_{sanitized}";
    }
}
