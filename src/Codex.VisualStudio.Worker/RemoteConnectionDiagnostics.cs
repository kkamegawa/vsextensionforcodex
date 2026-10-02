using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public interface IRemoteConnectionDiagnostics
{
    Task<ConnectionDiagnosticsResult> DiagnoseAsync(ConnectionDiagnosticsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Unauthenticated health diagnosis of a remote profile's listener. It sends only GET /healthz
/// and GET /readyz to the authority root, never reads a token file, never reads a response body,
/// and never starts, stops, reconnects, initializes, or sends a mutation. A healthy result never
/// proves JSON-RPC availability, authentication, or feature support.
/// </summary>
public sealed class RemoteConnectionDiagnostics : IRemoteConnectionDiagnostics
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly WorkerNetworking networking;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan budget;

    public RemoteConnectionDiagnostics(WorkerNetworking networking, TimeProvider? timeProvider = null, TimeSpan? budget = null)
    {
        this.networking = networking;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.budget = budget ?? Budget;
    }

    public async Task<ConnectionDiagnosticsResult> DiagnoseAsync(
        ConnectionDiagnosticsRequest request,
        CancellationToken cancellationToken)
    {
        var result = new ConnectionDiagnosticsResult
        {
            ProfileName = request.ProfileName,
            ObservedAt = timeProvider.GetUtcNow(),
        };

        // One monotonic budget covers DNS verification and both probes.
        using var deadline = new CancellationTokenSource(budget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        VerifiedRemoteEndpoint endpoint;
        try
        {
            endpoint = await networking.VerifyAsync(request.Endpoint, linked.Token).ConfigureAwait(false);
        }
        catch (RemoteConnectionException ex)
        {
            result.RejectionReason = ex.Message;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.RejectionReason = "Endpoint verification did not complete in time.";
            return result;
        }

        result.RouteCoverage = endpoint.Validation.HasRoutingPath ? RouteCoverage.Unverified : RouteCoverage.NotApplicable;
        (HttpMessageInvoker invoker, bool owned) = networking.SelectInvoker(endpoint);
        try
        {
            Uri root = GetAuthorityRoot(endpoint.Endpoint);
            Task<HealthProbeResult> health = ProbeAsync(invoker, new Uri(root, "/healthz"), linked.Token, cancellationToken);
            Task<HealthProbeResult> ready = ProbeAsync(invoker, new Uri(root, "/readyz"), linked.Token, cancellationToken);
            HealthProbeResult[] results = await Task.WhenAll(health, ready).ConfigureAwait(false);
            result.Health = results[0];
            result.Ready = results[1];
        }
        finally
        {
            if (owned)
            {
                invoker.Dispose();
            }
        }

        result.ObservedAt = timeProvider.GetUtcNow();
        return result;
    }

    // ws maps to HTTP and wss to HTTPS; authority and port are preserved and any routing path is
    // dropped, so the probes describe the root listener only.
    internal static Uri GetAuthorityRoot(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint)
        {
            Scheme = string.Equals(endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (endpoint.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    private async Task<HealthProbeResult> ProbeAsync(
        HttpMessageInvoker invoker,
        Uri target,
        CancellationToken probeToken,
        CancellationToken callerToken)
    {
        long start = timeProvider.GetTimestamp();
        var result = new HealthProbeResult();
        try
        {
            // No Authorization or Origin header is ever added; the invoker has no default
            // credentials, cookies, or redirect following.
            using var message = new HttpRequestMessage(HttpMethod.Get, target);
            using HttpResponseMessage response = await invoker
                .SendAsync(message, probeToken)
                .ConfigureAwait(false);

            // The body is never read; only the status line is classified.
            int status = (int)response.StatusCode;
            result.HttpStatus = status;
            (result.State, result.Reason) = status switch
            {
                >= 200 and < 300 => (HealthProbeState.Healthy, "The route responded successfully."),
                >= 300 and < 400 => (HealthProbeState.Redirected, "The route redirected; redirects are not followed."),
                _ => (HealthProbeState.Unhealthy, "The route responded with an error status."),
            };
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            (result.State, result.Reason) = (HealthProbeState.Canceled, "The check was canceled.");
        }
        catch (OperationCanceledException)
        {
            (result.State, result.Reason) = (HealthProbeState.TimedOut, "The route did not respond within five seconds.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            (result.State, result.Reason) = (HealthProbeState.Unreachable, "The server certificate was not trusted.");
        }
        catch (HttpRequestException)
        {
            (result.State, result.Reason) = (HealthProbeState.Unreachable, "The route could not be reached (DNS, proxy, or network failure).");
        }

        result.DurationMilliseconds = (long)timeProvider.GetElapsedTime(start).TotalMilliseconds;
        return result;
    }
}

internal static class HttpStatusText
{
    public static bool IsAuthenticationFailure(int? status)
        => status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden;
}

internal static class StopwatchDeadline
{
    public static TimeSpan Remaining(long start, TimeSpan budget)
    {
        TimeSpan remaining = budget - Stopwatch.GetElapsedTime(start);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
