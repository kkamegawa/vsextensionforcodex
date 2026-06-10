using System.Diagnostics;
using Codex.AppServer.Protocol;

string codexPath = GetOption("--codex") ?? "codex";
string schemaDirectory = Path.GetFullPath(GetOption("--schema-out") ?? "schemas");
string workingDirectory = Path.GetFullPath(GetOption("--cwd") ?? Environment.CurrentDirectory);

Console.WriteLine(await RunCommandAsync(codexPath, ["--version"], CancellationToken.None).ConfigureAwait(false));
Directory.CreateDirectory(schemaDirectory);
await RunCommandAsync(
    codexPath,
    ["app-server", "generate-json-schema", "--out", schemaDirectory],
    CancellationToken.None).ConfigureAwait(false);
Console.WriteLine($"Generated schemas in {schemaDirectory}");

using var process = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = codexPath,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    },
};
process.StartInfo.ArgumentList.Add("app-server");
if (!process.Start())
{
    throw new InvalidOperationException("Failed to start codex app-server.");
}

using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
Task<string> stderr = process.StandardError.ReadToEndAsync(lifetime.Token);
await using var connection = new JsonLineRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
await connection.StartAsync(lifetime.Token).ConfigureAwait(false);

await connection.SendRequestAsync(
    "initialize",
    new
    {
        clientInfo = new { name = "codex_visual_studio_poc", title = "Codex Visual Studio PoC", version = "0.1.0" },
        capabilities = new { experimentalApi = false },
    },
    TimeSpan.FromSeconds(15),
    lifetime.Token).ConfigureAwait(false);
await connection.SendNotificationAsync("initialized", new { }, lifetime.Token).ConfigureAwait(false);
Console.WriteLine("initialize/initialized: OK");

var thread = await connection.SendRequestAsync(
    "thread/start",
    new { cwd = workingDirectory },
    TimeSpan.FromSeconds(30),
    lifetime.Token).ConfigureAwait(false);
string threadId = thread.GetProperty("thread").GetProperty("id").GetString()
    ?? throw new InvalidDataException("thread/start returned no thread id.");
Console.WriteLine($"thread/start: OK ({threadId})");

var turn = await connection.SendRequestAsync(
    "turn/start",
    new
    {
        threadId,
        input = new[] { new { type = "text", text = "Reply with OK only." } },
    },
    TimeSpan.FromSeconds(30),
    lifetime.Token).ConfigureAwait(false);
string turnId = turn.GetProperty("turn").GetProperty("id").GetString()
    ?? throw new InvalidDataException("turn/start returned no turn id.");
Console.WriteLine($"turn/start: OK ({turnId})");

try
{
    await connection.SendRequestAsync(
        "turn/interrupt",
        new { threadId, turnId },
        TimeSpan.FromSeconds(10),
        lifetime.Token).ConfigureAwait(false);
    Console.WriteLine("turn/interrupt: OK");
}
catch (JsonRpcRemoteException ex) when (ex.Message.Contains("no active turn", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("turn/interrupt: skipped because the turn already completed");
}

if (!process.HasExited)
{
    process.Kill(entireProcessTree: true);
}
await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

string errorText = await stderr.ConfigureAwait(false);
if (!string.IsNullOrWhiteSpace(errorText))
{
    Console.Error.WriteLine(errorText);
}

return;

string? GetOption(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static async Task<string> RunCommandAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        },
    };
    foreach (string argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    if (!process.Start())
    {
        throw new InvalidOperationException($"Failed to start {fileName}.");
    }

    string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {error}");
    }

    return output.Trim();
}
