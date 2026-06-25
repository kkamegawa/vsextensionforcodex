using System.Text.RegularExpressions;
using Markdig;

namespace Codex.VisualStudio.Extension;

public sealed class SafeMarkdownService
{
    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .Build();

    public string ToSafeText(string value)
    {
        string withoutAnsi = AnsiEscape.Replace(value ?? string.Empty, string.Empty);
        string withoutHtml = HtmlTag.Replace(withoutAnsi, string.Empty);
        string withoutControlCharacters = ControlCharacters.Replace(withoutHtml, string.Empty);
        string plainText = Markdown.ToPlainText(withoutControlCharacters, pipeline);
        return CjkSpace.Replace(plainText, string.Empty);
    }

    private static readonly Regex ControlCharacters = new(
        "[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiEscape = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HtmlTag = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CjkSpace = new(
        @"(?<=[\u2E80-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF\u3000-\u303F]) (?=[\u2E80-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF\u3000-\u303F])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
