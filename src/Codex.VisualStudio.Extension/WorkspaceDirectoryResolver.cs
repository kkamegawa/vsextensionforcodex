using System.IO;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.ProjectSystem.Query;
using Microsoft.Win32;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// The choices presented when no working directory could be determined from the open
/// solution/folder or a remembered value. <see cref="None"/> is returned when the user
/// dismisses the prompt without choosing.
/// </summary>
public enum WorkingDirectoryChoice
{
    None,
    Documents,
    VsDefaultLocation,
    Custom,
}

/// <summary>
/// Resolves the directory Codex should use as its working directory (<c>cwd</c>).
/// </summary>
public interface IWorkspaceDirectoryResolver
{
    /// <summary>
    /// Resolves the working directory, prompting the user if necessary. Returns
    /// <see langword="null"/> if the user dismissed the prompt without choosing — callers
    /// should not connect to Codex in that case.
    /// </summary>
    Task<string?> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the working directory in the following order: the open solution's folder, a
/// remembered choice from a previous session, or a folder the user picks via a VS prompt.
/// Any candidate that is an OS-protected directory is rejected — this is the extension-side
/// half of the defense-in-depth that keeps Codex out of protected folders (the worker also
/// hard-blocks operations targeting such directories).
/// </summary>
public sealed class WorkspaceDirectoryResolver : IWorkspaceDirectoryResolver
{
    private const int MaxPromptAttempts = 3;

    private readonly VisualStudioExtensibility? extensibility;
    private readonly IWorkspaceDirectoryStore store;
    private readonly IProtectedDirectoryPolicy protectedDirectoryPolicy;

    public WorkspaceDirectoryResolver(
        VisualStudioExtensibility? extensibility,
        IWorkspaceDirectoryStore store,
        IProtectedDirectoryPolicy? protectedDirectoryPolicy = null)
    {
        this.extensibility = extensibility;
        this.store = store;
        this.protectedDirectoryPolicy = protectedDirectoryPolicy ?? new ProtectedDirectoryPolicy();
    }

    public async Task<string?> ResolveAsync(CancellationToken cancellationToken)
    {
        string? solutionDirectory = await TryGetSolutionDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (solutionDirectory is not null && IsUsable(solutionDirectory))
        {
            return solutionDirectory;
        }

        string? remembered = store.GetLastDirectory();
        if (remembered is not null && IsUsable(remembered))
        {
            return remembered;
        }

        return await PromptForDirectoryAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsUsable(string directory)
        => !protectedDirectoryPolicy.IsProtected(directory) && Directory.Exists(directory);

    private async Task<string?> TryGetSolutionDirectoryAsync(CancellationToken cancellationToken)
    {
        if (extensibility is null)
        {
            return null;
        }

        try
        {
            IQueryResults<ISolutionSnapshot> solutions = await extensibility.Workspaces().QuerySolutionAsync(
                solution => solution.With(s => s.Path),
                cancellationToken).ConfigureAwait(false);
            ISolutionSnapshot? solution = solutions.FirstOrDefault();
            if (solution?.Path is { Length: > 0 } solutionPath)
            {
                return Path.GetDirectoryName(solutionPath);
            }
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Querying the open solution for its directory failed", ex);
        }

        return null;
    }

    private async Task<string?> PromptForDirectoryAsync(CancellationToken cancellationToken)
    {
        if (extensibility is null)
        {
            return null;
        }

        string message = "Codex needs a working directory. Open a solution, project, or folder, " +
            "or choose where Codex should work:";
        for (int attempt = 0; attempt < MaxPromptAttempts; attempt++)
        {
            WorkingDirectoryChoice choice = await extensibility.Shell().ShowPromptAsync(
                message,
                new PromptOptions<WorkingDirectoryChoice>
                {
                    Choices =
                    {
                        { "My Documents", WorkingDirectoryChoice.Documents },
                        { "Visual Studio's default projects folder", WorkingDirectoryChoice.VsDefaultLocation },
                        { "Choose a folder…", WorkingDirectoryChoice.Custom },
                    },
                    DefaultChoiceIndex = 0,
                    DismissedReturns = WorkingDirectoryChoice.None,
                    Title = "Codex Working Directory",
                },
                cancellationToken).ConfigureAwait(false);

            string? candidate = choice switch
            {
                WorkingDirectoryChoice.Documents => GetDocumentsDirectory(),
                WorkingDirectoryChoice.VsDefaultLocation => GetVsDefaultProjectsDirectory(),
                WorkingDirectoryChoice.Custom => await PromptForCustomDirectoryAsync(cancellationToken).ConfigureAwait(false),
                _ => null,
            };

            if (candidate is null)
            {
                // Dismissed, or the custom-path prompt was dismissed: stop without connecting.
                return null;
            }

            if (protectedDirectoryPolicy.IsProtected(candidate))
            {
                message = $"\"{candidate}\" is an OS-protected folder and cannot be used. " +
                    "Choose a different working directory for Codex:";
                continue;
            }

            try
            {
                Directory.CreateDirectory(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ExtensionDiagnostics.Write($"Failed to create working directory \"{candidate}\"", ex);
                message = $"\"{candidate}\" could not be created or accessed. " +
                    "Choose a different working directory for Codex:";
                continue;
            }

            store.SetLastDirectory(candidate);
            return candidate;
        }

        return null;
    }

    private async Task<string?> PromptForCustomDirectoryAsync(CancellationToken cancellationToken)
    {
        if (extensibility is null)
        {
            return null;
        }

        string? path = await extensibility.Shell().ShowPromptAsync(
            "Enter the folder Codex should use as its working directory:",
            InputPromptOptions.Default with { Title = "Codex Working Directory", DefaultText = GetDocumentsDirectory() },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ExtensionDiagnostics.Write($"The entered working directory \"{path}\" is not a valid path", ex);
            return null;
        }
    }

    private static string GetDocumentsDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CodexProjects");

    /// <summary>
    /// Best-effort lookup of Visual Studio's "default location for new projects" from the
    /// registry, falling back to <c>%USERPROFILE%\source\repos</c> (the IDE's own default)
    /// when the setting is absent or unreadable.
    /// </summary>
    private static string GetVsDefaultProjectsDirectory()
    {
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio");
            if (root is null)
            {
                return fallback;
            }

            foreach (string subKeyName in root.GetSubKeyNames())
            {
                using RegistryKey? generalKey = root.OpenSubKey($@"{subKeyName}\General");
                if (generalKey?.GetValue("ProjectsLocation") is string location && !string.IsNullOrWhiteSpace(location))
                {
                    return location;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            ExtensionDiagnostics.Write("Reading the Visual Studio default projects location failed", ex);
        }

        return fallback;
    }
}
