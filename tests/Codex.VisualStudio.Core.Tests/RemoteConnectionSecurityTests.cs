using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class BearerTokenFileReaderTests
{
    private static readonly string Token = "tok_" + new string('A', 28) + "-._~+/9==";

    [TestMethod]
    public async Task ReadsTokenWithAndWithoutBomAndTrimsOnlyOuterWhitespace()
    {
        using var directory = new TempDirectory();
        string plain = directory.Write("plain.token", Encoding.ASCII.GetBytes("  " + Token + "\r\n"));
        string bom = directory.Write("bom.token", [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes(Token)]);
        var reader = LocalReader();

        Assert.AreEqual(Token, await reader.ReadAsync(plain, CancellationToken.None));
        Assert.AreEqual(Token, await reader.ReadAsync(bom, CancellationToken.None));
    }

    [TestMethod]
    public async Task EnforcesTheSixteenKibibyteBoundOnTheOpenedStream()
    {
        using var directory = new TempDirectory();
        string atLimit = directory.Write("limit.token", Encoding.ASCII.GetBytes(new string('a', BearerTokenFileReader.MaxBytes)));
        string overLimit = directory.Write("over.token", Encoding.ASCII.GetBytes(new string('a', BearerTokenFileReader.MaxBytes + 1)));
        var reader = LocalReader();

        Assert.AreEqual(BearerTokenFileReader.MaxBytes, (await reader.ReadAsync(atLimit, CancellationToken.None)).Length);
        await AssertFailureAsync(reader, overLimit, RemoteConnectionFailure.TokenFileInvalid);
    }

    [TestMethod]
    public async Task RejectsMalformedContents()
    {
        using var directory = new TempDirectory();
        var reader = LocalReader();
        byte[][] invalid =
        [
            [],
            Encoding.ASCII.GetBytes("   "),
            Encoding.ASCII.GetBytes(new string('a', 31)),
            Encoding.ASCII.GetBytes(new string('a', 20) + " " + new string('a', 20)),
            Encoding.ASCII.GetBytes(new string('a', 20) + "\n" + new string('a', 20)),
            Encoding.ASCII.GetBytes(new string('a', 40) + "\u0007"),
            Encoding.UTF8.GetBytes(new string('a', 40) + "é"),
            [.. Encoding.ASCII.GetBytes(new string('a', 40)), 0xC3, 0x28],
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(new string('a', 40))],
        ];
        for (int i = 0; i < invalid.Length; i++)
        {
            string path = directory.Write($"invalid{i}.token", invalid[i]);
            await AssertFailureAsync(reader, path, RemoteConnectionFailure.TokenFileInvalid);
        }
    }

    [TestMethod]
    public async Task ExistingButUnreadableFileIsNotReportedAsMissing()
    {
        using var directory = new TempDirectory();
        string path = directory.Write("locked.token", Encoding.ASCII.GetBytes(Token));

        // An exclusive handle makes the open fail with a sharing violation; no ACL changes.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await AssertFailureAsync(LocalReader(), path, RemoteConnectionFailure.TokenFileUnreadable);
        }
    }

    [TestMethod]
    public async Task RejectsMissingFilesDirectoriesAndNonLocalPaths()
    {
        using var directory = new TempDirectory();
        var reader = LocalReader();

        await AssertFailureAsync(reader, Path.Combine(directory.Path, "missing.token"), RemoteConnectionFailure.TokenFileMissing);
        await AssertFailureAsync(reader, Path.Combine(directory.Path, "missing", "app-server.token"), RemoteConnectionFailure.TokenFileMissing);
        await AssertFailureAsync(reader, directory.Path, RemoteConnectionFailure.TokenFileUnreadable);
        await AssertFailureAsync(reader, @"\\server\share\app-server.token", RemoteConnectionFailure.TokenFileUnreadable);
        await AssertFailureAsync(reader, @"\\?\C:\tokens\app-server.token", RemoteConnectionFailure.TokenFileUnreadable);
        await AssertFailureAsync(reader, @"relative\app-server.token", RemoteConnectionFailure.TokenFileUnreadable);
        await AssertFailureAsync(reader, null, RemoteConnectionFailure.TokenFileUnreadable);

        // A mapped network drive is not a local file even though its path looks drive-qualified.
        string valid = directory.Write("valid.token", Encoding.ASCII.GetBytes(Token));
        var networkReader = new BearerTokenFileReader(static _ => DriveType.Network);
        await AssertFailureAsync(networkReader, valid, RemoteConnectionFailure.TokenFileUnreadable);
    }

    [TestMethod]
    public async Task RequiresTheFinalLinkTargetToBeLocal()
    {
        using var directory = new TempDirectory();
        string target = directory.Write("target.token", Encoding.ASCII.GetBytes(Token));
        string link = Path.Combine(directory.Path, "link.token");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating a symbolic link requires Developer Mode or elevation on this machine.");
            return;
        }

        Assert.AreEqual(Token, await LocalReader().ReadAsync(link, CancellationToken.None));
        var reader = new BearerTokenFileReader(path => string.Equals(path, target, StringComparison.OrdinalIgnoreCase)
            ? DriveType.Network
            : DriveType.Fixed);
        await AssertFailureAsync(reader, link, RemoteConnectionFailure.TokenFileUnreadable);
    }

    [TestMethod]
    public async Task RequiresTheFinalPathOfTheOpenedHandleToBeLocal()
    {
        using var directory = new TempDirectory();
        string path = directory.Write("redirected.token", Encoding.ASCII.GetBytes(Token));

        // A directory junction or symlink earlier in the path can redirect the open to a share.
        var redirected = new BearerTokenFileReader(static _ => DriveType.Fixed, static _ => @"\\server\share\app-server.token");
        var unknown = new BearerTokenFileReader(static _ => DriveType.Fixed, static _ => null);

        await AssertFailureAsync(redirected, path, RemoteConnectionFailure.TokenFileUnreadable);
        await AssertFailureAsync(unknown, path, RemoteConnectionFailure.TokenFileUnreadable);
    }

    [TestMethod]
    public void FinalPathOfALocalHandleIsDriveQualified()
    {
        using var directory = new TempDirectory();
        string path = directory.Write("final.token", Encoding.ASCII.GetBytes(Token));
        using var stream = File.OpenRead(path);

        string? finalPath = BearerTokenFileReader.GetFinalPath(stream.SafeFileHandle);

        Assert.IsNotNull(finalPath);
        Assert.IsTrue(TokenFilePathPolicy.IsSyntacticallyValid(finalPath), finalPath);
        Assert.AreEqual(Path.GetFileName(path), Path.GetFileName(finalPath));
    }

    [TestMethod]
    public async Task RotationIsObservedOnTheNextRead()
    {
        using var directory = new TempDirectory();
        string path = directory.Write("rotating.token", Encoding.ASCII.GetBytes(new string('a', 32)));
        var reader = LocalReader();
        Assert.AreEqual(new string('a', 32), await reader.ReadAsync(path, CancellationToken.None));

        await File.WriteAllTextAsync(path, new string('b', 32));

        Assert.AreEqual(new string('b', 32), await reader.ReadAsync(path, CancellationToken.None));
    }

    internal static BearerTokenFileReader LocalReader() => new(static _ => DriveType.Fixed);

    private static async Task AssertFailureAsync(BearerTokenFileReader reader, string? path, RemoteConnectionFailure expected)
    {
        RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(
            () => reader.ReadAsync(path, CancellationToken.None));
        Assert.AreEqual(expected, ex.Failure, path);
        Assert.IsFalse(ex.Message.Contains(new string('a', 20), StringComparison.Ordinal));
    }
}

[TestClass]
public sealed class SecretLeaseTests
{
    [TestMethod]
    public void RegisteredSecretIsRedactedUntilEveryLeaseIsReleased()
    {
        var redactor = new SecretRedactor();
        const string secret = "plainsecretwithoutanymarker0123456789";

        IDisposable first = redactor.RegisterSecret(secret);
        IDisposable second = redactor.RegisterSecret(secret);
        Assert.AreEqual("echo [REDACTED] done", redactor.Redact($"echo {secret} done"));

        first.Dispose();
        first.Dispose();
        Assert.AreEqual("echo [REDACTED] done", redactor.Redact($"echo {secret} done"), "A duplicate lease must keep redaction active.");

        second.Dispose();
        Assert.AreEqual($"echo {secret} done", redactor.Redact($"echo {secret} done"));
        Assert.AreEqual(0, redactor.ActiveSecretCount);
    }

    [TestMethod]
    public async Task ConcurrentEqualSecretLeasesAreReferenceCounted()
    {
        var redactor = new SecretRedactor();
        const string secret = "concurrentsecret0123456789abcdefghij";
        using IDisposable anchor = redactor.RegisterSecret(secret);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                using IDisposable lease = redactor.RegisterSecret(secret);
                Assert.IsFalse(redactor.Redact(secret).Contains(secret, StringComparison.Ordinal));
            }
        })));

        Assert.AreEqual("[REDACTED]", redactor.Redact(secret));
        Assert.AreEqual(1, redactor.ActiveSecretCount);
    }

    [TestMethod]
    public void SeparateWorkersDoNotShareLeases()
    {
        var first = new SecretRedactor();
        var second = new SecretRedactor();
        const string secret = "workerlocalsecret0123456789abcdefghij";

        using IDisposable lease = first.RegisterSecret(secret);

        Assert.AreEqual("[REDACTED]", first.Redact(secret));
        Assert.AreEqual(secret, second.Redact(secret));
    }

    [TestMethod]
    public void WorkerDiagnosticsRedactsBeforeAppendingToTheSharedLog()
    {
        var redactor = new SecretRedactor();
        WorkerDiagnostics.Configure(redactor);
        string secret = "diagsecret" + Guid.NewGuid().ToString("N");
        using IDisposable lease = redactor.RegisterSecret(secret);

        WorkerDiagnostics.Write($"handshake echoed {secret} and Authorization: Bearer {secret}");

        string logPath = Path.Combine(Path.GetTempPath(), "Kkamegawa.CodexForVisualStudio", "diagnostics.log");
        string log = ReadShared(logPath);
        Assert.IsFalse(log.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(log, "handshake echoed [REDACTED]");
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

[TestClass]
public sealed class WorkerNetworkingTests
{
    [TestMethod]
    public async Task ExactLocalhostIsResolvedVerifiedAndPinnedBeforeUse()
    {
        int lookups = 0;
        using var networking = new WorkerNetworking(new RecordingProxy(), (_, _) =>
        {
            lookups++;
            return Task.FromResult(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback });
        });

        VerifiedRemoteEndpoint endpoint = await networking.VerifyAsync("ws://localhost:9000", CancellationToken.None);

        Assert.AreEqual(1, lookups);
        CollectionAssert.AreEqual(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback }, endpoint.PinnedAddresses.ToArray());
        Assert.IsTrue(endpoint.IsVerifiedLocal);
        (HttpMessageInvoker invoker, bool owned) = networking.SelectInvoker(endpoint);
        Assert.IsTrue(owned);
        Assert.AreNotSame(networking.ProxiedInvoker, invoker);
        Assert.AreNotSame(networking.DirectLoopbackInvoker, invoker);
        invoker.Dispose();
    }

    [TestMethod]
    public async Task LocalhostResolvingToAnyNonLoopbackAddressIsRejected()
    {
        using var networking = new WorkerNetworking(
            new RecordingProxy(),
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Parse("192.0.2.10") }));

        RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(
            () => networking.VerifyAsync("ws://localhost:9000", CancellationToken.None));

        Assert.AreEqual(RemoteConnectionFailure.InvalidEndpoint, ex.Failure);
    }

    [TestMethod]
    public async Task DnsIsNeverUsedToAuthorizeOtherNamesOrLiterals()
    {
        int lookups = 0;
        using var networking = new WorkerNetworking(new RecordingProxy(), (_, _) =>
        {
            lookups++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });

        await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => networking.VerifyAsync("ws://loopback-alias.invalid", CancellationToken.None));
        VerifiedRemoteEndpoint remote = await networking.VerifyAsync("wss://app-server.example.invalid", CancellationToken.None);
        VerifiedRemoteEndpoint literal = await networking.VerifyAsync("ws://127.0.0.1:9000", CancellationToken.None);
        VerifiedRemoteEndpoint remoteLiteral = await networking.VerifyAsync("wss://203.0.113.10:8443", CancellationToken.None);

        Assert.AreEqual(0, lookups);
        Assert.AreSame(networking.ProxiedInvoker, networking.SelectInvoker(remote).Invoker);
        Assert.AreSame(networking.DirectLoopbackInvoker, networking.SelectInvoker(literal).Invoker);
        Assert.AreSame(networking.ProxiedInvoker, networking.SelectInvoker(remoteLiteral).Invoker, "A remote wss literal must still follow the proxy.");
    }

    [TestMethod]
    public void ProxyResolutionMapsWebSocketSchemesToHttpSchemes()
    {
        var recording = new RecordingProxy();
        using var networking = new WorkerNetworking(recording);

        networking.Proxy.GetProxy(new Uri("wss://app-server.example.invalid/codex"));
        networking.Proxy.IsBypassed(new Uri("ws://127.0.0.1:9000/"));

        CollectionAssert.AreEqual(
            new[] { "https://app-server.example.invalid/codex", "http://127.0.0.1:9000/" },
            recording.Seen.Select(static uri => uri.AbsoluteUri).ToArray());
    }

    [TestMethod]
    public async Task RemoteHandshakeUsesTheProxyAndNeverForwardsTheBearerTokenToIt()
    {
        await using var proxy = new LoopbackTestServer((_, stream) => LoopbackTestServer.RespondAsync(stream, 502, "Bad Gateway"));
        var recording = new RecordingProxy(new Uri($"http://127.0.0.1:{proxy.Port}/"));
        using var networking = new WorkerNetworking(recording);
        var redactor = new SecretRedactor();
        await using var host = new CodexProcessHost(redactor, networking, BearerTokenFileReaderTests.LocalReader());
        using var directory = new TempDirectory();
        string token = new('t', 40);
        string tokenPath = directory.Write("proxy.token", Encoding.ASCII.GetBytes(token));

        RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => host.StartRemoteAsync(
            new RemoteConnectionRequest("wss://app-server.example.invalid/codex", tokenPath),
            CancellationToken.None));

        Assert.AreEqual(RemoteConnectionFailure.NetworkFailure, ex.Failure);
        RecordedRequest connect = proxy.Requests.Single();
        Assert.AreEqual("CONNECT", connect.Method);
        Assert.AreEqual("app-server.example.invalid:443", connect.Target);
        Assert.IsFalse(connect.Headers.ContainsKey("Authorization"));
        Assert.IsFalse(connect.Headers.Values.Any(value => value.Contains(token, StringComparison.Ordinal)));
        Assert.AreEqual(0, redactor.ActiveSecretCount, "A failed handshake must release its secret lease.");
    }

    internal sealed class RecordingProxy : IWebProxy
    {
        private readonly Uri? proxy;

        public RecordingProxy(Uri? proxy = null)
        {
            this.proxy = proxy;
        }

        public List<Uri> Seen { get; } = [];

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination)
        {
            Seen.Add(destination);
            return proxy ?? destination;
        }

        public bool IsBypassed(Uri host)
        {
            Seen.Add(host);
            return proxy is null;
        }
    }
}

[TestClass]
public sealed class RemoteHandshakeTests
{
    private static readonly string Token = new('k', 40);

    [TestMethod]
    public async Task LoopbackHandshakeSendsTheBearerTokenOnlyOnTheUpgradeRequest()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackTestServer(async (request, stream) =>
        {
            using WebSocket socket = await LoopbackTestServer.AcceptWebSocketAsync(request, stream);
            byte[] buffer = new byte[4096];
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            received.TrySetResult(Encoding.UTF8.GetString(buffer, 0, result.Count));
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{\"ok\":true}}"), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        });
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("ok.token", Encoding.ASCII.GetBytes(Token));
        var redactor = new SecretRedactor();
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        await using var host = new CodexProcessHost(redactor, networking, BearerTokenFileReaderTests.LocalReader());

        await host.StartRemoteAsync(new RemoteConnectionRequest($"ws://127.0.0.1:{server.Port}/codex", tokenPath), CancellationToken.None);
        System.Text.Json.JsonElement response = await host.Connection!.SendRequestAsync("model/list", new { }, TimeSpan.FromSeconds(5), CancellationToken.None);
        string firstFrame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(response.GetProperty("ok").GetBoolean());
        RecordedRequest upgrade = server.Requests.Single();
        Assert.AreEqual("/codex", upgrade.Target);
        Assert.AreEqual($"Bearer {Token}", upgrade.Headers["Authorization"]);
        Assert.IsFalse(firstFrame.Contains(Token, StringComparison.Ordinal));
        Assert.IsNull(host.ProcessId);
        Assert.AreEqual(1, redactor.ActiveSecretCount);

        await host.StopAsync(CancellationToken.None);
        Assert.AreEqual(0, redactor.ActiveSecretCount, "Stopping releases the lease after the connection is retired.");
    }

    [TestMethod]
    public async Task ExactLocalhostConnectsThroughThePinnedVerifiedAddress()
    {
        await using var server = new LoopbackTestServer(async (request, stream) =>
        {
            using WebSocket socket = await LoopbackTestServer.AcceptWebSocketAsync(request, stream);
            await socket.ReceiveAsync(new byte[16].AsMemory(), CancellationToken.None);
        });
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("ok.token", Encoding.ASCII.GetBytes(Token));
        int lookups = 0;
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy(), (_, _) =>
        {
            lookups++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });
        await using var host = new CodexProcessHost(new SecretRedactor(), networking, BearerTokenFileReaderTests.LocalReader());

        await host.StartRemoteAsync(new RemoteConnectionRequest($"ws://localhost:{server.Port}", tokenPath), CancellationToken.None);

        Assert.AreEqual(1, lookups);
        Assert.AreEqual($"localhost:{server.Port}", server.Requests.Single().Headers["Host"], "The URI authority is preserved for Host processing.");
    }

    [TestMethod]
    [DataRow(true, DisplayName = "server on IPv6 loopback only")]
    [DataRow(false, DisplayName = "server on IPv4 loopback only")]
    public async Task ExactLocalhostTriesEachVerifiedAddressUntilOneConnects(bool ipv6Only)
    {
        await using var server = new LoopbackTestServer(
            async (request, stream) =>
            {
                using WebSocket socket = await LoopbackTestServer.AcceptWebSocketAsync(request, stream);
                await socket.ReceiveAsync(new byte[16].AsMemory(), CancellationToken.None);
            },
            address: ipv6Only ? IPAddress.IPv6Loopback : IPAddress.Loopback);
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("dual.token", Encoding.ASCII.GetBytes(Token));

        // The unreachable family is listed first, so the connect must move on to the next answer.
        IPAddress[] answers = ipv6Only
            ? [IPAddress.Loopback, IPAddress.IPv6Loopback]
            : [IPAddress.IPv6Loopback, IPAddress.Loopback];
        int lookups = 0;
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy(), (_, _) =>
        {
            lookups++;
            return Task.FromResult(answers);
        });
        await using var host = new CodexProcessHost(new SecretRedactor(), networking, BearerTokenFileReaderTests.LocalReader());

        await host.StartRemoteAsync(new RemoteConnectionRequest($"ws://localhost:{server.Port}", tokenPath), CancellationToken.None);

        Assert.AreEqual(1, lookups, "Only the pre-verified answers are used; no second lookup is made.");
        Assert.AreEqual(1, server.Requests.Count);
        Assert.AreEqual($"localhost:{server.Port}", server.Requests.Single().Headers["Host"]);
    }

    [TestMethod]
    public async Task HealthDiagnosisReachesAnIPv6OnlyLocalhostListener()
    {
        await using var server = new LoopbackTestServer(
            (_, stream) => LoopbackTestServer.RespondAsync(stream, 200, "OK"),
            address: IPAddress.IPv6Loopback);
        using var networking = new WorkerNetworking(
            new WorkerNetworkingTests.RecordingProxy(),
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }));
        var diagnostics = new RemoteConnectionDiagnostics(networking);

        ConnectionDiagnosticsResult result = await diagnostics.DiagnoseAsync(
            new ConnectionDiagnosticsRequest { ProfileName = "Local", Endpoint = $"ws://localhost:{server.Port}" },
            CancellationToken.None);

        Assert.AreEqual(HealthProbeState.Healthy, result.Health.State);
        Assert.AreEqual(HealthProbeState.Healthy, result.Ready.State);
    }

    [TestMethod]
    public async Task EndpointIsRejectedBeforeTheTokenFileIsRead()
    {
        using var networking = new WorkerNetworking(
            new WorkerNetworkingTests.RecordingProxy(),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.10") }));
        await using var host = new CodexProcessHost(new SecretRedactor(), networking, BearerTokenFileReaderTests.LocalReader());

        // The token path does not exist; an invalid endpoint must win before any token access.
        foreach (string endpoint in new[] { "ws://app-server.example.invalid", "ws://localhost:9000", "wss://user:pw@example.invalid" })
        {
            RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => host.StartRemoteAsync(
                new RemoteConnectionRequest(endpoint, @"C:\definitely\missing\app-server.token"),
                CancellationToken.None));
            Assert.AreEqual(RemoteConnectionFailure.InvalidEndpoint, ex.Failure, endpoint);
        }
    }

    [TestMethod]
    public async Task AuthenticationRejectionIsCategorizedAndReleasesTheLease()
    {
        await using var server = new LoopbackTestServer((_, stream) => LoopbackTestServer.RespondAsync(
            stream, 401, "Unauthorized", $"bad token {Token}", "WWW-Authenticate: Bearer\r\n"));
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("rejected.token", Encoding.ASCII.GetBytes(Token));
        var redactor = new SecretRedactor();
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        await using var host = new CodexProcessHost(redactor, networking, BearerTokenFileReaderTests.LocalReader());

        RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => host.StartRemoteAsync(
            new RemoteConnectionRequest($"ws://127.0.0.1:{server.Port}", tokenPath),
            CancellationToken.None));

        Assert.AreEqual(RemoteConnectionFailure.AuthenticationRejected, ex.Failure);
        Assert.IsFalse(ex.Message.Contains(Token, StringComparison.Ordinal));
        Assert.IsNull(ex.InnerException);
        Assert.AreEqual(0, redactor.ActiveSecretCount);
    }

    [TestMethod]
    public async Task MappedLoopbackLiteralConnectsOverIPv4()
    {
        await using var server = new LoopbackTestServer(async (request, stream) =>
        {
            using WebSocket socket = await LoopbackTestServer.AcceptWebSocketAsync(request, stream);
            await socket.ReceiveAsync(new byte[16].AsMemory(), CancellationToken.None);
        });
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("mapped.token", Encoding.ASCII.GetBytes(Token));
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        await using var host = new CodexProcessHost(new SecretRedactor(), networking, BearerTokenFileReaderTests.LocalReader());

        await host.StartRemoteAsync(new RemoteConnectionRequest($"ws://[::ffff:127.0.0.1]:{server.Port}", tokenPath), CancellationToken.None);

        Assert.AreEqual(1, server.Requests.Count);
    }

    [TestMethod]
    public async Task UpgradeRejectedByAReachableServerIsNotReportedAsANetworkFailure()
    {
        await using var server = new LoopbackTestServer((_, stream) => LoopbackTestServer.RespondAsync(stream, 404, "Not Found"));
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("path.token", Encoding.ASCII.GetBytes(Token));
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        await using var host = new CodexProcessHost(new SecretRedactor(), networking, BearerTokenFileReaderTests.LocalReader());

        RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => host.StartRemoteAsync(
            new RemoteConnectionRequest($"ws://127.0.0.1:{server.Port}/wrong", tokenPath),
            CancellationToken.None));

        Assert.AreEqual(RemoteConnectionFailure.UpgradeRejected, ex.Failure);
    }

    [TestMethod]
    public async Task RedirectsAreNotFollowedForTheCredentialBearingHandshake()
    {
        await using var other = new LoopbackTestServer((_, stream) => LoopbackTestServer.RespondAsync(stream, 200, "OK"));
        await using var server = new LoopbackTestServer((_, stream) => LoopbackTestServer.RespondAsync(
            stream, 307, "Temporary Redirect", string.Empty, $"Location: ws://127.0.0.1:{other.Port}/\r\n"));
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("redirect.token", Encoding.ASCII.GetBytes(Token));
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        await using var host = new CodexProcessHost(new SecretRedactor(), networking, BearerTokenFileReaderTests.LocalReader());

        RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => host.StartRemoteAsync(
            new RemoteConnectionRequest($"ws://127.0.0.1:{server.Port}", tokenPath),
            CancellationToken.None));

        Assert.AreEqual(RemoteConnectionFailure.UpgradeRejected, ex.Failure);
        Assert.AreEqual(1, server.Requests.Count);
        Assert.AreEqual(0, other.Requests.Count, "The token must never be sent to a redirect target.");
    }

    [TestMethod]
    public async Task UntrustedCertificateIsRejectedWithoutChangingTrust()
    {
        await using var server = new LoopbackTestServer((_, stream) => LoopbackTestServer.RespondAsync(stream, 200, "OK"), useTls: true);
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("tls.token", Encoding.ASCII.GetBytes(Token));
        var redactor = new SecretRedactor();
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        await using var host = new CodexProcessHost(redactor, networking, BearerTokenFileReaderTests.LocalReader());

        RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => host.StartRemoteAsync(
            new RemoteConnectionRequest($"wss://127.0.0.1:{server.Port}", tokenPath),
            CancellationToken.None));

        Assert.AreEqual(RemoteConnectionFailure.CertificateRejected, ex.Failure);
        Assert.AreEqual(0, server.Requests.Count, "No HTTP request (and no token) may cross an untrusted TLS session.");
        Assert.AreEqual(0, redactor.ActiveSecretCount);
    }

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("localhost")]
    public async Task TrustedCertificateCompletesTheWssHandshake(string host)
    {
        // The client trusts the test certificate as a custom root, so platform chain building and
        // hostname matching still run; the pinned localhost path must preserve the authority.
        using X509Certificate2 certificate = LoopbackTestServer.CreateCertificate("CN=codex-test-trusted", ["localhost"], [IPAddress.Loopback]);
        await using var server = new LoopbackTestServer(
            async (request, stream) =>
            {
                using WebSocket socket = await LoopbackTestServer.AcceptWebSocketAsync(request, stream);
                byte[] buffer = new byte[4096];
                await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{\"ok\":true}}"), WebSocketMessageType.Text, true, CancellationToken.None);
                await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            },
            serverCertificate: certificate);
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("trusted.token", Encoding.ASCII.GetBytes(Token));
        var redactor = new SecretRedactor();
        using var networking = new WorkerNetworking(
            new WorkerNetworkingTests.RecordingProxy(),
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
            TrustOnly(certificate));
        await using var codexHost = new CodexProcessHost(redactor, networking, BearerTokenFileReaderTests.LocalReader());

        await codexHost.StartRemoteAsync(new RemoteConnectionRequest($"wss://{host}:{server.Port}/codex", tokenPath), CancellationToken.None);
        System.Text.Json.JsonElement response = await codexHost.Connection!.SendRequestAsync("model/list", new { }, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.IsTrue(response.GetProperty("ok").GetBoolean());
        RecordedRequest upgrade = server.Requests.Single();
        Assert.AreEqual("/codex", upgrade.Target);
        StringAssert.StartsWith(upgrade.Headers["Host"], host + ":");
        Assert.AreEqual($"Bearer {Token}", upgrade.Headers["Authorization"]);
        await codexHost.StopAsync(CancellationToken.None);
        Assert.AreEqual(0, redactor.ActiveSecretCount);
    }

    [TestMethod]
    public async Task TrustedCertificateForAnotherHostIsRejected()
    {
        using X509Certificate2 certificate = LoopbackTestServer.CreateCertificate("CN=codex-test-other", ["codex-other.test"], []);
        await using var server = new LoopbackTestServer((_, stream) => LoopbackTestServer.RespondAsync(stream, 200, "OK"), serverCertificate: certificate);
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("mismatch.token", Encoding.ASCII.GetBytes(Token));
        var redactor = new SecretRedactor();
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy(), resolveHost: null, TrustOnly(certificate));
        await using var codexHost = new CodexProcessHost(redactor, networking, BearerTokenFileReaderTests.LocalReader());

        RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => codexHost.StartRemoteAsync(
            new RemoteConnectionRequest($"wss://127.0.0.1:{server.Port}", tokenPath),
            CancellationToken.None));

        Assert.AreEqual(RemoteConnectionFailure.CertificateRejected, ex.Failure);
        Assert.AreEqual(0, server.Requests.Count, "A trusted chain for another host must not carry the token.");
        Assert.AreEqual(0, redactor.ActiveSecretCount);
    }

    // Trusts exactly one test certificate for this client; no store or machine trust changes.
    private static X509ChainPolicy TrustOnly(X509Certificate2 root)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(root);
        return policy;
    }

    [TestMethod]
    public async Task StageCancellationIsTimeoutButParentCancellationStaysCancellation()
    {
        // ClientWebSocket.ConnectAsync may report either cancellation as WebSocketException.
        RemoteConnectionException timeout = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() =>
            CodexProcessHost.RunStageAsync<bool>(
                async stage =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, stage);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    throw new WebSocketException("connect aborted");
                },
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));
        Assert.AreEqual(RemoteConnectionFailure.Timeout, timeout.Failure);

        using var parent = new CancellationTokenSource();
        OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CodexProcessHost.RunStageAsync<bool>(
                stage =>
                {
                    parent.Cancel();
                    throw new WebSocketException("connect aborted");
                },
                TimeSpan.FromSeconds(30),
                parent.Token));
        Assert.AreEqual(parent.Token, canceled.CancellationToken);
    }

    [TestMethod]
    public async Task HandshakeStageCapIsReportedAsTimeout()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackTestServer((_, _) => hold.Task);
        using var directory = new TempDirectory();
        string tokenPath = directory.Write("slow.token", Encoding.ASCII.GetBytes(Token));
        var redactor = new SecretRedactor();
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        var limits = RemoteStartupLimits.Default with { Handshake = TimeSpan.FromMilliseconds(200) };
        await using var host = new CodexProcessHost(redactor, networking, BearerTokenFileReaderTests.LocalReader(), limits);

        try
        {
            RemoteConnectionException ex = await Assert.ThrowsExactlyAsync<RemoteConnectionException>(() => host.StartRemoteAsync(
                new RemoteConnectionRequest($"ws://127.0.0.1:{server.Port}", tokenPath),
                CancellationToken.None));
            Assert.AreEqual(RemoteConnectionFailure.Timeout, ex.Failure);
            Assert.AreEqual(0, redactor.ActiveSecretCount);
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ReconnectReadsTheRotatedTokenFile()
    {
        await using var server = new LoopbackTestServer(async (request, stream) =>
        {
            using WebSocket socket = await LoopbackTestServer.AcceptWebSocketAsync(request, stream);
            try
            {
                await socket.ReceiveAsync(new byte[16].AsMemory(), CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        });
        using var directory = new TempDirectory();
        string first = new('a', 40);
        string second = new('b', 40);
        string tokenPath = directory.Write("rotate.token", Encoding.ASCII.GetBytes(first));
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        await using var host = new CodexProcessHost(new SecretRedactor(), networking, BearerTokenFileReaderTests.LocalReader());
        var request = new RemoteConnectionRequest($"ws://127.0.0.1:{server.Port}", tokenPath);

        await host.StartRemoteAsync(request, CancellationToken.None);
        await File.WriteAllTextAsync(tokenPath, second);
        await host.StartRemoteAsync(request, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { $"Bearer {first}", $"Bearer {second}" },
            server.Requests.Select(static item => item.Headers["Authorization"]).ToArray());
    }
}

[TestClass]
public sealed class RemoteConnectionDiagnosticsTests
{
    [TestMethod]
    public async Task ProbesRootHealthAndReadyConcurrentlyWithoutCredentials()
    {
        await using var server = new LoopbackTestServer((request, stream) => request.Target == "/readyz"
            ? LoopbackTestServer.RespondAsync(stream, 503, "Service Unavailable", "secret body")
            : LoopbackTestServer.RespondAsync(stream, 200, "OK", "secret body"));
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        var diagnostics = new RemoteConnectionDiagnostics(networking);

        ConnectionDiagnosticsResult result = await diagnostics.DiagnoseAsync(
            new ConnectionDiagnosticsRequest { ProfileName = "Local", Endpoint = $"ws://127.0.0.1:{server.Port}/codex/app" },
            CancellationToken.None);

        Assert.AreEqual(HealthProbeState.Healthy, result.Health.State);
        Assert.AreEqual(200, result.Health.HttpStatus);
        Assert.AreEqual(HealthProbeState.Unhealthy, result.Ready.State);
        Assert.AreEqual(503, result.Ready.HttpStatus);
        Assert.AreEqual(HealthScope.AuthorityRoot, result.Scope);
        Assert.AreEqual(RouteCoverage.Unverified, result.RouteCoverage);
        CollectionAssert.AreEquivalent(new[] { "/healthz", "/readyz" }, server.Requests.Select(static request => request.Target).ToArray());
        foreach (RecordedRequest request in server.Requests)
        {
            Assert.AreEqual("GET", request.Method);
            Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
            Assert.IsFalse(request.Headers.ContainsKey("Origin"));
            Assert.IsFalse(request.Headers.ContainsKey("Cookie"));
        }

        Assert.IsFalse(result.Health.Reason.Contains("secret body", StringComparison.Ordinal));
        Assert.IsFalse(result.Ready.Reason.Contains("secret body", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RootEndpointReportsNoRouteGapAndRedirectsAreNotFollowed()
    {
        await using var server = new LoopbackTestServer((_, stream) => LoopbackTestServer.RespondAsync(
            stream, 302, "Found", string.Empty, "Location: http://127.0.0.1:1/elsewhere\r\n"));
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        var diagnostics = new RemoteConnectionDiagnostics(networking);

        ConnectionDiagnosticsResult result = await diagnostics.DiagnoseAsync(
            new ConnectionDiagnosticsRequest { ProfileName = "Local", Endpoint = $"ws://127.0.0.1:{server.Port}" },
            CancellationToken.None);

        Assert.AreEqual(RouteCoverage.NotApplicable, result.RouteCoverage);
        Assert.AreEqual(HealthProbeState.Redirected, result.Health.State);
        Assert.AreEqual(HealthProbeState.Redirected, result.Ready.State);
        Assert.AreEqual(2, server.Requests.Count);
    }

    [TestMethod]
    public async Task BothProbesShareOneDeadline()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackTestServer((_, _) => hold.Task);
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        var diagnostics = new RemoteConnectionDiagnostics(networking, budget: TimeSpan.FromMilliseconds(400));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ConnectionDiagnosticsResult result = await diagnostics.DiagnoseAsync(
                new ConnectionDiagnosticsRequest { ProfileName = "Slow", Endpoint = $"ws://127.0.0.1:{server.Port}" },
                CancellationToken.None);

            Assert.AreEqual(HealthProbeState.TimedOut, result.Health.State);
            Assert.AreEqual(HealthProbeState.TimedOut, result.Ready.State);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2), "Concurrent probes must share the budget.");
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [TestMethod]
    public async Task UnreachableAndInvalidEndpointsAreReportedWithoutThrowing()
    {
        using var networking = new WorkerNetworking(new WorkerNetworkingTests.RecordingProxy());
        var diagnostics = new RemoteConnectionDiagnostics(networking);
        int unusedPort;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            unusedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        ConnectionDiagnosticsResult unreachable = await diagnostics.DiagnoseAsync(
            new ConnectionDiagnosticsRequest { ProfileName = "Down", Endpoint = $"ws://127.0.0.1:{unusedPort}" },
            CancellationToken.None);
        ConnectionDiagnosticsResult invalid = await diagnostics.DiagnoseAsync(
            new ConnectionDiagnosticsRequest { ProfileName = "Bad", Endpoint = "ws://app-server.example.invalid" },
            CancellationToken.None);

        Assert.AreEqual(HealthProbeState.Unreachable, unreachable.Health.State);
        Assert.IsNull(unreachable.RejectionReason);
        Assert.IsNotNull(invalid.RejectionReason);
        Assert.AreEqual(HealthProbeState.NotChecked, invalid.Health.State);
    }

    [TestMethod]
    public void AuthorityRootMapsSchemesAndDropsThePath()
    {
        Assert.AreEqual("https://example.invalid/", RemoteConnectionDiagnostics.GetAuthorityRoot(new Uri("wss://example.invalid/codex/app")).AbsoluteUri);
        Assert.AreEqual("http://127.0.0.1:9000/", RemoteConnectionDiagnostics.GetAuthorityRoot(new Uri("ws://127.0.0.1:9000/x")).AbsoluteUri);
        Assert.AreEqual("https://example.invalid:8443/", RemoteConnectionDiagnostics.GetAuthorityRoot(new Uri("wss://example.invalid:8443")).AbsoluteUri);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-vs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string name, byte[] contents)
    {
        string path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
