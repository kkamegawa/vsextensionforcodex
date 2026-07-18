using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;
using Microsoft.VisualStudio.Extensibility.UI;
using AsyncCommand = Codex.VisualStudio.Extension.AsyncCommand;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ViewModelTests
{
    private static readonly ApprovalDecision[] AcceptDeclineCancel =
        [ApprovalDecision.Accept, ApprovalDecision.Decline, ApprovalDecision.Cancel];

    private static readonly ApprovalDecision[] AcceptCancel =
        [ApprovalDecision.Accept, ApprovalDecision.Cancel];

    private static readonly ApprovalDecision[] AcceptDecline =
        [ApprovalDecision.Accept, ApprovalDecision.Decline];

    private static readonly ApprovalDecision[] NoDecisions = [];

    private static readonly string[] ExpectedModes = ["Agent", "Chat"];
    private static readonly string[] ExpectedWorkerModels = ["gpt-5-codex", "gpt-5"];
    private static readonly string[] ExpectedModelsWithInjectedDefault = ["gpt-5.1-codex-max", "gpt-5-codex", "gpt-5"];
    private static readonly string[] ExpectedRefreshedModels = ["gpt-5.5", "gpt-5.4", "gpt-5.4-mini"];
    private static readonly string[] ExpectedReorderedModels = ["gpt-5", "gpt-5-codex", "gpt-5-mini"];
    private static readonly string[] CreativeOnly = ["Creative"];
    [TestMethod]
    public async Task ApprovalViewModel_ResolvesOnlyOnce()
    {
        int calls = 0;
        var viewModel = new ApprovalViewModel(
            new ApprovalRequest
            {
                RequestId = "request-1",
                Risk = ApprovalRiskCategory.Destructive,
                DisplayText = "git reset --hard",
                AvailableDecisions = AcceptDeclineCancel,
            },
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });

        viewModel.AcceptCommand.Execute(null);
        viewModel.DeclineCommand.Execute(null);
        await Task.Delay(100);

        Assert.AreEqual(1, calls);
        Assert.IsTrue(viewModel.IsResolved);
    }

    [TestMethod]
    public async Task UserInputViewModel_SingleSelect_SubmitsSelectedRawLabel()
    {
        string? capturedRequestId = null;
        IReadOnlyDictionary<string, string[]>? capturedAnswers = null;
        var request = new UserInputRequest
        {
            RequestId = "ui-1",
            Questions =
            [
                new UserInputQuestion
                {
                    Id = "q1",
                    Header = "Direction",
                    Question = "Which style?",
                    Options =
                    [
                        new UserInputOption { Label = "Sharp", Description = "d1" },
                        new UserInputOption { Label = "Creative", Description = "d2" },
                    ],
                },
            ],
        };
        var vm = new UserInputViewModel(
            request,
            (id, answers) =>
            {
                capturedRequestId = id;
                capturedAnswers = answers;
                return Task.CompletedTask;
            },
            new SafeMarkdownService());

        UserInputOptionViewModel first = vm.Questions[0].Options[0];
        UserInputOptionViewModel second = vm.Questions[0].Options[1];
        first.IsSelected = true;
        second.IsSelected = true; // selecting the second clears the first (single-select)

        Assert.IsFalse(first.IsSelected);
        Assert.IsTrue(second.IsSelected);

        vm.SubmitCommand.Execute(null);
        await Task.Delay(50);

        Assert.AreEqual("ui-1", capturedRequestId);
        Assert.IsNotNull(capturedAnswers);
        CollectionAssert.AreEqual(CreativeOnly, capturedAnswers!["q1"]);
        Assert.IsTrue(vm.IsResolved);
    }

    [TestMethod]
    public async Task ChatViewModel_UserInputQueue_ShowsOneActiveCardAtATime()
    {
        using var vm = new ChatViewModel();
        MethodInfo requested = typeof(ChatViewModel).GetMethod(
            "OnUserInputRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo resolved = typeof(ChatViewModel).GetMethod(
            "OnUserInputResolvedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)requested.Invoke(vm, [MakeUserInputRequest("ui-1")])!;
        Assert.IsTrue(vm.HasActiveUserInput);
        Assert.AreEqual("ui-1", vm.ActiveUserInput!.RequestId);
        Assert.AreEqual(string.Empty, vm.UserInputQueueText);

        // Second request is queued, not shown — the active card stays put.
        await (Task)requested.Invoke(vm, [MakeUserInputRequest("ui-2")])!;
        Assert.AreEqual("ui-1", vm.ActiveUserInput!.RequestId);
        Assert.AreEqual("1 choice waiting", vm.UserInputQueueText);

        // Resolving the active one promotes the queued one.
        await (Task)resolved.Invoke(vm, ["ui-1"])!;
        Assert.AreEqual("ui-2", vm.ActiveUserInput!.RequestId);
        Assert.AreEqual(string.Empty, vm.UserInputQueueText);

        // Resolving the last clears the card; a duplicate resolve is a no-op.
        await (Task)resolved.Invoke(vm, ["ui-2"])!;
        Assert.IsFalse(vm.HasActiveUserInput);
        await (Task)resolved.Invoke(vm, ["ui-2"])!;
        Assert.IsFalse(vm.HasActiveUserInput);
    }

    [TestMethod]
    public async Task ChatViewModel_ApprovalQueue_ShowsOneActiveCardAtATime()
    {
        using var vm = new ChatViewModel();
        MethodInfo requested = typeof(ChatViewModel).GetMethod(
            "OnApprovalRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo resolved = typeof(ChatViewModel).GetMethod(
            "OnApprovalResolvedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)requested.Invoke(vm, [MakeApprovalRequest("req-1")])!;
        Assert.IsTrue(vm.HasActiveApproval);
        Assert.AreEqual("req-1", vm.ActiveApproval!.RequestId);
        Assert.AreEqual(string.Empty, vm.ApprovalQueueText);

        // Concurrent prompts are queued, not stacked: the active card stays put and the rest are counted.
        await (Task)requested.Invoke(vm, [MakeApprovalRequest("req-2")])!;
        Assert.AreEqual("req-1", vm.ActiveApproval!.RequestId);
        Assert.AreEqual("1 approval waiting", vm.ApprovalQueueText);

        await (Task)requested.Invoke(vm, [MakeApprovalRequest("req-3")])!;
        Assert.AreEqual("2 approvals waiting", vm.ApprovalQueueText);

        // Resolving the active one promotes the next queued prompt.
        await (Task)resolved.Invoke(vm, ["req-1"])!;
        Assert.AreEqual("req-2", vm.ActiveApproval!.RequestId);
        Assert.AreEqual("1 approval waiting", vm.ApprovalQueueText);

        // A prompt resolved while still queued is dropped without becoming active.
        await (Task)resolved.Invoke(vm, ["req-3"])!;
        Assert.AreEqual("req-2", vm.ActiveApproval!.RequestId);
        Assert.AreEqual(string.Empty, vm.ApprovalQueueText);

        // Resolving the last clears the card; a duplicate resolve is a no-op.
        await (Task)resolved.Invoke(vm, ["req-2"])!;
        Assert.IsFalse(vm.HasActiveApproval);
        await (Task)resolved.Invoke(vm, ["req-2"])!;
        Assert.IsFalse(vm.HasActiveApproval);
    }

    private static ApprovalRequest MakeApprovalRequest(string requestId) => new()
    {
        RequestId = requestId,
        Risk = ApprovalRiskCategory.ReadOnly,
        DisplayText = "apply change",
        AvailableDecisions = AcceptDeclineCancel,
    };

    private static UserInputRequest MakeUserInputRequest(string requestId) => new()
    {
        RequestId = requestId,
        Questions =
        [
            new UserInputQuestion
            {
                Id = "q1",
                Header = "Direction",
                Question = "Which style?",
                Options = [new UserInputOption { Label = "Sharp", Description = "d1" }],
            },
        ],
    };

    [TestMethod]
    public void ChoicePromptParser_DetectsQuestionWithNumberedOptions()
    {
        string text = string.Join('\n',
            "次のどれで進めますか？",
            "1. **シャープな開発者ポートフォリオ**: 黒/白/赤",
            "2. クリエイティブ寄りポートフォリオ",
            "3. 企業/コンサル寄りポートフォリオ");

        bool ok = ChoicePromptParser.TryParse(text, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, request.Questions.Count);
        Assert.AreEqual(3, request.Questions[0].Options.Count);
        // Inline markdown is stripped from the echoed label.
        Assert.AreEqual("シャープな開発者ポートフォリオ: 黒/白/赤", request.Questions[0].Options[0].Label);
        Assert.IsTrue(request.Questions[0].Question.EndsWith('？'));
        Assert.IsTrue(request.RequestId.StartsWith("choice-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ChoicePromptParser_DetectsJapaneseConfirmationQuestion()
    {
        string text = string.Join('\n',
            "既存プロジェクトに LICENSE がありませんでした。AGENTS.md の指示により、作業開始前に確認が必要です。",
            "MIT ライセンスで進めてよいですか？",
            "MIT 以外にする場合は、使用するライセンス名を指定してください。");

        bool ok = ChoicePromptParser.TryParse(text, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, request.Questions.Count);
        Assert.AreEqual("MIT ライセンスで進めてよいですか？", request.Questions[0].Question);
        Assert.AreEqual(2, request.Questions[0].Options.Count);
        Assert.AreEqual("Yes", request.Questions[0].Options[0].Label);
        Assert.AreEqual("No", request.Questions[0].Options[1].Label);
    }

    [TestMethod]
    [DataRow("Do you want me to continue?")]
    [DataRow("Do you want to apply the change?")]
    public void ChoicePromptParser_DetectsEnglishActionConfirmationQuestion(string text)
    {
        bool ok = ChoicePromptParser.TryParse(text, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, request.Questions.Count);
        Assert.AreEqual(text, request.Questions[0].Question);
        Assert.AreEqual(2, request.Questions[0].Options.Count);
        Assert.AreEqual("Yes", request.Questions[0].Options[0].Label);
        Assert.AreEqual("No", request.Questions[0].Options[1].Label);
    }

    [TestMethod]
    public void ChoicePromptParser_ProceedMentionOutsideQuestionLine_DoesNotTriggerConfirmationCard()
    {
        // "proceed" appears in an earlier sentence, but the actual question is open-ended (not
        // answerable with Yes/No). Only the extracted question line should be checked for
        // confirmation keywords, not the whole message.
        string text = string.Join('\n',
            "I'll proceed with the fix now that you've confirmed the plan.",
            "What time should the job run?");

        bool ok = ChoicePromptParser.TryParse(text, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    [DataRow("Here are the steps:\n1. Build\n2. Test\n3. Ship", DisplayName = "numbered list without a question")]
    [DataRow("Which one?\n1. Only one option", DisplayName = "question with a single option")]
    [DataRow("Just a sentence with no list?", DisplayName = "question without options")]
    [DataRow("What do you want me to do?", DisplayName = "open-ended do-you-want question")]
    [DataRow("Which option do you want me to use?", DisplayName = "embedded do-you-want question")]
    [DataRow("", DisplayName = "empty")]
    public void ChoicePromptParser_RejectsNonChoiceText(string text)
    {
        Assert.IsFalse(ChoicePromptParser.TryParse(text, out _));
    }

    [TestMethod]
    public async Task ChatViewModel_ProseConfirmationPrompt_ShowsCardWithoutExperimentalApi()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);
        ExtensionSettings settings = GetSettings(vm);
        settings.ExperimentalApiEnabled = false;

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            Text = string.Join('\n',
                "既存プロジェクトに LICENSE がありませんでした。",
                "MIT ライセンスで進めてよいですか？",
                "MIT 以外にする場合は、使用するライセンス名を指定してください。"),
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });

        Assert.IsTrue(vm.HasActiveUserInput);
        Assert.IsNotNull(vm.ActiveUserInput);
        Assert.IsTrue(vm.ActiveUserInput!.IsSynthetic);
        Assert.AreEqual(2, vm.ActiveUserInput.Questions[0].Options.Count);
        Assert.AreEqual("Yes", vm.ActiveUserInput.Questions[0].Options[0].DisplayLabel);
        Assert.AreEqual("No", vm.ActiveUserInput.Questions[0].Options[1].DisplayLabel);
    }

    [TestMethod]
    public void SafeMarkdown_RemovesHtmlAnsiAndControlCharacters()
    {
        var service = new SafeMarkdownService();

        // Contains HTML tag, ANSI escape sequences, and a control character.
        string input = "<script>bad()</script> **safe** \x1b[31mred\x1b[0m \x00";
        string text = service.ToSafeText(input);

        Assert.IsFalse(text.Contains("<script>", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains('\x1b'));
        Assert.IsFalse(text.Contains('\x00'));
        Assert.IsTrue(text.Contains("safe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SafeMarkdown_JapaneseSoftBreak_NoSpaceBetweenCharacters()
    {
        var service = new SafeMarkdownService();

        string text = service.ToSafeText("こんにちは\n今日は良い天気ですね");

        Assert.IsFalse(text.Contains("こんにちは 今日は", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SafeMarkdown_LatinSoftBreak_PreservesWordBoundary()
    {
        var service = new SafeMarkdownService();

        string text = service.ToSafeText("Hello\nworld");

        Assert.IsTrue(text.Contains("Hello", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("world", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SafeMarkdown_DoubleParagraphBreak_PreservesSeparation()
    {
        var service = new SafeMarkdownService();

        string text = service.ToSafeText("First paragraph\n\nSecond paragraph");

        Assert.IsTrue(text.Contains("First paragraph", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Second paragraph", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains('\n'));
    }

    [TestMethod]
    public void SafeMarkdown_FencedCodeBlock_PreservesInternalNewlines()
    {
        var service = new SafeMarkdownService();

        string text = service.ToSafeText("Intro\n\n```\nfoo()\nbar()\n```\n\nOutro");

        Assert.IsTrue(text.Contains("foo()\nbar()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SafeMarkdown_MixedCjkAndLatin_PreservesBoundarySpace()
    {
        var service = new SafeMarkdownService();

        string text = service.ToSafeText("API の 使い方");

        Assert.IsTrue(text.Contains("API の", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("の使い方", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SafeMarkdown_ToBlocks_PreservesMarkdownStructure()
    {
        var service = new SafeMarkdownService();

        IReadOnlyList<ChatBlockViewModel> blocks = service.ToBlocks(string.Join('\n',
            "# Heading",
            "",
            "Intro **bold** text",
            "",
            "- First",
            "- Second",
            "",
            "```csharp",
            "Console.WriteLine(\"hi\");",
            "Next();",
            "```",
            "",
            "---"));

        Assert.IsTrue(blocks.Any(block => block.IsHeading && block.IsH1 && block.Text == "Heading"));
        Assert.IsTrue(blocks.Any(block => block.IsParagraph && block.Text == "Intro bold text"));
        Assert.AreEqual(2, blocks.Count(block => block.IsListItem));
        ChatBlockViewModel code = blocks.Single(block => block.IsCodeBlock);
        Assert.AreEqual("csharp", code.Language);
        Assert.IsTrue(code.Code.Contains("Console.WriteLine", StringComparison.Ordinal));
        Assert.IsTrue(code.Code.Contains('\n'));
        Assert.IsTrue(blocks.Any(block => block.IsSeparator));
    }

    [TestMethod]
    public void SafeMarkdown_ToBlocks_RemovesHtmlAnsiAndControlCharacters()
    {
        var service = new SafeMarkdownService();

        IReadOnlyList<ChatBlockViewModel> blocks = service.ToBlocks("<script>bad()</script> **safe** \x1b[31mred\x1b[0m \x00");

        string combined = string.Concat(blocks.Select(block => block.Text + block.Code));
        Assert.IsFalse(combined.Contains("<script>", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains('\x1b'));
        Assert.IsFalse(combined.Contains('\x00'));
        Assert.IsTrue(combined.Contains("safe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SafeMarkdown_ToBlocks_FencedCode_PreservesAngleBrackets()
    {
        var service = new SafeMarkdownService();

        IReadOnlyList<ChatBlockViewModel> blocks = service.ToBlocks("```csharp\nList<int> values = [];\n```");

        ChatBlockViewModel code = blocks.Single(block => block.IsCodeBlock);
        Assert.AreEqual("List<int> values = [];", code.Code);
    }

    [TestMethod]
    public void SafeMarkdown_ToBlocks_StripsHtmlTagsFromHeadingParagraphAndList()
    {
        var service = new SafeMarkdownService();

        IReadOnlyList<ChatBlockViewModel> blocks = service.ToBlocks(string.Join('\n',
            "# Heading <script>alert(1)</script>",
            "",
            "Paragraph with <b>bold</b> and <i>markup</i>",
            "",
            "- item <span>one</span>"));

        ChatBlockViewModel heading = blocks.Single(block => block.IsHeading);
        ChatBlockViewModel paragraph = blocks.Single(block => block.IsParagraph);
        ChatBlockViewModel listItem = blocks.Single(block => block.IsListItem);

        Assert.AreEqual("Heading alert(1)", heading.Text);
        Assert.AreEqual("Paragraph with bold and markup", paragraph.Text);
        Assert.AreEqual("• item one", listItem.Text);
        Assert.IsFalse(string.Concat(blocks.Select(block => block.Text)).Contains('<'));
    }

    [TestMethod]
    public void SafeMarkdown_ToBlocks_FencedCodeLanguage_UsesOnlyFirstInfoToken()
    {
        var service = new SafeMarkdownService();

        IReadOnlyList<ChatBlockViewModel> blocks = service.ToBlocks("```csharp hl_lines=\"1\"\nConsole.WriteLine(\"hi\");\n```");

        ChatBlockViewModel code = blocks.Single(block => block.IsCodeBlock);
        Assert.AreEqual("csharp", code.Language);
    }

    [TestMethod]
    public void SafeMarkdown_ToBlocks_OrderedList_NumbersItems()
    {
        var service = new SafeMarkdownService();

        IReadOnlyList<ChatBlockViewModel> blocks = service.ToBlocks(string.Join('\n',
            "1. First step",
            "2. Second step",
            "3. Third step"));

        ChatBlockViewModel[] listItems = blocks.Where(block => block.IsListItem).ToArray();
        Assert.AreEqual(3, listItems.Length);
        Assert.AreEqual("1. First step", listItems[0].Text);
        Assert.AreEqual("2. Second step", listItems[1].Text);
        Assert.AreEqual("3. Third step", listItems[2].Text);
        Assert.IsFalse(listItems.Any(block => block.IsNestedListItem || block.IsDeeplyNestedListItem));
    }

    [TestMethod]
    public void SafeMarkdown_ToBlocks_NestedList_SetsIndentFlags()
    {
        var service = new SafeMarkdownService();

        IReadOnlyList<ChatBlockViewModel> blocks = service.ToBlocks(string.Join('\n',
            "- Top",
            "  - Middle",
            "    - Deep"));

        ChatBlockViewModel top = blocks.Single(block => block.Text == "• Top");
        ChatBlockViewModel middle = blocks.Single(block => block.Text == "• Middle");
        ChatBlockViewModel deep = blocks.Single(block => block.Text == "• Deep");
        Assert.IsFalse(top.IsNestedListItem);
        Assert.IsFalse(top.IsDeeplyNestedListItem);
        Assert.IsTrue(middle.IsNestedListItem);
        Assert.IsFalse(middle.IsDeeplyNestedListItem);
        Assert.IsFalse(deep.IsNestedListItem);
        Assert.IsTrue(deep.IsDeeplyNestedListItem);
    }

    [TestMethod]
    public void SafeMarkdown_ToBlocks_OrderedList_HonorsStartNumber()
    {
        var service = new SafeMarkdownService();

        IReadOnlyList<ChatBlockViewModel> blocks = service.ToBlocks(string.Join('\n',
            "4. Fourth",
            "5. Fifth"));

        ChatBlockViewModel[] listItems = blocks.Where(block => block.IsListItem).ToArray();
        Assert.AreEqual(2, listItems.Length);
        Assert.AreEqual("4. Fourth", listItems[0].Text);
        Assert.AreEqual("5. Fifth", listItems[1].Text);
    }

    [TestMethod]
    public void ApprovalViewModel_AvailableDecisions_ControlButtonVisibility()
    {
        var request = new ApprovalRequest
        {
            RequestId = "req-2",
            Risk = ApprovalRiskCategory.ReadOnly,
            DisplayText = "read file",
            AvailableDecisions = AcceptCancel,
        };
        var vm = new ApprovalViewModel(request, (_, _) => Task.CompletedTask);

        Assert.IsTrue(vm.ShowAccept);
        Assert.IsFalse(vm.ShowAcceptForTurn);
        Assert.IsFalse(vm.ShowAcceptForThread);
        Assert.IsFalse(vm.ShowAcceptForSession);
        Assert.IsFalse(vm.ShowDecline);
        Assert.IsTrue(vm.ShowCancel);
    }

    [TestMethod]
    public void ApprovalViewModel_NetworkApproval_ParsesHostAndPort()
    {
        var request = new ApprovalRequest
        {
            RequestId = "req-3",
            Risk = ApprovalRiskCategory.Network,
            RiskKey = "network:api.example.com:443",
            DisplayText = "api.example.com",
            AvailableDecisions = AcceptDecline,
        };
        var vm = new ApprovalViewModel(request, (_, _) => Task.CompletedTask);

        Assert.IsTrue(vm.IsNetworkApproval);
        Assert.AreEqual("api.example.com", vm.NetworkHost);
        Assert.AreEqual("443", vm.NetworkPort);
    }

    [TestMethod]
    public void ApprovalViewModel_PolicyBlocked_NoButtonsAvailable()
    {
        var request = new ApprovalRequest
        {
            RequestId = "req-4",
            Risk = ApprovalRiskCategory.Destructive,
            DisplayText = "rm -rf /",
            IsPolicyBlocked = true,
            PolicyBlockReason = "Destructive commands are blocked by policy.",
            AvailableDecisions = NoDecisions,
        };
        var vm = new ApprovalViewModel(request, (_, _) => Task.CompletedTask);

        Assert.IsFalse(vm.ShowAccept);
        Assert.IsFalse(vm.ShowAcceptForSession);
        Assert.IsFalse(vm.ShowDecline);
        Assert.IsFalse(vm.ShowCancel);
        Assert.IsFalse(vm.CanResolve);
    }

    [TestMethod]
    public async Task ChatViewModel_ApprovalDecision_AppendsResultOnlyTranscriptLine()
    {
        // Copilot Chat parity (issue #25): after the user decides, the approval card goes away
        // (worker echo) and the transcript keeps a single sanitized result line.
        using var vm = new ChatViewModel();
        MethodInfo requested = typeof(ChatViewModel).GetMethod(
            "OnApprovalRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)requested.Invoke(vm, [new ApprovalRequest
        {
            RequestId = "req-decision",
            Risk = ApprovalRiskCategory.Destructive,
            DisplayText = "git reset --hard",
            AvailableDecisions = AcceptDeclineCancel,
        }])!;

        vm.AppendDecisionResultItem(vm.BuildDecisionSummary("req-decision", ApprovalDecision.Accept));

        ChatItemViewModel result = vm.Items.Single(item => item.Role == "Decision");
        Assert.AreEqual(ConversationEventKind.ItemCompleted, result.Kind);
        Assert.IsTrue(result.Text.Contains("Accepted", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("git reset --hard", StringComparison.Ordinal));
        Assert.IsFalse(result.UsesBlockRendering);
    }

    [TestMethod]
    public void ChatViewModel_UnknownApprovalId_AppendsDecisionWithoutDisplayText()
    {
        using var vm = new ChatViewModel();

        vm.AppendDecisionResultItem(vm.BuildDecisionSummary("missing-request", ApprovalDecision.Decline));

        ChatItemViewModel result = vm.Items.Single(item => item.Role == "Decision");
        Assert.AreEqual("Declined", result.Text.Trim());
    }

    [TestMethod]
    public void ChatViewModel_UserInputResult_AppendsSanitizedSelection()
    {
        using var vm = new ChatViewModel();
        var answers = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["q1"] = ["<b>Sharp</b> \x1b[31mstyle\x1b[0m"],
        };

        vm.AppendUserInputResultItem(answers);

        ChatItemViewModel result = vm.Items.Single(item => item.Role == "Decision");
        Assert.IsTrue(result.Text.Contains("Selected", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("Sharp style", StringComparison.Ordinal));
        Assert.IsFalse(result.Text.Contains('<'));
        Assert.IsFalse(result.Text.Contains('\x1b'));
    }

    [TestMethod]
    public void ChatViewModel_UserInputResult_NoSelection_AppendsNothing()
    {
        using var vm = new ChatViewModel();

        vm.AppendUserInputResultItem(new Dictionary<string, string[]>(StringComparer.Ordinal));

        Assert.AreEqual(0, vm.Items.Count);
    }

    [TestMethod]
    [DataRow(ApprovalDecision.Accept, "Accepted")]
    [DataRow(ApprovalDecision.AcceptForTurn, "Accepted for turn")]
    [DataRow(ApprovalDecision.AcceptForThread, "Accepted for thread")]
    [DataRow(ApprovalDecision.AcceptForSession, "Accepted for session")]
    [DataRow(ApprovalDecision.Decline, "Declined")]
    [DataRow(ApprovalDecision.Cancel, "Cancelled")]
    public void ChatViewModel_DescribeDecision_MapsEveryDecision(ApprovalDecision decision, string expected)
    {
        Assert.AreEqual(expected, ChatViewModel.DescribeDecision(decision));
    }

    [TestMethod]
    public async Task ChatViewModel_ApprovalRpcFailure_DoesNotAppendTranscriptLine()
    {
        var bridge = new FakeWorkerBridge { ResolveApprovalException = new InvalidOperationException("disconnected") };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        MethodInfo requested = typeof(ChatViewModel).GetMethod(
            "OnApprovalRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)requested.Invoke(vm, [new ApprovalRequest
        {
            RequestId = "req-fail",
            Risk = ApprovalRiskCategory.Destructive,
            DisplayText = "git reset --hard",
            AvailableDecisions = AcceptDeclineCancel,
        }])!;

        MethodInfo resolve = typeof(ChatViewModel).GetMethod(
            "ResolveApprovalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        InvalidOperationException? caught = null;
        try
        {
            await (Task)resolve.Invoke(vm, ["req-fail", ApprovalDecision.Accept])!;
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Assert.IsNotNull(caught);
        Assert.IsFalse(vm.Items.Any(item => item.Role == "Decision"));
    }

    [TestMethod]
    public async Task ChatViewModel_ApprovalRpcSuccess_AppendsTranscriptLineAfterResolve()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        MethodInfo requested = typeof(ChatViewModel).GetMethod(
            "OnApprovalRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)requested.Invoke(vm, [new ApprovalRequest
        {
            RequestId = "req-ok",
            Risk = ApprovalRiskCategory.Destructive,
            DisplayText = "git push --force",
            AvailableDecisions = AcceptDeclineCancel,
        }])!;

        MethodInfo resolve = typeof(ChatViewModel).GetMethod(
            "ResolveApprovalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)resolve.Invoke(vm, ["req-ok", ApprovalDecision.Accept])!;

        ChatItemViewModel result = vm.Items.Single(item => item.Role == "Decision");
        Assert.IsTrue(result.Text.Contains("Accepted", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("git push --force", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_UserInputRpcFailure_DoesNotAppendTranscriptLine()
    {
        var bridge = new FakeWorkerBridge { ResolveUserInputException = new InvalidOperationException("disconnected") };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        var answers = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["q1"] = ["Yes"] };

        MethodInfo resolve = typeof(ChatViewModel).GetMethod(
            "ResolveUserInputAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        InvalidOperationException? caught = null;
        try
        {
            await (Task)resolve.Invoke(vm, ["req-1", answers])!;
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Assert.IsNotNull(caught);
        Assert.IsFalse(vm.Items.Any(item => item.Role == "Decision"));
    }

    [TestMethod]
    public async Task ChatViewModel_UserInputRpcSuccess_AppendsTranscriptLineAfterResolve()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        var answers = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["q1"] = ["Yes"] };

        MethodInfo resolve = typeof(ChatViewModel).GetMethod(
            "ResolveUserInputAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)resolve.Invoke(vm, ["req-1", answers])!;

        ChatItemViewModel result = vm.Items.Single(item => item.Role == "Decision");
        Assert.IsTrue(result.Text.Contains("Selected — Yes", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ChatItemViewModel_ReasoningItem_StartsCollapsed()
    {
        var item = new ChatItemViewModel("Reasoning", "some reasoning text", ConversationEventKind.ReasoningSummaryDelta);

        Assert.IsTrue(item.IsCollapsed);
        Assert.IsTrue(item.IsReasoningItem);
        Assert.IsFalse(item.IsCommandItem);
        Assert.AreEqual("▶ Reasoning", item.CollapseButtonText);
    }

    [TestMethod]
    public async Task ChatItemViewModel_ToggleCollapse_ChangesState()
    {
        var item = new ChatItemViewModel("Reasoning", "text", ConversationEventKind.ReasoningSummaryDelta);
        Assert.IsTrue(item.IsCollapsed);

        item.ToggleCollapseCommand.Execute(null);
        await Task.Delay(50);

        Assert.IsFalse(item.IsCollapsed);
        Assert.AreEqual("▼ Reasoning", item.CollapseButtonText);
    }

    [TestMethod]
    public void ChatItemViewModel_ParsePlanSteps_ExtractsStepTitles()
    {
        string json = """{"steps":[{"title":"Step 1"},{"title":"Step 2"},{"title":"Step 3"}]}""";
        IReadOnlyList<string> steps = ChatItemViewModel.ParsePlanSteps(json);

        Assert.AreEqual(3, steps.Count);
        Assert.AreEqual("Step 1", steps[0]);
        Assert.AreEqual("Step 3", steps[2]);
    }

    [TestMethod]
    public void ChatItemViewModel_ParsePlanSteps_ReturnEmptyOnMalformed()
    {
        IReadOnlyList<string> steps = ChatItemViewModel.ParsePlanSteps("not-json");
        Assert.AreEqual(0, steps.Count);
    }

    [TestMethod]
    public void ChatItemViewModel_UpdatePlanSteps_PrefixesBullet()
    {
        var item = new ChatItemViewModel("Plan", string.Empty, ConversationEventKind.PlanUpdated);
        item.UpdatePlanSteps(["Do A", "Do B"]);

        Assert.AreEqual(2, item.PlanSteps.Count);
        Assert.IsTrue(item.PlanSteps[0].StartsWith('•'));
    }

    [TestMethod]
    public void ChatItemViewModel_CommandItem_KindFlags()
    {
        var item = new ChatItemViewModel("Command", "output", ConversationEventKind.CommandOutputDelta);

        Assert.IsTrue(item.IsCommandItem);
        Assert.IsFalse(item.IsReasoningItem);
        Assert.IsFalse(item.IsDiffItem);
        Assert.IsFalse(item.IsPlanItem);
        Assert.IsFalse(item.IsCollapsed);
    }

    [TestMethod]
    public async Task ChatViewModel_AgentMessageDelta_MultipleChunks_NoArtificialNewlines()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-1",
            Text = "作る前に Product Design の",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-1",
            Text = "確認が必要です。",
        });

        string text = SingleConversationItem(vm, "agent-1", ConversationEventKind.AgentMessageDelta).Text.TrimEnd('\r', '\n');
        Assert.IsFalse(text.Contains('\n'));
        Assert.IsFalse(text.Contains('\r'));
        Assert.IsTrue(text.Contains("Product Design の確認", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_AgentMessageDelta_FullTextRerendered_NotAppended()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-2",
            Text = "Hello ",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-2",
            Text = "world",
        });

        string text = SingleConversationItem(vm, "agent-2", ConversationEventKind.AgentMessageDelta).Text.TrimEnd('\r', '\n');
        Assert.AreEqual("Hello world", text);
    }

    [TestMethod]
    public async Task ChatViewModel_AgentMessageDelta_JapaneseFragments_NoInterCharacterSpace()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-3",
            Text = "こんにちは",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-3",
            Text = "今日は",
        });

        string text = SingleConversationItem(vm, "agent-3", ConversationEventKind.AgentMessageDelta).Text.TrimEnd('\r', '\n');
        Assert.IsTrue(text.Contains("こんにちは今日は", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("こんにちは\n今日は", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("こんにちは 今日は", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_AgentAndReasoningDeltas_WithSameItemId_DoNotShareAccumulator()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "shared-item",
            Text = "Answer ",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.ReasoningSummaryDelta,
            ItemId = "shared-item",
            Text = "Thought ",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "shared-item",
            Text = "done",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.ReasoningSummaryDelta,
            ItemId = "shared-item",
            Text = "complete",
        });

        string agentText = SingleConversationItem(vm, "shared-item", ConversationEventKind.AgentMessageDelta).Text.TrimEnd('\r', '\n');
        string reasoningText = SingleConversationItem(vm, "shared-item", ConversationEventKind.ReasoningSummaryDelta).Text.TrimEnd('\r', '\n');
        Assert.AreEqual("Answer done", agentText);
        Assert.AreEqual("Thought complete", reasoningText);
    }

    [TestMethod]
    public async Task ChatViewModel_AgentMessageDelta_PopulatesStructuredBlocks()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-blocks",
            Text = "# Title\n\nParagraph ",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-blocks",
            Text = "text\n\n```bash\necho hi\n```",
        });

        ChatItemViewModel item = SingleConversationItem(vm, "agent-blocks", ConversationEventKind.AgentMessageDelta);
        Assert.IsTrue(item.UsesBlockRendering);
        Assert.IsTrue(item.Blocks.Any(block => block.IsHeading && block.Text == "Title"));
        Assert.IsTrue(item.Blocks.Any(block => block.IsParagraph && block.Text == "Paragraph text"));
        ChatBlockViewModel code = item.Blocks.Single(block => block.IsCodeBlock);
        Assert.AreEqual("bash", code.Language);
        Assert.AreEqual("echo hi", code.Code);
        Assert.IsTrue(item.Text.Contains("Paragraph text", StringComparison.Ordinal));
        Assert.IsTrue(item.Text.Contains("echo hi", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_ReasoningSummaryDelta_PopulatesStructuredBlocksButStartsCollapsed()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.ReasoningSummaryDelta,
            ItemId = "reasoning-blocks",
            Text = "## Why\n\nBecause.",
        });

        ChatItemViewModel item = SingleConversationItem(vm, "reasoning-blocks", ConversationEventKind.ReasoningSummaryDelta);
        Assert.IsTrue(item.UsesBlockRendering);
        Assert.IsTrue(item.IsCollapsed);
        Assert.IsTrue(item.Blocks.Any(block => block.IsHeading && block.IsH2 && block.Text == "Why"));
        Assert.IsTrue(item.Blocks.Any(block => block.IsParagraph && block.Text == "Because."));
    }

    [TestMethod]
    public async Task ChatViewModel_CommandOutputDelta_PreservesNewlines()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.CommandOutputDelta,
            ItemId = "command-1",
            Text = "line1\n",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.CommandOutputDelta,
            ItemId = "command-1",
            Text = "line2\n",
        });

        string text = SingleConversationItem(vm, "command-1", ConversationEventKind.CommandOutputDelta).Text;
        Assert.IsTrue(text.Contains('\n'));
        Assert.IsTrue(text.Contains("line1", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("line2", StringComparison.Ordinal));
        Assert.IsFalse(SingleConversationItem(vm, "command-1", ConversationEventKind.CommandOutputDelta).UsesBlockRendering);
        Assert.AreEqual(0, SingleConversationItem(vm, "command-1", ConversationEventKind.CommandOutputDelta).Blocks.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_TurnStarted_ClearsItemRawText()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-reset",
            Text = "First",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = "agent-reset",
            Text = "Second",
        });

        string text = SingleConversationItem(vm, "agent-reset", ConversationEventKind.AgentMessageDelta).Text.TrimEnd('\r', '\n');
        Assert.AreEqual("Second", text);
    }

    [TestMethod]
    [DataRow(ConversationEventKind.AgentMessageDelta, true)]
    [DataRow(ConversationEventKind.ReasoningSummaryDelta, true)]
    [DataRow(ConversationEventKind.CommandOutputDelta, true)]
    [DataRow(ConversationEventKind.DiffUpdated, true)]
    [DataRow(ConversationEventKind.PlanUpdated, true)]
    [DataRow(ConversationEventKind.ItemStarted, false)]
    [DataRow(ConversationEventKind.ItemCompleted, false)]
    [DataRow(ConversationEventKind.TurnStarted, false)]
    [DataRow(ConversationEventKind.TurnCompleted, false)]
    [DataRow(ConversationEventKind.Error, false)]
    [DataRow(ConversationEventKind.Unknown, false)]
    public void ConversationEventPresentation_IsPanelContent_SeparatesUserFacingFromDiagnostic(
        ConversationEventKind kind, bool expected)
    {
        // Regression guard (issue #17): only user-facing Codex content reaches the panel; lifecycle,
        // protocol, error, and unknown events are diagnostic and must be routed to the Output channel.
        Assert.AreEqual(expected, ConversationEventPresentation.IsPanelContent(kind));
    }

    [TestMethod]
    public void ConversationEventPresentation_IsPanelContent_ExactlyFiveUserFacingKinds()
    {
        // If a new ConversationEventKind is added, force a deliberate panel/diagnostic decision
        // rather than silently inheriting the diagnostic default.
        int panelKinds = Enum.GetValues<ConversationEventKind>()
            .Count(ConversationEventPresentation.IsPanelContent);
        Assert.AreEqual(5, panelKinds);
    }

    [TestMethod]
    public void ConversationEventPresentation_FormatDiagnostic_Error_IsSingleLineWithText()
    {
        var value = new ConversationEvent
        {
            Kind = ConversationEventKind.Error,
            Text = "boom\r\nsecond line",
        };

        string line = ConversationEventPresentation.FormatDiagnostic(value);

        Assert.IsTrue(line.StartsWith("[codex-error]", StringComparison.Ordinal));
        Assert.IsTrue(line.Contains("boom", StringComparison.Ordinal));
        Assert.IsFalse(line.Contains('\n'), "A diagnostic must occupy a single Output line.");
        Assert.IsFalse(line.Contains('\r'), "A diagnostic must occupy a single Output line.");
    }

    [TestMethod]
    public void ConversationEventPresentation_FormatDiagnostic_Lifecycle_IncludesKindAndIds()
    {
        var value = new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "t1",
            TurnId = "u1",
            PayloadJson = """{"turn":"done"}""",
        };

        string line = ConversationEventPresentation.FormatDiagnostic(value);

        Assert.IsTrue(line.StartsWith("[event]", StringComparison.Ordinal));
        Assert.IsTrue(line.Contains("TurnCompleted", StringComparison.Ordinal));
        Assert.IsTrue(line.Contains("thread=t1", StringComparison.Ordinal));
        Assert.IsTrue(line.Contains("turn=u1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AccountPanelViewModel_MapsAccountStatesToDisplayText()
    {
        var viewModel = new AccountPanelViewModel();

        viewModel.Update(new AccountStatus { State = AccountState.SignedOut });
        Assert.AreEqual("Not signed in", viewModel.DisplayText);
        Assert.IsTrue(viewModel.ShowSignIn);
        Assert.IsTrue(viewModel.ShowAction);
        Assert.AreEqual("Sign in", viewModel.ActionText);

        viewModel.Update(new AccountStatus { State = AccountState.SigningIn });
        Assert.AreEqual("Signing in...", viewModel.DisplayText);
        Assert.IsFalse(viewModel.ShowSignIn);
        Assert.IsFalse(viewModel.ShowAction);

        viewModel.Update(new AccountStatus { State = AccountState.SignedIn, PlanType = "plus" });
        Assert.AreEqual("Signed in \u00b7 plus", viewModel.DisplayText);
        Assert.IsFalse(viewModel.ShowSignIn);
        Assert.IsTrue(viewModel.ShowAction);
        Assert.IsTrue(viewModel.IsSignedIn);
        Assert.AreEqual("Sign out", viewModel.ActionText);

        viewModel.Update(new AccountStatus { State = AccountState.Unavailable });
        Assert.AreEqual("Account status unavailable", viewModel.DisplayText);
        Assert.IsTrue(viewModel.ShowSignIn);
        Assert.IsTrue(viewModel.ShowAction);
        Assert.AreEqual("Sign in", viewModel.ActionText);

        viewModel.Update(new AccountStatus { State = AccountState.Unavailable, Message = "Could not open the default browser." });
        Assert.AreEqual("Account status unavailable \u00b7 Could not open the default browser.", viewModel.DisplayText);
    }

    [TestMethod]
    public void ChatToolWindowXaml_UsesTopLevelAccountBindings()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        string xaml = reader.ReadToEnd();

        Assert.IsTrue(xaml.Contains("{Binding AccountActionText}", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("{Binding ShowAccountAction,", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("{Binding StatusDetailText}", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("{Binding Account.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ChatToolWindowXaml_ComposerBindsCtrlEnterToSendCommand()
    {
        // Regression: the redesigned composer uses an icon Send button (not IsDefault), so the
        // only keyboard send affordance is a Ctrl+Enter KeyBinding, while plain Enter keeps
        // inserting a newline (AcceptsReturn="True"). KeyBinding honours SendCommand.CanExecute,
        // so it is a no-op on empty/disabled input. Lock the gesture wiring so it cannot
        // silently disappear again.
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        // Locate the composer specifically (the TextBox bound to ComposerText), so the
        // assertions can't be satisfied by some other TextBox/KeyBinding elsewhere.
        XElement? composer = doc
            .Descendants(presentation + "TextBox")
            .SingleOrDefault(tb => (tb.Attribute("Text")?.Value ?? string.Empty)
                .Contains("ComposerText", StringComparison.Ordinal));
        Assert.IsNotNull(composer, "Could not find the composer TextBox bound to ComposerText.");

        // Plain Enter must keep inserting a newline.
        Assert.AreEqual(
            "True",
            composer!.Attribute("AcceptsReturn")?.Value,
            "Composer TextBox must keep AcceptsReturn=\"True\" so plain Enter inserts a newline.");

        // Ctrl+Enter (scoped to the composer via TextBox.InputBindings) invokes SendCommand.
        // Attribute values are matched case-sensitively (XAML is case-sensitive).
        XElement? keyBinding = composer
            .Element(presentation + "TextBox.InputBindings")?
            .Elements(presentation + "KeyBinding")
            .SingleOrDefault(kb => kb.Attribute("Key")?.Value == "Return" && kb.Attribute("Modifiers")?.Value == "Control");
        Assert.IsNotNull(keyBinding, "Composer TextBox.InputBindings must contain a Ctrl+Enter (Key=Return, Modifiers=Control) KeyBinding.");
        Assert.AreEqual(
            "{Binding SendCommand}",
            keyBinding!.Attribute("Command")?.Value,
            "The Ctrl+Enter KeyBinding must invoke SendCommand.");
    }

    // Remote UI replicates only [DataMember] properties of [DataContract] types into the
    // VS-side data context proxy; a type without the attributes serializes as an empty
    // object and every binding to it fails silently (blank text, empty button content).
    private static readonly Type[] RemoteUiContextTypes =
        [
            typeof(ChatViewModel), typeof(ChatItemViewModel), typeof(ChatBlockViewModel), typeof(ApprovalViewModel),
            typeof(UserInputViewModel), typeof(UserInputQuestionViewModel), typeof(UserInputOptionViewModel),
            typeof(SuggestionChip), typeof(SlashCommandPresentationViewModel),
            typeof(SlashCommandSuggestionViewModel), typeof(SlashCommandOptionViewModel),
            typeof(WorkerStatus), typeof(ThreadSummary),
        ];

    [TestMethod]
    public void RemoteUiContextTypes_AreDataContracts()
    {
        foreach (Type type in RemoteUiContextTypes)
        {
            Assert.IsNotNull(
                type.GetCustomAttribute<DataContractAttribute>(),
                $"{type.Name} is bound by ChatToolWindowContent.xaml and must be [DataContract] for Remote UI.");
        }
    }

    [TestMethod]
    public void ChatToolWindowXaml_ActiveApprovalDetails_AreBoundedAndScrollable()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement? displayText = doc
            .Descendants(presentation + "TextBlock")
            .SingleOrDefault(tb => string.Equals(
                tb.Attribute("Text")?.Value,
                "{Binding ActiveApproval.DisplayText}",
                StringComparison.Ordinal));
        Assert.IsNotNull(displayText, "Could not find ActiveApproval.DisplayText TextBlock.");

        XElement? detailsScrollViewer = displayText!
            .Ancestors(presentation + "ScrollViewer")
            .FirstOrDefault();
        Assert.IsNotNull(detailsScrollViewer, "Approval details must be wrapped in a ScrollViewer.");
        Assert.AreEqual("Auto", detailsScrollViewer!.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.AreEqual("Auto", detailsScrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.AreEqual("220", detailsScrollViewer.Attribute("MaxHeight")?.Value);

        XElement? acceptButton = doc
            .Descendants(presentation + "Button")
            .SingleOrDefault(btn => string.Equals(
                btn.Attribute("Command")?.Value,
                "{Binding ActiveApproval.AcceptCommand}",
                StringComparison.Ordinal));
        Assert.IsNotNull(acceptButton, "Could not find ActiveApproval.AcceptCommand button.");
        Assert.IsNull(
            acceptButton!.Ancestors(presentation + "ScrollViewer").FirstOrDefault(),
            "Approval decision buttons must remain outside the details ScrollViewer.");
    }

    [TestMethod]
    public void ChatToolWindowXaml_EveryBindingRoot_IsSerializableDataMember()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        string xaml = reader.ReadToEnd();

        var dataMemberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type type in RemoteUiContextTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetCustomAttribute<DataMemberAttribute>() is not null)
                    dataMemberNames.Add(property.Name);
            }
        }

        // First path segment of every {Binding Foo...} expression ({Binding} alone has no name).
        foreach (Match match in Regex.Matches(xaml, @"\{Binding\s+([A-Za-z_]\w*)"))
        {
            string root = match.Groups[1].Value;
            Assert.IsTrue(
                dataMemberNames.Contains(root),
                $"XAML binds '{root}' but no Remote UI context type exposes it as a [DataMember] property.");
        }
    }

    [TestMethod]
    public void AsyncCommand_CanExecute_IsResolvableViaPublicReflection()
    {
        // NotificationsDispatcher.HandleNotifyPropertyChanged resolves the changed property
        // with sender.GetType().GetProperty(name) and THROWS when the lookup fails. An
        // explicit interface implementation of IAsyncCommand.CanExecute is invisible to that
        // lookup; the resulting ArgumentException froze the worker RPC dispatch loop.
        var command = new AsyncCommand(() => Task.CompletedTask);

        PropertyInfo? property = command.GetType().GetProperty("CanExecute");
        Assert.IsNotNull(property, "AsyncCommand.CanExecute must be a public property.");
        Assert.AreEqual(typeof(bool), property!.PropertyType);
        Assert.IsTrue((bool)property.GetValue(command)!);
    }

    [TestMethod]
    public void AsyncCommand_RaiseCanExecuteChanged_RaisesPropertyChangedForCanExecute()
    {
        bool gate = false;
        var command = new AsyncCommand(() => Task.CompletedTask, () => gate);
        string? raisedProperty = null;
        command.PropertyChanged += (_, e) => raisedProperty = e.PropertyName;

        gate = true;
        command.RaiseCanExecuteChanged();

        Assert.AreEqual("CanExecute", raisedProperty);
        Assert.IsTrue(command.CanExecute);
    }

    [TestMethod]
    public async Task SuggestionChip_UseCommand_InvokesCallbackWithText()
    {
        string? captured = null;
        var chip = new SuggestionChip("Write unit tests for this file", text =>
        {
            captured = text;
            return Task.CompletedTask;
        });

        chip.UseCommand.Execute(null);
        await Task.Delay(50);

        Assert.AreEqual("Write unit tests for this file", chip.Text);
        Assert.AreEqual("Write unit tests for this file", captured);
    }

    [TestMethod]
    public void ChatViewModel_NewInstance_SeedsWelcomeState()
    {
        // Construction fires a fire-and-forget ConnectAsync; the worker exe is absent from the
        // test output so it fails fast and is caught (no process, no hang). We only assert the
        // synchronously-seeded welcome-state members here.
        using var vm = new ChatViewModel();

        Assert.IsTrue(vm.IsThreadEmpty);
        Assert.IsTrue(vm.IsComposerEmpty);
        Assert.IsFalse(vm.IsHistoryOpen);
        Assert.AreEqual(3, vm.Suggestions.Count);
        Assert.IsTrue(vm.Models.Count > 0);
        Assert.AreEqual(vm.Models[0], vm.SelectedModel);
        CollectionAssert.AreEqual(ExpectedModes, vm.Modes);
        Assert.AreEqual("Agent", vm.SelectedMode);
    }

    [TestMethod]
    public async Task ChatViewModel_RefreshReadyState_LoadsModelsBeforeAccountUiNotificationCompletes()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo { Id = "gpt-5-codex" },
                    new ModelInfo { Id = "gpt-5" },
                ],
                DefaultModel = "gpt-5",
            },
        };
        using var accountUpdateStarted = new ManualResetEventSlim();
        using var releaseAccountUpdate = new ManualResetEventSlim();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        vm.AccountCommand.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(AsyncCommand.CanExecute), StringComparison.Ordinal))
            {
                accountUpdateStarted.Set();
                releaseAccountUpdate.Wait();
            }
        };

        Task refresh = Task.Run(() => vm.RefreshReadyStateAsync(reloadThreads: false));
        try
        {
            Assert.IsTrue(accountUpdateStarted.Wait(TimeSpan.FromSeconds(5)), "The account UI update did not start.");
            Assert.AreEqual(1, bridge.ModelListCallCount);
            CollectionAssert.AreEqual(ExpectedWorkerModels, vm.Models);
            Assert.AreEqual("gpt-5", vm.SelectedModel);
        }
        finally
        {
            releaseAccountUpdate.Set();
        }

        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ChatViewModel_PopulateModels_UsesWorkerModelsAndDefault()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo { Id = "gpt-5-codex" },
                    new ModelInfo { Id = "gpt-5" },
                ],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();

        CollectionAssert.AreEqual(ExpectedWorkerModels, vm.Models);
        Assert.AreEqual("gpt-5", vm.SelectedModel);
    }

    [TestMethod]
    public async Task ChatViewModel_PopulateModels_AddsDefaultModelMissingFromPickerList()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo { Id = "gpt-5-codex" },
                    new ModelInfo { Id = "gpt-5" },
                ],
                DefaultModel = "gpt-5.1-codex-max",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();

        CollectionAssert.AreEqual(ExpectedModelsWithInjectedDefault, vm.Models);
        Assert.AreEqual("gpt-5.1-codex-max", vm.SelectedModel);
    }

    [TestMethod]
    public async Task ChatViewModel_PopulateModels_KeepsFallbackSeedsWhenWorkerReturnsEmpty()
    {
        var bridge = new FakeWorkerBridge { ModelListResult = new ListModelsResult() };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        string[] originalModels = vm.Models.ToArray();
        string? originalSelection = vm.SelectedModel;

        await vm.PopulateModelsAsync();

        CollectionAssert.AreEqual(originalModels, vm.Models);
        Assert.AreEqual(originalSelection, vm.SelectedModel);
    }

    [TestMethod]
    public async Task ChatViewModel_PopulateModels_RefreshNeverInvalidatesSelection()
    {
        // Remote UI mirrors every collection change to a VS-process proxy where the TwoWay
        // SelectedItem binding writes null back if the selection ever leaves the list. The
        // refresh must therefore never reset the list, and whenever an entry is removed the
        // current selection must already point at a surviving entry.
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo { Id = "gpt-5.5" },
                    new ModelInfo { Id = "gpt-5.4" },
                    new ModelInfo { Id = "gpt-5.4-mini" },
                ],
                DefaultModel = "gpt-5.5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        var violations = new List<string>();
        vm.Models.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                violations.Add("Reset raised");
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove
                && (vm.SelectedModel is null || !vm.Models.Contains(vm.SelectedModel)))
            {
                violations.Add($"Selection '{vm.SelectedModel}' invalid after removing '{e.OldItems![0]}'");
            }
        };

        await vm.PopulateModelsAsync();

        CollectionAssert.AreEqual(ExpectedRefreshedModels, vm.Models);
        Assert.AreEqual("gpt-5.5", vm.SelectedModel);
        Assert.AreEqual(0, violations.Count, string.Join("; ", violations));
    }

    [TestMethod]
    public void ChatViewModel_SelectedModel_IgnoresNullWriteBackWhileModelsExist()
    {
        // The VS-side ComboBox can write null back through the TwoWay binding while the list
        // is being refreshed; that late write-back must not blank a valid selection.
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);
        string? before = vm.SelectedModel;
        Assert.IsNotNull(before);

        vm.SelectedModel = null;

        Assert.AreEqual(before, vm.SelectedModel);
    }

    [TestMethod]
    public void ChatViewModel_SelectedModel_AllowsNullWhenCurrentSelectionAlreadyInvalid()
    {
        // If the current selection no longer exists in Models, a null is not a stale
        // write-back to guard against -- it's a legitimate clear, and must go through so the
        // VM does not get stuck holding a value the picker can no longer display.
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);
        vm.Models.Clear();
        Assert.IsFalse(vm.Models.Contains(vm.SelectedModel!));

        vm.SelectedModel = null;

        Assert.IsNull(vm.SelectedModel);
    }

    [TestMethod]
    public async Task ChatViewModel_PopulateModels_ReordersExistingEntriesToMatchCatalogOrder()
    {
        // The merge must reorder entries that already exist in Models (not just insert new
        // ones), so the dropdown order tracks the app-server catalog order across refreshes.
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo { Id = "gpt-5-codex" },
                    new ModelInfo { Id = "gpt-5" },
                ],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        CollectionAssert.AreEqual(ExpectedWorkerModels, vm.Models);

        bridge.ModelListResult = new ListModelsResult
        {
            Models =
            [
                new ModelInfo { Id = "gpt-5" },
                new ModelInfo { Id = "gpt-5-codex" },
                new ModelInfo { Id = "gpt-5-mini" },
            ],
            DefaultModel = "gpt-5",
        };

        await vm.PopulateModelsAsync();

        CollectionAssert.AreEqual(ExpectedReorderedModels, vm.Models);
        Assert.AreEqual("gpt-5", vm.SelectedModel);
    }

    [TestMethod]
    [DataRow("Agent", null)]
    [DataRow("Chat", "never")]
    [DataRow("Unknown", null)]
    public void ChatViewModel_MapModeToApprovalPolicy_MapsSupportedModes(string mode, string? expected)
    {
        Assert.AreEqual(expected, ChatViewModel.MapModeToApprovalPolicy(mode));
    }

    [TestMethod]
    [DataRow("Agent", null)]
    [DataRow("Chat", "readOnly")]
    [DataRow("Unknown", null)]
    public void ChatViewModel_MapModeToSandbox_MapsSupportedModes(string mode, string? expected)
    {
        Assert.AreEqual(expected, ChatViewModel.MapModeToSandbox(mode));
    }

    [TestMethod]
    public async Task ChatViewModel_SendMessage_PassesSelectedModelAndModeToStartTurn()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            SelectedModel = "gpt-5",
            SelectedMode = "Chat",
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.AreEqual("thread-1", bridge.LastStartTurnRequest!.ThreadId);
        Assert.AreEqual("hello", bridge.LastStartTurnRequest.Text);
        Assert.AreEqual("gpt-5", bridge.LastStartTurnRequest.Model);
        Assert.AreEqual("never", bridge.LastStartTurnRequest.ApprovalPolicy);
        Assert.AreEqual("readOnly", bridge.LastStartTurnRequest.SandboxMode);
    }

    [TestMethod]
    public async Task ChatViewModel_SendMessage_UsesAgentPresetForAgentMode()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            SelectedModel = "gpt-5-codex",
            SelectedMode = "Agent",
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.AreEqual("gpt-5-codex", bridge.LastStartTurnRequest!.Model);
        Assert.IsNull(bridge.LastStartTurnRequest.ApprovalPolicy);
        Assert.IsNull(bridge.LastStartTurnRequest.SandboxMode);
    }

    [TestMethod]
    public async Task ChatViewModel_StatusSlashCommand_DoesNotSteerActiveTurn()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            ComposerText = "/status",
        };
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "turn-1",
        });

        await InvokeComposerSendAsync(vm);

        Assert.IsNull(bridge.LastSteerTurnRequest);
        Assert.AreEqual(1, bridge.RateLimitCallCount);
        Assert.AreEqual(string.Empty, vm.ComposerText);
    }

    [TestMethod]
    public async Task ChatViewModel_DoubleSlashSteersLiteralSlashPrompt()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            ComposerText = "//status",
        };
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "turn-1",
        });

        await InvokeComposerSendAsync(vm);

        Assert.IsNotNull(bridge.LastSteerTurnRequest);
        Assert.AreEqual("/status", bridge.LastSteerTurnRequest!.Text);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanWithPrompt_StartsPlanModeTurn()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            SelectedModel = "gpt-5",
            ComposerText = "/plan design the change",
        };
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
        });

        await InvokeComposerSendAsync(vm);

        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.AreEqual("design the change", bridge.LastStartTurnRequest!.Text);
        Assert.IsNotNull(bridge.LastStartTurnRequest.CollaborationMode);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest.CollaborationMode!.Mode);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanWithoutPrompt_AppliesOnlyToNextSuccessfulTurn()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            ComposerText = "/plan",
        };
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
        });

        await InvokeComposerSendAsync(vm);
        await SendMessageAsync(vm, "first", clearComposer: false);

        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.IsNotNull(bridge.LastStartTurnRequest!.CollaborationMode);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest.CollaborationMode!.Mode);

        await SendMessageAsync(vm, "second", clearComposer: false);

        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.IsNull(bridge.LastStartTurnRequest!.CollaborationMode);
    }

    [TestMethod]
    public async Task ChatViewModel_QueuedReviewRunsAfterTurnCompletionWithoutSteer()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            ComposerText = "/review uncommitted",
        };
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "turn-1",
        });

        await InvokeComposerSendAsync(vm);
        Assert.AreEqual(0, bridge.ReviewCallCount);
        Assert.IsNull(bridge.LastSteerTurnRequest);

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
        });
        await Task.Delay(100);

        Assert.AreEqual(1, bridge.ReviewCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_NextTurnSettings_ApplyOnlyToNextSuccessfulTurn()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo
                    {
                        Id = "gpt-5",
                        SupportsPersonality = true,
                        SupportedReasoningEfforts = [new ReasoningEffortInfo { Id = "high" }],
                        ServiceTiers = [new ServiceTierInfo { Id = "fast" }],
                    },
                ],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });

        vm.ComposerText = "/fast";
        await InvokeComposerSendAsync(vm);
        vm.ComposerText = "/reasoning high";
        await InvokeComposerSendAsync(vm);
        vm.ComposerText = "/personality friendly";
        await InvokeComposerSendAsync(vm);

        await SendMessageAsync(vm, "first", clearComposer: false);

        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.AreEqual("fast", bridge.LastStartTurnRequest!.ServiceTier);
        Assert.AreEqual("high", bridge.LastStartTurnRequest.Effort);
        Assert.AreEqual("friendly", bridge.LastStartTurnRequest.Personality);

        await SendMessageAsync(vm, "second", clearComposer: false);

        Assert.IsNull(bridge.LastStartTurnRequest!.ServiceTier);
        Assert.IsNull(bridge.LastStartTurnRequest.Effort);
        Assert.IsNull(bridge.LastStartTurnRequest.Personality);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanWithPrompt_ClearsPendingPlanMode()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });

        vm.ComposerText = "/plan";
        await InvokeComposerSendAsync(vm);
        vm.ComposerText = "/plan design the change";
        await InvokeComposerSendAsync(vm);

        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);

        await SendMessageAsync(vm, "after the plan turn", clearComposer: false);

        Assert.IsNull(bridge.LastStartTurnRequest!.CollaborationMode);
    }

    [TestMethod]
    public async Task ChatViewModel_QueueContinuesDrainingAfterFailedCommand()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            SelectedModel = "gpt-5-codex",
        };
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "turn-1",
        });

        // Feedback fails on drain (no extensibility to confirm the prompt); /model must still run.
        vm.ComposerText = "/feedback the composer lost my draft";
        await InvokeComposerSendAsync(vm);
        vm.ComposerText = "/model gpt-5";
        await InvokeComposerSendAsync(vm);
        Assert.AreEqual("gpt-5-codex", vm.SelectedModel);

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await Task.Delay(100);

        Assert.AreEqual("gpt-5", vm.SelectedModel);
    }

    [TestMethod]
    public async Task ChatViewModel_SessionQueuedCommandRunsAfterTurnCompletes()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedModel = "gpt-5-codex",
        };
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            TurnId = "turn-1",
        });

        // No selected thread and no status thread id: the command lands in the session queue.
        vm.ComposerText = "/model gpt-5";
        await InvokeComposerSendAsync(vm);
        Assert.AreEqual("gpt-5-codex", vm.SelectedModel);

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        await Task.Delay(100);

        Assert.AreEqual("gpt-5", vm.SelectedModel);
    }

    [TestMethod]
    public async Task ChatViewModel_SelectedThreadQueueDrainsWhenOtherThreadTurnCompletes()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "turn-1",
        });
        vm.SelectedThread = new ThreadSummary { Id = "thread-2" };

        vm.ComposerText = "/review uncommitted";
        await InvokeComposerSendAsync(vm);
        Assert.AreEqual(0, bridge.ReviewCallCount);

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await Task.Delay(100);

        Assert.AreEqual(1, bridge.ReviewCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_ForkResumesForkedThreadHistory()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });

        vm.ComposerText = "/fork";
        await InvokeComposerSendAsync(vm);
        await Task.Delay(100);

        Assert.AreEqual("thread-fork", vm.SelectedThread!.Id);
        Assert.AreEqual("thread-fork", bridge.LastResumedThreadId);
    }

    [TestMethod]
    public async Task ChatViewModel_ModelCommandMatchesCatalogCaseInsensitively()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedModel = "gpt-5-codex",
        };

        vm.ComposerText = "/model GPT-5";
        await InvokeComposerSendAsync(vm);

        Assert.AreEqual("gpt-5", vm.SelectedModel);
    }

    [TestMethod]
    [DataRow("my-app", "my_app")]
    [DataRow("123", "_123")]
    [DataRow("---", "App")]
    [DataRow("valid_Name9", "valid_Name9")]
    public void ProjectScaffolder_GetProjectName_ReturnsIdentifierLikeName(string folderName, string expected)
    {
        MethodInfo? getProjectName = typeof(ProjectScaffolder).GetMethod(
            "GetProjectName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(getProjectName);
        string rootDirectory = Path.Combine(Path.GetTempPath(), folderName);
        Assert.AreEqual(expected, getProjectName.Invoke(null, [rootDirectory]));
    }

    [TestMethod]
    public void ChatViewModel_IsThreadEmpty_TracksItems()
    {
        using var vm = new ChatViewModel();
        Assert.IsTrue(vm.IsThreadEmpty);

        vm.Items.Add(new ChatItemViewModel("You", "hello", ConversationEventKind.ItemStarted));
        Assert.IsFalse(vm.IsThreadEmpty);

        vm.Items.Clear();
        Assert.IsTrue(vm.IsThreadEmpty);
    }

    [TestMethod]
    public async Task ChatViewModel_ToggleHistoryCommand_TogglesIsHistoryOpen()
    {
        using var vm = new ChatViewModel();
        Assert.IsFalse(vm.IsHistoryOpen);

        vm.ToggleHistoryCommand.Execute(null);
        await Task.Delay(50);
        Assert.IsTrue(vm.IsHistoryOpen);

        vm.ToggleHistoryCommand.Execute(null);
        await Task.Delay(50);
        Assert.IsFalse(vm.IsHistoryOpen);
    }

    [TestMethod]
    public void ChatViewModel_TypingComposerText_DoesNotEchoComposerTextPropertyChanged()
    {
        // Regression: the binding-driven (user-typing) setter must NOT raise PropertyChanged for
        // ComposerText. In Remote UI that notification is echoed back to the TextBox across the
        // process boundary and resets the caret to position 0 on every keystroke. IsComposerEmpty
        // must still update so the "Ask Codex" placeholder hides.
        using var vm = new ChatViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ComposerText = "hello";

        Assert.AreEqual("hello", vm.ComposerText);
        Assert.IsFalse(
            raised.Contains("ComposerText"),
            "Typing must not echo ComposerText PropertyChanged (would reset the caret in Remote UI).");
        Assert.IsTrue(raised.Contains("IsComposerEmpty"), "Placeholder visibility must still update.");
        Assert.IsFalse(vm.IsComposerEmpty);
    }

    [TestMethod]
    public void ChatViewModel_LeadingSlash_OpensEightSlashCommandSuggestions()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

        vm.ComposerText = "/";

        Assert.IsTrue(vm.SlashCommands.IsSuggestionOpen);
        Assert.HasCount(8, vm.SlashCommands.Suggestions);
        Assert.AreEqual("/compact", vm.SlashCommands.SelectedSuggestion?.CommandName);
    }

    [TestMethod]
    public void ChatViewModel_Dispose_StartsWorkerBridgeCleanupOnce()
    {
        var bridge = new FakeWorkerBridge();
        var vm = new ChatViewModel(bridge, autoConnect: false);

        vm.Dispose();
        vm.Dispose();

        Assert.AreEqual(1, bridge.DisposeCallCount);
    }

    [TestMethod]
    public void ChatViewModel_SlashCommandPrefix_FiltersReasoningAndReview()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

        vm.ComposerText = "/re";

        Assert.IsTrue(vm.SlashCommands.IsSuggestionOpen);
        Assert.HasCount(2, vm.SlashCommands.Suggestions);
        Assert.AreEqual("/reasoning", vm.SlashCommands.Suggestions[0].CommandName);
        Assert.AreEqual("/review", vm.SlashCommands.Suggestions[1].CommandName);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("hello")]
    [DataRow("hello /status")]
    [DataRow("//")]
    public void ChatViewModel_NonCommandComposerText_ClosesSlashCommandSuggestions(string text)
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false)
        {
            ComposerText = "/",
        };
        Assert.IsTrue(vm.SlashCommands.IsSuggestionOpen);

        vm.ComposerText = text;

        Assert.IsFalse(vm.SlashCommands.IsSuggestionOpen);
        Assert.IsNull(vm.SlashCommands.SelectedSuggestion);
        Assert.IsFalse(vm.SlashCommands.HasStatusAnnouncement);
    }

    [TestMethod]
    public void ChatViewModel_LeadingSlash_RaisesNestedPresentationNotifications()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);
        var slashPropertyChanges = new List<string?>();
        var suggestionCollectionChanges = new List<NotifyCollectionChangedAction>();
        vm.SlashCommands.PropertyChanged += (_, eventArgs) =>
        {
            slashPropertyChanges.Add(eventArgs.PropertyName);
        };
        vm.SlashCommands.Suggestions.CollectionChanged += (_, eventArgs) =>
        {
            suggestionCollectionChanges.Add(eventArgs.Action);
        };

        vm.ComposerText = "/";

        CollectionAssert.Contains(
            slashPropertyChanges,
            nameof(SlashCommandPresentationViewModel.SelectedSuggestion));
        CollectionAssert.Contains(
            slashPropertyChanges,
            nameof(SlashCommandPresentationViewModel.IsSuggestionOpen));
        Assert.AreEqual(NotifyCollectionChangedAction.Reset, suggestionCollectionChanges[0]);
        Assert.AreEqual(
            8,
            suggestionCollectionChanges.Count(static action => action == NotifyCollectionChangedAction.Add));
        Assert.IsTrue(vm.SlashCommands.IsSuggestionOpen);
        Assert.HasCount(8, vm.SlashCommands.Suggestions);
    }

    [TestMethod]
    public async Task ChatViewModel_SuggestionChip_RaisesComposerTextPropertyChanged()
    {
        // The programmatic path (suggestion chip / clear-after-send) DOES notify ComposerText so
        // the TextBox reflects the new value; the caret-reset concern does not apply off the
        // typing path.
        using var vm = new ChatViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Suggestions[0].UseCommand.Execute(null);
        await Task.Delay(50);

        Assert.AreEqual(vm.Suggestions[0].Text, vm.ComposerText);
        Assert.IsTrue(
            raised.Contains("ComposerText"),
            "Programmatic composer updates must notify so the TextBox reflects the new value.");
    }

    [TestMethod]
    public void DataMemberCommands_ImplementIAsyncCommand()
    {
        // Remote UI rejects plain ICommand values at serialization time
        // ("ICommand is not supported, please implement IAsyncCommand instead").
        foreach (Type type in RemoteUiContextTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetCustomAttribute<DataMemberAttribute>() is null)
                    continue;
                if (!typeof(ICommand).IsAssignableFrom(property.PropertyType))
                    continue;

                Assert.IsTrue(
                    typeof(IAsyncCommand).IsAssignableFrom(property.PropertyType),
                    $"{type.Name}.{property.Name} is a serialized command and must implement IAsyncCommand for Remote UI.");
            }
        }
    }

    private static Task SendMessageAsync(ChatViewModel viewModel, string text, bool clearComposer)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "SendMessageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find SendMessageAsync.");

        return (Task)method.Invoke(viewModel, [text, clearComposer])!;
    }

    private static Task InvokeComposerSendAsync(ChatViewModel viewModel)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "SendAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find SendAsync.");

        return (Task)method.Invoke(viewModel, null)!;
    }

    private static Task RaiseConversationEventAsync(ChatViewModel viewModel, ConversationEvent value)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "OnConversationEventAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find OnConversationEventAsync.");

        return (Task)method.Invoke(viewModel, [value])!;
    }

    private static ExtensionSettings GetSettings(ChatViewModel viewModel)
    {
        FieldInfo field = typeof(ChatViewModel).GetField(
            "settings",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find settings.");

        return (ExtensionSettings)field.GetValue(viewModel)!;
    }

    private static ChatItemViewModel SingleConversationItem(
        ChatViewModel viewModel,
        string itemId,
        ConversationEventKind kind)
        => viewModel.Items.Single(item => item.ItemId == itemId && item.Kind == kind);

    private sealed class FakeWorkerBridge : IWorkerBridge
    {
        public event Func<WorkerStatus, Task>? StateChanged;

        public event Func<AccountStatus, Task>? AccountChanged { add { } remove { } }

        public event Func<ConversationEvent, Task>? ConversationEventReceived { add { } remove { } }

        public event Func<ApprovalRequest, Task>? ApprovalRequested { add { } remove { } }

        public event Func<string, Task>? ApprovalResolved { add { } remove { } }

        public event Func<UserInputRequest, Task>? UserInputRequested { add { } remove { } }

        public event Func<string, Task>? UserInputResolved { add { } remove { } }

        public event Func<ContextCompactionEvent, Task>? ContextCompacted { add { } remove { } }

        public event Func<ReviewModeEvent, Task>? ReviewModeChanged { add { } remove { } }

        public event Func<ThreadGoalEvent, Task>? ThreadGoalChanged { add { } remove { } }

        public event Func<RateLimitsResult, Task>? RateLimitsChanged { add { } remove { } }

        public ListModelsResult ModelListResult { get; set; } = new();

        public int ModelListCallCount { get; private set; }

        public StartTurnRequest? LastStartTurnRequest { get; private set; }

        public SteerTurnRequest? LastSteerTurnRequest { get; private set; }

        public int ReviewCallCount { get; private set; }

        public int RateLimitCallCount { get; private set; }

        public Exception? ResolveApprovalException { get; set; }

        public Exception? ResolveUserInputException { get; set; }

        public Task PublishStateAsync(WorkerStatus status)
            => StateChanged?.Invoke(status) ?? Task.CompletedTask;

        public Task<WorkerStatus> ConnectAsync(string workingDirectory, bool experimentalApi, CancellationToken cancellationToken)
            => Task.FromResult(new WorkerStatus { State = WorkerConnectionState.Ready });

        public Task<WorkerStatus> RestartAsync(CancellationToken cancellationToken)
            => Task.FromResult(new WorkerStatus { State = WorkerConnectionState.Ready });

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AccountStatus { State = AccountState.SignedIn });

        public Task<StartAccountLoginResult> StartAccountLoginAsync(CancellationToken cancellationToken)
            => Task.FromResult(new StartAccountLoginResult());

        public Task<AccountStatus> LogoutAccountAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AccountStatus { State = AccountState.SignedOut });

        public Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken)
            => Task.FromResult(new ThreadPage());

        public Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken)
        {
            ModelListCallCount++;
            return Task.FromResult(ModelListResult);
        }

        public Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ThreadSummary { Id = "thread-1" });

        public string? LastResumedThreadId { get; private set; }

        public Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken)
        {
            LastResumedThreadId = threadId;
            return Task.FromResult(new ThreadSummary { Id = threadId });
        }

        public Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
        {
            LastStartTurnRequest = request;
            return Task.FromResult("turn-1");
        }

        public Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken)
        {
            LastSteerTurnRequest = request;
            return Task.FromResult(request.ExpectedTurnId);
        }

        public Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CompactThreadResult> CompactThreadAsync(CompactThreadRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new CompactThreadResult());

        public Task<StartReviewResult> StartReviewAsync(StartReviewRequest request, CancellationToken cancellationToken)
        {
            ReviewCallCount++;
            return Task.FromResult(new StartReviewResult { TurnId = "turn-review" });
        }

        public Task<ForkThreadResult> ForkThreadAsync(ForkThreadRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ForkThreadResult { Thread = new ThreadSummary { Id = "thread-fork" } });

        public Task<ThreadGoalResult> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken)
            => Task.FromResult(new ThreadGoalResult());

        public Task<ThreadGoalResult> SetThreadGoalAsync(SetThreadGoalRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ThreadGoalResult
            {
                Goal = new ThreadGoalInfo
                {
                    ThreadId = request.ThreadId,
                    Objective = request.Objective ?? string.Empty,
                    Status = request.Status ?? ThreadGoalStatus.Active,
                },
            });

        public Task<ThreadGoalResult> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken)
            => Task.FromResult(new ThreadGoalResult { Cleared = true });

        public Task<McpServerListResult> ListMcpServersAsync(string? threadId, CancellationToken cancellationToken)
            => Task.FromResult(new McpServerListResult());

        public Task<UploadFeedbackResult> UploadFeedbackAsync(UploadFeedbackRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new UploadFeedbackResult { ThreadId = request.ThreadId });

        public Task<RateLimitsResult> GetRateLimitsAsync(CancellationToken cancellationToken)
        {
            RateLimitCallCount++;
            return Task.FromResult(new RateLimitsResult());
        }

        public Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
            => ResolveApprovalException is not null ? Task.FromException(ResolveApprovalException) : Task.CompletedTask;

        public Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken)
            => ResolveUserInputException is not null ? Task.FromException(ResolveUserInputException) : Task.CompletedTask;

        public int DisposeCallCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }
}
