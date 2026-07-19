using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace Codex.VisualStudio.Extension;

public sealed record FileSuggestionDescriptor(
    string FullPath,
    string DisplayName,
    string RelativePath);

/// <summary>
/// Owns Remote UI state for inline file-reference discovery.
/// File search and composer updates remain in the parent view model.
/// </summary>
[DataContract]
public sealed class FileSuggestionPresentationViewModel : ObservableObject
{
    private readonly SafeMarkdownService markdown = new();
    private Func<FileSuggestionViewModel, Task>? accepted;
    private FileSuggestionViewModel? selectedSuggestion;
    private string statusAnnouncement = string.Empty;
    private bool isSuggestionOpen;

    public FileSuggestionPresentationViewModel()
    {
        MovePreviousCommand = new AsyncCommand(
            () => MoveSelectionAsync(-1),
            CanMoveSelection);
        MoveNextCommand = new AsyncCommand(
            () => MoveSelectionAsync(1),
            CanMoveSelection);
        AcceptSuggestionCommand = new AsyncCommand(
            AcceptSelectedSuggestionAsync,
            CanAcceptSuggestion);
        DismissSuggestionsCommand = new AsyncCommand(
            DismissSuggestionsAsync,
            () => IsSuggestionOpen);
    }

    [DataMember]
    public ObservableCollection<FileSuggestionViewModel> Suggestions { get; } = [];

    [DataMember]
    public FileSuggestionViewModel? SelectedSuggestion
    {
        get => selectedSuggestion;
        set
        {
            if (SetProperty(ref selectedSuggestion, value))
            {
                AcceptSuggestionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public bool IsSuggestionOpen
    {
        get => isSuggestionOpen;
        private set
        {
            if (SetProperty(ref isSuggestionOpen, value))
            {
                OnPropertyChanged(nameof(MovePreviousKeyCommand));
                OnPropertyChanged(nameof(MoveNextKeyCommand));
                OnPropertyChanged(nameof(AcceptSuggestionKeyCommand));
                OnPropertyChanged(nameof(DismissSuggestionsKeyCommand));
                MovePreviousCommand.RaiseCanExecuteChanged();
                MoveNextCommand.RaiseCanExecuteChanged();
                AcceptSuggestionCommand.RaiseCanExecuteChanged();
                DismissSuggestionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public string StatusAnnouncement
    {
        get => statusAnnouncement;
        private set
        {
            string safeValue = markdown.ToSafeText(value).Trim();
            if (SetProperty(ref statusAnnouncement, safeValue))
            {
                OnPropertyChanged(nameof(HasStatusAnnouncement));
            }
        }
    }

    [DataMember]
    public bool HasStatusAnnouncement => !string.IsNullOrWhiteSpace(StatusAnnouncement);

    [DataMember]
    public AsyncCommand MovePreviousCommand { get; }

    [DataMember]
    public AsyncCommand MoveNextCommand { get; }

    [DataMember]
    public AsyncCommand AcceptSuggestionCommand { get; }

    [DataMember]
    public AsyncCommand DismissSuggestionsCommand { get; }

    // Null key commands let ordinary multiline TextBox gestures continue when the list is closed.
    [DataMember]
    public AsyncCommand? MovePreviousKeyCommand => IsSuggestionOpen ? MovePreviousCommand : null;

    [DataMember]
    public AsyncCommand? MoveNextKeyCommand => IsSuggestionOpen ? MoveNextCommand : null;

    [DataMember]
    public AsyncCommand? AcceptSuggestionKeyCommand => IsSuggestionOpen ? AcceptSuggestionCommand : null;

    [DataMember]
    public AsyncCommand? DismissSuggestionsKeyCommand => IsSuggestionOpen ? DismissSuggestionsCommand : null;

    public void Configure(Func<FileSuggestionViewModel, Task> onAccepted)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        accepted = onAccepted;
    }

    public void ShowSuggestions(IEnumerable<FileSuggestionDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        Suggestions.Clear();
        foreach (FileSuggestionDescriptor descriptor in descriptors)
        {
            Suggestions.Add(new FileSuggestionViewModel(descriptor, AcceptSuggestionAsync, markdown));
        }

        SelectedSuggestion = Suggestions.FirstOrDefault();
        IsSuggestionOpen = Suggestions.Count > 0;
        StatusAnnouncement = Suggestions.Count switch
        {
            0 => "No matching files.",
            1 => "1 file available.",
            _ => $"{Suggestions.Count} files available.",
        };
    }

    public void CloseSuggestions()
    {
        IsSuggestionOpen = false;
        SelectedSuggestion = null;
        StatusAnnouncement = string.Empty;
    }

    private bool CanMoveSelection()
        => IsSuggestionOpen && Suggestions.Count > 0;

    private bool CanAcceptSuggestion()
        => IsSuggestionOpen && SelectedSuggestion is not null;

    private Task MoveSelectionAsync(int offset)
    {
        if (!CanMoveSelection())
        {
            return Task.CompletedTask;
        }

        int currentIndex = SelectedSuggestion is null
            ? -1
            : Suggestions.IndexOf(SelectedSuggestion);
        int nextIndex = currentIndex < 0
            ? 0
            : (currentIndex + offset + Suggestions.Count) % Suggestions.Count;
        SelectedSuggestion = Suggestions[nextIndex];
        StatusAnnouncement = $"{SelectedSuggestion.DisplayName}: {SelectedSuggestion.RelativePath}";
        return Task.CompletedTask;
    }

    private Task AcceptSelectedSuggestionAsync()
        => SelectedSuggestion is null
            ? Task.CompletedTask
            : AcceptSuggestionAsync(SelectedSuggestion);

    private async Task AcceptSuggestionAsync(FileSuggestionViewModel suggestion)
    {
        CloseSuggestions();
        if (accepted is not null)
        {
            await accepted(suggestion).ConfigureAwait(false);
        }

        StatusAnnouncement = $"{suggestion.DisplayName} attached.";
    }

    private Task DismissSuggestionsAsync()
    {
        CloseSuggestions();
        StatusAnnouncement = "File suggestions closed.";
        return Task.CompletedTask;
    }
}

[DataContract]
public sealed class FileSuggestionViewModel
{
    private readonly Func<FileSuggestionViewModel, Task> use;

    internal FileSuggestionViewModel(
        FileSuggestionDescriptor descriptor,
        Func<FileSuggestionViewModel, Task> use,
        SafeMarkdownService markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.FullPath);
        ArgumentNullException.ThrowIfNull(use);
        ArgumentNullException.ThrowIfNull(markdown);

        this.use = use;
        FullPath = descriptor.FullPath;
        DisplayName = markdown.ToSafeText(descriptor.DisplayName).Trim();
        RelativePath = markdown.ToSafeText(descriptor.RelativePath).Trim();
        AutomationName = string.IsNullOrWhiteSpace(RelativePath)
            ? $"Attach {DisplayName}"
            : $"Attach {DisplayName}, {RelativePath}";
        UseCommand = new AsyncCommand(UseAsync);
    }

    /// <summary>
    /// Gets the trusted path retained in the extension process for attachment creation.
    /// </summary>
    public string FullPath { get; }

    [DataMember]
    public string DisplayName { get; }

    [DataMember]
    public string RelativePath { get; }

    [DataMember]
    public string AutomationName { get; }

    [DataMember]
    public AsyncCommand UseCommand { get; }

    private Task UseAsync()
        => use(this);
}
