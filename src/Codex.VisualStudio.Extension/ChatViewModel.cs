using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility.Documents;

namespace Codex.VisualStudio.Extension;

public sealed class ChatViewModel : ObservableObject, IDisposable
{
    private readonly WorkerBridge bridge;
    private readonly OutputChannel? outputChannel;
    private readonly SafeMarkdownService markdown = new();
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    private WorkerStatus status = new() { State = WorkerConnectionState.Disconnected, Message = "Open Codex to connect." };
    private ThreadSummary? selectedThread;
    private string composerText = string.Empty;
    private string? nextCursor;
    private bool initialized;

    public ChatViewModel(OutputChannel? outputChannel = null)
    {
        this.outputChannel = outputChannel;
        bridge = new WorkerBridge(outputChannel);
        bridge.StateChanged += OnStateChangedAsync;
        bridge.ConversationEventReceived += OnConversationEventAsync;
        bridge.ApprovalRequested += OnApprovalRequestedAsync;
        bridge.ApprovalResolved += OnApprovalResolvedAsync;
        ConnectCommand = new AsyncCommand(ConnectAsync, () => Status.State is WorkerConnectionState.Disconnected or WorkerConnectionState.Degraded);
        RestartCommand = new AsyncCommand(RestartAsync, () => Status.State == WorkerConnectionState.Degraded);
        NewThreadCommand = new AsyncCommand(NewThreadAsync, () => Status.State == WorkerConnectionState.Ready);
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => initialized && nextCursor is not null);
        SendCommand = new AsyncCommand(SendAsync, CanSend);
        InterruptCommand = new AsyncCommand(InterruptAsync, () => Status.TurnId is not null);
        _ = ConnectAsync();
    }

    public ObservableCollection<ThreadSummary> Threads { get; } = new();

    public ObservableCollection<ChatItemViewModel> Items { get; } = new();

    public ObservableCollection<ApprovalViewModel> Approvals { get; } = new();

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
            }
        }
    }

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

    public string ComposerText
    {
        get => composerText;
        set
        {
            if (SetProperty(ref composerText, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsDegraded => Status.State == WorkerConnectionState.Degraded;

    public bool IsTurnActive => Status.TurnId is not null;

    public string SendButtonText => IsTurnActive ? "Steer" : "Send";

    public AsyncCommand ConnectCommand { get; }

    public AsyncCommand RestartCommand { get; }

    public AsyncCommand NewThreadCommand { get; }

    public AsyncCommand LoadMoreCommand { get; }

    public AsyncCommand SendCommand { get; }

    public AsyncCommand InterruptCommand { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lifetime.Cancel();
        lifetime.Dispose();
        // Best-effort async disposal on shutdown; fire-and-forget is acceptable here.
        _ = Task.Run(async () => await bridge.DisposeAsync().ConfigureAwait(false));
    }

    private async Task ConnectAsync()
    {
        WorkerStatus result = await bridge.ConnectAsync(Environment.CurrentDirectory, lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(() => Status = result).ConfigureAwait(false);
        if (result.State == WorkerConnectionState.Ready)
        {
            initialized = true;
            await LoadMoreAsync().ConfigureAwait(false);
        }
    }

    private async Task RestartAsync()
    {
        WorkerStatus result = await bridge.RestartAsync(lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(() => Status = result).ConfigureAwait(false);
        if (result.State == WorkerConnectionState.Ready)
        {
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
        string text = ComposerText;
        await OnUiAsync(() => ComposerText = string.Empty).ConfigureAwait(false);
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
        => OnUiAsync(() => Approvals.Add(new ApprovalViewModel(value, ResolveApprovalAsync)));

    private Task OnApprovalResolvedAsync(string requestId)
        => OnUiAsync(() =>
        {
            ApprovalViewModel? approval = Approvals.FirstOrDefault(item => item.RequestId == requestId);
            approval?.MarkResolved();
        });

    private async Task ResolveApprovalAsync(string requestId, ApprovalDecision decision)
    {
        _ = outputChannel?.WriteLineAsync($"[AUDIT] Approval resolved: {requestId} → {decision}");
        await bridge.ResolveApprovalAsync(new ResolveApprovalRequest { RequestId = requestId, Decision = decision }, lifetime.Token).ConfigureAwait(false);
    }

    private bool CanSend()
        => !string.IsNullOrWhiteSpace(ComposerText)
        && Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval;

    private void RaiseCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        NewThreadCommand.RaiseCanExecuteChanged();
        LoadMoreCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        InterruptCommand.RaiseCanExecuteChanged();
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

        action();
        return Task.CompletedTask;
    }
}

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

    public string Role { get; }

    public ConversationEventKind Kind { get; }

    public string? ItemId { get; set; }

    public string Text
    {
        get => text;
        set => SetProperty(ref text, value);
    }

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

    public bool IsCollapsed
    {
        get => isCollapsed;
        set
        {
            if (SetProperty(ref isCollapsed, value))
                OnPropertyChanged(nameof(CollapseButtonText));
        }
    }

    public string CollapseButtonText => isCollapsed ? "▶ Reasoning" : "▼ Reasoning";

    // Computed kind helpers — used by XAML DataTriggers (bool avoids enum reference in remote XAML).
    public bool IsReasoningItem => Kind == ConversationEventKind.ReasoningSummaryDelta;
    public bool IsCommandItem => Kind == ConversationEventKind.CommandOutputDelta;
    public bool IsDiffItem => Kind == ConversationEventKind.DiffUpdated;
    public bool IsPlanItem => Kind == ConversationEventKind.PlanUpdated;

    public ObservableCollection<string> PlanSteps { get; } = [];

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
        AcceptForSessionCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.AcceptForSession), () => CanResolve);
        DeclineCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.Decline), () => CanResolve);
        CancelCommand = new AsyncCommand(() => ResolveOnceAsync(ApprovalDecision.Cancel), () => CanResolve);
    }

    public string RequestId { get; }

    public string DisplayText { get; }

    public string? Reason { get; }

    public ApprovalRiskCategory Risk { get; }

    public bool IsPolicyBlocked { get; }

    public string? PolicyBlockReason { get; }

    public bool ShowAccept { get; }

    public bool ShowAcceptForSession { get; }

    public bool ShowDecline { get; }

    public bool ShowCancel { get; }

    public bool IsNetworkApproval { get; }

    public string? NetworkHost { get; }

    public string? NetworkPort { get; }

    public bool IsResolved
    {
        get => isResolved;
        private set
        {
            if (SetProperty(ref isResolved, value))
            {
                AcceptCommand.RaiseCanExecuteChanged();
                AcceptForSessionCommand.RaiseCanExecuteChanged();
                DeclineCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanResolve => !IsResolved && !IsPolicyBlocked;

    public AsyncCommand AcceptCommand { get; }

    public AsyncCommand AcceptForSessionCommand { get; }

    public AsyncCommand DeclineCommand { get; }

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

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool>? canExecute;
    private int running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => Volatile.Read(ref running) == 0 && (canExecute?.Invoke() ?? true);

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
        finally
        {
            Interlocked.Exchange(ref running, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
