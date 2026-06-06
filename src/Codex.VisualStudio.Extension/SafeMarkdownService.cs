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
        return Markdown.ToPlainText(withoutControlCharacters, pipeline);
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
}