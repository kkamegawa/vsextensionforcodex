using System.Diagnostics;

namespace Codex.VisualStudio.Extension;

public enum ExternalLinkTarget
{
    UsageDashboard,
    UsageHelp,
}

public interface IExternalLinkOpener
{
    Task OpenAsync(ExternalLinkTarget target, CancellationToken cancellationToken);
}

public sealed class ExternalLinkOpener : IExternalLinkOpener
{
    private static readonly Uri UsageDashboardUri = new("https://chatgpt.com/codex/settings/usage", UriKind.Absolute);
    private static readonly Uri UsageHelpUri = new("https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan", UriKind.Absolute);
    private readonly Action<ProcessStartInfo> startProcess;

    public ExternalLinkOpener()
        : this(StartProcess)
    {
    }

    internal ExternalLinkOpener(Action<ProcessStartInfo> startProcess)
    {
        this.startProcess = startProcess;
    }

    public Task OpenAsync(ExternalLinkTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri uri = target switch
        {
            ExternalLinkTarget.UsageDashboard => UsageDashboardUri,
            ExternalLinkTarget.UsageHelp => UsageHelpUri,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        if (!IsAllowed(uri))
        {
            throw new InvalidOperationException("The external link target is not allowed.");
        }

        startProcess(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    internal static bool IsAllowed(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && ((string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(uri.AbsolutePath, "/codex/settings/usage", StringComparison.Ordinal))
                || (string.Equals(uri.Host, "help.openai.com", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(uri.AbsolutePath, "/en/articles/11369540-using-codex-with-your-chatgpt-plan", StringComparison.Ordinal)));

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo);
    }
}
