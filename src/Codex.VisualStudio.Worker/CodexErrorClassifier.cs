namespace Codex.VisualStudio.Worker;

// Recognizes codex app-server stderr lines that indicate the process cannot
// reach the OpenAI backend (DNS/proxy/websocket failures). All codex output is
// treated as untrusted input, so matching is done on fixed, well-known
// substrings only.
internal static class CodexErrorClassifier
{
    private static readonly string[] NetworkFailureSignatures =
    [
        "os error 11003", // WSANO_RECOVERY - non-recoverable DNS failure
        "os error 11001", // WSAHOST_NOT_FOUND - host not found
        "failed to connect to websocket",
        "stream disconnected before completion",
        "error sending request for url",
        "transport channel closed",
        "failed to refresh available models",
    ];

    public const string NetworkFailureMessage =
        "Cannot reach chatgpt.com. Check your network connection or proxy settings " +
        "(HTTP_PROXY / HTTPS_PROXY). See the Codex output window for details.";

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
