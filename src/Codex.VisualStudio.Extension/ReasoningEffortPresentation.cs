using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class ReasoningEffortOption : ObservableObject
{
    private string displayText;
    private string description;

    internal ReasoningEffortOption(string id, string displayText, string description)
    {
        Id = id;
        this.displayText = displayText;
        this.description = description;
    }

    [DataMember]
    public string Id { get; }

    [DataMember]
    public string DisplayText
    {
        get => displayText;
        private set => SetProperty(ref displayText, value);
    }

    [DataMember]
    public string Description
    {
        get => description;
        private set => SetProperty(ref description, value);
    }

    [DataMember]
    public string AutomationName => string.IsNullOrEmpty(Description)
        ? DisplayText
        : $"{DisplayText}. {Description}";

    internal void UpdateFrom(ReasoningEffortOption source)
    {
        DisplayText = source.DisplayText;
        Description = source.Description;
        OnPropertyChanged(nameof(AutomationName));
    }
}

internal static class ReasoningEffortCatalog
{
    public const string DefaultId = "";

    public static IReadOnlyList<ReasoningEffortOption> Create(
        ModelInfo? model,
        SafeMarkdownService markdown)
    {
        string defaultDescription = string.IsNullOrEmpty(model?.DefaultReasoningEffort)
            ? "Inherit the reasoning effort from the Codex configuration."
            : $"Inherit the Codex configuration. The model reports {FormatLabel(model.DefaultReasoningEffort)} as its default.";
        var options = new List<ReasoningEffortOption>
        {
            new(DefaultId, "Default", defaultDescription),
        };
        if (model is null)
        {
            return options;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ReasoningEffortInfo effort in model.SupportedReasoningEfforts)
        {
            if (string.IsNullOrWhiteSpace(effort.Id) || !seen.Add(effort.Id))
            {
                continue;
            }

            string safeDescription = markdown.ToSafeText(effort.Description ?? string.Empty).Trim();
            options.Add(new ReasoningEffortOption(
                effort.Id,
                FormatLabel(effort.Id),
                safeDescription));
        }

        return options;
    }

    public static void Merge(
        ObservableCollection<ReasoningEffortOption> target,
        IReadOnlyList<ReasoningEffortOption> source)
    {
        for (int index = 0; index < source.Count; index++)
        {
            ReasoningEffortOption incoming = source[index];
            int existingIndex = IndexOf(target, incoming.Id);
            if (existingIndex < 0)
            {
                target.Insert(index, incoming);
            }
            else
            {
                ReasoningEffortOption existing = target[existingIndex];
                existing.UpdateFrom(incoming);
                if (existingIndex != index)
                {
                    target.Move(existingIndex, index);
                }
            }
        }

        for (int index = target.Count - 1; index >= source.Count; index--)
        {
            target.RemoveAt(index);
        }
    }

    private static int IndexOf(ObservableCollection<ReasoningEffortOption> options, string id)
    {
        for (int index = 0; index < options.Count; index++)
        {
            if (string.Equals(options[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string FormatLabel(string id) => id.ToLowerInvariant() switch
    {
        "xhigh" => "Extra high",
        "high" => "High",
        "medium" => "Medium",
        "low" => "Low",
        "minimal" => "Minimal",
        _ => string.Join(
            " ",
            id.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant())),
    };
}
