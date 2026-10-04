using System.Text.Json;
using Codex.VisualStudio.Contracts;

namespace Codex.AppServer.Protocol;

/// <summary>
/// Bounded overload retry for read-only requests. Retry eligibility is owned by
/// <see cref="ReadOnlyRequestAllowlist"/>, never by the caller.
/// </summary>
public sealed class ReadOnlyRetryPolicy
{
    public const int ServerOverloadedCode = -32001;
    public const int MaxRetries = 3;
    public const double JitterFraction = 0.2;

    private static readonly TimeSpan[] BaseDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000),
    ];

    private readonly Func<double> jitterSource;

    public ReadOnlyRetryPolicy(Func<double>? jitterSource = null, TimeProvider? timeProvider = null)
    {
        this.jitterSource = jitterSource ?? Random.Shared.NextDouble;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public static ReadOnlyRetryPolicy Default { get; } = new();

    public TimeProvider TimeProvider { get; }

    public static TimeSpan GetBaseDelay(int retryIndex) => BaseDelays[retryIndex];

    // Uniform ±20% jitter keeps several clients from retrying an overloaded app-server in
    // lockstep. A non-finite or out-of-range jitter sample is clamped to the [0, 1] range.
    public TimeSpan GetDelay(int retryIndex)
    {
        if (retryIndex is < 0 or >= MaxRetries)
        {
            throw new ArgumentOutOfRangeException(nameof(retryIndex));
        }

        double sample = jitterSource();
        if (double.IsNaN(sample) || double.IsInfinity(sample))
        {
            sample = 0.5;
        }

        double factor = (1 - JitterFraction) + (Math.Clamp(sample, 0, 1) * 2 * JitterFraction);
        return TimeSpan.FromMilliseconds(BaseDelays[retryIndex].TotalMilliseconds * factor);
    }
}

/// <summary>
/// Exact allowlist of app-server methods whose overload responses may be retried. Unknown
/// methods, mutations, forced discovery reloads, and unreviewed history reads are sent once.
/// </summary>
public static class ReadOnlyRequestAllowlist
{
    private static readonly HashSet<string> UnconditionalMethods = new(StringComparer.Ordinal)
    {
        "account/rateLimits/read",
        "thread/list",
        "thread/goal/get",
        "model/list",
        "permissionProfile/list",
        "mcpServerStatus/list",
    };

    public static IReadOnlyCollection<string> Methods { get; } =
    [
        "account/read",
        .. UnconditionalMethods,
        "skills/list",
        "thread/read",
        "thread/turns/list",
        "thread/items/list",
        "thread/attachment/list",
    ];

    public static bool IsRetryable(string method, object? parameters)
    {
        if (UnconditionalMethods.Contains(method))
        {
            return true;
        }

        if (method is "thread/read" or "thread/turns/list" or "thread/items/list" or "thread/attachment/list")
        {
            return HasSafeHistoryReadParameters(method, parameters);
        }

        return method switch
        {
            // refreshToken=true asks the server to refresh credentials; only an explicit false
            // keeps the request a pure read.
            "account/read" => HasExplicitFalse(parameters, "refreshToken"),

            // forceReload can clear the skills cache and rescan discovery; it is sent once.
            "skills/list" => HasExplicitFalse(parameters, "forceReload"),
            _ => false,
        };
    }

    private static bool HasExplicitFalse(object? parameters, string property)
    {
        if (parameters is null)
        {
            return false;
        }

        JsonElement element = parameters is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(parameters, parameters.GetType());
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.False;
    }

    private static bool HasSafeHistoryReadParameters(string method, object? parameters)
    {
        if (parameters is null)
        {
            return false;
        }

        JsonElement element = parameters is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(parameters, parameters.GetType());
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("threadId", out JsonElement threadId)
            || threadId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(threadId.GetString()))
        {
            return false;
        }

        if (method == "thread/read")
        {
            return element.TryGetProperty("includeTurns", out JsonElement includeTurns)
                && includeTurns.ValueKind == JsonValueKind.False;
        }

        if (!element.TryGetProperty("limit", out JsonElement limit)
            || !limit.TryGetInt32(out int pageSize))
        {
            return false;
        }

        int maximum = method switch
        {
            "thread/turns/list" => 50,
            "thread/items/list" => 100,
            "thread/attachment/list" => 50,
            _ => 0,
        };
        if (pageSize is < 1 || pageSize > maximum)
        {
            return false;
        }

        if (method == "thread/items/list"
            && element.TryGetProperty("cursor", out JsonElement cursor)
            && cursor.ValueKind == JsonValueKind.Object)
        {
            return element.TryGetProperty("turnId", out JsonElement turnId)
                && turnId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(turnId.GetString())
                && cursor.TryGetProperty("type", out JsonElement anchorType)
                && anchorType.ValueKind == JsonValueKind.String
                && anchorType.GetString() == "item"
                && cursor.TryGetProperty("itemId", out JsonElement itemId)
                && itemId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(itemId.GetString());
        }

        return true;
    }
}

public static class JsonRpcConnectionRetryExtensions
{
    /// <summary>
    /// Sends a request, retrying only a completed <c>-32001</c> overload response for an
    /// allowlisted read-only method. At most three retries (four sends) share one monotonic
    /// deadline derived from <paramref name="timeout"/>. Every other method is sent exactly once.
    /// </summary>
    public static async Task<JsonElement> SendReadOnlyRequestAsync(
        this IJsonRpcConnection connection,
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ReadOnlyRetryPolicy? policy = null)
    {
        if (!ReadOnlyRequestAllowlist.IsRetryable(method, parameters))
        {
            return await connection.SendRequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        }

        policy ??= ReadOnlyRetryPolicy.Default;
        TimeProvider time = policy.TimeProvider;
        long start = time.GetTimestamp();

        // Observe a close for the whole call, so a connection closed or retired (disposed) before
        // or during a backoff stops the retry promptly.
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnClosed(object? sender, Exception? exception)
        {
            // Closed may have captured this handler before the finally below unsubscribes it and
            // invoke it after the source is disposed; that must not fault the transport's close.
            try
            {
                closed.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        connection.Closed += OnClosed;
        try
        {
            JsonRpcRemoteException? lastOverload = null;
            for (int retry = 0; ; retry++)
            {
                TimeSpan remaining = timeout - time.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero)
                {
                    // Timer granularity can overshoot the deadline during a backoff; the caller
                    // still receives the original overload error rather than a cancellation.
                    if (lastOverload is not null)
                    {
                        throw lastOverload;
                    }

                    throw new OperationCanceledException("The read-only request deadline elapsed.");
                }

                try
                {
                    return await connection.SendRequestAsync(method, parameters, remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonRpcRemoteException ex) when (ex.Code == ReadOnlyRetryPolicy.ServerOverloadedCode && retry < ReadOnlyRetryPolicy.MaxRetries)
                {
                    lastOverload = ex;
                    TimeSpan delay = policy.GetDelay(retry);
                    if (delay >= timeout - time.GetElapsedTime(start))
                    {
                        // No time is left for another attempt; preserve the original overload error.
                        throw;
                    }

                    try
                    {
                        await Task.Delay(delay, time, closed.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new JsonRpcConnectionClosedException("The app-server connection closed during a retry backoff.");
                    }
                }
            }
        }
        finally
        {
            connection.Closed -= OnClosed;
        }
    }
}

public sealed record WebSocketTransportValidation(bool IsAllowed, string? Reason);

/// <summary>
/// WebSocket transport admission: the shared endpoint policy plus the shared bearer-token
/// format. Certificate validation stays the platform default and cannot be disabled.
/// </summary>
public static class WebSocketTransportSecurityPolicy
{
    public static WebSocketTransportValidation Validate(bool enabled, Uri? endpoint, string? capabilityToken)
    {
        if (!enabled)
        {
            return new WebSocketTransportValidation(false, "WebSocket transport is disabled by default.");
        }

        RemoteEndpointValidation validation = RemoteEndpointPolicy.Validate(endpoint?.OriginalString);
        if (!validation.IsValid)
        {
            return new WebSocketTransportValidation(false, validation.Message);
        }

        if (!BearerTokenPolicy.IsValid(capabilityToken))
        {
            return new WebSocketTransportValidation(false, "WebSocket transport requires a capability or signed bearer token.");
        }

        return new WebSocketTransportValidation(true, null);
    }
}
