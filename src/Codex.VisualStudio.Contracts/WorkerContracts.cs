using System.Runtime.Serialization;
using StreamJsonRpc;

namespace Codex.VisualStudio.Contracts;

public static class ContractVersions
{
    public const int Current = 9;
}

public enum WorkerConnectionState
{
    Disconnected,
    Connecting,
    Ready,
    Busy,
    WaitingForApproval,
    Degraded,
}

public enum AccountState
{
    Checking,
    SignedOut,
    SigningIn,
    SignedIn,
    Unavailable,
}

public enum ConversationEventKind
{
    ItemStarted,
    ItemCompleted,
    AgentMessageDelta,
    ReasoningSummaryDelta,
    CommandOutputDelta,
    DiffUpdated,
    PlanUpdated,
    TurnStarted,
    TurnCompleted,
    Error,
    Unknown,
}

public enum ApprovalRiskCategory
{
    ReadOnly,
    WorkspaceWrite,
    WorkspaceOutside,
    Network,
    Destructive,
    CredentialOAuth,
}

public enum ApprovalDecision
{
    Accept,
    AcceptForTurn,
    AcceptForThread,
    AcceptForSession,
    Decline,
    Cancel,
}

public enum ApprovalScope
{
    Once,
    Turn,
    Thread,
    Session,
}

public enum ApprovalAuditAction
{
    GrantCreated,
    AutoApproved,
}

public enum ReviewTargetKind
{
    UncommittedChanges,
    BaseBranch,
    Commit,
    Custom,
}

public enum ReviewDelivery
{
    Inline,
    Detached,
}

public enum ThreadGoalStatus
{
    Active,
    Paused,
    Blocked,
    UsageLimited,
    BudgetLimited,
    Complete,
}

public enum ReviewModeChangeKind
{
    Entered,
    Exited,
}

public sealed class WorkerOptions
{
    public int ContractVersion { get; set; } = ContractVersions.Current;

    public string CodexPath { get; set; } = "codex";

    public string WorkingDirectory { get; set; } = string.Empty;

    public string ExtensionVersion { get; set; } = "0.1.0";

    public bool ExperimentalApi { get; set; }
}

// DataContract/DataMember are required by Remote UI: the VS-side data context proxy only
// replicates DataMember properties of DataContract types. Every public property must stay
// a DataMember, since adding DataContract without it would drop that property from both
// the Remote UI proxy and the StreamJsonRpc payload.
[DataContract]
public sealed class WorkerStatus
{
    [DataMember]
    public int ContractVersion { get; set; } = ContractVersions.Current;

    [DataMember]
    public WorkerConnectionState State { get; set; }

    [DataMember]
    public string Message { get; set; } = string.Empty;

    [DataMember]
    public string? ThreadId { get; set; }

    [DataMember]
    public string? TurnId { get; set; }

    [DataMember]
    public int? ProcessId { get; set; }

    [DataMember]
    public string? CodexVersion { get; set; }
}

public sealed class AccountStatus
{
    public AccountState State { get; set; } = AccountState.Checking;

    public string? PlanType { get; set; }

    public string? Message { get; set; }
}

public sealed class StartAccountLoginResult
{
    public AccountStatus Status { get; set; } = new();

    public string? LoginId { get; set; }

    public string? AuthUrl { get; set; }
}

// DataContract/DataMember required by Remote UI (bound in the thread-history list).
[DataContract]
public sealed class ThreadSummary
{
    [DataMember]
    public string Id { get; set; } = string.Empty;

    [DataMember]
    public string? Preview { get; set; }

    [DataMember]
    public string? Cwd { get; set; }

    [DataMember]
    public long? UpdatedAt { get; set; }
}

public sealed class ThreadPage
{
    public IReadOnlyList<ThreadSummary> Threads { get; set; } = Array.Empty<ThreadSummary>();

    public string? NextCursor { get; set; }
}

public sealed class ModelInfo
{
    public string Id { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? DefaultReasoningEffort { get; set; }

    public IReadOnlyList<ReasoningEffortInfo> SupportedReasoningEfforts { get; set; } = Array.Empty<ReasoningEffortInfo>();

    public bool SupportsPersonality { get; set; }

    public string? DefaultServiceTier { get; set; }

    public IReadOnlyList<ServiceTierInfo> ServiceTiers { get; set; } = Array.Empty<ServiceTierInfo>();
}

public sealed class ReasoningEffortInfo
{
    public string Id { get; set; } = string.Empty;

    public string? Description { get; set; }
}

public sealed class ServiceTierInfo
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? Description { get; set; }
}

public sealed class ListModelsResult
{
    public IReadOnlyList<ModelInfo> Models { get; set; } = Array.Empty<ModelInfo>();

    public string? DefaultModel { get; set; }
}

public sealed class StartTurnRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string? Model { get; set; }

    // Per-turn approval policy override mapped from the Agent/Chat mode preset.
    // Matches the codex app-server turn/start "approvalPolicy" field (for example "on-request" or "never").
    public string? ApprovalPolicy { get; set; }

    // Per-turn sandbox policy type override mapped from the Agent/Chat mode preset.
    // Matches the codex app-server turn/start "sandboxPolicy.type" field (for example "workspaceWrite" or "readOnly").
    public string? SandboxMode { get; set; }

    public string? Effort { get; set; }

    public string? Personality { get; set; }

    public string? ServiceTier { get; set; }

    public CollaborationModeInfo? CollaborationMode { get; set; }

    public IdeContextInfo? IdeContext { get; set; }
}

public sealed class CollaborationModeInfo
{
    public string Mode { get; set; } = "default";

    public string Model { get; set; } = string.Empty;

    public string? ReasoningEffort { get; set; }

    public string? DeveloperInstructions { get; set; }
}

public sealed class IdeContextInfo
{
    public string? ActiveDocumentPath { get; set; }

    public IReadOnlyList<string> ReferencedFilePaths { get; set; } = Array.Empty<string>();

    public string? SelectionFilePath { get; set; }

    public string? SelectionText { get; set; }
}

public sealed class SteerTurnRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public string ExpectedTurnId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public sealed class InterruptTurnRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;
}

public abstract class AppServerOperationResult
{
    public bool IsSupported { get; set; } = true;

    public string? UnavailableReason { get; set; }
}

public sealed class CompactThreadRequest
{
    public string ThreadId { get; set; } = string.Empty;
}

public sealed class CompactThreadResult : AppServerOperationResult
{
}

public sealed class ReviewTarget
{
    public ReviewTargetKind Kind { get; set; }

    public string? Value { get; set; }

    public string? Title { get; set; }
}

public sealed class StartReviewRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public ReviewTarget Target { get; set; } = new();

    public ReviewDelivery Delivery { get; set; } = ReviewDelivery.Inline;
}

public sealed class StartReviewResult : AppServerOperationResult
{
    public string? ReviewThreadId { get; set; }

    public string? TurnId { get; set; }
}

public sealed class ForkThreadRequest
{
    public string ThreadId { get; set; } = string.Empty;
}

public sealed class ForkThreadResult : AppServerOperationResult
{
    public ThreadSummary? Thread { get; set; }
}

public sealed class ThreadGoalInfo
{
    public string ThreadId { get; set; } = string.Empty;

    public string Objective { get; set; } = string.Empty;

    public ThreadGoalStatus Status { get; set; } = ThreadGoalStatus.Active;

    public long? TokenBudget { get; set; }

    public long TokensUsed { get; set; }

    public long TimeUsedSeconds { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

public sealed class SetThreadGoalRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public string? Objective { get; set; }

    public ThreadGoalStatus? Status { get; set; }

    public long? TokenBudget { get; set; }
}

public sealed class ThreadGoalResult : AppServerOperationResult
{
    public ThreadGoalInfo? Goal { get; set; }

    public bool Cleared { get; set; }
}

public sealed class McpServerStatusInfo
{
    public string Name { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string AuthStatus { get; set; } = string.Empty;

    public IReadOnlyList<string> ToolNames { get; set; } = Array.Empty<string>();

    public int ResourceCount { get; set; }

    public int ResourceTemplateCount { get; set; }
}

public sealed class McpServerListResult : AppServerOperationResult
{
    public IReadOnlyList<McpServerStatusInfo> Servers { get; set; } = Array.Empty<McpServerStatusInfo>();
}

public sealed class UploadFeedbackRequest
{
    public string Classification { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public bool IncludeLogs { get; set; }

    public string? ThreadId { get; set; }

    public IReadOnlyDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
}

public sealed class UploadFeedbackResult : AppServerOperationResult
{
    public string? ThreadId { get; set; }
}

public sealed class RateLimitWindowInfo
{
    public int UsedPercent { get; set; }

    public long? ResetsAt { get; set; }

    public long? WindowDurationMinutes { get; set; }
}

public sealed class CreditsInfo
{
    public bool HasCredits { get; set; }

    public bool Unlimited { get; set; }

    public string? Balance { get; set; }
}

public sealed class RateLimitInfo
{
    public string? LimitId { get; set; }

    public string? LimitName { get; set; }

    public string? PlanType { get; set; }

    public string? ReachedType { get; set; }

    public RateLimitWindowInfo? Primary { get; set; }

    public RateLimitWindowInfo? Secondary { get; set; }

    public CreditsInfo? Credits { get; set; }
}

public sealed class RateLimitsResult : AppServerOperationResult
{
    public RateLimitInfo? RateLimits { get; set; }

    public IReadOnlyDictionary<string, RateLimitInfo> RateLimitsByLimitId { get; set; } =
        new Dictionary<string, RateLimitInfo>();
}

public sealed class ContextCompactionEvent
{
    public string ThreadId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string? ItemId { get; set; }

    public bool IsCompleted { get; set; }
}

public sealed class ReviewModeEvent
{
    public string ThreadId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string? ItemId { get; set; }

    public ReviewModeChangeKind ChangeKind { get; set; }

    public string? Review { get; set; }
}

public sealed class ThreadGoalEvent
{
    public string ThreadId { get; set; } = string.Empty;

    public string? TurnId { get; set; }

    public ThreadGoalInfo? Goal { get; set; }

    public bool IsCleared { get; set; }
}

public sealed class ConversationEvent
{
    public ConversationEventKind Kind { get; set; }

    public string? ThreadId { get; set; }

    public string? TurnId { get; set; }

    public string? ItemId { get; set; }

    public string? Text { get; set; }

    public string? PayloadJson { get; set; }

    public bool Truncated { get; set; }

    public string? OverflowFile { get; set; }
}

public sealed class ApprovalRequest
{
    public string RequestId { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string ThreadId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string? ItemId { get; set; }

    public ApprovalRiskCategory Risk { get; set; }

    public string RiskKey { get; set; } = string.Empty;

    public string DisplayText { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public bool IsPolicyBlocked { get; set; }

    public string? PolicyBlockReason { get; set; }

    public IReadOnlyList<ApprovalDecision> AvailableDecisions { get; set; } = Array.Empty<ApprovalDecision>();
}

public sealed class ResolveApprovalRequest
{
    public string RequestId { get; set; } = string.Empty;

    public ApprovalDecision Decision { get; set; }
}

public sealed class ApprovalAuditRecord
{
    public string RequestId { get; set; } = string.Empty;

    public ApprovalAuditAction Action { get; set; }

    public ApprovalRiskCategory Risk { get; set; }

    public ApprovalScope Scope { get; set; }

    public string DisplayText { get; set; } = string.Empty;

    public string? ThreadId { get; set; }

    public string? TurnId { get; set; }
}

// Plain classes (no [DataContract]) so StreamJsonRpc/Newtonsoft serializes every public
// property by default, matching ApprovalRequest. These cross only the worker RPC boundary;
// the Remote-UI-bound types are the UserInput*ViewModel classes in the Extension project.
public sealed class UserInputRequest
{
    public string RequestId { get; set; } = string.Empty;

    public string ThreadId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string? ItemId { get; set; }

    public IReadOnlyList<UserInputQuestion> Questions { get; set; } = Array.Empty<UserInputQuestion>();
}

public sealed class UserInputQuestion
{
    public string Id { get; set; } = string.Empty;

    public string Header { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public IReadOnlyList<UserInputOption> Options { get; set; } = Array.Empty<UserInputOption>();
}

public sealed class UserInputOption
{
    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

public sealed class ResolveUserInputRequest
{
    public string RequestId { get; set; } = string.Empty;

    // Maps each question id to the labels the user selected. Single-select answers carry one entry.
    public IDictionary<string, string[]> Answers { get; set; } = new Dictionary<string, string[]>();
}

public interface ICodexWorkerObserver
{
    [JsonRpcMethod("observer/stateChanged")]
    Task OnStateChangedAsync(WorkerStatus status, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/accountChanged")]
    Task OnAccountChangedAsync(AccountStatus status, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/conversationEvent")]
    Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/approvalRequested")]
    Task OnApprovalRequestedAsync(ApprovalRequest approval, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/approvalResolved")]
    Task OnApprovalResolvedAsync(string requestId, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/approvalAudit")]
    Task OnApprovalAuditAsync(ApprovalAuditRecord record, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/userInputRequested")]
    Task OnUserInputRequestedAsync(UserInputRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/userInputResolved")]
    Task OnUserInputResolvedAsync(string requestId, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/contextCompacted")]
    Task OnContextCompactedAsync(ContextCompactionEvent value, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/reviewModeChanged")]
    Task OnReviewModeChangedAsync(ReviewModeEvent value, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/threadGoalChanged")]
    Task OnThreadGoalChangedAsync(ThreadGoalEvent value, CancellationToken cancellationToken);

    [JsonRpcMethod("observer/rateLimitsChanged")]
    Task OnRateLimitsChangedAsync(RateLimitsResult value, CancellationToken cancellationToken);
}

public interface ICodexWorkerClient
{
    [JsonRpcMethod("worker/connect")]
    Task<WorkerStatus> ConnectAsync(WorkerOptions options, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/restart")]
    Task<WorkerStatus> RestartAsync(CancellationToken cancellationToken);

    [JsonRpcMethod("worker/status")]
    Task<WorkerStatus> GetStatusAsync(CancellationToken cancellationToken);

    [JsonRpcMethod("worker/account/status")]
    Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken);

    [JsonRpcMethod("worker/account/login/start")]
    Task<StartAccountLoginResult> StartAccountLoginAsync(CancellationToken cancellationToken);

    [JsonRpcMethod("worker/account/logout")]
    Task<AccountStatus> LogoutAccountAsync(CancellationToken cancellationToken);

    [JsonRpcMethod("worker/thread/start")]
    Task<ThreadSummary> StartThreadAsync(CancellationToken cancellationToken);

    [JsonRpcMethod("worker/thread/resume")]
    Task<ThreadSummary> ResumeThreadAsync(string threadId, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/thread/list")]
    Task<ThreadPage> ListThreadsAsync(string? cursor, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/models/list")]
    Task<ListModelsResult> ListModelsAsync(CancellationToken cancellationToken);

    [JsonRpcMethod("worker/turn/start")]
    Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/turn/steer")]
    Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/turn/interrupt")]
    Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/thread/compact")]
    Task<CompactThreadResult> CompactThreadAsync(CompactThreadRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/review/start")]
    Task<StartReviewResult> StartReviewAsync(StartReviewRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/thread/fork")]
    Task<ForkThreadResult> ForkThreadAsync(ForkThreadRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/thread/goal/get")]
    Task<ThreadGoalResult> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/thread/goal/set")]
    Task<ThreadGoalResult> SetThreadGoalAsync(SetThreadGoalRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/thread/goal/clear")]
    Task<ThreadGoalResult> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/mcp/list")]
    Task<McpServerListResult> ListMcpServersAsync(string? threadId, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/feedback/upload")]
    Task<UploadFeedbackResult> UploadFeedbackAsync(UploadFeedbackRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/account/rateLimits")]
    Task<RateLimitsResult> GetRateLimitsAsync(CancellationToken cancellationToken);

    [JsonRpcMethod("worker/approval/resolve")]
    Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/userInput/resolve")]
    Task ResolveUserInputAsync(ResolveUserInputRequest request, CancellationToken cancellationToken);
}
