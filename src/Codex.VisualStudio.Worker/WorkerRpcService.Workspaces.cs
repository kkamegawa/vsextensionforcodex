using System.Diagnostics;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class WorkerRpcService
{
    public Task<ListWorkspacesResult> ListWorkspacesAsync(
        ListWorkspacesRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedGenerationAsync(
            request,
            () => session.ListWorkspacesAsync(cancellationToken),
            cancellationToken);

    public Task<SwitchWorkspaceResult> SwitchWorkspaceAsync(
        SwitchWorkspaceRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(request, async () =>
        {
            if (!request.UserConfirmed)
            {
                throw new InvalidOperationException("Switching workspaces requires explicit user confirmation.");
            }

            if (status.State != WorkerConnectionState.Ready || session.ActiveTurnId is not null || session.PendingInteractionCount != 0)
            {
                return new SwitchWorkspaceResult
                {
                    State = WorkspaceSwitchState.Failed,
                    Message = "Finish the current turn and resolve pending approvals before switching workspaces.",
                };
            }

            SwitchWorkspaceResult result = await session.SwitchWorkspaceAsync(request, cancellationToken).ConfigureAwait(false);
            return result;
        }, cancellationToken);

    public Task<SwitchWorkspaceResult> CancelWorkspaceLoginAsync(
        CancelWorkspaceLoginRequest request,
        CancellationToken cancellationToken)
        => ExecuteOwnerScopedAsync(
            request,
            () => session.CancelWorkspaceLoginAsync(request.LoginId, cancellationToken),
            cancellationToken);
}
