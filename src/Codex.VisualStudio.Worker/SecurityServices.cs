using System.Text.RegularExpressions;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public interface ISecretRedactor
{
    string Redact(string? value);
}

public sealed partial class SecretRedactor : ISecretRedactor
{
    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        string result = AuthorizationRegex().Replace(value, "$1[REDACTED]");
        result = KeyValueSecretRegex().Replace(result, "$1[REDACTED]");
        result = PrivateKeyRegex().Replace(result, "-----BEGIN PRIVATE KEY-----[REDACTED]-----END PRIVATE KEY-----"); // gitleaks:allow
        return result;
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
        if (protectedDirectoryPolicy.IsProtected(effectiveCwd))
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"cwd:{effectiveCwd}", true, "The path is an OS-protected directory.");
        }

        PathAccessResult cwdResult = pathPolicy.Evaluate(effectiveCwd, workspaceRoot);
        if (!cwdResult.IsValid || !cwdResult.IsWithinWorkspace)
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"cwd:{cwdResult.NormalizedPath}", true, cwdResult.Reason);
        }

        return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceWrite, $"command:{command}", false, null);
    }

    public ApprovalPolicyResult EvaluateFile(string? path, string workspaceRoot)
    {
        string effectivePath = path ?? string.Empty;
        if (protectedDirectoryPolicy.IsProtected(effectivePath))
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"file:{effectivePath}", true, "The path is an OS-protected directory.");
        }

        PathAccessResult result = pathPolicy.Evaluate(effectivePath, workspaceRoot);
        if (!result.IsValid)
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"file:{path}", true, result.Reason);
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
