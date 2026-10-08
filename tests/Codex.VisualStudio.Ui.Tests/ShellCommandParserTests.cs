using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ShellCommandParserTests
{
    [TestMethod]
    public void Parser_PreservesCommandTextAfterDelimiter()
    {
        const string command = "  echo 'hello world'  \nnext line  ";

        bool success = ShellCommandParser.TryParseArguments($"--timeout-ms 250 -- {command}", out ShellCommandRequest? request, out string? errorMessage);

        Assert.IsTrue(success, errorMessage);
        Assert.IsNotNull(request);
        Assert.AreEqual(command, request!.Command);
        Assert.AreEqual(250L, request.TimeoutMs);
    }

    [TestMethod]
    public void Parser_LeavesTimeoutUnsetWhenOmitted()
    {
        bool success = ShellCommandParser.TryParseArguments("-- echo hello", out ShellCommandRequest? request, out string? errorMessage);

        Assert.IsTrue(success, errorMessage);
        Assert.IsNotNull(request);
        Assert.AreEqual("echo hello", request!.Command);
        Assert.IsNull(request.TimeoutMs);
    }

    [TestMethod]
    [DataRow("--timeout-ms 0 -- echo hello", 0L)]
    [DataRow("--timeout-ms 9223372036854775807 -- echo hello", long.MaxValue)]
    public void Parser_AcceptsNonNegativeInt64Timeout(string arguments, long expectedTimeoutMs)
    {
        bool success = ShellCommandParser.TryParseArguments(arguments, out ShellCommandRequest? request, out string? errorMessage);

        Assert.IsTrue(success, errorMessage);
        Assert.IsNotNull(request);
        Assert.AreEqual(expectedTimeoutMs, request!.TimeoutMs);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("--")]
    [DataRow("-- ")]
    [DataRow("--timeout-ms 5")]
    [DataRow("--timeout-ms -- echo hello")]
    [DataRow("--timeout-ms -1 -- echo hello")]
    [DataRow("--timeout-ms +1 -- echo hello")]
    [DataRow("--timeout-ms 1.5 -- echo hello")]
    [DataRow("--timeout-ms 9223372036854775808 -- echo hello")]
    [DataRow("--timeout-ms 1 --timeout-ms 2 -- echo hello")]
    [DataRow("--unknown -- echo hello")]
    [DataRow("--timeout-ms=1 -- echo hello")]
    public void Parser_RejectsInvalidArguments(string? arguments)
    {
        bool success = ShellCommandParser.TryParseArguments(arguments, out ShellCommandRequest? request, out string? errorMessage);

        Assert.IsFalse(success);
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrWhiteSpace(errorMessage));
    }
}
