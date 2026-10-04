using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using VSUI = Microsoft.VisualStudio.Extensibility.UI;

namespace Codex.VisualStudio.Extension;

// Remote UI serializes the data context to a proxy in the VS process. Only DataMember
// properties of DataContract types reach that proxy — a class without the attributes is
// serialized as an EMPTY object and every binding to it silently fails (buttons with bound
// Content render as empty pills, bound text stays blank).
[DataContract]
public sealed class ChatViewModel : ObservableObject, IDisposable
{
    private IWorkerBridge bridge;
    private readonly Func<IWorkerBridge> bridgeFactory;
    private readonly OutputChannel? outputChannel;
    private readonly SafeMarkdownService markdown = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly WorkspaceDirectoryResolver workspaceDirectoryResolver;
    private readonly ProjectScaffolder projectScaffolder;
    private readonly AgentsFileInitializer agentsFileInitializer;
    private readonly IFilePickerService filePickerService;
    private readonly IWorkspaceFileSearchService workspaceFileSearchService;
    private readonly IProtectedDirectoryPolicy protectedDirectoryPolicy;
    private readonly VisualStudioExtensibility? extensibility;
    private readonly SlashCommandCatalog slashCommandCatalog = new();
    private readonly SlashCommandParser slashCommandParser;
    private readonly SlashCommandCoordinator slashCommandCoordinator = new();
    private readonly IExtensionSettingsStore settingsStore;
    private readonly IExternalLinkOpener externalLinkOpener;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly SemaphoreSlim usageRefreshGate = new(1, 1);
    private readonly ExtensionSettings settings;
    private readonly RemoteProfilesPresentationViewModel remoteProfiles;
    // One gate for every same-instance profile/settings mutation, reconnect snapshot validation,
    // and the Worker RPC dispatch that follows (design: profile freshness).
    private readonly SemaphoreSlim profileOperationGate = new(1, 1);
    private readonly ConnectionHealthPresentationViewModel connectionHealth;
    private RemoteProfileViewModel? observedHealthProfile;
    private readonly Queue<UserInputViewModel> userInputQueue = new();
    private readonly Queue<ApprovalViewModel> approvalQueue = new();
    private readonly Dictionary<string, StringBuilder> agentRawText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> itemRawText = new(StringComparer.Ordinal);
    private static readonly Regex HeaderWhitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private UserInputViewModel? activeUserInput;
    private ApprovalViewModel? activeApproval;
    private string? lastAgentRawKey;
    private int disposed;
    private int connecting;
    private WorkerStatus status = new() { State = WorkerConnectionState.Disconnected, Message = "Open Codex to connect." };
    private ThreadSummary? selectedThread;
    private string composerText = string.Empty;
    private string? nextCursor;
    private bool initialized;
    private bool isHistoryOpen;
    private bool isUsageOpen;
    private bool isConnectionTargetOpen;
    // Profile name the current or last connection attempt used; null means local stdio.
    private string? connectedProfileName;
    private string? observedStatePartitionFingerprint;
    private long observedOwnerGeneration;
    private bool usageConnectionActive;
    private long usageConnectionGeneration;
    private long usageFetchedGeneration = -1;
    private long rateLimitPushVersion;
    private DateTimeOffset? latestRateLimitsAt;
    private bool ideContextEnabled = true;
    private string? selectedModel;
    private string selectedReasoningEffortId = ReasoningEffortCatalog.DefaultId;
    private string selectedMode = "Agent";
    private ApprovalModeOption? selectedApprovalMode;
    private ApprovalModeOption? pendingApprovalMode;
    private string approvalModeConfirmationText = string.Empty;
    private string? approvalModeBeforeConfirmationId;
    private bool confirmationStartsNewThread;
    private string? workingDirectory;
    private readonly Dictionary<string, PendingReasoningOverride> pendingReasoningByThread = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> reasoningRestoreByThread = new(StringComparer.Ordinal);
    private string? nextPersonality;
    private string? nextCollaborationMode;
    private string selectedServiceTierId = ServiceTierCatalog.DefaultId;
    private readonly Dictionary<string, PendingServiceTierOverride> pendingServiceTierByThread = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> serviceTierRestoreByThread = new(StringComparer.Ordinal);
    private ListModelsResult modelCatalog = new();
    private RateLimitsResult? latestRateLimits;
    private readonly HashSet<SlashCommandId> unavailableSlashCommands = [];
    private int drainingSlashQueue;
    private CancellationTokenSource? fileSuggestionRefresh;
    private ListSkillsResult? skillsSnapshot;
    private DateTimeOffset skillsSnapshotExpiresAt;
    private Task? skillsLoadTask;
    private string? skillsLoadFailure;
    private bool skillsRefreshPending;
    private long minimumSkillsGeneration;
    private long skillSelectionSequence;
    private Dictionary<string, SkillInfo> skillSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ChatItemViewModel> inFlightMutations = [];
    private Action<WorkerRecoveryFailureKind>? bridgeConnectionLostHandler;
    private CancellationTokenSource? recoveryCancellation;
    private RecoveryDraftViewModel? quarantinedDraft;
    private string recoveryStatusText = string.Empty;
    private bool isRecovering;
    private bool historyIsStale;
    private bool isHistoryLoading;
    private bool isThreadJoined;
    private bool canLoadOlderHistory;
    private string historyStatusText = string.Empty;
    private string? threadTurnsCursor;
    private ThreadItemCursor? threadItemsCursor;
    private readonly HashSet<string> deletedAttachmentIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> historyItemKeys = new(StringComparer.Ordinal);
    private long historyBytes;
    private int bufferedEventCount;
    private long bufferedEventBytes;
    private readonly Queue<WorkerNotification<ConversationEvent>> pendingHistoryEvents = new();
    private const int MaximumVisibleHistoryItems = 1000;
    private const long MaximumVisibleHistoryBytes = 16 * 1024 * 1024;
    private const int MaximumBufferedHistoryEvents = 1024;
    private const long MaximumBufferedHistoryBytes = 8 * 1024 * 1024;
    private readonly ObservableCollection<ThreadAttachmentPresentationViewModel> threadAttachments = [];
    private readonly ObservableCollection<ThreadTurnSummary> threadTurns = [];
    private string threadAttachmentsStatusText = string.Empty;

    private sealed record UnifiedSlashSuggestionSnapshot(
        List<SlashCommandSuggestionDescriptor> Suggestions,
        Dictionary<string, SkillInfo> SkillSelections);

    private readonly record struct PendingReasoningOverride(string Effort, string? RestoreEffort);

    public ChatViewModel(OutputChannel? outputChannel = null, VisualStudioExtensibility? extensibility = null)
        : this(new WorkerBridge(outputChannel), outputChannel, extensibility, autoConnect: true, bridgeFactory: () => new WorkerBridge(outputChannel))
    {
    }

    internal ChatViewModel(
        IWorkerBridge bridge,
        OutputChannel? outputChannel = null,
        VisualStudioExtensibility? extensibility = null,
        bool autoConnect = true,
        IFilePickerService? filePickerService = null,
        IWorkspaceFileSearchService? workspaceFileSearchService = null,
        IProtectedDirectoryPolicy? protectedDirectoryPolicy = null,
        IExtensionSettingsStore? settingsStore = null,
        IExternalLinkOpener? externalLinkOpener = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<IWorkerBridge>? bridgeFactory = null)
    {
        this.bridge = bridge;
        this.bridgeFactory = bridgeFactory ?? (() => new WorkerBridge(outputChannel));
        this.outputChannel = outputChannel;
        this.extensibility = extensibility;
        workspaceDirectoryResolver = new WorkspaceDirectoryResolver(extensibility);
        projectScaffolder = new ProjectScaffolder(extensibility);
        agentsFileInitializer = new AgentsFileInitializer(extensibility);
        this.filePickerService = filePickerService ?? new FilePickerService(extensibility);
        this.workspaceFileSearchService = workspaceFileSearchService ?? new WorkspaceFileSearchService(extensibility);
        this.protectedDirectoryPolicy = protectedDirectoryPolicy ?? new ProtectedDirectoryPolicy();
        this.settingsStore = settingsStore ?? new FileExtensionSettingsStore();
        this.externalLinkOpener = externalLinkOpener ?? new ExternalLinkOpener();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        settings = this.settingsStore.Load();
        remoteProfiles = new RemoteProfilesPresentationViewModel(settings, this.settingsStore, profileOperationGate);
        connectionHealth = new ConnectionHealthPresentationViewModel(markdown);
        connectedProfileName = remoteProfiles.AppliedProfileName;
        slashCommandParser = new SlashCommandParser(slashCommandCatalog);
        SubscribeBridge(bridge);
        // The welcome/empty state is driven by IsThreadEmpty; keep it in sync with every
        // mutation of Items (Add/Clear from any call site) via a single subscription.
        Items.CollectionChanged += OnItemsCollectionChanged;
        ThreadTurns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HistoryTurnSummaryText));
        PendingSkills.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingSkill));
            RaiseCommandStates();
        };
        ConnectCommand = new AsyncCommand(ConnectAsync, () => Status.State is WorkerConnectionState.Disconnected or WorkerConnectionState.Degraded);
        RestartCommand = new AsyncCommand(RestartAsync, () => Status.State == WorkerConnectionState.Degraded);
        NewThreadCommand = new AsyncCommand(NewThreadAsync, () => Status.State == WorkerConnectionState.Ready);
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => initialized && nextCursor is not null);
        LoadOlderHistoryCommand = new AsyncCommand(LoadOlderHistoryAsync, () => SelectedThread is not null && canLoadOlderHistory && !isHistoryLoading && !historyIsStale);
        JoinThreadCommand = new AsyncCommand(JoinSelectedThreadAsync, () => SelectedThread is not null && !IsThreadJoined && !IsRecovering && Status.State == WorkerConnectionState.Ready);
        RefreshHistoryCommand = new AsyncCommand(RefreshSelectedHistoryAsync, () => SelectedThread is not null && historyIsStale && !isHistoryLoading && Status.State == WorkerConnectionState.Ready);
        RefreshAttachmentsCommand = new AsyncCommand(RefreshAttachmentsAsync, () => SelectedThread is not null && Status.State == WorkerConnectionState.Ready);
        SendCommand = new AsyncCommand(SendAsync, CanSend);
        InterruptCommand = new AsyncCommand(InterruptAsync, CanInterruptSelectedThread);
        AccountCommand = new AsyncCommand(ExecuteAccountActionAsync, CanExecuteAccountAction);
        ToggleHistoryCommand = new AsyncCommand(() =>
        {
            IsHistoryOpen = !IsHistoryOpen;
            return Task.CompletedTask;
        });
        CloseHistoryCommand = new AsyncCommand(() =>
        {
            IsHistoryOpen = false;
            return Task.CompletedTask;
        });
        ToggleUsageCommand = new AsyncCommand(ToggleUsageAsync, () => IsUsageAvailable);
        CloseUsageCommand = new AsyncCommand(() =>
        {
            IsUsageOpen = false;
            return Task.CompletedTask;
        });
        ToggleConnectionTargetCommand = new AsyncCommand(() =>
        {
            IsConnectionTargetOpen = !IsConnectionTargetOpen;
            return Task.CompletedTask;
        });
        CloseConnectionTargetCommand = new AsyncCommand(() =>
        {
            IsConnectionTargetOpen = false;
            return Task.CompletedTask;
        });
        ApplyRemoteProfileCommand = new AsyncCommand(ApplyRemoteProfileAsync, () => CanReconnectForProfile() && remoteProfiles.HasSelection);
        UseLocalAppServerCommand = new AsyncCommand(UseLocalAppServerAsync, CanReconnectForProfile);
        CheckProfileHealthCommand = new AsyncCommand(CheckProfileHealthAsync, CanCheckProfileHealth);
        remoteProfiles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RemoteProfilesPresentationViewModel.SelectedProfile))
            {
                ApplyRemoteProfileCommand.RaiseCanExecuteChanged();

                // The diagnostic profile is the selection; a different selection clears it.
                connectionHealth.Clear();
                ObserveHealthProfile(remoteProfiles.SelectedProfile);
            }
        };
        remoteProfiles.SavedProfilesChanged += (_, _) =>
        {
            connectionHealth.Clear();
            CheckProfileHealthCommand.RaiseCanExecuteChanged();
        };
        ObserveHealthProfile(remoteProfiles.SelectedProfile);
        OpenUsageDashboardCommand = new AsyncCommand(() => OpenExternalLinkAsync(ExternalLinkTarget.UsageDashboard));
        OpenUsageHelpCommand = new AsyncCommand(() => OpenExternalLinkAsync(ExternalLinkTarget.UsageHelp));
        AttachCommand = new AsyncCommand(AttachAsync);
        RestoreQuarantinedDraftCommand = new AsyncCommand(RestoreQuarantinedDraftAsync, CanRestoreQuarantinedDraft);
        DiscardQuarantinedDraftCommand = new AsyncCommand(DiscardQuarantinedDraftAsync, () => QuarantinedDraft is not null);
        ConfirmApprovalModeCommand = new AsyncCommand(ConfirmApprovalModeAsync, () => HasApprovalModeConfirmation);
        CancelApprovalModeCommand = new AsyncCommand(CancelApprovalModeAsync, () => HasApprovalModeConfirmation);
        SlashCommands.Configure(OnSlashSuggestionAcceptedAsync, ExecuteSlashSubmissionAsync, OnSlashCommandClearedAsync);
        FileSuggestions.Configure(OnFileSuggestionAcceptedAsync);

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
        ReasoningEfforts = [];
        RefreshReasoningEfforts();
        ServiceTiers = [];
        RefreshServiceTiers();

        ApprovalModes = ApprovalModeCatalog.CreateBuiltIns();
        selectedApprovalMode = FindApprovalMode(ApprovalModeCatalog.CustomId);
        ApprovalModeOption? configuredApprovalMode = FindApprovalMode(settings.ApprovalModeId);
        bool isPendingPermissionProfile = settings.ApprovalModeId?.StartsWith("permission:", StringComparison.Ordinal) == true;
        if (configuredApprovalMode is null && isPendingPermissionProfile)
        {
            string rawPermissionId = settings.ApprovalModeId!["permission:".Length..];
            if (IsValidPermissionProfileId(rawPermissionId))
            {
                string safeId = markdown.ToSafeText(rawPermissionId).Trim();
                configuredApprovalMode = new ApprovalModeOption(
                    settings.ApprovalModeId,
                    $"Permission: {safeId}",
                    "Loading this Codex permission profile.",
                    null,
                    null,
                    null,
                    "Loading",
                    rawPermissionId);
                ApprovalModes.Add(configuredApprovalMode);
            }
            else
            {
                isPendingPermissionProfile = false;
            }
        }

        ApprovalModeOption desiredApprovalMode = configuredApprovalMode
            ?? selectedApprovalMode
            ?? throw new InvalidOperationException("The built-in Custom approval mode is missing.");
        if (configuredApprovalMode is null && !isPendingPermissionProfile)
        {
            settings.ApprovalModeId = ApprovalModeCatalog.CustomId;
            this.settingsStore.Save(settings);
        }
        if (string.Equals(desiredApprovalMode.Id, ApprovalModeCatalog.FullAccessId, StringComparison.Ordinal))
        {
            selectedApprovalMode = FindApprovalMode(ApprovalModeCatalog.CustomId);
            approvalModeBeforeConfirmationId = ApprovalModeCatalog.CustomId;
            BeginApprovalModeConfirmation(desiredApprovalMode, startsNewThread: false);
        }
        else
        {
            selectedApprovalMode = desiredApprovalMode;
        }

        if (autoConnect)
        {
            _ = TryAutoConnectAsync();
        }
    }

    [DataMember]
    public RemoteProfilesPresentationViewModel RemoteProfiles => remoteProfiles;

    [DataMember]
    public ConnectionHealthPresentationViewModel ConnectionHealth => connectionHealth;

    [DataMember]
    public AsyncCommand CheckProfileHealthCommand { get; }

    private bool IsRemoteTarget => Status.Target?.Kind == ConnectionTargetKind.Remote;

    // Local Restart replaces the Worker-owned process; remote Reconnect closes and reopens only
    // this window's socket and never stops the externally managed server.
    [DataMember]
    public string RestartActionText => IsRemoteTarget ? "Reconnect remote app-server" : "Restart local app-server";

    [DataMember]
    public string RestartActionHelpText => IsRemoteTarget
        ? "Closes and reopens this window's connection to the remote app-server you run. The remote server itself is not restarted."
        : "Stops and restarts the local codex app-server process that this window owns.";

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

    // Opt-in structured API support. Natural-language confirmation/choice prompts are detected
    // locally regardless of this flag; this setting only asks codex to expose experimental
    // request_user_input APIs on the next connect.
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
            settingsStore.Save(settings);
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
    public string StatusStateText => Status.State.ToString();

    [DataMember]
    public bool IsRecovering
    {
        get => isRecovering;
        private set
        {
            if (SetProperty(ref isRecovering, value))
            {
                OnPropertyChanged(nameof(HasRecoveryStatus));
                RaiseCommandStates();
            }
        }
    }

    [DataMember]
    public string RecoveryStatusText
    {
        get => recoveryStatusText;
        private set
        {
            if (SetProperty(ref recoveryStatusText, value))
            {
                OnPropertyChanged(nameof(HasRecoveryStatus));
            }
        }
    }

    [DataMember]
    public bool HasRecoveryStatus => IsRecovering || !string.IsNullOrWhiteSpace(RecoveryStatusText);

    [DataMember]
    public RecoveryDraftViewModel? QuarantinedDraft
    {
        get => quarantinedDraft;
        private set
        {
            if (SetProperty(ref quarantinedDraft, value))
            {
                OnPropertyChanged(nameof(HasQuarantinedDraft));
                RestoreQuarantinedDraftCommand?.RaiseCanExecuteChanged();
                DiscardQuarantinedDraftCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public bool HasQuarantinedDraft => QuarantinedDraft is not null;

    [DataMember]
    public AsyncCommand RestoreQuarantinedDraftCommand { get; }

    [DataMember]
    public AsyncCommand DiscardQuarantinedDraftCommand { get; }

    [DataMember]
    public string StatusVersionText
    {
        get
        {
            string version = GetVisibleCodexVersion();
            return version.Length == 0 ? string.Empty : $"\u00b7 Codex {version}";
        }
    }

    [DataMember]
    public string StatusAutomationName
    {
        get
        {
            string version = GetVisibleCodexVersion();
            return version.Length == 0
                ? StatusStateText
                : $"{StatusStateText}, Codex version {version}";
        }
    }

    [DataMember]
    public string StatusAutomationHelpText
    {
        get
        {
            string message = ToSafeHeaderText(Status.Message);
            return message.Length == 0 ? "Codex connection status." : message;
        }
    }

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
            long? previousGeneration = status.Target?.Generation;
            string? previousPartition = observedStatePartitionFingerprint;
            long previousOwnerGeneration = observedOwnerGeneration;
            if (SetProperty(ref status, value))
            {
                string? currentPartition = value.Target?.StatePartitionFingerprint;
                long currentOwnerGeneration = value.Target?.OwnerGeneration ?? 0;
                observedStatePartitionFingerprint = currentPartition;
                observedOwnerGeneration = currentOwnerGeneration;
                if (!string.Equals(previousPartition, currentPartition, StringComparison.Ordinal)
                    || previousOwnerGeneration != currentOwnerGeneration)
                {
                    if (QuarantinedDraft is null && HasDraftContent())
                    {
                        QuarantinedDraft = CaptureRecoveryDraft();
                    }

                    ClearOwnerScopedState();
                }

                UpdateUsageConnectionLifecycle(value.State);

                // A new connection generation makes any outstanding health result stale.
                if (previousGeneration != value.Target?.Generation)
                {
                    connectionHealth.Clear();
                }
                else
                {
                    connectionHealth.UpdateRpc(value, StatusStateText);
                }

                OnPropertyChanged(nameof(RestartActionText));
                OnPropertyChanged(nameof(RestartActionHelpText));
                RaiseCommandStates();
                OnPropertyChanged(nameof(IsDegraded));
                OnPropertyChanged(nameof(IsTurnActive));
                OnPropertyChanged(nameof(SendButtonText));
                OnPropertyChanged(nameof(StatusDetailText));
                OnPropertyChanged(nameof(ConnectionTargetLabel));
                OnPropertyChanged(nameof(StatusStateText));
                OnPropertyChanged(nameof(StatusVersionText));
                OnPropertyChanged(nameof(StatusAutomationName));
                OnPropertyChanged(nameof(StatusAutomationHelpText));
                OnPropertyChanged(nameof(EffectiveApprovalModeText));
                OnPropertyChanged(nameof(IsUsageAvailable));
            }
        }
    }

    private string ToSafeHeaderText(string? value)
        => HeaderWhitespace.Replace(markdown.ToSafeText(value ?? string.Empty), " ").Trim();

    private OwnerSnapshot CaptureOwnerSnapshot()
    {
        ConnectionTargetSnapshot? target = Status.Target;
        return new OwnerSnapshot(
            target?.StatePartitionFingerprint,
            target?.OwnerGeneration ?? 0,
            target?.Generation ?? 0);
    }

    private bool IsCurrentOwner(OwnerSnapshot snapshot)
    {
        ConnectionTargetSnapshot? target = Status.Target;
        return snapshot.OwnerGeneration == (target?.OwnerGeneration ?? 0)
            && snapshot.ConnectionGeneration == (target?.Generation ?? 0)
            && string.Equals(
                snapshot.StatePartitionFingerprint,
                target?.StatePartitionFingerprint,
                StringComparison.Ordinal);
    }

    private bool IsNotificationCurrent<T>(WorkerNotification<T> notification)
        => IsCurrentOwner(new OwnerSnapshot(
            notification.StatePartitionFingerprint,
            notification.OwnerGeneration,
            notification.ConnectionGeneration));

    private static OwnerSnapshot NotificationOwner<T>(WorkerNotification<T> notification)
        => new(
            notification.StatePartitionFingerprint,
            notification.OwnerGeneration,
            notification.ConnectionGeneration);

    private void SubscribeBridge(IWorkerBridge source)
    {
        bridgeConnectionLostHandler = failureKind =>
        {
            if (ReferenceEquals(source, bridge))
            {
                _ = HandleConnectionLostAsync(source, failureKind);
            }
        };
        source.ConnectionLost += bridgeConnectionLostHandler;
        source.StateChanged += OnStateChangedAsync;
        source.AccountChanged += OnAccountChangedAsync;
        source.ConversationEventReceived += OnConversationEventAsync;
        source.ThreadAttachmentUpdated += OnThreadAttachmentUpdatedAsync;
        source.ApprovalRequested += OnApprovalRequestedAsync;
        source.ApprovalResolved += OnApprovalResolvedAsync;
        source.UserInputRequested += OnUserInputRequestedAsync;
        source.UserInputResolved += OnUserInputResolvedAsync;
        source.ContextCompacted += OnContextCompactedAsync;
        source.ReviewModeChanged += OnReviewModeChangedAsync;
        source.ThreadGoalChanged += OnThreadGoalChangedAsync;
        source.RateLimitsChanged += OnRateLimitsChangedAsync;
        source.SkillsChanged += OnSkillsChangedAsync;
        source.ApprovalAuditReceived += OnApprovalAuditReceivedAsync;
    }

    private void UnsubscribeBridge(IWorkerBridge source)
    {
        if (bridgeConnectionLostHandler is not null)
        {
            source.ConnectionLost -= bridgeConnectionLostHandler;
            bridgeConnectionLostHandler = null;
        }
        source.StateChanged -= OnStateChangedAsync;
        source.AccountChanged -= OnAccountChangedAsync;
        source.ConversationEventReceived -= OnConversationEventAsync;
        source.ThreadAttachmentUpdated -= OnThreadAttachmentUpdatedAsync;
        source.ApprovalRequested -= OnApprovalRequestedAsync;
        source.ApprovalResolved -= OnApprovalResolvedAsync;
        source.UserInputRequested -= OnUserInputRequestedAsync;
        source.UserInputResolved -= OnUserInputResolvedAsync;
        source.ContextCompacted -= OnContextCompactedAsync;
        source.ReviewModeChanged -= OnReviewModeChangedAsync;
        source.ThreadGoalChanged -= OnThreadGoalChangedAsync;
        source.RateLimitsChanged -= OnRateLimitsChangedAsync;
        source.SkillsChanged -= OnSkillsChangedAsync;
        source.ApprovalAuditReceived -= OnApprovalAuditReceivedAsync;
    }

    private async Task HandleConnectionLostAsync(IWorkerBridge source, WorkerRecoveryFailureKind failureKind)
    {
        if (!ConnectionRecoveryCoordinator.IsTransient(failureKind) || lifetime.IsCancellationRequested)
        {
            await OnUiAsync(() => RecoveryStatusText = GetRecoveryFailureText(failureKind)).ConfigureAwait(false);
            return;
        }

        string? root = workingDirectory;
        string? profileName = connectedProfileName;
        string? fingerprint = profileName is null
            ? null
            : settings.RemoteProfiles.FirstOrDefault(profile => string.Equals(profile.Name, profileName, StringComparison.Ordinal))?.ComputeFingerprint();
        bool experimentalApi = settings.ExperimentalApiEnabled;
        bool started = false;
        await OnUiAsync(() =>
        {
            if (!ReferenceEquals(source, bridge) || IsRecovering)
            {
                return;
            }

            if (QuarantinedDraft is null && HasDraftContent())
            {
                QuarantinedDraft = CaptureRecoveryDraft();
            }

            ChatItemViewModel[] uncertainOperations = inFlightMutations.Values
                .Concat(Items.Where(static item => item.IsDeliveryUnknown))
                .Distinct()
                .ToArray();
            foreach (ChatItemViewModel item in uncertainOperations)
            {
                item.MarkDeliveryUnknown();
            }
            inFlightMutations.Clear();

            ClearOwnerScopedState();
            if (uncertainOperations.Length > 0)
            {
                Items.Add(new ChatItemViewModel(
                    "Status",
                    $"{uncertainOperations.Length} unresolved operation(s) have an unknown delivery outcome. They were not retried.",
                    ConversationEventKind.Error));
            }
            IsRecovering = true;
            RecoveryStatusText = "Connection lost. Reconnecting and verifying the active target…";
            recoveryCancellation?.Cancel();
            recoveryCancellation?.Dispose();
            recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            started = true;
        }).ConfigureAwait(false);

        if (!started)
        {
            return;
        }

        CancellationTokenSource? cancellation = recoveryCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested || root is null)
        {
            await FinishRecoveryAsync(new ConnectionRecoveryResult(ConnectionRecoveryOutcome.Stopped, 0, WorkerRecoveryFailureKind.RootChanged)).ConfigureAwait(false);
            return;
        }

        ConnectionRecoveryResult result = await ConnectionRecoveryCoordinator.RunAsync(
            async (_, token) => await RecoverAttemptAsync(root, profileName, fingerprint, experimentalApi, token).ConfigureAwait(false),
            token => IsRecoveryTargetValidAsync(root, profileName, fingerprint, experimentalApi, token),
            cancellation.Token).ConfigureAwait(false);
        await FinishRecoveryAsync(result).ConfigureAwait(false);
    }

    private bool HasDraftContent()
        => ComposerText.Length > 0 || HasPendingAttachments || HasPendingSkill
            || nextPersonality is not null || nextCollaborationMode is not null || inFlightMutations.Count > 0
            || Items.Any(static item => item.IsDeliveryUnknown);

    private RecoveryDraftViewModel CaptureRecoveryDraft()
    {
        string[] attachmentPaths = PendingAttachments.Select(static attachment => attachment.FullPath).ToArray();
        SkillInvocationInfo? skill = PendingSkills.FirstOrDefault()?.Invocation;
        SkillInvocationInfo? skillCopy = skill is null ? null : new SkillInvocationInfo { Name = skill.Name, Scope = skill.Scope, Path = skill.Path };
        bool uncertain = inFlightMutations.Count > 0 || Items.Any(static item => item.IsDeliveryUnknown);
        return new RecoveryDraftViewModel(
            ComposerText,
            attachmentPaths.Select(static path => Path.GetFileName(path) ?? "Attachment").ToArray(),
            attachmentPaths,
            selectedModel,
            selectedReasoningEffortId,
            selectedServiceTierId,
            nextPersonality,
            nextCollaborationMode,
            skillCopy,
            uncertain,
            RestoreQuarantinedDraftCommand,
            DiscardQuarantinedDraftCommand,
            markdown);
    }

    private async Task<WorkerStatus> RecoverAttemptAsync(
        string root,
        string? profileName,
        string? fingerprint,
        bool experimentalApi,
        CancellationToken cancellationToken)
    {
        await profileOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRecoveryTargetValid(root, profileName, fingerprint, experimentalApi))
            {
                return new WorkerStatus { State = WorkerConnectionState.Degraded, RecoveryFailureKind = WorkerRecoveryFailureKind.ProfileChanged };
            }

            IWorkerBridge previous = bridge;
            UnsubscribeBridge(previous);
            bridge = bridgeFactory();
            SubscribeBridge(bridge);
            await previous.DisposeAsync().ConfigureAwait(false);

            WorkerStatus result;
            try
            {
                result = await bridge.ConnectAsync(root, experimentalApi, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ExtensionDiagnostics.Write("Transient Worker recovery attempt failed", ex);
                return new WorkerStatus { State = WorkerConnectionState.Degraded, RecoveryFailureKind = WorkerRecoveryFailureKind.PeerUnresponsive };
            }

            await OnUiAsync(() =>
            {
                if (CanApplyStatusSnapshot(result))
                {
                    Status = result;
                }
            }).ConfigureAwait(false);
            if (result.State != WorkerConnectionState.Ready)
            {
                return result;
            }

            workingDirectory = root;
            return result;
        }
        finally
        {
            profileOperationGate.Release();
        }
    }

    private async Task<bool> IsRecoveryTargetValidAsync(
        string root,
        string? profileName,
        string? fingerprint,
        bool experimentalApi,
        CancellationToken cancellationToken)
    {
        await profileOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return IsRecoveryTargetValid(root, profileName, fingerprint, experimentalApi);
        }
        finally
        {
            profileOperationGate.Release();
        }
    }

    private bool IsRecoveryTargetValid(string root, string? profileName, string? fingerprint, bool experimentalApi)
    {
        if (!string.Equals(workingDirectory, root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(remoteProfiles.AppliedProfileName, profileName, StringComparison.Ordinal)
            || settings.ExperimentalApiEnabled != experimentalApi)
        {
            return false;
        }

        if (profileName is null)
        {
            return fingerprint is null;
        }

        RemoteConnectionProfile? applied = settings.RemoteProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, profileName, StringComparison.Ordinal));
        return applied is not null
            && string.Equals(applied.ComputeFingerprint(), fingerprint, StringComparison.Ordinal);
    }

    private async Task FinishRecoveryAsync(ConnectionRecoveryResult result)
    {
        if (result.Outcome == ConnectionRecoveryOutcome.Reconnected)
        {
            using var synchronizationDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            synchronizationDeadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await SynchronizeRecoveredThreadListAsync(synchronizationDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (synchronizationDeadline.IsCancellationRequested)
            {
                await OnUiAsync(() => MarkThreadListStale("Connection restored, but thread synchronization timed out. Refresh the thread list manually.")).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExtensionDiagnostics.Write("Recovered connection state synchronization failed", ex);
                await OnUiAsync(() => MarkThreadListStale("Connection restored, but thread history could not be synchronized. Refresh manually.")).ConfigureAwait(false);
            }
        }

        await OnUiAsync(() =>
        {
            IsRecovering = false;
            RecoveryStatusText = result.Outcome switch
            {
                ConnectionRecoveryOutcome.Reconnected => "Connection restored. Choose a conversation to inspect its history, then Join explicitly before sending.",
                ConnectionRecoveryOutcome.Cancelled => string.Empty,
                _ => GetRecoveryFailureText(result.LastFailureKind),
            };
            RaiseCommandStates();
        }).ConfigureAwait(false);
    }

    private async Task SynchronizeRecoveredThreadListAsync(CancellationToken cancellationToken)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        ThreadPage page = await bridge.ListThreadsAsync(
            StampOwner(new ListThreadsRequest(), owner), cancellationToken).ConfigureAwait(false);
        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner) || !MatchesOwner(page, owner))
            {
                return;
            }

            Threads.Clear();
            foreach (ThreadSummary thread in page.Threads)
            {
                Threads.Add(thread);
            }

            nextCursor = page.NextCursor;
            initialized = true;
            HistoryStatusText = "Conversation list synchronized. Select one to inspect read-only history.";
            RaiseCommandStates();
        }).ConfigureAwait(false);
    }

    private void MarkThreadListStale(string message)
    {
        initialized = false;
        RecoveryStatusText = message;
        RaiseCommandStates();
    }

    private static string GetRecoveryFailureText(WorkerRecoveryFailureKind failureKind) => failureKind switch
    {
        WorkerRecoveryFailureKind.AuthenticationRejected => "Reconnection requires sign-in or updated credentials.",
        WorkerRecoveryFailureKind.TlsRejected => "The server identity could not be verified. Reconnection stopped.",
        WorkerRecoveryFailureKind.ProfileChanged => "The connection profile changed. Review the target and connect manually.",
        WorkerRecoveryFailureKind.RootChanged => "The working directory changed. Choose a working directory and connect manually.",
        WorkerRecoveryFailureKind.OwnerChanged => "The app-server owner changed. Select a conversation to inspect history; no conversation was resumed.",
        WorkerRecoveryFailureKind.ConfigurationChanged => "The connection configuration changed. Review it and connect manually.",
        WorkerRecoveryFailureKind.None => "Reconnection stopped. Connect manually to continue.",
        _ => "Automatic reconnection stopped. Check the connection and connect manually.",
    };

    private async Task RestoreQuarantinedDraftAsync()
    {
        RecoveryDraftViewModel? draft = QuarantinedDraft;
        if (draft is null || SelectedThread is null || !IsThreadJoined || IsRecovering)
        {
            return;
        }

        int requestedAttachments = draft.AttachmentPaths.Count;
        int restoredAttachments = 0;
        bool skillRestoreFailed = false;
        bool settingsRestoreFailed = false;
        await OnUiAsync(() =>
        {
            SetComposerText(draft.Text);
            foreach (string path in draft.AttachmentPaths)
            {
                if (TryAddPendingAttachment(path))
                {
                    restoredAttachments++;
                }
            }

            if (draft.Model is not null && modelCatalog.Models.Any(model => string.Equals(model.Id, draft.Model, StringComparison.Ordinal)))
            {
                SelectedModel = draft.Model;
            }
            else if (draft.Model is not null)
            {
                settingsRestoreFailed = true;
            }

            if (draft.ReasoningEffort is not null && FindReasoningEffort(draft.ReasoningEffort) is not null)
            {
                SelectedReasoningEffortId = draft.ReasoningEffort;
            }
            else if (draft.ReasoningEffort is not null)
            {
                settingsRestoreFailed = true;
            }

            if (draft.ServiceTier is not null && ServiceTiers.Any(option => string.Equals(option.Id, draft.ServiceTier, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedServiceTierId = draft.ServiceTier;
            }
            else if (draft.ServiceTier is not null)
            {
                settingsRestoreFailed = true;
            }

            if (draft.Personality is not null)
            {
                if (GetSelectedModelInfo()?.SupportsPersonality == true
                    && (draft.Personality is "none" or "friendly" or "pragmatic"))
                {
                    nextPersonality = draft.Personality;
                }
                else
                {
                    settingsRestoreFailed = true;
                }
            }

            if (draft.CollaborationMode is not null)
            {
                if (string.Equals(draft.CollaborationMode, "plan", StringComparison.Ordinal))
                {
                    nextCollaborationMode = draft.CollaborationMode;
                }
                else
                {
                    settingsRestoreFailed = true;
                }
            }
            if (draft.Skill is { } skill
                && skillsSnapshot?.Skills.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, skill.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.Scope, skill.Scope, StringComparison.Ordinal)
                    && string.Equals(candidate.Path, skill.Path, StringComparison.Ordinal)) is { } currentSkill)
            {
                PendingSkills.Clear();
                PendingSkills.Add(new PendingSkillViewModel(
                    string.IsNullOrWhiteSpace(currentSkill.DisplayName) ? currentSkill.Name : currentSkill.DisplayName!,
                    FormatSkillScope(currentSkill.Scope),
                    currentSkill.Description,
                    new SkillInvocationInfo { Name = currentSkill.Name, Scope = currentSkill.Scope, Path = currentSkill.Path },
                    RemovePendingSkillAsync,
                    currentSkill.DefaultPrompt,
                    UsePendingSkillPromptAsync,
                    markdown));
            }
            else if (draft.Skill is not null)
            {
                skillRestoreFailed = true;
            }

            QuarantinedDraft = null;
            RecoveryStatusText = requestedAttachments != restoredAttachments || skillRestoreFailed || settingsRestoreFailed
                ? "Draft text restored. Some attachments, selected skill, or settings were unavailable and must be reviewed and restored manually."
                : "Draft restored to the composer. Review it before sending.";
        }).ConfigureAwait(false);
    }

    private bool CanRestoreQuarantinedDraft()
        => QuarantinedDraft is not null
            && !IsRecovering
            && SelectedThread is not null
            && IsThreadJoined
            && string.IsNullOrWhiteSpace(ComposerText)
            && !HasPendingAttachments
            && !HasPendingSkill;

    private Task DiscardQuarantinedDraftAsync()
    {
        QuarantinedDraft = null;
        RecoveryStatusText = "Quarantined draft discarded.";
        return Task.CompletedTask;
    }

    private async Task<bool> RunTrackedMutationAsync(string operationName, OwnerSnapshot owner, Func<Task> dispatch)
    {
        var operationRow = new ChatItemViewModel("Status", $"{operationName} request sent.", ConversationEventKind.ItemStarted)
        {
            ThreadId = SelectedThread?.Id,
            TurnId = Status.TurnId,
            OwnerGeneration = owner.OwnerGeneration,
            ConnectionGeneration = owner.ConnectionGeneration,
        };
        Guid operationId = Guid.NewGuid();
        await OnUiAsync(() =>
        {
            if (IsCurrentOwner(owner))
            {
                Items.Add(operationRow);
                inFlightMutations[operationId] = operationRow;
            }
        }).ConfigureAwait(false);

        try
        {
            await dispatch().ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                inFlightMutations.Remove(operationId);
                Items.Remove(operationRow);
            }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await OnUiAsync(() =>
            {
                if (inFlightMutations.Remove(operationId))
                {
                    operationRow.MarkDeliveryUnknown();
                }
            }).ConfigureAwait(false);
            if (IsCurrentOwner(owner))
            {
                RecoveryStatusText = $"{operationName} outcome is unknown. It was not retried; review the current state before trying again.";
            }
            ExtensionDiagnostics.Write($"{operationName} outcome is unknown after dispatch", ex);
            return false;
        }
    }

    private T StampOwner<T>(T request) where T : OwnerScopedRequest
    {
        return StampOwner(request, CaptureOwnerSnapshot());
    }

    private static T StampOwner<T>(T request, OwnerSnapshot owner) where T : OwnerScopedRequest
    {
        request.StatePartitionFingerprint = owner.StatePartitionFingerprint;
        request.OwnerGeneration = owner.OwnerGeneration;
        request.ConnectionGeneration = owner.ConnectionGeneration;
        return request;
    }

    private bool CanApplyStatusNotification(WorkerNotification<WorkerStatus> notification)
        => CanApplyStatusOwner(notification.StatePartitionFingerprint, notification.OwnerGeneration, notification.ConnectionGeneration);

    private bool CanApplyStatusSnapshot(WorkerStatus snapshot)
        => CanApplyStatusOwner(
            snapshot.Target?.StatePartitionFingerprint,
            snapshot.Target?.OwnerGeneration ?? 0,
            snapshot.Target?.Generation ?? 0);

    private bool CanApplyStatusOwner(string? fingerprint, long ownerGeneration, long connectionGeneration)
    {
        OwnerSnapshot current = CaptureOwnerSnapshot();
        if (ownerGeneration < current.OwnerGeneration)
        {
            return false;
        }

        if (ownerGeneration == current.OwnerGeneration
            && !string.Equals(fingerprint, current.StatePartitionFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        return ownerGeneration > current.OwnerGeneration
            || connectionGeneration >= current.ConnectionGeneration;
    }

    private readonly record struct OwnerSnapshot(
        string? StatePartitionFingerprint,
        long OwnerGeneration,
        long ConnectionGeneration);

    private void ClearOwnerScopedState()
    {
        slashCommandCoordinator.CancelAll();
        CancelFileSuggestionRefresh();
        SlashCommands.ClearForOwnerChange();
        FileSuggestions.CloseSuggestions();
        FileSuggestions.Suggestions.Clear();
        unavailableSlashCommands.Clear();
        skillsSnapshot = null;
        skillsSnapshotExpiresAt = default;
        skillsLoadTask = null;
        skillsLoadFailure = null;
        skillsRefreshPending = false;
        minimumSkillsGeneration = 0;
        skillSelectionSequence++;
        skillSelections.Clear();
        agentRawText.Clear();
        itemRawText.Clear();
        lastAgentRawKey = null;
        pendingReasoningByThread.Clear();
        reasoningRestoreByThread.Clear();
        pendingServiceTierByThread.Clear();
        serviceTierRestoreByThread.Clear();
        nextPersonality = null;
        nextCollaborationMode = null;
        initialized = false;
        nextCursor = null;

        selectedThread = null;
        OnPropertyChanged(nameof(SelectedThread));
        IsThreadJoined = false;
        IsHistoryStale = false;
        HistoryStatusText = string.Empty;
        CanLoadOlderHistory = false;
        threadTurnsCursor = null;
        threadItemsCursor = null;
        historyItemKeys.Clear();
        historyBytes = 0;
        pendingHistoryEvents.Clear();
        bufferedEventCount = 0;
        bufferedEventBytes = 0;
        ThreadAttachments.Clear();
        ThreadAttachmentsStatusText = string.Empty;
        ThreadTurns.Clear();
        OnPropertyChanged(nameof(EffectiveApprovalModeText));
        Threads.Clear();
        Items.Clear();
        inFlightMutations.Clear();
        SetComposerText(string.Empty);
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasPendingAttachments));
        PendingSkills.Clear();
        OnPropertyChanged(nameof(HasPendingSkill));

        ActiveApproval?.MarkResolved();
        ActiveApproval = null;
        approvalQueue.Clear();
        OnPropertyChanged(nameof(ApprovalQueueText));
        ActiveUserInput?.MarkResolved();
        ActiveUserInput = null;
        userInputQueue.Clear();
        OnPropertyChanged(nameof(UserInputQueueText));

        ClearApprovalModeConfirmation();
        for (int index = ApprovalModes.Count - 1; index >= 0; index--)
        {
            if (ApprovalModes[index].Source is "PermissionProfile" or "Loading")
            {
                ApprovalModes.RemoveAt(index);
            }
        }

        if (settings.ApprovalModeId.StartsWith("permission:", StringComparison.Ordinal))
        {
            ApplyApprovalMode(FindApprovalMode(ApprovalModeCatalog.CustomId)!);
        }

        IsHistoryOpen = false;
        IsUsageOpen = false;
        InvalidateUsage();
        Interlocked.Increment(ref usageConnectionGeneration);
        usageConnectionActive = false;
        modelCatalog = new ListModelsResult();
        Models.Clear();
        Models.Add("gpt-5-codex");
        Models.Add("gpt-5");
        selectedModel = Models[0];
        OnPropertyChanged(nameof(SelectedModel));
        RefreshReasoningEfforts();
        RefreshServiceTiers();
        UpdateAccount(new AccountStatus { State = AccountState.Checking });
        RaiseCommandStates();
    }

    private string GetVisibleCodexVersion()
        => Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval
            ? ToSafeHeaderText(Status.CodexVersion)
            : string.Empty;

    [DataMember]
    public ThreadSummary? SelectedThread
    {
        get => selectedThread;
        set
        {
            if (SetProperty(ref selectedThread, value))
            {
                OnPropertyChanged(nameof(EffectiveApprovalModeText));
                OnPropertyChanged(nameof(IsHistoryOnly));
                OnPropertyChanged(nameof(HasHistoryNotice));
                if (value is not null)
                {
                    IsThreadJoined = false;
                    Items.Clear();
                    ThreadAttachments.Clear();
                    ThreadAttachmentsStatusText = string.Empty;
                    ThreadTurns.Clear();
                    deletedAttachmentIds.Clear();
                    IsHistoryStale = false;
                    HistoryStatusText = "Loading read-only history…";
                    CanLoadOlderHistory = false;
                    historyItemKeys.Clear();
                    historyBytes = 0;
                    threadTurnsCursor = null;
                    threadItemsCursor = null;
                    _ = ReadSelectedThreadHistoryAsync(value);
                    _ = DrainSlashQueuesAsync(value.Id);
                }
                else
                {
                    IsThreadJoined = false;
                    Items.Clear();
                    ThreadAttachments.Clear();
                    ThreadAttachmentsStatusText = string.Empty;
                    ThreadTurns.Clear();
                }
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
            UpdateComposerSuggestions(value);

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
        UpdateComposerSuggestions(value);
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

    // Two-way bound to the thread-history Popup.IsOpen. The History, Usage, and connection-target
    // flyouts are mutually exclusive.
    [DataMember]
    public bool IsHistoryOpen
    {
        get => isHistoryOpen;
        set
        {
            if (SetProperty(ref isHistoryOpen, value) && value)
            {
                CloseOtherFlyouts(nameof(IsHistoryOpen));
            }
        }
    }

    [DataMember]
    public bool IsUsageOpen
    {
        get => isUsageOpen;
        set
        {
            if (SetProperty(ref isUsageOpen, value) && value)
            {
                CloseOtherFlyouts(nameof(IsUsageOpen));
            }
        }
    }

    [DataMember]
    public bool IsConnectionTargetOpen
    {
        get => isConnectionTargetOpen;
        set
        {
            if (SetProperty(ref isConnectionTargetOpen, value) && value)
            {
                CloseOtherFlyouts(nameof(IsConnectionTargetOpen));
            }
        }
    }

    // Where turns run: the connected remote profile, or the local app-server. This reflects the
    // connection, not the row being edited in the flyout.
    [DataMember]
    public string ConnectionTargetText => connectedProfileName is null ? "Local" : markdown.ToSafeText(connectedProfileName).Trim();

    // Only a live RPC connection is labeled as connected; otherwise the flyout names the intended
    // target (Disconnected, Connecting, Degraded).
    [DataMember]
    public string ConnectionTargetLabel
        => Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval
            ? "Connected to:"
            : "Target:";

    [DataMember]
    public string ConnectionTargetAutomationName => connectedProfileName is null
        ? "Connection target: local codex app-server"
        : $"Connection target: remote profile {markdown.ToSafeText(connectedProfileName).Trim()}";

    [DataMember]
    public AsyncCommand ToggleConnectionTargetCommand { get; }

    [DataMember]
    public AsyncCommand CloseConnectionTargetCommand { get; }

    [DataMember]
    public AsyncCommand ApplyRemoteProfileCommand { get; }

    [DataMember]
    public AsyncCommand UseLocalAppServerCommand { get; }

    private void CloseOtherFlyouts(string keep)
    {
        if (keep != nameof(IsHistoryOpen) && isHistoryOpen)
        {
            isHistoryOpen = false;
            OnPropertyChanged(nameof(IsHistoryOpen));
        }

        if (keep != nameof(IsUsageOpen) && isUsageOpen)
        {
            isUsageOpen = false;
            OnPropertyChanged(nameof(IsUsageOpen));
        }

        if (keep != nameof(IsConnectionTargetOpen) && isConnectionTargetOpen)
        {
            isConnectionTargetOpen = false;
            OnPropertyChanged(nameof(IsConnectionTargetOpen));
        }
    }

    [DataMember]
    public bool IsUsageAvailable
        => Account.IsSignedIn && IsConnectedState(Status.State);

    [DataMember]
    public UsagePresentation Usage { get; } = new();

    [DataMember]
    public ObservableCollection<SuggestionChip> Suggestions { get; }

    [DataMember]
    public SlashCommandPresentationViewModel SlashCommands { get; } = new();

    [DataMember]
    public FileSuggestionPresentationViewModel FileSuggestions { get; } = new();

    [DataMember]
    public ObservableCollection<AttachmentChipViewModel> PendingAttachments { get; } = [];

    [DataMember]
    public ObservableCollection<PendingSkillViewModel> PendingSkills { get; } = [];

    [DataMember]
    public bool HasPendingSkill => PendingSkills.Count > 0;

    [DataMember]
    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    [DataMember]
    public ObservableCollection<string> Models { get; }

    [DataMember]
    public ObservableCollection<ReasoningEffortOption> ReasoningEfforts { get; }

    [DataMember]
    public ObservableCollection<ServiceTierOption> ServiceTiers { get; }

    [DataMember]
    public string SelectedServiceTierId
    {
        get => selectedServiceTierId;
        set
        {
            ServiceTierOption? option = ServiceTiers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, value, StringComparison.OrdinalIgnoreCase));
            if (option is null || !SetProperty(ref selectedServiceTierId, option.Id))
            {
                return;
            }

            settings.ServiceTierId = option.Id;
            settingsStore.Save(settings);
            OnPropertyChanged(nameof(ServiceTierHelpText));
        }
    }

    [DataMember]
    public bool HasServiceTiers => ServiceTiers.Count > 1;

    [DataMember]
    public string ServiceTierHelpText
        => ServiceTiers.FirstOrDefault(option =>
            string.Equals(option.Id, SelectedServiceTierId, StringComparison.OrdinalIgnoreCase))?.AutomationName
        ?? "Inherit the service tier from the Codex configuration.";

    [DataMember]
    public string SelectedReasoningEffortId
    {
        get => selectedReasoningEffortId;
        set
        {
            ReasoningEffortOption? option = FindReasoningEffort(value);
            if (option is null || string.Equals(selectedReasoningEffortId, option.Id, StringComparison.Ordinal))
            {
                return;
            }

            selectedReasoningEffortId = option.Id;
            settings.ReasoningEffortId = option.Id;
            settingsStore.Save(settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReasoningEffortHelpText));
        }
    }

    [DataMember]
    public bool HasReasoningEfforts => ReasoningEfforts.Count > 1;

    [DataMember]
    public string ReasoningEffortHelpText
        => FindReasoningEffort(SelectedReasoningEffortId)?.Description
            ?? "Inherit the reasoning effort from the Codex configuration.";

    [DataMember]
    public string? SelectedModel
    {
        get => selectedModel;
        set
        {
            // The VS-side ComboBox writes null back through the TwoWay SelectedItem binding
            // whenever its ItemsSource momentarily invalidates the current selection. Remote UI
            // delivers that write-back asynchronously, so it can arrive after a newer selection
            // was already set here and would blank the picker. Only ignore the null when the
            // current selection is still a valid entry (the signature of a stale write-back);
            // if the current selection is already invalid, let the null through so the VM does
            // not end up stuck on a value that no longer exists in the list.
            if (value is null && selectedModel is not null && Models.Contains(selectedModel))
            {
                return;
            }

            if (SetProperty(ref selectedModel, value))
            {
                RefreshReasoningEfforts();
                RefreshServiceTiers();
                UpdateComposerSuggestions(ComposerText);
            }
        }
    }

    [DataMember]
    public ObservableCollection<string> Modes { get; } = ["Agent", "Chat"];

    [DataMember]
    public string SelectedMode
    {
        get => selectedMode;
        set
        {
            if (SetProperty(ref selectedMode, value))
            {
                OnPropertyChanged(nameof(IsApprovalModeEnabled));
                OnPropertyChanged(nameof(ApprovalModeHelpText));
            }
        }
    }

    [DataMember]
    public ObservableCollection<ApprovalModeOption> ApprovalModes { get; }

    public ApprovalModeOption? SelectedApprovalMode
    {
        get => selectedApprovalMode;
        set
        {
            if (value is null || ReferenceEquals(value, selectedApprovalMode) || !IsApprovalModeEnabled)
            {
                return;
            }

            RequestApprovalMode(value);
        }
    }

    [DataMember]
    public string SelectedApprovalModeId
    {
        get => SelectedApprovalMode?.Id ?? ApprovalModeCatalog.CustomId;
        set
        {
            ApprovalModeOption? option = FindApprovalMode(value);
            if (option is not null && !ReferenceEquals(option, selectedApprovalMode) && IsApprovalModeEnabled)
            {
                RequestApprovalMode(option);
            }
        }
    }

    [DataMember]
    public bool IsApprovalModeEnabled => string.Equals(SelectedMode, "Agent", StringComparison.Ordinal);

    [DataMember]
    public string ApprovalModeHelpText => IsApprovalModeEnabled
        ? SelectedApprovalMode?.Description ?? "Select the approval and sandbox policy used for Agent turns."
        : "Approval mode is unavailable in Chat mode. Chat uses a read-only sandbox without approval prompts.";

    [DataMember]
    public bool HasApprovalModeConfirmation => pendingApprovalMode is not null;

    [DataMember]
    public string ApprovalModeConfirmationText
    {
        get => approvalModeConfirmationText;
        private set => SetProperty(ref approvalModeConfirmationText, value);
    }

    [DataMember]
    public AsyncCommand ConfirmApprovalModeCommand { get; }

    [DataMember]
    public AsyncCommand CancelApprovalModeCommand { get; }

    [DataMember]
    public string DesiredApprovalModeText
        => FindApprovalMode(settings.ApprovalModeId)?.DisplayText ?? settings.ApprovalModeId;

    [DataMember]
    public string EffectiveApprovalModeText
    {
        get
        {
            EffectiveApprovalState? effective = SelectedThread?.EffectiveApprovalState
                ?? Status.EffectiveApprovalState;
            if (effective is null)
            {
                return "Not reported by the app-server";
            }

            if (!string.IsNullOrWhiteSpace(effective.ActivePermissionProfile))
            {
                return $"Permission profile: {ToSafeHeaderText(effective.ActivePermissionProfile)}";
            }

            return $"approval={ToSafeHeaderText(effective.ApprovalPolicy) switch { "" => "default", string value => value }}, "
                + $"reviewer={ToSafeHeaderText(effective.ApprovalsReviewer) switch { "" => "default", string value => value }}, "
                + $"sandbox={ToSafeHeaderText(effective.SandboxMode) switch { "" => "default", string value => value }}";
        }
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
    public AsyncCommand LoadOlderHistoryCommand { get; }

    [DataMember]
    public AsyncCommand JoinThreadCommand { get; }

    [DataMember]
    public AsyncCommand RefreshHistoryCommand { get; }

    [DataMember]
    public AsyncCommand RefreshAttachmentsCommand { get; }

    [DataMember]
    public bool IsThreadJoined
    {
        get => isThreadJoined;
        private set
        {
            if (SetProperty(ref isThreadJoined, value))
            {
                OnPropertyChanged(nameof(IsHistoryOnly));
                RaiseCommandStates();
            }
        }
    }

    [DataMember]
    public bool IsHistoryOnly => SelectedThread is not null && !IsThreadJoined;

    [DataMember]
    public bool HasHistoryNotice => IsHistoryOnly || IsHistoryStale || !string.IsNullOrWhiteSpace(ThreadAttachmentsStatusText);

    [DataMember]
    public bool IsHistoryStale
    {
        get => historyIsStale;
        private set
        {
            if (SetProperty(ref historyIsStale, value))
            {
                OnPropertyChanged(nameof(HasHistoryNotice));
                OnPropertyChanged(nameof(HistoryStatusText));
                RefreshHistoryCommand.RaiseCanExecuteChanged();
                LoadOlderHistoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public string HistoryStatusText
    {
        get => historyStatusText;
        private set => SetProperty(ref historyStatusText, value);
    }

    [DataMember]
    public bool CanLoadOlderHistory
    {
        get => canLoadOlderHistory;
        private set
        {
            if (SetProperty(ref canLoadOlderHistory, value))
            {
                LoadOlderHistoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public ObservableCollection<ThreadAttachmentPresentationViewModel> ThreadAttachments => threadAttachments;

    [DataMember]
    public string ThreadAttachmentsStatusText
    {
        get => threadAttachmentsStatusText;
        private set
        {
            if (SetProperty(ref threadAttachmentsStatusText, value))
            {
                OnPropertyChanged(nameof(HasHistoryNotice));
            }
        }
    }

    [DataMember]
    public ObservableCollection<ThreadTurnSummary> ThreadTurns => threadTurns;

    [DataMember]
    public string HistoryTurnSummaryText => ThreadTurns.Count == 1 ? "1 turn in loaded history" : $"{ThreadTurns.Count} turns in loaded history";

    [DataMember]
    public AsyncCommand SendCommand { get; }

    [DataMember]
    public AsyncCommand InterruptCommand { get; }

    [DataMember]
    public AsyncCommand AccountCommand { get; }

    [DataMember]
    public AsyncCommand ToggleHistoryCommand { get; }

    [DataMember]
    public AsyncCommand CloseHistoryCommand { get; }

    [DataMember]
    public AsyncCommand ToggleUsageCommand { get; }

    [DataMember]
    public AsyncCommand CloseUsageCommand { get; }

    [DataMember]
    public AsyncCommand OpenUsageDashboardCommand { get; }

    [DataMember]
    public AsyncCommand OpenUsageHelpCommand { get; }

    [DataMember]
    public AsyncCommand AttachCommand { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Interlocked.Increment(ref usageConnectionGeneration);
        InvalidateUsage();
        connectionHealth.Clear();
        recoveryCancellation?.Cancel();
        recoveryCancellation?.Dispose();
        recoveryCancellation = null;
        UnsubscribeBridge(bridge);
        lifetime.Cancel();
        CancelFileSuggestionRefresh();
        slashCommandCoordinator.CancelAll();
        lifetime.Dispose();
        ValueTask bridgeDisposal = bridge.DisposeAsync();
        if (!bridgeDisposal.IsCompletedSuccessfully)
        {
            _ = ObserveBridgeDisposalAsync(bridgeDisposal);
        }
    }

    private static async Task ObserveBridgeDisposalAsync(ValueTask bridgeDisposal)
    {
        try
        {
            await bridgeDisposal.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Worker bridge disposal failed", ex);
        }
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
    private async Task<bool> ConnectWithDirectoryAsync(string workingDirectory, bool reloadThreads = false, bool profileGateHeld = false)
    {
        // The bridge reads the saved profile selection, so every connect runs under the profile
        // operation gate (gated callers already hold it). An ungated caller takes the gate before
        // the connect guard and keeps it until the guard is cleared, so a profile action queued
        // behind this connect never finds the guard still set and silently drops its dispatch.
        bool enteredGate = false;
        if (!profileGateHeld)
        {
            if (!await TryEnterProfileGateAsync().ConfigureAwait(false))
            {
                return false;
            }

            enteredGate = true;
        }

        try
        {
            // The auto-connect watcher and a user-initiated Send/Connect can both reach here; the
            // guard ensures only one connect attempt runs at a time so we never spawn two workers.
            if (Interlocked.CompareExchange(ref connecting, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                return await ConnectUnderGateAsync(workingDirectory, reloadThreads).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref connecting, 0);
            }
        }
        finally
        {
            if (enteredGate)
            {
                profileOperationGate.Release();
            }
        }
    }

    // Called under profileOperationGate with the connect guard held.
    private async Task<bool> ConnectUnderGateAsync(string workingDirectory, bool reloadThreads)
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

        // Capture the target under the gate so the label names the profile the bridge
        // dispatches, even when a queued selection/save completed while we waited.
        string? targetProfileName = remoteProfiles.AppliedProfileName;
        await OnUiAsync(() => SetConnectedProfileName(targetProfileName)).ConfigureAwait(false);
        WorkerStatus result;
        try
        {
            result = await bridge.ConnectAsync(workingDirectory, settings.ExperimentalApiEnabled, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested)
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

                // Keep the attempted remote target so recovery offers Reconnect, not a local
                // restart. Generation 0 marks that no Worker connection was ever confirmed.
                Target = targetProfileName is null
                    ? null
                    : new ConnectionTargetSnapshot { Kind = ConnectionTargetKind.Remote, DisplayName = targetProfileName },
            };
        }

        bool applied = false;
        await OnUiAsync(() =>
        {
            if (CanApplyStatusSnapshot(result))
            {
                Status = result;
                applied = true;
            }
        }).ConfigureAwait(false);
        if (!applied)
        {
            return false;
        }

        if (result.State == WorkerConnectionState.Ready)
        {
            this.workingDirectory = workingDirectory;
            unavailableSlashCommands.Clear();
            initialized = true;
            await RefreshReadyStateAsync(reloadThreads).ConfigureAwait(false);
        }

        return result.State == WorkerConnectionState.Ready;
    }

    private void SetConnectedProfileName(string? profileName)
    {
        if (string.Equals(connectedProfileName, profileName, StringComparison.Ordinal))
        {
            return;
        }

        connectedProfileName = profileName;
        OnPropertyChanged(nameof(ConnectionTargetText));
        OnPropertyChanged(nameof(ConnectionTargetAutomationName));
    }

    // Switching the connection target replaces the Worker's app-server connection, so it waits
    // until no connect, turn, or approval is in flight.
    private bool CanReconnectForProfile()
        => Volatile.Read(ref connecting) == 0
            && Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Disconnected or WorkerConnectionState.Degraded
            && Status.TurnId is null;

    private async Task ApplyRemoteProfileAsync()
    {
        // Capture the row Apply was invoked for; the selection may change while this waits.
        RemoteProfileViewModel? invoked = remoteProfiles.SelectedProfile;
        if (!await TryEnterProfileGateAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!remoteProfiles.TryGetApplicableProfile(invoked, out string profileName, out string error))
            {
                remoteProfiles.ReportStatus(error);
                return;
            }

            // Persist that same profile under the gate so the bridge connects to it, not to a
            // selection still queued behind this operation.
            remoteProfiles.PersistAppliedSelectionUnderGate(invoked!);

            await ReconnectForProfileAsync($"Connected with remote profile '{profileName}'.").ConfigureAwait(false);
        }
        finally
        {
            profileOperationGate.Release();
        }
    }

    private async Task UseLocalAppServerAsync()
    {
        if (!await TryEnterProfileGateAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // Persist the local selection before the Worker bridge reads settings for connect.
            remoteProfiles.ClearSelectionUnderGate();
            await ReconnectForProfileAsync("Connected to the local codex app-server.").ConfigureAwait(false);
        }
        finally
        {
            profileOperationGate.Release();
        }
    }

    private async Task RetrySavedProfileConnectAsync()
    {
        if (!await TryEnterProfileGateAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await ReconnectForProfileAsync("Connected with the saved remote profile.").ConfigureAwait(false);
        }
        finally
        {
            profileOperationGate.Release();
        }
    }

    private async Task<bool> TryEnterProfileGateAsync()
    {
        try
        {
            await profileOperationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task ReconnectForProfileAsync(string successText)
    {
        string? directory = workingDirectory;
        if (directory is null)
        {
            try
            {
                directory = await workspaceDirectoryResolver.ResolveAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
        }

        if (directory is null)
        {
            remoteProfiles.ReportStatus("Open a solution or folder, then connect again.");
            return;
        }

        IReadOnlyList<SlashCommandInvocation> canceled = slashCommandCoordinator.CancelAll();
        if (canceled.Count > 0)
        {
            await ShowSlashStatusAsync(
                $"Canceled {canceled.Count} queued slash commands because the connection target changed.").ConfigureAwait(false);
        }

        bool connected = await ConnectWithDirectoryAsync(directory, reloadThreads: true, profileGateHeld: true).ConfigureAwait(false);
        remoteProfiles.ReportStatus(connected
            ? successText
            : $"Could not connect: {markdown.ToSafeText(Status.Message).Trim()}");
    }

    private async Task RestartAsync()
    {
        if (IsRemoteTarget)
        {
            if (Status.Target!.Generation == 0)
            {
                // The Worker never confirmed this remote connection, so there is no generation
                // snapshot to reconnect; rerun the full saved-profile connect instead.
                await RetrySavedProfileConnectAsync().ConfigureAwait(false);
                return;
            }

            await ReconnectRemoteAsync().ConfigureAwait(false);
            return;
        }

        IReadOnlyList<SlashCommandInvocation> canceled = slashCommandCoordinator.CancelAll();
        if (canceled.Count > 0)
        {
            await ShowSlashStatusAsync(
                $"Canceled {canceled.Count} queued slash commands because the worker is restarting.").ConfigureAwait(false);
        }

        WorkerStatus result = await bridge.RestartAsync(lifetime.Token).ConfigureAwait(false);
        bool applied = false;
        await OnUiAsync(() =>
        {
            if (CanApplyStatusSnapshot(result))
            {
                Status = result;
                applied = true;
            }
        }).ConfigureAwait(false);
        if (applied && result.State == WorkerConnectionState.Ready)
        {
            unavailableSlashCommands.Clear();
            await RefreshReadyStateAsync(reloadThreads: true).ConfigureAwait(false);
        }
    }

    // Explicit remote reconnect: reload saved settings and require that the applied profile is
    // still saved, enabled, unedited, and unchanged before the Worker reads the token again.
    private async Task ReconnectRemoteAsync()
    {
        if (!await TryEnterProfileGateAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            ConnectionTargetSnapshot? appliedTarget = Status.Target;
            if (!TryCreateReconnectRequest(appliedTarget, out RemoteReconnectRequest? request, out ConnectionOperationRejectionReason reason))
            {
                await ShowConnectionNoticeAsync(DescribeRejection(reason)).ConfigureAwait(false);
                return;
            }

            // Share the connect guard so an auto or lazy connect cannot overlap this reconnect.
            if (Interlocked.CompareExchange(ref connecting, 1, 0) != 0)
            {
                return;
            }

            IReadOnlyList<SlashCommandInvocation> canceled = slashCommandCoordinator.CancelAll();
            if (canceled.Count > 0)
            {
                await ShowSlashStatusAsync(
                    $"Canceled {canceled.Count} queued slash commands because the remote connection is reconnecting.").ConfigureAwait(false);
            }

            WorkerStatus result;
            try
            {
                result = await bridge.ReconnectAsync(request!, lifetime.Token).ConfigureAwait(false);
            }
            catch (StreamJsonRpc.RemoteInvocationException ex) when (ex.ErrorCode == WorkerErrorCodes.ConnectionOperationRejected)
            {
                await ShowConnectionNoticeAsync(DescribeRejection(ParseRejection(ex))).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                Interlocked.Exchange(ref connecting, 0);
            }

            bool statusApplied = false;
            await OnUiAsync(() =>
            {
                if (CanApplyStatusSnapshot(result))
                {
                    Status = result;
                    statusApplied = true;
                }
            }).ConfigureAwait(false);
            if (statusApplied && result.State == WorkerConnectionState.Ready)
            {
                unavailableSlashCommands.Clear();
                await RefreshReadyStateAsync(reloadThreads: true).ConfigureAwait(false);
            }
        }
        finally
        {
            profileOperationGate.Release();
        }
    }

    // Called under profileOperationGate. Never reads a token file.
    private bool TryCreateReconnectRequest(
        ConnectionTargetSnapshot? applied,
        out RemoteReconnectRequest? request,
        out ConnectionOperationRejectionReason reason)
    {
        request = null;
        reason = ConnectionOperationRejectionReason.RemoteConnectionRequired;
        if (applied is not { Kind: ConnectionTargetKind.Remote } || string.IsNullOrEmpty(applied.DisplayName))
        {
            return false;
        }

        RemoteConnectionProfile? saved = settingsStore.Load().RemoteProfiles
            .FirstOrDefault(profile => profile is not null && string.Equals(profile.Name, applied.DisplayName, StringComparison.Ordinal));
        if (saved is null || !saved.Enabled)
        {
            reason = ConnectionOperationRejectionReason.ProfileUnavailable;
            return false;
        }

        if (remoteProfiles.FindByPersistedName(saved.Name)?.HasUnsavedChanges == true
            || !string.Equals(saved.ComputeFingerprint(), applied.Fingerprint, StringComparison.Ordinal))
        {
            reason = ConnectionOperationRejectionReason.ProfileChanged;
            return false;
        }

        request = new RemoteReconnectRequest
        {
            ProfileName = saved.Name,
            Fingerprint = applied.Fingerprint!,
            ExpectedGeneration = applied.Generation,
        };
        return true;
    }

    internal static ConnectionOperationRejectionReason ParseRejection(StreamJsonRpc.RemoteInvocationException exception)
    {
        string? data = exception.ErrorData?.ToString() ?? exception.Message;
        return Enum.TryParse(data?.Trim('"'), out ConnectionOperationRejectionReason reason)
            ? reason
            : Enum.TryParse(exception.Message, out reason) ? reason : ConnectionOperationRejectionReason.StaleGeneration;
    }

    internal static string DescribeRejection(ConnectionOperationRejectionReason reason) => reason switch
    {
        ConnectionOperationRejectionReason.LocalProcessRequired
            => "Restart applies only to the local app-server. Use Reconnect remote app-server for a remote profile.",
        ConnectionOperationRejectionReason.RemoteConnectionRequired
            => "Reconnect applies only to a remote profile. Use Restart local app-server instead.",
        ConnectionOperationRejectionReason.ProfileUnavailable
            => "The applied remote profile is no longer saved and enabled. Apply a saved profile or use the local app-server.",
        ConnectionOperationRejectionReason.ProfileChanged
            => "The applied remote profile has unsaved or saved changes. Save it and connect with it again, or use the local app-server.",
        _ => "The connection changed before the request ran. Check the connection status and try again.",
    };

    private Task ShowConnectionNoticeAsync(string message)
        => OnUiAsync(() =>
        {
            string safeMessage = markdown.ToSafeText(message).Trim();
            remoteProfiles.ReportStatus(safeMessage);
            Items.Add(new ChatItemViewModel("Status", safeMessage, ConversationEventKind.ItemCompleted));
        });

    private void ObserveHealthProfile(RemoteProfileViewModel? profile)
    {
        if (observedHealthProfile is not null)
        {
            observedHealthProfile.PropertyChanged -= OnHealthProfileChanged;
        }

        observedHealthProfile = profile;
        if (profile is not null)
        {
            profile.PropertyChanged += OnHealthProfileChanged;
        }

        CheckProfileHealthCommand.RaiseCanExecuteChanged();
    }

    private void OnHealthProfileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Editing the checked profile's metadata invalidates any shown or in-flight result, so a
        // late completion for the previously saved endpoint is discarded.
        if (e.PropertyName is nameof(RemoteProfileViewModel.Name)
            or nameof(RemoteProfileViewModel.Endpoint)
            or nameof(RemoteProfileViewModel.TokenFilePath)
            or nameof(RemoteProfileViewModel.LocalRoot)
            or nameof(RemoteProfileViewModel.ServerRoot)
            or nameof(RemoteProfileViewModel.IsEnabled))
        {
            connectionHealth.Clear();
        }

        CheckProfileHealthCommand.RaiseCanExecuteChanged();
    }

    // Health diagnosis targets only a saved, enabled profile without pending edits whose saved
    // endpoint passes the shared policy. It is independent of the active connection.
    private bool CanCheckProfileHealth()
    {
        RemoteConnectionProfile? saved = remoteProfiles.SelectedProfile?.PersistedProfile;
        return saved is not null
            && saved.Enabled
            && remoteProfiles.SelectedProfile!.HasUnsavedChanges == false
            && RemoteEndpointPolicy.Validate(saved.Endpoint).IsValid
            && !connectionHealth.IsChecking;
    }

    private async Task CheckProfileHealthAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        RemoteProfileViewModel? row = remoteProfiles.SelectedProfile;
        RemoteConnectionProfile? saved = row?.PersistedProfile;
        if (row is null || saved is null || !CanCheckProfileHealth())
        {
            return;
        }

        long token = connectionHealth.Begin(saved.Name, saved.ComputeFingerprint());
        CheckProfileHealthCommand.RaiseCanExecuteChanged();
        try
        {
            ConnectionDiagnosticsResult result = await bridge.DiagnoseConnectionAsync(
                new ConnectionDiagnosticsRequest { ProfileName = saved.Name, Endpoint = saved.Endpoint },
                lifetime.Token).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    connectionHealth.Complete(token, result, Status, StatusStateText);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Connection health check failed", ex);
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    connectionHealth.Fail(token, "The health check could not run because the Codex Worker is unavailable.");
                }
            })
                .ConfigureAwait(false);
        }
        finally
        {
            await OnUiAsync(CheckProfileHealthCommand.RaiseCanExecuteChanged).ConfigureAwait(false);
        }
    }

    internal async Task RefreshReadyStateAsync(bool reloadThreads)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        AccountStatus accountStatus = await bridge.GetAccountStatusAsync(lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        ExtensionDiagnostics.Write($"Account status received state={accountStatus.State} plan={accountStatus.PlanType ?? "none"}");

        // Model discovery must not wait on Remote UI account synchronization. Account updates
        // raise several cross-process property and command notifications, and a delayed VS-side
        // subscriber previously kept the picker on its built-in fallback entries indefinitely.
        await PopulateModelsAsync().ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        await PopulatePermissionProfilesAsync().ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        await OnUiAsync(() =>
        {
            if (IsCurrentOwner(owner))
            {
                UpdateAccount(accountStatus);
            }
        }).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        if (accountStatus.State == AccountState.SignedIn)
        {
            await RefreshUsageAsync(force: false).ConfigureAwait(false);
        }
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
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        ThreadSummary? thread = null;
        bool started = await RunTrackedMutationAsync("New conversation", owner, async () =>
        {
            thread = await bridge.StartThreadAsync(StampOwner(new StartThreadRequest(), owner), lifetime.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!started || thread is null)
        {
            return;
        }

        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            Threads.Insert(0, thread);
            selectedThread = thread;
            IsThreadJoined = true;
            OnPropertyChanged(nameof(SelectedThread));
            OnPropertyChanged(nameof(IsHistoryOnly));
            OnPropertyChanged(nameof(EffectiveApprovalModeText));
            ResetSelectedHistoryState(thread);
        }).ConfigureAwait(false);
    }

    private async Task JoinSelectedThreadAsync()
    {
        ThreadSummary? thread = SelectedThread;
        if (thread is null || IsThreadJoined || IsRecovering)
        {
            return;
        }

        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (Status.TurnId is not null && !string.Equals(Status.ThreadId, thread.Id, StringComparison.Ordinal))
        {
            return;
        }

        ThreadSummary? resumed = null;
        bool joined = await RunTrackedMutationAsync("Join conversation", owner, async () =>
        {
            resumed = await bridge.ResumeThreadAsync(
                StampOwner(new ResumeThreadRequest { ThreadId = thread.Id, UserConfirmed = true }, owner),
                lifetime.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!joined || resumed is null)
        {
            return;
        }

        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner)
                || !string.Equals(SelectedThread?.Id, thread.Id, StringComparison.Ordinal))
            {
                return;
            }

            if (resumed.EffectiveApprovalState is not null)
            {
                thread.EffectiveApprovalState = resumed.EffectiveApprovalState;
            }
            if (!string.IsNullOrWhiteSpace(resumed.EffectiveReasoningEffort))
            {
                thread.EffectiveReasoningEffort = resumed.EffectiveReasoningEffort;
            }
            if (!string.IsNullOrWhiteSpace(resumed.EffectiveServiceTier))
            {
                thread.EffectiveServiceTier = resumed.EffectiveServiceTier;
            }
            IsThreadJoined = true;
            OnPropertyChanged(nameof(EffectiveApprovalModeText));
            HistoryStatusText = "Conversation joined. Sending and turn controls are enabled.";
        }).ConfigureAwait(false);

        await DrainSlashQueuesAsync(thread.Id).ConfigureAwait(false);
    }

    private async Task ReadSelectedThreadHistoryAsync(ThreadSummary thread)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        await OnUiAsync(() => isHistoryLoading = true).ConfigureAwait(false);
        try
        {
            ThreadReadResult read = await bridge.ReadThreadAsync(
                StampOwner(new ReadThreadRequest { ThreadId = thread.Id }, owner), lifetime.Token).ConfigureAwait(false);
            ThreadTurnsPage turns = await bridge.ListThreadTurnsAsync(
                StampOwner(new ListThreadTurnsRequest { ThreadId = thread.Id, Limit = 50 }, owner), lifetime.Token).ConfigureAwait(false);
            ThreadItemsPage items = await bridge.ListThreadItemsAsync(
                StampOwner(new ListThreadItemsRequest { ThreadId = thread.Id, Limit = 100 }, owner), lifetime.Token).ConfigureAwait(false);
            (List<ThreadAttachmentMetadata> attachments, bool truncated) = await ListAllAttachmentMetadataAsync(thread.Id, owner, lifetime.Token).ConfigureAwait(false);

            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner) || !ReferenceEquals(SelectedThread, thread) && SelectedThread?.Id != thread.Id)
                {
                    return;
                }

                if (!MatchesOwner(read, owner) || !MatchesOwner(turns, owner) || !MatchesOwner(items, owner))
                {
                    MarkHistoryStale("History changed while it was loading. Refresh before relying on this transcript.");
                    return;
                }

                thread.Preview = read.Thread.Preview;
                thread.Cwd = read.Thread.Cwd;
                thread.UpdatedAt = read.Thread.UpdatedAt;
                threadTurnsCursor = turns.NextCursor;
                threadItemsCursor = items.NextCursor is null ? null : new ThreadItemCursor { Kind = ThreadItemCursorKind.Opaque, Value = items.NextCursor };
                foreach (ThreadHistoryItem item in items.Items.Reverse())
                {
                    MergeHistoryItem(thread.Id, item, owner, prepend: false);
                }

                MergeThreadTurns(turns.Turns.Reverse(), prepend: false);

                foreach (ThreadAttachmentMetadata attachment in attachments)
                {
                    MergeAttachment(attachment);
                }

                ThreadAttachmentsStatusText = truncated
                    ? "Showing the first 100 attachment records; the bounded metadata view has more entries."
                    : string.Empty;

                CanLoadOlderHistory = threadItemsCursor is not null || threadTurnsCursor is not null;
                isHistoryLoading = false;
                HistoryStatusText = IsHistoryStale
                    ? "History synchronization was incomplete. Refresh to load a consistent transcript."
                    : "Read-only history. Join explicitly before sending or controlling this conversation.";
                RefreshAttachmentsCommand.RaiseCanExecuteChanged();
                DrainPendingHistoryEvents(owner, thread.Id);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Could not load read-only thread history", ex);
            await OnUiAsync(() => MarkHistoryStale("History could not be loaded completely. Refresh to try again.")).ConfigureAwait(false);
        }
        finally
        {
            await OnUiAsync(() => isHistoryLoading = false).ConfigureAwait(false);
        }
    }

    private async Task LoadOlderHistoryAsync()
    {
        ThreadSummary? thread = SelectedThread;
        ThreadItemCursor? cursor = threadItemsCursor;
        string? turnsCursor = threadTurnsCursor;
        if (thread is null || (cursor is null && turnsCursor is null) || IsHistoryStale || isHistoryLoading)
        {
            return;
        }

        OwnerSnapshot owner = CaptureOwnerSnapshot();
        await OnUiAsync(() => isHistoryLoading = true).ConfigureAwait(false);
        try
        {
            ThreadItemsPage? page = cursor is null ? null : await bridge.ListThreadItemsAsync(
                StampOwner(new ListThreadItemsRequest { ThreadId = thread.Id, Cursor = cursor, Limit = 100 }, owner), lifetime.Token).ConfigureAwait(false);
            ThreadTurnsPage? turns = turnsCursor is null ? null : await bridge.ListThreadTurnsAsync(
                StampOwner(new ListThreadTurnsRequest { ThreadId = thread.Id, Cursor = turnsCursor, Limit = 50 }, owner), lifetime.Token).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner) || SelectedThread?.Id != thread.Id)
                {
                    return;
                }

                if ((page is not null && !MatchesOwner(page, owner)) || (turns is not null && !MatchesOwner(turns, owner)))
                {
                    MarkHistoryStale("History changed while paging. Refresh before relying on this transcript.");
                    return;
                }

                foreach (ThreadHistoryItem item in page?.Items ?? Array.Empty<ThreadHistoryItem>())
                {
                    MergeHistoryItem(thread.Id, item, owner, prepend: true);
                }

                MergeThreadTurns(turns?.Turns ?? Array.Empty<ThreadTurnSummary>(), prepend: true);

                threadItemsCursor = page?.NextCursor is null ? null : new ThreadItemCursor { Kind = ThreadItemCursorKind.Opaque, Value = page.NextCursor };
                threadTurnsCursor = turns?.NextCursor;
                CanLoadOlderHistory = threadItemsCursor is not null || threadTurnsCursor is not null;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExtensionDiagnostics.Write("Could not load an older history page", ex);
            await OnUiAsync(() => MarkHistoryStale("History paging failed. Refresh to load a consistent transcript.")).ConfigureAwait(false);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                isHistoryLoading = false;
                if (IsCurrentOwner(owner) && SelectedThread?.Id == thread.Id)
                {
                    DrainPendingHistoryEvents(owner, thread.Id);
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task RefreshSelectedHistoryAsync()
    {
        ThreadSummary? thread = SelectedThread;
        if (thread is null || Status.State != WorkerConnectionState.Ready || isHistoryLoading)
        {
            return;
        }

        Items.Clear();
        historyItemKeys.Clear();
        historyBytes = 0;
        pendingHistoryEvents.Clear();
        bufferedEventCount = 0;
        bufferedEventBytes = 0;
        threadItemsCursor = null;
        threadTurnsCursor = null;
        ThreadAttachments.Clear();
        ThreadAttachmentsStatusText = string.Empty;
        ThreadTurns.Clear();
        IsHistoryStale = false;
        HistoryStatusText = "Refreshing read-only history…";
        await ReadSelectedThreadHistoryAsync(thread).ConfigureAwait(false);
    }

    private static bool MatchesOwner(WorkerGenerationResult result, OwnerSnapshot owner)
        => result.OwnerGeneration == owner.OwnerGeneration
            && result.ConnectionGeneration == owner.ConnectionGeneration
            && string.Equals(result.StatePartitionFingerprint, owner.StatePartitionFingerprint, StringComparison.Ordinal);

    private void MergeHistoryItem(string threadId, ThreadHistoryItem historyItem, OwnerSnapshot owner, bool prepend)
    {
        string key = string.Concat(owner.StatePartitionFingerprint, ":", owner.OwnerGeneration, ":", owner.ConnectionGeneration, ":", threadId, ":", historyItem.TurnId, ":", historyItem.Id);
        ChatItemViewModel? existingItem = Items.LastOrDefault(item => string.Equals(item.HistoryKey, key, StringComparison.Ordinal)
            || (string.Equals(item.ThreadId, threadId, StringComparison.Ordinal)
                && string.Equals(item.TurnId, historyItem.TurnId, StringComparison.Ordinal)
                && string.Equals(item.ItemId, historyItem.Id, StringComparison.Ordinal)
                && item.OwnerGeneration == owner.OwnerGeneration
                && item.ConnectionGeneration == owner.ConnectionGeneration));
        if (!historyItemKeys.Add(key))
        {
            if (existingItem is not null && !existingItem.IsHistoryCompleted)
            {
                string replacement = markdown.ToSafeText(historyItem.Text ?? string.Empty);
                SetTranscriptText(existingItem, replacement);
                existingItem.HistoryKey = key;
                existingItem.IsHistoryCompleted = true;
                TrimHistoryWindow();
            }

            return;
        }

        if (existingItem is not null)
        {
            SetTranscriptText(existingItem, markdown.ToSafeText(historyItem.Text ?? string.Empty));
            existingItem.HistoryKey = key;
            existingItem.IsHistoryCompleted = true;
            TrimHistoryWindow();
            return;
        }

        string type = historyItem.Type.ToLowerInvariant();
        string role = type.Contains("user", StringComparison.Ordinal) ? "You"
            : type.Contains("command", StringComparison.Ordinal) ? "Command"
            : type.Contains("reason", StringComparison.Ordinal) ? "Reasoning"
            : type.Contains("agent", StringComparison.Ordinal) || type.Contains("assistant", StringComparison.Ordinal) ? "Codex"
            : "History";
        ConversationEventKind kind = role switch
        {
            "You" => ConversationEventKind.ItemCompleted,
            "Command" => ConversationEventKind.CommandOutputDelta,
            "Reasoning" => ConversationEventKind.ReasoningSummaryDelta,
            "Codex" => ConversationEventKind.AgentMessageDelta,
            _ => ConversationEventKind.ItemCompleted,
        };
        string safeText = markdown.ToSafeText(historyItem.Text ?? string.Empty);
        var item = new ChatItemViewModel(role, safeText, kind)
        {
            ItemId = historyItem.Id,
            ThreadId = threadId,
            TurnId = historyItem.TurnId,
            HistoryKey = key,
            IsHistoryCompleted = true,
            StatePartitionFingerprint = owner.StatePartitionFingerprint,
            OwnerGeneration = owner.OwnerGeneration,
            ConnectionGeneration = owner.ConnectionGeneration,
        };
        if (prepend)
        {
            Items.Insert(0, item);
        }
        else
        {
            Items.Add(item);
        }
        TrimHistoryWindow();
    }

    private void TrimHistoryWindow()
    {
        bool trimmed = false;
        while (Items.Count > MaximumVisibleHistoryItems || historyBytes > MaximumVisibleHistoryBytes)
        {
            ChatItemViewModel? oldest = Items.FirstOrDefault();
            if (oldest is null)
            {
                break;
            }

            if (oldest.HistoryKey is not null)
            {
                historyItemKeys.Remove(oldest.HistoryKey);
            }
            Items.Remove(oldest);
            trimmed = true;
        }

        if (trimmed)
        {
            MarkHistoryStale("The transcript reached its 1,000-item / 16 MiB display limit. Refresh to load a consistent recent window.");
        }
    }

    private void MarkHistoryStale(string message)
    {
        IsHistoryStale = true;
        CanLoadOlderHistory = false;
        HistoryStatusText = message;
    }

    private void DrainPendingHistoryEvents(OwnerSnapshot owner, string threadId)
    {
        while (pendingHistoryEvents.TryDequeue(out WorkerNotification<ConversationEvent>? notification))
        {
            bufferedEventCount--;
            bufferedEventBytes -= GetConversationEventBytes(notification.Value);
            if (IsNotificationCurrent(notification) && string.Equals(notification.Value.ThreadId, threadId, StringComparison.Ordinal))
            {
                _ = OnConversationEventAsync(notification);
            }
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            historyBytes = 0;
        }

        if (e.OldItems is not null)
        {
            foreach (ChatItemViewModel item in e.OldItems)
            {
                historyBytes -= Encoding.UTF8.GetByteCount(item.Text);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChatItemViewModel item in e.NewItems)
            {
                historyBytes += Encoding.UTF8.GetByteCount(item.Text);
            }
        }

        historyBytes = Math.Max(0, historyBytes);
        OnPropertyChanged(nameof(IsThreadEmpty));
    }

    private void SetTranscriptText(ChatItemViewModel item, string text)
    {
        historyBytes -= Encoding.UTF8.GetByteCount(item.Text);
        item.Text = text;
        historyBytes += Encoding.UTF8.GetByteCount(text);
    }

    private void ResetSelectedHistoryState(ThreadSummary thread)
    {
        IsHistoryStale = false;
        HistoryStatusText = "Read-only history. Join explicitly before sending or controlling this conversation.";
        CanLoadOlderHistory = false;
        threadTurnsCursor = null;
        threadItemsCursor = null;
        historyItemKeys.Clear();
        historyBytes = 0;
        pendingHistoryEvents.Clear();
        bufferedEventCount = 0;
        bufferedEventBytes = 0;
        ThreadAttachments.Clear();
        ThreadAttachmentsStatusText = string.Empty;
        ThreadTurns.Clear();
        deletedAttachmentIds.Clear();
        Items.Clear();
    }

    private void MergeThreadTurns(IEnumerable<ThreadTurnSummary> turns, bool prepend)
    {
        var knownIds = ThreadTurns.Select(static turn => turn.Id).ToHashSet(StringComparer.Ordinal);
        foreach (ThreadTurnSummary turn in turns)
        {
            if (knownIds.Add(turn.Id))
            {
                if (prepend)
                {
                    ThreadTurns.Insert(0, turn);
                }
                else
                {
                    ThreadTurns.Add(turn);
                }
            }
        }
    }

    private async Task RefreshAttachmentsAsync()
    {
        ThreadSummary? thread = SelectedThread;
        if (thread is null || Status.State != WorkerConnectionState.Ready)
        {
            return;
        }

        OwnerSnapshot owner = CaptureOwnerSnapshot();
        try
        {
            var deleted = new HashSet<string>(deletedAttachmentIds, StringComparer.Ordinal);
            (List<ThreadAttachmentMetadata> all, bool truncated) = await ListAllAttachmentMetadataAsync(
                thread.Id,
                owner,
                lifetime.Token,
                deleted,
                () => IsCurrentOwner(owner) && SelectedThread?.Id == thread.Id).ConfigureAwait(false);

            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner) || SelectedThread?.Id != thread.Id)
                {
                    return;
                }

                ThreadAttachments.Clear();
                foreach (ThreadAttachmentMetadata attachment in all)
                {
                    MergeAttachment(attachment);
                }

                ThreadAttachmentsStatusText = truncated
                    ? "Showing the first 100 attachment records; the bounded metadata view has more entries."
                    : string.Empty;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExtensionDiagnostics.Write("Could not refresh attachment metadata", ex);
            await OnUiAsync(() => ThreadAttachmentsStatusText = "Attachment metadata could not be refreshed. Retry when the connection is ready.").ConfigureAwait(false);
        }
    }

    private void MergeAttachment(ThreadAttachmentMetadata attachment)
    {
        if (ThreadAttachments.Count >= 100 || deletedAttachmentIds.Contains(attachment.Id)
            || ThreadAttachments.Any(existing => string.Equals(existing.Id, attachment.Id, StringComparison.Ordinal)))
        {
            return;
        }

        ThreadAttachments.Add(new ThreadAttachmentPresentationViewModel(attachment, markdown));
    }

    private async Task<(List<ThreadAttachmentMetadata> Items, bool Truncated)> ListAllAttachmentMetadataAsync(
        string threadId,
        OwnerSnapshot owner,
        CancellationToken cancellationToken,
        HashSet<string>? excludedIds = null,
        Func<bool>? isCurrent = null)
    {
        var result = new List<ThreadAttachmentMetadata>(100);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        bool truncated = false;
        do
        {
            ThreadAttachmentsPage page = await bridge.ListThreadAttachmentsAsync(
                StampOwner(new ListThreadAttachmentsRequest { ThreadId = threadId, Cursor = cursor, Limit = 50 }, owner), cancellationToken).ConfigureAwait(false);
            if (!MatchesOwner(page, owner) || isCurrent is not null && !isCurrent())
            {
                throw new InvalidOperationException("Attachment page ownership changed during history synchronization.");
            }

            foreach (ThreadAttachmentMetadata attachment in page.Attachments)
            {
                if (excludedIds?.Contains(attachment.Id) == true || !seen.Add(attachment.Id))
                {
                    continue;
                }

                if (result.Count == 100)
                {
                    truncated = true;
                    break;
                }

                result.Add(attachment);
            }

            cursor = page.NextCursor;
            truncated |= result.Count == 100 && cursor is not null;
            if (truncated)
            {
                break;
            }
        }
        while (cursor is not null && result.Count < 100);

        return (result, truncated);
    }

    private Task OnThreadAttachmentUpdatedAsync(WorkerNotification<ThreadAttachmentUpdatedEvent> notification)
        => OnUiAsync(() =>
        {
            if (!IsNotificationCurrent(notification) || SelectedThread?.Id != notification.Value.ThreadId)
            {
                return;
            }

            if (notification.Value.Operation == ThreadAttachmentOperation.Deleted)
            {
                deletedAttachmentIds.Add(notification.Value.AttachmentId);
                ThreadAttachmentPresentationViewModel? existing = ThreadAttachments.FirstOrDefault(
                    item => string.Equals(item.Id, notification.Value.AttachmentId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    ThreadAttachments.Remove(existing);
                }
            }
            else
            {
                _ = RefreshAttachmentsAsync();
            }
        });

    private async Task LoadMoreAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        ThreadPage page = await bridge.ListThreadsAsync(StampOwner(new ListThreadsRequest { Cursor = nextCursor }, owner), lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

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
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        await OnUiAsync(() =>
        {
            if (IsCurrentOwner(owner))
            {
                Threads.Clear();
                nextCursor = null;
            }
        }).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        await LoadMoreAsync().ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }
        if (nextCursor is null)
        {
            var existingThreadIds = new HashSet<string>(
                Threads.Select(static thread => thread.Id),
                StringComparer.Ordinal);
            IReadOnlyList<SlashCommandInvocation> canceled =
                slashCommandCoordinator.CancelMissingThreads(existingThreadIds);
            if (canceled.Count > 0)
            {
                await ShowSlashStatusAsync(
                    $"Canceled {canceled.Count} queued slash commands because their threads no longer exist.").ConfigureAwait(false);
            }
        }
    }

    internal async Task PopulateModelsAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        ListModelsResult result;
        try
        {
            result = await bridge.ListModelsAsync(StampOwner(new ListModelsRequest(), owner), lifetime.Token).ConfigureAwait(false);
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

        await OnUiAsync(() =>
        {
            if (IsCurrentOwner(owner))
            {
                modelCatalog = result;
            }
        }).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
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
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            string? previousSelection = SelectedModel;

            // Merge in place instead of Clear+Add: with Remote UI, clearing the list would
            // momentarily invalidate the VS-side ComboBox selection, whose TwoWay binding then
            // asynchronously writes SelectedModel = null back and clobbers the selection set
            // below. Insert new entries and Move existing ones into the catalog's order (both
            // raise granular CollectionChanged notifications that WPF's ComboBox applies without
            // touching the selection), and only then drop stale entries so the proxy-side
            // selection never becomes invalid.
            for (int i = 0; i < modelIds.Count; i++)
            {
                string modelId = modelIds[i];
                int existingIndex = Models.IndexOf(modelId);
                if (existingIndex < 0)
                {
                    Models.Insert(i, modelId);
                }
                else if (existingIndex != i)
                {
                    Models.Move(existingIndex, i);
                }
            }

            if (result.DefaultModel is not null && Models.Contains(result.DefaultModel))
            {
                SelectedModel = result.DefaultModel;
            }
            else if (previousSelection is not null && modelIds.Contains(previousSelection, StringComparer.Ordinal))
            {
                SelectedModel = previousSelection;
            }
            else
            {
                SelectedModel = modelIds[0];
            }

            for (int i = Models.Count - 1; i >= 0; i--)
            {
                if (!modelIds.Contains(Models[i], StringComparer.Ordinal))
                {
                    Models.RemoveAt(i);
                }
            }

            RefreshServiceTiers();
            UpdateComposerSuggestions(ComposerText);
            RefreshReasoningEfforts();
        }).ConfigureAwait(false);
    }

    private async Task SendAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        // Typing a normal message supersedes a still-pending prose-detected choice card (the user
        // chose to answer in their own words instead of picking an option).
        if (ActiveUserInput?.IsSynthetic == true)
        {
            await OnUiAsync(() => RemoveUserInput(ActiveUserInput!.RequestId)).ConfigureAwait(false);
        }

        string text = UnescapeFinalFileToken(ComposerText);
        SlashCommandParseResult parseResult = slashCommandParser.Parse(text);
        switch (parseResult.Kind)
        {
            case SlashCommandParseKind.Command:
                if (parseResult.Invocation is not null
                    && await ScheduleOrExecuteSlashCommandAsync(parseResult.Invocation).ConfigureAwait(false))
                {
                    await OnUiAsync(() =>
                    {
                        if (IsCurrentOwner(owner))
                        {
                            SetComposerText(string.Empty);
                        }
                    }).ConfigureAwait(false);
                }

                return;

            case SlashCommandParseKind.Unsupported:
            case SlashCommandParseKind.Unknown:
            case SlashCommandParseKind.InputTooLong:
                await ShowSlashFailureAsync(parseResult.ErrorMessage ?? "The slash command could not be parsed.").ConfigureAwait(false);
                return;

            case SlashCommandParseKind.EscapedPrompt:
                text = parseResult.PromptText ?? string.Empty;
                break;
        }

        await SendMessageAsync(text, clearComposer: true).ConfigureAwait(false);
    }

    // Core send path, shared by the composer (clearComposer: true) and the synthetic-choice resolver
    // (clearComposer: false), which sends the picked option text as the next turn.
    private async Task SendMessageAsync(string text, bool clearComposer, OwnerSnapshot? expectedOwner = null)
    {
        OwnerSnapshot owner = expectedOwner ?? CaptureOwnerSnapshot();
        if (!IsCurrentOwner(owner))
        {
            return;
        }
        if (SelectedThread is not null && !IsThreadJoined)
        {
            await ShowSlashFailureAsync("This conversation is read-only. Join it explicitly before sending.").ConfigureAwait(false);
            return;
        }
        if (HasPendingSkill && Status.State is WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval)
        {
            await ShowSlashStatusAsync("A skill is pending. Wait for the current turn or remove the skill chip before sending.").ConfigureAwait(false);
            return;
        }

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

        if (string.IsNullOrWhiteSpace(text)
            && (Status.TurnId is not null || (!HasPendingAttachments && !HasPendingSkill)))
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

        string targetThreadId = SelectedThread.Id;

        if (!IsCurrentOwner(owner))
        {
            return;
        }

        if (Status.TurnId is null)
        {
            string displayText = string.IsNullOrWhiteSpace(text)
                ? HasPendingSkill
                    ? $"Invoked skill: {PendingSkills[0].DisplayName}."
                    : PendingAttachments.Count == 1 ? "Attached 1 file." : $"Attached {PendingAttachments.Count} files."
                : markdown.ToSafeText(text);
            ChatItemViewModel? optimisticItem = null;
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    optimisticItem = new ChatItemViewModel("You", displayText, ConversationEventKind.ItemStarted)
                    {
                        ThreadId = targetThreadId,
                        StatePartitionFingerprint = owner.StatePartitionFingerprint,
                        OwnerGeneration = owner.OwnerGeneration,
                        ConnectionGeneration = owner.ConnectionGeneration,
                    };
                    Items.Add(optimisticItem);
                }
            }).ConfigureAwait(false);
            StartTurnRequest request = await CreateStartTurnRequestAsync(
                targetThreadId,
                text,
                forcePlanMode: false).ConfigureAwait(false);
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            StampOwner(request, owner);
            Guid operationId = Guid.NewGuid();
            try
            {
                await OnUiAsync(() =>
                {
                    if (IsCurrentOwner(owner) && optimisticItem is not null)
                    {
                        inFlightMutations[operationId] = optimisticItem;
                    }
                }).ConfigureAwait(false);
                string startedTurnId = await bridge.StartTurnAsync(request, lifetime.Token).ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (IsCurrentOwner(owner)
                        && SelectedThread?.Id == targetThreadId
                        && optimisticItem is not null
                        && Items.Contains(optimisticItem))
                    {
                        optimisticItem.ThreadId = targetThreadId;
                        optimisticItem.TurnId = startedTurnId;
                        optimisticItem.OwnerGeneration = owner.OwnerGeneration;
                        optimisticItem.ConnectionGeneration = owner.ConnectionGeneration;
                    }
                }).ConfigureAwait(false);
                await OnUiAsync(() => inFlightMutations.Remove(operationId)).ConfigureAwait(false);
            }
            catch (StreamJsonRpc.RemoteInvocationException ex) when (ex.ErrorCode == WorkerErrorCodes.AttachmentRejected)
            {
                await OnUiAsync(() => inFlightMutations.Remove(operationId)).ConfigureAwait(false);
                if (!IsCurrentOwner(owner))
                {
                    return;
                }

                if (optimisticItem is not null)
                {
                    await OnUiAsync(() => Items.Remove(optimisticItem)).ConfigureAwait(false);
                }

                // The Worker rejects an attachment a remote app-server cannot read (outside the
                // mapped local root) before the turn starts, so nothing was sent. Show the reason
                // and keep the chips so the user can remove or move the file.
                await ShowSlashFailureAsync($"The message was not sent: {ex.Message}").ConfigureAwait(false);
                return;
            }
            catch (StreamJsonRpc.RemoteInvocationException ex) when (ex.ErrorCode == WorkerErrorCodes.SkillRejected)
            {
                await OnUiAsync(() => inFlightMutations.Remove(operationId)).ConfigureAwait(false);
                if (optimisticItem is not null)
                {
                    await OnUiAsync(() => Items.Remove(optimisticItem)).ConfigureAwait(false);
                }

                if (IsCurrentOwner(owner))
                {
                    await ShowSlashFailureAsync($"The selected skill is no longer available: {ex.Message}").ConfigureAwait(false);
                }

                return;
            }
            catch (StreamJsonRpc.RemoteInvocationException ex) when (ex.ErrorCode is WorkerErrorCodes.ConnectionOperationRejected or WorkerErrorCodes.PreDispatchRejected)
            {
                await OnUiAsync(() => inFlightMutations.Remove(operationId)).ConfigureAwait(false);
                if (optimisticItem is not null)
                {
                    await OnUiAsync(() => Items.Remove(optimisticItem)).ConfigureAwait(false);
                }

                if (IsCurrentOwner(owner))
                {
                    await ShowSlashFailureAsync($"The message was rejected before it was sent: {ex.Message}").ConfigureAwait(false);
                }

                return;
            }
            catch (OperationCanceledException)
            {
                await OnUiAsync(() => inFlightMutations.Remove(operationId)).ConfigureAwait(false);
                if (optimisticItem is not null)
                {
                    await OnUiAsync(() => Items.Remove(optimisticItem)).ConfigureAwait(false);
                }

                if (IsCurrentOwner(owner))
                {
                    await ShowSlashStatusAsync("The send was cancelled before it completed.").ConfigureAwait(false);
                }

                return;
            }
            catch (StreamJsonRpc.RemoteInvocationException ex) when (ex.ErrorCode == WorkerErrorCodes.UpstreamOperationFailed)
            {
                await ShowStartTurnOutcomeUnknownAsync(operationId, owner, ex).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                await ShowStartTurnOutcomeUnknownAsync(operationId, owner, ex).ConfigureAwait(false);
                return;
            }

            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner))
                {
                    return;
                }

                ClearSentAttachments(request.Attachments);
                ClearSentSkill(request.Skill);
                ConsumeNextTurnSettings(request);
            }).ConfigureAwait(false);
        }
        else
        {
            var optimisticItem = new ChatItemViewModel("You", markdown.ToSafeText(text), ConversationEventKind.ItemStarted)
            {
                ThreadId = SelectedThread.Id,
                TurnId = Status.TurnId,
                StatePartitionFingerprint = owner.StatePartitionFingerprint,
                OwnerGeneration = owner.OwnerGeneration,
                ConnectionGeneration = owner.ConnectionGeneration,
            };
            Guid operationId = Guid.NewGuid();
            await OnUiAsync(() =>
            {
                Items.Add(optimisticItem);
                inFlightMutations[operationId] = optimisticItem;
            }).ConfigureAwait(false);
            try
            {
                await bridge.SteerTurnAsync(
                    StampOwner(new SteerTurnRequest { ThreadId = SelectedThread.Id, ExpectedTurnId = Status.TurnId, Text = text }, owner),
                    lifetime.Token).ConfigureAwait(false);
                await OnUiAsync(() => inFlightMutations.Remove(operationId)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await OnUiAsync(() =>
                {
                    if (inFlightMutations.Remove(operationId))
                    {
                        optimisticItem.MarkDeliveryUnknown();
                    }
                }).ConfigureAwait(false);
                await ShowSlashFailureAsync("Delivery is unknown. The message was not retried. Review the transcript and composer before sending again.").ConfigureAwait(false);
                ExtensionDiagnostics.Write("Turn steer outcome is unknown after dispatch", ex);
                return;
            }
        }

        if (clearComposer)
        {
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    SetComposerText(string.Empty);
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task ShowStartTurnOutcomeUnknownAsync(Guid operationId, OwnerSnapshot owner, Exception exception)
    {
        await OnUiAsync(() =>
        {
            if (inFlightMutations.Remove(operationId, out ChatItemViewModel? item))
            {
                item.MarkDeliveryUnknown();
            }

            if (IsCurrentOwner(owner))
            {
                RecoveryStatusText = "Message delivery outcome is unknown. The request was not retried.";
            }
        }).ConfigureAwait(false);

        if (IsCurrentOwner(owner))
        {
            await ShowSlashFailureAsync(
                "Delivery is unknown. The message was not retried. Review the transcript and composer before sending again.").ConfigureAwait(false);
        }

        ExtensionDiagnostics.Write("Turn start outcome is unknown after dispatch", exception);
    }

    private async Task<StartTurnRequest> CreateStartTurnRequestAsync(
        string threadId,
        string text,
        bool forcePlanMode)
    {
        AttachmentInfo[] attachments = PendingAttachments
            .Select(attachment => new AttachmentInfo
            {
                Path = attachment.FullPath,
                Kind = IsImageAttachment(attachment.FullPath) ? "image" : "mention",
            })
            .ToArray();
        IdeContextInfo? ideContext = ideContextEnabled
            ? await IdeContextCaptureService.CaptureAsync(
                workingDirectory,
                AsyncCommand.CurrentClientContext,
                lifetime.Token).ConfigureAwait(false)
            : null;
        string? collaborationMode = forcePlanMode ? "plan" : nextCollaborationMode;
        TurnSettingResolution reasoning = ResolveReasoningSetting(threadId);
        TurnSettingResolution serviceTier = ResolveServiceTierSetting(threadId);
        return StampOwner(new StartTurnRequest
        {
            ThreadId = threadId,
            Text = text,
            Model = SelectedModel,
            ApprovalPolicy = GetTurnApprovalPolicy(),
            ApprovalsReviewer = GetTurnApprovalsReviewer(),
            SandboxMode = GetTurnSandboxMode(),
            Permissions = GetTurnPermissions(),
            HasEffort = reasoning.HasValue,
            Effort = reasoning.Value,
            Personality = nextPersonality,
            HasServiceTier = serviceTier.HasValue,
            ServiceTier = serviceTier.Value,
            CollaborationMode = collaborationMode is null
                ? null
                : new CollaborationModeInfo
                {
                    Mode = collaborationMode,
                    Model = SelectedModel ?? string.Empty,
                    ReasoningEffort = reasoning.Value,
                },
            IdeContext = ideContext,
            Attachments = attachments,
            Skill = PendingSkills.FirstOrDefault()?.Invocation,
        });
    }

    // Next-turn settings apply to exactly one started turn. Compare against the request so a
    // value re-queued while the turn was starting survives for the following turn.
    private void ConsumeNextTurnSettings(StartTurnRequest request)
    {
        if (pendingReasoningByThread.TryGetValue(request.ThreadId, out PendingReasoningOverride reasoningPending)
            && request.HasEffort
            && string.Equals(reasoningPending.Effort, request.Effort, StringComparison.Ordinal))
        {
            pendingReasoningByThread.Remove(request.ThreadId);
            reasoningRestoreByThread[request.ThreadId] = reasoningPending.RestoreEffort;
        }
        else if (reasoningRestoreByThread.TryGetValue(request.ThreadId, out string? reasoningRestore)
            && request.HasEffort
            && string.Equals(reasoningRestore, request.Effort, StringComparison.Ordinal))
        {
            reasoningRestoreByThread.Remove(request.ThreadId);
        }

        if (string.Equals(nextPersonality, request.Personality, StringComparison.Ordinal))
        {
            nextPersonality = null;
        }

        if (pendingServiceTierByThread.TryGetValue(request.ThreadId, out PendingServiceTierOverride? pending)
            && request.HasServiceTier
            && string.Equals(pending.Tier, request.ServiceTier, StringComparison.Ordinal))
        {
            pendingServiceTierByThread.Remove(request.ThreadId);
            serviceTierRestoreByThread[request.ThreadId] = pending.RestoreTier;
        }
        else if (serviceTierRestoreByThread.TryGetValue(request.ThreadId, out string? restore)
            && request.HasServiceTier
            && string.Equals(restore, request.ServiceTier, StringComparison.Ordinal))
        {
            serviceTierRestoreByThread.Remove(request.ThreadId);
        }

        if (request.CollaborationMode is not null
            && string.Equals(
                nextCollaborationMode,
                request.CollaborationMode.Mode,
                StringComparison.Ordinal))
        {
            nextCollaborationMode = null;
        }
    }

    private TurnSettingResolution ResolveServiceTierSetting(string threadId)
    {
        if (pendingServiceTierByThread.TryGetValue(threadId, out PendingServiceTierOverride? pending))
        {
            if (SelectedModelSupportsServiceTier(pending.Tier))
            {
                return new TurnSettingResolution(true, pending.Tier);
            }
        }

        if (serviceTierRestoreByThread.TryGetValue(threadId, out string? restore))
        {
            if (restore is null || SelectedModelSupportsServiceTier(restore))
            {
                return new TurnSettingResolution(true, restore);
            }
        }

        ServiceTierOption? configured = ServiceTiers.FirstOrDefault(option =>
            option.Id.Length > 0
            && string.Equals(option.Id, settings.ServiceTierId, StringComparison.OrdinalIgnoreCase));
        return configured is null
            ? new TurnSettingResolution(false, null)
            : new TurnSettingResolution(true, configured.Id);
    }

    private bool SelectedModelSupportsServiceTier(string tier)
        => GetSelectedModelInfo()?.ServiceTiers.Any(option =>
            string.Equals(option.Id, tier, StringComparison.OrdinalIgnoreCase)) == true;

    private string? GetEffectiveServiceTier(string threadId)
    {
        ThreadSummary? thread = SelectedThread;
        if (thread is not null && string.Equals(thread.Id, threadId, StringComparison.Ordinal))
        {
            return thread.EffectiveServiceTier;
        }

        return string.Equals(Status.ThreadId, threadId, StringComparison.Ordinal)
            ? Status.EffectiveServiceTier
            : null;
    }

    private sealed record PendingServiceTierOverride(string Tier, string? RestoreTier);

    private readonly record struct TurnSettingResolution(bool HasValue, string? Value);

    private void UpdateComposerSuggestions(string text)
    {
        if (TryGetFileSuggestionQuery(text, out string query))
        {
            SlashCommands.CloseSuggestions();
            QueueFileSuggestionRefresh(query);
            return;
        }

        CancelFileSuggestionRefresh();
        FileSuggestions.CloseSuggestions();

        if (SlashCommands.HasActiveCommand)
        {
            return;
        }

        if (string.IsNullOrEmpty(text)
            || text[0] != '/'
            || text.StartsWith("//", StringComparison.Ordinal))
        {
            SlashCommands.CloseSuggestions();
            return;
        }

        int separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        string filter = separator < 0 ? text : text[..separator];
        IReadOnlyList<SlashCommandSuggestionDescriptor> builtIns = slashCommandCatalog
            .Filter(filter)
            .Select(CreateSlashCommandSuggestion)
            .ToArray();
        bool requiresSkillRefresh = skillsSnapshot is null
            || utcNow() >= skillsSnapshotExpiresAt;
        if (requiresSkillRefresh)
        {
            skillsRefreshPending = true;
        }

        UnifiedSlashSuggestionSnapshot suggestionSnapshot = BuildUnifiedSlashSuggestions(builtIns, text[1..]);
        skillSelections = suggestionSnapshot.SkillSelections;
        SlashCommands.ShowSuggestions(suggestionSnapshot.Suggestions);
        if (requiresSkillRefresh)
        {
            _ = EnsureSkillsLoadedAsync();
        }
    }

    private UnifiedSlashSuggestionSnapshot BuildUnifiedSlashSuggestions(
        IReadOnlyList<SlashCommandSuggestionDescriptor> builtIns,
        string skillQuery)
    {
        var selections = new Dictionary<string, SkillInfo>(StringComparer.Ordinal);
        int skillCapacity = skillsSnapshot?.Skills.Count ?? 0;
        var result = new List<SlashCommandSuggestionDescriptor>(builtIns.Count + skillCapacity + 4);
        result.AddRange(builtIns);
        result.Add(new SlashCommandSuggestionDescriptor(
            "Skills",
            "Structured skill invocation",
            IsAvailable: true,
            IsSkill: false,
            IsSelectable: false));

        if (skillsSnapshot is null)
        {
            if (!string.IsNullOrWhiteSpace(skillsLoadFailure))
            {
                result.Add(new SlashCommandSuggestionDescriptor(
                    "Skills temporarily unavailable",
                    skillsLoadFailure,
                    IsAvailable: true,
                    IsSelectable: false));
                return new UnifiedSlashSuggestionSnapshot(result, selections);
            }

            result.Add(new SlashCommandSuggestionDescriptor(
                "Loading skills…",
                "Fetching the skill catalog.",
                IsAvailable: true,
                IsSelectable: false));
            return new UnifiedSlashSuggestionSnapshot(result, selections);
        }

        if (!skillsSnapshot.IsSupported)
        {
            result.Add(new SlashCommandSuggestionDescriptor(
                "Skills unavailable",
                skillsSnapshot.UnavailableReason ?? "This app-server does not expose skills.",
                IsAvailable: true,
                IsSelectable: false));
            return new UnifiedSlashSuggestionSnapshot(result, selections);
        }

        SkillInfo[] uniqueSkills = skillsSnapshot.Skills
            .GroupBy(skill => string.Concat(skill.Name, "\0", skill.Scope, "\0", skill.Path), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        SkillInfo[] matches = uniqueSkills
            .Where(skill => SkillMatches(skill, skillQuery))
            .OrderBy(skill => SkillRank(skill, skillQuery))
            .ThenByDescending(skill => skill.Enabled)
            .ThenBy(skill => ScopeRank(skill.Scope))
            .ThenBy(skill => skill.DisplayName ?? skill.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.Name, StringComparer.Ordinal)
            .ThenBy(skill => skill.Path, StringComparer.Ordinal)
            .ToArray();
        Dictionary<(string Name, string Scope), SkillInfo[]> pathCollisions = uniqueSkills
            .GroupBy(skill => (skill.Name, skill.Scope))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(skill => skill.Path, StringComparer.Ordinal).ToArray());

        foreach (SkillInfo skill in matches)
        {
            string id = $"skill-{skillsSnapshot.Generation}-{Interlocked.Increment(ref skillSelectionSequence)}";
            bool isLiveCatalog = !skillsRefreshPending
                && !skillsSnapshot.IsStale
                && skillsSnapshot.Generation >= minimumSkillsGeneration;
            bool canSelect = isLiveCatalog && skill.Enabled;
            if (canSelect)
            {
                selections[id] = skill;
            }

            string displayName = string.IsNullOrWhiteSpace(skill.DisplayName) ? skill.Name : skill.DisplayName!;
            SkillInfo[] pathCollision = pathCollisions[(skill.Name, skill.Scope)];
            string scopeLabel = FormatSkillScope(skill.Scope);
            if (pathCollision.Length > 1)
            {
                int ordinal = Array.FindIndex(pathCollision, candidate => string.Equals(candidate.Path, skill.Path, StringComparison.Ordinal)) + 1;
                scopeLabel = $"{scopeLabel} {ordinal} of {pathCollision.Length}";
            }

            if (!isLiveCatalog)
            {
                scopeLabel = $"{scopeLabel} · Cached";
            }

            result.Add(new SlashCommandSuggestionDescriptor(
                $"/{displayName}",
                skill.Description,
                IsAvailable: canSelect,
                UnavailableReason: !isLiveCatalog
                    ? "Cached skill metadata cannot be selected until the catalog refresh completes."
                    : skill.Enabled ? string.Empty : "Skill is disabled.",
                IsSkill: true,
                ScopeLabel: scopeLabel,
                SelectionId: id,
                BrandColor: skill.BrandColor ?? string.Empty,
                IsSelectable: canSelect));
        }

        if (matches.Length == 0)
        {
            result.Add(new SlashCommandSuggestionDescriptor(
                "No matching skills",
                "Try the skill name or description.",
                IsAvailable: true,
                IsSelectable: false));
        }

        if (skillsRefreshPending)
        {
            result.Add(new SlashCommandSuggestionDescriptor(
                "Cached skill catalog",
                "Showing cached skills while the Worker refreshes the catalog in the background.",
                IsAvailable: true,
                IsSelectable: false));
        }
        else if (!string.IsNullOrWhiteSpace(skillsLoadFailure))
        {
            result.Add(new SlashCommandSuggestionDescriptor(
                "Skill refresh failed",
                $"{skillsLoadFailure} Cached skills remain visible; reopen the menu later to retry.",
                IsAvailable: true,
                IsSelectable: false));
        }

        if (skillsSnapshot.IsTruncated)
        {
            result.Add(new SlashCommandSuggestionDescriptor(
                "Skill catalog incomplete",
                "Showing every skill returned by the Worker; additional app-server skills were omitted by its safety limit.",
                IsAvailable: true,
                IsSelectable: false));
        }

        return new UnifiedSlashSuggestionSnapshot(result, selections);
    }

    private async Task EnsureSkillsLoadedAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        Task load = skillsLoadTask ??= LoadSkillsAsync();
        try
        {
            await load.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Skill discovery failed.", ex);
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner) || !ReferenceEquals(skillsLoadTask, load))
                {
                    return;
                }

                skillsRefreshPending = false;
                skillsLoadFailure = markdown.ToSafeText(ex.Message).Trim();
                skillsSnapshotExpiresAt = utcNow().AddSeconds(60);
                if (!string.IsNullOrEmpty(ComposerText) && ComposerText[0] == '/')
                {
                    UpdateComposerSuggestions(ComposerText);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (IsCurrentOwner(owner) && ReferenceEquals(skillsLoadTask, load))
            {
                skillsLoadTask = null;
            }
        }
    }

    private async Task LoadSkillsAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        bool forceReload = skillsSnapshot is not null
            && minimumSkillsGeneration > skillsSnapshot.Generation;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ListSkillsResult result = await bridge.ListSkillsAsync(StampOwner(new ListSkillsRequest { ForceReload = forceReload }, owner), lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentOwner(owner))
            {
                return;
            }
            bool accepted = false;
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner))
                {
                    return;
                }

                long currentGeneration = skillsSnapshot?.Generation ?? long.MinValue;
                if (result.Generation < minimumSkillsGeneration
                    || result.Generation < currentGeneration
                    || (result.Generation == currentGeneration
                        && result.IsStale
                        && skillsSnapshot?.IsStale == false))
                {
                    return;
                }

                accepted = true;
                skillsLoadFailure = null;
                skillsSnapshot = result;
                minimumSkillsGeneration = Math.Max(minimumSkillsGeneration, result.Generation);
                skillsRefreshPending = result.IsStale;
                skillsSnapshotExpiresAt = utcNow().AddSeconds(60);
                if (!string.IsNullOrEmpty(ComposerText)
                    && ComposerText[0] == '/'
                    && !ComposerText.StartsWith("//", StringComparison.Ordinal))
                {
                    UpdateComposerSuggestions(ComposerText);
                }
            }).ConfigureAwait(false);

            if (accepted)
            {
                return;
            }

            // Only a generation mismatch retries here. A stale cache result is final for this
            // call because the Worker owns its single background refresh and emits skills/changed
            // after that refresh succeeds.
            forceReload = true;
        }

        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            skillsRefreshPending = false;
            skillsLoadFailure = "The cached skill catalog could not be refreshed.";
            skillsSnapshotExpiresAt = utcNow().AddSeconds(60);
            if (!string.IsNullOrEmpty(ComposerText)
                && ComposerText[0] == '/'
                && !ComposerText.StartsWith("//", StringComparison.Ordinal))
            {
                UpdateComposerSuggestions(ComposerText);
            }
        }).ConfigureAwait(false);
    }

    private Task OnSkillsChangedAsync(WorkerNotification<SkillsChangedEvent> notification)
        // Worker notifications arrive on the StreamJsonRpc dispatch thread; mutating
        // skillsRefreshPending/skillsLoadFailure and rebuilding SlashCommands.Suggestions
        // from that thread races with LoadSkillsAsync's own OnUiAsync-marshaled writes and
        // touches Remote UI-bound collections off the UI thread.
        => OnUiAsync(() =>
        {
            if (!IsNotificationCurrent(notification))
            {
                return;
            }

            SkillsChangedEvent value = notification.Value;
            minimumSkillsGeneration = Math.Max(minimumSkillsGeneration, value.Generation);
            skillsRefreshPending = true;
            skillsLoadFailure = null;
            skillsSnapshotExpiresAt = default;
            if (!string.IsNullOrEmpty(ComposerText) && ComposerText[0] == '/')
            {
                UpdateComposerSuggestions(ComposerText);
            }

            _ = EnsureSkillsLoadedAsync();
        });

    private static bool SkillMatches(SkillInfo skill, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        string name = skill.Name;
        string display = skill.DisplayName ?? string.Empty;
        return name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || display.Contains(query, StringComparison.OrdinalIgnoreCase)
            || skill.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static int SkillRank(SkillInfo skill, string query)
    {
        if (query.Length == 0) return 6;
        string display = skill.DisplayName ?? string.Empty;
        if (string.Equals(skill.Name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(display, query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (skill.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (display.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (IsWordBoundary(skill.Name, query) || IsWordBoundary(display, query)) return 4;
        if (skill.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || display.Contains(query, StringComparison.OrdinalIgnoreCase)) return 5;
        return 6;
    }

    private static bool IsWordBoundary(string value, string query)
        => value.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase));

    private static int ScopeRank(string scope)
        => scope.ToLowerInvariant() switch
        {
            "repo" => 0,
            "user" => 1,
            "system" => 2,
            "admin" => 3,
            _ => 4,
        };

    private static string FormatSkillScope(string scope)
        => scope.ToLowerInvariant() switch
        {
            "repo" => "Repository",
            "user" => "User",
            "system" => "System",
            "admin" => "Admin",
            _ => "Unknown",
        };

    internal async Task PopulatePermissionProfilesAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        ListPermissionProfilesResult result;
        try
        {
            result = await bridge.ListPermissionProfilesAsync(StampOwner(new ListPermissionProfilesRequest(), owner), lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Permission profile discovery failed; preserving the saved selection", ex);
            return;
        }

        // Unsupported, transient, or incomplete catalogs are not authoritative. Preserve both
        // the current picker state and saved stable ID until a complete successful response.
        if (!result.IsSupported || result.IsTruncated)
        {
            return;
        }

        ApprovalModeOption[] discovered = result.Profiles
            .Where(static profile => profile.Allowed && IsValidPermissionProfileId(profile.Id))
            .GroupBy(static profile => profile.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                PermissionProfileInfo profile = group.First();
                string safeId = markdown.ToSafeText(profile.Id).Trim();
                string safeDescription = markdown.ToSafeText(profile.Description ?? string.Empty).Trim();
                return new ApprovalModeOption(
                    $"permission:{profile.Id}",
                    $"Permission: {safeId}",
                    safeDescription.Length == 0 ? "Codex permission profile." : safeDescription,
                    null,
                    null,
                    null,
                    "PermissionProfile",
                    profile.Id);
            })
            .ToArray();

        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            if (!IsCurrentOwner(owner))
            {
                return;
            }

            string savedId = settings.ApprovalModeId;
            foreach (ApprovalModeOption option in discovered)
            {
                ApprovalModeOption? existing = FindApprovalMode(option.Id);
                if (existing is null)
                {
                    ApprovalModes.Add(option);
                }
                else if (string.Equals(existing.Source, "Loading", StringComparison.Ordinal))
                {
                    ApprovalModes.Insert(ApprovalModes.IndexOf(existing), option);
                    if (string.Equals(savedId, option.Id, StringComparison.Ordinal))
                    {
                        ApplyApprovalMode(option);
                    }

                    ApprovalModes.Remove(existing);
                }
            }

            ApprovalModeOption? savedOption = FindApprovalMode(savedId);
            if (savedOption is null && savedId.StartsWith("permission:", StringComparison.Ordinal))
            {
                // Move the selection and persistence first. Removing an active item first makes
                // the Remote UI ComboBox write null back and can erase a newer stable selection.
                ApplyApprovalMode(FindApprovalMode(ApprovalModeCatalog.CustomId)!);
            }
            else if (savedOption is not null)
            {
                ApplyApprovalMode(savedOption);
            }

            for (int index = ApprovalModes.Count - 1; index >= 0; index--)
            {
                ApprovalModeOption option = ApprovalModes[index];
                if ((string.Equals(option.Source, "PermissionProfile", StringComparison.Ordinal)
                        || string.Equals(option.Source, "Loading", StringComparison.Ordinal))
                    && !discovered.Any(candidate => string.Equals(candidate.Id, option.Id, StringComparison.Ordinal)))
                {
                    ApprovalModes.RemoveAt(index);
                }
            }
        }).ConfigureAwait(false);
    }

    private static bool IsValidPermissionProfileId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string trimmed = id.Trim();
        return string.Equals(id, trimmed, StringComparison.Ordinal)
            && trimmed.Length <= 256
            && !trimmed.Any(char.IsControl);
    }

    private static bool TryGetFileSuggestionQuery(string text, out string query)
    {
        query = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int tokenStart = text.LastIndexOfAny([' ', '\t', '\r', '\n']) + 1;
        string token = text[tokenStart..];
        if (token.Length == 0
            || token[0] != '#'
            || token.StartsWith("##", StringComparison.Ordinal))
        {
            return false;
        }

        query = token[1..];
        return true;
    }

    private static string UnescapeFinalFileToken(string text)
    {
        int tokenStart = text.LastIndexOfAny([' ', '\t', '\r', '\n']) + 1;
        return text.AsSpan(tokenStart).StartsWith("##", StringComparison.Ordinal)
            ? string.Concat(text.AsSpan(0, tokenStart), text.AsSpan(tokenStart + 1))
            : text;
    }

    private void QueueFileSuggestionRefresh(string query)
    {
        CancelFileSuggestionRefresh();
        var refresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        fileSuggestionRefresh = refresh;
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        _ = RefreshFileSuggestionsAsync(query, refresh, owner);
    }

    private async Task RefreshFileSuggestionsAsync(string query, CancellationTokenSource refresh, OwnerSnapshot owner)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), refresh.Token).ConfigureAwait(false);
            string? workspaceRoot = workingDirectory
                ?? await workspaceDirectoryResolver.TryResolveFromWorkspaceAsync(refresh.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                await OnUiAsync(() =>
                {
                    if (IsFileSuggestionRefreshCurrent(refresh, owner))
                    {
                        FileSuggestions.CloseSuggestions();
                    }
                }).ConfigureAwait(false);
                return;
            }

            if (!IsFileSuggestionRefreshCurrent(refresh, owner))
            {
                return;
            }

            IReadOnlyList<WorkspaceFileSearchResult> results = await workspaceFileSearchService
                .SearchAsync(workspaceRoot, query, refresh.Token)
                .ConfigureAwait(false);
            if (!IsFileSuggestionRefreshCurrent(refresh, owner))
            {
                return;
            }

            FileSuggestionDescriptor[] descriptors = results
                .Select(result => new FileSuggestionDescriptor(
                    result.Path,
                    Path.GetFileName(result.Path),
                    result.DisplayPath))
                .ToArray();
            await OnUiAsync(() =>
            {
                if (IsFileSuggestionRefreshCurrent(refresh, owner))
                {
                    FileSuggestions.ShowSuggestions(descriptors);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsFileSuggestionRefreshCurrent(refresh, owner))
            {
                return;
            }

            ExtensionDiagnostics.Write("Refreshing file suggestions failed", ex);
            await OnUiAsync(() =>
            {
                if (IsFileSuggestionRefreshCurrent(refresh, owner))
                {
                    FileSuggestions.CloseSuggestions();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            CompleteFileSuggestionRefresh(ref fileSuggestionRefresh, refresh);
        }
    }

    internal static void CompleteFileSuggestionRefresh(
        ref CancellationTokenSource? currentRefresh,
        CancellationTokenSource completedRefresh)
    {
        CancellationTokenSource? released = Interlocked.CompareExchange(
            ref currentRefresh,
            null,
            completedRefresh);
        if (ReferenceEquals(released, completedRefresh))
        {
            completedRefresh.Dispose();
        }
    }

    private void CancelFileSuggestionRefresh()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref fileSuggestionRefresh, null);
        if (previous is null)
        {
            return;
        }

        previous.Cancel();
        previous.Dispose();
    }

    private Task OnFileSuggestionAcceptedAsync(FileSuggestionViewModel suggestion)
    {
        if (!TryAddPendingAttachment(suggestion.FullPath))
        {
            FileSuggestions.CloseSuggestions();
            return Task.CompletedTask;
        }

        string text = UnescapeFinalFileToken(ComposerText);
        int tokenStart = text.LastIndexOfAny([' ', '\t', '\r', '\n']) + 1;
        string replacement = string.Concat("#", suggestion.DisplayName, " ");
        SetComposerText(string.Concat(text.AsSpan(0, tokenStart), replacement));
        return Task.CompletedTask;
    }

    private SlashCommandSuggestionDescriptor CreateSlashCommandSuggestion(SlashCommandDefinition definition)
    {
        bool isAvailable = !unavailableSlashCommands.Contains(definition.Id);
        string unavailableReason = isAvailable
            ? string.Empty
            : $"The /{definition.Name} command is unavailable for this app-server session.";
        SlashCommandOptionDescriptor[]? options = definition.Id switch
        {
            SlashCommandId.Goal =>
            [
                new("get", "Show", "Show the current goal."),
                new("set", "Set", "Set a new goal objective."),
                new("edit", "Edit", "Replace the current goal objective."),
                new("pause", "Pause", "Pause the current goal."),
                new("resume", "Resume", "Resume the current goal."),
                new("clear", "Clear", "Clear the current goal."),
            ],
            SlashCommandId.Review =>
            [
                new("uncommitted", "Uncommitted changes", "Review working-tree changes."),
                new("base", "Base branch", "Review changes against a base branch."),
                new("commit", "Commit", "Review a specific commit."),
                new("custom", "Custom", "Review using custom instructions."),
            ],
            SlashCommandId.Model => Models
                .Select(model => new SlashCommandOptionDescriptor(model, model))
                .ToArray(),
            SlashCommandId.Personality =>
            [
                new("none", "None"),
                new("friendly", "Friendly"),
                new("pragmatic", "Pragmatic"),
            ],
            SlashCommandId.Reasoning => ReasoningEfforts
                .Skip(1)
                .Select(effort => new SlashCommandOptionDescriptor(
                    effort.Id,
                    effort.DisplayText,
                    effort.Description))
                .ToArray(),
            SlashCommandId.Permissions => ApprovalModes
                .Select(mode => new SlashCommandOptionDescriptor(mode.Id, mode.DisplayText, mode.Description))
                .ToArray(),
            _ => null,
        };

        if (definition.Id == SlashCommandId.Personality && GetSelectedModelInfo()?.SupportsPersonality != true)
        {
            isAvailable = false;
            unavailableReason = "The selected model does not support personality settings.";
        }
        else if (definition.Id == SlashCommandId.Reasoning && (options is null || options.Length == 0))
        {
            isAvailable = false;
            unavailableReason = "The selected model did not report supported reasoning efforts.";
        }
        else if (definition.Id == SlashCommandId.Fast && !SelectedModelSupportsFastTier())
        {
            isAvailable = false;
            unavailableReason = "The selected model did not report a fast service tier.";
        }

        string argumentHint = definition.Id switch
        {
            SlashCommandId.Feedback => "Feedback reason",
            SlashCommandId.Goal => "Goal objective for Set or Edit",
            SlashCommandId.Review => "Branch, commit, or custom instructions",
            SlashCommandId.Plan => "Optional prompt to start in Plan mode",
            _ => string.Empty,
        };
        bool showArgumentInput = definition.ArgumentKind is
            SlashCommandArgumentKind.OptionalText
            or SlashCommandArgumentKind.RequiredText
            or SlashCommandArgumentKind.GoalOperation
            or SlashCommandArgumentKind.ReviewTarget;
        return new SlashCommandSuggestionDescriptor(
            string.Concat("/", definition.Name),
            definition.Description,
            argumentHint,
            showArgumentInput,
            isAvailable,
            unavailableReason,
            options);
    }

    private ModelInfo? GetSelectedModelInfo()
    {
        if (string.Equals(modelCatalog.DefaultModelInfo?.Id, SelectedModel, StringComparison.Ordinal))
        {
            return modelCatalog.DefaultModelInfo;
        }

        return modelCatalog.Models.FirstOrDefault(
            model => string.Equals(model.Id, SelectedModel, StringComparison.Ordinal));
    }

    private void RefreshServiceTiers()
    {
        IReadOnlyList<ServiceTierOption> options = ServiceTierCatalog.Create(GetSelectedModelInfo(), markdown);
        ServiceTierCatalog.Merge(ServiceTiers, options);
        ServiceTierOption? configured = ServiceTiers.FirstOrDefault(option =>
            string.Equals(option.Id, settings.ServiceTierId, StringComparison.OrdinalIgnoreCase));
        string displayedId = configured?.Id ?? ServiceTierCatalog.DefaultId;
        if (!string.Equals(selectedServiceTierId, displayedId, StringComparison.Ordinal))
        {
            selectedServiceTierId = displayedId;
            OnPropertyChanged(nameof(SelectedServiceTierId));
        }

        OnPropertyChanged(nameof(HasServiceTiers));
        OnPropertyChanged(nameof(ServiceTierHelpText));
    }

    private void RefreshReasoningEfforts()
    {
        IReadOnlyList<ReasoningEffortOption> options = ReasoningEffortCatalog.Create(GetSelectedModelInfo(), markdown);
        ReasoningEffortCatalog.Merge(ReasoningEfforts, options);
        selectedReasoningEffortId = FindReasoningEffort(settings.ReasoningEffortId)?.Id
            ?? ReasoningEffortCatalog.DefaultId;
        OnPropertyChanged(nameof(SelectedReasoningEffortId));
        OnPropertyChanged(nameof(HasReasoningEfforts));
        OnPropertyChanged(nameof(ReasoningEffortHelpText));
    }

    private ReasoningEffortOption? FindReasoningEffort(string? id)
        => id is null
            ? null
            : ReasoningEfforts.FirstOrDefault(option =>
                string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

    private TurnSettingResolution ResolveReasoningSetting(string threadId)
    {
        if (pendingReasoningByThread.TryGetValue(threadId, out PendingReasoningOverride pending))
        {
            if (SelectedModelSupportsReasoningEffort(pending.Effort))
            {
                return new TurnSettingResolution(true, pending.Effort);
            }
        }

        if (reasoningRestoreByThread.TryGetValue(threadId, out string? restore))
        {
            if (restore is null || SelectedModelSupportsReasoningEffort(restore))
            {
                return new TurnSettingResolution(true, restore);
            }
        }

        ReasoningEffortOption? persistent = FindReasoningEffort(settings.ReasoningEffortId);
        return persistent is null || persistent.Id.Length == 0
            ? new TurnSettingResolution(false, null)
            : new TurnSettingResolution(true, persistent.Id);
    }

    private bool SelectedModelSupportsReasoningEffort(string effort)
        => GetSelectedModelInfo()?.SupportedReasoningEfforts.Any(option =>
            string.Equals(option.Id, effort, StringComparison.OrdinalIgnoreCase)) == true;

    private bool SelectedModelSupportsFastTier()
        => GetSelectedModelInfo()?.ServiceTiers.Any(
            tier => string.Equals(tier.Id, "fast", StringComparison.OrdinalIgnoreCase)) == true;

    private Task OnSlashSuggestionAcceptedAsync(SlashCommandSuggestionViewModel suggestion)
    {
        if (suggestion.IsSkill)
        {
            if (!skillSelections.TryGetValue(suggestion.SelectionId, out SkillInfo? skill))
            {
                SlashCommands.ShowFailure("The selected skill is no longer available. Reopen the menu and try again.");
                return Task.CompletedTask;
            }

            var invocation = new SkillInvocationInfo
            {
                Name = skill.Name,
                Scope = skill.Scope,
                Path = skill.Path,
            };
            PendingSkillViewModel chip = new(
                string.IsNullOrWhiteSpace(skill.DisplayName) ? skill.Name : skill.DisplayName!,
                FormatSkillScope(skill.Scope),
                skill.Description,
                invocation,
                RemovePendingSkillAsync,
                skill.DefaultPrompt,
                UsePendingSkillPromptAsync,
                markdown);
            PendingSkills.Clear();
            PendingSkills.Add(chip);
            // Only the slash query is removed. The normal composer remains visible so the
            // user can add text, or start a skill-only turn when it is empty.
            SetComposerText(string.Empty);
            SlashCommands.ShowStatus("Skill selected. Add text or send when ready.");
            return Task.CompletedTask;
        }

        SetComposerText(string.Empty);
        return Task.CompletedTask;
    }

    private Task RemovePendingSkillAsync()
    {
        PendingSkills.Clear();
        SlashCommands.ShowStatus("Skill removed. Text-only sending is available again.");
        return Task.CompletedTask;
    }

    private Task UsePendingSkillPromptAsync(string prompt)
    {
        if (!IsComposerEmpty)
        {
            SlashCommands.ShowStatus("Clear the composer before using the suggested prompt.");
            return Task.CompletedTask;
        }

        SetComposerText(prompt);
        // A suggested prompt is ordinary composer text. If it begins with '/', keep the
        // integrated menu closed until the user explicitly types a new slash trigger.
        SlashCommands.CloseSuggestions();
        return Task.CompletedTask;
    }

    private void ClearSentSkill(SkillInvocationInfo? skill)
    {
        if (skill is null || PendingSkills.Count == 0)
        {
            return;
        }

        SkillInvocationInfo pending = PendingSkills[0].Invocation;
        if (string.Equals(pending.Name, skill.Name, StringComparison.Ordinal)
            && string.Equals(pending.Scope, skill.Scope, StringComparison.Ordinal)
            && string.Equals(pending.Path, skill.Path, StringComparison.Ordinal))
        {
            PendingSkills.Clear();
        }
    }

    private Task OnSlashCommandClearedAsync()
        => Task.CompletedTask;

    private async Task<bool> ExecuteSlashSubmissionAsync(SlashCommandSubmission submission)
    {
        string commandName = submission.CommandName.TrimStart('/');
        string arguments = BuildSlashSubmissionArguments(commandName, submission.OptionValue, submission.ArgumentText);
        SlashCommandParseResult parseResult = slashCommandParser.Parse(
            string.IsNullOrEmpty(arguments)
                ? string.Concat("/", commandName)
                : string.Concat("/", commandName, " ", arguments));
        if (parseResult.Kind != SlashCommandParseKind.Command || parseResult.Invocation is null)
        {
            await ShowSlashFailureAsync(parseResult.ErrorMessage ?? "The slash command could not be parsed.").ConfigureAwait(false);
            return false;
        }

        return await ScheduleOrExecuteSlashCommandAsync(parseResult.Invocation).ConfigureAwait(false);
    }

    private static string BuildSlashSubmissionArguments(
        string commandName,
        string? optionValue,
        string argumentText)
    {
        string argument = argumentText.Trim();
        if (string.Equals(commandName, "goal", StringComparison.OrdinalIgnoreCase))
        {
            return optionValue switch
            {
                null or "get" => string.Empty,
                "set" or "edit" => string.IsNullOrEmpty(argument)
                    ? optionValue
                    : string.Concat(optionValue, " ", argument),
                _ => optionValue,
            };
        }

        if (string.Equals(commandName, "review", StringComparison.OrdinalIgnoreCase))
        {
            return optionValue switch
            {
                null or "uncommitted" => "uncommitted",
                _ => string.IsNullOrEmpty(argument)
                    ? optionValue
                    : string.Concat(optionValue, " ", argument),
            };
        }

        return optionValue ?? argument;
    }

    private async Task<bool> ScheduleOrExecuteSlashCommandAsync(SlashCommandInvocation invocation)
    {
        if (SelectedThread is not null && !IsThreadJoined)
        {
            await ShowSlashFailureAsync("This conversation is read-only. Join it explicitly before running commands.").ConfigureAwait(false);
            return false;
        }

        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (RequiresWorkerConnection(invocation.Definition.Id)
            && Status.State is not (WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval))
        {
            bool connected = await ConnectAsync().ConfigureAwait(false);
            if (!connected)
            {
                return false;
            }

            if (!IsCurrentOwner(owner))
            {
                return false;
            }
        }

        string? targetThreadId = SelectedThread?.Id ?? Status.ThreadId;
        bool requiresThread = RequiresThread(invocation);
        if (requiresThread && targetThreadId is null)
        {
            await NewThreadAsync().ConfigureAwait(false);
            if (!IsCurrentOwner(owner))
            {
                return false;
            }

            targetThreadId = SelectedThread?.Id;
        }

        if (requiresThread && targetThreadId is null)
        {
            await ShowSlashFailureAsync("The slash command requires an active thread.").ConfigureAwait(false);
            return false;
        }

        SlashCommandQueueDecision decision = slashCommandCoordinator.Schedule(
            invocation,
            targetThreadId,
            Status.TurnId is not null);
        if (decision.Kind == SlashCommandQueueDecisionKind.QueueFull)
        {
            await ShowSlashFailureAsync(decision.Message).ConfigureAwait(false);
            return false;
        }

        if (decision.Kind is SlashCommandQueueDecisionKind.Queued or SlashCommandQueueDecisionKind.Replaced)
        {
            await ShowSlashStatusAsync(decision.Message).ConfigureAwait(false);
            return true;
        }

        return await ExecuteSlashCommandAsync(invocation, targetThreadId, owner).ConfigureAwait(false);
    }

    private static bool RequiresThread(SlashCommandInvocation invocation)
        => invocation.Definition.Id is
            SlashCommandId.Compact
            or SlashCommandId.Fork
            or SlashCommandId.Fast
            or SlashCommandId.Goal
            or SlashCommandId.Reasoning
            or SlashCommandId.Review
            || (invocation.Definition.Id == SlashCommandId.Plan
                && !string.IsNullOrWhiteSpace(invocation.Arguments));

    private static bool RequiresWorkerConnection(SlashCommandId commandId)
        => commandId is
            SlashCommandId.Compact
            or SlashCommandId.Feedback
            or SlashCommandId.Fork
            or SlashCommandId.Goal
            or SlashCommandId.Mcp
            or SlashCommandId.Review;

    private async Task<bool> ExecuteSlashCommandAsync(
        SlashCommandInvocation invocation,
        string? targetThreadId,
        OwnerSnapshot? expectedOwner = null)
    {
        if (expectedOwner is { } owner && !IsCurrentOwner(owner))
        {
            return false;
        }

        try
        {
            return invocation.Definition.Id switch
            {
                SlashCommandId.Compact => await ExecuteCompactAsync(targetThreadId!).ConfigureAwait(false),
                SlashCommandId.Feedback => await ExecuteFeedbackAsync(invocation.Arguments, targetThreadId).ConfigureAwait(false),
                SlashCommandId.Fork => await ExecuteForkAsync(targetThreadId!).ConfigureAwait(false),
                SlashCommandId.Goal => await ExecuteGoalAsync(targetThreadId!, invocation.Arguments).ConfigureAwait(false),
                SlashCommandId.Mcp => await ExecuteMcpAsync(targetThreadId).ConfigureAwait(false),
                SlashCommandId.Review => await ExecuteReviewAsync(targetThreadId!, invocation.Arguments).ConfigureAwait(false),
                SlashCommandId.Fast => await ExecuteFastAsync(targetThreadId!).ConfigureAwait(false),
                SlashCommandId.Model => await ExecuteModelAsync(invocation.Arguments).ConfigureAwait(false),
                SlashCommandId.Personality => await ExecutePersonalityAsync(invocation.Arguments).ConfigureAwait(false),
                SlashCommandId.Plan => await ExecutePlanAsync(targetThreadId!, invocation.Arguments).ConfigureAwait(false),
                SlashCommandId.Reasoning => await ExecuteReasoningAsync(targetThreadId!, invocation.Arguments).ConfigureAwait(false),
                SlashCommandId.IdeContext => await ExecuteIdeContextAsync().ConfigureAwait(false),
                SlashCommandId.Init => await ExecuteInitAsync().ConfigureAwait(false),
                SlashCommandId.Status => await ExecuteStatusAsync(targetThreadId).ConfigureAwait(false),
                SlashCommandId.Permissions => await ExecutePermissionsAsync(invocation.Arguments).ConfigureAwait(false),
                _ => false,
            };
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write($"Slash command /{invocation.Definition.Name} failed", ex);
            await ShowSlashFailureAsync(
                $"The /{invocation.Definition.Name} command failed. See diagnostics.log.").ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> ExecuteCompactAsync(string threadId)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        CompactThreadResult result = await bridge.CompactThreadAsync(
            StampOwner(new CompactThreadRequest { ThreadId = threadId }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        return await HandleOperationResultAsync(SlashCommandId.Compact, result, "Context compaction started.").ConfigureAwait(false);
    }

    private async Task<bool> ExecuteFeedbackAsync(string arguments, string? threadId)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        string reason = arguments.Trim();
        if (reason.Length == 0)
        {
            await ShowSlashFailureAsync("Enter feedback text after /feedback.").ConfigureAwait(false);
            return false;
        }

        if (!await ConfirmFeedbackAsync(reason).ConfigureAwait(false))
        {
            await ShowSlashStatusAsync("Feedback submission was canceled.").ConfigureAwait(false);
            return false;
        }

        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        UploadFeedbackResult result = await bridge.UploadFeedbackAsync(
            StampOwner(new UploadFeedbackRequest
            {
                Classification = "visual-studio",
                Reason = reason,
                IncludeLogs = false,
                ThreadId = threadId,
            }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return false;
        }
        return await HandleOperationResultAsync(SlashCommandId.Feedback, result, "Feedback was uploaded.").ConfigureAwait(false);
    }

    private async Task<bool> ConfirmFeedbackAsync(string reason)
    {
        if (extensibility is null)
        {
            return false;
        }

        FeedbackSubmissionChoice choice = await extensibility.Shell().ShowPromptAsync(
            $"Send this feedback to Codex?\r\n\r\n{reason}\r\n\r\nExtension logs will not be included.",
            new PromptOptions<FeedbackSubmissionChoice>
            {
                Choices =
                {
                    { "Send feedback", FeedbackSubmissionChoice.Send },
                    { "Cancel", FeedbackSubmissionChoice.Cancel },
                },
                DefaultChoiceIndex = 1,
                DismissedReturns = FeedbackSubmissionChoice.Cancel,
                Title = "Send Codex Feedback",
            },
            lifetime.Token).ConfigureAwait(false);
        return choice == FeedbackSubmissionChoice.Send;
    }

    private async Task<bool> ExecuteForkAsync(string threadId)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        ForkThreadResult result = await bridge.ForkThreadAsync(
            StampOwner(new ForkThreadRequest { ThreadId = threadId }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        if (!await EnsureOperationSupportedAsync(SlashCommandId.Fork, result).ConfigureAwait(false))
        {
            return false;
        }

        if (result.Thread is null)
        {
            await ShowSlashFailureAsync("The app-server did not return the forked thread.").ConfigureAwait(false);
            return false;
        }

        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            Threads.Insert(0, result.Thread);
            // Selection loads read-only history. Joining the forked conversation remains an
            // explicit user action, just like selecting any other history entry.
            SelectedThread = result.Thread;
        }).ConfigureAwait(false);
        await ShowSlashStatusAsync("Forked the thread and switched to the new copy.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteGoalAsync(string threadId, string arguments)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (!SlashCommandArgumentParser.TryParseGoal(arguments, out GoalCommandArguments? goalArguments, out string? error)
            || goalArguments is null)
        {
            await ShowSlashFailureAsync(error ?? "The goal command is invalid.").ConfigureAwait(false);
            return false;
        }

        ThreadGoalResult result;
        switch (goalArguments.Operation)
        {
            case GoalCommandOperation.Get:
                result = await bridge.GetThreadGoalAsync(StampOwner(new ThreadGoalRequest { ThreadId = threadId }, owner), lifetime.Token).ConfigureAwait(false);
                break;
            case GoalCommandOperation.Clear:
                result = await bridge.ClearThreadGoalAsync(StampOwner(new ThreadGoalRequest { ThreadId = threadId }, owner), lifetime.Token).ConfigureAwait(false);
                break;
            case GoalCommandOperation.Set:
            case GoalCommandOperation.Edit:
                result = await bridge.SetThreadGoalAsync(
                    StampOwner(new SetThreadGoalRequest
                    {
                        ThreadId = threadId,
                        Objective = goalArguments.Objective,
                        Status = ThreadGoalStatus.Active,
                    }, owner),
                    lifetime.Token).ConfigureAwait(false);
                break;
            case GoalCommandOperation.Pause:
            case GoalCommandOperation.Resume:
                ThreadGoalResult current = await bridge.GetThreadGoalAsync(StampOwner(new ThreadGoalRequest { ThreadId = threadId }, owner), lifetime.Token).ConfigureAwait(false);
                if (!IsCurrentOwner(owner))
                {
                    return false;
                }
                if (!await EnsureOperationSupportedAsync(SlashCommandId.Goal, current).ConfigureAwait(false))
                {
                    return false;
                }

                if (current.Goal is null)
                {
                    await ShowSlashFailureAsync("No goal is set for this thread.").ConfigureAwait(false);
                    return false;
                }

                result = await bridge.SetThreadGoalAsync(
                    StampOwner(new SetThreadGoalRequest
                    {
                        ThreadId = threadId,
                        Objective = current.Goal.Objective,
                        TokenBudget = current.Goal.TokenBudget,
                        Status = goalArguments.Operation == GoalCommandOperation.Pause
                            ? ThreadGoalStatus.Paused
                            : ThreadGoalStatus.Active,
                    }, owner),
                    lifetime.Token).ConfigureAwait(false);
                break;
            default:
                return false;
        }

        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        if (!await EnsureOperationSupportedAsync(SlashCommandId.Goal, result).ConfigureAwait(false))
        {
            return false;
        }

        await ShowSlashStatusAsync(FormatGoal(result)).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteMcpAsync(string? threadId)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        McpServerListResult result = await bridge.ListMcpServersAsync(StampOwner(new ListMcpServersRequest { ThreadId = threadId }, owner), lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        if (!await EnsureOperationSupportedAsync(SlashCommandId.Mcp, result).ConfigureAwait(false))
        {
            return false;
        }

        string message = result.Servers.Count == 0
            ? "No MCP servers are configured."
            : string.Join(
                "\r\n",
                result.Servers.Select(server =>
                    $"- {server.DisplayName ?? server.Name}: {server.AuthStatus}; {server.ToolNames.Count} tools, {server.ResourceCount} resources, {server.ResourceTemplateCount} templates"));
        await ShowSlashStatusAsync(message).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteReviewAsync(string threadId, string arguments)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (!SlashCommandArgumentParser.TryParseReview(arguments, out ReviewCommandArguments? reviewArguments, out string? error)
            || reviewArguments is null)
        {
            await ShowSlashFailureAsync(error ?? "The review target is invalid.").ConfigureAwait(false);
            return false;
        }

        StartReviewResult result = await bridge.StartReviewAsync(
            StampOwner(new StartReviewRequest
            {
                ThreadId = threadId,
                Target = new ReviewTarget
                {
                    Kind = reviewArguments.Target switch
                    {
                        ReviewCommandTargetKind.UncommittedChanges => ReviewTargetKind.UncommittedChanges,
                        ReviewCommandTargetKind.BaseBranch => ReviewTargetKind.BaseBranch,
                        ReviewCommandTargetKind.Commit => ReviewTargetKind.Commit,
                        ReviewCommandTargetKind.Custom => ReviewTargetKind.Custom,
                        _ => ReviewTargetKind.UncommittedChanges,
                    },
                    Value = reviewArguments.Value,
                },
            }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return false;
        }
        return await HandleOperationResultAsync(SlashCommandId.Review, result, "Code review started.").ConfigureAwait(false);
    }

    private async Task<bool> ExecuteFastAsync(string threadId)
    {
        ServiceTierInfo? fastTier = GetSelectedModelInfo()?.ServiceTiers.FirstOrDefault(
            tier => string.Equals(tier.Id, "fast", StringComparison.OrdinalIgnoreCase));
        if (fastTier is null)
        {
            await ShowSlashFailureAsync("The selected model does not support the fast service tier.").ConfigureAwait(false);
            return false;
        }

        string? restoreTier;
        if (pendingServiceTierByThread.TryGetValue(threadId, out PendingServiceTierOverride? existing))
        {
            restoreTier = existing.RestoreTier;
        }
        else if (serviceTierRestoreByThread.TryGetValue(threadId, out string? queuedRestore))
        {
            restoreTier = queuedRestore;
        }
        else
        {
            ServiceTierOption? configured = ServiceTiers.FirstOrDefault(option =>
                option.Id.Length > 0
                && string.Equals(option.Id, settings.ServiceTierId, StringComparison.OrdinalIgnoreCase));
            restoreTier = configured?.Id ?? GetEffectiveServiceTier(threadId);
        }

        pendingServiceTierByThread[threadId] = new PendingServiceTierOverride(fastTier.Id, restoreTier);
        await ShowSlashStatusAsync("Fast service tier will be used for the next turn.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteModelAsync(string arguments)
    {
        string requested = arguments.Trim();
        string? model = requested.Length == 0
            ? null
            : Models.FirstOrDefault(candidate => string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            await ShowSlashFailureAsync("Select a model from the current model catalog.").ConfigureAwait(false);
            return false;
        }

        await OnUiAsync(() => SelectedModel = model).ConfigureAwait(false);
        await ShowSlashStatusAsync($"Model set to {model}.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecutePersonalityAsync(string arguments)
    {
        string personality = arguments.Trim().ToLowerInvariant();
        if (GetSelectedModelInfo()?.SupportsPersonality != true
            || personality is not ("none" or "friendly" or "pragmatic"))
        {
            await ShowSlashFailureAsync("Select none, friendly, or pragmatic for a model that supports personalities.").ConfigureAwait(false);
            return false;
        }

        nextPersonality = personality;
        await ShowSlashStatusAsync($"Personality set to {personality} for the next turn.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecutePlanAsync(string threadId, string arguments)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        string prompt = arguments.Trim();
        if (prompt.Length == 0)
        {
            nextCollaborationMode = "plan";
            await ShowSlashStatusAsync("Plan mode will be used for the next turn.").ConfigureAwait(false);
            return true;
        }

        await OnUiAsync(() =>
        {
            if (IsCurrentOwner(owner))
            {
                Items.Add(new ChatItemViewModel("You", markdown.ToSafeText(prompt), ConversationEventKind.ItemStarted));
            }
        }).ConfigureAwait(false);
        StartTurnRequest request = await CreateStartTurnRequestAsync(threadId, prompt, forcePlanMode: true).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        StampOwner(request, owner);
        await bridge.StartTurnAsync(request, lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            ClearSentAttachments(request.Attachments);
            ConsumeNextTurnSettings(request);
        }).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteReasoningAsync(string threadId, string arguments)
    {
        string effort = arguments.Trim();
        ModelInfo? model = GetSelectedModelInfo();
        ReasoningEffortInfo? matched = model?.SupportedReasoningEfforts.FirstOrDefault(
            option => string.Equals(option.Id, effort, StringComparison.OrdinalIgnoreCase));
        if (effort.Length == 0 || matched is null)
        {
            await ShowSlashFailureAsync("Select a reasoning effort supported by the current model.").ConfigureAwait(false);
            return false;
        }

        string? restoreEffort;
        if (pendingReasoningByThread.TryGetValue(threadId, out PendingReasoningOverride existing))
        {
            restoreEffort = existing.RestoreEffort;
        }
        else if (reasoningRestoreByThread.TryGetValue(threadId, out string? queuedRestore))
        {
            restoreEffort = queuedRestore;
        }
        else
        {
            ReasoningEffortOption? persistent = FindReasoningEffort(settings.ReasoningEffortId);
            restoreEffort = persistent is null || persistent.Id.Length == 0
                ? GetEffectiveReasoningEffort(threadId)
                : persistent.Id;
        }

        pendingReasoningByThread[threadId] = new PendingReasoningOverride(matched.Id, restoreEffort);
        await ShowSlashStatusAsync($"Reasoning effort set to {matched.Id} for the next turn in this thread.").ConfigureAwait(false);
        return true;
    }

    private string? GetEffectiveReasoningEffort(string threadId)
    {
        ThreadSummary? thread = SelectedThread;
        if (thread is not null && string.Equals(thread.Id, threadId, StringComparison.Ordinal))
        {
            return thread.EffectiveReasoningEffort;
        }

        return string.Equals(Status.ThreadId, threadId, StringComparison.Ordinal)
            ? Status.EffectiveReasoningEffort
            : null;
    }

    private async Task<bool> ExecuteIdeContextAsync()
    {
        ideContextEnabled = !ideContextEnabled;
        await ShowSlashStatusAsync(
            ideContextEnabled
                ? "IDE context is enabled for future turns."
                : "IDE context is disabled for future turns.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecuteInitAsync()
    {
        string? root = workingDirectory
            ?? await workspaceDirectoryResolver.TryResolveFromWorkspaceAsync(lifetime.Token).ConfigureAwait(false);
        AgentsFileInitializationResult result = await agentsFileInitializer.InitializeAsync(root, lifetime.Token).ConfigureAwait(false);
        if (result.Status is AgentsFileInitializationStatus.Failed or AgentsFileInitializationStatus.InvalidWorkspace)
        {
            await ShowSlashFailureAsync(result.Message).ConfigureAwait(false);
            return false;
        }

        await ShowSlashStatusAsync(result.Message).ConfigureAwait(false);
        return result.Status is AgentsFileInitializationStatus.Created or AgentsFileInitializationStatus.AlreadyExists;
    }

    private async Task<bool> ExecuteStatusAsync(string? threadId)
    {
        if (IsUsageAvailable)
        {
            await RefreshUsageAsync(force: true).ConfigureAwait(false);
        }

        string usage = Usage.SlashStatusText;
        string desiredReasoning = string.IsNullOrEmpty(settings.ReasoningEffortId)
            ? "(config.toml)"
            : markdown.ToSafeText(settings.ReasoningEffortId);
        TurnSettingResolution nextReasoning = threadId is null
            ? new TurnSettingResolution(false, null)
            : ResolveReasoningSetting(threadId);
        string nextReasoningText = nextReasoning.HasValue
            ? nextReasoning.Value ?? "(explicit null)"
            : "(config.toml)";
        string desiredServiceTier = string.IsNullOrEmpty(settings.ServiceTierId)
            ? "(config.toml)"
            : markdown.ToSafeText(settings.ServiceTierId);
        string effectiveServiceTier = threadId is null
            ? Status.EffectiveServiceTier ?? "(unknown)"
            : GetEffectiveServiceTier(threadId) ?? "(config/default)";
        TurnSettingResolution nextServiceTier = threadId is null
            ? new TurnSettingResolution(false, null)
            : ResolveServiceTierSetting(threadId);
        string pendingServiceTier = threadId is not null
            && pendingServiceTierByThread.TryGetValue(threadId, out PendingServiceTierOverride? pending)
                ? pending.Tier
                : "(none)";
        string nextServiceTierText = nextServiceTier.HasValue
            ? nextServiceTier.Value ?? "(explicit null)"
            : "(config.toml)";
        string message = string.Join(
            "\r\n",
            $"Connection: {Status.State}",
            $"Thread: {threadId ?? "(none)"}",
            $"Model: {SelectedModel ?? "(default)"}",
            $"Desired reasoning effort: {desiredReasoning}",
            $"Effective reasoning effort: {GetEffectiveReasoningEffort(threadId ?? string.Empty) ?? "(not reported)"}",
            $"Next-turn reasoning effort: {nextReasoningText}",
            $"Personality: {nextPersonality ?? "(default)"}",
            $"Desired service tier: {desiredServiceTier}",
            $"Effective service tier: {effectiveServiceTier}",
            $"Pending service-tier override: {pendingServiceTier}",
            $"Next-turn service tier: {nextServiceTierText}",
            $"Collaboration mode: {nextCollaborationMode ?? "default"}",
            $"Desired permissions: {DesiredApprovalModeText}",
            $"Effective permissions: {EffectiveApprovalModeText}",
            $"Next-turn approval policy: {GetTurnApprovalPolicy() ?? "(config.toml)"}",
            $"Next-turn sandbox: {GetTurnSandboxMode() ?? "(config.toml)"}",
            $"IDE context: {(ideContextEnabled ? "enabled" : "disabled")}",
            $"Queued commands: {slashCommandCoordinator.GetQueueCount(threadId)}",
            $"Usage: {usage}");
        await ShowSlashStatusAsync(message).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExecutePermissionsAsync(string arguments)
    {
        string requested = arguments.Trim();
        if (requested.Length == 0)
        {
            string choices = string.Join(", ", ApprovalModes.Select(mode => $"{mode.Id} ({mode.DisplayText})"));
            await ShowSlashStatusAsync(
                $"Desired permissions: {DesiredApprovalModeText}\r\nEffective permissions: {EffectiveApprovalModeText}\r\nAvailable: {choices}").ConfigureAwait(false);
            return true;
        }

        ApprovalModeOption? option = ApprovalModes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, requested, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            ApprovalModeOption[] displayMatches = ApprovalModes
                .Where(candidate => string.Equals(candidate.DisplayText, requested, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (displayMatches.Length > 1)
            {
                await ShowSlashFailureAsync("That display name is ambiguous. Select a mode by stable ID.").ConfigureAwait(false);
                return false;
            }

            option = displayMatches.SingleOrDefault();
        }

        if (option is null)
        {
            string availableIds = string.Join(", ", ApprovalModes.Select(mode => mode.Id));
            await ShowSlashFailureAsync($"Select a permission mode by stable ID. Available: {availableIds}.").ConfigureAwait(false);
            return false;
        }

        if (!IsApprovalModeEnabled)
        {
            await ShowSlashStatusAsync("Chat mode is fixed to a read-only sandbox without approval prompts. Switch to Agent mode to select permissions.").ConfigureAwait(false);
            return true;
        }

        RequestApprovalMode(option);
        await ShowSlashStatusAsync(HasApprovalModeConfirmation
            ? ApprovalModeConfirmationText
            : $"Permissions set to {option.DisplayText}.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleOperationResultAsync(
        SlashCommandId commandId,
        AppServerOperationResult result,
        string successMessage)
    {
        if (!await EnsureOperationSupportedAsync(commandId, result).ConfigureAwait(false))
        {
            return false;
        }

        await ShowSlashStatusAsync(successMessage).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> EnsureOperationSupportedAsync(
        SlashCommandId commandId,
        AppServerOperationResult result)
    {
        if (result.IsSupported)
        {
            return true;
        }

        unavailableSlashCommands.Add(commandId);
        string reason = string.IsNullOrWhiteSpace(result.UnavailableReason)
            ? "The app-server does not support this command."
            : result.UnavailableReason;
        await ShowSlashFailureAsync(reason).ConfigureAwait(false);
        UpdateComposerSuggestions(ComposerText);
        return false;
    }

    private static string FormatGoal(ThreadGoalResult result)
    {
        if (result.Cleared)
        {
            return "The thread goal was cleared.";
        }

        return result.Goal is null
            ? "No goal is set for this thread."
            : $"Goal: {result.Goal.Objective}\r\nStatus: {result.Goal.Status}\r\nTokens used: {result.Goal.TokensUsed}";
    }

    private Task ShowSlashStatusAsync(string message)
        => OnUiAsync(() =>
        {
            string safeMessage = markdown.ToSafeText(message);
            SlashCommands.ShowStatus(safeMessage);
            Items.Add(new ChatItemViewModel("Status", safeMessage, ConversationEventKind.ItemCompleted));
        });

    private Task ShowSlashFailureAsync(string message)
        => OnUiAsync(() =>
        {
            string safeMessage = markdown.ToSafeText(message);
            SlashCommands.ShowFailure(safeMessage);
            Items.Add(new ChatItemViewModel("Error", safeMessage, ConversationEventKind.Error));
        });

    // Drains the given thread queues followed by the session-scoped queue (commands queued
    // before any thread existed). A failed command has already been surfaced to the user, so
    // draining continues past it; only a successfully started turn pauses the drain, and the
    // next turn-completed state change resumes it.
    private async Task DrainSlashQueuesAsync(params string?[] threadIds)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if ((SelectedThread is not null && !IsThreadJoined)
            || Status.TurnId is not null
            || Interlocked.CompareExchange(ref drainingSlashQueue, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var queueKeys = new List<string?>();
            foreach (string? threadId in threadIds)
            {
                if (!string.IsNullOrWhiteSpace(threadId) && !queueKeys.Contains(threadId))
                {
                    queueKeys.Add(threadId);
                }
            }

            queueKeys.Add(null);
            foreach (string? queueKey in queueKeys)
            {
                // Thread-scoped queues belong to that selected, explicitly joined conversation.
                // Session commands remain drainable when no conversation is selected.
                if (queueKey is not null
                    && (!IsThreadJoined || !string.Equals(SelectedThread?.Id, queueKey, StringComparison.Ordinal)))
                {
                    continue;
                }

                // The session queue (queueKey null) holds commands issued before any thread
                // existed. By drain time a thread may already be selected, so target that
                // thread instead of leaving thread-optional commands (e.g. /status) contextless.
                string? executionThreadId = queueKey ?? SelectedThread?.Id;
                while (IsCurrentOwner(owner)
                    && (SelectedThread is null || IsThreadJoined)
                    && Status.TurnId is null
                    && slashCommandCoordinator.TryDequeue(queueKey, out SlashCommandInvocation? invocation)
                    && invocation is not null)
                {
                    bool succeeded = await ExecuteSlashCommandAsync(invocation, executionThreadId, owner).ConfigureAwait(false);
                    if (succeeded && invocation.StartsTurn)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref drainingSlashQueue, 0);
        }
    }

    private enum FeedbackSubmissionChoice
    {
        Cancel,
        Send,
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

    private string? GetTurnApprovalPolicy()
        => string.Equals(SelectedMode, "Chat", StringComparison.Ordinal)
            ? "never"
            : SelectedApprovalMode?.ApprovalPolicy;

    private string? GetTurnSandboxMode()
        => string.Equals(SelectedMode, "Chat", StringComparison.Ordinal)
            ? "readOnly"
            : SelectedApprovalMode?.SandboxMode;

    private string? GetTurnApprovalsReviewer()
        => string.Equals(SelectedMode, "Chat", StringComparison.Ordinal)
            ? "user"
            : SelectedApprovalMode?.ApprovalsReviewer;

    private string? GetTurnPermissions()
        => string.Equals(SelectedMode, "Chat", StringComparison.Ordinal)
            ? null
            : SelectedApprovalMode?.Permissions;

    private ApprovalModeOption? FindApprovalMode(string? id)
        => ApprovalModes.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));

    private void RequestApprovalMode(ApprovalModeOption option)
    {
        bool requiresFullAccessConfirmation = string.Equals(option.Id, ApprovalModeCatalog.FullAccessId, StringComparison.Ordinal);
        bool requiresNewThread = string.Equals(option.Id, ApprovalModeCatalog.CustomId, StringComparison.Ordinal)
            && SelectedThread is not null
            && HasPerTurnApprovalOverride(selectedApprovalMode);
        if (requiresFullAccessConfirmation || requiresNewThread)
        {
            approvalModeBeforeConfirmationId = selectedApprovalMode?.Id ?? ApprovalModeCatalog.CustomId;
            BeginApprovalModeConfirmation(option, requiresNewThread);
            OnPropertyChanged(nameof(SelectedApprovalMode));
            OnPropertyChanged(nameof(SelectedApprovalModeId));
            return;
        }

        ApplyApprovalMode(option);
    }

    private void BeginApprovalModeConfirmation(ApprovalModeOption option, bool startsNewThread)
    {
        pendingApprovalMode = option;
        confirmationStartsNewThread = startsNewThread;
        ApprovalModeConfirmationText = startsNewThread
            ? "Custom cannot reset approval overrides already applied to this thread. Start a new thread and use config.toml settings?"
            : "Full access disables the Codex sandbox and normal approval prompts. Operations may run without any request reaching the extension approval policy. Continue?";
        OnPropertyChanged(nameof(HasApprovalModeConfirmation));
        ConfirmApprovalModeCommand.RaiseCanExecuteChanged();
        CancelApprovalModeCommand.RaiseCanExecuteChanged();
    }

    private async Task ConfirmApprovalModeAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        ApprovalModeOption? option = pendingApprovalMode;
        bool startNewThread = confirmationStartsNewThread;
        if (option is null || !IsCurrentOwner(owner))
        {
            return;
        }

        if (startNewThread)
        {
            if (Status.State != WorkerConnectionState.Ready)
            {
                ApprovalModeConfirmationText = "Finish or interrupt the active turn before starting a new thread with Custom settings.";
                return;
            }

            await NewThreadAsync().ConfigureAwait(false);
            if (!IsCurrentOwner(owner) || !ReferenceEquals(pendingApprovalMode, option))
            {
                return;
            }
        }

        if (!IsCurrentOwner(owner) || !ReferenceEquals(pendingApprovalMode, option))
        {
            return;
        }

        ClearApprovalModeConfirmation();
        ApplyApprovalMode(option);
    }

    private static bool HasPerTurnApprovalOverride(ApprovalModeOption? option)
        => option is not null
            && (option.ApprovalPolicy is not null
                || option.ApprovalsReviewer is not null
                || option.SandboxMode is not null
                || option.Permissions is not null);

    private Task CancelApprovalModeAsync()
    {
        if (approvalModeBeforeConfirmationId is not null)
        {
            settings.ApprovalModeId = approvalModeBeforeConfirmationId;
            settingsStore.Save(settings);
            OnPropertyChanged(nameof(DesiredApprovalModeText));
        }

        ClearApprovalModeConfirmation();
        OnPropertyChanged(nameof(SelectedApprovalMode));
        OnPropertyChanged(nameof(SelectedApprovalModeId));
        return Task.CompletedTask;
    }

    private void ApplyApprovalMode(ApprovalModeOption option)
    {
        settings.ApprovalModeId = option.Id;
        settingsStore.Save(settings);
        selectedApprovalMode = option;
        OnPropertyChanged(nameof(SelectedApprovalMode));
        OnPropertyChanged(nameof(SelectedApprovalModeId));
        OnPropertyChanged(nameof(ApprovalModeHelpText));
        OnPropertyChanged(nameof(DesiredApprovalModeText));
    }

    private void ClearApprovalModeConfirmation()
    {
        pendingApprovalMode = null;
        confirmationStartsNewThread = false;
        approvalModeBeforeConfirmationId = null;
        ApprovalModeConfirmationText = string.Empty;
        OnPropertyChanged(nameof(HasApprovalModeConfirmation));
        ConfirmApprovalModeCommand.RaiseCanExecuteChanged();
        CancelApprovalModeCommand.RaiseCanExecuteChanged();
    }

    private async Task AttachAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        IReadOnlyList<string> selectedFiles = await filePickerService
            .PickFilesAsync(workingDirectory, lifetime.Token)
            .ConfigureAwait(false);
        await OnUiAsync(() =>
        {
            foreach (string path in selectedFiles)
            {
                if (PendingAttachments.Count >= 10)
                {
                    break;
                }

                TryAddPendingAttachment(path);
            }
        }).ConfigureAwait(false);
    }

    private bool TryAddPendingAttachment(string path)
    {
        if (PendingAttachments.Count >= 10)
        {
            return false;
        }

        string fullPath;
        try
        {
            string absolutePath = Path.GetFullPath(path);
            if (!LocalPath.TryCreate(absolutePath, out LocalPath localPath))
            {
                ExtensionDiagnostics.Write("Ignoring an attachment path outside the supported local path domain");
                return false;
            }

            fullPath = localPath.Value;
        }

        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ExtensionDiagnostics.Write("Ignoring an invalid attachment path", ex);
            return false;
        }

        if (!File.Exists(fullPath)
            || protectedDirectoryPolicy.IsProtected(fullPath)
            || PendingAttachments.Any(
                attachment => string.Equals(attachment.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        PendingAttachments.Add(new AttachmentChipViewModel(fullPath, markdown, RemovePendingAttachmentAsync));
        OnPropertyChanged(nameof(HasPendingAttachments));
        RaiseCommandStates();
        return true;
    }

    private Task RemovePendingAttachmentAsync(AttachmentChipViewModel attachment)
    {
        PendingAttachments.Remove(attachment);
        OnPropertyChanged(nameof(HasPendingAttachments));
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    private void ClearSentAttachments(IReadOnlyList<AttachmentInfo> sentAttachments)
    {
        if (PendingAttachments.Count == 0 || sentAttachments.Count == 0)
        {
            return;
        }

        var sentPaths = new HashSet<string>(
            sentAttachments.Select(attachment => attachment.Path),
            StringComparer.OrdinalIgnoreCase);
        for (int index = PendingAttachments.Count - 1; index >= 0; index--)
        {
            if (sentPaths.Contains(PendingAttachments[index].FullPath))
            {
                PendingAttachments.RemoveAt(index);
            }
        }

        OnPropertyChanged(nameof(HasPendingAttachments));
        RaiseCommandStates();
    }

    private static bool IsImageAttachment(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    private async Task InterruptAsync()
    {
        if (!CanInterruptSelectedThread() || Status.ThreadId is null || Status.TurnId is null)
        {
            return;
        }

        // The click time anchors the Worker's interrupt timings in the shared diagnostics log.
        ExtensionDiagnostics.Write($"Interrupt requested by user thread={Status.ThreadId} turn={Status.TurnId}");
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        await RunTrackedMutationAsync("Interrupt request", owner, () => bridge.InterruptTurnAsync(
            StampOwner(new InterruptTurnRequest { ThreadId = Status.ThreadId, TurnId = Status.TurnId }),
            lifetime.Token)).ConfigureAwait(false);
    }

    private async Task OnStateChangedAsync(WorkerNotification<WorkerStatus> notification)
    {
        WorkerStatus value = notification.Value;
        bool applied = false;
        await OnUiAsync(() =>
        {
            if (!CanApplyStatusNotification(notification))
            {
                return;
            }

            WorkerStatus previousStatus = Status;
            WorkerConnectionState previous = previousStatus.State;
            Status = value;
            applied = true;
            if (SelectedThread is not null
                && string.Equals(SelectedThread.Id, value.ThreadId, StringComparison.Ordinal))
            {
                SelectedThread.EffectiveReasoningEffort = value.EffectiveReasoningEffort;
                SelectedThread.EffectiveServiceTier = value.EffectiveServiceTier;
            }
            if (value.State is WorkerConnectionState.Disconnected or WorkerConnectionState.Degraded)
            {
                IReadOnlyList<SlashCommandInvocation> canceled = slashCommandCoordinator.CancelAll();
                if (canceled.Count > 0)
                {
                    string cancellationMessage = markdown.ToSafeText(
                        $"Canceled {canceled.Count} queued slash commands because the worker connection ended.");
                    SlashCommands.ShowFailure(cancellationMessage);
                    Items.Add(new ChatItemViewModel("Status", cancellationMessage, ConversationEventKind.ItemCompleted));
                }
            }
            else if (previousStatus.TurnId is not null && value.TurnId is null)
            {
                // Drain the completed turn's queue and the selected thread's queue: the user
                // may have queued commands for a different thread while the turn was running.
                _ = DrainSlashQueuesAsync(previousStatus.ThreadId ?? value.ThreadId, SelectedThread?.Id);
            }

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
        }).ConfigureAwait(false);

        if (applied
            && IsNotificationCurrent(notification)
            && value.State == WorkerConnectionState.Ready
            && Account.IsSignedIn
            && usageFetchedGeneration != usageConnectionGeneration)
        {
            await RefreshUsageAsync(force: false).ConfigureAwait(false);
        }

        if (applied && value.State == WorkerConnectionState.Degraded)
        {
            IWorkerBridge source = bridge;
            if (ConnectionRecoveryCoordinator.IsTransient(value.RecoveryFailureKind))
            {
                _ = HandleConnectionLostAsync(source, value.RecoveryFailureKind);
            }
            else
            {
                await OnUiAsync(() => RecoveryStatusText = GetRecoveryFailureText(value.RecoveryFailureKind)).ConfigureAwait(false);
            }
        }
    }

    private async Task OnAccountChangedAsync(WorkerNotification<AccountStatus> notification)
    {
        AccountStatus value = notification.Value;
        await OnUiAsync(() =>
        {
            if (IsNotificationCurrent(notification))
            {
                ExtensionDiagnostics.Write($"Account status notification received state={value.State} plan={value.PlanType ?? "none"}");
                UpdateAccount(value);
            }
        }).ConfigureAwait(false);
        if (IsNotificationCurrent(notification)
            && value.State == AccountState.SignedIn
            && Status.State == WorkerConnectionState.Ready)
        {
            await RefreshUsageAsync(force: false).ConfigureAwait(false);
        }
    }

    private bool IsFileSuggestionRefreshCurrent(CancellationTokenSource refresh, OwnerSnapshot owner)
        => ReferenceEquals(fileSuggestionRefresh, refresh)
            && !refresh.IsCancellationRequested
            && IsCurrentOwner(owner);

    private async Task OnContextCompactedAsync(WorkerNotification<ContextCompactionEvent> notification)
    {
        ContextCompactionEvent value = notification.Value;
        if (!value.IsCompleted)
        {
            return;
        }

        await OnUiAsync(() =>
        {
            if (IsNotificationCurrent(notification))
            {
                string message = markdown.ToSafeText("Context compaction completed.");
                SlashCommands.ShowStatus(message);
                Items.Add(new ChatItemViewModel("Status", message, ConversationEventKind.ItemCompleted));
            }
        }).ConfigureAwait(false);

        // Compaction consumes model calls but the app-server does not always follow it with a
        // turn/completed notification (see WorkerRpcService.PublishContextCompactedAsync). Treat
        // completed compaction as its own usage-consumption boundary so the header and flyout do
        // not go stale until the next turn or TTL-driven refresh.
        if (IsNotificationCurrent(notification))
        {
            await RefreshUsageAsync(force: true).ConfigureAwait(false);
        }
    }

    private Task OnReviewModeChangedAsync(WorkerNotification<ReviewModeEvent> notification)
    {
        ReviewModeEvent value = notification.Value;
        string message = value.ChangeKind == ReviewModeChangeKind.Entered
            ? "Code review mode started."
            : string.IsNullOrWhiteSpace(value.Review)
                ? "Code review mode completed."
                : $"Code review completed.\r\n{value.Review}";
        return OnUiAsync(() =>
        {
            if (IsNotificationCurrent(notification))
            {
                string safeMessage = markdown.ToSafeText(message);
                SlashCommands.ShowStatus(safeMessage);
                Items.Add(new ChatItemViewModel("Status", safeMessage, ConversationEventKind.ItemCompleted));
            }
        });
    }

    private Task OnThreadGoalChangedAsync(WorkerNotification<ThreadGoalEvent> notification)
    {
        ThreadGoalEvent value = notification.Value;
        var result = new ThreadGoalResult
        {
            Goal = value.Goal,
            Cleared = value.IsCleared,
        };
        return OnUiAsync(() =>
        {
            if (IsNotificationCurrent(notification))
            {
                string safeMessage = markdown.ToSafeText(FormatGoal(result));
                SlashCommands.ShowStatus(safeMessage);
                Items.Add(new ChatItemViewModel("Status", safeMessage, ConversationEventKind.ItemCompleted));
            }
        });
    }

    private Task OnRateLimitsChangedAsync(WorkerNotification<RateLimitsResult> notification)
        => OnUiAsync(() =>
        {
            if (IsNotificationCurrent(notification))
            {
                long generation = Volatile.Read(ref usageConnectionGeneration);
                long pushVersion = Interlocked.Increment(ref rateLimitPushVersion);
                ApplyRateLimitsPush(notification.Value, generation, pushVersion);
            }
        });

    private void ApplyRateLimitsPush(RateLimitsResult value, long generation, long pushVersion)
    {
        if (!IsUsageAvailable
            || generation != Volatile.Read(ref usageConnectionGeneration)
            || pushVersion != Volatile.Read(ref rateLimitPushVersion))
        {
            return;
        }

        DateTimeOffset refreshedAt = utcNow();
        latestRateLimits = value;
        latestRateLimitsAt = refreshedAt;
        usageFetchedGeneration = generation;
        Usage.Update(value, refreshedAt, markdown);
    }

    private async Task OnConversationEventAsync(WorkerNotification<ConversationEvent> notification)
    {
        ConversationEvent value = notification.Value;
        bool isTurnCompleted = value.Kind == ConversationEventKind.TurnCompleted;
        await OnUiAsync(() =>
        {
            if (!IsNotificationCurrent(notification))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value.ThreadId))
            {
                if (value.Kind != ConversationEventKind.Error)
                {
                    return;
                }

                string errorText = markdown.ToSafeText(string.IsNullOrWhiteSpace(value.Text)
                    ? "The app-server reported an error without a conversation context."
                    : value.Text);
                Items.Add(new ChatItemViewModel("Error", errorText, ConversationEventKind.Error));
                return;
            }
            else if (SelectedThread is null || !string.Equals(SelectedThread.Id, value.ThreadId, StringComparison.Ordinal))
            {
                return;
            }

            if (isHistoryLoading && !string.IsNullOrWhiteSpace(value.ThreadId))
            {
                long eventBytes = GetConversationEventBytes(value);
                if (bufferedEventCount + 1 > MaximumBufferedHistoryEvents
                    || bufferedEventBytes + eventBytes > MaximumBufferedHistoryBytes)
                {
                    pendingHistoryEvents.Clear();
                    bufferedEventCount = 0;
                    bufferedEventBytes = 0;
                    MarkHistoryStale("Live updates exceeded the synchronization buffer. Refresh history before continuing.");
                    return;
                }

                pendingHistoryEvents.Enqueue(notification);
                bufferedEventCount++;
                bufferedEventBytes += eventBytes;
                return;
            }

            // Plan events carry a full replacement payload — handle separately to avoid text append.
            if (value.Kind == ConversationEventKind.PlanUpdated)
            {
                ChatItemViewModel? planItem = FindCurrentTranscriptItem(value, notification);
                IReadOnlyList<string> steps = ChatItemViewModel.ParsePlanSteps(value.PayloadJson);
                if (planItem is null)
                {
                    planItem = new ChatItemViewModel("Plan", string.Empty, ConversationEventKind.PlanUpdated)
                    {
                        ItemId = value.ItemId,
                        ThreadId = value.ThreadId,
                        TurnId = value.TurnId,
                        StatePartitionFingerprint = notification.StatePartitionFingerprint,
                        OwnerGeneration = notification.OwnerGeneration,
                        ConnectionGeneration = notification.ConnectionGeneration,
                    };
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
                    lastAgentRawKey = null;
                    break;
                case ConversationEventKind.AgentMessageDelta when !string.IsNullOrEmpty(value.Text):
                    AppendAgentRaw(GetAgentRawKey(value), value.Text);
                    break;
                case ConversationEventKind.TurnCompleted:
                    TryDetectChoicePrompt(NotificationOwner(notification));
                    itemRawText.Clear();
                    break;
            }

            // A command-output overflow notification can carry only truncation metadata after the
            // visible 2 MiB buffer is full. Apply that metadata to the existing item even though
            // there is no text delta to render.
            if (value.Kind == ConversationEventKind.CommandOutputDelta && string.IsNullOrEmpty(value.Text))
            {
                ChatItemViewModel? commandItem = FindCurrentTranscriptItem(value, notification);
                if (commandItem is not null)
                {
                    commandItem.IsTruncated |= value.Truncated;
                    commandItem.OverflowFile = value.OverflowFile ?? commandItem.OverflowFile;
                    return;
                }
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

            ChatItemViewModel? existing = FindCurrentTranscriptItem(value, notification);
            if (existing?.IsHistoryCompleted == true)
            {
                return;
            }
            if (existing is null)
            {
                var item = new ChatItemViewModel(role, renderedText, value.Kind)
                {
                    ItemId = value.ItemId,
                    ThreadId = value.ThreadId,
                    TurnId = value.TurnId,
                    StatePartitionFingerprint = notification.StatePartitionFingerprint,
                    OwnerGeneration = notification.OwnerGeneration,
                    ConnectionGeneration = notification.ConnectionGeneration,
                    IsTruncated = value.Truncated,
                    OverflowFile = value.OverflowFile,
                };
                if (renderFromAccumulatedText)
                {
                    item.UpdateBlocks(renderedBlocks);
                }

                Items.Add(item);
                TrimHistoryWindow();
            }
            else
            {
                if (value.Kind == ConversationEventKind.CommandOutputDelta)
                {
                    historyBytes += existing.AppendCommandOutput(renderedText);
                }
                else
                {
                    SetTranscriptText(existing, renderFromAccumulatedText ? renderedText : existing.Text + renderedText);
                }
                if (renderFromAccumulatedText)
                {
                    existing.UpdateBlocks(renderedBlocks);
                }

                existing.IsTruncated |= value.Truncated;
                existing.OverflowFile = value.OverflowFile ?? existing.OverflowFile;
                TrimHistoryWindow();
            }
        }).ConfigureAwait(false);

        // A completed turn is an explicit usage-consumption boundary. Refresh after the
        // transcript projection so the header and flyout show the post-turn snapshot without
        // blocking UI-bound collection updates on the rate-limit RPC.
        if (isTurnCompleted && IsNotificationCurrent(notification))
        {
            await RefreshUsageAsync(force: true).ConfigureAwait(false);
        }
    }

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

    private ChatItemViewModel? FindCurrentTranscriptItem(ConversationEvent value, WorkerNotification<ConversationEvent> notification)
    {
        if (string.IsNullOrWhiteSpace(value.ThreadId) || string.IsNullOrWhiteSpace(value.TurnId) || string.IsNullOrWhiteSpace(value.ItemId))
        {
            return null;
        }

        return Items.LastOrDefault(item =>
            string.Equals(item.ThreadId, value.ThreadId, StringComparison.Ordinal)
            && string.Equals(item.TurnId, value.TurnId, StringComparison.Ordinal)
            && string.Equals(item.ItemId, value.ItemId, StringComparison.Ordinal)
            && item.Kind == value.Kind
            && string.Equals(item.StatePartitionFingerprint, notification.StatePartitionFingerprint, StringComparison.Ordinal)
            && item.OwnerGeneration == notification.OwnerGeneration
            && item.ConnectionGeneration == notification.ConnectionGeneration);
    }

    private static long GetConversationEventBytes(ConversationEvent value)
        => Encoding.UTF8.GetByteCount(value.Text ?? string.Empty)
            + Encoding.UTF8.GetByteCount(value.PayloadJson ?? string.Empty)
            + Encoding.UTF8.GetByteCount(value.ThreadId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(value.TurnId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(value.ItemId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(value.OverflowFile ?? string.Empty)
            + 64;

    private Task OnApprovalRequestedAsync(WorkerNotification<ApprovalRequest> notification)
        => OnUiAsync(() =>
        {
            if (!IsNotificationCurrent(notification))
            {
                return;
            }

            OwnerSnapshot owner = NotificationOwner(notification);
            ApprovalRequest approval = notification.Value;
            _ = ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                $"[AUDIT] Approval requested: {approval.Risk} — {approval.DisplayText}");
            EnqueueApproval(new ApprovalViewModel(
                approval,
                (requestId, decision) => ResolveApprovalAsync(requestId, decision, owner)));
        });

    private Task OnApprovalAuditReceivedAsync(WorkerNotification<ApprovalAuditRecord> notification)
        => OnUiAsync(() =>
        {
            if (!IsNotificationCurrent(notification))
            {
                return;
            }

            ApprovalAuditRecord record = notification.Value;
            _ = ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                $"[AUDIT] Approval {record.Action}: request={record.RequestId}, scope={record.Scope}, risk={record.Risk}, target={record.DisplayText}");
        });

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

    private Task OnApprovalResolvedAsync(WorkerNotification<string> notification)
        => OnUiAsync(() =>
        {
            if (IsNotificationCurrent(notification))
            {
                RemoveApproval(notification.Value);
            }
        });

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

    private async Task ResolveApprovalAsync(string requestId, ApprovalDecision decision, OwnerSnapshot owner)
    {
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        _ = outputChannel?.WriteLineAsync($"[AUDIT] Approval resolved: {requestId} → {decision}");
        // The DisplayText lookup must happen before the RPC call: a concurrent approvalResolved
        // echo from the worker can remove the card from the queue while the RPC is in flight.
        string summary = BuildDecisionSummary(requestId, decision);
        bool delivered = await RunTrackedMutationAsync("Approval response", owner, () => bridge.ResolveApprovalAsync(
            StampOwner(new ResolveApprovalRequest { RequestId = requestId, Decision = decision }, owner),
            lifetime.Token)).ConfigureAwait(false);
        if (!delivered)
        {
            return;
        }
        // Copilot Chat parity: the card disappears (via the worker's approvalResolved echo) and the
        // transcript keeps a single, safe result line so the outcome stays visible in context. Only
        // appended once the RPC has actually succeeded, so a failed resolve doesn't leave a
        // misleading "Accepted" line for a decision the worker never received.
        await OnUiAsync(() =>
        {
            if (IsCurrentOwner(owner))
            {
                AppendDecisionResultItem(summary);
            }
        }).ConfigureAwait(false);
    }

    // Builds the result-only transcript summary for a user-resolved approval. The DisplayText was
    // already redacted by the worker, but it is still routed through SafeMarkdownService before
    // display because everything shown in the panel must pass the same sanitization path.
    // Internal: exercised directly by the UI test assembly (InternalsVisibleTo).
    internal string BuildDecisionSummary(string requestId, ApprovalDecision decision)
    {
        ApprovalViewModel? approval = ActiveApproval?.RequestId == requestId
            ? ActiveApproval
            : approvalQueue.FirstOrDefault(item => item.RequestId == requestId);
        return approval is null
            ? DescribeDecision(decision)
            : string.Concat(DescribeDecision(decision), " — ", approval.DisplayText);
    }

    // Internal: exercised directly by the UI test assembly (InternalsVisibleTo).
    internal void AppendDecisionResultItem(string summary)
        => Items.Add(new ChatItemViewModel("Decision", markdown.ToSafeText(summary), ConversationEventKind.ItemCompleted));

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

    private Task OnUserInputRequestedAsync(WorkerNotification<UserInputRequest> notification)
        => OnUiAsync(() =>
        {
            if (!IsNotificationCurrent(notification))
            {
                return;
            }

            OwnerSnapshot owner = NotificationOwner(notification);
            EnqueueUserInput(new UserInputViewModel(
                notification.Value,
                (requestId, answers) => ResolveUserInputAsync(requestId, answers, owner),
                markdown));
        });

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

    private Task OnUserInputResolvedAsync(WorkerNotification<string> notification)
        => OnUiAsync(() =>
        {
            if (IsNotificationCurrent(notification))
            {
                RemoveUserInput(notification.Value);
            }
        });

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
    private async Task ResolveUserInputAsync(
        string requestId,
        IReadOnlyDictionary<string, string[]> answers,
        OwnerSnapshot owner)
    {
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        bool delivered = await RunTrackedMutationAsync("Choice response", owner, () => bridge.ResolveUserInputAsync(
            StampOwner(new ResolveUserInputRequest
            {
                RequestId = requestId,
                Answers = answers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            }, owner),
            lifetime.Token)).ConfigureAwait(false);
        if (!delivered)
        {
            return;
        }
        // Appended only after the RPC succeeds, so a failed resolve doesn't leave a result line
        // for a selection the worker never received.
        await OnUiAsync(() =>
        {
            if (IsCurrentOwner(owner))
            {
                AppendUserInputResultItem(answers);
            }
        }).ConfigureAwait(false);
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
    private async Task ResolveSyntheticUserInputAsync(
        string requestId,
        IReadOnlyDictionary<string, string[]> answers,
        OwnerSnapshot owner)
    {
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        string? choice = answers.Values.SelectMany(values => values).FirstOrDefault();
        await OnUiAsync(() =>
        {
            if (IsCurrentOwner(owner))
            {
                RemoveUserInput(requestId);
            }
        }).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(choice))
        {
            await SendMessageAsync(choice!, clearComposer: false, expectedOwner: owner).ConfigureAwait(false);
        }
    }

    // Accumulate the raw (pre-markdown-stripping) agent text per item so a completed message can be
    // inspected for a choice prompt. This is local-only; the experimental flag controls only server
    // request_user_input capability negotiation.
    private void AppendAgentRaw(string key, string rawChunk)
    {
        lastAgentRawKey = key;
        if (!agentRawText.TryGetValue(key, out StringBuilder? builder))
        {
            builder = new StringBuilder();
            agentRawText[key] = builder;
        }

        // Choice prompts are small; cap accumulation so a long message can't grow memory unbounded.
        if (builder.Length < 16 * 1024)
        {
            builder.Append(rawChunk);
        }
    }

    // On turn completion (codex is now waiting for the user), promote a detected choice prompt in the
    // last agent message into the same single-card selection UI.
    private void TryDetectChoicePrompt(OwnerSnapshot owner)
    {
        if (lastAgentRawKey is null)
        {
            return;
        }

        agentRawText.TryGetValue(lastAgentRawKey, out StringBuilder? builder);
        string raw = builder?.ToString() ?? string.Empty;
        agentRawText.Clear();
        lastAgentRawKey = null;
        if (ChoicePromptParser.TryParse(raw, out UserInputRequest synthesized))
        {
            EnqueueUserInput(new UserInputViewModel(
                synthesized,
                (requestId, answers) => ResolveSyntheticUserInputAsync(requestId, answers, owner),
                markdown) { IsSynthetic = true });
        }
    }

    private static string GetAgentRawKey(ConversationEvent value)
        => value.ItemId ?? value.TurnId ?? "global-agent-message";

    private async Task SignInAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        StartAccountLoginRequest request = StampOwner(new StartAccountLoginRequest(), owner);
        await ExtensionDiagnostics.WriteOutputAsync(outputChannel, "[CODEX AUTH] Extension login command started.").ConfigureAwait(false);
        try
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            StartAccountLoginResult result = await bridge.StartAccountLoginAsync(request, lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    UpdateAccount(result.Status);
                }
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
                if (IsCurrentOwner(owner))
                {
                    UpdateAccount(unavailable);
                }
            }).ConfigureAwait(false);
            await ExtensionDiagnostics.WriteOutputAsync(
                outputChannel,
                $"[CODEX] Sign in RPC failed ({ex.GetType().Name}).").ConfigureAwait(false);
        }
    }

    private async Task SignOutAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        LogoutAccountRequest request = StampOwner(new LogoutAccountRequest(), owner);
        await ExtensionDiagnostics.WriteOutputAsync(outputChannel, "[CODEX AUTH] Extension logout command started.").ConfigureAwait(false);
        try
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            AccountStatus result = await bridge.LogoutAccountAsync(request, lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner))
                {
                    UpdateAccount(result);
                }
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
                if (IsCurrentOwner(owner))
                {
                    UpdateAccount(unavailable);
                }
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

    private async Task ToggleUsageAsync()
    {
        bool opening = !IsUsageOpen;
        await OnUiAsync(() => IsUsageOpen = opening).ConfigureAwait(false);
        if (opening)
        {
            await RefreshUsageAsync(force: false).ConfigureAwait(false);
        }
    }

    private async Task OpenExternalLinkAsync(ExternalLinkTarget target)
    {
        try
        {
            await externalLinkOpener.OpenAsync(target, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write($"Opening an approved usage link failed ({ex.GetType().Name}).");
        }
    }

    internal async Task RefreshUsageAsync(bool force)
    {
        try
        {
            await usageRefreshGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }

        long generation = Volatile.Read(ref usageConnectionGeneration);
        try
        {
            if (!IsUsageAvailable)
            {
                return;
            }

            generation = Volatile.Read(ref usageConnectionGeneration);
            DateTimeOffset requestedAt = utcNow();
            if (!force
                && usageFetchedGeneration == generation
                && latestRateLimitsAt.HasValue
                && requestedAt - latestRateLimitsAt.Value < TimeSpan.FromSeconds(60))
            {
                return;
            }

            long pushVersion = Volatile.Read(ref rateLimitPushVersion);
            OwnerSnapshot usageOwner = CaptureOwnerSnapshot();
            await OnUiAsync(() =>
            {
                if (generation == Volatile.Read(ref usageConnectionGeneration) && IsUsageAvailable)
                {
                    Usage.SetLoading(true);
                }
            }).ConfigureAwait(false);
            RateLimitsResult result;
            try
            {
                result = await bridge.GetRateLimitsAsync(StampOwner(new GetRateLimitsRequest(), usageOwner), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ExtensionDiagnostics.Write($"Usage lookup failed ({ex.GetType().Name}).");
                return;
            }

            await OnUiAsync(() =>
            {
                if (generation != Volatile.Read(ref usageConnectionGeneration)
                    || !IsUsageAvailable
                    || pushVersion != Volatile.Read(ref rateLimitPushVersion))
                {
                    return;
                }

                DateTimeOffset refreshedAt = utcNow();
                latestRateLimits = result;
                latestRateLimitsAt = refreshedAt;
                usageFetchedGeneration = generation;
                Usage.Update(result, refreshedAt, markdown);
            }).ConfigureAwait(false);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                if (generation == Volatile.Read(ref usageConnectionGeneration))
                {
                    Usage.SetLoading(false);
                }
            }).ConfigureAwait(false);
            usageRefreshGate.Release();
        }
    }

    private static bool IsConnectedState(WorkerConnectionState state)
        => state is WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval;

    private void UpdateUsageConnectionLifecycle(WorkerConnectionState state)
    {
        bool isConnected = IsConnectedState(state);
        if (isConnected && !usageConnectionActive)
        {
            usageConnectionActive = true;
            Interlocked.Increment(ref usageConnectionGeneration);
            usageFetchedGeneration = -1;
            latestRateLimits = null;
            latestRateLimitsAt = null;
            Usage.Clear();
        }
        else if (!isConnected && usageConnectionActive)
        {
            usageConnectionActive = false;
            Interlocked.Increment(ref usageConnectionGeneration);
            InvalidateUsage();
        }
    }

    private void InvalidateUsage()
    {
        usageFetchedGeneration = -1;
        latestRateLimits = null;
        latestRateLimitsAt = null;
        Interlocked.Increment(ref rateLimitPushVersion);
        IsUsageOpen = false;
        Usage.Clear();
    }

    private void UpdateAccount(AccountStatus value)
    {
        bool wasSignedIn = Account.IsSignedIn;
        Account.Update(value);
        if (!Account.IsSignedIn)
        {
            if (wasSignedIn)
            {
                Interlocked.Increment(ref usageConnectionGeneration);
            }

            InvalidateUsage();
        }
        OnPropertyChanged(nameof(StatusDetailText));
        OnPropertyChanged(nameof(ShowAccountAction));
        OnPropertyChanged(nameof(AccountActionText));
        OnPropertyChanged(nameof(IsUsageAvailable));
        AccountCommand.RaiseCanExecuteChanged();
        ToggleUsageCommand.RaiseCanExecuteChanged();
    }

    // Disconnected is allowed so the user can send with no solution/folder open: SendAsync
    // performs a lazy interactive connect (prompting for a working directory) before sending.
    // Connecting and Degraded are excluded — Connecting is transient and Degraded requires
    // Restart. Disconnected is also gated on connecting == 0: if ConnectWithDirectoryAsync has
    // already started (and will reject a second attempt), disable Send until that attempt settles.
    private bool CanSend()
        => (!string.IsNullOrWhiteSpace(ComposerText) || (Status.TurnId is null && (HasPendingAttachments || HasPendingSkill)))
        && !IsRecovering
        && (SelectedThread is null || IsThreadJoined)
        && !(HasPendingSkill && (Status.State is WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval))
        && (Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy
                or WorkerConnectionState.WaitingForApproval
            || (Status.State is WorkerConnectionState.Disconnected && Volatile.Read(ref connecting) == 0));

    private bool CanInterruptSelectedThread()
        => !IsRecovering && IsThreadJoined && SelectedThread is not null
            && Status.TurnId is not null
            && string.Equals(SelectedThread.Id, Status.ThreadId, StringComparison.Ordinal);

    private void RaiseCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        NewThreadCommand.RaiseCanExecuteChanged();
        LoadMoreCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        InterruptCommand.RaiseCanExecuteChanged();
        AccountCommand.RaiseCanExecuteChanged();
        ToggleUsageCommand.RaiseCanExecuteChanged();
        ApplyRemoteProfileCommand.RaiseCanExecuteChanged();
        UseLocalAppServerCommand.RaiseCanExecuteChanged();

        // A connection generation change can clear an in-flight health check.
        CheckProfileHealthCommand?.RaiseCanExecuteChanged();
        JoinThreadCommand?.RaiseCanExecuteChanged();
        LoadOlderHistoryCommand?.RaiseCanExecuteChanged();
        RefreshHistoryCommand?.RaiseCanExecuteChanged();
        RefreshAttachmentsCommand?.RaiseCanExecuteChanged();
        RestoreQuarantinedDraftCommand?.RaiseCanExecuteChanged();
        DiscardQuarantinedDraftCommand?.RaiseCanExecuteChanged();
    }

    // In the OOP extension process, Application.Current is null so the null-conditional
    // falls through to the inline call. RemoteUI marshals property/collection changes
    // to VS's UI thread automatically via its proxy mechanism.
    private static async Task OnUiAsync(Action action)
    {
        try
        {
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
                return;
            }

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
    internal const int CommandPreviewLineLimit = 3;
    internal const int CommandPreviewCharacterLimit = 4 * 1024;
    internal const int CommandBufferCharacterLimit = 2 * 1024 * 1024;

    private readonly StringBuilder? commandOutputBuffer;
    private string text = string.Empty;
    private bool isDeliveryUnknown;
    private bool isHistoryCompleted;
    private bool isTruncated;
    private string? overflowFile;
    private bool isCollapsed;
    private bool isCommandOutputExpanded;
    private int commandLineBreakCount;
    private bool commandEndsWithLineBreak;

    public ChatItemViewModel(string role, string text, ConversationEventKind kind)
    {
        Role = role;
        Kind = kind;
        isCollapsed = kind == ConversationEventKind.ReasoningSummaryDelta;
        if (kind == ConversationEventKind.CommandOutputDelta)
        {
            commandOutputBuffer = new StringBuilder(Math.Min(text.Length, CommandBufferCharacterLimit));
            AppendCommandOutput(text);
        }
        else
        {
            this.text = text;
        }

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
    public string? ThreadId { get; set; }

    [DataMember]
    public string? TurnId { get; set; }

    [DataMember]
    public string? StatePartitionFingerprint { get; set; }

    [DataMember]
    public long OwnerGeneration { get; set; }

    [DataMember]
    public long ConnectionGeneration { get; set; }

    internal string? HistoryKey { get; set; }

    internal bool IsHistoryCompleted
    {
        get => isHistoryCompleted;
        set => isHistoryCompleted = value;
    }

    [DataMember]
    public string Text
    {
        get => text;
        set => SetProperty(ref text, value);
    }

    [DataMember]
    public bool IsDeliveryUnknown
    {
        get => isDeliveryUnknown;
        private set => SetProperty(ref isDeliveryUnknown, value);
    }

    [DataMember]
    public string DeliveryStatusText => IsDeliveryUnknown
        ? "Delivery is unknown. This message was not retried; review it before sending again."
        : string.Empty;

    internal void MarkDeliveryUnknown()
    {
        if (IsDeliveryUnknown)
        {
            return;
        }

        IsDeliveryUnknown = true;
        OnPropertyChanged(nameof(DeliveryStatusText));
    }

    [DataMember]
    public bool IsTruncated
    {
        get => isTruncated;
        set
        {
            if (SetProperty(ref isTruncated, value))
            {
                RefreshCommandOutputPresentation();
                OnPropertyChanged(nameof(TruncationNotice));
            }
        }
    }

    public string? OverflowFile
    {
        get => overflowFile;
        set
        {
            if (SetProperty(ref overflowFile, value))
            {
                OnPropertyChanged(nameof(TruncationNotice));
            }
        }
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

    [DataMember]
    public bool IsCommandOutputExpanded
    {
        get => isCommandOutputExpanded;
        set
        {
            if (SetProperty(ref isCommandOutputExpanded, value))
            {
                RefreshCommandOutputPresentation();
            }
        }
    }

    [DataMember]
    public bool IsCommandOutputCollapsible
        => IsCommandItem
            && (BufferedCommandLineCount > CommandPreviewLineLimit
                || BufferedCommandCharacterCount > CommandPreviewCharacterLimit
                || IsTruncated);

    [DataMember]
    public string CommandOutputExpansionLabel
    {
        get
        {
            if (IsCommandOutputExpanded)
            {
                return IsTruncated ? "Hide buffered command output (truncated)" : "Hide command output";
            }

            if (IsTruncated)
            {
                return "Show buffered command output (truncated)";
            }

            int hiddenLineCount = Math.Max(0, BufferedCommandLineCount - CommandPreviewLineLimit);
            return hiddenLineCount switch
            {
                0 => "Show remaining buffered command output",
                1 => "Show 1 more line",
                _ => $"Show {hiddenLineCount.ToString(CultureInfo.InvariantCulture)} more lines",
            };
        }
    }

    [DataMember]
    public string CommandOutputAutomationName
        => IsCommandOutputExpanded ? "Collapse command output" : "Expand command output";

    [DataMember]
    public string CommandOutputAutomationHelpText => CommandOutputExpansionLabel;

    [DataMember]
    public string TruncationNotice
        => !IsTruncated
            ? string.Empty
            : IsCommandItem && string.IsNullOrEmpty(OverflowFile)
            ? "Command output was truncated to the buffered limit."
            : "Output truncated; additional output is stored in a temporary file.";

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

    internal int BufferedCommandCharacterCount => commandOutputBuffer?.Length ?? 0;

    internal int BufferedCommandLineCount
        => BufferedCommandCharacterCount == 0
            ? 0
            : commandLineBreakCount + (commandEndsWithLineBreak ? 0 : 1);

    internal int AppendCommandOutput(string value)
    {
        if (!IsCommandItem || string.IsNullOrEmpty(value))
        {
            return 0;
        }

        StringBuilder buffer = commandOutputBuffer!;
        int remaining = Math.Max(0, CommandBufferCharacterLimit - buffer.Length);
        int acceptedLength = Math.Min(remaining, value.Length);
        int startIndex = buffer.Length;
        if (acceptedLength > 0)
        {
            buffer.Append(value.AsSpan(0, acceptedLength));
            UpdateCommandLineState(startIndex);
        }

        if (acceptedLength < value.Length)
        {
            IsTruncated = true;
        }

        RefreshCommandOutputPresentation();
        return Encoding.UTF8.GetByteCount(value.AsSpan(0, acceptedLength));
    }

    private void UpdateCommandLineState(int startIndex)
    {
        StringBuilder buffer = commandOutputBuffer!;
        for (int index = startIndex; index < buffer.Length; index++)
        {
            char current = buffer[index];
            if (current == '\r')
            {
                commandLineBreakCount++;
                commandEndsWithLineBreak = true;
            }
            else if (current == '\n')
            {
                if (index == 0 || buffer[index - 1] != '\r')
                {
                    commandLineBreakCount++;
                }

                commandEndsWithLineBreak = true;
            }
            else
            {
                commandEndsWithLineBreak = false;
            }
        }
    }

    private void RefreshCommandOutputPresentation()
    {
        if (!IsCommandItem)
        {
            return;
        }

        string projectedText = IsCommandOutputCollapsible && !IsCommandOutputExpanded
            ? CreateCommandPreview()
            : commandOutputBuffer!.ToString();
        SetProperty(ref text, projectedText, nameof(Text));
        OnPropertyChanged(nameof(IsCommandOutputCollapsible));
        OnPropertyChanged(nameof(CommandOutputExpansionLabel));
        OnPropertyChanged(nameof(CommandOutputAutomationName));
        OnPropertyChanged(nameof(CommandOutputAutomationHelpText));
    }

    private string CreateCommandPreview()
    {
        StringBuilder buffer = commandOutputBuffer!;
        int previewLength = Math.Min(buffer.Length, CommandPreviewCharacterLimit);
        int lineBreakCount = 0;
        for (int index = 0; index < previewLength; index++)
        {
            char current = buffer[index];
            bool isLineBreak = current == '\r'
                || (current == '\n' && (index == 0 || buffer[index - 1] != '\r'));
            if (!isLineBreak)
            {
                continue;
            }

            lineBreakCount++;
            if (lineBreakCount == CommandPreviewLineLimit)
            {
                previewLength = index;
                break;
            }
        }

        // Do not split a CRLF pair when the character limit lands between its two characters.
        if (previewLength > 0
            && previewLength < buffer.Length
            && buffer[previewLength - 1] == '\r'
            && buffer[previewLength] == '\n')
        {
            previewLength--;
        }

        return buffer.ToString(0, previewLength);
    }

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
        Header = markdown.ToSafeText(question.Header).Trim();
        Question = markdown.ToSafeText(question.Question).Trim();
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
        DisplayLabel = markdown.ToSafeText(option.Label).Trim();
        Description = markdown.ToSafeText(option.Description).Trim();
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
[DataContract]
public sealed class RecoveryDraftViewModel
{
    private readonly string[] attachmentPaths;

    internal RecoveryDraftViewModel(
        string text,
        string[] attachmentNames,
        string[] attachmentPaths,
        string? model,
        string? reasoningEffort,
        string? serviceTier,
        string? personality,
        string? collaborationMode,
        SkillInvocationInfo? skill,
        bool hasUncertainDelivery,
        AsyncCommand restoreCommand,
        AsyncCommand discardCommand,
        SafeMarkdownService markdown)
    {
        Text = text;
        string safeText = markdown.ToSafeText(text);
        TextPreview = safeText.Length > 512 ? string.Concat(safeText.AsSpan(0, 512), "…") : safeText;
        AttachmentSummary = attachmentNames.Length == 0 ? "No file attachments" : string.Join(", ", attachmentNames.Select(markdown.ToSafeText));
        string? skillSummary = skill is null ? null : $"Skill: {skill.Name} ({skill.Scope})";
        SettingsSummary = markdown.ToSafeText(string.Join(" · ", new[] { model, reasoningEffort, serviceTier, personality, collaborationMode, skillSummary }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        this.attachmentPaths = attachmentPaths;
        Model = model;
        ReasoningEffort = reasoningEffort;
        ServiceTier = serviceTier;
        Personality = personality;
        CollaborationMode = collaborationMode;
        Skill = skill;
        HasUncertainDelivery = hasUncertainDelivery;
        RestoreCommand = restoreCommand;
        DiscardCommand = discardCommand;
    }

    [DataMember]
    public string TextPreview { get; }

    internal string Text { get; }

    [DataMember]
    public string AttachmentSummary { get; }

    [DataMember]
    public string SettingsSummary { get; }

    [DataMember]
    public bool HasUncertainDelivery { get; }

    [DataMember]
    public AsyncCommand RestoreCommand { get; }

    [DataMember]
    public AsyncCommand DiscardCommand { get; }

    internal IReadOnlyList<string> AttachmentPaths => attachmentPaths;

    internal string? Model { get; }

    internal string? ReasoningEffort { get; }

    internal string? ServiceTier { get; }

    internal string? Personality { get; }

    internal string? CollaborationMode { get; }

    internal SkillInvocationInfo? Skill { get; }
}

[DataContract]
public sealed class ThreadAttachmentPresentationViewModel
{
    internal ThreadAttachmentPresentationViewModel(ThreadAttachmentMetadata metadata, SafeMarkdownService markdown)
    {
        Id = metadata.Id;
        DisplayText = $"{markdown.ToSafeText(metadata.AttachmentType)} · {markdown.ToSafeText(metadata.IdentityKey)}";
        StatusText = string.IsNullOrWhiteSpace(metadata.UnavailableReason)
            ? "Metadata only"
            : markdown.ToSafeText(metadata.UnavailableReason);
    }

    [DataMember]
    public string Id { get; }

    [DataMember]
    public string DisplayText { get; }

    [DataMember]
    public string StatusText { get; }
}

public sealed class AsyncCommand : ICommand, VSUI.IAsyncCommand, INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs CanExecuteChangedArgs = new(nameof(CanExecute));
    private static readonly AsyncLocal<Microsoft.VisualStudio.Extensibility.IClientContext?> ActiveClientContext = new();

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

    internal static Microsoft.VisualStudio.Extensibility.IClientContext? CurrentClientContext
        => ActiveClientContext.Value;

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
        Microsoft.VisualStudio.Extensibility.IClientContext? previousClientContext = ActiveClientContext.Value;
        ActiveClientContext.Value = clientContext;
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
            ActiveClientContext.Value = previousClientContext;
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
