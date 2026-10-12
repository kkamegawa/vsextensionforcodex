using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class WindowsSandboxBackendOption
{
    internal WindowsSandboxBackendOption(string id, WindowsSandboxBackendPreference preference, string displayText, string description)
    {
        Id = id;
        Preference = preference;
        DisplayText = displayText;
        Description = description;
    }

    [DataMember]
    public string Id { get; }

    [DataMember]
    public string DisplayText { get; }

    [DataMember]
    public string Description { get; }

    public WindowsSandboxBackendPreference Preference { get; }
}

// Stable IDs are persisted; display text can change without invalidating user settings.
internal static class WindowsSandboxBackendCatalog
{
    public const string FollowId = "follow";
    public const string PreferMxcId = "preferMxc";
    public const string RequireMxcId = "requireMxc";

    public static IReadOnlyList<WindowsSandboxBackendOption> Options { get; } =
    [
        new(FollowId, WindowsSandboxBackendPreference.FollowCodexConfig, "Follow Codex configuration",
            "Use windows.sandbox and features from the Codex configuration and managed policy."),
        new(PreferMxcId, WindowsSandboxBackendPreference.PreferMxc, "Prefer MXC",
            "Use MXC when managed policy allows it; otherwise Codex keeps its configured backend."),
        new(RequireMxcId, WindowsSandboxBackendPreference.RequireMxc, "Require MXC",
            "Always use MXC. Codex does not start when managed policy does not allow MXC."),
    ];

    public static ObservableCollection<WindowsSandboxBackendOption> CreateCollection() => new(Options);

    public static WindowsSandboxBackendOption Find(string? id)
        => Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal)) ?? Options[0];

    public static WindowsSandboxBackendOption Find(WindowsSandboxBackendPreference preference)
        => Options.FirstOrDefault(option => option.Preference == preference) ?? Options[0];
}
