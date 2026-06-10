namespace Codex.AppServer.Protocol;

public sealed record RetryDecision(bool ShouldRetry, TimeSpan Delay, string Reason);

public sealed class JsonRpcRetryPolicy
{
    public const int ServerOverloadedCode = -32001;

    private readonly int maxAttempts;
    private readonly TimeSpan initialDelay;

    public JsonRpcRetryPolicy(int maxAttempts = 3, TimeSpan? initialDelay = null)
    {
        this.maxAttempts = maxAttempts;
        this.initialDelay = initialDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public RetryDecision Evaluate(Exception exception, int attempt, bool isIdempotent)
    {
        if (!isIdempotent)
        {
            return new RetryDecision(false, TimeSpan.Zero, "Non-idempotent requests are never retried.");
        }

        if (exception is not JsonRpcRemoteException remote || remote.Code != ServerOverloadedCode)
        {
            return new RetryDecision(false, TimeSpan.Zero, "Only server-overload responses are retryable.");
        }

        if (attempt >= maxAttempts)
        {
            return new RetryDecision(false, TimeSpan.Zero, "The overload retry limit was reached.");
        }

        return new RetryDecision(
            true,
            TimeSpan.FromMilliseconds(initialDelay.TotalMilliseconds * Math.Pow(2, attempt)),
            "Retry an idempotent request after exponential backoff.");
    }
}

public static class JsonRpcConnectionRetryExtensions
{
    public static async Task<System.Text.Json.JsonElement> SendIdempotentRequestAsync(
        this IJsonRpcConnection connection,
        string method,
        object? parameters,
        TimeSpan timeout,
        JsonRpcRetryPolicy retryPolicy,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await connection.SendRequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcRemoteException)
            {
                RetryDecision decision = retryPolicy.Evaluate(ex, attempt, isIdempotent: true);
                if (!decision.ShouldRetry)
                {
                    throw;
                }

                await Task.Delay(decision.Delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

public sealed record WebSocketTransportValidation(bool IsAllowed, string? Reason);

public sealed class WebSocketTransportSecurityPolicy
{
    private readonly int minimumTokenLength;

    public WebSocketTransportSecurityPolicy(int minimumTokenLength = 32)
    {
        this.minimumTokenLength = minimumTokenLength;
    }

    public WebSocketTransportValidation Validate(bool enabled, Uri? endpoint, string? capabilityToken)
    {
        if (!enabled)
        {
            return new WebSocketTransportValidation(false, "WebSocket transport is disabled by default.");
        }

        if (endpoint is null
            || (endpoint.Scheme != Uri.UriSchemeWs && endpoint.Scheme != Uri.UriSchemeWss)
            || !endpoint.IsLoopback)
        {
            return new WebSocketTransportValidation(false, "WebSocket transport must use a loopback ws/wss endpoint.");
        }

        if (string.IsNullOrWhiteSpace(capabilityToken) || capabilityToken.Length < minimumTokenLength)
        {
            return new WebSocketTransportValidation(false, "WebSocket transport requires a capability or signed bearer token.");
        }

        return new WebSocketTransportValidation(true, null);
    }
}
