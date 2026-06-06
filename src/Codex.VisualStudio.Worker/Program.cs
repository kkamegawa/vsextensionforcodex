using System.IO.Pipes;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;
using StreamJsonRpc;

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
var pathPolicy = new PathAccessPolicy();
var approvalPolicy = new ApprovalPolicyEngine(pathPolicy);
await using var host = new CodexProcessHost(redactor);
await using var session = new CodexSessionService(approvalPolicy, redactor);
await using var service = new WorkerRpcService(redactor, host, session);

using var rpc = new JsonRpc(pipe);
rpc.AddLocalRpcTarget<ICodexWorkerClient>(service, null);
service.AttachClient(rpc);
rpc.StartListening();
await rpc.Completion.ConfigureAwait(false);
return 0;
