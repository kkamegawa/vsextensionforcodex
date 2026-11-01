using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class CodexSessionService
{
    private int reviewThreadStartInFlight;

    // thread/start and review/start run as one operation. ActiveThreadId and the effective settings
    // stay on the source conversation until the review has started, and nothing is resent.
    private async Task<StartReviewResult> StartReviewInNewThreadAsync(
        StartReviewRequest request,
        CancellationToken cancellationToken)
    {
        object target = CreateReviewTarget(request.Target);
        ConnectionContext context = RequireContext();
        EnsureCredentialReady(context);
        if (Interlocked.CompareExchange(ref reviewThreadStartInFlight, 1, 0) != 0)
        {
            return ReviewNotCreated("A review is already starting in a separate conversation.");
        }

        try
        {
            if (!TryBuildReviewThreadParameters(context, request.ThreadId, out Dictionary<string, object?> parameters, out string? reason))
            {
                return ReviewNotCreated(reason);
            }

            OperationCallResult created;
            try
            {
                created = await TrySendOperationAsync("thread/start", parameters, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            }
            catch (JsonRpcRemoteException ex)
            {
                WorkerDiagnostics.Write("review thread/start was rejected", ex);
                return ReviewNotCreated("The app-server rejected the new conversation, so the review did not start.");
            }
            catch (Exception ex) when (IsUnknownOperationOutcome(ex, cancellationToken))
            {
                WorkerDiagnostics.Write("review thread/start outcome is unknown", ex);
                return new StartReviewResult { Outcome = ReviewStartOutcome.Unknown };
            }

            if (!created.IsSupported)
            {
                return Unsupported<StartReviewResult>("Starting a new conversation is not supported by this app-server.");
            }

            EffectiveApprovalState createdApproval = ReadEffectiveApprovalState(created.Result);
            ReadEffectiveTurnSettings(created.Result, out string? reasoningEffort, out string? serviceTier);
            ThreadSummary thread = ReadThread(created.Result.GetProperty("thread"), createdApproval, reasoningEffort, serviceTier);
            string? modelId = thread.EffectiveModelId ?? ReadEffectiveModelId(created.Result);
            if (!IsReviewThreadWithinSource(createdApproval, EffectiveApprovalState))
            {
                return ReviewCreatedNotStarted(thread, "The new conversation did not report the same permissions, so the review did not start.");
            }

            lock (turnStateLock)
            {
                pendingTurnThreadId = thread.Id;
            }

            JsonElement started;
            try
            {
                started = await context.Connection.SendRequestAsync(
                    "review/start",
                    new { threadId = thread.Id, target, delivery = "inline" },
                    TimeSpan.FromSeconds(60),
                    cancellationToken).ConfigureAwait(false);
                EnsureCurrent(context);
            }
            catch (JsonRpcRemoteException ex)
            {
                WorkerDiagnostics.Write("review/start was rejected in the new conversation", ex);
                return ReviewCreatedNotStarted(thread, "The conversation was created, but the app-server rejected the review.");
            }
            catch (Exception ex) when (IsUnknownOperationOutcome(ex, cancellationToken))
            {
                WorkerDiagnostics.Write("review/start outcome is unknown in the new conversation", ex);
                lock (turnStateLock)
                {
                    if (IsCurrent(context))
                    {
                        unconfirmedTurnThreadId = thread.Id;
                    }
                }

                return new StartReviewResult { Outcome = ReviewStartOutcome.Unknown, CreatedThread = thread };
            }
            finally
            {
                lock (turnStateLock)
                {
                    if (string.Equals(pendingTurnThreadId, thread.Id, StringComparison.Ordinal))
                    {
                        pendingTurnThreadId = null;
                    }
                }
            }

            string? turnId = started.TryGetProperty("turn", out JsonElement turn) ? GetString(turn, "id") : null;
            lock (turnStateLock)
            {
                EnsureCurrent(context);
                ActiveThreadId = thread.Id;
                ActiveTurnId = turnId is not null && !completedTurnIds.Contains(new TurnKey(context.Generation, thread.Id, turnId))
                    ? turnId
                    : null;
            }

            EffectiveApprovalState = createdApproval;
            EffectiveReasoningEffort = reasoningEffort;
            EffectiveServiceTier = serviceTier;
            effectiveThreadModelId = modelId;
            activeThreadInheritsDefaultModel = modelId is null;
            return new StartReviewResult
            {
                ReviewThreadId = thread.Id,
                TurnId = turnId,
                CreatedThread = thread,
            };
        }
        finally
        {
            Volatile.Write(ref reviewThreadStartInFlight, 0);
        }
    }

    private bool TryBuildReviewThreadParameters(
        ConnectionContext context,
        string sourceThreadId,
        out Dictionary<string, object?> parameters,
        out string? reason)
    {
        parameters = [];
        reason = null;
        if (!string.Equals(sourceThreadId, ActiveThreadId, StringComparison.Ordinal))
        {
            reason = "Join the conversation before starting a review from it.";
            return false;
        }

        EffectiveApprovalState? source = EffectiveApprovalState;
        if (source?.ApprovalPolicy is not ("untrusted" or "on-request" or "never"))
        {
            reason = "The current approval policy cannot be applied to a new conversation.";
            return false;
        }

        if (source.ApprovalsReviewer is not (null or "user" or "auto_review" or "guardian_subagent"))
        {
            reason = "The current approvals reviewer cannot be applied to a new conversation.";
            return false;
        }

        if (!TryMapLocalPathForServer(options.WorkingDirectory, out string serverPath))
        {
            reason = "The working directory cannot be applied to a new conversation.";
            return false;
        }

        if (effectiveThreadModelId is null && !activeThreadInheritsDefaultModel)
        {
            reason = "The current model cannot be applied to a new conversation.";
            return false;
        }

        parameters["cwd"] = serverPath;
        parameters["approvalPolicy"] = source.ApprovalPolicy;
        AddOptional(parameters, "approvalsReviewer", source.ApprovalsReviewer);
        AddOptional(parameters, "model", effectiveThreadModelId);
        AddOptional(parameters, "serviceTier", EffectiveServiceTier);

        // thread/start accepts a named profile only on the experimental surface, and never with sandbox.
        if (source.ActivePermissionProfile is not null)
        {
            if (!context.ExperimentalApi)
            {
                reason = "The current permission profile cannot be applied to a new conversation.";
                return false;
            }

            parameters["permissions"] = source.ActivePermissionProfile;
            return true;
        }

        string? sandbox = source.SandboxMode switch
        {
            "readOnly" => "read-only",
            "workspaceWrite" => "workspace-write",
            "dangerFullAccess" => "danger-full-access",
            _ => null,
        };
        if (sandbox is null)
        {
            reason = "The current sandbox mode cannot be applied to a new conversation.";
            return false;
        }

        parameters["sandbox"] = sandbox;
        return true;
    }

    private static bool IsReviewThreadWithinSource(EffectiveApprovalState created, EffectiveApprovalState? source)
        => source is not null
            && string.Equals(created.ApprovalPolicy, source.ApprovalPolicy, StringComparison.Ordinal)
            && (source.ActivePermissionProfile is not null
                ? string.Equals(created.ActivePermissionProfile, source.ActivePermissionProfile, StringComparison.Ordinal)
                : string.Equals(created.SandboxMode, source.SandboxMode, StringComparison.Ordinal));

    private static bool IsUnknownOperationOutcome(Exception exception, CancellationToken cancellationToken)
        => exception is TimeoutException or JsonRpcConnectionClosedException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static StartReviewResult ReviewNotCreated(string? reason)
        => new() { Outcome = ReviewStartOutcome.NotCreated, FailureReason = reason };

    private static StartReviewResult ReviewCreatedNotStarted(ThreadSummary thread, string reason)
        => new() { Outcome = ReviewStartOutcome.CreatedNotStarted, CreatedThread = thread, FailureReason = reason };
}
