using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Windows;

namespace Codex.VisualStudio.Extension;

public sealed record SlashCommandOptionDescriptor(
    string Value,
    string DisplayText,
    string Description = "");

public sealed record SlashCommandSuggestionDescriptor(
    string CommandName,
    string Description,
    string ArgumentHint = "",
    bool ShowArgumentInput = false,
    bool IsAvailable = true,
    string UnavailableReason = "",
    IReadOnlyList<SlashCommandOptionDescriptor>? Options = null,
    bool IsSkill = false,
    string ScopeLabel = "",
    string SelectionId = "",
    string BrandColor = "",
    bool IsSelectable = true);

public sealed record SlashCommandSubmission(
    string CommandName,
    string ArgumentText,
    string? OptionValue);

/// <summary>
/// Owns only the Remote UI state for slash-command discovery and argument entry.
/// Parsing, routing, validation, and App Server calls remain in the parent view model.
/// </summary>
[DataContract]
public sealed class SlashCommandPresentationViewModel : ObservableObject
{
    private readonly SafeMarkdownService markdown = new();
    private Func<SlashCommandSuggestionViewModel, Task>? suggestionAccepted;
    private Func<SlashCommandSubmission, Task<bool>>? executeRequested;
    private Func<Task>? cleared;
    private SlashCommandSuggestionViewModel? selectedSuggestion;
    private SlashCommandSuggestionViewModel? activeCommand;
    private SlashCommandOptionViewModel? selectedOption;
    private string argumentText = string.Empty;
    private string statusAnnouncement = string.Empty;
    private bool isSuggestionOpen;
    private bool showArgumentInput;

    public SlashCommandPresentationViewModel()
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
        ClearCommandCommand = new AsyncCommand(
            ClearActiveCommandAsync,
            () => HasActiveCommand);
        ExecuteCommand = new AsyncCommand(
            ExecuteActiveCommandAsync,
            () => HasActiveCommand);
    }

    [DataMember]
    public ObservableCollection<SlashCommandSuggestionViewModel> Suggestions { get; } = [];

    [DataMember]
    public ObservableCollection<SlashCommandOptionViewModel> Options { get; } = [];

    [DataMember]
    public SlashCommandSuggestionViewModel? SelectedSuggestion
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
    public SlashCommandSuggestionViewModel? ActiveCommand
    {
        get => activeCommand;
        private set
        {
            if (SetProperty(ref activeCommand, value))
            {
                OnPropertyChanged(nameof(HasActiveCommand));
                OnPropertyChanged(nameof(HasNoActiveCommand));
                OnPropertyChanged(nameof(ExecuteKeyCommand));
                OnPropertyChanged(nameof(ClearKeyCommand));
                ClearCommandCommand.RaiseCanExecuteChanged();
                ExecuteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public SlashCommandOptionViewModel? SelectedOption
    {
        get => selectedOption;
        private set => SetProperty(ref selectedOption, value);
    }

    [DataMember]
    public string ArgumentText
    {
        get => argumentText;
        set
        {
            if (string.Equals(argumentText, value, StringComparison.Ordinal))
            {
                return;
            }

            // Do not echo TextBox.Text back through the Remote UI proxy while the user types.
            // Programmatic changes use SetArgumentText so the VS-side TextBox still updates.
            argumentText = value;
            ExecuteCommand.RaiseCanExecuteChanged();
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
    public bool IsSuggestionOpen
    {
        get => isSuggestionOpen;
        private set
        {
            if (SetProperty(ref isSuggestionOpen, value))
            {
                RaiseSuggestionCommandStates();
            }
        }
    }

    [DataMember]
    public bool HasActiveCommand => ActiveCommand is not null;

    [DataMember]
    public bool HasNoActiveCommand => ActiveCommand is null;

    [DataMember]
    public bool HasFixedOptions => Options.Count > 0;

    [DataMember]
    public bool ShowArgumentInput
    {
        get => showArgumentInput;
        private set => SetProperty(ref showArgumentInput, value);
    }

    [DataMember]
    public AsyncCommand MovePreviousCommand { get; }

    [DataMember]
    public AsyncCommand MoveNextCommand { get; }

    [DataMember]
    public AsyncCommand AcceptSuggestionCommand { get; }

    [DataMember]
    public AsyncCommand DismissSuggestionsCommand { get; }

    [DataMember]
    public AsyncCommand ClearCommandCommand { get; }

    [DataMember]
    public AsyncCommand ExecuteCommand { get; }

    // KeyBindings must become null while the inline list is closed. Binding a permanently
    // present command whose CanExecute is false can still consume the gesture in WPF, which
    // would break normal Enter, Tab, and caret navigation in the multiline composer.
    [DataMember]
    public AsyncCommand? MovePreviousKeyCommand => IsSuggestionOpen ? MovePreviousCommand : null;

    [DataMember]
    public AsyncCommand? MoveNextKeyCommand => IsSuggestionOpen ? MoveNextCommand : null;

    [DataMember]
    public AsyncCommand? AcceptSuggestionKeyCommand => IsSuggestionOpen ? AcceptSuggestionCommand : null;

    [DataMember]
    public AsyncCommand? DismissSuggestionsKeyCommand => IsSuggestionOpen ? DismissSuggestionsCommand : null;

    [DataMember]
    public AsyncCommand? ExecuteKeyCommand => HasActiveCommand ? ExecuteCommand : null;

    [DataMember]
    public AsyncCommand? ClearKeyCommand => HasActiveCommand ? ClearCommandCommand : null;

    public void Configure(
        Func<SlashCommandSuggestionViewModel, Task> onSuggestionAccepted,
        Func<SlashCommandSubmission, Task<bool>> onExecuteRequested,
        Func<Task>? onCleared = null)
    {
        suggestionAccepted = onSuggestionAccepted;
        executeRequested = onExecuteRequested;
        cleared = onCleared;
    }

    public void ShowSuggestions(IEnumerable<SlashCommandSuggestionDescriptor> descriptors)
    {
        Suggestions.Clear();
        foreach (SlashCommandSuggestionDescriptor descriptor in descriptors)
        {
            Suggestions.Add(new SlashCommandSuggestionViewModel(descriptor, AcceptSuggestionAsync, markdown));
        }

        SelectedSuggestion = Suggestions.FirstOrDefault(item => item.IsAvailable && item.IsSelectable)
            ?? Suggestions.FirstOrDefault();
        IsSuggestionOpen = Suggestions.Count > 0;
        StatusAnnouncement = Suggestions.Count switch
        {
            0 => "No matching slash commands.",
            1 => "1 slash command available.",
            _ => $"{Suggestions.Count} slash commands available.",
        };
    }

    public void CloseSuggestions()
    {
        IsSuggestionOpen = false;
        SelectedSuggestion = null;
        StatusAnnouncement = string.Empty;
    }

    public void ShowFailure(string message)
    {
        StatusAnnouncement = message;
    }

    public void ShowStatus(string message)
    {
        StatusAnnouncement = message;
    }

    public void ClearAfterSuccess()
    {
        bool useDefaultMessage = !HasStatusAnnouncement
            || StatusAnnouncement.EndsWith(" selected.", StringComparison.Ordinal);
        ResetActiveCommand();
        if (useDefaultMessage)
        {
            StatusAnnouncement = "Slash command completed.";
        }
    }

    private bool CanMoveSelection()
        => IsSuggestionOpen && Suggestions.Count > 0;

    private bool CanAcceptSuggestion()
        => IsSuggestionOpen && SelectedSuggestion is { IsAvailable: true, IsSelectable: true };

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

        for (int attempt = 0; attempt < Suggestions.Count; attempt++)
        {
            SlashCommandSuggestionViewModel candidate = Suggestions[nextIndex];
            if (candidate.IsAvailable && candidate.IsSelectable)
            {
                SelectedSuggestion = candidate;
                StatusAnnouncement = $"{candidate.CommandName}: {candidate.Description}";
                break;
            }

            nextIndex = (nextIndex + offset + Suggestions.Count) % Suggestions.Count;
        }

        return Task.CompletedTask;
    }

    private Task AcceptSelectedSuggestionAsync()
        => SelectedSuggestion is null
            ? Task.CompletedTask
            : AcceptSuggestionAsync(SelectedSuggestion);

    private async Task AcceptSuggestionAsync(SlashCommandSuggestionViewModel suggestion)
    {
        if (!suggestion.IsAvailable || !suggestion.IsSelectable)
        {
            StatusAnnouncement = suggestion.UnavailableReason;
            return;
        }

        if (suggestion.IsSkill)
        {
            CloseSuggestions();
            StatusAnnouncement = $"{suggestion.CommandName} selected.";
            if (suggestionAccepted is not null)
            {
                await suggestionAccepted(suggestion).ConfigureAwait(false);
            }

            return;
        }

        ActiveCommand = suggestion;
        ShowArgumentInput = suggestion.ShowArgumentInput;
        Options.Clear();
        foreach (SlashCommandOptionDescriptor option in suggestion.OptionDescriptors)
        {
            Options.Add(new SlashCommandOptionViewModel(option, SelectOptionAsync, markdown));
        }

        OnPropertyChanged(nameof(HasFixedOptions));
        SelectedOption = null;
        SetArgumentText(string.Empty);
        CloseSuggestions();
        StatusAnnouncement = $"{suggestion.CommandName} selected.";

        if (suggestionAccepted is not null)
        {
            await suggestionAccepted(suggestion).ConfigureAwait(false);
        }
    }

    private Task DismissSuggestionsAsync()
    {
        CloseSuggestions();
        StatusAnnouncement = "Slash command suggestions closed.";
        return Task.CompletedTask;
    }

    private async Task ClearActiveCommandAsync()
    {
        ResetActiveCommand();
        StatusAnnouncement = "Slash command cleared.";
        if (cleared is not null)
        {
            await cleared().ConfigureAwait(false);
        }
    }

    private Task SelectOptionAsync(SlashCommandOptionViewModel option)
    {
        foreach (SlashCommandOptionViewModel candidate in Options)
        {
            candidate.IsSelected = ReferenceEquals(candidate, option);
        }

        SelectedOption = option;
        StatusAnnouncement = $"{option.DisplayText} selected.";
        ExecuteCommand.RaiseCanExecuteChanged();
        return Task.CompletedTask;
    }

    private async Task ExecuteActiveCommandAsync()
    {
        if (ActiveCommand is null)
        {
            return;
        }

        if (executeRequested is null)
        {
            StatusAnnouncement = "Slash command execution is not connected.";
            return;
        }

        var submission = new SlashCommandSubmission(
            ActiveCommand.CommandValue,
            ArgumentText,
            SelectedOption?.Value);
        bool succeeded = await executeRequested(submission).ConfigureAwait(false);
        if (succeeded)
        {
            ClearAfterSuccess();
        }
    }

    private void ResetActiveCommand()
    {
        ActiveCommand = null;
        Options.Clear();
        OnPropertyChanged(nameof(HasFixedOptions));
        SelectedOption = null;
        ShowArgumentInput = false;
        SetArgumentText(string.Empty);
    }

    private void SetArgumentText(string value)
    {
        if (string.Equals(argumentText, value, StringComparison.Ordinal))
        {
            return;
        }

        argumentText = value;
        OnPropertyChanged(nameof(ArgumentText));
        ExecuteCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSuggestionCommandStates()
    {
        MovePreviousCommand.RaiseCanExecuteChanged();
        MoveNextCommand.RaiseCanExecuteChanged();
        AcceptSuggestionCommand.RaiseCanExecuteChanged();
        DismissSuggestionsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(MovePreviousKeyCommand));
        OnPropertyChanged(nameof(MoveNextKeyCommand));
        OnPropertyChanged(nameof(AcceptSuggestionKeyCommand));
        OnPropertyChanged(nameof(DismissSuggestionsKeyCommand));
    }
}

[DataContract]
public sealed class SlashCommandSuggestionViewModel
{
    private readonly Func<SlashCommandSuggestionViewModel, Task> use;

    internal SlashCommandSuggestionViewModel(
        SlashCommandSuggestionDescriptor descriptor,
        Func<SlashCommandSuggestionViewModel, Task> use,
        SafeMarkdownService markdown)
    {
        this.use = use;
        CommandValue = descriptor.CommandName;
        CommandName = markdown.ToSafeText(descriptor.CommandName).Trim();
        Description = markdown.ToSafeText(descriptor.Description).Trim();
        ArgumentHint = markdown.ToSafeText(descriptor.ArgumentHint).Trim();
        ShowArgumentInput = descriptor.ShowArgumentInput;
        IsAvailable = descriptor.IsAvailable;
        IsSelectable = descriptor.IsSelectable;
        IsSkill = descriptor.IsSkill;
        ScopeLabel = markdown.ToSafeText(descriptor.ScopeLabel).Trim();
        SelectionId = descriptor.SelectionId;
        // "Transparent" (not an empty string) so every row remains a valid BrushConverter
        // input. High Contrast never honors an app-server-supplied color: it always falls
        // back to Visual Studio theme resources, so the accent is suppressed entirely.
        BrandColor = !SystemParameters.HighContrast && IsSafeBrandColor(descriptor.BrandColor)
            ? descriptor.BrandColor.ToUpperInvariant()
            : "Transparent";
        UnavailableReason = markdown.ToSafeText(descriptor.UnavailableReason).Trim();
        OptionDescriptors = descriptor.Options ?? [];
        UseCommand = new AsyncCommand(UseAsync, () => IsAvailable && IsSelectable);
    }

    [DataMember]
    public string CommandName { get; }

    [DataMember]
    public string Description { get; }

    [DataMember]
    public string ArgumentHint { get; }

    [DataMember]
    public bool ShowArgumentInput { get; }

    [DataMember]
    public bool IsAvailable { get; }

    [DataMember]
    public bool IsSelectable { get; }

    [DataMember]
    public bool IsSkill { get; }

    [DataMember]
    public string ScopeLabel { get; }

    [DataMember]
    public string BrandColor { get; }

    // Opaque per-snapshot key. It is not a path or an app-server identifier.
    internal string SelectionId { get; }

    [DataMember]
    public string UnavailableReason { get; }

    [DataMember]
    public string AutomationName => string.IsNullOrWhiteSpace(Description)
        ? CommandName
        : $"{CommandName}, {Description}";

    [DataMember]
    public AsyncCommand UseCommand { get; }

    internal string CommandValue { get; }

    internal IReadOnlyList<SlashCommandOptionDescriptor> OptionDescriptors { get; }

    private Task UseAsync()
        => use(this);

    private static bool IsSafeBrandColor(string value)
        => value.Length == 7
            && value[0] == '#'
            && value.Skip(1).All(Uri.IsHexDigit);
}

[DataContract]
public sealed class SlashCommandOptionViewModel : ObservableObject
{
    private readonly Func<SlashCommandOptionViewModel, Task> use;
    private bool isSelected;

    internal SlashCommandOptionViewModel(
        SlashCommandOptionDescriptor descriptor,
        Func<SlashCommandOptionViewModel, Task> use,
        SafeMarkdownService markdown)
    {
        this.use = use;
        Value = descriptor.Value;
        DisplayText = markdown.ToSafeText(descriptor.DisplayText).Trim();
        Description = markdown.ToSafeText(descriptor.Description).Trim();
        UseCommand = new AsyncCommand(UseAsync);
    }

    public string Value { get; }

    [DataMember]
    public string DisplayText { get; }

    [DataMember]
    public string Description { get; }

    [DataMember]
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    [DataMember]
    public string AutomationName
    {
        get
        {
            string selectionState = IsSelected ? ", selected" : string.Empty;
            return string.IsNullOrWhiteSpace(Description)
                ? $"{DisplayText}{selectionState}"
                : $"{DisplayText}{selectionState}, {Description}";
        }
    }

    [DataMember]
    public AsyncCommand UseCommand { get; }

    private Task UseAsync()
        => use(this);
}
