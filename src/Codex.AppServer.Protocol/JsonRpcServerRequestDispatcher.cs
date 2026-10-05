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
    private const string GenericRequestError = "The client could not process the server request.";
    private readonly Func<object, CancellationToken, Task> send;
    private readonly ConcurrentDictionary<string, RequestRegistration> outstanding = new(StringComparer.Ordinal);

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

        var registration = new RequestRegistration();
        if (!outstanding.TryAdd(id, registration))
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RespondAsync(message, registration, handler, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // This guard only covers an internal dispatch failure; it never sends a
                    // second response after the terminal delivery attempt.
                    _ = ex;
                }
                finally
                {
                    outstanding.TryRemove(new KeyValuePair<string, RequestRegistration>(id, registration));
                    registration.CompleteTask.TrySetResult();
                }
            },
            CancellationToken.None);
    }

    /// <summary>Completes when every request that was outstanding at the time of the call ends.</summary>
    public Task WhenOutstandingCompletedAsync()
        => Task.WhenAll(outstanding.Values.Select(item => item.CompleteTask.Task).ToArray());

    /// <summary>
    /// Suppresses a pending server request response when the ordered app-server notification
    /// reports that the original request was resolved elsewhere.
    /// </summary>
    public void ResolveServerRequest(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("requestId", out JsonElement requestId)
            || !JsonRpcRequestId.TryGetKey(requestId, out string key))
        {
            return;
        }

        if (outstanding.TryGetValue(key, out RequestRegistration? registration))
        {
            registration.TrySuppressResponse();
        }
    }

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
        RequestRegistration registration,
        Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? handler,
        CancellationToken cancellationToken)
    {
        object id = JsonRpcRequestId.ToWireValue(message.Id!.Value);
        object response;
        try
        {
            JsonElement result = handler is null
                ? JsonSerializer.SerializeToElement(new { })
                : await handler(message, cancellationToken).ConfigureAwait(false);
            response = new { id, result };
        }
        catch (JsonRpcRequestResolvedException)
        {
            registration.TrySuppressResponse();
            return;
        }
        catch (JsonRpcRemoteException ex)
        {
            response = new { id, error = new { code = ex.Code, message = ex.Message } };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the connection cancels the handler and intentionally suppresses its response.
            return;
        }
        catch (Exception ex)
        {
            _ = ex;
            // Server-request data can contain challenges or credentials. Never copy unexpected
            // exception text onto the wire.
            response = new { id, error = new { code = -32603, message = GenericRequestError } };
        }

        // Claim the only response slot before attempting transport delivery. If the write fails
        // after partially reaching the peer, a second error response would violate JSON-RPC.
        if (cancellationToken.IsCancellationRequested
            || !registration.TryBeginResponse(() => send(response, cancellationToken), out Task delivery))
        {
            return;
        }

        try
        {
            await delivery.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = ex;
            // The delivery state is uncertain; the terminal response is never retried.
        }
    }

    private sealed class RequestRegistration
    {
        private readonly object gate = new();
        private int terminalState;

        public TaskCompletionSource CompleteTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryBeginResponse(Func<Task> beginDelivery, out Task delivery)
        {
            lock (gate)
            {
                if (terminalState != 0)
                {
                    delivery = Task.CompletedTask;
                    return false;
                }

                terminalState = 1;
                try
                {
                    // Start delivery before allowing the ordered notification pump to mark this
                    // request resolved. A failure remains terminal because peer delivery is unknown.
                    delivery = beginDelivery();
                }
                catch (Exception exception)
                {
                    delivery = Task.FromException(exception);
                }

                return true;
            }
        }

        public bool TrySuppressResponse()
        {
            lock (gate)
            {
                if (terminalState != 0)
                {
                    return false;
                }

                terminalState = 2;
                return true;
            }
        }
    }
}
