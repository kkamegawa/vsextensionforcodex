using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;
using ReentrantSemaphore = Microsoft.VisualStudio.Threading.ReentrantSemaphore;

namespace Codex.VisualStudio.Extension;

/// <summary>Presentation state and commands for the conversation history management surface.</summary>
public sealed partial class ChatViewModel
{
    private static readonly bool[] ThreadArchiveStates = [false, true];
    private bool showArchivedThreads;
    private string historySearchText = string.Empty;
    private string? selectedThreadSectionId;
    private string? selectedRevertTurnId;
    private string? pendingThreadMutation;
    private string? pendingMutationThreadId;
    private string? pendingMutationThreadTitle;
    private string? pendingMutationBeforeTurnId;
    private string? activeThreadSearchRequestId;
    private HashSet<string>? activeSearchEligibleThreads;
    private readonly List<ThreadSearchHit> accumulatedHistorySearchHits = [];
    private readonly HashSet<string> accumulatedHistorySearchIds = new(StringComparer.Ordinal);
    private IReadOnlyList<ThreadSearchHit> activeHistorySearchPartialHits = Array.Empty<ThreadSearchHit>();
    private readonly Dictionary<string, string> linkedForkParentByChildId = new(StringComparer.Ordinal);
    private readonly object linkedForkIndexStateGate = new();
    private readonly ReentrantSemaphore linkedForkIndexRefreshGate = ExtensionConcurrency.CreateSemaphore();
    private Dictionary<string, string?>? linkedForkIndexUpdatesDuringRefresh;
    private OwnerSnapshot? linkedForkIndexOwner;
    private OwnerSnapshot? threadManagementOwner;
    private bool linkedForkIndexComplete;
    private bool threadManagementOwnerDataRefreshPending;
    private OwnerSnapshot? pendingMutationOwner;
    private bool useThreadListTitleSearchFallback;
    private string historyManagementStatusText = string.Empty;
    private long historyManagementGeneration;
    private CancellationTokenSource? historySearchCancellation;
    private readonly object historySearchCancellationGate = new();
    private IReadOnlyList<ThreadSectionInfo> threadSections = Array.Empty<ThreadSectionInfo>();
    private readonly bool isJapaneseUi = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase);

    [DataMember]
    public ObservableCollection<ThreadSectionOption> ThreadSections { get; } = [];

    [DataMember]
    public bool ShowArchivedThreads
    {
        get => showArchivedThreads;
        set
        {
            if (SetProperty(ref showArchivedThreads, value))
            {
                OnPropertyChanged(nameof(ThreadArchiveActionText));
                _ = RefreshManagedThreadListAsync();
            }
        }
    }

    [DataMember]
    public string HistorySearchText
    {
        get => historySearchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetProperty(ref historySearchText, normalized))
            {
                useThreadListTitleSearchFallback = false;
                SearchHistoryCommand.RaiseCanExecuteChanged();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    CancelHistorySearch();
                    _ = RefreshManagedThreadListAsync();
                }
            }
        }
    }

    [DataMember]
    public string? SelectedThreadSectionId
    {
        get => selectedThreadSectionId;
        set
        {
            if (SetProperty(ref selectedThreadSectionId, value))
            {
                _ = RefreshManagedThreadListAsync();
            }
        }
    }

    [DataMember]
    public string? SelectedRevertTurnId
    {
        get => selectedRevertTurnId;
        set
        {
            if (SetProperty(ref selectedRevertTurnId, value))
            {
                RevertThreadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public bool IsThreadMutationConfirmationOpen => pendingThreadMutation is not null;

    [DataMember]
    public string ThreadMutationConfirmationText => pendingThreadMutation switch
    {
        "delete" => IsJapaneseUi
            ? $"「{pendingMutationThreadTitle ?? SafeThreadTitle(pendingMutationThreadId)}」を完全に削除します。この操作は取り消せません。ローカルファイルの変更は保持されます。"
            : $"Permanently delete “{pendingMutationThreadTitle ?? SafeThreadTitle(pendingMutationThreadId)}”? This cannot be undone. Local file changes are preserved.",
        "revert" => IsJapaneseUi
            ? $"「{pendingMutationThreadTitle ?? SafeThreadTitle(pendingMutationThreadId)}」の選択したターンとそれ以降を会話履歴から削除します。ローカルファイルの変更は元に戻りません。"
            : $"Revert “{pendingMutationThreadTitle ?? SafeThreadTitle(pendingMutationThreadId)}” to before the selected turn? This turn and every later turn will be removed from conversation history. Local file changes will not be reverted.",
        _ => string.Empty,
    };

    [DataMember]
    public string ThreadManagementStatusText => markdown.ToSafeText(historyManagementStatusText);

    [DataMember]
    public string ThreadArchiveActionText => SelectedThread?.Archived == true
        ? (IsJapaneseUi ? "アーカイブ解除" : "Unarchive")
        : (IsJapaneseUi ? "アーカイブ" : "Archive");

    [DataMember] public string ArchivedToggleText => IsJapaneseUi ? "アーカイブ済みの会話を表示" : "Show archived conversations";
    [DataMember] public string HistorySearchButtonText => IsJapaneseUi ? "検索" : "Search";
    [DataMember] public string HistorySearchToolTipText => IsJapaneseUi ? "会話のタイトルとメッセージを検索" : "Search conversation titles and messages";
    [DataMember] public string SectionFilterToolTipText => IsJapaneseUi ? "セクションで会話を絞り込む" : "Filter conversations by section";
    [DataMember] public string ThreadMutationConfirmHeading => pendingThreadMutation switch { "delete" => IsJapaneseUi ? "会話の削除を確認" : "Confirm conversation deletion", "revert" => IsJapaneseUi ? "会話を戻すことを確認" : "Confirm conversation revert", _ => IsJapaneseUi ? "会話の変更を確認" : "Confirm conversation change" };
    [DataMember] public string ThreadMutationConfirmButtonText => pendingThreadMutation switch { "delete" => IsJapaneseUi ? "完全に削除" : "Delete permanently", "revert" => IsJapaneseUi ? "この位置に戻す" : "Revert to this turn", _ => IsJapaneseUi ? "確認" : "Confirm" };
    [DataMember] public string ThreadMutationCancelButtonText => IsJapaneseUi ? "キャンセル" : "Cancel";
    [DataMember] public string ThreadDeleteButtonText => IsJapaneseUi ? "会話を削除" : "Delete conversation";
    [DataMember] public string ThreadArchiveAutomationName => IsJapaneseUi ? "選択した会話をアーカイブまたは解除" : "Archive or unarchive selected conversation";
    [DataMember] public string ArchivedToggleAutomationName => ArchivedToggleText;
    [DataMember] public string ThreadSectionsAutomationName => IsJapaneseUi ? "会話のセクションで絞り込む" : "Filter conversations by section";
    [DataMember] public string HistorySearchAutomationName => IsJapaneseUi ? "会話履歴を検索" : "Search conversation history";
    [DataMember] public string HistorySearchButtonAutomationName => IsJapaneseUi ? "会話履歴を検索" : "Search conversation history";
    [DataMember] public string ThreadDeleteAutomationName => IsJapaneseUi ? "選択した会話を削除" : "Delete selected conversation";
    [DataMember] public string ThreadMutationConfirmAutomationName => pendingThreadMutation == "delete" ? (IsJapaneseUi ? "削除を確定" : "Confirm deletion") : (IsJapaneseUi ? "戻す操作を確定" : "Confirm revert");
    [DataMember] public string ThreadMutationCancelAutomationName => IsJapaneseUi ? "操作をキャンセル" : "Cancel conversation change";
    [DataMember] public string ThreadRevertButtonText => IsJapaneseUi ? "選択したターンの前に戻す" : "Revert before turn";
    [DataMember] public string ThreadRevertTurnSelectorToolTip => IsJapaneseUi ? "選択したターンとそれ以降を削除します" : "Remove the selected turn and all following turns";
    [DataMember] public string ThreadRevertTurnSelectorAutomationName => IsJapaneseUi ? "会話履歴を戻すターンを選択" : "Select the turn to revert the conversation to";
    [DataMember] public string ThreadRevertButtonAutomationName => IsJapaneseUi ? "選択したターンの前に会話を戻す" : "Revert conversation before selected turn";

    [DataMember]
    public bool HasThreadManagementStatus => !string.IsNullOrWhiteSpace(historyManagementStatusText);

    [DataMember]
    public bool CanDeleteSelectedThread
    {
        get
        {
            lock (linkedForkIndexStateGate)
            {
                EnsureLinkedForkIndexOwner();
                return SelectedThread is { } thread
                    && !IsThreadRunning(thread.Id)
                    && linkedForkIndexComplete
                    && !HasLinkedChildren(thread.Id)
                    && !HasLinkedParent(thread.Id)
                    && Status.State == WorkerConnectionState.Ready;
            }
        }
    }

    [DataMember]
    public bool CanRevertSelectedThread => SelectedThread is { } thread
        && IsThreadJoined
        && Status.TurnId is null
        && ThreadTurns.Count > 0
        && !string.IsNullOrWhiteSpace(SelectedRevertTurnId)
        && Status.State == WorkerConnectionState.Ready;

    [DataMember]
    public string ThreadManagementDeleteUnavailableText => IsJapaneseUi
        ? "実行中の会話、リンクされたフォーク、または親会話があるフォークは削除できません。"
        : "A running conversation, a linked fork, or a parent with linked forks cannot be deleted.";

    [DataMember]
    public bool ShowThreadManagementDeleteUnavailable => SelectedThread is not null && !CanDeleteSelectedThread;

    [DataMember]
    public bool ShowThreadRevertControls => SelectedThread is not null && IsThreadJoined;

    [DataMember]
    public bool HasSelectedThread => SelectedThread is not null;

    private AsyncCommand? searchHistoryCommand;
    private AsyncCommand? archiveThreadCommand;
    private AsyncCommand? deleteThreadCommand;
    private AsyncCommand? revertThreadCommand;
    private AsyncCommand? confirmThreadMutationCommand;
    private AsyncCommand? cancelThreadMutationCommand;

    [DataMember] public AsyncCommand SearchHistoryCommand => searchHistoryCommand ??= new AsyncCommand(SearchHistoryAsync, () => !string.IsNullOrWhiteSpace(HistorySearchText));
    [DataMember] public AsyncCommand ArchiveThreadCommand => archiveThreadCommand ??= new AsyncCommand(ArchiveSelectedThreadAsync, () => SelectedThread is { } t && !IsThreadRunning(t.Id) && Status.State == WorkerConnectionState.Ready);
    [DataMember] public AsyncCommand DeleteThreadCommand => deleteThreadCommand ??= new AsyncCommand(RequestDeleteSelectedThreadAsync, () => CanDeleteSelectedThread);
    [DataMember] public AsyncCommand RevertThreadCommand => revertThreadCommand ??= new AsyncCommand(RequestRevertSelectedThreadAsync, () => CanRevertSelectedThread);
    [DataMember] public AsyncCommand ConfirmThreadMutationCommand => confirmThreadMutationCommand ??= new AsyncCommand(ConfirmThreadMutationAsync, () => IsThreadMutationConfirmationOpen);
    [DataMember] public AsyncCommand CancelThreadMutationCommand => cancelThreadMutationCommand ??= new AsyncCommand(CancelThreadMutationAsync, () => IsThreadMutationConfirmationOpen);

    private bool IsJapaneseUi => isJapaneseUi;

    private string SafeThreadTitle(string? threadId)
    {
        string? title = ThreadRows.FirstOrDefault(row => row.Id == threadId)?.Preview;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Threads.FirstOrDefault(thread => thread.Id == threadId)?.Preview;
        }
        if (string.IsNullOrWhiteSpace(title) && SelectedThread is { } selected && selected.Id == threadId)
        {
            title = selected.Preview;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            return IsJapaneseUi ? "タイトルなしの会話" : "Untitled conversation";
        }

        return SafeMarkdownService.ToSafeLiteralText(title);
    }

    private bool IsThreadRunning(string threadId)
        => Status.TurnId is not null && string.Equals(Status.ThreadId, threadId, StringComparison.Ordinal);

    private bool HasLinkedChildren(string threadId)
    {
        lock (linkedForkIndexStateGate)
        {
            EnsureLinkedForkIndexOwner();
            return linkedForkParentByChildId.Values.Contains(threadId, StringComparer.Ordinal);
        }
    }

    internal void TrackLinkedForkRelationship(ThreadSummary thread)
    {
        lock (linkedForkIndexStateGate)
        {
            EnsureLinkedForkIndexOwner();
            if (string.IsNullOrWhiteSpace(thread.LinkedParentThreadId))
            {
                // A summary may omit its parent relationship. Only an explicit deletion
                // notification can safely remove a previously observed fork link.
                return;
            }

            linkedForkParentByChildId[thread.Id] = thread.LinkedParentThreadId;
            if (linkedForkIndexUpdatesDuringRefresh is { } updates)
            {
                updates[thread.Id] = thread.LinkedParentThreadId;
            }
        }
    }

    private bool HasLinkedParent(string threadId)
    {
        lock (linkedForkIndexStateGate)
        {
            EnsureLinkedForkIndexOwner();
            return linkedForkParentByChildId.ContainsKey(threadId);
        }
    }

    private void EnsureLinkedForkIndexOwner()
    {
        lock (linkedForkIndexStateGate)
        {
            OwnerSnapshot owner = CaptureOwnerSnapshot();
            if (linkedForkIndexOwner is OwnerSnapshot current && current == owner)
            {
                return;
            }

            linkedForkParentByChildId.Clear();
            linkedForkIndexUpdatesDuringRefresh = null;
            linkedForkIndexComplete = false;
            linkedForkIndexOwner = owner;
        }
    }

    internal async Task RefreshCompleteLinkedForkIndexAsync(OwnerSnapshot owner, CancellationToken cancellationToken)
    {
        try
        {
            await linkedForkIndexRefreshGate.ExecuteAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                long historyGeneration = Interlocked.Read(ref historyManagementGeneration);
                bool indexComplete = false;
                bool isCurrent = false;
                bool includeAttachedThreads = false;
                Dictionary<string, string?>? updatesForThisRefresh = null;
                await OnUiAsync(() =>
                {
                    includeAttachedThreads = settings.ExperimentalApiEnabled
                        && Status.FeatureTier != CodexFeatureTier.Unsupported;
                    lock (linkedForkIndexStateGate)
                    {
                        EnsureLinkedForkIndexOwner();
                        isCurrent = IsCurrentOwner(owner)
                            && historyGeneration == Interlocked.Read(ref historyManagementGeneration);
                        indexComplete = isCurrent && linkedForkIndexComplete;
                        if (isCurrent && !indexComplete)
                        {
                            updatesForThisRefresh = new Dictionary<string, string?>(StringComparer.Ordinal);
                            linkedForkIndexUpdatesDuringRefresh = updatesForThisRefresh;
                        }
                    }
                }).ConfigureAwait(false);
                if (!isCurrent || indexComplete)
                {
                    return;
                }

                var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
                var allThreadIds = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    foreach (bool archived in ThreadArchiveStates)
                    {
                        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
                        string? cursor = null;
                        for (int pageNumber = 0; pageNumber < 100; pageNumber++)
                        {
                            ThreadPage page = await bridge.ListThreadsAsync(StampOwner(new ListThreadsRequest
                            {
                                Cursor = cursor,
                                Archived = archived,
                                IncludeAttachedThreads = includeAttachedThreads,
                            }, owner), cancellationToken).ConfigureAwait(false);
                            if (!IsCurrentOwner(owner) || historyGeneration != Interlocked.Read(ref historyManagementGeneration) || !MatchesOwner(page, owner))
                            {
                                return;
                            }

                            foreach (ThreadSummary thread in page.Threads)
                            {
                                allThreadIds.Add(thread.Id);
                                if (!string.IsNullOrWhiteSpace(thread.LinkedParentThreadId))
                                {
                                    relationships[thread.Id] = thread.LinkedParentThreadId;
                                }
                            }

                            cursor = page.NextCursor;
                            if (string.IsNullOrWhiteSpace(cursor))
                            {
                                break;
                            }

                            if (!seenCursors.Add(cursor) || pageNumber == 99)
                            {
                                throw new InvalidDataException("The complete thread list did not terminate within its paging bounds.");
                            }
                        }
                    }

                    if (!includeAttachedThreads)
                    {
                        await Parallel.ForEachAsync(
                            allThreadIds,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = 8,
                                CancellationToken = cancellationToken,
                            },
                            async (parentThreadId, token) =>
                            {
                                var seenCursors = new HashSet<string>(StringComparer.Ordinal);
                                var childrenForParent = new List<string>();
                                string? cursor = null;
                                for (int pageNumber = 0; pageNumber < 100; pageNumber++)
                                {
                                    ForkThreadResult page = await bridge.ForkThreadAsync(StampOwner(new ForkThreadRequest
                                    {
                                        ThreadId = parentThreadId,
                                        ListLinkedForks = true,
                                        Cursor = cursor,
                                    }, owner), token).ConfigureAwait(false);
                                    if (!IsCurrentOwner(owner)
                                        || historyGeneration != Interlocked.Read(ref historyManagementGeneration))
                                    {
                                        return;
                                    }

                                    if (!page.IsSupported)
                                    {
                                        throw new InvalidDataException("The app-server did not support linked-fork discovery.");
                                    }

                                    foreach (ThreadSummary child in page.RelatedThreads)
                                    {
                                        child.LinkedParentThreadId = parentThreadId;
                                        childrenForParent.Add(child.Id);
                                    }

                                    cursor = page.NextCursor;
                                    if (string.IsNullOrWhiteSpace(cursor))
                                    {
                                        break;
                                    }

                                    if (!seenCursors.Add(cursor) || pageNumber == 99)
                                    {
                                        throw new InvalidDataException("Linked-fork discovery did not terminate within its paging bounds.");
                                    }
                                }

                                lock (relationships)
                                {
                                    foreach (string childId in childrenForParent)
                                    {
                                        relationships[childId] = parentThreadId;
                                    }
                                }
                            }).ConfigureAwait(false);
                    }

                    await OnUiAsync(() =>
                    {
                        lock (linkedForkIndexStateGate)
                        {
                            if (!IsCurrentOwner(owner) || historyGeneration != Interlocked.Read(ref historyManagementGeneration))
                            {
                                return;
                            }

                            linkedForkParentByChildId.Clear();
                            foreach ((string childId, string parentId) in relationships)
                            {
                                linkedForkParentByChildId[childId] = parentId;
                            }

                            if (linkedForkIndexUpdatesDuringRefresh is { } updates)
                            {
                                foreach ((string childId, string? parentId) in updates)
                                {
                                    if (parentId is null)
                                    {
                                        linkedForkParentByChildId.Remove(childId);
                                    }
                                    else
                                    {
                                        linkedForkParentByChildId[childId] = parentId;
                                    }
                                }
                            }

                            linkedForkIndexUpdatesDuringRefresh = null;
                            linkedForkIndexOwner = owner;
                            linkedForkIndexComplete = true;
                        }
                        NotifyThreadManagementCanExecuteChanged();
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    ExtensionDiagnostics.Write($"Complete linked-fork index failed ({exception.GetType().Name}).");
                    await OnUiAsync(() =>
                    {
                        lock (linkedForkIndexStateGate)
                        {
                            if (IsCurrentOwner(owner) && historyGeneration == Interlocked.Read(ref historyManagementGeneration))
                            {
                                linkedForkIndexUpdatesDuringRefresh = null;
                                linkedForkIndexComplete = false;
                                NotifyThreadManagementCanExecuteChanged();
                            }
                        }
                    }).ConfigureAwait(false);
                }
                finally
                {
                    if (updatesForThisRefresh is not null)
                    {
                        await OnUiAsync(() =>
                        {
                            lock (linkedForkIndexStateGate)
                            {
                                if (ReferenceEquals(linkedForkIndexUpdatesDuringRefresh, updatesForThisRefresh))
                                {
                                    linkedForkIndexUpdatesDuringRefresh = null;
                                    linkedForkIndexComplete = false;
                                    NotifyThreadManagementCanExecuteChanged();
                                }
                            }
                        }).ConfigureAwait(false);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
    private async Task RefreshManagedThreadListAsync()
    {
        CancelHistorySearchAndAdvanceGeneration();
        SelectedRevertTurnId = null;
        long historyGeneration = Interlocked.Read(ref historyManagementGeneration);
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        await RefreshCompleteLinkedForkIndexAsync(owner, lifetime.Token).ConfigureAwait(false);
        if (lifetime.IsCancellationRequested
            || historyGeneration != Interlocked.Read(ref historyManagementGeneration)
            || !IsCurrentOwner(owner))
        {
            return;
        }

        bool useTitleSearchFallback = !settings.ExperimentalApiEnabled;
        await OnUiAsync(() =>
        {
            if (!lifetime.IsCancellationRequested
                && historyGeneration == Interlocked.Read(ref historyManagementGeneration)
                && IsCurrentOwner(owner))
            {
                useThreadListTitleSearchFallback = useTitleSearchFallback;
            }
        }).ConfigureAwait(false);
        if (lifetime.IsCancellationRequested
            || historyGeneration != Interlocked.Read(ref historyManagementGeneration)
            || !IsCurrentOwner(owner))
        {
            return;
        }

        if (settings.ExperimentalApiEnabled && !string.IsNullOrWhiteSpace(historySearchText))
        {
            await SearchHistoryAsync().ConfigureAwait(false);
            return;
        }

        if (IsHistoryOpen)
        {
            await LoadThreadSectionsAsync().ConfigureAwait(false);
        }

        await RefreshManagedListForCurrentViewAsync(
            owner,
            IsJapaneseUi ? "会話一覧を更新しました。" : "Conversation list refreshed.",
            lifetime.Token).ConfigureAwait(false);
    }

    private ListThreadsRequest CreateManagedThreadListRequest(IReadOnlyList<string>? workingDirectories, string? cursor = null)
        => new()
        {
            Cursor = cursor,
            WorkingDirectories = workingDirectories,
            Archived = showArchivedThreads,
            SectionId = string.IsNullOrWhiteSpace(selectedThreadSectionId) ? null : selectedThreadSectionId,
            SearchTerm = (!settings.ExperimentalApiEnabled || useThreadListTitleSearchFallback)
                && !string.IsNullOrWhiteSpace(historySearchText)
                    ? historySearchText.Trim()
                    : null,
        };

    private async Task LoadThreadSectionsAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        long historyGeneration = Interlocked.Read(ref historyManagementGeneration);
        try
        {
            ThreadSectionsResult result = await bridge.ListThreadSectionsAsync(
                StampOwner(new GetThreadListScopesRequest(), owner), lifetime.Token).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner) || historyGeneration != Interlocked.Read(ref historyManagementGeneration) || !MatchesOwner(result, owner))
                {
                    return;
                }

                threadSections = result.Sections;
                ThreadSections.Clear();
                ThreadSections.Add(new ThreadSectionOption(string.Empty, IsJapaneseUi ? "すべてのセクション" : "All sections"));
                foreach (ThreadSectionInfo section in result.Sections)
                {
                    ThreadSections.Add(new ThreadSectionOption(section.Id, SafeMarkdownService.ToSafeLiteralText(section.Name)));
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write($"Thread section list failed ({ex.GetType().Name}).");
        }
    }

    internal async Task SearchHistoryAsync()
    {
        string term = HistorySearchText.Trim();
        if (term.Length == 0)
        {
            await RefreshManagedThreadListAsync().ConfigureAwait(false);
            return;
        }

        useThreadListTitleSearchFallback = !settings.ExperimentalApiEnabled;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken cancellationToken = cancellation.Token;
        long requestGeneration;
        lock (historySearchCancellationGate)
        {
            activeThreadSearchRequestId = null;
            activeSearchEligibleThreads = null;
            historySearchCancellation?.Cancel();
            historySearchCancellation = cancellation;
            requestGeneration = Interlocked.Increment(ref historyManagementGeneration);
        }
        lock (threadHistoryStateGate)
        {
            accumulatedHistorySearchHits.Clear();
            accumulatedHistorySearchIds.Clear();
            activeHistorySearchPartialHits = Array.Empty<ThreadSearchHit>();
        }

        bool useExperimentalSearchResults = settings.ExperimentalApiEnabled;
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        try
        {
            await RefreshCompleteLinkedForkIndexAsync(owner, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested
                || requestGeneration != Interlocked.Read(ref historyManagementGeneration)
                || !IsCurrentOwner(owner))
            {
                return;
            }

            if (settings.ExperimentalApiEnabled)
            {
                HashSet<string>? eligibleThreads = await LoadEligibleSearchThreadIdsAsync(
                    owner, threadListWorkingDirectories, cancellationToken).ConfigureAwait(false);
                lock (historySearchCancellationGate)
                {
                    if (cancellationToken.IsCancellationRequested
                        || requestGeneration != Interlocked.Read(ref historyManagementGeneration)
                        || !IsCurrentOwner(owner))
                    {
                        return;
                    }

                    activeSearchEligibleThreads = eligibleThreads;
                }

                var seenCursors = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                bool finalSearchPageSeen = false;
                for (int pageNumber = 0; pageNumber < 100; pageNumber++)
                {
                    string requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                    lock (historySearchCancellationGate)
                    {
                        if (cancellationToken.IsCancellationRequested
                            || requestGeneration != Interlocked.Read(ref historyManagementGeneration)
                            || !IsCurrentOwner(owner))
                        {
                            return;
                        }

                        activeThreadSearchRequestId = requestId;
                    }

                    await OnUiAsync(() =>
                    {
                        lock (threadHistoryStateGate)
                        {
                            if (!cancellationToken.IsCancellationRequested
                                && requestGeneration == Interlocked.Read(ref historyManagementGeneration)
                                && IsCurrentOwner(owner)
                                && string.Equals(activeThreadSearchRequestId, requestId, StringComparison.Ordinal))
                            {
                                activeHistorySearchPartialHits = Array.Empty<ThreadSearchHit>();
                                ApplyThreadSearchHits(FilterEligibleSearchHits(accumulatedHistorySearchHits, eligibleThreads));
                            }
                        }
                    }).ConfigureAwait(false);

                    ThreadSearchResult result = await bridge.SearchThreadsAsync(
                        StampOwner(new ThreadSearchRequest
                        {
                            SearchTerm = term,
                            Archived = showArchivedThreads,
                            Cursor = cursor,
                            RequestId = requestId,
                        }, owner),
                        cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested
                        || requestGeneration != Interlocked.Read(ref historyManagementGeneration)
                        || !IsCurrentOwner(owner))
                    {
                        return;
                    }

                    if (!result.IsSupported)
                    {
                        if (cursor is not null)
                        {
                            throw new InvalidDataException("Thread search became unsupported after returning a cursor.");
                        }

                        useThreadListTitleSearchFallback = true;
                        useExperimentalSearchResults = false;
                        await RefreshManagedListForCurrentViewAsync(
                            owner,
                            IsJapaneseUi ? "タイトルの検索結果を表示しています。" : "Showing title search results.",
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (!MatchesOwner(result, owner))
                    {
                        return;
                    }

                    bool complete = string.IsNullOrWhiteSpace(result.NextCursor);
                    cursor = result.NextCursor;
                    if (cursor is not null && !seenCursors.Add(cursor))
                    {
                        throw new InvalidDataException("Thread search repeated a page cursor.");
                    }
                    bool pageLimitReached = !complete && pageNumber == 99;

                    await OnUiAsync(() =>
                    {
                        lock (threadHistoryStateGate)
                        {
                            if (cancellation.IsCancellationRequested
                                || requestGeneration != Interlocked.Read(ref historyManagementGeneration)
                                || !IsCurrentOwner(owner)
                                || !string.Equals(activeThreadSearchRequestId, requestId, StringComparison.Ordinal))
                            {
                                return;
                            }

                            if (!finalSearchPageSeen)
                            {
                                accumulatedHistorySearchHits.Clear();
                                accumulatedHistorySearchIds.Clear();
                                finalSearchPageSeen = true;
                            }

                            MergeHistorySearchHits(result.Results);
                            activeHistorySearchPartialHits = Array.Empty<ThreadSearchHit>();
                            activeThreadSearchRequestId = null;
                            ApplyThreadSearchHits(FilterEligibleSearchHits(accumulatedHistorySearchHits, eligibleThreads));
                            HistoryStatusText = complete
                                ? (IsJapaneseUi ? "検索結果を表示しています。" : "Showing search results.")
                                : pageLimitReached
                                    ? (IsJapaneseUi ? "検索を100ページで停止し、取得済みの結果を表示しています。" : "Search stopped at the 100-page limit. Showing results retrieved so far.")
                                    : (IsJapaneseUi ? "検索を続けています…" : "Searching more results…");
                            nextCursor = null;
                            OnPropertyChanged(nameof(ThreadRows));
                        }
                    }).ConfigureAwait(false);

                    if (complete || pageLimitReached)
                    {
                        break;
                    }
                }
            }
            else
            {
                // The stable thread/list title filter remains available when experimental APIs are off.
                await RefreshManagedListForCurrentViewAsync(owner,
                    IsJapaneseUi ? "タイトルの検索結果を表示しています。" : "Showing title search results.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write($"Thread search failed ({ex.GetType().Name}).");
            await OnUiAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested
                    && requestGeneration == Interlocked.Read(ref historyManagementGeneration)
                    && IsCurrentOwner(owner))
                {
                    historyManagementStatusText = IsJapaneseUi ? "検索できませんでした。再試行してください。" : "Search failed. Try again.";
                    OnPropertyChanged(nameof(ThreadManagementStatusText));
                    OnPropertyChanged(nameof(HasThreadManagementStatus));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            bool currentSearch = false;
            HashSet<string>? eligibleThreadsForCleanup = null;
            lock (historySearchCancellationGate)
            {
                currentSearch = requestGeneration == Interlocked.Read(ref historyManagementGeneration)
                    && IsCurrentOwner(owner);
                if (currentSearch)
                {
                    activeThreadSearchRequestId = null;
                    eligibleThreadsForCleanup = activeSearchEligibleThreads;
                }

                if (ReferenceEquals(historySearchCancellation, cancellation))
                {
                    historySearchCancellation = null;
                }

                cancellation.Dispose();
            }

            if (currentSearch)
            {
                await OnUiAsync(() =>
                {
                    if (requestGeneration != Interlocked.Read(ref historyManagementGeneration) || !IsCurrentOwner(owner))
                    {
                        return;
                    }

                    lock (threadHistoryStateGate)
                    {
                        activeHistorySearchPartialHits = Array.Empty<ThreadSearchHit>();
                        if (useExperimentalSearchResults
                            && string.Equals(historySearchText.Trim(), term, StringComparison.Ordinal))
                        {
                            ApplyThreadSearchHits(FilterEligibleSearchHits(
                                accumulatedHistorySearchHits,
                                eligibleThreadsForCleanup));
                        }
                    }
                }).ConfigureAwait(false);

                lock (historySearchCancellationGate)
                {
                    if (requestGeneration == Interlocked.Read(ref historyManagementGeneration)
                        && IsCurrentOwner(owner))
                    {
                        activeSearchEligibleThreads = null;
                    }
                }
            }
        }
    }

    private async Task RefreshManagedListForCurrentViewAsync(
        OwnerSnapshot owner,
        string successMessage,
        CancellationToken cancellationToken)
    {
        ThreadListRefreshOutcome outcome = await RefreshCompleteThreadListAsync(
            owner,
            successMessage,
            cancellationToken).ConfigureAwait(false);
        if (outcome == ThreadListRefreshOutcome.Busy && !QueueThreadListRefreshIfBusy(owner))
        {
            outcome = await RefreshCompleteThreadListAsync(owner, successMessage, cancellationToken).ConfigureAwait(false);
            if (outcome == ThreadListRefreshOutcome.Busy && !QueueThreadListRefreshIfBusy(owner))
            {
                await SetThreadListFailureStatusAsync(
                    owner,
                    IsJapaneseUi ? "会話一覧の更新が混み合っています。もう一度お試しください。" : "Conversation list refresh is busy. Try again.").ConfigureAwait(false);
            }
        }
    }

    private void CancelHistorySearch()
    {
        lock (historySearchCancellationGate)
        {
            activeThreadSearchRequestId = null;
            CancellationTokenSource? previous = historySearchCancellation;
            historySearchCancellation = null;
            previous?.Cancel();
        }
    }

    private void CancelHistorySearchAndAdvanceGeneration()
    {
        lock (historySearchCancellationGate)
        {
            activeThreadSearchRequestId = null;
            CancellationTokenSource? previous = historySearchCancellation;
            historySearchCancellation = null;
            previous?.Cancel();
            Interlocked.Increment(ref historyManagementGeneration);
        }
    }

    private Task OnThreadSearchPartialAsync(WorkerNotification<ThreadSearchPartialEvent> notification)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (notification.OwnerGeneration != owner.OwnerGeneration
            || notification.ConnectionGeneration != owner.ConnectionGeneration
            || !string.Equals(notification.StatePartitionFingerprint, owner.StatePartitionFingerprint, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        string? expectedRequestId = activeThreadSearchRequestId;
        if (expectedRequestId is null || !string.Equals(notification.Value.RequestId, expectedRequestId, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        long expectedGeneration = Interlocked.Read(ref historyManagementGeneration);
        _ = OnUiAsync(() =>
        {
            lock (threadHistoryStateGate)
            {
                if (expectedGeneration == Interlocked.Read(ref historyManagementGeneration)
                    && IsCurrentOwner(owner)
                    && !string.IsNullOrWhiteSpace(activeThreadSearchRequestId)
                    && string.Equals(notification.Value.RequestId, activeThreadSearchRequestId, StringComparison.Ordinal))
                {
                    activeHistorySearchPartialHits = notification.Value.Results;
                    ApplyThreadSearchHits(FilterEligibleSearchHits(GetDisplayedHistorySearchHits(), activeSearchEligibleThreads));
                    HistoryStatusText = IsJapaneseUi ? "検索中…" : "Searching…";
                }
            }
        });
        return Task.CompletedTask;
    }

    private List<ThreadSearchHit> GetDisplayedHistorySearchHits()
    {
        var hits = accumulatedHistorySearchHits.ToList();
        var indicesByThreadId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < hits.Count; index++)
        {
            indicesByThreadId[hits[index].Thread.Id] = index;
        }

        foreach (ThreadSearchHit partialHit in activeHistorySearchPartialHits)
        {
            if (indicesByThreadId.TryGetValue(partialHit.Thread.Id, out int existingIndex))
            {
                hits[existingIndex] = partialHit;
            }
            else
            {
                indicesByThreadId[partialHit.Thread.Id] = hits.Count;
                hits.Add(partialHit);
            }
        }

        return hits;
    }

    private void MergeHistorySearchHits(IEnumerable<ThreadSearchHit> hits)
    {
        foreach (ThreadSearchHit hit in hits)
        {
            if (accumulatedHistorySearchIds.Add(hit.Thread.Id))
            {
                accumulatedHistorySearchHits.Add(hit);
                continue;
            }

            int existingIndex = accumulatedHistorySearchHits.FindIndex(existing =>
                string.Equals(existing.Thread.Id, hit.Thread.Id, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                accumulatedHistorySearchHits[existingIndex] = hit;
            }
        }
    }

    private void ApplyThreadSearchHits(IEnumerable<ThreadSearchHit> hits)
    {
        Threads.Clear();
        foreach (ThreadSearchHit hit in hits)
        {
            ThreadSummary source = hit.Thread;
            var displayThread = new ThreadSummary
            {
                Id = source.Id,
                Preview = source.Preview,
                Cwd = source.Cwd,
                UpdatedAt = source.UpdatedAt,
                EffectiveApprovalState = source.EffectiveApprovalState,
                EffectiveReasoningEffort = source.EffectiveReasoningEffort,
                EffectiveServiceTier = source.EffectiveServiceTier,
                EffectiveModelId = source.EffectiveModelId,
                LinkedParentThreadId = source.LinkedParentThreadId,
                Archived = source.Archived,
                SectionId = source.SectionId,
                SectionName = source.SectionName,
                SearchSnippet = hit.Snippet,
                Status = source.Status,
                HasAuthoritativeTurnSettings = source.HasAuthoritativeTurnSettings,
                DisabledPluginIds = source.DisabledPluginIds,
            };
            AddDistinctThreads([displayThread], skipExisting: true, trackLinkedRelationships: false);
        }

        KeepSelectedThreadRow();
    }

    internal async Task<HashSet<string>?> LoadEligibleSearchThreadIdsAsync(
        OwnerSnapshot owner,
        IReadOnlyList<string>? workingDirectories,
        CancellationToken cancellationToken)
    {
        long historyGeneration = Interlocked.Read(ref historyManagementGeneration);
        if (!showArchivedThreads && string.IsNullOrWhiteSpace(selectedThreadSectionId) && workingDirectories is null)
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            ThreadPage page = await bridge.ListThreadsAsync(StampOwner(new ListThreadsRequest
            {
                Cursor = cursor,
                Archived = showArchivedThreads,
                SectionId = string.IsNullOrWhiteSpace(selectedThreadSectionId) ? null : selectedThreadSectionId,
                WorkingDirectories = workingDirectories,
            }, owner), cancellationToken).ConfigureAwait(false);
            if (!IsCurrentOwner(owner) || historyGeneration != Interlocked.Read(ref historyManagementGeneration) || !MatchesOwner(page, owner))
            {
                return [];
            }

            foreach (ThreadSummary thread in page.Threads)
            {
                allowed.Add(thread.Id);
            }

            cursor = page.NextCursor;
            if (string.IsNullOrWhiteSpace(cursor))
            {
                return allowed;
            }

            if (!seenCursors.Add(cursor))
            {
                throw new InvalidDataException("The thread list returned a repeated cursor.");
            }
        }

        throw new InvalidDataException("The thread list exceeded the maximum page count.");
    }

    private static IEnumerable<ThreadSearchHit> FilterEligibleSearchHits(IEnumerable<ThreadSearchHit> hits, HashSet<string>? eligible)
        => eligible is null ? hits : hits.Where(hit => eligible.Contains(hit.Thread.Id));

    private async Task ArchiveSelectedThreadAsync()
    {
        if (SelectedThread is not { } thread || IsThreadRunning(thread.Id))
        {
            return;
        }

        OwnerSnapshot owner = CaptureOwnerSnapshot();
        bool unarchive = thread.Archived;
        var request = StampOwner(new ThreadMutationRequest { ThreadId = thread.Id }, owner);
        ThreadMutationResult result;
        try
        {
            result = unarchive
                ? await bridge.UnarchiveThreadAsync(request, lifetime.Token).ConfigureAwait(false)
                : await bridge.ArchiveThreadAsync(request, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (WorkerRpcUnavailableException exception)
        {
            result = new ThreadMutationResult { Message = exception.Message };
        }
        catch (StreamJsonRpc.RemoteInvocationException exception)
        {
            result = new ThreadMutationResult
            {
                OutcomeUnknown = exception.ErrorCode != WorkerErrorCodes.PreDispatchRejected,
                Message = exception.Message,
            };
        }
        catch (Exception)
        {
            result = new ThreadMutationResult { OutcomeUnknown = true };
        }

        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            historyManagementStatusText = MutationStatusMessage(result, unarchive ? "unarchive" : "archive");
            OnPropertyChanged(nameof(ThreadManagementStatusText));
            OnPropertyChanged(nameof(HasThreadManagementStatus));
            if (result.Succeeded && SelectedThread?.Id == thread.Id)
            {
                thread.Archived = !unarchive;
                OnPropertyChanged(nameof(ThreadArchiveActionText));
            }
        }).ConfigureAwait(false);
        if (IsCurrentOwner(owner))
        {
            await RefreshManagedThreadListAsync().ConfigureAwait(false);
        }
    }

    private string MutationStatusMessage(ThreadMutationResult result, string action)
    {
        if (result.OutcomeUnknown)
        {
            return IsJapaneseUi
                ? "操作の結果を確認できませんでした。会話一覧を更新してください。"
                : "The operation outcome is unknown. Refresh the conversation list to check its status.";
        }

        if (!result.Succeeded)
        {
            return markdown.ToSafeText(result.Message ?? (IsJapaneseUi ? "操作を完了できませんでした。" : "The operation could not be completed."));
        }

        return result.Message is { Length: > 0 } message
            ? markdown.ToSafeText(message)
            : action switch
            {
                "archive" => IsJapaneseUi ? "会話をアーカイブしました。" : "Conversation archived.",
                "unarchive" => IsJapaneseUi ? "会話のアーカイブを解除しました。" : "Conversation unarchived.",
                "delete" => IsJapaneseUi ? "会話を削除しました。" : "Conversation deleted.",
                "revert" => IsJapaneseUi ? "会話を選択したターンの前に戻しました。" : "Conversation reverted to the selected turn.",
                _ => IsJapaneseUi ? "操作が完了しました。" : "Operation completed.",
            };
    }

    private Task RequestDeleteSelectedThreadAsync()
    {
        if (!CanDeleteSelectedThread || SelectedThread is not { } thread)
        {
            return Task.CompletedTask;
        }

        pendingThreadMutation = "delete";
        pendingMutationOwner = CaptureOwnerSnapshot();
        pendingMutationThreadId = thread.Id;
        pendingMutationThreadTitle = SafeThreadTitle(thread.Id);
        pendingMutationBeforeTurnId = null;
        NotifyThreadMutationConfirmationChanged();
        return Task.CompletedTask;
    }

    private Task RequestRevertSelectedThreadAsync()
    {
        if (!CanRevertSelectedThread || SelectedThread is not { } thread || string.IsNullOrWhiteSpace(SelectedRevertTurnId))
        {
            return Task.CompletedTask;
        }

        pendingThreadMutation = "revert";
        pendingMutationOwner = CaptureOwnerSnapshot();
        pendingMutationThreadId = thread.Id;
        pendingMutationThreadTitle = SafeThreadTitle(thread.Id);
        pendingMutationBeforeTurnId = SelectedRevertTurnId;
        NotifyThreadMutationConfirmationChanged();
        return Task.CompletedTask;
    }

    private Task CancelThreadMutationAsync()
    {
        pendingThreadMutation = null;
        pendingMutationOwner = null;
        pendingMutationThreadId = null;
        pendingMutationThreadTitle = null;
        pendingMutationBeforeTurnId = null;
        NotifyThreadMutationConfirmationChanged();
        return Task.CompletedTask;
    }

    private async Task ConfirmThreadMutationAsync()
    {
        string? mutation = pendingThreadMutation;
        string? threadId = pendingMutationThreadId;
        string? beforeTurnId = pendingMutationBeforeTurnId;
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (pendingMutationOwner is not OwnerSnapshot confirmationOwner
            || confirmationOwner != owner
            || mutation is null
            || threadId is null
            || SelectedThread?.Id != threadId
            || (mutation == "delete" && !CanDeleteSelectedThread)
            || (mutation == "revert" && (!CanRevertSelectedThread || beforeTurnId != SelectedRevertTurnId)))
        {
            await CancelThreadMutationAsync().ConfigureAwait(false);
            return;
        }

        await CancelThreadMutationAsync().ConfigureAwait(false);
        var request = StampOwner(new ThreadMutationRequest
        {
            ThreadId = threadId,
            BeforeTurnId = beforeTurnId,
            UserConfirmed = true,
        }, owner);
        ThreadMutationResult result;
        try
        {
            result = mutation == "delete"
                ? await bridge.DeleteThreadAsync(request, lifetime.Token).ConfigureAwait(false)
                : await bridge.RevertThreadAsync(request, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (WorkerRpcUnavailableException exception)
        {
            result = new ThreadMutationResult { Message = exception.Message };
        }
        catch (StreamJsonRpc.RemoteInvocationException exception)
        {
            result = new ThreadMutationResult
            {
                OutcomeUnknown = exception.ErrorCode != WorkerErrorCodes.PreDispatchRejected,
                Message = exception.Message,
            };
        }
        catch (Exception)
        {
            result = new ThreadMutationResult { OutcomeUnknown = true };
        }

        bool refresh = false;
        bool reloadTranscript = false;
        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            historyManagementStatusText = MutationStatusMessage(result, mutation);
            OnPropertyChanged(nameof(ThreadManagementStatusText));
            OnPropertyChanged(nameof(HasThreadManagementStatus));
            if (result.Succeeded && mutation == "delete" && SelectedThread?.Id == threadId)
            {
                SelectedThread = null;
            }

            refresh = true;
            reloadTranscript = result.Succeeded && mutation == "revert" && SelectedThread?.Id == threadId;
            if (reloadTranscript)
            {
                lock (threadHistoryStateGate)
                {
                    selectedThreadGeneration++;
                }
            }
        }).ConfigureAwait(false);

        if (refresh && IsCurrentOwner(owner))
        {
            await RefreshManagedThreadListAsync().ConfigureAwait(false);
            if (reloadTranscript && SelectedThread is { } selected && selected.Id == threadId && IsCurrentOwner(owner))
            {
                await ReadSelectedThreadHistoryAsync(selected, replaceTranscript: true, ownerFence: owner).ConfigureAwait(false);
            }
        }
    }

    private void NotifyThreadMutationConfirmationChanged()
    {
        OnPropertyChanged(nameof(IsThreadMutationConfirmationOpen));
        OnPropertyChanged(nameof(ThreadMutationConfirmationText));
        OnPropertyChanged(nameof(ThreadMutationConfirmHeading));
        OnPropertyChanged(nameof(ThreadMutationConfirmButtonText));
        OnPropertyChanged(nameof(ThreadMutationConfirmAutomationName));
        ConfirmThreadMutationCommand.RaiseCanExecuteChanged();
        CancelThreadMutationCommand.RaiseCanExecuteChanged();
    }

    private void NotifyThreadManagementCanExecuteChanged()
    {
        ArchiveThreadCommand.RaiseCanExecuteChanged();
        DeleteThreadCommand.RaiseCanExecuteChanged();
        RevertThreadCommand.RaiseCanExecuteChanged();
        SearchHistoryCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteSelectedThread));
        OnPropertyChanged(nameof(CanRevertSelectedThread));
        OnPropertyChanged(nameof(ShowThreadManagementDeleteUnavailable));
        OnPropertyChanged(nameof(HasSelectedThread));
        OnPropertyChanged(nameof(ShowThreadRevertControls));
        OnPropertyChanged(nameof(ThreadArchiveActionText));
    }

    private void OnThreadManagementHistoryOpened()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        _ = LoadThreadSectionsAsync();
        _ = RefreshCompleteLinkedForkIndexAsync(owner, lifetime.Token);
    }

    private void OnManagedSelectedThreadChanged()
    {
        if (SelectedThread is { } selected)
        {
            TrackLinkedForkRelationship(selected);
        }

        OnPropertyChanged(nameof(HasSelectedThread));
        OnPropertyChanged(nameof(ShowThreadRevertControls));
        OnPropertyChanged(nameof(ThreadArchiveActionText));
        SelectedRevertTurnId = null;
        pendingThreadMutation = null;
        pendingMutationOwner = null;
        pendingMutationThreadId = null;
        pendingMutationThreadTitle = null;
        pendingMutationBeforeTurnId = null;
        NotifyThreadMutationConfirmationChanged();
        NotifyThreadManagementCanExecuteChanged();
    }

    private bool OnThreadManagementOwnerChanged()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (threadManagementOwner is OwnerSnapshot previous && previous == owner)
        {
            EnsureLinkedForkIndexOwner();
            return false;
        }

        threadManagementOwner = owner;
        threadManagementOwnerDataRefreshPending = true;
        CancelHistorySearchAndAdvanceGeneration();
        useThreadListTitleSearchFallback = false;
        EnsureLinkedForkIndexOwner();
        lock (threadHistoryStateGate)
        {
            accumulatedHistorySearchHits.Clear();
            accumulatedHistorySearchIds.Clear();
            activeHistorySearchPartialHits = Array.Empty<ThreadSearchHit>();
            activeSearchEligibleThreads = null;
            Threads.Clear();
        }
        ResetThreadListScope();
        threadSections = Array.Empty<ThreadSectionInfo>();
        ThreadSections.Clear();
        ThreadSections.Add(new ThreadSectionOption(string.Empty, IsJapaneseUi ? "すべてのセクション" : "All sections"));
        Interlocked.Increment(ref linkedForkRequestGeneration);
        RelatedConversations.Clear();
        relatedConversationsParentId = null;
        relatedConversationsNextCursor = null;
        LinkedForkStatusText = string.Empty;
        OnPropertyChanged(nameof(HasRelatedConversations));
        OnPropertyChanged(nameof(HasMoreRelatedConversations));
        historyManagementStatusText = string.Empty;
        OnPropertyChanged(nameof(ThreadManagementStatusText));
        OnPropertyChanged(nameof(HasThreadManagementStatus));
        if (historySearchText.Length > 0)
        {
            historySearchText = string.Empty;
            OnPropertyChanged(nameof(HistorySearchText));
            SearchHistoryCommand.RaiseCanExecuteChanged();
        }
        nextCursor = null;
        SelectedThread = null;
        HistoryStatusText = string.Empty;
        if (pendingMutationOwner is OwnerSnapshot pendingOwner && pendingOwner != owner)
        {
            _ = CancelThreadMutationAsync();
        }

        return true;
    }

    private async Task RefreshThreadManagementOwnerDataAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        await LoadThreadSectionsAsync().ConfigureAwait(false);
        if (IsCurrentOwner(owner))
        {
            await RefreshCompleteLinkedForkIndexAsync(owner, lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task OnThreadHistoryChangedAsync(WorkerNotification<ThreadHistoryChangedEvent> notification)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (notification.OwnerGeneration != owner.OwnerGeneration
            || notification.ConnectionGeneration != owner.ConnectionGeneration
            || !string.Equals(notification.StatePartitionFingerprint, owner.StatePartitionFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        ThreadHistoryChangedEvent change = notification.Value;
        bool refreshHistory = false;
        bool refreshSections = false;
        bool replaceSelectedTranscript = false;
        ThreadSummary? selectedTranscriptForReplacement = null;
        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            lock (linkedForkIndexStateGate)
            {
                EnsureLinkedForkIndexOwner();
            }
            refreshSections = IsHistoryOpen && change.ChangeKind == "thread/section/changed";
            bool removedLinkedChild;
            lock (linkedForkIndexStateGate)
            {
                removedLinkedChild = change.ChangeKind == "thread/deleted"
                    && linkedForkParentByChildId.Remove(change.ThreadId);
                if (change.ChangeKind == "thread/deleted" && linkedForkIndexUpdatesDuringRefresh is { } updates)
                {
                    updates[change.ThreadId] = null;
                    removedLinkedChild = true;
                }
            }
            if (removedLinkedChild)
            {
                NotifyThreadManagementCanExecuteChanged();
            }

            if (change.ChangeKind == "thread/deleted" && SelectedThread?.Id == change.ThreadId)
            {
                SelectedThread = null;
            }

            if (change.ChangeKind == "thread/reverted"
                && SelectedThread is { } selectedForRevert
                && string.Equals(selectedForRevert.Id, change.ThreadId, StringComparison.Ordinal))
            {
                replaceSelectedTranscript = true;
                selectedTranscriptForReplacement = selectedForRevert;
                lock (threadHistoryStateGate)
                {
                    // Invalidate any pre-revert read that could finish after this replacement.
                    selectedThreadGeneration++;
                }
            }

            bool affectsForkIndex;
            lock (linkedForkIndexStateGate)
            {
                affectsForkIndex = linkedForkParentByChildId.ContainsKey(change.ThreadId)
                    || linkedForkParentByChildId.Values.Contains(change.ThreadId, StringComparer.Ordinal);
            }
            bool membershipChange = change.ChangeKind is "thread/archived" or "thread/unarchived" or "thread/section/changed";
            bool affectsDisplayedHistory = Threads.Any(thread => thread.Id == change.ThreadId)
                || ThreadRows.Any(row => row.Id == change.ThreadId)
                || affectsForkIndex;
            refreshHistory = IsHistoryOpen
                && (removedLinkedChild || membershipChange || affectsDisplayedHistory);
        }).ConfigureAwait(false);

        if (refreshSections)
        {
            await LoadThreadSectionsAsync().ConfigureAwait(false);
        }

        if (replaceSelectedTranscript && selectedTranscriptForReplacement is { } selected)
        {
            await ReadSelectedThreadHistoryAsync(selected, replaceTranscript: true, ownerFence: owner).ConfigureAwait(false);
        }

        if (refreshHistory)
        {
            await RefreshManagedThreadListAsync().ConfigureAwait(false);
        }
    }
}

[DataContract]
public sealed class ThreadSectionOption
{
    public ThreadSectionOption(string id, string name)
    {
        Id = id;
        Name = name;
    }

    [DataMember] public string Id { get; }
    [DataMember] public string Name { get; }
}
