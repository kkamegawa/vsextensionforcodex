using System.Text;

namespace Codex.VisualStudio.Extension;

internal sealed record ModelCommandArguments(string? Model, string? Reasoning, string? Speed)
{
    public bool HasSettings => Reasoning is not null || Speed is not null;
}

// Mirrors the Codex CLI 0.164.0 slash-command argument parser (tui slash_command_args.rs) so a
// preset written by either client is accepted or rejected the same way.
internal static class ModelCommandArgumentParser
{
    private const string ReasoningFlag = "--reasoning";
    private const string SpeedFlag = "--speed";

    public static bool TryParse(string arguments, out ModelCommandArguments? result, out string? errorMessage)
    {
        result = null;
        if (!ShellWordSplitter.TrySplit(arguments, out IReadOnlyList<string> words))
        {
            errorMessage = "Invalid command arguments. Check the quoting.";
            return false;
        }

        var positionals = new List<string>();
        string? reasoning = null;
        string? speed = null;
        bool parseSettings = true;
        for (int index = 0; index < words.Count; index++)
        {
            string word = words[index];
            if (parseSettings && word == "--")
            {
                parseSettings = false;
                continue;
            }

            if (!parseSettings || !word.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(word);
                continue;
            }

            int equals = word.IndexOf('=');
            string name = equals < 0 ? word : word[..equals];
            string? inlineValue = equals < 0 ? null : word[(equals + 1)..];
            if (name is not (ReasoningFlag or SpeedFlag))
            {
                errorMessage = $"Unknown command setting `{name}`.";
                return false;
            }

            // An empty inline value falls back to the next word, which must not be another setting.
            string? value = string.IsNullOrEmpty(inlineValue) ? null : inlineValue;
            if (value is null
                && index + 1 < words.Count
                && !words[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = words[++index];
            }

            if (value is null)
            {
                errorMessage = $"Command setting `{name}` requires a value.";
                return false;
            }

            if ((name == ReasoningFlag ? reasoning : speed) is not null)
            {
                errorMessage = $"Command setting `{name}` was provided more than once.";
                return false;
            }

            if (name == ReasoningFlag)
            {
                reasoning = value;
            }
            else
            {
                speed = value;
            }
        }

        if (positionals.Count > 1)
        {
            errorMessage = $"Unexpected argument '{positionals[1]}'. Use '--reasoning=<level>' or '--speed=<tier>'.";
            return false;
        }

        result = new ModelCommandArguments(positionals.Count == 0 ? null : positionals[0], reasoning, speed);
        errorMessage = null;
        return true;
    }
}

// A port of the POSIX-style word splitting in the shlex 1.3.0 crate that the Codex CLI uses:
// whitespace is space, tab, and newline; '#' at the start of a word comments out the rest of the
// line; backslash escapes outside quotes; single quotes are literal; double quotes honor \$ \` \"
// \\ and line continuation. An unterminated quote or a trailing backslash is an error.
internal static class ShellWordSplitter
{
    public static bool TrySplit(string input, out IReadOnlyList<string> words)
    {
        // The Visual Studio composer uses CRLF; the CLI composer produces LF only.
        string text = input.Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = new List<string>();
        words = result;
        int position = 0;
        while (true)
        {
            // Skip leading whitespace and comments.
            while (position < text.Length)
            {
                char character = text[position];
                if (character is ' ' or '\t' or '\n')
                {
                    position++;
                }
                else if (character == '#')
                {
                    int newline = text.IndexOf('\n', position);
                    position = newline < 0 ? text.Length : newline + 1;
                }
                else
                {
                    break;
                }
            }

            if (position >= text.Length)
            {
                return true;
            }

            var word = new StringBuilder();
            while (position < text.Length)
            {
                char character = text[position++];
                if (character is ' ' or '\t' or '\n')
                {
                    break;
                }

                switch (character)
                {
                    case '"':
                        if (!ReadDoubleQuoted(text, ref position, word))
                        {
                            return Fail(out words);
                        }

                        break;

                    case '\'':
                        int closing = text.IndexOf('\'', position);
                        if (closing < 0)
                        {
                            return Fail(out words);
                        }

                        word.Append(text, position, closing - position);
                        position = closing + 1;
                        break;

                    case '\\':
                        if (position >= text.Length)
                        {
                            return Fail(out words);
                        }

                        char escaped = text[position++];
                        if (escaped != '\n')
                        {
                            word.Append(escaped);
                        }

                        break;

                    default:
                        word.Append(character);
                        break;
                }
            }

            result.Add(word.ToString());
        }
    }

    private static bool ReadDoubleQuoted(string text, ref int position, StringBuilder word)
    {
        while (position < text.Length)
        {
            char character = text[position++];
            if (character == '"')
            {
                return true;
            }

            if (character != '\\')
            {
                word.Append(character);
                continue;
            }

            if (position >= text.Length)
            {
                return false;
            }

            char escaped = text[position++];
            switch (escaped)
            {
                case '$' or '`' or '"' or '\\':
                    word.Append(escaped);
                    break;
                case '\n':
                    break;
                default:
                    word.Append('\\').Append(escaped);
                    break;
            }
        }

        return false;
    }

    private static bool Fail(out IReadOnlyList<string> words)
    {
        words = Array.Empty<string>();
        return false;
    }
}
