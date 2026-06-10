using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Extensibility.Documents;

namespace Codex.VisualStudio.Extension;

internal static class ExtensionDiagnostics
{
    private static readonly object FileLock = new();
    private static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CredentialPattern = new(
        @"(?i)(authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|bearer)\s*[:=]\s*\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string LogPath { get; } = Path.Combine(
        Path.GetTempPath(),
        "Kkamegawa.CodexForVisualStudio",
        "diagnostics.log");

    public static void Write(string stage, Exception? exception = null)
    {
        string detail = exception is null
            ? string.Empty
            : $" exception={exception.GetType().FullName} message={Sanitize(exception.Message)}";
        string line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} [EXTENSION] {Sanitize(stage)}{detail}";
        Debug.WriteLine(line);
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never alter extension behavior.
        }
    }

    public static async Task WriteOutputAsync(OutputChannel? outputChannel, string message)
    {
        Write(message);
        if (outputChannel is null)
        {
            Write("Codex Output channel is unavailable");
            return;
        }

        try
        {
            await outputChannel.WriteLineAsync(Sanitize(message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Write("Codex Output channel write failed", ex);
        }
    }

    public static string Sanitize(string value)
        => CredentialPattern.Replace(UrlPattern.Replace(value, "[URL REDACTED]"), "$1=[REDACTED]");
}
