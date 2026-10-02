using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class RemoteEndpointPolicyTests
{
    [TestMethod]
    [DataRow("wss://app-server.example.invalid")]
    [DataRow("wss://app-server.example.invalid:8443")]
    [DataRow("WSS://App-Server.Example.Invalid/")]
    [DataRow("wss://app-server.example.invalid/codex/app-server")]
    [DataRow("wss://203.0.113.10:8443")]
    [DataRow("wss://[2001:db8::1]:8443")]
    [DataRow("wss://localhost:8443")]
    [DataRow("wss://127.0.0.1:8443")]
    [DataRow("wss://123abc.example:8443")]
    [DataRow("wss://3d.cafe")]
    [DataRow("wss://deadbeef.example")]
    public void SecureEndpoints_AreAccepted(string endpoint)
    {
        RemoteEndpointValidation result = RemoteEndpointPolicy.Validate(endpoint);

        Assert.IsTrue(result.IsValid, result.Message);
        Assert.IsTrue(result.IsSecure);
        Assert.AreEqual(string.Empty, result.Message);
    }

    [TestMethod]
    [DataRow("ws://localhost:8080", RemoteEndpointHostKind.ExactLocalhost)]
    [DataRow("ws://LOCALHOST:8080", RemoteEndpointHostKind.ExactLocalhost)]
    [DataRow("ws://127.0.0.1:8080", RemoteEndpointHostKind.LoopbackLiteral)]
    [DataRow("ws://127.255.255.254:8080", RemoteEndpointHostKind.LoopbackLiteral)]
    [DataRow("ws://[::1]:8080", RemoteEndpointHostKind.LoopbackLiteral)]
    [DataRow("ws://[::ffff:127.0.0.1]:8080", RemoteEndpointHostKind.LoopbackLiteral)]
    public void PlainWebSocket_IsAcceptedOnlyForEligibleLoopbackForms(string endpoint, RemoteEndpointHostKind kind)
    {
        RemoteEndpointValidation result = RemoteEndpointPolicy.Validate(endpoint);

        Assert.IsTrue(result.IsValid, result.Message);
        Assert.IsFalse(result.IsSecure);
        Assert.AreEqual(kind, result.HostKind);
        Assert.IsTrue(result.IsLocalCandidate);
    }

    [TestMethod]
    [DataRow("ws://app-server.example.invalid")]
    [DataRow("ws://localhost.:8080")]
    [DataRow("ws://foo.localhost:8080")]
    [DataRow("ws://localhost.example.invalid:8080")]
    [DataRow("ws://10.0.0.5:8080")]
    [DataRow("ws://[2001:db8::1]:8080")]
    [DataRow("ws://[::ffff:10.0.0.5]:8080")]
    [DataRow("ws://my-loopback-alias:8080")]
    public void PlainWebSocket_ToNonLoopbackOrAlias_IsRejected(string endpoint)
    {
        RemoteEndpointValidation result = RemoteEndpointPolicy.Validate(endpoint);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(RemoteEndpointRejection.PlainWebSocketRequiresLoopback, result.Rejection);
        Assert.AreEqual("Use a wss endpoint. Plain ws is allowed only for loopback.", result.Message);
    }

    [TestMethod]
    [DataRow("ws://0.0.0.0:8080")]
    [DataRow("wss://0.0.0.0:8443")]
    [DataRow("ws://[::]:8080")]
    [DataRow("wss://[::]:8443")]
    [DataRow("wss://[::ffff:0.0.0.0]:8443")]
    public void UnspecifiedDestinations_AreRejectedForEveryScheme(string endpoint)
    {
        Assert.AreEqual(RemoteEndpointRejection.UnspecifiedAddress, RemoteEndpointPolicy.Validate(endpoint).Rejection);
    }

    [TestMethod]
    [DataRow("ws://127.1:8080")]
    [DataRow("ws://0x7f.0.0.1:8080")]
    [DataRow("ws://0177.0.0.1:8080")]
    [DataRow("ws://2130706433:8080")]
    [DataRow("ws://127.000.000.001:8080")]
    [DataRow("wss://010.0.0.1:8443")]
    [DataRow("ws://[0:0:0:0:0:0:0:1]:8080")]
    [DataRow("wss://1.2.3.0x4:8443")]
    [DataRow("wss://example.127:8443")]
    public void AmbiguousLiteralEncodings_AreRejected(string endpoint)
    {
        RemoteEndpointValidation result = RemoteEndpointPolicy.Validate(endpoint);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(RemoteEndpointRejection.AmbiguousLiteral, result.Rejection, endpoint);
    }

    [TestMethod]
    public void UserInformationQueryAndFragment_AreRejected()
    {
        Assert.AreEqual(RemoteEndpointRejection.UserInfo, RemoteEndpointPolicy.Validate("wss://user:secret@example.invalid").Rejection);
        Assert.AreEqual(RemoteEndpointRejection.UserInfo, RemoteEndpointPolicy.Validate("wss://user@example.invalid").Rejection);
        Assert.AreEqual(RemoteEndpointRejection.QueryOrFragment, RemoteEndpointPolicy.Validate("wss://example.invalid/?token=abc").Rejection);
        Assert.AreEqual(RemoteEndpointRejection.QueryOrFragment, RemoteEndpointPolicy.Validate("wss://example.invalid/#frag").Rejection);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void MissingEndpoint_IsRejected(string? endpoint)
    {
        Assert.AreEqual(RemoteEndpointRejection.Missing, RemoteEndpointPolicy.Validate(endpoint).Rejection);
    }

    [TestMethod]
    [DataRow("example.invalid")]
    [DataRow("/relative/path")]
    [DataRow("wss:example.invalid")]
    public void RelativeEndpoints_AreRejected(string endpoint)
    {
        Assert.AreEqual(RemoteEndpointRejection.NotAbsolute, RemoteEndpointPolicy.Validate(endpoint).Rejection);
    }

    [TestMethod]
    [DataRow("https://example.invalid")]
    [DataRow("http://127.0.0.1:8080")]
    [DataRow("ftp://example.invalid")]
    [DataRow("file://C:/tokens/x")]
    public void OtherSchemes_AreRejected(string endpoint)
    {
        Assert.AreEqual(RemoteEndpointRejection.UnsupportedScheme, RemoteEndpointPolicy.Validate(endpoint).Rejection);
    }

    [TestMethod]
    public void RoutingPath_IsAllowedAndReported()
    {
        Assert.IsTrue(RemoteEndpointPolicy.Validate("wss://example.invalid/codex").HasRoutingPath);
        Assert.IsFalse(RemoteEndpointPolicy.Validate("wss://example.invalid/").HasRoutingPath);
        Assert.IsFalse(RemoteEndpointPolicy.Validate("wss://example.invalid").HasRoutingPath);
    }

    [TestMethod]
    public void RejectionMessages_NeverEchoTheEndpoint()
    {
        foreach (string endpoint in new[]
        {
            "wss://user:hunter2@example.invalid", "wss://example.invalid/?token=abc", "ws://secret-host.invalid",
            "ws://127.1", "ftp://secret-host.invalid",
        })
        {
            RemoteEndpointValidation result = RemoteEndpointPolicy.Validate(endpoint);
            Assert.IsFalse(result.Message.Contains("hunter2", StringComparison.Ordinal));
            Assert.IsFalse(result.Message.Contains("secret-host", StringComparison.Ordinal));
            Assert.IsFalse(result.Message.Contains("abc", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void BearerTokenPolicy_EnforcesRfc6750GrammarAndLength()
    {
        Assert.IsTrue(BearerTokenPolicy.IsValid(new string('a', 32)));
        Assert.IsTrue(BearerTokenPolicy.IsValid("abcDEF0123456789-._~+/abcdefghijk=="));
        Assert.IsFalse(BearerTokenPolicy.IsValid(new string('a', 31)));
        Assert.IsFalse(BearerTokenPolicy.IsValid(null));
        Assert.IsFalse(BearerTokenPolicy.IsValid(string.Empty));
        Assert.IsFalse(BearerTokenPolicy.IsValid(new string('a', 20) + " " + new string('a', 20)));
        Assert.IsFalse(BearerTokenPolicy.IsValid(new string('a', 20) + "\t" + new string('a', 20)));
        Assert.IsFalse(BearerTokenPolicy.IsValid(new string('a', 40) + "\u0001"));
        Assert.IsFalse(BearerTokenPolicy.IsValid(new string('a', 40) + "é"));
        Assert.IsFalse(BearerTokenPolicy.IsValid(new string('=', 40)));
        Assert.IsFalse(BearerTokenPolicy.IsValid(new string('a', 20) + "=" + new string('a', 20)));
        Assert.IsFalse(BearerTokenPolicy.IsValid(new string('a', 40) + ","));
    }

    [TestMethod]
    public void TokenFilePathPolicy_AcceptsOnlyDriveQualifiedAbsolutePaths()
    {
        Assert.IsTrue(TokenFilePathPolicy.IsSyntacticallyValid(@"C:\tokens\app-server.token"));
        Assert.IsTrue(TokenFilePathPolicy.IsSyntacticallyValid("d:/tokens/app-server.token"));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(@"\\server\share\token"));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(@"\\?\C:\tokens\token"));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(@"\\.\pipe\token"));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(@"tokens\token"));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(@"C:token"));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(@"C:\tokens\token:stream"));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(@"C:\tokens\*.token"));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(string.Empty));
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(null));
    }

    [TestMethod]
    [DataRow(@"C:\CON")]
    [DataRow(@"C:\temp\NUL.txt")]
    [DataRow(@"C:\temp\COM1")]
    [DataRow(@"C:\temp\lpt9.token")]
    [DataRow(@"C:\temp\aux .token")]
    [DataRow(@"C:\temp\conout$")]
    [DataRow("C:\\temp\\COM\u00B9")]
    [DataRow(@"C:\prn\token")]
    [DataRow(@"C:\temp\token.")]
    [DataRow(@"C:\temp \token")]
    public void TokenFilePathPolicy_RejectsDosDeviceNamesAndAmbiguousComponents(string path)
    {
        Assert.IsFalse(TokenFilePathPolicy.IsSyntacticallyValid(path), path);
    }

    [TestMethod]
    public void TokenFilePathPolicy_AcceptsNamesThatOnlyResembleDevices()
    {
        Assert.IsTrue(TokenFilePathPolicy.IsSyntacticallyValid(@"C:\tokens\console.token"));
        Assert.IsTrue(TokenFilePathPolicy.IsSyntacticallyValid(@"C:\tokens\COM10"));
        Assert.IsTrue(TokenFilePathPolicy.IsSyntacticallyValid(@"C:\nullable\aux-token"));
    }

    [TestMethod]
    public void Fingerprint_CoversMetadataButNotTokenContents()
    {
        string baseline = RemoteProfileFingerprint.Compute("Build", "wss://example.invalid", @"C:\t\token", @"C:\repo", "/repo", true);

        Assert.AreEqual(baseline, RemoteProfileFingerprint.Compute("Build", "wss://example.invalid", @"C:\t\token", @"C:\repo", "/repo", true));
        Assert.AreEqual(baseline, RemoteProfileFingerprint.Compute(" Build ", "wss://example.invalid ", @"C:\t\token", @"C:\repo", "/repo", true));
        Assert.AreNotEqual(baseline, RemoteProfileFingerprint.Compute("Build2", "wss://example.invalid", @"C:\t\token", @"C:\repo", "/repo", true));
        Assert.AreNotEqual(baseline, RemoteProfileFingerprint.Compute("Build", "wss://other.invalid", @"C:\t\token", @"C:\repo", "/repo", true));
        Assert.AreNotEqual(baseline, RemoteProfileFingerprint.Compute("Build", "wss://example.invalid", @"C:\t\other", @"C:\repo", "/repo", true));
        Assert.AreNotEqual(baseline, RemoteProfileFingerprint.Compute("Build", "wss://example.invalid", @"C:\t\token", @"C:\other", "/repo", true));
        Assert.AreNotEqual(baseline, RemoteProfileFingerprint.Compute("Build", "wss://example.invalid", @"C:\t\token", @"C:\repo", "/other", true));
        Assert.AreNotEqual(baseline, RemoteProfileFingerprint.Compute("Build", "wss://example.invalid", @"C:\t\token", @"C:\repo", "/repo", false));
        Assert.AreNotEqual(
            RemoteProfileFingerprint.Compute("ab", "c", null, string.Empty, string.Empty, true),
            RemoteProfileFingerprint.Compute("a", "bc", null, string.Empty, string.Empty, true));
    }
}
