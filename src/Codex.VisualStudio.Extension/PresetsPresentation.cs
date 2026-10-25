using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

// One tui.presets slot shared with the Codex CLI. Display text is sanitized; the raw input is kept
// unchanged for recall and editing so slash-command arguments survive byte for byte.
[DataContract]
public sealed class PresetSlotViewModel : ObservableObject
{
    private const int MaximumPreviewLength = 160;

    internal PresetSlotViewModel(
        PresetSlotInfo info,
        SafeMarkdownService markdown,
        Func<PresetSlotViewModel, Task> recall,
        Func<PresetSlotViewModel, Task> edit,
        Func<PresetSlotViewModel, Task> delete)
    {
        Slot = info.Slot;
        RawInput = info.Input;
        RawLabel = info.Label;
        IsDefined = info.Input is not null;
        CanEdit = info.CanEdit;
        CanDelete = IsDefined && info.CanDelete;
        string firstLine = (info.Input ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
        string title = !string.IsNullOrWhiteSpace(info.Label) ? info.Label! : firstLine;
        Title = IsDefined ? Truncate(markdown.ToSafeText(title).Trim()) : "Empty";
        InputPreview = IsDefined ? Truncate(markdown.ToSafeText(firstLine).Trim()) : string.Empty;
        SourceText = info.SourceLayer switch
        {
            PresetLayerKind.None => "Not set",
            PresetLayerKind.User => "User config",
            PresetLayerKind.Project => "Project config",
            PresetLayerKind.System => "System config",
            PresetLayerKind.Managed => "Managed config",
            PresetLayerKind.SessionFlags => "Session flags",
            _ => "Other config",
        };
        AvailabilityText = (CanEdit, CanDelete) switch
        {
            (true, true) => "Editable",
            (true, false) when !IsDefined => "Available",
            (true, false) => "Editable; also defined in another layer, so it cannot be deleted",
            _ => "Read-only; change it where it is defined",
        };
        AutomationName = IsDefined
            ? $"Preset {Slot}: {Title}. {SourceText}. {AvailabilityText}."
            : $"Preset {Slot}: empty.";
        RecallCommand = new AsyncCommand(() => recall(this), () => IsDefined);
        EditCommand = new AsyncCommand(() => edit(this), () => CanEdit);
        DeleteCommand = new AsyncCommand(() => delete(this), () => CanDelete);
    }

    [DataMember]
    public string Slot { get; }

    [DataMember]
    public string Title { get; }

    [DataMember]
    public string InputPreview { get; }

    [DataMember]
    public bool HasInputPreview => InputPreview.Length > 0 && !string.Equals(InputPreview, Title, StringComparison.Ordinal);

    [DataMember]
    public string SourceText { get; }

    [DataMember]
    public string AvailabilityText { get; }

    [DataMember]
    public string AutomationName { get; }

    [DataMember]
    public bool IsDefined { get; }

    [DataMember]
    public bool CanEdit { get; }

    [DataMember]
    public bool CanDelete { get; }

    [DataMember]
    public string EditText => IsDefined ? "Edit" : "Add";

    [DataMember]
    public AsyncCommand RecallCommand { get; }

    [DataMember]
    public AsyncCommand EditCommand { get; }

    [DataMember]
    public AsyncCommand DeleteCommand { get; }

    internal string? RawInput { get; }

    internal string? RawLabel { get; }

    private static string Truncate(string value)
        => value.Length > MaximumPreviewLength ? string.Concat(value.AsSpan(0, MaximumPreviewLength), "…") : value;
}
