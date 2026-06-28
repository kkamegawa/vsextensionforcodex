using System.Text.RegularExpressions;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

// Heuristic detector for choice-style prompts that codex writes as prose. In Agent/Default mode the
// structured request_user_input tool is unavailable (codex only offers it in Plan mode), so the model
// asks "which option?" as ordinary assistant text. This recognizes a question followed by a numbered
// option list and synthesizes a single-question UserInputRequest, letting the same selection card be
// reused. It is deliberately conservative — a question mark plus at least two numbered items — because
// a false positive only adds a dismissible card.
public static partial class ChoicePromptParser
{
    private const int MaxOptions = 12;
    private const int MaxLabelLength = 400;

    public static bool TryParse(string? rawText, out UserInputRequest request)
    {
        request = new UserInputRequest();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return false;
        }

        // Must read like a question addressed to the user.
        if (!rawText.Contains('?') && !rawText.Contains('？'))
        {
            return false;
        }

        string[] lines = rawText.Replace("\r\n", "\n").Split('\n');
        var options = new List<UserInputOption>();
        foreach (string line in lines)
        {
            Match match = NumberedItem().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string label = CleanInline(match.Groups[1].Value);
            if (label.Length is > 0 and <= MaxLabelLength)
            {
                options.Add(new UserInputOption { Label = label, Description = string.Empty });
            }
        }

        if (options.Count is < 2 or > MaxOptions)
        {
            return false;
        }

        request = new UserInputRequest
        {
            RequestId = "choice-" + Guid.NewGuid().ToString("N"),
            Questions =
            [
                new UserInputQuestion
                {
                    Id = "choice",
                    Header = string.Empty,
                    Question = FindQuestionLine(lines) ?? "Select an option",
                    Options = options,
                },
            ],
        };
        return true;
    }

    // The last non-list line that ends with a question mark reads as the prompt.
    private static string? FindQuestionLine(string[] lines)
    {
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (NumberedItem().IsMatch(lines[i]))
            {
                continue;
            }

            string trimmed = lines[i].Trim();
            if (trimmed.EndsWith('?') || trimmed.EndsWith('？'))
            {
                return CleanInline(trimmed);
            }
        }

        return null;
    }

    // Strips common inline markdown (bold/italic/code emphasis) so the label is clean both for display
    // and for echoing the choice back to codex.
    private static string CleanInline(string value)
        => Emphasis().Replace(value.Trim(), "$1").Trim();

    [GeneratedRegex(@"^\s*\d{1,2}[.)）、]\s+(.+?)\s*$")]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"[*_`]{1,2}([^*_`]+)[*_`]{1,2}")]
    private static partial Regex Emphasis();
}
