using System.Security.Cryptography;
using System.Text;

namespace Codex.VisualStudio.Contracts;

public enum AppServerNoticeKind
{
    ThreadStatusChanged,
    ConfigWarning,
    ModelRerouted,
    ModelVerification,
    McpToolCallProgress,
}

public sealed class AppServerNotice
{
    public AppServerNoticeKind Kind { get; set; }

    public string Identity { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string? ThreadId { get; set; }

    public string? TurnId { get; set; }

    public string? ItemId { get; set; }

    public bool IsInformationalOnly { get; set; }
}

public sealed class PlanStepInfo
{
    public string StepId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}

public sealed class TurnPlanSnapshot
{
    public string ThreadId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string? Explanation { get; set; }

    public IReadOnlyList<PlanStepInfo> Steps { get; set; } = Array.Empty<PlanStepInfo>();

    public bool IsComplete { get; set; }
}

public sealed class TurnPlanDeltaEvent
{
    public string ThreadId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public enum ArtifactPartKind
{
    Text,
    Image,
    File,
    Fallback,
}

public enum ArtifactActionKind
{
    Preview,
    Open,
    Reveal,
}

public sealed class ArtifactPart
{
    public ArtifactPartKind Kind { get; set; }

    public string? Text { get; set; }

    public string? MimeType { get; set; }

    public string? DisplayName { get; set; }

    public bool IsServerTruncated { get; set; }

    public bool IsLocallyTruncated { get; set; }

    public string? ActionId { get; set; }

    public IReadOnlyList<ArtifactActionKind> AllowedActions { get; set; } = Array.Empty<ArtifactActionKind>();
}

public sealed class ArtifactActionRequest : OwnerScopedRequest
{
    public string ActionId { get; set; } = string.Empty;

    public ArtifactActionKind Action { get; set; }
}

public sealed class ArtifactActionResult : AppServerOperationResult
{
    public bool Success { get; set; }

    public string? FailureReason { get; set; }

    public string? LocalPath { get; set; }

    public byte[]? PreviewBytes { get; set; }

    public string? PreviewMimeType { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }
}

public enum ShellCommandOutcome
{
    NotSent,
    Acknowledged,
    DefinitiveFailure,
    OutcomeUnknown,
}

public sealed class ShellCommandPrepareRequest : OwnerScopedRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public long? TimeoutMs { get; set; }

    public int RpcTimeoutMs { get; set; } = 15000;
}

public sealed class ShellCommandConfirmationSnapshot
{
    public string ConfirmationId { get; set; } = string.Empty;

    public string? ConnectionProfile { get; set; }

    public string ThreadId { get; set; } = string.Empty;

    public string? ThreadTitle { get; set; }

    public string Command { get; set; } = string.Empty;

    public string? ServerWorkingDirectory { get; set; }

    public long? TimeoutMs { get; set; }

    public long OwnerGeneration { get; set; }

    public long ConnectionGeneration { get; set; }

    public string? StatePartitionFingerprint { get; set; }

    public bool RunsUnsandboxedWithFullAccess { get; set; }
}

public sealed class ShellCommandPrepareResult : AppServerOperationResult
{
    public bool IsEligible { get; set; }

    public string? RejectionReason { get; set; }

    public ShellCommandConfirmationSnapshot? Confirmation { get; set; }
}

public sealed class ShellCommandExecuteRequest : OwnerScopedRequest
{
    public ShellCommandConfirmationSnapshot Confirmation { get; set; } = new();

    public bool UserConfirmed { get; set; }

    public int RpcTimeoutMs { get; set; } = 15000;
}

public sealed class ShellCommandExecuteResult : AppServerOperationResult
{
    public ShellCommandOutcome Outcome { get; set; }

    public string? ErrorMessage { get; set; }
}

public sealed class SavedAttachmentPayload
{
    public int Version { get; set; }

    public string ServerPath { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}

public static class SavedAttachmentPayloadRegistry
{
    public const string FileAttachmentType = "relaycodex.file.v1";
    public const int CurrentFilePayloadVersion = 1;
    public const int MaximumPayloadBytes = 65536;
    public const int MaximumTypeOrIdentityBytes = 256;
    public const string IdentityKeyPrefix = "sha256:v1:";

    public static bool IsKnownType(string attachmentType) =>
        string.Equals(attachmentType, FileAttachmentType, StringComparison.Ordinal);

    // The caller first normalizes with ServerPath and validates its family against initialize's OS.
    public static string CreateIdentityKey(ServerPath normalizedServerPath, bool serverIsWindows)
    {
        if (normalizedServerPath is null)
        {
            throw new ArgumentNullException(nameof(normalizedServerPath));
        }

        bool pathIsWindows = normalizedServerPath.Family != PathFamily.Posix;
        if (pathIsWindows != serverIsWindows)
        {
            throw new ArgumentException("The path family does not match the initialized server OS.", nameof(normalizedServerPath));
        }

        byte[] digest;
        using (SHA256 sha256 = SHA256.Create())
        {
            digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedServerPath.IdentityValue));
        }

        string hash = BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
        return IdentityKeyPrefix + hash;
    }

    public static bool IsSupportedFilePayloadVersion(int version) => version == CurrentFilePayloadVersion;

    public static bool IsKnownMimeType(string? mimeType) =>
        string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase);
}

public enum AttachmentMutationOutcome
{
    NotSent,
    Created,
    AlreadyExists,
    Removed,
    AlreadyAbsent,
    Rejected,
    OutcomeUnknown,
}

public sealed class SavedAttachmentAddRequest : OwnerScopedRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}

public sealed class SavedAttachmentRemoveRequest : OwnerScopedRequest
{
    public string ThreadId { get; set; } = string.Empty;

    public string AttachmentType { get; set; } = SavedAttachmentPayloadRegistry.FileAttachmentType;

    public string IdentityKey { get; set; } = string.Empty;

    public bool UserConfirmed { get; set; }
}

public sealed class SavedAttachmentMutationResult : AppServerOperationResult
{
    public AttachmentMutationOutcome Outcome { get; set; }

    public ThreadAttachmentMetadata? Attachment { get; set; }

    public string? ErrorMessage { get; set; }
}

public sealed class WindowsSandboxReadinessRequest : OwnerScopedRequest
{
}

public sealed class WindowsSandboxReadinessResult : AppServerOperationResult
{
    public string Status { get; set; } = string.Empty;

    public bool IsWindows { get; set; }

    public bool IsLocalStdio { get; set; }
}

public enum WindowsSandboxSetupState
{
    NotObserved,
    Starting,
    Running,
    Succeeded,
    Failed,
    Unsupported,
    OutcomeUnknown,
}

public enum WindowsSandboxSetupMode
{
    Elevated,
    Unelevated,
}

public sealed class WindowsSandboxSetupStartRequest : OwnerScopedRequest
{
    public WindowsSandboxSetupMode Mode { get; set; }

    public string? Cwd { get; set; }
}

public sealed class WindowsSandboxSetupStartResult : AppServerOperationResult
{
    public WindowsSandboxSetupState State { get; set; }

    public bool? Started { get; set; }

    public string? Message { get; set; }
}

public sealed class WindowsSandboxSetupCompletedEvent
{
    public WindowsSandboxSetupState State { get; set; }

    public WindowsSandboxSetupMode Mode { get; set; }

    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }
}
