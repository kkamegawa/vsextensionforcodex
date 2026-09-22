using System.Runtime.InteropServices;

namespace Codex.VisualStudio.Contracts;

/// <summary>
/// Maps paths between the Visual Studio workspace and a remote app-server root.
/// Mapping is segment based; a root named <c>C:\repo</c> never matches <c>C:\repo2</c>.
/// </summary>
public sealed class RemotePathMapper
{
    private static readonly char[] PathSeparators = { '/' };
    private readonly string localRoot;
    private readonly string serverRoot;
    private readonly StringComparison localComparison;
    private readonly char serverSeparator;

    public RemotePathMapper(string localRoot, string serverRoot)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            throw new ArgumentException("A local root is required.", nameof(localRoot));
        }

        if (string.IsNullOrWhiteSpace(serverRoot))
        {
            throw new ArgumentException("A server root is required.", nameof(serverRoot));
        }

        this.localRoot = NormalizeLocal(localRoot);
        this.serverRoot = NormalizeServer(serverRoot);
        localComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        serverSeparator = serverRoot.Contains('\\') && !serverRoot.Contains('/') ? '\\' : '/';
    }

    public bool TryMapLocalToServer(string localPath, out string serverPath)
    {
        serverPath = string.Empty;
        string candidate;
        try
        {
            candidate = NormalizeLocal(localPath);
        }
        catch (Exception) when (localPath is null or { Length: 0 })
        {
            return false;
        }

        if (!TryGetRelative(candidate, localRoot, localComparison, out string relative))
        {
            return false;
        }

        serverPath = CombineServer(relative);
        return true;
    }

    public bool TryMapServerToLocal(string serverPath, out string localPath)
    {
        localPath = string.Empty;
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            return false;
        }

        string candidate = NormalizeServer(serverPath);
        if (!TryGetRelative(candidate, serverRoot, StringComparison.Ordinal, out string relative)
            || relative.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).Any(static part => part == "." || part == ".."))
        {
            return false;
        }

        string localCandidate = relative.Length == 0
            ? localRoot
            : Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        string fullPath = NormalizeLocal(localCandidate);
        if (!TryGetRelative(fullPath, localRoot, localComparison, out _))
        {
            return false;
        }

        localPath = fullPath.Replace('/', Path.DirectorySeparatorChar);
        return true;
    }

    private string CombineServer(string relative)
        => relative.Length == 0
            ? serverRoot
            : serverRoot.TrimEnd('/', '\\')
                + serverSeparator
                + relative.Replace('/', serverSeparator);

    private static string NormalizeLocal(string path)
    {
        string fullPath = Path.GetFullPath(path).Replace('\\', '/');
        string? root = Path.GetPathRoot(path);
        string normalizedRoot = root?.Replace('\\', '/') ?? string.Empty;
        if (normalizedRoot.Length > 0
            && string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedRoot;
        }

        return fullPath.Length > 1 ? fullPath.TrimEnd('/') : fullPath;
    }

    private static string NormalizeServer(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        while (normalized.Contains("//"))
        {
            normalized = normalized.Replace("//", "/");
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    private static bool TryGetRelative(
        string candidate,
        string root,
        StringComparison comparison,
        out string relative)
    {
        relative = string.Empty;
        if (string.Equals(candidate, root, comparison))
        {
            return true;
        }

        string prefix = root.EndsWith("/", StringComparison.Ordinal)
            || root.EndsWith("\\", StringComparison.Ordinal)
                ? root
                : root + '/';
        if (!candidate.StartsWith(prefix, comparison))
        {
            return false;
        }

        relative = candidate.Substring(prefix.Length).Replace('\\', '/');
        return relative.Length == 0
            || !relative.Split(PathSeparators).Any(static part => part == "." || part == "..");
    }
}
