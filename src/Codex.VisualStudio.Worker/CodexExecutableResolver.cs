namespace Codex.VisualStudio.Worker;

public static class CodexExecutableResolver
{
    private static readonly string[] CodexScriptPathCandidates =
    [
        "codex.cmd",
        "codex.bat",
    ];

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
            if (TryResolveCommandFromPath(requestedPath, out string? resolvedConfigured))
            {
                WorkerDiagnostics.Write("codex executable resolved from Worker options PATH lookup");
                return resolvedConfigured;
            }

            WorkerDiagnostics.Write(IsUsableFile(requestedPath)
                ? "codex executable resolved from Worker options"
                : "configured codex executable does not exist");
            return requestedPath;
        }

        if (TryResolveOnPath(["codex.exe"], out string? pathResolvedCandidate)
            || TryResolveOnPath(CodexScriptPathCandidates, out pathResolvedCandidate))
        {
            WorkerDiagnostics.Write("codex executable resolved from PATH");
            return pathResolvedCandidate;
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

    private static bool TryResolveCommandFromPath(string requestedCommand, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedCommand))
        {
            return false;
        }

        if (requestedCommand.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || Path.IsPathRooted(requestedCommand))
        {
            return false;
        }

        if (Path.HasExtension(requestedCommand))
        {
            return TryResolveOnPath([requestedCommand], out resolvedPath);
        }

        IEnumerable<string> pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<string> candidateFileNames = pathExt.Select(ext => $"{requestedCommand}{ext}");
        return TryResolveOnPath(candidateFileNames, out resolvedPath);
    }

    private static bool TryResolveOnPath(IEnumerable<string> candidateFileNames, out string resolvedPath)
    {
        foreach (string directory in GetPathDirectories())
        {
            foreach (string fileName in candidateFileNames)
            {
                string candidate = Path.Combine(directory, fileName);
                if (IsUsableFile(candidate) && !IsWindowsAppsPath(candidate))
                {
                    resolvedPath = Path.GetFullPath(candidate);
                    return true;
                }
            }
        }

        resolvedPath = string.Empty;
        return false;
    }

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
