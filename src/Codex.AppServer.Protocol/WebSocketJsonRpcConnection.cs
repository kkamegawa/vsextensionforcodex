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
    private readonly ConcurrentDictionary<string, ServerRequestOperation> serverRequests = new();
    private readonly ConcurrentDictionary<long, Task> dispatchTasks = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? receivePump;
    private long nextId;
    private int started;
    private int closed;
    private long nextDispatchId;

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

        WebSocketTransportValidation validation = new WebSocketTransportSecurityPolicy()
            .Validate(enabled: true, endpoint, bearerToken);
        if (!validation.IsAllowed)
        {
            throw new ArgumentException(validation.Reason, nameof(endpoint));
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

        Task[] remaining = dispatchTasks.Values.ToArray();
        if (remaining.Length > 0)
        {
            try
            {
                await Task.WhenAll(remaining).ConfigureAwait(false);
            }
            catch (Exception) when (lifetime.IsCancellationRequested)
            {
                // Dispatch handlers are canceled by Close; their failures are observed below.
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
                    StartServerRequest(rpcMessage, cancellationToken);
                }
                else if (rpcMessage.IsNotification && NotificationReceived is not null)
                {
                    StartNotification(rpcMessage, cancellationToken);
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

    private void StartNotification(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        Func<JsonRpcMessage, CancellationToken, Task>? handler = NotificationReceived;
        if (handler is null)
        {
            return;
        }

        long dispatchId = Interlocked.Increment(ref nextDispatchId);
        var tracked = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatchTasks.TryAdd(dispatchId, tracked.Task);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await handler(message, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    // Receive must continue while one notification is being processed. The task is
                    // retained until completion so Close/Dispose can observe every failure.
                    _ = ex;
                }
                finally
                {
                    dispatchTasks.TryRemove(dispatchId, out _);
                    tracked.TrySetResult(null);
                }
            },
            CancellationToken.None);
    }

    private void StartServerRequest(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        string? id = message.GetIdKey();
        if (id is null)
        {
            return;
        }

        var operation = new ServerRequestOperation(id);
        if (!serverRequests.TryAdd(id, operation))
        {
            // A reused outstanding id must not produce a second response or approval prompt.
            return;
        }

        Task task = Task.Run(
            () => RunServerRequestAsync(message, operation, cancellationToken),
            CancellationToken.None);
        long dispatchId = Interlocked.Increment(ref nextDispatchId);
        dispatchTasks.TryAdd(dispatchId, task);
        _ = task.ContinueWith(
            completed =>
            {
                dispatchTasks.TryRemove(dispatchId, out _);
                ObserveCompletedTask(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunServerRequestAsync(
        JsonRpcMessage message,
        ServerRequestOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await ResolveServerRequestAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = ex;
        }
        finally
        {
            serverRequests.TryRemove(new KeyValuePair<string, ServerRequestOperation>(operation.Id, operation));
        }
    }

    private sealed class ServerRequestOperation
    {
        public ServerRequestOperation(string id)
        {
            Id = id;
        }

        public string Id { get; }
    }

    private static void ObserveCompletedTask(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
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
            if (!cancellationToken.IsCancellationRequested)
            {
                await SendMessageAsync(new { id = ToWireId(message.Id.Value), result }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonRpcRemoteException ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await SendMessageAsync(
                    new { id = ToWireId(message.Id.Value), error = new { code = ex.Code, message = ex.Message } },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await SendMessageAsync(
                    new { id = ToWireId(message.Id.Value), error = new { code = -32603, message = ex.Message } },
                    cancellationToken).ConfigureAwait(false);
            }
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
