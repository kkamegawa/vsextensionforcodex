using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    private const int TranscriptSearchPageSize = 100;
    private CancellationTokenSource? transcriptSearchCancellation;
    private long transcriptSearchGeneration;
    private string transcriptSearchText = string.Empty;
    private string transcriptSearchStatusText = string.Empty;
    private bool isTranscriptSearchOpen;
    private bool isTranscriptSearchLoading;
    private TranscriptSearchResultViewModel? selectedTranscriptSearchResult;

    [DataMember]
    public string TranscriptSearchText
    {
        get => transcriptSearchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetProperty(ref transcriptSearchText, normalized))
            {
                InvalidateTranscriptSearch(clearResults: true);
                SearchTranscriptCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public bool IsTranscriptSearchOpen
    {
        get => isTranscriptSearchOpen;
        private set => SetProperty(ref isTranscriptSearchOpen, value);
    }

    [DataMember]
    public bool IsTranscriptSearchLoading
    {
        get => isTranscriptSearchLoading;
        private set
        {
            if (SetProperty(ref isTranscriptSearchLoading, value))
            {
                SearchTranscriptCommand.RaiseCanExecuteChanged();
                CancelTranscriptSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public string TranscriptSearchStatusText
    {
        get => transcriptSearchStatusText;
        private set
        {
            if (SetProperty(ref transcriptSearchStatusText, value))
            {
                OnPropertyChanged(nameof(HasTranscriptSearchStatus));
            }
        }
    }

    [DataMember]
    public bool HasTranscriptSearchStatus => TranscriptSearchStatusText.Length > 0;

    [DataMember]
    public ObservableCollection<TranscriptSearchResultViewModel> TranscriptSearchResults { get; } = new();

    [DataMember]
    public TranscriptSearchResultViewModel? SelectedTranscriptSearchResult
    {
        get => selectedTranscriptSearchResult;
        set => SetProperty(ref selectedTranscriptSearchResult, value);
    }

    [DataMember]
    public AsyncCommand OpenTranscriptSearchCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand SearchTranscriptCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand CancelTranscriptSearchCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand CloseTranscriptSearchCommand { get; private set; } = null!;

    private void InitializeTranscriptSearch()
    {
        OpenTranscriptSearchCommand = new AsyncCommand(() =>
        {
            IsTranscriptSearchOpen = true;
            return Task.CompletedTask;
        });
        SearchTranscriptCommand = new AsyncCommand(SearchTranscriptAsync, CanSearchTranscript);
        CancelTranscriptSearchCommand = new AsyncCommand(() =>
        {
            transcriptSearchCancellation?.Cancel();
            return Task.CompletedTask;
        }, () => IsTranscriptSearchLoading);
        CloseTranscriptSearchCommand = new AsyncCommand(() =>
        {
            IsTranscriptSearchOpen = false;
            InvalidateTranscriptSearch(clearResults: true);
            return Task.CompletedTask;
        });
    }

    private bool CanSearchTranscript()
        => SelectedThread is not null && TranscriptSearchText.Trim().Length > 0 && !IsTranscriptSearchLoading;

    private async Task SearchTranscriptAsync()
    {
        ThreadSummary? thread = SelectedThread;
        string query = TranscriptSearchText.Trim();
        if (thread is null || query.Length == 0 || IsTranscriptSearchLoading)
        {
            return;
        }

        InvalidateTranscriptSearch(clearResults: true);
        long searchGeneration = transcriptSearchGeneration;
        long selectionGeneration = Interlocked.Read(ref selectedThreadGeneration);
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        transcriptSearchCancellation = cancellation;
        await OnUiAsync(() =>
        {
            IsTranscriptSearchOpen = true;
            IsTranscriptSearchLoading = true;
            TranscriptSearchStatusText = "Searching the full conversation…";
        }).ConfigureAwait(false);

        try
        {
            var matches = new List<TranscriptSearchResultViewModel>();
            var historyItemIdentities = new HashSet<string>(StringComparer.Ordinal);
            ThreadItemCursor? cursor = null;
            int scannedItemCount = 0;
            do
            {
                cancellation.Token.ThrowIfCancellationRequested();
                ThreadItemsPage page = await bridge.ListThreadItemsAsync(
                    StampOwner(new ListThreadItemsRequest { ThreadId = thread.Id, Cursor = cursor, Limit = TranscriptSearchPageSize }, owner),
                    cancellation.Token).ConfigureAwait(false);
                if (!IsCurrentOwner(owner)
                    || !MatchesOwner(page, owner)
                    || Interlocked.Read(ref selectedThreadGeneration) != selectionGeneration
                    || !string.Equals(SelectedThread?.Id, thread.Id, StringComparison.Ordinal)
                    || searchGeneration != Interlocked.Read(ref transcriptSearchGeneration))
                {
                    throw new OperationCanceledException(cancellation.Token);
                }

                foreach (ThreadHistoryItem item in page.Items)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    scannedItemCount++;
                    if (item.Id is not null)
                    {
                        historyItemIdentities.Add(GetTranscriptSearchIdentity(item.TurnId, item.Id));
                    }

                    string safeText = markdown.ToSafeText(item.Text ?? string.Empty);
                    int matchCount = CountMatches(safeText, query);
                    if (matchCount > 0)
                    {
                        matches.Add(new TranscriptSearchResultViewModel(
                            GetHistoryRole(item),
                            safeText,
                            item.TurnId,
                            item.Id,
                            matchCount));
                    }
                }

                cursor = page.NextCursor is null ? null : new ThreadItemCursor { Kind = ThreadItemCursorKind.Opaque, Value = page.NextCursor };
                int progressCount = scannedItemCount;
                await OnUiAsync(() =>
                {
                    if (searchGeneration == Interlocked.Read(ref transcriptSearchGeneration))
                    {
                        TranscriptSearchStatusText = $"Searching the full conversation… {progressCount} items";
                    }
                }).ConfigureAwait(false);
            }
            while (cursor is not null);

            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentOwner(owner)
                || Interlocked.Read(ref selectedThreadGeneration) != selectionGeneration
                || !string.Equals(SelectedThread?.Id, thread.Id, StringComparison.Ordinal)
                || searchGeneration != Interlocked.Read(ref transcriptSearchGeneration))
            {
                throw new OperationCanceledException(cancellation.Token);
            }

            ChatItemViewModel[] currentTranscript = [];
            await OnUiAsync(() => currentTranscript = Items.ToArray()).ConfigureAwait(false);
            matches.Reverse();
            int liveItemCount = 0;
            foreach (ChatItemViewModel item in currentTranscript)
            {
                if ((item.ThreadId is null || string.Equals(item.ThreadId, thread.Id, StringComparison.Ordinal))
                    && (item.ItemId is null || !historyItemIdentities.Contains(GetTranscriptSearchIdentity(item.TurnId, item.ItemId))))
                {
                    liveItemCount++;
                    string safeText = markdown.ToSafeText(item.Text);
                    int matchCount = CountMatches(safeText, query);
                    if (matchCount > 0)
                    {
                        matches.Add(new TranscriptSearchResultViewModel(item.Role, safeText, item.TurnId, item.ItemId, matchCount));
                    }
                }
            }

            scannedItemCount += liveItemCount;
            await OnUiAsync(() =>
            {
                if (searchGeneration != Interlocked.Read(ref transcriptSearchGeneration))
                {
                    return;
                }

                for (int index = 0; index < matches.Count; index++)
                {
                    matches[index].PositionText = $"Result {index + 1} of {matches.Count}";
                    TranscriptSearchResults.Add(matches[index]);
                }

                SelectedTranscriptSearchResult = TranscriptSearchResults.FirstOrDefault();
                TranscriptSearchStatusText = matches.Count == 0
                    ? $"No matches in {scannedItemCount} conversation items."
                    : $"{matches.Count} matching conversation items found across {scannedItemCount} items.";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || lifetime.IsCancellationRequested)
        {
            await SetTranscriptSearchInterruptedStatusAsync(searchGeneration, "Search canceled. Run it again to search the full conversation.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SetTranscriptSearchInterruptedStatusAsync(searchGeneration, "The conversation changed during search. Run it again for complete results.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Full conversation search failed", ex);
            await SetTranscriptSearchInterruptedStatusAsync(searchGeneration, "Search failed before the full conversation was read. No partial results are shown; try again.").ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(transcriptSearchCancellation, cancellation))
            {
                transcriptSearchCancellation = null;
                await OnUiAsync(() =>
                {
                    if (searchGeneration == Interlocked.Read(ref transcriptSearchGeneration))
                    {
                        IsTranscriptSearchLoading = false;
                    }
                }).ConfigureAwait(false);
            }
        }
    }

    private static int CountMatches(string text, string query)
    {
        int count = 0;
        int startIndex = 0;
        while (startIndex <= text.Length - query.Length)
        {
            int index = text.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            count++;
            startIndex = index + Math.Max(query.Length, 1);
        }

        return count;
    }

    private static string GetTranscriptSearchIdentity(string? turnId, string itemId)
        => string.Concat(turnId ?? string.Empty, "\u001F", itemId);

    private Task SetTranscriptSearchInterruptedStatusAsync(long searchGeneration, string status)
        => OnUiAsync(() =>
        {
            if (searchGeneration == Interlocked.Read(ref transcriptSearchGeneration))
            {
                TranscriptSearchResults.Clear();
                SelectedTranscriptSearchResult = null;
                TranscriptSearchStatusText = status;
            }
        });

    private void ResetTranscriptSearchForThreadChange()
    {
        InvalidateTranscriptSearch(clearResults: true);
        TranscriptSearchText = string.Empty;
        TranscriptSearchStatusText = string.Empty;
    }

    private void InvalidateTranscriptSearch(bool clearResults)
    {
        Interlocked.Increment(ref transcriptSearchGeneration);
        transcriptSearchCancellation?.Cancel();
        IsTranscriptSearchLoading = false;
        if (clearResults)
        {
            TranscriptSearchResults.Clear();
            SelectedTranscriptSearchResult = null;
            TranscriptSearchStatusText = string.Empty;
        }
    }
}

[DataContract]
public sealed class TranscriptSearchResultViewModel : ObservableObject
{
    private string positionText;

    public TranscriptSearchResultViewModel(string role, string text, string? turnId, string? itemId, int matchCount)
    {
        Role = role;
        Text = text;
        TurnId = turnId;
        ItemId = itemId;
        MatchCount = matchCount;
        positionText = string.Empty;
    }

    [DataMember]
    public string Role { get; }

    [DataMember]
    public string Text { get; }

    [DataMember]
    public string? TurnId { get; }

    [DataMember]
    public string? ItemId { get; }

    [DataMember]
    public int MatchCount { get; }

    [DataMember]
    public string PositionText
    {
        get => positionText;
        set => SetProperty(ref positionText, value);
    }
}
