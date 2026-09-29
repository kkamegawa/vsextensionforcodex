using System.Collections.Concurrent;
using System.Text.Json;

namespace Codex.AppServer.Protocol;

/// <summary>
/// Transport-independent handling of app-server requests and client responses. Every
/// <see cref="IJsonRpcConnection"/> implementation shares this type so request ownership,
/// response suppression after close, and error shaping cannot drift between transports.
/// </summary>
internal sealed class JsonRpcServerRequestDispatcher
{
    private readonly Func<object, CancellationToken, Task> send;
    private readonly ConcurrentDictionary<string, Task> outstanding = new(StringComparer.Ordinal);

    public JsonRpcServerRequestDispatcher(Func<object, CancellationToken, Task> send)
    {
        this.send = send;
    }

    /// <summary>
    /// Starts handling one server request. A reused outstanding id is ignored so the first
    /// request remains the sole owner of the response id and no second prompt is shown.
    /// </summary>
    public void Start(
        JsonRpcMessage message,
        Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? handler,
        CancellationToken cancellationToken)
    {
        string? id = message.GetIdKey();
        if (id is null)
        {
            return;
        }

        var registration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!outstanding.TryAdd(id, registration.Task))
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RespondAsync(message, handler, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The response path already shapes protocol errors; this guard only covers a
                    // close/write race so the request task never becomes unobserved.
                    _ = ex;
                }
                finally
                {
                    outstanding.TryRemove(new KeyValuePair<string, Task>(id, registration.Task));
                    registration.TrySetResult();
                }
            },
            CancellationToken.None);
    }

    /// <summary>Completes when every request that was outstanding at the time of the call ends.</summary>
    public Task WhenOutstandingCompletedAsync() => Task.WhenAll(outstanding.Values.ToArray());

    public static void ResolveResponse(
        ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending,
        JsonRpcMessage message)
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

    private async Task RespondAsync(
        JsonRpcMessage message,
        Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? handler,
        CancellationToken cancellationToken)
    {
        object? id = ToWireId(message.Id!.Value);
        try
        {
            JsonElement result = handler is null
                ? JsonSerializer.SerializeToElement(new { })
                : await handler(message, cancellationToken).ConfigureAwait(false);
            await SendUnlessClosedAsync(new { id, result }, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            await SendUnlessClosedAsync(
                new { id, error = new { code = ex.Code, message = ex.Message } },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the connection cancels the handler and intentionally suppresses its response.
        }
        catch (Exception ex)
        {
            await SendUnlessClosedAsync(
                new { id, error = new { code = -32603, message = ex.Message } },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendUnlessClosedAsync(object response, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await send(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The connection closed while the response was being written.
        }
    }

    private static object? ToWireId(JsonElement id)
        => id.ValueKind == JsonValueKind.Number ? id.GetInt64() : id.GetString();
}
