using System.Text.Json;

namespace Codex.AppServer.Protocol;

public interface IJsonRpcConnection : IAsyncDisposable
{
    event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

    event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

    event EventHandler<Exception?>? Closed;

    Task StartAsync(CancellationToken cancellationToken);

    Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken);

    Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken);
}

/// <summary>
/// A transport that counts valid parsed inbound JSON-RPC messages. An idle watchdog compares
/// the sequence before and after a probe to tell a silent peer from a merely quiet one.
/// </summary>
public interface IInboundActivitySource
{
    long InboundActivitySequence { get; }
}
