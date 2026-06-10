namespace Codex.VisualStudio.Worker;

public static class CodexExecutableResolver
{
    public static string Resolve(string requestedPath)
    {
        string? configured = Environment.GetEnvironmentVariable("CODEX_PATH");
        if (IsUsableFile(configured))
        {
            WorkerDiagnostics.Write("codex executable resolved from CODEX_PATH");
            return Path.GetFullPath(configured!);
        }

        if (!string.Equals(requestedPath, "codex", StringComparison.OrdinalIgnoreCase))
        {
            WorkerDiagnostics.Write(IsUsableFile(requestedPath)
                ? "codex executable resolved from Worker options"
                : "configured codex executable does not exist");
            return requestedPath;
        }

        foreach (string directory in GetPathDirectories())
        {
            string candidate = Path.Combine(directory, "codex.exe");
            if (IsUsableFile(candidate) && !IsWindowsAppsPath(candidate))
            {
                WorkerDiagnostics.Write("codex executable resolved from PATH");
                return Path.GetFullPath(candidate);
            }
        }

        string localBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");
        if (Directory.Exists(localBin))
        {
            string? candidate = Directory
                .EnumerateFiles(localBin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault(IsUsableFile);
            if (candidate is not null)
            {
                WorkerDiagnostics.Write("codex executable resolved from OpenAI local cache");
                return Path.GetFullPath(candidate);
            }
        }

        WorkerDiagnostics.Write("codex executable resolution fell back to requested command");
        return requestedPath;
    }

    private static IEnumerable<string> GetPathDirectories()
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists);

    private static bool IsUsableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsWindowsAppsPath(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
