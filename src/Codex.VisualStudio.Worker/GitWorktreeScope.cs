using System.ComponentModel;
using System.IO;

namespace Codex.VisualStudio.Worker;

/// <summary>
/// Finds the folder that corresponds to the working directory in every worktree of its repository.
/// Only read-only Git commands run, and any failure means the scope is not offered.
/// </summary>
internal sealed class GitWorktreeScope
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);
    private readonly BoundedProcessRun run;

    public GitWorktreeScope(BoundedProcessRun? run = null)
    {
        this.run = run ?? BoundedProcessRunner.RunAsync;
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            BoundedProcessResult prefix = await RunGitAsync(workingDirectory, ["rev-parse", "--show-prefix"], cancellationToken).ConfigureAwait(false);
            if (prefix.ExitCode != 0)
            {
                return [];
            }

            BoundedProcessResult worktrees = await RunGitAsync(workingDirectory, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
            return worktrees.ExitCode == 0
                ? BuildPaths(workingDirectory, prefix.StandardOutput.Trim(), ParseWorktreeRoots(worktrees.StandardOutput))
                : [];
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            WorkerDiagnostics.Write($"Git worktree lookup was unavailable ({ex.GetType().Name}).");
            return [];
        }
    }

    internal static IReadOnlyList<string> ParseWorktreeRoots(string porcelain)
    {
        var roots = new List<string>();
        string? current = null;
        bool skip = false;
        foreach (string rawLine in porcelain.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (current is not null && !skip)
                {
                    roots.Add(current);
                }

                current = null;
                skip = false;
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = line["worktree ".Length..];
            }
            else if (line == "bare" || line.StartsWith("prunable", StringComparison.Ordinal))
            {
                skip = true;
            }
        }

        if (current is not null && !skip)
        {
            roots.Add(current);
        }

        return roots;
    }

    // thread/list matches exactly, so the session's own spelling is kept for the current folder.
    internal static IReadOnlyList<string> BuildPaths(string workingDirectory, string prefix, IReadOnlyList<string> roots)
    {
        string current = Normalize(workingDirectory);
        var paths = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string root in roots)
        {
            string path = Normalize(Path.Combine(root, prefix));
            if (seen.Add(path))
            {
                paths.Add(seen.Comparer.Equals(path, current) ? workingDirectory : path);
            }
        }

        return paths;
    }

    private static string Normalize(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() && full.Length >= 2 && full[1] == ':'
            ? string.Concat(char.ToUpperInvariant(full[0]).ToString(), full[1..])
            : full;
    }

    private Task<BoundedProcessResult> RunGitAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
        => run("git", ["-c", "core.fsmonitor=false", .. arguments], workingDirectory, GitTimeout, cancellationToken);
}
