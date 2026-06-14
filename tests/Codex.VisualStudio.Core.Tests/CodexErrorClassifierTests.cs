using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class CodexErrorClassifierTests
{
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
