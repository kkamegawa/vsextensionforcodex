using System.Text;

namespace Codex.VisualStudio.Contracts;

// The configuration layer that defines a preset slot. Only User slots can be written.
public enum PresetLayerKind
{
    None,
    User,
    Project,
    System,
    Managed,
    SessionFlags,
    Other,
}

public sealed class PresetSlotInfo
{
    // "1" through "9", then "0".
    public string Slot { get; set; } = string.Empty;

    public string? Input { get; set; }

    public string? Label { get; set; }

    public PresetLayerKind SourceLayer { get; set; }

    // False when a non-user layer has the highest definition of the slot.
    public bool CanEdit { get; set; }

    // False when any non-user layer defines the slot, so deleting never reveals a shadowed value.
    public bool CanDelete { get; set; }
}

public sealed class ListPresetsRequest : OwnerScopedRequest
{
}

public sealed class ListPresetsResult : AppServerOperationResult
{
    public IReadOnlyList<PresetSlotInfo> Slots { get; set; } = Array.Empty<PresetSlotInfo>();

    // The user layer version to pass as expectedVersion; null when no user layer exists yet.
    public string? UserLayerVersion { get; set; }
}

public sealed class WritePresetRequest : OwnerScopedRequest
{
    public string Slot { get; set; } = string.Empty;

    // Null deletes the slot.
    public string? Input { get; set; }

    public string? Label { get; set; }

    public string? ExpectedVersion { get; set; }
}

public enum WritePresetOutcome
{
    Written,
    Conflict,
    Rejected,
    Failed,
}

public sealed class WritePresetResult : AppServerOperationResult
{
    public WritePresetOutcome Outcome { get; set; }

    public string? Message { get; set; }
}

/// <summary>Preset rules shared with the Codex CLI 0.164.0 QuickDial validation.</summary>
public static class PresetRules
{
    public const int MaximumInputBytes = 8000;

    public static IReadOnlyList<string> Slots { get; } = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

    public static bool IsSlot(string? slot) => slot is not null && Slots.Contains(slot, StringComparer.Ordinal);

    public static string? ValidateInput(string? input)
    {
        if (input is null || input.Trim().Length == 0)
        {
            return "Preset input cannot be empty.";
        }

        if (input.TrimStart().StartsWith("!", StringComparison.Ordinal))
        {
            return "Presets support prompts and slash commands, not shell commands.";
        }

        return Encoding.UTF8.GetByteCount(input) > MaximumInputBytes
            ? "Preset input cannot exceed 8,000 UTF-8 bytes."
            : null;
    }

    // An empty label is written explicitly so it masks a label inherited from a lower layer.
    public static string NormalizeLabel(string? label) => label?.Trim() ?? string.Empty;
}
