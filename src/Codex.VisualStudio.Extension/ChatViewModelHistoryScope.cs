using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class ThreadListScopeOption
{
    internal ThreadListScopeOption(string id, string displayText)
    {
        Id = id;
        DisplayText = displayText;
    }

    [DataMember]
    public string Id { get; }

    [DataMember]
    public string DisplayText { get; }
}

// The history list scope. The folders are fixed when a scope is chosen, so every page of one list
// uses the same set, and a conversation outside the scope is simply not listed.
public sealed partial class ChatViewModel
{
    internal const string AllConversationsScopeId = "all";
    internal const string CurrentFolderScopeId = "currentFolder";
    internal const string RepositoryWorktreesScopeId = "repositoryWorktrees";
    private const int MinimumWorktreesForScope = 2;

    private string selectedThreadListScopeId = AllConversationsScopeId;
    private ThreadListScopesResult? threadListScopes;
    private OwnerSnapshot? threadListScopesOwner;
    private IReadOnlyList<string>? threadListWorkingDirectories;
    private long threadListScopeGeneration;
    private int threadListReloadPending;

    [DataMember]
    public ObservableCollection<ThreadListScopeOption> ThreadListScopes { get; } = [];

    private void InitializeThreadListScopeOptions()
    {
        ThreadListScopes.Add(new ThreadListScopeOption(AllConversationsScopeId, IsJapaneseUi ? "すべて" : "All"));
    }

    [DataMember]
    public string ThreadListScopeToolTipText => IsJapaneseUi ? "会話履歴の表示範囲を選択" : "Conversations to list";
    [DataMember]
    public string ThreadListScopeAutomationName => IsJapaneseUi ? "会話履歴の範囲" : "Conversation history scope";
    [DataMember]
    public string ThreadListScopeHelpText => IsJapaneseUi ? "履歴一覧に表示する会話の範囲を選択します。" : "Choose which conversations the history lists.";

    [DataMember]
    public string SelectedThreadListScopeId
    {
        get => selectedThreadListScopeId;
        set
        {
            if (value is not null && SetProperty(ref selectedThreadListScopeId, value))
            {
                _ = ApplyThreadListScopeAsync(value);
            }
        }
    }

    private async Task RefreshThreadListScopesAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (threadListScopesOwner == owner)
        {
            return;
        }

        try
        {
            ThreadListScopesResult scopes = await bridge.GetThreadListScopesAsync(
                StampOwner(new GetThreadListScopesRequest(), owner), lifetime.Token).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner))
                {
                    return;
                }

                threadListScopes = scopes;
                threadListScopesOwner = owner;
                ThreadListScopes.Clear();
                ThreadListScopes.Add(new ThreadListScopeOption(AllConversationsScopeId, IsJapaneseUi ? "すべて" : "All"));
                if (scopes.CurrentFolder is not null)
                {
                    ThreadListScopes.Add(new ThreadListScopeOption(CurrentFolderScopeId, IsJapaneseUi ? "現在の作業フォルダー" : "Current working folder"));
                }

                if (!IsRemoteTarget && scopes.WorktreeFolders.Count >= MinimumWorktreesForScope)
                {
                    ThreadListScopes.Add(new ThreadListScopeOption(RepositoryWorktreesScopeId, IsJapaneseUi ? "同じリポジトリのワークツリー" : "Same repository worktrees"));
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // All stays available, and the next time the history opens tries again.
            ExtensionDiagnostics.Write($"History scope lookup failed ({ex.GetType().Name}).");
        }
    }

    private async Task ApplyThreadListScopeAsync(string scopeId)
    {
        try
        {
            await OnUiAsync(() =>
            {
                threadListWorkingDirectories = scopeId switch
                {
                    CurrentFolderScopeId when threadListScopes?.CurrentFolder is { } folder => [folder],
                    RepositoryWorktreesScopeId when threadListScopes is { WorktreeFolders.Count: >= MinimumWorktreesForScope } scopes => scopes.WorktreeFolders,
                    _ => null,
                };
                Interlocked.Increment(ref threadListScopeGeneration);
                CancelHistorySearchAndAdvanceGeneration();
                nextCursor = null;
                RaiseCommandStates();
            }).ConfigureAwait(false);

            if (settings.ExperimentalApiEnabled && !string.IsNullOrWhiteSpace(historySearchText))
            {
                await SearchHistoryAsync().ConfigureAwait(false);
                return;
            }

            OwnerSnapshot owner = CaptureOwnerSnapshot();
            ThreadListRefreshOutcome outcome = await RefreshCompleteThreadListAsync(
                owner,
                "Conversation list refreshed. Select one to inspect read-only history.",
                lifetime.Token).ConfigureAwait(false);
            if (outcome == ThreadListRefreshOutcome.Busy)
            {
                // Queue while holding the same gate used to retire the active lease. If it
                // finished between the Busy result and this point, start the refresh here.
                if (!QueueThreadListRefreshIfBusy(owner))
                {
                    outcome = await RefreshCompleteThreadListAsync(
                        owner,
                        "Conversation list refreshed. Select one to inspect read-only history.",
                        lifetime.Token).ConfigureAwait(false);
                    if (outcome == ThreadListRefreshOutcome.Busy && !QueueThreadListRefreshIfBusy(owner))
                    {
                        await SetThreadListFailureStatusAsync(
                            owner,
                            IsJapaneseUi ? "会話一覧の更新が混み合っています。もう一度お試しください。" : "Conversation list refresh is busy. Try again.").ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Applying the history scope failed", ex);
        }
    }

    private async Task ReloadPendingThreadListAsync()
    {
        if (Interlocked.Exchange(ref threadListReloadPending, 0) != 0)
        {
            await ReloadThreadsAsync().ConfigureAwait(false);
        }
    }

    // Filtering never closes the open conversation, so its row stays even outside the scope.
    private void KeepSelectedThreadRow()
    {
        if (threadListWorkingDirectories is not null
            && selectedThread is { } selected
            && string.IsNullOrWhiteSpace(selected.LinkedParentThreadId)
            && !Threads.Any(thread => string.Equals(thread.Id, selected.Id, StringComparison.Ordinal)))
        {
            Threads.Insert(0, selected);
        }
    }

    private void ResetThreadListScope()
    {
        threadListScopes = null;
        threadListScopesOwner = null;
        threadListWorkingDirectories = null;
        Interlocked.Increment(ref threadListScopeGeneration);
        Volatile.Write(ref threadListReloadPending, 0);
        ThreadListScopes.Clear();
        ThreadListScopes.Add(new ThreadListScopeOption(AllConversationsScopeId, IsJapaneseUi ? "すべて" : "All"));
        selectedThreadListScopeId = AllConversationsScopeId;
        OnPropertyChanged(nameof(SelectedThreadListScopeId));
    }
}
