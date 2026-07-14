using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Codex.VisualStudio.Extension;

public sealed class SafeMarkdownService
{
    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .Build();

    public string ToSafeText(string value)
    {
        string withoutControlCharacters = Sanitize(value);
        string plainText = Markdown.ToPlainText(withoutControlCharacters, pipeline);
        return CjkSpace.Replace(StripHtmlTags(plainText), string.Empty);
    }

    public IReadOnlyList<ChatBlockViewModel> ToBlocks(string value)
        => ToSafeTextAndBlocks(value).Blocks;

    public SafeMarkdownRenderResult ToSafeTextAndBlocks(string value)
    {
        string safeMarkdown = Sanitize(value);
        if (string.IsNullOrWhiteSpace(safeMarkdown))
        {
            return new(string.Empty, []);
        }

        MarkdownDocument document = Markdown.Parse(safeMarkdown, pipeline);
        var blocks = new List<ChatBlockViewModel>();
        var safeTextBuilder = new StringBuilder();
        foreach (Block block in document)
        {
            AppendBlock(block, blocks, 0, safeTextBuilder);
        }

        if (blocks.Count == 0)
        {
            string fallback = NormalizeText(StripHtmlTags(Markdown.ToPlainText(safeMarkdown, pipeline)));
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                blocks.Add(ChatBlockViewModel.Paragraph(fallback));
                AppendSafeTextSegment(safeTextBuilder, fallback);
            }
        }

        return new(CjkSpace.Replace(safeTextBuilder.ToString(), string.Empty), blocks);
    }

    private static string Sanitize(string value)
    {
        string withoutAnsi = AnsiEscape.Replace(value ?? string.Empty, string.Empty);
        return ControlCharacters.Replace(withoutAnsi, string.Empty);
    }

    private static void AppendBlock(Block block, List<ChatBlockViewModel> blocks, int listDepth, StringBuilder safeTextBuilder)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AddTextBlock(blocks, ChatBlockViewModel.Heading(
                    NormalizeText(StripHtmlTags(ExtractInlineText(heading.Inline))),
                    heading.Level),
                    safeTextBuilder);
                break;
            case ParagraphBlock paragraph:
                AddTextBlock(blocks, listDepth > 0
                    ? ChatBlockViewModel.ListItem(NormalizeText(StripHtmlTags(ExtractInlineText(paragraph.Inline))), "•", listDepth)
                    : ChatBlockViewModel.Paragraph(NormalizeText(StripHtmlTags(ExtractInlineText(paragraph.Inline)))),
                    safeTextBuilder);
                break;
            case FencedCodeBlock fenced:
                AddCodeBlock(blocks, GetCodeText(fenced), fenced.Info, safeTextBuilder);
                break;
            case CodeBlock code:
                AddCodeBlock(blocks, GetCodeText(code), string.Empty, safeTextBuilder);
                break;
            case ThematicBreakBlock:
                blocks.Add(ChatBlockViewModel.Separator());
                AppendSafeTextSegment(safeTextBuilder, string.Empty);
                break;
            case ListBlock list:
                AppendList(list, blocks, listDepth + 1, safeTextBuilder);
                break;
            case ContainerBlock container:
                foreach (Block child in container)
                {
                    AppendBlock(child, blocks, listDepth, safeTextBuilder);
                }
                break;
        }
    }

    private static void AppendList(ListBlock list, List<ChatBlockViewModel> blocks, int listDepth, StringBuilder safeTextBuilder)
    {
        foreach (Block item in list)
        {
            if (item is ListItemBlock listItem)
            {
                // Ordered lists render their source number ("1.", "2."); unordered lists a bullet.
                // Only the first paragraph of an item carries the marker; continuation paragraphs
                // keep the indent without repeating it. Nested blocks recurse with the same depth
                // so deeper ListBlocks bump the indent one level further.
                string marker = list.IsOrdered
                    ? string.Concat(listItem.Order.ToString(System.Globalization.CultureInfo.InvariantCulture), ".")
                    : "•";
                bool isFirstParagraph = true;
                foreach (Block child in listItem)
                {
                    if (child is ParagraphBlock paragraph)
                    {
                        AddTextBlock(blocks, ChatBlockViewModel.ListItem(
                            NormalizeText(StripHtmlTags(ExtractInlineText(paragraph.Inline))),
                            isFirstParagraph ? marker : string.Empty,
                            listDepth),
                            safeTextBuilder);
                        isFirstParagraph = false;
                    }
                    else
                    {
                        AppendBlock(child, blocks, listDepth, safeTextBuilder);
                    }
                }
            }
            else
            {
                AppendBlock(item, blocks, listDepth, safeTextBuilder);
            }
        }
    }

    private static void AddTextBlock(List<ChatBlockViewModel> blocks, ChatBlockViewModel block, StringBuilder safeTextBuilder)
    {
        if (!string.IsNullOrWhiteSpace(block.Text))
        {
            blocks.Add(block);
            AppendSafeTextSegment(safeTextBuilder, block.Text);
        }
    }

    private static void AddCodeBlock(List<ChatBlockViewModel> blocks, string code, string? language, StringBuilder safeTextBuilder)
    {
        if (!string.IsNullOrEmpty(code))
        {
            blocks.Add(ChatBlockViewModel.CodeBlock(code, NormalizeLanguage(language)));
            AppendSafeTextSegment(safeTextBuilder, code);
        }
    }

    private static string GetCodeText(CodeBlock block)
        => NormalizeLineEndings(block.Lines.ToString()).TrimEnd('\n');

    private static string NormalizeText(string value)
    {
        string normalized = NormalizeLineEndings(value).Trim();
        return CjkSpace.Replace(normalized, string.Empty);
    }

    private static string NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = ControlCharacters.Replace(value, string.Empty).Trim();
        int firstWhitespace = normalized.IndexOfAny(LanguageWhitespace);
        return firstWhitespace >= 0 ? normalized[..firstWhitespace] : normalized;
    }

    private static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string ExtractInlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendInlineText(inline.FirstChild, builder);
        return builder.ToString();
    }

    private static void AppendSafeTextSegment(StringBuilder builder, string segment)
    {
        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(segment))
        {
            builder.Append(segment);
        }
    }

    private static string StripHtmlTags(string value)
        => HtmlTag.Replace(value, string.Empty);

    private static void AppendInlineText(Inline? inline, StringBuilder builder)
    {
        for (Inline? current = inline; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content);
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline lineBreak:
                    builder.Append(lineBreak.IsHard ? '\n' : ' ');
                    break;
                case LinkInline link:
                    AppendInlineText(link.FirstChild, builder);
                    break;
                case ContainerInline container:
                    AppendInlineText(container.FirstChild, builder);
                    break;
            }
        }
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

    private static readonly char[] LanguageWhitespace = [' ', '\t', '\r', '\n'];
}

public readonly record struct SafeMarkdownRenderResult(string Text, IReadOnlyList<ChatBlockViewModel> Blocks);
