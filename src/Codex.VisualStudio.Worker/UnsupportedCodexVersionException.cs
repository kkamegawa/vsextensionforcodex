namespace Codex.VisualStudio.Worker;

/// <summary>
/// The app-server is older than the minimum supported Codex CLI or did not report a parseable
/// version. The connection is rejected before initialized and is never retried automatically.
/// </summary>
public sealed class UnsupportedCodexVersionException : InvalidOperationException
{
    public UnsupportedCodexVersionException(string message)
        : base(message)
    {
    }
}
