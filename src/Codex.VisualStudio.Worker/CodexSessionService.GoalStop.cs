using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class CodexSessionService
{
    // Not disposed: a stop still in flight during DisposeAsync must be able to release it.
    private readonly SemaphoreSlim goalStopGate = new(1, 1);

    public async Task<StopThreadGoalResult> StopThreadGoalAsync(
        StopThreadGoalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("A thread id is required.", nameof(request));
        }

        // Overlapping explicit stops run one after another. The later one observes the earlier
        // outcome (for example an already paused goal) instead of reporting a failure it never had.
        await goalStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConnectionContext context = RequireContext();
            EnsureGoalStopScope(context, request.ThreadId);
            return await StopThreadGoalCoreAsync(context, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            goalStopGate.Release();
        }
    }

    private async Task<StopThreadGoalResult> StopThreadGoalCoreAsync(
        ConnectionContext context,
        StopThreadGoalRequest request,
        CancellationToken cancellationToken)
    {
        var result = new StopThreadGoalResult();

        ThreadGoalInfo? goal = null;
        bool goalStateKnown = false;
        try
        {
            OperationCallResult read = await SendGoalStopOperationAsync(
                context,
                "thread/goal/get",
                request.ThreadId,
                new { threadId = request.ThreadId },
                TimeSpan.FromSeconds(15),
                readOnly: true,
                cancellationToken).ConfigureAwait(false);
            EnsureGoalStopScope(context, request.ThreadId);
            if (!read.IsSupported)
            {
                // Nothing was attempted: goals are unavailable on this app-server.
                result.IsSupported = false;
                result.UnavailableReason = "Thread goals are not supported by this app-server.";
                result.Message = result.UnavailableReason;
                return result;
            }

            goal = ReadOptionalGoal(read.Result);
            goalStateKnown = true;
            result.Goal = goal;
        }
        catch (Exception ex) when (IsGoalStopStepFailure(ex, cancellationToken))
        {
            // The explicit Stop still proceeds: a status-only pause is attempted with the goal
            // state unknown, and the same-thread turn is interrupted afterwards.
            EnsureGoalStopScope(context, request.ThreadId);
            WorkerDiagnostics.Write($"thread/goal/get failed thread={request.ThreadId} source=goal-stop", ex);
        }

        if (!goalStateKnown || goal?.Status == ThreadGoalStatus.Active)
        {
            try
            {
                OperationCallResult pause = await SendGoalStopOperationAsync(
                    context,
                    "thread/goal/set",
                    request.ThreadId,
                    new { threadId = request.ThreadId, status = "paused" },
                    TimeSpan.FromSeconds(30),
                    readOnly: false,
                    cancellationToken).ConfigureAwait(false);
                EnsureGoalStopScope(context, request.ThreadId);
                if (!pause.IsSupported)
                {
                    // The interrupt below is still attempted, so the result stays supported and
                    // reports the pause stage as a definitive failure.
                    result.UnavailableReason = "Pausing thread goals is not supported by this app-server.";
                    result.PauseOutcome = GoalStopStepOutcome.Failed;
                    result.Message = result.UnavailableReason;
                }
                else
                {
                    ThreadGoalInfo? pausedGoal = ReadOptionalGoal(pause.Result);
                    if (pausedGoal is not null)
                    {
                        result.Goal = pausedGoal;
                    }

                    result.PauseOutcome = pausedGoal?.Status == ThreadGoalStatus.Paused
                        ? GoalStopStepOutcome.Succeeded
                        : GoalStopStepOutcome.OutcomeUnknown;
                    if (result.PauseOutcome == GoalStopStepOutcome.OutcomeUnknown)
                    {
                        result.Message = "The app-server did not confirm that the goal was paused.";
                    }
                }
            }
            catch (Exception ex) when (IsGoalStopStepFailure(ex, cancellationToken))
            {
                EnsureGoalStopScope(context, request.ThreadId);
                result.PauseOutcome = ClassifyGoalStopFailure(ex);
                result.Message = result.PauseOutcome == GoalStopStepOutcome.Failed
                    ? "The app-server rejected the goal pause request."
                    : "The goal pause outcome could not be confirmed.";
                WorkerDiagnostics.Write($"thread/goal/set failed thread={request.ThreadId} source=goal-stop", ex);
            }
        }
        else if (goal is null)
        {
            result.Message = "No goal is set for this thread.";
        }
        else if (goal.Status != ThreadGoalStatus.Paused)
        {
            result.Message = "The goal is no longer active.";
        }

        // The server may create a continuation turn without a local turn/start. Snapshot the
        // latest exact-thread turn only after the pause attempt, so no successor can be mistaken
        // for it. The explicit Stop applies to that turn whatever the goal state turned out to be.
        EnsureGoalStopScope(context, request.ThreadId);
        string? turnId;
        bool turnStarting;
        lock (turnStateLock)
        {
            bool sameThread = string.Equals(ActiveThreadId, request.ThreadId, StringComparison.Ordinal);
            turnId = sameThread ? ActiveTurnId : null;
            turnStarting = turnId is null
                && (string.Equals(pendingTurnThreadId, request.ThreadId, StringComparison.Ordinal)
                    || string.Equals(unconfirmedTurnThreadId, request.ThreadId, StringComparison.Ordinal));
        }

        if (turnId is null)
        {
            if (turnStarting)
            {
                // A turn/start is in flight, or timed out without a definitive start, and has no
                // server turn id yet. It cannot be targeted now; report the stage as unknown so the
                // caller re-reads state before any explicit retry.
                result.InterruptOutcome = GoalStopStepOutcome.OutcomeUnknown;
                result.Message = AppendGoalStopMessage(result.Message, "A turn was starting and could not be interrupted yet.");
                return result;
            }

            result.InterruptOutcome = GoalStopStepOutcome.NotRequired;
            if (result.PauseOutcome == GoalStopStepOutcome.Succeeded)
            {
                result.Message = "The goal is paused. No turn is active.";
            }

            return result;
        }

        result.TurnId = turnId;
        long requestedAt = timeProvider.GetTimestamp();
        var key = new TurnKey(context.Generation, request.ThreadId, turnId);
        lock (turnStateLock)
        {
            interruptRequestedAt[key] = requestedAt;
        }

        WorkerDiagnostics.Write($"turn/interrupt requested thread={request.ThreadId} turn={turnId} source=goal-stop");
        EnsureGoalStopScope(context, request.ThreadId);
        try
        {
            await context.Connection.SendRequestAsync(
                "turn/interrupt",
                new { threadId = request.ThreadId, turnId },
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            EnsureGoalStopScope(context, request.ThreadId);
            result.InterruptOutcome = GoalStopStepOutcome.Succeeded;
            WorkerDiagnostics.Write($"turn/interrupt acknowledged turn={turnId} elapsedMs={ElapsedMilliseconds(requestedAt)} source=goal-stop");
        }
        catch (Exception ex) when (IsGoalStopStepFailure(ex, cancellationToken))
        {
            EnsureGoalStopScope(context, request.ThreadId);
            result.InterruptOutcome = ClassifyGoalStopFailure(ex);
            WorkerDiagnostics.Write($"turn/interrupt failed turn={turnId} elapsedMs={ElapsedMilliseconds(requestedAt)} source=goal-stop", ex);
            result.Message = AppendGoalStopMessage(
                result.Message,
                result.InterruptOutcome == GoalStopStepOutcome.Failed
                    ? "The app-server rejected the current turn interrupt."
                    : "The current turn interrupt outcome could not be confirmed.");
        }

        if (string.IsNullOrWhiteSpace(result.Message))
        {
            result.Message = result.PauseOutcome switch
            {
                GoalStopStepOutcome.Succeeded => "The goal is paused and an interrupt request was acknowledged.",
                GoalStopStepOutcome.NotRequired => "An interrupt request was acknowledged.",
                _ => "The interrupt request was acknowledged, but the goal pause was not confirmed.",
            };
        }

        return result;
    }

    private static string AppendGoalStopMessage(string? message, string addition)
        => string.IsNullOrWhiteSpace(message) ? addition : $"{message} {addition}";

    // A request timeout surfaces as OperationCanceledException; only the caller's own
    // cancellation aborts the stop. A closed connection propagates as a stale operation.
    private static bool IsGoalStopStepFailure(Exception exception, CancellationToken cancellationToken)
        => exception is not JsonRpcConnectionClosedException
            && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested);

    private void EnsureGoalStopScope(ConnectionContext context, string threadId)
    {
        EnsureCurrent(context);
        lock (turnStateLock)
        {
            if (!string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The requested thread is not the currently joined thread.");
            }
        }
    }

    private async Task<OperationCallResult> SendGoalStopOperationAsync(
        ConnectionContext context,
        string method,
        string threadId,
        object parameters,
        TimeSpan timeout,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        EnsureGoalStopScope(context, threadId);
        lock (context.UnsupportedMethodsLock)
        {
            if (context.UnsupportedMethods.Contains(method))
            {
                return OperationCallResult.Unsupported;
            }
        }

        try
        {
            EnsureGoalStopScope(context, threadId);
            JsonElement response = readOnly
                ? await context.Connection.SendReadOnlyRequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false)
                : await context.Connection.SendRequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            return new OperationCallResult(true, response);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == -32601)
        {
            lock (context.UnsupportedMethodsLock)
            {
                if (IsCurrent(context))
                {
                    context.UnsupportedMethods.Add(method);
                }
            }

            WorkerDiagnostics.Write($"app-server method disabled for this session method={method}", ex);
            return OperationCallResult.Unsupported;
        }
    }

    private static GoalStopStepOutcome ClassifyGoalStopFailure(Exception exception)
        => exception is JsonRpcRemoteException
            ? GoalStopStepOutcome.Failed
            : GoalStopStepOutcome.OutcomeUnknown;
}
