using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class ApprovalModeOption
{
    internal ApprovalModeOption(
        string id,
        string displayText,
        string description,
        string? approvalPolicy,
        string? approvalsReviewer,
        string? sandboxMode,
        string source = "BuiltIn",
        string? permissions = null)
    {
        Id = id;
        DisplayText = displayText;
        Description = description;
        ApprovalPolicy = approvalPolicy;
        ApprovalsReviewer = approvalsReviewer;
        SandboxMode = sandboxMode;
        Source = source;
        Permissions = permissions;
    }

    [DataMember]
    public string Id { get; }

    [DataMember]
    public string Source { get; }

    [DataMember]
    public string DisplayText { get; }

    [DataMember]
    public string Description { get; }

    [DataMember]
    public string AutomationName => $"{DisplayText}. {Description}";

    internal string? ApprovalPolicy { get; }

    internal string? ApprovalsReviewer { get; }

    internal string? SandboxMode { get; }

    internal string? Permissions { get; }
}

internal static class ApprovalModeCatalog
{
    public const string AskId = "ask";
    public const string AutoReviewId = "auto";
    public const string FullAccessId = "full";
    public const string CustomId = "custom";

    public static ObservableCollection<ApprovalModeOption> CreateBuiltIns() =>
    [
        new(AskId, "Ask for approval", "Ask before operations that require approval; workspace writes stay sandboxed.", "on-request", "user", "workspaceWrite"),
        new(AutoReviewId, "Approve on my behalf", "Let the configured reviewer decide approval requests; workspace writes stay sandboxed.", "on-request", "auto_review", "workspaceWrite"),
        new(FullAccessId, "Full access", "Disables the Codex sandbox and normal approval prompts. Operations may run without an extension approval request.", "never", "user", "dangerFullAccess"),
        new(CustomId, "Custom (config.toml)", "Use the app-server configuration without a per-turn approval override.", null, null, null),
    ];
}
