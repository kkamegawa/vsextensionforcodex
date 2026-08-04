namespace Codex.VisualStudio.Extension;

// Resolution-time counterpart to the file-suggestion '#' trigger. Unlike
// TryGetFileSuggestionQuery (which only inspects the trailing token being typed, for the live
// completion overlay), this extracts every $<skill-name> token anywhere in submitted text, since
// a message can carry mentions anywhere by the time it is sent. "$$" is the escape for a literal
// '$', mirroring the "##" escape the file-mention parser uses for '#'.
internal static class SkillMentionParser
{
    public static IReadOnlyList<string> ExtractSkillTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        int index = 0;
        while (index < text.Length)
        {
            bool atWordStart = index == 0 || char.IsWhiteSpace(text[index - 1]);
            if (!atWordStart || text[index] != '$')
            {
                index++;
                continue;
            }

            int nameStart = index + 1;
            if (nameStart < text.Length && text[nameStart] == '$')
            {
                index = nameStart + 1;
                continue;
            }

            int nameEnd = nameStart;
            while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]))
            {
                nameEnd++;
            }

            if (nameEnd > nameStart)
            {
                tokens.Add(text[nameStart..nameEnd]);
            }

            index = nameEnd;
        }

        return tokens;
    }
}
