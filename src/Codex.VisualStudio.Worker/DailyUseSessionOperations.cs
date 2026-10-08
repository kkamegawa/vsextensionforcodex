using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

internal static class ShellCommandAdmission
{
    public const int MaximumCommandBytes = 64 * 1024;
    public const int MaximumRpcTimeoutMs = 60_000;

    public static string? Validate(string? command, long? timeoutMs, int rpcTimeoutMs)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "The shell command is empty.";
        }

        if (Encoding.UTF8.GetByteCount(command) > MaximumCommandBytes)
        {
            return "The shell command exceeds the 64 KiB limit.";
        }

        if (timeoutMs is < 0)
        {
            return "The shell execution timeout cannot be negative.";
        }

        if (rpcTimeoutMs is < 1 or > MaximumRpcTimeoutMs)
        {
            return $"The shell acknowledgement timeout must be between 1 and {MaximumRpcTimeoutMs} ms.";
        }

        return null;
    }

    internal static bool IsAcknowledgement(JsonElement response)
        => response.ValueKind == JsonValueKind.Object && !response.EnumerateObject().Any();

    internal static bool TryReadStarted(JsonElement response, out bool started)
    {
        started = false;
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("started", out JsonElement value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        started = value.ValueKind == JsonValueKind.True;
        return true;
    }
}

internal static class SavedAttachmentPayloadCodec
{
    private static readonly HashSet<string> SupportedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "application/pdf",
        "image/png",
        "image/jpeg",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsSupportedMimeType(string? mimeType)
        => mimeType is not null && SupportedMimeTypes.Contains(mimeType);

    public static JsonElement CreatePayload(SavedAttachmentPayload payload)
        => JsonSerializer.SerializeToElement(payload, JsonOptions);

    internal static bool TryReadAddResponse(
        JsonElement response,
        string expectedIdentityKey,
        string expectedServerPath,
        bool serverIsWindows,
        out string outcome,
        out JsonElement attachment)
    {
        outcome = string.Empty;
        attachment = default;
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("outcome", out JsonElement outcomeElement)
            || outcomeElement.ValueKind != JsonValueKind.String
            || outcomeElement.GetString() is not ("created" or "existing")
            || !response.TryGetProperty("attachment", out attachment)
            || attachment.ValueKind != JsonValueKind.Object
            || GetString(attachment, "attachmentType") != SavedAttachmentPayloadRegistry.FileAttachmentType
            || GetString(attachment, "identityKey") != expectedIdentityKey
            || !attachment.TryGetProperty("payload", out JsonElement payload)
            || !TryRead(payload, SavedAttachmentPayloadRegistry.FileAttachmentType, expectedIdentityKey,
                expectedServerPath, serverIsWindows, out _, out _))
        {
            attachment = default;
            return false;
        }

        outcome = outcomeElement.GetString()!;
        return true;
    }

    private static string? GetString(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out JsonElement propertyValue)
            && propertyValue.ValueKind == JsonValueKind.String
                ? propertyValue.GetString()
                : null;

    public static bool TryRead(
        JsonElement payload,
        string attachmentType,
        string identityKey,
        string serverPath,
        bool serverIsWindows,
        out SavedAttachmentPayload decoded,
        out string reason)
    {
        decoded = new SavedAttachmentPayload();
        reason = "The attachment payload is malformed or unsupported.";
        if (!SavedAttachmentPayloadRegistry.IsKnownType(attachmentType))
        {
            reason = "The attachment type is not supported by this client.";
            return false;
        }

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("version", out JsonElement version)
            || !version.TryGetInt32(out int payloadVersion)
            || !payload.TryGetProperty("serverPath", out JsonElement pathValue)
            || pathValue.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("mimeType", out JsonElement mimeValue)
            || mimeValue.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("displayName", out JsonElement nameValue)
            || nameValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? payloadPath = pathValue.GetString();
        string? mimeType = mimeValue.GetString();
        string? displayName = nameValue.GetString();
        if (payloadVersion != SavedAttachmentPayloadRegistry.CurrentFilePayloadVersion
            || string.IsNullOrWhiteSpace(payloadPath)
            || string.IsNullOrWhiteSpace(mimeType)
            || !IsSupportedMimeType(mimeType)
            || string.IsNullOrWhiteSpace(displayName)
            || Encoding.UTF8.GetByteCount(payload.GetRawText()) > SavedAttachmentPayloadRegistry.MaximumPayloadBytes
            || Encoding.UTF8.GetByteCount(attachmentType) > SavedAttachmentPayloadRegistry.MaximumTypeOrIdentityBytes
            || Encoding.UTF8.GetByteCount(identityKey) > SavedAttachmentPayloadRegistry.MaximumTypeOrIdentityBytes)
        {
            reason = "The attachment payload version, size, MIME type, or required fields are invalid.";
            return false;
        }

        if (!ServerPath.TryCreate(payloadPath, out ServerPath parsedPayloadPath)
            || !ServerPathMatchesPlatform(parsedPayloadPath, serverIsWindows)
            || !PathEquals(parsedPayloadPath.Value, serverPath, serverIsWindows)
            || !string.Equals(SavedAttachmentPayloadRegistry.CreateIdentityKey(parsedPayloadPath, serverIsWindows), identityKey, StringComparison.Ordinal))
        {
            reason = "The attachment payload provenance does not match its server path and identity.";
            return false;
        }

        decoded = new SavedAttachmentPayload
        {
            Version = payloadVersion,
            ServerPath = payloadPath,
            MimeType = mimeType,
            DisplayName = displayName,
        };
        reason = string.Empty;
        return true;
    }

    private static bool PathEquals(string left, string right, bool windows)
        => string.Equals(left, right, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool ServerPathMatchesPlatform(ServerPath path, bool serverIsWindows)
        => serverIsWindows
            ? path.Family is PathFamily.WindowsDrive or PathFamily.WindowsUnc
            : path.Family == PathFamily.Posix;
}

public partial interface ICodexSessionService
{
    event Func<WindowsSandboxSetupCompletedEvent, CancellationToken, Task>? WindowsSandboxSetupCompleted;

    Task<ShellCommandPrepareResult> PrepareShellCommandAsync(
        ShellCommandPrepareRequest request,
        string? connectionProfile,
        CancellationToken cancellationToken);

    Task<ShellCommandExecuteResult> ExecuteShellCommandAsync(
        ShellCommandExecuteRequest request,
        string? connectionProfile,
        CancellationToken cancellationToken);

    Task<SavedAttachmentMutationResult> AddSavedAttachmentAsync(
        SavedAttachmentAddRequest request,
        CancellationToken cancellationToken);

    Task<SavedAttachmentMutationResult> RemoveSavedAttachmentAsync(
        SavedAttachmentRemoveRequest request,
        CancellationToken cancellationToken);

    Task<WindowsSandboxReadinessResult> GetWindowsSandboxReadinessAsync(
        WindowsSandboxReadinessRequest request,
        CancellationToken cancellationToken);

    Task<WindowsSandboxSetupStartResult> StartWindowsSandboxSetupAsync(
        WindowsSandboxSetupStartRequest request,
        CancellationToken cancellationToken);

    Task<ArtifactActionResult> ResolveArtifactActionAsync(
        ArtifactActionRequest request,
        CancellationToken cancellationToken);
}

public sealed partial class CodexSessionService
{
    private static readonly TimeSpan ShellConfirmationLifetime = TimeSpan.FromMinutes(5);
    private const int MaxShellConfirmations = 32;
    private const int MaxThreadAttachmentPages = 2;
    private const int MaxThreadAttachments = 100;
    private const int MaxThreadAttachmentsPerPage = 50;
    private const int MaxSavedAttachmentIdentityLength = 256;
    private readonly object dailyUseGate = new();
    private readonly Dictionary<string, ShellConfirmationEntry> shellConfirmations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingShellSubmission> pendingShellSubmissions = new(StringComparer.Ordinal);
    private long windowsSandboxAttemptGeneration = -1;
    private long windowsSandboxAttemptOwnerGeneration = -1;
    private string? windowsSandboxAttemptPartitionFingerprint;
    private WindowsSandboxSetupMode? windowsSandboxAttemptMode;
    private WindowsSandboxSetupState windowsSandboxSetupState = WindowsSandboxSetupState.NotObserved;

    public async Task<ShellCommandPrepareResult> PrepareShellCommandAsync(
        ShellCommandPrepareRequest request,
        string? connectionProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? invalid = ShellCommandAdmission.Validate(request.Command, request.TimeoutMs, request.RpcTimeoutMs);
        if (invalid is not null)
        {
            return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = invalid };
        }

        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        if (request.OwnerGeneration != context.OwnerGeneration
            || !string.Equals(request.StatePartitionFingerprint, context.StatePartitionFingerprint, StringComparison.Ordinal))
        {
            return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = "The connection owner changed. Prepare the command again." };
        }

        if (!string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal)
            || !TryGetThreadStatus(request.ThreadId, request.ConnectionGeneration, out string threadStatus)
            || !string.Equals(threadStatus, "idle", StringComparison.OrdinalIgnoreCase)
            || ActiveTurnId is not null)
        {
            return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = "The selected thread must be the current joined thread with a fresh idle status." };
        }

        string? serverCwd = await GetServerWorkingDirectoryAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        if (cancellationToken.IsCancellationRequested)
        {
            return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = "Command preparation was canceled." };
        }
        if (!TryGetThreadStatus(request.ThreadId, request.ConnectionGeneration, out threadStatus)
            || !string.Equals(threadStatus, "idle", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal)
            || ActiveTurnId is not null)
        {
            return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = "The thread status changed while preparing the confirmation." };
        }
        if (string.IsNullOrWhiteSpace(serverCwd))
        {
            return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = "The thread working directory is unavailable." };
        }

        string? localCwd = remotePathMapper is null ? serverCwd : MapServerPathToLocal(serverCwd);
        if (string.IsNullOrWhiteSpace(localCwd))
        {
            return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = "The server working directory is outside the configured local mapping." };
        }

        ApprovalPolicyResult policy = approvalPolicy.EvaluateCommand(request.Command, localCwd, options.WorkingDirectory, null, null);
        if (policy.IsBlocked)
        {
            return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = policy.BlockReason ?? "The local approval policy blocks this command." };
        }

        var snapshot = new ShellCommandConfirmationSnapshot
        {
            ConfirmationId = Guid.NewGuid().ToString("N"),
            ConnectionProfile = connectionProfile,
            ThreadId = request.ThreadId,
            Command = request.Command,
            ServerWorkingDirectory = serverCwd,
            TimeoutMs = request.TimeoutMs,
            OwnerGeneration = context.OwnerGeneration,
            ConnectionGeneration = request.ConnectionGeneration,
            StatePartitionFingerprint = context.StatePartitionFingerprint,
            RunsUnsandboxedWithFullAccess = true,
        };

        lock (dailyUseGate)
        {
            PruneShellConfirmationsLocked();
            if (shellConfirmations.Count >= MaxShellConfirmations)
            {
                return new ShellCommandPrepareResult { IsEligible = false, RejectionReason = "Too many unconfirmed shell commands are pending. Close an old confirmation and try again." };
            }

            shellConfirmations.Add(snapshot.ConfirmationId, new ShellConfirmationEntry(snapshot, timeProvider.GetUtcNow()));
        }

        return new ShellCommandPrepareResult { IsEligible = true, Confirmation = snapshot };
    }

    public async Task<ShellCommandExecuteResult> ExecuteShellCommandAsync(
        ShellCommandExecuteRequest request,
        string? connectionProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? invalid = ShellCommandAdmission.Validate(request.Confirmation.Command, request.Confirmation.TimeoutMs, request.RpcTimeoutMs);
        if (invalid is not null)
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent, ErrorMessage = invalid };
        }

        ShellCommandConfirmationSnapshot expected;
        lock (dailyUseGate)
        {
            PruneShellConfirmationsLocked();
            if (!shellConfirmations.TryGetValue(request.Confirmation.ConfirmationId, out ShellConfirmationEntry? entry)
                || !SnapshotEquals(entry.Snapshot, request.Confirmation))
            {
                return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent, ErrorMessage = "This shell confirmation is expired, unknown, or has been changed." };
            }

            expected = entry.Snapshot;
            shellConfirmations.Remove(expected.ConfirmationId);
        }

        if (!request.UserConfirmed)
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent };
        }

        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        if (!SnapshotMatchesCurrentOwner(expected, request, context)
            || !string.Equals(expected.ConnectionProfile, connectionProfile, StringComparison.Ordinal)
            || !string.Equals(expected.ThreadId, ActiveThreadId, StringComparison.Ordinal)
            || ActiveTurnId is not null
            || !TryGetThreadStatus(expected.ThreadId, expected.ConnectionGeneration, out string threadStatus)
            || !string.Equals(threadStatus, "idle", StringComparison.OrdinalIgnoreCase))
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent, ErrorMessage = "The thread or connection changed after confirmation. Review and prepare the command again." };
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent };
        }
        string? currentServerCwd = await GetServerWorkingDirectoryAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        if (cancellationToken.IsCancellationRequested)
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent };
        }
        if (!SnapshotMatchesCurrentOwner(expected, request, context)
            || !string.Equals(expected.ConnectionProfile, connectionProfile, StringComparison.Ordinal)
            || !string.Equals(expected.ThreadId, ActiveThreadId, StringComparison.Ordinal)
            || ActiveTurnId is not null
            || !TryGetThreadStatus(expected.ThreadId, expected.ConnectionGeneration, out threadStatus)
            || !string.Equals(threadStatus, "idle", StringComparison.OrdinalIgnoreCase))
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent, ErrorMessage = "The thread or connection changed before dispatch." };
        }
        string? currentLocalCwd = currentServerCwd is null ? null : remotePathMapper is null ? currentServerCwd : MapServerPathToLocal(currentServerCwd);
        if (!string.Equals(currentServerCwd, expected.ServerWorkingDirectory, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(currentLocalCwd))
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent, ErrorMessage = "The thread working directory changed after confirmation." };
        }

        ApprovalPolicyResult policy = approvalPolicy.EvaluateCommand(expected.Command, currentLocalCwd, options.WorkingDirectory, null, null);
        if (policy.IsBlocked)
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent, ErrorMessage = policy.BlockReason ?? "The local approval policy blocks this command." };
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent };
        }
        EnsureCurrent(context);
        string? finalServerCwd = await GetServerWorkingDirectoryAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        if (cancellationToken.IsCancellationRequested)
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent };
        }
        if (!SnapshotMatchesCurrentOwner(expected, request, context)
            || !string.Equals(expected.ConnectionProfile, connectionProfile, StringComparison.Ordinal)
            || !string.Equals(expected.ThreadId, ActiveThreadId, StringComparison.Ordinal)
            || ActiveTurnId is not null
            || !string.Equals(finalServerCwd, expected.ServerWorkingDirectory, StringComparison.Ordinal)
            || !TryGetThreadStatus(expected.ThreadId, expected.ConnectionGeneration, out threadStatus)
            || !string.Equals(threadStatus, "idle", StringComparison.OrdinalIgnoreCase))
        {
            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent, ErrorMessage = "The shell command target changed immediately before dispatch." };
        }

        Guid pendingId = Guid.NewGuid();
        lock (dailyUseGate)
        {
            if (pendingShellSubmissions.TryGetValue(expected.ThreadId, out PendingShellSubmission? pending)
                && pending.ConnectionGeneration == expected.ConnectionGeneration)
            {
                return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.NotSent, ErrorMessage = "A shell command is already pending for this thread." };
            }

            pendingShellSubmissions[expected.ThreadId] = new PendingShellSubmission(expected.ConnectionGeneration, pendingId);
        }

        try
        {
            JsonElement result = await context.Connection.SendRequestAsync(
                "thread/shellCommand",
                new { threadId = expected.ThreadId, command = expected.Command, timeoutMs = expected.TimeoutMs },
                TimeSpan.FromMilliseconds(request.RpcTimeoutMs),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            if (!SnapshotMatchesCurrentOwner(expected, request, context)
                || !string.Equals(expected.ConnectionProfile, connectionProfile, StringComparison.Ordinal)
                || !ShellCommandAdmission.IsAcknowledgement(result))
            {
                return new ShellCommandExecuteResult
                {
                    Outcome = ShellCommandOutcome.OutcomeUnknown,
                    ErrorMessage = "The app-server returned an invalid shell acknowledgement; execution outcome is unknown.",
                };
            }

            ReleasePendingShell(expected.ThreadId, expected.ConnectionGeneration, pendingId);

            return new ShellCommandExecuteResult { Outcome = ShellCommandOutcome.Acknowledged };
        }
        catch (JsonRpcRemoteException ex)
        {
            ReleasePendingShell(expected.ThreadId, expected.ConnectionGeneration, pendingId);

            return new ShellCommandExecuteResult
            {
                Outcome = ShellCommandOutcome.DefinitiveFailure,
                ErrorMessage = SafeError(ex.Message),
            };
        }
        catch (Exception ex)
        {
            // Once sent, an acknowledgement timeout or transport close is delivery-uncertain.
            // Keep the per-thread lock until this generation retires; never replay the command.
            return new ShellCommandExecuteResult
            {
                Outcome = ShellCommandOutcome.OutcomeUnknown,
                ErrorMessage = SafeError(ex.Message),
            };
        }
    }

    public async Task<SavedAttachmentMutationResult> AddSavedAttachmentAsync(
        SavedAttachmentAddRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        if (!string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal))
        {
            return AttachmentRejected("Saved attachments can only be added to the current joined thread.");
        }

        if (!SavedAttachmentPayloadCodec.IsSupportedMimeType(request.MimeType)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || Encoding.UTF8.GetByteCount(request.DisplayName) > 512
            || !LocalPath.TryCreate(request.LocalPath, out LocalPath localPath)
            || !File.Exists(localPath.Value)
            || Directory.Exists(localPath.Value))
        {
            return AttachmentRejected("Select an existing file with a supported MIME type and a valid display name.");
        }

        PathAccessResult pathResult = pathAccessPolicy.Evaluate(localPath.Value, options.WorkingDirectory);
        ApprovalPolicyResult filePolicy = approvalPolicy.EvaluateFile(localPath.Value, options.WorkingDirectory);
        if (!pathResult.IsValid || !pathResult.IsWithinWorkspace || protectedDirectoryPolicy.IsProtected(pathResult.NormalizedPath)
            || filePolicy.IsBlocked
            || !LocalPath.TryCreate(options.WorkingDirectory, out LocalPath localRoot)
            || !localPathBoundary.IsWithinRoot(localRoot, localPath))
        {
            return AttachmentRejected(pathResult.Reason ?? "The selected file is outside the mapped workspace or is protected.");
        }

        string serverPath;
        if (remotePathMapper is null)
        {
            serverPath = pathResult.NormalizedPath;
        }
        else if (!remotePathMapper.TryMapLocalToServer(localPath, localPathBoundary, out ServerPath mapped, out _))
        {
            return AttachmentRejected("The selected file is outside the configured local/server root mapping.");
        }
        else
        {
            serverPath = mapped.Value;
        }

        bool serverIsWindows = IsServerWindows(InitializationMetadata);
        if (!ServerPath.TryCreate(serverPath, out ServerPath parsedServerPath)
            || !ServerPathMatchesPlatform(parsedServerPath, serverIsWindows))
        {
            return AttachmentRejected("The mapped server path does not match the initialized server operating system.");
        }

        string identityKey = SavedAttachmentPayloadRegistry.CreateIdentityKey(parsedServerPath, serverIsWindows);
        IReadOnlyList<JsonElement> existing;
        try
        {
            existing = await ReadRawThreadAttachmentsAsync(context, request.ThreadId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.NotSent };
        }
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        if (!string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal))
        {
            return AttachmentRejected("The connection or joined thread changed before the attachment could be added.");
        }
        if (existing.Any(item => GetString(item, "attachmentType") == SavedAttachmentPayloadRegistry.FileAttachmentType
            && GetString(item, "identityKey") == identityKey))
        {
            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.AlreadyExists };
        }

        var payload = new SavedAttachmentPayload
        {
            Version = SavedAttachmentPayloadRegistry.CurrentFilePayloadVersion,
            ServerPath = parsedServerPath.Value,
            MimeType = request.MimeType.ToLowerInvariant(),
            DisplayName = request.DisplayName,
        };
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload)) > SavedAttachmentPayloadRegistry.MaximumPayloadBytes)
        {
            return AttachmentRejected("The saved attachment metadata exceeds the payload limit.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.NotSent };
        }
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        PathAccessResult dispatchPath = pathAccessPolicy.Evaluate(localPath.Value, options.WorkingDirectory);
        ApprovalPolicyResult dispatchPolicy = approvalPolicy.EvaluateFile(localPath.Value, options.WorkingDirectory);
        if (!File.Exists(localPath.Value)
            || Directory.Exists(localPath.Value)
            || !dispatchPath.IsValid
            || !dispatchPath.IsWithinWorkspace
            || protectedDirectoryPolicy.IsProtected(dispatchPath.NormalizedPath)
            || dispatchPolicy.IsBlocked
            || !localPathBoundary.IsWithinRoot(localRoot, localPath))
        {
            return AttachmentRejected(dispatchPolicy.BlockReason ?? dispatchPath.Reason ?? "The selected file is no longer available within the mapped workspace.");
        }

        try
        {
            JsonElement response = await context.Connection.SendRequestAsync(
                "thread/attachment/add",
                new
                {
                    threadId = request.ThreadId,
                    attachmentType = SavedAttachmentPayloadRegistry.FileAttachmentType,
                    identityKey,
                    payload = SavedAttachmentPayloadCodec.CreatePayload(payload),
                },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            ValidateDailyUseOwner(request, context);
            if (!SavedAttachmentPayloadCodec.TryReadAddResponse(response, identityKey, parsedServerPath.Value,
                serverIsWindows, out string outcome, out JsonElement attachment))
            {
                throw new InvalidDataException("The app-server returned an invalid saved attachment result.");
            }

            ThreadAttachmentMetadata info = ProjectSavedAttachment(context, attachment);
            if (!info.IsKnownPayload || !info.CanRemove)
            {
                throw new InvalidDataException("The app-server returned saved attachment metadata with invalid provenance.");
            }

            return new SavedAttachmentMutationResult
            {
                Outcome = outcome == "existing" ? AttachmentMutationOutcome.AlreadyExists : AttachmentMutationOutcome.Created,
                Attachment = info,
            };
        }
        catch (JsonRpcRemoteException ex)
        {
            if (ex.Code == -32601)
            {
                return new SavedAttachmentMutationResult { IsSupported = false, UnavailableReason = "This app-server does not support saved attachments." };
            }

            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.Rejected, ErrorMessage = SafeError(ex.Message) };
        }
        catch (Exception ex)
        {
            // A read-only reconciliation may describe current state, but cannot prove which actor
            // created it. Preserve OutcomeUnknown and never retry the add.
            try
            {
                _ = await ReadRawThreadAttachmentsAsync(context, request.ThreadId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reconcileException)
            {
                WorkerDiagnostics.Write("saved attachment add reconciliation failed", reconcileException);
            }

            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.OutcomeUnknown, ErrorMessage = SafeError(ex.Message) };
        }
    }

    public async Task<SavedAttachmentMutationResult> RemoveSavedAttachmentAsync(
        SavedAttachmentRemoveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        if (!SavedAttachmentPayloadRegistry.IsKnownType(request.AttachmentType)
            || !IsValidIdentityKey(request.IdentityKey))
        {
            return AttachmentRejected("The saved attachment type or identity key is invalid or unsupported.");
        }

        if (!request.UserConfirmed)
        {
            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.NotSent };
        }

        if (!SavedAttachmentPayloadRegistry.IsKnownType(request.AttachmentType)
            || !IsValidIdentityKey(request.IdentityKey)
            || !string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(request.AttachmentType) > MaxSavedAttachmentIdentityLength
            || Encoding.UTF8.GetByteCount(request.IdentityKey) > MaxSavedAttachmentIdentityLength)
        {
            return AttachmentRejected("The saved attachment target or identity is invalid.");
        }

        IReadOnlyList<JsonElement> records;
        try
        {
            records = await ReadRawThreadAttachmentsAsync(context, request.ThreadId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.NotSent };
        }
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        if (!string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal))
        {
            return AttachmentRejected("The connection or joined thread changed before the attachment could be removed.");
        }

        JsonElement record = records.FirstOrDefault(item => GetString(item, "attachmentType") == request.AttachmentType
            && GetString(item, "identityKey") == request.IdentityKey);
        if (record.ValueKind == JsonValueKind.Undefined)
        {
            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.AlreadyAbsent };
        }

        string provenanceReason = "The attachment payload cannot be validated for removal.";
        if (!TryGetDailyProperty(record, "payload", out JsonElement payload)
            || !TryGetDailyProperty(payload, "serverPath", out JsonElement serverPathElement)
            || serverPathElement.ValueKind != JsonValueKind.String
            || !ServerPath.TryCreate(serverPathElement.GetString(), out ServerPath serverPath)
            || !ServerPathMatchesPlatform(serverPath, IsServerWindows(InitializationMetadata))
            || !SavedAttachmentPayloadCodec.TryRead(payload, request.AttachmentType, request.IdentityKey, serverPath.Value,
                IsServerWindows(InitializationMetadata), out _, out provenanceReason))
        {
            return AttachmentRejected(provenanceReason);
        }

        string? localPath;
        if (remotePathMapper is null)
        {
            localPath = serverPath.Value;
        }
        else if (remotePathMapper.TryMapServerToLocal(serverPath, localPathBoundary, out LocalPath mappedLocal, out _))
        {
            localPath = mappedLocal.Value;
        }
        else
        {
            return AttachmentRejected("The saved attachment path is outside the configured mapping.");
        }

        PathAccessResult policyPath = pathAccessPolicy.Evaluate(localPath, options.WorkingDirectory);
        ApprovalPolicyResult removePolicy = approvalPolicy.EvaluateFile(localPath, options.WorkingDirectory);
        if (!policyPath.IsValid || !policyPath.IsWithinWorkspace || protectedDirectoryPolicy.IsProtected(policyPath.NormalizedPath)
            || removePolicy.IsBlocked
            || !LocalPath.TryCreate(options.WorkingDirectory, out LocalPath removeRoot)
            || !LocalPath.TryCreate(localPath, out LocalPath removePath)
            || !localPathBoundary.IsWithinRoot(removeRoot, removePath))
        {
            return AttachmentRejected(removePolicy.BlockReason ?? policyPath.Reason ?? "The saved attachment is outside the workspace or protected.");
        }

        // Removal concerns metadata only. Do not require the referenced file to still exist.
        if (cancellationToken.IsCancellationRequested)
        {
            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.NotSent };
        }
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        try
        {
            JsonElement response = await context.Connection.SendRequestAsync(
                "thread/attachment/remove",
                new { threadId = request.ThreadId, attachmentType = request.AttachmentType, identityKey = request.IdentityKey },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            ValidateDailyUseOwner(request, context);
            if (response.ValueKind != JsonValueKind.Object || response.EnumerateObject().Any())
            {
                throw new InvalidDataException("The app-server returned an invalid saved attachment removal result.");
            }

            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.Removed };
        }
        catch (JsonRpcRemoteException ex)
        {
            if (ex.Code == -32601)
            {
                return new SavedAttachmentMutationResult { IsSupported = false, UnavailableReason = "This app-server does not support saved attachments." };
            }

            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.Rejected, ErrorMessage = SafeError(ex.Message) };
        }
        catch (Exception ex)
        {
            try
            {
                _ = await ReadRawThreadAttachmentsAsync(context, request.ThreadId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reconcileException)
            {
                WorkerDiagnostics.Write("saved attachment remove reconciliation failed", reconcileException);
            }

            return new SavedAttachmentMutationResult { Outcome = AttachmentMutationOutcome.OutcomeUnknown, ErrorMessage = SafeError(ex.Message) };
        }
    }

    public async Task<WindowsSandboxReadinessResult> GetWindowsSandboxReadinessAsync(
        WindowsSandboxReadinessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        if (!context.IsLocal || !IsServerWindows(InitializationMetadata))
        {
            return new WindowsSandboxReadinessResult
            {
                IsSupported = false,
                UnavailableReason = "Windows sandbox setup is available only for an initialized local Windows stdio connection.",
                Status = "Unsupported",
                IsWindows = IsServerWindows(InitializationMetadata),
                IsLocalStdio = context.IsLocal,
            };
        }

        JsonElement response;
        try
        {
            response = await context.Connection.SendRequestAsync("windowsSandbox/readiness", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == -32601)
        {
            return new WindowsSandboxReadinessResult { IsSupported = false, UnavailableReason = "This app-server does not support Windows sandbox readiness.", IsWindows = true, IsLocalStdio = true };
        }
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        string? readiness = GetString(response, "status");
        if (readiness is not ("ready" or "notConfigured" or "updateRequired"))
        {
            return new WindowsSandboxReadinessResult { IsSupported = false, UnavailableReason = "The app-server returned an unknown Windows sandbox readiness state.", IsWindows = true, IsLocalStdio = true };
        }
        return new WindowsSandboxReadinessResult
        {
            IsWindows = true,
            IsLocalStdio = true,
            Status = readiness,
        };
    }

    public async Task<WindowsSandboxSetupStartResult> StartWindowsSandboxSetupAsync(
        WindowsSandboxSetupStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        if (!context.IsLocal || !IsServerWindows(InitializationMetadata))
        {
            return new WindowsSandboxSetupStartResult
            {
                IsSupported = false,
                UnavailableReason = "Windows sandbox setup is available only for an initialized local Windows stdio connection.",
                State = WindowsSandboxSetupState.Unsupported,
            };
        }

        if (!Enum.IsDefined(request.Mode))
        {
            return new WindowsSandboxSetupStartResult { State = WindowsSandboxSetupState.Unsupported, Message = "The Windows sandbox setup mode is invalid." };
        }

        string? cwd = request.Cwd;
        if (cwd is not null)
        {
            if (!LocalPath.TryCreate(cwd, out LocalPath localCwd)
                || !LocalPath.TryCreate(options.WorkingDirectory, out LocalPath localRoot)
                || !localPathBoundary.IsWithinRoot(localRoot, localCwd))
            {
                return new WindowsSandboxSetupStartResult { State = WindowsSandboxSetupState.Failed, Message = "The setup working directory must be the active solution root." };
            }

            cwd = localCwd.Value;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new WindowsSandboxSetupStartResult { State = WindowsSandboxSetupState.NotObserved, Started = false, Message = "Windows sandbox setup was canceled before dispatch." };
        }

        lock (dailyUseGate)
        {
            if (windowsSandboxAttemptGeneration == context.Generation)
            {
                if (windowsSandboxAttemptOwnerGeneration != context.OwnerGeneration
                    || !string.Equals(windowsSandboxAttemptPartitionFingerprint, context.StatePartitionFingerprint, StringComparison.Ordinal))
                {
                    windowsSandboxSetupState = WindowsSandboxSetupState.NotObserved;
                    return new WindowsSandboxSetupStartResult
                    {
                        State = WindowsSandboxSetupState.NotObserved,
                        Message = "A setup attempt belongs to a previous connection owner and cannot be shown or retried in this generation.",
                    };
                }

                return new WindowsSandboxSetupStartResult { State = windowsSandboxSetupState, Message = "A Windows sandbox setup attempt was already made for this connection." };
            }

            windowsSandboxAttemptGeneration = context.Generation;
            windowsSandboxAttemptOwnerGeneration = context.OwnerGeneration;
            windowsSandboxAttemptPartitionFingerprint = context.StatePartitionFingerprint;
            windowsSandboxAttemptMode = request.Mode;
            windowsSandboxSetupState = WindowsSandboxSetupState.Starting;
        }

        JsonElement response;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Nothing was sent, so release this generation's single attempt for a retry.
                lock (dailyUseGate)
                {
                    if (IsCurrentWindowsSandboxAttemptLocked(context)
                        && windowsSandboxSetupState == WindowsSandboxSetupState.Starting)
                    {
                        windowsSandboxAttemptGeneration = -1;
                        windowsSandboxAttemptOwnerGeneration = -1;
                        windowsSandboxAttemptPartitionFingerprint = null;
                        windowsSandboxAttemptMode = null;
                        windowsSandboxSetupState = WindowsSandboxSetupState.NotObserved;
                    }
                }

                return new WindowsSandboxSetupStartResult { State = WindowsSandboxSetupState.NotObserved, Started = false, Message = "Windows sandbox setup was canceled before dispatch." };
            }

            response = await context.Connection.SendRequestAsync(
                "windowsSandbox/setupStart",
                new { mode = request.Mode == WindowsSandboxSetupMode.Elevated ? "elevated" : "unelevated", cwd },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == -32601)
        {
            lock (dailyUseGate)
            {
                if (IsCurrentWindowsSandboxAttemptLocked(context)
                    && windowsSandboxSetupState == WindowsSandboxSetupState.Starting)
                {
                    windowsSandboxSetupState = WindowsSandboxSetupState.Unsupported;
                }

                if (!IsCurrentWindowsSandboxAttemptLocked(context))
                {
                    return new WindowsSandboxSetupStartResult { State = WindowsSandboxSetupState.NotObserved, Started = false, Message = "The setup attempt belongs to a previous connection owner." };
                }

                return new WindowsSandboxSetupStartResult { IsSupported = false, State = windowsSandboxSetupState, Started = false, Message = "This app-server does not support Windows sandbox setup." };
            }
        }
        catch (JsonRpcRemoteException ex)
        {
            lock (dailyUseGate)
            {
                if (IsCurrentWindowsSandboxAttemptLocked(context)
                    && windowsSandboxSetupState == WindowsSandboxSetupState.Starting)
                {
                    windowsSandboxSetupState = WindowsSandboxSetupState.Failed;
                }

                if (!IsCurrentWindowsSandboxAttemptLocked(context))
                {
                    return new WindowsSandboxSetupStartResult { State = WindowsSandboxSetupState.NotObserved, Started = null, Message = "The setup attempt belongs to a previous connection owner." };
                }

                return new WindowsSandboxSetupStartResult { State = windowsSandboxSetupState, Started = false, Message = SafeError(ex.Message) };
            }
        }
        catch (Exception ex)
        {
            lock (dailyUseGate)
            {
                if (!IsCurrentWindowsSandboxAttemptLocked(context))
                {
                    return new WindowsSandboxSetupStartResult { State = WindowsSandboxSetupState.NotObserved, Started = null, Message = "The setup attempt belongs to a previous connection owner." };
                }

                if (windowsSandboxSetupState == WindowsSandboxSetupState.Starting)
                {
                    windowsSandboxSetupState = WindowsSandboxSetupState.OutcomeUnknown;
                }

                return new WindowsSandboxSetupStartResult { State = windowsSandboxSetupState, Started = null, Message = SafeError(ex.Message) };
            }
        }

        EnsureCurrent(context);
        ValidateDailyUseOwner(request, context);
        if (!ShellCommandAdmission.TryReadStarted(response, out bool started))
        {
            lock (dailyUseGate)
            {
                if (windowsSandboxSetupState == WindowsSandboxSetupState.Starting)
                {
                    windowsSandboxSetupState = WindowsSandboxSetupState.OutcomeUnknown;
                }

                return new WindowsSandboxSetupStartResult
                {
                    State = windowsSandboxSetupState,
                    Started = null,
                    Message = "The app-server returned an invalid setup acknowledgement; setup outcome is unknown.",
                };
            }
        }

        lock (dailyUseGate)
        {
            if (windowsSandboxSetupState == WindowsSandboxSetupState.Starting)
            {
                windowsSandboxSetupState = started ? WindowsSandboxSetupState.Running : WindowsSandboxSetupState.Failed;
            }

            return new WindowsSandboxSetupStartResult
            {
                State = windowsSandboxSetupState,
                Started = started,
                Message = started ? "Windows sandbox setup was started." : "The server did not start Windows sandbox setup.",
            };
        }
    }

    private bool TryCompleteWindowsSandboxSetup(
        ConnectionContext context,
        string mode,
        bool success,
        string? error,
        out WindowsSandboxSetupCompletedEvent completion)
    {
        lock (dailyUseGate)
        {
            WindowsSandboxSetupMode? expected = mode switch
            {
                "elevated" => WindowsSandboxSetupMode.Elevated,
                "unelevated" => WindowsSandboxSetupMode.Unelevated,
                _ => null,
            };
            if (!IsCurrentWindowsSandboxAttemptLocked(context)
                || expected is null
                || windowsSandboxAttemptMode != expected
                || windowsSandboxSetupState is not (WindowsSandboxSetupState.Starting or WindowsSandboxSetupState.Running or WindowsSandboxSetupState.OutcomeUnknown))
            {
                completion = new WindowsSandboxSetupCompletedEvent();
                return false;
            }

            windowsSandboxSetupState = success ? WindowsSandboxSetupState.Succeeded : WindowsSandboxSetupState.Failed;
            completion = new WindowsSandboxSetupCompletedEvent
            {
                State = windowsSandboxSetupState,
                Mode = expected.Value,
                Success = success,
                ErrorMessage = error is null ? null : SafeError(error),
            };
            return true;
        }
    }

    private async Task<IReadOnlyList<JsonElement>> ReadRawThreadAttachmentsAsync(
        ConnectionContext context,
        string threadId,
        CancellationToken cancellationToken)
    {
        var results = new List<JsonElement>();
        string? cursor = null;
        for (int pageNumber = 0; pageNumber < MaxThreadAttachmentPages && results.Count < MaxThreadAttachments; pageNumber++)
        {
            JsonElement page = await context.Connection.SendReadOnlyRequestAsync(
                "thread/attachment/list",
                new { threadId, cursor, limit = MaxThreadAttachmentsPerPage },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            if (!page.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The server returned an invalid thread attachment page.");
            }

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    results.Add(item.Clone());
                    if (results.Count == MaxThreadAttachments)
                    {
                        break;
                    }
                }
            }

            cursor = GetString(page, "nextCursor");
            if (string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        EnsureCurrent(context);
        return results;
    }

    private ThreadAttachmentMetadata ProjectSavedAttachment(ConnectionContext context, JsonElement attachment)
    {
        string? attachmentType = GetString(attachment, "attachmentType");
        string? identityKey = GetString(attachment, "identityKey");
        JsonElement payload = TryGetDailyProperty(attachment, "payload", out JsonElement payloadValue) ? payloadValue : default;
        bool serverIsWindows = IsServerWindows(InitializationMetadata);
        string? serverPathText = TryGetDailyProperty(payload, "serverPath", out JsonElement pathElement)
            && pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString()
                : null;
        ThreadAttachmentMetadata result = new()
        {
            Id = GetString(attachment, "id") ?? string.Empty,
            AttachmentType = attachmentType ?? string.Empty,
            IdentityKey = identityKey ?? string.Empty,
            DisplayName = "Unknown saved attachment",
            IsKnownPayload = false,
            CanRemove = false,
            AllowedActions = [],
        };

        if (attachmentType is null
            || identityKey is null
            || !SavedAttachmentPayloadRegistry.IsKnownType(attachmentType)
            || !IsValidIdentityKey(identityKey)
            || serverPathText is null
            || !ServerPath.TryCreate(serverPathText, out ServerPath serverPath)
            || !ServerPathMatchesPlatform(serverPath, serverIsWindows)
            || !SavedAttachmentPayloadCodec.TryRead(payload, attachmentType, identityKey, serverPath.Value,
                serverIsWindows, out SavedAttachmentPayload decoded, out _))
        {
            return result;
        }

        var actions = new List<ArtifactActionKind> { ArtifactActionKind.Open, ArtifactActionKind.Reveal };
        if (string.Equals(decoded.MimeType, "image/png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decoded.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            actions.Insert(0, ArtifactActionKind.Preview);
        }

        bool registered = TryRegisterServerPath(context, serverPath, actions, out string actionId);
        result.DisplayName = redactor.Redact(decoded.DisplayName);
        result.MimeType = decoded.MimeType;
        result.IsKnownPayload = true;
        result.CanRemove = true;
        result.ActionId = registered ? actionId : null;
        result.AllowedActions = registered ? actions : [];
        return result;
    }

    private async Task<string?> GetServerWorkingDirectoryAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ActiveThreadId))
        {
            return null;
        }

        string threadId = ActiveThreadId;
        JsonElement response;
        try
        {
            response = await context.Connection.SendReadOnlyRequestAsync(
                "thread/read",
                new { threadId, includeTurns = false },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        EnsureCurrent(context);
        if (!response.TryGetProperty("thread", out JsonElement thread)
            || thread.ValueKind != JsonValueKind.Object
            || !string.Equals(GetString(thread, "id"), threadId, StringComparison.Ordinal))
        {
            return null;
        }

        string? cwd = GetString(thread, "cwd");
        return string.IsNullOrWhiteSpace(cwd) ? null : cwd;
    }

    private static void ValidateDailyUseOwner(OwnerScopedRequest request, ConnectionContext context)
    {
        if (request.OwnerGeneration != context.OwnerGeneration
            || request.ConnectionGeneration != context.Generation
            || !string.Equals(request.StatePartitionFingerprint, context.StatePartitionFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The connection owner or generation changed. Refresh the view and try again.");
        }
    }

    private static bool SnapshotMatchesCurrentOwner(
        ShellCommandConfirmationSnapshot expected,
        ShellCommandExecuteRequest request,
        ConnectionContext context)
        => request.OwnerGeneration == expected.OwnerGeneration
            && request.ConnectionGeneration == expected.ConnectionGeneration
            && expected.OwnerGeneration == context.OwnerGeneration
            && expected.ConnectionGeneration == context.Generation
            && string.Equals(request.StatePartitionFingerprint, expected.StatePartitionFingerprint, StringComparison.Ordinal)
            && string.Equals(expected.StatePartitionFingerprint, context.StatePartitionFingerprint, StringComparison.Ordinal);

    private void PruneShellConfirmationsLocked()
    {
        DateTimeOffset expiredBefore = timeProvider.GetUtcNow() - ShellConfirmationLifetime;
        foreach (string id in shellConfirmations.Where(pair => pair.Value.CreatedAt < expiredBefore).Select(pair => pair.Key).ToArray())
        {
            shellConfirmations.Remove(id);
        }

        foreach (string threadId in pendingShellSubmissions.Where(pair => pair.Value.ConnectionGeneration != Volatile.Read(ref connectionGeneration)).Select(pair => pair.Key).ToArray())
        {
            pendingShellSubmissions.Remove(threadId);
        }
    }

    private static bool SnapshotEquals(ShellCommandConfirmationSnapshot left, ShellCommandConfirmationSnapshot right)
        => string.Equals(left.ConfirmationId, right.ConfirmationId, StringComparison.Ordinal)
            && string.Equals(left.ConnectionProfile, right.ConnectionProfile, StringComparison.Ordinal)
            && string.Equals(left.ThreadId, right.ThreadId, StringComparison.Ordinal)
            && string.Equals(left.ThreadTitle, right.ThreadTitle, StringComparison.Ordinal)
            && string.Equals(left.Command, right.Command, StringComparison.Ordinal)
            && string.Equals(left.ServerWorkingDirectory, right.ServerWorkingDirectory, StringComparison.Ordinal)
            && left.TimeoutMs == right.TimeoutMs
            && left.OwnerGeneration == right.OwnerGeneration
            && left.ConnectionGeneration == right.ConnectionGeneration
            && string.Equals(left.StatePartitionFingerprint, right.StatePartitionFingerprint, StringComparison.Ordinal)
            && left.RunsUnsandboxedWithFullAccess == right.RunsUnsandboxedWithFullAccess;

    private static bool IsServerWindows(AppServerInitializationMetadata? metadata)
        => string.Equals(metadata?.PlatformOs, "windows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(metadata?.PlatformFamily, "windows", StringComparison.OrdinalIgnoreCase);

    private bool IsCurrentWindowsSandboxAttemptLocked(ConnectionContext context)
        => ReferenceEquals(Volatile.Read(ref connectionContext), context)
            && windowsSandboxAttemptGeneration == context.Generation
            && windowsSandboxAttemptOwnerGeneration == context.OwnerGeneration
            && string.Equals(windowsSandboxAttemptPartitionFingerprint, context.StatePartitionFingerprint, StringComparison.Ordinal);

    private static bool ServerPathMatchesPlatform(ServerPath path, bool serverIsWindows)
        => serverIsWindows
            ? path.Family is PathFamily.WindowsDrive or PathFamily.WindowsUnc
            : path.Family == PathFamily.Posix;

    private static SavedAttachmentMutationResult AttachmentRejected(string message)
        => new() { Outcome = AttachmentMutationOutcome.Rejected, ErrorMessage = message };

    private static bool IsValidIdentityKey(string identityKey)
    {
        if (identityKey.Length != SavedAttachmentPayloadRegistry.IdentityKeyPrefix.Length + 64
            || !identityKey.StartsWith(SavedAttachmentPayloadRegistry.IdentityKeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char value in identityKey.AsSpan(SavedAttachmentPayloadRegistry.IdentityKeyPrefix.Length))
        {
            if (value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetDailyProperty(JsonElement value, string property, out JsonElement propertyValue)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out propertyValue))
        {
            return true;
        }

        propertyValue = default;
        return false;
    }

    private string SafeError(string message)
    {
        string safe = redactor.Redact(message);
        if (Encoding.UTF8.GetByteCount(safe) <= 4096)
        {
            return safe;
        }

        var truncated = new StringBuilder(4096);
        int byteCount = 0;
        foreach (Rune rune in safe.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > 4096)
            {
                break;
            }

            truncated.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }

        return truncated.ToString();
    }

    private void ReleasePendingShell(string threadId, long generation, Guid submissionId)
    {
        lock (dailyUseGate)
        {
            if (pendingShellSubmissions.TryGetValue(threadId, out PendingShellSubmission? current)
                && current.ConnectionGeneration == generation
                && current.SubmissionId == submissionId)
            {
                pendingShellSubmissions.Remove(threadId);
            }
        }
    }

    private sealed record ShellConfirmationEntry(ShellCommandConfirmationSnapshot Snapshot, DateTimeOffset CreatedAt);

    private sealed record PendingShellSubmission(long ConnectionGeneration, Guid SubmissionId);
}
