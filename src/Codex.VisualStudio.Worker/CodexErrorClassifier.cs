namespace Codex.VisualStudio.Worker;

// Recognizes codex app-server stderr lines that indicate the process cannot
// reach the OpenAI backend (DNS/proxy/websocket failures). All codex output is
// treated as untrusted input, so matching is done on fixed, well-known
// substrings only.
internal static class CodexErrorClassifier
{
    internal const int MaximumDiagnosticCharacters = 2048;
    private static readonly string[] HtmlResponseStarts = ["<!doctype html", "<html", "<body", "<script"];

    public const string McpAccessDeniedMessage =
        "An MCP service rejected its connection (HTTP 403). Open MCP details to review the affected service. The conversation can continue.";

    public static string GetNetworkFailureMessage(string line)
        => line.Contains("rmcp::", StringComparison.OrdinalIgnoreCase)
            && line.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
                ? McpAccessDeniedMessage
                : NetworkFailureMessage;

    // HTTP error bodies may contain challenge scripts, cookies, and very large HTML pages.
    // Keep only the diagnostic prefix and status in persisted/output diagnostics.
    public static string FormatStandardError(string line)
    {
        int bodyStart = -1;
        foreach (string marker in HtmlResponseStarts)
        {
            int markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0 && (bodyStart < 0 || markerIndex < bodyStart))
            {
                bodyStart = markerIndex;
            }
        }

        string diagnostic = bodyStart < 0
            ? line
            : string.Concat(line.AsSpan(0, bodyStart), "[HTML response omitted]");
        diagnostic = diagnostic.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const string Truncated = " [truncated]";
        return diagnostic.Length <= MaximumDiagnosticCharacters
            ? diagnostic
            : string.Concat(diagnostic.AsSpan(0, MaximumDiagnosticCharacters - Truncated.Length), Truncated);
    }

    private static readonly string[] NetworkFailureSignatures =
    [
        "os error 11003", // WSANO_RECOVERY - non-recoverable DNS failure
        "os error 11001", // WSAHOST_NOT_FOUND - host not found
        "failed to connect to websocket",
        "stream disconnected before completion",
        "error sending request for url",
        "transport channel closed",
    ];

    public const string NetworkFailureMessage =
        "Codex reported a network request failure. The app-server connection remains active. " +
        "Check the affected request and your network or proxy/TLS settings " +
        "(HTTP_PROXY / HTTPS_PROXY / ALL_PROXY / NO_PROXY / SSL_CERT_FILE / NODE_EXTRA_CA_CERTS). " +
        "See the Codex output window for details.";

    public static bool IsNetworkFailure(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        foreach (string signature in NetworkFailureSignatures)
        {
            if (line.Contains(signature, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
