using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

/// <summary>Presentation state and commands for the conversation history management surface.</summary>
public sealed partial class ChatViewModel
{
    private bool showArchivedThreads;
    private string historySearchText = string.Empty;
    private string? selectedThreadSectionId;
    private string? selectedRevertTurnId;
    private string? pendingThreadMutation;
    private string? pendingMutationThreadId;
    private string? pendingMutationBeforeTurnId;
    private string? activeThreadSearchRequestId;
    private HashSet<string>? activeSearchEligibleThreads;
    private readonly Dictionary<string, string> linkedForkParentByChildId = new(StringComparer.Ordinal);
    private OwnerSnapshot? linkedForkIndexOwner;
    private bool linkedForkIndexComplete;
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
            ? $"「{SafeThreadTitle(pendingMutationThreadId)}」を完全に削除します。この操作は取り消せません。ローカルファイルの変更は保持されます。"
            : $"Permanently delete “{SafeThreadTitle(pendingMutationThreadId)}”? This cannot be undone. Local file changes are preserved.",
        "revert" => IsJapaneseUi
            ? $"「{SafeThreadTitle(pendingMutationThreadId)}」の選択したターンとそれ以降を会話履歴から削除します。ローカルファイルの変更は元に戻りません。"
            : $"Revert “{SafeThreadTitle(pendingMutationThreadId)}” to before the selected turn? This turn and every later turn will be removed from conversation history. Local file changes will not be reverted.",
        _ => string.Empty,
    };

    [DataMember]
    public string ThreadManagementStatusText => markdown.ToSafeText(historyManagementStatusText);

    [DataMember]
    public string ThreadArchiveActionText => ShowArchivedThreads
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
    [DataMember] public string ThreadDeleteAutomationName => IsJapaneseUi ? "選択した会話を削除" : "Delete selected conversation";
    [DataMember] public string ThreadMutationConfirmAutomationName => pendingThreadMutation == "delete" ? (IsJapaneseUi ? "削除を確定" : "Confirm deletion") : (IsJapaneseUi ? "戻す操作を確定" : "Confirm revert");
    [DataMember] public string ThreadMutationCancelAutomationName => IsJapaneseUi ? "操作をキャンセル" : "Cancel conversation change";
    [DataMember] public string ThreadRevertButtonText => IsJapaneseUi ? "選択したターンの前に戻す" : "Revert before turn";

    [DataMember]
    public bool HasThreadManagementStatus => !string.IsNullOrWhiteSpace(historyManagementStatusText);

    [DataMember]
    public bool CanDeleteSelectedThread => SelectedThread is { } thread
        && !IsThreadRunning(thread.Id)
        && linkedForkIndexComplete
        && !HasLinkedChildren(thread.Id)
        && !HasLinkedParent(thread.Id)
        && Status.State == WorkerConnectionState.Ready;

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
        string? title = ThreadRows.FirstOrDefault(row => row.Id == threadId)?.Preview
            ?? Threads.FirstOrDefault(thread => thread.Id == threadId)?.Preview
            ?? (SelectedThread is { } selected && selected.Id == threadId ? selected.Preview : null);
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
        EnsureLinkedForkIndexOwner();
        return linkedForkParentByChildId.Values.Contains(threadId, StringComparer.Ordinal);
    }

    private void TrackLinkedForkRelationship(ThreadSummary thread)
    {
        EnsureLinkedForkIndexOwner();
        if (string.IsNullOrWhiteSpace(thread.LinkedParentThreadId))
        {
            return;
        }

        linkedForkParentByChildId[thread.Id] = thread.LinkedParentThreadId;
    }

    private bool HasLinkedParent(string threadId)
    {
        EnsureLinkedForkIndexOwner();
        return linkedForkParentByChildId.ContainsKey(threadId);
    }

    private void EnsureLinkedForkIndexOwner()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (linkedForkIndexOwner is OwnerSnapshot current && current == owner)
        {
            return;
        }

        linkedForkParentByChildId.Clear();
        linkedForkIndexComplete = false;
        linkedForkIndexOwner = owner;
    }

    private async Task RefreshCompleteLinkedForkIndexAsync(OwnerSnapshot owner, CancellationToken cancellationToken)
    {
        long historyGeneration = Interlocked.Read(ref historyManagementGeneration);
        bool indexComplete = false;
        await OnUiAsync(() =>
        {
            EnsureLinkedForkIndexOwner();
            indexComplete = linkedForkIndexComplete;
        }).ConfigureAwait(false);
        if (indexComplete)
        {
            return;
        }

        var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (bool archived in new[] { false, true })
            {
                var seenCursors = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                for (int pageNumber = 0; pageNumber < 100; pageNumber++)
                {
                    ThreadPage page = await bridge.ListThreadsAsync(StampOwner(new ListThreadsRequest
                    {
                        Cursor = cursor,
                        Archived = archived,
                    }, owner), cancellationToken).ConfigureAwait(false);
                    if (!IsCurrentOwner(owner) || historyGeneration != Interlocked.Read(ref historyManagementGeneration) || !MatchesOwner(page, owner))
                    {
                        return;
                    }

                    foreach (ThreadSummary thread in page.Threads)
                    {
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

            await OnUiAsync(() =>
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

                linkedForkIndexOwner = owner;
                linkedForkIndexComplete = true;
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
                if (IsCurrentOwner(owner) && historyGeneration == Interlocked.Read(ref historyManagementGeneration))
                {
                    linkedForkIndexComplete = false;
                    NotifyThreadManagementCanExecuteChanged();
                }
            }).ConfigureAwait(false);
        }
    }
    private async Task RefreshManagedThreadListAsync()
    {
        CancelHistorySearchAndAdvanceGeneration();
        SelectedRevertTurnId = null;
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        await RefreshCompleteLinkedForkIndexAsync(owner, lifetime.Token).ConfigureAwait(false);
        useThreadListTitleSearchFallback = !settings.ExperimentalApiEnabled;
        if (settings.ExperimentalApiEnabled && !string.IsNullOrWhiteSpace(historySearchText))
        {
            await SearchHistoryAsync().ConfigureAwait(false);
            return;
        }

        if (IsHistoryOpen)
        {
            await LoadThreadSectionsAsync().ConfigureAwait(false);
        }

        await RefreshCompleteThreadListAsync(
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

    private async Task SearchHistoryAsync()
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
            historySearchCancellation?.Cancel();
            historySearchCancellation = cancellation;
            requestGeneration = Interlocked.Increment(ref historyManagementGeneration);
        }

        string requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
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
                HashSet<string>? eligibleThreads = await LoadEligibleSearchThreadIdsAsync(owner, cancellationToken).ConfigureAwait(false);
                lock (historySearchCancellationGate)
                {
                    if (cancellationToken.IsCancellationRequested
                        || requestGeneration != Interlocked.Read(ref historyManagementGeneration)
                        || !IsCurrentOwner(owner))
                    {
                        return;
                    }

                    activeSearchEligibleThreads = eligibleThreads;
                    activeThreadSearchRequestId = requestId;
                }

                ThreadSearchResult result = await bridge.SearchThreadsAsync(
                    StampOwner(new ThreadSearchRequest { SearchTerm = term, RequestId = requestId }, owner),
                    cancellationToken).ConfigureAwait(false);
                if (!result.IsSupported)
                {
                    useThreadListTitleSearchFallback = true;
                    await RefreshCompleteThreadListAsync(
                        owner,
                        IsJapaneseUi ? "タイトルの検索結果を表示しています。" : "Showing title search results.",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await OnUiAsync(() =>
                {
                    if (cancellation.IsCancellationRequested || requestGeneration != Interlocked.Read(ref historyManagementGeneration)
                        || !IsCurrentOwner(owner) || !MatchesOwner(result, owner))
                    {
                        return;
                    }

                    ApplyThreadSearchHits(FilterEligibleSearchHits(result.Results, eligibleThreads));
                    HistoryStatusText = IsJapaneseUi ? "検索結果を表示しています。" : "Showing search results.";
                    nextCursor = null;
                    OnPropertyChanged(nameof(ThreadRows));
                }).ConfigureAwait(false);
            }
            else
            {
                // The stable thread/list title filter remains available when experimental APIs are off.
                await RefreshCompleteThreadListAsync(owner,
                    IsJapaneseUi ? "タイトルの検索結果を表示しています。" : "Showing title search results.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write($"Thread search failed ({ex.GetType().Name}).");
            await OnUiAsync(() =>
            {
                if (requestGeneration == Interlocked.Read(ref historyManagementGeneration) && IsCurrentOwner(owner))
                {
                    historyManagementStatusText = IsJapaneseUi ? "検索できませんでした。再試行してください。" : "Search failed. Try again.";
                    OnPropertyChanged(nameof(ThreadManagementStatusText));
                    OnPropertyChanged(nameof(HasThreadManagementStatus));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            lock (historySearchCancellationGate)
            {
                if (string.Equals(activeThreadSearchRequestId, requestId, StringComparison.Ordinal))
                {
                    activeThreadSearchRequestId = null;
                }

                if (ReferenceEquals(historySearchCancellation, cancellation))
                {
                    historySearchCancellation = null;
                }

                cancellation.Dispose();
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
            if (expectedGeneration == Interlocked.Read(ref historyManagementGeneration)
                && IsCurrentOwner(owner)
                && !string.IsNullOrWhiteSpace(activeThreadSearchRequestId)
                && string.Equals(notification.Value.RequestId, activeThreadSearchRequestId, StringComparison.Ordinal))
            {
                ApplyThreadSearchHits(FilterEligibleSearchHits(notification.Value.Results, activeSearchEligibleThreads));
                HistoryStatusText = IsJapaneseUi ? "検索中…" : "Searching…";
            }
        });
        return Task.CompletedTask;
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

    private async Task<HashSet<string>?> LoadEligibleSearchThreadIdsAsync(OwnerSnapshot owner, CancellationToken cancellationToken)
    {
        long historyGeneration = Interlocked.Read(ref historyManagementGeneration);
        IReadOnlyList<string>? workingDirectories = threadListWorkingDirectories;
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
        bool unarchive = ShowArchivedThreads;
        var request = StampOwner(new ThreadMutationRequest { ThreadId = thread.Id }, owner);
        ThreadMutationResult result;
        try
        {
            result = unarchive
                ? await bridge.UnarchiveThreadAsync(request, lifetime.Token).ConfigureAwait(false)
                : await bridge.ArchiveThreadAsync(request, lifetime.Token).ConfigureAwait(false);
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
        pendingMutationBeforeTurnId = SelectedRevertTurnId;
        NotifyThreadMutationConfirmationChanged();
        return Task.CompletedTask;
    }

    private Task CancelThreadMutationAsync()
    {
        pendingThreadMutation = null;
        pendingMutationOwner = null;
        pendingMutationThreadId = null;
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
            if (result.Succeeded && mutation == "delete")
            {
                SelectedThread = null;
            }

            refresh = true;
            reloadTranscript = result.Succeeded && mutation == "revert" && SelectedThread is not null;
        }).ConfigureAwait(false);

        if (refresh && IsCurrentOwner(owner))
        {
            await RefreshManagedThreadListAsync().ConfigureAwait(false);
            if (reloadTranscript && SelectedThread is { } selected && IsCurrentOwner(owner))
            {
                await ReadSelectedThreadHistoryAsync(selected).ConfigureAwait(false);
            }
        }
    }

    private void NotifyThreadMutationConfirmationChanged()
    {
        OnPropertyChanged(nameof(IsThreadMutationConfirmationOpen));
        OnPropertyChanged(nameof(ThreadMutationConfirmationText));
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
        OnPropertyChanged(nameof(HasSelectedThread));
        OnPropertyChanged(nameof(ShowThreadRevertControls));
        SelectedRevertTurnId = null;
        pendingThreadMutation = null;
        pendingMutationOwner = null;
        pendingMutationThreadId = null;
        pendingMutationBeforeTurnId = null;
        NotifyThreadMutationConfirmationChanged();
        NotifyThreadManagementCanExecuteChanged();
    }

    private void OnThreadManagementOwnerChanged()
    {
        EnsureLinkedForkIndexOwner();
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (pendingMutationOwner is OwnerSnapshot pendingOwner && pendingOwner != owner)
        {
            _ = CancelThreadMutationAsync();
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
        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            EnsureLinkedForkIndexOwner();
            refreshSections = IsHistoryOpen && change.ChangeKind == "thread/section/changed";
            bool removedLinkedChild = change.ChangeKind == "thread/deleted"
                && linkedForkParentByChildId.Remove(change.ThreadId);
            if (removedLinkedChild)
            {
                NotifyThreadManagementCanExecuteChanged();
            }

            if (SelectedThread?.Id == change.ThreadId)
            {
                SelectedThread = null;
            }

            bool affectsDisplayedHistory = Threads.Any(thread => thread.Id == change.ThreadId)
                || ThreadRows.Any(row => row.Id == change.ThreadId)
                || linkedForkParentByChildId.ContainsKey(change.ThreadId)
                || linkedForkParentByChildId.Values.Contains(change.ThreadId, StringComparer.Ordinal)
                || (change.ChangeKind == "thread/section/changed"
                    && SelectedThreadSectionId is not null
                    && string.Equals(change.SectionId, SelectedThreadSectionId, StringComparison.Ordinal));
            refreshHistory = IsHistoryOpen && (removedLinkedChild || affectsDisplayedHistory);
        }).ConfigureAwait(false);

        if (refreshSections)
        {
            await LoadThreadSectionsAsync().ConfigureAwait(false);
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
