using System.Globalization;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// Health diagnosis result for the profile selected in the connection-target flyout. It is kept
/// separate from the active RPC target: a health result never enables or disables Connect or any
/// feature, and an inactive profile always shows its RPC row as "Not connected".
/// </summary>
[DataContract]
public sealed class ConnectionHealthPresentationViewModel : ObservableObject
{
    private readonly SafeMarkdownService markdown;
    private long sequence;
    private bool isChecking;
    private bool hasResult;
    private string checkedProfileName = string.Empty;
    private string checkedFingerprint = string.Empty;
    private string checkedProfileText = string.Empty;
    private string healthText = string.Empty;
    private string readyText = string.Empty;
    private string rpcText = string.Empty;
    private string scopeText = string.Empty;
    private string statusText = string.Empty;

    internal ConnectionHealthPresentationViewModel(SafeMarkdownService markdown)
    {
        this.markdown = markdown;
    }

    [DataMember]
    public bool IsChecking
    {
        get => isChecking;
        private set => SetProperty(ref isChecking, value);
    }

    [DataMember]
    public bool HasResult
    {
        get => hasResult;
        private set => SetProperty(ref hasResult, value);
    }

    [DataMember]
    public string CheckedProfileText
    {
        get => checkedProfileText;
        private set => SetProperty(ref checkedProfileText, value);
    }

    [DataMember]
    public string HealthText
    {
        get => healthText;
        private set => SetProperty(ref healthText, value);
    }

    [DataMember]
    public string ReadyText
    {
        get => readyText;
        private set => SetProperty(ref readyText, value);
    }

    [DataMember]
    public string RpcText
    {
        get => rpcText;
        private set => SetProperty(ref rpcText, value);
    }

    [DataMember]
    public string ScopeText
    {
        get => scopeText;
        private set => SetProperty(ref scopeText, value);
    }

    // Bound to a polite live region so screen readers hear check progress and completion.
    [DataMember]
    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    internal long Begin(string profileName, string fingerprint)
    {
        long token = Interlocked.Increment(ref sequence);
        ResetResult();
        checkedProfileName = profileName;
        checkedFingerprint = fingerprint;
        IsChecking = true;
        StatusText = $"Checking health for {SafeName(profileName)}...";
        return token;
    }

    // Applies a completion only when no newer check, clear, connection change, or disposal
    // happened since Begin returned the token.
    internal bool Complete(long token, ConnectionDiagnosticsResult result, WorkerStatus activeStatus, string rpcStateText)
    {
        if (token != Interlocked.Read(ref sequence))
        {
            return false;
        }

        IsChecking = false;
        CheckedProfileText = $"Checked profile: {SafeName(checkedProfileName)}{DescribeObservedAt(result.ObservedAt)}";
        if (!string.IsNullOrEmpty(result.RejectionReason))
        {
            HasResult = false;
            StatusText = $"Health check not run: {Safe(result.RejectionReason!)}";
            return true;
        }

        HealthText = $"Health (/healthz): {Describe(result.Health)}";
        ReadyText = $"Ready (/readyz): {Describe(result.Ready)}";
        ScopeText = result.RouteCoverage == RouteCoverage.Unverified
            ? "These probes check the root listener only. The routed path in the endpoint is not verified. A healthy result does not prove that JSON-RPC, authentication, or features work."
            : "These probes check the root listener of the endpoint. A healthy result does not prove that JSON-RPC, authentication, or features work.";
        HasResult = true;
        UpdateRpc(activeStatus, rpcStateText);
        StatusText = $"Health check completed for {SafeName(checkedProfileName)}.";
        return true;
    }

    internal bool Fail(long token, string message)
    {
        if (token != Interlocked.Read(ref sequence))
        {
            return false;
        }

        IsChecking = false;
        HasResult = false;
        StatusText = Safe(message);
        return true;
    }

    // Refreshes the RPC row from the active target. Health never feeds into it: the row reflects
    // the actual connection, and an inactive checked profile is "Not connected".
    internal void UpdateRpc(WorkerStatus activeStatus, string rpcStateText)
    {
        if (!HasResult && !IsChecking)
        {
            return;
        }

        ConnectionTargetSnapshot? target = activeStatus.Target;
        bool isActive = target is { Kind: ConnectionTargetKind.Remote }
            && string.Equals(target.DisplayName, checkedProfileName, StringComparison.Ordinal)
            && string.Equals(target.Fingerprint, checkedFingerprint, StringComparison.Ordinal);
        RpcText = isActive
            ? $"RPC connection: {Safe(rpcStateText)}"
            : "RPC connection: Not connected";
    }

    // Clears any shown result and invalidates an outstanding check.
    internal void Clear()
    {
        Interlocked.Increment(ref sequence);
        IsChecking = false;
        ResetResult();
        StatusText = string.Empty;
    }

    private void ResetResult()
    {
        HasResult = false;
        CheckedProfileText = string.Empty;
        HealthText = string.Empty;
        ReadyText = string.Empty;
        RpcText = string.Empty;
        ScopeText = string.Empty;
        checkedProfileName = string.Empty;
        checkedFingerprint = string.Empty;
    }

    private string Describe(HealthProbeResult probe)
    {
        string state = probe.State switch
        {
            HealthProbeState.Healthy => "Healthy",
            HealthProbeState.Unhealthy => "Unhealthy",
            HealthProbeState.Redirected => "Redirected (not followed)",
            HealthProbeState.Unreachable => "Unreachable",
            HealthProbeState.TimedOut => "Timed out",
            HealthProbeState.Canceled => "Canceled",
            _ => "Not checked",
        };
        string status = probe.HttpStatus is int code
            ? $"HTTP {code.ToString(CultureInfo.InvariantCulture)}, "
            : string.Empty;
        string detail = $" ({status}{probe.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)} ms)";
        string reason = string.IsNullOrWhiteSpace(probe.Reason) ? string.Empty : $" {Safe(probe.Reason)}";
        return state + detail + reason;
    }

    // The Worker stamps every result; local time tells the user how old the shown result is.
    internal static string DescribeObservedAt(DateTimeOffset observedAt)
        => observedAt == default
            ? string.Empty
            : $" at {observedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)}";

    private string SafeName(string name) => $"'{Safe(name)}'";

    private string Safe(string value) => markdown.ToSafeText(value).Trim();
}
