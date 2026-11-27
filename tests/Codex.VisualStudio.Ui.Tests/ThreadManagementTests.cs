using System.IO;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

using System.Reflection;

namespace Codex.VisualStudio.Ui.Tests;

public sealed partial class ViewModelTests
{
    private static readonly string[] EligibleSearchWorkingDirectories = [@"C:\workspace"];
    private static readonly string[] SearchPageRowsWithPartial = ["first-page-final", "second-page-partial"];
    private static readonly string[] SearchPageRowsAfterFinal = ["first-page-final", "second-page-final"];
    private static readonly string[] SearchPageRowsAfterSecondPageFailure = ["first-page-final"];

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
        vm.SelectedThread!.Archived = true;
        await RunCommandAsync(vm.ArchiveThreadCommand);

        Assert.AreEqual(1, bridge.UnarchiveThreadCallCount);
        Assert.IsTrue(bridge.LastListThreadsRequest?.Archived ?? false);
    }

    [TestMethod]
    public async Task ThreadArchiveActionTextTracksSelectedConversationArchiveState()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-archive-label", 1, 1));

        vm.SelectedThread = new ThreadSummary { Id = "active-thread", Archived = false };
        string archiveText = vm.ThreadArchiveActionText;
        vm.SelectedThread = new ThreadSummary { Id = "archived-thread", Archived = true };

        Assert.AreNotEqual(archiveText, vm.ThreadArchiveActionText,
            "Changing the selected conversation must notify bindings when the archive action changes.");
        Assert.IsTrue(vm.ThreadArchiveActionText.Contains("Unarchive", StringComparison.Ordinal)
            || vm.ThreadArchiveActionText.Contains("アーカイブ解除", StringComparison.Ordinal));
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
        vm.SelectedThread!.Archived = true;
        await RunCommandAsync(vm.ArchiveThreadCommand);

        Assert.AreEqual("thread-archive-failure", vm.SelectedThread?.Id);
        Assert.IsTrue(vm.ShowArchivedThreads);
        StringAssert.Contains(vm.ThreadManagementStatusText, "Unarchive rejected by server.");
        Assert.AreEqual(1, bridge.ArchiveThreadCallCount);
        Assert.AreEqual(1, bridge.UnarchiveThreadCallCount);
    }

    [TestMethod]
    public async Task MutationRemoteErrorsUseKnownRejectionsAsDefiniteFailures()
    {
        foreach ((int errorCode, bool expectedUnknown) in new[]
        {
            (WorkerErrorCodes.PreDispatchRejected, false),
            (WorkerErrorCodes.ConnectionOperationRejected, false),
            (WorkerErrorCodes.UpstreamOperationFailed, true),
            (-32055, true),
        })
        {
            var bridge = new FakeWorkerBridge
            {
                ArchiveThreadHandler = (_, _) => Task.FromException<ThreadMutationResult>(
                    new StreamJsonRpc.RemoteInvocationException("App server returned a mutation error.", errorCode, errorData: null)),
            };
            using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
            await bridge.PublishStateAsync(OwnerStatus($"history-mutation-error-{errorCode}", 1, 1));
            vm.SelectedThread = new ThreadSummary { Id = "thread-mutation-error" };

            await RunCommandAsync(vm.ArchiveThreadCommand);

            bool reportedUnknown = vm.ThreadManagementStatusText.Contains("outcome is unknown", StringComparison.Ordinal)
                || vm.ThreadManagementStatusText.Contains("結果を確認できません", StringComparison.Ordinal);
            Assert.AreEqual(expectedUnknown, reportedUnknown, $"Unexpected classification for Worker error code {errorCode}.");
            if (!expectedUnknown)
            {
                StringAssert.Contains(vm.ThreadManagementStatusText, "App server returned a mutation error.");
            }
        }
    }

    [TestMethod]
    public async Task RevertMutationUsesKnownRejectionsAsDefiniteFailures()
    {
        foreach ((int errorCode, bool expectedUnknown) in new[]
        {
            (WorkerErrorCodes.PreDispatchRejected, false),
            (WorkerErrorCodes.ConnectionOperationRejected, false),
            (WorkerErrorCodes.UpstreamOperationFailed, true),
            (-32055, true),
        })
        {
            var bridge = new FakeWorkerBridge
            {
                RevertThreadHandler = (_, _) => Task.FromException<ThreadMutationResult>(
                    new StreamJsonRpc.RemoteInvocationException("App server returned a revert error.", errorCode, errorData: null)),
            };
            using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
            await bridge.PublishStateAsync(OwnerStatus($"history-revert-error-{errorCode}", 1, 1));
            vm.SelectedThread = new ThreadSummary { Id = "thread-revert-error" };
            typeof(ChatViewModel).GetProperty(nameof(ChatViewModel.IsThreadJoined))!
                .GetSetMethod(nonPublic: true)!.Invoke(vm, [true]);
            vm.ThreadTurns.Add(new ThreadTurnOptionViewModel("turn-before", "Turn before · completed"));
            vm.SelectedRevertTurnId = "turn-before";

            await RunCommandAsync(vm.RevertThreadCommand);
            await RunCommandAsync(vm.ConfirmThreadMutationCommand);

            bool reportedUnknown = vm.ThreadManagementStatusText.Contains("outcome is unknown", StringComparison.Ordinal)
                || vm.ThreadManagementStatusText.Contains("結果を確認できません", StringComparison.Ordinal);
            Assert.AreEqual(expectedUnknown, reportedUnknown, $"Unexpected classification for Worker error code {errorCode}.");
            if (!expectedUnknown)
            {
                StringAssert.Contains(vm.ThreadManagementStatusText, "App server returned a revert error.");
            }
        }
    }

    [TestMethod]
    public async Task DeleteMutationUsesKnownRejectionsAsDefiniteFailures()
    {
        foreach ((int errorCode, bool expectedUnknown) in new[]
        {
            (WorkerErrorCodes.PreDispatchRejected, false),
            (WorkerErrorCodes.ConnectionOperationRejected, false),
            (WorkerErrorCodes.UpstreamOperationFailed, true),
            (-32055, true),
        })
        {
            var bridge = new FakeWorkerBridge
            {
                DeleteThreadHandler = (_, _) => Task.FromException<ThreadMutationResult>(
                    new StreamJsonRpc.RemoteInvocationException("Remote mutation error.", errorCode, errorData: null)),
            };
            using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
            await bridge.PublishStateAsync(OwnerStatus($"history-delete-error-{errorCode}", 1, 1));
            await vm.RefreshCompleteLinkedForkIndexAsync(vm.CaptureOwnerSnapshot(), CancellationToken.None);
            vm.SelectedThread = new ThreadSummary { Id = "thread-delete-error", Preview = "Delete error" };

            await RunCommandAsync(vm.DeleteThreadCommand);
            await RunCommandAsync(vm.ConfirmThreadMutationCommand);

            bool reportedUnknown = vm.ThreadManagementStatusText.Contains("outcome is unknown", StringComparison.Ordinal)
                || vm.ThreadManagementStatusText.Contains("結果を確認できません", StringComparison.Ordinal);
            Assert.AreEqual(expectedUnknown, reportedUnknown, $"Unexpected classification for Worker error code {errorCode}.");
        }
    }

    [TestMethod]
    public async Task DeleteConversationRequiresNamedConfirmationAndBlocksLinkedParent()
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
        Assert.AreEqual("Parent conversation", parent.Preview);
        vm.SelectedThread = new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent" };
        Assert.IsFalse(vm.CanDeleteSelectedThread, "A linked child cannot be deleted through history management.");
        vm.SelectedThread = new ThreadSummary { Id = "thread-parent", Preview = "Parent conversation" };
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
        vm.SelectedThread = new ThreadSummary { Id = "thread-parent", Preview = "Parent conversation" };
        await RunCommandAsync(vm.DeleteThreadCommand);
        Assert.IsTrue(vm.IsThreadMutationConfirmationOpen);
        Assert.IsFalse(string.IsNullOrWhiteSpace(vm.ThreadMutationConfirmationText));
        StringAssert.Contains(vm.ThreadMutationConfirmationText, "Parent conversation");
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
    public async Task ThreadManagementOwnerRefreshRunsWhenNewOwnerReachesReady()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-owner-before-connect", 1, 1));
        int sectionReadsBeforeReconnect = bridge.ListThreadSectionsCallCount;
        Assert.IsTrue(sectionReadsBeforeReconnect > 0);

        WorkerStatus connecting = OwnerStatus("history-owner-after-connect", 2, 2);
        connecting.State = WorkerConnectionState.Connecting;
        await bridge.PublishStateAsync(connecting);
        Assert.AreEqual(sectionReadsBeforeReconnect, bridge.ListThreadSectionsCallCount,
            "Owner-scoped metadata should wait until the replacement worker is ready.");

        await bridge.PublishStateAsync(OwnerStatus("history-owner-after-connect", 2, 2));

        Assert.AreEqual(sectionReadsBeforeReconnect + 1, bridge.ListThreadSectionsCallCount,
            "The pending refresh must run when Ready arrives for the same owner observed while Connecting.");
    }

    [TestMethod]
    public async Task BusyThreadListRefreshFromRetiredOwnerQueuesAgainstCurrentOwner()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-busy-old-owner", 1, 1));
        object oldOwner = typeof(ChatViewModel).GetMethod("CaptureOwnerSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null)!;
        await bridge.PublishStateAsync(OwnerStatus("history-busy-current-owner", 2, 2));
        var replacementOwnerListStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacementOwnerList = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.ListThreadsRequestHandler = (request, _) => request.OwnerGeneration == 2
            ? WaitForReplacementOwnerListAsync()
            : Task.FromResult(new ThreadPage());

        async Task<ThreadPage> WaitForReplacementOwnerListAsync()
        {
            replacementOwnerListStarted.TrySetResult();
#pragma warning disable VSTHRD003
            return await releaseReplacementOwnerList.Task;
#pragma warning restore VSTHRD003
        }

        MethodInfo captureOwnerMethod = typeof(ChatViewModel).GetMethod("CaptureOwnerSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo refreshMethod = typeof(ChatViewModel).GetMethod("RefreshCompleteThreadListAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object replacementOwner = captureOwnerMethod.Invoke(vm, null)!;
        Task replacementOwnerRefresh = (Task)refreshMethod.Invoke(vm, [replacementOwner, "Replacement owner refresh", CancellationToken.None])!;
        await replacementOwnerListStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task staleOwnerRefresh = (Task)refreshMethod.Invoke(vm, [oldOwner, "Stale owner refresh", CancellationToken.None])!;
#pragma warning disable VSTHRD003
        await staleOwnerRefresh.WaitAsync(TimeSpan.FromSeconds(10));
#pragma warning restore VSTHRD003
        Assert.AreEqual("Busy", staleOwnerRefresh.GetType().GetProperty("Result")!.GetValue(staleOwnerRefresh)?.ToString());

        await bridge.PublishStateAsync(OwnerStatus("history-busy-latest-owner", 3, 3));
        object currentOwner = captureOwnerMethod.Invoke(vm, null)!;
        var currentOwnerListStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCurrentOwnerList = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int currentOwnerListCalls = 0;
        bridge.ListThreadsRequestHandler = async (request, _) =>
        {
            if (request.OwnerGeneration == 3 && Interlocked.Increment(ref currentOwnerListCalls) == 1)
            {
                currentOwnerListStarted.TrySetResult();
#pragma warning disable VSTHRD003
                return await releaseCurrentOwnerList.Task;
#pragma warning restore VSTHRD003
            }

            return new ThreadPage { Threads = [new ThreadSummary { Id = "current-owner-thread" }] };
        };
        Task currentOwnerRefresh = (Task)refreshMethod.Invoke(vm, [currentOwner, "Current owner refresh", CancellationToken.None])!;
        await currentOwnerListStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        MethodInfo queueMethod = typeof(ChatViewModel).GetMethod("QueueThreadListRefreshIfBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool queued = (bool)queueMethod.Invoke(vm, [oldOwner])!;
        releaseCurrentOwnerList.TrySetResult(new ThreadPage { Threads = [new ThreadSummary { Id = "current-owner-thread" }] });
#pragma warning disable VSTHRD003
        await currentOwnerRefresh.WaitAsync(TimeSpan.FromSeconds(10));
        releaseReplacementOwnerList.TrySetResult(new ThreadPage { Threads = [new ThreadSummary { Id = "stale-owner-thread" }] });
        await replacementOwnerRefresh.WaitAsync(TimeSpan.FromSeconds(10));
#pragma warning restore VSTHRD003

        Assert.IsTrue(queued, "A stale owner request that already observed Busy must preserve a refresh behind the latest owner's active request.");
        Assert.AreEqual(2, currentOwnerListCalls,
            "The pending refresh must be drained by re-reading the newest owner after the active request completes.");
        Assert.AreEqual(1, vm.Threads.Count,
            "A response from the retired owner must not overwrite the current owner's conversation list.");
        Assert.AreEqual("current-owner-thread", vm.Threads[0].Id);
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
        int listCallsBeforeSearch = bridge.ListThreadsCallCount;

        await RunCommandAsync(vm.SearchHistoryCommand);

        Assert.AreEqual(1, bridge.SearchThreadsCallCount);
        Assert.IsTrue(bridge.ListThreadsCallCount > listCallsBeforeSearch);
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
        vm.HistorySearchText = "needle";

        await RunCommandAsync(vm.SearchHistoryCommand);

        Assert.AreEqual(1, bridge.SearchThreadsCallCount, "The search request should be sent exactly once before its failure is surfaced.");
        Assert.IsFalse(string.Equals("needle", bridge.LastListThreadsRequest?.SearchTerm, StringComparison.Ordinal), "Arbitrary search failures must not trigger title-filter fallback.");
        Assert.IsTrue(vm.ThreadManagementStatusText.Contains("Search failed", StringComparison.Ordinal)
            || vm.ThreadManagementStatusText.Contains("検索できません", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ThreadSearchPagesPastFilteredHitsAndUsesArchivedView()
    {
        var requests = new List<ThreadSearchRequest>();
        var bridge = new FakeWorkerBridge
        {
            ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
            {
                Threads = [new ThreadSummary { Id = "eligible-match" }],
            }),
            SearchThreadsHandler = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(request.Cursor is null
                    ? new ThreadSearchResult
                    {
                        Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "filtered-out" }, Snippet = "hidden" }],
                        NextCursor = "second-page",
                    }
                    : new ThreadSearchResult
                    {
                        Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "eligible-match" }, Snippet = "visible" }],
                    });
            },
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-search-paging", 1, 1));
        typeof(ChatViewModel).GetField("showArchivedThreads", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
        typeof(ChatViewModel).GetField("selectedThreadSectionId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, "section-allowed");
        vm.HistorySearchText = "needle";

        await RunCommandAsync(vm.SearchHistoryCommand);

        Assert.AreEqual(2, requests.Count, "Search must continue past a filtered-out first page.");
        Assert.IsTrue(requests.All(static request => request.Archived));
        Assert.IsNull(requests[0].Cursor);
        Assert.AreEqual("second-page", requests[1].Cursor);
        Assert.AreEqual(2, requests.Select(static request => request.RequestId).Distinct(StringComparer.Ordinal).Count(),
            "Each paged app-server search needs a unique correlation request ID.");
        Assert.AreEqual("eligible-match", vm.ThreadRows.Single().Id);
        Assert.AreEqual("visible", vm.ThreadRows.Single().SearchSnippet);
        Assert.IsTrue(vm.HistoryStatusText.Contains("Showing search results", StringComparison.Ordinal)
            || vm.HistoryStatusText.Contains("検索結果を表示しています", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ThreadSearch_CancellationDuringForkIndexRefreshDoesNotUseDisposedSource()
    {
        var firstListReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstListRead = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        Task ownerRefresh = bridge.PublishStateAsync(OwnerStatus("history-search-cancel", 1, 1));
        await firstListReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.HistorySearchText = "needle";

        Task firstSearch = vm.SearchHistoryAsync();
        vm.ShowArchivedThreads = true;
        releaseFirstListRead.TrySetResult(new ThreadPage());

#pragma warning disable VSTHRD003
        await firstSearch;
        await ownerRefresh;
#pragma warning restore VSTHRD003
        await WaitUntilAsync(() => Volatile.Read(ref searchCalls) > 0
            && (vm.HistoryStatusText.Contains("Showing search results", StringComparison.Ordinal)
                || vm.HistoryStatusText.Contains("検索結果を表示しています", StringComparison.Ordinal)));
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
        ChatViewModel.OwnerSnapshot owner = vm.CaptureOwnerSnapshot();
        Task<HashSet<string>?> load = vm.LoadEligibleSearchThreadIdsAsync(owner, EligibleSearchWorkingDirectories, CancellationToken.None);
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
        vm.ThreadTurns.Add(new ThreadTurnOptionViewModel("turn-before", "Turn turn-bef · completed"));
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
    public void ThreadTurnDisplayLabelsDistinguishIdsWithSameSortablePrefix()
    {
        using var vm = new ChatViewModel(new FakeWorkerBridge(), autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        var createLabel = typeof(ChatViewModel).GetMethod("CreateThreadTurnDisplayLabel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string firstLabel = (string)createLabel.Invoke(vm, [new ThreadTurnSummary
        {
            Id = "019a1b2c-0000-7000-8000-000000000001",
            Status = "completed",
        }])!;
        string secondLabel = (string)createLabel.Invoke(vm, [new ThreadTurnSummary
        {
            Id = "019a1b2c-0000-7000-8000-000000000002",
            Status = "completed",
        }])!;

        Assert.AreNotEqual(firstLabel, secondLabel,
            "Turn choices used by revert must remain distinguishable when sortable IDs share their prefix.");
        StringAssert.Contains(firstLabel, "00000001");
        StringAssert.Contains(secondLabel, "00000002");
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
#pragma warning disable VSTHRD003
        await search;
#pragma warning restore VSTHRD003

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
    public async Task ThreadSearch_SecondPagePartialHitsAreReplacedByThatPagesFinalResults()
    {
        var secondPageStarted = new TaskCompletionSource<ThreadSearchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPageCompletion = new TaskCompletionSource<ThreadSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<ThreadSearchRequest>();
        var bridge = new FakeWorkerBridge
        {
            SearchThreadsHandler = (request, _) =>
            {
                requests.Add(request);
                if (request.Cursor is null)
                {
                    return Task.FromResult(new ThreadSearchResult
                    {
                        Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "first-page-final" }, Snippet = "first final" }],
                        NextCursor = "second-page",
                    });
                }

                secondPageStarted.TrySetResult(request);
#pragma warning disable VSTHRD003
                return secondPageCompletion.Task;
#pragma warning restore VSTHRD003
            },
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-search-page-partials", 1, 1));
        vm.HistorySearchText = "needle";
        Task search = RunCommandAsync(vm.SearchHistoryCommand);
        ThreadSearchRequest secondRequest = await secondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await bridge.RaiseThreadSearchPartialAsync(new WorkerNotification<ThreadSearchPartialEvent>
        {
            Value = new ThreadSearchPartialEvent
            {
                RequestId = secondRequest.RequestId!,
                Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "second-page-partial" }, Snippet = "provisional" }],
            },
            StatePartitionFingerprint = secondRequest.StatePartitionFingerprint,
            OwnerGeneration = secondRequest.OwnerGeneration,
            ConnectionGeneration = secondRequest.ConnectionGeneration,
        });
        CollectionAssert.AreEqual(SearchPageRowsWithPartial, vm.ThreadRows.Select(static row => row.Id).ToArray());

        secondPageCompletion.SetResult(new ThreadSearchResult
        {
            Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "second-page-final" }, Snippet = "second final" }],
        });
#pragma warning disable VSTHRD003
        await search;
#pragma warning restore VSTHRD003

        Assert.AreEqual(2, requests.Select(static request => request.RequestId).Distinct(StringComparer.Ordinal).Count());
        CollectionAssert.AreEqual(SearchPageRowsAfterFinal, vm.ThreadRows.Select(static row => row.Id).ToArray());
        Assert.AreEqual("second final", vm.ThreadRows.Single(static row => row.Id == "second-page-final").SearchSnippet);
    }

    [TestMethod]
    public async Task ThreadSearch_FailedPageDoesNotLeaveItsPartialHitsInTheResults()
    {
        var secondPageStarted = new TaskCompletionSource<ThreadSearchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPageCompletion = new TaskCompletionSource<ThreadSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            SearchThreadsHandler = (request, _) =>
            {
                if (request.Cursor is null)
                {
                    return Task.FromResult(new ThreadSearchResult
                    {
                        Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "first-page-final" }, Snippet = "first final" }],
                        NextCursor = "second-page",
                    });
                }

                secondPageStarted.TrySetResult(request);
#pragma warning disable VSTHRD003
                return secondPageCompletion.Task;
#pragma warning restore VSTHRD003
            },
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-search-failed-partial", 1, 1));
        vm.HistorySearchText = "needle";
        Task search = RunCommandAsync(vm.SearchHistoryCommand);
        ThreadSearchRequest secondRequest = await secondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await bridge.RaiseThreadSearchPartialAsync(new WorkerNotification<ThreadSearchPartialEvent>
        {
            Value = new ThreadSearchPartialEvent
            {
                RequestId = secondRequest.RequestId!,
                Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "second-page-partial" }, Snippet = "provisional" }],
            },
            StatePartitionFingerprint = secondRequest.StatePartitionFingerprint,
            OwnerGeneration = secondRequest.OwnerGeneration,
            ConnectionGeneration = secondRequest.ConnectionGeneration,
        });
        Assert.AreEqual("second-page-partial", vm.ThreadRows.Last().Id);

        secondPageCompletion.SetException(new IOException("search transport failed"));
#pragma warning disable VSTHRD003
        await search;
#pragma warning restore VSTHRD003

        CollectionAssert.AreEqual(SearchPageRowsAfterSecondPageFailure, vm.ThreadRows.Select(static row => row.Id).ToArray());
        Assert.IsTrue(vm.ThreadManagementStatusText.Contains("Search failed", StringComparison.Ordinal)
            || vm.ThreadManagementStatusText.Contains("検索できません", StringComparison.Ordinal));
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
        ChatViewModel.OwnerSnapshot owner = vm.CaptureOwnerSnapshot();
        Task<HashSet<string>?> load = vm.LoadEligibleSearchThreadIdsAsync(owner, EligibleSearchWorkingDirectories, CancellationToken.None);
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

        int priorListCallsBeforeSectionSelection = bridge.ListThreadsCallCount;
        vm.SelectedThreadSectionId = "section-inbox";
        await WaitUntilAsync(() => bridge.LastListThreadsRequest?.SectionId == "section-inbox");
        Assert.IsTrue(bridge.ListThreadsCallCount > priorListCallsBeforeSectionSelection);
        int priorSectionsCalls = bridge.ListThreadSectionsCallCount;
        int priorListCalls = bridge.ListThreadsCallCount;
        bridge.ThreadSectionsResult = new ThreadSectionsResult
        {
            Sections =
            [
                new ThreadSectionInfo { Id = "section-inbox", Name = "Renamed inbox" },
                new ThreadSectionInfo { Id = "section-done", Name = "Done" },
            ],
        };
        await bridge.RaiseThreadHistoryChangedAsync(new WorkerNotification<ThreadHistoryChangedEvent>
        {
            Value = new ThreadHistoryChangedEvent { ThreadId = "thread-inbox", SectionId = "section-inbox", ChangeKind = "thread/section/changed" },
            StatePartitionFingerprint = "history-sections",
            OwnerGeneration = 1,
            ConnectionGeneration = 1,
        });
        await WaitUntilAsync(() => bridge.ListThreadSectionsCallCount > priorSectionsCalls
            && bridge.ListThreadsCallCount > priorListCalls);
        Assert.AreEqual("Renamed inbox", vm.ThreadSections.Single(section => section.Id == "section-inbox").Name);
        Assert.AreEqual(3, vm.ThreadSections.Count);
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
        vm.ThreadTurns.Add(new ThreadTurnOptionViewModel("turn-before", "Turn turn-bef · completed"));
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

    [TestMethod]
    public async Task SearchResultWithMismatchedOwnerScopeIsDropped()
    {
        var bridge = new FakeWorkerBridge
        {
            PreserveThreadManagementResultOwner = true,
            SearchThreadsHandler = (_, _) => Task.FromResult(new ThreadSearchResult
            {
                Results = [new ThreadSearchHit { Thread = new ThreadSummary { Id = "wrong-owner-hit", Preview = "Wrong owner" } }],
                StatePartitionFingerprint = "different-partition",
                OwnerGeneration = 90,
                ConnectionGeneration = 91,
            }),
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-owner-result", 1, 1));
        vm.HistorySearchText = "needle";

        await RunCommandAsync(vm.SearchHistoryCommand);

        Assert.AreEqual(1, bridge.SearchThreadsCallCount);
        Assert.IsFalse(vm.ThreadRows.Any(row => row.Id == "wrong-owner-hit"));
    }

    [TestMethod]
    public async Task SectionChangeDoesNotClearTheSelectedConversation()
    {
        var bridge = new FakeWorkerBridge();
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-section-selection", 1, 1));
        vm.SelectedThread = new ThreadSummary { Id = "thread-selected", Preview = "Selected conversation" };

        await bridge.RaiseThreadHistoryChangedAsync(new WorkerNotification<ThreadHistoryChangedEvent>
        {
            Value = new ThreadHistoryChangedEvent
            {
                ThreadId = "thread-selected",
                SectionId = "section-inbox",
                ChangeKind = "thread/section/changed",
            },
            StatePartitionFingerprint = "history-section-selection",
            OwnerGeneration = 1,
            ConnectionGeneration = 1,
        });

        Assert.AreEqual("thread-selected", vm.SelectedThread?.Id);
    }

    [TestMethod]
    public async Task StaleUnsupportedSearchCannotEnableFallbackForReplacementSearch()
    {
        var firstSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSearch = new TaskCompletionSource<ThreadSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int searchCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            SearchThreadsHandler = async (_, _) =>
            {
                if (Interlocked.Increment(ref searchCalls) == 1)
                {
                    firstSearchStarted.TrySetResult();
#pragma warning disable VSTHRD003
                    return await releaseFirstSearch.Task;
#pragma warning restore VSTHRD003
                }

                return new ThreadSearchResult();
            },
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        await bridge.PublishStateAsync(OwnerStatus("history-replacement-search", 1, 1));
        vm.HistorySearchText = "first term";
        Task firstSearch = vm.SearchHistoryAsync();
        await firstSearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        vm.HistorySearchText = "replacement term";
        await RunCommandAsync(vm.SearchHistoryCommand);
        releaseFirstSearch.TrySetResult(new ThreadSearchResult { IsSupported = false });
#pragma warning disable VSTHRD003
        await firstSearch;
#pragma warning restore VSTHRD003

        Assert.IsFalse(bridge.ThreadListRequests.Any(request => request.SearchTerm is "first term" or "replacement term"),
            "Neither the canceled search nor its replacement should issue a title-filter fallback.");
    }

    [TestMethod]
    public async Task LinkedForkIndexIncludesAttachedThreadsWithoutPerThreadRequests()
    {
        var bridge = new FakeWorkerBridge
        {
            ListThreadsRequestHandler = (request, _) => Task.FromResult(new ThreadPage
            {
                Threads = request.Archived
                    ? []
                    : [
                        new ThreadSummary { Id = "thread-parent" },
                        new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent" },
                    ],
            }),
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        WorkerStatus status = OwnerStatus("history-attached-threads", 1, 1);
        status.FeatureTier = CodexFeatureTier.Baseline;

        await bridge.PublishStateAsync(status);

        Assert.AreEqual(2, bridge.ThreadListRequests.Count(static request => request.IncludeAttachedThreads),
            "Both archived and active full-history scans must ask for attached children.");
        Assert.AreEqual(0, bridge.ForkThreadRequests.Count,
            "A complete attached-thread scan must avoid one child-list request per conversation.");
        vm.SelectedThread = new ThreadSummary { Id = "thread-parent" };
        Assert.IsFalse(vm.CanDeleteSelectedThread, "The included child's parent relationship must block parent deletion.");
    }

    [TestMethod]
    public async Task LiveForkRelationshipWinsOverPagedSnapshotAndParentlessSummary()
    {
        var firstListReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstListRead = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int listCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListThreadsHandler = (_, _) =>
            {
                if (Interlocked.Increment(ref listCalls) == 1)
                {
#pragma warning disable VSTHRD003
                    firstListReadStarted.TrySetResult();
                    return releaseFirstListRead.Task;
#pragma warning restore VSTHRD003
                }

                return Task.FromResult(new ThreadPage
                {
                    Threads =
                    [
                        new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent-snapshot" },
                        new ThreadSummary { Id = "thread-control-child", LinkedParentThreadId = "thread-parent-snapshot" },
                    ],
                });
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        Task ownerRefresh = bridge.PublishStateAsync(OwnerStatus("history-link-race", 1, 1));
        await firstListReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task refresh = vm.RefreshCompleteLinkedForkIndexAsync(vm.CaptureOwnerSnapshot(), CancellationToken.None);
        Assert.IsFalse(refresh.IsCompleted, "The fork-index refresh must still be waiting on its first page.");
        vm.TrackLinkedForkRelationship(new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent-live" });
        vm.TrackLinkedForkRelationship(new ThreadSummary { Id = "thread-child" });
        releaseFirstListRead.TrySetResult(new ThreadPage
        {
            Threads =
            [
                new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent-snapshot" },
                new ThreadSummary { Id = "thread-control-child", LinkedParentThreadId = "thread-parent-snapshot" },
            ],
        });
#pragma warning disable VSTHRD003
        await refresh;
        await ownerRefresh;
#pragma warning restore VSTHRD003

        vm.SelectedThread = new ThreadSummary { Id = "thread-index-ready-parent" };
        await WaitUntilAsync(() => vm.CanDeleteSelectedThread);
        Assert.IsTrue(vm.CanDeleteSelectedThread, "The complete fork index must be ready before relationship assertions.");
        vm.SelectedThread = new ThreadSummary { Id = "thread-parent-snapshot" };
        Assert.IsFalse(vm.CanDeleteSelectedThread, "The unmodified control relationship from the paged snapshot must remain indexed.");
        vm.SelectedThread = new ThreadSummary { Id = "thread-parent-live" };
        Assert.IsFalse(vm.CanDeleteSelectedThread, "The live relationship must override the earlier paged snapshot, and a parentless summary must not erase it.");
    }

    [TestMethod]
    public async Task DeletedChildDuringIndexRefreshIsNotRestoredFromPagedSnapshot()
    {
        var firstListReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstListRead = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int listCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListThreadsHandler = (_, _) =>
            {
                if (Interlocked.Increment(ref listCalls) == 1)
                {
#pragma warning disable VSTHRD003
                    firstListReadStarted.TrySetResult();
                    return releaseFirstListRead.Task;
#pragma warning restore VSTHRD003
                }

                return Task.FromResult(new ThreadPage
                {
                    Threads =
                    [
                        new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent" },
                        new ThreadSummary { Id = "thread-control-child", LinkedParentThreadId = "thread-control-parent" },
                    ],
                });
            },
        };
        using var vm = new ChatViewModel(
            bridge,
            autoConnect: false,
            settingsStore: new MemorySettingsStore(new ExtensionSettings { ExperimentalApiEnabled = true }));
        Task ownerRefresh = bridge.PublishStateAsync(OwnerStatus("history-delete-index-race", 1, 1));
        await firstListReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task refresh = vm.RefreshCompleteLinkedForkIndexAsync(vm.CaptureOwnerSnapshot(), CancellationToken.None);
        Assert.IsFalse(refresh.IsCompleted, "The fork-index refresh must still be waiting on its first page.");
        await bridge.RaiseThreadHistoryChangedAsync(new WorkerNotification<ThreadHistoryChangedEvent>
        {
            Value = new ThreadHistoryChangedEvent { ThreadId = "thread-child", ChangeKind = "thread/deleted" },
            StatePartitionFingerprint = "history-delete-index-race",
            OwnerGeneration = 1,
            ConnectionGeneration = 1,
        });
        releaseFirstListRead.TrySetResult(new ThreadPage
        {
            Threads =
            [
                new ThreadSummary { Id = "thread-child", LinkedParentThreadId = "thread-parent" },
                new ThreadSummary { Id = "thread-control-child", LinkedParentThreadId = "thread-control-parent" },
            ],
        });
#pragma warning disable VSTHRD003
        await refresh;
        await ownerRefresh;
#pragma warning restore VSTHRD003

        vm.SelectedThread = new ThreadSummary { Id = "thread-index-ready-parent" };
        await WaitUntilAsync(() => vm.CanDeleteSelectedThread);
        Assert.IsTrue(vm.CanDeleteSelectedThread, "The complete fork index must be ready before relationship assertions.");
        vm.SelectedThread = new ThreadSummary { Id = "thread-control-parent" };
        Assert.IsFalse(vm.CanDeleteSelectedThread, "The unaffected control relationship from the paged snapshot must remain indexed.");
        vm.SelectedThread = new ThreadSummary { Id = "thread-parent", Preview = "Parent conversation" };
        Assert.IsTrue(vm.CanDeleteSelectedThread, "A deletion notification received during paging must override a stale child summary.");
    }

    [TestMethod]
    public async Task DeleteCompletionDoesNotClearAConversationSelectedWhileTheRequestWasPending()
    {
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource<ThreadMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            ListThreadsHandler = (_, _) => Task.FromResult(new ThreadPage
            {
                Threads = [new ThreadSummary { Id = "thread-other", Preview = "Other conversation" }],
            }),
            DeleteThreadHandler = async (_, _) =>
            {
                deleteStarted.TrySetResult();
#pragma warning disable VSTHRD003
                return await releaseDelete.Task;
#pragma warning restore VSTHRD003
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()));
        await bridge.PublishStateAsync(OwnerStatus("history-selection-race", 1, 1));
        Task refresh = vm.RefreshCompleteLinkedForkIndexAsync(vm.CaptureOwnerSnapshot(), CancellationToken.None);
#pragma warning disable VSTHRD003
        await refresh;
#pragma warning restore VSTHRD003
        vm.SelectedThread = new ThreadSummary { Id = "thread-delete-target", Preview = "Delete target" };
        await RunCommandAsync(vm.DeleteThreadCommand);
        Task delete = RunCommandAsync(vm.ConfirmThreadMutationCommand);
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.SelectedThread = new ThreadSummary { Id = "thread-other", Preview = "Other conversation" };
        releaseDelete.TrySetResult(new ThreadMutationResult { Succeeded = true });
#pragma warning disable VSTHRD003
        await delete;
#pragma warning restore VSTHRD003

        Assert.AreEqual("thread-other", vm.SelectedThread?.Id);
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
