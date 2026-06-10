using StreamJsonRpc;

namespace Codex.VisualStudio.Contracts;

public static class ContractVersions
{
    public const int Current = 5;
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

public sealed class WorkerOptions
{
    public int ContractVersion { get; set; } = ContractVersions.Current;

    public string CodexPath { get; set; } = "codex";

    public string WorkingDirectory { get; set; } = string.Empty;

    public string ExtensionVersion { get; set; } = "0.1.0";

    public bool ExperimentalApi { get; set; }
}

public sealed class WorkerStatus
{
    public int ContractVersion { get; set; } = ContractVersions.Current;

    public WorkerConnectionState State { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? ThreadId { get; set; }

    public string? TurnId { get; set; }

    public int? ProcessId { get; set; }
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

public sealed class ThreadSummary
{
    public string Id { get; set; } = string.Empty;

    public string? Preview { get; set; }

    public string? Cwd { get; set; }

    public long? UpdatedAt { get; set; }
}

public sealed class ThreadPage
{
    public IReadOnlyList<ThreadSummary> Threads { get; set; } = Array.Empty<ThreadSummary>();

    public string? NextCursor { get; set; }
}

public sealed class StartTurnRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
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

    [JsonRpcMethod("worker/turn/start")]
    Task<string> StartTurnAsync(StartTurnRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/turn/steer")]
    Task<string> SteerTurnAsync(SteerTurnRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/turn/interrupt")]
    Task InterruptTurnAsync(InterruptTurnRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("worker/approval/resolve")]
    Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken);
}
