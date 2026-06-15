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
    SolutionAndProject,
    FileBasedApp,
    DontCreate,
}

/// <summary>
/// Offers to scaffold a starter solution/project (or a file-based app) when Codex's resolved
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
/// Creates a minimal solution/project layout (<c>ROOT/src/&lt;Name&gt;.slnx</c> and
/// <c>ROOT/src/&lt;Name&gt;/&lt;Name&gt;.csproj</c>) or a file-based app (no solution), based on
/// the user's choice. Never overwrites existing files.
/// </summary>
public sealed class ProjectScaffolder : IProjectScaffolder
{
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
                    { "Create a solution and project", ScaffoldChoice.SolutionAndProject },
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
            case ScaffoldChoice.SolutionAndProject:
                CreateSolutionAndProject(rootDirectory);
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

    private static void CreateSolutionAndProject(string rootDirectory)
    {
        string name = GetProjectName(rootDirectory);
        string srcDirectory = Path.Combine(rootDirectory, "src");
        string projectDirectory = Path.Combine(srcDirectory, name);
        Directory.CreateDirectory(projectDirectory);

        WriteFileIfMissing(
            Path.Combine(srcDirectory, $"{name}.slnx"),
            $"<Solution>\r\n  <Project Path=\"{name}/{name}.csproj\" />\r\n</Solution>\r\n");

        WriteFileIfMissing(
            Path.Combine(projectDirectory, $"{name}.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
            "  <PropertyGroup>\r\n" +
            "    <OutputType>Exe</OutputType>\r\n" +
            "    <TargetFramework>net8.0</TargetFramework>\r\n" +
            "    <ImplicitUsings>enable</ImplicitUsings>\r\n" +
            "    <Nullable>enable</Nullable>\r\n" +
            "  </PropertyGroup>\r\n" +
            "</Project>\r\n");

        WriteFileIfMissing(
            Path.Combine(projectDirectory, "Program.cs"),
            "Console.WriteLine(\"Hello from Codex!\");\r\n");
    }

    private static void CreateFileBasedApp(string rootDirectory)
    {
        // A file-based app (requirement 4) skips solution/project creation entirely; just seed a
        // starter file so Codex has something to work with.
        WriteFileIfMissing(
            Path.Combine(rootDirectory, "Program.cs"),
            "Console.WriteLine(\"Hello from Codex!\");\r\n");
    }

    private static void WriteFileIfMissing(string path, string contents)
    {
        if (File.Exists(path))
        {
            return;
        }

        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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

        char[] chars = folderName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        string sanitized = new string(chars).Trim('_', '-');
        return sanitized.Length == 0 ? "App" : sanitized;
    }
}
