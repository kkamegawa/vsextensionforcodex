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
        int bodyStart = FindHtmlResponseStart(line);
        string diagnostic = bodyStart < 0
            ? line
            : string.Concat(line.AsSpan(0, bodyStart), "[HTML response omitted]");
        diagnostic = diagnostic.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const string Truncated = " [truncated]";
        return diagnostic.Length <= MaximumDiagnosticCharacters
            ? diagnostic
            : string.Concat(diagnostic.AsSpan(0, MaximumDiagnosticCharacters - Truncated.Length), Truncated);
    }

    internal const int MaximumDiagnosticReportCharacters = 32 * 1024;
    internal const string DiagnosticReportTruncated = "\n[Output truncated]";
    internal const string UnterminatedHtmlOmitted = "[The rest of the output was omitted because an HTML response did not end.]";
    private static readonly string[] HtmlResponseEnds = ["</html>", "</body>"];

    // A multi-line diagnostic report, such as codex doctor output, keeps its line breaks. HTML
    // response bodies are omitted from the line where they start through their closing tag, and
    // the whole report is bounded.
    public static string FormatDiagnosticReport(string text)
    {
        var report = new System.Text.StringBuilder();
        bool insideHtml = false;
        foreach (string rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine;
            if (insideHtml)
            {
                int end = FindHtmlResponseEnd(line, 0);
                if (end < 0)
                {
                    continue;
                }

                insideHtml = false;
                line = line[end..];
                if (line.Trim().Length == 0)
                {
                    continue;
                }
            }

            int bodyStart = FindHtmlResponseStart(line);
            if (bodyStart >= 0)
            {
                insideHtml = FindHtmlResponseEnd(line, bodyStart) < 0;
                line = string.Concat(line.AsSpan(0, bodyStart), "[HTML response omitted]");
            }

            // Room for the truncation notice is always reserved, so the result never exceeds the limit.
            int capacity = MaximumDiagnosticReportCharacters - DiagnosticReportTruncated.Length;
            if (report.Length + line.Length + 1 > capacity)
            {
                int remaining = Math.Max(0, capacity - report.Length);
                report.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                report.Append(DiagnosticReportTruncated);
                return report.ToString();
            }

            report.Append(line).Append('\n');
        }

        // An HTML body that never closed hid the rest of the report; say so instead of ending silently.
        if (insideHtml)
        {
            report.Append(UnterminatedHtmlOmitted);
        }

        return report.ToString().TrimEnd();
    }

    private static int FindHtmlResponseStart(string line)
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

        return bodyStart;
    }

    // Returns the index just after the last closing tag, or -1 when the body does not end here.
    private static int FindHtmlResponseEnd(string line, int startIndex)
    {
        int end = -1;
        foreach (string marker in HtmlResponseEnds)
        {
            int markerIndex = line.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= startIndex)
            {
                end = Math.Max(end, markerIndex + marker.Length);
            }
        }

        return end;
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
