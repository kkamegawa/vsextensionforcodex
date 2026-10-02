using System.Security.Cryptography;
using System.Text;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

/// <summary>
/// Creates an opaque, attempt-scoped owner key. The pinned account contract has no stable account
/// identifier, so keys are deliberately not reusable between Worker instances or attempts.
/// </summary>
internal static class ConnectionStatePartition
{
    public static string Create(
        WorkerOptions options,
        string workerInstanceId,
        long ownerGeneration,
        string? credentialFingerprint)
    {
        ArgumentNullException.ThrowIfNull(options);

        string endpoint = NormalizeEndpoint(options.RemoteEndpoint);
        string localRoot = LocalPath.TryCreate(options.LocalRoot, out LocalPath parsedLocalRoot)
            ? parsedLocalRoot.IdentityValue
            : NormalizeLocalRoot(options.WorkingDirectory);
        string workingDirectory = LocalPath.TryCreate(options.WorkingDirectory, out LocalPath parsedWorkingDirectory)
            ? parsedWorkingDirectory.IdentityValue
            : NormalizeLocalRoot(options.WorkingDirectory);
        string serverRoot = ServerPath.TryCreate(options.ServerRoot, out ServerPath parsedServerRoot)
            ? parsedServerRoot.IdentityValue
            : string.Empty;
        string serverWorkingDirectory = string.Empty;
        if (LocalPath.TryCreate(options.WorkingDirectory, out LocalPath localWorkingDirectory)
            && LocalPath.TryCreate(options.LocalRoot, out LocalPath mapperLocalRoot)
            && ServerPath.TryCreate(options.ServerRoot, out ServerPath mapperServerRoot)
            && new RemotePathMapper(mapperLocalRoot, mapperServerRoot)
                .TryMapLocalToServer(localWorkingDirectory, out ServerPath mappedServerWorkingDirectory))
        {
            serverWorkingDirectory = mappedServerWorkingDirectory.IdentityValue;
        }
        var builder = new StringBuilder();
        Append(builder, "owner-partition-v1");
        Append(builder, string.IsNullOrWhiteSpace(options.RemoteEndpoint) ? "local" : "remote");
        Append(builder, options.RemoteProfileName);
        Append(builder, options.RemoteProfileFingerprint);
        Append(builder, endpoint);
        Append(builder, localRoot);
        Append(builder, workingDirectory);
        Append(builder, serverRoot);
        Append(builder, serverWorkingDirectory);
        Append(builder, credentialFingerprint);
        Append(builder, workerInstanceId);
        Append(builder, ownerGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static string NormalizeEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint))
        {
            return value?.Trim() ?? string.Empty;
        }

        return endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.SafeUnescaped);
    }

    private static string NormalizeLocalRoot(string? value)
    {
        if (LocalPath.TryCreate(value, out LocalPath root))
        {
            return root.IdentityValue;
        }

        return value?.Trim() ?? string.Empty;
    }

    private static void Append(StringBuilder builder, string? value)
    {
        string text = value ?? string.Empty;
        builder.Append(text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(text)
            .Append('|');
    }
}
