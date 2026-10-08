using System.Text;
using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class CodexSessionService
{
    private const int MaxPlanDeltaUtf8Bytes = 64 * 1024;
    private const int MaxPlanSteps = 200;
    private const int MaxNoticeUtf8Bytes = 4 * 1024;
    private const int MaxThreadStatusSnapshots = 500;
    private const int MaxTrackedPlanItems = 2000;
    private const int MaxModelCatalogValues = 32;
    private const int MaxModelIdentifierLength = 128;

    private readonly object dailyUseStateLock = new();
    private readonly Dictionary<ThreadStatusKey, string> threadStatuses = new();
    private readonly Dictionary<PlanItemKey, PlanDeltaState> planDeltaStates = new();
    private readonly HashSet<PlanTurnKey> finalizedPlanTurns = new();
    private readonly DailyUseArtifactStore dailyUseArtifactStore = new();

    public event Func<WindowsSandboxSetupCompletedEvent, CancellationToken, Task>? WindowsSandboxSetupCompleted;

    internal bool TryGetThreadStatus(string threadId, long expectedGeneration, out string status)
    {
        status = string.Empty;
        ConnectionContext? current = Volatile.Read(ref connectionContext);
        if (string.IsNullOrWhiteSpace(threadId)
            || expectedGeneration <= 0
            || current is null
            || current.Generation != expectedGeneration
            || expectedGeneration != Interlocked.Read(ref connectionGeneration))
        {
            return false;
        }

        lock (dailyUseStateLock)
        {
            return threadStatuses.TryGetValue(new ThreadStatusKey(expectedGeneration, threadId), out status!);
        }
    }

    private async Task EmitBufferedEventAsync(
        ConnectionContext context,
        ConversationEvent value,
        CancellationToken cancellationToken)
    {
        if (value.PlanDelta is { } planDelta)
        {
            lock (dailyUseStateLock)
            {
                PlanItemKey key = new(context.Generation, planDelta.ThreadId, planDelta.TurnId, planDelta.ItemId);
                if (planDeltaStates.TryGetValue(key, out PlanDeltaState? state) && state.IsFinalized)
                {
                    return;
                }
            }

            planDelta.Text = value.Text ?? string.Empty;
        }

        await EmitForContextAsync(context, value, cancellationToken).ConfigureAwait(false);
    }

    public Task<ArtifactActionResult> ResolveArtifactActionAsync(
        ArtifactActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.StatePartitionFingerprint, context.StatePartitionFingerprint, StringComparison.Ordinal)
            || request.OwnerGeneration != context.OwnerGeneration
            || request.ConnectionGeneration != context.Generation)
        {
            return Task.FromResult(new ArtifactActionResult { Success = false, FailureReason = "The file action expired with its connection owner." });
        }

        if (request.Action == ArtifactActionKind.Preview
            && dailyUseArtifactStore.TryResolvePreview(
                context.StatePartitionFingerprint,
                context.OwnerGeneration,
                context.Generation,
                request.ActionId,
                out ArtifactPreviewInfo inlinePreview))
        {
            return Task.FromResult(new ArtifactActionResult
            {
                Success = true,
                PreviewBytes = inlinePreview.Bytes,
                PreviewMimeType = inlinePreview.MimeType,
                Width = inlinePreview.Width,
                Height = inlinePreview.Height,
            });
        }

        RemotePathMapper? mapper = remotePathMapper ?? CreateLocalArtifactPathMapper(context);
        if (mapper is null
            || !dailyUseArtifactStore.TryResolve(
                context.StatePartitionFingerprint,
                context.OwnerGeneration,
                context.Generation,
                request.ActionId,
                request.Action,
                out ServerPath serverPath)
            || !DailyUseArtifactFiles.TryMapExistingFile(serverPath, mapper, localPathBoundary, out LocalPath localPath))
        {
            return Task.FromResult(new ArtifactActionResult { Success = false, FailureReason = "The file is unavailable or its path is outside the mapped root." });
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.Action == ArtifactActionKind.Preview)
        {
            LocalPath? localRoot = LocalPath.TryCreate(options.LocalRoot, out LocalPath parsedRoot)
                ? parsedRoot
                : LocalPath.TryCreate(options.WorkingDirectory, out LocalPath workingRoot) ? workingRoot : null;
            if (localRoot is null)
            {
                return Task.FromResult(new ArtifactActionResult { Success = false, FailureReason = "A local root is not configured for image preview." });
            }

            return ReadFilePreviewAsync(context, localRoot, localPath, cancellationToken);
        }

        EnsureCurrent(context);
        return Task.FromResult(new ArtifactActionResult { Success = true, LocalPath = localPath.Value });
    }

    private RemotePathMapper? CreateLocalArtifactPathMapper(ConnectionContext context)
    {
        if (!context.IsLocal)
        {
            return null;
        }

        LocalPath? root = LocalPath.TryCreate(options.LocalRoot, out LocalPath configuredRoot)
            ? configuredRoot
            : LocalPath.TryCreate(options.WorkingDirectory, out LocalPath workingRoot) ? workingRoot : null;
        return root is not null && ServerPath.TryCreate(root.Value, out ServerPath serverRoot)
            ? new RemotePathMapper(root, serverRoot)
            : null;
    }

    private async Task<ArtifactActionResult> ReadFilePreviewAsync(
        ConnectionContext context,
        LocalPath localRoot,
        LocalPath localPath,
        CancellationToken cancellationToken)
    {
        ArtifactPreviewInfo? preview = await DailyUseArtifactFiles.TryReadPreviewAsync(
            localRoot,
            localPath,
            localPathBoundary,
            GetMimeTypeForPath(localPath.Value),
            cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        if (preview is null)
        {
            return new ArtifactActionResult { Success = false, FailureReason = "Only bounded PNG and JPEG images can be previewed." };
        }

        return new ArtifactActionResult
        {
            Success = true,
            PreviewBytes = preview.Value.Bytes,
            PreviewMimeType = preview.Value.MimeType,
            Width = preview.Value.Width,
            Height = preview.Value.Height,
        };
    }

    private async Task ValidateTurnInputModelAsync(
        ConnectionContext context,
        StartTurnRequest request,
        CancellationToken cancellationToken)
    {
        bool needsText = !string.IsNullOrEmpty(request.Text)
            || request.Skill is not null
            || request.IdeContext is not null
            || request.Attachments.Any(attachment => IsMentionAttachment(attachment.Kind));
        bool needsImage = request.Attachments.Any(attachment => string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase));

        if (request.Attachments.Any(attachment =>
            !string.IsNullOrWhiteSpace(attachment.Kind)
            && !string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase)
            && !IsMentionAttachment(attachment.Kind)))
        {
            throw new AttachmentRejectedException("This attachment type is display-only and cannot be sent to the selected model.");
        }

        JsonElement result = await context.Connection.SendReadOnlyRequestAsync(
            "model/list",
            new { includeHidden = true },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        ListModelsResult catalog = ReadModelsResult(result);

        string? effectiveModel = NormalizeModelId(request.Model)
            ?? NormalizeModelId(request.CollaborationMode?.Model)
            ?? (string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal)
                ? NormalizeModelId(effectiveThreadModelId)
                : null);
        if (effectiveModel is null
            && string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal)
            && activeThreadInheritsDefaultModel)
        {
            effectiveModel = catalog.DefaultModel;
        }

        if (effectiveModel is null)
        {
            ThreadSummary joinedThread = await ReadThreadAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            effectiveModel = NormalizeModelId(joinedThread.EffectiveModelId);
            if (string.Equals(request.ThreadId, ActiveThreadId, StringComparison.Ordinal))
            {
                effectiveThreadModelId = effectiveModel;
                activeThreadInheritsDefaultModel = false;
            }
        }
        if (effectiveModel is null)
        {
            throw new AttachmentRejectedException("The active thread's effective model is unknown. Refresh the thread or explicitly select an advertised model before sending.");
        }

        ModelInfo? model = string.Equals(catalog.DefaultModelInfo?.Id, effectiveModel, StringComparison.Ordinal)
            ? catalog.DefaultModelInfo
            : catalog.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, effectiveModel, StringComparison.Ordinal));
        if (model is null)
        {
            throw new AttachmentRejectedException("The selected model is no longer available in the live model catalog. Select an advertised model and try again.");
        }

        bool supportsText = model.InputModalities.Any(modality => string.Equals(modality, "text", StringComparison.Ordinal));
        bool supportsImage = model.InputModalities.Any(modality => string.Equals(modality, "image", StringComparison.Ordinal));
        if ((needsText && !supportsText) || (needsImage && !supportsImage))
        {
            string reason = needsImage && !supportsImage
                ? "The selected model does not accept image input. Keep the image attached and choose a model that advertises image support."
                : "The selected model does not accept text input. Keep the current input in the composer and choose a model that advertises text support.";
            throw new AttachmentRejectedException(reason);
        }
    }

    private static IReadOnlyList<string> ReadModelStringValues(JsonElement model, string propertyName, IReadOnlyList<string> omittedDefault)
    {
        if (!model.TryGetProperty(propertyName, out JsonElement value))
        {
            return omittedDefault;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (JsonElement item in value.EnumerateArray().Take(MaxModelCatalogValues))
        {
            string? identifier = GetBoundedString(item, MaxModelIdentifierLength);
            if (identifier is not null && !values.Contains(identifier, StringComparer.Ordinal))
            {
                values.Add(NormalizeWireIdentifier(identifier) ?? identifier);
            }
        }

        return values;
    }

    private static ModelAccessPrograms? ReadModelAccessPrograms(JsonElement model)
    {
        if (!model.TryGetProperty("availableAccessPrograms", out JsonElement programs)
            || programs.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (programs.ValueKind != JsonValueKind.Object
            || !programs.TryGetProperty("cyber", out JsonElement cyberPrograms)
            || cyberPrograms.ValueKind != JsonValueKind.Array)
        {
            return new ModelAccessPrograms();
        }

        var values = new List<string>();
        foreach (JsonElement item in cyberPrograms.EnumerateArray().Take(MaxModelCatalogValues))
        {
            string? identifier = GetBoundedString(item, MaxModelIdentifierLength);
            if (identifier is not null && !values.Contains(identifier, StringComparer.Ordinal))
            {
                values.Add(identifier);
            }
        }

        return new ModelAccessPrograms { Cyber = values };
    }

    private static string? GetBoundedString(JsonElement value, int maximumUtf8Bytes)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? result = value.GetString();
        return !string.IsNullOrWhiteSpace(result)
            && Encoding.UTF8.GetByteCount(result) <= maximumUtf8Bytes
                ? result
                : null;
    }

    private static string? GetMimeTypeForPath(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => null,
        };

    private async Task<bool> TryHandleDailyUseNotificationAsync(
        ConnectionContext context,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "thread/status/changed":
            {
                string? threadId = GetBoundedString(parameters, "threadId", MaxAttachmentIdentityBytes);
                string? status = parameters.TryGetProperty("status", out JsonElement statusNode)
                    ? GetBoundedString(statusNode, "type", 64)
                    : null;
                if (threadId is null)
                {
                    return true;
                }

                if (status is not ("idle" or "active" or "notLoaded" or "systemError"))
                {
                    RecordThreadStatus(context.Generation, threadId, string.Empty);
                    return true;
                }

                RecordThreadStatus(context.Generation, threadId, status);
                AppServerNotice notice = new()
                {
                    Kind = AppServerNoticeKind.ThreadStatusChanged,
                    Identity = threadId,
                    Text = DescribeThreadStatus(status, statusNode),
                    ThreadId = threadId,
                };
                await EmitNoticeAsync(context, notice, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "turn/plan/updated":
            {
                TurnPlanSnapshot? plan = ReadTurnPlanSnapshot(parameters);
                if (plan is null || IsPlanTurnFinalized(context.Generation, plan.ThreadId, plan.TurnId)
                    || IsTurnCompleted(context.Generation, plan.ThreadId, plan.TurnId))
                {
                    return true;
                }

                await EmitForContextAsync(context, new ConversationEvent
                {
                    Kind = ConversationEventKind.PlanUpdated,
                    ThreadId = plan.ThreadId,
                    TurnId = plan.TurnId,
                    Plan = plan,
                }, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "item/plan/delta":
            {
                await EmitPlanDeltaAsync(context, parameters, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "configWarning":
            {
                string? summary = GetBoundedString(parameters, "summary", 2048);
                if (summary is null)
                {
                    return true;
                }

                string details = GetBoundedString(parameters, "details", 2048) ?? string.Empty;
                await EmitNoticeAsync(context, new AppServerNotice
                {
                    Kind = AppServerNoticeKind.ConfigWarning,
                    Identity = "configuration",
                    Text = JoinNoticeText(summary, details),
                }, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "model/rerouted":
            {
                string? threadId = GetBoundedString(parameters, "threadId", MaxAttachmentIdentityBytes);
                string? turnId = GetBoundedString(parameters, "turnId", MaxAttachmentIdentityBytes);
                string? from = GetBoundedString(parameters, "fromModel", MaxModelIdentifierLength);
                string? to = GetBoundedString(parameters, "toModel", MaxModelIdentifierLength);
                string? reason = GetBoundedString(parameters, "reason", 256);
                await EmitNoticeAsync(context, new AppServerNotice
                {
                    Kind = AppServerNoticeKind.ModelRerouted,
                    Identity = $"{threadId}:{turnId}",
                    ThreadId = threadId,
                    TurnId = turnId,
                    Text = JoinNoticeText($"Model routed from {from ?? "(unknown)"} to {to ?? "(unknown)"}.", reason ?? string.Empty),
                }, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "model/verification":
            {
                string? threadId = GetBoundedString(parameters, "threadId", MaxAttachmentIdentityBytes);
                string? turnId = GetBoundedString(parameters, "turnId", MaxAttachmentIdentityBytes);
                string verifications = ReadVerificationNames(parameters);
                await EmitNoticeAsync(context, new AppServerNotice
                {
                    Kind = AppServerNoticeKind.ModelVerification,
                    Identity = $"{threadId}:{turnId}",
                    ThreadId = threadId,
                    TurnId = turnId,
                    IsInformationalOnly = true,
                    Text = $"Model verification metadata: {verifications}",
                }, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "item/mcpToolCall/progress":
            {
                string? threadId = GetBoundedString(parameters, "threadId", MaxAttachmentIdentityBytes);
                string? turnId = GetBoundedString(parameters, "turnId", MaxAttachmentIdentityBytes);
                string? itemId = GetBoundedString(parameters, "itemId", MaxAttachmentIdentityBytes);
                string? text = GetBoundedString(parameters, "message", 4096);
                if (text is not null)
                {
                    await EmitNoticeAsync(context, new AppServerNotice
                    {
                        Kind = AppServerNoticeKind.McpToolCallProgress,
                        Identity = $"{threadId}:{turnId}:{itemId}",
                        ThreadId = threadId,
                        TurnId = turnId,
                        ItemId = itemId,
                        Text = text,
                    }, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }

            case "windowsSandbox/setupCompleted":
            {
                string? mode = GetBoundedString(parameters, "mode", 32);
                bool? success = GetBoolean(parameters, "success");
                string? error = GetBoundedString(parameters, "error", MaxNoticeUtf8Bytes);
                if (mode is null || success is null
                    || !TryCompleteWindowsSandboxSetup(context, mode, success.Value, error, out WindowsSandboxSetupCompletedEvent completion))
                {
                    return true;
                }

                EnsureCurrent(context);
                if (WindowsSandboxSetupCompleted is not null)
                {
                    await WindowsSandboxSetupCompleted(completion, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }

            default:
                return false;
        }
    }

    private Task EmitPlanDeltaAsync(
        ConnectionContext context,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        string? threadId = GetBoundedString(parameters, "threadId", MaxAttachmentIdentityBytes);
        string? turnId = GetBoundedString(parameters, "turnId", MaxAttachmentIdentityBytes);
        string? itemId = GetBoundedString(parameters, "itemId", MaxAttachmentIdentityBytes);
        string? rawDelta = GetBoundedString(parameters, "delta", MaxPlanDeltaUtf8Bytes);
        if (threadId is null || turnId is null || itemId is null || rawDelta is null)
        {
            return Task.CompletedTask;
        }

        PlanItemKey key = new(context.Generation, threadId, turnId, itemId);
        string visibleDelta;
        lock (dailyUseStateLock)
        {
            if (planDeltaStates.TryGetValue(key, out PlanDeltaState? existing) && existing.IsFinalized)
            {
                return Task.CompletedTask;
            }

            if (existing is null && planDeltaStates.Count >= MaxTrackedPlanItems)
            {
                return Task.CompletedTask;
            }

            PlanDeltaState state = existing ?? new PlanDeltaState();
            int remaining = MaxPlanDeltaUtf8Bytes - state.Utf8Bytes;
            visibleDelta = ClipUtf8(rawDelta, Math.Max(0, remaining));
            state.Utf8Bytes += Encoding.UTF8.GetByteCount(visibleDelta);
            planDeltaStates[key] = state;
        }

        if (visibleDelta.Length == 0)
        {
            return Task.CompletedTask;
        }

        string safeDelta = redactor.Redact(visibleDelta);
        var delta = new TurnPlanDeltaEvent
        {
            ThreadId = threadId,
            TurnId = turnId,
            ItemId = itemId,
            Text = safeDelta,
        };
        var output = new ConversationEvent
        {
            Kind = ConversationEventKind.PlanDelta,
            ThreadId = threadId,
            TurnId = turnId,
            ItemId = itemId,
            Text = safeDelta,
            PlanDelta = delta,
            Truncated = Encoding.UTF8.GetByteCount(rawDelta) > Encoding.UTF8.GetByteCount(visibleDelta),
        };
        streamingBuffer?.Append(PlanDeltaBufferKey(threadId, turnId, itemId), output, safeDelta, MaxPlanDeltaUtf8Bytes);
        return Task.CompletedTask;
    }

    private async Task EmitCompletedPlanItemAsync(
        ConnectionContext context,
        JsonElement item,
        string? threadId,
        string? turnId,
        CancellationToken cancellationToken)
    {
        string? itemId = GetBoundedString(item, "id", MaxAttachmentIdentityBytes);
        if (itemId is null || threadId is null || turnId is null)
        {
            return;
        }

        MarkPlanFinalized(context.Generation, threadId, turnId, itemId);

        string? rawFinalText = GetString(item, "text");
        string? finalText = rawFinalText is null ? null : ClipUtf8(rawFinalText, MaxPlanDeltaUtf8Bytes);
        await EmitForContextAsync(context, new ConversationEvent
        {
            Kind = ConversationEventKind.ItemCompleted,
            ThreadId = threadId,
            TurnId = turnId,
            ItemId = itemId,
            Text = finalText is null ? null : redactor.Redact(finalText),
            Plan = new TurnPlanSnapshot
            {
                ThreadId = threadId,
                TurnId = turnId,
                Steps = Array.Empty<PlanStepInfo>(),
                IsComplete = true,
            },
            Truncated = rawFinalText is not null && Encoding.UTF8.GetByteCount(rawFinalText) > MaxPlanDeltaUtf8Bytes,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitNoticeAsync(ConnectionContext context, AppServerNotice notice, CancellationToken cancellationToken)
    {
        notice.Identity = BoundAndRedact(notice.Identity, 512);
        notice.Text = BoundAndRedact(notice.Text, MaxNoticeUtf8Bytes);
        EnsureCurrent(context);
        await EmitForContextAsync(context, new ConversationEvent
        {
            Kind = ConversationEventKind.AppServerNotice,
            ThreadId = notice.ThreadId,
            TurnId = notice.TurnId,
            ItemId = notice.ItemId,
            Notice = notice,
            Text = notice.Text,
        }, cancellationToken).ConfigureAwait(false);
    }

    private TurnPlanSnapshot? ReadTurnPlanSnapshot(JsonElement parameters)
    {
        string? threadId = GetBoundedString(parameters, "threadId", MaxAttachmentIdentityBytes);
        string? turnId = GetBoundedString(parameters, "turnId", MaxAttachmentIdentityBytes);
        if (threadId is null || turnId is null
            || !parameters.TryGetProperty("plan", out JsonElement planArray)
            || planArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var steps = new List<PlanStepInfo>(Math.Min(planArray.GetArrayLength(), MaxPlanSteps));
        foreach (JsonElement step in planArray.EnumerateArray().Take(MaxPlanSteps))
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? text = GetBoundedString(step, "step", 2048);
            string? status = GetBoundedString(step, "status", 64);
            if (text is null || status is not ("pending" or "inProgress" or "completed"))
            {
                continue;
            }

            steps.Add(new PlanStepInfo
            {
                StepId = steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Text = redactor.Redact(text),
                Status = status,
            });
        }

        return new TurnPlanSnapshot
        {
            ThreadId = threadId,
            TurnId = turnId,
            Steps = steps,
            Explanation = GetBoundedString(parameters, "explanation", 4096) is { } explanation
                ? redactor.Redact(explanation)
                : null,
            IsComplete = false,
        };
    }

    private void RecordThreadStatus(long generation, string threadId, string status)
    {
        lock (dailyUseStateLock)
        {
            string snapshot = status switch
            {
                "idle" => "idle",
                "active" => "busy",
                _ => string.Empty,
            };
            ThreadStatusKey key = new(generation, threadId);
            if (snapshot.Length == 0)
            {
                threadStatuses.Remove(key);
                return;
            }

            if (generation != Interlocked.Read(ref connectionGeneration))
            {
                return;
            }

            if (threadStatuses.Count >= MaxThreadStatusSnapshots && !threadStatuses.ContainsKey(key))
            {
                return;
            }

            threadStatuses[key] = snapshot;
        }
    }

    private static string DescribeThreadStatus(string status, JsonElement statusNode)
    {
        if (status != "active" || !statusNode.TryGetProperty("activeFlags", out JsonElement flags)
            || flags.ValueKind != JsonValueKind.Array)
        {
            return $"Thread status: {status}.";
        }

        string[] activeFlags = flags.EnumerateArray()
            .Take(16)
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(value => GetBoundedString(value, 64) ?? string.Empty)
            .Where(static value => value.Length > 0)
            .ToArray();
        return activeFlags.Length == 0
            ? "Thread status: active."
            : $"Thread status: active ({string.Join(", ", activeFlags)}).";
    }

    private static string ReadVerificationNames(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("verifications", out JsonElement verifications)
            || verifications.ValueKind != JsonValueKind.Array)
        {
            return "unspecified";
        }

        string[] names = verifications.EnumerateArray()
            .Take(16)
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(value => GetBoundedString(value, 64) ?? string.Empty)
            .Where(static value => value.Length > 0)
            .ToArray();
        return names.Length == 0 ? "none" : string.Join(", ", names);
    }

    private static string JoinNoticeText(string first, string second)
        => string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";

    private string BoundAndRedact(string value, int maximumUtf8Bytes)
        => ClipUtf8(redactor.Redact(value), maximumUtf8Bytes);

    private static string ClipUtf8(string value, int maximumUtf8Bytes)
    {
        if (maximumUtf8Bytes <= 0 || value.Length == 0)
        {
            return string.Empty;
        }

        if (Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes)
        {
            return value;
        }

        int end = 0;
        int used = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int bytes = rune.Utf8SequenceLength;
            if (used + bytes > maximumUtf8Bytes)
            {
                break;
            }

            used += bytes;
            end += rune.Utf16SequenceLength;
        }

        return value[..end];
    }

    private async Task ValidateAndEmitTypedItemAsync(
        ConnectionContext context,
        ConversationEvent output,
        JsonElement item,
        CancellationToken cancellationToken)
    {
        string? itemType = GetString(item, "type");
        if (itemType == "plan" && output.Kind == ConversationEventKind.ItemCompleted)
        {
            await EmitCompletedPlanItemAsync(context, item, output.ThreadId, output.TurnId, cancellationToken).ConfigureAwait(false);
            return;
        }

        List<ArtifactPart> parts = ProjectArtifactParts(context, item, itemType);
        if (parts.Count > 0)
        {
            output.Parts = parts;
            output.PayloadJson = null;
            await EmitForContextAsync(context, output, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (itemType == "agentMessage")
        {
            string? rawText = GetString(item, "text");
            string? text = rawText is null ? null : ClipUtf8(rawText, MaxHistoryTextBytes);
            if (text is not null)
            {
                output.Text = redactor.Redact(text);
                output.Parts = [new ArtifactPart
                {
                    Kind = ArtifactPartKind.Text,
                    Text = output.Text,
                    IsServerTruncated = output.Truncated,
                    IsLocallyTruncated = Encoding.UTF8.GetByteCount(rawText!) > MaxHistoryTextBytes,
                }];
                output.PayloadJson = null;
                await EmitForContextAsync(context, output, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        output.Parts = [new ArtifactPart
        {
            Kind = ArtifactPartKind.Fallback,
            Text = $"The {BoundAndRedact(itemType ?? "unknown", 128)} result is available in the app-server, but its content is not supported for display.",
        }];
        output.PayloadJson = null;
        await EmitForContextAsync(context, output, cancellationToken).ConfigureAwait(false);
    }

    private List<ArtifactPart> ProjectArtifactParts(ConnectionContext context, JsonElement item, string? itemType)
    {
        var parts = new List<ArtifactPart>();
        if (itemType == "imageView")
        {
            AddServerPathPart(context, parts, GetString(item, "path"), ArtifactPartKind.Image,
                [ArtifactActionKind.Preview, ArtifactActionKind.Open, ArtifactActionKind.Reveal]);
        }
        else if (itemType == "imageGeneration")
        {
            string? savedPath = GetString(item, "savedPath");
            if (savedPath is not null)
            {
                AddServerPathPart(context, parts, savedPath, ArtifactPartKind.Image,
                    [ArtifactActionKind.Preview, ArtifactActionKind.Open, ArtifactActionKind.Reveal]);
            }
            else
            {
                AddImageGenerationFallback(context, parts, GetString(item, "result"));
            }
        }
        else if (itemType == "fileChange")
        {
            JsonElement changes = item.TryGetProperty("changes", out JsonElement changesValue) ? changesValue : default;
            if (changes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement change in changes.EnumerateArray().Take(100))
                {
                    string? path = GetString(change, "path");
                    string? changeKind = change.TryGetProperty("kind", out JsonElement kind)
                        ? GetString(kind, "type")
                        : null;
                    var filePart = new ArtifactPart
                    {
                        Kind = ArtifactPartKind.File,
                        DisplayName = path is null ? "Changed file" : redactor.Redact(GetServerPathDisplayName(path)),
                        Text = changeKind is null ? "File change" : $"File {changeKind}.",
                    };
                    if (changeKind is not "delete" && path is not null)
                    {
                        var actions = new[] { ArtifactActionKind.Open, ArtifactActionKind.Reveal };
                        if (TryRegisterServerPath(context, path, actions, out string actionId))
                        {
                            filePart.ActionId = actionId;
                            filePart.AllowedActions = actions;
                        }
                    }

                    parts.Add(filePart);
                }
            }
        }
        else if (itemType == "mcpToolCall")
        {
            string serverName = BoundAndRedact(GetBoundedString(item, "server", 256) ?? "unknown server", 256);
            string toolName = BoundAndRedact(GetBoundedString(item, "tool", 256) ?? "unknown tool", 256);
            string status = BoundAndRedact(GetBoundedString(item, "status", 64) ?? "unknown", 64);
            parts.Add(new ArtifactPart
            {
                Kind = ArtifactPartKind.Fallback,
                Text = $"MCP {serverName} / {toolName} ({status}).",
            });

            string? errorMessage = item.TryGetProperty("error", out JsonElement error)
                ? GetBoundedString(error, "message", MaxNoticeUtf8Bytes)
                : null;
            if (errorMessage is not null)
            {
                parts.Add(new ArtifactPart
                {
                    Kind = ArtifactPartKind.Fallback,
                    Text = BoundAndRedact($"MCP error: {errorMessage}", MaxNoticeUtf8Bytes),
                });
            }

            JsonElement content = item.TryGetProperty("result", out JsonElement result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("content", out JsonElement contentValue)
                    ? contentValue
                    : default;
            if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement contentItem in content.EnumerateArray().Take(100))
                {
                    string? type = GetString(contentItem, "type");
                    if (type == "text")
                    {
                        AddTextPart(parts, GetString(contentItem, "text"), 64 * 1024);
                    }
                    else if (type == "image")
                    {
                        AddInlineImagePart(context, parts, contentItem);
                    }
                    else if (type == "resource_link")
                    {
                        string? name = GetBoundedString(contentItem, "name", 256);
                        parts.Add(new ArtifactPart
                        {
                            Kind = ArtifactPartKind.File,
                            DisplayName = name is null ? "MCP resource" : redactor.Redact(name),
                            Text = "MCP resource link is shown as text and cannot be opened automatically.",
                        });
                    }
                    else
                    {
                        parts.Add(new ArtifactPart
                        {
                            Kind = ArtifactPartKind.Fallback,
                            Text = $"Unsupported MCP content type: {BoundAndRedact(type ?? "unknown", 128)}.",
                        });
                    }
                }
            }

            if (item.TryGetProperty("mcpAppUi", out JsonElement appUi) && appUi.ValueKind == JsonValueKind.Object)
            {
                string? displayMode = GetBoundedString(appUi, "preferredModelDisplayMode", 64);
                parts.Add(new ArtifactPart
                {
                    Kind = ArtifactPartKind.Fallback,
                    Text = displayMode is null
                        ? "This MCP app is available in the server client; its output is shown here as text."
                        : BoundAndRedact($"This MCP app prefers {displayMode} presentation; its output is shown here as text.", 512),
                });
            }

            if (parts.Count == 0)
            {
                parts.Add(new ArtifactPart
                {
                    Kind = ArtifactPartKind.Fallback,
                    Text = "The MCP result has no displayable content.",
                });
            }
        }

        return parts;
    }

    private void AddTextPart(List<ArtifactPart> parts, string? rawText, int maximumUtf8Bytes)
    {
        if (rawText is null)
        {
            return;
        }

        string bounded = ClipUtf8(rawText, maximumUtf8Bytes);
        string safe = redactor.Redact(bounded);
        parts.Add(new ArtifactPart
        {
            Kind = ArtifactPartKind.Text,
            Text = safe,
            IsLocallyTruncated = Encoding.UTF8.GetByteCount(rawText) > Encoding.UTF8.GetByteCount(bounded),
        });
    }

    private void AddInlineImagePart(ConnectionContext context, List<ArtifactPart> parts, JsonElement contentItem)
    {
        string? mimeType = GetBoundedString(contentItem, "mimeType", 64);
        string? encoded = GetString(contentItem, "data");
        if (encoded is null || encoded.Length > ((ArtifactPreviewValidator.MaximumBytes + 2) / 3 * 4) + 8)
        {
            parts.Add(new ArtifactPart { Kind = ArtifactPartKind.Fallback, Text = "Image preview exceeds the supported size." });
            return;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            if (!ArtifactPreviewValidator.TryValidate(bytes, mimeType, out ArtifactPreviewInfo preview)
                || !dailyUseArtifactStore.TryRegisterPreview(
                    context.StatePartitionFingerprint,
                    context.OwnerGeneration,
                    context.Generation,
                    preview.Bytes,
                    preview.MimeType,
                    out string actionId))
            {
                parts.Add(new ArtifactPart { Kind = ArtifactPartKind.Fallback, Text = "This image cannot be previewed safely." });
                return;
            }

            parts.Add(new ArtifactPart
            {
                Kind = ArtifactPartKind.Image,
                MimeType = preview.MimeType,
                ActionId = actionId,
                AllowedActions = [ArtifactActionKind.Preview],
            });
        }
        catch (FormatException)
        {
            parts.Add(new ArtifactPart { Kind = ArtifactPartKind.Fallback, Text = "The image data is malformed." });
        }
    }

    private void AddImageGenerationFallback(ConnectionContext context, List<ArtifactPart> parts, string? result)
    {
        const string pngPrefix = "data:image/png;base64,";
        const string jpegPrefix = "data:image/jpeg;base64,";
        string? mimeType = result?.StartsWith(pngPrefix, StringComparison.OrdinalIgnoreCase) == true
            ? "image/png"
            : result?.StartsWith(jpegPrefix, StringComparison.OrdinalIgnoreCase) == true ? "image/jpeg" : null;
        string? encoded = mimeType == "image/png" ? result![pngPrefix.Length..]
            : mimeType == "image/jpeg" ? result![jpegPrefix.Length..]
            : null;
        if (encoded is null)
        {
            parts.Add(new ArtifactPart
            {
                Kind = ArtifactPartKind.Fallback,
                Text = "Image generation completed, but no safe local preview was provided.",
            });
            return;
        }

        AddInlineImagePart(context, parts, JsonSerializer.SerializeToElement(new { mimeType, data = encoded }));
    }

    private void AddServerPathPart(
        ConnectionContext context,
        List<ArtifactPart> parts,
        string? rawPath,
        ArtifactPartKind kind,
        IReadOnlyCollection<ArtifactActionKind> actions)
    {
        if (rawPath is null || !ServerPath.TryCreate(rawPath, out ServerPath serverPath))
        {
            parts.Add(new ArtifactPart { Kind = ArtifactPartKind.Fallback, Text = "A server file reference could not be validated." });
            return;
        }

        string displayName = GetServerPathDisplayName(rawPath);
        if (!TryRegisterServerPath(context, serverPath, actions, out string actionId))
        {
            parts.Add(new ArtifactPart { Kind = kind, DisplayName = redactor.Redact(displayName), Text = "File actions are unavailable for this result." });
            return;
        }

        parts.Add(new ArtifactPart
        {
            Kind = kind,
            DisplayName = redactor.Redact(displayName),
            ActionId = actionId,
            AllowedActions = actions.ToArray(),
        });
    }

    private bool TryRegisterServerPath(
        ConnectionContext context,
        string rawPath,
        IReadOnlyCollection<ArtifactActionKind> actions,
        out string actionId)
    {
        actionId = string.Empty;
        return ServerPath.TryCreate(rawPath, out ServerPath serverPath)
            && TryRegisterServerPath(context, serverPath, actions, out actionId);
    }

    private bool TryRegisterServerPath(
        ConnectionContext context,
        ServerPath serverPath,
        IReadOnlyCollection<ArtifactActionKind> actions,
        out string actionId)
        => dailyUseArtifactStore.TryRegister(
            context.StatePartitionFingerprint,
            context.OwnerGeneration,
            context.Generation,
            serverPath,
            out actionId,
            actions.ToArray());

    private static bool IsMentionAttachment(string? kind)
        => string.Equals(kind, "mention", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "file", StringComparison.OrdinalIgnoreCase);

    internal static string? ReadTurnErrorReason(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("error", out JsonElement error)
            || error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("codexErrorInfo", out JsonElement info))
        {
            return null;
        }

        string? code = info.ValueKind switch
        {
            JsonValueKind.String => info.GetString(),
            JsonValueKind.Object => info.EnumerateObject().Select(static property => property.Name).FirstOrDefault(),
            _ => null,
        };
        return code is "flexUnavailable" or "tooManyDenials" ? code : null;
    }

    private static string PlanDeltaBufferKey(string threadId, string turnId, string itemId)
        => $"plan-delta:{threadId}:{turnId}:{itemId}";

    private void MarkPlanFinalized(long generation, string threadId, string turnId, string itemId)
    {
        lock (dailyUseStateLock)
        {
            PlanItemKey key = new(generation, threadId, turnId, itemId);
            if (finalizedPlanTurns.Count < MaxTrackedPlanItems)
            {
                finalizedPlanTurns.Add(new PlanTurnKey(generation, threadId, turnId));
            }

            if (planDeltaStates.TryGetValue(key, out PlanDeltaState? state))
            {
                state.IsFinalized = true;
            }
            else if (planDeltaStates.Count < MaxTrackedPlanItems)
            {
                planDeltaStates[key] = new PlanDeltaState { IsFinalized = true };
            }
        }

        streamingBuffer?.Discard(PlanDeltaBufferKey(threadId, turnId, itemId));
    }

    private bool IsPlanTurnFinalized(long generation, string threadId, string turnId)
    {
        lock (dailyUseStateLock)
        {
            return finalizedPlanTurns.Contains(new PlanTurnKey(generation, threadId, turnId));
        }
    }

    private bool IsTurnCompleted(long generation, string threadId, string turnId)
    {
        lock (turnStateLock)
        {
            return completedTurnIds.Contains(new TurnKey(generation, threadId, turnId));
        }
    }

    private List<ArtifactPart> ProjectHistoryParts(
        ConnectionContext context,
        JsonElement item,
        string itemType,
        string? text)
    {
        List<ArtifactPart> parts = ProjectArtifactParts(context, item, itemType);
        if (parts.Count > 0)
        {
            return parts;
        }

        if (text is not null)
        {
            string safeText = BoundAndRedact(text, MaxHistoryTextBytes);
            return [new ArtifactPart
            {
                Kind = ArtifactPartKind.Text,
                Text = safeText,
                IsLocallyTruncated = GetHistoryTextByteCount(item, itemType) > MaxHistoryTextBytes,
            }];
        }

        return [new ArtifactPart
        {
            Kind = ArtifactPartKind.Fallback,
            Text = $"The {BoundAndRedact(itemType, 128)} history item is not supported for display.",
        }];
    }

    private static int GetHistoryTextByteCount(JsonElement item, string itemType)
    {
        if (itemType == "agentMessage")
        {
            string? text = GetString(item, "text");
            return text is null ? 0 : Encoding.UTF8.GetByteCount(text);
        }

        if (itemType == "userMessage"
            && item.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.Array)
        {
            int bytes = 0;
            foreach (JsonElement part in content.EnumerateArray().Take(512))
            {
                if (GetString(part, "type") == "inputText" && GetString(part, "text") is { } text)
                {
                    bytes = Math.Min(MaxHistoryTextBytes + 1, bytes + Encoding.UTF8.GetByteCount(text));
                    if (bytes > MaxHistoryTextBytes)
                    {
                        break;
                    }
                }
            }

            return bytes;
        }

        return 0;
    }

    private static string GetServerPathDisplayName(string path)
    {
        int index = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return index >= 0 && index + 1 < path.Length ? path[(index + 1)..] : path;
    }

    private readonly record struct ThreadStatusKey(long Generation, string ThreadId);

    private readonly record struct PlanItemKey(long Generation, string ThreadId, string TurnId, string ItemId);

    private readonly record struct PlanTurnKey(long Generation, string ThreadId, string TurnId);

    private sealed class PlanDeltaState
    {
        public int Utf8Bytes { get; set; }

        public bool IsFinalized { get; set; }
    }
}
