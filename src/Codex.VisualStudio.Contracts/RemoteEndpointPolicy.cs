using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Codex.VisualStudio.Contracts;

public enum RemoteEndpointRejection
{
    None,
    Missing,
    NotAbsolute,
    UnsupportedScheme,
    UserInfo,
    QueryOrFragment,
    UnspecifiedAddress,
    AmbiguousLiteral,
    PlainWebSocketRequiresLoopback,
}

public enum RemoteEndpointHostKind
{
    // A DNS name other than the exact "localhost". Permitted only with wss.
    DnsName,

    // The exact hostname "localhost". The Worker must still resolve it and verify that every
    // answer is loopback before it reads a token or opens a socket.
    ExactLocalhost,

    // A canonical IPv4 127.0.0.0/8, IPv6 ::1, or IPv4-mapped IPv6 loopback literal.
    LoopbackLiteral,

    // Any other canonical IP literal. Permitted only with wss.
    Literal,
}

/// <summary>
/// Result of <see cref="RemoteEndpointPolicy.Validate"/>. The message is fixed text; it never
/// echoes the endpoint, so it is safe to show and to log.
/// </summary>
public sealed class RemoteEndpointValidation
{
    private RemoteEndpointValidation(
        RemoteEndpointRejection rejection,
        Uri? endpoint,
        bool isSecure,
        RemoteEndpointHostKind hostKind,
        bool hasRoutingPath,
        string message)
    {
        Rejection = rejection;
        Endpoint = endpoint;
        IsSecure = isSecure;
        HostKind = hostKind;
        HasRoutingPath = hasRoutingPath;
        Message = message;
    }

    public bool IsValid => Rejection == RemoteEndpointRejection.None;

    public RemoteEndpointRejection Rejection { get; }

    public Uri? Endpoint { get; }

    public bool IsSecure { get; }

    public RemoteEndpointHostKind HostKind { get; }

    // True when the WebSocket URI routes through a non-root path. Health probes still target the
    // authority root, so their result does not cover that route.
    public bool HasRoutingPath { get; }

    // Only verified local endpoints bypass the proxy; exact localhost additionally needs the
    // Worker's DNS verification before it may be treated as local.
    public bool IsLocalCandidate => HostKind is RemoteEndpointHostKind.ExactLocalhost or RemoteEndpointHostKind.LoopbackLiteral;

    public string Message { get; }

    internal static RemoteEndpointValidation Reject(RemoteEndpointRejection rejection, string message)
        => new(rejection, null, false, RemoteEndpointHostKind.DnsName, false, message);

    internal static RemoteEndpointValidation Accept(Uri endpoint, bool isSecure, RemoteEndpointHostKind hostKind, bool hasRoutingPath)
        => new(RemoteEndpointRejection.None, endpoint, isSecure, hostKind, hasRoutingPath, string.Empty);
}

/// <summary>
/// Pure endpoint validation shared by the Extension, Worker, and Protocol. It performs no DNS,
/// file, or network access. Remote endpoints must use wss; plain ws is limited to syntactically
/// eligible loopback forms. DNS verification of the exact "localhost" name belongs to the Worker.
/// </summary>
public static class RemoteEndpointPolicy
{
    public const string WebSocketScheme = "ws";
    public const string SecureWebSocketScheme = "wss";

    public static RemoteEndpointValidation Validate(string? endpoint)
    {
        if (endpoint is null || string.IsNullOrWhiteSpace(endpoint))
        {
            return RemoteEndpointValidation.Reject(RemoteEndpointRejection.Missing, "An endpoint is required.");
        }

        string raw = endpoint.Trim();
        int schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
        {
            return RemoteEndpointValidation.Reject(RemoteEndpointRejection.NotAbsolute, "Enter an absolute wss:// endpoint.");
        }

        string scheme = raw.Substring(0, schemeEnd);
        bool secure = string.Equals(scheme, SecureWebSocketScheme, StringComparison.OrdinalIgnoreCase);
        bool plain = string.Equals(scheme, WebSocketScheme, StringComparison.OrdinalIgnoreCase);
        if (!secure && !plain)
        {
            return RemoteEndpointValidation.Reject(
                RemoteEndpointRejection.UnsupportedScheme,
                "Use a wss endpoint. Plain ws is allowed only for loopback.");
        }

        string afterScheme = raw.Substring(schemeEnd + 3);
        int authorityEnd = afterScheme.IndexOfAny(['/', '?', '#']);
        string authority = authorityEnd < 0 ? afterScheme : afterScheme.Substring(0, authorityEnd);
        if (authority.IndexOf('@') >= 0 || uri.UserInfo.Length > 0)
        {
            return RemoteEndpointValidation.Reject(
                RemoteEndpointRejection.UserInfo,
                "Do not put credentials in the endpoint. Use the token file.");
        }

        if (raw.IndexOf('?') >= 0 || raw.IndexOf('#') >= 0)
        {
            return RemoteEndpointValidation.Reject(
                RemoteEndpointRejection.QueryOrFragment,
                "The endpoint must not contain a query string or fragment.");
        }

        string? rawHost = ExtractHost(authority);
        if (rawHost is null || rawHost.Length == 0)
        {
            return RemoteEndpointValidation.Reject(RemoteEndpointRejection.NotAbsolute, "Enter an absolute wss:// endpoint.");
        }

        if (!TryClassifyHost(rawHost, uri, out RemoteEndpointHostKind hostKind, out RemoteEndpointRejection hostRejection))
        {
            return hostRejection == RemoteEndpointRejection.UnspecifiedAddress
                ? RemoteEndpointValidation.Reject(hostRejection, "An unspecified address is not a valid destination.")
                : RemoteEndpointValidation.Reject(hostRejection, "Write IP addresses in canonical form.");
        }

        if (plain && hostKind is not (RemoteEndpointHostKind.ExactLocalhost or RemoteEndpointHostKind.LoopbackLiteral))
        {
            return RemoteEndpointValidation.Reject(
                RemoteEndpointRejection.PlainWebSocketRequiresLoopback,
                "Use a wss endpoint. Plain ws is allowed only for loopback.");
        }

        string path = uri.AbsolutePath;
        bool hasRoutingPath = path.Length > 0 && path != "/";
        return RemoteEndpointValidation.Accept(uri, secure, hostKind, hasRoutingPath);
    }

    // Splits the raw authority into its host without trusting Uri canonicalization, which would
    // accept forms such as "127.1" or "0x7f.0.0.1" and rewrite them as 127.0.0.1.
    private static string? ExtractHost(string authority)
    {
        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            int close = authority.IndexOf(']');
            if (close < 0)
            {
                return null;
            }

            string remainder = authority.Substring(close + 1);
            if (remainder.Length > 0 && !IsPortSuffix(remainder))
            {
                return null;
            }

            return authority.Substring(0, close + 1);
        }

        int colon = authority.LastIndexOf(':');
        if (colon < 0)
        {
            return authority;
        }

        return IsPortSuffix(authority.Substring(colon)) ? authority.Substring(0, colon) : null;
    }

    private static bool IsPortSuffix(string value)
    {
        if (value.Length < 2 || value[0] != ':')
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryClassifyHost(
        string rawHost,
        Uri uri,
        out RemoteEndpointHostKind hostKind,
        out RemoteEndpointRejection rejection)
    {
        hostKind = RemoteEndpointHostKind.DnsName;
        rejection = RemoteEndpointRejection.None;
        if (rawHost.StartsWith("[", StringComparison.Ordinal))
        {
            string literal = rawHost.Substring(1, rawHost.Length - 2);
            if (literal.IndexOf('%') >= 0
                || !IPAddress.TryParse(literal, out IPAddress? address)
                || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                rejection = RemoteEndpointRejection.AmbiguousLiteral;
                return false;
            }

            // An unspecified destination is rejected however it is spelled.
            if (!ClassifyAddress(address, out hostKind, out rejection))
            {
                return false;
            }

            if (!IsCanonicalIPv6(literal, address))
            {
                rejection = RemoteEndpointRejection.AmbiguousLiteral;
                return false;
            }

            return true;
        }

        if (uri.HostNameType == UriHostNameType.IPv4 || LooksNumeric(rawHost))
        {
            if (!IsCanonicalIPv4(rawHost) || !IPAddress.TryParse(rawHost, out IPAddress? address))
            {
                rejection = RemoteEndpointRejection.AmbiguousLiteral;
                return false;
            }

            return ClassifyAddress(address, out hostKind, out rejection);
        }

        if (uri.HostNameType != UriHostNameType.Dns)
        {
            rejection = RemoteEndpointRejection.AmbiguousLiteral;
            return false;
        }

        // Exact ordinal match only: "localhost." and "*.localhost" are DNS names, not loopback.
        hostKind = string.Equals(rawHost, "localhost", StringComparison.OrdinalIgnoreCase)
            ? RemoteEndpointHostKind.ExactLocalhost
            : RemoteEndpointHostKind.DnsName;
        return true;
    }

    private static bool ClassifyAddress(
        IPAddress address,
        out RemoteEndpointHostKind hostKind,
        out RemoteEndpointRejection rejection)
    {
        rejection = RemoteEndpointRejection.None;
        hostKind = RemoteEndpointHostKind.Literal;
        IPAddress effective = address;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            effective = address.MapToIPv4();
        }

        if (effective.Equals(IPAddress.Any) || effective.Equals(IPAddress.IPv6Any) || effective.Equals(IPAddress.IPv6None))
        {
            rejection = RemoteEndpointRejection.UnspecifiedAddress;
            return false;
        }

        if (IPAddress.IsLoopback(effective))
        {
            hostKind = RemoteEndpointHostKind.LoopbackLiteral;
        }

        return true;
    }

    // The canonical spelling is the platform's RFC 5952 text; an IPv4-mapped address may also be
    // written with its embedded IPv4 part in canonical dotted-quad form.
    private static bool IsCanonicalIPv6(string literal, IPAddress address)
    {
        if (string.Equals(address.ToString(), literal, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string MappedPrefix = "::ffff:";
        return address.IsIPv4MappedToIPv6
            && literal.StartsWith(MappedPrefix, StringComparison.OrdinalIgnoreCase)
            && IsCanonicalIPv4(literal.Substring(MappedPrefix.Length));
    }

    // A host whose last label is a number (decimal, or 0x hexadecimal) is parsed as an IPv4
    // address by URL parsers ("127.1", "0x7f.0.0.1", "2130706433"); require the canonical
    // dotted-quad spelling for those. Other names, such as "3d.example", stay DNS names.
    private static bool LooksNumeric(string host)
    {
        string[] labels = host.Split('.');
        int last = labels.Length - 1;
        if (last > 0 && labels[last].Length == 0)
        {
            last--;
        }

        string label = labels[last].ToLowerInvariant();
        if (label.Length == 0)
        {
            return false;
        }

        if (label.StartsWith("0x", StringComparison.Ordinal))
        {
            for (int i = 2; i < label.Length; i++)
            {
                if (!(label[i] is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (char c in label)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCanonicalIPv4(string host)
    {
        string[] parts = host.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            foreach (char c in part)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }

            if (int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// RFC 6750 bearer token grammar (b64token) with a minimum length. Pure and shared so the
/// Worker reader and the Protocol transport apply the same rule.
/// </summary>
public static class BearerTokenPolicy
{
    public const int MinimumLength = 32;

    public static bool IsValid(string? token)
    {
        if (token is null || token.Length < MinimumLength)
        {
            return false;
        }

        int index = 0;
        while (index < token.Length && IsTokenCharacter(token[index]))
        {
            index++;
        }

        // At least one token character before optional trailing '=' padding.
        if (index == 0)
        {
            return false;
        }

        while (index < token.Length && token[index] == '=')
        {
            index++;
        }

        return index == token.Length;
    }

    private static bool IsTokenCharacter(char c)
        => c is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '.' or '_' or '~' or '+' or '/';
}

/// <summary>
/// Syntactic token-file path rule applied at Save. It never touches the file system; the Worker
/// repeats the check and also verifies the drive type and final link target before reading.
/// </summary>
public static class TokenFilePathPolicy
{
    public static bool IsSyntacticallyValid(string? path)
    {
        if (path is null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string value = path.Trim();

        // Only a drive-qualified absolute path ("C:\..."). UNC (\\server\share), device
        // namespaces (\\?\, \\.\), relative, and drive-relative ("C:file") forms are rejected.
        if (value.Length < 4
            || !(value[0] is >= 'A' and <= 'Z' || value[0] is >= 'a' and <= 'z')
            || value[1] != ':'
            || (value[2] != '\\' && value[2] != '/'))
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c < ' ' || c is '<' or '>' or '"' or '|' or '?' or '*')
            {
                return false;
            }
        }

        return value.IndexOf(':', 2) < 0;
    }
}
