using System.Collections.ObjectModel;

namespace Codex.VisualStudio.Extension;

internal enum SlashCommandId
{
    Compact,
    Feedback,
    Fork,
    Goal,
    Mcp,
    Skills,
    Review,
    Fast,
    Model,
    Personality,
    Plan,
    Reasoning,
    IdeContext,
    Init,
    Status,
    Permissions,
    Cloud,
    CloudEnvironment,
    Local,
    Memories,
    Project,
    Side,
}

internal enum SlashCommandArgumentKind
{
    None,
    OptionalText,
    RequiredText,
    Model,
    Personality,
    ReasoningEffort,
    ReviewTarget,
    GoalOperation,
    ApprovalMode,
}

internal sealed record SlashCommandDefinition(
    SlashCommandId Id,
    string Name,
    string Description,
    SlashCommandArgumentKind ArgumentKind,
    bool IsVisible,
    bool IsSetting,
    bool StartsTurn,
    string? UnsupportedReason = null,
    params string[] Aliases)
{
    public bool Matches(string name)
        => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)
            || Aliases.Contains(name, StringComparer.OrdinalIgnoreCase);
}

internal sealed class SlashCommandCatalog
{
    private static readonly IReadOnlyList<SlashCommandDefinition> Definitions =
    [
        new(SlashCommandId.Compact, "compact", "Compact the current thread context.", SlashCommandArgumentKind.None, true, false, true),
        new(SlashCommandId.Feedback, "feedback", "Send feedback to Codex.", SlashCommandArgumentKind.RequiredText, true, false, false),
        new(SlashCommandId.Fork, "fork", "Fork the current thread.", SlashCommandArgumentKind.None, true, false, false),
        new(SlashCommandId.Goal, "goal", "View or update the thread goal.", SlashCommandArgumentKind.GoalOperation, true, false, false),
        new(SlashCommandId.Mcp, "mcp", "Show MCP server status.", SlashCommandArgumentKind.None, true, false, false),
        new(SlashCommandId.Skills, "skills", "Show configured Codex skills. Use 'reload' to bypass the skills cache.", SlashCommandArgumentKind.OptionalText, true, false, false),
        new(SlashCommandId.Review, "review", "Start a code review.", SlashCommandArgumentKind.ReviewTarget, true, false, true),
        new(SlashCommandId.Fast, "fast", "Use the fast service tier for the next turn.", SlashCommandArgumentKind.None, true, true, false),
        new(SlashCommandId.Model, "model", "Select the model for the next turn.", SlashCommandArgumentKind.Model, true, true, false),
        new(SlashCommandId.Personality, "personality", "Select the personality for the next turn.", SlashCommandArgumentKind.Personality, true, true, false),
        new(SlashCommandId.Plan, "plan", "Use Plan mode for the next turn.", SlashCommandArgumentKind.OptionalText, true, true, false),
        new(SlashCommandId.Reasoning, "reasoning", "Select the reasoning effort for the next turn.", SlashCommandArgumentKind.ReasoningEffort, true, true, false),
        new(SlashCommandId.IdeContext, "ide-context", "Toggle IDE context for future turns.", SlashCommandArgumentKind.None, true, true, false),
        new(SlashCommandId.Init, "init", "Create an AGENTS.md file in the workspace root.", SlashCommandArgumentKind.None, true, false, false),
        new(SlashCommandId.Status, "status", "Show connection, model, permissions, and usage.", SlashCommandArgumentKind.None, true, false, false),
        new(SlashCommandId.Permissions, "permissions", "View or select the approval and sandbox mode.", SlashCommandArgumentKind.ApprovalMode, true, true, false, null, "approve"),
        new(SlashCommandId.Cloud, "cloud", "Cloud tasks are not available in this local-only extension.", SlashCommandArgumentKind.None, false, false, false, "Cloud tasks require a cloud-capable Codex surface."),
        new(SlashCommandId.CloudEnvironment, "cloud-environment", "Cloud environments are not available in this local-only extension.", SlashCommandArgumentKind.None, false, false, false, "Cloud environments require a cloud-capable Codex surface."),
        new(SlashCommandId.Local, "local", "The extension already uses the local app-server.", SlashCommandArgumentKind.None, false, false, false, "Changing execution surfaces is not supported by this extension."),
        new(SlashCommandId.Memories, "memories", "Memories are not exposed by the current app-server API.", SlashCommandArgumentKind.None, false, false, false, "The app-server does not expose a compatible memories API."),
        new(SlashCommandId.Project, "project", "Project switching is not supported by the current single-workspace session.", SlashCommandArgumentKind.None, false, false, false, "Project switching requires multi-workspace session support."),
        new(SlashCommandId.Side, "side", "Side threads are not supported by the current single-thread view.", SlashCommandArgumentKind.None, false, false, false, "Side threads require simultaneous multi-thread UI support."),
    ];

    // Suggestions further than this edit distance are noise rather than likely typos.
    public const int MaximumSuggestionDistance = 3;

    private readonly IReadOnlyList<SlashCommandDefinition> definitions;

    public SlashCommandCatalog()
        : this(Definitions)
    {
    }

    internal SlashCommandCatalog(IReadOnlyList<SlashCommandDefinition> definitions)
    {
        this.definitions = definitions;
    }

    public IReadOnlyList<SlashCommandDefinition> VisibleCommands
        => definitions.Where(static command => command.IsVisible).ToArray();

    public bool TryFind(string name, out SlashCommandDefinition? command)
    {
        command = definitions.FirstOrDefault(definition => definition.Matches(name));
        return command is not null;
    }

    public IReadOnlyList<SlashCommandDefinition> Filter(string filter, int maximum = 8)
    {
        string normalized = filter.TrimStart('/');
        return definitions
            .Where(static command => command.IsVisible)
            .Select(command => new
            {
                Command = command,
                Rank = GetMatchRank(command, normalized),
            })
            .Where(static candidate => candidate.Rank < int.MaxValue)
            .OrderBy(static candidate => candidate.Rank)
            .ThenBy(static candidate => candidate.Command.Name, StringComparer.Ordinal)
            .Take(maximum)
            .Select(static candidate => candidate.Command)
            .ToArray();
    }

    public IReadOnlyList<string> Suggest(string name, int maximum = 3)
        => definitions
            .Where(static command => command.IsVisible)
            .Select(command => new
            {
                command.Name,
                Distance = EditDistance(name, command.Name),
            })
            .Where(static candidate => candidate.Distance <= MaximumSuggestionDistance)
            .OrderBy(static candidate => candidate.Distance)
            .ThenBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .Take(maximum)
            .Select(static candidate => string.Concat("/", candidate.Name))
            .ToArray();

    private static int GetMatchRank(SlashCommandDefinition command, string filter)
    {
        if (filter.Length == 0)
        {
            return 0;
        }

        if (command.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (command.Aliases.Any(alias => alias.StartsWith(filter, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        if (command.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return int.MaxValue;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (int index = 0; index <= right.Length; index++)
        {
            previous[index] = index;
        }

        for (int leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                int substitution = char.ToUpperInvariant(left[leftIndex - 1]) == char.ToUpperInvariant(right[rightIndex - 1]) ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

internal enum SlashCommandParseKind
{
    NotCommand,
    EscapedPrompt,
    Command,
    Unsupported,
    Unknown,
    InputTooLong,
}

internal sealed record SlashCommandInvocation(
    SlashCommandDefinition Definition,
    string Arguments,
    string OriginalText)
{
    public bool IsImmediateWhileTurnActive
        => Definition.Id is SlashCommandId.Status or SlashCommandId.Mcp
            || (Definition.Id == SlashCommandId.Goal && string.IsNullOrWhiteSpace(Arguments));

    public bool IsCoalescibleSetting
        => Definition.IsSetting
            && !(Definition.Id == SlashCommandId.Plan && !string.IsNullOrWhiteSpace(Arguments));

    public bool StartsTurn
        => Definition.StartsTurn
            || (Definition.Id == SlashCommandId.Plan && !string.IsNullOrWhiteSpace(Arguments));
}

internal sealed record SlashCommandParseResult(
    SlashCommandParseKind Kind,
    SlashCommandInvocation? Invocation = null,
    string? PromptText = null,
    string? ErrorMessage = null,
    IReadOnlyList<string>? Suggestions = null);

internal enum GoalCommandOperation
{
    Get,
    Set,
    Edit,
    Pause,
    Resume,
    Clear,
}

internal sealed record GoalCommandArguments(
    GoalCommandOperation Operation,
    string? Objective = null);

internal enum ReviewCommandTargetKind
{
    UncommittedChanges,
    BaseBranch,
    Commit,
    Custom,
}

internal sealed record ReviewCommandArguments(
    ReviewCommandTargetKind Target,
    string? Value = null);

internal static class SlashCommandArgumentParser
{
    public const int MaximumGoalLength = 4000;

    public static bool TryParseGoal(
        string arguments,
        out GoalCommandArguments? result,
        out string? errorMessage)
    {
        string trimmed = arguments.Trim();
        if (trimmed.Length == 0)
        {
            result = new GoalCommandArguments(GoalCommandOperation.Get);
            errorMessage = null;
            return true;
        }

        SplitOperation(trimmed, out string operation, out string value);
        switch (operation.ToUpperInvariant())
        {
            case "GET" when value.Length == 0:
            case "SHOW" when value.Length == 0:
                result = new GoalCommandArguments(GoalCommandOperation.Get);
                errorMessage = null;
                return true;

            case "SET":
            case "EDIT":
                if (value.Length is < 1 or > MaximumGoalLength)
                {
                    result = null;
                    errorMessage = $"Goal text must be between 1 and {MaximumGoalLength} characters.";
                    return false;
                }

                result = new GoalCommandArguments(
                    string.Equals(operation, "set", StringComparison.OrdinalIgnoreCase)
                        ? GoalCommandOperation.Set
                        : GoalCommandOperation.Edit,
                    value);
                errorMessage = null;
                return true;

            case "PAUSE" when value.Length == 0:
                result = new GoalCommandArguments(GoalCommandOperation.Pause);
                errorMessage = null;
                return true;

            case "RESUME" when value.Length == 0:
                result = new GoalCommandArguments(GoalCommandOperation.Resume);
                errorMessage = null;
                return true;

            case "CLEAR" when value.Length == 0:
                result = new GoalCommandArguments(GoalCommandOperation.Clear);
                errorMessage = null;
                return true;

            default:
                result = null;
                errorMessage = "Use /goal, /goal set <objective>, /goal edit <objective>, /goal pause, /goal resume, or /goal clear.";
                return false;
        }
    }

    public static bool TryParseReview(
        string arguments,
        out ReviewCommandArguments? result,
        out string? errorMessage)
    {
        string trimmed = arguments.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, "uncommitted", StringComparison.OrdinalIgnoreCase))
        {
            result = new ReviewCommandArguments(ReviewCommandTargetKind.UncommittedChanges);
            errorMessage = null;
            return true;
        }

        SplitOperation(trimmed, out string target, out string value);
        if (value.Length == 0)
        {
            result = null;
            errorMessage = "Review targets require a value: base <branch>, commit <sha>, or custom <instructions>.";
            return false;
        }

        result = target.ToUpperInvariant() switch
        {
            "BASE" => new ReviewCommandArguments(ReviewCommandTargetKind.BaseBranch, value),
            "COMMIT" => new ReviewCommandArguments(ReviewCommandTargetKind.Commit, value),
            "CUSTOM" => new ReviewCommandArguments(ReviewCommandTargetKind.Custom, value),
            _ => null,
        };
        errorMessage = result is null
            ? "Use /review uncommitted, /review base <branch>, /review commit <sha>, or /review custom <instructions>."
            : null;
        return result is not null;
    }

    private static void SplitOperation(string value, out string operation, out string remainder)
    {
        int separator = value.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator < 0)
        {
            operation = value;
            remainder = string.Empty;
            return;
        }

        operation = value[..separator];
        remainder = value[(separator + 1)..].Trim();
    }
}

internal sealed class SlashCommandParser
{
    public const int MaximumInputLength = 64 * 1024;

    private readonly SlashCommandCatalog catalog;

    public SlashCommandParser(SlashCommandCatalog catalog)
    {
        this.catalog = catalog;
    }

    public SlashCommandParseResult Parse(string? input)
    {
        if (string.IsNullOrEmpty(input) || input[0] != '/')
        {
            return new SlashCommandParseResult(SlashCommandParseKind.NotCommand);
        }

        if (input.Length > MaximumInputLength)
        {
            return new SlashCommandParseResult(
                SlashCommandParseKind.InputTooLong,
                ErrorMessage: $"Slash command input must not exceed {MaximumInputLength} characters.");
        }

        if (input.StartsWith("//", StringComparison.Ordinal))
        {
            return new SlashCommandParseResult(
                SlashCommandParseKind.EscapedPrompt,
                PromptText: input[1..]);
        }

        int separatorIndex = FindSeparator(input);
        string commandName = input[1..separatorIndex];
        if (commandName.Length == 0)
        {
            return new SlashCommandParseResult(
                SlashCommandParseKind.Unknown,
                ErrorMessage: "Enter a command name after '/'.",
                Suggestions: catalog.VisibleCommands.Take(3).Select(static command => string.Concat("/", command.Name)).ToArray());
        }

        string arguments = separatorIndex == input.Length
            ? string.Empty
            : input[(separatorIndex + 1)..];

        if (!catalog.TryFind(commandName, out SlashCommandDefinition? definition) || definition is null)
        {
            IReadOnlyList<string> suggestions = catalog.Suggest(commandName);
            string suffix = suggestions.Count == 0
                ? string.Empty
                : $" Did you mean {string.Join(", ", suggestions)}?";
            return new SlashCommandParseResult(
                SlashCommandParseKind.Unknown,
                ErrorMessage: $"Unknown slash command '/{commandName}'.{suffix}",
                Suggestions: suggestions);
        }

        var invocation = new SlashCommandInvocation(definition, arguments, input);
        if (!definition.IsVisible)
        {
            return new SlashCommandParseResult(
                SlashCommandParseKind.Unsupported,
                invocation,
                ErrorMessage: definition.UnsupportedReason ?? $"The /{definition.Name} command is not supported.");
        }

        return new SlashCommandParseResult(SlashCommandParseKind.Command, invocation);
    }

    private static int FindSeparator(string input)
    {
        for (int index = 1; index < input.Length; index++)
        {
            if (char.IsWhiteSpace(input[index]))
            {
                return index;
            }
        }

        return input.Length;
    }
}

internal enum SlashCommandQueueDecisionKind
{
    ExecuteNow,
    Queued,
    Replaced,
    QueueFull,
}

internal sealed record SlashCommandQueueDecision(
    SlashCommandQueueDecisionKind Kind,
    int QueueCount,
    string Message);

internal sealed class SlashCommandCoordinator
{
    public const int MaximumQueuedCommands = 10;

    private const string SessionQueueKey = "<session>";
    private readonly object gate = new();
    private readonly Dictionary<string, List<SlashCommandInvocation>> queues = new(StringComparer.Ordinal);

    public SlashCommandQueueDecision Schedule(
        SlashCommandInvocation invocation,
        string? threadId,
        bool turnActive)
    {
        if (!turnActive || invocation.IsImmediateWhileTurnActive)
        {
            return new SlashCommandQueueDecision(
                SlashCommandQueueDecisionKind.ExecuteNow,
                GetQueueCount(threadId),
                $"Executing /{invocation.Definition.Name}.");
        }

        string key = GetQueueKey(threadId);
        lock (gate)
        {
            if (!queues.TryGetValue(key, out List<SlashCommandInvocation>? queue))
            {
                queue = [];
                queues[key] = queue;
            }

            if (invocation.IsCoalescibleSetting)
            {
                int existingIndex = queue.FindIndex(item => item.Definition.Id == invocation.Definition.Id);
                if (existingIndex >= 0)
                {
                    queue[existingIndex] = invocation;
                    return new SlashCommandQueueDecision(
                        SlashCommandQueueDecisionKind.Replaced,
                        queue.Count,
                        $"Updated the queued /{invocation.Definition.Name} command.");
                }
            }

            if (queue.Count >= MaximumQueuedCommands)
            {
                return new SlashCommandQueueDecision(
                    SlashCommandQueueDecisionKind.QueueFull,
                    queue.Count,
                    $"The slash command queue is full ({MaximumQueuedCommands} commands).");
            }

            queue.Add(invocation);
            return new SlashCommandQueueDecision(
                SlashCommandQueueDecisionKind.Queued,
                queue.Count,
                $"Queued /{invocation.Definition.Name} until the active turn completes.");
        }
    }

    public bool TryDequeue(string? threadId, out SlashCommandInvocation? invocation)
    {
        string key = GetQueueKey(threadId);
        lock (gate)
        {
            if (!queues.TryGetValue(key, out List<SlashCommandInvocation>? queue) || queue.Count == 0)
            {
                invocation = null;
                return false;
            }

            invocation = queue[0];
            queue.RemoveAt(0);
            if (queue.Count == 0)
            {
                queues.Remove(key);
            }

            return true;
        }
    }

    public int GetQueueCount(string? threadId)
    {
        string key = GetQueueKey(threadId);
        lock (gate)
        {
            return queues.TryGetValue(key, out List<SlashCommandInvocation>? queue) ? queue.Count : 0;
        }
    }

    public IReadOnlyList<SlashCommandInvocation> CancelThread(string? threadId)
    {
        string key = GetQueueKey(threadId);
        lock (gate)
        {
            if (!queues.Remove(key, out List<SlashCommandInvocation>? queue))
            {
                return [];
            }

            return new ReadOnlyCollection<SlashCommandInvocation>(queue);
        }
    }

    public IReadOnlyList<SlashCommandInvocation> CancelAll()
    {
        lock (gate)
        {
            SlashCommandInvocation[] canceled = queues.Values.SelectMany(static queue => queue).ToArray();
            queues.Clear();
            return canceled;
        }
    }

    public IReadOnlyList<SlashCommandInvocation> CancelMissingThreads(IReadOnlySet<string> existingThreadIds)
    {
        lock (gate)
        {
            string[] missingKeys = queues.Keys
                .Where(key => !string.Equals(key, SessionQueueKey, StringComparison.Ordinal)
                    && !existingThreadIds.Contains(key))
                .ToArray();
            var canceled = new List<SlashCommandInvocation>();
            foreach (string key in missingKeys)
            {
                canceled.AddRange(queues[key]);
                queues.Remove(key);
            }

            return canceled;
        }
    }

    private static string GetQueueKey(string? threadId)
        => string.IsNullOrWhiteSpace(threadId) ? SessionQueueKey : threadId;
}
