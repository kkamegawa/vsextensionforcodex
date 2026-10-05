using System.Globalization;
using System.Text.Json;

namespace Codex.AppServer.Protocol;

/// <summary>
/// Creates a collision-free key for JSON-RPC request IDs. JSON strings and integer IDs are
/// distinct protocol values even when their textual representations match.
/// </summary>
public static class JsonRpcRequestId
{
    public static bool TryGetKey(JsonElement id, out string key)
    {
        switch (id.ValueKind)
        {
            case JsonValueKind.String:
                key = GetStringKey(id.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.Number when id.TryGetInt64(out long value):
                key = GetNumberKey(value);
                return true;
            default:
                key = string.Empty;
                return false;
        }
    }

    public static string GetKey(JsonElement id)
    {
        if (TryGetKey(id, out string key))
        {
            return key;
        }

        throw new ArgumentException("A JSON-RPC request ID must be a string or signed 64-bit integer.", nameof(id));
    }

    public static string GetStringKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "s:" + value;
    }

    public static string GetNumberKey(long value)
        => "n:" + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Returns a response-safe CLR value while retaining the JSON-RPC ID kind.</summary>
    public static object ToWireValue(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? string.Empty,
            JsonValueKind.Number when id.TryGetInt64(out long value) => value,
            _ => throw new ArgumentException("A JSON-RPC request ID must be a string or signed 64-bit integer.", nameof(id)),
        };
    }
}
