using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class CodexSessionService
{
    public async Task<ListWorkspacesResult> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireExperimentalWorkspaceContext();
        JsonElement result = await context.Connection.SendReadOnlyRequestAsync(
            "accountSession/list",
            new { refreshWorkspaceMetadata = true },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        return ReadWorkspaces(result);
    }

    public async Task<SwitchWorkspaceResult> SwitchWorkspaceAsync(
        SwitchWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.UserConfirmed)
        {
            throw new InvalidOperationException("Switching workspaces requires explicit user confirmation.");
        }

        ConnectionContext context = RequireExperimentalWorkspaceContext();
        if (context.PendingLoginId is not null || context.WorkspaceOperationInProgress)
        {
            return new SwitchWorkspaceResult
            {
                State = WorkspaceSwitchState.Failed,
                Message = "Complete or cancel the current account operation first.",
            };
        }

        // Set synchronously before the first await so concurrent calls cannot issue two switches.
        context.WorkspaceOperationInProgress = true;
        try
        {
            JsonElement listResponse = await context.Connection.SendReadOnlyRequestAsync(
                "accountSession/list",
                new { refreshWorkspaceMetadata = true },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            ListWorkspacesResult current = ReadWorkspaces(listResponse);
            if (!string.Equals(current.SessionId, request.SessionId, StringComparison.Ordinal)
                || !current.Workspaces.Any(workspace => string.Equals(workspace.AccountId, request.AccountId, StringComparison.Ordinal)))
            {
                return new SwitchWorkspaceResult
                {
                    State = WorkspaceSwitchState.Failed,
                    Message = "The selected workspace is no longer available. Refresh the workspace list.",
                };
            }

            if (string.Equals(current.SelectedWorkspaceAccountId, request.AccountId, StringComparison.Ordinal))
            {
                return new SwitchWorkspaceResult
                {
                    State = WorkspaceSwitchState.Failed,
                    Message = "This workspace is already selected.",
                };
            }

            context.WorkspaceSwitchRequested = true;
            var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sessionId"] = request.SessionId,
                ["accountId"] = request.AccountId,
            };
            if (context.IsLocal)
            {
                parameters["allowBrowserLogin"] = true;
            }

            JsonElement result;
            try
            {
                result = await context.Connection.SendRequestAsync(
                    "accountSession/switch",
                    parameters,
                    TimeSpan.FromSeconds(60),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUnknownOperationOutcome(ex, cancellationToken))
            {
                return await ConfirmUnknownWorkspaceSwitchAsync(context, request, cancellationToken).ConfigureAwait(false);
            }

            EnsureCurrent(context);
            if (!result.TryGetProperty("sessions", out _))
            {
                string? loginId = GetString(result, "login_id");
                string? authUrl = GetString(result, "auth_url");
                if (!context.IsLocal || string.IsNullOrWhiteSpace(loginId) || !IsSecureAbsoluteUrl(authUrl))
                {
                    context.WorkspaceSwitchRequested = false;
                    return new SwitchWorkspaceResult
                    {
                        State = WorkspaceSwitchState.Failed,
                        Message = "The app-server returned an invalid workspace sign-in response.",
                    };
                }

                try
                {
                    authorizationUrlOpener(new Uri(authUrl!, UriKind.Absolute));
                }
                catch (Exception)
                {
                    try
                    {
                        await context.Connection.SendRequestAsync(
                            "account/login/cancel",
                            new { loginId },
                            TimeSpan.FromSeconds(15),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }

                    context.WorkspaceSwitchRequested = false;
                    return new SwitchWorkspaceResult
                    {
                        State = WorkspaceSwitchState.Failed,
                        Message = "Could not open the default browser.",
                    };
                }

                context.PendingLoginId = loginId;
                context.WorkspaceSwitchRequested = false;
                return new SwitchWorkspaceResult
                {
                    State = WorkspaceSwitchState.BrowserLoginRequired,
                    LoginId = loginId,
                };
            }

            ListWorkspacesResult switched = ReadWorkspaces(result);
            if (!string.Equals(switched.SessionId, request.SessionId, StringComparison.Ordinal)
                || !string.Equals(switched.SelectedWorkspaceAccountId, request.AccountId, StringComparison.Ordinal))
            {
                context.WorkspaceSwitchRequested = false;
                return new SwitchWorkspaceResult
                {
                    State = WorkspaceSwitchState.Unknown,
                    Message = "The app-server did not confirm the requested workspace. Refresh the workspace list before trying again.",
                };
            }

            await RetireWorkspaceOwnerAsync(context, cancellationToken).ConfigureAwait(false);
            return new SwitchWorkspaceResult { State = WorkspaceSwitchState.Switched };
        }
        catch (JsonRpcRemoteException ex)
        {
            context.WorkspaceSwitchRequested = false;
            WorkerDiagnostics.Write("workspace switch was rejected", ex);
            return new SwitchWorkspaceResult
            {
                State = WorkspaceSwitchState.Failed,
                Message = "The app-server rejected the workspace switch.",
            };
        }
        finally
        {
            context.WorkspaceOperationInProgress = false;
        }
    }
    public async Task<SwitchWorkspaceResult> CancelWorkspaceLoginAsync(
        string loginId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        ConnectionContext context = RequireExperimentalWorkspaceContext();
        if (!string.Equals(context.PendingLoginId, loginId, StringComparison.Ordinal))
        {
            return new SwitchWorkspaceResult
            {
                State = WorkspaceSwitchState.Failed,
                Message = "The workspace sign-in is no longer active.",
            };
        }

        try
        {
            JsonElement response = await context.Connection.SendRequestAsync(
                "account/login/cancel",
                new { loginId },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            context.PendingLoginId = null;
            string? outcome = GetString(response, "status");
            return new SwitchWorkspaceResult
            {
                State = outcome == "canceled"
                    ? WorkspaceSwitchState.Failed
                    : WorkspaceSwitchState.Unknown,
                Message = outcome == "canceled" ? "Workspace sign-in was canceled." : "The sign-in cancellation outcome is unknown.",
            };
        }
        catch (Exception ex) when (IsUnknownOperationOutcome(ex, cancellationToken))
        {
            context.PendingLoginId = null;
            return new SwitchWorkspaceResult
            {
                State = WorkspaceSwitchState.Unknown,
                Message = "The sign-in cancellation outcome is unknown.",
            };
        }
    }

    private ConnectionContext RequireExperimentalWorkspaceContext()
    {
        ConnectionContext context = RequireContext();
        if (!context.ExperimentalApi || context.FeatureTier < CodexFeatureTier.Tier166)
        {
            throw new InvalidOperationException("Workspace switching requires Codex CLI 0.166 or later and Experimental API.");
        }

        return context;
    }

    private async Task<SwitchWorkspaceResult> ConfirmUnknownWorkspaceSwitchAsync(
        ConnectionContext context,
        SwitchWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(context))
        {
            return new SwitchWorkspaceResult
            {
                State = WorkspaceSwitchState.Unknown,
                Message = "The workspace switch outcome is unknown. Check the current workspace before trying again.",
            };
        }

        try
        {
            ListWorkspacesResult workspaces = await ListWorkspacesAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(workspaces.SessionId, request.SessionId, StringComparison.Ordinal)
                || !string.Equals(workspaces.SelectedWorkspaceAccountId, request.AccountId, StringComparison.Ordinal))
            {
                context.WorkspaceSwitchRequested = false;
                return new SwitchWorkspaceResult
                {
                    State = WorkspaceSwitchState.Unknown,
                    Message = "The app-server has not confirmed the requested workspace. Refresh the account state before trying again.",
                };
            }

            await RetireWorkspaceOwnerAsync(context, cancellationToken).ConfigureAwait(false);

            return new SwitchWorkspaceResult { State = WorkspaceSwitchState.Switched };
        }
        catch (JsonRpcConnectionClosedException)
        {
            context.WorkspaceSwitchRequested = false;
            return new SwitchWorkspaceResult
            {
                State = WorkspaceSwitchState.Unknown,
                Message = "The workspace switch outcome is unknown. Check the current workspace before trying again.",
            };
        }
        catch (Exception ex) when (IsUnknownOperationOutcome(ex, cancellationToken))
        {
            context.WorkspaceSwitchRequested = false;
            return new SwitchWorkspaceResult
            {
                State = WorkspaceSwitchState.Unknown,
                Message = "The workspace switch outcome is unknown. Check the current workspace before trying again.",
            };
        }
    }

    private static ListWorkspacesResult ReadWorkspaces(JsonElement result)
    {
        string? activeSessionId = GetString(result, "activeSessionId");
        JsonElement sessions = result.TryGetProperty("sessions", out JsonElement sessionArray)
            && sessionArray.ValueKind == JsonValueKind.Array
                ? sessionArray
                : default;
        if (sessions.ValueKind != JsonValueKind.Array)
        {
            throw new JsonRpcRemoteException(-32603, "The app-server returned an invalid workspace list.");
        }

        if (activeSessionId is null || sessions.GetArrayLength() == 0)
        {
            return new ListWorkspacesResult { IsSupported = true, Workspaces = [] };
        }

        JsonElement activeSession = default;
        foreach (JsonElement session in sessions.EnumerateArray())
        {
            if (string.Equals(GetString(session, "sessionId"), activeSessionId, StringComparison.Ordinal))
            {
                activeSession = session;
                break;
            }
        }

        if (activeSession.ValueKind != JsonValueKind.Object
            || !activeSession.TryGetProperty("workspaces", out JsonElement workspaces)
            || workspaces.ValueKind != JsonValueKind.Array)
        {
            throw new JsonRpcRemoteException(-32603, "The active app-server session returned an invalid workspace list.");
        }

        var entries = new List<AccountWorkspace>();
        foreach (JsonElement workspace in workspaces.EnumerateArray())
        {
            string? id = GetString(workspace, "accountId");
            string? name = GetString(workspace, "name");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            entries.Add(new AccountWorkspace { AccountId = id, Name = name ?? "Unnamed workspace" });
        }

        return new ListWorkspacesResult
        {
            IsSupported = true,
            SessionId = activeSessionId,
            SelectedWorkspaceAccountId = GetString(activeSession, "selectedWorkspaceAccountId"),
            Workspaces = entries,
        };
    }

    private async Task RetireWorkspaceOwnerAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        if (!IsCurrent(context))
        {
            return;
        }

        context.WorkspaceSwitchRequested = true;
        invalidatedAccountState = AccountState.Unavailable;
        invalidatedByOwnerAction = true;
        await InvalidateOwnerPartitionAsync(context, CancellationToken.None).ConfigureAwait(false);
    }
}
