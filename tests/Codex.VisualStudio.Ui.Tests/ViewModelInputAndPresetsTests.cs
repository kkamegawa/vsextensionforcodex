using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

// /model arguments, the /permissions queue hold, and presets (0.164 tier).
public sealed partial class ViewModelTests
{
    private static readonly string[] CliPresetSlotOrder = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];

    private static FakeWorkerBridge CreateModelCommandBridge()
        => new()
        {
            ModelListResult = new ListModelsResult
            {
                Models =
                [
                    new ModelInfo
                    {
                        Id = "model-a",
                        DefaultReasoningEffort = "medium",
                        SupportedReasoningEfforts = [new ReasoningEffortInfo { Id = "low" }, new ReasoningEffortInfo { Id = "medium" }],
                        ServiceTiers = [new ServiceTierInfo { Id = "priority", Name = "Fast" }],
                    },
                    new ModelInfo
                    {
                        Id = "model-b",
                        DefaultReasoningEffort = "high",
                        SupportedReasoningEfforts = [new ReasoningEffortInfo { Id = "high" }, new ReasoningEffortInfo { Id = "xhigh" }],
                    },
                ],
                DefaultModel = "model-a",
            },
        };

    private static async Task<ChatViewModel> CreateModelCommandViewModelAsync(FakeWorkerBridge bridge, ExtensionSettings? settings = null)
    {
        var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(settings ?? new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        return vm;
    }

    private static async Task RunComposerAsync(ChatViewModel vm, string text)
    {
        vm.ComposerText = text;
        await InvokeComposerSendAsync(vm);
    }

    [TestMethod]
    public async Task ModelCommand_AppliesModelReasoningAndSpeedTogether()
    {
        FakeWorkerBridge bridge = CreateModelCommandBridge();
        using ChatViewModel vm = await CreateModelCommandViewModelAsync(bridge);

        await RunComposerAsync(vm, "/model model-b --reasoning xhigh");
        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.AreEqual("model-b", bridge.LastStartTurnRequest!.Model);
        Assert.AreEqual("xhigh", bridge.LastStartTurnRequest.Effort);
    }

    [TestMethod]
    public async Task ModelCommand_NamedModelUsesItsDefaultEffort()
    {
        FakeWorkerBridge bridge = CreateModelCommandBridge();
        using ChatViewModel vm = await CreateModelCommandViewModelAsync(bridge, new ExtensionSettings { ReasoningEffortId = "low" });

        await RunComposerAsync(vm, "/model model-b");
        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.AreEqual("high", bridge.LastStartTurnRequest!.Effort);
    }

    [TestMethod]
    public async Task ModelCommand_StandardSpeedIsSentAsDefault()
    {
        FakeWorkerBridge bridge = CreateModelCommandBridge();
        using ChatViewModel vm = await CreateModelCommandViewModelAsync(bridge, new ExtensionSettings { ServiceTierId = "priority" });

        await RunComposerAsync(vm, "/model --speed standard");
        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.IsTrue(bridge.LastStartTurnRequest!.HasServiceTier);
        Assert.AreEqual("default", bridge.LastStartTurnRequest.ServiceTier);
    }

    [TestMethod]
    public async Task ModelCommand_SpeedMatchesCatalogNameCaseInsensitively()
    {
        FakeWorkerBridge bridge = CreateModelCommandBridge();
        using ChatViewModel vm = await CreateModelCommandViewModelAsync(bridge);

        await RunComposerAsync(vm, "/model --speed fast");
        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.AreEqual("priority", bridge.LastStartTurnRequest!.ServiceTier);
        Assert.AreEqual("priority", vm.SelectedServiceTierId);
    }

    [TestMethod]
    public async Task ModelCommand_UnsupportedCurrentSpeedFallsBackToStandardWithNotice()
    {
        FakeWorkerBridge bridge = CreateModelCommandBridge();
        using ChatViewModel vm = await CreateModelCommandViewModelAsync(bridge, new ExtensionSettings { ServiceTierId = "priority" });

        await RunComposerAsync(vm, "/model model-b");
        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.AreEqual("default", bridge.LastStartTurnRequest!.ServiceTier);
        Assert.IsTrue(vm.Items.Any(item => item.Text.Contains("standard speed is used", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("/model model-b --reasoning low")]
    [DataRow("/model model-a --speed turbo")]
    [DataRow("/model missing-model")]
    [DataRow("/model model-b --bogus x")]
    public async Task ModelCommand_ErrorChangesNothing(string command)
    {
        FakeWorkerBridge bridge = CreateModelCommandBridge();
        using ChatViewModel vm = await CreateModelCommandViewModelAsync(bridge, new ExtensionSettings { ReasoningEffortId = "low", ServiceTierId = "priority" });

        await RunComposerAsync(vm, command);
        await SendMessageAsync(vm, "hello", clearComposer: false);

        Assert.AreEqual("model-a", vm.SelectedModel);
        Assert.AreEqual("low", bridge.LastStartTurnRequest!.Effort);
        Assert.AreEqual("priority", bridge.LastStartTurnRequest.ServiceTier);
        Assert.IsTrue(vm.Items.Any(item => item.Role == "Error"));
    }

    [TestMethod]
    public async Task ModelCommand_SupersedesConsumedFastRestore()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelListResult = new ListModelsResult
            {
                Models = [new ModelInfo { Id = "gpt-5", ServiceTiers = [new ServiceTierInfo { Id = "fast" }] }],
                DefaultModel = "gpt-5",
            },
        };
        using ChatViewModel vm = await CreateModelCommandViewModelAsync(bridge);

        await RunComposerAsync(vm, "/fast");
        await SendMessageAsync(vm, "first", clearComposer: false);
        Assert.AreEqual("fast", bridge.LastStartTurnRequest!.ServiceTier);

        await RunComposerAsync(vm, "/model --speed flex");
        await SendMessageAsync(vm, "second", clearComposer: false);

        Assert.AreEqual("flex", bridge.LastStartTurnRequest!.ServiceTier);
    }

    [TestMethod]
    public async Task PermissionsConfirmation_HoldsQueuedCommandsUntilResolved()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await RunCommandAsync(vm.JoinThreadCommand);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Busy, ThreadId = "thread-1", TurnId = "turn-1" });

        await RunComposerAsync(vm, "/permissions full");
        await RunComposerAsync(vm, "/review uncommitted");
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await WaitForAsync(() => vm.HasApprovalModeConfirmation);

        Assert.AreEqual(0, bridge.ReviewCallCount, "A queued command must wait for the permission confirmation.");

        await RunCommandAsync(vm.CancelApprovalModeCommand);
        await WaitForAsync(() => bridge.ReviewCallCount == 1);
    }

    private static WorkerStatus Tier164Status(WorkerConnectionState state = WorkerConnectionState.Ready)
        => new() { State = state, FeatureTier = CodexFeatureTier.Tier164, CodexVersion = SupportedCodexVersions.Target };

    private static ListPresetsResult PresetsResult(string version = "v1")
        => new()
        {
            UserLayerVersion = version,
            Slots =
            [
                new PresetSlotInfo { Slot = "1", Input = "/model model-b --reasoning 'x high'", Label = "Deep", SourceLayer = PresetLayerKind.User, CanEdit = true, CanDelete = true },
                new PresetSlotInfo { Slot = "2", Input = "Review <b>this</b>", SourceLayer = PresetLayerKind.Project, CanEdit = false, CanDelete = false },
            ],
        };

    [TestMethod]
    public async Task Presets_OpenListsTenSlotsInCliOrder()
    {
        var bridge = new FakeWorkerBridge { ListPresetsHandler = (_, _) => Task.FromResult(PresetsResult()) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(Tier164Status());

        await RunCommandAsync(vm.OpenPresetsCommand);

        Assert.IsTrue(vm.IsPresetsPanelOpen);
        CollectionAssert.AreEqual(CliPresetSlotOrder, vm.PresetSlots.Select(slot => slot.Slot).ToArray());
        Assert.AreEqual("Deep", vm.PresetSlots[0].Title);
        Assert.IsTrue(vm.PresetSlots[0].CanEdit && vm.PresetSlots[0].CanDelete);
        Assert.IsFalse(vm.PresetSlots[1].CanEdit || vm.PresetSlots[1].CanDelete);
        Assert.AreEqual("Project config", vm.PresetSlots[1].SourceText);
        Assert.IsFalse(vm.PresetSlots[2].IsDefined);
        Assert.IsTrue(vm.ShowPresetSlots);
    }

    [TestMethod]
    public async Task Presets_BelowTier164ExplainsRequirementWithoutReading()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, FeatureTier = CodexFeatureTier.Tier163 });

        await RunComposerAsync(vm, "/presets");

        Assert.IsTrue(vm.IsPresetsPanelOpen);
        Assert.IsTrue(vm.ShowPresetsTierNotice);
        Assert.AreEqual(0, bridge.ListPresetsCallCount);
        Assert.IsFalse(vm.ShowPresetSlots);
    }

    [TestMethod]
    public async Task Presets_RecallFillsEmptyComposerWithRawInputWithoutSending()
    {
        var bridge = new FakeWorkerBridge { ListPresetsHandler = (_, _) => Task.FromResult(PresetsResult()) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(Tier164Status());
        await RunCommandAsync(vm.OpenPresetsCommand);

        await RunCommandAsync(vm.PresetSlots[0].RecallCommand);

        Assert.AreEqual("/model model-b --reasoning 'x high'", vm.ComposerText);
        Assert.AreEqual(0, bridge.StartTurnCallCount);
        Assert.IsFalse(vm.IsPresetsPanelOpen);
    }

    [TestMethod]
    public async Task Presets_RecallConfirmsBeforeReplacingDraft()
    {
        var bridge = new FakeWorkerBridge { ListPresetsHandler = (_, _) => Task.FromResult(PresetsResult()) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(Tier164Status());
        await RunCommandAsync(vm.OpenPresetsCommand);
        vm.ComposerText = "my draft";

        await RunCommandAsync(vm.PresetSlots[1].RecallCommand);
        Assert.IsTrue(vm.HasPresetConfirmation);
        Assert.AreEqual("my draft", vm.ComposerText);

        await RunCommandAsync(vm.CancelPresetConfirmationCommand);
        Assert.AreEqual("my draft", vm.ComposerText);

        await RunCommandAsync(vm.PresetSlots[1].RecallCommand);
        await RunCommandAsync(vm.ConfirmPresetCommand);
        Assert.AreEqual("Review <b>this</b>", vm.ComposerText, "Recall keeps the raw input; only the display is sanitized.");
    }

    [TestMethod]
    public async Task Presets_ReplaceRequiresConfirmationAndSendsExpectedVersion()
    {
        var bridge = new FakeWorkerBridge { ListPresetsHandler = (_, _) => Task.FromResult(PresetsResult("v7")) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(Tier164Status());
        await RunCommandAsync(vm.OpenPresetsCommand);

        await RunCommandAsync(vm.PresetSlots[0].EditCommand);
        Assert.AreEqual("/model model-b --reasoning 'x high'", vm.PresetEditorInput);
        vm.PresetEditorInput = "  keep\nwhitespace  ";
        vm.PresetEditorLabel = "Renamed";
        await RunCommandAsync(vm.SavePresetCommand);

        Assert.IsTrue(vm.HasPresetConfirmation);
        Assert.AreEqual(0, bridge.WritePresetRequests.Count);

        await RunCommandAsync(vm.ConfirmPresetCommand);

        WritePresetRequest request = bridge.WritePresetRequests.Single();
        Assert.AreEqual("1", request.Slot);
        Assert.AreEqual("  keep\nwhitespace  ", request.Input);
        Assert.AreEqual("Renamed", request.Label);
        Assert.AreEqual("v7", request.ExpectedVersion);
        Assert.IsFalse(vm.IsPresetEditorOpen);
        Assert.AreEqual(2, bridge.ListPresetsCallCount, "A successful write re-reads the slots.");
    }

    [TestMethod]
    public async Task Presets_ConflictKeepsEditorContentsAndRereads()
    {
        var bridge = new FakeWorkerBridge
        {
            ListPresetsHandler = (_, _) => Task.FromResult(PresetsResult()),
            WritePresetHandler = (_, _) => Task.FromResult(new WritePresetResult { Outcome = WritePresetOutcome.Conflict, Message = "The presets changed." }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(Tier164Status());
        await RunCommandAsync(vm.OpenPresetsCommand);

        await RunCommandAsync(vm.PresetSlots[2].EditCommand);
        vm.PresetEditorInput = "new preset";
        await RunCommandAsync(vm.SavePresetCommand);

        Assert.AreEqual(1, bridge.WritePresetRequests.Count, "An empty slot is written without a replace confirmation.");
        Assert.IsTrue(vm.IsPresetEditorOpen);
        Assert.AreEqual("new preset", vm.PresetEditorInput);
        Assert.AreEqual("The presets changed.", vm.PresetsStatusText);
        Assert.AreEqual(2, bridge.ListPresetsCallCount);
    }

    [TestMethod]
    public async Task Presets_DeleteConfirmsAndWritesNull()
    {
        var bridge = new FakeWorkerBridge { ListPresetsHandler = (_, _) => Task.FromResult(PresetsResult()) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(Tier164Status());
        await RunCommandAsync(vm.OpenPresetsCommand);

        await RunCommandAsync(vm.PresetSlots[0].DeleteCommand);
        Assert.AreEqual(0, bridge.WritePresetRequests.Count);
        await RunCommandAsync(vm.ConfirmPresetCommand);

        Assert.IsNull(bridge.WritePresetRequests.Single().Input);
        Assert.IsFalse(vm.PresetSlots[1].DeleteCommand.CanExecute);
    }

    [TestMethod]
    [DataRow("   ")]
    [DataRow("  !rm -rf")]
    public async Task Presets_InvalidInputIsRejectedLocally(string input)
    {
        var bridge = new FakeWorkerBridge { ListPresetsHandler = (_, _) => Task.FromResult(PresetsResult()) };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(Tier164Status());
        await RunCommandAsync(vm.OpenPresetsCommand);

        await RunCommandAsync(vm.PresetSlots[2].EditCommand);
        vm.PresetEditorInput = input;
        await RunCommandAsync(vm.SavePresetCommand);

        Assert.AreEqual(0, bridge.WritePresetRequests.Count);
        Assert.IsTrue(vm.HasPresetsStatus);
    }

    [TestMethod]
    public async Task Presets_StaleListResultIsIgnoredAfterConnectionChange()
    {
        var pending = new TaskCompletionSource<ListPresetsResult>();
#pragma warning disable VSTHRD003 // The fake holds the read until the connection generation changes.
        var bridge = new FakeWorkerBridge { ListPresetsHandler = (_, _) => pending.Task };
#pragma warning restore VSTHRD003
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        WorkerStatus first = Tier164Status();
        first.Target = new ConnectionTargetSnapshot { Generation = 1 };
        await bridge.PublishStateAsync(first);

        Task opening = RunCommandAsync(vm.OpenPresetsCommand);
        WorkerStatus second = Tier164Status();
        second.Target = new ConnectionTargetSnapshot { Generation = 2 };
        await bridge.PublishStateAsync(second);
        pending.SetResult(PresetsResult());
        await opening;

        Assert.AreEqual(0, vm.PresetSlots.Count);
    }
}
