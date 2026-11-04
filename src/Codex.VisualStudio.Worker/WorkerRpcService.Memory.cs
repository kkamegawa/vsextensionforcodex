using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class WorkerRpcService
{
    public Task<ThreadMemoryResult> GetThreadMemoryAsync(
        GetThreadMemoryRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(
            request,
            () => session.ReadThreadMemoryAsync(request.ThreadId, cancellationToken),
            cancellationToken);
}