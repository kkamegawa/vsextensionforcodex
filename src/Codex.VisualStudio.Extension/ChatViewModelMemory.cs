using System.ComponentModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    private const int MaximumConversationMemoryCharacters = 64 * 1024;
    private AsyncCommand? readConversationMemoryCommand;
    private AsyncCommand? closeConversationMemoryCommand;
    private long conversationMemoryRequestGeneration;
    private string conversationMemoryText = string.Empty;
    private string conversationMemoryStatusText = string.Empty;
    private bool isConversationMemoryOpen;

    [DataMember]
    public AsyncCommand ReadConversationMemoryCommand
    {
        get
        {
            if (readConversationMemoryCommand is null)
            {
                readConversationMemoryCommand = new AsyncCommand(
                    ReadConversationMemoryAsync,
                    CanReadConversationMemory);
                PropertyChanged += OnConversationMemoryContextChanged;
            }

            return readConversationMemoryCommand;
        }
    }

    [DataMember]
    public AsyncCommand CloseConversationMemoryCommand
    {
        get => closeConversationMemoryCommand ??= new AsyncCommand(() =>
        {
            CloseConversationMemory();
            return Task.CompletedTask;
        });
    }
    [DataMember]
    public bool IsConversationMemoryFeatureAvailable
        => Status.FeatureTier >= CodexFeatureTier.Tier165
            && Status.Target?.ExperimentalApi == true;

    [DataMember]
    public bool IsConversationMemoryOpen
    {
        get => isConversationMemoryOpen;
        set
        {
            if (SetProperty(ref isConversationMemoryOpen, value) && !value)
            {
                Interlocked.Increment(ref conversationMemoryRequestGeneration);
            }
        }
    }

    [DataMember]
    public bool HasConversationMemoryText => conversationMemoryText.Length > 0;

    [DataMember]
    public string ConversationMemoryText
    {
        get => conversationMemoryText;
        private set
        {
            if (!string.Equals(conversationMemoryText, value, StringComparison.Ordinal))
            {
                conversationMemoryText = value;
                OnPropertyChanged(nameof(ConversationMemoryText));
                OnPropertyChanged(nameof(HasConversationMemoryText));
            }
        }
    }

    [DataMember]
    public string ConversationMemoryStatusText
    {
        get => conversationMemoryStatusText;
        private set => SetProperty(ref conversationMemoryStatusText, value);
    }

    private bool CanReadConversationMemory()
        => IsConversationMemoryFeatureAvailable
            && SelectedThread is not null
            && IsThreadJoined
            && Status.State == WorkerConnectionState.Ready;

    private async Task ReadConversationMemoryAsync()
    {
        ThreadSummary? thread = SelectedThread;
        if (thread is null || !CanReadConversationMemory())
        {
            return;
        }

        OwnerSnapshot owner = CaptureOwnerSnapshot();
        long requestGeneration = Interlocked.Increment(ref conversationMemoryRequestGeneration);
        string threadId = thread.Id;
        await OnUiAsync(() =>
        {
            if (IsCurrentConversationMemoryRequest(owner, threadId, requestGeneration))
            {
                IsConversationMemoryOpen = true;
                ConversationMemoryText = string.Empty;
                ConversationMemoryStatusText = "Reading this conversation's memory…";
            }
        }).ConfigureAwait(false);

        try
        {
            if (!IsCurrentConversationMemoryRequest(owner, threadId, requestGeneration))
            {
                return;
            }

            ThreadMemoryResult result = await bridge.GetThreadMemoryAsync(
                StampOwner(new GetThreadMemoryRequest { ThreadId = threadId }, owner),
                lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentConversationMemoryRequest(owner, threadId, requestGeneration)
                || !MatchesOwner(result, owner)
                || !string.Equals(result.ThreadId, threadId, StringComparison.Ordinal))
            {
                return;
            }

            await OnUiAsync(() =>
            {
                if (!IsCurrentConversationMemoryRequest(owner, threadId, requestGeneration))
                {
                    return;
                }

                if (!result.IsSupported)
                {
                    ConversationMemoryText = string.Empty;
                    ConversationMemoryStatusText = markdown.ToSafeText(
                        result.UnavailableReason ?? "Conversation memory is not available.").Trim();
                    return;
                }

                if (string.IsNullOrWhiteSpace(result.Summary))
                {
                    ConversationMemoryText = string.Empty;
                    ConversationMemoryStatusText = "No memory is available for this conversation.";
                    return;
                }

                string safeSummary = markdown.ToSafeText(result.Summary);
                if (safeSummary.Length > MaximumConversationMemoryCharacters)
                {
                    safeSummary = string.Concat(
                        safeSummary.AsSpan(0, MaximumConversationMemoryCharacters),
                        Environment.NewLine + "[Memory summary truncated]");
                }

                ConversationMemoryText = safeSummary;
                ConversationMemoryStatusText = string.Empty;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentConversationMemoryRequest(owner, threadId, requestGeneration))
            {
                return;
            }

            ExtensionDiagnostics.Write($"Conversation memory could not be read ({ex.GetType().Name}).");
            await OnUiAsync(() =>
            {
                if (IsCurrentConversationMemoryRequest(owner, threadId, requestGeneration))
                {
                    ConversationMemoryText = string.Empty;
                    ConversationMemoryStatusText = "Conversation memory could not be read.";
                }
            }).ConfigureAwait(false);
        }
    }

    private bool IsCurrentConversationMemoryRequest(OwnerSnapshot owner, string threadId, long generation)
        => IsCurrentOwner(owner)
            && generation == Volatile.Read(ref conversationMemoryRequestGeneration)
            && IsThreadJoined
            && string.Equals(SelectedThread?.Id, threadId, StringComparison.Ordinal);

    private void CloseConversationMemory()
    {
        IsConversationMemoryOpen = false;
        Interlocked.Increment(ref conversationMemoryRequestGeneration);
        ConversationMemoryText = string.Empty;
        ConversationMemoryStatusText = string.Empty;
    }

    private void OnConversationMemoryContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SelectedThread) or nameof(IsThreadJoined) or nameof(Status)))
        {
            return;
        }

        // Ordinary status churn (turn start, token updates) keeps the pane. Only a different
        // conversation, a lost join, a lost connection, or a lost capability invalidates what it shows.
        bool invalidated = args.PropertyName != nameof(Status)
            || !IsConversationMemoryFeatureAvailable
            || Status.State is not (WorkerConnectionState.Ready or WorkerConnectionState.Busy);
        if (invalidated)
        {
            CloseConversationMemory();
        }

        OnPropertyChanged(nameof(IsConversationMemoryFeatureAvailable));
        ReadConversationMemoryCommand.RaiseCanExecuteChanged();
    }


}