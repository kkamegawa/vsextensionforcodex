using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codex.AppServer.Protocol;

public sealed class JsonRpcMessage
{
    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public bool IsRequest => Id.HasValue && Method is not null;

    [JsonIgnore]
    public bool IsNotification => !Id.HasValue && Method is not null;

    [JsonIgnore]
    public bool IsResponse => Id.HasValue && Method is null;

    public string? GetIdKey()
    {
        if (!Id.HasValue)
        {
            return null;
        }

        JsonElement id = Id.Value;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => id.GetRawText(),
        };
    }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

public sealed class JsonRpcRemoteException : Exception
{
    public JsonRpcRemoteException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}

public sealed class JsonRpcConnectionClosedException : Exception
{
    public JsonRpcConnectionClosedException(string message)
        : base(message)
    {
    }
}
