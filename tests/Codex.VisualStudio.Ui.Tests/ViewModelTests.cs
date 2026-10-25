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
public sealed partial class ViewModelTests
{
    private static readonly string[] ExpectedThreadIdsAfterDuplicatePages = ["thread-a", "thread-b"];
    private static readonly string[] ExpectedRowIdsAfterSelectedThreadLeavesPage = ["thread-b", "thread-c"];

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
    private static readonly string[] IndependentQuestionIds = ["ui-1", "ui-2"];
    private static readonly string[] IndependentApprovalIds = ["req-1", "req-2", "req-3"];
    private static readonly string[] ExpectedCreativeOptionIds = ["creative-id"];
    private static readonly string[] ExpectedModeChoiceIds = ["mode-id"];

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

    private static readonly string[] ExpectedWindowsSandboxBackendIds = ["follow", "preferMxc", "requireMxc", "disableMxc"];

    [TestMethod]
    public void WindowsSandboxBackendSelection_PersistsStableIdAndReportsPendingRestart()
    {
        var bridge = new FakeWorkerBridge();
        var store = new MemorySettingsStore(new ExtensionSettings());
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store);
        CollectionAssert.AreEqual(ExpectedWindowsSandboxBackendIds, vm.WindowsSandboxBackends.Select(option => option.Id).ToArray());
        Assert.AreEqual("follow", vm.SelectedWindowsSandboxBackendId);
        Assert.IsFalse(vm.HasWindowsSandboxBackendPending, "Nothing is pending before a local start.");

        MethodInfo setConnected = typeof(ChatViewModel).GetMethod("SetConnectedSandboxBackend", BindingFlags.NonPublic | BindingFlags.Instance)!;
        setConnected.Invoke(vm, ["follow"]);
        vm.SelectedWindowsSandboxBackendId = "requireMxc";

        Assert.AreEqual("requireMxc", store.Settings.WindowsSandboxBackendId);
        StringAssert.Contains(vm.WindowsSandboxBackendHelpText, "Applies the next time the local app-server starts.");
        Assert.IsTrue(vm.HasWindowsSandboxBackendPending);
        StringAssert.Contains(vm.WindowsSandboxBackendPendingText, "\"Follow Codex configuration\"");

        vm.SelectedWindowsSandboxBackendId = "unknown-backend";
        Assert.AreEqual("requireMxc", store.Settings.WindowsSandboxBackendId, "Unknown IDs are ignored.");

        setConnected.Invoke(vm, [null]);
        Assert.IsFalse(vm.HasWindowsSandboxBackendPending, "Remote connections never apply the local backend.");
    }

    [TestMethod]
    public async Task WindowsSandboxStatus_ShowsWorkerDiagnosisAndSetupReadiness()
    {
        var bridge = new FakeWorkerBridge
        {
            WindowsSandboxReadinessHandler = (_, _) => Task.FromResult(new WindowsSandboxReadinessResult
            {
                IsWindows = true,
                IsLocalStdio = true,
                Status = "ready",
                AppliedBackendPreference = WindowsSandboxBackendPreference.PreferMxc,
                BackendNotice = "Managed policy allows MXC.",
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        Assert.IsTrue(vm.ShowWindowsSandboxPanel);

        MethodInfo check = typeof(ChatViewModel).GetMethod("CheckWindowsSandboxReadinessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)check.Invoke(vm, null)!;

        Assert.AreEqual("Managed policy allows MXC.", vm.WindowsSandboxBackendStatusText);
        Assert.AreEqual("Setup readiness: ready", vm.WindowsSandboxStatusText);
    }

    [TestMethod]
    public async Task WindowsSandboxPanel_StaysAvailableAfterAFailedLocalStart()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        WorkerStatus degraded = OwnerStatus("owner-a", 1, 4);
        degraded.State = WorkerConnectionState.Degraded;
        await bridge.PublishStateAsync(degraded);

        Assert.IsTrue(vm.ShowWindowsSandboxPanel, "The backend can be changed after \"Require MXC\" prevented startup.");
        Assert.IsFalse(vm.ShowWindowsSandboxSetup, "Legacy setup needs a ready connection.");
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
    public async Task ThreadReadState_SelectionDoesNotMarkReadAndExplicitActionUsesRevision()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a", Preview = "Unread conversation" };

        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot
        {
            Kind = ThreadReadStateKind.UnreadTurn,
            Revision = "revision-1",
            TurnId = "turn-opaque",
        });
        await Task.Delay(20);

        Assert.AreEqual(0, bridge.UpdateThreadReadStateCallCount, "Selecting or loading a conversation must not mark it read.");
        Assert.IsTrue(vm.ThreadRows.Single(row => row.Id == thread.Id).IsUnread);
        Assert.IsTrue(vm.MarkSelectedThreadReadCommand.CanExecute);
        Assert.IsFalse(vm.MarkSelectedThreadUnreadCommand.CanExecute);

        await RunCommandAsync(vm.MarkSelectedThreadReadCommand);

        Assert.AreEqual(1, bridge.UpdateThreadReadStateCallCount);
        Assert.AreEqual(thread.Id, bridge.LastUpdateThreadReadStateRequest!.ThreadId);
        Assert.AreEqual("revision-1", bridge.LastUpdateThreadReadStateRequest.ExpectedRevision);
        Assert.IsTrue(bridge.LastUpdateThreadReadStateRequest.MarkRead);
        Assert.AreEqual(ThreadReadStateKind.Read, vm.SelectedThreadReadState!.Kind);
        Assert.IsTrue(vm.MarkSelectedThreadUnreadCommand.CanExecute);
    }

    [TestMethod]
    public async Task ThreadReadState_ControlsAreRepublishedAfterTheUpdateLeavesFlight()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.UnreadTurn, Revision = "revision-1" });

        // Remote UI reads a bound value when PropertyChanged is raised, so the last published value
        // is what the Visibility binding shows.
        bool? publishedUnreadVisibility = null;
        bool? publishedReadVisibility = null;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.CanMarkSelectedThreadUnread))
            {
                publishedUnreadVisibility = vm.CanMarkSelectedThreadUnread;
            }
            else if (args.PropertyName == nameof(ChatViewModel.CanMarkSelectedThreadRead))
            {
                publishedReadVisibility = vm.CanMarkSelectedThreadRead;
            }
        };

        await RunCommandAsync(vm.MarkSelectedThreadReadCommand);

        Assert.IsTrue(publishedUnreadVisibility, "\"Mark as unread\" must be published as visible once the update completes.");
        Assert.IsFalse(publishedReadVisibility);
    }

    [TestMethod]
    public async Task ThreadReadState_OwnUpdateNotificationIsNotReportedAsConcurrentChange()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        var pendingUpdate = new TaskCompletionSource<UpdateThreadReadStateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The fake holds the response so the server notification can arrive first.
        bridge.UpdateThreadReadStateHandler = (_, _) => pendingUpdate.Task;
#pragma warning restore VSTHRD003
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.UnreadTurn, Revision = "revision-1" });
        int readsBeforeUpdate = bridge.ReadThreadCallCount;
        var committed = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.Read, Revision = "revision-2" };

        Task update = RunCommandAsync(vm.MarkSelectedThreadReadCommand);
        await WaitForAsync(() => bridge.UpdateThreadReadStateCallCount == 1);
        await bridge.PublishThreadReadStateChangedAsync(new ThreadReadStateChangedEvent { ThreadId = thread.Id, State = committed });
        pendingUpdate.SetResult(new UpdateThreadReadStateResult { Succeeded = true, State = committed });
        await update;

        Assert.AreEqual(readsBeforeUpdate, bridge.ReadThreadCallCount, "The client's own committed receipt needs no reread.");
        Assert.AreEqual("Conversation marked as read.", vm.ThreadReadStateStatusText);

        // A notification that arrives after the response carries the same receipt.
        await bridge.PublishThreadReadStateChangedAsync(new ThreadReadStateChangedEvent { ThreadId = thread.Id, State = committed });

        Assert.AreEqual("Conversation marked as read.", vm.ThreadReadStateStatusText);
        Assert.AreEqual("revision-2", vm.SelectedThreadReadState!.Revision);
    }

    [TestMethod]
    public async Task ThreadReadState_UnknownUpdateOutcomeRereadsInsteadOfResending()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        bridge.UpdateThreadReadStateHandler = (_, _) => Task.FromResult(new UpdateThreadReadStateResult { OutcomeUnknown = true });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.UnreadTurn, Revision = "revision-1" });
        bridge.ReadThreadHandler = (request, _) => Task.FromResult(new ThreadReadResult
        {
            Thread = new ThreadSummary { Id = request.ThreadId },
            ReadState = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.Read, Revision = "committed-revision" },
        });
        int readsBeforeUpdate = bridge.ReadThreadCallCount;

        await RunCommandAsync(vm.MarkSelectedThreadReadCommand);

        Assert.AreEqual(1, bridge.UpdateThreadReadStateCallCount, "An unknown outcome must not resend the update.");
        Assert.AreEqual(readsBeforeUpdate + 1, bridge.ReadThreadCallCount);
        Assert.AreEqual("committed-revision", vm.SelectedThreadReadState!.Revision);
        StringAssert.Contains(vm.ThreadReadStateStatusText, "could not be confirmed");
        Assert.IsTrue(vm.MarkSelectedThreadUnreadCommand.CanExecute);
    }

    [TestMethod]
    public async Task ThreadReadState_SelectionKeepsUnreadMarkerWhenHistoryReadFails()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.UnreadTurn, Revision = "revision-1" });
        bridge.ReadThreadHandler = (_, _) => throw new IOException("simulated history failure");

        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);

        Assert.IsTrue(vm.ThreadRows.Single().IsUnread, "Selecting a conversation must not hide its unread marker.");
        Assert.AreEqual("revision-1", vm.SelectedThreadReadState!.Revision);
    }

    [TestMethod]
    public async Task ThreadRows_MirrorListOrderWhenSelectedThreadLeavesTheFirstPage()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        bridge.ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
        {
            Threads = [new ThreadSummary { Id = "thread-a" }, new ThreadSummary { Id = "thread-b" }],
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        await InvokeThreadListMethodAsync(vm, "LoadMoreAsync");
        vm.SelectedThreadRow = vm.ThreadRows.First();
        await WaitForHistoryLoadAsync(vm);
        ApplyThreadReadState(vm, "thread-a", new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.UnreadTurn, Revision = "revision-1" });
        bridge.ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
        {
            Threads = [new ThreadSummary { Id = "thread-b" }, new ThreadSummary { Id = "thread-c" }],
        });

        await InvokeThreadListMethodAsync(vm, "ReloadThreadsAsync");

        CollectionAssert.AreEqual(ExpectedRowIdsAfterSelectedThreadLeavesPage, vm.ThreadRows.Select(row => row.Id).ToArray());
        Assert.AreEqual("thread-a", vm.SelectedThread?.Id);
        Assert.AreEqual("revision-1", vm.SelectedThreadReadState?.Revision, "The selected row keeps its read state while off the loaded pages.");
    }

    [TestMethod]
    public async Task ThreadList_RetiredOwnerRequestNeitherBlocksNorStalesTheNewOwnerList()
    {
        var bridge = new FakeWorkerBridge();
        var oldOwnerPage = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The fake holds the retired owner's synchronization until the new owner has loaded.
        bridge.ListThreadsHandler = (_, _) => oldOwnerPage.Task;
#pragma warning restore VSTHRD003
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        Task oldSynchronization = (Task)typeof(ChatViewModel).GetMethod("SynchronizeRecoveredThreadListAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [CancellationToken.None])!;
        await WaitForAsync(() => bridge.ListThreadsCallCount == 1);

        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));
        bridge.ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
        {
            Threads = [new ThreadSummary { Id = "new-owner-thread" }],
            NextCursor = "next-page",
        });
        await InvokeThreadListMethodAsync(vm, "LoadMoreAsync");

        Assert.AreEqual(2, bridge.ListThreadsCallCount, "A request of the retired owner must not suppress the new owner's first page.");
        Assert.AreEqual("new-owner-thread", vm.Threads.Single().Id);

        oldOwnerPage.SetResult(new ThreadPage { Threads = [new ThreadSummary { Id = "old-owner-thread" }] });
        await oldSynchronization;

        Assert.AreEqual("new-owner-thread", vm.Threads.Single().Id);
        Assert.IsTrue(vm.LoadMoreCommand.CanExecute, "A superseded synchronization must not mark the new owner's list stale.");
        Assert.IsFalse(vm.RecoveryStatusText.Contains("could not be synchronized", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ThreadReadState_ListRowsDisplayUnreadStateAndKeepSelectionByIdAfterReload()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        bridge.ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
        {
            Threads = [new ThreadSummary { Id = "thread-a", Preview = "Updated title" }],
            ReadStatesAvailable = true,
            ReadStates = new Dictionary<string, ThreadReadStateSnapshot>(StringComparer.Ordinal)
            {
                ["thread-a"] = new() { Kind = ThreadReadStateKind.UnreadTurn, Revision = "revision-1" },
            },
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        Task load = (Task)typeof(ChatViewModel).GetMethod("LoadMoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null)!;
        await load;

        ThreadHistoryRowViewModel row = vm.ThreadRows.Single();
        Assert.AreEqual("thread-a", row.Id);
        Assert.IsTrue(row.IsUnread);
        vm.SelectedThreadRow = row;

        Task reload = (Task)typeof(ChatViewModel).GetMethod("ReloadThreadsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null)!;
        await reload;

        Assert.AreEqual("thread-a", vm.SelectedThread?.Id);
        Assert.AreEqual("thread-a", vm.SelectedThreadRow?.Id);
        Assert.AreEqual("Updated title\n", vm.ThreadRows.Single().Preview);
        Assert.IsTrue(vm.ThreadRows.Single().IsUnread);
    }

    [TestMethod]
    public async Task ThreadReadState_ConflictRefreshesStateAndWaitsForAnotherUserAction()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        bridge.UpdateThreadReadStateHandler = (request, _) => Task.FromResult(new UpdateThreadReadStateResult
        {
            ThreadId = request.ThreadId,
            Conflict = true,
            State = new ThreadReadStateSnapshot
            {
                Kind = ThreadReadStateKind.UnreadThreadStart,
                Revision = "revision-2",
            },
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot
        {
            Kind = ThreadReadStateKind.UnreadTurn,
            Revision = "revision-1",
        });

        await RunCommandAsync(vm.MarkSelectedThreadReadCommand);

        Assert.AreEqual(1, bridge.UpdateThreadReadStateCallCount, "A conflict must not trigger an automatic retry.");
        Assert.AreEqual("revision-1", bridge.LastUpdateThreadReadStateRequest!.ExpectedRevision);
        Assert.AreEqual("revision-2", vm.SelectedThreadReadState!.Revision);
        StringAssert.Contains(vm.ThreadReadStateStatusText, "choose again");

        await RunCommandAsync(vm.MarkSelectedThreadReadCommand);

        Assert.AreEqual(2, bridge.UpdateThreadReadStateCallCount, "A new explicit click may use the refreshed revision.");
        Assert.AreEqual("revision-2", bridge.LastUpdateThreadReadStateRequest!.ExpectedRevision);
    }

    [TestMethod]
    public async Task ThreadReadState_SerializesSameThreadUpdatesAcrossReadAndUnreadCommands()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        var pendingUpdate = new TaskCompletionSource<UpdateThreadReadStateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int updateCount = 0;
#pragma warning disable VSTHRD003 // The fake holds the first read-state mutation until the notification race is exercised.
        bridge.UpdateThreadReadStateHandler = (request, _) => Interlocked.Increment(ref updateCount) == 1
            ? pendingUpdate.Task
            : Task.FromResult(new UpdateThreadReadStateResult
            {
                ThreadId = request.ThreadId,
                Succeeded = true,
                State = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.UnreadThreadStart, Revision = "revision-3" },
            });
#pragma warning restore VSTHRD003
        bridge.ReadThreadHandler = (request, _) => Task.FromResult(new ThreadReadResult
        {
            Thread = new ThreadSummary { Id = request.ThreadId },
            ReadState = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.Read, Revision = "revision-2" },
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot
        {
            Kind = ThreadReadStateKind.UnreadTurn,
            Revision = "revision-1",
            TurnId = "turn-1",
        });
        int readsBeforeUpdate = bridge.ReadThreadCallCount;

        Task firstUpdate = RunCommandAsync(vm.MarkSelectedThreadReadCommand);
        await WaitForAsync(() => Volatile.Read(ref updateCount) == 1);
        await bridge.PublishThreadReadStateChangedAsync(new ThreadReadStateChangedEvent
        {
            ThreadId = thread.Id,
            State = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.Read, Revision = "revision-2" },
        });

        Assert.IsFalse(vm.MarkSelectedThreadUnreadCommand.CanExecute, "A state notification must not allow a second update while the first is pending.");
        await RunCommandAsync(vm.MarkSelectedThreadUnreadCommand);
        Assert.AreEqual(1, updateCount, "A second command for the same conversation must wait for another explicit action.");

        pendingUpdate.SetResult(new UpdateThreadReadStateResult
        {
            ThreadId = thread.Id,
            Succeeded = true,
            State = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.Read, Revision = "older-response" },
        });
        await firstUpdate;

        Assert.AreEqual(readsBeforeUpdate + 1, bridge.ReadThreadCallCount, "A successful update that races a notification must reread thread/read state.");
        Assert.AreEqual("revision-2", vm.SelectedThreadReadState!.Revision, "The newer notification wins over the delayed response.");
        Assert.IsTrue(vm.MarkSelectedThreadUnreadCommand.CanExecute);
        await RunCommandAsync(vm.MarkSelectedThreadUnreadCommand);

        Assert.AreEqual(2, updateCount);
        Assert.AreEqual("revision-2", bridge.LastUpdateThreadReadStateRequest!.ExpectedRevision);
        Assert.IsFalse(bridge.LastUpdateThreadReadStateRequest.MarkRead);
    }

    [TestMethod]
    public async Task ThreadReadState_NewConversationReadsInitialState()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        bridge.ReadThreadHandler = (request, _) => Task.FromResult(new ThreadReadResult
        {
            Thread = new ThreadSummary { Id = request.ThreadId },
            ReadState = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.UnreadThreadStart, Revision = "revision-new" },
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));

        await RunCommandAsync(vm.NewThreadCommand);

        Assert.AreEqual("thread-1", vm.SelectedThread!.Id);
        Assert.AreEqual(1, bridge.ReadThreadCallCount, "thread/start returns no read state, so the new row reads it once.");
        Assert.AreEqual(ThreadReadStateKind.UnreadThreadStart, vm.SelectedThreadReadState!.Kind);
        Assert.AreEqual("revision-new", vm.SelectedThreadReadState.Revision);
        Assert.IsTrue(vm.MarkSelectedThreadReadCommand.CanExecute);
    }

    [TestMethod]
    public async Task ThreadReadState_JoinedConversationRereadsStateAfterResume()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        int readNumber = 0;
        bridge.ReadThreadHandler = (request, _) => Task.FromResult(new ThreadReadResult
        {
            Thread = new ThreadSummary { Id = request.ThreadId },
            ReadState = new ThreadReadStateSnapshot
            {
                Kind = ThreadReadStateKind.Read,
                Revision = $"revision-{Interlocked.Increment(ref readNumber)}",
            },
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);
        Assert.AreEqual("revision-1", vm.SelectedThreadReadState!.Revision);

        await RunCommandAsync(vm.JoinThreadCommand);

        Assert.IsTrue(vm.IsThreadJoined);
        Assert.AreEqual("revision-2", vm.SelectedThreadReadState!.Revision, "thread/resume returns no read state, so the row rereads it.");
    }

    [TestMethod]
    public async Task ThreadReadState_StableConnectionDoesNotReadStateAfterNewConversation()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        await RunCommandAsync(vm.NewThreadCommand);

        Assert.AreEqual("thread-1", vm.SelectedThread!.Id);
        Assert.AreEqual(0, bridge.ReadThreadCallCount);
    }

    [TestMethod]
    public async Task ThreadReadState_NotificationWinsOverOlderListSnapshot()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        var listPage = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The fake deliberately holds this list response until the test publishes a newer state.
        bridge.ListThreadsHandler = async (_, _) => await listPage.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot
        {
            Kind = ThreadReadStateKind.Read,
            Revision = "revision-1",
        });

        Task load = (Task)typeof(ChatViewModel).GetMethod("LoadMoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null)!;
        await bridge.PublishThreadReadStateChangedAsync(new ThreadReadStateChangedEvent
        {
            ThreadId = thread.Id,
            State = new ThreadReadStateSnapshot
            {
                Kind = ThreadReadStateKind.UnreadTurn,
                Revision = "revision-2",
                TurnId = "turn-2",
            },
        });
        listPage.SetResult(new ThreadPage
        {
            Threads = [new ThreadSummary { Id = thread.Id }],
            ReadStatesAvailable = true,
            ReadStates = new Dictionary<string, ThreadReadStateSnapshot>(StringComparer.Ordinal)
            {
                [thread.Id] = new() { Kind = ThreadReadStateKind.Read, Revision = "revision-1" },
            },
        });
        await load;

        Assert.AreEqual("revision-2", vm.ThreadRows.Single(row => row.Id == thread.Id).ReadState.Revision);
        Assert.IsTrue(vm.ThreadRows.Single(row => row.Id == thread.Id).IsUnread);
    }

    [TestMethod]
    public async Task ThreadList_WrongOwnerPageKeepsRowsAndCursor()
    {
        var bridge = new FakeWorkerBridge();
        bridge.ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
        {
            Threads = [new ThreadSummary { Id = "thread-a" }],
            NextCursor = "cursor-next",
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        await InvokeThreadListMethodAsync(vm, "LoadMoreAsync");
        ThreadSummary existing = vm.Threads.Single();

        bridge.PreserveThreadPageOwner = true;
        bridge.ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
        {
            Threads = [new ThreadSummary { Id = "thread-b" }],
            NextCursor = "wrong-cursor",
            StatePartitionFingerprint = "different-owner",
        });
        await InvokeThreadListMethodAsync(vm, "LoadMoreAsync");

        Assert.AreSame(existing, vm.Threads.Single());
        Assert.AreEqual("cursor-next", GetPrivateField<string?>(vm, "nextCursor"));
    }

    [TestMethod]
    public async Task ThreadList_FailedRefreshPreservesRowsSelectionAndCursor()
    {
        var bridge = new FakeWorkerBridge();
        bridge.ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
        {
            Threads = [new ThreadSummary { Id = "thread-a", Preview = "Before refresh" }],
            NextCursor = "cursor-before-refresh",
        });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        await InvokeThreadListMethodAsync(vm, "LoadMoreAsync");
        ThreadSummary existing = vm.Threads.Single();
        vm.SelectedThread = existing;
        await WaitForHistoryLoadAsync(vm);
        bridge.ListThreadsHandler = (_, _) => throw new IOException("simulated refresh failure");

        await InvokeThreadListMethodAsync(vm, "ReloadThreadsAsync");

        Assert.AreSame(existing, vm.Threads.Single());
        Assert.AreEqual("thread-a", vm.SelectedThread?.Id);
        Assert.AreEqual("cursor-before-refresh", GetPrivateField<string?>(vm, "nextCursor"));
        StringAssert.Contains(vm.HistoryStatusText, "Existing conversations were kept");
    }

    [TestMethod]
    public async Task ThreadList_DeduplicatesPageIdsAndSuppressesConcurrentLoadMore()
    {
        var bridge = new FakeWorkerBridge();
        var firstPage = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The fake deliberately holds the first page while a duplicate request is suppressed.
        bridge.ListThreadsHandler = async (_, _) => await firstPage.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));

        Task firstLoad = InvokeThreadListMethodAsync(vm, "LoadMoreAsync");
        await Task.Delay(20);
        Task duplicateLoad = InvokeThreadListMethodAsync(vm, "LoadMoreAsync");
        await duplicateLoad;
        Assert.AreEqual(1, bridge.ListThreadsCallCount);
        firstPage.SetResult(new ThreadPage
        {
            Threads =
            [
                new ThreadSummary { Id = "thread-a" },
                new ThreadSummary { Id = "thread-a" },
            ],
            NextCursor = "cursor-next",
        });
        await firstLoad;

        bridge.ListThreadsHandler = (_, cursor) => Task.FromResult(new ThreadPage
        {
            Threads =
            [
                new ThreadSummary { Id = "thread-a" },
                new ThreadSummary { Id = "thread-b" },
                new ThreadSummary { Id = "thread-b" },
            ],
        });
        await InvokeThreadListMethodAsync(vm, "LoadMoreAsync");

        CollectionAssert.AreEqual(ExpectedThreadIdsAfterDuplicatePages, vm.Threads.Select(thread => thread.Id).ToArray());
    }

    [TestMethod]
    public async Task ThreadReadState_RequiresNegotiatedExperimentalApiAndValidRevision()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings
        {
            ExperimentalApiEnabled = true,
        }));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var thread = new ThreadSummary { Id = "thread-a" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForHistoryLoadAsync(vm);
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot
        {
            Kind = ThreadReadStateKind.Read,
            Revision = "revision-1",
        });

        Assert.IsFalse(vm.IsThreadReadStateAvailable, "A saved opt-in does not enable an API on a connection that did not negotiate it.");
        Assert.IsTrue(vm.ShowReadStateExperimentalNotice, "The history area should explain the experimental API requirement once.");
        Assert.IsFalse(vm.ShowSelectedThreadReadState, "Stable connections keep the selected-thread controls hidden.");
        Assert.IsFalse(vm.CanUpdateSelectedThreadReadState);
        Assert.IsFalse(vm.MarkSelectedThreadUnreadCommand.CanExecute);

        bridge.ExperimentalApi = true;
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        ApplyThreadReadState(vm, thread.Id, new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.Read });

        Assert.IsTrue(vm.IsThreadReadStateAvailable);
        Assert.IsFalse(vm.ShowReadStateExperimentalNotice);
        Assert.IsTrue(vm.ShowSelectedThreadReadState);
        Assert.IsFalse(vm.CanUpdateSelectedThreadReadState, "A valid state without an opaque revision is not actionable.");
    }

    [TestMethod]
    public async Task ThreadReadState_DiscardsLateReadWhenSelectionReturnsToSameThread()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        var firstRead = new TaskCompletionSource<ThreadReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int readNumber = 0;
        bridge.ReadThreadHandler = (request, _) => Interlocked.Increment(ref readNumber) == 1
            ? firstRead.Task
            : Task.FromResult(new ThreadReadResult
            {
                Thread = new ThreadSummary { Id = request.ThreadId },
                ReadState = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.Read, Revision = "latest" },
            });
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        var first = new ThreadSummary { Id = "thread-a" };
        var second = new ThreadSummary { Id = "thread-b" };
        vm.Threads.Add(first);
        vm.Threads.Add(second);
        vm.SelectedThread = first;
        vm.SelectedThread = second;
        vm.SelectedThread = first;
        for (int attempt = 0; attempt < 100 && readNumber < 3; attempt++)
        {
            await Task.Delay(10);
        }

        for (int attempt = 0; attempt < 100 && vm.SelectedThreadReadState?.Revision != "latest"; attempt++)
        {
            await Task.Delay(10);
        }

        firstRead.SetResult(new ThreadReadResult
        {
            Thread = new ThreadSummary { Id = first.Id },
            ReadState = new ThreadReadStateSnapshot { Kind = ThreadReadStateKind.UnreadTurn, Revision = "stale" },
        });
        for (int attempt = 0; attempt < 100 && vm.ThreadReadStateStatusText.Contains("Loading", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual(first.Id, vm.SelectedThread?.Id);
        Assert.AreEqual("latest", vm.SelectedThreadReadState?.Revision);
        Assert.AreEqual(ThreadReadStateKind.Read, vm.SelectedThreadReadState?.Kind);
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
    public async Task ChatViewModel_AccountChangeReconnectClearsStateAndKeepsSignInAvailable()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        vm.Items.Add(new ChatItemViewModel("Codex", "Previous owner's history", ConversationEventKind.AgentMessageDelta));
        vm.ComposerText = "unsent owner draft";

        // The Worker retires owner A after the owner's own logout and connects owner B itself.
        WorkerStatus connecting = OwnerStatus("owner-b", 2, 5);
        connecting.State = WorkerConnectionState.Connecting;
        connecting.Message = "The account changed. Starting a new isolated session...";
        await bridge.PublishStateAsync(connecting);
        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));
        int canExecuteNotifications = 0;
        vm.AccountCommand.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AsyncCommand.CanExecute))
            {
                canExecuteNotifications++;
            }
        };
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedOut });

        // Remote UI re-reads the button state only on PropertyChanged("CanExecute").
        Assert.IsTrue(canExecuteNotifications > 0);
        Assert.AreEqual(0, vm.Items.Count);
        Assert.AreEqual(string.Empty, vm.ComposerText);
        Assert.AreEqual(AccountState.SignedOut, vm.Account.State);
        Assert.IsTrue(vm.AccountCommand.CanExecute);
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
        var answers = new Dictionary<string, UserInputAnswer>
        {
            ["choice"] = new() { Kind = UserInputAnswerKind.SelectedOptions, OptionIds = ["old-owner-id"] },
        };
        await (Task)resolve.Invoke(vm, [new UserInputRequest(), "synthetic-old", answers, UserInputAction.Submit, oldOwner])!;

        Assert.IsNull(bridge.LastStartTurnRequest);
        Assert.IsFalse(vm.HasActiveUserInput);
        Assert.AreEqual(string.Empty, vm.ComposerText);
    }

    [TestMethod]
    public async Task ChatViewModel_SelectingHistoryIsReadOnlyUntilExplicitJoin()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        var thread = new ThreadSummary { Id = "thread-history" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForAsync(() => vm.HistoryStatusText.Contains("history", StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(vm.IsHistoryOnly);
        Assert.IsNull(bridge.LastResumeRequest);
        Assert.IsTrue(vm.JoinThreadCommand.CanExecute, "Joining is available only as an explicit user action.");
        Assert.IsFalse(vm.SendCommand.CanExecute);

        await RunCommandAsync(vm.JoinThreadCommand);

        Assert.AreEqual("thread-history", bridge.LastResumeRequest!.ThreadId);
        Assert.IsTrue(bridge.LastResumeRequest.UserConfirmed);
        Assert.IsTrue(vm.IsThreadJoined);
        Assert.IsFalse(vm.IsHistoryOnly);
    }

    [TestMethod]
    public async Task ChatViewModel_OwnerChangeQuarantinesDraftAndRestoreRequiresJoinedThread()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore());
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        vm.ComposerText = "unsubmitted prompt";
        vm.PlanModeEnabled = true;

        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));

        Assert.AreEqual(string.Empty, vm.ComposerText);
        Assert.IsFalse(vm.PlanModeEnabled, "An owner change must clear the active mode before any draft is explicitly restored.");
        Assert.IsNotNull(vm.QuarantinedDraft);
        Assert.IsFalse(vm.RestoreQuarantinedDraftCommand.CanExecute);

        var thread = new ThreadSummary { Id = "new-owner-thread" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForAsync(() => vm.HistoryStatusText.Contains("history", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(vm.RestoreQuarantinedDraftCommand.CanExecute);

        await RunCommandAsync(vm.JoinThreadCommand);
        Assert.IsTrue(vm.RestoreQuarantinedDraftCommand.CanExecute);
        await RunCommandAsync(vm.RestoreQuarantinedDraftCommand);

        Assert.AreEqual("unsubmitted prompt", vm.ComposerText);
        Assert.IsTrue(vm.PlanModeEnabled, "Explicitly restoring the quarantined draft also restores its composer mode.");
        Assert.IsNull(vm.QuarantinedDraft);
        StringAssert.Contains(vm.RecoveryStatusText, "settings were unavailable");
    }

    [TestMethod]
    public async Task ChatViewModel_QuarantinedDraftRestoresDefaultPlanMode()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore());
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        vm.ComposerText = "draft with default mode";

        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));
        vm.PlanModeEnabled = true;

        var thread = new ThreadSummary { Id = "new-owner-thread" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForAsync(() => vm.HistoryStatusText.Contains("history", StringComparison.OrdinalIgnoreCase));
        await RunCommandAsync(vm.JoinThreadCommand);
        await RunCommandAsync(vm.RestoreQuarantinedDraftCommand);

        Assert.IsFalse(vm.PlanModeEnabled, "A restored default-mode draft must replace the target composer's current mode.");
    }

    [TestMethod]
    public async Task ChatViewModel_DiscardQuarantinedDraftDoesNotPopulateComposer()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        vm.ComposerText = "do not send automatically";
        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));

        await RunCommandAsync(vm.DiscardQuarantinedDraftCommand);

        Assert.IsNull(vm.QuarantinedDraft);
        Assert.AreEqual(string.Empty, vm.ComposerText);
        Assert.IsNull(bridge.LastStartTurnRequest);
    }

    [TestMethod]
    public async Task ChatViewModel_HistoryWindowOverflowMarksStaleAndKeepsVisibleBound()
    {
        var bridge = new FakeWorkerBridge();
        bridge.ListThreadItemsHandler = (request, _) =>
        {
            int start = request.Cursor is null
                ? 0
                : int.TryParse(request.Cursor.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int cursorValue)
                    ? cursorValue
                    : throw new InvalidOperationException("The fake history cursor must be an integer.");
            int end = Math.Min(start + request.Limit, 1001);
            ThreadHistoryItem[] items = Enumerable.Range(start, end - start)
                .Select(index => new ThreadHistoryItem
                {
                    Id = $"history-{index}",
                    TurnId = "turn-history",
                    Type = "assistant_message",
                    Text = $"history row {index}",
                })
                .ToArray();
            string? nextCursor = end < 1001 ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new ThreadItemsPage { Items = items, NextCursor = nextCursor });
        };

        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var thread = new ThreadSummary { Id = "thread-history-bound" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForAsync(() => vm.CanLoadOlderHistory);

        while (vm.CanLoadOlderHistory && !vm.IsHistoryStale)
        {
            await RunCommandAsync(vm.LoadOlderHistoryCommand);
        }

        Assert.IsTrue(vm.IsHistoryStale);
        Assert.IsTrue(vm.HasHistoryNotice);
        Assert.IsTrue(vm.HistoryStatusText.Contains("Refresh", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(vm.Items.Count <= 1000);
        Assert.IsTrue(vm.RefreshHistoryCommand.CanExecute);
    }

    [TestMethod]
    public async Task ChatViewModel_HistoryPagesRenderChronologicallyAndNewThreadClearsCursor()
    {
        var bridge = new FakeWorkerBridge();
        bridge.ListThreadItemsHandler = (request, _) => Task.FromResult(request.Cursor is null
            ? new ThreadItemsPage
            {
                Items = [HistoryItem("h3"), HistoryItem("h2")],
                NextCursor = "older",
            }
            : new ThreadItemsPage { Items = [HistoryItem("h1"), HistoryItem("h0")] });
        bridge.ListThreadTurnsHandler = (request, _) => Task.FromResult(request.Cursor is null
            ? new ThreadTurnsPage { Turns = [new ThreadTurnSummary { Id = "turn-3" }, new ThreadTurnSummary { Id = "turn-2" }], NextCursor = "older-turns" }
            : new ThreadTurnsPage { Turns = [new ThreadTurnSummary { Id = "turn-1" }, new ThreadTurnSummary { Id = "turn-0" }] });

        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var oldThread = new ThreadSummary { Id = "old-thread" };
        vm.Threads.Add(oldThread);
        vm.SelectedThread = oldThread;
        await WaitForAsync(() => vm.CanLoadOlderHistory);

        CollectionAssert.AreEqual(new List<string> { "h2", "h3" }, HistoryItemIds(vm));
        CollectionAssert.AreEqual(new List<string> { "turn-2", "turn-3" }, vm.ThreadTurns.Select(static turn => turn.Id).ToList());
        await RunCommandAsync(vm.LoadOlderHistoryCommand);
        CollectionAssert.AreEqual(new List<string> { "h0", "h1", "h2", "h3" }, HistoryItemIds(vm));
        CollectionAssert.AreEqual(new List<string> { "turn-0", "turn-1", "turn-2", "turn-3" }, vm.ThreadTurns.Select(static turn => turn.Id).ToList());

        await RunCommandAsync(vm.NewThreadCommand);
        Assert.AreEqual("thread-1", vm.SelectedThread!.Id);
        Assert.IsFalse(vm.CanLoadOlderHistory, "A new conversation must not inherit the previous thread's cursor.");
    }

    [TestMethod]
    public async Task ChatViewModel_ReplaysLiveEventsBufferedDuringOlderPageLoad()
    {
        bool olderPageRequested = false;
        using var releaseOlder = new ManualResetEventSlim();
        var bridge = new FakeWorkerBridge
        {
            ListThreadItemsHandler = (request, _) =>
            {
                if (request.Cursor is null)
                {
                    return Task.FromResult(new ThreadItemsPage { Items = [HistoryItem("newest")], NextCursor = "older" });
                }

                olderPageRequested = true;
                return Task.Run(() =>
                {
                    releaseOlder.Wait();
                    return new ThreadItemsPage { Items = [HistoryItem("older")] };
                });
            },
        };

        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var thread = new ThreadSummary { Id = "thread-live-buffer" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForAsync(() => vm.CanLoadOlderHistory);

        Task loadOlder = RunCommandAsync(vm.LoadOlderHistoryCommand);
        await WaitForAsync(() => olderPageRequested);
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            ThreadId = thread.Id,
            TurnId = "turn-live",
            ItemId = "live-item",
            Kind = ConversationEventKind.AgentMessageDelta,
            Text = "live update",
        });
        releaseOlder.Set();
        await loadOlder;

        await WaitForAsync(() => vm.Items.Any(item => item.ItemId == "live-item"));
        Assert.IsTrue(vm.Items.Any(item => item.ItemId == "live-item" && item.Text.Contains("live update", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ChatViewModel_ThreadlessErrorEventIsShownInTranscript()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        vm.SelectedThread = new ThreadSummary { Id = "thread-error-context" };

        MethodInfo method = typeof(ChatViewModel).GetMethod("OnConversationEventAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(vm, [Notification(new ConversationEvent
        {
            Kind = ConversationEventKind.Error,
            Text = "The connection was interrupted.",
        })])!;

        Assert.IsTrue(vm.Items.Any(item => item.Kind == ConversationEventKind.Error
            && item.Text.Contains("connection was interrupted", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ChatViewModel_ConnectionLossKeepsUnknownOutcomeNoticeAfterTranscriptReset()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var inFlight = new ChatItemViewModel("You", "request pending", ConversationEventKind.ItemStarted);
        vm.Items.Add(inFlight);
        FieldInfo mutationsField = typeof(ChatViewModel).GetField("inFlightMutations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mutations = (Dictionary<Guid, ChatItemViewModel>)mutationsField.GetValue(vm)!;
        mutations.Add(Guid.NewGuid(), inFlight);

        MethodInfo method = typeof(ChatViewModel).GetMethod("HandleConnectionLostAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(vm, [bridge, WorkerRecoveryFailureKind.PeerUnresponsive])!;

        Assert.IsTrue(vm.Items.Any(item => item.Kind == ConversationEventKind.Error
            && item.Text.Contains("unknown delivery outcome", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ChatViewModel_ConnectionLossKeepsPreviouslyUnknownOutcomeNotice()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var unresolved = new ChatItemViewModel("You", "earlier message", ConversationEventKind.ItemStarted);
        unresolved.MarkDeliveryUnknown();
        vm.Items.Add(unresolved);

        MethodInfo method = typeof(ChatViewModel).GetMethod("HandleConnectionLostAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(vm, [bridge, WorkerRecoveryFailureKind.PeerUnresponsive])!;

        Assert.IsTrue(vm.Items.Any(item => item.Kind == ConversationEventKind.Error
            && item.Text.Contains("1 unresolved operation", StringComparison.OrdinalIgnoreCase)
            && item.Text.Contains("unknown delivery outcome", StringComparison.OrdinalIgnoreCase)));
    }

    private static ThreadHistoryItem HistoryItem(string id)
        => new() { Id = id, TurnId = $"turn-{id}", Type = "assistant_message", Text = id };

    private static string[] HistoryItemIds(ChatViewModel vm)
        => vm.Items.Where(static item => item.HistoryKey is not null).Select(static item => item.ItemId!).ToArray();

    [TestMethod]
    public async Task ChatViewModel_AttachmentMetadataCapIsVisible()
    {
        var bridge = new FakeWorkerBridge();
        bridge.ListThreadAttachmentsHandler = (request, _) =>
        {
            int start = request.Cursor is null
                ? 0
                : int.TryParse(request.Cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int cursorValue)
                    ? cursorValue
                    : throw new InvalidOperationException("The fake attachment cursor must be an integer.");
            int end = Math.Min(start + request.Limit, 120);
            ThreadAttachmentMetadata[] attachments = Enumerable.Range(start, end - start)
                .Select(index => new ThreadAttachmentMetadata
                {
                    Id = $"attachment-{index}",
                    AttachmentType = "image",
                    IdentityKey = $"sha256:identity-{index}",
                })
                .ToArray();
            string? nextCursor = end < 120 ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new ThreadAttachmentsPage { Attachments = attachments, NextCursor = nextCursor });
        };

        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var thread = new ThreadSummary { Id = "thread-attachments" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;

        await WaitForAsync(() => vm.ThreadAttachments.Count == 100 && !string.IsNullOrWhiteSpace(vm.ThreadAttachmentsStatusText));

        Assert.IsTrue(vm.HasHistoryNotice);
        StringAssert.Contains(vm.ThreadAttachmentsStatusText, "first 100");
        Assert.AreEqual(100, vm.ThreadAttachments.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_AttachmentMembershipIsKeyedByTypeAndIdentity()
    {
        var bridge = new FakeWorkerBridge();
        bridge.ListThreadAttachmentsHandler = (_, _) => Task.FromResult(new ThreadAttachmentsPage
        {
            Attachments =
            [
                new ThreadAttachmentMetadata { Id = "attachment-old", AttachmentType = "image", IdentityKey = "sha256:same", CreatedAt = 1 },
                new ThreadAttachmentMetadata { Id = "attachment-other", AttachmentType = "image", IdentityKey = "sha256:other", CreatedAt = 1 },
                new ThreadAttachmentMetadata { Id = "attachment-new", AttachmentType = "image", IdentityKey = "sha256:same", CreatedAt = 2 },
            ],
        });

        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var thread = new ThreadSummary { Id = "thread-attachment-identity" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;

        await WaitForAsync(() => vm.ThreadAttachments.Count == 2);

        Assert.AreEqual(
            "attachment-new,attachment-other",
            string.Join(',', vm.ThreadAttachments.Select(static attachment => attachment.Id).Order(StringComparer.Ordinal)));
    }

    [TestMethod]
    public async Task ChatViewModel_AttachmentRefreshesAreCoalescedAndNeverOverlap()
    {
        var bridge = new FakeWorkerBridge();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool blockScans = false;
        int blockedScans = 0;
        int inFlight = 0;
        int maxInFlight = 0;
        bridge.ListThreadAttachmentsHandler = async (_, _) =>
        {
            int current = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, current);
            try
            {
                if (Volatile.Read(ref blockScans))
                {
                    Interlocked.Increment(ref blockedScans);
#pragma warning disable VSTHRD003 // The test owns this completion source and releases it below.
                    await release.Task;
#pragma warning restore VSTHRD003
                }

                return new ThreadAttachmentsPage
                {
                    Attachments = [new ThreadAttachmentMetadata { Id = "attachment-1", AttachmentType = "image", IdentityKey = "sha256:one", CreatedAt = 1 }],
                };
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        };

        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var thread = new ThreadSummary { Id = "thread-attachment-refresh" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await WaitForAsync(() => vm.ThreadAttachments.Count == 1);

        Volatile.Write(ref blockScans, true);
        MethodInfo refresh = typeof(ChatViewModel).GetMethod("RefreshAttachmentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Task first = (Task)refresh.Invoke(vm, null)!;
        await WaitForAsync(() => Volatile.Read(ref blockedScans) == 1);
        await (Task)refresh.Invoke(vm, null)!;
        await (Task)refresh.Invoke(vm, null)!;
        Assert.IsFalse(first.IsCompleted);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, Volatile.Read(ref blockedScans));
        Assert.AreEqual(1, Volatile.Read(ref maxInFlight));
        Assert.AreEqual(1, vm.ThreadAttachments.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int observed = Volatile.Read(ref target);
        while (value > observed)
        {
            int previous = Interlocked.CompareExchange(ref target, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    [TestMethod]
    public async Task ChatViewModel_ConnectionRejectionWithoutStageProofRemainsOutcomeUnknown()
    {
        var bridge = new FakeWorkerBridge
        {
            StartThreadException = new StreamJsonRpc.RemoteInvocationException(
                "The Worker rejected the connection operation.",
                WorkerErrorCodes.ConnectionOperationRejected,
                errorData: "StaleGeneration"),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        await RunCommandAsync(vm.NewThreadCommand);

        Assert.AreEqual(1, bridge.StartThreadCallCount);
        Assert.IsTrue(vm.Items.Any(static item => item.IsDeliveryUnknown));
        StringAssert.Contains(vm.RecoveryStatusText, "outcome is unknown");
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

    private static WorkerStatus ReadyOwnerStatus(long ownerGeneration, string statePartitionFingerprint)
        => new()
        {
            State = WorkerConnectionState.Ready,
            Target = new ConnectionTargetSnapshot
            {
                Kind = ConnectionTargetKind.Local,
                Generation = 1,
                OwnerGeneration = ownerGeneration,
                StatePartitionFingerprint = statePartitionFingerprint,
            },
        };

    private static object CaptureOwnerSnapshot(ChatViewModel viewModel)
        => typeof(ChatViewModel).GetMethod("CaptureOwnerSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, null)!;

    private static void ApplyThreadReadState(ChatViewModel viewModel, string threadId, ThreadReadStateSnapshot state)
        => typeof(ChatViewModel).GetMethod("ApplyThreadReadState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, [threadId, state]);

    private static Task InvokeThreadListMethodAsync(ChatViewModel viewModel, string methodName)
        => (Task)typeof(ChatViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, null)!;

    private static T? GetPrivateField<T>(ChatViewModel viewModel, string fieldName)
        => (T?)typeof(ChatViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel);

    private static async Task WaitForHistoryLoadAsync(ChatViewModel viewModel)
    {
        for (int attempt = 0; attempt < 100 && viewModel.HistoryStatusText == "Loading read-only history…"; attempt++)
        {
            await Task.Delay(10);
        }
    }

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
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            }, new SafeMarkdownService());

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
        IReadOnlyDictionary<string, UserInputAnswer>? capturedAnswers = null;
        UserInputAction? capturedAction = null;
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
                        new UserInputOption { OptionId = "sharp-id", Label = "Sharp", Description = "d1" },
                        new UserInputOption { OptionId = "creative-id", Label = "Creative", Description = "d2" },
                    ],
                },
            ],
        };
        var vm = new UserInputViewModel(
            request,
            (id, answers, action) =>
            {
                capturedRequestId = id;
                capturedAnswers = answers;
                capturedAction = action;
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
        CollectionAssert.AreEqual(ExpectedCreativeOptionIds, capturedAnswers!["q1"].OptionIds.ToArray());
        Assert.AreEqual(UserInputAnswerKind.SelectedOptions, capturedAnswers["q1"].Kind);
        Assert.AreEqual(UserInputAction.Submit, capturedAction);
        Assert.IsTrue(vm.IsResolved);
    }

    [TestMethod]
    public async Task ChatViewModel_UserInputRequests_ShowIndependentCards()
    {
        using var vm = new ChatViewModel();
        MethodInfo requested = typeof(ChatViewModel).GetMethod(
            "OnUserInputRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo resolved = typeof(ChatViewModel).GetMethod(
            "OnUserInputResolvedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)requested.Invoke(vm, [Notification(MakeUserInputRequest("ui-1"))])!;
        Assert.IsTrue(vm.HasActiveUserInput);
        Assert.AreEqual("ui-1", vm.ActiveUserInput!.RequestId);
        Assert.AreEqual("1 question pending", vm.UserInputQueueText);

        await (Task)requested.Invoke(vm, [Notification(MakeUserInputRequest("ui-2"))])!;
        Assert.AreEqual(2, vm.PendingInteractions.Count(card => card.UserInput is not null));
        CollectionAssert.AreEquivalent(IndependentQuestionIds, vm.PendingInteractions
            .Where(card => card.UserInput is not null).Select(card => card.UserInput!.RequestId).ToArray());

        await (Task)resolved.Invoke(vm, [Notification("ui-1")])!;
        Assert.AreEqual("ui-2", vm.ActiveUserInput!.RequestId);
        Assert.AreEqual(1, vm.PendingInteractions.Count(card => card.UserInput is not null));
        Assert.AreEqual("1 question pending", vm.UserInputQueueText);

        // Resolving the last clears the card; a duplicate resolve is a no-op.
        await (Task)resolved.Invoke(vm, [Notification("ui-2")])!;
        Assert.IsFalse(vm.HasActiveUserInput);
        await (Task)resolved.Invoke(vm, [Notification("ui-2")])!;
        Assert.IsFalse(vm.HasActiveUserInput);
    }

    [TestMethod]
    public async Task ChatViewModel_ApprovalRequests_ShowIndependentCards()
    {
        using var vm = new ChatViewModel();
        MethodInfo requested = typeof(ChatViewModel).GetMethod(
            "OnApprovalRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo resolved = typeof(ChatViewModel).GetMethod(
            "OnApprovalResolvedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)requested.Invoke(vm, [Notification(MakeApprovalRequest("req-1"))])!;
        Assert.IsTrue(vm.HasActiveApproval);
        Assert.AreEqual("req-1", vm.ActiveApproval!.RequestId);
        Assert.AreEqual("1 approval pending", vm.ApprovalQueueText);

        await (Task)requested.Invoke(vm, [Notification(MakeApprovalRequest("req-2"))])!;

        await (Task)requested.Invoke(vm, [Notification(MakeApprovalRequest("req-3"))])!;
        Assert.AreEqual(3, vm.PendingInteractions.Count(card => card.Approval is not null));
        CollectionAssert.AreEquivalent(IndependentApprovalIds, vm.PendingInteractions
            .Where(card => card.Approval is not null).Select(card => card.Approval!.RequestId).ToArray());

        await (Task)resolved.Invoke(vm, [Notification("req-1")])!;
        Assert.AreEqual("req-2", vm.ActiveApproval!.RequestId);
        Assert.AreEqual(2, vm.PendingInteractions.Count(card => card.Approval is not null));

        await (Task)resolved.Invoke(vm, [Notification("req-3")])!;
        Assert.AreEqual("req-2", vm.ActiveApproval!.RequestId);
        Assert.AreEqual("1 approval pending", vm.ApprovalQueueText);

        // Resolving the last clears the card; a duplicate resolve is a no-op.
        await (Task)resolved.Invoke(vm, [Notification("req-2")])!;
        Assert.IsFalse(vm.HasActiveApproval);
        await (Task)resolved.Invoke(vm, [Notification("req-2")])!;
        Assert.IsFalse(vm.HasActiveApproval);
    }

    [TestMethod]
    public async Task McpElicitation_DefaultValuesArePrefilledButOnlySubmittedExplicitly()
    {
        McpElicitationRequest request = new()
        {
            RequestId = "mcp-1",
            ServerName = "forms",
            Kind = McpElicitationKind.Form,
            Message = "Provide values",
            Fields =
            [
                new McpElicitationField
                {
                    Name = "text",
                    Title = "Text",
                    Type = McpElicitationFieldType.Text,
                    Required = true,
                    MinLength = 4,
                    DefaultValue = new McpElicitationValue { StringValue = "**x**" },
                },
                new McpElicitationField
                {
                    Name = "empty",
                    Title = "Optional text",
                    Type = McpElicitationFieldType.Text,
                    DefaultValue = new McpElicitationValue { StringValue = string.Empty },
                },
                new McpElicitationField
                {
                    Name = "enabled",
                    Title = "Enabled",
                    Type = McpElicitationFieldType.Boolean,
                    DefaultValue = new McpElicitationValue { BooleanValue = false },
                },
                new McpElicitationField
                {
                    Name = "count",
                    Title = "Count",
                    Type = McpElicitationFieldType.Number,
                    DefaultValue = new McpElicitationValue { NumberValue = 0 },
                },
                new McpElicitationField
                {
                    Name = "mode",
                    Title = "Mode",
                    Type = McpElicitationFieldType.SingleSelect,
                    Required = true,
                    Choices = [new McpElicitationChoice { ChoiceId = "mode-id", Label = "Mode A" }],
                },
            ],
        };
        string? submittedId = null;
        IReadOnlyList<McpElicitationValue>? submittedValues = null;
        var vm = new McpElicitationViewModel(request, (id, _, values) =>
        {
            submittedId = id;
            submittedValues = values;
            return Task.CompletedTask;
        }, new SafeMarkdownService());

        Assert.IsFalse(vm.IsResolved);
        Assert.IsTrue(vm.CanAccept);
        Assert.AreEqual("**x**", vm.Fields[0].InputText);
        Assert.AreEqual(string.Empty, vm.Fields[1].InputText);
        Assert.IsFalse(vm.Fields[2].BooleanValue);
        Assert.AreEqual("0", vm.Fields[3].InputText);
        Assert.IsNull(submittedValues);

        await RunCommandAsync(vm.SubmitCommand);
        Assert.IsFalse(vm.IsResolved, "Required single-select fields need an explicit selection.");
        Assert.IsNull(submittedValues);
        vm.Fields[4].Choices[0].IsSelected = true;
        await RunCommandAsync(vm.SubmitCommand);

        Assert.AreEqual("mcp-1", submittedId);
        Assert.IsNotNull(submittedValues);
        Assert.AreEqual("**x**", submittedValues![0].StringValue);
        Assert.AreEqual(string.Empty, submittedValues[1].StringValue);
        Assert.AreEqual(false, submittedValues[2].BooleanValue);
        Assert.AreEqual(0d, submittedValues[3].NumberValue);
        CollectionAssert.AreEqual(ExpectedModeChoiceIds, submittedValues[4].ChoiceIds.ToArray());
        Assert.IsTrue(vm.IsResolved);
    }

    [TestMethod]
    public async Task McpAuthPresentation_NotifiesDismissabilityAndRetiresStaleOperationIds()
    {
        string? dismissedOperationId = null;
        var vm = new McpServerAuthPresentationViewModel(
            new McpServerAuthStatus { ServerName = "server", State = InteractionAuthState.ReauthenticationRequired },
            new SafeMarkdownService(),
            _ => Task.CompletedTask,
            operationId =>
            {
                dismissedOperationId = operationId;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask);
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        vm.Update(new McpOAuthLoginStatus
        {
            OperationId = "login-operation",
            ServerName = "server",
            State = InteractionAuthState.LoginPending,
        }, new SafeMarkdownService());

        Assert.IsTrue(vm.CanDismiss);
        Assert.IsTrue(changedProperties.Contains(nameof(vm.CanDismiss), StringComparer.Ordinal));

        await RunCommandAsync(vm.DismissCommand);
        Assert.AreEqual("login-operation", dismissedOperationId);
        Assert.IsFalse(vm.CanDismiss, "Dismissing the local wait must retire the action without claiming server cancellation.");

        vm.Update(new McpOAuthLoginStatus
        {
            OperationId = "login-operation-2",
            ServerName = "server",
            State = InteractionAuthState.LoginPending,
        }, new SafeMarkdownService());
        Assert.IsTrue(vm.CanDismiss);

        changedProperties.Clear();
        vm.Update(new McpServerAuthStatus { ServerName = "server", State = InteractionAuthState.Authenticated }, new SafeMarkdownService());

        Assert.IsFalse(vm.CanDismiss);
        Assert.IsFalse(vm.DismissCommand.CanExecute);
        Assert.IsTrue(changedProperties.Contains(nameof(vm.CanDismiss), StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_AuthenticationDetails_CheckHideAndRecheckPreservesPendingActions()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        WorkerStatus owner = ReadyOwnerStatus(1, "auth-owner-1");
        await bridge.PublishStateAsync(owner);

        bridge.ReadGatewayOAuthHandler = (_, _) => Task.FromResult(new InteractionAuthStatus
        {
            IsLocal = true,
            IsSupported = true,
            State = InteractionAuthState.Checking,
            Message = "Gateway check completed.",
            McpServers =
            [
                new McpServerAuthStatus
                {
                    ServerName = "docs",
                    State = InteractionAuthState.ReauthenticationRequired,
                    Message = "Sign in is required.",
                    OpenAuthorizationActionId = "auth-action-1",
                },
            ],
        });

        Assert.IsFalse(vm.IsInteractionAuthStatusExpanded, "Authentication details must start collapsed.");
        await RunCommandAsync(vm.ReadGatewayOAuthCommand);

        Assert.IsTrue(vm.IsInteractionAuthStatusExpanded);
        Assert.IsNotNull(vm.InteractionAuthStatus);
        Assert.AreEqual("docs", vm.InteractionAuthStatus!.McpServers.Single().ServerName);
        Assert.IsTrue(vm.InteractionAuthStatus.McpServers.Single().CanOpenAuthorization);

        // The MCP details toggle writes the expansion state through its two-way binding.
        vm.IsInteractionAuthStatusExpanded = false;
        Assert.IsTrue(vm.ReadGatewayOAuthCommand.CanExecute,
            "Check status must remain available when the details are hidden.");
        Assert.IsNotNull(vm.InteractionAuthStatus, "Hiding details must retain the authentication presentation.");
        Assert.IsTrue(vm.InteractionAuthStatus!.McpServers.Single().CanOpenAuthorization,
            "Hiding details must preserve pending authorization actions.");
        Assert.IsTrue(vm.InteractionAuthStatus.HasPendingMcpAuthAction,
            "The compact area must be able to signal the hidden MCP actions.");

        vm.IsInteractionAuthStatusExpanded = true;
        Assert.AreEqual(1, bridge.ReadGatewayOAuthCallCount, "Showing details through the toggle must not issue a read.");
        vm.IsInteractionAuthStatusExpanded = false;

        await RunCommandAsync(vm.ReadGatewayOAuthCommand);
        Assert.IsTrue(vm.IsInteractionAuthStatusExpanded, "Check status must reopen the MCP details.");
        Assert.AreEqual(2, bridge.ReadGatewayOAuthCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_AuthenticationDetails_InFlightReadAndNotificationsDoNotUndoHide()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        WorkerStatus owner = ReadyOwnerStatus(1, "auth-owner-1");
        await bridge.PublishStateAsync(owner);
        var readCompletion = new TaskCompletionSource<InteractionAuthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The fake intentionally holds this Gateway read until the test hides the details.
        bridge.ReadGatewayOAuthHandler = (_, _) => readCompletion.Task;
#pragma warning restore VSTHRD003

        Task read = RunCommandAsync(vm.ReadGatewayOAuthCommand);
        Assert.IsTrue(vm.IsInteractionAuthStatusExpanded, "An explicit Check status action expands immediately.");
        vm.IsInteractionAuthStatusExpanded = false;

        readCompletion.SetResult(new InteractionAuthStatus
        {
            IsLocal = true,
            IsSupported = true,
            State = InteractionAuthState.Ready,
            Message = "Read completed after hide.",
        });
        await read;

        Assert.IsFalse(vm.IsInteractionAuthStatusExpanded, "A read response must not reopen details hidden while it was pending.");
        Assert.AreEqual("Read completed after hide.", vm.InteractionAuthStatus?.StatusText);

        await bridge.PublishInteractionAuthStatusAsync(new InteractionAuthStatus
        {
            IsLocal = true,
            IsSupported = true,
            State = InteractionAuthState.ReauthenticationRequired,
            Message = "A background notification arrived.",
            McpServers =
            [new McpServerAuthStatus { ServerName = "docs", State = InteractionAuthState.ReauthenticationRequired }],
        });

        Assert.IsFalse(vm.IsInteractionAuthStatusExpanded, "A current-owner notification must not undo Hide status.");
        Assert.AreEqual("A background notification arrived.", vm.InteractionAuthStatus?.StatusText);
        Assert.AreEqual("docs", vm.InteractionAuthStatus!.McpServers.Single().ServerName);
        Assert.IsTrue(vm.InteractionAuthStatus.HasPendingMcpAuthAction,
            "A notification that needs MCP sign-in must be signalled while the details stay hidden.");
    }

    [TestMethod]
    public async Task InteractionAuthPresentation_PendingMcpActionTracksServerUpdates()
    {
        var presentation = new InteractionAuthStatusPresentationViewModel(
            new InteractionAuthStatus
            {
                IsLocal = true,
                IsSupported = true,
                State = InteractionAuthState.Ready,
                McpServers = [new McpServerAuthStatus { ServerName = "docs", State = InteractionAuthState.Authenticated }],
            },
            new SafeMarkdownService(),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        var changedProperties = new List<string?>();
        presentation.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        Assert.IsFalse(presentation.HasPendingMcpAuthAction);

        McpServerAuthPresentationViewModel server = presentation.McpServers.Single();
        server.Update(new McpOAuthLoginStatus
        {
            OperationId = "login-operation",
            ServerName = "docs",
            State = InteractionAuthState.LoginPending,
        }, new SafeMarkdownService());
        Assert.IsTrue(presentation.HasPendingMcpAuthAction, "A per-server login update must refresh the compact signal.");
        Assert.IsTrue(changedProperties.Contains(nameof(presentation.HasPendingMcpAuthAction), StringComparer.Ordinal));

        await RunCommandAsync(server.DismissCommand);
        Assert.IsFalse(presentation.HasPendingMcpAuthAction, "Dismissing the last pending action must clear the compact signal.");

        presentation.Update(new InteractionAuthStatus
        {
            IsLocal = true,
            IsSupported = true,
            State = InteractionAuthState.Ready,
            McpServers = [new McpServerAuthStatus { ServerName = "docs", State = InteractionAuthState.ReauthenticationRequired }],
        });
        Assert.IsTrue(presentation.HasPendingMcpAuthAction);

        presentation.Update(new InteractionAuthStatus { IsLocal = true, IsSupported = true, State = InteractionAuthState.Ready });
        Assert.IsFalse(presentation.HasPendingMcpAuthAction, "Removing the server must clear the compact signal.");
        changedProperties.Clear();
        server.Update(new McpServerAuthStatus { ServerName = "docs", State = InteractionAuthState.ReauthenticationRequired }, new SafeMarkdownService());
        Assert.IsFalse(presentation.HasPendingMcpAuthAction, "A removed server must no longer drive the compact signal.");
        Assert.IsFalse(changedProperties.Contains(nameof(presentation.HasPendingMcpAuthAction), StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_AuthenticationDetails_OwnerResetCompactsAndRejectsStaleRead()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(ReadyOwnerStatus(1, "auth-owner-1"));
        var readCompletion = new TaskCompletionSource<InteractionAuthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The fake intentionally holds this Gateway read until the owner changes.
        bridge.ReadGatewayOAuthHandler = (_, _) => readCompletion.Task;
#pragma warning restore VSTHRD003

        Task read = RunCommandAsync(vm.ReadGatewayOAuthCommand);
        Assert.IsTrue(vm.IsInteractionAuthStatusExpanded);

        await bridge.PublishStateAsync(ReadyOwnerStatus(2, "auth-owner-2"));
        Assert.IsFalse(vm.IsInteractionAuthStatusExpanded, "A new owner restores the compact presentation.");
        Assert.IsNull(vm.InteractionAuthStatus, "A new owner retires the previous owner's authentication state.");

        readCompletion.SetResult(new InteractionAuthStatus
        {
            IsLocal = true,
            IsSupported = true,
            State = InteractionAuthState.Ready,
            Message = "Stale owner response.",
        });
        await read;

        Assert.IsFalse(vm.IsInteractionAuthStatusExpanded);
        Assert.IsNull(vm.InteractionAuthStatus, "A stale read must not restore retired authentication state.");
    }

    [TestMethod]
    public void ChatToolWindowXaml_AuthenticationDetails_CanBeHiddenAndReopened()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement details = doc
            .Descendants(presentation + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding InteractionAuthStatus.McpServers}");
        Assert.AreEqual("{Binding IsInteractionAuthStatusExpanded, Converter={StaticResource BoolToVis}}", details.Attribute("Visibility")?.Value);

        XElement detailsToggle = doc
            .Descendants(presentation + "ToggleButton")
            .Single(element => element.Attribute("Content")?.Value == "MCP details");
        Assert.IsNull(detailsToggle.Attribute("Command"), "The two-way IsChecked binding is the single source of the expansion state.");
        Assert.AreEqual("{Binding IsInteractionAuthStatusExpanded, Mode=TwoWay}", detailsToggle.Attribute("IsChecked")?.Value);
        Assert.IsNull(detailsToggle.Attribute("Visibility"), "The details toggle must remain visible when collapsed.");
        Assert.AreEqual("Show or hide MCP server details", detailsToggle.Attribute("AutomationProperties.Name")?.Value);
        Assert.AreEqual("{StaticResource RoundToggleButtonStyle}", detailsToggle.Attribute("Style")?.Value);

        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement toggleStyle = doc
            .Descendants(presentation + "Style")
            .Single(element => element.Attribute(xaml + "Key")?.Value == "RoundToggleButtonStyle");
        Assert.AreEqual("{StaticResource RoundButtonStyle}", toggleStyle.Attribute("BasedOn")?.Value,
            "The toggle must share the round button template, including its disabled and hover states.");

        XElement pendingHint = doc
            .Descendants(presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value?.StartsWith("MCP servers need attention", StringComparison.Ordinal) == true);
        string[] hintConditions = pendingHint
            .Descendants(presentation + "Condition")
            .Select(element => element.Attribute("Binding")?.Value + "=" + element.Attribute("Value")?.Value)
            .ToArray();
        Assert.AreEqual(2, hintConditions.Length, "The compact hint must appear only while pending MCP actions are hidden.");
        CollectionAssert.Contains(hintConditions, "{Binding InteractionAuthStatus.HasPendingMcpAuthAction}=True");
        CollectionAssert.Contains(hintConditions, "{Binding IsInteractionAuthStatusExpanded}=False");
        Assert.IsFalse(pendingHint.Ancestors().Contains(details), "The compact hint must stay outside the collapsible details.");

        XElement checkButton = doc.Descendants(presentation + "Button").Single(element =>
            element.Attribute("Content")?.Value == "Check status"
            && element.Attribute("Command")?.Value == "{Binding ReadGatewayOAuthCommand}");
        Assert.AreSame(detailsToggle.Parent, checkButton.Parent,
            "The stable details toggle and Check status must remain side by side.");
        Assert.IsFalse(
            checkButton.Ancestors().Any(element =>
                element.Attribute("Visibility")?.Value?.Contains("IsInteractionAuthStatusExpanded", StringComparison.Ordinal) == true),
            "Check status must not be hidden together with the collapsible MCP details.");
    }

    [TestMethod]
    public async Task UserInput_OtherAndCancelAreExplicitTypedResponses()
    {
        IReadOnlyDictionary<string, UserInputAnswer>? submittedAnswers = null;
        UserInputAction? submittedAction = null;
        var request = new UserInputRequest
        {
            RequestId = "question-other",
            IsBlocking = true,
            Questions =
            [
                new UserInputQuestion
                {
                    Id = "q1",
                    Header = "Choice",
                    Question = "Select a value",
                    IsOther = true,
                    Options = [new UserInputOption { OptionId = "known", Label = "Known choice" }],
                },
            ],
        };
        var vm = new UserInputViewModel(request, (_, answers, action) =>
        {
            submittedAnswers = answers;
            submittedAction = action;
            return Task.CompletedTask;
        }, new SafeMarkdownService());

        Assert.IsFalse(vm.CanSubmit);
        Assert.IsNull(submittedAnswers);
        vm.Questions[0].IsOtherSelected = true;
        vm.Questions[0].OtherText = "custom response";
        await RunCommandAsync(vm.SubmitCommand);

        Assert.AreEqual(UserInputAction.Submit, submittedAction);
        Assert.AreEqual(UserInputAnswerKind.Other, submittedAnswers!["q1"].Kind);
        Assert.AreEqual("custom response", submittedAnswers["q1"].Text);

        submittedAnswers = null;
        submittedAction = null;
        var cancelVm = new UserInputViewModel(request, (_, answers, action) =>
        {
            submittedAnswers = answers;
            submittedAction = action;
            return Task.CompletedTask;
        }, new SafeMarkdownService());
        await RunCommandAsync(cancelVm.CancelCommand);

        Assert.AreEqual(UserInputAction.Cancel, submittedAction);
        Assert.AreEqual(0, submittedAnswers!.Count);
        Assert.IsTrue(cancelVm.IsResolved);
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
    public void ChoicePromptParser_DetectsEmphasizedTerminalConfirmationQuestion()
    {
        string text = string.Join('\n',
            "依存制約が変わっていないことを再確認しました。更新済みの変更は保持し、ゴールを回答待ち（blocked）にしました。",
            string.Empty,
            "残りの最新版化には対象フレームワークの変更が必要です。ご提示のAGENTS.mdの範囲外変更の確認ルールに従い、**.NET 11 RC2への移行を進めてよいですか？**");

        bool ok = ChoicePromptParser.TryParse(text, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, request.Questions.Count);
        Assert.AreEqual("残りの最新版化には対象フレームワークの変更が必要です。ご提示のAGENTS.mdの範囲外変更の確認ルールに従い、.NET 11 RC2への移行を進めてよいですか？", request.Questions[0].Question);
        Assert.AreEqual("Yes", request.Questions[0].Options[0].Label);
        Assert.AreEqual("No", request.Questions[0].Options[1].Label);
    }

    [TestMethod]
    [DataRow("**Do you want me to continue?**", "Do you want me to continue?")]
    [DataRow("*Do you want me to continue?*", "Do you want me to continue?")]
    [DataRow("__Do you want me to continue?__", "Do you want me to continue?")]
    [DataRow("**この変更を進めてよいですか？**", "この変更を進めてよいですか？")]
    [DataRow("__この変更を進めてよいですか？__", "この変更を進めてよいですか？")]
    public void ChoicePromptParser_NormalizesEmphasisBeforeTerminalQuestionDetection(string rawQuestion, string expectedQuestion)
    {
        bool ok = ChoicePromptParser.TryParse(rawQuestion, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(expectedQuestion, request.Questions[0].Question);
        Assert.AreEqual(2, request.Questions[0].Options.Count);
    }

    [TestMethod]
    public void ChoicePromptParser_BareConfirmationInsideCodeFence_DoesNotTriggerConfirmationCard()
    {
        // Cleaning inline Markdown must not make a fenced code example match the confirmation fallback.
        string text = "```markdown\n**Do you want me to continue?**\n```";

        Assert.IsFalse(ChoicePromptParser.TryParse(text, out _));
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
    public void ChoicePromptParser_DetectsSeveralBulletedQuestions()
    {
        string text = string.Join('\n',
            "「黒」はどちらのイメージですか？（赤・青も明るい画面のアクセント色として選べます。）",
            "",
            "- 黒を基調にしたダークテーマ",
            "- 黒はアクセント色、背景は明るいまま",
            "",
            "選択したテーマは次回アクセス時にも維持しますか？",
            "",
            "- ブラウザーに保存して次回も維持（おすすめ）",
            "- 画面を開いている間だけ適用",
            "",
            "テーマ切替UIはどこに置きますか？",
            "",
            "- 全ページ共通のナビゲーション（おすすめ）",
            "- ホーム画面だけ");

        bool ok = ChoicePromptParser.TryParse(text, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(3, request.Questions.Count);
        Assert.AreEqual("「黒」はどちらのイメージですか？（赤・青も明るい画面のアクセント色として選べます。）", request.Questions[0].Question);
        Assert.AreEqual("黒はアクセント色、背景は明るいまま", request.Questions[0].Options[1].Label);
        Assert.AreEqual("選択したテーマは次回アクセス時にも維持しますか？", request.Questions[1].Question);
        Assert.AreEqual(2, request.Questions[1].Options.Count);
        Assert.AreEqual("全ページ共通のナビゲーション（おすすめ）", request.Questions[2].Options[0].Label);
        CollectionAssert.AllItemsAreUnique(request.Questions.Select(static question => question.Id).ToArray());
    }

    [TestMethod]
    public void ChoicePromptParser_DetectsNumberedQuestionsWithNestedOptions()
    {
        string text = string.Join('\n',
            "Before I start, please decide:",
            "1. **Which database should I use?**",
            "   - PostgreSQL",
            "   - SQLite",
            "2. Should the API require authentication?",
            "   1) Yes, with OAuth",
            "   2) No",
            "   3) Later");

        bool ok = ChoicePromptParser.TryParse(text, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(2, request.Questions.Count);
        Assert.AreEqual("Which database should I use?", request.Questions[0].Question);
        Assert.AreEqual("SQLite", request.Questions[0].Options[1].Label);
        Assert.AreEqual(3, request.Questions[1].Options.Count);
    }

    [TestMethod]
    public void ChoicePromptParser_IgnoresQuestionsWithoutTwoOptions()
    {
        string text = string.Join('\n',
            "Which color do you prefer?",
            "- Red",
            "- Blue",
            "Anything else to add?",
            "",
            "Which layout?",
            "- Grid");

        bool ok = ChoicePromptParser.TryParse(text, out UserInputRequest request);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, request.Questions.Count);
        Assert.AreEqual("choice", request.Questions[0].Id);
        Assert.AreEqual("Which color do you prefer?", request.Questions[0].Question);
    }

    [TestMethod]
    [DataRow("仕様を決めるため、次の3点を確認中です。\n\n- 黒は、画面全体を暗くするテーマか、明るい画面のアクセント色か\n- 選択をブラウザーに保存するか\n\n回答をもとに実装計画をまとめます。", DisplayName = "bullets after a statement")]
    [DataRow("Why did the build fail?\nThe test runner exited early.\n- Missing SDK\n- Wrong path", DisplayName = "prose between the question and the bullets")]
    public void ChoicePromptParser_RejectsBulletsNotDirectlyUnderAQuestion(string text)
    {
        Assert.IsFalse(ChoicePromptParser.TryParse(text, out _));
    }

    [TestMethod]
    public async Task ChatViewModel_MultiQuestionProsePrompt_SendsEachAnswerInOneReply()
    {
        var bridge = new FakeWorkerBridge();
        var sentTexts = new List<string>();
        bridge.StartTurnHandler = (request, _) =>
        {
            sentTexts.Add(request.Text);
            return Task.FromResult($"multi-question-turn-{sentTexts.Count}");
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-multi-question" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-multi-question" });
        await RunCommandAsync(vm.JoinThreadCommand);

        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ThreadId = "thread-multi-question",
            TurnId = "agent-turn",
            ItemId = "agent-questions",
            Text = "Which theme?\n- Dark\n- Light\n\nWhere should the switch go?\n- Navigation bar\n- Home page only",
        });
        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "thread-multi-question",
            TurnId = "agent-turn",
        });

        UserInputViewModel card = vm.ActiveUserInput ?? throw new AssertFailedException("A multi-question card should be shown.");
        Assert.IsTrue(card.IsSynthetic);
        Assert.IsTrue(card.IsMultiStep);
        card.Questions[0].Options[0].IsSelected = true;
        card.Questions[1].Options[1].IsSelected = true;
        await RunCommandAsync(card.SubmitCommand);

        Assert.AreEqual(1, sentTexts.Count);
        Assert.AreEqual("Which theme?\n- Dark\n\nWhere should the switch go?\n- Home page only", sentTexts[0]);
        Assert.IsFalse(vm.HasActiveUserInput);
    }

    [TestMethod]
    public async Task ChatViewModel_ProseQuestionsInEarlierTurnMessage_ShowCardWhenTurnEndsWithStatusText()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseAgentMessageAsync(vm, "agent-questions", "「黒」はどちらのイメージですか？（補足）\n\n- ダークテーマ\n- アクセント色\n\nテーマ切替UIはどこに置きますか？\n\n- 全ページ共通\n- ホーム画面だけ");
        await RaiseAgentMessageAsync(vm, "agent-status", "選択UIの場所と黒テーマの意味について回答を待っています。");
        await RaiseAgentMessageAsync(vm, "agent-final", "仕様を決めるため、次の2点を確認中です。\n\n- 黒の意味\n- 切替UIの場所\n\n回答をもとに実装計画をまとめます。");
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });

        UserInputViewModel card = vm.ActiveUserInput ?? throw new AssertFailedException("The earlier question message should produce a card.");
        Assert.IsTrue(card.IsSynthetic);
        Assert.AreEqual(2, card.Questions.Count);
        Assert.AreEqual("テーマ切替UIはどこに置きますか？", card.Questions[1].Question);
    }

    [TestMethod]
    public async Task ChatViewModel_LastMessageChoiceWinsOverEarlierProseQuestions()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseAgentMessageAsync(vm, "agent-early", "Which database?\n- PostgreSQL\n- SQLite");
        await RaiseAgentMessageAsync(vm, "agent-last", "Which deployment target?\n1. Azure\n2. On-premises\n3. Both");
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });

        Assert.IsNotNull(vm.ActiveUserInput);
        Assert.AreEqual(1, vm.ActiveUserInput!.Questions.Count);
        Assert.AreEqual("Which deployment target?", vm.ActiveUserInput.Questions[0].Question);
    }

    [TestMethod]
    public async Task ChatViewModel_EarlierConfirmationQuestionWithoutOptions_DoesNotShowCard()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseAgentMessageAsync(vm, "agent-early", "MIT ライセンスで進めてよいですか？");
        await RaiseAgentMessageAsync(vm, "agent-last", "MIT ライセンスで作成しました。");
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });

        Assert.IsFalse(vm.HasActiveUserInput, "A settled confirmation question in an earlier message must not produce a card.");
    }

    private static Task RaiseAgentMessageAsync(ChatViewModel vm, string itemId, string text)
        => RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ItemId = itemId,
            Text = text,
        });

    [TestMethod]
    public void ChoicePromptParser_OptionQuestionsOnly_RejectsLooseNumberedListAfterQuestion()
    {
        string text = "Why did the build fail?\nThe runner stopped early. I checked:\n1. The SDK version\n2. The output path";

        Assert.IsTrue(ChoicePromptParser.TryParse(text, out _), "The last message keeps the loose numbered-list fallback.");
        Assert.IsFalse(ChoicePromptParser.TryParseOptionQuestions(text, out _), "Earlier messages require options directly under the question.");
    }

    [TestMethod]
    public async Task ChatViewModel_ProseQuestionsSixteenMessagesBeforeTheLast_StillShowCard()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnStarted });
        await RaiseAgentMessageAsync(vm, "agent-question", "Which database?\n- PostgreSQL\n- SQLite");
        for (int i = 0; i < 15; i++)
        {
            await RaiseAgentMessageAsync(vm, $"agent-status-{i}", "Still waiting for your answer.");
        }

        await RaiseAgentMessageAsync(vm, "agent-final", "I will continue after your answer.");
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted });

        Assert.IsNotNull(vm.ActiveUserInput, "The question is the sixteenth message before the last one.");
        Assert.AreEqual("Which database?", vm.ActiveUserInput!.Questions[0].Question);
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
    public async Task ChatViewModel_GoalBlockedMarkdownConfirmation_ShowsOneCardAndSendsOnlyAfterSubmit()
    {
        using GoalConfirmationFixture fixture = await CreateGoalConfirmationFixtureAsync();

        await fixture.PublishFinalQuestionAsync();
        Assert.IsFalse(fixture.ViewModel.HasActiveUserInput, "A Goal confirmation must wait until the turn completion event.");
        Assert.AreEqual(0, fixture.StartTurnCallCount, "Rendering the question must not submit an answer automatically.");

        await fixture.CompleteTurnAsync();

        Assert.IsNotNull(fixture.Card);
        Assert.IsTrue(fixture.Card!.IsSynthetic);
        Assert.AreEqual(1, fixture.ViewModel.PendingInteractions.Count(card => card.UserInput is not null));
        Assert.AreEqual("残りの最新版化には対象フレームワークの変更が必要です。ご提示のAGENTS.mdの範囲外変更の確認ルールに従い、.NET 11 RC2への移行を進めてよいですか？", fixture.Card.Questions[0].Question);
        Assert.AreEqual(2, fixture.Card.Questions[0].Options.Count);
        Assert.AreEqual("Yes", fixture.Card.Questions[0].Options[0].DisplayLabel);
        Assert.AreEqual("No", fixture.Card.Questions[0].Options[1].DisplayLabel);
        Assert.AreEqual(0, fixture.StartTurnCallCount, "Displaying the card must not send a response.");

        fixture.SelectYes();
        Assert.AreEqual(0, fixture.StartTurnCallCount, "Selecting an option alone must not send a response.");

        await fixture.SubmitAsync();

        Assert.AreEqual(1, fixture.StartTurnCallCount);
        Assert.AreEqual("Yes", fixture.LastStartTurnRequest!.Text);
        Assert.IsFalse(fixture.ViewModel.HasActiveUserInput);
    }

    [TestMethod]
    public async Task ChatViewModel_GoalConfirmationFromPreviousOwner_CannotSubmitAfterOwnerChanges()
    {
        using GoalConfirmationFixture fixture = await CreateGoalConfirmationFixtureAsync();
        await fixture.PublishFinalQuestionAsync();
        await fixture.CompleteTurnAsync();
        Assert.IsNotNull(fixture.Card);
        fixture.SelectYes();

        await fixture.ChangeOwnerAsync();
        await fixture.SubmitAsync();

        Assert.AreEqual(0, fixture.StartTurnCallCount, "A card created by a previous owner must not send a response.");
    }

    // Shared with the visual regression harness so it can capture the same Goal flow before
    // TurnCompleted, after the card appears, and after the user explicitly submits Yes.
    private static async Task<GoalConfirmationFixture> CreateGoalConfirmationFixtureAsync()
    {
        const string threadId = "thread-goal-confirmation";
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal(threadId) };
        var viewModel = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = threadId };
        viewModel.Threads.Add(thread);
        WorkerStatus ready = OwnerStatus("goal-confirmation-owner", 1, 1);
        ready.ThreadId = threadId;
        await bridge.PublishStateAsync(ready);
        viewModel.SelectedThread = thread;
        await RunCommandAsync(viewModel.JoinThreadCommand);
        await bridge.PublishThreadGoalChangedAsync(new ThreadGoalEvent
        {
            ThreadId = threadId,
            Goal = ActiveGoal(threadId),
        });

        return new GoalConfirmationFixture(bridge, viewModel, threadId, "goal-confirmation-turn");
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
    public void SafeMarkdown_LiteralTextRemovesControlsWithoutChangingMarkdownOrHtmlCharacters()
    {
        string text = SafeMarkdownService.ToSafeLiteralText("**literal** <b>value</b>\x1b[31m\x00");

        Assert.AreEqual("**literal** <b>value</b>", text);
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
        var vm = new ApprovalViewModel(request, (_, _, _) => Task.CompletedTask, new SafeMarkdownService());

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
        var vm = new ApprovalViewModel(request, (_, _, _) => Task.CompletedTask, new SafeMarkdownService());

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
        var vm = new ApprovalViewModel(request, (_, _, _) => Task.CompletedTask, new SafeMarkdownService());

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
        var answers = new Dictionary<string, UserInputAnswer>(StringComparer.Ordinal)
        {
            ["q1"] = new() { Kind = UserInputAnswerKind.FreeText, Text = "<b>Sharp</b> \x1b[31mstyle\x1b[0m" },
        };

        vm.AppendUserInputResultItem(answers);

        ChatItemViewModel result = vm.Items.Single(item => item.Role == "Decision");
        Assert.AreEqual("Response submitted", result.Text);
    }

    [TestMethod]
    public void ChatViewModel_UserInputResult_NoSelection_AppendsNothing()
    {
        using var vm = new ChatViewModel();

        vm.AppendUserInputResultItem(new Dictionary<string, UserInputAnswer>(StringComparer.Ordinal));

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

        await (Task)resolve.Invoke(vm, ["req-fail", ApprovalDecision.Accept, null, CaptureOwnerSnapshot(vm)])!;

        Assert.IsFalse(vm.Items.Any(item => item.Role == "Decision"));
        Assert.IsTrue(vm.Items.Any(item => item.IsDeliveryUnknown));
        StringAssert.Contains(vm.RecoveryStatusText, "outcome is unknown");
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
        await (Task)resolve.Invoke(vm, ["req-ok", ApprovalDecision.Accept, null, CaptureOwnerSnapshot(vm)])!;

        ChatItemViewModel result = vm.Items.Single(item => item.Role == "Decision");
        Assert.IsTrue(result.Text.Contains("Accepted", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("git push --force", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_UserInputRpcFailure_DoesNotAppendTranscriptLine()
    {
        var bridge = new FakeWorkerBridge { ResolveUserInputException = new InvalidOperationException("disconnected") };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        var answers = new Dictionary<string, UserInputAnswer>(StringComparer.Ordinal)
        {
            ["q1"] = new() { Kind = UserInputAnswerKind.FreeText, Text = "Yes" },
        };

        MethodInfo resolve = typeof(ChatViewModel).GetMethod(
            "ResolveUserInputAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)resolve.Invoke(vm, ["req-1", answers, UserInputAction.Submit, CaptureOwnerSnapshot(vm)])!;

        Assert.IsFalse(vm.Items.Any(item => item.Role == "Decision"));
        Assert.IsTrue(vm.Items.Any(item => item.IsDeliveryUnknown));
        StringAssert.Contains(vm.RecoveryStatusText, "outcome is unknown");
    }

    [TestMethod]
    public async Task ChatViewModel_UserInputRpcSuccess_AppendsTranscriptLineAfterResolve()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        var answers = new Dictionary<string, UserInputAnswer>(StringComparer.Ordinal)
        {
            ["q1"] = new() { Kind = UserInputAnswerKind.FreeText, Text = "Yes" },
        };

        MethodInfo resolve = typeof(ChatViewModel).GetMethod(
            "ResolveUserInputAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)resolve.Invoke(vm, ["req-1", answers, UserInputAction.Submit, CaptureOwnerSnapshot(vm)])!;

        ChatItemViewModel result = vm.Items.Single(item => item.Role == "Decision");
        Assert.AreEqual("Response submitted", result.Text);
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
    public async Task ChatViewModel_FinalPlanReplacesProvisionalDeltaAndIgnoresLateDelta()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.PlanDelta,
            ItemId = "plan-item",
            TurnId = "plan-turn",
            PlanDelta = new TurnPlanDeltaEvent
            {
                ThreadId = "test-thread",
                TurnId = "plan-turn",
                ItemId = "plan-item",
                Text = "Draft plan",
            },
        });

        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.ItemCompleted,
            ItemId = "plan-item",
            TurnId = "plan-turn",
            Plan = new TurnPlanSnapshot
            {
                ThreadId = "test-thread",
                TurnId = "plan-turn",
                IsComplete = true,
                Steps =
                [
                    new PlanStepInfo { StepId = "step-1", Text = "Final step", Status = "completed" },
                ],
            },
        });

        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.PlanDelta,
            ItemId = "plan-item",
            TurnId = "plan-turn",
            PlanDelta = new TurnPlanDeltaEvent
            {
                ThreadId = "test-thread",
                TurnId = "plan-turn",
                ItemId = "plan-item",
                Text = "Late provisional text",
            },
        });

        ChatItemViewModel plan = vm.Items.Single(item => item.ItemId == "plan-item");
        Assert.IsTrue(plan.IsPlanComplete);
        Assert.AreEqual(string.Empty, plan.PlanDeltaText);
        Assert.AreEqual(1, plan.StructuredPlanSteps.Count);
        Assert.AreEqual("Final step", plan.StructuredPlanSteps[0].Text);
        Assert.AreEqual("completed", plan.StructuredPlanSteps[0].StatusText);
    }

    [TestMethod]
    public void ChatItemViewModel_CompletedPlanKeepsStepsAndShowsFinalText()
    {
        var markdown = new SafeMarkdownService();
        var item = new ChatItemViewModel("Plan", string.Empty, ConversationEventKind.PlanUpdated);
        item.SetPlanSnapshot(new TurnPlanSnapshot
        {
            ThreadId = "t", TurnId = "u",
            Steps = [new PlanStepInfo { StepId = "0", Text = "Step one", Status = "inProgress" }],
        }, markdown);

        item.SetPlanSnapshot(new TurnPlanSnapshot { ThreadId = "t", TurnId = "u", IsComplete = true }, markdown,
            finalText: "Final plan text");

        Assert.AreEqual(1, item.StructuredPlanSteps.Count);
        Assert.AreEqual("Final plan text", item.PlanDeltaText);
        Assert.IsTrue(item.IsPlanComplete);
    }

    [TestMethod]
    public async Task ChatViewModel_CompletedAgentTextIsNotRepeatedAsArtifactCard()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.ItemCompleted,
            ItemId = "agent-item",
            TurnId = "agent-turn",
            Text = "final answer",
            Parts = [new ArtifactPart { Kind = ArtifactPartKind.Text, Text = "final answer" }],
        });

        ChatItemViewModel item = vm.Items.Single(value => value.ItemId == "agent-item");
        StringAssert.Contains(item.Text, "final answer");
        Assert.AreEqual(0, item.ArtifactParts.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_FallbackCompletionKeepsStreamedReasoningText()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);
        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.ReasoningSummaryDelta,
            ItemId = "reasoning-item",
            TurnId = "reasoning-turn",
            Text = "streamed reasoning",
        });

        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.ItemCompleted,
            ItemId = "reasoning-item",
            TurnId = "reasoning-turn",
            Parts = [new ArtifactPart { Kind = ArtifactPartKind.Fallback, Text = "The reasoning result is not supported for display." }],
        });

        ChatItemViewModel item = vm.Items.Single(value => value.ItemId == "reasoning-item");
        StringAssert.Contains(item.Text, "streamed reasoning");
        Assert.AreEqual(0, item.ArtifactParts.Count);
        Assert.IsTrue(item.IsHistoryCompleted);
    }

    [TestMethod]
    public void ChatItemViewModel_AppendPlanDeltaClipsAtUtf8LimitWithoutSplittingSurrogates()
    {
        var markdown = new SafeMarkdownService();
        var item = new ChatItemViewModel("Plan", string.Empty, ConversationEventKind.PlanUpdated);
        item.AppendPlanDelta("ab", 5, markdown);
        item.AppendPlanDelta("😀c", 5, markdown);

        Assert.AreEqual("ab", item.PlanDeltaText);
        Assert.IsTrue(item.PlanWasTruncated);

        item.AppendPlanDelta("é", 5, markdown);
        Assert.AreEqual("abé", item.PlanDeltaText);
    }

    [TestMethod]
    public async Task ChatViewModel_TypedArtifactCompletionCreatesAndReplacesOneItem()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);
        foreach (ConversationEventKind kind in new[] { ConversationEventKind.ItemStarted, ConversationEventKind.ItemCompleted })
        {
            await RaiseConversationEventAsync(vm, new ConversationEvent
            {
                Kind = kind, ItemId = "artifact-item", TurnId = "artifact-turn",
                Parts = [new ArtifactPart { Kind = ArtifactPartKind.File, DisplayName = "result.txt",
                    ActionId = "opaque-action", AllowedActions = [ArtifactActionKind.Open, ArtifactActionKind.Reveal] }],
            });
        }
        ChatItemViewModel item = vm.Items.Single(value => value.ItemId == "artifact-item");
        Assert.AreEqual(1, item.ArtifactParts.Count);
        Assert.IsTrue(item.ArtifactParts[0].CanOpen);
        Assert.IsFalse(item.ArtifactParts[0].CanPreview);
        Assert.IsTrue(item.IsHistoryCompleted);
    }

    [TestMethod]
    public void ChatViewModel_KnownSavedAttachmentUsesOwnerBoundFileActions()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);
        MethodInfo merge = typeof(ChatViewModel).GetMethod("MergeAttachment", BindingFlags.Instance | BindingFlags.NonPublic)!;
        merge.Invoke(vm, [new ThreadAttachmentMetadata
        {
            Id = "saved-file", AttachmentType = "relaycodex.file.v1", IdentityKey = "opaque-identity",
            IsKnownPayload = true, CanRemove = true, DisplayName = "result.png", MimeType = "image/png",
            ActionId = "opaque-preview", AllowedActions = [ArtifactActionKind.Preview, ArtifactActionKind.Open],
        }]);
        ThreadAttachmentPresentationViewModel saved = vm.ThreadAttachments.Single();
        Assert.IsNotNull(saved.FileActions);
        Assert.IsTrue(saved.FileActions.CanPreview);
        Assert.IsTrue(saved.FileActions.CanOpen);
        Assert.IsFalse(saved.FileActions.CanReveal);
        Assert.IsTrue(saved.CanRemove);
        Assert.IsFalse(saved.IsRemovalConfirmationOpen);
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
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false);

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
    public void ChatToolWindowXaml_ThreadHistoryShowsAccessibleReadStateActions()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement? rows = doc.Descendants(presentation + "ListBox")
            .SingleOrDefault(list => list.Attribute("ItemsSource")?.Value == "{Binding ThreadRows}");
        Assert.IsNotNull(rows, "The history list must bind row presentation models.");
        Assert.AreEqual("{Binding SelectedThreadRow, Mode=TwoWay}", rows!.Attribute("SelectedItem")?.Value);
        XElement? unread = rows.Descendants(presentation + "TextBlock")
            .SingleOrDefault(text => text.Attribute("Text")?.Value == "{Binding ReadStateBadgeText}");
        Assert.IsNotNull(unread, "Unread conversations need a visible row marker.");
        Assert.AreEqual("{Binding IsUnread, Converter={StaticResource BoolToVis}}", unread!.Attribute("Visibility")?.Value);
        Assert.AreEqual("Unread conversation", unread.Attribute(XName.Get("AutomationProperties.Name"))?.Value);

        XElement[] actions = doc.Descendants(presentation + "Button")
            .Where(button => button.Attribute("Content")?.Value is "Mark as read" or "Mark as unread")
            .ToArray();
        Assert.AreEqual(2, actions.Length);
        Assert.IsTrue(actions.All(button => button.Attribute(XName.Get("AutomationProperties.Name")) is not null));
        Assert.IsTrue(actions.All(button => button.Attribute(XName.Get("AutomationProperties.HelpText")) is not null));
        Assert.AreEqual("{Binding CanMarkSelectedThreadRead, Converter={StaticResource BoolToVis}}", actions.Single(button => button.Attribute("Content")?.Value == "Mark as read").Attribute("Visibility")?.Value);
        Assert.AreEqual("{Binding CanMarkSelectedThreadUnread, Converter={StaticResource BoolToVis}}", actions.Single(button => button.Attribute("Content")?.Value == "Mark as unread").Attribute("Visibility")?.Value);
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

        // Ctrl+Enter (scoped to the composer via TextBox.InputBindings) invokes SendKeyCommand,
        // which only sends and never triggers the Goal Stop primary action.
        // Attribute values are matched case-sensitively (XAML is case-sensitive).
        XElement? keyBinding = composer
            .Element(presentation + "TextBox.InputBindings")?
            .Elements(presentation + "KeyBinding")
            .SingleOrDefault(kb => kb.Attribute("Key")?.Value == "Return" && kb.Attribute("Modifiers")?.Value == "Control");
        Assert.IsNotNull(keyBinding, "Composer TextBox.InputBindings must contain a Ctrl+Enter (Key=Return, Modifiers=Control) KeyBinding.");
        Assert.AreEqual(
            "{Binding SendKeyCommand}",
            keyBinding!.Attribute("Command")?.Value,
            "The Ctrl+Enter KeyBinding must invoke SendKeyCommand.");
    }

    // Remote UI replicates only [DataMember] properties of [DataContract] types into the
    // VS-side data context proxy; a type without the attributes serializes as an empty
    // object and every binding to it fails silently (blank text, empty button content).
    private static readonly Type[] RemoteUiContextTypes =
        [
            typeof(ChatViewModel), typeof(ChatItemViewModel), typeof(ChatBlockViewModel), typeof(ApprovalViewModel),
            typeof(InteractionCardViewModel), typeof(ApprovalChoiceViewModel), typeof(PermissionSelectionViewModel),
            typeof(PermissionGrantOptionViewModel), typeof(McpElicitationViewModel), typeof(McpElicitationFieldViewModel),
            typeof(McpElicitationChoiceViewModel), typeof(InteractionAuthStatusPresentationViewModel),
            typeof(McpServerAuthPresentationViewModel), typeof(UserInputViewModel), typeof(UserInputQuestionViewModel), typeof(UserInputOptionViewModel),
            typeof(SuggestionChip), typeof(SlashCommandPresentationViewModel),
            typeof(SlashCommandSuggestionViewModel), typeof(SlashCommandOptionViewModel),
            typeof(AttachmentChipViewModel), typeof(FileSuggestionPresentationViewModel),
            typeof(FileSuggestionViewModel), typeof(ReasoningEffortOption), typeof(ServiceTierOption),
            typeof(PendingSkillViewModel), typeof(UsagePresentation),
            typeof(WorkerStatus), typeof(ThreadSummary), typeof(ThreadAttachmentPresentationViewModel),
            typeof(ThreadHistoryRowViewModel),
            typeof(ShellCommandConfirmationPresentation), typeof(PlanStepPresentationViewModel),
            typeof(AppServerNoticePresentationViewModel), typeof(ArtifactPartPresentationViewModel),
            typeof(RemoteProfilesPresentationViewModel), typeof(RemoteProfileViewModel),
            typeof(ConnectionHealthPresentationViewModel), typeof(ConnectionTargetSnapshot),
            typeof(PresetSlotViewModel),
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
    public void ChatToolWindowXaml_InteractionCards_AreBoundedVirtualizedAndExplicit()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement? interactionCards = doc
            .Descendants(presentation + "ListBox")
            .SingleOrDefault(list => list.Attribute("ItemsSource")?.Value == "{Binding PendingInteractions}");
        Assert.IsNotNull(interactionCards, "Pending interactions must render as a dedicated ListBox.");
        Assert.AreEqual("300", interactionCards!.Attribute("MaxHeight")?.Value);
        string xaml = doc.ToString(SaveOptions.DisableFormatting);
        Assert.IsTrue(xaml.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", StringComparison.Ordinal));

        string[] actionableBindings =
        [
            "{Binding Approval.DisplayText}", "{Binding SelectCommand}",
            "{Binding UserInput.SubmitCommand}", "{Binding UserInput.CancelCommand}",
            "{Binding Permission.SubmitCommand}", "{Binding Permission.DeclineCommand}",
            "{Binding Elicitation.SubmitCommand}", "{Binding Elicitation.DeclineCommand}",
        ];
        foreach (string binding in actionableBindings)
        {
            Assert.IsTrue(xaml.Contains(binding, StringComparison.Ordinal), $"Missing explicit interaction binding {binding}.");
        }
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
        var store = new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true, ReasoningEffortId = "HIGH" });
        var bridge = new FakeWorkerBridge
        {
            ExperimentalApi = true,
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
        var store = new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true, ServiceTierId = "FAST" });
        var bridge = new FakeWorkerBridge
        {
            ExperimentalApi = true,
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
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await RunCommandAsync(vm.JoinThreadCommand);
        int rateLimitCallsBeforeStatus = bridge.RateLimitCallCount;
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "turn-1",
        });

        await InvokeComposerSendAsync(vm);

        Assert.IsNull(bridge.LastSteerTurnRequest);
        Assert.AreEqual(rateLimitCallsBeforeStatus + 1, bridge.RateLimitCallCount);
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
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await RunCommandAsync(vm.JoinThreadCommand);
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
    public async Task ChatViewModel_PlanWithPrompt_StaysEnabledForFollowingTurns()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
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
        Assert.IsTrue(vm.PlanModeEnabled);
        await SendMessageAsync(vm, "continue planning", clearComposer: false);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanWithoutPrompt_StaysEnabledUntilExplicitlyDisabled()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
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
        Assert.IsTrue(vm.PlanModeEnabled);

        await SendMessageAsync(vm, "second", clearComposer: false);

        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);

        vm.PlanModeEnabled = false;
        await SendMessageAsync(vm, "third", clearComposer: false);

        Assert.AreEqual("default", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
        await SendMessageAsync(vm, "fourth", clearComposer: false);
        Assert.AreEqual("default", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanMode_IsolatedBySelectedThread()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = "thread-plan" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        vm.PlanModeEnabled = true;
        vm.SelectedThread = new ThreadSummary { Id = "thread-default" };
        Assert.IsFalse(vm.PlanModeEnabled);

        vm.PlanModeEnabled = false;
        vm.SelectedThread = new ThreadSummary { Id = "thread-plan" };
        Assert.IsTrue(vm.PlanModeEnabled);

        vm.SelectedThread = new ThreadSummary { Id = "thread-default" };
        Assert.IsFalse(vm.PlanModeEnabled);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChatViewModel_TransientRecoveryRestoresPlanModeOnlyAfterReconnecting(bool reconnected)
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore());
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        vm.SelectedThread = new ThreadSummary { Id = "thread-plan" };
        vm.PlanModeEnabled = true;
        vm.SelectedThread = new ThreadSummary { Id = "thread-default" };
        Assert.IsFalse(vm.PlanModeEnabled);

        // Mirrors HandleConnectionLostAsync, then the replacement Worker reports a new owner
        // partition (every attempt gets one), which clears owner-scoped state.
        InvokePrivate(vm, "CapturePlanModeForRecovery");
        await bridge.PublishStateAsync(OwnerStatus("owner-a-recovered", 2, 5));
        Assert.IsFalse(vm.PlanModeEnabled);
        await InvokeFinishRecoveryAsync(vm, reconnected ? ConnectionRecoveryOutcome.Reconnected : ConnectionRecoveryOutcome.Stopped);

        vm.SelectedThread = new ThreadSummary { Id = "thread-plan" };
        Assert.AreEqual(
            reconnected,
            vm.PlanModeEnabled,
            "Plan selections survive a recovery that reconnects to the validated target, and only that.");
        vm.SelectedThread = new ThreadSummary { Id = "thread-default" };
        Assert.IsFalse(vm.PlanModeEnabled);
    }

    [TestMethod]
    public async Task ChatViewModel_TransientRecoveryAppliesReplacementWorkerWithLowerGenerations()
    {
        var bridge = new FakeWorkerBridge();
        WorkerStatus recovered = OwnerStatus("worker-b-owner", 2, 1);
        recovered.CodexVersion = "0.163.0";
        var replacement = new FakeWorkerBridge
        {
            ConnectHandler = () => Task.FromResult(recovered),
            ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
            {
                Threads = [new ThreadSummary { Id = "thread-recovered" }],
            }),
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings()),
            bridgeFactory: () => replacement);
        SetWorkingDirectory(vm, Path.GetTempPath());

        // The retired Worker restarted its app-server before it exited, so its owner and connection
        // generations are higher than the ones a fresh replacement Worker process starts from.
        await bridge.PublishStateAsync(OwnerStatus("worker-a-owner", 4, 3));
        WorkerStatus degraded = OwnerStatus("worker-a-owner", 4, 3);
        degraded.State = WorkerConnectionState.Degraded;
        degraded.RecoveryFailureKind = WorkerRecoveryFailureKind.WorkerProcessExit;
        await bridge.PublishStateAsync(degraded);

        MethodInfo method = typeof(ChatViewModel).GetMethod("HandleConnectionLostAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(vm, [bridge, WorkerRecoveryFailureKind.WorkerProcessExit])!;

        Assert.AreEqual(WorkerConnectionState.Ready, vm.Status.State);
        Assert.AreEqual("worker-b-owner", vm.Status.Target!.StatePartitionFingerprint);
        Assert.AreEqual(2, vm.Status.Target.OwnerGeneration);
        Assert.IsFalse(vm.IsRecovering);
        Assert.AreEqual("thread-recovered", vm.Threads.Single().Id);

        // Later replacement notifications stay current instead of being dropped as stale.
        WorkerStatus busy = OwnerStatus("worker-b-owner", 2, 1);
        busy.State = WorkerConnectionState.Busy;
        await replacement.PublishStateAsync(busy);
        Assert.AreEqual(WorkerConnectionState.Busy, vm.Status.State);
    }

    [TestMethod]
    public async Task ChatViewModel_QueuedPlanForOtherThreadDoesNotChangeSelectedThreadMode()
    {
        const string threadA = "thread-plan-queue";
        const string threadB = "thread-current";
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = threadB },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = threadB });
        await RunCommandAsync(vm.JoinThreadCommand);

        // A queued invocation keeps its original target even if the user changes selection
        // before the queue reaches it.
        bool started = await InvokeExecutePlanAsync(vm, threadA, "plan the queued change");

        Assert.IsTrue(started);
        Assert.AreEqual(threadA, bridge.LastStartTurnRequest!.ThreadId);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest.CollaborationMode!.Mode);
        Assert.IsFalse(vm.PlanModeEnabled, "The selected thread B composer must not inherit thread A's queued Plan mode.");

        vm.SelectedThread = new ThreadSummary { Id = threadA };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = threadA });
        await RunCommandAsync(vm.JoinThreadCommand);
        await SendMessageAsync(vm, "continue A", clearComposer: false);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);

        vm.SelectedThread = new ThreadSummary { Id = threadB };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = threadB });
        await RunCommandAsync(vm.JoinThreadCommand);
        await SendMessageAsync(vm, "continue B", clearComposer: false);
        Assert.AreEqual("default", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanMode_ToggleDuringStartAppliesToNextTurnOnly()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.StartTurnHandler = async (_, _) =>
        {
            startEntered.TrySetResult();
            await WaitForAsync(() => releaseStart.Task.IsCompleted);
            return "turn-1";
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            PlanModeEnabled = true,
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });

        Task firstSend = SendMessageAsync(vm, "first", clearComposer: false);
        await WaitForAsync(() => startEntered.Task.IsCompleted);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
        vm.PlanModeEnabled = false;
        releaseStart.SetResult("turn-1");
        await firstSend;

        bridge.StartTurnHandler = null;
        await SendMessageAsync(vm, "second", clearComposer: false);
        Assert.AreEqual("default", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanMode_RemainsEnabledWhenTurnStartFails()
    {
        var bridge = new FakeWorkerBridge { StartTurnException = new InvalidOperationException("simulated failure"), ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        vm.PlanModeEnabled = true;

        await SendMessageAsync(vm, "fails", clearComposer: false);
        Assert.IsTrue(vm.PlanModeEnabled);

        bridge.StartTurnException = null;
        await SendMessageAsync(vm, "retries after review", clearComposer: false);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
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
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await RunCommandAsync(vm.JoinThreadCommand);
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
        await SendMessageAsync(vm, "fails", clearComposer: false);
        Assert.IsTrue(vm.Items.Any(static item => item.IsDeliveryUnknown));
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
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: store)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
            ComposerText = "/status",
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
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
        await SendMessageAsync(vm, "fails", clearComposer: false);
        Assert.IsTrue(vm.Items.Any(static item => item.IsDeliveryUnknown));
        bridge.StartTurnException = null;
        await SendMessageAsync(vm, "succeeds", clearComposer: false);
        Assert.AreEqual("high", bridge.LastStartTurnRequest!.Effort);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanModeUnavailableWithoutExperimentalApi()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });

        vm.PlanModeEnabled = true;
        Assert.IsFalse(vm.IsPlanModeAvailable);
        Assert.IsFalse(vm.PlanModeEnabled, "Plan mode cannot be active without the experimental collaborationMode field.");
        StringAssert.Contains(vm.PlanModeHelpText, "Experimental API");

        bool started = await InvokeExecutePlanAsync(vm, "thread-1", "plan the change");
        Assert.IsFalse(started);
        Assert.IsNull(bridge.LastStartTurnRequest, "/plan must not start a turn the app-server would reject.");

        await SendMessageAsync(vm, "ordinary turn", clearComposer: false);
        Assert.IsNotNull(bridge.LastStartTurnRequest);
        Assert.IsNull(bridge.LastStartTurnRequest!.CollaborationMode, "A non-experimental connection must omit collaborationMode.");

        vm.ExperimentalApiEnabled = true;
        Assert.IsFalse(vm.IsPlanModeAvailable, "The saved setting applies only after reconnecting.");
        StringAssert.Contains(vm.PlanModeHelpText, "after reconnecting");

        bridge.ExperimentalApi = true;
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        Assert.IsTrue(vm.IsPlanModeAvailable);
        Assert.IsTrue(vm.PlanModeEnabled, "The stored selection reappears once the connection supports Plan mode.");
    }

    [TestMethod]
    public async Task ChatViewModel_ExperimentalConnectionKeepsExplicitDefaultAfterSettingIsTurnedOff()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        vm.PlanModeEnabled = true;
        await SendMessageAsync(vm, "plan first", clearComposer: false);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);

        // The setting changes only the next connect; the live connection still negotiated
        // experimentalApi, so Plan stays usable and disabling it must still send default.
        vm.ExperimentalApiEnabled = false;
        Assert.IsTrue(vm.IsPlanModeAvailable);
        vm.PlanModeEnabled = false;
        await SendMessageAsync(vm, "implement now", clearComposer: false);

        Assert.AreEqual("default", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
    }

    [TestMethod]
    public async Task ChatViewModel_MissingModelWithRequiredCollaborationModeKeepsComposerAndSendsNothing()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await RunCommandAsync(vm.JoinThreadCommand);
        // A failed or empty model catalog leaves no selection; the picker setter ignores a null
        // write-back while a valid entry is selected, so clear the backing field directly.
        typeof(ChatViewModel).GetField("selectedModel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, null);
        vm.ComposerText = "keep this draft";

        await SendMessageAsync(vm, "keep this draft", clearComposer: true);

        Assert.AreEqual(0, bridge.StartTurnCallCount);
        Assert.IsFalse(vm.Items.Any(item => item.Role == "You"), "No optimistic user row may be left behind.");
        Assert.IsTrue(vm.Items.Any(item => item.Text.Contains("Select a model", StringComparison.Ordinal)));
        Assert.AreEqual("keep this draft", vm.ComposerText);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanModeAloneDoesNotQuarantineADraft()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore());
        await bridge.PublishStateAsync(OwnerStatus("owner-a", 1, 4));
        vm.PlanModeEnabled = true;

        await bridge.PublishStateAsync(OwnerStatus("owner-b", 2, 5));

        Assert.IsNull(vm.QuarantinedDraft, "A setting with an empty composer is not draft content.");
        Assert.IsFalse(vm.PlanModeEnabled);
    }

    [TestMethod]
    public async Task ChatViewModel_NewThreadTakesOverThreadlessPlanSelection()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore());
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        vm.PlanModeEnabled = true;

        await SendMessageAsync(vm, "start planning", clearComposer: false);

        Assert.IsNotNull(vm.SelectedThread);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
        vm.PlanModeEnabled = false;
        InvokePrivate(vm, "CapturePlanModeForRecovery");
        await bridge.PublishStateAsync(OwnerStatus("owner-recovered", 2, 5));
        await InvokeFinishRecoveryAsync(vm, ConnectionRecoveryOutcome.Reconnected);
        Assert.IsNull(vm.SelectedThread);
        Assert.IsFalse(vm.PlanModeEnabled, "The threadless selection moved to the created conversation and must not reappear.");
    }

    [TestMethod]
    public async Task ChatViewModel_ThreadlessPlanCommandEnablesTheThreadlessComposer()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore());
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        bool succeeded = await InvokeExecutePlanAsync(vm, null, string.Empty);

        Assert.IsTrue(succeeded);
        Assert.IsTrue(vm.PlanModeEnabled, "Argumentless /plan without a conversation must check the threadless composer.");
        await SendMessageAsync(vm, "start planning", clearComposer: false);
        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
        Assert.IsTrue(vm.PlanModeEnabled, "The selection moves to the created conversation.");
    }

    [TestMethod]
    public async Task ChatViewModel_ImplementationOfferUsesTheModeTheTurnStartedWith()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreatePlanThreadAsync(bridge);

        // Unchecking while the Plan turn runs affects only later turns.
        vm.PlanModeEnabled = false;
        await CompletePlanTurnAsync(vm, "- Step");

        Assert.IsNotNull(vm.ActiveUserInput, "The completed turn was a Plan turn, so it still offers implementation.");
    }

    [TestMethod]
    public async Task ChatViewModel_ProposedPlanRendersAsMarkdownAndOffersImplementation()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreatePlanThreadAsync(bridge);

        await RaiseConversationEventAsync(vm, new ConversationEvent
        {
            Kind = ConversationEventKind.PlanDelta,
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "turn-1-plan",
            PlanDelta = new TurnPlanDeltaEvent { ThreadId = "thread-1", TurnId = "turn-1", ItemId = "turn-1-plan", Text = "## Plan\n\n- Add" },
        });
        ChatItemViewModel plan = vm.Items.Single(item => item.ItemId == "turn-1-plan");
        Assert.IsTrue(plan.IsProposedPlan);
        Assert.IsTrue(plan.UsesBlockRendering);
        Assert.IsFalse(plan.ShowPlanDeltaText, "Markdown blocks replace the raw provisional text.");
        Assert.AreEqual("Proposed plan", plan.DisplayRole);
        Assert.IsTrue(plan.Blocks.Count >= 2);

        await CompletePlanTurnAsync(vm, "## Plan\n\n- Add the file\n- Link it from README");
        Assert.IsTrue(plan.IsPlanComplete);
        Assert.IsTrue(plan.IsProposedPlan);

        UserInputViewModel offer = vm.ActiveUserInput!;
        Assert.IsNotNull(offer);
        Assert.AreEqual("Implement this plan?", offer.Questions[0].Header);
        Assert.AreEqual("Yes, implement this plan", offer.Questions[0].Options[0].DisplayLabel);
        Assert.AreEqual("No, stay in Plan mode", offer.Questions[0].Options[1].DisplayLabel);

        offer.Questions[0].Options[0].IsSelected = true;
        await RunCommandAsync(offer.SubmitCommand);
        await WaitForAsync(() => bridge.LastStartTurnRequest?.Text == ChatViewModel.ImplementPlanPrompt);

        Assert.AreEqual("default", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
        Assert.IsFalse(vm.PlanModeEnabled);
        Assert.IsNull(vm.ActiveUserInput);
    }

    [TestMethod]
    public async Task ChatViewModel_PlanImplementationAndStructuredQuestionKeepCurrentTurnInterruptible()
    {
        var bridge = new FakeWorkerBridge();
        int startNumber = 0;
        bridge.StartTurnHandler = (_, _) => Task.FromResult(
            Interlocked.Increment(ref startNumber) == 1 ? "plan-turn" : "implementation-turn");
        using ChatViewModel vm = await CreatePlanThreadAsync(bridge);

        await CompletePlanTurnAsync(vm, "- Step", "plan-turn");
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        UserInputViewModel implementOffer = vm.ActiveUserInput!;
        implementOffer.Questions[0].Options[0].IsSelected = true;
        await RunCommandAsync(implementOffer.SubmitCommand);
        await WaitForAsync(() => bridge.LastStartTurnRequest?.Text == ChatViewModel.ImplementPlanPrompt);

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "implementation-turn",
        });
        Assert.IsTrue(vm.InterruptCommand.CanExecute, "The implementation turn is locally started and active.");

        var request = new UserInputRequest
        {
            RequestId = "implementation-question",
            ThreadId = "thread-1",
            TurnId = "implementation-turn",
            IsBlocking = true,
            Questions =
            [
                new UserInputQuestion
                {
                    Id = "continue",
                    Header = "Next step",
                    Question = "Continue?",
                    Options = [new UserInputOption { OptionId = "yes", Label = "Yes" }],
                },
            ],
        };
        MethodInfo requested = typeof(ChatViewModel).GetMethod(
            "OnUserInputRequestedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)requested.Invoke(vm, [Notification(request, vm.Status)])!;
        UserInputViewModel question = vm.ActiveUserInput!;
        question.Questions[0].Options[0].IsSelected = true;
        await RunCommandAsync(question.SubmitCommand);

        Assert.IsTrue(vm.InterruptCommand.CanExecute, "Resolving a structured question does not replace the active implementation turn.");
        Assert.AreEqual(2, bridge.StartTurnCallCount, "Resolving structured input must not start another turn.");

        await RunCommandAsync(vm.InterruptCommand);
        Assert.IsNotNull(bridge.LastInterruptTurnRequest, "The Interrupt command should dispatch a request.");
        InterruptTurnRequest interrupt = bridge.LastInterruptTurnRequest!;
        Assert.AreEqual("thread-1", interrupt.ThreadId);
        Assert.AreEqual("implementation-turn", interrupt.TurnId);
        Assert.AreEqual(vm.Status.Target?.StatePartitionFingerprint, interrupt.StatePartitionFingerprint);
        Assert.AreEqual(vm.Status.Target?.OwnerGeneration ?? 0, interrupt.OwnerGeneration);
        Assert.AreEqual(vm.Status.Target?.Generation ?? 0, interrupt.ConnectionGeneration);
    }

    [TestMethod]
    public async Task ChatViewModel_SyntheticQuestionAnswerStartsAnInterruptibleFollowUpTurn()
    {
        var bridge = new FakeWorkerBridge();
        int turnNumber = 0;
        bridge.StartTurnHandler = (_, _) => Task.FromResult($"synthetic-turn-{Interlocked.Increment(ref turnNumber)}");
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-synthetic" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-synthetic" });
        await RunCommandAsync(vm.JoinThreadCommand);
        await SendMessageAsync(vm, "start implementation", clearComposer: false);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-synthetic",
            TurnId = "synthetic-turn-1",
        });

        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.AgentMessageDelta,
            ThreadId = "thread-synthetic",
            TurnId = "synthetic-turn-1",
            ItemId = "agent-question",
            Text = "Do you want me to continue with the implementation?\n1. Yes\n2. No",
        });
        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "thread-synthetic",
            TurnId = "synthetic-turn-1",
        });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-synthetic" });
        Assert.IsNotNull(vm.ActiveUserInput);
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "The prose question is shown after the first turn has completed.");

        UserInputViewModel question = vm.ActiveUserInput!;
        question.Questions[0].Options[0].IsSelected = true;
        await RunCommandAsync(question.SubmitCommand);
        Assert.AreEqual(2, bridge.StartTurnCallCount);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-synthetic",
            TurnId = "synthetic-turn-2",
        });

        Assert.IsTrue(vm.InterruptCommand.CanExecute, "The answer starts a new locally acknowledged turn that can be interrupted.");
    }

    [TestMethod]
    public async Task ChatViewModel_DirectPlanPromptStartsAnInterruptibleLocalTurn()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = "thread-direct-plan" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-direct-plan" });
        await RunCommandAsync(vm.JoinThreadCommand);
        bridge.StartTurnHandler = (_, _) => Task.FromResult("direct-plan-turn");

        Assert.IsTrue(await InvokeExecutePlanAsync(vm, "thread-direct-plan", "design the change"));
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-direct-plan",
            TurnId = "direct-plan-turn",
        });

        Assert.IsTrue(vm.InterruptCommand.CanExecute, "A direct /plan prompt starts a local turn and should be interruptible.");
    }

    [TestMethod]
    public async Task ChatViewModel_DirectPlanPromptWithLostStartResponseAdoptsTheStartedTurn()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = "thread-direct-plan-lost" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-direct-plan-lost" });
        await RunCommandAsync(vm.JoinThreadCommand);
        bridge.StartTurnHandler = (_, _) => throw new InvalidOperationException("The start response was lost after dispatch.");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => InvokeExecutePlanAsync(vm, "thread-direct-plan-lost", "design the change"));
        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnStarted,
            ThreadId = "thread-direct-plan-lost",
            TurnId = "direct-plan-lost-turn",
        });
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-direct-plan-lost",
            TurnId = "direct-plan-lost-turn",
        });

        Assert.IsTrue(vm.InterruptCommand.CanExecute, "A direct /plan start with a lost response is identified by turn/started.");
    }

    [TestMethod]
    public async Task ChatViewModel_ImplementationOfferWaitsForTurnCompletion()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreatePlanThreadAsync(bridge);

        await RaiseConversationEventAsync(vm, CompletedPlanItem("- Step"));

        Assert.IsNull(vm.ActiveUserInput);
    }

    [TestMethod]
    public async Task ChatViewModel_StayInPlanModeDismissesOfferWithoutStartingATurn()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreatePlanThreadAsync(bridge);
        await CompletePlanTurnAsync(vm, "- Step");
        UserInputViewModel offer = vm.ActiveUserInput!;
        int startedTurns = bridge.StartTurnCallCount;

        offer.Questions[0].Options[1].IsSelected = true;
        await RunCommandAsync(offer.SubmitCommand);

        Assert.AreEqual(startedTurns, bridge.StartTurnCallCount);
        Assert.IsTrue(vm.PlanModeEnabled);
        Assert.IsNull(vm.ActiveUserInput);
    }

    [TestMethod]
    public async Task ChatViewModel_ImplementationOfferExpiresWhenTheConversationChanges()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreatePlanThreadAsync(bridge);
        await CompletePlanTurnAsync(vm, "- Step");
        Assert.IsNotNull(vm.ActiveUserInput);

        vm.SelectedThread = new ThreadSummary { Id = "thread-2" };

        Assert.IsNull(vm.ActiveUserInput);
    }

    [TestMethod]
    public async Task ChatViewModel_DefaultModeTurnDoesNotOfferImplementation()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreatePlanThreadAsync(bridge, planTurn: false);

        await CompletePlanTurnAsync(vm, "- Step");

        Assert.IsNull(vm.ActiveUserInput);
    }

    [TestMethod]
    public async Task UserInputViewModel_AnswersQuestionsOneAtATimeAndSubmitsAllTogether()
    {
        IReadOnlyDictionary<string, UserInputAnswer>? submitted = null;
        var request = new UserInputRequest
        {
            RequestId = "request-1",
            IsBlocking = true,
            Questions =
            [
                new UserInputQuestion
                {
                    Id = "spec",
                    Header = "echo spec",
                    Question = "Which format?",
                    IsOther = true,
                    Options = [new UserInputOption { OptionId = "raw", Label = "Raw body" }, new UserInputOption { OptionId = "json", Label = "JSON only" }],
                },
                new UserInputQuestion { Id = "name", Header = "Name", Question = "Project name?" },
                new UserInputQuestion
                {
                    Id = "license",
                    Header = "License",
                    Question = "Which license?",
                    Options = [new UserInputOption { OptionId = "mit", Label = "MIT" }, new UserInputOption { OptionId = "other", Label = "Other" }],
                },
            ],
        };
        var input = new UserInputViewModel(
            request,
            (_, answers, _) =>
            {
                submitted = answers;
                return Task.CompletedTask;
            },
            new SafeMarkdownService());

        Assert.IsTrue(input.IsMultiStep);
        Assert.AreEqual("Question 1 of 3", input.StepText);
        Assert.AreEqual(1, input.CurrentQuestions.Count);
        Assert.AreSame(input.Questions[0], input.CurrentQuestions[0]);
        Assert.IsFalse(input.IsLastStep);
        Assert.IsNull(input.ValidationText, "Intermediate steps do not report unanswered later questions.");
        Assert.IsFalse(input.BackCommand.CanExecute);
        Assert.IsFalse(input.NextCommand.CanExecute, "A blocking request needs an answer before moving on.");

        input.Questions[0].IsOtherSelected = true;
        input.Questions[0].OtherText = "Echo as text";
        Assert.IsTrue(input.NextCommand.CanExecute);
        await RunCommandAsync(input.NextCommand);
        Assert.AreEqual("Question 2 of 3", input.StepText);
        Assert.AreSame(input.Questions[1], input.CurrentQuestions[0]);
        Assert.IsTrue(input.BackCommand.CanExecute);

        await RunCommandAsync(input.BackCommand);
        Assert.AreEqual("Question 1 of 3", input.StepText);
        Assert.AreEqual("Echo as text", input.Questions[0].OtherText, "Answers survive navigation.");
        await RunCommandAsync(input.NextCommand);

        input.Questions[1].FreeText = "echo-api";
        await RunCommandAsync(input.NextCommand);
        Assert.IsTrue(input.IsLastStep);
        Assert.IsFalse(input.ShowNext);
        Assert.IsFalse(input.SubmitCommand.CanExecute);

        input.Questions[2].Options[0].IsSelected = true;
        await RunCommandAsync(input.SubmitCommand);

        Assert.IsNotNull(submitted);
        Assert.AreEqual(3, submitted!.Count);
        Assert.AreEqual(UserInputAnswerKind.Other, submitted["spec"].Kind);
        Assert.AreEqual("echo-api", submitted["name"].Text);
        Assert.AreEqual("mit", submitted["license"].OptionIds.Single());
    }

    [TestMethod]
    public void UserInputViewModel_SingleQuestionShowsSubmitWithoutStepper()
    {
        var input = new UserInputViewModel(
            new UserInputRequest
            {
                RequestId = "request-1",
                IsBlocking = true,
                Questions = [new UserInputQuestion { Id = "q", Header = "Q", Question = "Answer?" }],
            },
            (_, _, _) => Task.CompletedTask,
            new SafeMarkdownService());

        Assert.IsFalse(input.IsMultiStep);
        Assert.IsTrue(input.IsLastStep);
        Assert.IsFalse(input.ShowNext);
        Assert.AreEqual("Answer each question before submitting.", input.ValidationText);
    }

    // Starts turn-1 in the requested mode, as the offer depends on the mode the turn started with.
    private static async Task<ChatViewModel> CreatePlanThreadAsync(FakeWorkerBridge bridge, bool planTurn = true)
    {
        bridge.ExperimentalApi = true;
        var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await RunCommandAsync(vm.JoinThreadCommand);
        vm.PlanModeEnabled = planTurn;
        await SendMessageAsync(vm, "plan the change", clearComposer: false);
        return vm;
    }

    private static ConversationEvent CompletedPlanItem(string text, string turnId = "turn-1") => new()
    {
        Kind = ConversationEventKind.ItemCompleted,
        ThreadId = "thread-1",
        TurnId = turnId,
        ItemId = $"{turnId}-plan",
        Text = text,
        Plan = new TurnPlanSnapshot { ThreadId = "thread-1", TurnId = turnId, IsComplete = true },
    };

    private static async Task CompletePlanTurnAsync(ChatViewModel vm, string text, string turnId = "turn-1")
    {
        await RaiseConversationEventAsync(vm, CompletedPlanItem(text, turnId));
        await RaiseConversationEventAsync(vm, new ConversationEvent { Kind = ConversationEventKind.TurnCompleted, ThreadId = "thread-1", TurnId = turnId });
    }

    [TestMethod]
    public async Task ChatViewModel_PlanWithPrompt_LeavesPlanModeEnabled()
    {
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: ExperimentalSettingsStore())
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

        Assert.AreEqual("plan", bridge.LastStartTurnRequest!.CollaborationMode!.Mode);
        Assert.IsTrue(vm.PlanModeEnabled);
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
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await RunCommandAsync(vm.JoinThreadCommand);
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
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        var thread = new ThreadSummary { Id = "thread-2" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-1",
            TurnId = "turn-1",
        });

        vm.ComposerText = "/review uncommitted";
        await InvokeComposerSendAsync(vm);
        Assert.AreEqual(0, bridge.ReviewCallCount);

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
        await WaitForAsync(() => bridge.ReviewCallCount == 1);

        Assert.AreEqual(1, bridge.ReviewCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_QueuedThreadMutationWaitsForExplicitJoinAfterHistorySelection()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        var original = new ThreadSummary { Id = "thread-queued" };
        var history = new ThreadSummary { Id = "thread-history-only" };
        vm.Threads.Add(original);
        vm.Threads.Add(history);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = original.Id });
        vm.SelectedThread = original;
        await RunCommandAsync(vm.JoinThreadCommand);

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = original.Id,
            TurnId = "turn-queued",
        });
        vm.ComposerText = "/review uncommitted";
        await InvokeComposerSendAsync(vm);
        Assert.AreEqual(0, bridge.ReviewCallCount);

        vm.SelectedThread = history;
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = original.Id });
        Assert.AreEqual(0, bridge.ReviewCallCount, "A queued thread mutation must not execute while history is selected read-only.");

        vm.SelectedThread = original;
        await RunCommandAsync(vm.JoinThreadCommand);
        await WaitForAsync(() => bridge.ReviewCallCount == 1);

        Assert.AreEqual(1, bridge.ReviewCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_ForkSelectsReadOnlyHistoryUntilExplicitJoin()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false)
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });

        vm.ComposerText = "/fork";
        await InvokeComposerSendAsync(vm);

        Assert.AreEqual("thread-fork", vm.SelectedThread!.Id);
        Assert.AreEqual("thread-1", bridge.LastResumedThreadId, "Fork selection must not implicitly resume the new thread.");
        Assert.IsTrue(vm.IsHistoryOnly);

        await RunCommandAsync(vm.JoinThreadCommand);

        Assert.AreEqual("thread-fork", bridge.LastResumedThreadId);
        Assert.IsFalse(vm.IsHistoryOnly);
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
        Assert.HasCount(9, vm.SlashCommands.Suggestions.Where(static item => item.IsSelectable && !item.IsSkill));
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

            // Any other failure after dispatch may occur after server acceptance. Keep the input
            // available and mark the optimistic transcript row as an unresolved delivery outcome.
            bridge.StartTurnException = new InvalidOperationException("failed");
            vm.ComposerText = "inspect this";
            await SendMessageAsync(vm, "inspect this", clearComposer: true);
            Assert.IsTrue(vm.HasPendingAttachments);
            Assert.AreEqual("inspect this", vm.ComposerText);
            ChatItemViewModel unknown = vm.Items.Last(item => item.Role == "You");
            Assert.IsTrue(unknown.IsDeliveryUnknown);
            StringAssert.Contains(unknown.DeliveryStatusText, "not retried");

            bridge.StartTurnException = new StreamJsonRpc.RemoteInvocationException(
                "The app-server returned an error while starting the turn.",
                WorkerErrorCodes.UpstreamOperationFailed,
                errorData: null);
            vm.ComposerText = "inspect this after an RPC error";
            int callsBeforeRpcError = bridge.StartTurnCallCount;
            await SendMessageAsync(vm, "inspect this after an RPC error", clearComposer: true);
            ChatItemViewModel rpcError = vm.Items.Last(item => item.Role == "You");
            Assert.IsTrue(rpcError.IsDeliveryUnknown, "A generic remote error response does not prove that no side effect occurred.");
            Assert.AreEqual(callsBeforeRpcError + 1, bridge.StartTurnCallCount, "The uncertain operation is not automatically replayed.");
            StringAssert.Contains(vm.RecoveryStatusText, "outcome is unknown");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    public async Task ChatViewModel_StartTurnPreDispatchRejectionAndCancellationAreNotMarkedUnknown()
    {
        var bridge = new FakeWorkerBridge
        {
            StartTurnException = new StreamJsonRpc.RemoteInvocationException(
                "The request belongs to an inactive connection.",
                WorkerErrorCodes.ConnectionOperationRejected,
                errorData: "StaleGeneration"),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        vm.SelectedThread = new ThreadSummary { Id = "thread-pre-dispatch" };

        await SendMessageAsync(vm, "rejected before dispatch", clearComposer: true);
        Assert.IsFalse(vm.Items.Any(static item => item.IsDeliveryUnknown));
        Assert.IsFalse(vm.Items.Any(static item => item.Role == "You"));

        bridge.StartTurnException = new OperationCanceledException("The operation was cancelled before dispatch.");
        await SendMessageAsync(vm, "cancelled before dispatch", clearComposer: true);
        Assert.IsFalse(vm.Items.Any(static item => item.IsDeliveryUnknown));
        Assert.IsFalse(vm.Items.Any(static item => item.Role == "You"));
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
            await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1" });
            await RunCommandAsync(vm.JoinThreadCommand);
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
        Assert.HasCount(3, builtIns);
        Assert.AreEqual("/reasoning", builtIns[0].CommandName);
        Assert.AreEqual("/review", builtIns[1].CommandName);
        Assert.AreEqual("/presets", builtIns[2].CommandName, "Substring matches rank after prefix matches.");
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
        Assert.HasCount(9, vm.SlashCommands.Suggestions.Where(static item => item.IsSelectable && !item.IsSkill));
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

    private static async Task SendMessageAsync(ChatViewModel viewModel, string text, bool clearComposer)
    {
        await EnsureExplicitJoinAsync(viewModel);
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "SendMessageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find SendMessageAsync.");

        await (Task)method.Invoke(viewModel, [text, clearComposer, null])!;
    }

    private static async Task InvokeComposerSendAsync(ChatViewModel viewModel)
    {
        await EnsureExplicitJoinAsync(viewModel);
        MethodInfo method = typeof(ChatViewModel).GetMethod(
            "SendAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find SendAsync.");

        await (Task)method.Invoke(viewModel, null)!;
    }

    private static void InvokePrivate(ChatViewModel viewModel, string methodName)
        => (typeof(ChatViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Could not find {methodName}.")).Invoke(viewModel, null);

    private static Task InvokeFinishRecoveryAsync(ChatViewModel viewModel, ConnectionRecoveryOutcome outcome)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod("FinishRecoveryAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find FinishRecoveryAsync.");
        return (Task)method.Invoke(viewModel, [new ConnectionRecoveryResult(outcome, 1, WorkerRecoveryFailureKind.None)])!;
    }

    private static async Task<bool> InvokeExecutePlanAsync(ChatViewModel viewModel, string? threadId, string prompt)
    {
        MethodInfo method = typeof(ChatViewModel).GetMethod("ExecutePlanAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find ExecutePlanAsync.");
        FieldInfo fence = typeof(ChatViewModel).GetField("goalStopFenceVersion", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find goalStopFenceVersion.");
        Task<bool> execution = (Task<bool>)method.Invoke(viewModel, [threadId, prompt, (long)fence.GetValue(viewModel)!])!;
        return await execution.ConfigureAwait(false);
    }

    private static async Task EnsureExplicitJoinAsync(ChatViewModel viewModel)
    {
        if (viewModel.SelectedThread is not null
            && !viewModel.IsThreadJoined
            && viewModel.Status.State == WorkerConnectionState.Ready
            && viewModel.JoinThreadCommand.CanExecute)
        {
            await RunCommandAsync(viewModel.JoinThreadCommand);
        }
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
        if (viewModel.SelectedThread is null)
        {
            // Most event-rendering tests exercise presentation after the user selected a history
            // entry. Avoid the setter here because it starts real asynchronous history reads; the
            // selection state itself is all the event projection requires.
            FieldInfo selectedThread = typeof(ChatViewModel).GetField("selectedThread", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find selectedThread.");
            selectedThread.SetValue(viewModel, new ThreadSummary { Id = value.ThreadId ?? "test-thread" });
        }

        if (string.IsNullOrEmpty(value.ThreadId))
        {
            value.ThreadId = viewModel.SelectedThread!.Id;
        }

        if (!string.IsNullOrWhiteSpace(value.ItemId) && string.IsNullOrWhiteSpace(value.TurnId))
        {
            value.TurnId = "test-turn";
        }

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
    public async Task ChatViewModel_RemoteCredentialFinalization_AcceptsReadyAndRejectsRetiredOwner()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(SavedRemoteProfile())));
        SetWorkingDirectory(vm, Path.GetTempPath());
        WorkerStatus RemoteStatus(WorkerConnectionState state, string partition, long ownerGeneration)
            => new()
            {
                State = state,
                Target = new ConnectionTargetSnapshot
                {
                    Kind = ConnectionTargetKind.Remote,
                    DisplayName = "Build box",
                    Generation = 1,
                    OwnerGeneration = ownerGeneration,
                    StatePartitionFingerprint = partition,
                },
            };
        WorkerStatus provisional = RemoteStatus(WorkerConnectionState.Connecting, "provisional", 1);
        WorkerStatus finalized = RemoteStatus(WorkerConnectionState.Connecting, "credential-bound", 2);
        WorkerStatus ready = RemoteStatus(WorkerConnectionState.Ready, "credential-bound", 2);
        bridge.ConnectHandler = async () =>
        {
            await bridge.PublishStateAsync(provisional);
            await bridge.PublishStateAsync(finalized);
            await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
            Assert.AreEqual(AccountState.SignedIn, vm.Account.State, "Account events must be accepted before Ready.");
            await bridge.PublishStateAsync(ready);
            return ready;
        };

        await RunCommandAsync(vm.ApplyRemoteProfileCommand);

        Assert.AreEqual(WorkerConnectionState.Ready, vm.Status.State);
        Assert.AreEqual(2L, vm.Status.Target?.OwnerGeneration);
        Assert.AreEqual(1L, vm.Status.Target?.Generation);
        Assert.AreEqual("Build box", vm.ConnectionTargetText);
        Assert.AreEqual("Connected with remote profile 'Build box'.", vm.RemoteProfiles.StatusText);
        Assert.IsTrue(vm.UseLocalAppServerCommand.CanExecute);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedOut }, provisional);
        await bridge.PublishStateAsync(provisional);
        Assert.AreEqual(WorkerConnectionState.Ready, vm.Status.State);
        Assert.AreEqual(AccountState.SignedIn, vm.Account.State);
        await bridge.PublishStateAsync(RemoteStatus(WorkerConnectionState.Degraded, "different-credential", 2));
        Assert.AreEqual(WorkerConnectionState.Ready, vm.Status.State, "A different partition at the same generation remains invalid.");
        Assert.AreEqual("credential-bound", vm.Status.Target?.StatePartitionFingerprint);
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
        var thread = new ThreadSummary { Id = "t" };
        vm.Threads.Add(thread);
        vm.SelectedThread = thread;
        // Remote UI refreshes a button only when its command raises CanExecute; the value alone
        // is never polled. Every status change must therefore raise the turn and account commands.
        var raised = 0;
        var accountRaised = 0;
        vm.InterruptCommand.PropertyChanged += (_, _) => raised++;
        vm.AccountCommand.PropertyChanged += (_, _) => accountRaised++;

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "t" });
        await RunCommandAsync(vm.JoinThreadCommand);
        Assert.IsFalse(vm.IsTurnActive);
        Assert.IsFalse(vm.InterruptCommand.CanExecute);

        // A turn observed from another client cannot be interrupted.
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Busy, ThreadId = "t", TurnId = "external-turn" });
        Assert.IsFalse(vm.InterruptCommand.CanExecute);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "t" });
        vm.ComposerText = "Start an owned turn";
        await RunCommandAsync(vm.SendCommand);
        Assert.AreEqual(1, bridge.StartTurnCallCount);
        // The acknowledged locally started turn remains interruptible while waiting for approval.
        foreach (WorkerConnectionState state in new[] { WorkerConnectionState.Busy, WorkerConnectionState.WaitingForApproval })
        {
            await bridge.PublishStateAsync(new WorkerStatus { State = state, ThreadId = "t", TurnId = "turn-1" });
            Assert.IsTrue(vm.IsTurnActive, state.ToString());
            Assert.IsTrue(vm.InterruptCommand.CanExecute, state.ToString());
        }

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "t" });
        Assert.IsFalse(vm.InterruptCommand.CanExecute);
        Assert.IsTrue(raised >= 4, $"InterruptCommand raised CanExecute {raised} time(s).");
        Assert.IsTrue(accountRaised >= 4, $"AccountCommand raised CanExecute {accountRaised} time(s).");
    }

    [TestMethod]
    public async Task ChatViewModel_InterruptOwnershipSurvivesTransientStatusAndRetiresOnExactCompletion()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-interrupt" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-interrupt" });
        await RunCommandAsync(vm.JoinThreadCommand);
        bridge.StartTurnHandler = (_, _) => Task.FromResult("locally-started-turn");

        await SendMessageAsync(vm, "start implementation", clearComposer: false);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-interrupt",
            TurnId = "locally-started-turn",
        });
        Assert.IsTrue(vm.InterruptCommand.CanExecute);

        // A delayed prior-turn status must not erase ownership of the acknowledged active turn.
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-interrupt" });
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "An idle status has no interruptible current turn.");
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-interrupt",
            TurnId = "locally-started-turn",
        });
        Assert.IsTrue(vm.InterruptCommand.CanExecute, "The local-start marker should survive a transient null TurnId.");

        // A stale status for another turn also cannot erase the acknowledged marker.
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-interrupt",
            TurnId = "stale-turn",
        });
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "A different current turn must remain ineligible.");
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-interrupt",
            TurnId = "locally-started-turn",
        });
        Assert.IsTrue(vm.InterruptCommand.CanExecute, "A later current status for the acknowledged turn should recover eligibility.");

        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "thread-interrupt",
            TurnId = "locally-started-turn",
        });
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "Completion of the exact local turn retires its interrupt ownership.");
    }

    [TestMethod]
    [DataRow(WorkerConnectionState.Disconnected)]
    [DataRow(WorkerConnectionState.Degraded)]
    public async Task ChatViewModel_InterruptOwnership_IsClearedAtConnectionBoundary(WorkerConnectionState boundaryState)
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        WorkerStatus ready = OwnerStatus("interrupt-boundary-owner", 3, 7);
        ready.ThreadId = "thread-interrupt-boundary";
        await bridge.PublishStateAsync(ready);
        vm.SelectedThread = new ThreadSummary { Id = "thread-interrupt-boundary" };
        await RunCommandAsync(vm.JoinThreadCommand);
        bridge.StartTurnHandler = (_, _) => Task.FromResult("boundary-local-turn");

        await SendMessageAsync(vm, "start turn before connection boundary", clearComposer: false);
        WorkerStatus busy = OwnerStatus("interrupt-boundary-owner", 3, 7);
        busy.State = WorkerConnectionState.Busy;
        busy.ThreadId = "thread-interrupt-boundary";
        busy.TurnId = "boundary-local-turn";
        await bridge.PublishStateAsync(busy);
        Assert.IsTrue(vm.InterruptCommand.CanExecute);

        WorkerStatus boundary = OwnerStatus("interrupt-boundary-owner", 3, 7);
        boundary.State = boundaryState;
        boundary.ThreadId = "thread-interrupt-boundary";
        await bridge.PublishStateAsync(boundary);
        Assert.IsFalse(vm.InterruptCommand.CanExecute, $"{boundaryState} must clear interrupt ownership.");

        await bridge.PublishStateAsync(busy);
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "A stale Busy status must not restore ownership after a connection boundary.");
    }

    [TestMethod]
    public async Task ChatViewModel_InterruptOwnership_CompletionBeforeStartAcknowledgementStaysRetired()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-completion-race" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-completion-race" });
        await RunCommandAsync(vm.JoinThreadCommand);
        bridge.StartTurnHandler = async (_, _) =>
        {
            await bridge.PublishConversationEventAsync(new ConversationEvent
            {
                Kind = ConversationEventKind.TurnCompleted,
                ThreadId = "thread-completion-race",
                TurnId = "completed-before-ack",
            });
            return "completed-before-ack";
        };

        await SendMessageAsync(vm, "start and complete quickly", clearComposer: false);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-completion-race",
            TurnId = "completed-before-ack",
        });

        Assert.IsFalse(vm.InterruptCommand.CanExecute, "A late StartTurn acknowledgement must not resurrect a turn already completed.");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "turn/started after the lost response")]
    [DataRow(true, DisplayName = "turn/started before the lost response")]
    public async Task ChatViewModel_InterruptOwnership_UnknownStartOutcomeAdoptsTheStartedTurn(bool startedBeforeFailure)
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-unknown-start" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-unknown-start" });
        await RunCommandAsync(vm.JoinThreadCommand);
        var turnStarted = new ConversationEvent
        {
            Kind = ConversationEventKind.TurnStarted,
            ThreadId = "thread-unknown-start",
            TurnId = "turn-with-lost-response",
        };
        bridge.StartTurnHandler = async (_, _) =>
        {
            if (startedBeforeFailure)
            {
                await bridge.PublishConversationEventAsync(turnStarted);
            }

            throw new InvalidOperationException("The start response was lost after dispatch.");
        };

        await SendMessageAsync(vm, "start with a lost response", clearComposer: false);
        if (!startedBeforeFailure)
        {
            await bridge.PublishConversationEventAsync(turnStarted);
        }

        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-unknown-start",
            TurnId = "turn-with-lost-response",
        });
        Assert.IsTrue(vm.InterruptCommand.CanExecute, "The turn identified by turn/started belongs to the lost local start.");

        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "thread-unknown-start",
            TurnId = "turn-with-lost-response",
        });
        Assert.IsFalse(vm.InterruptCommand.CanExecute);
    }

    [TestMethod]
    public async Task ChatViewModel_InterruptOwnership_NewerLocalStartSupersedesAnUnknownStartOutcome()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-superseded-start" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-superseded-start" });
        await RunCommandAsync(vm.JoinThreadCommand);
        bridge.StartTurnHandler = (_, _) => throw new InvalidOperationException("The start response was lost after dispatch.");
        await SendMessageAsync(vm, "start with a lost response", clearComposer: false);

        bridge.StartTurnHandler = (_, _) => Task.FromResult("acknowledged-turn");
        await SendMessageAsync(vm, "start again", clearComposer: false);
        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = "thread-superseded-start",
            TurnId = "acknowledged-turn",
        });

        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnStarted,
            ThreadId = "thread-superseded-start",
            TurnId = "external-turn",
        });
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = "thread-superseded-start",
            TurnId = "external-turn",
        });
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "A later turn must not be attributed to a superseded lost start.");
    }

    [TestMethod]
    public void BoundedOrderedMap_EvictsOldestLiveEntryAfterOutOfOrderRemoval()
    {
        var map = new BoundedOrderedMap<string, int>(2, StringComparer.Ordinal);
        Assert.IsTrue(map.TryAdd("a", 1));
        Assert.IsTrue(map.TryAdd("b", 2));
        Assert.IsFalse(map.TryAdd("a", 3), "An existing key is left unchanged.");
        Assert.IsTrue(map.Remove("a", out int removed));
        Assert.AreEqual(1, removed);

        Assert.IsTrue(map.TryAdd("c", 3));
        Assert.IsTrue(map.TryAdd("d", 4));

        Assert.AreEqual(2, map.Count);
        Assert.IsFalse(map.ContainsKey("b"), "The oldest live entry is evicted first.");
        Assert.AreEqual("c,d", string.Join(",", map.Entries.Select(static entry => entry.Key)));
    }

    [TestMethod]
    public async Task ChatViewModel_InterruptOwnership_DoesNotCrossThreadOrOwnerChanges()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        WorkerStatus ownerAReady = OwnerStatus("interrupt-owner-a", 1, 1);
        ownerAReady.ThreadId = "thread-owner-a";
        await bridge.PublishStateAsync(ownerAReady);
        vm.SelectedThread = new ThreadSummary { Id = "thread-owner-a" };
        await RunCommandAsync(vm.JoinThreadCommand);
        bridge.StartTurnHandler = (_, _) => Task.FromResult("owner-a-turn");

        await SendMessageAsync(vm, "start owned turn", clearComposer: false);
        WorkerStatus ownerABusy = OwnerStatus("interrupt-owner-a", 1, 1);
        ownerABusy.State = WorkerConnectionState.Busy;
        ownerABusy.ThreadId = "thread-owner-a";
        ownerABusy.TurnId = "owner-a-turn";
        await bridge.PublishStateAsync(ownerABusy);
        Assert.IsTrue(vm.InterruptCommand.CanExecute);

        vm.SelectedThread = new ThreadSummary { Id = "thread-owner-b" };
        WorkerStatus wrongThread = OwnerStatus("interrupt-owner-a", 1, 1);
        wrongThread.State = WorkerConnectionState.Busy;
        wrongThread.ThreadId = "thread-owner-b";
        wrongThread.TurnId = "owner-a-turn";
        await bridge.PublishStateAsync(wrongThread);
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "A selected/status thread switch must not expose the old turn's interrupt.");

        await bridge.PublishStateAsync(OwnerStatus("interrupt-owner-b", 2, 2));
        WorkerStatus staleOwnerTurn = OwnerStatus("interrupt-owner-b", 2, 2);
        staleOwnerTurn.State = WorkerConnectionState.Busy;
        staleOwnerTurn.ThreadId = "thread-owner-b";
        staleOwnerTurn.TurnId = "owner-a-turn";
        await bridge.PublishStateAsync(staleOwnerTurn);
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "An acknowledged turn from a previous owner cannot become interruptible in a new owner.");
    }

    [TestMethod]
    public async Task ChatViewModel_ActiveGoalUsesPrimaryStopAndWaitsForExactTurnCompletion()
    {
        var stopResult = new TaskCompletionSource<StopThreadGoalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            CurrentThreadGoal = ActiveGoal("goal-thread"),
#pragma warning disable VSTHRD003 // The test owns this completion source and releases it after publishing completion.
            StopThreadGoalHandler = (_, _) => stopResult.Task,
#pragma warning restore VSTHRD003
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = "goal-thread" };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = thread.Id });
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = thread.Id,
            TurnId = "external-turn",
        });
        vm.ComposerText = "keep this draft while stopping";

        Assert.AreEqual("Stop", vm.SendButtonText);
        Assert.IsTrue(vm.SendCommand.CanExecute);
        Assert.IsFalse(vm.InterruptCommand.CanExecute, "Goal stop replaces the duplicate Interrupt control, including external turns.");
        Task stopTask = RunCommandAsync(vm.SendCommand);

        Assert.AreEqual(1, bridge.StopThreadGoalRequests.Count);
        Assert.IsTrue(vm.IsGoalStopPending);
        Assert.AreEqual("Stopping", vm.SendButtonText);
        Assert.IsFalse(vm.SendCommand.CanExecute);
        Assert.AreEqual("keep this draft while stopping", vm.ComposerText);

        await bridge.PublishThreadGoalChangedAsync(new ThreadGoalEvent
        {
            ThreadId = thread.Id,
            TurnId = "external-turn",
            Goal = new ThreadGoalInfo { ThreadId = thread.Id, Objective = "ship safely", Status = ThreadGoalStatus.Paused },
        });
        Assert.IsTrue(vm.IsGoalStopPending, "A paused-goal notification does not imply the active turn has finished.");

        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = thread.Id,
            TurnId = "external-turn",
        });
        Assert.IsTrue(vm.IsGoalStopPending, "Completion is remembered, but Stop remains pending until its RPC result arrives.");
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = thread.Id });

        StopThreadGoalRequest request = bridge.StopThreadGoalRequests.Single();
        stopResult.SetResult(new StopThreadGoalResult
        {
            StatePartitionFingerprint = request.StatePartitionFingerprint,
            OwnerGeneration = request.OwnerGeneration,
            ConnectionGeneration = request.ConnectionGeneration,
            Goal = new ThreadGoalInfo { ThreadId = thread.Id, Objective = "ship safely", Status = ThreadGoalStatus.Paused },
            PauseOutcome = GoalStopStepOutcome.Succeeded,
            InterruptOutcome = GoalStopStepOutcome.Succeeded,
            TurnId = "external-turn",
        });
        await stopTask;

        Assert.IsFalse(vm.IsGoalStopPending);
        Assert.AreEqual("Send", vm.SendButtonText);
        Assert.AreEqual("keep this draft while stopping", vm.ComposerText);
        Assert.AreEqual(0, bridge.StartTurnCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_ActiveGoalCanBeStoppedBetweenTurns()
    {
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal("between-turns") };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = "between-turns" };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = thread.Id });
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);

        Assert.IsNull(vm.Status.TurnId);
        Assert.AreEqual("Stop", vm.SendButtonText);
        Assert.IsTrue(vm.SendCommand.CanExecute, "An active goal remains stoppable during the gap between turns.");
        await RunCommandAsync(vm.SendCommand);

        Assert.AreEqual(1, bridge.StopThreadGoalRequests.Count);
        Assert.AreEqual(0, bridge.StartTurnCallCount);
        Assert.IsFalse(vm.IsGoalStopPending);
        Assert.AreEqual("Send", vm.SendButtonText);
    }

    [TestMethod]
    public async Task ChatViewModel_StopGoalUsesWorkerTurnIdWhenStatusHasNotObservedItYet()
    {
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal("new-turn-id") };
        var resultGate = new TaskCompletionSource<StopThreadGoalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The test owns this completion source and releases it after verifying the target turn.
        bridge.StopThreadGoalHandler = (_, _) => resultGate.Task;
#pragma warning restore VSTHRD003
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = "new-turn-id" };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = thread.Id });
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);

        Task stopTask = RunCommandAsync(vm.SendCommand);
        StopThreadGoalRequest request = bridge.StopThreadGoalRequests.Single();
        resultGate.SetResult(new StopThreadGoalResult
        {
            StatePartitionFingerprint = request.StatePartitionFingerprint,
            OwnerGeneration = request.OwnerGeneration,
            ConnectionGeneration = request.ConnectionGeneration,
            Goal = new ThreadGoalInfo { ThreadId = thread.Id, Objective = "goal", Status = ThreadGoalStatus.Paused },
            PauseOutcome = GoalStopStepOutcome.Succeeded,
            InterruptOutcome = GoalStopStepOutcome.Succeeded,
            TurnId = "turn-arrived-before-status",
        });
        await stopTask;

        Assert.IsTrue(vm.IsGoalStopPending, "A null/old UI turn snapshot cannot override the Worker’s target turn id.");
        Assert.AreEqual("Stopping", vm.SendButtonText);
        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = thread.Id,
            TurnId = "turn-arrived-before-status",
        });
        Assert.IsFalse(vm.IsGoalStopPending);
    }

    [TestMethod]
    public async Task ChatViewModel_StaleGoalReadCannotOverwriteNewerGoalNotification()
    {
        var read = new TaskCompletionSource<ThreadGoalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The test owns this read and releases it after publishing a newer goal notification.
        var bridge = new FakeWorkerBridge { GetThreadGoalHandler = (_, _) => read.Task };
#pragma warning restore VSTHRD003
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = "goal-revision" };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = thread.Id });
        vm.SelectedThread = thread;
        await bridge.PublishThreadGoalChangedAsync(new ThreadGoalEvent
        {
            ThreadId = thread.Id,
            Goal = new ThreadGoalInfo { ThreadId = thread.Id, Objective = "newer state", Status = ThreadGoalStatus.Paused },
        });
        read.SetResult(new ThreadGoalResult
        {
            Goal = new ThreadGoalInfo { ThreadId = thread.Id, Objective = "stale state", Status = ThreadGoalStatus.Active },
        });
        await Task.Delay(20);

        Assert.IsFalse(vm.IsGoalStopMode);
        Assert.AreEqual("newer state", vm.SelectedThreadGoal?.Objective);
    }

    [TestMethod]
    public async Task ChatViewModel_UnknownGoalStopReconcilesBeforeAllowingExplicitRetry()
    {
        var bridge = new FakeWorkerBridge
        {
            CurrentThreadGoal = ActiveGoal("unknown-stop"),
            StopThreadGoalHandler = (_, _) => Task.FromResult(new StopThreadGoalResult
            {
                IsSupported = true,
                PauseOutcome = GoalStopStepOutcome.OutcomeUnknown,
                InterruptOutcome = GoalStopStepOutcome.OutcomeUnknown,
                Message = "result uncertain",
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = "unknown-stop" };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = thread.Id });
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);

        await RunCommandAsync(vm.SendCommand);

        Assert.AreEqual(1, bridge.StopThreadGoalRequests.Count, "An unknown outcome is reconciled with a read and never auto-retried.");
        Assert.AreEqual("Retry Stop", vm.SendButtonText);
        Assert.IsTrue(vm.SendCommand.CanExecute);
    }

    [TestMethod]
    public async Task ChatViewModel_UnknownPausedGoalWithActiveTurnOffersExplicitRetry()
    {
        const string threadId = "unknown-paused-running";
        var bridge = new FakeWorkerBridge
        {
            CurrentThreadGoal = ActiveGoal(threadId),
        };
        bridge.StopThreadGoalHandler = (_, _) =>
        {
            bridge.CurrentThreadGoal = new ThreadGoalInfo
            {
                ThreadId = threadId,
                Objective = "finish the task",
                Status = ThreadGoalStatus.Paused,
            };
            return Task.FromResult(new StopThreadGoalResult
            {
                IsSupported = true,
                PauseOutcome = GoalStopStepOutcome.Succeeded,
                InterruptOutcome = GoalStopStepOutcome.OutcomeUnknown,
                TurnId = "still-running",
            });
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = threadId };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = threadId,
            TurnId = "still-running",
        });
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);

        await RunCommandAsync(vm.SendCommand);

        Assert.AreEqual("Paused", vm.SelectedThreadGoal?.Status.ToString());
        Assert.AreEqual("Retry Stop", vm.SendButtonText);
        Assert.IsTrue(vm.SendCommand.CanExecute);
        Assert.AreEqual(1, bridge.StopThreadGoalRequests.Count, "The unknown interrupt is never retried automatically.");
    }

    [TestMethod]
    public async Task ChatViewModel_UnknownStopWithClearedGoalAndActiveTurnOffersExplicitRetry()
    {
        const string threadId = "unknown-cleared-running";
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal(threadId) };
        bridge.StopThreadGoalHandler = (_, _) =>
        {
            bridge.CurrentThreadGoal = null;
            return Task.FromResult(new StopThreadGoalResult
            {
                IsSupported = true,
                PauseOutcome = GoalStopStepOutcome.OutcomeUnknown,
                InterruptOutcome = GoalStopStepOutcome.OutcomeUnknown,
                TurnId = "still-running",
            });
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = threadId };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Busy,
            ThreadId = threadId,
            TurnId = "still-running",
        });
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);

        await RunCommandAsync(vm.SendCommand);

        Assert.IsNull(vm.SelectedThreadGoal);
        Assert.AreEqual("Retry Stop", vm.SendButtonText);
        Assert.IsTrue(vm.SendCommand.CanExecute, "A missing goal must not hide the explicit Stop retry while its thread still runs.");
    }

    [TestMethod]
    public void ChatToolWindowXaml_PrimaryGoalStopHasAccessibleNameAndStatusRegion()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement? primary = doc.Descendants(presentation + "Button")
            .SingleOrDefault(button => button.Attribute("Command")?.Value == "{Binding SendCommand}");

        Assert.IsNotNull(primary);
        Assert.AreEqual("{Binding SendButtonText}", primary!.Attribute("AutomationProperties.Name")?.Value);
        Assert.AreEqual("{Binding SendButtonGlyph}", primary.Attribute("Content")?.Value);
        Assert.AreEqual("{Binding PrimaryActionHelpText}", primary.Attribute("AutomationProperties.HelpText")?.Value);
        Assert.IsTrue(doc.Descendants(presentation + "TextBlock")
            .Any(text => text.Attribute("Text")?.Value == "{Binding GoalStopStatusText}"
                && text.Attribute("AutomationProperties.LiveSetting")?.Value == "Assertive"));
    }

    [TestMethod]
    public async Task ChatViewModel_CtrlEnterNeverTriggersGoalStop()
    {
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal("keyboard-goal") };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = "keyboard-goal" };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = thread.Id });
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);
        vm.ComposerText = "a draft typed while the goal runs";

        Assert.AreEqual("Stop", vm.SendButtonText);
        Assert.IsTrue(vm.SendCommand.CanExecute);
        Assert.IsFalse(vm.SendKeyCommand.CanExecute, "The Send shortcut must not become a Stop shortcut.");
        await RunCommandAsync(vm.SendKeyCommand);

        Assert.AreEqual(0, bridge.StopThreadGoalRequests.Count);
        Assert.AreEqual(0, bridge.StartTurnCallCount);
        Assert.AreEqual("a draft typed while the goal runs", vm.ComposerText);

        // Remote UI does not poll command state: each Goal-mode transition must raise the
        // shortcut's CanExecute notification.
        int notifications = 0;
        vm.SendKeyCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AsyncCommand.CanExecute))
            {
                notifications++;
            }
        };
        await bridge.PublishThreadGoalChangedAsync(new ThreadGoalEvent
        {
            ThreadId = thread.Id,
            Goal = new ThreadGoalInfo { ThreadId = thread.Id, Objective = "finish the task", Status = ThreadGoalStatus.Paused },
        });
        Assert.IsTrue(vm.SendKeyCommand.CanExecute);
        Assert.IsTrue(notifications > 0, "Leaving Goal mode must notify the shortcut's CanExecute.");

        notifications = 0;
        await bridge.PublishThreadGoalChangedAsync(new ThreadGoalEvent { ThreadId = thread.Id, Goal = ActiveGoal(thread.Id) });
        Assert.IsFalse(vm.SendKeyCommand.CanExecute);
        Assert.IsTrue(notifications > 0, "Entering Goal mode must notify the shortcut's CanExecute.");
    }

    [TestMethod]
    public async Task ChatViewModel_PrimaryActionVisibilityFollowsSlashCommandSelection()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SlashCommands.ShowSuggestions(
        [
            new SlashCommandSuggestionDescriptor("/status", "Show status"),
        ]);

        vm.SlashCommands.AcceptSuggestionCommand.Execute(null);
        await WaitForAsync(() => vm.SlashCommands.HasActiveCommand);

        Assert.IsFalse(vm.IsPrimaryActionVisible);
        Assert.IsTrue(vm.ShowSlashExecuteButton);
        CollectionAssert.Contains(changed, nameof(ChatViewModel.IsPrimaryActionVisible));
        CollectionAssert.Contains(changed, nameof(ChatViewModel.ShowSlashExecuteButton));
    }

    [TestMethod]
    public async Task ChatViewModel_FailedStopReadCanBeCheckedAgainUntilResolved()
    {
        const string threadId = "unknown-read-fails";
        var bridge = new FakeWorkerBridge
        {
            CurrentThreadGoal = ActiveGoal(threadId),
            StopThreadGoalHandler = (_, _) => throw new TimeoutException("worker timed out"),
        };
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId);
        bridge.GetThreadGoalHandler = (_, _) => throw new InvalidOperationException("read failed");

        await RunCommandAsync(vm.SendCommand);

        Assert.AreEqual("Check Stop Status", vm.SendButtonText);
        Assert.IsTrue(vm.SendCommand.CanExecute, "A failed verification read can be retried explicitly.");
        Assert.IsFalse(vm.IsGoalStopPending);

        bridge.GetThreadGoalHandler = null;
        bridge.CurrentThreadGoal = new ThreadGoalInfo { ThreadId = threadId, Objective = "finish the task", Status = ThreadGoalStatus.Paused };
        await RunCommandAsync(vm.SendCommand);

        Assert.AreEqual(1, bridge.StopThreadGoalRequests.Count, "Checking the status never sends another stop.");
        Assert.AreEqual("Send", vm.SendButtonText);
        Assert.AreEqual("Goal stopped.", vm.GoalStopStatusText);
    }

    [TestMethod]
    [DataRow(ThreadGoalStatus.Complete)]
    [DataRow(ThreadGoalStatus.Blocked)]
    [DataRow(ThreadGoalStatus.BudgetLimited)]
    [DataRow(null)]
    public async Task ChatViewModel_UnknownStopWithEndedGoalRestoresSend(ThreadGoalStatus? endedStatus)
    {
        const string threadId = "unknown-ended";
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal(threadId) };
        bridge.StopThreadGoalHandler = (_, _) =>
        {
            bridge.CurrentThreadGoal = endedStatus is { } status
                ? new ThreadGoalInfo { ThreadId = threadId, Objective = "finish the task", Status = status }
                : null;
            return Task.FromException<StopThreadGoalResult>(new TimeoutException("worker timed out"));
        };
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId);

        await RunCommandAsync(vm.SendCommand);

        Assert.IsFalse(vm.IsGoalStopPending);
        Assert.AreEqual("Send", vm.SendButtonText);
        vm.ComposerText = "next message";
        Assert.IsTrue(vm.SendCommand.CanExecute);
    }

    [TestMethod]
    public async Task ChatViewModel_StopCompletionNotifiesEveryComposerProperty()
    {
        const string threadId = "completion-notify";
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal(threadId) };
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId, turnId: "goal-turn");
        await RunCommandAsync(vm.SendCommand);
        Assert.IsTrue(vm.IsGoalStopPending);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await bridge.PublishConversationEventAsync(new ConversationEvent
        {
            Kind = ConversationEventKind.TurnCompleted,
            ThreadId = threadId,
            TurnId = "goal-turn",
        });

        Assert.IsFalse(vm.IsGoalStopPending);
        foreach (string name in new[]
        {
            nameof(ChatViewModel.IsGoalStopPending),
            nameof(ChatViewModel.GoalStopStatusText),
            nameof(ChatViewModel.SendButtonText),
            nameof(ChatViewModel.SendButtonGlyph),
            nameof(ChatViewModel.PrimaryActionHelpText),
            nameof(ChatViewModel.IsPrimaryActionVisible),
        })
        {
            CollectionAssert.Contains(changed, name);
        }
    }

    [TestMethod]
    public async Task ChatViewModel_UnsupportedStopRestoresOrdinaryComposer()
    {
        const string threadId = "unsupported-stop";
        var bridge = new FakeWorkerBridge
        {
            CurrentThreadGoal = ActiveGoal(threadId),
            StopThreadGoalHandler = (_, _) => Task.FromResult(new StopThreadGoalResult
            {
                IsSupported = false,
                UnavailableReason = "Thread goals are not supported by this app-server.",
            }),
        };
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId);

        await RunCommandAsync(vm.SendCommand);

        Assert.IsFalse(vm.IsGoalStopMode);
        Assert.AreEqual("Send", vm.SendButtonText);
        Assert.IsTrue(vm.HasGoalStopStatus);
        vm.ComposerText = "continue without goals";
        Assert.IsTrue(vm.SendCommand.CanExecute);
    }

    [TestMethod]
    public async Task ChatViewModel_RetryClearsWhenTheTurnEndsAndTheGoalIsPaused()
    {
        const string threadId = "retry-clears";
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal(threadId) };
        bridge.StopThreadGoalHandler = (request, _) => Task.FromResult(new StopThreadGoalResult
        {
            StatePartitionFingerprint = request.StatePartitionFingerprint,
            OwnerGeneration = request.OwnerGeneration,
            ConnectionGeneration = request.ConnectionGeneration,
            Goal = new ThreadGoalInfo { ThreadId = threadId, Objective = "finish the task", Status = ThreadGoalStatus.Paused },
            PauseOutcome = GoalStopStepOutcome.Succeeded,
            InterruptOutcome = GoalStopStepOutcome.Failed,
            TurnId = "goal-turn",
            Message = "The app-server rejected the current turn interrupt.",
        });
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId, turnId: "goal-turn");

        await RunCommandAsync(vm.SendCommand);
        Assert.AreEqual("Retry Stop", vm.SendButtonText);
        Assert.AreEqual(ThreadGoalStatus.Paused, vm.SelectedThreadGoal?.Status, "A successful pause is retained when the interrupt fails.");

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = threadId });

        Assert.AreEqual("Send", vm.SendButtonText);
        Assert.AreEqual(1, bridge.StopThreadGoalRequests.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_StaleStopResultCannotApplyAfterReturningToTheThread()
    {
        const string threadId = "stale-return";
        var stopResult = new TaskCompletionSource<StopThreadGoalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The test owns this completion source and releases it after switching threads.
        var bridge = new FakeWorkerBridge
        {
            CurrentThreadGoal = ActiveGoal(threadId),
            StopThreadGoalHandler = (_, _) => stopResult.Task,
        };
#pragma warning restore VSTHRD003
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId);
        var other = new ThreadSummary { Id = "other-thread" };
        vm.Threads.Add(other);
        ThreadSummary original = vm.SelectedThread!;

        Task stopTask = RunCommandAsync(vm.SendCommand);
        vm.SelectedThread = other;
        vm.SelectedThread = original;
        await RunCommandAsync(vm.JoinThreadCommand);
        StopThreadGoalRequest request = bridge.StopThreadGoalRequests.Single();
        stopResult.SetResult(new StopThreadGoalResult
        {
            StatePartitionFingerprint = request.StatePartitionFingerprint,
            OwnerGeneration = request.OwnerGeneration,
            ConnectionGeneration = request.ConnectionGeneration,
            PauseOutcome = GoalStopStepOutcome.Failed,
            InterruptOutcome = GoalStopStepOutcome.NotRequired,
        });
        await stopTask;

        Assert.AreEqual("Stop", vm.SendButtonText, "The earlier operation's failure does not apply to the fresh projection.");
        Assert.IsFalse(vm.HasGoalStopStatus);
    }

    [TestMethod]
    public async Task ChatViewModel_ActiveGoalStillAllowsSlashCommandsAndResumeClearsStopMessage()
    {
        const string threadId = "slash-during-goal";
        var bridge = new FakeWorkerBridge { CurrentThreadGoal = ActiveGoal(threadId) };
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId);
        vm.SlashCommands.ShowSuggestions([new SlashCommandSuggestionDescriptor("/goal", "Manage the goal")]);
        vm.SlashCommands.AcceptSuggestionCommand.Execute(null);
        await WaitForAsync(() => vm.SlashCommands.HasActiveCommand);

        Assert.IsTrue(vm.ShowSlashExecuteButton, "An active goal does not block slash commands such as /goal pause.");
        Assert.IsFalse(vm.IsPrimaryActionVisible);
        await RunCommandAsync(vm.SlashCommands.ClearCommandCommand);
        await WaitForAsync(() => !vm.SlashCommands.HasActiveCommand);

        await RunCommandAsync(vm.SendCommand);
        Assert.AreEqual("Goal stopped.", vm.GoalStopStatusText);
        await bridge.PublishThreadGoalChangedAsync(new ThreadGoalEvent { ThreadId = threadId, Goal = ActiveGoal(threadId) });

        Assert.AreEqual("Stop", vm.SendButtonText);
        Assert.IsFalse(vm.HasGoalStopStatus, "Resuming the goal clears the previous stop message.");
    }

    [TestMethod]
    public async Task ChatViewModel_PlanRechecksTheGoalStopFenceImmediatelyBeforeStartingTheTurn()
    {
        const string threadId = "plan-fence";
        var bridge = new FakeWorkerBridge { ExperimentalApi = true };
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId);
        MethodInfo executePlan = typeof(ChatViewModel).GetMethod("ExecutePlanAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find ExecutePlanAsync.");
        FieldInfo fence = typeof(ChatViewModel).GetField("goalStopFenceVersion", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find goalStopFenceVersion.");
        long captured = (long)fence.GetValue(vm)!;

        // A Stop that starts while /plan awaits its request advances the fence.
        fence.SetValue(vm, captured + 1);
        bool staleStarted = await (Task<bool>)executePlan.Invoke(vm, [threadId, "plan the change", captured])!;

        Assert.IsFalse(staleStarted);
        Assert.AreEqual(0, bridge.StartTurnCallCount, "A dequeued /plan never starts a turn after a Stop began.");

        bool currentStarted = await (Task<bool>)executePlan.Invoke(vm, [threadId, "plan the change", captured + 1])!;
        Assert.IsTrue(currentStarted);
        Assert.AreEqual(1, bridge.StartTurnCallCount);
    }

    [TestMethod]
    public async Task ChatViewModel_ForkRechecksTheGoalStopFenceBeforeDispatch()
    {
        const string threadId = "fork-fence";
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId);
        MethodInfo executeFork = typeof(ChatViewModel).GetMethod("ExecuteForkAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find ExecuteForkAsync.");
        FieldInfo fence = GoalStopFenceField();
        long captured = (long)fence.GetValue(vm)!;
        fence.SetValue(vm, captured + 1);

        bool forked = await (Task<bool>)executeFork.Invoke(vm, [threadId, captured])!;

        Assert.IsFalse(forked);
        Assert.AreEqual(0, bridge.ForkThreadRequests.Count, "A dequeued /fork never dispatches after a Stop began.");
    }

    [TestMethod]
    public async Task ChatViewModel_ShellPreparationStoppedMidwayNeverOffersConfirmation()
    {
        const string threadId = "shell-fence";
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreateJoinedGoalViewModelAsync(bridge, threadId);
        MethodInfo prepareShell = typeof(ChatViewModel).GetMethod("PrepareShellCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find PrepareShellCommandAsync.");
        FieldInfo fence = GoalStopFenceField();
        long captured = (long)fence.GetValue(vm)!;

        bool staleQueued = await (Task<bool>)prepareShell.Invoke(vm, ["-- echo hi", threadId, captured - 1])!;
        Assert.IsFalse(staleQueued);
        Assert.AreEqual(0, bridge.ShellCommandPrepareRequests.Count, "A dequeued /shell is not prepared after a Stop began.");

        // A Stop that begins while the preparation request is in flight discards its confirmation.
        bridge.ShellCommandPrepareHandler = (request, _) =>
        {
            fence.SetValue(vm, captured + 1);
            return Task.FromResult(new ShellCommandPrepareResult
            {
                IsSupported = true,
                IsEligible = true,
                Confirmation = new ShellCommandConfirmationSnapshot { ConfirmationId = "c1", ThreadId = threadId, Command = request.Command },
            });
        };
        bool prepared = await (Task<bool>)prepareShell.Invoke(vm, ["-- echo hi", threadId, captured])!;

        Assert.IsFalse(prepared);
        Assert.IsNull(vm.ShellCommandConfirmation);
        Assert.AreEqual(1, bridge.ShellCommandPrepareRequests.Count);
    }

    private static FieldInfo GoalStopFenceField()
        => typeof(ChatViewModel).GetField("goalStopFenceVersion", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find goalStopFenceVersion.");

    private static async Task<ChatViewModel> CreateJoinedGoalViewModelAsync(FakeWorkerBridge bridge, string threadId, string? turnId = null)
    {
        var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var thread = new ThreadSummary { Id = threadId };
        vm.Threads.Add(thread);
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = threadId });
        vm.SelectedThread = thread;
        await RunCommandAsync(vm.JoinThreadCommand);
        if (turnId is not null)
        {
            await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Busy, ThreadId = threadId, TurnId = turnId });
        }

        return vm;
    }

    private static ThreadGoalInfo ActiveGoal(string threadId)
        => new() { ThreadId = threadId, Objective = "finish the task", Status = ThreadGoalStatus.Active };

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

    private static WorkerStatus AuthenticationRejectedStatus(RemoteConnectionProfile profile, long generation)
    {
        WorkerStatus status = RemoteStatus(profile, WorkerConnectionState.Degraded, generation);
        status.RecoveryFailureKind = WorkerRecoveryFailureKind.AuthenticationRejected;
        return status;
    }

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
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings { WindowsSandboxBackendId = "follow" }));
        await bridge.PublishStateAsync(new WorkerStatus
        {
            State = WorkerConnectionState.Degraded,
            Target = new ConnectionTargetSnapshot { Kind = ConnectionTargetKind.Local, Generation = 1 },
        });

        vm.SelectedWindowsSandboxBackendId = "preferMxc";
        await RunCommandAsync(vm.RestartCommand);

        Assert.AreEqual(1, bridge.RestartCallCount);
        Assert.AreEqual(0, bridge.ReconnectRequests.Count);
        Assert.AreEqual(WindowsSandboxBackendPreference.PreferMxc, bridge.LastRestartRequest?.WindowsSandboxBackend,
            "A local restart applies the saved backend, not the one cached from the failed start.");
        Assert.IsFalse(vm.HasWindowsSandboxBackendPending);
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
    public async Task ChatViewModel_RemoteReconnect_ClearsConnectionFailureBannerWhenReady()
    {
        var bridge = new FakeWorkerBridge();
        RemoteConnectionProfile profile = SavedRemoteProfile();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(profile)));
        await bridge.PublishStateAsync(AuthenticationRejectedStatus(profile, generation: 9));
        Assert.AreEqual("Reconnection requires sign-in or updated credentials.", vm.RecoveryStatusText);

        bridge.ReconnectStatus = RemoteStatus(profile, WorkerConnectionState.Ready, generation: 10);
        await RunCommandAsync(vm.RestartCommand);

        Assert.AreEqual(WorkerConnectionState.Ready, vm.Status.State);
        Assert.AreEqual(string.Empty, vm.RecoveryStatusText);
        Assert.IsFalse(vm.HasRecoveryStatus);
    }

    [TestMethod]
    public async Task ChatViewModel_StaleReady_KeepsConnectionFailureBanner()
    {
        var bridge = new FakeWorkerBridge();
        RemoteConnectionProfile profile = SavedRemoteProfile();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(SettingsWith(profile)));
        await bridge.PublishStateAsync(AuthenticationRejectedStatus(profile, generation: 9));

        await bridge.PublishStateAsync(RemoteStatus(profile, WorkerConnectionState.Ready, generation: 8));

        Assert.AreEqual(WorkerConnectionState.Degraded, vm.Status.State);
        Assert.AreEqual("Reconnection requires sign-in or updated credentials.", vm.RecoveryStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_RetiredBridgeConnectionLoss_DoesNotPublishFailureBanner()
    {
        var bridge = new FakeWorkerBridge();
        var retired = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        MethodInfo method = typeof(ChatViewModel).GetMethod("HandleConnectionLostAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(vm, [retired, WorkerRecoveryFailureKind.AuthenticationRejected])!;

        Assert.AreEqual(string.Empty, vm.RecoveryStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_RecoveryEndingAfterNewerReady_DoesNotPublishFailureBanner()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        MethodInfo method = typeof(ChatViewModel).GetMethod("FinishRecoveryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(vm, [new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Stopped, 5, WorkerRecoveryFailureKind.AuthenticationRejected)])!;

        Assert.AreEqual(string.Empty, vm.RecoveryStatusText);
        Assert.IsFalse(vm.IsRecovering);
    }

    [TestMethod]
    public async Task ChatViewModel_Ready_KeepsNonConnectionRecoveryNotices()
    {
        var bridge = new FakeWorkerBridge
        {
            StartThreadException = new StreamJsonRpc.RemoteInvocationException(
                "The Worker rejected the connection operation.",
                WorkerErrorCodes.ConnectionOperationRejected,
                errorData: "StaleGeneration"),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        await RunCommandAsync(vm.NewThreadCommand);
        StringAssert.Contains(vm.RecoveryStatusText, "outcome is unknown");

        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Busy });
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        StringAssert.Contains(vm.RecoveryStatusText, "outcome is unknown");
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
    {
        string role = kind switch
        {
            ConversationEventKind.AgentMessageDelta => "Codex",
            ConversationEventKind.ReasoningSummaryDelta => "Reasoning",
            ConversationEventKind.CommandOutputDelta => "Command",
            ConversationEventKind.DiffUpdated => "Diff",
            _ => "Codex",
        };
        return viewModel.Items.Single(item => item.ItemId == itemId && item.Kind == kind && item.Role == role);
    }

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

    // Plan mode needs Experimental API; tests that use it must not depend on the machine's settings file.
    private static MemorySettingsStore ExperimentalSettingsStore()
        => new(new ExtensionSettings { ExperimentalApiEnabled = true });

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

    private sealed class GoalConfirmationFixture : IDisposable
    {
        private UserInputViewModel? displayedCard;

        public GoalConfirmationFixture(FakeWorkerBridge bridge, ChatViewModel viewModel, string threadId, string turnId)
        {
            Bridge = bridge;
            ViewModel = viewModel;
            ThreadId = threadId;
            TurnId = turnId;
        }

        public FakeWorkerBridge Bridge { get; }

        public ChatViewModel ViewModel { get; }

        public string ThreadId { get; }

        public string TurnId { get; }

        public UserInputViewModel? Card => displayedCard ?? ViewModel.ActiveUserInput;

        public int StartTurnCallCount => Bridge.StartTurnCallCount;

        public StartTurnRequest? LastStartTurnRequest => Bridge.LastStartTurnRequest;

        public async Task PublishFinalQuestionAsync()
        {
            await Bridge.PublishConversationEventAsync(new ConversationEvent
            {
                Kind = ConversationEventKind.AgentMessageDelta,
                ThreadId = ThreadId,
                TurnId = TurnId,
                ItemId = "goal-confirmation-agent-message",
                Text = string.Join('\n',
                    "依存制約が変わっていないことを再確認しました。更新済みの変更は保持し、ゴールを回答待ち（blocked）にしました。",
                    string.Empty,
                    "残りの最新版化には対象フレームワークの変更が必要です。ご提示のAGENTS.mdの範囲外変更の確認ルールに従い、**.NET 11 RC2への移行を進めてよいですか？**"),
            });

            await Bridge.PublishThreadGoalChangedAsync(new ThreadGoalEvent
            {
                ThreadId = ThreadId,
                TurnId = TurnId,
                Goal = new ThreadGoalInfo
                {
                    ThreadId = ThreadId,
                    Objective = "preserve package target frameworks",
                    Status = ThreadGoalStatus.Blocked,
                },
            });

            WorkerStatus readyWithoutTurn = OwnerStatus("goal-confirmation-owner", 1, 1);
            readyWithoutTurn.ThreadId = ThreadId;
            await Bridge.PublishStateAsync(readyWithoutTurn);
        }

        public async Task CompleteTurnAsync()
        {
            await Bridge.PublishConversationEventAsync(new ConversationEvent
            {
                Kind = ConversationEventKind.TurnCompleted,
                ThreadId = ThreadId,
                TurnId = TurnId,
            });
            displayedCard = ViewModel.ActiveUserInput;
        }

        public void SelectYes()
        {
            UserInputViewModel card = Card ?? throw new InvalidOperationException("The Goal confirmation card is not available.");
            UserInputOptionViewModel yes = card.Questions.Single().Options.Single(option => option.DisplayLabel == "Yes");
            yes.IsSelected = true;
        }

        public Task SubmitAsync()
        {
            UserInputViewModel card = Card ?? throw new InvalidOperationException("The Goal confirmation card is not available.");
            return RunCommandAsync(card.SubmitCommand);
        }

        public Task ChangeOwnerAsync()
        {
            WorkerStatus replacementOwner = OwnerStatus("goal-confirmation-owner-replacement", 2, 2);
            replacementOwner.ThreadId = ThreadId;
            return Bridge.PublishStateAsync(replacementOwner);
        }

        public void Dispose() => ViewModel.Dispose();
    }

    private sealed class FakeWorkerBridge : IWorkerBridge
    {
        public event Action<WorkerRecoveryFailureKind>? ConnectionLost { add { } remove { } }

        public event Func<WorkerNotification<WorkerStatus>, Task>? StateChanged;

        public event Func<WorkerNotification<AccountStatus>, Task>? AccountChanged;

        public event Func<WorkerNotification<ConversationEvent>, Task>? ConversationEventReceived;

        public event Func<WorkerNotification<ThreadReadStateChangedEvent>, Task>? ThreadReadStateChanged;

        public event Func<WorkerNotification<ThreadAttachmentUpdatedEvent>, Task>? ThreadAttachmentUpdated { add { } remove { } }

        public event Func<WorkerNotification<WindowsSandboxSetupCompletedEvent>, Task>? WindowsSandboxSetupChanged;

        public event Func<WorkerNotification<ApprovalRequest>, Task>? ApprovalRequested { add { } remove { } }

        public event Func<WorkerNotification<string>, Task>? ApprovalResolved { add { } remove { } }

        public event Func<WorkerNotification<UserInputRequest>, Task>? UserInputRequested { add { } remove { } }

        public event Func<WorkerNotification<string>, Task>? UserInputResolved { add { } remove { } }

        public event Func<WorkerNotification<PermissionRequest>, Task>? PermissionRequested { add { } remove { } }

        public event Func<WorkerNotification<string>, Task>? PermissionResolved { add { } remove { } }

        public event Func<WorkerNotification<McpElicitationRequest>, Task>? McpElicitationRequested { add { } remove { } }

        public event Func<WorkerNotification<string>, Task>? McpElicitationResolved { add { } remove { } }

        public event Func<WorkerNotification<UnsupportedInteractionNotice>, Task>? UnsupportedInteractionReceived { add { } remove { } }

        public event Func<WorkerNotification<InteractionAuthStatus>, Task>? InteractionAuthStatusChanged;

        public event Func<WorkerNotification<ContextCompactionEvent>, Task>? ContextCompacted { add { } remove { } }

        public event Func<WorkerNotification<ReviewModeEvent>, Task>? ReviewModeChanged { add { } remove { } }

        public event Func<WorkerNotification<ThreadGoalEvent>, Task>? ThreadGoalChanged;

        public event Func<WorkerNotification<RateLimitsResult>, Task>? RateLimitsChanged;

        public event Func<WorkerNotification<SkillsChangedEvent>, Task>? SkillsChanged;

        public event Func<WorkerNotification<ApprovalAuditRecord>, Task>? ApprovalAuditReceived { add { } remove { } }

        private WorkerStatus currentStatus = new();

        public ListModelsResult ModelListResult { get; set; } = new();

        public ListPermissionProfilesResult PermissionProfilesResult { get; set; } = new();

        public int ModelListCallCount { get; private set; }

        public StartTurnRequest? LastStartTurnRequest { get; private set; }

        public int StartTurnCallCount { get; private set; }

        public ThreadGoalInfo? CurrentThreadGoal { get; set; }

        public Func<ThreadGoalRequest, CancellationToken, Task<ThreadGoalResult>>? GetThreadGoalHandler { get; set; }

        public Func<StopThreadGoalRequest, CancellationToken, Task<StopThreadGoalResult>>? StopThreadGoalHandler { get; set; }

        public List<StopThreadGoalRequest> StopThreadGoalRequests { get; } = [];

        public SteerTurnRequest? LastSteerTurnRequest { get; private set; }

        public int ReviewCallCount { get; private set; }

        public int RateLimitCallCount { get; private set; }

        public AccountStatus AccountStatusResult { get; set; } = new() { State = AccountState.SignedIn };

        public Func<ReadGatewayOAuthRequest, CancellationToken, Task<InteractionAuthStatus>>? ReadGatewayOAuthHandler { get; set; }

        public int ReadGatewayOAuthCallCount { get; private set; }

        public RateLimitsResult RateLimitsResult { get; set; } = new();

        public ListSkillsResult SkillsResult { get; set; } = new() { IsSupported = true };

        public Func<int, bool, Task<ListSkillsResult>>? SkillsListHandler { get; set; }

        public int SkillsListCallCount { get; private set; }

        public Func<int, Task<RateLimitsResult>>? RateLimitHandler { get; set; }

        public Func<string?, CancellationToken, Task<ThreadPage>>? ListThreadsHandler { get; set; }

        public bool PreserveThreadPageOwner { get; set; }

        public int ListThreadsCallCount { get; private set; }

        public int ReadThreadCallCount { get; private set; }

        public Func<ReadThreadRequest, CancellationToken, Task<ThreadReadResult>>? ReadThreadHandler { get; set; }

        public Func<UpdateThreadReadStateRequest, CancellationToken, Task<UpdateThreadReadStateResult>>? UpdateThreadReadStateHandler { get; set; }

        public UpdateThreadReadStateRequest? LastUpdateThreadReadStateRequest { get; private set; }

        public int UpdateThreadReadStateCallCount { get; private set; }

        public Func<ListThreadItemsRequest, CancellationToken, Task<ThreadItemsPage>>? ListThreadItemsHandler { get; set; }

        public Func<ListThreadTurnsRequest, CancellationToken, Task<ThreadTurnsPage>>? ListThreadTurnsHandler { get; set; }

        public Func<ListThreadAttachmentsRequest, CancellationToken, Task<ThreadAttachmentsPage>>? ListThreadAttachmentsHandler { get; set; }

        public Func<ArtifactActionRequest, CancellationToken, Task<ArtifactActionResult>>? ArtifactActionHandler { get; set; }

        public Func<ShellCommandPrepareRequest, CancellationToken, Task<ShellCommandPrepareResult>>? ShellCommandPrepareHandler { get; set; }

        public Func<ShellCommandExecuteRequest, CancellationToken, Task<ShellCommandExecuteResult>>? ShellCommandExecuteHandler { get; set; }

        public Func<SavedAttachmentAddRequest, CancellationToken, Task<SavedAttachmentMutationResult>>? SavedAttachmentAddHandler { get; set; }

        public Func<SavedAttachmentRemoveRequest, CancellationToken, Task<SavedAttachmentMutationResult>>? SavedAttachmentRemoveHandler { get; set; }

        public Func<WindowsSandboxReadinessRequest, CancellationToken, Task<WindowsSandboxReadinessResult>>? WindowsSandboxReadinessHandler { get; set; }

        public Func<WindowsSandboxSetupStartRequest, CancellationToken, Task<WindowsSandboxSetupStartResult>>? WindowsSandboxSetupStartHandler { get; set; }

        public List<ArtifactActionRequest> ArtifactActionRequests { get; } = [];

        public List<ShellCommandPrepareRequest> ShellCommandPrepareRequests { get; } = [];

        public List<ShellCommandExecuteRequest> ShellCommandExecuteRequests { get; } = [];

        public List<SavedAttachmentAddRequest> SavedAttachmentAddRequests { get; } = [];

        public List<SavedAttachmentRemoveRequest> SavedAttachmentRemoveRequests { get; } = [];

        public List<WindowsSandboxReadinessRequest> WindowsSandboxReadinessRequests { get; } = [];

        public List<WindowsSandboxSetupStartRequest> WindowsSandboxSetupStartRequests { get; } = [];

        public Func<ListPresetsRequest, CancellationToken, Task<ListPresetsResult>>? ListPresetsHandler { get; set; }

        public Func<WritePresetRequest, CancellationToken, Task<WritePresetResult>>? WritePresetHandler { get; set; }

        public int ListPresetsCallCount { get; private set; }

        public List<WritePresetRequest> WritePresetRequests { get; } = [];

        public Task<ListPresetsResult> ListPresetsAsync(ListPresetsRequest request, CancellationToken cancellationToken)
        {
            ListPresetsCallCount++;
            return ListPresetsHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ListPresetsResult { IsSupported = true });
        }

        public Task<WritePresetResult> WritePresetAsync(WritePresetRequest request, CancellationToken cancellationToken)
        {
            WritePresetRequests.Add(request);
            return WritePresetHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new WritePresetResult { IsSupported = true, Outcome = WritePresetOutcome.Written });
        }

        public Task PublishConversationEventAsync(ConversationEvent value, WorkerStatus? status = null)
            => ConversationEventReceived?.Invoke(Notification(value, status ?? currentStatus)) ?? Task.CompletedTask;

        public Task PublishThreadReadStateChangedAsync(ThreadReadStateChangedEvent value, WorkerStatus? status = null)
            => ThreadReadStateChanged?.Invoke(Notification(value, status ?? currentStatus)) ?? Task.CompletedTask;

        public Task PublishThreadGoalChangedAsync(ThreadGoalEvent value)
            => ThreadGoalChanged?.Invoke(Notification(value, currentStatus)) ?? Task.CompletedTask;

        public Task PublishWindowsSandboxSetupChangedAsync(WindowsSandboxSetupCompletedEvent value, WorkerStatus? status = null)
            => WindowsSandboxSetupChanged?.Invoke(Notification(value, status ?? currentStatus)) ?? Task.CompletedTask;

        public Exception? StartThreadException { get; set; }

        public int StartThreadCallCount { get; private set; }

        public Exception? ResolveApprovalException { get; set; }

        public Exception? ResolveUserInputException { get; set; }

        public Exception? StartTurnException { get; set; }

        // Stamps published statuses as a connection that negotiated experimentalApi.
        public bool ExperimentalApi { get; set; }

        public Task PublishStateAsync(WorkerStatus status)
        {
            if (ExperimentalApi)
            {
                status.Target ??= new ConnectionTargetSnapshot();
                status.Target.ExperimentalApi = true;
            }

            currentStatus = status;
            return StateChanged?.Invoke(Notification(status, status)) ?? Task.CompletedTask;
        }

        public Task PublishAccountAsync(AccountStatus status, WorkerStatus? owner = null)
            => AccountChanged?.Invoke(Notification(status, owner ?? currentStatus)) ?? Task.CompletedTask;

        public Task PublishRateLimitsAsync(RateLimitsResult result)
            => RateLimitsChanged?.Invoke(Notification(result, currentStatus)) ?? Task.CompletedTask;

        public Task PublishSkillsChangedAsync(SkillsChangedEvent? value = null)
            => SkillsChanged?.Invoke(Notification(value ?? new SkillsChangedEvent(), currentStatus)) ?? Task.CompletedTask;

        public Task PublishInteractionAuthStatusAsync(InteractionAuthStatus value)
            => InteractionAuthStatusChanged?.Invoke(Notification(value, currentStatus)) ?? Task.CompletedTask;

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

        public LocalRestartRequest? LastRestartRequest { get; private set; }

        public Task<WorkerStatus> RestartAsync(LocalRestartRequest request, CancellationToken cancellationToken)
        {
            RestartCallCount++;
            LastRestartRequest = request;
            return Task.FromResult(new WorkerStatus { State = WorkerConnectionState.Ready });
        }

        public List<RemoteReconnectRequest> ReconnectRequests { get; } = [];

        public Exception? ReconnectException { get; set; }

        public WorkerStatus? ReconnectStatus { get; set; }

        public Task<WorkerStatus> ReconnectAsync(RemoteReconnectRequest request, CancellationToken cancellationToken)
        {
            ReconnectRequests.Add(request);
            return ReconnectException is null
                ? Task.FromResult(ReconnectStatus ?? new WorkerStatus { State = WorkerConnectionState.Ready })
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

        public async Task<ThreadPage> ListThreadsAsync(ListThreadsRequest request, CancellationToken cancellationToken)
        {
            ListThreadsCallCount++;
            ThreadPage page = await (ListThreadsHandler?.Invoke(request.Cursor, cancellationToken)
                ?? Task.FromResult(new ThreadPage()));
            if (!PreserveThreadPageOwner)
            {
                page.StatePartitionFingerprint = request.StatePartitionFingerprint;
                page.OwnerGeneration = request.OwnerGeneration;
                page.ConnectionGeneration = request.ConnectionGeneration;
            }

            return page;
        }

        public async Task<ThreadReadResult> ReadThreadAsync(ReadThreadRequest request, CancellationToken cancellationToken)
        {
            ReadThreadCallCount++;
            ThreadReadResult result = await (ReadThreadHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ThreadReadResult { Thread = new ThreadSummary { Id = request.ThreadId } }));
            result.StatePartitionFingerprint = request.StatePartitionFingerprint;
            result.OwnerGeneration = request.OwnerGeneration;
            result.ConnectionGeneration = request.ConnectionGeneration;
            return result;
        }

        public async Task<UpdateThreadReadStateResult> UpdateThreadReadStateAsync(UpdateThreadReadStateRequest request, CancellationToken cancellationToken)
        {
            LastUpdateThreadReadStateRequest = request;
            UpdateThreadReadStateCallCount++;
            UpdateThreadReadStateResult result = await (UpdateThreadReadStateHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new UpdateThreadReadStateResult
                {
                    ThreadId = request.ThreadId,
                    Succeeded = true,
                    State = new ThreadReadStateSnapshot
                    {
                        Kind = request.MarkRead ? ThreadReadStateKind.Read : ThreadReadStateKind.UnreadThreadStart,
                        Revision = "next-revision",
                    },
                }));
            result.ThreadId = request.ThreadId;
            result.StatePartitionFingerprint = request.StatePartitionFingerprint;
            result.OwnerGeneration = request.OwnerGeneration;
            result.ConnectionGeneration = request.ConnectionGeneration;
            return result;
        }

        public async Task<ThreadTurnsPage> ListThreadTurnsAsync(ListThreadTurnsRequest request, CancellationToken cancellationToken)
        {
            ThreadTurnsPage result = await (ListThreadTurnsHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ThreadTurnsPage()));
            result.StatePartitionFingerprint = request.StatePartitionFingerprint;
            result.OwnerGeneration = request.OwnerGeneration;
            result.ConnectionGeneration = request.ConnectionGeneration;
            return result;
        }

        public async Task<ThreadItemsPage> ListThreadItemsAsync(ListThreadItemsRequest request, CancellationToken cancellationToken)
        {
            ThreadItemsPage result = await (ListThreadItemsHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ThreadItemsPage()));
            result.StatePartitionFingerprint = request.StatePartitionFingerprint;
            result.OwnerGeneration = request.OwnerGeneration;
            result.ConnectionGeneration = request.ConnectionGeneration;
            return result;
        }

        public async Task<ThreadAttachmentsPage> ListThreadAttachmentsAsync(ListThreadAttachmentsRequest request, CancellationToken cancellationToken)
        {
            ThreadAttachmentsPage page = await (ListThreadAttachmentsHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ThreadAttachmentsPage()));
            page.StatePartitionFingerprint = request.StatePartitionFingerprint;
            page.OwnerGeneration = request.OwnerGeneration;
            page.ConnectionGeneration = request.ConnectionGeneration;
            return page;
        }

        public Task<ArtifactActionResult> ResolveArtifactActionAsync(ArtifactActionRequest request, CancellationToken cancellationToken)
        {
            ArtifactActionRequests.Add(request);
            return ArtifactActionHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ArtifactActionResult { IsSupported = false, UnavailableReason = "Artifact actions are not configured in this fake." });
        }

        public Task<ShellCommandPrepareResult> PrepareShellCommandAsync(ShellCommandPrepareRequest request, CancellationToken cancellationToken)
        {
            ShellCommandPrepareRequests.Add(request);
            return ShellCommandPrepareHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ShellCommandPrepareResult { IsSupported = false, RejectionReason = "Shell command preparation is not configured in this fake." });
        }

        public Task<ShellCommandExecuteResult> ExecuteShellCommandAsync(ShellCommandExecuteRequest request, CancellationToken cancellationToken)
        {
            ShellCommandExecuteRequests.Add(request);
            return ShellCommandExecuteHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ShellCommandExecuteResult { IsSupported = false, Outcome = ShellCommandOutcome.NotSent });
        }

        public Task<SavedAttachmentMutationResult> AddSavedAttachmentAsync(SavedAttachmentAddRequest request, CancellationToken cancellationToken)
        {
            SavedAttachmentAddRequests.Add(request);
            return SavedAttachmentAddHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new SavedAttachmentMutationResult { IsSupported = false, Outcome = AttachmentMutationOutcome.NotSent });
        }

        public Task<SavedAttachmentMutationResult> RemoveSavedAttachmentAsync(SavedAttachmentRemoveRequest request, CancellationToken cancellationToken)
        {
            SavedAttachmentRemoveRequests.Add(request);
            return SavedAttachmentRemoveHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new SavedAttachmentMutationResult { IsSupported = false, Outcome = AttachmentMutationOutcome.NotSent });
        }

        public Task<WindowsSandboxReadinessResult> GetWindowsSandboxReadinessAsync(WindowsSandboxReadinessRequest request, CancellationToken cancellationToken)
        {
            WindowsSandboxReadinessRequests.Add(request);
            return WindowsSandboxReadinessHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new WindowsSandboxReadinessResult { IsSupported = false });
        }

        public Task<WindowsSandboxSetupStartResult> StartWindowsSandboxSetupAsync(WindowsSandboxSetupStartRequest request, CancellationToken cancellationToken)
        {
            WindowsSandboxSetupStartRequests.Add(request);
            return WindowsSandboxSetupStartHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new WindowsSandboxSetupStartResult { IsSupported = false, State = WindowsSandboxSetupState.Unsupported });
        }

        public Task<ListModelsResult> ListModelsAsync(ListModelsRequest request, CancellationToken cancellationToken)
        {
            ModelListCallCount++;
            return Task.FromResult(ModelListResult);
        }

        public Task<ListPermissionProfilesResult> ListPermissionProfilesAsync(ListPermissionProfilesRequest request, CancellationToken cancellationToken)
            => Task.FromResult(PermissionProfilesResult);

        public Task<ThreadSummary> StartThreadAsync(StartThreadRequest request, CancellationToken cancellationToken)
        {
            StartThreadCallCount++;
            return StartThreadException is null
                ? Task.FromResult(new ThreadSummary { Id = "thread-1" })
                : Task.FromException<ThreadSummary>(StartThreadException);
        }

        public string? LastResumedThreadId { get; private set; }

        public ResumeThreadRequest? LastResumeRequest { get; private set; }

        public Task<ThreadSummary> ResumeThreadAsync(ResumeThreadRequest request, CancellationToken cancellationToken)
        {
            LastResumedThreadId = request.ThreadId;
            LastResumeRequest = request;
            return Task.FromResult(new ThreadSummary { Id = request.ThreadId });
        }

        public Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken)
        {
            StartTurnCallCount++;
            LastStartTurnRequest = request;
            if (StartTurnHandler is not null)
            {
                return StartTurnHandler(request, cancellationToken);
            }

            return StartTurnException is null
                ? Task.FromResult("turn-1")
                : Task.FromException<string>(StartTurnException);
        }

        public Func<StartTurnRequest, CancellationToken, Task<string>>? StartTurnHandler { get; set; }

        public Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken)
        {
            LastSteerTurnRequest = request;
            return Task.FromResult(request.ExpectedTurnId);
        }

        public InterruptTurnRequest? LastInterruptTurnRequest { get; private set; }

        public Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken)
        {
            LastInterruptTurnRequest = request;
            return Task.CompletedTask;
        }

        public Task<CompactThreadResult> CompactThreadAsync(CompactThreadRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new CompactThreadResult());

        public Task<StartReviewResult> StartReviewAsync(StartReviewRequest request, CancellationToken cancellationToken)
        {
            ReviewCallCount++;
            return Task.FromResult(new StartReviewResult { TurnId = "turn-review" });
        }

        public List<ForkThreadRequest> ForkThreadRequests { get; } = [];

        public Task<ForkThreadResult> ForkThreadAsync(ForkThreadRequest request, CancellationToken cancellationToken)
        {
            ForkThreadRequests.Add(request);
            return Task.FromResult(new ForkThreadResult { Thread = new ThreadSummary { Id = "thread-fork" } });
        }

        public Task<ThreadGoalResult> GetThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken)
            => GetThreadGoalHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new ThreadGoalResult { Goal = CurrentThreadGoal });

        public Task<ThreadGoalResult> SetThreadGoalAsync(SetThreadGoalRequest request, CancellationToken cancellationToken)
        {
            CurrentThreadGoal = new ThreadGoalInfo
            {
                ThreadId = request.ThreadId,
                Objective = request.Objective ?? string.Empty,
                Status = request.Status ?? ThreadGoalStatus.Active,
            };
            return Task.FromResult(new ThreadGoalResult { Goal = CurrentThreadGoal });
        }

        public Task<ThreadGoalResult> ClearThreadGoalAsync(ThreadGoalRequest request, CancellationToken cancellationToken)
        {
            CurrentThreadGoal = null;
            return Task.FromResult(new ThreadGoalResult { Cleared = true });
        }

        public Task<StopThreadGoalResult> StopThreadGoalAsync(StopThreadGoalRequest request, CancellationToken cancellationToken)
        {
            StopThreadGoalRequests.Add(request);
            if (StopThreadGoalHandler is not null)
            {
                return StopThreadGoalHandler(request, cancellationToken);
            }

            CurrentThreadGoal = CurrentThreadGoal is null
                ? null
                : new ThreadGoalInfo
                {
                    ThreadId = CurrentThreadGoal.ThreadId,
                    Objective = CurrentThreadGoal.Objective,
                    Status = ThreadGoalStatus.Paused,
                };
            return Task.FromResult(new StopThreadGoalResult
            {
                StatePartitionFingerprint = request.StatePartitionFingerprint,
                OwnerGeneration = request.OwnerGeneration,
                ConnectionGeneration = request.ConnectionGeneration,
                Goal = CurrentThreadGoal,
                PauseOutcome = GoalStopStepOutcome.Succeeded,
                InterruptOutcome = currentStatus.TurnId is null ? GoalStopStepOutcome.NotRequired : GoalStopStepOutcome.Succeeded,
                TurnId = currentStatus.TurnId,
            });
        }

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

        public Task ResolvePermissionSelectionAsync(ResolvePermissionSelectionRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveMcpElicitationAsync(ResolveMcpElicitationRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<InteractionAuthStatus> ReadGatewayOAuthAsync(ReadGatewayOAuthRequest request, CancellationToken cancellationToken)
        {
            ReadGatewayOAuthCallCount++;
            return ReadGatewayOAuthHandler?.Invoke(request, cancellationToken) ?? Task.FromResult(new InteractionAuthStatus());
        }

        public Task<InteractionAuthStatus> LoginGatewayOAuthAsync(LoginGatewayOAuthRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InteractionAuthStatus());

        public Task<InteractionAuthStatus> CancelGatewayOAuthAsync(CancelGatewayOAuthRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InteractionAuthStatus());

        public Task OpenAuthorizationUrlAsync(OpenAuthorizationUrlRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<McpOAuthLoginStatus> StartMcpOAuthLoginAsync(StartMcpOAuthLoginRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new McpOAuthLoginStatus());

        public Task DismissMcpOAuthLoginAsync(DismissMcpOAuthLoginRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public int DisposeCallCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }
}
