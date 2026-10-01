using System.IO.Pipes;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;
using StreamJsonRpc;

// Hide the console window immediately. The worker is started with a console
// (instead of CREATE_NO_WINDOW) so that codex app-server and its cmd.exe
// children inherit a console and don't each pop up their own window; that
// inherited console must stay hidden. See HiddenConsole and CodexProcessHost.
if (OperatingSystem.IsWindows())
{
    HiddenConsole.Hide();
}

if (args.Length != 2 || !string.Equals(args[0], "--pipe", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: Codex.VisualStudio.Worker --pipe <name>");
    return 2;
}

string pipeName = args[1];
using var pipe = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
await pipe.WaitForConnectionAsync().ConfigureAwait(false);

var redactor = new SecretRedactor();
WorkerDiagnostics.Configure(redactor);
using var networking = new WorkerNetworking();
var pathPolicy = new PathAccessPolicy();
var approvalPolicy = new ApprovalPolicyEngine(pathPolicy);
await using var host = new CodexProcessHost(redactor, networking);
await using var session = new CodexSessionService(approvalPolicy, redactor);
await using var service = new WorkerRpcService(redactor, host, session, new RemoteConnectionDiagnostics(networking));

using var rpc = new JsonRpc(pipe);
rpc.AddLocalRpcTarget<ICodexWorkerClient>(service, null);
service.AttachClient(rpc);
rpc.StartListening();
await rpc.Completion.ConfigureAwait(false);
return 0;
