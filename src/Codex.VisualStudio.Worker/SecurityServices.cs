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
            string normalizedPath = Normalize(path);
            string normalizedRoot = Normalize(workspaceRoot);
            bool isWithin = normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return new PathAccessResult(normalizedPath, isWithin, true, isWithin ? null : "The path is outside the workspace.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PathAccessResult(path, false, false, ex.Message);
        }
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
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

    public ApprovalPolicyEngine(IPathAccessPolicy pathPolicy)
    {
        this.pathPolicy = pathPolicy;
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

        if (CredentialRegex().IsMatch(command ?? string.Empty))
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.CredentialOAuth, "credential", false, null);
        }

        if (DestructiveRegex().IsMatch(command ?? string.Empty))
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.Destructive, $"destructive:{command}", false, null);
        }

        PathAccessResult cwdResult = pathPolicy.Evaluate(cwd ?? workspaceRoot, workspaceRoot);
        if (!cwdResult.IsValid || !cwdResult.IsWithinWorkspace)
        {
            return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceOutside, $"cwd:{cwdResult.NormalizedPath}", true, cwdResult.Reason);
        }

        return new ApprovalPolicyResult(ApprovalRiskCategory.WorkspaceWrite, $"command:{command}", false, null);
    }

    public ApprovalPolicyResult EvaluateFile(string? path, string workspaceRoot)
    {
        PathAccessResult result = pathPolicy.Evaluate(path ?? string.Empty, workspaceRoot);
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

    [GeneratedRegex(@"(?i)\b(oauth|login|credential|token|client[_-]?secret|password)\b")]
    private static partial Regex CredentialRegex();
}
