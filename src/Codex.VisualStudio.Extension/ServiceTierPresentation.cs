using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class ServiceTierOption : ObservableObject
{
    private string displayText;
    private string description;

    internal ServiceTierOption(string id, string displayText, string description)
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

    internal void UpdateFrom(ServiceTierOption source)
    {
        DisplayText = source.DisplayText;
        Description = source.Description;
        OnPropertyChanged(nameof(AutomationName));
    }
}

internal static class ServiceTierCatalog
{
    public const string DefaultId = "";

    public static IReadOnlyList<ServiceTierOption> Create(ModelInfo? model, SafeMarkdownService markdown)
    {
        string defaultDescription = string.IsNullOrEmpty(model?.DefaultServiceTier)
            ? "Inherit the service tier from the Codex configuration."
            : $"Inherit the Codex configuration. The model reports {FormatLabel(model.DefaultServiceTier)} as its default.";
        var options = new List<ServiceTierOption>
        {
            new(DefaultId, "Default", defaultDescription),
        };
        if (model is null)
        {
            return options;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ServiceTierInfo tier in model.ServiceTiers)
        {
            if (string.IsNullOrWhiteSpace(tier.Id) || !seen.Add(tier.Id))
            {
                continue;
            }

            string safeName = markdown.ToSafeText(tier.Name ?? string.Empty).Trim();
            string safeDescription = markdown.ToSafeText(tier.Description ?? string.Empty).Trim();
            options.Add(new ServiceTierOption(
                tier.Id,
                safeName.Length == 0 ? FormatLabel(tier.Id) : safeName,
                safeDescription));
        }

        return options;
    }

    public static void Merge(ObservableCollection<ServiceTierOption> target, IReadOnlyList<ServiceTierOption> source)
    {
        for (int index = 0; index < source.Count; index++)
        {
            ServiceTierOption incoming = source[index];
            int existingIndex = IndexOf(target, incoming.Id);
            if (existingIndex < 0)
            {
                target.Insert(index, incoming);
            }
            else
            {
                ServiceTierOption existing = target[existingIndex];
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

    private static int IndexOf(ObservableCollection<ServiceTierOption> options, string id)
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

    private static string FormatLabel(string id)
        => string.Join(
            " ",
            id.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
}
