using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Codex.VisualStudio.Contracts;

public static class LocalizedStrings
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Resources = new(StringComparer.Ordinal);

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        IReadOnlyDictionary<string, string> selected = Resources.GetOrAdd(culture, Load);
        if (selected.TryGetValue(key, out string? value))
        {
            return value;
        }

        IReadOnlyDictionary<string, string> english = Resources.GetOrAdd("en", Load);
        return english.TryGetValue(key, out value) ? value : key;
    }

    public static string Format(string key, params object?[] arguments)
        => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    private static IReadOnlyDictionary<string, string> Load(string culture)
    {
        string resourceName = $"Codex.VisualStudio.Contracts.Strings.{culture}.json";
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return culture == "en"
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : Resources.GetOrAdd("en", Load);
        }

        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
