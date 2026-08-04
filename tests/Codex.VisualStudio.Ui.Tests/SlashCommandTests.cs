using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class SlashCommandTests
{
    private static readonly string[] ReasoningAndReviewCommands = ["reasoning", "review"];

    private readonly SlashCommandCatalog catalog = new();

    [TestMethod]
    public void Parser_OnlyTreatsLeadingSlashAsCommand()
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse(" /status");

        Assert.AreEqual(SlashCommandParseKind.NotCommand, result.Kind);
    }

    [TestMethod]
    [DataRow("/permissions", "")]
    [DataRow("/permissions ask", "ask")]
    [DataRow("/approve full", "full")]
    public void Parser_RecognizesPermissionsAndApproveAlias(string input, string expectedArguments)
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse(input);

        Assert.AreEqual(SlashCommandParseKind.Command, result.Kind);
        Assert.AreEqual(SlashCommandId.Permissions, result.Invocation!.Definition.Id);
        Assert.AreEqual(expectedArguments, result.Invocation.Arguments);
        Assert.AreEqual("permissions", result.Invocation.Definition.Name);
    }

    [TestMethod]
    public void SlashCommandParser_ParsesSkillsCommand()
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse("/skills");

        Assert.AreEqual(SlashCommandParseKind.Command, result.Kind);
        Assert.AreEqual(SlashCommandId.Skills, result.Invocation!.Definition.Id);
        Assert.AreEqual(string.Empty, result.Invocation.Arguments);
        Assert.IsFalse(result.Invocation.StartsTurn);
    }

    [TestMethod]
    public void SlashCommandParser_ParsesSkillsReloadArgument()
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse("/skills reload");

        Assert.AreEqual(SlashCommandParseKind.Command, result.Kind);
        Assert.AreEqual(SlashCommandId.Skills, result.Invocation!.Definition.Id);
        Assert.AreEqual("reload", result.Invocation.Arguments);
    }

    [TestMethod]
    public void Parser_DoubleSlashProducesLiteralPrompt()
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse("//review this");

        Assert.AreEqual(SlashCommandParseKind.EscapedPrompt, result.Kind);
        Assert.AreEqual("/review this", result.PromptText);
    }

    [TestMethod]
    public void Parser_PreservesMultilineArguments()
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse("/plan first line\nsecond line");

        Assert.AreEqual(SlashCommandParseKind.Command, result.Kind);
        Assert.IsNotNull(result.Invocation);
        Assert.AreEqual("first line\nsecond line", result.Invocation!.Arguments);
        Assert.IsTrue(result.Invocation.StartsTurn);
    }

    [TestMethod]
    public void Parser_ResolvesDefinitionAlias()
    {
        var definition = new SlashCommandDefinition(
            SlashCommandId.Model,
            "model",
            "Select a model.",
            SlashCommandArgumentKind.Model,
            true,
            true,
            false,
            null,
            "models");
        var parser = new SlashCommandParser(new SlashCommandCatalog([definition]));

        SlashCommandParseResult result = parser.Parse("/models gpt-5");

        Assert.AreEqual(SlashCommandParseKind.Command, result.Kind);
        Assert.AreEqual(SlashCommandId.Model, result.Invocation!.Definition.Id);
        Assert.AreEqual("gpt-5", result.Invocation.Arguments);
    }

    [TestMethod]
    public void Parser_UnknownCommandReturnsCandidates()
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse("/stats");

        Assert.AreEqual(SlashCommandParseKind.Unknown, result.Kind);
        Assert.IsTrue(result.Suggestions!.Contains("/status"));
        StringAssert.Contains(result.ErrorMessage, "/status");
    }

    [TestMethod]
    public void Parser_UnknownCommandWithoutNearMatchOmitsCandidates()
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse("/xyzzyqq");

        Assert.AreEqual(SlashCommandParseKind.Unknown, result.Kind);
        Assert.AreEqual(0, result.Suggestions!.Count);
        Assert.IsFalse(result.ErrorMessage!.Contains("Did you mean", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Catalog_SuggestOmitsCandidatesBeyondEditDistanceThreshold()
    {
        Assert.AreEqual(0, catalog.Suggest("xyzzyqq").Count);
        Assert.IsTrue(catalog.Suggest("stauts").Contains("/status"));
    }

    [TestMethod]
    public void Parser_RecognizesHiddenCommandAsUnsupported()
    {
        var parser = new SlashCommandParser(catalog);

        SlashCommandParseResult result = parser.Parse("/cloud");

        Assert.AreEqual(SlashCommandParseKind.Unsupported, result.Kind);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public void Parser_RejectsInputAboveLimit()
    {
        var parser = new SlashCommandParser(catalog);
        string input = "/" + new string('a', SlashCommandParser.MaximumInputLength);

        SlashCommandParseResult result = parser.Parse(input);

        Assert.AreEqual(SlashCommandParseKind.InputTooLong, result.Kind);
    }

    [TestMethod]
    public void Catalog_FilterUsesVisiblePrefixMatches()
    {
        IReadOnlyList<SlashCommandDefinition> results = catalog.Filter("/re");

        CollectionAssert.AreEqual(
            ReasoningAndReviewCommands,
            results.Select(static command => command.Name).ToArray());
        Assert.IsFalse(results.Any(static command => command.Name == "project"));
    }

    [TestMethod]
    public void ArgumentParser_ParsesGoalOperationsAndEnforcesLimit()
    {
        Assert.IsTrue(SlashCommandArgumentParser.TryParseGoal(string.Empty, out GoalCommandArguments? get, out _));
        Assert.AreEqual(GoalCommandOperation.Get, get!.Operation);

        Assert.IsTrue(SlashCommandArgumentParser.TryParseGoal("set Fix the failing build", out GoalCommandArguments? set, out _));
        Assert.AreEqual(GoalCommandOperation.Set, set!.Operation);
        Assert.AreEqual("Fix the failing build", set.Objective);

        string tooLong = "set " + new string('x', SlashCommandArgumentParser.MaximumGoalLength + 1);
        Assert.IsFalse(SlashCommandArgumentParser.TryParseGoal(tooLong, out _, out string? error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void ArgumentParser_AcceptsGoalShowAsAliasOfGet()
    {
        Assert.IsTrue(SlashCommandArgumentParser.TryParseGoal("show", out GoalCommandArguments? show, out _));
        Assert.AreEqual(GoalCommandOperation.Get, show!.Operation);

        Assert.IsFalse(SlashCommandArgumentParser.TryParseGoal("show something", out _, out string? error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void ArgumentParser_ParsesReviewTargets()
    {
        Assert.IsTrue(SlashCommandArgumentParser.TryParseReview("uncommitted", out ReviewCommandArguments? uncommitted, out _));
        Assert.AreEqual(ReviewCommandTargetKind.UncommittedChanges, uncommitted!.Target);

        Assert.IsTrue(SlashCommandArgumentParser.TryParseReview("base main", out ReviewCommandArguments? branch, out _));
        Assert.AreEqual(ReviewCommandTargetKind.BaseBranch, branch!.Target);
        Assert.AreEqual("main", branch.Value);

        Assert.IsTrue(SlashCommandArgumentParser.TryParseReview("custom focus on concurrency\nand cancellation", out ReviewCommandArguments? custom, out _));
        Assert.AreEqual(ReviewCommandTargetKind.Custom, custom!.Target);
        Assert.AreEqual("focus on concurrency\nand cancellation", custom.Value);
    }

    [TestMethod]
    public void Coordinator_AllowsOnlyReadCommandsDuringActiveTurn()
    {
        var parser = new SlashCommandParser(catalog);
        var coordinator = new SlashCommandCoordinator();
        SlashCommandInvocation status = ParseCommand(parser, "/status");
        SlashCommandInvocation goalGet = ParseCommand(parser, "/goal");
        SlashCommandInvocation goalSet = ParseCommand(parser, "/goal set Fix the failing build");

        Assert.AreEqual(
            SlashCommandQueueDecisionKind.ExecuteNow,
            coordinator.Schedule(status, "thread-1", turnActive: true).Kind);
        Assert.AreEqual(
            SlashCommandQueueDecisionKind.ExecuteNow,
            coordinator.Schedule(goalGet, "thread-1", turnActive: true).Kind);
        Assert.AreEqual(
            SlashCommandQueueDecisionKind.Queued,
            coordinator.Schedule(goalSet, "thread-1", turnActive: true).Kind);
    }

    [TestMethod]
    public void Coordinator_ReplacesOlderSettingWithoutChangingQueuePosition()
    {
        var parser = new SlashCommandParser(catalog);
        var coordinator = new SlashCommandCoordinator();
        SlashCommandInvocation firstModel = ParseCommand(parser, "/model gpt-5");
        SlashCommandInvocation review = ParseCommand(parser, "/review uncommitted");
        SlashCommandInvocation latestModel = ParseCommand(parser, "/model gpt-5-codex");

        coordinator.Schedule(firstModel, "thread-1", turnActive: true);
        coordinator.Schedule(review, "thread-1", turnActive: true);
        SlashCommandQueueDecision decision = coordinator.Schedule(latestModel, "thread-1", turnActive: true);

        Assert.AreEqual(SlashCommandQueueDecisionKind.Replaced, decision.Kind);
        Assert.AreEqual(2, decision.QueueCount);
        Assert.IsTrue(coordinator.TryDequeue("thread-1", out SlashCommandInvocation? dequeuedModel));
        Assert.AreEqual("gpt-5-codex", dequeuedModel!.Arguments);
        Assert.IsTrue(coordinator.TryDequeue("thread-1", out SlashCommandInvocation? dequeuedReview));
        Assert.AreEqual(SlashCommandId.Review, dequeuedReview!.Definition.Id);
    }

    [TestMethod]
    public void Coordinator_KeepsQueuesSeparateByThread()
    {
        var parser = new SlashCommandParser(catalog);
        var coordinator = new SlashCommandCoordinator();
        SlashCommandInvocation model = ParseCommand(parser, "/model gpt-5");
        SlashCommandInvocation review = ParseCommand(parser, "/review uncommitted");

        coordinator.Schedule(model, "thread-1", turnActive: true);
        coordinator.Schedule(review, "thread-2", turnActive: true);

        Assert.IsTrue(coordinator.TryDequeue("thread-2", out SlashCommandInvocation? threadTwo));
        Assert.AreEqual(SlashCommandId.Review, threadTwo!.Definition.Id);
        Assert.IsTrue(coordinator.TryDequeue("thread-1", out SlashCommandInvocation? threadOne));
        Assert.AreEqual(SlashCommandId.Model, threadOne!.Definition.Id);
    }

    [TestMethod]
    public void Coordinator_RejectsEleventhQueuedCommand()
    {
        var parser = new SlashCommandParser(catalog);
        var coordinator = new SlashCommandCoordinator();
        SlashCommandInvocation review = ParseCommand(parser, "/review uncommitted");

        for (int index = 0; index < SlashCommandCoordinator.MaximumQueuedCommands; index++)
        {
            Assert.AreEqual(
                SlashCommandQueueDecisionKind.Queued,
                coordinator.Schedule(review, "thread-1", turnActive: true).Kind);
        }

        Assert.AreEqual(
            SlashCommandQueueDecisionKind.QueueFull,
            coordinator.Schedule(review, "thread-1", turnActive: true).Kind);
    }

    [TestMethod]
    public void Coordinator_CancelAllReturnsQueuedCommands()
    {
        var parser = new SlashCommandParser(catalog);
        var coordinator = new SlashCommandCoordinator();
        coordinator.Schedule(ParseCommand(parser, "/review uncommitted"), "thread-1", turnActive: true);
        coordinator.Schedule(ParseCommand(parser, "/compact"), "thread-2", turnActive: true);

        IReadOnlyList<SlashCommandInvocation> canceled = coordinator.CancelAll();

        Assert.AreEqual(2, canceled.Count);
        Assert.AreEqual(0, coordinator.GetQueueCount("thread-1"));
        Assert.AreEqual(0, coordinator.GetQueueCount("thread-2"));
    }

    [TestMethod]
    public void Coordinator_CancelMissingThreadsKeepsExistingThreadQueue()
    {
        var parser = new SlashCommandParser(catalog);
        var coordinator = new SlashCommandCoordinator();
        coordinator.Schedule(ParseCommand(parser, "/review uncommitted"), "thread-1", turnActive: true);
        coordinator.Schedule(ParseCommand(parser, "/compact"), "thread-2", turnActive: true);

        IReadOnlyList<SlashCommandInvocation> canceled = coordinator.CancelMissingThreads(
            new HashSet<string>(["thread-2"], StringComparer.Ordinal));

        Assert.AreEqual(1, canceled.Count);
        Assert.AreEqual(SlashCommandId.Review, canceled[0].Definition.Id);
        Assert.AreEqual(0, coordinator.GetQueueCount("thread-1"));
        Assert.AreEqual(1, coordinator.GetQueueCount("thread-2"));
    }

    private static SlashCommandInvocation ParseCommand(SlashCommandParser parser, string text)
    {
        SlashCommandParseResult result = parser.Parse(text);
        Assert.AreEqual(SlashCommandParseKind.Command, result.Kind);
        return result.Invocation!;
    }
}
