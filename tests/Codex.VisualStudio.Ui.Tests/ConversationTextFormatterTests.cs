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

    [TestMethod]
    public void Format_PreservesLiteralCommandOutputAndCode()
    {
        const string literalContent = "C:\\Users\\<user>\\src\\app\r\n$_ = $matches[0]\r\napi[_-]?key\r\nTask<ForkThreadResult>\r\nDictionary<string, object?>\r\n<Grid><TextBlock Text=\"Hello\" /></Grid>\r\n# Heading\r\n- list item";

        string snapshot = ConversationTextFormatter.Format(
            "Conversation",
            DateTimeOffset.UnixEpoch,
            [new ThreadHistoryItem
            {
                Id = "message-1",
                Type = "agentMessage",
                Text = literalContent,
            }],
            new SafeMarkdownService());

        StringAssert.Contains(snapshot, literalContent);
    }

    [TestMethod]
    public void Format_IncludesFallbackExplanationAsLiteralText()
    {
        const string explanation = "This fallback preserves `literal` <tag> and $value.";

        string snapshot = ConversationTextFormatter.Format(
            "Conversation",
            DateTimeOffset.UnixEpoch,
            [new ThreadHistoryItem
            {
                Id = "message-1",
                Type = "agentMessage",
                Text = string.Empty,
                Parts =
                [
                    new ArtifactPart
                    {
                        Kind = ArtifactPartKind.Fallback,
                        Text = explanation,
                    },
                ],
            }],
            new SafeMarkdownService());

        StringAssert.Contains(snapshot, "[Fallback]");
        StringAssert.Contains(snapshot, explanation);
    }

    [TestMethod]
    public void Format_IncludesOrderedMcpTextPartsAndFallbackExplanation()
    {
        const string firstText = "first MCP result <tag>";
        const string fallback = "The remaining MCP result could not be displayed.";
        const string secondText = "second MCP result $_";

        string snapshot = ConversationTextFormatter.Format(
            "Conversation",
            DateTimeOffset.UnixEpoch,
            [new ThreadHistoryItem
            {
                Id = "mcp-call-1",
                Type = "mcpToolCall",
                Text = null,
                Parts =
                [
                    new ArtifactPart { Kind = ArtifactPartKind.Text, Text = firstText },
                    new ArtifactPart { Kind = ArtifactPartKind.Fallback, Text = fallback },
                    new ArtifactPart { Kind = ArtifactPartKind.Text, Text = secondText },
                ],
            }],
            new SafeMarkdownService());

        int firstIndex = snapshot.IndexOf(firstText, StringComparison.Ordinal);
        int fallbackIndex = snapshot.IndexOf(fallback, StringComparison.Ordinal);
        int secondIndex = snapshot.IndexOf(secondText, StringComparison.Ordinal);
        Assert.IsTrue(firstIndex >= 0 && fallbackIndex > firstIndex && secondIndex > fallbackIndex,
            "MCP text and fallback parts must remain visible in their original order.");
    }

    [TestMethod]
    public void Format_DoesNotDuplicateTextPartThatMatchesItemBody()
    {
        const string body = "MCP result body";

        string snapshot = ConversationTextFormatter.Format(
            "Conversation",
            DateTimeOffset.UnixEpoch,
            [new ThreadHistoryItem
            {
                Id = "mcp-call-1",
                Type = "mcpToolCall",
                Text = body,
                Parts =
                [new ArtifactPart { Kind = ArtifactPartKind.Text, Text = body }],
            }],
            new SafeMarkdownService());

        Assert.AreEqual(1, CountOccurrences(snapshot, body));
    }

    private static int CountOccurrences(string value, string searched)
    {
        int count = 0;
        int startIndex = 0;
        while ((startIndex = value.IndexOf(searched, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += searched.Length;
        }

        return count;
    }
}
