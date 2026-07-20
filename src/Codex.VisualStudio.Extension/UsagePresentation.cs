using System.Globalization;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class UsagePresentation : ObservableObject
{
    private bool hasData;
    private bool hasPrimary;
    private bool hasSecondary;
    private bool hasCredits;
    private bool isLoading;
    private string toolbarText = "Usage";
    private string automationName = "Codex usage";
    private string automationHelpText = "Usage data is unavailable.";
    private string statusText = "Usage data is unavailable.";
    private string slashStatusText = "Usage data is unavailable.";
    private string primaryText = string.Empty;
    private string secondaryText = string.Empty;
    private string creditsText = string.Empty;
    private string updatedText = string.Empty;

    [DataMember]
    public bool HasData
    {
        get => hasData;
        private set => SetProperty(ref hasData, value);
    }

    [DataMember]
    public bool HasPrimary
    {
        get => hasPrimary;
        private set => SetProperty(ref hasPrimary, value);
    }

    [DataMember]
    public bool HasSecondary
    {
        get => hasSecondary;
        private set => SetProperty(ref hasSecondary, value);
    }

    [DataMember]
    public bool HasCredits
    {
        get => hasCredits;
        private set => SetProperty(ref hasCredits, value);
    }

    [DataMember]
    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    [DataMember]
    public string ToolbarText
    {
        get => toolbarText;
        private set => SetProperty(ref toolbarText, value);
    }

    [DataMember]
    public string AutomationName
    {
        get => automationName;
        private set => SetProperty(ref automationName, value);
    }

    [DataMember]
    public string AutomationHelpText
    {
        get => automationHelpText;
        private set => SetProperty(ref automationHelpText, value);
    }

    [DataMember]
    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    [DataMember]
    public string SlashStatusText
    {
        get => slashStatusText;
        private set => SetProperty(ref slashStatusText, value);
    }

    [DataMember]
    public string PrimaryText
    {
        get => primaryText;
        private set => SetProperty(ref primaryText, value);
    }

    [DataMember]
    public string SecondaryText
    {
        get => secondaryText;
        private set => SetProperty(ref secondaryText, value);
    }

    [DataMember]
    public string CreditsText
    {
        get => creditsText;
        private set => SetProperty(ref creditsText, value);
    }

    [DataMember]
    public string UpdatedText
    {
        get => updatedText;
        private set => SetProperty(ref updatedText, value);
    }

    public void SetLoading(bool value) => IsLoading = value;

    public void Clear()
    {
        HasData = false;
        HasPrimary = false;
        HasSecondary = false;
        HasCredits = false;
        IsLoading = false;
        ToolbarText = "Usage";
        AutomationName = "Codex usage";
        AutomationHelpText = "Usage data is unavailable.";
        StatusText = "Usage data is unavailable.";
        SlashStatusText = "Usage data is unavailable.";
        PrimaryText = string.Empty;
        SecondaryText = string.Empty;
        CreditsText = string.Empty;
        UpdatedText = string.Empty;
    }

    public void Update(RateLimitsResult? result, DateTimeOffset refreshedAt, SafeMarkdownService markdown)
    {
        RateLimitInfo? rateLimit = SelectRateLimit(result);
        int? primaryRemaining = GetRemaining(rateLimit?.Primary);
        int? secondaryRemaining = GetRemaining(rateLimit?.Secondary);

        HasPrimary = primaryRemaining.HasValue;
        HasSecondary = secondaryRemaining.HasValue;
        PrimaryText = primaryRemaining.HasValue
            ? FormatWindow(rateLimit!.Primary!, primaryRemaining.Value, "Primary")
            : string.Empty;
        SecondaryText = secondaryRemaining.HasValue
            ? FormatWindow(rateLimit!.Secondary!, secondaryRemaining.Value, "Secondary")
            : string.Empty;

        CreditsText = FormatCredits(rateLimit?.Credits, markdown);
        HasCredits = CreditsText.Length > 0;
        HasData = HasPrimary || HasSecondary || HasCredits;
        int? summaryRemaining = primaryRemaining ?? secondaryRemaining;
        ToolbarText = summaryRemaining.HasValue ? $"{summaryRemaining.Value}% remaining" : "Usage";
        StatusText = summaryRemaining.HasValue
            ? ToolbarText
            : HasCredits
                ? "Credit information available."
                : "Usage data is unavailable.";
        string[] details = [PrimaryText, SecondaryText, CreditsText];
        SlashStatusText = HasData
            ? string.Join("\r\n", details.Where(value => value.Length > 0))
            : "Usage data is unavailable.";
        AutomationName = HasData ? $"Codex usage, {StatusText}" : "Codex usage";
        AutomationHelpText = HasData
            ? string.Join(" ", details.Where(value => value.Length > 0))
            : "Usage data is unavailable.";
        UpdatedText = $"Updated {refreshedAt.ToUniversalTime().ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture)}";
        IsLoading = false;
    }

    private static RateLimitInfo? SelectRateLimit(RateLimitsResult? result)
    {
        if (result is null || !result.IsSupported)
        {
            return null;
        }

        if (HasReportedUsage(result.RateLimits) || result.RateLimits?.Credits is not null)
        {
            return result.RateLimits;
        }

        KeyValuePair<string, RateLimitInfo>[] canonical = result.RateLimitsByLimitId
            .Where(pair => string.Equals(pair.Key, "codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Value.LimitId, "codex", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (canonical.Length == 1)
        {
            return canonical[0].Value;
        }

        return result.RateLimitsByLimitId.Count == 1
            ? result.RateLimitsByLimitId.Values.Single()
            : null;
    }

    private static bool HasReportedUsage(RateLimitInfo? value)
        => value?.Primary?.UsedPercent.HasValue == true || value?.Secondary?.UsedPercent.HasValue == true;

    private static int? GetRemaining(RateLimitWindowInfo? window)
        => window?.UsedPercent is int usedPercent
            ? Math.Clamp(100 - usedPercent, 0, 100)
            : null;

    private static string FormatWindow(RateLimitWindowInfo window, int remaining, string fallbackName)
    {
        string windowName = window.WindowDurationMinutes switch
        {
            300 => "5-hour limit",
            10_080 => "Weekly limit",
            > 0 => $"{window.WindowDurationMinutes.Value.ToString(CultureInfo.InvariantCulture)}-minute limit",
            _ => $"{fallbackName} limit",
        };
        string reset = FormatReset(window.ResetsAt);
        return reset.Length == 0
            ? $"{windowName}: {remaining}% remaining"
            : $"{windowName}: {remaining}% remaining · {reset}";
    }

    private static string FormatReset(long? unixSeconds)
    {
        if (!unixSeconds.HasValue)
        {
            return string.Empty;
        }

        try
        {
            DateTimeOffset value = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
            return $"Resets {value.ToUniversalTime().ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture)}";
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    private static string FormatCredits(CreditsInfo? credits, SafeMarkdownService markdown)
    {
        if (credits is null)
        {
            return string.Empty;
        }

        if (credits.Unlimited)
        {
            return "Credits: unlimited";
        }

        if (!credits.HasCredits)
        {
            return "Credits: not available";
        }

        string safeBalance = markdown.ToSafeText(credits.Balance ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (safeBalance.Length > 128)
        {
            safeBalance = safeBalance[..128];
        }

        return safeBalance.Length == 0 ? "Credits: unavailable" : $"Credits: {safeBalance}";
    }
}
