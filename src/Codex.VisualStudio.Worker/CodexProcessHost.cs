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

        // Proxy configuration. Without these, codex attempts a direct connection
        // and corporate DNS refuses to resolve chatgpt.com (WSANO_RECOVERY,
        // os error 11003). Windows environment lookups are case-insensitive, so
        // the upper-case names also match a user's lower-case http_proxy etc.
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",

        // Corporate TLS/CA bundles for proxies that perform TLS interception.
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "REQUESTS_CA_BUNDLE",
        "NODE_EXTRA_CA_CERTS",

        // Essential Windows system variables that native networking and TLS
        // (schannel) rely on once the inherited environment is cleared.
        "SystemRoot",
        "windir",
        "SystemDrive",
        "ComSpec",

        // PATHEXT lists the extensions Windows treats as executable (.EXE;.CMD;.BAT;...).
        // Without it, command resolution cannot find script-based launchers: when codex on
        // PATH is a shim (e.g. a mise shim that internally runs `mise x -- codex app-server`),
        // the inner lookup fails to resolve codex.cmd / the real binary and the launcher exits
        // with "cannot find binary path" (app-server exit code 1).
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS",
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

        ProcessStartInfo startInfo = CreateStartInfo(resolvedCodexPath, workingDirectory);
        PopulateChildEnvironment(startInfo.Environment);

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

    // Rebuilds the child environment from a curated allow-list rather than
    // inheriting the full parent environment, so secrets are not leaked into
    // codex while still forwarding the proxy, TLS, and system variables it needs.
    internal static void PopulateChildEnvironment(IDictionary<string, string?> target)
    {
        target.Clear();
        foreach (string name in EnvironmentAllowList)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                target[name] = value;
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string resolvedCodexPath, string workingDirectory)
    {
        bool isCommandScript = IsCommandScript(resolvedCodexPath);
        string effectiveWorkingDirectory = Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = effectiveWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (isCommandScript)
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"\"{resolvedCodexPath}\" app-server\"");

            // For script launchers (cmd/bat), force cmd.exe to run without creating
            // a visible console window. codex.exe direct launch keeps inherited hidden
            // console behavior to preserve child shell command execution semantics.
            startInfo.CreateNoWindow = true;
            WorkerDiagnostics.Write("codex launcher script detected; using hidden command interpreter startup");
        }
        else
        {
            startInfo.FileName = resolvedCodexPath;
            startInfo.ArgumentList.Add("app-server");

            // CREATE_NO_WINDOW leaves codex without any console, so when it spawns cmd.exe
            // to run shell commands, Windows allocates a new visible console window for each
            // one. Instead, let codex inherit this worker's (hidden) console - see
            // HiddenConsole and the worker startup in WorkerBridge - so its cmd.exe children
            // inherit that same hidden console and never pop up a window.
            startInfo.CreateNoWindow = false;
        }

        return startInfo;
    }

    internal static bool IsCommandScript(string path)
        => string.Equals(Path.GetExtension(path), ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".bat", StringComparison.OrdinalIgnoreCase);

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
