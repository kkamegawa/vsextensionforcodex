using System.IO;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

public sealed partial class ViewModelTests
{
    private static readonly string[] EligibleSearchWorkingDirectories = [@"C:\workspace"];

    [TestMethod]
    public async Task HistoryArchiveView_ArchivesAndUnarchivesTheSelectedConversation()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-archive", 1, 1));
        vm.SelectedThread = new ThreadSummary { Id = "thread-archive", Preview = "Archive target" };

        await RunCommandAsync(vm.ArchiveThreadCommand);

        Assert.AreEqual(1, bridge.ArchiveThreadCallCount);
        Assert.AreEqual("thread-archive", bridge.LastThreadMutationRequest?.ThreadId);
        Assert.IsFalse(bridge.LastThreadMutationRequest?.UserConfirmed ?? true);

        vm.ShowArchivedThreads = true;
        await RunCommandAsync(vm.ArchiveThreadCommand);

        Assert.AreEqual(1, bridge.UnarchiveThreadCallCount);
        Assert.IsTrue(bridge.LastListThreadsRequest?.Archived ?? false);
    }

    [TestMethod]
    public async Task ArchiveAndUnarchiveFailuresRemainVisibleWithoutChangingSelection()
    {
        var bridge = new FakeWorkerBridge
        {
            ArchiveThreadHandler = (_, _) => Task.FromResult(new ThreadMutationResult { Succeeded = false, Message = "Archive rejected by server." }),
            UnarchiveThreadHandler = (_, _) => Task.FromResult(new ThreadMutationResult { Succeeded = false, Message = "Unarchive rejected by server." }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-archive-failure", 1, 1));
        vm.SelectedThread = new ThreadSummary { Id = "thread-archive-failure", Preview = "Archive failure" };

        await RunCommandAsync(vm.ArchiveThreadCommand);

        Assert.AreEqual("thread-archive-failure", vm.SelectedThread?.Id);
        Assert.IsFalse(vm.ShowArchivedThreads);
        StringAssert.Contains(vm.ThreadManagementStatusText, "Archive rejected by server.");

        vm.ShowArchivedThreads = true;
        await RunCommandAsync(vm.ArchiveThreadCommand);

        Assert.AreEqual("thread-archive-failure", vm.SelectedThread?.Id);
        Assert.IsTrue(vm.ShowArchivedThreads);
        StringAssert.Contains(vm.ThreadManagementStatusText, "Unarchive rejected by server.");
        Assert.AreEqual(1, bridge.ArchiveThreadCallCount);
        Assert.AreEqual(1, bridge.UnarchiveThreadCallCount);
    }
    [TestMethod]
    public async Task DeleteConversation_RequiresNamedConfirmationAndBlocksRunningOrLinkedParent()
    {
        bool childPresent = true;
        int linkedChildPageReads = 0;
        var bridge = new FakeWorkerBridge
        {
            ListThreadsHandler = (cursor, _) => Task.FromResult(cursor is null
                ? new ThreadPage
                {
                    Threads = [new ThreadSummary { Id = "thread-parent", Preview = "Parent conversation" }],
                    NextCursor = "linked-child-page",
                }
                : ReadLinkedChildPage()),
        };

        ThreadPage ReadLinkedChildPage()
        {
            linkedChildPageReads++;
            return new ThreadPage
            {
                Threads = childPresent ? [new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent" }] : [],
            };
        }

        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-delete", 1, 1));
        vm.HistorySearchText = "Parent";
        await RunCommandAsync(vm.SearchHistoryCommand);

        Assert.IsTrue(linkedChildPageReads > 0, "The production list ingestion must read the linked child page.");
        Assert.AreEqual("thread-parent", vm.ThreadRows.Single().Id, "The linked child must remain hidden from history rows.");
        var parent = vm.Threads.Single(thread => thread.Id == "thread-parent");
        vm.SelectedThread = new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent" };
        Assert.IsFalse(vm.CanDeleteSelectedThread, "A linked child cannot be deleted through history management.");
        vm.SelectedThread = parent;
        Assert.IsFalse(vm.CanDeleteSelectedThread, "The unfiltered fork index must block deletion even though the child row is hidden.");
        await RunCommandAsync(vm.DeleteThreadCommand);
        Assert.IsFalse(vm.IsThreadMutationConfirmationOpen);
        Assert.AreEqual(0, bridge.DeleteThreadCallCount);

        childPresent = false;
        await bridge.RaiseThreadHistoryChangedAsync(new WorkerNotification<ThreadHistoryChangedEvent>
        {
            Value = new ThreadHistoryChangedEvent { ThreadId = "thread-child", ChangeKind = "thread/deleted" },
            StatePartitionFingerprint = "history-delete",
            OwnerGeneration = 1,
            ConnectionGeneration = 1,
        });
        Assert.IsTrue(vm.CanDeleteSelectedThread, "Deleting the linked child invalidates its parent constraint.");
        await RunCommandAsync(vm.DeleteThreadCommand);
        Assert.IsTrue(vm.IsThreadMutationConfirmationOpen);
        Assert.IsFalse(string.IsNullOrWhiteSpace(vm.ThreadMutationConfirmationText));
        Assert.IsTrue(vm.ThreadMutationConfirmationText.Contains("Parent conversation", StringComparison.Ordinal) || vm.ThreadMutationConfirmationText.Contains("親会話", StringComparison.Ordinal) || vm.ThreadMutationConfirmationText.Contains("親の会話", StringComparison.Ordinal) || vm.ThreadMutationConfirmationText.Contains("タイトルなし", StringComparison.Ordinal));
        Assert.AreEqual(0, bridge.DeleteThreadCallCount, "Opening the inline confirmation must not dispatch the destructive request.");

        await RunCommandAsync(vm.ConfirmThreadMutationCommand);

        Assert.AreEqual(1, bridge.DeleteThreadCallCount);
        Assert.AreEqual("thread-parent", bridge.LastThreadMutationRequest?.ThreadId);
        Assert.IsTrue(bridge.LastThreadMutationRequest?.UserConfirmed ?? false);
    }
    [TestMethod]
    public async Task PendingThreadMutationConfirmationIsCanceledWhenOwnerChanges()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-owner-change", 1, 1));
        vm.HistorySearchText = "history";
        await RunCommandAsync(vm.SearchHistoryCommand);
        vm.SelectedThread = new ThreadSummary { Id = "owner-change-target", Preview = "Owner change target" };

        await RunCommandAsync(vm.DeleteThreadCommand);
        Assert.IsTrue(vm.IsThreadMutationConfirmationOpen);
        await bridge.PublishStateAsync(OwnerStatus("history-owner-change-next", 2, 2));

        Assert.IsFalse(vm.IsThreadMutationConfirmationOpen);
        await RunCommandAsync(vm.ConfirmThreadMutationCommand);
        Assert.AreEqual(0, bridge.DeleteThreadCallCount);
    }
    [TestMethod]
    public async Task ThreadSearchFallsBackToTitleFilterWhenExperimentalSearchIsUnsupported()
    {
        var bridge = new FakeWorkerBridge
        {
            SearchThreadsHandler = (_, _) => Task.FromResult(new ThreadSearchResult { IsSupported = false }),
            ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
            {
                Threads = [new ThreadSummary { Id = "title-match", Preview = "Needle in title" }],
            }),
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-search-fallback", 1, 1));
        vm.HistorySearchText = "needle";

        await RunCommandAsync(vm.SearchHistoryCommand);

        Assert.AreEqual("needle", bridge.LastListThreadsRequest?.SearchTerm);
        Assert.AreEqual("title-match", vm.ThreadRows.Single().Id);
    }

    [TestMethod]
    public async Task ThreadSearchDoesNotMaskOtherFailuresWithTitleFallback()
    {
        var bridge = new FakeWorkerBridge
        {
            SearchThreadsHandler = (_, _) => throw new IOException("search transport failed"),
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-search-failure", 1, 1));
        int listCallsBeforeSearch = bridge.ListThreadsCallCount;
        vm.HistorySearchText = "needle";

        await RunCommandAsync(vm.SearchHistoryCommand);

        Assert.IsFalse(string.Equals("needle", bridge.LastListThreadsRequest?.SearchTerm, StringComparison.Ordinal), "Arbitrary search failures must not trigger title-filter fallback.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(vm.ThreadManagementStatusText));
    }

    [TestMethod]
    public async Task ThreadSearch_CancellationDuringForkIndexRefreshDoesNotUseDisposedSource()
    {
        var firstListReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstListRead = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration firstReadCancellationRegistration = default;
        int listCalls = 0;
        int searchCalls = 0;
        string? dispatchedRequestId = null;
        var bridge = new FakeWorkerBridge
        {
            ListThreadsHandler = (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref listCalls) == 1)
                {
                    firstListReadStarted.TrySetResult();
                    firstReadCancellationRegistration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
#pragma warning disable VSTHRD003
                    return releaseFirstListRead.Task;
#pragma warning restore VSTHRD003
                }

                return Task.FromResult(new ThreadPage());
            },
            SearchThreadsHandler = (request, _) =>
            {
                Interlocked.Increment(ref searchCalls);
                Volatile.Write(ref dispatchedRequestId, request.RequestId);
                return Task.FromResult(new ThreadSearchResult());
            },
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-search-cancel", 1, 1));
        vm.HistorySearchText = "needle";

        Task firstSearch = (Task)typeof(ChatViewModel).GetMethod(
            "SearchHistoryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null)!;
        await firstListReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.ShowArchivedThreads = true;
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        firstReadCancellationRegistration.Unregister();
        releaseFirstListRead.TrySetResult(new ThreadPage());

#pragma warning disable VSTHRD003
        await firstSearch;
#pragma warning restore VSTHRD003
        await WaitUntilAsync(() => Volatile.Read(ref searchCalls) > 0);
        Assert.AreEqual(1, Volatile.Read(ref searchCalls), "The canceled search must not dispatch a search request; only the replacement search should run.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(Volatile.Read(ref dispatchedRequestId)));
    }

    [TestMethod]
    public async Task EligibleThreadSearchStopsWhenListRepeatsCursor()
    {
        var bridge = new FakeWorkerBridge
        {
            ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage { NextCursor = "repeated-cursor" }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        typeof(ChatViewModel).GetField("threadListWorkingDirectories", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, EligibleSearchWorkingDirectories);
        object owner = CaptureOwnerSnapshot(vm);
        var method = typeof(ChatViewModel).GetMethod("LoadEligibleSearchThreadIdsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Task<HashSet<string>?> load = (Task<HashSet<string>?>)method.Invoke(vm, [owner, CancellationToken.None])!;
        // The reflected task is created by the view model; this test awaits it only to observe its guard exception.
#pragma warning disable VSTHRD003
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => load);
#pragma warning restore VSTHRD003

        Assert.AreEqual(2, bridge.ListThreadsCallCount, "The repeated cursor must stop pagination instead of looping forever.");
    }

    [TestMethod]
    public async Task RevertConversation_RequiresJoinedIdleThreadAndConfirmationForExactTurn()
    {
        var bridge = new FakeWorkerBridge
        {
            ListThreadTurnsHandler = (request, _) => Task.FromResult(new ThreadTurnsPage
            {
                Turns = [new ThreadTurnSummary { Id = "turn-before", Status = "completed" }],
                StatePartitionFingerprint = request.StatePartitionFingerprint,
                OwnerGeneration = request.OwnerGeneration,
                ConnectionGeneration = request.ConnectionGeneration,
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-revert", 1, 1));
        vm.SelectedThread = new ThreadSummary { Id = "thread-revert", Preview = "Revert target" };
        typeof(ChatViewModel).GetProperty(nameof(ChatViewModel.IsThreadJoined))!
            .GetSetMethod(nonPublic: true)!.Invoke(vm, [true]);
        vm.ThreadTurns.Add(new ThreadTurnSummary { Id = "turn-before", Status = "completed" });
        vm.SelectedRevertTurnId = "turn-before";

        await RunCommandAsync(vm.RevertThreadCommand);
        Assert.IsTrue(vm.IsThreadMutationConfirmationOpen);
        Assert.IsTrue(vm.ThreadMutationConfirmationText.Contains("選択したターン", StringComparison.Ordinal) || vm.ThreadMutationConfirmationText.Contains("selected turn", StringComparison.Ordinal));
        Assert.IsTrue(vm.ThreadMutationConfirmationText.Contains("Local file changes will not be reverted", StringComparison.Ordinal) || vm.ThreadMutationConfirmationText.Contains("ローカルファイルの変更は元に戻りません", StringComparison.Ordinal));
        Assert.AreEqual(0, bridge.RevertThreadCallCount);

        await RunCommandAsync(vm.ConfirmThreadMutationCommand);

        Assert.AreEqual(1, bridge.RevertThreadCallCount);
        Assert.AreEqual("thread-revert", bridge.LastThreadMutationRequest?.ThreadId);
        Assert.AreEqual("turn-before", bridge.LastThreadMutationRequest?.BeforeTurnId);
        Assert.IsTrue(bridge.LastThreadMutationRequest?.UserConfirmed ?? false);
    }

    [TestMethod]
    public async Task ThreadSearch_StreamsSafePartialHitsAndFinalPageReplacesThem()
    {
        var completion = new TaskCompletionSource<ThreadSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            // The test-controlled completion is the synchronization point for partial search events.
#pragma warning disable VSTHRD003
            SearchThreadsHandler = (_, _) => completion.Task,
#pragma warning restore VSTHRD003
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        WorkerStatus status = OwnerStatus("history-search", 1, 1);
        await bridge.PublishStateAsync(status);
        vm.HistorySearchText = "needle";
        Task search = RunCommandAsync(vm.SearchHistoryCommand);
        await WaitUntilAsync(() => bridge.LastThreadSearchRequest is not null);
        await bridge.RaiseThreadHistoryChangedAsync(new WorkerNotification<ThreadHistoryChangedEvent>
        {
            Value = new ThreadHistoryChangedEvent { ThreadId = "unrelated-history-item", ChangeKind = "thread/updated" },
            StatePartitionFingerprint = bridge.LastThreadSearchRequest!.StatePartitionFingerprint,
            OwnerGeneration = bridge.LastThreadSearchRequest.OwnerGeneration,
            ConnectionGeneration = bridge.LastThreadSearchRequest.ConnectionGeneration,
        });

        var partialHit = new ThreadSearchHit { Thread = new ThreadSummary { Id = "partial", Preview = "Partial" }, Snippet = "partial\0 snippet" };
        await bridge.RaiseThreadSearchPartialAsync(new WorkerNotification<ThreadSearchPartialEvent>
        {
            Value = new ThreadSearchPartialEvent
            {
                RequestId = bridge.LastThreadSearchRequest!.RequestId!,
                Results = [partialHit],
            },
            StatePartitionFingerprint = bridge.LastThreadSearchRequest!.StatePartitionFingerprint,
            OwnerGeneration = bridge.LastThreadSearchRequest.OwnerGeneration,
            ConnectionGeneration = bridge.LastThreadSearchRequest.ConnectionGeneration,
        });
        Assert.AreEqual("partial snippet", vm.ThreadRows.Single().SearchSnippet);
        Assert.IsNull(partialHit.Thread.SearchSnippet, "Snippet rendering must not mutate a shared worker DTO.");

        completion.SetResult(new ThreadSearchResult
        {
            Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "final", Preview = "Final" }, Snippet = "final snippet" }],
        });
        await search;

        Assert.AreEqual("final", vm.ThreadRows.Single().Id);
        Assert.AreEqual("final snippet", vm.ThreadRows.Single().SearchSnippet);
        await bridge.RaiseThreadSearchPartialAsync(new WorkerNotification<ThreadSearchPartialEvent>
        {
            Value = new ThreadSearchPartialEvent { RequestId = bridge.LastThreadSearchRequest!.RequestId!, Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "stale-after-completion" } }] },
            StatePartitionFingerprint = bridge.LastThreadSearchRequest.StatePartitionFingerprint,
            OwnerGeneration = bridge.LastThreadSearchRequest.OwnerGeneration,
            ConnectionGeneration = bridge.LastThreadSearchRequest.ConnectionGeneration,
        });
        Assert.AreEqual("final", vm.ThreadRows.Single().Id, "A partial hit after completion must not repopulate a finished search.");
    }

    [TestMethod]
    public async Task EligibleThreadSearchStopsAfter100Pages()
    {
        int calls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage { NextCursor = "cursor-" + ++calls }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        typeof(ChatViewModel).GetField("threadListWorkingDirectories", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, EligibleSearchWorkingDirectories);
        object owner = CaptureOwnerSnapshot(vm);
        var method = typeof(ChatViewModel).GetMethod("LoadEligibleSearchThreadIdsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task<HashSet<string>?> load = (Task<HashSet<string>?>)method.Invoke(vm, [owner, CancellationToken.None])!;
#pragma warning disable VSTHRD003
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => load);
#pragma warning restore VSTHRD003
        Assert.AreEqual(100, bridge.ListThreadsCallCount, "Pagination must stop at the documented 100-page bound.");
    }

    [TestMethod]
    public async Task HistorySectionsExposeAllOptionAndRefreshAfterSectionChange()
    {
        var bridge = new FakeWorkerBridge
        {
            ThreadSectionsResult = new ThreadSectionsResult
            {
                Sections = [new ThreadSectionInfo { Id = "section-inbox", Name = "Inbox" }],
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-sections", 1, 1));
        await RunCommandAsync(vm.ToggleHistoryCommand);
        Assert.AreEqual(string.Empty, vm.ThreadSections[0].Id);
        Assert.IsTrue(vm.ThreadSections[0].Name is "All sections" or "すべてのセクション");
        Assert.AreEqual("section-inbox", vm.ThreadSections[1].Id);

        int priorListCalls = bridge.ListThreadsCallCount;
        vm.SelectedThreadSectionId = "section-inbox";
        await WaitUntilAsync(() => bridge.LastListThreadsRequest?.SectionId == "section-inbox");
        Assert.IsTrue(bridge.ListThreadsCallCount > priorListCalls);
        int priorSections = vm.ThreadSections.Count;
        await bridge.RaiseThreadHistoryChangedAsync(new WorkerNotification<ThreadHistoryChangedEvent>
        {
            Value = new ThreadHistoryChangedEvent { ThreadId = "thread-inbox", SectionId = "section-inbox", ChangeKind = "thread/section/changed" },
            StatePartitionFingerprint = "history-sections",
            OwnerGeneration = 1,
            ConnectionGeneration = 1,
        });
        Assert.AreEqual(priorSections, vm.ThreadSections.Count);
        Assert.AreEqual("section-inbox", bridge.LastListThreadsRequest?.SectionId);
    }

    [TestMethod]
    public async Task DeleteAndRevertCommandsAreBlockedForAnActiveTurn()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        WorkerStatus status = OwnerStatus("history-active-turn", 1, 1);
        status.State = WorkerConnectionState.Busy;
        status.ThreadId = "thread-active";
        status.TurnId = "turn-active";
        await bridge.PublishStateAsync(status);
        vm.SelectedThread = new ThreadSummary { Id = "thread-active", Preview = "Active conversation" };
        typeof(ChatViewModel).GetProperty(nameof(ChatViewModel.IsThreadJoined))!
            .GetSetMethod(nonPublic: true)!.Invoke(vm, [true]);
        vm.ThreadTurns.Add(new ThreadTurnSummary { Id = "turn-before", Status = "completed" });
        vm.SelectedRevertTurnId = "turn-before";

        await RunCommandAsync(vm.DeleteThreadCommand);
        await RunCommandAsync(vm.RevertThreadCommand);

        Assert.IsFalse(vm.IsThreadMutationConfirmationOpen);
        Assert.AreEqual(0, bridge.DeleteThreadCallCount);
        Assert.AreEqual(0, bridge.RevertThreadCallCount);
    }

    [TestMethod]
    public async Task SearchCompletionAndPartialsFromAnOldOwnerAreIgnored()
    {
        var completion = new TaskCompletionSource<ThreadSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003
        var bridge = new FakeWorkerBridge { SearchThreadsHandler = (_, _) => completion.Task };
#pragma warning restore VSTHRD003
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-stale-search", 1, 1));
        vm.HistorySearchText = "first";
        Task search = RunCommandAsync(vm.SearchHistoryCommand);
        await WaitUntilAsync(() => bridge.LastThreadSearchRequest is not null);
        ThreadSearchRequest request = bridge.LastThreadSearchRequest!;
        await bridge.PublishStateAsync(OwnerStatus("history-stale-search-next-owner", 2, 2));
        completion.SetResult(new ThreadSearchResult { Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "stale-owner", Preview = "Stale owner" }, Snippet = "stale" }] });
        await search;
        Assert.IsFalse(vm.ThreadRows.Any(row => row.Id == "stale-owner"));
        await bridge.RaiseThreadSearchPartialAsync(new WorkerNotification<ThreadSearchPartialEvent>
        {
            Value = new ThreadSearchPartialEvent { RequestId = request.RequestId!, Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "stale-partial" } }] },
            StatePartitionFingerprint = request.StatePartitionFingerprint,
            OwnerGeneration = request.OwnerGeneration,
            ConnectionGeneration = request.ConnectionGeneration,
        });
        Assert.IsFalse(vm.ThreadRows.Any(row => row.Id == "stale-partial"));
    }
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 100 && !predicate(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(predicate(), "The expected asynchronous UI state was not reached.");
    }
}
