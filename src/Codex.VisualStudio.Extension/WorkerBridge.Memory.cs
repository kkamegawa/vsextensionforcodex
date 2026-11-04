using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

public sealed partial class WorkerBridge
{
    public Task<ThreadMemoryResult> GetThreadMemoryAsync(
        GetThreadMemoryRequest request,
        CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ThreadMemoryResult>(
            "worker/thread/memory/read",
            new object[] { request },
            cancellationToken);
}