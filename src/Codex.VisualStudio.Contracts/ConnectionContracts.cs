using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace Codex.VisualStudio.Contracts;

public enum ConnectionTargetKind
{
    Local,
    Remote,
}

// Typed reason carried in the error data of a WorkerErrorCodes.ConnectionOperationRejected
// response. It never contains paths, endpoints, token contents, or transport exception text.
public enum ConnectionOperationRejectionReason
{
    LocalProcessRequired,
    RemoteConnectionRequired,
    ProfileUnavailable,
    ProfileChanged,
    StaleGeneration,
}

// Bounded, user-actionable categories for a failed remote connection. The Worker maps each
// category to fixed text, so raw exception chains never reach the status message.
public enum RemoteConnectionFailure
{
    None,
    InvalidEndpoint,
    ProfileChanged,
    ProfileUnavailable,
    TokenFileMissing,
    TokenFileUnreadable,
    TokenFileInvalid,
    AuthenticationRejected,
    UpgradeRejected,
    CertificateRejected,
    NetworkFailure,
    Timeout,
    InitializeFailed,
    AccountReadFailed,
    ConnectionLost,
    PeerUnresponsive,
}

public enum HealthProbeState
{
    NotChecked,
    Healthy,
    Unhealthy,
    Redirected,
    Unreachable,
    TimedOut,
    Canceled,
}

public enum HealthScope
{
    // Probes always target the authority root (/healthz and /readyz).
    AuthorityRoot,
}

public enum RouteCoverage
{
    // The WebSocket URI has no routing path, so the root listener is the endpoint itself.
    NotApplicable,

    // The WebSocket URI routes through a path the root probes do not exercise.
    Unverified,
}

/// <summary>
/// Non-secret identity of the connection target for one attempt or connection generation.
/// Ready, Busy, and WaitingForApproval confirm it as the active target; Disconnected,
/// Connecting, and Degraded report the intended target.
/// </summary>
[DataContract]
public sealed class ConnectionTargetSnapshot
{
    [DataMember]
    public ConnectionTargetKind Kind { get; set; }

    // Configured display name of the remote profile; null for local.
    [DataMember]
    public string? DisplayName { get; set; }

    // RemoteProfileFingerprint of the applied profile metadata; null for local.
    [DataMember]
    public string? Fingerprint { get; set; }

    [DataMember]
    public long Generation { get; set; }

    // Opaque Worker-generated discriminator for the current state owner. It is salted per
    // Worker/attempt because the pinned account contract has no authoritative account ID.
    [DataMember]
    public string? StatePartitionFingerprint { get; set; }

    // Changes before the Worker begins using a different or unverifiable owner partition.
    [DataMember]
    public long OwnerGeneration { get; set; }

    public ConnectionTargetSnapshot Clone() => new()
    {
        Kind = Kind,
        DisplayName = DisplayName,
        Fingerprint = Fingerprint,
        Generation = Generation,
        StatePartitionFingerprint = StatePartitionFingerprint,
        OwnerGeneration = OwnerGeneration,
    };
}

/// <summary>
/// Immutable explicit reconnect request. The Extension builds it from the last applied snapshot
/// after re-reading saved settings; the Worker revalidates it before any token read, socket stop,
/// or send.
/// </summary>
public sealed class RemoteReconnectRequest
{
    public string ProfileName { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public long ExpectedGeneration { get; set; }
}

/// <summary>
/// Health diagnosis of a saved enabled profile. Only endpoint metadata crosses the boundary;
/// no token, token path, or root is needed because the probes are unauthenticated.
/// </summary>
public sealed class ConnectionDiagnosticsRequest
{
    public string ProfileName { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;
}

public sealed class HealthProbeResult
{
    public HealthProbeState State { get; set; } = HealthProbeState.NotChecked;

    public int? HttpStatus { get; set; }

    public long DurationMilliseconds { get; set; }

    // Fixed, categorical text. Never contains a response body, header, or exception chain.
    public string Reason { get; set; } = string.Empty;
}

public sealed class ConnectionDiagnosticsResult
{
    public string ProfileName { get; set; } = string.Empty;

    public HealthScope Scope { get; set; } = HealthScope.AuthorityRoot;

    public RouteCoverage RouteCoverage { get; set; } = RouteCoverage.NotApplicable;

    public HealthProbeResult Health { get; set; } = new();

    public HealthProbeResult Ready { get; set; } = new();

    public DateTimeOffset ObservedAt { get; set; }

    // Set when the request itself was refused before any probe (for example an invalid endpoint).
    public string? RejectionReason { get; set; }
}

/// <summary>
/// Canonical non-secret fingerprint of a remote profile's metadata. Token-file contents are not
/// an input, so token rotation leaves the fingerprint unchanged.
/// </summary>
public static class RemoteProfileFingerprint
{
    public static string Compute(
        string? name,
        string? endpoint,
        string? tokenFilePath,
        string? localRoot,
        string? serverRoot,
        bool enabled)
    {
        var builder = new StringBuilder();
        Append(builder, name);
        Append(builder, endpoint);
        Append(builder, tokenFilePath);
        Append(builder, localRoot);
        Append(builder, serverRoot);
        Append(builder, enabled ? "1" : "0");
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        var hex = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    // Length-prefixed fields keep ("ab", "c") and ("a", "bc") distinct.
    private static void Append(StringBuilder builder, string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        builder.Append(text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(text)
            .Append('|');
    }
}
