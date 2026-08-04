using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace Codex.VisualStudio.Extension;

public sealed record SkillSuggestionDescriptor(
    string Name,
    string DisplayName,
    string Scope,
    string? ShortDescription,
    bool Enabled);

/// <summary>
/// Owns Remote UI state for inline $ skill-mention discovery.
/// Catalog lookup and composer updates remain in the parent view model.
/// </summary>
[DataContract]
public sealed class SkillSuggestionPresentationViewModel : ObservableObject
{
    private readonly SafeMarkdownService markdown = new();
    private Func<SkillSuggestionViewModel, Task>? accepted;
    private SkillSuggestionViewModel? selectedSuggestion;
    private string statusAnnouncement = string.Empty;
    private bool isSuggestionOpen;

    public SkillSuggestionPresentationViewModel()
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
    public ObservableCollection<SkillSuggestionViewModel> Suggestions { get; } = [];

    [DataMember]
    public SkillSuggestionViewModel? SelectedSuggestion
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

    public void Configure(Func<SkillSuggestionViewModel, Task> onAccepted)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        accepted = onAccepted;
    }

    public void ShowSuggestions(IEnumerable<SkillSuggestionDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        Suggestions.Clear();
        foreach (SkillSuggestionDescriptor descriptor in descriptors)
        {
            Suggestions.Add(new SkillSuggestionViewModel(descriptor, AcceptSuggestionAsync, markdown));
        }

        SelectedSuggestion = Suggestions.FirstOrDefault();
        IsSuggestionOpen = Suggestions.Count > 0;
        StatusAnnouncement = Suggestions.Count switch
        {
            0 => "No matching skills.",
            1 => "1 skill available.",
            _ => $"{Suggestions.Count} skills available.",
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
        StatusAnnouncement = string.IsNullOrEmpty(SelectedSuggestion.ShortDescription)
            ? SelectedSuggestion.DisplayName
            : $"{SelectedSuggestion.DisplayName}: {SelectedSuggestion.ShortDescription}";
        return Task.CompletedTask;
    }

    private Task AcceptSelectedSuggestionAsync()
        => SelectedSuggestion is null
            ? Task.CompletedTask
            : AcceptSuggestionAsync(SelectedSuggestion);

    private async Task AcceptSuggestionAsync(SkillSuggestionViewModel suggestion)
    {
        CloseSuggestions();
        if (accepted is not null)
        {
            await accepted(suggestion).ConfigureAwait(false);
        }

        StatusAnnouncement = $"{suggestion.DisplayName} inserted.";
    }

    private Task DismissSuggestionsAsync()
    {
        CloseSuggestions();
        StatusAnnouncement = "Skill suggestions closed.";
        return Task.CompletedTask;
    }
}

[DataContract]
public sealed class SkillSuggestionViewModel
{
    private readonly Func<SkillSuggestionViewModel, Task> use;

    internal SkillSuggestionViewModel(
        SkillSuggestionDescriptor descriptor,
        Func<SkillSuggestionViewModel, Task> use,
        SafeMarkdownService markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        ArgumentNullException.ThrowIfNull(use);
        ArgumentNullException.ThrowIfNull(markdown);

        this.use = use;

        // The resolution key ChatViewModel.ResolveSkillToken matches against -- must stay the
        // raw skill name, never the sanitized display label, or accepting a suggestion could
        // insert a $token that no longer resolves to the skill the user picked.
        Name = descriptor.Name;
        string baseDisplayName = markdown.ToSafeText(descriptor.DisplayName).Trim();
        Enabled = descriptor.Enabled;
        DisplayName = Enabled ? baseDisplayName : $"{baseDisplayName} (disabled)";
        ShortDescription = markdown.ToSafeText(descriptor.ShortDescription ?? string.Empty).Trim();
        Scope = markdown.ToSafeText(descriptor.Scope).Trim();
        AutomationName = $"Insert skill {DisplayName}, {Scope}";
        UseCommand = new AsyncCommand(UseAsync);
    }

    /// <summary>
    /// Gets the raw skill name retained in the extension process to build the $&lt;name&gt;
    /// mention; never bound to a display element directly.
    /// </summary>
    public string Name { get; }

    [DataMember]
    public string DisplayName { get; }

    [DataMember]
    public string ShortDescription { get; }

    [DataMember]
    public string Scope { get; }

    [DataMember]
    public bool Enabled { get; }

    [DataMember]
    public string AutomationName { get; }

    [DataMember]
    public AsyncCommand UseCommand { get; }

    private Task UseAsync()
        => use(this);
}
