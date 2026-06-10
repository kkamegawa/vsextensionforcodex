using System.Diagnostics;
using Codex.AppServer.Protocol;

namespace Codex.VisualStudio.Worker;

public interface ICodexProcessHost : IAsyncDisposable
{
    event EventHandler<string>? StandardErrorReceived;

    event EventHandler<int>? Exited;

    int? ProcessId { get; }

    IJsonRpcConnection? Connection { get; }

    Task StartAsync(string codexPath, string workingDirectory, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class CodexProcessHost : ICodexProcessHost
{
    private static readonly string[] EnvironmentAllowList =
    [
        "PATH",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "TEMP",
        "TMP",
        "HOME",
        "CODEX_HOME",
        "CODEX_PATH",
        "OPENAI_API_KEY",
    ];

    private readonly ISecretRedactor redactor;
    private Process? process;
    private int? processId;

    public CodexProcessHost(ISecretRedactor redactor)
    {
        this.redactor = redactor;
    }

    public event EventHandler<string>? StandardErrorReceived;

    public event EventHandler<int>? Exited;

    public int? ProcessId => processId;

    public IJsonRpcConnection? Connection { get; private set; }

    public async Task StartAsync(string codexPath, string workingDirectory, CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        string resolvedCodexPath = CodexExecutableResolver.Resolve(codexPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedCodexPath,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.Environment.Clear();
        foreach (string name in EnvironmentAllowList)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }

        var startedProcess = new Process { StartInfo = startInfo };
        WorkerDiagnostics.Write("codex app-server process start requested");
        try
        {
            if (!startedProcess.Start())
            {
                throw new InvalidOperationException("Failed to start codex app-server.");
            }
        }
        catch (Exception ex)
        {
            startedProcess.Dispose();
            WorkerDiagnostics.Write("codex app-server process start failed", ex);
            throw;
        }

        process = startedProcess;
        processId = startedProcess.Id;
        startedProcess.EnableRaisingEvents = true;
        startedProcess.Exited += OnExited;
        WorkerDiagnostics.Write($"codex app-server process started pid={processId}");
        _ = Task.Run(() => ReadStandardErrorAsync(startedProcess, cancellationToken), CancellationToken.None);
        Connection = new JsonLineRpcConnection(startedProcess.StandardOutput.BaseStream, startedProcess.StandardInput.BaseStream);
        await Connection.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            Connection = null;
        }

        Process? current = process;
        process = null;
        processId = null;
        if (current is null)
        {
            return;
        }

        current.Exited -= OnExited;
        if (!current.HasExited)
        {
            current.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await current.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                current.Kill(entireProcessTree: true);
            }
        }

        current.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReadStandardErrorAsync(Process source, CancellationToken cancellationToken)
    {
        try
        {
            while (!source.HasExited && !cancellationToken.IsCancellationRequested)
            {
                string? line = await source.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                StandardErrorReceived?.Invoke(this, redactor.Redact(line));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (sender is Process source)
        {
            int exitCode;
            try
            {
                exitCode = source.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = -1;
            }

            processId = null;
            Exited?.Invoke(this, exitCode);
        }
    }
}

