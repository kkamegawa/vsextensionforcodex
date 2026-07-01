using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
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
    private readonly IWorkerBridge bridge;
    private readonly OutputChannel? outputChannel;
    private readonly SafeMarkdownService markdown = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly WorkspaceDirectoryResolver workspaceDirectoryResolver;
    private readonly ProjectScaffolder projectScaffolder;
    private readonly ExtensionSettings settings = ExtensionSettings.Load();
    private readonly Queue<UserInputViewModel> userInputQueue = new();
    private readonly Queue<ApprovalViewModel> approvalQueue = new();
    private readonly Dictionary<string, StringBuilder> agentRawText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> itemRawText = new(StringComparer.Ordinal);
    private UserInputViewModel? activeUserInput;
    private ApprovalViewModel? activeApproval;
    private string? lastAgentItemId;
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
        : this(new WorkerBridge(outputChannel), outputChannel, extensibility, autoConnect: true)
    {
    }

    internal ChatViewModel(
        IWorkerBridge bridge,
        OutputChannel? outputChannel = null,
        VisualStudioExtensibility? extensibility = null,
        bool autoConnect = true)
    {
        this.bridge = bridge;
        this.outputChannel = outputChannel;
        workspaceDirectoryResolver = new WorkspaceDirectoryResolver(extensibility);
        projectScaffolder = new ProjectScaffolder(extensibility);
        bridge.StateChanged += OnStateChangedAsync;
        bridge.AccountChanged += OnAccountChangedAsync;
        bridge.ConversationEventReceived += OnConversationEventAsync;
        bridge.ApprovalRequested += OnApprovalRequestedAsync;
        bridge.ApprovalResolved += OnApprovalResolvedAsync;
        bridge.UserInputRequested += OnUserInputRequestedAsync;
        bridge.UserInputResolved += OnUserInputResolvedAsync;
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

        Models = ["gpt-5-codex", "gpt-5"];
        selectedModel = Models[0];

        if (autoConnect)
        {
            _ = TryAutoConnectAsync();
        }
    }

    [DataMember]
    public ObservableCollection<ThreadSummary> Threads { get; } = new();

    [DataMember]
    public ObservableCollection<ChatItemViewModel> Items { get; } = new();

    // Approval prompts are shown one at a time, matching the interactive choice card: a single
    // active card pinned above the composer, with the rest held in a FIFO queue. A burst of
    // concurrent prompts never stacks up and pushes the transcript out of view.
    [DataMember]
    public ApprovalViewModel? ActiveApproval
    {
        get => activeApproval;
        private set
        {
            if (SetProperty(ref activeApproval, value))
            {
                OnPropertyChanged(nameof(HasActiveApproval));
                OnPropertyChanged(nameof(ApprovalQueueText));
            }
        }
    }

    [DataMember]
    public bool HasActiveApproval => ActiveApproval is not null;

    [DataMember]
    public string ApprovalQueueText => approvalQueue.Count switch
    {
        0 => string.Empty,
        1 => "1 approval waiting",
        _ => $"{approvalQueue.Count} approvals waiting",
    };

    // Interactive choices are shown one at a time, Claude Code-style: a single active card pinned
    // above the composer, with the rest held in a FIFO queue. This keeps a burst of choice prompts
    // from filling/scrolling the panel.
    [DataMember]
    public UserInputViewModel? ActiveUserInput
    {
        get => activeUserInput;
        private set
        {
            if (SetProperty(ref activeUserInput, value))
            {
                OnPropertyChanged(nameof(HasActiveUserInput));
                OnPropertyChanged(nameof(UserInputQueueText));
            }
        }
    }

    [DataMember]
    public bool HasActiveUserInput => ActiveUserInput is not null;

    [DataMember]
    public string UserInputQueueText => userInputQueue.Count switch
    {
        0 => string.Empty,
        1 => "1 choice waiting",
        _ => $"{userInputQueue.Count} choices waiting",
    };

    // Opt-in "Choices": surface codex's confirmation/choice prompts as a selection card. codex only
    // emits the structured request_user_input tool in Plan mode, so in Agent mode this drives the
    // prose detector (TryDetectChoicePrompt) — a purely client-side concern, so no reconnect is needed.
    // The flag is still sent at the next connect so the structured path also works if Plan mode is used.
    [DataMember]
    public bool ExperimentalApiEnabled
    {
        get => settings.ExperimentalApiEnabled;
        set
        {
            if (settings.ExperimentalApiEnabled == value)
            {
                return;
            }

            settings.ExperimentalApiEnabled = value;
            settings.Save();
            OnPropertyChanged();
        }
    }

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

    [DataMember]
    public ObservableCollection<string> Models { get; }

    [DataMember]
    public string? SelectedModel
    {
        get => selectedModel;
        set => SetProperty(ref selectedModel, value);
    }

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
            try
            {
                await projectScaffolder.EnsureScaffoldAsync(workingDirectory, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                ExtensionDiagnostics.Write("Project scaffolding failed; continuing with Worker connection", ex);
            }

            WorkerStatus result;
            try
            {
                result = await bridge.ConnectAsync(workingDirectory, settings.ExperimentalApiEnabled, lifetime.Token).ConfigureAwait(false);
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
                await RefreshReadyStateAsync(reloadThreads: false).ConfigureAwait(false);
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
            await RefreshReadyStateAsync(reloadThreads: true).ConfigureAwait(false);
        }
    }

    private async Task RefreshReadyStateAsync(bool reloadThreads)
    {
        AccountStatus accountStatus = await bridge.GetAccountStatusAsync(lifetime.Token).ConfigureAwait(false);
        ExtensionDiagnostics.Write($"Account status received state={accountStatus.State} plan={accountStatus.PlanType ?? "none"}");
        await OnUiAsync(() =>
        {
            UpdateAccount(accountStatus);
        }).ConfigureAwait(false);
        await PopulateModelsAsync().ConfigureAwait(false);
        if (reloadThreads)
        {
            await ReloadThreadsAsync().ConfigureAwait(false);
        }
        else
        {
            await LoadMoreAsync().ConfigureAwait(false);
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

    internal async Task PopulateModelsAsync()
    {
        ListModelsResult result;
        try
        {
            result = await bridge.ListModelsAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Model list refresh failed; keeping fallback models", ex);
            await ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                "[CODEX MODELS] Failed to query the app-server model list; keeping the built-in fallback models.").ConfigureAwait(false);
            return;
        }

        var modelIds = result.Models
            .Select(model => model.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The catalog default may be a hidden preset that the picker list excludes. Make sure it is
        // still offered (and pre-selected) by inserting it at the top of the dropdown when missing.
        bool defaultInjected = false;
        if (!string.IsNullOrWhiteSpace(result.DefaultModel)
            && !modelIds.Contains(result.DefaultModel!, StringComparer.Ordinal))
        {
            modelIds.Insert(0, result.DefaultModel!);
            defaultInjected = true;
        }

        if (modelIds.Count == 0)
        {
            await ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                "[CODEX MODELS] The app-server returned no models; keeping the built-in fallback models.").ConfigureAwait(false);
            return;
        }

        string defaultModelLabel = string.IsNullOrWhiteSpace(result.DefaultModel)
            ? "(none reported)"
            : defaultInjected
                ? $"{result.DefaultModel} (added to list)"
                : result.DefaultModel!;
        await ExtensionDiagnostics.WriteOutputAsync(
            outputChannel,
            $"[CODEX MODELS] Available models ({modelIds.Count}): {string.Join(", ", modelIds)}. Default: {defaultModelLabel}.").ConfigureAwait(false);

        await OnUiAsync(() =>
        {
            string? previousSelection = SelectedModel;
            Models.Clear();
            foreach (string modelId in modelIds)
            {
                Models.Add(modelId);
            }

            if (result.DefaultModel is not null && Models.Contains(result.DefaultModel))
            {
                SelectedModel = result.DefaultModel;
            }
            else if (previousSelection is not null && Models.Contains(previousSelection))
            {
                SelectedModel = previousSelection;
            }
            else
            {
                SelectedModel = Models[0];
            }
        }).ConfigureAwait(false);
    }

    private async Task SendAsync()
    {
        // Typing a normal message supersedes a still-pending prose-detected choice card (the user
        // chose to answer in their own words instead of picking an option).
        if (ActiveUserInput?.IsSynthetic == true)
        {
            await OnUiAsync(() => RemoveUserInput(ActiveUserInput!.RequestId)).ConfigureAwait(false);
        }

        await SendMessageAsync(ComposerText, clearComposer: true).ConfigureAwait(false);
    }

    // Core send path, shared by the composer (clearComposer: true) and the synthetic-choice resolver
    // (clearComposer: false), which sends the picked option text as the next turn.
    private async Task SendMessageAsync(string text, bool clearComposer)
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

        if (clearComposer)
        {
            await OnUiAsync(() => SetComposerText(string.Empty)).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

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
            await bridge.StartTurnAsync(
                new StartTurnRequest
                {
                    ThreadId = SelectedThread.Id,
                    Text = text,
                    Model = SelectedModel,
                    ApprovalPolicy = MapModeToApprovalPolicy(SelectedMode),
                    SandboxMode = MapModeToSandbox(SelectedMode),
                },
                lifetime.Token).ConfigureAwait(false);
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

    // Agent/Chat mode is a per-turn preset over the codex app-server approval policy and sandbox.
    // Chat: conversation only (read-only sandbox, approvals never prompted so no edits run).
    // Agent/unknown: omit the overrides so the app-server defaults apply to the turn.
    internal static string? MapModeToApprovalPolicy(string? mode)
        => string.Equals(mode, "Chat", StringComparison.Ordinal) ? "never" : null;

    internal static string? MapModeToSandbox(string? mode)
        => string.Equals(mode, "Chat", StringComparison.Ordinal) ? "readOnly" : null;

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
        => OnUiAsync(() =>
        {
            WorkerConnectionState previous = Status.State;
            Status = value;
            if (value.State == previous)
            {
                return;
            }

            // Connection open/close/degraded transitions are diagnostics, not transcript content —
            // log them to the Output channel (issue #17). The status chip in the panel header still
            // reflects the current state through the Status binding.
            string? connectionLine = value.State switch
            {
                WorkerConnectionState.Connecting => "[connection] Connecting to codex app-server...",
                WorkerConnectionState.Ready => "[connection] Connected to codex app-server.",
                WorkerConnectionState.Degraded => $"[connection] Connection degraded: {value.Message}",
                WorkerConnectionState.Disconnected => $"[connection] Disconnected: {value.Message}",
                _ => null,
            };
            if (connectionLine is not null)
            {
                _ = ExtensionDiagnostics.WriteOutputAsync(outputChannel, connectionLine);
            }

            // Surface the worker's curated, user-actionable message (network failure, connect
            // failure, app-server exit) in the panel as a concise error item — never raw JSON. The
            // worker only sets Degraded with such a message, so this is the panel's actionable-error
            // path now that raw Error events are routed to Output.
            if (value.State == WorkerConnectionState.Degraded && !string.IsNullOrWhiteSpace(value.Message))
            {
                string errorText = markdown.ToSafeText(value.Message);
                ChatItemViewModel? last = Items.LastOrDefault();
                if (last is null
                    || last.Kind != ConversationEventKind.Error
                    || !string.Equals(last.Text, errorText, StringComparison.Ordinal))
                {
                    Items.Add(new ChatItemViewModel("Error", errorText, ConversationEventKind.Error));
                }
            }
        });

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

            // Track the agent message text across a turn so a completed message can be scanned for a
            // prose choice prompt (codex doesn't emit the structured tool in Agent mode).
            switch (value.Kind)
            {
                case ConversationEventKind.TurnStarted:
                    agentRawText.Clear();
                    itemRawText.Clear();
                    lastAgentItemId = null;
                    break;
                case ConversationEventKind.AgentMessageDelta when !string.IsNullOrEmpty(value.Text) && value.ItemId is not null:
                    AppendAgentRaw(value.ItemId, value.Text);
                    break;
                case ConversationEventKind.TurnCompleted:
                    TryDetectChoicePrompt();
                    itemRawText.Clear();
                    break;
            }

            // Only user-facing Codex content reaches the panel. Lifecycle, protocol, error, and
            // unknown events — and any user-facing kind that arrived without rendered Text (carrying
            // only raw/structured PayloadJson) — are routed to the Output channel so the transcript
            // never shows raw JSON or internal event names. See issue #17.
            if (!ConversationEventPresentation.IsPanelContent(value.Kind) || string.IsNullOrEmpty(value.Text))
            {
                _ = ExtensionDiagnostics.WriteOutputAsync(outputChannel, ConversationEventPresentation.FormatDiagnostic(value));
                return;
            }

            string text = value.Text; // Non-null below: guarded by IsNullOrEmpty above.
            string role = value.Kind switch
            {
                ConversationEventKind.AgentMessageDelta => "Codex",
                ConversationEventKind.ReasoningSummaryDelta => "Reasoning",
                ConversationEventKind.CommandOutputDelta => "Command",
                ConversationEventKind.DiffUpdated => "Diff",
                _ => "Codex",
            };
            bool renderFromAccumulatedText = ShouldRenderFromAccumulatedText(value.Kind);
            string renderedText;
            IReadOnlyList<ChatBlockViewModel> renderedBlocks = [];
            if (renderFromAccumulatedText)
            {
                string rawText = AppendAccumulatedText(value, text);
                SafeMarkdownRenderResult rendered = markdown.ToSafeTextAndBlocks(rawText);
                renderedText = rendered.Text;
                renderedBlocks = rendered.Blocks;
            }
            else
            {
                renderedText = markdown.ToSafeText(text);
            }

            ChatItemViewModel? existing = Items.LastOrDefault(item => item.ItemId == value.ItemId && item.Kind == value.Kind);
            if (existing is null)
            {
                var item = new ChatItemViewModel(role, renderedText, value.Kind)
                {
                    ItemId = value.ItemId,
                    IsTruncated = value.Truncated,
                    OverflowFile = value.OverflowFile,
                };
                if (renderFromAccumulatedText)
                {
                    item.UpdateBlocks(renderedBlocks);
                }

                Items.Add(item);
            }
            else
            {
                existing.Text = renderFromAccumulatedText ? renderedText : existing.Text + renderedText;
                if (renderFromAccumulatedText)
                {
                    existing.UpdateBlocks(renderedBlocks);
                }

                existing.IsTruncated |= value.Truncated;
                existing.OverflowFile = value.OverflowFile ?? existing.OverflowFile;
            }
        });

    private string AppendAccumulatedText(ConversationEvent value, string text)
    {
        string key = string.Concat(value.Kind.ToString(), ":", value.ItemId ?? string.Empty);
        if (!itemRawText.TryGetValue(key, out StringBuilder? rawText))
        {
            rawText = new StringBuilder();
            itemRawText[key] = rawText;
        }

        rawText.Append(text);
        return rawText.ToString();
    }

    private static bool ShouldRenderFromAccumulatedText(ConversationEventKind kind)
        => kind is ConversationEventKind.AgentMessageDelta or ConversationEventKind.ReasoningSummaryDelta;

    private Task OnApprovalRequestedAsync(ApprovalRequest value)
        => OnUiAsync(() => EnqueueApproval(new ApprovalViewModel(value, ResolveApprovalAsync)));

    // Show one approval card, queue the rest. Concurrent requestApproval prompts must not stack up
    // and push the transcript out of view.
    private void EnqueueApproval(ApprovalViewModel approval)
    {
        if (ActiveApproval is null)
        {
            ActiveApproval = approval;
        }
        else
        {
            approvalQueue.Enqueue(approval);
            OnPropertyChanged(nameof(ApprovalQueueText));
        }
    }

    private Task OnApprovalResolvedAsync(string requestId)
        => OnUiAsync(() => RemoveApproval(requestId));

    // Idempotent, mirroring RemoveUserInput: a repeat resolve for an already-removed id is a no-op.
    private void RemoveApproval(string requestId)
    {
        if (ActiveApproval?.RequestId == requestId)
        {
            ActiveApproval.MarkResolved();
            ActiveApproval = approvalQueue.Count > 0 ? approvalQueue.Dequeue() : null;
            OnPropertyChanged(nameof(ApprovalQueueText));
            return;
        }

        // Resolved/cancelled while still queued: drop it without promoting.
        if (approvalQueue.Any(item => item.RequestId == requestId))
        {
            ApprovalViewModel[] remaining = approvalQueue.Where(item => item.RequestId != requestId).ToArray();
            approvalQueue.Clear();
            foreach (ApprovalViewModel item in remaining)
            {
                approvalQueue.Enqueue(item);
            }

            OnPropertyChanged(nameof(ApprovalQueueText));
        }
    }

    private async Task ResolveApprovalAsync(string requestId, ApprovalDecision decision)
    {
        _ = outputChannel?.WriteLineAsync($"[AUDIT] Approval resolved: {requestId} → {decision}");
        // Copilot Chat parity: the card disappears (via the worker's approvalResolved echo) and the
        // transcript keeps a single, safe result line so the outcome stays visible in context.
        await OnUiAsync(() => AppendDecisionResultItem(requestId, decision)).ConfigureAwait(false);
        await bridge.ResolveApprovalAsync(new ResolveApprovalRequest { RequestId = requestId, Decision = decision }, lifetime.Token).ConfigureAwait(false);
    }

    // Appends a result-only transcript line for a user-resolved approval. The DisplayText was
    // already redacted by the worker, but it is still routed through SafeMarkdownService before
    // display because everything shown in the panel must pass the same sanitization path.
    // Internal: exercised directly by the UI test assembly (InternalsVisibleTo).
    internal void AppendDecisionResultItem(string requestId, ApprovalDecision decision)
    {
        ApprovalViewModel? approval = ActiveApproval?.RequestId == requestId
            ? ActiveApproval
            : approvalQueue.FirstOrDefault(item => item.RequestId == requestId);
        string summary = approval is null
            ? DescribeDecision(decision)
            : string.Concat(DescribeDecision(decision), " — ", approval.DisplayText);
        Items.Add(new ChatItemViewModel("Decision", markdown.ToSafeText(summary), ConversationEventKind.ItemCompleted));
    }

    // Human-readable decision labels for the transcript result line.
    internal static string DescribeDecision(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Accept => "Accepted",
        ApprovalDecision.AcceptForTurn => "Accepted for turn",
        ApprovalDecision.AcceptForThread => "Accepted for thread",
        ApprovalDecision.AcceptForSession => "Accepted for session",
        ApprovalDecision.Decline => "Declined",
        ApprovalDecision.Cancel => "Cancelled",
        _ => decision.ToString(),
    };

    private Task OnUserInputRequestedAsync(UserInputRequest value)
        => OnUiAsync(() => EnqueueUserInput(new UserInputViewModel(value, ResolveUserInputAsync, markdown)));

    // Shared by the structured (server-request) path and the prose-detection path: show one card,
    // queue the rest.
    private void EnqueueUserInput(UserInputViewModel userInput)
    {
        if (ActiveUserInput is null)
        {
            ActiveUserInput = userInput;
        }
        else
        {
            userInputQueue.Enqueue(userInput);
            OnPropertyChanged(nameof(UserInputQueueText));
        }
    }

    private Task OnUserInputResolvedAsync(string requestId)
        => OnUiAsync(() => RemoveUserInput(requestId));

    // Idempotent: the worker emits userInputResolved twice (from ResolveUserInputAsync and from the
    // request handler after it returns), so a repeat call for an already-removed id is a no-op.
    private void RemoveUserInput(string requestId)
    {
        if (ActiveUserInput?.RequestId == requestId)
        {
            ActiveUserInput.MarkResolved();
            ActiveUserInput = userInputQueue.Count > 0 ? userInputQueue.Dequeue() : null;
            OnPropertyChanged(nameof(UserInputQueueText));
            return;
        }

        // Resolved/cancelled while still queued: drop it without promoting.
        if (userInputQueue.Any(item => item.RequestId == requestId))
        {
            UserInputViewModel[] remaining = userInputQueue.Where(item => item.RequestId != requestId).ToArray();
            userInputQueue.Clear();
            foreach (UserInputViewModel item in remaining)
            {
                userInputQueue.Enqueue(item);
            }

            OnPropertyChanged(nameof(UserInputQueueText));
        }
    }

    // Structured choice (real item/tool/requestUserInput server request): answer it via RPC. The
    // card is removed by the worker's userInputResolved echo; a result-only line keeps the picked
    // option visible in the transcript (Copilot Chat parity), sanitized because option labels come
    // from untrusted app-server data.
    private async Task ResolveUserInputAsync(string requestId, IReadOnlyDictionary<string, string[]> answers)
    {
        await OnUiAsync(() => AppendUserInputResultItem(answers)).ConfigureAwait(false);
        await bridge.ResolveUserInputAsync(
            new ResolveUserInputRequest
            {
                RequestId = requestId,
                Answers = answers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            },
            lifetime.Token).ConfigureAwait(false);
    }

    // Internal: exercised directly by the UI test assembly (InternalsVisibleTo).
    internal void AppendUserInputResultItem(IReadOnlyDictionary<string, string[]> answers)
    {
        string[] selections = answers.Values.SelectMany(values => values).ToArray();
        if (selections.Length == 0)
        {
            return;
        }

        string summary = string.Concat("Selected — ", string.Join(", ", selections));
        Items.Add(new ChatItemViewModel("Decision", markdown.ToSafeText(summary), ConversationEventKind.ItemCompleted));
    }

    // Prose-detected choice: there is no pending server request, so the picked option is sent as the
    // next turn (it also shows in the transcript as the user's message), then the card is removed.
    private async Task ResolveSyntheticUserInputAsync(string requestId, IReadOnlyDictionary<string, string[]> answers)
    {
        string? choice = answers.Values.SelectMany(values => values).FirstOrDefault();
        await OnUiAsync(() => RemoveUserInput(requestId)).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(choice))
        {
            await SendMessageAsync(choice!, clearComposer: false).ConfigureAwait(false);
        }
    }

    // Accumulate the raw (pre-markdown-stripping) agent text per item so a completed message can be
    // inspected for a choice prompt. Gated on the opt-in toggle so detection is off by default.
    private void AppendAgentRaw(string itemId, string rawChunk)
    {
        if (!settings.ExperimentalApiEnabled)
        {
            return;
        }

        lastAgentItemId = itemId;
        if (!agentRawText.TryGetValue(itemId, out StringBuilder? builder))
        {
            builder = new StringBuilder();
            agentRawText[itemId] = builder;
        }

        // Choice prompts are small; cap accumulation so a long message can't grow memory unbounded.
        if (builder.Length < 16 * 1024)
        {
            builder.Append(rawChunk);
        }
    }

    // On turn completion (codex is now waiting for the user), promote a detected choice prompt in the
    // last agent message into the same single-card selection UI.
    private void TryDetectChoicePrompt()
    {
        if (!settings.ExperimentalApiEnabled || lastAgentItemId is null)
        {
            return;
        }

        agentRawText.TryGetValue(lastAgentItemId, out StringBuilder? builder);
        string raw = builder?.ToString() ?? string.Empty;
        agentRawText.Clear();
        lastAgentItemId = null;
        if (ChoicePromptParser.TryParse(raw, out UserInputRequest synthesized))
        {
            EnqueueUserInput(new UserInputViewModel(synthesized, ResolveSyntheticUserInputAsync, markdown) { IsSynthetic = true });
        }
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

    [DataMember]
    public bool UsesBlockRendering => Kind is ConversationEventKind.AgentMessageDelta or ConversationEventKind.ReasoningSummaryDelta;

    [DataMember]
    public ObservableCollection<ChatBlockViewModel> Blocks { get; } = [];

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

    public void UpdateBlocks(IReadOnlyList<ChatBlockViewModel> blocks)
    {
        int sharedCount = Math.Min(Blocks.Count, blocks.Count);
        for (int index = 0; index < sharedCount; index++)
        {
            Blocks[index].CopyFrom(blocks[index]);
        }

        while (Blocks.Count > blocks.Count)
        {
            Blocks.RemoveAt(Blocks.Count - 1);
        }

        for (int index = Blocks.Count; index < blocks.Count; index++)
        {
            Blocks.Add(blocks[index]);
        }
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
public sealed class ChatBlockViewModel : ObservableObject
{
    private string text = string.Empty;
    private string code = string.Empty;
    private string language = string.Empty;
    private bool isParagraph;
    private bool isHeading;
    private bool isCodeBlock;
    private bool isListItem;
    private bool isNestedListItem;
    private bool isDeeplyNestedListItem;
    private bool isSeparator;
    private bool isH1;
    private bool isH2;
    private bool isH3;

    [DataMember]
    public string Text
    {
        get => text;
        set => SetProperty(ref text, value);
    }

    [DataMember]
    public string Code
    {
        get => code;
        set => SetProperty(ref code, value);
    }

    [DataMember]
    public string Language
    {
        get => language;
        set
        {
            if (SetProperty(ref language, value))
            {
                OnPropertyChanged(nameof(CodeBlockAutomationName));
            }
        }
    }

    [DataMember]
    public bool IsParagraph
    {
        get => isParagraph;
        set => SetProperty(ref isParagraph, value);
    }

    [DataMember]
    public bool IsHeading
    {
        get => isHeading;
        set => SetProperty(ref isHeading, value);
    }

    [DataMember]
    public bool IsCodeBlock
    {
        get => isCodeBlock;
        set
        {
            if (SetProperty(ref isCodeBlock, value))
            {
                OnPropertyChanged(nameof(CodeBlockAutomationName));
            }
        }
    }

    [DataMember]
    public bool IsListItem
    {
        get => isListItem;
        set => SetProperty(ref isListItem, value);
    }

    // Precomputed indent flags (Remote UI cannot use value converters, so the XAML maps each
    // flag to a fixed Margin via DataTriggers). Depth 2 gets one extra indent step; depth 3 and
    // beyond are capped at a second step so pathological nesting can't push text off-panel.
    [DataMember]
    public bool IsNestedListItem
    {
        get => isNestedListItem;
        set => SetProperty(ref isNestedListItem, value);
    }

    [DataMember]
    public bool IsDeeplyNestedListItem
    {
        get => isDeeplyNestedListItem;
        set => SetProperty(ref isDeeplyNestedListItem, value);
    }

    [DataMember]
    public bool IsSeparator
    {
        get => isSeparator;
        set => SetProperty(ref isSeparator, value);
    }

    [DataMember]
    public bool IsH1
    {
        get => isH1;
        set => SetProperty(ref isH1, value);
    }

    [DataMember]
    public bool IsH2
    {
        get => isH2;
        set => SetProperty(ref isH2, value);
    }

    [DataMember]
    public bool IsH3
    {
        get => isH3;
        set => SetProperty(ref isH3, value);
    }

    [DataMember]
    public string CodeBlockAutomationName
        => string.IsNullOrWhiteSpace(Language) ? "Code block" : $"Code block: {Language}";

    public static ChatBlockViewModel Paragraph(string text) => new()
    {
        Text = text,
        IsParagraph = true,
    };

    public static ChatBlockViewModel Heading(string text, int level) => new()
    {
        Text = text,
        IsHeading = true,
        IsH1 = level <= 1,
        IsH2 = level == 2,
        IsH3 = level >= 3,
    };

    public static ChatBlockViewModel CodeBlock(string code, string language) => new()
    {
        Code = code,
        Language = language,
        IsCodeBlock = true,
    };

    public static ChatBlockViewModel ListItem(string text) => ListItem(text, "•", 1);

    public static ChatBlockViewModel ListItem(string text, string marker, int depth) => new()
    {
        Text = string.IsNullOrEmpty(marker) ? text : string.Concat(marker, " ", text),
        IsListItem = true,
        IsNestedListItem = depth == 2,
        IsDeeplyNestedListItem = depth >= 3,
    };

    public static ChatBlockViewModel Separator() => new()
    {
        IsSeparator = true,
    };

    public void CopyFrom(ChatBlockViewModel other)
    {
        Text = other.Text;
        Code = other.Code;
        Language = other.Language;
        IsParagraph = other.IsParagraph;
        IsHeading = other.IsHeading;
        IsCodeBlock = other.IsCodeBlock;
        IsListItem = other.IsListItem;
        IsNestedListItem = other.IsNestedListItem;
        IsDeeplyNestedListItem = other.IsDeeplyNestedListItem;
        IsSeparator = other.IsSeparator;
        IsH1 = other.IsH1;
        IsH2 = other.IsH2;
        IsH3 = other.IsH3;
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
            }
        }
    }

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

    public void MarkResolved() => IsResolved = true;

    private async Task ResolveOnceAsync(ApprovalDecision decision)
    {
        if (Interlocked.Exchange(ref resolving, 1) != 0 || !CanResolve)
        {
            return;
        }

        IsResolved = true;
        await resolver(RequestId, decision).ConfigureAwait(false);
    }
}

// An interactive choice prompt (request_user_input) surfaced as a card with one radio group per
// question and a Submit button. [DataContract]/[DataMember] are required so Remote UI replicates
// the questions, options, and command into the VS-side proxy.
[DataContract]
public sealed class UserInputViewModel : ObservableObject
{
    private readonly Func<string, IReadOnlyDictionary<string, string[]>, Task> resolver;
    private bool isResolved;
    private int resolving;

    public UserInputViewModel(
        UserInputRequest request,
        Func<string, IReadOnlyDictionary<string, string[]>, Task> resolver,
        SafeMarkdownService markdown)
    {
        this.resolver = resolver;
        RequestId = request.RequestId;
        Questions = new ObservableCollection<UserInputQuestionViewModel>(
            request.Questions.Select(question => new UserInputQuestionViewModel(question, markdown)));
        SubmitCommand = new AsyncCommand(SubmitOnceAsync, () => CanSubmit);
    }

    public string RequestId { get; }

    // True when synthesized from a detected prose choice (no pending server request); drives
    // ChatViewModel's "send the picked option as the next turn" resolution path.
    public bool IsSynthetic { get; init; }

    [DataMember]
    public ObservableCollection<UserInputQuestionViewModel> Questions { get; }

    [DataMember]
    public AsyncCommand SubmitCommand { get; }

    [DataMember]
    public bool IsResolved
    {
        get => isResolved;
        private set
        {
            if (SetProperty(ref isResolved, value))
                SubmitCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanSubmit => !IsResolved;

    public void MarkResolved() => IsResolved = true;

    private async Task SubmitOnceAsync()
    {
        if (Interlocked.Exchange(ref resolving, 1) != 0 || !CanSubmit)
        {
            return;
        }

        IsResolved = true;
        var answers = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (UserInputQuestionViewModel question in Questions)
        {
            if (question.SelectedLabel is { } label)
            {
                answers[question.Id] = [label];
            }
        }

        await resolver(RequestId, answers).ConfigureAwait(false);
    }
}

[DataContract]
public sealed class UserInputQuestionViewModel : ObservableObject
{
    public UserInputQuestionViewModel(UserInputQuestion question, SafeMarkdownService markdown)
    {
        Id = question.Id;
        Header = markdown.ToSafeText(question.Header);
        Question = markdown.ToSafeText(question.Question);
        Options = new ObservableCollection<UserInputOptionViewModel>(
            question.Options.Select(option => new UserInputOptionViewModel(option, markdown, OnOptionSelected)));
    }

    // Not a DataMember: used only to key the answer sent back to the app-server.
    public string Id { get; }

    [DataMember]
    public string Header { get; }

    [DataMember]
    public string Question { get; }

    [DataMember]
    public ObservableCollection<UserInputOptionViewModel> Options { get; }

    // The verbatim (unsanitized) label of the selected option, echoed back to the app-server.
    public string? SelectedLabel => Options.FirstOrDefault(option => option.IsSelected)?.Label;

    // Single-select: selecting one option clears the rest of this question's group.
    private void OnOptionSelected(UserInputOptionViewModel selected)
    {
        foreach (UserInputOptionViewModel option in Options)
        {
            if (!ReferenceEquals(option, selected))
                option.SetSelectedSilently(false);
        }
    }
}

[DataContract]
public sealed class UserInputOptionViewModel : ObservableObject
{
    private readonly Action<UserInputOptionViewModel> onSelected;
    private bool isSelected;

    public UserInputOptionViewModel(UserInputOption option, SafeMarkdownService markdown, Action<UserInputOptionViewModel> onSelected)
    {
        this.onSelected = onSelected;
        // Label is echoed back verbatim so it matches the server's option; DisplayLabel is the
        // sanitized text actually rendered.
        Label = option.Label;
        DisplayLabel = markdown.ToSafeText(option.Label);
        Description = markdown.ToSafeText(option.Description);
    }

    // Not a DataMember: the raw value, never rendered, only submitted back to the app-server.
    public string Label { get; }

    [DataMember]
    public string DisplayLabel { get; }

    [DataMember]
    public string Description { get; }

    [DataMember]
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value) && value)
                onSelected(this);
        }
    }

    internal void SetSelectedSilently(bool value) => SetProperty(ref isSelected, value, nameof(IsSelected));
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
