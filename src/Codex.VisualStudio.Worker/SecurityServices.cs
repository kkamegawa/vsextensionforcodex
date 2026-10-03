using System.Text.RegularExpressions;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public interface ISecretRedactor
{
    string Redact(string? value);

    // Registers an exact secret value (for example a bearer token read from a token file) for
    // literal redaction until the returned lease is disposed. Leases are reference counted, so a
    // duplicate registration never removes another active lease for the same value.
    IDisposable RegisterSecret(string secret) => NoopLease.Instance;

    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();

        public void Dispose()
        {
        }
    }
}

public sealed partial class SecretRedactor : ISecretRedactor
{
    private readonly object secretsGate = new();
    private readonly Dictionary<string, int> registeredSecrets = new(StringComparer.Ordinal);
    private string[] secretSnapshot = [];

    public IDisposable RegisterSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        lock (secretsGate)
        {
            registeredSecrets[secret] = registeredSecrets.TryGetValue(secret, out int count) ? count + 1 : 1;
            RefreshSnapshot();
        }

        return new SecretLease(this, secret);
    }

    internal int ActiveSecretCount
    {
        get
        {
            lock (secretsGate)
            {
                return registeredSecrets.Count;
            }
        }
    }

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        string result = value;

        // Literal secrets first, longest first, so a registered token is removed even when it
        // appears without any "token=" or "Authorization:" marker.
        foreach (string secret in Volatile.Read(ref secretSnapshot))
        {
            result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        result = AuthorizationRegex().Replace(result, "$1[REDACTED]");
        result = KeyValueSecretRegex().Replace(result, "$1[REDACTED]");
        result = PrivateKeyRegex().Replace(result, "-----BEGIN PRIVATE KEY-----[REDACTED]-----END PRIVATE KEY-----"); // gitleaks:allow
        return result;
    }

    private void Release(string secret)
    {
        lock (secretsGate)
        {
            if (!registeredSecrets.TryGetValue(secret, out int count))
            {
                return;
            }

            if (count <= 1)
            {
                registeredSecrets.Remove(secret);
            }
            else
            {
                registeredSecrets[secret] = count - 1;
            }

            RefreshSnapshot();
        }
    }

    private void RefreshSnapshot()
        => Volatile.Write(
            ref secretSnapshot,
            registeredSecrets.Keys.OrderByDescending(static key => key.Length).ToArray());

    private sealed class SecretLease : IDisposable
    {
        private SecretRedactor? owner;
        private readonly string secret;

        public SecretLease(SecretRedactor owner, string secret)
        {
            this.owner = owner;
            this.secret = secret;
        }

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(secret);
    }

    [GeneratedRegex(@"(?i)(authorization\s*:\s*(?:bearer|basic)\s+)[^\s]+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"(?i)((?:api[_-]?key|token|password|client[_-]?secret|access[_-]?token)\s*[=:]\s*)[^\s;,""]+")]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")]
    private static partial Regex PrivateKeyRegex();
}

public interface IPathAccessPolicy
{
    PathAccessResult Evaluate(string path, string workspaceRoot);
}

public sealed record PathAccessResult(string NormalizedPath, bool IsWithinWorkspace, bool IsValid, string? Reason);

public sealed class PathAccessPolicy : IPathAccessPolicy
{
    public PathAccessResult Evaluate(string path, string workspaceRoot)
    {
        try
        {
            string normalizedRoot = Normalize(workspaceRoot, Environment.CurrentDirectory);
            string normalizedPath = Normalize(path, normalizedRoot);
            bool isWithin = normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return new PathAccessResult(normalizedPath, isWithin, true, isWithin ? null : "The path is outside the workspace.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return new PathAccessResult(path, false, false, ex.Message);
        }
    }

    private static string Normalize(string path, string basePath)
    {
        string fullPath = Path.GetFullPath(path, basePath);
        string root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("The path has no root.", nameof(path));
        string current = root;
        string remainder = fullPath[root.Length..];
        foreach (string segment in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            string candidate = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            if (info is not null && info.LinkTarget is not null)
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                current = target?.FullName ?? candidate;
            }
            else
            {
                current = candidate;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
}

/// <summary>Resolves existing symlinks and junctions before a mapped path crosses a local trust boundary.</summary>
public sealed class LocalPathBoundary : ILocalPathBoundary
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public LocalPathBoundary(IPathAccessPolicy? pathAccessPolicy = null)
    {
        _ = pathAccessPolicy;
    }

    public bool IsWithinRoot(LocalPath root, LocalPath candidate)
    {
        if (root is null || candidate is null
            || !IsHostCompatible(root)
            || !IsHostCompatible(candidate)
            || root.Family != candidate.Family
            || !TryResolvePhysicalPath(root.Value, out string physicalRoot)
            || !TryResolvePhysicalPath(candidate.Value, out string physicalCandidate))
        {
            return false;
        }

        physicalRoot = Path.TrimEndingDirectorySeparator(physicalRoot);
        physicalCandidate = Path.TrimEndingDirectorySeparator(physicalCandidate);
        string rootPrefix = physicalRoot.EndsWith(Path.DirectorySeparatorChar)
            || physicalRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? physicalRoot
            : physicalRoot + Path.DirectorySeparatorChar;
        return physicalCandidate.Equals(physicalRoot, PathComparison)
            || physicalCandidate.StartsWith(rootPrefix, PathComparison);
    }

    private static bool IsHostCompatible(LocalPath path)
        => OperatingSystem.IsWindows()
            ? path.Family is PathFamily.WindowsDrive or PathFamily.WindowsUnc
            : path.Family == PathFamily.Posix;

    private static bool TryResolvePhysicalPath(string path, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            resolved = ResolvePhysicalPath(path, new HashSet<string>(PathComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal), 0);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string ResolvePhysicalPath(string path, HashSet<string> activeLinks, int depth)
    {
        if (depth > 64)
        {
            throw new IOException("The local path contains too many nested symbolic links.");
        }

        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (root.Length == 0)
        {
            throw new IOException("The local path has no filesystem root.");
        }

        string current = root;
        string remainder = fullPath[root.Length..];
        foreach (string segment in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            string next = Path.Combine(current, segment);
            FileInfo fileInfo = new(next);
            DirectoryInfo directoryInfo = new(next);
            string? linkTarget = fileInfo.LinkTarget ?? directoryInfo.LinkTarget;
            FileAttributes? attributes = TryGetAttributes(fileInfo);
            if (attributes is null)
            {
                attributes = TryGetAttributes(directoryInfo);
            }

            if (attributes is null)
            {
                if (linkTarget is not null)
                {
                    throw new IOException("The local path contains a broken reparse point.");
                }

                // A not-yet-created leaf is permitted; all existing ancestors have
                // already been checked as the path was walked from its filesystem root.
                current = next;
                continue;
            }

            bool isReparsePoint = (attributes.Value & FileAttributes.ReparsePoint) != 0;
            if (!isReparsePoint)
            {
                if (linkTarget is not null)
                {
                    throw new IOException("The filesystem reported a link without a reparse point.");
                }

                current = next;
                continue;
            }

            if (string.IsNullOrWhiteSpace(linkTarget) || !activeLinks.Add(next))
            {
                throw new IOException("The local path contains an unresolved or cyclic reparse point.");
            }

            try
            {
                FileSystemInfo info = fileInfo.LinkTarget is not null ? fileInfo : directoryInfo;
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    throw new IOException("The local path contains an unresolved reparse point.");
                }

                // Resolve the whole target path, including every ancestor. ResolveLinkTarget
                // only guarantees the final target object, not that its parent chain is physical.
                current = ResolvePhysicalPath(target.FullName, activeLinks, depth + 1);
            }
            finally
            {
                activeLinks.Remove(next);
            }
        }

        return Path.GetFullPath(current);
    }

    private static FileAttributes? TryGetAttributes(FileSystemInfo info)
    {
        try
        {
            return File.GetAttributes(info.FullName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}

public sealed record ApprovalPolicyResult(
    ApprovalRiskCategory Risk,
    string RiskKey,
    bool IsBlocked,
    string? BlockReason);

public interface IApprovalPolicyEngine
{
    ApprovalPolicyResult EvaluateCommand(string? command, string? cwd, string workspaceRoot, string? networkHost, int? networkPort);

    ApprovalPolicyResult EvaluateFile(string? path, string workspaceRoot);
}

public sealed partial class ApprovalPolicyEngine : IApprovalPolicyEngine
{
    private readonly IPathAccessPolicy pathPolicy;
    private readonly IProtectedDirectoryPolicy protectedDirectoryPolicy;

    public ApprovalPolicyEngine(IPathAccessPolicy pathPolicy)
        : this(pathPolicy, new ProtectedDirectoryPolicy())
    {
    }

    public ApprovalPolicyEngine(IPathAccessPolicy pathPolicy, IProtectedDirectoryPolicy protectedDirectoryPolicy)
    {
        this.pathPolicy = pathPolicy;
        this.protectedDirectoryPolicy = protectedDirectoryPolicy;
    }

    public ApprovalPolicyResult EvaluateCommand(
        string? command,
        string? cwd,
        string workspaceRoot,
        string? networkHost,
        int? networkPort)
    {
        if (!string.IsNullOrWhiteSpace(networkHost))
        {
            return new ApprovalPolicyResult(
                ApprovalRiskCategory.Network,
                $"network:{networkHost.ToLowerInvariant()}:{networkPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "*"}",
                false,
                null);
        }

        if (CredentialRegex().IsMatch(command ?? string.Empty) || SecretValueRegex().IsMatch(command ?? string.Empty))
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.CredentialOAuth, "credential", false, null);
        }

        if (DestructiveRegex().IsMatch(command ?? string.Empty))
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.Destructive, $"destructive:{command}", false, null);
        }

        string effectiveCwd = cwd ?? workspaceRoot;
        PathAccessResult cwdResult = pathPolicy.Evaluate(effectiveCwd, workspaceRoot);
        if (!cwdResult.IsValid)
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"cwd:{cwdResult.NormalizedPath}", true, cwdResult.Reason);
        }

        if (protectedDirectoryPolicy.IsProtected(cwdResult.NormalizedPath))
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"cwd:{cwdResult.NormalizedPath}", true, "The path is an OS-protected directory.");
        }

        if (!cwdResult.IsWithinWorkspace)
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"cwd:{cwdResult.NormalizedPath}", true, cwdResult.Reason);
        }

        return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceWrite, $"command:{command}", false, null);
    }

    public ApprovalPolicyResult EvaluateFile(string? path, string workspaceRoot)
    {
        string effectivePath = path ?? string.Empty;
        PathAccessResult result = pathPolicy.Evaluate(effectivePath, workspaceRoot);
        if (!result.IsValid)
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"file:{path}", true, result.Reason);
        }

        if (protectedDirectoryPolicy.IsProtected(result.NormalizedPath))
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"file:{result.NormalizedPath}", true, "The path is an OS-protected directory.");
        }

        return result.IsWithinWorkspace
            ? new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceWrite, $"file:{result.NormalizedPath}", false, null)
            : new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"file:{result.NormalizedPath}", false, result.Reason);
    }

    [GeneratedRegex(@"(?i)\b(rm\s+-rf|del\s+/[fsq]|remove-item\b.*-recurse|format\b|git\s+reset\s+--hard|git\s+clean\s+-[a-z]*f|drop\s+(database|table))\b")]
    private static partial Regex DestructiveRegex();

    [GeneratedRegex(@"(?i)\b(oauth|login|credential|token|client[_-]?secret|password|api[_-]?key|authorization)\b")]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"(?i)(?:sk-[a-z0-9_-]{16,}|gh[pousr]_[a-z0-9]{20,}|authorization\s*:\s*(?:bearer|basic)\s+\S+)")]
    private static partial Regex SecretValueRegex();
}

public sealed record ApprovalGrant(
    string RiskKey,
    ApprovalScope Scope,
    string? ThreadId,
    string? TurnId,
    DateTimeOffset CreatedAt);

public sealed class ApprovalGrantStore
{
    private readonly List<ApprovalGrant> grants = [];
    private readonly object gate = new();

    public void Add(ApprovalRequest request, ApprovalScope scope)
    {
        if (scope == ApprovalScope.Once)
        {
            return;
        }

        lock (gate)
        {
            grants.RemoveAll(item => item.RiskKey.Equals(request.RiskKey, StringComparison.OrdinalIgnoreCase)
                && item.Scope == scope
                && item.ThreadId == ScopeThreadId(request, scope)
                && item.TurnId == ScopeTurnId(request, scope));
            grants.Add(new ApprovalGrant(
                request.RiskKey,
                scope,
                ScopeThreadId(request, scope),
                ScopeTurnId(request, scope),
                DateTimeOffset.UtcNow));
        }
    }

    public bool IsApproved(ApprovalRequest request)
        => FindApproval(request) is not null;

    public ApprovalGrant? FindApproval(ApprovalRequest request)
    {
        lock (gate)
        {
            return grants.LastOrDefault(item =>
                item.RiskKey.Equals(request.RiskKey, StringComparison.OrdinalIgnoreCase)
                && item.Scope switch
                {
                    ApprovalScope.Session => true,
                    ApprovalScope.Thread => item.ThreadId == request.ThreadId,
                    ApprovalScope.Turn => item.ThreadId == request.ThreadId && item.TurnId == request.TurnId,
                    _ => false,
                });
        }
    }

    public void EndTurn(string? threadId, string? turnId)
    {
        lock (gate)
        {
            grants.RemoveAll(item => item.Scope == ApprovalScope.Turn
                && item.ThreadId == threadId
                && item.TurnId == turnId);
        }
    }

    public void EndThread(string? threadId)
    {
        lock (gate)
        {
            grants.RemoveAll(item => item.ThreadId == threadId
                && item.Scope is ApprovalScope.Thread or ApprovalScope.Turn);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            grants.Clear();
        }
    }

    public IReadOnlyList<ApprovalGrant> Snapshot()
    {
        lock (gate)
        {
            return grants.ToArray();
        }
    }

    private static string? ScopeThreadId(ApprovalRequest request, ApprovalScope scope)
        => scope is ApprovalScope.Thread or ApprovalScope.Turn ? request.ThreadId : null;

    private static string? ScopeTurnId(ApprovalRequest request, ApprovalScope scope)
        => scope == ApprovalScope.Turn ? request.TurnId : null;
}
