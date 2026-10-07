using System;
using System.Collections.Generic;

namespace Codex.VisualStudio.Contracts;

public enum PermissionScope
{
    Turn,
    Session,
}

public sealed class PermissionRequest
{
    public string RequestId { get; set; } = string.Empty;

    public string ThreadId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string? ItemId { get; set; }

    public string? Reason { get; set; }

    public IReadOnlyList<PermissionGrantOption> RequestedPermissions { get; set; } = Array.Empty<PermissionGrantOption>();
}

public sealed class PermissionGrantOption
{
    public string PermissionId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

public sealed class ResolvePermissionSelectionRequest : OwnerScopedRequest
{
    public string RequestId { get; set; } = string.Empty;

    public IReadOnlyList<string> SelectedPermissionIds { get; set; } = Array.Empty<string>();

    public PermissionScope Scope { get; set; } = PermissionScope.Turn;
}

public sealed class ApprovalChoice
{
    public string ChoiceId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

public enum UserInputAnswerKind
{
    SelectedOptions,
    FreeText,
    Other,
}

public enum UserInputAction
{
    Submit,
    Cancel,
}

public sealed class UserInputAnswer
{
    public UserInputAnswerKind Kind { get; set; }

    public IReadOnlyList<string> OptionIds { get; set; } = Array.Empty<string>();

    public string? Text { get; set; }
}

public enum UnsupportedInteractionKind
{
    SecretInput,
    UserVerification,
    McpElicitationSchema,
}

public sealed class UnsupportedInteractionNotice
{
    public UnsupportedInteractionKind Kind { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? ThreadId { get; set; }

    public string? TurnId { get; set; }

    public string? ServerName { get; set; }
}

public enum McpElicitationFieldType
{
    Text,
    Number,
    WholeNumber,
    Boolean,
    SingleSelect,
    MultiSelect,
}

public enum McpElicitationFormat
{
    None,
    Email,
    Uri,
    Date,
    DateTime,
}

public enum McpElicitationAction
{
    Accept,
    Decline,
    Cancel,
}

public enum McpElicitationKind
{
    Form,
    Url,
}

public sealed class McpElicitationRequest
{
    public string RequestId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string? ThreadId { get; set; }

    public string? TurnId { get; set; }

    public McpElicitationKind Kind { get; set; }

    public string Message { get; set; } = string.Empty;

    public IReadOnlyList<McpElicitationField> Fields { get; set; } = Array.Empty<McpElicitationField>();

    public string? OriginDisplay { get; set; }

    public string? OpenAuthorizationActionId { get; set; }
}

public sealed class McpElicitationField
{
    public string Name { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool Required { get; set; }

    public McpElicitationFieldType Type { get; set; }

    public McpElicitationFormat Format { get; set; }

    // A schema default may prepopulate the form, but never submits it on the user's behalf.
    public McpElicitationValue? DefaultValue { get; set; }

    public uint? MinLength { get; set; }

    public uint? MaxLength { get; set; }

    // Preserve the app-server's original inclusive numeric bounds, including for whole numbers.
    public double? MinimumNumber { get; set; }

    public double? MaximumNumber { get; set; }

    // Inclusive integral bounds derived from the original numeric bounds when representable.
    public long? MinimumInteger { get; set; }

    public long? MaximumInteger { get; set; }

    public ulong? MinimumSelections { get; set; }

    public ulong? MaximumSelections { get; set; }

    public IReadOnlyList<McpElicitationChoice> Choices { get; set; } = Array.Empty<McpElicitationChoice>();
}

public sealed class McpElicitationChoice
{
    public string ChoiceId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

public sealed class McpElicitationValue
{
    public string FieldName { get; set; } = string.Empty;

    public string? StringValue { get; set; }

    public long? IntegerValue { get; set; }

    public double? NumberValue { get; set; }

    public bool? BooleanValue { get; set; }

    public IReadOnlyList<string> ChoiceIds { get; set; } = Array.Empty<string>();
}

public sealed class ResolveMcpElicitationRequest : OwnerScopedRequest
{
    public string RequestId { get; set; } = string.Empty;

    public McpElicitationAction Action { get; set; }

    public IReadOnlyList<McpElicitationValue> Values { get; set; } = Array.Empty<McpElicitationValue>();
}

public enum InteractionAuthState
{
    Unavailable,
    Checking,
    Ready,
    LoginPending,
    Authenticated,
    ReauthenticationRequired,
    Failed,
}

public sealed class InteractionAuthStatus
{
    public bool IsLocal { get; set; }

    public bool IsSupported { get; set; }

    public InteractionAuthState State { get; set; }

    public string? Message { get; set; }

    public string? OriginDisplay { get; set; }

    public string? OpenAuthorizationActionId { get; set; }

    public IReadOnlyList<McpServerAuthStatus> McpServers { get; set; } = Array.Empty<McpServerAuthStatus>();
}

public sealed class McpServerAuthStatus
{
    public string ServerName { get; set; } = string.Empty;

    public InteractionAuthState State { get; set; }

    public string? Message { get; set; }

    public string? OriginDisplay { get; set; }

    public string? OpenAuthorizationActionId { get; set; }
}

public sealed class ReadGatewayOAuthRequest : OwnerScopedRequest
{
}

public sealed class LoginGatewayOAuthRequest : OwnerScopedRequest
{
}

public sealed class CancelGatewayOAuthRequest : OwnerScopedRequest
{
}

public sealed class OpenAuthorizationUrlRequest : OwnerScopedRequest
{
    public string ActionId { get; set; } = string.Empty;
}

public sealed class StartMcpOAuthLoginRequest : OwnerScopedRequest
{
    public string ServerName { get; set; } = string.Empty;

    public string? ThreadId { get; set; }
}

public sealed class McpOAuthLoginStatus
{
    public string OperationId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public InteractionAuthState State { get; set; }

    public string? Message { get; set; }

    public string? OriginDisplay { get; set; }

    public string? OpenAuthorizationActionId { get; set; }
}

public sealed class DismissMcpOAuthLoginRequest : OwnerScopedRequest
{
    public string OperationId { get; set; } = string.Empty;
}
