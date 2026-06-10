using Codex.AppServer.Protocol;
using System.Text;

namespace Codex.VisualStudio.Worker;

internal static class WorkerDiagnostics
{
    private static readonly object FileLock = new();
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(),
        "Kkamegawa.CodexForVisualStudio",
        "diagnostics.log");

    public static void Write(string stage, Exception? exception = null)
    {
        string detail = exception switch
        {
            JsonRpcRemoteException remote => $" exception={exception.GetType().Name} rpcCode={remote.Code}",
            not null => $" exception={exception.GetType().Name}",
            _ => string.Empty,
        };
        string message = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} [WORKER] [CODEX AUTH] {stage}{detail}";
        Console.Error.WriteLine(message);
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never alter Worker behavior.
        }
    }
}
