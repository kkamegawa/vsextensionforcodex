using System.Diagnostics;
using System.Text;

namespace Codex.VisualStudio.Worker;

internal sealed record BoundedProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut);

internal delegate Task<BoundedProcessResult> BoundedProcessRun(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    TimeSpan timeout,
    CancellationToken cancellationToken);

/// <summary>Runs a short diagnostic command with no shell, no input, bounded output, and a timeout.</summary>
internal static class BoundedProcessRunner
{
    private const int MaximumOutputCharacters = 256 * 1024;

    public static async Task<BoundedProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<string> output = ReadBoundedAsync(process.StandardOutput);
        Task<string> error = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new BoundedProcessResult(null, await output.ConfigureAwait(false), await error.ConfigureAwait(false), TimedOut: true);
        }

        return new BoundedProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false), TimedOut: false);
    }

    // Excess output is drained and dropped, so a noisy child cannot block on a full pipe.
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            int remaining = MaximumOutputCharacters - text.Length;
            if (remaining > 0)
            {
                text.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return text.ToString();
    }
}
