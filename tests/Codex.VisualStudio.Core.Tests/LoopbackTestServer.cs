using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Codex.VisualStudio.Core.Tests;

/// <summary>
/// Test-owned loopback listener. It speaks just enough HTTP/1.1 to answer probes and to upgrade
/// a WebSocket handshake, optionally over TLS with an ephemeral self-signed certificate that is
/// never added to any certificate store.
/// </summary>
internal sealed class LoopbackTestServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<RecordedRequest, Stream, Task> handler;
    private readonly X509Certificate2? certificate;
    private readonly Task acceptLoop;
    private readonly ConcurrentBag<Task> connections = [];

    public LoopbackTestServer(Func<RecordedRequest, Stream, Task> handler, bool useTls = false, IPAddress? address = null)
    {
        this.handler = handler;
        certificate = useTls ? CreateEphemeralCertificate() : null;
        listener = new TcpListener(address ?? IPAddress.Loopback, 0);
        listener.Start();
        acceptLoop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public static Task RespondAsync(Stream stream, int status, string reason, string body = "", string extraHeaders = "")
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Length: {content.Length}\r\nConnection: close\r\n{extraHeaders}\r\n";
        byte[] bytes = [.. Encoding.ASCII.GetBytes(head), .. content];
        return stream.WriteAsync(bytes).AsTask();
    }

    // Completes the RFC 6455 opening handshake and returns the server-side WebSocket.
    public static async Task<WebSocket> AcceptWebSocketAsync(RecordedRequest request, Stream stream)
    {
        string key = request.Headers["Sec-WebSocket-Key"];
#pragma warning disable CA5350 // RFC 6455 defines Sec-WebSocket-Accept with SHA-1; it is not a security control.
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
        string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        foreach (Task connection in connections)
        {
            try
            {
                await connection.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        certificate?.Dispose();
        lifetime.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            connections.Add(Task.Run(() => HandleAsync(client)));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            Stream stream = client.GetStream();
            try
            {
                if (certificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(certificate).ConfigureAwait(false);
                    stream = ssl;
                }

                RecordedRequest? request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                Requests.Enqueue(request);
                await handler(request, stream).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The client may abort; tests assert on their own observations.
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<RecordedRequest?> ReadRequestAsync(Stream stream)
    {
        var buffer = new List<byte>();
        byte[] one = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            int read = await stream.ReadAsync(one).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
            int n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
            {
                break;
            }
        }

        string[] lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        string[] requestLine = lines[0].Split(' ');
        return new RecordedRequest(requestLine[0], requestLine[1], headers);
    }

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=codex-test-untrusted", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        // Round-trip through PFX so SChannel can use the private key. Without PersistKeySet the
        // imported key is removed when the certificate is disposed; no store or trust changes.
        return new X509Certificate2(created.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}

internal sealed record RecordedRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers);
