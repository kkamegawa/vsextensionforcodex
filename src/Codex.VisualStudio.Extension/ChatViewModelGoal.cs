using System.ComponentModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    // Every stop state has an exit: Stopping resolves from the Worker result, AwaitingTurn from the
    // target turn's completion, Unknown from a definitive goal read or notification, and Retry once
    // no same-thread turn runs and the goal is no longer active.
    private enum GoalStopPhase
    {
        None,
        Stopping,
        AwaitingTurn,
        Unknown,
        Retry,
    }

    // Goal state is updated from the Worker notification thread and from RPC continuations, so
    // every read-modify-write runs under this lock. Change notifications are raised after release.
    private readonly object goalStateLock = new();
    private readonly SemaphoreSlim goalOperationGate = new(1, 1);
    private readonly HashSet<string> completedGoalTurnIds = new(StringComparer.Ordinal);
    private ThreadGoalInfo? selectedThreadGoal;
    private string? goalProjectionThreadId;
    private long goalProjectionRevision;
    // Captured by queued slash commands; advanced only when a Stop starts.
    private long goalStopFenceVersion;
    // Identifies one stop operation; advanced on every Stop and every projection reset so a late
    // result from an earlier stop or projection can never apply to the current one.
    private long goalStopOperationId;
    private long goalStatusRevision;
    private long goalStopStatusRevision;
    private GoalStopPhase goalStopPhase;
    private bool goalStopUnsupported;
    private string? goalStopThreadId;
    private string? goalStopTargetTurnId;
    private bool goalStopTargetTurnCompleted;
    private string goalStopStatusText = string.Empty;

    public ThreadGoalInfo? SelectedThreadGoal => selectedThreadGoal;

    [DataMember]
    public bool IsGoalStopMode
        => !goalStopUnsupported
            && SelectedThreadGoal?.Status == ThreadGoalStatus.Active
            && string.Equals(goalProjectionThreadId, SelectedThread?.Id, StringComparison.Ordinal);

    [DataMember]
    public bool IsGoalStopPending => goalStopPhase is GoalStopPhase.Stopping or GoalStopPhase.AwaitingTurn;

    [DataMember]
    public string GoalStopStatusText => goalStopStatusText;

    [DataMember]
    public bool HasGoalStopStatus => !string.IsNullOrWhiteSpace(goalStopStatusText);

    // Exactly one of the primary action and the slash Run button is visible.
    [DataMember]
    public bool IsPrimaryActionVisible => !ShowSlashExecuteButton;

    [DataMember]
    public bool ShowSlashExecuteButton => SlashCommands.HasActiveCommand && goalStopPhase == GoalStopPhase.None;

    private bool IsGoalPrimaryMode => IsGoalStopMode || goalStopPhase != GoalStopPhase.None;

    // Send, Steer, Resume, and queued commands wait until a stop reaches a definitive state.
    private bool IsGoalStopUnresolved
        => goalStopPhase is GoalStopPhase.Stopping or GoalStopPhase.AwaitingTurn or GoalStopPhase.Unknown;

    private bool CanStopThreadGoal()
        => goalStopPhase is not (GoalStopPhase.Stopping or GoalStopPhase.AwaitingTurn)
            && !IsRecovering
            && IsThreadJoined
            && SelectedThread is not null
            && string.Equals(goalProjectionThreadId, SelectedThread.Id, StringComparison.Ordinal)
            && (Status.TurnId is null || string.Equals(Status.ThreadId, SelectedThread.Id, StringComparison.Ordinal))
            && (Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval)
            && (IsGoalStopMode || goalStopPhase is GoalStopPhase.Unknown or GoalStopPhase.Retry);

    private string GoalPrimaryActionText => goalStopPhase switch
    {
        GoalStopPhase.Stopping or GoalStopPhase.AwaitingTurn => "Stopping",
        GoalStopPhase.Unknown => "Check Stop Status",
        GoalStopPhase.Retry => "Retry Stop",
        _ => "Stop",
    };

    private string GoalPrimaryActionHelpText => goalStopPhase switch
    {
        GoalStopPhase.Stopping or GoalStopPhase.AwaitingTurn => "Stopping the goal. Waiting for its target turn to finish.",
        GoalStopPhase.Unknown => "Check whether stopping the goal completed. No message will be sent.",
        GoalStopPhase.Retry => "Retry stopping the goal and its current turn. No message will be sent.",
        _ => "Stop the active goal and its current turn. No message will be sent.",
    };

    private void OnSlashCommandsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SlashCommandPresentationViewModel.HasActiveCommand))
        {
            OnPropertyChanged(nameof(IsPrimaryActionVisible));
            OnPropertyChanged(nameof(ShowSlashExecuteButton));
        }
    }

    private void OnGoalThreadChanged(string? threadId)
    {
        ResetGoalProjectionCore(threadId);
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            _ = LoadSelectedThreadGoalAsync(threadId);
        }
    }

    private void ResetGoalProjection() => ResetGoalProjectionCore(SelectedThread?.Id);

    private void ResetGoalProjectionCore(string? threadId)
    {
        lock (goalStateLock)
        {
            goalProjectionRevision++;
            completedGoalTurnIds.Clear();
            goalProjectionThreadId = threadId;
            selectedThreadGoal = null;
            ResetGoalStopState();
        }

        NotifyGoalPresentationChanged();
    }

    private void ResetGoalStopState()
    {
        goalStopOperationId++;
        goalStopPhase = GoalStopPhase.None;
        goalStopUnsupported = false;
        goalStopThreadId = null;
        goalStopTargetTurnId = null;
        goalStopTargetTurnCompleted = false;
        goalStopStatusText = string.Empty;
    }

    private void NotifyGoalPresentationChanged()
    {
        OnPropertyChanged(nameof(SelectedThreadGoal));
        OnPropertyChanged(nameof(IsGoalStopMode));
        OnPropertyChanged(nameof(IsGoalStopPending));
        OnPropertyChanged(nameof(GoalStopStatusText));
        OnPropertyChanged(nameof(HasGoalStopStatus));
        OnPropertyChanged(nameof(IsPrimaryActionVisible));
        OnPropertyChanged(nameof(ShowSlashExecuteButton));
        OnPropertyChanged(nameof(SendButtonText));
        OnPropertyChanged(nameof(SendButtonGlyph));
        OnPropertyChanged(nameof(PrimaryActionHelpText));
        OnPropertyChanged(nameof(IsLocallyInterruptible));
        RaiseCommandStates();
    }

    private async Task LoadSelectedThreadGoalAsync(string threadId)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        long revision = Volatile.Read(ref goalProjectionRevision);
        try
        {
            ThreadGoalResult result = await bridge.GetThreadGoalAsync(
                StampOwner(new ThreadGoalRequest { ThreadId = threadId }, owner),
                lifetime.Token).ConfigureAwait(false);
            bool applied;
            lock (goalStateLock)
            {
                applied = IsCurrentOwner(owner)
                    && revision == goalProjectionRevision
                    && string.Equals(SelectedThread?.Id, threadId, StringComparison.Ordinal)
                    && string.Equals(goalProjectionThreadId, threadId, StringComparison.Ordinal);
                if (applied)
                {
                    ApplyDefinitiveGoal(result.Goal);
                }
            }

            if (applied)
            {
                NotifyGoalPresentationChanged();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Could not load the selected thread goal", ex);
        }
    }

    private Task ApplyThreadGoalNotificationAsync(WorkerNotification<ThreadGoalEvent> notification, ThreadGoalResult result)
        => OnUiAsync(() =>
        {
            lock (goalStateLock)
            {
                if (!IsNotificationCurrent(notification)
                    || !string.Equals(notification.Value.ThreadId, SelectedThread?.Id, StringComparison.Ordinal)
                    || !string.Equals(goalProjectionThreadId, notification.Value.ThreadId, StringComparison.Ordinal))
                {
                    return;
                }

                // A push supersedes any read that started before it, even when the read completes later.
                ApplyDefinitiveGoal(result.Cleared ? null : result.Goal);
            }

            NotifyGoalPresentationChanged();
        });

    // Applies a goal state from a read, notification, or goal command. Callers hold the lock.
    private void ApplyDefinitiveGoal(ThreadGoalInfo? goal)
    {
        goalProjectionRevision++;
        selectedThreadGoal = goal;
        if (goalStopPhase == GoalStopPhase.Unknown)
        {
            ResolveUnknownGoalStop();
            return;
        }

        if (goalStopPhase == GoalStopPhase.None && goal?.Status == ThreadGoalStatus.Active)
        {
            // A resumed goal starts a fresh cycle; the previous stop message no longer applies.
            goalStopStatusText = string.Empty;
        }

        ReevaluateGoalStop();
    }

    private void ApplyGoalCommandResult(OwnerSnapshot owner, string threadId, long projectionRevision, ThreadGoalInfo? goal)
    {
        lock (goalStateLock)
        {
            if (!IsCurrentOwner(owner)
                || projectionRevision != goalProjectionRevision
                || !string.Equals(SelectedThread?.Id, threadId, StringComparison.Ordinal))
            {
                return;
            }

            goalProjectionThreadId = threadId;
            ApplyDefinitiveGoal(goal);
        }

        NotifyGoalPresentationChanged();
    }

    private bool IsGoalStopTurnRunning()
        => goalStopThreadId is not null
            && Status.TurnId is not null
            && string.Equals(Status.ThreadId, goalStopThreadId, StringComparison.Ordinal);

    // With the goal state known, an unconfirmed stop either completed or needs an explicit retry.
    private void ResolveUnknownGoalStop()
    {
        bool turnRunning = IsGoalStopTurnRunning();
        ThreadGoalStatus? goalStatus = selectedThreadGoal?.Status;
        if (turnRunning || goalStatus == ThreadGoalStatus.Active)
        {
            goalStopPhase = GoalStopPhase.Retry;
            goalStopStatusText = goalStatus == ThreadGoalStatus.Active
                ? "The goal is still active. Select Retry Stop to try again."
                : "The goal is not active, but its turn is still running. Select Retry Stop to interrupt it.";
            return;
        }

        goalStopPhase = GoalStopPhase.None;
        goalStopStatusText = "Goal stopped.";
    }

    // Callers hold the lock.
    private void ReevaluateGoalStop()
    {
        switch (goalStopPhase)
        {
            case GoalStopPhase.AwaitingTurn when goalStopTargetTurnCompleted || goalStopTargetTurnId is null:
                if (IsGoalStopTurnRunning()
                    && !string.Equals(Status.TurnId, goalStopTargetTurnId, StringComparison.Ordinal))
                {
                    goalStopPhase = GoalStopPhase.Retry;
                    goalStopStatusText = "Another turn started after the stop. Select Retry Stop to interrupt it.";
                }
                else
                {
                    goalStopPhase = GoalStopPhase.None;
                    goalStopStatusText = selectedThreadGoal?.Status == ThreadGoalStatus.Active ? string.Empty : "Goal stopped.";
                }

                break;
            case GoalStopPhase.Retry when !IsGoalStopTurnRunning() && selectedThreadGoal?.Status != ThreadGoalStatus.Active:
                goalStopPhase = GoalStopPhase.None;
                goalStopStatusText = "Goal stopped.";
                break;
        }
    }

    private void OnGoalStatusChanged(WorkerStatus value)
    {
        bool changed = false;
        lock (goalStateLock)
        {
            long revision = ++goalStatusRevision;
            if (goalStopPhase == GoalStopPhase.None
                || !string.Equals(value.ThreadId, goalStopThreadId, StringComparison.Ordinal))
            {
                return;
            }

            GoalStopPhase before = goalStopPhase;
            if (goalStopPhase is GoalStopPhase.Stopping or GoalStopPhase.AwaitingTurn
                && revision > goalStopStatusRevision
                && (value.TurnId is null
                    || (goalStopTargetTurnId is not null
                        && !string.Equals(value.TurnId, goalStopTargetTurnId, StringComparison.Ordinal))))
            {
                goalStopTargetTurnCompleted = true;
            }

            ReevaluateGoalStop();
            changed = before != goalStopPhase;
        }

        if (changed)
        {
            NotifyGoalPresentationChanged();
        }
    }

    private void OnGoalConversationCompleted(ConversationEvent value)
    {
        if (string.IsNullOrWhiteSpace(value.ThreadId) || string.IsNullOrWhiteSpace(value.TurnId))
        {
            return;
        }

        bool changed = false;
        lock (goalStateLock)
        {
            if (completedGoalTurnIds.Count >= 128)
            {
                completedGoalTurnIds.Clear();
            }

            completedGoalTurnIds.Add(TurnKey(value.ThreadId, value.TurnId));
            if (goalStopPhase is GoalStopPhase.Stopping or GoalStopPhase.AwaitingTurn
                && string.Equals(value.ThreadId, goalStopThreadId, StringComparison.Ordinal)
                && string.Equals(value.TurnId, goalStopTargetTurnId, StringComparison.Ordinal))
            {
                GoalStopPhase before = goalStopPhase;
                goalStopTargetTurnCompleted = true;
                ReevaluateGoalStop();
                changed = before != goalStopPhase;
            }
        }

        if (changed)
        {
            NotifyGoalPresentationChanged();
        }
    }

    private static string TurnKey(string threadId, string turnId) => string.Concat(threadId, "\0", turnId);

    private bool IsCurrentGoalStop(OwnerSnapshot owner, string threadId, long operationId)
        => operationId == goalStopOperationId
            && IsCurrentOwner(owner)
            && string.Equals(SelectedThread?.Id, threadId, StringComparison.Ordinal);

    private async Task StopThreadGoalAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        string? threadId = SelectedThread?.Id;
        long operationId;
        bool reconcileOnly;
        SlashCommandInvocation[] canceled = [];
        lock (goalStateLock)
        {
            if (!CanStopThreadGoal() || string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            reconcileOnly = goalStopPhase == GoalStopPhase.Unknown;
            if (reconcileOnly)
            {
                goalStopStatusText = "Checking the current goal…";
                operationId = goalStopOperationId;
            }
            else
            {
                operationId = ++goalStopOperationId;
                goalStopFenceVersion++;
                goalStopPhase = GoalStopPhase.Stopping;
                goalStopThreadId = threadId;
                goalStopStatusRevision = goalStatusRevision;
                goalStopTargetTurnId = string.Equals(Status.ThreadId, threadId, StringComparison.Ordinal) ? Status.TurnId : null;
                goalStopTargetTurnCompleted = goalStopTargetTurnId is not null
                    && completedGoalTurnIds.Contains(TurnKey(threadId, goalStopTargetTurnId));
                goalStopStatusText = "Stopping the goal and its active turn…";
                canceled = slashCommandCoordinator.CancelThread(threadId)
                    .Concat(slashCommandCoordinator.CancelSessionGoalCommands())
                    .ToArray();
            }
        }

        NotifyGoalPresentationChanged();
        if (reconcileOnly)
        {
            await ReconcileUnknownGoalStopAsync(owner, threadId!, operationId).ConfigureAwait(false);
            return;
        }

        if (canceled.Length > 0)
        {
            string message = markdown.ToSafeText($"Canceled {canceled.Length} queued slash commands because the goal is stopping.");
            await OnUiAsync(() =>
            {
                SlashCommands.ShowFailure(message);
                Items.Add(new ChatItemViewModel("Status", message, ConversationEventKind.ItemCompleted));
            }).ConfigureAwait(false);
        }

        long projectionRevision = Volatile.Read(ref goalProjectionRevision);
        StopThreadGoalResult? result = null;
        Exception? failure = null;
        await goalOperationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!IsCurrentGoalStop(owner, threadId!, operationId))
            {
                return;
            }

            try
            {
                result = await bridge.StopThreadGoalAsync(
                    StampOwner(new StopThreadGoalRequest { ThreadId = threadId! }, owner),
                    lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !lifetime.IsCancellationRequested)
            {
                // Timeouts and disconnects leave both stages unknown. Nothing is retried automatically.
                failure = ex;
            }
        }
        finally
        {
            goalOperationGate.Release();
        }

        if (failure is not null)
        {
            ExtensionDiagnostics.Write("Stopping the thread goal failed with an unknown outcome", failure);
        }

        bool reconcile = false;
        lock (goalStateLock)
        {
            if (!IsCurrentGoalStop(owner, threadId!, operationId))
            {
                return;
            }

            if (failure is not null || result is null)
            {
                goalStopPhase = GoalStopPhase.Unknown;
                goalStopStatusText = "The stop result is unknown. Checking the current goal…";
                reconcile = true;
            }
            else if (result.IsSupported && !StopResultMatchesOwner(result, owner))
            {
                goalStopPhase = GoalStopPhase.Unknown;
                goalStopStatusText = "The stop response came from a different connection. Checking the current goal…";
                reconcile = true;
            }
            else
            {
                reconcile = ApplyStopResult(result, threadId!, projectionRevision);
            }
        }

        NotifyGoalPresentationChanged();
        if (reconcile)
        {
            await ReconcileUnknownGoalStopAsync(owner, threadId!, operationId).ConfigureAwait(false);
        }
    }

    // Callers hold the lock. Returns true when the outcome needs a read-only reconciliation.
    private bool ApplyStopResult(StopThreadGoalResult result, string threadId, long projectionRevision)
    {
        if (!result.IsSupported)
        {
            // Nothing was attempted. Ordinary Send and Interrupt remain available for this thread.
            goalStopUnsupported = true;
            goalStopPhase = GoalStopPhase.None;
            goalStopStatusText = SafeStopMessage(result.UnavailableReason, "Stopping goals is not supported by this app-server.");
            return false;
        }

        if (result.TurnId is not null)
        {
            bool sameTarget = string.Equals(result.TurnId, goalStopTargetTurnId, StringComparison.Ordinal);
            goalStopTargetTurnCompleted = (sameTarget && goalStopTargetTurnCompleted)
                || completedGoalTurnIds.Contains(TurnKey(threadId, result.TurnId));
            goalStopTargetTurnId = result.TurnId;
        }
        else if (result.InterruptOutcome == GoalStopStepOutcome.NotRequired)
        {
            // The Worker saw no running turn after the pause attempt.
            goalStopTargetTurnId = null;
        }

        if (result.Goal is not null && projectionRevision == goalProjectionRevision)
        {
            goalProjectionRevision++;
            selectedThreadGoal = result.Goal;
        }

        bool unknown = result.PauseOutcome == GoalStopStepOutcome.OutcomeUnknown
            || result.InterruptOutcome == GoalStopStepOutcome.OutcomeUnknown;
        bool failed = result.PauseOutcome == GoalStopStepOutcome.Failed
            || result.InterruptOutcome == GoalStopStepOutcome.Failed;
        if (unknown)
        {
            goalStopPhase = GoalStopPhase.Unknown;
            goalStopStatusText = "The stop result is uncertain. Checking the current goal…";
            return true;
        }

        if (failed)
        {
            goalStopPhase = GoalStopPhase.Retry;
            goalStopStatusText = SafeStopMessage(result.Message, "Stopping the goal was incomplete. Select Retry Stop to finish.");
            ReevaluateGoalStop();
            if (goalStopPhase == GoalStopPhase.None)
            {
                // Keep the partial-result explanation; a failed pause is never reported as stopped.
                goalStopStatusText = SafeStopMessage(result.Message, "Stopping the goal was incomplete.");
            }

            return false;
        }

        goalStopPhase = GoalStopPhase.AwaitingTurn;
        goalStopStatusText = "Stop requested. Waiting for the active turn to finish…";
        ReevaluateGoalStop();
        return false;
    }

    private async Task ReconcileUnknownGoalStopAsync(OwnerSnapshot owner, string threadId, long operationId)
    {
        try
        {
            ThreadGoalResult result = await bridge.GetThreadGoalAsync(
                StampOwner(new ThreadGoalRequest { ThreadId = threadId }, owner),
                lifetime.Token).ConfigureAwait(false);
            lock (goalStateLock)
            {
                if (!IsCurrentGoalStop(owner, threadId, operationId) || goalStopPhase != GoalStopPhase.Unknown)
                {
                    return;
                }

                // A read is definitive even if a notification raced it: both reflect server state
                // after the stop request, and the outcome only decides whether to offer a retry.
                ApplyDefinitiveGoal(result.Goal);
            }

            NotifyGoalPresentationChanged();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            bool applied = false;
            lock (goalStateLock)
            {
                if (IsCurrentGoalStop(owner, threadId, operationId) && goalStopPhase == GoalStopPhase.Unknown)
                {
                    goalStopStatusText = "Could not verify the stop result. Select Check Stop Status to retry the read.";
                    applied = true;
                }
            }

            if (applied)
            {
                NotifyGoalPresentationChanged();
            }

            ExtensionDiagnostics.Write("Could not reconcile the thread goal stop result", ex);
        }
    }

    private static bool StopResultMatchesOwner(StopThreadGoalResult result, OwnerSnapshot owner)
        => result.OwnerGeneration == owner.OwnerGeneration
            && result.ConnectionGeneration == owner.ConnectionGeneration
            && string.Equals(result.StatePartitionFingerprint, owner.StatePartitionFingerprint, StringComparison.Ordinal);

    private string SafeStopMessage(string? message, string fallback)
        => string.IsNullOrWhiteSpace(message) ? fallback : markdown.ToSafeText(message);

    private long CaptureGoalStopFenceVersion() => Volatile.Read(ref goalStopFenceVersion);

    private bool IsGoalStopUnresolvedFor(string? threadId)
        => IsGoalStopUnresolved
            && (threadId is null || string.Equals(threadId, goalStopThreadId, StringComparison.Ordinal));

    private async Task<ThreadGoalResult?> RunGoalMutationAsync(long fenceVersion, Func<Task<ThreadGoalResult>> operation)
    {
        await goalOperationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (fenceVersion != CaptureGoalStopFenceVersion() || IsGoalStopUnresolved)
            {
                return null;
            }

            return await operation().ConfigureAwait(false);
        }
        finally
        {
            goalOperationGate.Release();
        }
    }
}
