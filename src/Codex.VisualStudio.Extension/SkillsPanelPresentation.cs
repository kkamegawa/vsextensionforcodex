using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// Owns Remote UI state for the read-only skills panel. Skill discovery and refresh
/// scheduling remain in the parent view model; this type only shapes the sanitized display.
/// </summary>
[DataContract]
public sealed class SkillsPanelPresentation : ObservableObject
{
    private bool hasData;
    private bool hasErrors;
    private bool isLoading;
    private bool isTruncated;
    private string statusText = "Skills are not available.";

    [DataMember]
    public ObservableCollection<SkillPresentation> Skills { get; } = [];

    [DataMember]
    public ObservableCollection<SkillLoadErrorPresentation> Errors { get; } = [];

    [DataMember]
    public bool HasData
    {
        get => hasData;
        private set => SetProperty(ref hasData, value);
    }

    [DataMember]
    public bool HasErrors
    {
        get => hasErrors;
        private set => SetProperty(ref hasErrors, value);
    }

    [DataMember]
    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    [DataMember]
    public bool IsTruncated
    {
        get => isTruncated;
        private set => SetProperty(ref isTruncated, value);
    }

    [DataMember]
    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public void SetLoading(bool value) => IsLoading = value;

    public void Clear()
    {
        Skills.Clear();
        Errors.Clear();
        HasData = false;
        HasErrors = false;
        IsLoading = false;
        IsTruncated = false;
        StatusText = "Skills are not available.";
    }

    public void Update(ListSkillsResult result, SafeMarkdownService markdown)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(markdown);

        if (!result.IsSupported)
        {
            Skills.Clear();
            Errors.Clear();
            HasData = false;
            HasErrors = false;
            IsTruncated = false;
            StatusText = string.IsNullOrWhiteSpace(result.UnavailableReason)
                ? "Skills are not supported by this Codex version."
                : markdown.ToSafeText(result.UnavailableReason).Trim();
            return;
        }

        // Merge in place instead of Clear+Add: matches PopulateModelsAsync's rationale (avoids
        // momentarily invalidating Remote UI list state) and keeps each SkillPresentation
        // instance stable across refreshes for a future enable/disable toggle bound to it.
        MergeSkills(result.Skills, markdown);
        MergeErrors(result.Errors, markdown);

        HasData = Skills.Count > 0;
        HasErrors = Errors.Count > 0;
        IsTruncated = result.IsTruncated;
        StatusText = Skills.Count switch
        {
            0 => "No skills found.",
            1 => "1 skill available.",
            _ => $"{Skills.Count} skills available.",
        };
    }

    private void MergeSkills(IReadOnlyList<SkillInfo> skills, SafeMarkdownService markdown)
    {
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (SkillInfo info in skills)
        {
            if (string.IsNullOrWhiteSpace(info.Path) || !seenKeys.Add(info.Path))
            {
                // WorkerContracts.cs documents SkillInfo's identity as the (Name, Scope, Path)
                // tuple (SkillMetadata has no id field), but merging by the full tuple would
                // still require a single field to key the ObservableCollection lookup, and two
                // distinct skills cannot legitimately share one filesystem path. Path is used
                // as that key for exactly that reason -- not because the contract asserts path
                // alone is globally unique. A blank or duplicate path cannot be merged in place,
                // so it is dropped from the panel rather than risking two entries racing over
                // the same key.
                continue;
            }

            int existingIndex = IndexOfSkill(info.Path);
            if (existingIndex < 0)
            {
                Skills.Insert(index, new SkillPresentation(info, markdown));
            }
            else
            {
                if (existingIndex != index)
                {
                    Skills.Move(existingIndex, index);
                }

                Skills[index].Update(info, markdown);
            }

            index++;
        }

        for (int i = Skills.Count - 1; i >= index; i--)
        {
            Skills.RemoveAt(i);
        }
    }

    private void MergeErrors(IReadOnlyList<SkillLoadError> errors, SafeMarkdownService markdown)
    {
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (SkillLoadError error in errors)
        {
            string key = $"{error.Cwd}␟{error.Path}␟{error.Message}";
            if (!seenKeys.Add(key))
            {
                continue;
            }

            int existingIndex = IndexOfError(key);
            if (existingIndex < 0)
            {
                Errors.Insert(index, new SkillLoadErrorPresentation(error, markdown, key));
            }
            else if (existingIndex != index)
            {
                Errors.Move(existingIndex, index);
            }

            index++;
        }

        for (int i = Errors.Count - 1; i >= index; i--)
        {
            Errors.RemoveAt(i);
        }
    }

    private int IndexOfSkill(string path)
    {
        for (int i = 0; i < Skills.Count; i++)
        {
            if (string.Equals(Skills[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOfError(string key)
    {
        for (int i = 0; i < Errors.Count; i++)
        {
            if (string.Equals(Errors[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

[DataContract]
public sealed class SkillPresentation : ObservableObject
{
    private string name = string.Empty;
    private string displayName = string.Empty;
    private string shortDescription = string.Empty;
    private string scopeLabel = string.Empty;
    private string pathTooltip = string.Empty;
    private bool isEnabled;
    private bool canToggle;

    internal SkillPresentation(SkillInfo info, SafeMarkdownService markdown)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(markdown);

        Path = info.Path;
        Update(info, markdown);
    }

    // Merge identity key for SkillsPanelPresentation; not shown in the UI directly (PathTooltip
    // carries the sanitized display copy).
    internal string Path { get; }

    [DataMember]
    public string Name
    {
        get => name;
        private set => SetProperty(ref name, value);
    }

    [DataMember]
    public string DisplayName
    {
        get => displayName;
        private set => SetProperty(ref displayName, value);
    }

    [DataMember]
    public string ShortDescription
    {
        get => shortDescription;
        private set
        {
            if (SetProperty(ref shortDescription, value))
            {
                OnPropertyChanged(nameof(HasShortDescription));
            }
        }
    }

    [DataMember]
    public bool HasShortDescription => ShortDescription.Length > 0;

    [DataMember]
    public string ScopeLabel
    {
        get => scopeLabel;
        private set => SetProperty(ref scopeLabel, value);
    }

    [DataMember]
    public string PathTooltip
    {
        get => pathTooltip;
        private set => SetProperty(ref pathTooltip, value);
    }

    [DataMember]
    public bool IsEnabled
    {
        get => isEnabled;
        internal set => SetProperty(ref isEnabled, value);
    }

    [DataMember]
    public bool CanToggle
    {
        get => canToggle;
        private set => SetProperty(ref canToggle, value);
    }

    internal void Update(SkillInfo info, SafeMarkdownService markdown)
    {
        Name = markdown.ToSafeText(info.Name).Trim();
        DisplayName = FormatDisplayName(info, markdown);
        ShortDescription = markdown.ToSafeText(
            string.IsNullOrWhiteSpace(info.ShortDescription) ? info.Description : info.ShortDescription!).Trim();
        ScopeLabel = FormatScopeLabel(info.Scope);
        PathTooltip = markdown.ToSafeText(info.Path).Trim();
        CanToggle = !string.Equals(info.Scope, "system", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(info.Scope, "admin", StringComparison.OrdinalIgnoreCase);
        IsEnabled = info.Enabled;
    }

    // Matches SkillSuggestionViewModel's suffix convention (S4: disabled skills stay visible,
    // marked rather than hidden, so "my skill is missing" always has an answer in the panel).
    private static string FormatDisplayName(SkillInfo info, SafeMarkdownService markdown)
    {
        string baseName = markdown.ToSafeText(
            string.IsNullOrWhiteSpace(info.DisplayName) ? info.Name : info.DisplayName!).Trim();
        return info.Enabled ? baseName : $"{baseName} (disabled)";
    }

    private static string FormatScopeLabel(string scope) => scope switch
    {
        "repo" => "Repository",
        "user" => "User",
        "system" => "System",
        "admin" => "Admin",
        _ => string.IsNullOrWhiteSpace(scope) ? "Unknown" : scope,
    };
}

[DataContract]
public sealed class SkillLoadErrorPresentation
{
    internal SkillLoadErrorPresentation(SkillLoadError error, SafeMarkdownService markdown, string key)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(markdown);

        Key = key;
        Cwd = markdown.ToSafeText(error.Cwd ?? string.Empty).Trim();
        Path = markdown.ToSafeText(error.Path ?? string.Empty).Trim();
        Message = markdown.ToSafeText(error.Message).Trim();
    }

    // Merge identity key for SkillsPanelPresentation; not shown in the UI directly.
    internal string Key { get; }

    [DataMember]
    public string Cwd { get; }

    [DataMember]
    public string Path { get; }

    [DataMember]
    public string Message { get; }
}
