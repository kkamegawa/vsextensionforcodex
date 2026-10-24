using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

// Parity cases for the Codex CLI 0.164.0 /model argument parser and its shlex 1.3.0 tokenizer.
[TestClass]
public sealed class ModelCommandArgumentsTests
{
    [TestMethod]
    [DataRow("", null, null, null)]
    [DataRow("gpt-5", "gpt-5", null, null)]
    [DataRow("gpt-5 --reasoning high --speed fast", "gpt-5", "high", "fast")]
    [DataRow("--reasoning=high --speed=fast", null, "high", "fast")]
    [DataRow("--speed= fast", null, null, "fast")]
    [DataRow("--reasoning high gpt-5", "gpt-5", "high", null)]
    [DataRow("'gpt 5' --reasoning \"x high\"", "gpt 5", "x high", null)]
    [DataRow("--reasoning -x", null, "-x", null)]
    [DataRow("-- --literal", "--literal", null, null)]
    [DataRow("--reasoning=a=b", null, "a=b", null)]
    [DataRow("gpt-5 # comment --unknown", "gpt-5", null, null)]
    [DataRow("gpt\\-5", "gpt-5", null, null)]
    [DataRow("\"a\\\"b\\x\"", "a\"b\\x", null, null)]
    [DataRow("gpt-5\r\n--speed fast", "gpt-5", null, "fast")]
    public void TryParse_AcceptsCliSyntax(string arguments, string? model, string? reasoning, string? speed)
    {
        Assert.IsTrue(ModelCommandArgumentParser.TryParse(arguments, out ModelCommandArguments? result, out string? error), error);

        Assert.AreEqual(model, result!.Model);
        Assert.AreEqual(reasoning, result.Reasoning);
        Assert.AreEqual(speed, result.Speed);
    }

    [TestMethod]
    [DataRow("--reasoning", "Command setting `--reasoning` requires a value.")]
    [DataRow("--reasoning --speed fast", "Command setting `--reasoning` requires a value.")]
    [DataRow("--speed=", "Command setting `--speed` requires a value.")]
    [DataRow("--speed fast --speed fast", "Command setting `--speed` was provided more than once.")]
    [DataRow("--Speed fast", "Unknown command setting `--Speed`.")]
    [DataRow("--fast", "Unknown command setting `--fast`.")]
    [DataRow("a b", "Unexpected argument 'b'. Use '--reasoning=<level>' or '--speed=<tier>'.")]
    [DataRow("-- a --reasoning", "Unexpected argument '--reasoning'. Use '--reasoning=<level>' or '--speed=<tier>'.")]
    [DataRow("'unfinished", "Invalid command arguments. Check the quoting.")]
    [DataRow("\"unfinished", "Invalid command arguments. Check the quoting.")]
    [DataRow("trailing\\", "Invalid command arguments. Check the quoting.")]
    public void TryParse_RejectsInvalidArguments(string arguments, string expected)
    {
        Assert.IsFalse(ModelCommandArgumentParser.TryParse(arguments, out ModelCommandArguments? result, out string? error));

        Assert.IsNull(result);
        Assert.AreEqual(expected, error);
    }

    [TestMethod]
    public void QueuedModelCommandWithSettingsIsNotCoalesced()
    {
        var parser = new SlashCommandParser(new SlashCommandCatalog());
        var coordinator = new SlashCommandCoordinator();

        coordinator.Schedule(parser.Parse("/model --reasoning high").Invocation!, "thread-1", turnActive: true);
        SlashCommandQueueDecision second = coordinator.Schedule(parser.Parse("/model --speed fast").Invocation!, "thread-1", turnActive: true);

        Assert.AreEqual(SlashCommandQueueDecisionKind.Queued, second.Kind);
        Assert.AreEqual(2, coordinator.GetQueueCount("thread-1"));
    }

    [TestMethod]
    public void PresetsCommandRunsImmediatelyDuringTurn()
    {
        var parser = new SlashCommandParser(new SlashCommandCatalog());

        SlashCommandParseResult result = parser.Parse("/presets");

        Assert.AreEqual(SlashCommandParseKind.Command, result.Kind);
        Assert.IsTrue(result.Invocation!.IsImmediateWhileTurnActive);
    }
}
