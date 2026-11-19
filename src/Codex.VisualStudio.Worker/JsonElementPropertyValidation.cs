using System.Text.Json;

namespace Codex.VisualStudio.Worker;

internal static class JsonElementPropertyValidation
{
    public static bool OnlyProperties(JsonElement element, params ReadOnlySpan<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            bool isAllowed = false;
            foreach (string allowedName in allowed)
            {
                if (string.Equals(property.Name, allowedName, StringComparison.Ordinal))
                {
                    isAllowed = true;
                    break;
                }
            }

            if (!isAllowed || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }
}
