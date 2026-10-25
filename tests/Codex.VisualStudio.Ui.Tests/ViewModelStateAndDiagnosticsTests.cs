using System.IO;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

// Resumed thread settings, MCP status, diagnostic ZIP saving, and compaction outcomes.
public sealed partial class ViewModelTests
{
    private static async Task<ChatViewModel> CreateJoinableViewModelAsync(FakeWorkerBridge bridge, CodexFeatureTier tier, ExtensionSettings? settings = null)
    {
        var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(settings ?? new ExtensionSettings()));
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, FeatureTier = tier });
        vm.SelectedThread = new ThreadSummary { Id = "thread-saved" };
        return vm;
    }

    [TestMethod]
    public async Task Resume_Tier164AppliesSavedSettingsWithNullMeaningDefaults()
    {
        FakeWorkerBridge bridge = CreateModelCommandBridge();
        bridge.ResumeThreadHandler = (request, _) => Task.FromResult(new ThreadSummary
        {
            Id = request.ThreadId,
            EffectiveModelId = "model-b",
            EffectiveReasoningEffort = null,
            EffectiveServiceTier = null,
            HasAuthoritativeTurnSettings = true,
        });
        using ChatViewModel vm = await CreateJoinableViewModelAsync(
            bridge,
            CodexFeatureTier.Tier164,
            new ExtensionSettings { ReasoningEffortId = "low", ServiceTierId = "priority" });

        await RunCommandAsync(vm.JoinThreadCommand);
        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.AreEqual("model-b", vm.SelectedModel);
        Assert.IsFalse(bridge.LastStartTurnRequest!.HasEffort, "A null effort means the model default.");
        Assert.AreEqual("default", bridge.LastStartTurnRequest.ServiceTier, "A null tier means normal speed.");
    }

    [TestMethod]
    public async Task Resume_OlderTierKeepsComposerSettings()
    {
        FakeWorkerBridge bridge = CreateModelCommandBridge();
        bridge.ResumeThreadHandler = (request, _) => Task.FromResult(new ThreadSummary
        {
            Id = request.ThreadId,
            EffectiveModelId = "model-b",
            EffectiveReasoningEffort = "high",
        });
        using ChatViewModel vm = await CreateJoinableViewModelAsync(
            bridge,
            CodexFeatureTier.Tier163,
            new ExtensionSettings { ServiceTierId = "priority" });

        await RunCommandAsync(vm.JoinThreadCommand);
        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.AreEqual("model-a", vm.SelectedModel);
        Assert.AreEqual("priority", bridge.LastStartTurnRequest!.ServiceTier);
        Assert.AreEqual("high", vm.SelectedThread!.EffectiveReasoningEffort);
    }

    [TestMethod]
    public async Task Resume_OlderTierFailureMentionsCompressedHistories()
    {
        var bridge = new FakeWorkerBridge
        {
            ResumeThreadHandler = (_, _) => Task.FromException<ThreadSummary>(
                new StreamJsonRpc.RemoteInvocationException("history could not be loaded", -32000, errorData: null)),
        };
        using ChatViewModel vm = await CreateJoinableViewModelAsync(bridge, CodexFeatureTier.Tier163);

        await RunCommandAsync(vm.JoinThreadCommand);

        StringAssert.Contains(vm.HistoryStatusText, "history could not be loaded");
        StringAssert.Contains(vm.HistoryStatusText, "0.164.0");
        Assert.IsFalse(vm.IsThreadJoined);
        Assert.AreEqual("thread-saved", vm.SelectedThread?.Id, "The history row stays selected.");
    }

    [TestMethod]
    public async Task Resume_OlderTierConnectionFailureMentionsCompressedHistories()
    {
        var bridge = new FakeWorkerBridge
        {
            ResumeThreadHandler = (_, _) => Task.FromException<ThreadSummary>(new IOException("the connection closed")),
        };
        using ChatViewModel vm = await CreateJoinableViewModelAsync(bridge, CodexFeatureTier.Tier163);

        await RunCommandAsync(vm.JoinThreadCommand);

        StringAssert.Contains(vm.HistoryStatusText, "the connection closed");
        StringAssert.Contains(vm.HistoryStatusText, "0.164.0");
        Assert.IsFalse(vm.IsThreadJoined);
    }

    [TestMethod]
    [DataRow(McpRuntimeStatus.Connected, "oAuth", 0, null, "authentication oAuth; runtime connected; 0 tools")]
    [DataRow(McpRuntimeStatus.AuthenticationRequired, "oAuth", 3, null, "authentication sign-in required; runtime authentication required; 3 tools")]
    [DataRow(McpRuntimeStatus.Unknown, "", 1, null, "authentication unknown; runtime unknown; 1 tool")]
    [DataRow(McpRuntimeStatus.Failed, "notLoggedIn", 0, "listing failed", "runtime failed; tool discovery failed: listing failed")]
    public void McpStatus_Tier164ShowsThreeSeparateValues(McpRuntimeStatus runtime, string auth, int tools, string? toolsError, string expected)
    {
        string text = ChatViewModel.FormatMcpServerStatus(new McpServerStatusInfo
        {
            Name = "docs",
            AuthStatus = auth,
            HasRuntimeDetails = true,
            RuntimeStatus = runtime,
            ToolCount = tools,
            ToolsError = toolsError,
        });

        StringAssert.Contains(text, expected);
    }

    [TestMethod]
    public void McpStatus_OlderTierKeepsAuthenticationOnlyFormat()
    {
        string text = ChatViewModel.FormatMcpServerStatus(new McpServerStatusInfo { Name = "docs", AuthStatus = "oAuth", ToolNames = ["a", "b"] });

        Assert.AreEqual("- docs: oAuth; 2 tools, 0 resources, 0 templates", text);
    }

    private static ChatViewModel CreateDiagnosticsViewModel(FakeWorkerBridge bridge, string? savePath)
        => new(
            bridge,
            autoConnect: false,
            filePickerService: new FakeFilePickerService([], savePath));

    [TestMethod]
    public async Task Diagnostics_BelowTier164IsExplainedWithoutRequest()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = CreateDiagnosticsViewModel(bridge, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, FeatureTier = CodexFeatureTier.Tier163 });

        await RunComposerAsync(vm, "/diagnostics");

        Assert.AreEqual(0, bridge.SaveFeedbackRequests.Count);
        Assert.IsTrue(vm.Items.Any(item => item.Text.Contains("0.164.0", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Diagnostics_LocalSaveUsesPickerAndExcludesLogsByDefault()
    {
        string target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = CreateDiagnosticsViewModel(bridge, target);
        string? confirmation = null;
        vm.DiagnosticsConfirmationOverride = message =>
        {
            confirmation = message;
            return Task.FromResult(ChatViewModel.DiagnosticsSaveChoice.WithoutLogs);
        };
        await bridge.PublishStateAsync(Tier164Status());

        await RunComposerAsync(vm, "/diagnostics");

        SaveFeedbackRequest request = bridge.SaveFeedbackRequests.Single();
        Assert.AreEqual(target, request.SavePath);
        Assert.IsFalse(request.IncludeLogs);
        Assert.AreEqual("visual-studio", request.Classification);
        StringAssert.Contains(confirmation, "this computer");
        Assert.IsTrue(vm.Items.Any(item => item.Text.Contains("diagnostic ZIP was saved", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Diagnostics_ExistingLocalPathIsRejectedBeforeRequest()
    {
        string existing = Path.GetTempFileName();
        try
        {
            var bridge = new FakeWorkerBridge();
            using ChatViewModel vm = CreateDiagnosticsViewModel(bridge, existing);
            vm.DiagnosticsConfirmationOverride = _ => Task.FromResult(ChatViewModel.DiagnosticsSaveChoice.WithoutLogs);
            await bridge.PublishStateAsync(Tier164Status());

            await RunComposerAsync(vm, "/diagnostics");

            Assert.AreEqual(0, bridge.SaveFeedbackRequests.Count);
            Assert.IsTrue(vm.Items.Any(item => item.Text.Contains("already exists", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(existing);
        }
    }

    [TestMethod]
    public async Task Diagnostics_RemoteRequiresAbsoluteServerPathAndLabelsResult()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = CreateDiagnosticsViewModel(bridge, savePath: null);
        vm.DiagnosticsConfirmationOverride = _ => Task.FromResult(ChatViewModel.DiagnosticsSaveChoice.WithLogs);
        WorkerStatus remote = Tier164Status();
        remote.Target = new ConnectionTargetSnapshot { Kind = ConnectionTargetKind.Remote, DisplayName = "devbox" };
        await bridge.PublishStateAsync(remote);

        await RunComposerAsync(vm, "/diagnostics relative.zip");
        Assert.AreEqual(0, bridge.SaveFeedbackRequests.Count);

        await RunComposerAsync(vm, "/diagnostics /tmp/codex-diag.zip");

        SaveFeedbackRequest request = bridge.SaveFeedbackRequests.Single();
        Assert.AreEqual("/tmp/codex-diag.zip", request.SavePath);
        Assert.IsTrue(request.IncludeLogs);
        Assert.IsTrue(vm.Items.Any(item => item.Text.Contains("Server path: /tmp/codex-diag.zip", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Diagnostics_UnconfirmedOutcomeIsReportedWithoutResend()
    {
        var bridge = new FakeWorkerBridge
        {
            SaveFeedbackHandler = (_, _) => Task.FromResult(new SaveFeedbackResult
            {
                Outcome = SaveFeedbackOutcome.Unconfirmed,
                Message = "The connection ended before the app-server confirmed the save.",
            }),
        };
        using ChatViewModel vm = CreateDiagnosticsViewModel(bridge, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"));
        vm.DiagnosticsConfirmationOverride = _ => Task.FromResult(ChatViewModel.DiagnosticsSaveChoice.WithoutLogs);
        await bridge.PublishStateAsync(Tier164Status());

        await RunComposerAsync(vm, "/diagnostics");

        Assert.AreEqual(1, bridge.SaveFeedbackRequests.Count);
        Assert.IsTrue(vm.Items.Any(item => item.Role == "Error" && item.Text.Contains("confirmed the save", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Diagnostics_CancelSendsNothing()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = CreateDiagnosticsViewModel(bridge, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"));
        vm.DiagnosticsConfirmationOverride = _ => Task.FromResult(ChatViewModel.DiagnosticsSaveChoice.Cancel);
        await bridge.PublishStateAsync(Tier164Status());

        await RunComposerAsync(vm, "/diagnostics");

        Assert.AreEqual(0, bridge.SaveFeedbackRequests.Count);
    }

    private static async Task<ChatViewModel> CreateCompactionViewModelAsync(FakeWorkerBridge bridge)
    {
        var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await RunCommandAsync(vm.JoinThreadCommand);
        return vm;
    }

    [TestMethod]
    public async Task Compaction_RpcErrorIsReportedAsFailure()
    {
        var bridge = new FakeWorkerBridge
        {
            CompactThreadHandler = (_, _) => Task.FromException<CompactThreadResult>(new InvalidOperationException("boom")),
        };
        using ChatViewModel vm = await CreateCompactionViewModelAsync(bridge);

        await RunComposerAsync(vm, "/compact");

        Assert.IsTrue(vm.Items.Any(item => item.Text.Contains(ChatViewModel.CompactionFailedText, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Compaction_TurnEndingWithoutCompactionIsReportedAsFailure()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreateCompactionViewModelAsync(bridge);

        await RunComposerAsync(vm, "/compact");
        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "thread-1",
            TurnId = "turn-compact",
        });

        Assert.AreEqual(1, vm.Items.Count(item => item.Text == ChatViewModel.CompactionFailedText));
    }

    [TestMethod]
    public async Task Compaction_CompletedCompactionIsNotReportedAsFailure()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreateCompactionViewModelAsync(bridge);

        await RunComposerAsync(vm, "/compact");
        await RaiseContextCompactedAsync(vm, new ContextCompactionEvent { ThreadId = "thread-1", IsCompleted = true });
        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "thread-1",
            TurnId = "turn-compact",
        });

        Assert.IsFalse(vm.Items.Any(item => item.Text == ChatViewModel.CompactionFailedText));
    }

    [TestMethod]
    public async Task Compaction_FailureNotifiedBeforeTheRequestReturnsIsReportedOnce()
    {
        var bridge = new FakeWorkerBridge();
        bridge.CompactThreadHandler = async (_, _) =>
        {
            await bridge.PublishConversationEventAsync(new ConversationEvent
            {
                Kind = ConversationEventKind.TurnCompleted,
                ThreadId = "thread-1",
                TurnId = "turn-compact",
            });
            return new CompactThreadResult();
        };
        using ChatViewModel vm = await CreateCompactionViewModelAsync(bridge);

        await RunComposerAsync(vm, "/compact");
        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "thread-1",
            TurnId = "turn-later",
        });

        Assert.AreEqual(1, vm.Items.Count(item => item.Text == ChatViewModel.CompactionFailedText),
            "The early failure is reported, and a later unrelated turn is not.");
    }
}
