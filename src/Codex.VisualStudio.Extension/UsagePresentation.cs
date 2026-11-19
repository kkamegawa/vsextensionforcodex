using System.Globalization;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class UsagePresentation : ObservableObject
{
    private string limitsSlashStatusText = "Usage data is unavailable.";

    [DataMember]
    public bool HasThreadUsage
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    [DataMember]
    public string ThreadUsageText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    [DataMember]
    public bool HasData
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    [DataMember]
    public bool HasPrimary
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    [DataMember]
    public bool HasSecondary
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    [DataMember]
    public bool HasCredits
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    [DataMember]
    public bool IsLoading
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    [DataMember]
    public string ToolbarText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = "Usage";

    [DataMember]
    public string AutomationName
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = "Codex usage";

    [DataMember]
    public string AutomationHelpText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = "Usage data is unavailable.";

    [DataMember]
    public string StatusText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = "Usage data is unavailable.";

    [DataMember]
    public string SlashStatusText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = "Usage data is unavailable.";

    [DataMember]
    public string PrimaryText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    [DataMember]
    public string SecondaryText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    [DataMember]
    public string CreditsText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    [DataMember]
    public string UpdatedText
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = string.Empty;

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
        limitsSlashStatusText = "Usage data is unavailable.";
        SlashStatusText = limitsSlashStatusText;
        PrimaryText = string.Empty;
        SecondaryText = string.Empty;
        CreditsText = string.Empty;
        UpdatedText = string.Empty;
        HasThreadUsage = false;
        ThreadUsageText = string.Empty;
    }

    /// <summary>Shows only amounts the server reported, each under its own label.</summary>
    public void UpdateThreadUsage(ThreadUsageResult? result)
    {
        var lines = new List<string>();
        if (result is { IsSupported: true })
        {
            if (result.NativeUsageUsdMicros is long native)
            {
                lines.Add($"This conversation: {FormatUsd(native)}");
            }

            if (result.EstimatedUsageUsdMicros is long estimatedUsd)
            {
                lines.Add($"This conversation (estimated): {FormatUsd(estimatedUsd)}");
            }

            if (result.EstimatedUsageCreditsMicros is long estimatedCredits)
            {
                lines.Add($"This conversation (estimated credits): {FormatMicros(estimatedCredits)}");
            }
        }

        ThreadUsageText = string.Join("\r\n", lines);
        HasThreadUsage = lines.Count > 0;
        SlashStatusText = HasThreadUsage
            ? string.Concat(limitsSlashStatusText, "\r\n", ThreadUsageText)
            : limitsSlashStatusText;
    }

    private static string FormatUsd(long micros)
        => string.Concat(micros < 0 ? "-$" : "$", FormatMicros(Math.Abs((decimal)micros)));

    private static string FormatMicros(decimal micros)
        => (micros / 1_000_000m).ToString("0.00####", CultureInfo.InvariantCulture);

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
        limitsSlashStatusText = HasData
            ? string.Join("\r\n", details.Where(value => value.Length > 0))
            : "Usage data is unavailable.";
        SlashStatusText = HasThreadUsage
            ? string.Concat(limitsSlashStatusText, "\r\n", ThreadUsageText)
            : limitsSlashStatusText;
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
