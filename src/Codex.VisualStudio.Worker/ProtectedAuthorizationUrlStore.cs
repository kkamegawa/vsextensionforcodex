using System.Net;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

internal sealed class ProtectedAuthorizationUrlStore
{
    private const int MaximumUrlLength = 8192;
    private const int MaximumEntries = 128;
    private readonly object gate = new();
    private readonly Dictionary<(long Generation, string ActionId), ProtectedAuthorizationUrl> urls = new();
    private bool disposed;
    private long retiredGenerationCutoff;

    public bool TryStore(long generation, string? value, out ProtectedAuthorizationUrlInfo info)
    {
        info = new ProtectedAuthorizationUrlInfo(string.Empty, string.Empty);
        if (generation <= 0
            || string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumUrlLength
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !IsAllowed(uri))
        {
            return false;
        }

        string actionId = Guid.NewGuid().ToString("N");
        string origin = uri.GetLeftPart(UriPartial.Authority);
        lock (gate)
        {
            if (disposed || generation <= retiredGenerationCutoff || urls.Count >= MaximumEntries)
            {
                return false;
            }

            urls.Add((generation, actionId), new ProtectedAuthorizationUrl(uri, origin));
        }

        info = new ProtectedAuthorizationUrlInfo(actionId, origin);
        return true;
    }

    /// <summary>
    /// Removes and returns a URL for one explicit open action. The URI is never serialized into
    /// a Worker contract or included in a diagnostic message.
    /// </summary>
    public bool TryTake(long generation, string actionId, out Uri? uri)
    {
        uri = null;
        lock (gate)
        {
            if (disposed
                || generation <= 0
                || string.IsNullOrWhiteSpace(actionId)
                || !urls.Remove((generation, actionId), out ProtectedAuthorizationUrl? protectedUrl))
            {
                return false;
            }

            uri = protectedUrl.Uri;
            return true;
        }
    }

    public bool Remove(long generation, string actionId)
    {
        lock (gate)
        {
            return !disposed && urls.Remove((generation, actionId));
        }
    }

    public void RetireGeneration(long generation)
    {
        lock (gate)
        {
            retiredGenerationCutoff = Math.Max(retiredGenerationCutoff, generation);
            foreach ((long candidateGeneration, string actionId) in urls.Keys.ToArray())
            {
                if (candidateGeneration == generation)
                {
                    urls.Remove((candidateGeneration, actionId));
                }
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            urls.Clear();
            retiredGenerationCutoff = long.MaxValue;
        }
    }

    private static bool IsAllowed(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (uri.IsLoopback || (IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address)));
    }
}

internal sealed record ProtectedAuthorizationUrlInfo(string ActionId, string OriginDisplay);

internal sealed record ProtectedAuthorizationUrl(Uri Uri, string OriginDisplay);
