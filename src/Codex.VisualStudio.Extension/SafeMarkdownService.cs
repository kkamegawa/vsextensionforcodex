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
        return CjkSpace.Replace(plainText, string.Empty);
    }

    public IReadOnlyList<ChatBlockViewModel> ToBlocks(string value)
    {
        string safeMarkdown = Sanitize(value);
        if (string.IsNullOrWhiteSpace(safeMarkdown))
        {
            return [];
        }

        MarkdownDocument document = Markdown.Parse(safeMarkdown, pipeline);
        var blocks = new List<ChatBlockViewModel>();
        foreach (Block block in document)
        {
            AppendBlock(block, blocks, 0);
        }

        if (blocks.Count == 0)
        {
            string fallback = NormalizeText(Markdown.ToPlainText(safeMarkdown, pipeline));
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                blocks.Add(ChatBlockViewModel.Paragraph(fallback));
            }
        }

        return blocks;
    }

    private static string Sanitize(string value)
    {
        string withoutAnsi = AnsiEscape.Replace(value ?? string.Empty, string.Empty);
        string withoutHtml = HtmlTag.Replace(withoutAnsi, string.Empty);
        return ControlCharacters.Replace(withoutHtml, string.Empty);
    }

    private static void AppendBlock(Block block, List<ChatBlockViewModel> blocks, int listDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AddTextBlock(blocks, ChatBlockViewModel.Heading(NormalizeText(ExtractInlineText(heading.Inline)), heading.Level));
                break;
            case ParagraphBlock paragraph:
                AddTextBlock(blocks, listDepth > 0
                    ? ChatBlockViewModel.ListItem(NormalizeText(ExtractInlineText(paragraph.Inline)))
                    : ChatBlockViewModel.Paragraph(NormalizeText(ExtractInlineText(paragraph.Inline))));
                break;
            case FencedCodeBlock fenced:
                AddCodeBlock(blocks, GetCodeText(fenced), fenced.Info);
                break;
            case CodeBlock code:
                AddCodeBlock(blocks, GetCodeText(code), string.Empty);
                break;
            case ThematicBreakBlock:
                blocks.Add(ChatBlockViewModel.Separator());
                break;
            case ListBlock list:
                AppendList(list, blocks, listDepth + 1);
                break;
            case ContainerBlock container:
                foreach (Block child in container)
                {
                    AppendBlock(child, blocks, listDepth);
                }
                break;
        }
    }

    private static void AppendList(ListBlock list, List<ChatBlockViewModel> blocks, int listDepth)
    {
        foreach (Block item in list)
        {
            if (item is ListItemBlock listItem)
            {
                foreach (Block child in listItem)
                {
                    AppendBlock(child, blocks, listDepth);
                }
            }
            else
            {
                AppendBlock(item, blocks, listDepth);
            }
        }
    }

    private static void AddTextBlock(List<ChatBlockViewModel> blocks, ChatBlockViewModel block)
    {
        if (!string.IsNullOrWhiteSpace(block.Text))
        {
            blocks.Add(block);
        }
    }

    private static void AddCodeBlock(List<ChatBlockViewModel> blocks, string code, string? language)
    {
        if (!string.IsNullOrEmpty(code))
        {
            blocks.Add(ChatBlockViewModel.CodeBlock(code, NormalizeLanguage(language)));
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
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : ControlCharacters.Replace(value.Trim(), string.Empty);

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
}
