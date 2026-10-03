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
    private static readonly string[] ExpectedApprovalModes = ["ask", "auto", "full", "custom"];
    private static readonly string[] ExpectedWorkerModels = ["gpt-5-codex", "gpt-5"];
    private static readonly string[] ExpectedModelsWithInjectedDefault = ["gpt-5.1-codex-max", "gpt-5-codex", "gpt-5"];
    private static readonly string[] ExpectedRefreshedModels = ["gpt-5.5", "gpt-5.4", "gpt-5.4-mini"];
    private static readonly string[] ExpectedReorderedModels = ["gpt-5", "gpt-5-codex", "gpt-5-mini"];
    private static readonly string[] ExpectedStatusHeaderColumnWidths = ["Auto", "*"];
    private static readonly string[] CreativeOnly = ["Creative"];

    [TestMethod]
    public async Task ChatViewModel_OwnerChangeClearsOwnerScopedPresentationState()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"codex owner {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string attachmentPath = Path.Combine(directory, "draft.txt");
        await File.WriteAllTextAsync(attachmentPath, "private draft");

        try
        {
            var bridge = new FakeWorkerBridge();
            using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
            await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));

            var thread = new ThreadSummary { Id = "thread-a", Preview = "Previous owner" };
            vm.Threads.Add(thread);
            vm.SelectedThread = thread;
            await Task.Delay(20);
            vm.Items.Add(new ChatItemViewModel("Codex", "Previous owner's history", ConversationEventKind.AgentMessageDelta));
            vm.ComposerText = "unsent owner draft";
            vm.Models.Add("owner-a-model");
            vm.SelectedModel = "owner-a-model";
            vm.PendingAttachments.Add(new AttachmentChipViewModel(
                attachmentPath,
                new SafeMarkdownService(),
                _ => Task.CompletedTask));
            vm.PendingSkills.Add(new PendingSkillViewModel(
                "owner-a-skill",
                "Repository",
                "Owner-specific skill",
                new SkillInvocationInfo { Name = "owner-a-skill", Scope = "repo", Path = "/srv/a/skill" },
                () => Task.CompletedTask,
                null,
                _ => Task.CompletedTask,
                new SafeMarkdownService()));
            vm.Usage.Update(new RateLimitsResult(), DateTimeOffset.UtcNow, new SafeMarkdownService());
            vm.IsHistoryOpen = true;

            MethodInfo approvalRequested = typeof(ChatViewModel).GetMethod(
                "OnApprovalRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)approvalRequested.Invoke(vm, [Notification(MakeApprovalRequest("owner-a-approval"), OwnerStatus("owner-a", 1, 4))])!;
            Assert.IsTrue(vm.HasActiveApproval);

            await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));

            Assert.IsNull(vm.SelectedThread);
            Assert.AreEqual(0, vm.Threads.Count);
            Assert.AreEqual(0, vm.Items.Count);
            Assert.AreEqual(string.Empty, vm.ComposerText);
            Assert.AreEqual(0, vm.PendingAttachments.Count);
            Assert.AreEqual(0, vm.PendingSkills.Count);
            Assert.IsFalse(vm.HasActiveApproval);
            Assert.AreEqual(string.Empty, vm.ApprovalQueueText);
            Assert.IsFalse(vm.IsHistoryOpen);
            Assert.IsFalse(vm.Usage.HasData);
            CollectionAssert.AreEqual(ExpectedWorkerModels, vm.Models.ToArray());
            Assert.AreEqual("gpt-5-codex", vm.SelectedModel);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChatViewModel_DiscardsThreadPageCompletedForPreviousOwner()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));

        var response = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The Worker fake intentionally holds this request until the owner changes.
        bridge.ListThreadsHandler = (_, _) => response.Task;
#pragma warning restore VSTHRD003
        MethodInfo loadMore = typeof(ChatViewModel).GetMethod("LoadMoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Task loading = (Task)loadMore.Invoke(vm, null)!;

        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));
        response.SetResult(new ThreadPage { Threads = [new ThreadSummary { Id = "old-owner-thread" }] });
        await loading;

        Assert.AreEqual(0, vm.Threads.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_DiscardsLogoutCompletedForPreviousOwner()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));

        var response = new TaskCompletionSource<AccountStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The Worker fake intentionally holds this request until the owner changes.
        bridge.LogoutHandler = (_, _) => response.Task;
#pragma warning restore VSTHRD003
        MethodInfo signOut = typeof(ChatViewModel).GetMethod("SignOutAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Task logout = (Task)signOut.Invoke(vm, null)!;
        for (int attempt = 0; attempt < 100 && bridge.LastLogoutRequest is null; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.IsNotNull(bridge.LastLogoutRequest);
        Assert.AreEqual("owner-a", bridge.LastLogoutRequest!.StatePartitionFingerprint);
        Assert.AreEqual(1, bridge.LastLogoutRequest.OwnerGeneration);
        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));
        response.SetResult(new AccountStatus { State = AccountState.SignedOut });
#pragma warning disable VSTHRD003 // The test is waiting for the reflected ViewModel operation started above.
        await logout;
#pragma warning restore VSTHRD003

        Assert.AreEqual(AccountState.Checking, vm.Account.State);
    }

    [TestMethod]
    public async Task ChatViewModel_DiscardsSyntheticChoiceFromPreviousOwner()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        object oldOwner = CaptureOwnerSnapshot(vm);
        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));

        MethodInfo resolve = typeof(ChatViewModel).GetMethod("ResolveSyntheticUserInputAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var answers = new Dictionary<string, string[]> { ["choice"] = ["old owner answer"] };
        await (Task)resolve.Invoke(vm, ["synthetic-old", answers, oldOwner])!;

        Assert.IsNull(bridge.LastStartTurnRequest);
        Assert.IsFalse(vm.HasActiveUserInput);
        Assert.AreEqual(string.Empty, vm.ComposerText);
    }

    private static WorkerStatus OwnerStatus(string fingerprint, long ownerGeneration, long connectionGeneration)
        => new()
        {
            State = WorkerConnectionState.Ready,
            Target = new ConnectionTargetSnapshot
            {
                Kind = ConnectionTargetKind.Local,
                Generation = connectionGeneration,
                StatePartitionFingerprint = fingerprint,
                OwnerGeneration = ownerGeneration,
            },
        };

    private static WorkerNotification<T> Notification<T>(T value, WorkerStatus? status = null)
        => new()
        {
            Value = value,
            StatePartitionFingerprint = status?.Target?.StatePartitionFingerprint,
            OwnerGeneration = status?.Target?.OwnerGeneration ?? 0,
            ConnectionGeneration = status?.Target?.Generation ?? 0,
        };

    private static object CaptureOwnerSnapshot(ChatViewModel viewModel)
        => typeof(ChatViewModel).GetMethod("CaptureOwnerSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, null)!;

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

        await (Task)requested.Invoke(vm, [Notification(MakeUserInputRequest("ui-1"))])!;
        Assert.IsTrue(vm.HasActiveUserInput);
        Assert.AreEqual("ui-1", vm.ActiveUserInput!.RequestId);
        Assert.AreEqual(string.Empty, vm.UserInputQueueText);

        // Second request is queued, not shown — the active card stays put.
        await (Task)requested.Invoke(vm, [Notification(MakeUserInputRequest("ui-2"))])!;
        Assert.AreEqual("ui-1", vm.ActiveUserInput!.RequestId);
        Assert.AreEqual("1 choice waiting", vm.UserInputQueueText);

        // Resolving the active one promotes the queued one.
        await (Task)resolved.Invoke(vm, [Notification("ui-1")])!;
        Assert.AreEqual("ui-2", vm.ActiveUserInput!.RequestId);
        Assert.AreEqual(string.Empty, vm.UserInputQueueText);

        // Resolving the last clears the card; a duplicate resolve is a no-op.
        await (Task)resolved.Invoke(vm, [Notification("ui-2")])!;
        Assert.IsFalse(vm.HasActiveUserInput);
        await (Task)resolved.Invoke(vm, [Notification("ui-2")])!;
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

        await (Task)requested.Invoke(vm, [Notification(MakeApprovalRequest("req-1"))])!;
        Assert.IsTrue(vm.HasActiveApproval);
        Assert.AreEqual("req-1", vm.ActiveApproval!.RequestId);
        Assert.AreEqual(string.Empty, vm.ApprovalQueueText);

        // Concurrent prompts are queued, not stacked: the active card stays put and the rest are counted.
        await (Task)requested.Invoke(vm, [Notification(MakeApprovalRequest("req-2"))])!;
        Assert.AreEqual("req-1", vm.ActiveApproval!.RequestId);
        Assert.AreEqual("1 approval waiting", vm.ApprovalQueueText);

        await (Task)requested.Invoke(vm, [Notification(MakeApprovalRequest("req-3"))])!;
        Assert.AreEqual("2 approvals waiting", vm.ApprovalQueueText);

        // Resolving the active one promotes the next queued prompt.
        await (Task)resolved.Invoke(vm, [Notification("req-1")])!;
        Assert.AreEqual("req-2", vm.ActiveApproval!.RequestId);
        Assert.AreEqual("1 approval waiting", vm.ApprovalQueueText);

        // A prompt resolved while still queued is dropped without becoming active.
        await (Task)resolved.Invoke(vm, [Notification("req-3")])!;
        Assert.AreEqual("req-2", vm.ActiveApproval!.RequestId);
        Assert.AreEqual(string.Empty, vm.ApprovalQueueText);

        // Resolving the last clears the card; a duplicate resolve is a no-op.
        await (Task)resolved.Invoke(vm, [Notification("req-2")])!;
        Assert.IsFalse(vm.HasActiveApproval);
        await (Task)resolved.Invoke(vm, [Notification("req-2")])!;
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
    public void ChoicePromptParser_DoYouWantWithoutToClause_DoesNotTriggerConfirmationCard()
    {
        string text = string.Join('\n',
            "I can help with either approach.",
            "What do you want me to do?");

        bool ok = ChoicePromptParser.TryParse(text, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void ChoicePromptParser_DoYouWantMeToClause_TriggersConfirmationCard()
    {
        string text = "Do you want me to continue with this implementation?";

        bool ok = ChoicePromptParser.TryParse(text, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(2, request.Questions[0].Options.Count);
        Assert.AreEqual("Yes", request.Questions[0].Options[0].Label);
        Assert.AreEqual("No", request.Questions[0].Options[1].Label);
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
        await (Task)requested.Invoke(vm, [Notification(new ApprovalRequest
        {
            RequestId = "req-decision",
            Risk = ApprovalRiskCategory.Destructive,
            DisplayText = "git reset --hard",
            AvailableDecisions = AcceptDeclineCancel,
        })])!;

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
        await (Task)requested.Invoke(vm, [Notification(new ApprovalRequest
        {
            RequestId = "req-fail",
            Risk = ApprovalRiskCategory.Destructive,
            DisplayText = "git reset --hard",
            AvailableDecisions = AcceptDeclineCancel,
        })])!;

        MethodInfo resolve = typeof(ChatViewModel).GetMethod(
            "ResolveApprovalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        InvalidOperationException? caught = null;
        try
        {
            await (Task)resolve.Invoke(vm, ["req-fail", ApprovalDecision.Accept, CaptureOwnerSnapshot(vm)])!;
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
        await (Task)requested.Invoke(vm, [Notification(new ApprovalRequest
        {
            RequestId = "req-ok",
            Risk = ApprovalRiskCategory.Destructive,
            DisplayText = "git push --force",
            AvailableDecisions = AcceptDeclineCancel,
        })])!;

        MethodInfo resolve = typeof(ChatViewModel).GetMethod(
            "ResolveApprovalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)resolve.Invoke(vm, ["req-ok", ApprovalDecision.Accept, CaptureOwnerSnapshot(vm)])!;

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
            await (Task)resolve.Invoke(vm, ["req-1", answers, CaptureOwnerSnapshot(vm)])!;
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
        await (Task)resolve.Invoke(vm, ["req-1", answers, CaptureOwnerSnapshot(vm)])!;

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
    [DataRow("")]
    [DataRow("one line")]
    [DataRow("one\ntwo\nthree")]
    [DataRow("one\ntwo\nthree\n")]
    public void ChatItemViewModel_ShortCommandOutput_RemainsInline(string output)
    {
        var item = new ChatItemViewModel("Command", output, ConversationEventKind.CommandOutputDelta);

        Assert.IsFalse(item.IsCommandOutputCollapsible);
        Assert.IsFalse(item.IsCommandOutputExpanded);
        Assert.AreEqual(output, item.Text);
    }

    [TestMethod]
    public void ChatItemViewModel_FourCommandLines_StartCollapsedWithThreeLinePreview()
    {
        var item = new ChatItemViewModel(
            "Command",
            "one\r\ntwo\r\nthree\r\nfour",
            ConversationEventKind.CommandOutputDelta);

        Assert.IsTrue(item.IsCommandOutputCollapsible);
        Assert.IsFalse(item.IsCommandOutputExpanded);
        Assert.AreEqual("one\r\ntwo\r\nthree", item.Text);
        Assert.AreEqual("Show 1 more line", item.CommandOutputExpansionLabel);
        Assert.AreEqual(4, item.BufferedCommandLineCount);
    }

    [TestMethod]
    public void ChatItemViewModel_CommandCrLfSplitAcrossDeltas_CountsOneLineBreak()
    {
        var item = new ChatItemViewModel("Command", "one\r", ConversationEventKind.CommandOutputDelta);

        item.AppendCommandOutput("\ntwo\r\nthree\r\nfour");

        Assert.AreEqual(4, item.BufferedCommandLineCount);
        Assert.AreEqual("one\r\ntwo\r\nthree", item.Text);
        Assert.AreEqual("Show 1 more line", item.CommandOutputExpansionLabel);
    }

    [TestMethod]
    public void ChatItemViewModel_LongSingleCommandLine_UsesBoundedPreview()
    {
        string output = new('x', ChatItemViewModel.CommandPreviewCharacterLimit + 17);
        var item = new ChatItemViewModel("Command", output, ConversationEventKind.CommandOutputDelta);

        Assert.IsTrue(item.IsCommandOutputCollapsible);
        Assert.AreEqual(ChatItemViewModel.CommandPreviewCharacterLimit, item.Text.Length);
        Assert.AreEqual("Show remaining buffered command output", item.CommandOutputExpansionLabel);
    }

    [TestMethod]
    public void ChatItemViewModel_CommandPreview_DoesNotSplitCrLfAtCharacterLimit()
    {
        string output = new string('x', ChatItemViewModel.CommandPreviewCharacterLimit - 1) + "\r\nremaining";
        var item = new ChatItemViewModel("Command", output, ConversationEventKind.CommandOutputDelta);

        Assert.IsTrue(item.IsCommandOutputCollapsible);
        Assert.AreEqual(ChatItemViewModel.CommandPreviewCharacterLimit - 1, item.Text.Length);
        Assert.IsFalse(item.Text.EndsWith('\r'));
    }

    [TestMethod]
    public void ChatItemViewModel_ExpandedCommandOutput_ProjectsFullBufferAndCanCollapseAgain()
    {
        const string output = "one\ntwo\nthree\nfour";
        var item = new ChatItemViewModel("Command", output, ConversationEventKind.CommandOutputDelta);

        item.IsCommandOutputExpanded = true;
        Assert.AreEqual(output, item.Text);
        Assert.AreEqual("Hide command output", item.CommandOutputExpansionLabel);
        Assert.AreEqual("Collapse command output", item.CommandOutputAutomationName);
        Assert.AreEqual(string.Empty, item.TruncationNotice);

        item.IsCommandOutputExpanded = false;
        Assert.AreEqual("one\ntwo\nthree", item.Text);
        Assert.AreEqual("Expand command output", item.CommandOutputAutomationName);
    }

    [TestMethod]
    public void ChatItemViewModel_CollapsedCommandOutput_DoesNotRepublishFullTextForHiddenDeltas()
    {
        var item = new ChatItemViewModel(
            "Command",
            "one\ntwo\nthree\nfour",
            ConversationEventKind.CommandOutputDelta);
        int textNotifications = 0;
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatItemViewModel.Text))
            {
                textNotifications++;
            }
        };

        item.AppendCommandOutput("\nfive");
        item.AppendCommandOutput("\nsix");

        Assert.AreEqual(0, textNotifications);
        Assert.AreEqual("one\ntwo\nthree", item.Text);
        Assert.AreEqual("Show 3 more lines", item.CommandOutputExpansionLabel);
        Assert.AreEqual(27, item.BufferedCommandCharacterCount);
    }

    [TestMethod]
    public void ChatItemViewModel_CommandBuffer_IsBoundedAndUsesTruncationSafeLabels()
    {
        string output = new('x', ChatItemViewModel.CommandBufferCharacterLimit + 1);
        var item = new ChatItemViewModel("Command", output, ConversationEventKind.CommandOutputDelta);

        Assert.AreEqual(ChatItemViewModel.CommandBufferCharacterLimit, item.BufferedCommandCharacterCount);
        Assert.IsTrue(item.IsTruncated);
        Assert.AreEqual("Show buffered command output (truncated)", item.CommandOutputExpansionLabel);
        Assert.AreEqual("Command output was truncated to the buffered limit.", item.TruncationNotice);

        item.IsCommandOutputExpanded = true;
        Assert.AreEqual("Hide buffered command output (truncated)", item.CommandOutputExpansionLabel);
        Assert.AreEqual(ChatItemViewModel.CommandBufferCharacterLimit, item.Text.Length);
    }

    [TestMethod]
    public void ChatItemViewModel_CommandBuffer_IsNotPartOfRemoteUiContract()
    {
        FieldInfo? buffer = typeof(ChatItemViewModel).GetField(
            "commandOutputBuffer",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(buffer);
        Assert.IsNull(buffer!.GetCustomAttribute<DataMemberAttribute>());
        Assert.IsNotNull(typeof(ChatItemViewModel).GetProperty(nameof(ChatItemViewModel.Text))!
            .GetCustomAttribute<DataMemberAttribute>());
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
    public async Task ChatViewModel_CommandOutputMetadataOnlyDelta_PreservesTruncationState()
    {
        using var vm = new ChatViewModel();

        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.CommandOutputDelta,
            ItemId = "command-overflow",
            Text = "visible output",
        });
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.CommandOutputDelta,
            ItemId = "command-overflow",
            Truncated = true,
            OverflowFile = "temporary-output.log",
        });

        ChatItemViewModel item = SingleConversationItem(
            vm,
            "command-overflow",
            ConversationEventKind.CommandOutputDelta);
        Assert.IsTrue(item.IsTruncated);
        Assert.IsTrue(item.IsCommandOutputCollapsible);
        Assert.AreEqual("Show buffered command output (truncated)", item.CommandOutputExpansionLabel);
        Assert.AreEqual("Output truncated; additional output is stored in a temporary file.", item.TruncationNotice);
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
        Assert.IsTrue(xaml.Contains("ItemsSource=\"{Binding ServiceTiers}\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("SelectedValue=\"{Binding SelectedServiceTierId, Mode=TwoWay}\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("AutomationProperties.Name=\"Speed\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("ItemsSource=\"{Binding ReasoningEfforts}\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("SelectedValue=\"{Binding SelectedReasoningEffortId, Mode=TwoWay}\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("{Binding Account.", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(WorkerConnectionState.Ready)]
    [DataRow(WorkerConnectionState.Busy)]
    [DataRow(WorkerConnectionState.WaitingForApproval)]
    public async Task ChatViewModel_StatusHeader_ShowsSanitizedCodexVersion(WorkerConnectionState state)
    {
        var bridge = new FakeWorkerBridge();
        using var viewModel = new ChatViewModel(bridge, autoConnect: false);

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = state,
            Message = "  Connected\r\n\u001b[31msafely\u001b[0m.  ",
            CodexVersion = "  **0.42.0**\r\npreview\u0001  ",
        });

        Assert.AreEqual(state.ToString(), viewModel.StatusStateText);
        Assert.AreEqual("\u00b7 Codex 0.42.0 preview", viewModel.StatusVersionText);
        Assert.AreEqual($"{state}, Codex version 0.42.0 preview", viewModel.StatusAutomationName);
        Assert.AreEqual("Connected safely.", viewModel.StatusAutomationHelpText);
    }

    [TestMethod]
    [DataRow(WorkerConnectionState.Disconnected)]
    [DataRow(WorkerConnectionState.Connecting)]
    [DataRow(WorkerConnectionState.Degraded)]
    public async Task ChatViewModel_StatusHeader_HidesCodexVersionOutsideConnectedStates(WorkerConnectionState state)
    {
        var bridge = new FakeWorkerBridge();
        using var viewModel = new ChatViewModel(bridge, autoConnect: false);

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = state,
            CodexVersion = "0.42.0",
        });

        Assert.AreEqual(string.Empty, viewModel.StatusVersionText);
        Assert.AreEqual(state.ToString(), viewModel.StatusAutomationName);
    }

    [TestMethod]
    public async Task ChatViewModel_StatusHeader_HidesBlankCodexVersion()
    {
        var bridge = new FakeWorkerBridge();
        using var viewModel = new ChatViewModel(bridge, autoConnect: false);

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            CodexVersion = " \r\n\t ",
        });

        Assert.AreEqual(string.Empty, viewModel.StatusVersionText);
        Assert.AreEqual("Ready", viewModel.StatusAutomationName);
        Assert.AreEqual("Codex connection status.", viewModel.StatusAutomationHelpText);
    }

    [TestMethod]
    public void ChatToolWindowXaml_StatusHeader_PreservesStateAndProvidesOneLiveRegion()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XName automationName = XName.Get("AutomationProperties.Name");
        XName automationHelpText = XName.Get("AutomationProperties.HelpText");
        XName automationLiveSetting = XName.Get("AutomationProperties.LiveSetting");

        XElement stateText = doc
            .Descendants(presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value == "{Binding StatusStateText}");
        XElement versionText = doc
            .Descendants(presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value == "{Binding StatusVersionText}");
        XElement header = stateText.Parent!;

        Assert.AreEqual("0", stateText.Attribute("Grid.Column")?.Value);
        Assert.AreEqual("1", versionText.Attribute("Grid.Column")?.Value);
        CollectionAssert.AreEqual(
            ExpectedStatusHeaderColumnWidths,
            header
                .Element(presentation + "Grid.ColumnDefinitions")!
                .Elements(presentation + "ColumnDefinition")
                .Select(column => column.Attribute("Width")?.Value)
                .ToArray());
        Assert.IsNull(stateText.Attribute("TextTrimming"), "The connection state must remain visible.");
        Assert.AreEqual("CharacterEllipsis", versionText.Attribute("TextTrimming")?.Value);
        Assert.AreEqual("{Binding StatusAutomationName}", stateText.Attribute(automationName)?.Value);
        Assert.AreEqual("{Binding StatusAutomationHelpText}", stateText.Attribute(automationHelpText)?.Value);
        Assert.AreEqual("Polite", stateText.Attribute(automationLiveSetting)?.Value);
        Assert.IsNull(versionText.Attribute(automationName));
        Assert.IsNull(versionText.Attribute(automationHelpText));
        Assert.IsNull(versionText.Attribute(automationLiveSetting));
        Assert.AreEqual(
            1,
            new[] { stateText, versionText }.Count(element => element.Attribute(automationLiveSetting)?.Value == "Polite"),
            "Only the state TextBlock may announce status-header changes.");
    }

    [TestMethod]
    public void ChatToolWindowXaml_UsesOnlyWpfAutomationProperties()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        var supportedProperties = typeof(System.Windows.Automation.AutomationProperties)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal) && method.GetParameters().Length == 2)
            .Select(method => $"AutomationProperties.{method.Name[3..]}")
            .ToHashSet(StringComparer.Ordinal);

        string[] unsupportedProperties = doc
            .Descendants()
            .Attributes()
            .Select(attribute => attribute.Name.LocalName)
            .Where(name => name.StartsWith("AutomationProperties.", StringComparison.Ordinal))
            .Where(name => !supportedProperties.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            unsupportedProperties,
            $"Remote UI XAML contains unsupported AutomationProperties members: {string.Join(", ", unsupportedProperties)}");
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
            typeof(AttachmentChipViewModel), typeof(FileSuggestionPresentationViewModel),
            typeof(FileSuggestionViewModel), typeof(ReasoningEffortOption), typeof(ServiceTierOption),
            typeof(PendingSkillViewModel), typeof(UsagePresentation),
            typeof(WorkerStatus), typeof(ThreadSummary),
            typeof(RemoteProfilesPresentationViewModel), typeof(RemoteProfileViewModel),
            typeof(ConnectionHealthPresentationViewModel), typeof(ConnectionTargetSnapshot),
        ];

    [TestMethod]
    public void ChatToolWindowXaml_RemoteProfileBindings_ResolveThroughDataMembers()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        string xaml = reader.ReadToEnd();

        // The profile editor must be reachable from the normal tool window: every path below
        // RemoteProfiles resolves segment by segment through [DataMember] properties.
        string[] required =
        [
            "RemoteProfiles.Profiles", "RemoteProfiles.SelectedProfile", "RemoteProfiles.AddCommand",
            "RemoteProfiles.RemoveCommand", "RemoteProfiles.SaveCommand", "RemoteProfiles.StatusText",
            "RemoteProfiles.SelectedProfile.Endpoint", "RemoteProfiles.SelectedProfile.LocalRoot",
            "RemoteProfiles.SelectedProfile.ServerRoot", "RemoteProfiles.SelectedProfile.TokenFilePath",
            "ApplyRemoteProfileCommand", "UseLocalAppServerCommand", "IsConnectionTargetOpen",
            "CheckProfileHealthCommand", "ConnectionHealth.StatusText", "ConnectionHealth.HasResult",
            "ConnectionHealth.CheckedProfileText", "ConnectionHealth.HealthText", "ConnectionHealth.ReadyText",
            "ConnectionHealth.RpcText", "ConnectionHealth.ScopeText", "RestartActionText", "RestartActionHelpText",
            "ConnectionTargetLabel",
        ];
        var bound = Regex.Matches(xaml, @"\{Binding\s+([A-Za-z_][\w.]*)")
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string path in required)
        {
            Assert.IsTrue(bound.Contains(path), $"ChatToolWindowContent.xaml does not bind '{path}'.");
        }

        foreach (string path in bound.Where(static path => path.StartsWith("RemoteProfiles", StringComparison.Ordinal)
            || path.StartsWith("ConnectionHealth", StringComparison.Ordinal)))
        {
            Type current = typeof(ChatViewModel);
            foreach (string segment in path.Split('.'))
            {
                PropertyInfo? property = current.GetProperty(segment, BindingFlags.Instance | BindingFlags.Public);
                Assert.IsNotNull(property, $"'{path}': {current.Name}.{segment} does not exist.");
                Assert.IsNotNull(
                    property!.GetCustomAttribute<DataMemberAttribute>(),
                    $"'{path}': {current.Name}.{segment} is not a [DataMember].");
                current = property.PropertyType;
            }
        }
    }

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

        // First path segment of every data-context {Binding Foo...} expression. RelativeSource
        // bindings target WPF UI ancestors and are not serialized Remote UI context properties.
        foreach (Match match in Regex.Matches(xaml, @"\{Binding\s+([A-Za-z_]\w*)(?<options>[^}]*)"))
        {
            if (match.Groups["options"].Value.Contains("RelativeSource=", StringComparison.Ordinal)
                || match.Groups["options"].Value.Contains("ElementName=", StringComparison.Ordinal))
                continue;

            string root = match.Groups[1].Value;
            if (string.Equals(root, "ElementName", StringComparison.Ordinal))
                continue;
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
    public async Task ChatViewModel_HiddenDefaultModelExposesSanitizedReasoningOptions()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [new ModelInfo { Id = "gpt-5" }],
                DefaultModel = "hidden-default",
                DefaultModelInfo = new ModelInfo
                {
                    Id = "hidden-default",
                    DefaultReasoningEffort = "high",
                    SupportedReasoningEfforts =
                    [
                        new ReasoningEffortInfo
                        {
                            Id = "high",
                            Description = "**Deep**\u001b[31m <script>bad</script>",
                        },
                    ],
                },
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();

        Assert.AreEqual("hidden-default", vm.SelectedModel);
        Assert.IsTrue(vm.HasReasoningEfforts);
        Assert.AreEqual("", vm.ReasoningEfforts[0].Id);
        Assert.AreEqual("Default", vm.ReasoningEfforts[0].DisplayText);
        Assert.AreEqual("high", vm.ReasoningEfforts[1].Id);
        Assert.AreEqual("High", vm.ReasoningEfforts[1].DisplayText);
        Assert.IsFalse(vm.ReasoningEfforts[1].Description.Contains('\u001b'));
        Assert.IsFalse(vm.ReasoningEfforts[1].Description.Contains("script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ChatViewModel_ReasoningFallbackDoesNotOverwritePersistedChoice()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ReasoningEffortId = "high" });
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [ModelWithReasoning("model-high", "high"), ModelWithReasoning("model-low", "low")],
                DefaultModel = "model-high",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store);
        await vm.PopulateModelsAsync();

        Assert.AreEqual("high", vm.SelectedReasoningEffortId);
        vm.SelectedModel = "model-low";
        Assert.AreEqual(ReasoningEffortCatalog.DefaultId, vm.SelectedReasoningEffortId);
        Assert.AreEqual("high", store.Settings.ReasoningEffortId);
        vm.SelectedModel = "model-high";
        Assert.AreEqual("high", vm.SelectedReasoningEffortId);
    }

    [TestMethod]
    public async Task ChatViewModel_PersistentReasoningUsesCanonicalValueForDefaultAndPlanTurns()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ReasoningEffortId = "HIGH" });
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [ModelWithReasoning("gpt-5", "high")],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });

        await SendMessageAsync(vm, "default turn", clearComposer: false);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasEffort);
        Assert.AreEqual("high", bridge.LastStartTurnRequest.Effort);
        vm.ComposerText = "/plan plan turn";
        await InvokeComposerSendAsync(vm);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasEffort);
        Assert.AreEqual("high", bridge.LastStartTurnRequest.Effort);
        Assert.AreEqual("high", bridge.LastStartTurnRequest.CollaborationMode!.ReasoningEffort);
    }

    [TestMethod]
    public async Task ChatViewModel_ServiceTiersUseHiddenDefaultMetadataAndSanitizeCatalogText()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [new ModelInfo { Id = "visible" }],
                DefaultModel = "hidden-default",
                DefaultModelInfo = new ModelInfo
                {
                    Id = "hidden-default",
                    DefaultServiceTier = "<script>standard</script>",
                    ServiceTiers =
                    [
                        new ServiceTierInfo { Id = "standard", Name = "Standard", Description = "Normal queue" },
                        new ServiceTierInfo { Id = "FAST", Name = "<b>Fast</b>", Description = "<script>bad()</script> Low latency" },
                        new ServiceTierInfo { Id = "fast", Name = "Duplicate" },
                        new ServiceTierInfo { Id = "<script>ultra</script>" },
                    ],
                },
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();

        string[] expectedIds = ["", "standard", "FAST", "<script>ultra</script>"];
        CollectionAssert.AreEqual(expectedIds, vm.ServiceTiers.Select(option => option.Id).ToArray());
        Assert.AreEqual("Fast", vm.ServiceTiers[2].DisplayText);
        Assert.IsFalse(vm.ServiceTiers[2].Description.Contains("<script>", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(vm.ServiceTiers[0].Description.Contains("<script>", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(vm.ServiceTiers[0].AutomationName.Contains("<script>", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(vm.ServiceTiers[3].DisplayText.Contains('<'));
        Assert.IsFalse(vm.ServiceTiers[3].AutomationName.Contains('<'));
    }

    [TestMethod]
    public async Task ChatViewModel_ServiceTierSelectionPreservesUnsupportedPersistedIdAcrossModels()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ServiceTierId = "FAST" });
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo { Id = "fast-model", ServiceTiers = [new ServiceTierInfo { Id = "fast" }] },
                    new ModelInfo { Id = "standard-model", ServiceTiers = [new ServiceTierInfo { Id = "standard" }] },
                ],
                DefaultModel = "fast-model",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store);
        await vm.PopulateModelsAsync();
        Assert.AreEqual("fast", vm.SelectedServiceTierId);

        vm.SelectedModel = "standard-model";
        Assert.AreEqual(ServiceTierCatalog.DefaultId, vm.SelectedServiceTierId);
        Assert.AreEqual("FAST", store.Settings.ServiceTierId);

        vm.SelectedModel = "fast-model";
        Assert.AreEqual("fast", vm.SelectedServiceTierId);
    }

    [TestMethod]
    public async Task ChatViewModel_PersistentServiceTierUsesCanonicalValueForNormalAndPlanTurns()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ServiceTierId = "FAST" });
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [new ModelInfo { Id = "gpt-5", ServiceTiers = [new ServiceTierInfo { Id = "fast" }] }],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });

        await SendMessageAsync(vm, "normal", clearComposer: false);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasServiceTier);
        Assert.AreEqual("fast", bridge.LastStartTurnRequest.ServiceTier);

        vm.ComposerText = "/plan direct plan";
        await InvokeComposerSendAsync(vm);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasServiceTier);
        Assert.AreEqual("fast", bridge.LastStartTurnRequest.ServiceTier);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest.CollaborationMode!.Mode);
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
        Assert.AreEqual("user", bridge.LastStartTurnRequest.ApprovalsReviewer);
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
    public void ChatViewModel_ApprovalModesUseStableIdsAndCustomDefault()
    {
        var store = new MemorySettingsStore(new ExtensionSettings());
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false, settingsStore: store);

        CollectionAssert.AreEqual(
            ExpectedApprovalModes,
            vm.ApprovalModes.Select(static mode => mode.Id).ToArray());
        Assert.AreEqual("custom", vm.SelectedApprovalModeId);
        Assert.AreEqual("Custom (config.toml)", vm.DesiredApprovalModeText);
        Assert.IsNotNull(typeof(ApprovalModeOption).GetProperty(nameof(ApprovalModeOption.Id))!
            .GetCustomAttribute<DataMemberAttribute>());
        Assert.IsNotNull(typeof(ApprovalModeOption).GetProperty(nameof(ApprovalModeOption.Source))!
            .GetCustomAttribute<DataMemberAttribute>());
    }

    [TestMethod]
    public async Task ChatViewModel_FullAccessRequiresConfirmationBeforePersistence()
    {
        var store = new MemorySettingsStore(new ExtensionSettings());
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false, settingsStore: store);

        vm.SelectedApprovalModeId = "full";

        Assert.IsTrue(vm.HasApprovalModeConfirmation);
        Assert.AreEqual("custom", vm.SelectedApprovalModeId);
        Assert.AreEqual("custom", store.Settings.ApprovalModeId);
        StringAssert.Contains(vm.ApprovalModeConfirmationText, "without any request reaching");

        vm.ConfirmApprovalModeCommand.Execute(null);
        await WaitForAsync(() => !vm.HasApprovalModeConfirmation);

        Assert.AreEqual("full", vm.SelectedApprovalModeId);
        Assert.AreEqual("full", store.Settings.ApprovalModeId);
    }

    [TestMethod]
    public void ChatViewModel_SavedFullAccessFallsBackUntilReconfirmed()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ApprovalModeId = "full" });
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false, settingsStore: store);

        Assert.AreEqual("custom", vm.SelectedApprovalModeId);
        Assert.IsTrue(vm.HasApprovalModeConfirmation);
        Assert.AreEqual("Full access", vm.DesiredApprovalModeText);
    }

    [TestMethod]
    public async Task ChatViewModel_PermissionProfileUsesExclusiveTurnOverride()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ApprovalModeId = "permission:workspace-safe" });
        var bridge = new FakeWorkerBridge
        {
            PermissionProfilesResult = new ListPermissionProfilesResult
            {
                Profiles =
                [
                    new PermissionProfileInfo { Id = "workspace-safe", Description = "Workspace only", Allowed = true },
                    new PermissionProfileInfo { Id = "blocked", Allowed = false },
                ],
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        Assert.AreEqual("permission:workspace-safe", vm.SelectedApprovalModeId);
        Assert.AreEqual("Loading", vm.ApprovalModes.Single(mode => mode.Id == "permission:workspace-safe").Source);
        await vm.PopulatePermissionProfilesAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.AreEqual("permission:workspace-safe", vm.SelectedApprovalModeId);
        Assert.IsFalse(vm.ApprovalModes.Any(mode => mode.Id == "permission:blocked"));
        Assert.AreEqual("workspace-safe", bridge.LastStartTurnRequest!.Permissions);
        Assert.IsNull(bridge.LastStartTurnRequest.ApprovalPolicy);
        Assert.IsNull(bridge.LastStartTurnRequest.ApprovalsReviewer);
        Assert.IsNull(bridge.LastStartTurnRequest.SandboxMode);

        vm.SelectedApprovalModeId = "custom";
        Assert.IsTrue(vm.HasApprovalModeConfirmation);
        Assert.AreEqual("permission:workspace-safe", store.Settings.ApprovalModeId);
        vm.ConfirmApprovalModeCommand.Execute(null);
        await WaitForAsync(() => vm.SelectedApprovalModeId == "custom");
        Assert.AreEqual("custom", store.Settings.ApprovalModeId);
    }

    [TestMethod]
    public void ChatViewModel_WhitespacePermissionProfileIdFallsBackToCustom()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ApprovalModeId = "permission: workspace-safe " });

        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false, settingsStore: store);

        Assert.AreEqual("custom", vm.SelectedApprovalModeId);
        Assert.AreEqual("custom", store.Settings.ApprovalModeId);
        Assert.IsFalse(vm.ApprovalModes.Any(mode => mode.Id.StartsWith("permission:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ChatViewModel_PermissionsErrorListsRuntimeProfileStableIds()
    {
        var bridge = new FakeWorkerBridge
        {
            PermissionProfilesResult = new ListPermissionProfilesResult
            {
                Profiles = [new PermissionProfileInfo { Id = "workspace-safe", Allowed = true }],
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulatePermissionProfilesAsync();

        MethodInfo executePermissions = typeof(ChatViewModel).GetMethod(
            "ExecutePermissionsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool succeeded = await (Task<bool>)executePermissions.Invoke(vm, ["unknown-mode"])!;

        Assert.IsFalse(succeeded);
        StringAssert.Contains(vm.Items.Last().Text, "permission:workspace-safe");
    }

    [TestMethod]
    public async Task ChatViewModel_StatusSlashCommand_DoesNotSteerActiveTurn()
    {
        var bridge = new FakeWorkerBridge
        {
            RateLimitsResult = new RateLimitsResult
            {
                RateLimits = new RateLimitInfo
                {
                    Primary = new() { UsedPercent = 20, WindowDurationMinutes = 300 },
                    Secondary = new() { UsedPercent = 50, WindowDurationMinutes = 10_080 },
                    Credits = new() { HasCredits = true, Balance = "12.50" },
                },
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            ComposerText = "/status",
        };
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "turn-1",
        });

        await InvokeComposerSendAsync(vm);

        Assert.IsNull(bridge.LastSteerTurnRequest);
        Assert.AreEqual(1, bridge.RateLimitCallCount);
        StringAssert.Contains(vm.Items.Last().Text, "5-hour limit: 80% remaining");
        StringAssert.Contains(vm.Items.Last().Text, "Weekly limit: 50% remaining");
        StringAssert.Contains(vm.Items.Last().Text, "Credits: 12.50");
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
        await WaitForAsync(() => bridge.ReviewCallCount == 1);

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
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings()))
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
        Assert.IsTrue(bridge.LastStartTurnRequest.HasServiceTier);
        Assert.IsNull(bridge.LastStartTurnRequest.Effort);
        Assert.IsNull(bridge.LastStartTurnRequest.Personality);

        await SendMessageAsync(vm, "third", clearComposer: false);
        Assert.IsFalse(bridge.LastStartTurnRequest!.HasServiceTier);
    }

    [TestMethod]
    public async Task ChatViewModel_FastOverrideSurvivesFailureAndRestoresPersistentTier()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ServiceTierId = "standard" });
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo
                    {
                        Id = "gpt-5",
                        ServiceTiers = [new ServiceTierInfo { Id = "standard" }, new ServiceTierInfo { Id = "Fast" }],
                    },
                ],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1", EffectiveServiceTier = "standard" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
            EffectiveServiceTier = "standard",
        });
        vm.ComposerText = "/fast";
        await InvokeComposerSendAsync(vm);

        bridge.StartTurnException = new InvalidOperationException("start failed");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SendMessageAsync(vm, "fails", clearComposer: false));
        Assert.AreEqual("Fast", bridge.LastStartTurnRequest!.ServiceTier);
        bridge.StartTurnException = null;
        await SendMessageAsync(vm, "fast succeeds", clearComposer: false);
        Assert.AreEqual("Fast", bridge.LastStartTurnRequest!.ServiceTier);
        vm.ComposerText = "/status";
        await InvokeComposerSendAsync(vm);
        StringAssert.Contains(vm.Items[^1].Text, "Desired service tier: standard");
        StringAssert.Contains(vm.Items[^1].Text, "Effective service tier: standard");
        StringAssert.Contains(vm.Items[^1].Text, "Next-turn service tier: standard");
        await SendMessageAsync(vm, "restore", clearComposer: false);
        Assert.AreEqual("standard", bridge.LastStartTurnRequest!.ServiceTier);
        await SendMessageAsync(vm, "stable", clearComposer: false);
        Assert.AreEqual("standard", bridge.LastStartTurnRequest!.ServiceTier);
    }

    [TestMethod]
    public async Task ChatViewModel_FastOverrideIsIsolatedByThread()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ServiceTierId = "standard" });
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo
                    {
                        Id = "gpt-5",
                        ServiceTiers = [new ServiceTierInfo { Id = "standard" }, new ServiceTierInfo { Id = "fast" }],
                    },
                ],
                DefaultModel = "gpt-5",
            },
        };
        var thread1 = new ThreadSummary { Id = "thread-1", EffectiveServiceTier = "standard" };
        var thread2 = new ThreadSummary { Id = "thread-2", EffectiveServiceTier = "standard" };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store) { SelectedThread = thread1 };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        vm.ComposerText = "/fast";
        await InvokeComposerSendAsync(vm);

        vm.SelectedThread = thread2;
        await SendMessageAsync(vm, "thread two", clearComposer: false);
        Assert.AreEqual("standard", bridge.LastStartTurnRequest!.ServiceTier);
        vm.SelectedThread = thread1;
        await SendMessageAsync(vm, "thread one", clearComposer: false);
        Assert.AreEqual("fast", bridge.LastStartTurnRequest!.ServiceTier);
    }

    [TestMethod]
    public async Task ChatViewModel_RepeatedFastOverridePreservesInheritedRestoreTarget()
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
                        ServiceTiers = [new ServiceTierInfo { Id = "standard" }, new ServiceTierInfo { Id = "fast" }],
                    },
                ],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1", EffectiveServiceTier = "standard" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
            EffectiveServiceTier = "standard",
        });

        vm.ComposerText = "/fast";
        await InvokeComposerSendAsync(vm);
        await SendMessageAsync(vm, "first fast", clearComposer: false);
        Assert.AreEqual("fast", bridge.LastStartTurnRequest!.ServiceTier);

        vm.ComposerText = "/fast";
        await InvokeComposerSendAsync(vm);
        await SendMessageAsync(vm, "second fast", clearComposer: false);
        Assert.AreEqual("fast", bridge.LastStartTurnRequest!.ServiceTier);

        await SendMessageAsync(vm, "restore", clearComposer: false);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasServiceTier);
        Assert.AreEqual("standard", bridge.LastStartTurnRequest.ServiceTier);

        await SendMessageAsync(vm, "inherit", clearComposer: false);
        Assert.IsFalse(bridge.LastStartTurnRequest!.HasServiceTier);
    }

    [TestMethod]
    public async Task ChatViewModel_FastOverrideAndRestoreWaitForSupportedModel()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo
                    {
                        Id = "tier-model",
                        ServiceTiers = [new ServiceTierInfo { Id = "standard" }, new ServiceTierInfo { Id = "fast" }],
                    },
                    new ModelInfo { Id = "plain-model" },
                ],
                DefaultModel = "tier-model",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1", EffectiveServiceTier = "standard" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
            EffectiveServiceTier = "standard",
        });

        vm.ComposerText = "/fast";
        await InvokeComposerSendAsync(vm);
        vm.SelectedModel = "plain-model";
        await SendMessageAsync(vm, "unsupported fast", clearComposer: false);
        Assert.IsFalse(bridge.LastStartTurnRequest!.HasServiceTier);

        vm.SelectedModel = "tier-model";
        await SendMessageAsync(vm, "supported fast", clearComposer: false);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasServiceTier);
        Assert.AreEqual("fast", bridge.LastStartTurnRequest.ServiceTier);

        vm.SelectedModel = "plain-model";
        await SendMessageAsync(vm, "unsupported restore", clearComposer: false);
        Assert.IsFalse(bridge.LastStartTurnRequest!.HasServiceTier);

        vm.SelectedModel = "tier-model";
        await SendMessageAsync(vm, "supported restore", clearComposer: false);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasServiceTier);
        Assert.AreEqual("standard", bridge.LastStartTurnRequest.ServiceTier);
    }

    [TestMethod]
    public async Task ChatViewModel_ReasoningSlashIsCanonicalThreadScopedAndRestoresStickySetting()
    {
        var store = new MemorySettingsStore(new ExtensionSettings());
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [ModelWithReasoning("gpt-5", "medium", "high")],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
            EffectiveReasoningEffort = "medium",
        });

        vm.ComposerText = "/reasoning HIGH";
        await InvokeComposerSendAsync(vm);
        await SendMessageAsync(vm, "override", clearComposer: false);
        Assert.AreEqual("high", bridge.LastStartTurnRequest!.Effort);
        vm.ComposerText = "/status";
        await InvokeComposerSendAsync(vm);
        StringAssert.Contains(vm.Items[^1].Text, "Next-turn reasoning effort: medium");
        await SendMessageAsync(vm, "restore", clearComposer: false);
        Assert.AreEqual("medium", bridge.LastStartTurnRequest!.Effort);
        await SendMessageAsync(vm, "inherit", clearComposer: false);
        Assert.IsFalse(bridge.LastStartTurnRequest!.HasEffort);
    }

    [TestMethod]
    public async Task ChatViewModel_ReplacingReasoningOverridePreservesPersistentRestoreTarget()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ReasoningEffortId = "high" });
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [ModelWithReasoning("gpt-5", "low", "medium", "high", "xhigh")],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
            EffectiveReasoningEffort = "medium",
        });

        vm.ComposerText = "/reasoning xhigh";
        await InvokeComposerSendAsync(vm);
        await SendMessageAsync(vm, "first", clearComposer: false);
        vm.ComposerText = "/reasoning low";
        await InvokeComposerSendAsync(vm);
        await SendMessageAsync(vm, "replacement", clearComposer: false);
        Assert.AreEqual("low", bridge.LastStartTurnRequest!.Effort);
        await SendMessageAsync(vm, "restore", clearComposer: false);
        Assert.AreEqual("high", bridge.LastStartTurnRequest!.Effort);
    }

    [TestMethod]
    public async Task ChatViewModel_ReplacingReasoningOverridePreservesOriginalEffectiveRestore()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [ModelWithReasoning("gpt-5", "low", "medium", "high")],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
            EffectiveReasoningEffort = "medium",
        });
        vm.ComposerText = "/reasoning high";
        await InvokeComposerSendAsync(vm);
        await SendMessageAsync(vm, "high", clearComposer: false);
        vm.ComposerText = "/reasoning low";
        await InvokeComposerSendAsync(vm);
        await SendMessageAsync(vm, "low", clearComposer: false);
        Assert.AreEqual("low", bridge.LastStartTurnRequest!.Effort);
        await SendMessageAsync(vm, "restore", clearComposer: false);
        Assert.AreEqual("medium", bridge.LastStartTurnRequest!.Effort);
    }

    [TestMethod]
    public async Task ChatViewModel_ReasoningOverrideAndRestoreWaitForSupportedModel()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    ModelWithReasoning("reasoning-model", "medium", "high"),
                    new ModelInfo { Id = "plain-model" },
                ],
                DefaultModel = "reasoning-model",
            },
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-1", EffectiveReasoningEffort = "medium" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            ThreadId = "thread-1",
            EffectiveReasoningEffort = "medium",
        });

        vm.ComposerText = "/reasoning high";
        await InvokeComposerSendAsync(vm);
        vm.SelectedModel = "plain-model";
        await SendMessageAsync(vm, "unsupported override", clearComposer: false);
        Assert.IsFalse(bridge.LastStartTurnRequest!.HasEffort);

        vm.SelectedModel = "reasoning-model";
        await SendMessageAsync(vm, "supported override", clearComposer: false);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasEffort);
        Assert.AreEqual("high", bridge.LastStartTurnRequest.Effort);

        vm.SelectedModel = "plain-model";
        await SendMessageAsync(vm, "unsupported restore", clearComposer: false);
        Assert.IsFalse(bridge.LastStartTurnRequest!.HasEffort);

        vm.SelectedModel = "reasoning-model";
        await SendMessageAsync(vm, "supported restore", clearComposer: false);
        Assert.IsTrue(bridge.LastStartTurnRequest!.HasEffort);
        Assert.AreEqual("medium", bridge.LastStartTurnRequest.Effort);
    }

    [TestMethod]
    public async Task ChatViewModel_StatusReportsUnsupportedPersistedReasoningId()
    {
        var store = new MemorySettingsStore(new ExtensionSettings { ReasoningEffortId = "organization-tier" });
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false, settingsStore: store)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            ComposerText = "/status",
        };
        await InvokeComposerSendAsync(vm);
        StringAssert.Contains(vm.Items[^1].Text, "Desired reasoning effort: organization-tier");
        StringAssert.Contains(vm.Items[^1].Text, "Next-turn reasoning effort: (config.toml)");
    }

    [TestMethod]
    public async Task ChatViewModel_FailedTurnDoesNotConsumeReasoningSlashOverride()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [ModelWithReasoning("gpt-5", "high")],
                DefaultModel = "gpt-5",
            },
            StartTurnException = new InvalidOperationException("start failed"),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        vm.ComposerText = "/reasoning high";
        await InvokeComposerSendAsync(vm);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SendMessageAsync(vm, "fails", clearComposer: false));
        bridge.StartTurnException = null;
        await SendMessageAsync(vm, "succeeds", clearComposer: false);
        Assert.AreEqual("high", bridge.LastStartTurnRequest!.Effort);
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
        await WaitForAsync(() => vm.SelectedModel == "gpt-5");

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
        await WaitForAsync(() => vm.SelectedModel == "gpt-5");

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
        await WaitForAsync(() => bridge.ReviewCallCount == 1);

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
        await WaitForAsync(() => bridge.LastResumedThreadId == "thread-fork");

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
    public async Task ChatViewModel_UsageFetchesOncePerConnectionGeneration()
    {
        var bridge = new FakeWorkerBridge
        {
            RateLimitsResult = UsageResult(20),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        Assert.AreEqual(1, bridge.RateLimitCallCount);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Busy });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        Assert.AreEqual(1, bridge.RateLimitCallCount);

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Disconnected });
        Assert.IsFalse(vm.Usage.HasData);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        Assert.AreEqual(2, bridge.RateLimitCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_TurnCompleted_ForcesUsageRefreshWithinTtl()
    {
        DateTimeOffset now = new(2026, 8, 12, 1, 0, 30, TimeSpan.Zero);
        var bridge = new FakeWorkerBridge { RateLimitsResult = UsageResult(20) };
        using var vm = new ChatViewModel(bridge, autoConnect: false, utcNow: () => now);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        string initialUpdatedText = vm.Usage.UpdatedText;
        Assert.AreEqual(1, bridge.RateLimitCallCount);
        Assert.AreEqual("80% remaining", vm.Usage.ToolbarText);

        bridge.RateLimitsResult = UsageResult(60);
        now = now.AddSeconds(40);
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });

        Assert.AreEqual(2, bridge.RateLimitCallCount);
        Assert.AreEqual("40% remaining", vm.Usage.ToolbarText);
        Assert.AreNotEqual(initialUpdatedText, vm.Usage.UpdatedText);
    }

    [TestMethod]
    public async Task ChatViewModel_TurnCompleted_DoesNotRefreshWhenUsageUnavailable()
    {
        var bridge = new FakeWorkerBridge { RateLimitsResult = UsageResult(20) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Disconnected });
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });
        Assert.AreEqual(1, bridge.RateLimitCallCount);

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        Assert.AreEqual(2, bridge.RateLimitCallCount);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedOut });
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });
        Assert.AreEqual(2, bridge.RateLimitCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_TurnCompleted_RefreshFailurePreservesSnapshotAndRetries()
    {
        var bridge = new FakeWorkerBridge
        {
            RateLimitHandler = call => call switch
            {
                1 => Task.FromResult(UsageResult(20)),
                2 => Task.FromException<RateLimitsResult>(new InvalidOperationException("transient")),
                _ => Task.FromResult(UsageResult(60)),
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });
        Assert.AreEqual(2, bridge.RateLimitCallCount);
        Assert.AreEqual("80% remaining", vm.Usage.ToolbarText);

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });
        Assert.AreEqual(3, bridge.RateLimitCallCount);
        Assert.AreEqual("40% remaining", vm.Usage.ToolbarText);
    }

    [TestMethod]
    public async Task ChatViewModel_ContextCompacted_ForcesUsageRefreshWithinTtl()
    {
        DateTimeOffset now = new(2026, 8, 12, 1, 0, 30, TimeSpan.Zero);
        var bridge = new FakeWorkerBridge { RateLimitsResult = UsageResult(20) };
        using var vm = new ChatViewModel(bridge, autoConnect: false, utcNow: () => now);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        string initialUpdatedText = vm.Usage.UpdatedText;
        Assert.AreEqual(1, bridge.RateLimitCallCount);
        Assert.AreEqual("80% remaining", vm.Usage.ToolbarText);

        bridge.RateLimitsResult = UsageResult(60);
        now = now.AddSeconds(40);
        await RaiseContextCompactedAsync(vm, new ContextCompactionEvent { IsCompleted = true });

        Assert.AreEqual(2, bridge.RateLimitCallCount);
        Assert.AreEqual("40% remaining", vm.Usage.ToolbarText);
        Assert.AreNotEqual(initialUpdatedText, vm.Usage.UpdatedText);
    }

    [TestMethod]
    public async Task ChatViewModel_ContextCompacted_InProgressDoesNotRefreshUsage()
    {
        var bridge = new FakeWorkerBridge { RateLimitsResult = UsageResult(20) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        Assert.AreEqual(1, bridge.RateLimitCallCount);

        bridge.RateLimitsResult = UsageResult(60);
        await RaiseContextCompactedAsync(vm, new ContextCompactionEvent { IsCompleted = false });

        Assert.AreEqual(1, bridge.RateLimitCallCount);
        Assert.AreEqual("80% remaining", vm.Usage.ToolbarText);
    }

    [TestMethod]
    public async Task ChatViewModel_UsagePopupRefreshesOnlyAfterTtl()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var bridge = new FakeWorkerBridge { RateLimitsResult = UsageResult(20) };
        using var vm = new ChatViewModel(bridge, autoConnect: false, utcNow: () => now);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        await vm.RefreshUsageAsync(force: false);
        Assert.AreEqual(1, bridge.RateLimitCallCount);
        now = now.AddSeconds(61);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Busy });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        Assert.AreEqual(1, bridge.RateLimitCallCount, "Busy-to-Ready must not refresh usage even after the popup TTL.");
        await vm.RefreshUsageAsync(force: false);
        Assert.AreEqual(2, bridge.RateLimitCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_UsagePushWinsAgainstOlderReadResponse()
    {
        using var releaseRead = new SemaphoreSlim(0, 1);
        var bridge = new FakeWorkerBridge
        {
            RateLimitHandler = async _ =>
            {
                await releaseRead.WaitAsync();
                return UsageResult(10);
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });

        Task ready = Task.Run(() => bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready }));
        await WaitForAsync(() => bridge.RateLimitCallCount == 1);
        await bridge.PublishRateLimitsAsync(UsageResult(70));
        releaseRead.Release();
        await ready;

        Assert.AreEqual("30% remaining", vm.Usage.ToolbarText);
    }

    [TestMethod]
    public async Task ChatViewModel_OldGenerationPushCannotOverwriteReconnect()
    {
        var bridge = new FakeWorkerBridge { RateLimitsResult = UsageResult(20) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        long oldGeneration = GetPrivateLong(vm, "usageConnectionGeneration");
        await bridge.PublishRateLimitsAsync(UsageResult(40));
        long oldPushVersion = GetPrivateLong(vm, "rateLimitPushVersion");

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Disconnected });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        await bridge.PublishRateLimitsAsync(UsageResult(70));
        long currentPushVersion = GetPrivateLong(vm, "rateLimitPushVersion");

        Assert.IsTrue(currentPushVersion > oldPushVersion, "The push version must remain monotonic across reconnects.");
        ApplyRateLimitsPush(vm, UsageResult(5), oldGeneration, currentPushVersion);
        Assert.AreEqual("30% remaining", vm.Usage.ToolbarText);
    }

    [TestMethod]
    public async Task ChatViewModel_UsageRefreshFailurePreservesSnapshotAndCanRetry()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var bridge = new FakeWorkerBridge
        {
            RateLimitHandler = call => call switch
            {
                1 => Task.FromResult(UsageResult(20)),
                2 => Task.FromException<RateLimitsResult>(new InvalidOperationException("transient")),
                _ => Task.FromResult(UsageResult(60)),
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, utcNow: () => now);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        now = now.AddSeconds(61);
        await vm.RefreshUsageAsync(force: false);
        Assert.AreEqual("80% remaining", vm.Usage.ToolbarText);
        Assert.AreEqual(2, bridge.RateLimitCallCount);

        await vm.RefreshUsageAsync(force: false);
        Assert.AreEqual("40% remaining", vm.Usage.ToolbarText);
        Assert.AreEqual(3, bridge.RateLimitCallCount, "A failed refresh must remain immediately retryable.");
    }

    [TestMethod]
    public async Task ChatViewModel_UsageLifecycleAndFlyoutsAreMutuallyExclusive()
    {
        var bridge = new FakeWorkerBridge { RateLimitsResult = UsageResult(20) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        vm.IsHistoryOpen = true;
        vm.IsUsageOpen = true;
        Assert.IsFalse(vm.IsHistoryOpen);
        Assert.IsTrue(vm.IsUsageOpen);

        long signedInGeneration = GetPrivateLong(vm, "usageConnectionGeneration");
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedOut });
        Assert.IsFalse(vm.IsUsageAvailable);
        Assert.IsFalse(vm.IsUsageOpen);
        Assert.IsFalse(vm.Usage.HasData);
        Assert.IsTrue(GetPrivateLong(vm, "usageConnectionGeneration") > signedInGeneration);
    }

    [TestMethod]
    public async Task ChatViewModel_DisposeInvalidatesUsageAndRejectsInFlightRead()
    {
        using var releaseRead = new SemaphoreSlim(0, 1);
        var bridge = new FakeWorkerBridge
        {
            RateLimitHandler = call => call == 1
                ? Task.FromResult(UsageResult(20))
                : WaitForReadReleaseAsync(releaseRead),
        };
        var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        vm.IsUsageOpen = true;
        Task refresh = vm.RefreshUsageAsync(force: true);
        await WaitForAsync(() => bridge.RateLimitCallCount == 2);

        vm.Dispose();
        releaseRead.Release();
        await refresh;

        Assert.IsFalse(vm.IsUsageOpen);
        Assert.IsFalse(vm.Usage.HasData);
        Assert.AreEqual("Usage", vm.Usage.ToolbarText);
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
    public void ChatViewModel_LeadingSlash_OpensUnifiedSlashSuggestions()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

        vm.ComposerText = "/";

        Assert.IsTrue(vm.SlashCommands.IsSuggestionOpen);
        Assert.HasCount(8, vm.SlashCommands.Suggestions.Where(static item => item.IsSelectable && !item.IsSkill));
        Assert.IsTrue(vm.SlashCommands.Suggestions.Any(static item => !item.IsSelectable && item.CommandName == "Skills"));
        Assert.AreEqual("/compact", vm.SlashCommands.SelectedSuggestion?.CommandName);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(20)]
    [DataRow(21)]
    [DataRow(200)]
    [DataRow(201)]
    public async Task ChatViewModel_SlashMenuShowsEverySkillReturnedByWorker(int skillCount)
    {
        var bridge = new FakeWorkerBridge
        {
            SkillsResult = CreateSkillsResult(skillCount, generation: 1),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        vm.ComposerText = "/";
        await WaitForAsync(() => skillCount == 0
            ? vm.SlashCommands.Suggestions.Any(item => item.CommandName == "No matching skills")
            : vm.SlashCommands.Suggestions.Count(static item => item.IsSkill) == skillCount);

        Assert.HasCount(skillCount, vm.SlashCommands.Suggestions.Where(static item => item.IsSkill));
        Assert.IsFalse(vm.SlashCommands.Suggestions.Any(item => item.CommandName == "More skills available"));
        Assert.AreEqual(
            skillCount == 0,
            vm.SlashCommands.Suggestions.Any(item => item.CommandName == "No matching skills"));
    }

    [TestMethod]
    public async Task ChatViewModel_TruncatedWorkerCatalogShowsAllReturnedSkillsAndOnlyWorkerStatusRow()
    {
        var bridge = new FakeWorkerBridge
        {
            SkillsResult = CreateSkillsResult(21, generation: 4, isTruncated: true),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        vm.ComposerText = "/";
        await WaitForAsync(() => vm.SlashCommands.Suggestions.Count(static item => item.IsSkill) == 21);

        Assert.IsTrue(vm.SlashCommands.Suggestions.Any(item => item.CommandName == "Skill catalog incomplete"));
        Assert.IsFalse(vm.SlashCommands.Suggestions.Any(item => item.CommandName == "More skills available"));
    }

    [TestMethod]
    public async Task ChatViewModel_StaleSkillsRemainVisibleUntilNewGenerationReplacesThem()
    {
        var bridge = new FakeWorkerBridge
        {
            SkillsResult = CreateSkillsResult(1, generation: 1, isStale: true, namePrefix: "cached"),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        vm.ComposerText = "/";
        await WaitForAsync(() => vm.SlashCommands.Suggestions.Any(item => item.CommandName == "/cached-000")
            && vm.SlashCommands.Suggestions.Any(item => item.CommandName == "Cached skill catalog"));
        SlashCommandSuggestionViewModel cached = vm.SlashCommands.Suggestions.Single(item => item.CommandName == "/cached-000");
        Assert.IsFalse(cached.IsAvailable);
        Assert.IsFalse(cached.IsSelectable);
        StringAssert.Contains(cached.ScopeLabel, "Cached");
        Assert.AreEqual(1, bridge.SkillsListCallCount, "The UI must not duplicate the Worker's background refresh.");

        bridge.SkillsResult = CreateSkillsResult(1, generation: 2, namePrefix: "fresh");
        await bridge.PublishSkillsChangedAsync(new SkillsChangedEvent { Generation = 2 });
        await WaitForAsync(() => vm.SlashCommands.Suggestions.Any(item => item.CommandName == "/fresh-000"));

        Assert.IsFalse(vm.SlashCommands.Suggestions.Any(item => item.CommandName == "/cached-000"));
        Assert.IsFalse(vm.SlashCommands.Suggestions.Any(item => item.CommandName == "Cached skill catalog"));
    }

    [TestMethod]
    public async Task ChatViewModel_RebuildReplacesSkillSelectionSnapshot()
    {
        var bridge = new FakeWorkerBridge
        {
            SkillsResult = CreateSkillsResult(21, generation: 3),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        vm.ComposerText = "/";
        await WaitForAsync(() => vm.SlashCommands.Suggestions.Count(static item => item.IsSkill) == 21);
        SlashCommandSuggestionViewModel staleSuggestion = vm.SlashCommands.Suggestions.First(static item => item.IsSkill);

        vm.ComposerText = "/skill-019";

        Assert.AreEqual(1, GetPrivateDictionaryCount(vm, "skillSelections"));
        await bridge.PublishSkillsChangedAsync(new SkillsChangedEvent { Generation = 4 });
        await WaitForAsync(() => GetPrivateDictionaryCount(vm, "skillSelections") == 0);
        staleSuggestion.UseCommand.Execute(null);
        await WaitForAsync(() => vm.SlashCommands.StatusAnnouncement.Contains("no longer available", StringComparison.Ordinal));
        Assert.IsFalse(vm.HasPendingSkill);
    }

    [TestMethod]
    public async Task ChatViewModel_AttachAndStartTurn_CopiesAndClearsAttachments()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            var bridge = new FakeWorkerBridge();
            var picker = new FakeFilePickerService([filePath, filePath]);
            using var vm = new ChatViewModel(
                bridge,
                autoConnect: false,
                filePickerService: picker,
                protectedDirectoryPolicy: new ProtectedDirectoryPolicy([]));
            await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
            vm.SelectedThread = new ThreadSummary { Id = "thread-1" };

            vm.AttachCommand.Execute(null);
            await WaitForAsync(() => vm.PendingAttachments.Count == 1);

            await SendMessageAsync(vm, "inspect this", clearComposer: true);

            Assert.IsNotNull(bridge.LastStartTurnRequest);
            Assert.HasCount(1, bridge.LastStartTurnRequest!.Attachments);
            Assert.AreEqual(Path.GetFullPath(filePath), bridge.LastStartTurnRequest.Attachments[0].Path);
            Assert.AreEqual("mention", bridge.LastStartTurnRequest.Attachments[0].Kind);
            Assert.IsFalse(vm.HasPendingAttachments);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    public async Task ChatViewModel_StartTurnFailure_ShowsReasonAndPreservesAttachments()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            // The Worker rejects an attachment a remote app-server cannot read before the turn
            // starts. The reason must reach the transcript instead of only diagnostics, and the
            // attachment chip must stay so the user can fix it.
            var bridge = new FakeWorkerBridge
            {
                StartTurnException = new StreamJsonRpc.RemoteInvocationException(
                    "The attachment 'spec.md' is outside the remote profile's local root.",
                    WorkerErrorCodes.AttachmentRejected,
                    errorData: null),
            };
            using var vm = new ChatViewModel(
                bridge,
                autoConnect: false,
                filePickerService: new FakeFilePickerService([filePath]),
                protectedDirectoryPolicy: new ProtectedDirectoryPolicy([]));
            await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
            vm.SelectedThread = new ThreadSummary { Id = "thread-1" };
            vm.AttachCommand.Execute(null);
            await WaitForAsync(() => vm.HasPendingAttachments);

            await SendMessageAsync(vm, "inspect this", clearComposer: true);

            Assert.IsTrue(vm.HasPendingAttachments);
            Assert.IsTrue(vm.Items.Any(item =>
                item.Kind == ConversationEventKind.Error
                && item.Text.Contains("outside the remote profile's local root", StringComparison.Ordinal)));

            // Any other failure (connection loss, timeout) is not reported as "not sent": the turn
            // may have started server-side, so it keeps the previous propagation behavior.
            bridge.StartTurnException = new InvalidOperationException("failed");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => SendMessageAsync(vm, "inspect this", clearComposer: true));
            Assert.IsTrue(vm.HasPendingAttachments);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    public async Task ChatViewModel_SteerTurn_PreservesAttachmentsForNextStart()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            var bridge = new FakeWorkerBridge();
            using var vm = new ChatViewModel(
                bridge,
                autoConnect: false,
                filePickerService: new FakeFilePickerService([filePath]),
                protectedDirectoryPolicy: new ProtectedDirectoryPolicy([]));
            vm.SelectedThread = new ThreadSummary { Id = "thread-1" };
            await bridge.PublishStateAsync(new WorkerStatus
            {
                State = WorkerConnectionState.Busy,
                ThreadId = "thread-1",
                TurnId = "turn-active",
            });
            vm.AttachCommand.Execute(null);
            await WaitForAsync(() => vm.HasPendingAttachments);

            await SendMessageAsync(vm, "one more detail", clearComposer: true);

            Assert.IsNotNull(bridge.LastSteerTurnRequest);
            Assert.IsTrue(vm.HasPendingAttachments);
            Assert.IsNull(bridge.LastStartTurnRequest);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    public async Task ChatViewModel_FileSuggestion_ReplacesFinalTokenAndAddsChip()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, "Program.cs");
        await File.WriteAllTextAsync(filePath, "class Program { }");
        try
        {
            var search = new FakeWorkspaceFileSearchService(
                [new WorkspaceFileSearchResult(filePath, "src/Program.cs")]);
            using var vm = new ChatViewModel(
                new FakeWorkerBridge(),
                autoConnect: false,
                workspaceFileSearchService: search,
                protectedDirectoryPolicy: new ProtectedDirectoryPolicy([]));
            SetWorkingDirectory(vm, directory);

            vm.ComposerText = "review #prog";
            await WaitForAsync(() => vm.FileSuggestions.IsSuggestionOpen);
            vm.FileSuggestions.AcceptSuggestionCommand.Execute(null);
            await WaitForAsync(() => vm.PendingAttachments.Count == 1);

            Assert.AreEqual("review #Program.cs ", vm.ComposerText);
            Assert.AreEqual(filePath, vm.PendingAttachments[0].FullPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChatViewModel_DiscardsFileSuggestionsCompletedForPreviousOwner()
    {
        var response = new TaskCompletionSource<IReadOnlyList<WorkspaceFileSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new FakeWorkspaceFileSearchService([]);
#pragma warning disable VSTHRD003 // The fake intentionally delays search completion across an owner switch.
        search.SearchHandler = (_, _, _) => response.Task;
#pragma warning restore VSTHRD003
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            workspaceFileSearchService: search,
            protectedDirectoryPolicy: new ProtectedDirectoryPolicy([]));
        SetWorkingDirectory(vm, Path.GetTempPath());
        var oldFile = new WorkspaceFileSearchResult(Path.Combine(Path.GetTempPath(), "old-owner.cs"), "old-owner.cs");
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));

        vm.ComposerText = "#old";
        await WaitForAsync(() => search.CallCount == 1);
        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));
        response.SetResult([oldFile]);
        await Task.Delay(200);

        Assert.IsFalse(vm.FileSuggestions.IsSuggestionOpen);
        Assert.AreEqual(0, vm.FileSuggestions.Suggestions.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_DoubleHash_SendsLiteralHashWithoutOpeningFileSuggestions()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        vm.SelectedThread = new ThreadSummary { Id = "thread-1" };
        vm.ComposerText = "review ##Program.cs";

        await InvokeComposerSendAsync(vm);

        Assert.IsFalse(vm.FileSuggestions.IsSuggestionOpen);
        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.AreEqual("review #Program.cs", bridge.LastStartTurnRequest!.Text);
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
        var builtIns = vm.SlashCommands.Suggestions.Where(static item => item.IsSelectable && !item.IsSkill).ToArray();
        Assert.HasCount(2, builtIns);
        Assert.AreEqual("/reasoning", builtIns[0].CommandName);
        Assert.AreEqual("/review", builtIns[1].CommandName);
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
        Assert.IsTrue(
            suggestionCollectionChanges.Count(static action => action == NotifyCollectionChangedAction.Add) >= 8);
        Assert.IsTrue(vm.SlashCommands.IsSuggestionOpen);
        Assert.HasCount(8, vm.SlashCommands.Suggestions.Where(static item => item.IsSelectable && !item.IsSkill));
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

        return (Task)method.Invoke(viewModel, [text, clearComposer, null])!;
    }

    private static Task InvokeComposerSendAsync(ChatViewModel viewModel)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "SendAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find SendAsync.");

        return (Task)method.Invoke(viewModel, null)!;
    }

    private static long GetPrivateLong(ChatViewModel viewModel, string fieldName)
    {
        FieldInfo field = typeof(ChatViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Could not find {fieldName}.");
        return (long)field.GetValue(viewModel)!;
    }

    private static int GetPrivateDictionaryCount(ChatViewModel viewModel, string fieldName)
    {
        FieldInfo? field = typeof(ChatViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        object value = field.GetValue(viewModel)!;
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    }

    private static ListSkillsResult CreateSkillsResult(
        int count,
        long generation,
        bool isStale = false,
        bool isTruncated = false,
        string namePrefix = "skill")
        => new()
        {
            Generation = generation,
            IsStale = isStale,
            IsTruncated = isTruncated,
            Skills = Enumerable.Range(0, count)
                .Select(index => new SkillInfo
                {
                    Name = $"{namePrefix}-{index:000}",
                    Description = $"Description {index}",
                    Scope = "repo",
                    Path = $@"C:\skills\{namePrefix}-{index:000}\SKILL.md",
                    Enabled = index % 5 != 0,
                })
                .ToArray(),
        };

    private static void ApplyRateLimitsPush(
        ChatViewModel viewModel,
        RateLimitsResult result,
        long generation,
        long pushVersion)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "ApplyRateLimitsPush",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find ApplyRateLimitsPush.");
        method.Invoke(viewModel, [result, generation, pushVersion]);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the view model state to update.");
            }

            await Task.Delay(10);
        }
    }

    private static RateLimitsResult UsageResult(int usedPercent)
        => new()
        {
            RateLimits = new RateLimitInfo
            {
                LimitId = "codex",
                Primary = new RateLimitWindowInfo
                {
                    UsedPercent = usedPercent,
                    WindowDurationMinutes = 300,
                },
            },
        };

    private static async Task<RateLimitsResult> WaitForReadReleaseAsync(SemaphoreSlim releaseRead)
    {
        await releaseRead.WaitAsync();
        return UsageResult(5);
    }

    private static Task RaiseConversationEventAsync(ChatViewModel viewModel, ConversationEvent value)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "OnConversationEventAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find OnConversationEventAsync.");

        return (Task)method.Invoke(viewModel, [Notification(value)])!;
    }

    private static Task RaiseContextCompactedAsync(ChatViewModel viewModel, ContextCompactionEvent value)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "OnContextCompactedAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find OnContextCompactedAsync.");

        return (Task)method.Invoke(viewModel, [Notification(value)])!;
    }

    private static ExtensionSettings GetSettings(ChatViewModel viewModel)
    {
        FieldInfo field = typeof(ChatViewModel).GetField(
            "settings",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find settings.");

        return (ExtensionSettings)field.GetValue(viewModel)!;
    }

    [TestMethod]
    public async Task ChatViewModel_RemoteConnectionLoss_EnablesConnectAndRestart()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        Assert.IsFalse(vm.ConnectCommand.CanExecute);
        Assert.IsFalse(vm.RestartCommand.CanExecute);
        var raised = new List<string>();
        vm.ConnectCommand.PropertyChanged += (_, _) => raised.Add("connect");
        vm.RestartCommand.PropertyChanged += (_, _) => raised.Add("restart");

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Degraded,
            Message = "The remote codex app-server connection closed. Reconnect to continue.",
        });

        Assert.IsTrue(vm.IsDegraded);
        Assert.IsTrue(vm.ConnectCommand.CanExecute);
        Assert.IsTrue(vm.RestartCommand.CanExecute);
        CollectionAssert.Contains(raised, "connect");
        CollectionAssert.Contains(raised, "restart");
    }

    [TestMethod]
    public async Task ChatViewModel_ApplySavedRemoteProfile_ReconnectsAndShowsTarget()
    {
        var bridge = new FakeWorkerBridge();
        var store = new MemorySettingsStore(new ExtensionSettings());
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store);
        SetWorkingDirectory(vm, Path.GetTempPath());
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        Assert.AreEqual("Local", vm.ConnectionTargetText);

        vm.RemoteProfiles.AddCommand.Execute(null);
        RemoteProfileViewModel profile = vm.RemoteProfiles.SelectedProfile!;
        profile.Name = "Build box";
        profile.Endpoint = "wss://build.example.invalid";
        profile.TokenFilePath = @"C:\tokens\codex.token";
        profile.LocalRoot = @"C:\repo";
        profile.ServerRoot = "/srv/repo";
        profile.IsEnabled = true;

        // Unsaved edits are never applied.
        await RunCommandAsync(vm.ApplyRemoteProfileCommand);
        Assert.AreEqual(0, bridge.ConnectCallCount);
        Assert.AreEqual("Save the profile before connecting with it.", vm.RemoteProfiles.StatusText);

        vm.RemoteProfiles.SaveCommand.Execute(null);
        await RunCommandAsync(vm.ApplyRemoteProfileCommand);

        Assert.AreEqual(1, bridge.ConnectCallCount);
        Assert.AreEqual("Build box", store.Settings.SelectedRemoteProfileName);
        Assert.AreEqual("Build box", vm.ConnectionTargetText);
        Assert.AreEqual("Connected with remote profile 'Build box'.", vm.RemoteProfiles.StatusText);

        await RunCommandAsync(vm.UseLocalAppServerCommand);

        Assert.AreEqual(2, bridge.ConnectCallCount);
        Assert.IsNull(store.Settings.SelectedRemoteProfileName);
        Assert.AreEqual("Local", vm.ConnectionTargetText);
        Assert.AreEqual(1, store.Settings.RemoteProfiles.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_ConnectionTargetLabel_SaysConnectedOnlyForLiveStates()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        foreach ((WorkerConnectionState state, string expected) in new[]
        {
            (WorkerConnectionState.Ready, "Connected to:"),
            (WorkerConnectionState.Degraded, "Target:"),
            (WorkerConnectionState.Busy, "Connected to:"),
            (WorkerConnectionState.Disconnected, "Target:"),
            (WorkerConnectionState.WaitingForApproval, "Connected to:"),
            (WorkerConnectionState.Connecting, "Target:"),
        })
        {
            raised.Clear();
            await bridge.PublishStateAsync(new WorkerStatus { State = state });
            Assert.AreEqual(expected, vm.ConnectionTargetLabel, state.ToString());
            CollectionAssert.Contains(raised, nameof(ChatViewModel.ConnectionTargetLabel), state.ToString());
        }
    }

    [TestMethod]
    public async Task ChatViewModel_FailedRemoteConnect_OffersReconnectThatRerunsTheSavedProfileConnect()
    {
        var bridge = new FakeWorkerBridge
        {
            ConnectHandler = () => Task.FromException<WorkerStatus>(new InvalidOperationException("worker unavailable")),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(SavedRemoteProfile())));
        SetWorkingDirectory(vm, Path.GetTempPath());

        Assert.IsFalse(await ConnectDirectlyAsync(vm, Path.GetTempPath()));

        Assert.AreEqual(WorkerConnectionState.Degraded, vm.Status.State);
        Assert.AreEqual(ConnectionTargetKind.Remote, vm.Status.Target?.Kind);
        Assert.AreEqual("Build box", vm.Status.Target?.DisplayName);
        Assert.AreEqual(0, vm.Status.Target?.Generation);
        Assert.AreEqual("Reconnect remote app-server", vm.RestartActionText);

        bridge.ConnectHandler = null;
        await RunCommandAsync(vm.RestartCommand);

        Assert.AreEqual(0, bridge.RestartCallCount, "A remote target must never trigger a local restart.");
        Assert.AreEqual(0, bridge.ReconnectRequests.Count, "No generation snapshot exists to reconnect.");
        Assert.AreEqual(2, bridge.ConnectCallCount, "Recovery reruns the saved-profile connect.");
        Assert.AreEqual(WorkerConnectionState.Ready, vm.Status.State);
    }

    [TestMethod]
    public async Task ChatViewModel_ProfileActionQueuedBehindAConnect_StillDispatchesItsConnect()
    {
        var release = new TaskCompletionSource<WorkerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge();
#pragma warning disable VSTHRD003 // The test owns this completion source and releases it below.
        bridge.ConnectHandler = () => release.Task;
#pragma warning restore VSTHRD003
        var store = new MemorySettingsStore(SettingsWith(SavedRemoteProfile()));
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store);
        SetWorkingDirectory(vm, Path.GetTempPath());
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        Task<bool> first = ConnectDirectlyAsync(vm, Path.GetTempPath());
        Task useLocal = RunCommandAsync(vm.UseLocalAppServerCommand);
        bridge.ConnectHandler = null;
        release.SetResult(new WorkerStatus { State = WorkerConnectionState.Ready });
        await first;
        await useLocal;

        Assert.AreEqual(2, bridge.ConnectCallCount, "The queued switch to local must dispatch its own connect.");
        Assert.IsNull(store.Settings.SelectedRemoteProfileName);
        Assert.AreEqual("Local", vm.ConnectionTargetText);
        Assert.AreEqual("Connected to the local codex app-server.", vm.RemoteProfiles.StatusText);
    }

    private static Task<bool> ConnectDirectlyAsync(ChatViewModel viewModel, string directory)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod("ConnectWithDirectoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find ConnectWithDirectoryAsync.");
        return (Task<bool>)method.Invoke(viewModel, [directory, false, false])!;
    }

    [TestMethod]
    public async Task ChatViewModel_ApplyRemoteProfile_ConnectsTheRowItWasInvokedFor()
    {
        RemoteConnectionProfile first = SavedRemoteProfile();
        ExtensionSettings settings = SettingsWith(first);
        settings.RemoteProfiles.Add(new RemoteConnectionProfile
        {
            Name = "Second",
            Endpoint = "wss://second.example.invalid",
            TokenFilePath = @"C:\tokens\second.token",
            LocalRoot = @"C:\second",
            ServerRoot = "/second",
            Enabled = true,
        });
        var store = new MemorySettingsStore(settings);
        var bridge = new FakeWorkerBridge();
        string? selectionAtConnect = null;
        bridge.OnConnect = () => selectionAtConnect = store.Settings.SelectedRemoteProfileName;
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store);
        SetWorkingDirectory(vm, Path.GetTempPath());
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        // Apply waits behind another operation; the user then selects a different row.
        await vm.RemoteProfiles.OperationGate.WaitAsync();
        Task apply = RunCommandAsync(vm.ApplyRemoteProfileCommand);
        vm.RemoteProfiles.SelectedProfile = vm.RemoteProfiles.Profiles[1];
        vm.RemoteProfiles.OperationGate.Release();
        await apply;

        Assert.AreEqual(1, bridge.ConnectCallCount);
        Assert.AreEqual("Build box", selectionAtConnect, "The bridge must read the invoked profile, not the newer selection.");
        Assert.AreEqual("Build box", vm.ConnectionTargetText);
        Assert.AreEqual("Connected with remote profile 'Build box'.", vm.RemoteProfiles.StatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_ApplyRemoteProfile_IsDisabledDuringTurn()
    {
        var bridge = new FakeWorkerBridge();
        var settings = new ExtensionSettings
        {
            SelectedRemoteProfileName = "Build box",
            RemoteProfiles =
            [
                new RemoteConnectionProfile
                {
                    Name = "Build box",
                    Endpoint = "wss://build.example.invalid",
                    TokenFilePath = @"C:\tokens\codex.token",
                    LocalRoot = @"C:\repo",
                    ServerRoot = "/srv/repo",
                    Enabled = true,
                },
            ],
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(settings));
        Assert.AreEqual("Build box", vm.ConnectionTargetText);

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Busy, ThreadId = "t", TurnId = "turn" });
        Assert.IsFalse(vm.ApplyRemoteProfileCommand.CanExecute);
        Assert.IsFalse(vm.UseLocalAppServerCommand.CanExecute);

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "t" });
        Assert.IsTrue(vm.ApplyRemoteProfileCommand.CanExecute);
        Assert.IsTrue(vm.UseLocalAppServerCommand.CanExecute);
    }

    [TestMethod]
    public async Task ChatViewModel_InterruptCommand_FollowsActiveTurnInEveryTurnState()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        // Remote UI refreshes a button only when its command raises CanExecute; the value alone
        // is never polled. Every status change must therefore raise the turn and account commands.
        var raised = 0;
        var accountRaised = 0;
        vm.InterruptCommand.PropertyChanged += (_, _) => raised++;
        vm.AccountCommand.PropertyChanged += (_, _) => accountRaised++;

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "t" });
        Assert.IsFalse(vm.IsTurnActive);
        Assert.IsFalse(vm.InterruptCommand.CanExecute);

        // The Interrupt button is shown whenever a turn is active, so it must also be enabled then,
        // including while the turn waits for an approval.
        foreach (WorkerConnectionState state in new[] { WorkerConnectionState.Busy, WorkerConnectionState.WaitingForApproval })
        {
            await bridge.PublishStateAsync(new WorkerStatus { State = state, ThreadId = "t", TurnId = "turn" });
            Assert.IsTrue(vm.IsTurnActive, state.ToString());
            Assert.IsTrue(vm.InterruptCommand.CanExecute, state.ToString());
        }

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "t" });
        Assert.IsFalse(vm.InterruptCommand.CanExecute);
        Assert.IsTrue(raised >= 4, $"InterruptCommand raised CanExecute {raised} time(s).");
        Assert.IsTrue(accountRaised >= 4, $"AccountCommand raised CanExecute {accountRaised} time(s).");
    }

    [TestMethod]
    public void ChatViewModel_ConnectionTargetFlyout_IsExclusiveWithUsageAndHistory()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));

        vm.IsHistoryOpen = true;
        vm.IsConnectionTargetOpen = true;
        Assert.IsFalse(vm.IsHistoryOpen);

        vm.IsUsageOpen = true;
        Assert.IsFalse(vm.IsConnectionTargetOpen);

        vm.CloseConnectionTargetCommand.Execute(null);
        Assert.IsFalse(vm.IsConnectionTargetOpen);
    }

    // Awaits the command body through the Remote UI entry point instead of fire-and-forget Execute.
    private static RemoteConnectionProfile SavedRemoteProfile() => new()
    {
        Name = "Build box",
        Endpoint = "wss://build.example.invalid",
        TokenFilePath = @"C:\tokens\codex.token",
        LocalRoot = @"C:\repo",
        ServerRoot = "/srv/repo",
        Enabled = true,
    };

    private static WorkerStatus RemoteStatus(RemoteConnectionProfile profile, WorkerConnectionState state, long generation = 7) => new()
    {
        State = state,
        Message = "The remote codex app-server stopped responding. Reconnect to continue.",
        Target = new ConnectionTargetSnapshot
        {
            Kind = ConnectionTargetKind.Remote,
            DisplayName = profile.Name,
            Fingerprint = profile.ComputeFingerprint(),
            Generation = generation,
        },
    };

    private static ExtensionSettings SettingsWith(RemoteConnectionProfile profile) => new()
    {
        RemoteProfiles = [profile],
        SelectedRemoteProfileName = profile.Name,
    };

    [TestMethod]
    public async Task ChatViewModel_RestartAction_DistinguishesLocalRestartFromRemoteReconnect()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Degraded,
            Target = new ConnectionTargetSnapshot { Kind = ConnectionTargetKind.Local, Generation = 1 },
        });
        Assert.AreEqual("Restart local app-server", vm.RestartActionText);
        StringAssert.Contains(vm.RestartActionHelpText, "local codex app-server process");

        await bridge.PublishStateAsync(RemoteStatus(SavedRemoteProfile(), WorkerConnectionState.Degraded));
        Assert.AreEqual("Reconnect remote app-server", vm.RestartActionText);
        StringAssert.Contains(vm.RestartActionHelpText, "remote server itself is not restarted");
        CollectionAssert.Contains(raised, nameof(ChatViewModel.RestartActionText));
        CollectionAssert.Contains(raised, nameof(ChatViewModel.RestartActionHelpText));
    }

    [TestMethod]
    public async Task ChatViewModel_LocalRestart_UsesTheRestartOperation()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Degraded,
            Target = new ConnectionTargetSnapshot { Kind = ConnectionTargetKind.Local, Generation = 1 },
        });

        await RunCommandAsync(vm.RestartCommand);

        Assert.AreEqual(1, bridge.RestartCallCount);
        Assert.AreEqual(0, bridge.ReconnectRequests.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_RemoteReconnect_SendsTheAppliedSnapshotAfterReloadingSettings()
    {
        var bridge = new FakeWorkerBridge();
        RemoteConnectionProfile profile = SavedRemoteProfile();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(profile)));
        await bridge.PublishStateAsync(RemoteStatus(profile, WorkerConnectionState.Degraded, generation: 9));

        await RunCommandAsync(vm.RestartCommand);

        Assert.AreEqual(0, bridge.RestartCallCount, "A remote target is never restarted.");
        RemoteReconnectRequest request = bridge.ReconnectRequests.Single();
        Assert.AreEqual("Build box", request.ProfileName);
        Assert.AreEqual(profile.ComputeFingerprint(), request.Fingerprint);
        Assert.AreEqual(9, request.ExpectedGeneration);
    }

    [TestMethod]
    public async Task ChatViewModel_RemoteReconnect_RejectsChangedRemovedDisabledOrUnsavedProfiles()
    {
        RemoteConnectionProfile applied = SavedRemoteProfile();

        async Task AssertRejectedAsync(ExtensionSettings settings, ConnectionOperationRejectionReason reason, Action<ChatViewModel>? edit = null)
        {
            var bridge = new FakeWorkerBridge();
            using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(settings));
            edit?.Invoke(vm);
            await bridge.PublishStateAsync(RemoteStatus(applied, WorkerConnectionState.Degraded));

            await RunCommandAsync(vm.RestartCommand);

            Assert.AreEqual(0, bridge.ReconnectRequests.Count, reason.ToString());
            Assert.AreEqual(0, bridge.RestartCallCount);
            Assert.AreEqual(ChatViewModel.DescribeRejection(reason), vm.RemoteProfiles.StatusText);
        }

        RemoteConnectionProfile changed = SavedRemoteProfile();
        changed.Endpoint = "wss://other.example.invalid";
        await AssertRejectedAsync(SettingsWith(changed), ConnectionOperationRejectionReason.ProfileChanged);

        RemoteConnectionProfile disabled = SavedRemoteProfile();
        disabled.Enabled = false;
        await AssertRejectedAsync(SettingsWith(disabled), ConnectionOperationRejectionReason.ProfileUnavailable);

        await AssertRejectedAsync(new ExtensionSettings(), ConnectionOperationRejectionReason.ProfileUnavailable);

        await AssertRejectedAsync(
            SettingsWith(SavedRemoteProfile()),
            ConnectionOperationRejectionReason.ProfileChanged,
            vm => vm.RemoteProfiles.SelectedProfile!.ServerRoot = "/srv/edited");
    }

    [TestMethod]
    public async Task ChatViewModel_RemoteReconnect_ShowsTypedWorkerRejection()
    {
        var bridge = new FakeWorkerBridge
        {
            ReconnectException = new StreamJsonRpc.RemoteInvocationException(
                nameof(ConnectionOperationRejectionReason.StaleGeneration),
                WorkerErrorCodes.ConnectionOperationRejected,
                nameof(ConnectionOperationRejectionReason.StaleGeneration)),
        };
        RemoteConnectionProfile profile = SavedRemoteProfile();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(profile)));
        await bridge.PublishStateAsync(RemoteStatus(profile, WorkerConnectionState.Degraded));

        await RunCommandAsync(vm.RestartCommand);

        Assert.AreEqual(1, bridge.ReconnectRequests.Count);
        Assert.AreEqual(ChatViewModel.DescribeRejection(ConnectionOperationRejectionReason.StaleGeneration), vm.RemoteProfiles.StatusText);
        Assert.AreEqual(WorkerConnectionState.Degraded, vm.Status.State);
    }

    [TestMethod]
    public async Task ChatViewModel_HealthCheck_RequiresASavedEnabledProfileWithoutPendingEdits()
    {
        var bridge = new FakeWorkerBridge();
        RemoteConnectionProfile profile = SavedRemoteProfile();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(profile)));
        Assert.IsTrue(vm.CheckProfileHealthCommand.CanExecute);

        // Remote UI does not poll CanExecute, so every transition must also raise a notification.
        int notifications = 0;
        vm.CheckProfileHealthCommand.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(AsyncCommand.CanExecute), StringComparison.Ordinal))
            {
                notifications++;
            }
        };

        void AssertTransition(bool expected, string message)
        {
            Assert.AreEqual(expected, vm.CheckProfileHealthCommand.CanExecute, message);
            Assert.IsTrue(notifications > 0, $"No CanExecute notification: {message}");
            notifications = 0;
        }

        vm.RemoteProfiles.SelectedProfile!.Endpoint = "wss://edited.example.invalid";
        AssertTransition(false, "Pending edits block diagnosis.");

        vm.RemoteProfiles.SelectedProfile.Endpoint = profile.Endpoint;
        AssertTransition(true, "Reverting the edit re-enables diagnosis.");

        vm.RemoteProfiles.SelectedProfile.IsEnabled = false;
        AssertTransition(false, "A disabled profile cannot be diagnosed.");

        vm.RemoteProfiles.SelectedProfile.IsEnabled = true;
        AssertTransition(true, "Re-enabling the profile re-enables diagnosis.");

        vm.RemoteProfiles.AddCommand.Execute(null);
        AssertTransition(false, "An unsaved profile cannot be diagnosed.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task ChatViewModel_HealthCheck_ShowsHealthSeparatelyFromRpcState()
    {
        var bridge = new FakeWorkerBridge();
        RemoteConnectionProfile profile = SavedRemoteProfile();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(profile)));
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            Target = new ConnectionTargetSnapshot { Kind = ConnectionTargetKind.Local, Generation = 1 },
        });
        bool connectEnabled = vm.ConnectCommand.CanExecute;

        await RunCommandAsync(vm.CheckProfileHealthCommand);

        ConnectionDiagnosticsRequest request = bridge.DiagnoseRequests.Single();
        Assert.AreEqual("Build box", request.ProfileName);
        Assert.AreEqual(profile.Endpoint, request.Endpoint);
        Assert.IsTrue(vm.ConnectionHealth.HasResult);
        string observedAt = bridge.DiagnoseObservedAt.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        Assert.AreEqual($"Checked profile: 'Build box' at {observedAt}", vm.ConnectionHealth.CheckedProfileText, "The label names the observation time.");
        StringAssert.StartsWith(vm.ConnectionHealth.HealthText, "Health (/healthz): Healthy (HTTP 200");
        StringAssert.StartsWith(vm.ConnectionHealth.ReadyText, "Ready (/readyz): Unhealthy (HTTP 503");
        Assert.AreEqual("RPC connection: Not connected", vm.ConnectionHealth.RpcText, "An inactive profile is never inferred from health.");
        StringAssert.Contains(vm.ConnectionHealth.ScopeText, "does not prove");
        Assert.AreEqual(connectEnabled, vm.ConnectCommand.CanExecute, "Health never changes what Connect can do.");
        Assert.AreEqual(WorkerConnectionState.Ready, vm.Status.State);
    }

    [TestMethod]
    public async Task ChatViewModel_HealthCheck_RpcRowFollowsTheActiveTargetAndPathScope()
    {
        var bridge = new FakeWorkerBridge
        {
            DiagnoseHandler = request => Task.FromResult(new ConnectionDiagnosticsResult
            {
                ProfileName = request.ProfileName,
                RouteCoverage = RouteCoverage.Unverified,
                Health = new HealthProbeResult { State = HealthProbeState.Healthy, HttpStatus = 200 },
                Ready = new HealthProbeResult { State = HealthProbeState.Healthy, HttpStatus = 200 },
            }),
        };
        RemoteConnectionProfile profile = SavedRemoteProfile();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(profile)));
        await bridge.PublishStateAsync(RemoteStatus(profile, WorkerConnectionState.Degraded));

        await RunCommandAsync(vm.CheckProfileHealthCommand);

        StringAssert.StartsWith(vm.ConnectionHealth.RpcText, "RPC connection: ");
        Assert.AreNotEqual("RPC connection: Not connected", vm.ConnectionHealth.RpcText);
        StringAssert.Contains(vm.ConnectionHealth.ScopeText, "routed path in the endpoint is not verified");
        Assert.AreEqual(WorkerConnectionState.Degraded, vm.Status.State, "A healthy root never upgrades RPC state.");
    }

    [TestMethod]
    public async Task ChatViewModel_HealthCheck_DiscardsStaleCompletionAndClearsOnChanges()
    {
        var pending = new TaskCompletionSource<ConnectionDiagnosticsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The test owns this completion source and releases it below.
        var bridge = new FakeWorkerBridge { DiagnoseHandler = _ => pending.Task };
#pragma warning restore VSTHRD003
        RemoteConnectionProfile profile = SavedRemoteProfile();
        var settings = SettingsWith(profile);
        settings.RemoteProfiles.Add(new RemoteConnectionProfile
        {
            Name = "Second",
            Endpoint = "wss://second.example.invalid",
            TokenFilePath = @"C:\tokens\second.token",
            LocalRoot = @"C:\second",
            ServerRoot = "/second",
            Enabled = true,
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(settings));

        Task check = RunCommandAsync(vm.CheckProfileHealthCommand);
        Assert.IsTrue(vm.ConnectionHealth.IsChecking);
        Assert.IsFalse(vm.CheckProfileHealthCommand.CanExecute, "Duplicate checks are disabled while one runs.");

        // A selection change invalidates the outstanding check; its late completion is dropped.
        vm.RemoteProfiles.SelectedProfile = vm.RemoteProfiles.Profiles[1];
        pending.SetResult(new ConnectionDiagnosticsResult
        {
            ProfileName = profile.Name,
            Health = new HealthProbeResult { State = HealthProbeState.Healthy },
            Ready = new HealthProbeResult { State = HealthProbeState.Healthy },
        });
        await check;

        Assert.IsFalse(vm.ConnectionHealth.HasResult);
        Assert.AreEqual(string.Empty, vm.ConnectionHealth.HealthText);

        // Editing the checked profile's metadata also invalidates an outstanding check.
        var editing = new TaskCompletionSource<ConnectionDiagnosticsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The test owns this completion source and releases it below.
        bridge.DiagnoseHandler = _ => editing.Task;
#pragma warning restore VSTHRD003
        Task edited = RunCommandAsync(vm.CheckProfileHealthCommand);
        Assert.IsTrue(vm.ConnectionHealth.IsChecking);
        vm.RemoteProfiles.SelectedProfile!.Endpoint = "wss://edited.example.invalid";
        editing.SetResult(new ConnectionDiagnosticsResult
        {
            ProfileName = "Second",
            Health = new HealthProbeResult { State = HealthProbeState.Healthy },
            Ready = new HealthProbeResult { State = HealthProbeState.Healthy },
        });
        await edited;

        Assert.IsFalse(vm.ConnectionHealth.HasResult, "A late result for the previously saved endpoint must be discarded.");
        Assert.IsFalse(vm.ConnectionHealth.IsChecking);
        vm.RemoteProfiles.SelectedProfile.Endpoint = "wss://second.example.invalid";

        // A completed result is cleared by a later Save and by a connection generation change.
        bridge.DiagnoseHandler = null;
        await RunCommandAsync(vm.CheckProfileHealthCommand);
        Assert.IsTrue(vm.ConnectionHealth.HasResult);
        await RunCommandAsync(vm.RemoteProfiles.SaveCommand);
        Assert.IsFalse(vm.ConnectionHealth.HasResult);

        await RunCommandAsync(vm.CheckProfileHealthCommand);
        Assert.IsTrue(vm.ConnectionHealth.HasResult);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Ready,
            Target = new ConnectionTargetSnapshot { Kind = ConnectionTargetKind.Local, Generation = 42 },
        });
        Assert.IsFalse(vm.ConnectionHealth.HasResult);
    }

    [TestMethod]
    public void ChatViewModel_ParseRejection_ReadsTypedErrorData()
    {
        var withData = new StreamJsonRpc.RemoteInvocationException("x", WorkerErrorCodes.ConnectionOperationRejected, "ProfileChanged");
        var messageOnly = new StreamJsonRpc.RemoteInvocationException("LocalProcessRequired", WorkerErrorCodes.ConnectionOperationRejected, errorData: null!);

        Assert.AreEqual(ConnectionOperationRejectionReason.ProfileChanged, ChatViewModel.ParseRejection(withData));
        Assert.AreEqual(ConnectionOperationRejectionReason.LocalProcessRequired, ChatViewModel.ParseRejection(messageOnly));
    }

    private static Task RunCommandAsync(AsyncCommand command)
        => ((IAsyncCommand)command).ExecuteAsync(null, null!, CancellationToken.None);

    private static void SetWorkingDirectory(ChatViewModel viewModel, string path)
    {
        FieldInfo field = typeof(ChatViewModel).GetField(
            "workingDirectory",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find workingDirectory.");
        field.SetValue(viewModel, path);
    }

    private static ChatItemViewModel SingleConversationItem(
        ChatViewModel viewModel,
        string itemId,
        ConversationEventKind kind)
        => viewModel.Items.Single(item => item.ItemId == itemId && item.Kind == kind);

    private sealed class FakeFilePickerService(IReadOnlyList<string> files) : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(string? initialDirectory, CancellationToken cancellationToken)
            => Task.FromResult(files);
    }

    private sealed class FakeWorkspaceFileSearchService(IReadOnlyList<WorkspaceFileSearchResult> results)
        : IWorkspaceFileSearchService
    {
        public int CallCount { get; private set; }

        public Func<string, string, CancellationToken, Task<IReadOnlyList<WorkspaceFileSearchResult>>>? SearchHandler { get; set; }

        public Task<IReadOnlyList<WorkspaceFileSearchResult>> SearchAsync(
            string workspaceRoot,
            string query,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (SearchHandler is not null)
            {
                return SearchHandler(workspaceRoot, query, cancellationToken);
            }

            return Task.FromResult(results);
        }
    }

    private static ModelInfo ModelWithReasoning(string id, params string[] efforts)
        => new()
        {
            Id = id,
            DefaultReasoningEffort = efforts.FirstOrDefault(),
            SupportedReasoningEfforts = efforts
                .Select(effort => new ReasoningEffortInfo { Id = effort, Description = $"{effort} description" })
                .ToArray(),
        };

    private sealed class MemorySettingsStore : IExtensionSettingsStore
    {
        public MemorySettingsStore(ExtensionSettings settings)
        {
            Settings = settings;
        }

        public ExtensionSettings Settings { get; private set; }

        public ExtensionSettings Load() => Settings;

        public void Save(ExtensionSettings settings) => Settings = settings;
    }

    private sealed class FakeWorkerBridge : IWorkerBridge
    {
        public event Func<WorkerNotification<WorkerStatus>, Task>? StateChanged;

        public event Func<WorkerNotification<AccountStatus>, Task>? AccountChanged;

        public event Func<WorkerNotification<ConversationEvent>, Task>? ConversationEventReceived { add { } remove { } }

        public event Func<WorkerNotification<ApprovalRequest>, Task>? ApprovalRequested { add { } remove { } }

        public event Func<WorkerNotification<string>, Task>? ApprovalResolved { add { } remove { } }

        public event Func<WorkerNotification<UserInputRequest>, Task>? UserInputRequested { add { } remove { } }

        public event Func<WorkerNotification<string>, Task>? UserInputResolved { add { } remove { } }

        public event Func<WorkerNotification<ContextCompactionEvent>, Task>? ContextCompacted { add { } remove { } }

        public event Func<WorkerNotification<ReviewModeEvent>, Task>? ReviewModeChanged { add { } remove { } }

        public event Func<WorkerNotification<ThreadGoalEvent>, Task>? ThreadGoalChanged { add { } remove { } }

        public event Func<WorkerNotification<RateLimitsResult>, Task>? RateLimitsChanged;

        public event Func<WorkerNotification<SkillsChangedEvent>, Task>? SkillsChanged;

        public event Func<WorkerNotification<ApprovalAuditRecord>, Task>? ApprovalAuditReceived { add { } remove { } }

        private WorkerStatus currentStatus = new();

        public ListModelsResult ModelListResult { get; set; } = new();

        public ListPermissionProfilesResult PermissionProfilesResult { get; set; } = new();

        public int ModelListCallCount { get; private set; }

        public StartTurnRequest? LastStartTurnRequest { get; private set; }

        public SteerTurnRequest? LastSteerTurnRequest { get; private set; }

        public int ReviewCallCount { get; private set; }

        public int RateLimitCallCount { get; private set; }

        public AccountStatus AccountStatusResult { get; set; } = new() { State = AccountState.SignedIn };

        public RateLimitsResult RateLimitsResult { get; set; } = new();

        public ListSkillsResult SkillsResult { get; set; } = new() { IsSupported = true };

        public Func<int, bool, Task<ListSkillsResult>>? SkillsListHandler { get; set; }

        public int SkillsListCallCount { get; private set; }

        public Func<int, Task<RateLimitsResult>>? RateLimitHandler { get; set; }

        public Func<string?, CancellationToken, Task<ThreadPage>>? ListThreadsHandler { get; set; }

        public Exception? ResolveApprovalException { get; set; }

        public Exception? ResolveUserInputException { get; set; }

        public Exception? StartTurnException { get; set; }

        public Task PublishStateAsync(WorkerStatus status)
        {
            currentStatus = status;
            return StateChanged?.Invoke(Notification(status, status)) ?? Task.CompletedTask;
        }

        public Task PublishAccountAsync(AccountStatus status)
            => AccountChanged?.Invoke(Notification(status, currentStatus)) ?? Task.CompletedTask;

        public Task PublishRateLimitsAsync(RateLimitsResult result)
            => RateLimitsChanged?.Invoke(Notification(result, currentStatus)) ?? Task.CompletedTask;

        public Task PublishSkillsChangedAsync(SkillsChangedEvent? value = null)
            => SkillsChanged?.Invoke(Notification(value ?? new SkillsChangedEvent(), currentStatus)) ?? Task.CompletedTask;

        public int ConnectCallCount { get; private set; }

        // Runs when the bridge would read the persisted selection for a connect.
        public Action? OnConnect { get; set; }

        // When set, supplies the connect result (it may throw or wait).
        public Func<Task<WorkerStatus>>? ConnectHandler { get; set; }

        public Task<WorkerStatus> ConnectAsync(string workingDirectory, bool experimentalApi, CancellationToken cancellationToken)
        {
            ConnectCallCount++;
            OnConnect?.Invoke();
            return ConnectHandler?.Invoke() ?? Task.FromResult(new WorkerStatus { State = WorkerConnectionState.Ready });
        }

        public int RestartCallCount { get; private set; }

        public Task<WorkerStatus> RestartAsync(CancellationToken cancellationToken)
        {
            RestartCallCount++;
            return Task.FromResult(new WorkerStatus { State = WorkerConnectionState.Ready });
        }

        public List<RemoteReconnectRequest> ReconnectRequests { get; } = [];

        public Exception? ReconnectException { get; set; }

        public Task<WorkerStatus> ReconnectAsync(RemoteReconnectRequest request, CancellationToken cancellationToken)
        {
            ReconnectRequests.Add(request);
            return ReconnectException is null
                ? Task.FromResult(new WorkerStatus { State = WorkerConnectionState.Ready })
                : Task.FromException<WorkerStatus>(ReconnectException);
        }

        public List<ConnectionDiagnosticsRequest> DiagnoseRequests { get; } = [];

        public Func<ConnectionDiagnosticsRequest, Task<ConnectionDiagnosticsResult>>? DiagnoseHandler { get; set; }

        public DateTimeOffset DiagnoseObservedAt { get; set; } = new(2026, 10, 2, 9, 15, 30, TimeSpan.Zero);

        public Task<ConnectionDiagnosticsResult> DiagnoseConnectionAsync(ConnectionDiagnosticsRequest request, CancellationToken cancellationToken)
        {
            DiagnoseRequests.Add(request);
            return DiagnoseHandler?.Invoke(request) ?? Task.FromResult(new ConnectionDiagnosticsResult
            {
                ProfileName = request.ProfileName,
                ObservedAt = DiagnoseObservedAt,
                Health = new HealthProbeResult { State = HealthProbeState.Healthy, HttpStatus = 200, DurationMilliseconds = 12, Reason = "The route responded successfully." },
                Ready = new HealthProbeResult { State = HealthProbeState.Unhealthy, HttpStatus = 503, DurationMilliseconds = 14, Reason = "The route responded with an error status." },
            });
        }

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(AccountStatusResult);

        public Task<StartAccountLoginResult> StartAccountLoginAsync(StartAccountLoginRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new StartAccountLoginResult());

        public Task<AccountStatus> LogoutAccountAsync(LogoutAccountRequest request, CancellationToken cancellationToken)
        {
            LastLogoutRequest = request;
            return LogoutHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new AccountStatus { State = AccountState.SignedOut });
        }

        public LogoutAccountRequest? LastLogoutRequest { get; private set; }

        public Func<LogoutAccountRequest, CancellationToken, Task<AccountStatus>>? LogoutHandler { get; set; }

        public Task<ThreadPage> ListThreadsAsync(ListThreadsRequest request, CancellationToken cancellationToken)
            => ListThreadsHandler?.Invoke(request.Cursor, cancellationToken) ?? Task.FromResult(new ThreadPage());

        public Task<ListModelsResult> ListModelsAsync(ListModelsRequest request, CancellationToken cancellationToken)
        {
            ModelListCallCount++;
            return Task.FromResult(ModelListResult);
        }

        public Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(ListPermissionProfilesRequest request, CancellationToken cancellationToken)
            => Task.FromResult(PermissionProfilesResult);

        public Task<ThreadSummary> StartThreadAsync(StartThreadRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ThreadSummary { Id = "thread-1" });

        public string? LastResumedThreadId { get; private set; }

        public Task<ThreadSummary> ResumeThreadAsync(ResumeThreadRequest request, CancellationToken cancellationToken)
        {
            LastResumedThreadId = request.ThreadId;
            return Task.FromResult(new ThreadSummary { Id = request.ThreadId });
        }

        public Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
        {
            LastStartTurnRequest = request;
            return StartTurnException is null
                ? Task.FromResult("turn-1")
                : Task.FromException<string>(StartTurnException);
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

        public Task<ThreadGoalResult> GetThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken)
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

        public Task<ThreadGoalResult> ClearThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ThreadGoalResult { Cleared = true });

        public Task<McpServerListResult> ListMcpServersAsync(ListMcpServersRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new McpServerListResult());

        public Task<ListSkillsResult> ListSkillsAsync(ListSkillsRequest request, CancellationToken cancellationToken)
        {
            SkillsListCallCount++;
            return SkillsListHandler?.Invoke(SkillsListCallCount, request.ForceReload) ?? Task.FromResult(SkillsResult);
        }

        public Task<UploadFeedbackResult> UploadFeedbackAsync(UploadFeedbackRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new UploadFeedbackResult { ThreadId = request.ThreadId });

        public Task<RateLimitsResult> GetRateLimitsAsync(GetRateLimitsRequest request, CancellationToken cancellationToken)
        {
            RateLimitCallCount++;
            return RateLimitHandler?.Invoke(RateLimitCallCount) ?? Task.FromResult(RateLimitsResult);
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
