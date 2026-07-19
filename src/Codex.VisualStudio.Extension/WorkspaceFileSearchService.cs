using System.IO;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace Codex.VisualStudio.Extension;

public sealed record WorkspaceFileSearchResult(string Path, string DisplayPath);

public interface IWorkspaceFileSearchService
{
    Task<IReadOnlyList<WorkspaceFileSearchResult>> SearchAsync(
        string workspaceRoot,
        string query,
        CancellationToken cancellationToken);
}

public sealed class WorkspaceFileSearchService : IWorkspaceFileSearchService
{
    private const int MaximumIndexedFiles = 5000;
    private const int MaximumResults = 20;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
    };

    private readonly VisualStudioExtensibility? extensibility;
    private readonly object cacheLock = new();
    private CacheEntry? cache;

    public WorkspaceFileSearchService(VisualStudioExtensibility? extensibility)
    {
        this.extensibility = extensibility;
    }

    public async Task<IReadOnlyList<WorkspaceFileSearchResult>> SearchAsync(
        string workspaceRoot,
        string query,
        CancellationToken cancellationToken)
    {
        string? normalizedRoot = TryNormalizeDirectory(workspaceRoot);
        if (normalizedRoot is null)
        {
            return Array.Empty<WorkspaceFileSearchResult>();
        }

        string[] paths = await GetIndexedFilesAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
        string searchText = query.Trim();
        return paths
            .Select(path => new WorkspaceFileSearchResult(path, Path.GetRelativePath(normalizedRoot, path)))
            .Select(result => new RankedResult(result, GetRank(result, searchText)))
            .Where(item => item.Rank >= 0)
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Result.DisplayPath.Length)
            .ThenBy(item => item.Result.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumResults)
            .Select(item => item.Result)
            .ToArray();
    }

    private async Task<string[]> GetIndexedFilesAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        lock (cacheLock)
        {
            if (cache is not null
                && string.Equals(cache.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.UtcNow - cache.CreatedAt < CacheDuration)
            {
                return cache.Paths;
            }
        }

        string[] paths = await QueryProjectFilesAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        if (paths.Length == 0)
        {
            paths = EnumerateDiskFiles(workspaceRoot, cancellationToken).ToArray();
        }

        lock (cacheLock)
        {
            cache = new CacheEntry(workspaceRoot, paths, DateTimeOffset.UtcNow);
        }

        return paths;
    }

    private async Task<string[]> QueryProjectFilesAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        if (extensibility is null)
        {
            return [];
        }

        try
        {
            IQueryResults<IFileSnapshot> files = await extensibility.Workspaces().QueryProjectsAsync(
                projects => projects.Get(project => project.Files).With(file => file.Path),
                cancellationToken).ConfigureAwait(false);
            return files
                .Select(file => TryNormalizeWorkspaceFile(workspaceRoot, file.Path))
                .Where(path => path is not null)
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumIndexedFiles)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Querying project files for attachment suggestions failed", ex);
            return [];
        }
    }

    private static IEnumerable<string> EnumerateDiskFiles(string workspaceRoot, CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(workspaceRoot);
        int count = 0;

        while (pendingDirectories.Count > 0 && count < MaximumIndexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pendingDirectories.Pop();

            foreach (string file in EnumerateFilesSafely(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count++ >= MaximumIndexedFiles)
                {
                    yield break;
                }

                yield return file;
            }

            foreach (string childDirectory in EnumerateDirectoriesSafely(directory))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(childDirectory)))
                {
                    pendingDirectories.Push(childDirectory);
                }
            }
        }
    }

    private static string[] EnumerateFilesSafely(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] EnumerateDirectoriesSafely(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int GetRank(WorkspaceFileSearchResult result, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        string fileName = Path.GetFileName(result.Path);
        if (fileName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return result.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : -1;
    }

    private static string? TryNormalizeDirectory(string path)
    {
        try
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? TryNormalizeWorkspaceFile(string workspaceRoot, string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string relativePath = Path.GetRelativePath(workspaceRoot, fullPath);
            bool outsideWorkspace = Path.IsPathRooted(relativePath)
                || relativePath.Equals("..", StringComparison.Ordinal)
                || relativePath.StartsWith(string.Concat("..", Path.DirectorySeparatorChar), StringComparison.Ordinal)
                || relativePath.StartsWith(string.Concat("..", Path.AltDirectorySeparatorChar), StringComparison.Ordinal);
            return !outsideWorkspace && File.Exists(fullPath) && !HasExcludedDirectory(relativePath)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasExcludedDirectory(string relativePath)
        => relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1)
            .Any(ExcludedDirectories.Contains);

    private sealed record CacheEntry(string WorkspaceRoot, string[] Paths, DateTimeOffset CreatedAt);

    private sealed record RankedResult(WorkspaceFileSearchResult Result, int Rank);
}
