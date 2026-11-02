using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ConversationTextFormatterTests
{
    [TestMethod]
    public void Format_NormalizesServerLineEndingsForTheVisualStudioEditor()
    {
        string snapshot = ConversationTextFormatter.Format(
            "Conversation",
            DateTimeOffset.UnixEpoch,
            [new ThreadHistoryItem
            {
                Id = "message-1",
                Type = "agentMessage",
                Text = "日本語の段落\nSecond line\r\nThird line\rFinal line",
            }],
            new SafeMarkdownService());

        StringAssert.Contains(snapshot, "日本語の段落\r\nSecond line\r\nThird line\r\nFinal line");
        string withoutWindowsNewlines = snapshot.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        Assert.IsFalse(withoutWindowsNewlines.Contains('\r') || withoutWindowsNewlines.Contains('\n'),
            "Opening a fixed snapshot must not prompt the user to normalize mixed line endings.");
    }
}
