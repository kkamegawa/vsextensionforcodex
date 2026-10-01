using System.Net;
using System.Net.Sockets;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

/// <summary>
/// An endpoint that passed the shared policy and, for the exact "localhost" name, the Worker's
/// DNS verification. Only a verified local endpoint bypasses the proxy.
/// </summary>
public sealed class VerifiedRemoteEndpoint
{
    internal VerifiedRemoteEndpoint(RemoteEndpointValidation validation, IReadOnlyList<IPAddress> pinnedAddresses)
    {
        Validation = validation;
        PinnedAddresses = pinnedAddresses;
    }

    public RemoteEndpointValidation Validation { get; }

    public Uri Endpoint => Validation.Endpoint!;

    // Every verified loopback answer for an exact-localhost endpoint, in resolver order; empty
    // otherwise. A server may listen on only ::1 or only 127.0.0.1, so connects try each in turn.
    public IReadOnlyList<IPAddress> PinnedAddresses { get; }

    public bool IsVerifiedLocal => Validation.HostKind == RemoteEndpointHostKind.LoopbackLiteral || PinnedAddresses.Count > 0;
}

/// <summary>
/// Worker-lifetime networking factory shared by the WebSocket handshake and health diagnosis.
/// Both paths use the same captured <see cref="HttpClient.DefaultProxy"/> resolver, disable
/// cookies, redirects, and default credentials, and keep the platform TLS defaults. Proxy
/// failure never falls back to a direct connection, and proxy configuration is never logged.
/// </summary>
public sealed class WorkerNetworking : IDisposable
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveHost;
    private readonly SocketsHttpHandler proxiedHandler;
    private readonly SocketsHttpHandler directLoopbackHandler;
    private int disposed;

    public WorkerNetworking(
        IWebProxy? proxy = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        // Captured once: a new Worker inherits the Visual Studio process environment, and
        // restarting the Worker refreshes the Windows user proxy settings.
        Proxy = new SchemeMappingProxy(proxy ?? HttpClient.DefaultProxy);
        this.resolveHost = resolveHost ?? Dns.GetHostAddressesAsync;
        proxiedHandler = CreateHandler();
        proxiedHandler.UseProxy = true;
        proxiedHandler.Proxy = Proxy;
        ProxiedInvoker = new HttpMessageInvoker(proxiedHandler, disposeHandler: false);

        directLoopbackHandler = CreateHandler();
        directLoopbackHandler.UseProxy = false;
        directLoopbackHandler.ConnectCallback = ConnectLoopbackLiteralAsync;
        DirectLoopbackInvoker = new HttpMessageInvoker(directLoopbackHandler, disposeHandler: false);
    }

    public IWebProxy Proxy { get; }

    public HttpMessageInvoker ProxiedInvoker { get; }

    public HttpMessageInvoker DirectLoopbackInvoker { get; }

    /// <summary>
    /// Applies the shared endpoint policy and, for exact localhost, resolves the name and
    /// requires every answer to be loopback. Runs before any token-file access.
    /// </summary>
    public async Task<VerifiedRemoteEndpoint> VerifyAsync(string? endpoint, CancellationToken cancellationToken)
    {
        RemoteEndpointValidation validation = RemoteEndpointPolicy.Validate(endpoint);
        if (!validation.IsValid)
        {
            throw new RemoteConnectionException(RemoteConnectionFailure.InvalidEndpoint);
        }

        if (validation.HostKind != RemoteEndpointHostKind.ExactLocalhost)
        {
            return new VerifiedRemoteEndpoint(validation, []);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await resolveHost("localhost", cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            throw new RemoteConnectionException(RemoteConnectionFailure.NetworkFailure);
        }

        if (addresses.Length == 0 || addresses.Any(static address => !IPAddress.IsLoopback(Unmap(address))))
        {
            throw new RemoteConnectionException(RemoteConnectionFailure.InvalidEndpoint);
        }

        // Keep the whole verified set (mapped forms connect over IPv4); no later lookup is made.
        IPAddress[] pinned = addresses.Select(Unmap).Distinct().ToArray();
        return new VerifiedRemoteEndpoint(validation, pinned);
    }

    /// <summary>
    /// Returns the invoker for a verified endpoint and whether the caller owns (and must
    /// dispose) it. Exact localhost gets a per-attempt invoker pinned to the verified addresses so a
    /// second DNS lookup cannot change the destination; the URI authority is preserved for Host
    /// and TLS processing.
    /// </summary>
    public (HttpMessageInvoker Invoker, bool OwnedByCaller) SelectInvoker(VerifiedRemoteEndpoint endpoint)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (endpoint.PinnedAddresses.Count > 0)
        {
            IReadOnlyList<IPAddress> pinned = endpoint.PinnedAddresses;
            SocketsHttpHandler handler = CreateHandler();
            handler.UseProxy = false;
            handler.ConnectCallback = (context, cancellationToken) =>
                ConnectFirstReachableAsync(pinned, context.DnsEndPoint.Port, cancellationToken);
            return (new HttpMessageInvoker(handler, disposeHandler: true), true);
        }

        return endpoint.Validation.HostKind == RemoteEndpointHostKind.LoopbackLiteral
            ? (DirectLoopbackInvoker, false)
            : (ProxiedInvoker, false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        ProxiedInvoker.Dispose();
        DirectLoopbackInvoker.Dispose();
        proxiedHandler.Dispose();
        directLoopbackHandler.Dispose();
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    private static IPAddress Unmap(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // The direct handler is only selected for loopback literals. Refuse anything else so a
    // misrouted request can never leave the machine without the proxy policy.
    private static async ValueTask<Stream> ConnectLoopbackLiteralAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out IPAddress? address) || !IPAddress.IsLoopback(Unmap(address)))
        {
            throw new HttpRequestException("The direct connection is limited to loopback literals.");
        }

        // An IPv4-mapped literal (::ffff:127.0.0.1) needs an IPv4 socket; an IPv6 socket that is
        // not dual-mode cannot reach it.
        return await ConnectPinnedAsync(Unmap(address), context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
    }

    // Tries each verified address in order and returns the first connection that succeeds. Only
    // connection failures move on; cancellation stops at once.
    internal static async ValueTask<Stream> ConnectFirstReachableAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        SocketException? lastFailure = null;
        foreach (IPAddress address in addresses)
        {
            try
            {
                return await ConnectPinnedAsync(address, port, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                lastFailure = ex;
            }
        }

        throw lastFailure ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static async ValueTask<Stream> ConnectPinnedAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves proxies for ws/wss as their HTTP/HTTPS equivalents, so HTTP_PROXY, HTTPS_PROXY,
    /// ALL_PROXY, NO_PROXY, and the Windows user settings apply consistently to the WebSocket
    /// handshake and to health probes.
    /// </summary>
    internal sealed class SchemeMappingProxy : IWebProxy
    {
        private readonly IWebProxy inner;

        public SchemeMappingProxy(IWebProxy inner)
        {
            this.inner = inner;
        }

        public ICredentials? Credentials
        {
            get => inner.Credentials;
            set => inner.Credentials = value;
        }

        public Uri? GetProxy(Uri destination) => inner.GetProxy(Map(destination));

        public bool IsBypassed(Uri host) => inner.IsBypassed(Map(host));

        internal static Uri Map(Uri destination)
        {
            string? scheme = destination.Scheme.ToLowerInvariant() switch
            {
                "ws" => Uri.UriSchemeHttp,
                "wss" => Uri.UriSchemeHttps,
                _ => null,
            };
            if (scheme is null)
            {
                return destination;
            }

            var builder = new UriBuilder(destination) { Scheme = scheme };
            if (destination.IsDefaultPort)
            {
                builder.Port = -1;
            }

            return builder.Uri;
        }
    }
}
