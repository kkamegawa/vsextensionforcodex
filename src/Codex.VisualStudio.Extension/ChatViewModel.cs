using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using VSUI = Microsoft.VisualStudio.Extensibility.UI;

namespace Codex.VisualStudio.Extension;

// Remote UI serializes the data context to a proxy in the VS process. Only DataMember
// properties of DataContract types reach that proxy — a class without the attributes is
// serialized as an EMPTY object and every binding to it silently fails (buttons with bound
// Content render as empty pills, bound text stays blank).
[DataContract]
public sealed class ChatViewModel : ObservableObject, IDisposable
{
    private readonly WorkerBridge bridge;
    private readonly OutputChannel? outputChannel;
    private readonly SafeMarkdownService markdown = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly WorkspaceDirectoryResolver workspaceDirectoryResolver;
    private readonly ProjectScaffolder projectScaffolder;
    private int disposed;
    private int connecting;
    private WorkerStatus status = new() { State = WorkerConnectionState.Disconnected, Message = "Open Codex to connect." };
    private ThreadSummary? selectedThread;
    private string composerText = string.Empty;
    private string? nextCursor;
    private bool initialized;
    private bool isHistoryOpen;
    private string? selectedModel;
    private string selectedMode = "Agent";

    public ChatViewModel(OutputChannel? outputChannel = null, VisualStudioExtensibility? extensibility = null)
    {
        this.outputChannel = outputChannel;
        workspaceDirectoryResolver = new WorkspaceDirectoryResolver(extensibility);
        projectScaffolder = new ProjectScaffolder(extensibility);
        bridge = new WorkerBridge(outputChannel);
        bridge.StateChanged += OnStateChangedAsync;
        bridge.AccountChanged += OnAccountChangedAsync;
        bridge.ConversationEventReceived += OnConversationEventAsync;
        bridge.ApprovalRequested += OnApprovalRequestedAsync;
        bridge.ApprovalResolved += OnApprovalResolvedAsync;
        // The welcome/empty state is driven by IsThreadEmpty; keep it in sync with every
        // mutation of Items (Add/Clear from any call site) via a single subscription.
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsThreadEmpty));
        ConnectCommand = new AsyncCommand(ConnectAsync, () => Status.State is WorkerConnectionState.Disconnected or WorkerConnectionState.Degraded);
        RestartCommand = new AsyncCommand(RestartAsync, () => Status.State == WorkerConnectionState.Degraded);
        NewThreadCommand = new AsyncCommand(NewThreadAsync, () => Status.State == WorkerConnectionState.Ready);
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => initialized && nextCursor is not null);
        SendCommand = new AsyncCommand(SendAsync, CanSend);
        InterruptCommand = new AsyncCommand(InterruptAsync, () => Status.TurnId is not null);
        AccountCommand = new AsyncCommand(ExecuteAccountActionAsync, CanExecuteAccountAction);
        ToggleHistoryCommand = new AsyncCommand(() =>
        {
            IsHistoryOpen = !IsHistoryOpen;
            return Task.CompletedTask;
        });
        AttachCommand = new AsyncCommand(AttachStubAsync);

        // Suggestion chips for the empty state. Selecting one populates the composer; the
        // user then presses Send. Keeps behavior simple and avoids needing editor context.
        Suggestions =
        [
            new SuggestionChip("Review this code for bugs and edge cases", UseSuggestionAsync),
            new SuggestionChip("Fix all errors in the active document", UseSuggestionAsync),
            new SuggestionChip("Write unit tests for this file", UseSuggestionAsync),
        ];

        // TODO(issue): replace these placeholders by querying codex app-server for the
        // available models. There is no backend RPC for this yet, so the picker is a stub.
        Models = ["gpt-5-codex", "gpt-5"];
        selectedModel = Models[0];

        _ = TryAutoConnectAsync();
    }

    [DataMember]
    public ObservableCollection<ThreadSummary> Threads { get; } = new();

    [DataMember]
    public ObservableCollection<ChatItemViewModel> Items { get; } = new();

    // Intentionally NOT a DataMember: the XAML binds the flattened Account* properties below.
    public AccountPanelViewModel Account { get; } = new();

    // Shown beneath the connection status. Before the first connection attempt the account
    // has never been checked, so showing "Checking account..." would look like a stuck
    // loading state; show the connection status's own guidance message instead.
    [DataMember]
    public string StatusDetailText => Status.State == WorkerConnectionState.Disconnected && Account.State == AccountState.Checking
        ? Status.Message
        : Account.DisplayText;

    [DataMember]
    public bool ShowAccountAction => Account.ShowAction;

    [DataMember]
    public string AccountActionText => Account.ActionText;

    [DataMember]
    public WorkerStatus Status
    {
        get => status;
        private set
        {
            if (SetProperty(ref status, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(IsDegraded));
                OnPropertyChanged(nameof(IsTurnActive));
                OnPropertyChanged(nameof(SendButtonText));
                OnPropertyChanged(nameof(StatusDetailText));
            }
        }
    }

    [DataMember]
    public ThreadSummary? SelectedThread
    {
        get => selectedThread;
        set
        {
            if (SetProperty(ref selectedThread, value) && value is not null)
            {
                _ = ResumeThreadAsync(value);
            }
        }
    }

    [DataMember]
    public string ComposerText
    {
        get => composerText;
        set
        {
            if (composerText == value)
            {
                return;
            }

            composerText = value;

            // Deliberately do NOT raise PropertyChanged for ComposerText on this binding-driven
            // (user-typing) path. The data context is replicated to a proxy in a separate process,
            // so the notification is echoed back to the TextBox asynchronously — outside WPF's
            // synchronous "transfer" window that normally suppresses a binding's own echo — which
            // re-assigns TextBox.Text and snaps the caret to position 0 on every keystroke. The
            // TextBox already holds this value, so the echo is redundant. Programmatic changes go
            // through SetComposerText, which DOES notify so the TextBox reflects the new value.
            OnPropertyChanged(nameof(IsComposerEmpty));
            RaiseCommandStates();
        }
    }

    // Replaces the composer text from code (clear-after-send, suggestion chips). Unlike the
    // binding-driven setter this raises PropertyChanged for ComposerText so the TextBox updates;
    // the caret-reset concern does not apply because the user is not mid-typing here.
    private void SetComposerText(string value)
    {
        if (composerText == value)
        {
            return;
        }

        composerText = value;
        OnPropertyChanged(nameof(ComposerText));
        OnPropertyChanged(nameof(IsComposerEmpty));
        RaiseCommandStates();
    }

    // True when the active thread has no transcript items — drives the centered welcome state.
    [DataMember]
    public bool IsThreadEmpty => Items.Count == 0;

    // True when the composer is empty — drives the "Ask Codex" placeholder overlay
    // (WPF TextBox has no native placeholder, and custom converters cannot be used because
    // they would not resolve inside VS's WPF process).
    [DataMember]
    public bool IsComposerEmpty => string.IsNullOrEmpty(composerText);

    // Two-way bound to the thread-history Popup.IsOpen.
    [DataMember]
    public bool IsHistoryOpen
    {
        get => isHistoryOpen;
        set => SetProperty(ref isHistoryOpen, value);
    }

    [DataMember]
    public ObservableCollection<SuggestionChip> Suggestions { get; }

    // TODO(issue): populate from codex app-server available models (no backend RPC yet).
    [DataMember]
    public ObservableCollection<string> Models { get; }

    [DataMember]
    public string? SelectedModel
    {
        get => selectedModel;
        set => SetProperty(ref selectedModel, value);
    }

    // TODO(issue): the agent/chat mode has no backend effect yet — this is a UI stub.
    [DataMember]
    public ObservableCollection<string> Modes { get; } = ["Agent", "Chat"];

    [DataMember]
    public string SelectedMode
    {
        get => selectedMode;
        set => SetProperty(ref selectedMode, value);
    }

    public bool IsDegraded => Status.State == WorkerConnectionState.Degraded;

    // Bound by the composer to show the Interrupt button only while a turn is active.
    [DataMember]
    public bool IsTurnActive => Status.TurnId is not null;

    [DataMember]
    public string SendButtonText => IsTurnActive ? "Steer" : "Send";

    public AsyncCommand ConnectCommand { get; }

    [DataMember]
    public AsyncCommand RestartCommand { get; }

    [DataMember]
    public AsyncCommand NewThreadCommand { get; }

    [DataMember]
    public AsyncCommand LoadMoreCommand { get; }

    [DataMember]
    public AsyncCommand SendCommand { get; }

    [DataMember]
    public AsyncCommand InterruptCommand { get; }

    [DataMember]
    public AsyncCommand AccountCommand { get; }

    [DataMember]
    public AsyncCommand ToggleHistoryCommand { get; }

    [DataMember]
    public AsyncCommand AttachCommand { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lifetime.Cancel();
        lifetime.Dispose();
        // Best-effort async disposal on shutdown; fire-and-forget is acceptable here.
        _ = Task.Run(async () => await bridge.DisposeAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// Started once on construction. Watches for a solution/folder to be open and connects
    /// automatically using its directory — no prompt, no persisted directory. The tool window
    /// is restored from the previous layout on VS startup, usually BEFORE a solution finishes
    /// loading, so a single resolve attempt almost always finds nothing; we poll until a
    /// workspace appears. Polling stops as soon as we connect, the user connects another way
    /// (Send/Connect moves us out of <see cref="WorkerConnectionState.Disconnected"/>), or the
    /// view model is disposed. When no solution/folder is ever opened the window simply stays
    /// Disconnected and the working-directory prompt is deferred to the user's first send (see
    /// <see cref="SendAsync"/>, which calls the interactive <see cref="ConnectAsync"/>).
    /// </summary>
    private async Task TryAutoConnectAsync()
    {
        bool announced = false;
        while (!lifetime.IsCancellationRequested)
        {
            // Once any connect path has moved us past Disconnected, stop watching.
            if (Status.State != WorkerConnectionState.Disconnected)
            {
                return;
            }

            string? workingDirectory;
            try
            {
                workingDirectory = await workspaceDirectoryResolver.TryResolveFromWorkspaceAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }

            if (workingDirectory is not null)
            {
                ExtensionDiagnostics.Write($"Auto-connect workspace resolution: \"{workingDirectory}\"");
                await ConnectWithDirectoryAsync(workingDirectory).ConfigureAwait(false);
                return;
            }

            if (!announced)
            {
                announced = true;
                ExtensionDiagnostics.Write("Auto-connect workspace resolution: no solution/folder open (watching for one)");
                await OnUiAsync(() => Status = new WorkerStatus
                {
                    State = WorkerConnectionState.Disconnected,
                    Message = "Open a solution or folder, or type a message to choose a working directory.",
                }).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Interactive connect: resolves the working directory, prompting the user for one if no
    /// solution/folder is open. Used by <see cref="ConnectCommand"/> and lazily by
    /// <see cref="SendAsync"/> before the first send. Returns <see langword="true"/> if the
    /// connection reached <see cref="WorkerConnectionState.Ready"/>.
    /// </summary>
    private async Task<bool> ConnectAsync()
    {
        string? workingDirectory;
        try
        {
            workingDirectory = await workspaceDirectoryResolver.ResolveAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return false;
        }

        if (workingDirectory is null)
        {
            await OnUiAsync(() => Status = new WorkerStatus
            {
                State = WorkerConnectionState.Disconnected,
                Message = "No working directory was chosen. Click Connect to choose one.",
            }).ConfigureAwait(false);
            return false;
        }

        return await ConnectWithDirectoryAsync(workingDirectory).ConfigureAwait(false);
    }

    /// <summary>
    /// Scaffolds (if needed) and connects the worker to <paramref name="workingDirectory"/>,
    /// then loads account status and threads once ready. Returns <see langword="true"/> if the
    /// connection reached <see cref="WorkerConnectionState.Ready"/>.
    /// </summary>
    private async Task<bool> ConnectWithDirectoryAsync(string workingDirectory)
    {
        // The auto-connect watcher and a user-initiated Send/Connect can both reach here; the
        // guard ensures only one connect attempt runs at a time so we never spawn two workers.
        if (Interlocked.CompareExchange(ref connecting, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            WorkerStatus result;
            try
            {
                await projectScaffolder.EnsureScaffoldAsync(workingDirectory, lifetime.Token).ConfigureAwait(false);
                result = await bridge.ConnectAsync(workingDirectory, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                ExtensionDiagnostics.Write("Initial Worker connection failed", ex);
                result = new WorkerStatus
                {
                    State = WorkerConnectionState.Degraded,
                    Message = "Could not connect to the Codex Worker. See diagnostics.log.",
                };
            }

            await OnUiAsync(() => Status = result).ConfigureAwait(false);
            if (result.State == WorkerConnectionState.Ready)
            {
                initialized = true;
                AccountStatus accountStatus = await bridge.GetAccountStatusAsync(lifetime.Token).ConfigureAwait(false);
                ExtensionDiagnostics.Write($"Initial account status received state={accountStatus.State} plan={accountStatus.PlanType ?? "none"}");
                await OnUiAsync(() =>
                {
                    UpdateAccount(accountStatus);
                }).ConfigureAwait(false);
                await LoadMoreAsync().ConfigureAwait(false);
            }

            return result.State == WorkerConnectionState.Ready;
        }
        finally
        {
            Interlocked.Exchange(ref connecting, 0);
        }
    }

    private async Task RestartAsync()
    {
        WorkerStatus result = await bridge.RestartAsync(lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(() => Status = result).ConfigureAwait(false);
        if (result.State == WorkerConnectionState.Ready)
        {
            AccountStatus accountStatus = await bridge.GetAccountStatusAsync(lifetime.Token).ConfigureAwait(false);
            ExtensionDiagnostics.Write($"Restart account status received state={accountStatus.State} plan={accountStatus.PlanType ?? "none"}");
            await OnUiAsync(() =>
            {
                UpdateAccount(accountStatus);
            }).ConfigureAwait(false);
            await ReloadThreadsAsync().ConfigureAwait(false);
        }
    }

    private async Task NewThreadAsync()
    {
        ThreadSummary thread = await bridge.StartThreadAsync(lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(() =>
        {
            Threads.Insert(0, thread);
            selectedThread = thread;
            OnPropertyChanged(nameof(SelectedThread));
            Items.Clear();
        }).ConfigureAwait(false);
    }

    private async Task ResumeThreadAsync(ThreadSummary thread)
    {
        if (Status.TurnId is not null && !string.Equals(Status.ThreadId, thread.Id, StringComparison.Ordinal))
        {
            return;
        }

        await bridge.ResumeThreadAsync(thread.Id, lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(Items.Clear).ConfigureAwait(false);
    }

    private async Task LoadMoreAsync()
    {
        ThreadPage page = await bridge.ListThreadsAsync(nextCursor, lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(() =>
        {
            foreach (ThreadSummary thread in page.Threads)
            {
                Threads.Add(thread);
            }

            nextCursor = page.NextCursor;
            RaiseCommandStates();
        }).ConfigureAwait(false);
    }

    private async Task ReloadThreadsAsync()
    {
        await OnUiAsync(Threads.Clear).ConfigureAwait(false);
        nextCursor = null;
        await LoadMoreAsync().ConfigureAwait(false);
    }

    private async Task SendAsync()
    {
        if (Status.State is not (WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval))
        {
            // Not connected yet: this is the user's first send with no solution/folder open.
            // Resolve (prompting for a working directory if needed) and connect before
            // sending. Leave the composer text intact if the user cancels the prompt or the
            // connection fails, so nothing is lost.
            bool connected = await ConnectAsync().ConfigureAwait(false);
            if (!connected)
            {
                return;
            }
        }

        string text = ComposerText;
        await OnUiAsync(() => SetComposerText(string.Empty)).ConfigureAwait(false);
        if (SelectedThread is null)
        {
            await NewThreadAsync().ConfigureAwait(false);
        }

        if (SelectedThread is null)
        {
            return;
        }

        if (Status.TurnId is null)
        {
            await OnUiAsync(() => Items.Add(new ChatItemViewModel("You", markdown.ToSafeText(text), ConversationEventKind.ItemStarted))).ConfigureAwait(false);
            await bridge.StartTurnAsync(new StartTurnRequest { ThreadId = SelectedThread.Id, Text = text }, lifetime.Token).ConfigureAwait(false);
        }
        else
        {
            await bridge.SteerTurnAsync(
                new SteerTurnRequest { ThreadId = SelectedThread.Id, ExpectedTurnId = Status.TurnId, Text = text },
                lifetime.Token).ConfigureAwait(false);
        }
    }

    private Task UseSuggestionAsync(string text)
    {
        SetComposerText(text);
        return Task.CompletedTask;
    }

    // TODO(issue): wire to a real file/context attach picker. Stubbed for now so the
    // composer + button is present without a backend dependency.
    private async Task AttachStubAsync()
    {
        await ExtensionDiagnostics.WriteOutputAsync(
            outputChannel,
            "[CODEX] Attach is not implemented yet.").ConfigureAwait(false);
    }

    private Task InterruptAsync()
    {
        if (Status.ThreadId is null || Status.TurnId is null)
        {
            return Task.CompletedTask;
        }

        return bridge.InterruptTurnAsync(
            new InterruptTurnRequest { ThreadId = Status.ThreadId, TurnId = Status.TurnId },
            lifetime.Token);
    }

    private Task OnStateChangedAsync(WorkerStatus value)
        => OnUiAsync(() => Status = value);

    private Task OnAccountChangedAsync(AccountStatus value)
    {
        ExtensionDiagnostics.Write($"Account status notification received state={value.State} plan={value.PlanType ?? "none"}");
        return OnUiAsync(() =>
        {
            UpdateAccount(value);
        });
    }

    private Task OnConversationEventAsync(ConversationEvent value)
        => OnUiAsync(() =>
        {
            // Plan events carry a full replacement payload — handle separately to avoid text append.
            if (value.Kind == ConversationEventKind.PlanUpdated)
            {
                ChatItemViewModel? planItem = Items.LastOrDefault(item => item.ItemId == value.ItemId && item.Kind == value.Kind);
                IReadOnlyList<string> steps = ChatItemViewModel.ParsePlanSteps(value.PayloadJson);
                if (planItem is null)
                {
                    planItem = new ChatItemViewModel("Plan", string.Empty, ConversationEventKind.PlanUpdated) { ItemId = value.ItemId };
                    Items.Add(planItem);
                }

                planItem.UpdatePlanSteps(steps);
                return;
            }

            string role = value.Kind switch
            {
                ConversationEventKind.AgentMessageDelta => "Codex",
                ConversationEventKind.ReasoningSummaryDelta => "Reasoning",
                ConversationEventKind.CommandOutputDelta => "Command",
                ConversationEventKind.DiffUpdated => "Diff",
                ConversationEventKind.Error => "Error",
                _ => "Codex",
            };
            ChatItemViewModel? existing = Items.LastOrDefault(item => item.ItemId == value.ItemId && item.Kind == value.Kind);
            if (existing is null)
            {
                Items.Add(new ChatItemViewModel(role, markdown.ToSafeText(value.Text ?? value.PayloadJson ?? string.Empty), value.Kind)
                {
                    ItemId = value.ItemId,
                    IsTruncated = value.Truncated,
                    OverflowFile = value.OverflowFile,
                });
            }
            else
            {
                existing.Text += markdown.ToSafeText(value.Text ?? string.Empty);
                existing.IsTruncated |= value.Truncated;
                existing.OverflowFile = value.OverflowFile ?? existing.OverflowFile;
            }
        });

    private Task OnApprovalRequestedAsync(ApprovalRequest value)
        => OnUiAsync(() => ApplyApprovalRequested(value));

    private Task OnApprovalResolvedAsync(string requestId)
        => OnUiAsync(() => ApplyApprovalResolved(requestId));

    // Adds the approval as an inline transcript entry, in document order, so the prompt appears
    // where it occurred instead of in a separate stacking panel. Internal so unit tests can drive
    // it without spinning up the worker; the private handler above marshals to the UI thread.
    internal void ApplyApprovalRequested(ApprovalRequest request)
        => Items.Add(new ChatItemViewModel(new ApprovalViewModel(request, ResolveApprovalAsync)));

    // Transforms the inline card in place (no removal) so the choice never stacks. Idempotent: a
    // user click already resolved it optimistically and the worker may notify more than once.
    internal void ApplyApprovalResolved(string requestId)
        => FindApproval(requestId)?.MarkResolved();

    private ApprovalViewModel? FindApproval(string requestId)
    {
        foreach (ChatItemViewModel item in Items)
        {
            if (item.Approval is { } approval && string.Equals(approval.RequestId, requestId, StringComparison.Ordinal))
            {
                return approval;
            }
        }

        return null;
    }

    private async Task ResolveApprovalAsync(string requestId, ApprovalDecision decision)
    {
        _ = outputChannel?.WriteLineAsync($"[AUDIT] Approval resolved: {requestId} → {decision}");
        await bridge.ResolveApprovalAsync(new ResolveApprovalRequest { RequestId = requestId, Decision = decision }, lifetime.Token).ConfigureAwait(false);
    }

    private async Task SignInAsync()
    {
        await ExtensionDiagnostics.WriteOutputAsync(outputChannel, "[CODEX AUTH] Extension login command started.").ConfigureAwait(false);
        try
        {
            StartAccountLoginResult result = await bridge.StartAccountLoginAsync(lifetime.Token).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                UpdateAccount(result.Status);
            }).ConfigureAwait(false);
            await ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                $"[CODEX AUTH] Extension login command completed state={result.Status.State}.").ConfigureAwait(false);
            if (result.Status.State == AccountState.Unavailable)
            {
                await ExtensionDiagnostics.WriteOutputAsync(
                    outputChannel,
                    $"[CODEX] Sign in failed: {result.Status.Message ?? "Unknown error."}").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var unavailable = new AccountStatus
            {
                State = AccountState.Unavailable,
                Message = "Codex Worker did not complete the sign-in request.",
            };
            await OnUiAsync(() =>
            {
                UpdateAccount(unavailable);
            }).ConfigureAwait(false);
            await ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                $"[CODEX] Sign in RPC failed ({ex.GetType().Name}).").ConfigureAwait(false);
        }
    }

    private async Task SignOutAsync()
    {
        await ExtensionDiagnostics.WriteOutputAsync(outputChannel, "[CODEX AUTH] Extension logout command started.").ConfigureAwait(false);
        try
        {
            AccountStatus result = await bridge.LogoutAccountAsync(lifetime.Token).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                UpdateAccount(result);
            }).ConfigureAwait(false);
            await ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                $"[CODEX AUTH] Extension logout command completed state={result.State}.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var unavailable = new AccountStatus
            {
                State = AccountState.Unavailable,
                Message = "Codex Worker did not complete the sign-out request.",
            };
            await OnUiAsync(() =>
            {
                UpdateAccount(unavailable);
            }).ConfigureAwait(false);
            await ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                $"[CODEX] Sign out RPC failed ({ex.GetType().Name}).").ConfigureAwait(false);
        }
    }

    private Task ExecuteAccountActionAsync()
        => Account.IsSignedIn ? SignOutAsync() : SignInAsync();

    private bool CanExecuteAccountAction()
        => (Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval)
        && Account.ShowAction;

    private void UpdateAccount(AccountStatus value)
    {
        Account.Update(value);
        OnPropertyChanged(nameof(StatusDetailText));
        OnPropertyChanged(nameof(ShowAccountAction));
        OnPropertyChanged(nameof(AccountActionText));
        AccountCommand.RaiseCanExecuteChanged();
    }

    // Disconnected is allowed so the user can send with no solution/folder open: SendAsync
    // performs a lazy interactive connect (prompting for a working directory) before sending.
    // Connecting and Degraded are excluded — Connecting is transient and Degraded requires
    // Restart. Disconnected is also gated on connecting == 0: if ConnectWithDirectoryAsync has
    // already started (and will reject a second attempt), disable Send until that attempt settles.
    private bool CanSend()
        => !string.IsNullOrWhiteSpace(ComposerText)
        && (Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy
                or WorkerConnectionState.WaitingForApproval
            || (Status.State is WorkerConnectionState.Disconnected && Volatile.Read(ref connecting) == 0));

    private void RaiseCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        NewThreadCommand.RaiseCanExecuteChanged();
        LoadMoreCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        InterruptCommand.RaiseCanExecuteChanged();
        AccountCommand.RaiseCanExecuteChanged();
    }

    // In the OOP extension process, Application.Current is null so the null-conditional
    // falls through to the inline call. RemoteUI marshals property/collection changes
    // to VS's UI thread automatically via its proxy mechanism.
    private static Task OnUiAsync(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(action).Task;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            // These updates run inline on the StreamJsonRpc dispatch thread. A synchronous
            // throw (e.g. from a Remote UI change-notification subscriber) would unwind into
            // the dispatch loop and silently stop all further worker notifications and
            // responses — the tool window then freezes at its last rendered state.
            ExtensionDiagnostics.Write("UI state update failed", ex);
        }

        return Task.CompletedTask;
    }
}

public sealed class AccountPanelViewModel : ObservableObject
{
    private AccountStatus status = new();

    public AccountState State => status.State;

    public string DisplayText => status.State switch
    {
        AccountState.SignedOut => "Not signed in",
        AccountState.SigningIn => "Signing in...",
        AccountState.SignedIn when !string.IsNullOrWhiteSpace(status.PlanType) => $"Signed in \u00b7 {status.PlanType}",
        AccountState.SignedIn => "Signed in",
        AccountState.Unavailable when !string.IsNullOrWhiteSpace(status.Message) => $"Account status unavailable \u00b7 {status.Message}",
        AccountState.Unavailable => "Account status unavailable",
        _ => "Checking account...",
    };

    public bool ShowSignIn => status.State is AccountState.SignedOut or AccountState.Unavailable;

    public bool IsSignedIn => status.State == AccountState.SignedIn;

    public bool ShowAction => status.State is AccountState.SignedOut or AccountState.SignedIn or AccountState.Unavailable;

    public string ActionText => IsSignedIn ? "Sign out" : "Sign in";

    public void Update(AccountStatus value)
    {
        status = value;
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(ShowAction));
        OnPropertyChanged(nameof(ActionText));
    }
}

// A clickable suggestion shown in the empty/welcome state. Selecting it populates the
// composer with its text (the user then presses Send). [DataContract]/[DataMember] are
// required so Remote UI replicates Text and the command into the VS-side proxy.
[DataContract]
public sealed class SuggestionChip
{
    public SuggestionChip(string text, Func<string, Task> use)
    {
        Text = text;
        UseCommand = new AsyncCommand(() => use(text));
    }

    [DataMember]
    public string Text { get; }

    [DataMember]
    public AsyncCommand UseCommand { get; }
}

[DataContract]
public sealed class ChatItemViewModel : ObservableObject
{
    private string text;
    private bool isTruncated;
    private string? overflowFile;
    private bool isCollapsed;

    public ChatItemViewModel(string role, string text, ConversationEventKind kind)
    {
        Role = role;
        this.text = text;
        Kind = kind;
        isCollapsed = kind == ConversationEventKind.ReasoningSummaryDelta;
        ToggleCollapseCommand = new AsyncCommand(() =>
        {
            IsCollapsed = !IsCollapsed;
            return Task.CompletedTask;
        });
    }

    // Hosts an approval prompt inline in the transcript, in document order. While pending the
    // entry shows the decision buttons; on resolution it transforms in place to show the verdict.
    public ChatItemViewModel(ApprovalViewModel approval)
        : this("Approval", string.Empty, ConversationEventKind.ItemCompleted)
    {
        Approval = approval;
        IsApprovalItem = true;
    }

    [DataMember]
    public string Role { get; }

    public ConversationEventKind Kind { get; }

    public string? ItemId { get; set; }

    [DataMember]
    public string Text
    {
        get => text;
        set => SetProperty(ref text, value);
    }

    [DataMember]
    public bool IsTruncated
    {
        get => isTruncated;
        set => SetProperty(ref isTruncated, value);
    }

    public string? OverflowFile
    {
        get => overflowFile;
        set => SetProperty(ref overflowFile, value);
    }

    [DataMember]
    public bool IsCollapsed
    {
        get => isCollapsed;
        set
        {
            if (SetProperty(ref isCollapsed, value))
                OnPropertyChanged(nameof(CollapseButtonText));
        }
    }

    [DataMember]
    public string CollapseButtonText => isCollapsed ? "▶ Reasoning" : "▼ Reasoning";

    // Computed kind helpers — used by XAML DataTriggers (bool avoids enum reference in remote XAML).
    [DataMember]
    public bool IsReasoningItem => Kind == ConversationEventKind.ReasoningSummaryDelta;
    [DataMember]
    public bool IsCommandItem => Kind == ConversationEventKind.CommandOutputDelta;
    [DataMember]
    public bool IsDiffItem => Kind == ConversationEventKind.DiffUpdated;
    [DataMember]
    public bool IsPlanItem => Kind == ConversationEventKind.PlanUpdated;

    // True when this transcript entry is an inline approval card (composes an ApprovalViewModel).
    [DataMember]
    public bool IsApprovalItem { get; }

    // Inverse of IsApprovalItem — drives the ordinary-message sub-tree's visibility (Remote UI
    // has no inverse-bool converter, so the flag is precomputed here).
    [DataMember]
    public bool IsNormalMessageItem => !IsApprovalItem;

    // The hosted approval for an inline approval entry; null for ordinary messages. ApprovalViewModel
    // is [DataContract], so nested Approval.* bindings replicate through the Remote UI proxy.
    [DataMember]
    public ApprovalViewModel? Approval { get; }

    [DataMember]
    public ObservableCollection<string> PlanSteps { get; } = [];

    [DataMember]
    public AsyncCommand ToggleCollapseCommand { get; }

    public void UpdatePlanSteps(IReadOnlyList<string> steps)
    {
        PlanSteps.Clear();
        foreach (string step in steps)
            PlanSteps.Add("• " + step);
    }

    // Matches a JSON string property: "fieldName":"value" (handles \" and \\ escapes inside value).
    private static readonly Regex TitlePattern = new(@"""title""\s*:\s*""((?:[^\\""]|\\.)*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DescriptionPattern = new(@"""description""\s*:\s*""((?:[^\\""]|\\.)*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TextFieldPattern = new(@"""text""\s*:\s*""((?:[^\\""]|\\.)*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex[] StepFieldPatterns = [TitlePattern, DescriptionPattern, TextFieldPattern];

    public static IReadOnlyList<string> ParsePlanSteps(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return [];

        foreach (Regex pattern in StepFieldPatterns)
        {
            MatchCollection matches = pattern.Matches(payloadJson);
            if (matches.Count > 0)
            {
                var result = new List<string>(matches.Count);
                foreach (Match m in matches)
                    result.Add(m.Groups[1].Value);
                return result;
            }
        }
        return [];
    }
}

[DataContract]
public sealed class ApprovalViewModel : ObservableObject
{
    private readonly Func<string, ApprovalDecision, Task> resolver;
    private bool isResolved;
    private int resolving;

    public ApprovalViewModel(ApprovalRequest request, Func<string, ApprovalDecision, Task> resolver)
    {
        this.resolver = resolver;
        RequestId = request.RequestId;
        DisplayText = request.DisplayText;
        Reason = request.Reason;
        Risk = request.Risk;
        IsPolicyBlocked = request.IsPolicyBlocked;
        PolicyBlockReason = request.PolicyBlockReason;

        ShowAccept = request.AvailableDecisions.Contains(ApprovalDecision.Accept);
        ShowAcceptForTurn = request.AvailableDecisions.Contains(ApprovalDecision.AcceptForTurn);
        ShowAcceptForThread = request.AvailableDecisions.Contains(ApprovalDecision.AcceptForThread);
        ShowAcceptForSession = request.AvailableDecisions.Contains(ApprovalDecision.AcceptForSession);
        ShowDecline = request.AvailableDecisions.Contains(ApprovalDecision.Decline);
        ShowCancel = request.AvailableDecisions.Contains(ApprovalDecision.Cancel);

        if (request.Risk == ApprovalRiskCategory.Network)
        {
            IsNetworkApproval = true;
            // RiskKey format: "network:host:port"
            string[] parts = request.RiskKey.Split(':');
            NetworkHost = parts.Length > 1 ? parts[1] : null;
            NetworkPort = parts.Length > 2 ? parts[2] : null;
        }

        AcceptCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.Accept), () => CanResolve);
        AcceptForTurnCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.AcceptForTurn), () => CanResolve);
        AcceptForThreadCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.AcceptForThread), () => CanResolve);
        AcceptForSessionCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.AcceptForSession), () => CanResolve);
        DeclineCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.Decline), () => CanResolve);
        CancelCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.Cancel), () => CanResolve);
    }

    public string RequestId { get; }

    [DataMember]
    public string DisplayText { get; }

    [DataMember]
    public string? Reason { get; }

    [DataMember]
    public ApprovalRiskCategory Risk { get; }

    [DataMember]
    public bool IsPolicyBlocked { get; }

    [DataMember]
    public string? PolicyBlockReason { get; }

    [DataMember]
    public bool ShowAccept { get; }

    [DataMember]
    public bool ShowAcceptForSession { get; }

    [DataMember]
    public bool ShowAcceptForTurn { get; }

    [DataMember]
    public bool ShowAcceptForThread { get; }

    [DataMember]
    public bool ShowDecline { get; }

    [DataMember]
    public bool ShowCancel { get; }

    [DataMember]
    public bool IsNetworkApproval { get; }

    [DataMember]
    public string? NetworkHost { get; }

    [DataMember]
    public string? NetworkPort { get; }

    [DataMember]
    public bool IsResolved
    {
        get => isResolved;
        private set
        {
            if (SetProperty(ref isResolved, value))
            {
                AcceptCommand.RaiseCanExecuteChanged();
                AcceptForTurnCommand.RaiseCanExecuteChanged();
                AcceptForThreadCommand.RaiseCanExecuteChanged();
                AcceptForSessionCommand.RaiseCanExecuteChanged();
                DeclineCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowDecisionButtons));
                OnPropertyChanged(nameof(ResultText));
            }
        }
    }

    // The decision buttons show while pending; once resolved they collapse and the verdict
    // (ResultText) takes their place — the inline card transforms in place in the transcript.
    [DataMember]
    public bool ShowDecisionButtons => !IsResolved;

    // Terse outcome shown after resolution. The command is already shown as DisplayText, so this
    // is a fixed verdict string and carries no dynamic (untrusted) text — no SafeMarkdownService
    // pass is required.
    [DataMember]
    public string ResultText => Decision switch
    {
        ApprovalDecision.Accept or ApprovalDecision.AcceptForTurn
            or ApprovalDecision.AcceptForThread or ApprovalDecision.AcceptForSession => "✓ Approved",
        ApprovalDecision.Decline => "✗ Declined",
        _ => "Cancelled",
    };

    // The user's choice, captured on click so the verdict renders correctly even before the
    // worker confirms. Stays null for externally-resolved approvals (timeout / cancel).
    public ApprovalDecision? Decision { get; private set; }

    public bool CanResolve => !IsResolved && !IsPolicyBlocked;

    [DataMember]
    public AsyncCommand AcceptCommand { get; }

    [DataMember]
    public AsyncCommand AcceptForSessionCommand { get; }

    [DataMember]
    public AsyncCommand AcceptForTurnCommand { get; }

    [DataMember]
    public AsyncCommand AcceptForThreadCommand { get; }

    [DataMember]
    public AsyncCommand DeclineCommand { get; }

    [DataMember]
    public AsyncCommand CancelCommand { get; }

    // Confirmation path (worker → observer/approvalResolved). Idempotent: a user click already
    // set IsResolved (and Decision) optimistically, and the worker can emit the resolution more
    // than once for one request. An external resolution (timeout / cancel) arrives here with no
    // prior decision, so the verdict falls back to "Cancelled".
    public void MarkResolved()
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
    }

    private async Task ResolveOnceAsync(ApprovalDecision decision)
    {
        if (Interlocked.Exchange(ref resolving, 1) != 0 || !CanResolve)
        {
            return;
        }

        // Capture the choice before flipping IsResolved so the in-place verdict is correct
        // immediately, without waiting for the worker's confirmation round-trip.
        Decision = decision;
        IsResolved = true;
        await resolver(RequestId, decision).ConfigureAwait(false);
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

// Remote UI rejects plain ICommand values at serialization time ("ICommand is not supported,
// please implement Microsoft.VisualStudio.Extensibility.UI.IAsyncCommand instead"), so this
// command implements IAsyncCommand for the VS-side proxy and keeps ICommand for local WPF use
// and unit tests. The proxy reads the IAsyncCommand.CanExecute property and listens to
// INotifyPropertyChanged("CanExecute") to drive Button.IsEnabled across the process boundary.
public sealed class AsyncCommand : ICommand, VSUI.IAsyncCommand, INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs CanExecuteChangedArgs = new(nameof(CanExecute));

    private readonly Func<Task> execute;
    private readonly Func<bool>? canExecute;
    private int running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    // CanExecute must be a PUBLIC property (not an explicit interface implementation):
    // when PropertyChanged("CanExecute") fires, the SDK's NotificationsDispatcher resolves
    // it via GetType().GetProperty("CanExecute") and THROWS ArgumentException when the
    // lookup fails — the exception unwinds into the StreamJsonRpc dispatch loop and
    // silently stops all further worker notifications (tool window freezes mid-state).
    public bool CanExecute => Volatile.Read(ref running) == 0 && (canExecute?.Invoke() ?? true);

    bool ICommand.CanExecute(object? parameter) => CanExecute;

    public async void Execute(object? parameter)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
        {
            return;
        }

        RaiseCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Async command execution failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
            RaiseCanExecuteChanged();
        }
    }

    async Task VSUI.IAsyncCommand.ExecuteAsync(object? parameter, Microsoft.VisualStudio.Extensibility.IClientContext clientContext, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
        {
            return;
        }

        RaiseCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Async command execution failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        PropertyChanged?.Invoke(this, CanExecuteChangedArgs);
    }
}
