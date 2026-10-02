using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Codex.AppServer.Protocol;

/// <summary>
/// JSON-RPC connection for an already running app-server WebSocket endpoint.
/// The transport is opt-in and never starts or restarts the remote process.
/// </summary>
public sealed class WebSocketJsonRpcConnection : IJsonRpcConnection, IInboundActivitySource
{
    // .NET 8 sends unsolicited PONG frames at this interval. They keep intermediaries from idling
    // the socket out but are not proof that the peer is alive; the Worker's idle watchdog is.
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private const int ReceiveBufferBytes = 8192;
    private const int NotificationQueueCapacity = 256;
    private const int MaxConsecutiveMalformedMessages = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri? endpoint;
    private readonly string? bearerToken;
    private readonly HttpMessageInvoker? invoker;
    private readonly int maxMessageBytes;
    private readonly WebSocket socket;
    private readonly ClientWebSocket? clientSocket;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    // Notifications and server requests in wire order. Responses bypass it (see ReceivePumpAsync).
    private readonly Channel<JsonRpcMessage> inbound;
    private readonly JsonRpcServerRequestDispatcher dispatcher;
    private readonly CancellationTokenSource lifetime = new();
    private Task? receivePump;
    private Task? notificationPump;
    private long nextId;
    private long inboundActivity;
    private int started;
    private int closed;

    public WebSocketJsonRpcConnection(
        Uri endpoint,
        string bearerToken,
        int maxMessageBytes = JsonLineRpcConnection.DefaultMaxLineBytes)
        : this(endpoint, bearerToken, invoker: null, maxMessageBytes)
    {
    }

    /// <summary>
    /// Creates a connection whose handshake runs through <paramref name="invoker"/>. The caller
    /// owns the invoker: it carries proxy, redirect, cookie, and TLS policy, and is not disposed
    /// with this connection. WebSocket options then contain only transport-specific settings.
    /// </summary>
    public WebSocketJsonRpcConnection(
        Uri endpoint,
        string bearerToken,
        HttpMessageInvoker? invoker,
        int maxMessageBytes = JsonLineRpcConnection.DefaultMaxLineBytes)
        : this(new ClientWebSocket(), maxMessageBytes)
    {
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
        {
            socket.Dispose();
            throw new ArgumentException("The endpoint must use ws or wss.", nameof(endpoint));
        }

        WebSocketTransportValidation validation = WebSocketTransportSecurityPolicy
            .Validate(enabled: true, endpoint, bearerToken);
        if (!validation.IsAllowed)
        {
            socket.Dispose();
            throw new ArgumentException(validation.Reason, nameof(endpoint));
        }

        this.endpoint = endpoint;
        this.bearerToken = bearerToken;
        this.invoker = invoker;
        clientSocket = (ClientWebSocket)socket;
    }

    // Wraps an already connected socket. Contract tests use this with an in-memory server
    // socket so framing, ordering, and close behavior are exercised without a network listener.
    internal WebSocketJsonRpcConnection(WebSocket connectedSocket, int maxMessageBytes = JsonLineRpcConnection.DefaultMaxLineBytes)
    {
        socket = connectedSocket;
        this.maxMessageBytes = maxMessageBytes;
        inbound = Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(NotificationQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        dispatcher = new JsonRpcServerRequestDispatcher(SendMessageAsync);
    }

    public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

    public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

    public event EventHandler<Exception?>? Closed;

    // Monotonic count of valid parsed inbound responses, notifications, and server requests.
    public long InboundActivitySequence => Interlocked.Read(ref inboundActivity);

    public event EventHandler? InboundActivity;

    // HTTP status of a failed upgrade (for example 401), when the server returned one.
    public int? HandshakeHttpStatus { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        try
        {
            if (clientSocket is not null)
            {
                clientSocket.Options.KeepAliveInterval = KeepAliveInterval;
                clientSocket.Options.CollectHttpResponseDetails = true;

                // The bearer token is sent only on this handshake request.
                clientSocket.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
                try
                {
                    if (invoker is null)
                    {
                        await clientSocket.ConnectAsync(endpoint!, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await clientSocket.ConnectAsync(endpoint!, invoker, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (WebSocketException)
                {
                    int status = (int)clientSocket.HttpStatusCode;
                    HandshakeHttpStatus = status == 0 ? null : status;
                    throw;
                }
            }

            notificationPump = Task.Run(() => NotificationPumpAsync(lifetime.Token), CancellationToken.None);
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
        foreach (Task? pump in new[] { receivePump, notificationPump })
        {
            if (pump is null)
            {
                continue;
            }

            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception) when (lifetime.IsCancellationRequested)
            {
                // The pumps are canceled by Close; their terminal failure was already reported.
            }
        }

        await dispatcher.WhenOutstandingCompletedAsync().ConfigureAwait(false);
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
        byte[] buffer = new byte[ReceiveBufferBytes];
        int malformedCount = 0;
        try
        {
            using var message = new MemoryStream();
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
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

                JsonRpcMessage? rpcMessage;
                try
                {
                    rpcMessage = JsonSerializer.Deserialize<JsonRpcMessage>(
                        message.GetBuffer().AsSpan(0, checked((int)message.Length)),
                        SerializerOptions);
                    malformedCount = 0;
                }
                catch (JsonException)
                {
                    // Mirror the stdio transport: tolerate isolated malformed frames, but treat a
                    // sustained stream of them as a broken peer.
                    if (++malformedCount >= MaxConsecutiveMalformedMessages)
                    {
                        throw new InvalidDataException("The app-server emitted three malformed WebSocket messages.");
                    }

                    continue;
                }

                if (rpcMessage is null)
                {
                    continue;
                }

                if (rpcMessage.IsResponse || rpcMessage.IsRequest || rpcMessage.IsNotification)
                {
                    Interlocked.Increment(ref inboundActivity);
                    InboundActivity?.Invoke(this, EventArgs.Empty);
                }

                if (rpcMessage.IsResponse)
                {
                    // Responses are resolved on the receive loop, never behind a notification
                    // handler, so a handler that awaits a request cannot block its own response.
                    JsonRpcServerRequestDispatcher.ResolveResponse(pending, rpcMessage);
                }
                else if (rpcMessage.IsRequest || rpcMessage.IsNotification)
                {
                    await inbound.Writer.WriteAsync(rpcMessage, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException) when (cancellationToken.IsCancellationRequested)
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

    // Notifications are delivered one at a time in wire order, and a server request starts only
    // after every earlier notification was handled, matching the stdio transport. Streaming deltas,
    // turn lifecycle, and approvals for a just-started item depend on that ordering. Request
    // handlers run concurrently once started, so a pending approval never blocks later messages.
    private async Task NotificationPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (JsonRpcMessage message in inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (message.IsRequest)
                {
                    dispatcher.Start(message, RequestReceived, cancellationToken);
                    continue;
                }

                Func<JsonRpcMessage, CancellationToken, Task>? handler = NotificationReceived;
                if (handler is null)
                {
                    continue;
                }

                try
                {
                    await handler(message, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // One failing observer must not stop delivery of later notifications.
                    _ = ex;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Close(Exception? exception)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        // Fail outstanding requests before canceling the lifetime so callers observe a connection
        // loss rather than a cancellation they did not request.
        var closedException = new JsonRpcConnectionClosedException(
            exception?.Message ?? "The app-server WebSocket connection closed.");
        foreach (TaskCompletionSource<JsonElement> completion in pending.Values)
        {
            completion.TrySetException(closedException);
        }

        pending.Clear();
        lifetime.Cancel();
        inbound.Writer.TryComplete();
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

        Closed?.Invoke(this, exception);
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref closed) != 0 || socket.State is WebSocketState.Aborted or WebSocketState.Closed)
        {
            throw new JsonRpcConnectionClosedException("The app-server WebSocket connection is closed.");
        }
    }
}
