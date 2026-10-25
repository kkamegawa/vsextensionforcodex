namespace Codex.VisualStudio.Contracts;

// Mirrors minimumVersion and targetVersion in app-server-contract.json; a Core test keeps them in sync.
public static class SupportedCodexVersions
{
    public const string Minimum = "0.161.0";
    public const string Target = "0.164.0";
    public const string Tier163 = "0.163.0";
}

// The features a connection may use. Computed once per connection from the initialize userAgent.
public enum CodexFeatureTier
{
    Unsupported,
    Baseline,
    Tier163,
    Tier164,
}

/// <summary>A Codex CLI version ordered by Semantic Versioning 2.0 precedence.</summary>
public sealed class CodexCliVersion
{
    private const int MaximumLength = 64;

    private CodexCliVersion(string text, int major, int minor, int patch, string? prerelease)
    {
        Text = text;
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public string Text { get; }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string? Prerelease { get; }

    public static CodexCliVersion MinimumSupported { get; } = Parse(SupportedCodexVersions.Minimum);

    public static CodexCliVersion Target { get; } = Parse(SupportedCodexVersions.Target);

    private static CodexCliVersion Tier163Minimum { get; } = Parse(SupportedCodexVersions.Tier163);

    public static CodexCliVersion Parse(string value)
        => TryParse(value, out CodexCliVersion? version)
            ? version!
            : throw new FormatException($"'{value}' is not a Semantic Versioning 2.0 version.");

    public static bool TryParse(string? value, out CodexCliVersion? version)
    {
        version = null;
        if (value is null || value.Length == 0 || value.Length > MaximumLength)
        {
            return false;
        }

        // Build metadata never affects precedence and may itself contain hyphens, so strip it first.
        string withoutBuild = value;
        int build = value.IndexOf('+');
        if (build >= 0)
        {
            if (!IsValidIdentifierList(value.Substring(build + 1), allowNumericLeadingZero: true))
            {
                return false;
            }

            withoutBuild = value.Substring(0, build);
        }

        string core = withoutBuild;
        string? prerelease = null;
        int hyphen = withoutBuild.IndexOf('-');
        if (hyphen >= 0)
        {
            prerelease = withoutBuild.Substring(hyphen + 1);
            if (!IsValidIdentifierList(prerelease, allowNumericLeadingZero: false))
            {
                return false;
            }

            core = withoutBuild.Substring(0, hyphen);
        }

        string[] components = core.Split('.');
        if (components.Length != 3
            || !TryParseCoreComponent(components[0], out int major)
            || !TryParseCoreComponent(components[1], out int minor)
            || !TryParseCoreComponent(components[2], out int patch))
        {
            return false;
        }

        version = new CodexCliVersion(value, major, minor, patch, prerelease);
        return true;
    }

    /// <summary>Unknown and pre-minimum versions are Unsupported; a prerelease ranks below its release.</summary>
    public static CodexFeatureTier GetFeatureTier(CodexCliVersion? version)
    {
        if (version is null || version.CompareTo(MinimumSupported) < 0)
        {
            return CodexFeatureTier.Unsupported;
        }

        if (version.CompareTo(Target) >= 0)
        {
            return CodexFeatureTier.Tier164;
        }

        return version.CompareTo(Tier163Minimum) >= 0 ? CodexFeatureTier.Tier163 : CodexFeatureTier.Baseline;
    }

    public bool IsNewerThanTarget => CompareTo(Target) > 0;

    public int CompareTo(CodexCliVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
    }

    public override string ToString() => Text;

    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null || right is null)
        {
            // A release has higher precedence than any of its prereleases.
            return left is null ? (right is null ? 0 : 1) : -1;
        }

        string[] leftIdentifiers = left.Split('.');
        string[] rightIdentifiers = right.Split('.');
        for (int index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
        {
            string leftIdentifier = leftIdentifiers[index];
            string rightIdentifier = rightIdentifiers[index];
            bool leftNumeric = IsAsciiDigits(leftIdentifier);
            bool rightNumeric = IsAsciiDigits(rightIdentifier);
            int result = leftNumeric && rightNumeric
                ? CompareNumericIdentifiers(leftIdentifier, rightIdentifier)
                : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(leftIdentifier, rightIdentifier);
            if (result != 0)
            {
                return result;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static int CompareNumericIdentifiers(string left, string right)
        => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

    private static bool TryParseCoreComponent(string text, out int value)
    {
        value = 0;
        return IsAsciiDigits(text)
            && !(text.Length > 1 && text[0] == '0')
            && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidIdentifierList(string text, bool allowNumericLeadingZero)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (string identifier in text.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            foreach (char character in identifier)
            {
                if (!(character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-'))
                {
                    return false;
                }
            }

            if (!allowNumericLeadingZero && IsAsciiDigits(identifier) && identifier.Length > 1 && identifier[0] == '0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiDigits(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
