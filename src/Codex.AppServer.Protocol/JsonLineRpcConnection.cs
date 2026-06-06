using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Codex.AppServer.Protocol;

public sealed class JsonLineRpcConnection : IJsonRpcConnection
{
    public const int DefaultMaxLineBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly int maxLineBytes;
    private readonly Channel<string> parseQueue;
    private readonly Channel<string> writeQueue;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Task> pumps = new();
    private long nextId;
    private int malformedCount;
    private int started;
    private int closed;

    public JsonLineRpcConnection(Stream input, Stream output, int maxLineBytes = DefaultMaxLineBytes)
    {
        reader = new StreamReader(input, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        this.maxLineBytes = maxLineBytes;
        parseQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        writeQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

    public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

    public event EventHandler<Exception?>? Closed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        pumps.Add(Task.Run(() => ReadPumpAsync(lifetime.Token), CancellationToken.None));
        pumps.Add(Task.Run(() => ParsePumpAsync(lifetime.Token), CancellationToken.None));
        pumps.Add(Task.Run(() => WritePumpAsync(lifetime.Token), CancellationToken.None));
        return Task.CompletedTask;
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, lifetime.Token);
        using CancellationTokenRegistration registration = linked.Token.Register(
            static state => ((TaskCompletionSource<JsonElement>)state!).TrySetCanceled(),
            completion);

        try
        {
            await EnqueueAsync(new { method, @params = parameters, id = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture) }, linked.Token)
                .ConfigureAwait(false);
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
        return EnqueueAsync(new { method, @params = parameters }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Close(null);
        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        reader.Dispose();
        writer.Dispose();
        lifetime.Dispose();
    }

    private async Task EnqueueAsync(object value, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(value, SerializerOptions);
        await writeQueue.Writer.WriteAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadPumpAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (Encoding.UTF8.GetByteCount(line) > maxLineBytes)
                {
                    if (++malformedCount >= 3)
                    {
                        throw new InvalidDataException("The app-server emitted three invalid or oversized JSONL messages.");
                    }

                    continue;
                }

                await parseQueue.Writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);
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
            parseQueue.Writer.TryComplete(failure);
            Close(failure);
        }
    }

    private async Task ParsePumpAsync(CancellationToken cancellationToken)
    {
        await foreach (string line in parseQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            JsonRpcMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<JsonRpcMessage>(line, SerializerOptions);
                malformedCount = 0;
            }
            catch (JsonException)
            {
                if (++malformedCount >= 3)
                {
                    Close(new InvalidDataException("The app-server emitted three malformed JSONL messages."));
                    return;
                }

                continue;
            }

            if (message is null)
            {
                continue;
            }

            if (message.IsResponse)
            {
                ResolveResponse(message);
            }
            else if (message.IsRequest)
            {
                _ = Task.Run(() => ResolveServerRequestAsync(message, cancellationToken), CancellationToken.None);
            }
            else if (message.IsNotification && NotificationReceived is not null)
            {
                await NotificationReceived(message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ResolveServerRequestAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        string? id = message.GetIdKey();
        if (id is null)
        {
            return;
        }

        try
        {
            JsonElement result = RequestReceived is null
                ? JsonSerializer.SerializeToElement(new { })
                : await RequestReceived(message, cancellationToken).ConfigureAwait(false);
            await EnqueueAsync(new { id = ToWireId(message.Id!.Value), result }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await EnqueueAsync(
                new { id = ToWireId(message.Id!.Value), error = new { code = -32603, message = ex.Message } },
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

    private async Task WritePumpAsync(CancellationToken cancellationToken)
    {
        await foreach (string json in writeQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Close(Exception? exception)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        writeQueue.Writer.TryComplete(exception);
        var closedException = new JsonRpcConnectionClosedException(exception?.Message ?? "The app-server connection closed.");
        foreach (TaskCompletionSource<JsonElement> completion in pending.Values)
        {
            completion.TrySetException(closedException);
        }

        pending.Clear();
        Closed?.Invoke(this, exception);
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref closed) != 0)
        {
            throw new JsonRpcConnectionClosedException("The app-server connection is closed.");
        }
    }

    private static object? ToWireId(JsonElement id)
    {
        return id.ValueKind == JsonValueKind.Number ? id.GetInt64() : id.GetString();
    }
}

