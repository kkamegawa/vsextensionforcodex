using System.Text.RegularExpressions;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

// Heuristic detector for choice-style prompts that codex writes as prose. In Agent/Default mode the
// structured request_user_input tool is unavailable (codex only offers it in Plan mode), and a Plan-mode
// tool call can still be rejected, so the model asks "which option?" as ordinary assistant text. This
// recognizes one or more questions each followed by a numbered or bulleted option list, or a single
// confirmation question, and synthesizes a UserInputRequest so the same selection card is reused. It is
// deliberately conservative because a false positive only adds a dismissible card.
public static partial class ChoicePromptParser
{
    private const int MaxOptions = 12;
    private const int MaxQuestions = 8;
    private const int MaxLabelLength = 400;

    public static bool TryParse(string? rawText, out UserInputRequest request)
        => TryParse(rawText, optionListsOnly: false, out request);

    // Recognizes only questions that come with their own option lists. Earlier messages of a turn are
    // scanned this way: a bare confirmation question there has usually been settled by the time the
    // turn ends, while explicit options still describe the choice codex is waiting on.
    public static bool TryParseOptionQuestions(string? rawText, out UserInputRequest request)
        => TryParse(rawText, optionListsOnly: true, out request);

    private static bool TryParse(string? rawText, bool optionListsOnly, out UserInputRequest request)
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
        List<UserInputQuestion> groups = FindQuestionGroups(lines);
        if (groups.Count > MaxQuestions)
        {
            return false;
        }

        if (groups.Count > 0)
        {
            request = CreateRequest(groups);
            return true;
        }

        string? question = FindQuestionLine(lines);
        if (question is null)
        {
            return false;
        }

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

        if (options.Count is >= 2 and <= MaxOptions)
        {
            request = CreateRequest(question, options);
            return true;
        }

        if (!optionListsOnly && options.Count == 0 && ConfirmationQuestion().IsMatch(question))
        {
            request = CreateRequest(question,
            [
                new UserInputOption { Label = "Yes", Description = string.Empty },
                new UserInputOption { Label = "No", Description = string.Empty },
            ]);
            return true;
        }

        return false;
    }

    private static UserInputRequest CreateRequest(string question, IReadOnlyList<UserInputOption> options)
        => CreateRequest(
        [
            new UserInputQuestion
            {
                Header = string.Empty,
                Question = question,
                Options = options,
            },
        ]);

    private static UserInputRequest CreateRequest(List<UserInputQuestion> questions)
    {
        for (int i = 0; i < questions.Count; i++)
        {
            questions[i].Id = questions.Count == 1 ? "choice" : $"choice-{i + 1}";
        }

        return new UserInputRequest
        {
            RequestId = "choice-" + Guid.NewGuid().ToString("N"),
            Questions = questions,
        };
    }

    // Pairs each question line with the numbered or bulleted options listed directly under it. Blank
    // lines may separate them; any other prose line ends the list. Questions without two to MaxOptions
    // options are ignored, so ordinary bullet lists and open-ended questions never form a group.
    private static List<UserInputQuestion> FindQuestionGroups(string[] lines)
    {
        var questions = new List<UserInputQuestion>();
        string? question = null;
        List<UserInputOption>? options = null;

        void Flush()
        {
            if (question is not null && options is { Count: >= 2 and <= MaxOptions })
            {
                questions.Add(new UserInputQuestion { Header = string.Empty, Question = question, Options = options });
            }

            question = null;
            options = null;
        }

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? item = MatchListItem(line);
            string text = CleanInline(item ?? line);
            if (IsQuestion(text))
            {
                Flush();
                question = text;
                options = [];
            }
            else if (item is not null && options is not null)
            {
                if (text.Length is > 0 and <= MaxLabelLength)
                {
                    options.Add(new UserInputOption { Label = text, Description = string.Empty });
                }
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return questions;
    }

    private static string? MatchListItem(string line)
    {
        Match match = NumberedItem().Match(line);
        if (!match.Success)
        {
            match = BulletItem().Match(line);
        }

        return match.Success ? match.Groups[1].Value : null;
    }

    // A question ends with a question mark, optionally followed by a parenthetical aside.
    private static bool IsQuestion(string text) => QuestionEnding().IsMatch(text);

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
            if (IsQuestion(trimmed))
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

    [GeneratedRegex(@"^\s*(?:[-*+]\s+|[・•]\s*)(.+?)\s*$")]
    private static partial Regex BulletItem();

    [GeneratedRegex(@"[?？]\s*(?:[(（][^()（）]*[)）])?$")]
    private static partial Regex QuestionEnding();

    [GeneratedRegex(@"\Ado\s+you\s+want\s+(?:me\s+)?to\b|\b(?:proceed|continue|ok(?:ay)?\s+to|should\s+i|shall\s+i)\b|よいですか|いいですか|よろしいですか|進めて(?:よい|いい|もよい|もいい)|進めますか|続けますか", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConfirmationQuestion();

    [GeneratedRegex(@"[*_`]{1,2}([^*_`]+)[*_`]{1,2}")]
    private static partial Regex Emphasis();
}
