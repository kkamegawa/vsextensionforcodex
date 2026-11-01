using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

// Open conversation text: a fixed snapshot of the whole history in a read-only editor tab. A failure
// or cancellation opens nothing, so a partial conversation is never presented as complete.
public sealed partial class ChatViewModel
{
    private const int ConversationTextPageSize = 100;
    private const int MaximumCaptionLength = 60;

    private readonly ConversationTextStore conversationTextStore = new();
    private CancellationTokenSource? conversationTextCancellation;
    private bool isConversationTextLoading;
    private string conversationTextStatusText = string.Empty;

    internal IConversationTextDocuments ConversationTextDocuments { get; set; } = new ConversationTextDocuments(null);

    [DataMember]
    public AsyncCommand OpenConversationTextCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand CancelConversationTextCommand { get; private set; } = null!;

    [DataMember]
    public bool IsConversationTextLoading
    {
        get => isConversationTextLoading;
        private set
        {
            if (SetProperty(ref isConversationTextLoading, value))
            {
                OpenConversationTextCommand.RaiseCanExecuteChanged();
                CancelConversationTextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public string ConversationTextStatusText
    {
        get => conversationTextStatusText;
        private set
        {
            if (SetProperty(ref conversationTextStatusText, value))
            {
                OnPropertyChanged(nameof(HasConversationTextStatus));
            }
        }
    }

    [DataMember]
    public bool HasConversationTextStatus => ConversationTextStatusText.Length > 0;

    private void InitializeConversationText()
    {
        OpenConversationTextCommand = new AsyncCommand(
            OpenConversationTextAsync,
            () => SelectedThread is not null && !IsConversationTextLoading && Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy);
        CancelConversationTextCommand = new AsyncCommand(() =>
        {
            conversationTextCancellation?.Cancel();
            return Task.CompletedTask;
        }, () => IsConversationTextLoading);
        _ = Task.Run(() => conversationTextStore.RemoveUnused(utcNow()));
    }

    private async Task OpenConversationTextAsync()
    {
        ThreadSummary? thread = SelectedThread;
        if (thread is null || IsConversationTextLoading)
        {
            return;
        }

        OwnerSnapshot owner = CaptureOwnerSnapshot();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        conversationTextCancellation = cancellation;
        await OnUiAsync(() =>
        {
            IsConversationTextLoading = true;
            ConversationTextStatusText = "Reading the conversation…";
        }).ConfigureAwait(false);
        string? path = null;
        try
        {
            List<ThreadHistoryItem> items = await ReadWholeHistoryAsync(thread.Id, owner, cancellation.Token).ConfigureAwait(false);
            items.Reverse();
            string title = GetConversationTextTitle(thread);
            string text = ConversationTextFormatter.Format(title, utcNow(), items, markdown);
            path = conversationTextStore.Write(ToArtifactOwner(owner), text);
            await ConversationTextDocuments.OpenAsync(path, title.Replace("%", string.Empty, StringComparison.Ordinal), conversationTextStore.Remove, cancellation.Token).ConfigureAwait(false);
            await SetConversationTextStatusAsync(string.Empty).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            RemoveUnopenedSnapshot(path);
        }
        catch (OperationCanceledException)
        {
            RemoveUnopenedSnapshot(path);
            await SetConversationTextStatusAsync("Opening the conversation text was canceled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Conversation text could not be opened", ex);
            RemoveUnopenedSnapshot(path);
            await SetConversationTextStatusAsync("The conversation text could not be opened. Nothing incomplete was shown; try again.").ConfigureAwait(false);
        }
        finally
        {
            conversationTextCancellation = null;
            if (!lifetime.IsCancellationRequested)
            {
                await OnUiAsync(() => IsConversationTextLoading = false).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<ThreadHistoryItem>> ReadWholeHistoryAsync(string threadId, OwnerSnapshot owner, CancellationToken cancellationToken)
    {
        var items = new List<ThreadHistoryItem>();
        ThreadItemCursor? cursor = null;
        do
        {
            ThreadItemsPage page = await bridge.ListThreadItemsAsync(
                StampOwner(new ListThreadItemsRequest { ThreadId = threadId, Cursor = cursor, Limit = ConversationTextPageSize }, owner),
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrentOwner(owner) || !MatchesOwner(page, owner))
            {
                throw new InvalidOperationException("The connection changed while reading the conversation.");
            }

            items.AddRange(page.Items);
            cursor = page.NextCursor is null ? null : new ThreadItemCursor { Kind = ThreadItemCursorKind.Opaque, Value = page.NextCursor };
            int count = items.Count;
            await SetConversationTextStatusAsync($"Reading the conversation… {count} items").ConfigureAwait(false);
        }
        while (cursor is not null);
        return items;
    }

    private string GetConversationTextTitle(ThreadSummary thread)
    {
        string title = markdown.ToSafeText(thread.Preview ?? string.Empty)
            .ReplaceLineEndings(" ")
            .Trim();
        if (title.Length > MaximumCaptionLength)
        {
            title = title[..MaximumCaptionLength].TrimEnd();
        }

        return title.Length == 0 ? "Conversation text" : $"Conversation: {title}";
    }

    private void RemoveUnopenedSnapshot(string? path)
    {
        if (path is not null)
        {
            conversationTextStore.Remove(path);
        }
    }

    private Task SetConversationTextStatusAsync(string text)
        => OnUiAsync(() => ConversationTextStatusText = text);

    private static ArtifactPreviewOwner ToArtifactOwner(OwnerSnapshot owner)
        => new(owner.StatePartitionFingerprint, owner.OwnerGeneration, owner.ConnectionGeneration);

    internal static string GetHistoryRole(ThreadHistoryItem item)
    {
        string type = item.Type.ToLowerInvariant();
        return item.ErrorReason is not null ? "Error"
            : type.Contains("user", StringComparison.Ordinal) ? "You"
            : type.Contains("command", StringComparison.Ordinal) ? "Command"
            : type.Contains("reason", StringComparison.Ordinal) ? "Reasoning"
            : type.Contains("agent", StringComparison.Ordinal) || type.Contains("assistant", StringComparison.Ordinal) ? "Codex"
            : "History";
    }
}
