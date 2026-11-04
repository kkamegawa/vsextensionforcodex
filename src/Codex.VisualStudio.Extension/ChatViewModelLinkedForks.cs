using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    private AsyncCommand? createLinkedForkCommand;
    private AsyncCommand? loadRelatedConversationsCommand;
    private AsyncCommand? loadMoreRelatedConversationsCommand;
    private AsyncCommand? returnToLinkedParentCommand;
    private long linkedForkRequestGeneration;
    private string? relatedConversationsParentId;
    private string? relatedConversationsNextCursor;
    private string linkedForkStatusText = string.Empty;
    private bool linkedForkContextChangedSubscribed;

    [DataMember]
    public ObservableCollection<LinkedForkConversationViewModel> RelatedConversations { get; } = new();

    [DataMember]
    public AsyncCommand CreateLinkedForkCommand
    {
        get
        {
            if (createLinkedForkCommand is null)
            {
                createLinkedForkCommand = new AsyncCommand(CreateLinkedForkAsync, CanCreateLinkedFork);
                SubscribeLinkedForkContextChanged();
            }

            return createLinkedForkCommand;
        }
    }

    [DataMember]
    public AsyncCommand LoadRelatedConversationsCommand
    {
        get
        {
            if (loadRelatedConversationsCommand is null)
            {
                loadRelatedConversationsCommand = new AsyncCommand(LoadRelatedConversationsAsync, CanLoadRelatedConversations);
                SubscribeLinkedForkContextChanged();
            }

            return loadRelatedConversationsCommand;
        }
    }

    [DataMember]
    public AsyncCommand LoadMoreRelatedConversationsCommand
    {
        get
        {
            if (loadMoreRelatedConversationsCommand is null)
            {
                loadMoreRelatedConversationsCommand = new AsyncCommand(LoadMoreRelatedConversationsAsync, CanLoadMoreRelatedConversations);
                SubscribeLinkedForkContextChanged();
            }

            return loadMoreRelatedConversationsCommand;
        }
    }

    [DataMember]
    public AsyncCommand ReturnToLinkedParentCommand
    {
        get
        {
            if (returnToLinkedParentCommand is null)
            {
                returnToLinkedParentCommand = new AsyncCommand(ReturnToLinkedParentAsync, CanReturnToLinkedParent);
                SubscribeLinkedForkContextChanged();
            }

            return returnToLinkedParentCommand;
        }
    }

    [DataMember]
    public bool IsLinkedForkFeatureAvailable
        => Status.FeatureTier >= CodexFeatureTier.Tier166
            && Status.Target?.ExperimentalApi == true;

    [DataMember]
    public string LinkedForkStatusText
    {
        get => linkedForkStatusText;
        private set => SetProperty(ref linkedForkStatusText, value);
    }

    [DataMember]
    public bool HasRelatedConversations => RelatedConversations.Count > 0;

    [DataMember]
    public bool HasMoreRelatedConversations
        => !string.IsNullOrWhiteSpace(relatedConversationsParentId)
            && relatedConversationsNextCursor is not null;

    private void SubscribeLinkedForkContextChanged()
    {
        if (linkedForkContextChangedSubscribed)
        {
            return;
        }

        linkedForkContextChangedSubscribed = true;
        PropertyChanged += OnLinkedForkContextChanged;
    }

    private bool CanCreateLinkedFork()
        => IsLinkedForkFeatureAvailable
            && SelectedThread is not null
            && IsThreadJoined
            && Status.State == WorkerConnectionState.Ready
            && Status.TurnId is null;

    private bool CanLoadRelatedConversations()
        => IsLinkedForkFeatureAvailable
            && SelectedThread is not null
            && Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy;

    private bool CanLoadMoreRelatedConversations()
        => CanLoadRelatedConversations()
            && HasMoreRelatedConversations
            && string.Equals(relatedConversationsParentId, SelectedThread?.Id, StringComparison.Ordinal);

    private bool CanReturnToLinkedParent()
        => IsLinkedForkFeatureAvailable
            && !string.IsNullOrWhiteSpace(SelectedThread?.LinkedParentThreadId)
            && Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy;

    private async Task CreateLinkedForkAsync()
    {
        ThreadSummary? parent = SelectedThread;
        if (parent is null || !CanCreateLinkedFork())
        {
            return;
        }

        OwnerSnapshot owner = CaptureOwnerSnapshot();
        long requestGeneration = Interlocked.Increment(ref linkedForkRequestGeneration);
        LinkedForkStatusText = "Creating a linked conversation…";
        try
        {
            ForkThreadResult result = await bridge.ForkThreadAsync(
                StampOwner(new ForkThreadRequest
                {
                    ThreadId = parent.Id,
                    CreateLinkedFork = true,
                }, owner),
                lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentLinkedForkRequest(owner, parent.Id, requestGeneration))
            {
                return;
            }

            if (!result.IsSupported || result.Thread is null)
            {
                LinkedForkStatusText = markdown.ToSafeText(
                    result.UnavailableReason ?? "The app-server did not create a linked conversation.");
                return;
            }

            result.Thread.LinkedParentThreadId = parent.Id;
            await OnUiAsync(() =>
            {
                if (!IsCurrentLinkedForkRequest(owner, parent.Id, requestGeneration))
                {
                    return;
                }

                relatedConversationsParentId = parent.Id;
                LinkedForkConversationViewModel? existing = RelatedConversations.FirstOrDefault(
                    item => string.Equals(item.Thread.Id, result.Thread.Id, StringComparison.Ordinal));
                if (existing is null)
                {
                    RelatedConversations.Add(new LinkedForkConversationViewModel(result.Thread, markdown.ToSafeText(result.Thread.Preview ?? string.Empty), SelectRelatedConversationAsync));
                }

                OnPropertyChanged(nameof(HasRelatedConversations));
                OnPropertyChanged(nameof(HasMoreRelatedConversations));
                SelectedThread = result.Thread;
                LinkedForkStatusText = "Linked conversation created. Its history starts independently.";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentLinkedForkRequest(owner, parent.Id, requestGeneration))
            {
                return;
            }

            ExtensionDiagnostics.Write("Linked conversation could not be created", ex);
            LinkedForkStatusText = "The linked conversation could not be created. It requires a persistent paginated parent in local storage.";
        }
    }

    private async Task LoadRelatedConversationsAsync()
    {
        ThreadSummary? parent = SelectedThread;
        if (parent is null || !CanLoadRelatedConversations())
        {
            return;
        }

        Interlocked.Increment(ref linkedForkRequestGeneration);
        relatedConversationsParentId = parent.Id;
        relatedConversationsNextCursor = null;
        RelatedConversations.Clear();
        OnPropertyChanged(nameof(HasRelatedConversations));
        OnPropertyChanged(nameof(HasMoreRelatedConversations));
        LinkedForkStatusText = string.Empty;
        await LoadRelatedConversationsPageAsync(parent.Id, cursor: null).ConfigureAwait(false);
    }

    private async Task LoadMoreRelatedConversationsAsync()
    {
        string? parentId = relatedConversationsParentId;
        string? cursor = relatedConversationsNextCursor;
        if (parentId is null || cursor is null || !CanLoadMoreRelatedConversations())
        {
            return;
        }

        await LoadRelatedConversationsPageAsync(parentId, cursor).ConfigureAwait(false);
    }

    private async Task LoadRelatedConversationsPageAsync(string parentId, string? cursor)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        long requestGeneration = Volatile.Read(ref linkedForkRequestGeneration);
        try
        {
            ForkThreadResult result = await bridge.ForkThreadAsync(
                StampOwner(new ForkThreadRequest
                {
                    ThreadId = parentId,
                    ListLinkedForks = true,
                    Cursor = cursor,
                }, owner),
                lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentLinkedForkRequest(owner, parentId, requestGeneration)
                || !string.Equals(relatedConversationsParentId, parentId, StringComparison.Ordinal))
            {
                return;
            }

            if (!result.IsSupported)
            {
                LinkedForkStatusText = markdown.ToSafeText(
                    result.UnavailableReason ?? "Related conversations are not available.");
                return;
            }

            await OnUiAsync(() =>
            {
                if (!IsCurrentLinkedForkRequest(owner, parentId, requestGeneration)
                    || !string.Equals(relatedConversationsParentId, parentId, StringComparison.Ordinal))
                {
                    return;
                }

                foreach (ThreadSummary thread in result.RelatedThreads)
                {
                    thread.LinkedParentThreadId = parentId;
                    if (RelatedConversations.All(
                        item => !string.Equals(item.Thread.Id, thread.Id, StringComparison.Ordinal)))
                    {
                        RelatedConversations.Add(new LinkedForkConversationViewModel(thread, markdown.ToSafeText(thread.Preview ?? string.Empty), SelectRelatedConversationAsync));
                    }
                }

                relatedConversationsNextCursor = result.NextCursor;
                OnPropertyChanged(nameof(HasRelatedConversations));
                OnPropertyChanged(nameof(HasMoreRelatedConversations));
                LinkedForkStatusText = RelatedConversations.Count == 0
                    ? "This conversation has no linked conversations."
                    : string.Empty;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentLinkedForkRequest(owner, parentId, requestGeneration))
            {
                return;
            }

            ExtensionDiagnostics.Write("Related conversations could not be loaded", ex);
            LinkedForkStatusText = "Related conversations could not be loaded.";
        }
    }

    private Task SelectRelatedConversationAsync(ThreadSummary thread)
    {
        if (!IsLinkedForkFeatureAvailable)
        {
            return Task.CompletedTask;
        }

        SelectedThread = thread;
        IsHistoryOpen = true;
        return Task.CompletedTask;
    }

    private async Task ReturnToLinkedParentAsync()
    {
        string? parentId = SelectedThread?.LinkedParentThreadId;
        if (string.IsNullOrWhiteSpace(parentId) || !CanReturnToLinkedParent())
        {
            return;
        }

        ThreadSummary? parent = Threads.FirstOrDefault(item => string.Equals(item.Id, parentId, StringComparison.Ordinal));
        if (parent is null)
        {
            OwnerSnapshot owner = CaptureOwnerSnapshot();
            try
            {
                ThreadReadResult read = await bridge.ReadThreadAsync(
                    StampOwner(new ReadThreadRequest { ThreadId = parentId }, owner),
                    lifetime.Token).ConfigureAwait(false);
                if (!IsCurrentOwner(owner)
                    || !MatchesOwner(read, owner)
                    || !string.Equals(SelectedThread?.LinkedParentThreadId, parentId, StringComparison.Ordinal))
                {
                    return;
                }

                parent = read.Thread;
                Threads.Insert(0, parent);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ExtensionDiagnostics.Write("Linked conversation parent could not be opened", ex);
                LinkedForkStatusText = "The parent conversation could not be opened.";
                return;
            }
        }

        if (parent is not null)
        {
            await SelectRelatedConversationAsync(parent).ConfigureAwait(false);
            if (RelatedConversations.All(item => !string.Equals(item.Thread.Id, SelectedThread?.Id, StringComparison.Ordinal)))
            {
                await LoadRelatedConversationsAsync().ConfigureAwait(false);
            }
        }
    }

    private bool IsCurrentLinkedForkRequest(OwnerSnapshot owner, string threadId, long generation)
        => IsCurrentOwner(owner)
            && generation == Volatile.Read(ref linkedForkRequestGeneration)
            && string.Equals(SelectedThread?.Id, threadId, StringComparison.Ordinal);

    private void OnLinkedForkContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Status) or nameof(SelectedThread) or nameof(IsThreadJoined))
        {
            if (args.PropertyName == nameof(SelectedThread)
                && !string.Equals(SelectedThread?.Id, relatedConversationsParentId, StringComparison.Ordinal)
                && !string.Equals(SelectedThread?.LinkedParentThreadId, relatedConversationsParentId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref linkedForkRequestGeneration);
                RelatedConversations.Clear();
                relatedConversationsParentId = null;
                relatedConversationsNextCursor = null;
                LinkedForkStatusText = string.Empty;
                OnPropertyChanged(nameof(HasRelatedConversations));
                OnPropertyChanged(nameof(HasMoreRelatedConversations));
            }

            OnPropertyChanged(nameof(IsLinkedForkFeatureAvailable));
            CreateLinkedForkCommand.RaiseCanExecuteChanged();
            LoadRelatedConversationsCommand.RaiseCanExecuteChanged();
            LoadMoreRelatedConversationsCommand.RaiseCanExecuteChanged();
            ReturnToLinkedParentCommand.RaiseCanExecuteChanged();
        }
    }
}

[DataContract]
public sealed class LinkedForkConversationViewModel
{
    private readonly Func<ThreadSummary, Task> select;
    private readonly string preview;

    internal LinkedForkConversationViewModel(ThreadSummary thread, string preview, Func<ThreadSummary, Task> select)
    {
        Thread = thread;
        this.preview = preview;
        this.select = select;
        NavigateCommand = new AsyncCommand(() => this.select(Thread));
    }

    [DataMember]
    public ThreadSummary Thread { get; }

    [DataMember]
    public string Preview => string.IsNullOrWhiteSpace(preview) ? "(No preview)" : preview;

    [DataMember]
    public AsyncCommand NavigateCommand { get; }
}
