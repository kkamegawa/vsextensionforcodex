using System.Globalization;

namespace Codex.VisualStudio.Extension;

internal static class ShellCommandParser
{
    public static bool TryParseArguments(
        string? arguments,
        out ShellCommandRequest? request,
        out string? errorMessage)
    {
        request = null;
        errorMessage = null;

        if (arguments is null)
        {
            errorMessage = "A shell command is required after '--'.";
            return false;
        }

        int cursor = 0;
        SkipWhitespace(arguments, ref cursor);

        long? timeoutMs = null;
        bool timeoutWasProvided = false;

        while (cursor < arguments.Length)
        {
            int tokenStart = cursor;
            while (cursor < arguments.Length && !char.IsWhiteSpace(arguments[cursor]))
            {
                cursor++;
            }

            string token = arguments[tokenStart..cursor];
            if (token == "--")
            {
                if (cursor >= arguments.Length || !char.IsWhiteSpace(arguments[cursor]))
                {
                    errorMessage = "A shell command must follow '--'.";
                    return false;
                }

                int commandStart = cursor + 1;
                string command = arguments[commandStart..];
                if (string.IsNullOrWhiteSpace(command))
                {
                    errorMessage = "A shell command must follow '--'.";
                    return false;
                }

                request = new ShellCommandRequest(command, timeoutMs);
                return true;
            }

            if (token != "--timeout-ms")
            {
                errorMessage = $"Unknown /shell option '{token}'.";
                return false;
            }

            if (timeoutWasProvided)
            {
                errorMessage = "The '--timeout-ms' option can only be specified once.";
                return false;
            }

            timeoutWasProvided = true;
            SkipWhitespace(arguments, ref cursor);
            int valueStart = cursor;
            while (cursor < arguments.Length && !char.IsWhiteSpace(arguments[cursor]))
            {
                cursor++;
            }

            if (valueStart == cursor)
            {
                errorMessage = "The '--timeout-ms' option requires a non-negative integer value.";
                return false;
            }

            ReadOnlySpan<char> value = arguments.AsSpan(valueStart, cursor - valueStart);
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedTimeout))
            {
                errorMessage = "The '--timeout-ms' value must be a non-negative Int64 integer.";
                return false;
            }

            timeoutMs = parsedTimeout;
            if (cursor >= arguments.Length)
            {
                errorMessage = "A shell command must follow '--'.";
                return false;
            }

            SkipWhitespace(arguments, ref cursor);
        }

        errorMessage = "A shell command must follow '--'.";
        return false;
    }

    private static void SkipWhitespace(string value, ref int cursor)
    {
        while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
        {
            cursor++;
        }
    }
}

internal sealed record ShellCommandRequest(string Command, long? TimeoutMs);
