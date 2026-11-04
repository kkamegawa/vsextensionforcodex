using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    private readonly ObservableCollection<AccountWorkspacePresentationViewModel> workspaces = [];
    private string? workspaceSessionId;
    private string? pendingWorkspaceLoginId;
    private string? selectedWorkspaceAccountId;
    private AccountWorkspacePresentationViewModel? selectedWorkspace;
    private string workspaceStatusText = string.Empty;
    private bool isWorkspacePickerOpen;

    [DataMember]
    public ObservableCollection<AccountWorkspacePresentationViewModel> Workspaces => workspaces;

    [DataMember]
    public AccountWorkspacePresentationViewModel? SelectedWorkspace
    {
        get => selectedWorkspace;
        set
        {
            if (SetProperty(ref selectedWorkspace, value))
            {
                SwitchWorkspaceCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public string CurrentWorkspaceText
        => Account.WorkspaceName is { Length: > 0 } name ? ToSafeHeaderText(name) : "Workspace";

    [DataMember]
    public bool ShowWorkspaceSwitcher
        => Account.IsSignedIn
            && Status.FeatureTier >= CodexFeatureTier.Tier166
            && Status.Target?.ExperimentalApi == true;

    [DataMember]
    public bool IsWorkspacePickerOpen
    {
        get => isWorkspacePickerOpen;
        set
        {
            if (SetProperty(ref isWorkspacePickerOpen, value) && value)
            {
                CloseOtherFlyouts(nameof(IsWorkspacePickerOpen));
            }
        }
    }

    [DataMember]
    public string WorkspaceStatusText
    {
        get => workspaceStatusText;
        private set => SetProperty(ref workspaceStatusText, value);
    }

    [DataMember]
    public bool IsWorkspaceLoginPending => pendingWorkspaceLoginId is not null;

    [DataMember]
    public AsyncCommand OpenWorkspacePickerCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand RefreshWorkspacesCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand SwitchWorkspaceCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand CancelWorkspaceLoginCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand CloseWorkspacePickerCommand { get; private set; } = null!;

    private void InitializeWorkspaceCommands()
    {
        OpenWorkspacePickerCommand = new AsyncCommand(OpenWorkspacePickerAsync, CanOpenWorkspacePicker);
        RefreshWorkspacesCommand = new AsyncCommand(RefreshWorkspacesAsync, CanOpenWorkspacePicker);
        SwitchWorkspaceCommand = new AsyncCommand(SwitchWorkspaceAsync, CanSwitchWorkspace);
        CancelWorkspaceLoginCommand = new AsyncCommand(CancelWorkspaceLoginAsync, () => IsWorkspaceLoginPending);
        CloseWorkspacePickerCommand = new AsyncCommand(() =>
        {
            IsWorkspacePickerOpen = false;
            return Task.CompletedTask;
        });
    }

    private bool CanOpenWorkspacePicker()
        => ShowWorkspaceSwitcher
            && Status.State == WorkerConnectionState.Ready
            && Account.IsSignedIn;

    private bool CanSwitchWorkspace()
        => CanOpenWorkspacePicker()
            && SelectedWorkspace is not null
            && !string.Equals(SelectedWorkspace.AccountId, selectedWorkspaceAccountId, StringComparison.Ordinal)
            && !IsWorkspaceLoginPending;
    private async Task OpenWorkspacePickerAsync()
    {
        if (!CanOpenWorkspacePicker())
        {
            return;
        }

        await OnUiAsync(() =>
        {
            IsWorkspacePickerOpen = true;
            WorkspaceStatusText = "Loading workspaces…";
        }).ConfigureAwait(false);
        await RefreshWorkspacesAsync().ConfigureAwait(false);
    }

    private async Task RefreshWorkspacesAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        try
        {
            ListWorkspacesResult result = await bridge.ListWorkspacesAsync(
                StampOwner(new ListWorkspacesRequest(), owner),
                lifetime.Token).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner))
                {
                    return;
                }

                workspaces.Clear();
                workspaceSessionId = result.SessionId;
                selectedWorkspaceAccountId = result.SelectedWorkspaceAccountId;
                SelectedWorkspace = null;
                foreach (AccountWorkspace item in result.Workspaces)
                {
                    var presentation = new AccountWorkspacePresentationViewModel(
                        item.AccountId,
                        ToSafeHeaderText(item.Name));
                    workspaces.Add(presentation);
                    if (string.Equals(item.AccountId, result.SelectedWorkspaceAccountId, StringComparison.Ordinal))
                    {
                        SelectedWorkspace = presentation;
                    }
                }

                WorkspaceStatusText = result.IsSupported
                    ? result.Workspaces.Count == 0 ? "No workspaces are available." : string.Empty
                    : ToSafeHeaderText(result.UnavailableReason ?? "Workspace switching is unavailable.");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    WorkspaceStatusText = "Could not load workspaces (" + ex.GetType().Name + ").";
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task SwitchWorkspaceAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        AccountWorkspacePresentationViewModel? selected = SelectedWorkspace;
        string? sessionId = workspaceSessionId;
        if (!CanSwitchWorkspace() || selected is null || string.IsNullOrWhiteSpace(sessionId) || extensibility is null)
        {
            return;
        }

        string impact = IsRemoteTarget
            ? "This stops loaded conversations and may affect other clients connected to this server."
            : "This stops all loaded conversations.";
        WorkspaceSwitchConfirmationChoice choice = await extensibility.Shell().ShowPromptAsync(
            $"Switch to {selected.Name}?\r\n\r\n{impact}",
            new PromptOptions<WorkspaceSwitchConfirmationChoice>
            {
                Choices =
                {
                    { "Switch workspace", WorkspaceSwitchConfirmationChoice.Continue },
                    { "Cancel", WorkspaceSwitchConfirmationChoice.Cancel },
                },
                DefaultChoiceIndex = 1,
                DismissedReturns = WorkspaceSwitchConfirmationChoice.Cancel,
                Title = "Switch Codex Workspace",
            },
            lifetime.Token).ConfigureAwait(false);
        if (choice != WorkspaceSwitchConfirmationChoice.Continue
            || !IsCurrentOwner(owner)
            || !CanSwitchWorkspace()
            || !string.Equals(SelectedWorkspace?.AccountId, selected.AccountId, StringComparison.Ordinal)
            || !string.Equals(workspaceSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            SwitchWorkspaceResult result = await bridge.SwitchWorkspaceAsync(
                StampOwner(new SwitchWorkspaceRequest
                {
                    SessionId = sessionId,
                    AccountId = selected.AccountId,
                    UserConfirmed = true,
                }, owner),
                lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner))
                {
                    return;
                }

                pendingWorkspaceLoginId = result.State == WorkspaceSwitchState.BrowserLoginRequired
                    ? result.LoginId
                    : null;
                OnPropertyChanged(nameof(IsWorkspaceLoginPending));
                WorkspaceStatusText = result.State switch
                {
                    WorkspaceSwitchState.BrowserLoginRequired => "Complete the workspace sign-in in your browser.",
                    WorkspaceSwitchState.Unknown => ToSafeHeaderText(result.Message ?? "The workspace switch outcome is unknown. Refresh to check the current workspace."),
                    WorkspaceSwitchState.Failed => ToSafeHeaderText(result.Message ?? "The workspace could not be changed."),
                    _ => "Workspace changed. Reconnecting to the selected workspace…",
                };
                if (result.State == WorkspaceSwitchState.Switched)
                {
                    IsWorkspacePickerOpen = false;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    WorkspaceStatusText = "Workspace switch failed (" + ex.GetType().Name + ").";
                }
            }).ConfigureAwait(false);
        }
    }
    private async Task CancelWorkspaceLoginAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        string? loginId = pendingWorkspaceLoginId;
        if (!IsCurrentOwner(owner) || string.IsNullOrWhiteSpace(loginId))
        {
            return;
        }

        try
        {
            SwitchWorkspaceResult result = await bridge.CancelWorkspaceLoginAsync(
                StampOwner(new CancelWorkspaceLoginRequest { LoginId = loginId }, owner),
                lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner))
                {
                    return;
                }

                pendingWorkspaceLoginId = null;
                OnPropertyChanged(nameof(IsWorkspaceLoginPending));
                WorkspaceStatusText = ToSafeHeaderText(result.Message ?? "Workspace sign-in canceled.");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    WorkspaceStatusText = "Workspace sign-in cancellation is unknown (" + ex.GetType().Name + ").";
                    pendingWorkspaceLoginId = null;
                    OnPropertyChanged(nameof(IsWorkspaceLoginPending));
                }
            }).ConfigureAwait(false);
        }
    }
}

[DataContract]
public sealed class AccountWorkspacePresentationViewModel(string accountId, string name)
{
    [DataMember]
    public string AccountId { get; } = accountId;

    [DataMember]
    public string Name { get; } = name;
}
