using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class SkillMentionParserTests
{
    private static readonly string[] SingleToken = ["review-diff"];
    private static readonly string[] TwoTokens = ["review-diff", "write-tests"];

    [TestMethod]
    public void SkillMentionParser_ExtractsTokenAtStart()
    {
        IReadOnlyList<string> tokens = SkillMentionParser.ExtractSkillTokens("$review-diff please");

        CollectionAssert.AreEqual(SingleToken, tokens.ToArray());
    }

    [TestMethod]
    public void SkillMentionParser_ExtractsMultipleTokensAnywhereInText()
    {
        IReadOnlyList<string> tokens = SkillMentionParser.ExtractSkillTokens("run $review-diff then $write-tests please");

        CollectionAssert.AreEqual(TwoTokens, tokens.ToArray());
    }

    [TestMethod]
    public void SkillMentionParser_IgnoresDoubledDollarEscape()
    {
        IReadOnlyList<string> tokens = SkillMentionParser.ExtractSkillTokens("costs $$5 today");

        Assert.AreEqual(0, tokens.Count);
    }

    [TestMethod]
    public void SkillMentionParser_IgnoresDollarInsideWord()
    {
        IReadOnlyList<string> tokens = SkillMentionParser.ExtractSkillTokens("price$review-diff not a mention");

        Assert.AreEqual(0, tokens.Count);
    }

    [TestMethod]
    public void SkillMentionParser_ReturnsEmptyForBareDollarAtEnd()
    {
        IReadOnlyList<string> tokens = SkillMentionParser.ExtractSkillTokens("trailing $");

        Assert.AreEqual(0, tokens.Count);
    }

    [TestMethod]
    public void SkillMentionParser_ReturnsEmptyForEmptyText()
    {
        IReadOnlyList<string> tokens = SkillMentionParser.ExtractSkillTokens(string.Empty);

        Assert.AreEqual(0, tokens.Count);
    }
}
