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

