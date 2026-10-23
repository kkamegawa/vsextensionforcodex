using System.Text;
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

        return JsonRpcRequestId.TryGetKey(Id.Value, out string key) ? key : null;
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
    private const int MaximumErrorDataBytes = 16 * 1024;

    public JsonRpcRemoteException(int code, string message)
        : this(code, message, errorData: null)
    {
    }

    public JsonRpcRemoteException(int code, string message, JsonElement? errorData)
        : base(message)
    {
        Code = code;
        ErrorData = CloneErrorData(errorData);
    }

    public int Code { get; }

    /// <summary>
    /// Gets a bounded copy of the remote JSON-RPC error data. This is separate from
    /// <see cref="Exception.Data"/> and is never included in logs or user-facing messages.
    /// </summary>
    public JsonElement? ErrorData { get; }

    private static JsonElement? CloneErrorData(JsonElement? errorData)
    {
        if (errorData is not JsonElement value)
        {
            return null;
        }

        try
        {
            if (Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumErrorDataBytes)
            {
                return null;
            }

            return value.Clone();
        }
        catch (InvalidOperationException)
        {
            // A disposed or otherwise unusable source element is not safe to retain.
            return null;
        }
    }
}

public sealed class JsonRpcConnectionClosedException : Exception
{
    public JsonRpcConnectionClosedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// An externally resolved server request no longer needs a client response. The connection
/// dispatcher recognizes this exception and suppresses the JSON-RPC reply.
/// </summary>
public sealed class JsonRpcRequestResolvedException : Exception
{
    public JsonRpcRequestResolvedException()
        : base("The server request was resolved by the app-server.")
    {
    }
}
