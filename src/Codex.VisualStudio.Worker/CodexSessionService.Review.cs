using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class CodexSessionService
{
    private sealed record EffectiveThreadPermissions(
        EffectiveApprovalState Approval,
        JsonElement? Sandbox,
        bool HasMalformedProfile);

    private EffectiveThreadPermissions? effectiveThreadPermissions;
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
            EffectiveThreadPermissions? sourcePermissions = effectiveThreadPermissions;
            if (!TryBuildReviewThreadParameters(
                    context, request.ThreadId, sourcePermissions,
                    out Dictionary<string, object?> parameters, out string? reason))
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

            EffectiveThreadPermissions createdPermissions = ReadEffectiveThreadPermissions(created.Result);
            ReadEffectiveTurnSettings(created.Result, out string? reasoningEffort, out string? serviceTier);
            ThreadSummary thread = ReadThread(created.Result.GetProperty("thread"), createdPermissions.Approval, reasoningEffort, serviceTier);
            string? modelId = thread.EffectiveModelId ?? ReadEffectiveModelId(created.Result);
            if (!string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal)
                || !ReferenceEquals(sourcePermissions, effectiveThreadPermissions))
            {
                return ReviewCreatedNotStarted(thread, "The source conversation changed, so the review did not start.");
            }

            if (!IsReviewThreadWithinSource(createdPermissions, sourcePermissions))
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
            string? rootTurnId = started.TryGetProperty("turn", out turn) ? GetString(turn, "rootTurnId") : null;
            lock (turnStateLock)
            {
                EnsureCurrent(context);
                ActiveThreadId = thread.Id;
                bool turnIsActive = turnId is not null
                    && !IsTurnCompletedLocked(context.Generation, thread.Id, turnId, rootTurnId);
                ActiveTurnId = turnIsActive
                    ? turnId
                    : null;
                activeRootTurnId = turnIsActive ? rootTurnId : null;
            }

            effectiveThreadPermissions = createdPermissions;
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
        EffectiveThreadPermissions? sourcePermissions,
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

        if (sourcePermissions is { HasMalformedProfile: true })
        {
            reason = "The current permission profile could not be read.";
            return false;
        }

        EffectiveApprovalState? source = sourcePermissions?.Approval;
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

        // Same storage as an ordinary new conversation, so the review can be a linked-fork source.
        if (context.FeatureTier >= CodexFeatureTier.Tier166 && context.ExperimentalApi)
        {
            parameters["historyMode"] = "paginated";
        }

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

    private static bool IsReviewThreadWithinSource(EffectiveThreadPermissions created, EffectiveThreadPermissions? source)
        => source is not null
            && !created.HasMalformedProfile
            && string.Equals(created.Approval.ApprovalPolicy, source.Approval.ApprovalPolicy, StringComparison.Ordinal)
            && string.Equals(created.Approval.ApprovalsReviewer, source.Approval.ApprovalsReviewer, StringComparison.Ordinal)
            && string.Equals(created.Approval.ActivePermissionProfile, source.Approval.ActivePermissionProfile, StringComparison.Ordinal)
            && SandboxPoliciesMatch(created.Sandbox, source.Sandbox);

    private static bool SandboxPoliciesMatch(JsonElement? created, JsonElement? source)
    {
        try
        {
            return JsonNode.DeepEquals(
                created is JsonElement createdSandbox ? JsonNode.Parse(createdSandbox.GetRawText()) : null,
                source is JsonElement sourceSandbox ? JsonNode.Parse(sourceSandbox.GetRawText()) : null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUnknownOperationOutcome(Exception exception, CancellationToken cancellationToken)
        => exception is TimeoutException or JsonRpcConnectionClosedException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static StartReviewResult ReviewNotCreated(string? reason)
        => new() { Outcome = ReviewStartOutcome.NotCreated, FailureReason = reason };

    private static StartReviewResult ReviewCreatedNotStarted(ThreadSummary thread, string reason)
        => new() { Outcome = ReviewStartOutcome.CreatedNotStarted, CreatedThread = thread, FailureReason = reason };
}
