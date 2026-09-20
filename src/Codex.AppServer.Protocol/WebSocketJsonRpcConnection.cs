using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Codex.AppServer.Protocol;

/// <summary>
/// JSON-RPC connection for an already running app-server WebSocket endpoint.
/// The transport is opt-in and never starts or restarts the remote process.
/// </summary>
public sealed class WebSocketJsonRpcConnection : IJsonRpcConnection
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri endpoint;
    private readonly string bearerToken;
    private readonly int maxMessageBytes;
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? receivePump;
    private long nextId;
    private int started;
    private int closed;

    public WebSocketJsonRpcConnection(
        Uri endpoint,
        string bearerToken,
        int maxMessageBytes = JsonLineRpcConnection.DefaultMaxLineBytes)
    {
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The endpoint must use ws or wss.", nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new ArgumentException("A bearer token is required.", nameof(bearerToken));
        }

        this.endpoint = endpoint;
        this.bearerToken = bearerToken;
        this.maxMessageBytes = maxMessageBytes;
    }

    public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

    public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

    public event EventHandler<Exception?>? Closed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        socket.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            receivePump = Task.Run(() => ReceivePumpAsync(lifetime.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Close(ex);
            throw;
        }
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        string id = Interlocked.Increment(ref nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"Duplicate JSON-RPC request id '{id}'.");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token,
            lifetime.Token);
        using CancellationTokenRegistration registration = linked.Token.Register(
            static state => ((TaskCompletionSource<JsonElement>)state!).TrySetCanceled(),
            completion);

        try
        {
            await SendMessageAsync(
                new { method, @params = parameters, id = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture) },
                linked.Token).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        return SendMessageAsync(new { method, @params = parameters }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Close(null);
        if (receivePump is not null)
        {
            try
            {
                await receivePump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        socket.Dispose();
        sendGate.Dispose();
        lifetime.Dispose();
    }

    private async Task SendMessageAsync(object message, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(message, SerializerOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > maxMessageBytes)
        {
            throw new InvalidDataException("The JSON-RPC message exceeds the configured WebSocket limit.");
        }

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task ReceivePumpAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    byte[] buffer = new byte[8192];
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > maxMessageBytes)
                    {
                        throw new InvalidDataException("The app-server emitted an oversized WebSocket message.");
                    }
                }
                while (!result.EndOfMessage);

                JsonRpcMessage? rpcMessage = JsonSerializer.Deserialize<JsonRpcMessage>(
                    message.GetBuffer().AsSpan(0, checked((int)message.Length)),
                    SerializerOptions);
                if (rpcMessage is null)
                {
                    continue;
                }

                if (rpcMessage.IsResponse)
                {
                    ResolveResponse(rpcMessage);
                }
                else if (rpcMessage.IsRequest)
                {
                    _ = Task.Run(() => ResolveServerRequestAsync(rpcMessage, cancellationToken), CancellationToken.None);
                }
                else if (rpcMessage.IsNotification && NotificationReceived is not null)
                {
                    await NotificationReceived(rpcMessage, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            Close(failure);
        }
    }

    private async Task ResolveServerRequestAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        if (message.Id is null)
        {
            return;
        }

        try
        {
            JsonElement result = RequestReceived is null
                ? JsonSerializer.SerializeToElement(new { })
                : await RequestReceived(message, cancellationToken).ConfigureAwait(false);
            await SendMessageAsync(new { id = ToWireId(message.Id.Value), result }, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            await SendMessageAsync(
                new { id = ToWireId(message.Id.Value), error = new { code = ex.Code, message = ex.Message } },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendMessageAsync(
                new { id = ToWireId(message.Id.Value), error = new { code = -32603, message = ex.Message } },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void ResolveResponse(JsonRpcMessage message)
    {
        string? id = message.GetIdKey();
        if (id is null || !pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion))
        {
            return;
        }

        if (message.Error is not null)
        {
            completion.TrySetException(new JsonRpcRemoteException(message.Error.Code, message.Error.Message));
            return;
        }

        completion.TrySetResult(message.Result ?? JsonSerializer.SerializeToElement(new { }));
    }

    private void Close(Exception? exception)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                socket.Abort();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        var closedException = new JsonRpcConnectionClosedException(
            exception?.Message ?? "The app-server WebSocket connection closed.");
        foreach (TaskCompletionSource<JsonElement> completion in pending.Values)
        {
            completion.TrySetException(closedException);
        }

        pending.Clear();
        Closed?.Invoke(this, exception);
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref closed) != 0 || socket.State is WebSocketState.Aborted or WebSocketState.Closed)
        {
            throw new JsonRpcConnectionClosedException("The app-server WebSocket connection is closed.");
        }
    }

    private static object? ToWireId(JsonElement id)
        => id.ValueKind == JsonValueKind.Number ? id.GetInt64() : id.GetString();
}
