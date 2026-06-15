namespace Codex.VisualStudio.Contracts;

/// <summary>
/// Decides whether a file system path is inside an OS-protected directory (Windows,
/// Program Files, ProgramData, the running process's own install directory, a bare drive
/// root, etc.). Used to refuse writes/commands targeting such locations outright, without
/// even raising an approval request.
/// </summary>
public interface IProtectedDirectoryPolicy
{
    bool IsProtected(string path);
}

public sealed class ProtectedDirectoryPolicy : IProtectedDirectoryPolicy
{
    private readonly string[] protectedRoots;

    public ProtectedDirectoryPolicy()
        : this(GetDefaultProtectedRoots())
    {
    }

    public ProtectedDirectoryPolicy(IEnumerable<string> protectedRoots)
    {
        this.protectedRoots = protectedRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(SafeNormalize)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string? normalized = SafeNormalize(path);
        if (normalized is null)
        {
            // Cannot resolve the path at all; treat as protected (fail closed).
            return true;
        }

        if (IsDriveRoot(normalized))
        {
            return true;
        }

        foreach (string root in protectedRoots)
        {
            if (IsDriveRoot(root))
            {
                // A protected root that is itself a bare drive root (e.g. "C:") would match
                // every path on that drive via the prefix check below; the drive-root check
                // above already covers that case, so skip it here.
                continue;
            }

            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The default set of OS-protected roots: standard Windows system/program folders plus
    /// the directory the current process is running from (Visual Studio, PowerShell, etc.).
    /// </summary>
    public static IEnumerable<string> GetDefaultProtectedRoots()
    {
        var specialFolders = new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.CommonApplicationData,
        };

        foreach (Environment.SpecialFolder folder in specialFolders)
        {
            string value = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        yield return AppContext.BaseDirectory;
        yield return Environment.CurrentDirectory;
    }

    /// <summary>
    /// Normalizes a path to a full, trailing-separator-trimmed form for comparison.
    /// Returns <see langword="null"/> if the path cannot be resolved.
    /// </summary>
    private static string? SafeNormalize(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string trimmed = full.TrimEnd('\\', '/');
            return trimmed.Length == 0 ? full : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsDriveRoot(string normalized)
    {
        string? root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        string? normalizedRoot = SafeNormalize(root);
        return normalizedRoot is not null && normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
