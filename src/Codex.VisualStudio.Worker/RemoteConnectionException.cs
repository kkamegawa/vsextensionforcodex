using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

/// <summary>
/// A remote connection failure reduced to a bounded category with fixed user-visible text. The
/// original exception is deliberately not retained, so no header, token, or raw transport
/// message survives past the point where the failure was classified.
/// </summary>
public sealed class RemoteConnectionException : Exception
{
    public RemoteConnectionException(RemoteConnectionFailure failure)
        : base(Describe(failure))
    {
        Failure = failure;
    }

    public RemoteConnectionFailure Failure { get; }

    public static string Describe(RemoteConnectionFailure failure) => failure switch
    {
        RemoteConnectionFailure.InvalidEndpoint => "The remote endpoint is not allowed. Use wss, or ws only for a loopback host.",
        RemoteConnectionFailure.ProfileChanged => "The remote profile changed after it was applied. Apply the saved profile again or switch to local.",
        RemoteConnectionFailure.ProfileUnavailable => "The applied remote profile is no longer saved and enabled. Apply a saved profile or switch to local.",
        RemoteConnectionFailure.TokenFileMissing => "The token file was not found. Check the token file path in the remote profile.",
        RemoteConnectionFailure.TokenFileUnreadable => "The token file could not be read. Use a readable file on a local drive.",
        RemoteConnectionFailure.TokenFileInvalid => "The token file does not contain one valid bearer token of at least 32 characters.",
        RemoteConnectionFailure.AuthenticationRejected => "The remote app-server rejected the bearer token.",
        RemoteConnectionFailure.UpgradeRejected => "The remote endpoint answered but did not accept the WebSocket connection. Check the endpoint path; redirects are not followed.",
        RemoteConnectionFailure.CertificateRejected => "The remote app-server certificate was not trusted.",
        RemoteConnectionFailure.NetworkFailure => "The remote app-server could not be reached (DNS, proxy, or network failure).",
        RemoteConnectionFailure.Timeout => "The remote app-server did not respond in time.",
        RemoteConnectionFailure.InitializeFailed => "The remote app-server did not complete JSON-RPC initialization.",
        RemoteConnectionFailure.AccountReadFailed => "The remote app-server closed the connection while reading the account status.",
        RemoteConnectionFailure.ConnectionLost => "The remote codex app-server connection was lost. Reconnect to continue.",
        RemoteConnectionFailure.PeerUnresponsive => "The remote codex app-server stopped responding. Reconnect to continue.",
        _ => "The remote app-server connection failed.",
    };
}
