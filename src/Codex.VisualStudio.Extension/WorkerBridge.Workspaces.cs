using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

public sealed partial class WorkerBridge
{
    public Task<ListWorkspacesResult> ListWorkspacesAsync(
        ListWorkspacesRequest request,
        CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<ListWorkspacesResult>(
            "worker/account/workspaces/list",
            new object[] { request },
            cancellationToken);

    public Task<SwitchWorkspaceResult> SwitchWorkspaceAsync(
        SwitchWorkspaceRequest request,
        CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<SwitchWorkspaceResult>(
            "worker/account/workspace/switch",
            new object[] { request },
            cancellationToken);

    public Task<SwitchWorkspaceResult> CancelWorkspaceLoginAsync(
        CancelWorkspaceLoginRequest request,
        CancellationToken cancellationToken)
        => RequireRpc().InvokeWithCancellationAsync<SwitchWorkspaceResult>(
            "worker/account/workspace/cancelLogin",
            new object[] { request },
            cancellationToken);
}
