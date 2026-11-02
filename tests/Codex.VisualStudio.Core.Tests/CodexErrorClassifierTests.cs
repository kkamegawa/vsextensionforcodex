using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class CodexErrorClassifierTests
{
    [TestMethod]
    [DataRow("<html>")]
    [DataRow("<!DOCTYPE html><HTML>")]
    public void StandardErrorOmitsHtmlResponseBody(string opening)
    {
        string diagnostic = CodexErrorClassifier.FormatStandardError($"ERROR rmcp: HTTP 403: {opening}private-challenge-body</html>");
        StringAssert.Contains(diagnostic, "HTTP 403");
        StringAssert.Contains(diagnostic, "[HTML response omitted]");
        Assert.IsFalse(diagnostic.Contains("private-challenge-body", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StandardErrorDiagnosticHasBoundedLengthAndNoLineBreaks()
    {
        string diagnostic = CodexErrorClassifier.FormatStandardError("ERROR\r\n" + new string('x', 10000));
        Assert.IsTrue(diagnostic.Length <= CodexErrorClassifier.MaximumDiagnosticCharacters);
        Assert.IsFalse(diagnostic.Contains('\r') || diagnostic.Contains('\n'));
        Assert.EndsWith(" [truncated]", diagnostic);
    }

    [TestMethod]
    public void McpAccessDeniedHasSpecificActionWithoutResponseBody()
    {
        Assert.AreEqual(CodexErrorClassifier.McpAccessDeniedMessage,
            CodexErrorClassifier.GetNetworkFailureMessage("ERROR rmcp::transport::worker: HTTP 403: <html>private</html>"));
        Assert.AreEqual(CodexErrorClassifier.NetworkFailureMessage,
            CodexErrorClassifier.GetNetworkFailureMessage("ERROR responses_websocket: HTTP 403"));
    }

    [TestMethod]
    public void WebsocketDnsFailureIsClassifiedAsNetworkFailure()
    {
        const string line =
            "ERROR codex_api::endpoint::responses_websocket: failed to connect to websocket: " +
            "IO error: ... (os error 11003), url: wss://chatgpt.com/backend-api/codex/responses";

        Assert.IsTrue(CodexErrorClassifier.IsNetworkFailure(line));
    }

    [TestMethod]
    public void RequestSendFailureIsClassifiedAsNetworkFailure()
    {
        const string line =
            "ERROR rmcp::transport::worker: worker quit with fatal: Transport channel closed, " +
            "when Client(HttpRequest(\"http/request failed: error sending request for url " +
            "(https://chatgpt.com/backend-api/wham/apps)\"))";

        Assert.IsTrue(CodexErrorClassifier.IsNetworkFailure(line));
    }

    [TestMethod]
    public void OrdinaryOutputIsNotClassifiedAsNetworkFailure()
    {
        Assert.IsFalse(CodexErrorClassifier.IsNetworkFailure("INFO codex: turn completed successfully"));
    }

    [TestMethod]
    public void NullOrWhitespaceIsNotClassifiedAsNetworkFailure()
    {
        Assert.IsFalse(CodexErrorClassifier.IsNetworkFailure(null));
        Assert.IsFalse(CodexErrorClassifier.IsNetworkFailure("   "));
    }
}
