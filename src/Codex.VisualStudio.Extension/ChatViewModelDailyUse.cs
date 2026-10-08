using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility.Documents;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    private const int MaximumPlanTextBytes = 64 * 1024;
    private const int MaximumVisiblePlanSteps = 200;
    private const int MaximumVisibleNotices = 50;
    private const int MaximumArtifactParts = 50;

    private readonly ObservableCollection<AppServerNoticePresentationViewModel> appServerNotices = [];
    private readonly HashSet<string> completedPlanKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> noticeIndices = new(StringComparer.Ordinal);
    private readonly ArtifactPreviewCache artifactPreviewCache = new();
    private readonly Dictionary<string, StringBuilder> provisionalPlanText = new(StringComparer.Ordinal);
    private readonly ArtifactFileActions artifactFileActions;
    private string? noticeThreadId;
    private ShellCommandConfirmationSnapshot? shellCommandSnapshot;
    private OwnerSnapshot? shellCommandOwner;
    private ShellCommandConfirmationPresentation? shellCommandConfirmation;
    private string shellCommandStatusText = string.Empty;
    private string windowsSandboxStatusText = "Readiness has not been checked.";
    private WindowsSandboxReadinessResult? windowsSandboxReadiness;
    private WindowsSandboxSetupState windowsSandboxSetupState = WindowsSandboxSetupState.NotObserved;
    private WindowsSandboxSetupMode selectedWindowsSandboxMode = WindowsSandboxSetupMode.Unelevated;
    private long? windowsSandboxReadinessGeneration;
    private long? windowsSandboxSetupGeneration;
    private string? windowsSandboxSetupSolutionRoot;
    private bool windowsSandboxSetupInProgress;

    [DataMember]
    public ObservableCollection<AppServerNoticePresentationViewModel> AppServerNotices => appServerNotices;

    [DataMember]
    public bool HasAppServerNotices => AppServerNotices.Count > 0;

    [DataMember]
    public string ComposerAdmissionReason => GetComposerAdmissionReason();

    [DataMember]
    public bool HasComposerAdmissionReason => !string.IsNullOrWhiteSpace(ComposerAdmissionReason);

    [DataMember]
    public ShellCommandConfirmationPresentation? ShellCommandConfirmation
    {
        get => shellCommandConfirmation;
        private set
        {
            if (SetProperty(ref shellCommandConfirmation, value))
            {
                OnPropertyChanged(nameof(HasShellCommandConfirmation));
                ConfirmShellCommandCommand?.RaiseCanExecuteChanged();
                CancelShellCommandCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public bool HasShellCommandConfirmation => ShellCommandConfirmation is not null;

    [DataMember]
    public bool HasShellCommandStatus => !string.IsNullOrWhiteSpace(ShellCommandStatusText);

    [DataMember]
    public string ShellCommandStatusText
    {
        get => shellCommandStatusText;
        private set
        {
            if (SetProperty(ref shellCommandStatusText, markdown.ToSafeText(value).Trim()))
            {
                OnPropertyChanged(nameof(HasShellCommandStatus));
            }
        }
    }

    [DataMember]
    public AsyncCommand ConfirmShellCommandCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand CancelShellCommandCommand { get; private set; } = null!;

    [DataMember]
    public string WindowsSandboxStatusText
    {
        get => windowsSandboxStatusText;
        private set => SetProperty(ref windowsSandboxStatusText, markdown.ToSafeText(value).Trim());
    }

    [DataMember]
    public string WindowsSandboxSolutionRootText => windowsSandboxSetupSolutionRoot is { Length: > 0 } root
        ? $"Solution working directory: {SafeMarkdownService.ToSafeLiteralText(root)}"
        : "No active solution root; setup will omit its working directory.";

    [DataMember]
    public WindowsSandboxSetupMode SelectedWindowsSandboxMode
    {
        get => selectedWindowsSandboxMode;
        set => SetProperty(ref selectedWindowsSandboxMode, value);
    }

    [DataMember]
    public bool IsWindowsSandboxSetupInProgress
    {
        get => windowsSandboxSetupInProgress;
        private set
        {
            if (SetProperty(ref windowsSandboxSetupInProgress, value))
            {
                StartWindowsSandboxSetupCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public AsyncCommand CheckWindowsSandboxReadinessCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand StartWindowsSandboxSetupCommand { get; private set; } = null!;

    [DataMember]
    public ObservableCollection<WindowsSandboxSetupMode> WindowsSandboxSetupModes { get; } =
    [
        WindowsSandboxSetupMode.Unelevated,
        WindowsSandboxSetupMode.Elevated,
    ];

    [DataMember]
    public bool ShowWindowsSandboxSetup => connectedProfileName is null
        && Status.State == WorkerConnectionState.Ready;

    [DataMember]
    public bool IsLocallyInterruptible => CanInterruptSelectedThread();

    private void InitializeDailyUseCommands()
    {
        ConfirmShellCommandCommand = new AsyncCommand(ConfirmShellCommandAsync, () => HasShellCommandConfirmation && Status.TurnId is null);
        CancelShellCommandCommand = new AsyncCommand(CancelShellCommandAsync, () => HasShellCommandConfirmation);
        CheckWindowsSandboxReadinessCommand = new AsyncCommand(CheckWindowsSandboxReadinessAsync, () => Status.State == WorkerConnectionState.Ready);
        StartWindowsSandboxSetupCommand = new AsyncCommand(StartWindowsSandboxSetupAsync, CanStartWindowsSandboxSetup);
    }

    private bool ApplyDailyUseEvent(ConversationEvent value, WorkerNotification<ConversationEvent> notification)
    {
        if (value.Notice is { } notice)
        {
            AddAppServerNotice(notice);
            return value.Kind == ConversationEventKind.AppServerNotice;
        }

        if (value.Kind == ConversationEventKind.PlanDelta && value.PlanDelta is { } delta)
        {
            ApplyPlanDelta(delta, notification);
            return true;
        }

        if (value.Kind == ConversationEventKind.ItemCompleted && value.Plan is { IsComplete: true } finalPlan)
        {
            ChatItemViewModel? completedPlan = FindDailyUseTranscriptItem(value, notification);
            if (completedPlan is null && value.ItemId is { Length: > 0 })
            {
                completedPlan = new ChatItemViewModel("Plan", string.Empty, ConversationEventKind.PlanUpdated)
                {
                    ItemId = value.ItemId,
                    ThreadId = value.ThreadId,
                    TurnId = value.TurnId,
                    StatePartitionFingerprint = notification.StatePartitionFingerprint,
                    OwnerGeneration = notification.OwnerGeneration,
                    ConnectionGeneration = notification.ConnectionGeneration,
                };
                Items.Add(completedPlan);
            }

            if (completedPlan is not null)
            {
                completedPlan.SetPlanSnapshot(finalPlan, markdown, value.Truncated);
                if (completedPlan.ItemId is { Length: > 0 } itemId
                    && completedPlan.ThreadId is { Length: > 0 } threadId
                    && completedPlan.TurnId is { Length: > 0 } turnId)
                {
                    string key = CreatePlanKey(threadId, turnId, itemId, notification);
                    completedPlanKeys.Add(key);
                    if (completedPlanKeys.Count > 1000)
                    {
                        completedPlanKeys.Remove(completedPlanKeys.First());
                    }
                    provisionalPlanText.Remove(key);
                }
            }

            return true;
        }

        if (value.Parts.Count > 0 && value.ItemId is { Length: > 0 })
        {
            ChatItemViewModel? item = FindDailyUseTranscriptItem(value, notification);
            if (item is null)
            {
                item = new ChatItemViewModel("Result", string.Empty, value.Kind)
                {
                    ItemId = value.ItemId, ThreadId = value.ThreadId, TurnId = value.TurnId,
                    StatePartitionFingerprint = notification.StatePartitionFingerprint,
                    OwnerGeneration = notification.OwnerGeneration, ConnectionGeneration = notification.ConnectionGeneration,
                };
                Items.Add(item);
            }
            if (item is not null)
            {
                var projected = value.Parts.Take(MaximumArtifactParts)
                    .Select(part => CreateArtifactPartPresentation(part, notification))
                    .ToArray();
                item.ReplaceArtifactParts(projected);
                item.Text = markdown.ToSafeText(value.Text ?? string.Empty);
                item.IsHistoryCompleted = value.Kind == ConversationEventKind.ItemCompleted;
                return true;
            }
        }

        return false;
    }

    private ChatItemViewModel? FindDailyUseTranscriptItem(ConversationEvent value, WorkerNotification<ConversationEvent> notification)
        => Items.LastOrDefault(item =>
            !string.IsNullOrEmpty(value.ItemId)
            && string.Equals(item.ThreadId, value.ThreadId, StringComparison.Ordinal)
            && string.Equals(item.TurnId, value.TurnId, StringComparison.Ordinal)
            && string.Equals(item.ItemId, value.ItemId, StringComparison.Ordinal)
            && string.Equals(item.StatePartitionFingerprint, notification.StatePartitionFingerprint, StringComparison.Ordinal)
            && item.OwnerGeneration == notification.OwnerGeneration
            && item.ConnectionGeneration == notification.ConnectionGeneration);

    private void ApplyPlanDelta(TurnPlanDeltaEvent delta, WorkerNotification<ConversationEvent> notification)
    {
        if (string.IsNullOrWhiteSpace(delta.ThreadId)
            || string.IsNullOrWhiteSpace(delta.TurnId)
            || string.IsNullOrWhiteSpace(delta.ItemId)
            || !string.Equals(delta.ThreadId, SelectedThread?.Id ?? Status.ThreadId, StringComparison.Ordinal))
        {
            return;
        }

        string key = CreatePlanKey(delta.ThreadId, delta.TurnId, delta.ItemId, notification);
        if (completedPlanKeys.Contains(key))
        {
            return;
        }

        ConversationEvent eventValue = notification.Value;
        ChatItemViewModel? item = FindDailyUseTranscriptItem(eventValue, notification);
        if (item?.IsPlanComplete == true) return;
        if (item is null)
        {
            item = new ChatItemViewModel("Plan", string.Empty, ConversationEventKind.PlanUpdated)
            {
                ItemId = delta.ItemId,
                ThreadId = delta.ThreadId,
                TurnId = delta.TurnId,
                StatePartitionFingerprint = notification.StatePartitionFingerprint,
                OwnerGeneration = notification.OwnerGeneration,
                ConnectionGeneration = notification.ConnectionGeneration,
            };
            Items.Add(item);
        }

        if (!provisionalPlanText.TryGetValue(key, out StringBuilder? text))
        {
            if (provisionalPlanText.Count >= 200)
            {
                provisionalPlanText.Remove(provisionalPlanText.Keys.First());
            }

            text = new StringBuilder();
            provisionalPlanText.Add(key, text);
        }

        int remaining = Math.Max(0, MaximumPlanTextBytes - Encoding.UTF8.GetByteCount(text.ToString()));
        if (remaining > 0 && !string.IsNullOrEmpty(delta.Text))
        {
            int length = delta.Text.Length;
            while (length > 0 && Encoding.UTF8.GetByteCount(delta.Text.AsSpan(0, length)) > remaining)
            {
                length--;
            }

            if (length > 0 && char.IsHighSurrogate(delta.Text[length - 1]))
            {
                length--;
            }

            if (length > 0)
            {
                text.Append(delta.Text.AsSpan(0, length));
                item.AppendPlanDelta(delta.Text[..length], MaximumPlanTextBytes);
            }
        }
    }

    private void ApplyDailyUsePlanSnapshot(
        ChatItemViewModel item,
        TurnPlanSnapshot? snapshot,
        WorkerNotification<ConversationEvent> notification)
    {
        if (snapshot is null)
        {
            return;
        }

        if (!string.Equals(snapshot.ThreadId, item.ThreadId, StringComparison.Ordinal)
            || !string.Equals(snapshot.TurnId, item.TurnId, StringComparison.Ordinal))
        {
            return;
        }

        item.SetPlanSnapshot(snapshot, markdown, notification.Value.Truncated);
        if (snapshot.IsComplete && item.ItemId is { Length: > 0 }
            && item.ThreadId is { Length: > 0 } threadId
            && item.TurnId is { Length: > 0 } turnId)
        {
            string key = CreatePlanKey(threadId, turnId, item.ItemId, notification);
            completedPlanKeys.Add(key);
            if (completedPlanKeys.Count > 1000)
            {
                completedPlanKeys.Remove(completedPlanKeys.First());
            }
            provisionalPlanText.Remove(key);
        }
    }

    private void ApplyDailyUseHistoryItem(
        ChatItemViewModel item,
        ThreadHistoryItem historyItem,
        string threadId,
        OwnerSnapshot owner)
    {
        if (historyItem.Plan is { } plan
            && string.Equals(plan.ThreadId, threadId, StringComparison.Ordinal)
            && string.Equals(plan.TurnId, historyItem.TurnId, StringComparison.Ordinal))
        {
            item.SetPlanSnapshot(plan, markdown);
            if (plan.IsComplete)
            {
                string planKey = string.Join("\0",
                    owner.StatePartitionFingerprint ?? string.Empty,
                    owner.OwnerGeneration.ToString(CultureInfo.InvariantCulture),
                    owner.ConnectionGeneration.ToString(CultureInfo.InvariantCulture),
                    threadId,
                    historyItem.TurnId,
                    historyItem.Id);
                completedPlanKeys.Add(planKey);
                if (completedPlanKeys.Count > 1000)
                {
                    completedPlanKeys.Remove(completedPlanKeys.First());
                }
            }
        }

        if (historyItem.Parts is { Count: > 0 })
        {
            var previewOwner = new ArtifactPreviewOwner(
                owner.StatePartitionFingerprint,
                owner.OwnerGeneration,
                owner.ConnectionGeneration);
            ArtifactPartPresentationViewModel[] parts = historyItem.Parts.Take(MaximumArtifactParts)
                .Select(part => new ArtifactPartPresentationViewModel(
                    part,
                    markdown,
                    (presentation, action) => PerformArtifactActionAsync(presentation, action, previewOwner, threadId)))
                .ToArray();
            item.ReplaceArtifactParts(parts);
        }
    }

    private string GetComposerAdmissionReason()
    {
        SlashCommandParseResult slashParse = slashCommandParser.Parse(ComposerText);
        if (slashParse.Kind != SlashCommandParseKind.NotCommand
            && (slashParse.Invocation is null || !slashParse.Invocation.StartsTurn))
        {
            return string.Empty;
        }

        ModelInfo? model = GetSelectedModelInfo();
        if (model is null)
        {
            return string.Empty;
        }

        bool needsText = !string.IsNullOrWhiteSpace(ComposerText)
            || HasPendingSkill
            || PendingAttachments.Any(static attachment => !IsImageAttachment(attachment.FullPath));
        bool needsImage = PendingAttachments.Any(static attachment => IsImageAttachment(attachment.FullPath));
        HashSet<string> modalities = model.InputModalities
            .Where(static modality => !string.IsNullOrWhiteSpace(modality))
            .ToHashSet(StringComparer.Ordinal);

        if (needsText && !modalities.Contains("text"))
        {
            return $"The selected model {markdown.ToSafeText(model.DisplayName ?? model.Id).Trim()} does not advertise text input. The draft remains in the composer.";
        }

        if (needsImage && !modalities.Contains("image"))
        {
            return $"The selected model {markdown.ToSafeText(model.DisplayName ?? model.Id).Trim()} does not support image input. The draft remains in the composer.";
        }

        return string.Empty;
    }

    private void AddAppServerNotice(AppServerNotice notice)
    {
        string currentThreadId = SelectedThread?.Id ?? Status.ThreadId ?? string.Empty;
        if (!string.IsNullOrEmpty(notice.ThreadId)
            && !string.Equals(notice.ThreadId, currentThreadId, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(noticeThreadId, currentThreadId, StringComparison.Ordinal))
        {
            appServerNotices.Clear();
            noticeIndices.Clear();
            noticeThreadId = currentThreadId;
        }

        var projected = new AppServerNoticePresentationViewModel(notice, markdown);
        string key = projected.CoalesceKey;
        if (noticeIndices.TryGetValue(key, out int existingIndex) && existingIndex < appServerNotices.Count)
        {
            appServerNotices[existingIndex] = projected;
            return;
        }

        if (appServerNotices.Count == MaximumVisibleNotices)
        {
            appServerNotices.RemoveAt(0);
            noticeIndices.Clear();
            for (int index = 0; index < appServerNotices.Count; index++)
            {
                noticeIndices[appServerNotices[index].CoalesceKey] = index;
            }
        }

        int indexToAdd = appServerNotices.Count;
        appServerNotices.Add(projected);
        noticeIndices[key] = indexToAdd;
        OnPropertyChanged(nameof(HasAppServerNotices));
    }

    private static string CreatePlanKey(
        string threadId,
        string turnId,
        string itemId,
        WorkerNotification<ConversationEvent> notification)
        => string.Join("\0",
            notification.StatePartitionFingerprint ?? string.Empty,
            notification.OwnerGeneration.ToString(CultureInfo.InvariantCulture),
            notification.ConnectionGeneration.ToString(CultureInfo.InvariantCulture),
            threadId,
            turnId,
            itemId);

    private void OnDailyUseThreadChanged(string? threadId)
    {
        if (string.Equals(noticeThreadId, threadId, StringComparison.Ordinal))
        {
            return;
        }

        noticeThreadId = threadId;
        completedPlanKeys.Clear();
        provisionalPlanText.Clear();
        ClearShellCommandConfirmation();
        ShellCommandStatusText = string.Empty;
        appServerNotices.Clear();
        noticeIndices.Clear();
        OnPropertyChanged(nameof(HasAppServerNotices));
        artifactPreviewCache.Reset();
        foreach (ChatItemViewModel item in Items)
        {
            foreach (ArtifactPartPresentationViewModel part in item.ArtifactParts)
            {
                part.PreviewFragment = null;
                part.ActionStatusText = string.Empty;
            }
        }
    }

    private void ClearDailyUseOwnerState()
    {
        appServerNotices.Clear();
        noticeIndices.Clear();
        completedPlanKeys.Clear();
        provisionalPlanText.Clear();
        noticeThreadId = null;
        ClearShellCommandConfirmation();
        ShellCommandStatusText = string.Empty;
        artifactPreviewCache.Reset();
        OnPropertyChanged(nameof(HasAppServerNotices));
        OnPropertyChanged(nameof(ComposerAdmissionReason));
        OnPropertyChanged(nameof(HasComposerAdmissionReason));
    }

    private void OnDailyUseStatusChanged(long? previousGeneration, WorkerStatus value)
    {
        OnPropertyChanged(nameof(ShowWindowsSandboxSetup));
        CheckWindowsSandboxReadinessCommand?.RaiseCanExecuteChanged();
        if (previousGeneration != value.Target?.Generation)
        {
            artifactPreviewCache.Reset();
            windowsSandboxReadiness = null;
            windowsSandboxReadinessGeneration = null;
            windowsSandboxSetupGeneration = null;
            windowsSandboxSetupState = WindowsSandboxSetupState.NotObserved;
            windowsSandboxSetupSolutionRoot = null;
            OnPropertyChanged(nameof(WindowsSandboxSolutionRootText));
            IsWindowsSandboxSetupInProgress = false;
            WindowsSandboxStatusText = "Readiness has not been checked for this connection.";
        }
    }

    private void DisposeDailyUse()
    {
        shellCommandSnapshot = null;
        shellCommandOwner = null;
        ShellCommandConfirmation = null;
        artifactPreviewCache.Dispose();
    }

    private async Task<bool> PrepareShellCommandAsync(string arguments, string threadId)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (Status.TurnId is not null || !IsThreadJoined || !string.Equals(threadId, SelectedThread?.Id ?? Status.ThreadId, StringComparison.Ordinal))
        {
            await ShowSlashFailureAsync("Join the current thread and wait for it to become idle before preparing a shell command.").ConfigureAwait(false);
            return false;
        }

        if (!ShellCommandParser.TryParseArguments(arguments, out ShellCommandRequest? parsed, out string? errorMessage)
            || parsed is null)
        {
            await ShowSlashFailureAsync(errorMessage ?? "Use /shell [--timeout-ms N] -- <command>.").ConfigureAwait(false);
            return false;
        }

        shellCommandSnapshot = null;
        shellCommandOwner = null;
        await OnUiAsync(() =>
        {
            ShellCommandConfirmation = null;
            ShellCommandStatusText = "Checking the command target and policy…";
        }).ConfigureAwait(false);

        ShellCommandPrepareResult result = await bridge.PrepareShellCommandAsync(
            StampOwner(new ShellCommandPrepareRequest
            {
                ThreadId = threadId,
                Command = parsed.Command,
                TimeoutMs = parsed.TimeoutMs,
            }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        if (!result.IsEligible || result.Confirmation is null)
        {
            string rejection = markdown.ToSafeText(result.RejectionReason ?? result.UnavailableReason ?? "The server rejected this shell command.").Trim();
            await OnUiAsync(() => ShellCommandStatusText = rejection).ConfigureAwait(false);
            return false;
        }

        ShellCommandConfirmationSnapshot snapshot = result.Confirmation;
        if (!string.Equals(snapshot.ThreadId, threadId, StringComparison.Ordinal)
            || snapshot.OwnerGeneration != owner.OwnerGeneration
            || snapshot.ConnectionGeneration != owner.ConnectionGeneration
            || !string.Equals(snapshot.StatePartitionFingerprint, owner.StatePartitionFingerprint, StringComparison.Ordinal)
            || !string.Equals(snapshot.Command, parsed.Command, StringComparison.Ordinal)
            || Status.TurnId is not null)
        {
            await OnUiAsync(() => ShellCommandStatusText = "The prepared command no longer matches the selected connection. Prepare it again.").ConfigureAwait(false);
            return false;
        }

        shellCommandSnapshot = snapshot;
        shellCommandOwner = owner;
        await OnUiAsync(() =>
        {
            ShellCommandConfirmation = new ShellCommandConfirmationPresentation(snapshot, markdown);
            ShellCommandStatusText = string.Empty;
        }).ConfigureAwait(false);
        return true;
    }

    private async Task ConfirmShellCommandAsync()
    {
        ShellCommandConfirmationSnapshot? snapshot = shellCommandSnapshot;
        OwnerSnapshot? owner = shellCommandOwner;
        if (snapshot is null || owner is null || !IsCurrentOwner(owner.Value)
            || Status.TurnId is not null
            || !IsThreadJoined
            || !string.Equals(SelectedThread?.Id ?? Status.ThreadId, snapshot.ThreadId, StringComparison.Ordinal))
        {
            await OnUiAsync(() =>
            {
                ShellCommandStatusText = "The command target changed. Prepare the command again before confirming.";
                ClearShellCommandConfirmation();
            }).ConfigureAwait(false);
            return;
        }

        await OnUiAsync(() =>
        {
            ClearShellCommandConfirmation();
            ShellCommandStatusText = "Sending the confirmed command…";
        }).ConfigureAwait(false);
        ShellCommandExecuteResult result = await bridge.ExecuteShellCommandAsync(
            StampOwner(new ShellCommandExecuteRequest { Confirmation = snapshot, UserConfirmed = true }, owner.Value),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner.Value))
        {
            return;
        }

        string status = result.Outcome switch
        {
            ShellCommandOutcome.NotSent => "The command was not sent. You can prepare it again.",
            ShellCommandOutcome.Acknowledged => "The server acknowledged the shell command. Its output is not attached to this conversation turn.",
            ShellCommandOutcome.DefinitiveFailure => markdown.ToSafeText(result.ErrorMessage ?? "The server rejected the shell command.").Trim(),
            ShellCommandOutcome.OutcomeUnknown => "The command outcome is unknown. Do not retry unless you have checked the server; it may have run.",
            _ => "The command outcome was not reported.",
        };
        await OnUiAsync(() => ShellCommandStatusText = status).ConfigureAwait(false);
    }

    private Task CancelShellCommandAsync()
    {
        ClearShellCommandConfirmation();
        ShellCommandStatusText = "Shell command canceled before dispatch.";
        return Task.CompletedTask;
    }

    private void ClearShellCommandConfirmation()
    {
        shellCommandSnapshot = null;
        shellCommandOwner = null;
        ShellCommandConfirmation = null;
    }

    private async Task SavePendingAsThreadAttachmentAsync(AttachmentChipViewModel attachment)
    {
        string? threadId = SelectedThread?.Id ?? Status.ThreadId;
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (threadId is null || !IsThreadJoined || Status.State != WorkerConnectionState.Ready)
        {
            await ShowSlashFailureAsync("Join an idle conversation before saving an attachment to it.").ConfigureAwait(false);
            return;
        }

        string extension = Path.GetExtension(attachment.FullPath).ToLowerInvariant();
        string? mimeType = extension switch
        {
            ".txt" => "text/plain",
            ".md" => "text/plain",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => null,
        };
        if (mimeType is null)
        {
            await ShowSlashFailureAsync("Only text, PDF, PNG, and JPEG files can be saved as conversation attachments.").ConfigureAwait(false);
            return;
        }

        SavedAttachmentMutationResult result = await bridge.AddSavedAttachmentAsync(
            StampOwner(new SavedAttachmentAddRequest
            {
                ThreadId = threadId,
                LocalPath = attachment.FullPath,
                MimeType = mimeType,
                DisplayName = Path.GetFileName(attachment.FullPath),
            }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner) || SelectedThread?.Id != threadId)
        {
            return;
        }

        string status = result.Outcome switch
        {
            AttachmentMutationOutcome.Created => "Attachment saved to this conversation.",
            AttachmentMutationOutcome.AlreadyExists => "This file is already saved to the conversation.",
            AttachmentMutationOutcome.OutcomeUnknown => "The save outcome is unknown. Refresh attachment metadata before retrying.",
            _ => markdown.ToSafeText(result.ErrorMessage ?? "The attachment could not be saved.").Trim(),
        };
        await OnUiAsync(() =>
        {
            ThreadAttachmentsStatusText = status;
            if (result.Attachment is not null)
            {
                MergeAttachment(result.Attachment);
            }
            else if (result.Outcome is AttachmentMutationOutcome.Created or AttachmentMutationOutcome.AlreadyExists)
            {
                _ = RefreshAttachmentsAsync();
            }
        }).ConfigureAwait(false);
    }

    private async Task RemoveSavedAttachmentAsync(ThreadAttachmentPresentationViewModel attachment)
    {
        string? threadId = SelectedThread?.Id ?? Status.ThreadId;
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (!attachment.CanRemove || threadId is null || !IsThreadJoined || Status.State != WorkerConnectionState.Ready)
        {
            await OnUiAsync(() => ThreadAttachmentsStatusText = "Join an idle conversation before removing a saved attachment.").ConfigureAwait(false);
            return;
        }

        SavedAttachmentMutationResult result = await bridge.RemoveSavedAttachmentAsync(
            StampOwner(new SavedAttachmentRemoveRequest
            {
                ThreadId = threadId,
                AttachmentType = attachment.MembershipKey.AttachmentType,
                IdentityKey = attachment.IdentityKey,
                UserConfirmed = true,
            }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner) || SelectedThread?.Id != threadId)
        {
            return;
        }

        await OnUiAsync(() =>
        {
            attachment.CloseRemovalConfirmation();
            ThreadAttachmentsStatusText = result.Outcome switch
            {
                AttachmentMutationOutcome.Removed or AttachmentMutationOutcome.AlreadyAbsent => "Saved attachment removed.",
                AttachmentMutationOutcome.OutcomeUnknown => "Removal outcome is unknown. Refresh attachment metadata before retrying.",
                _ => markdown.ToSafeText(result.ErrorMessage ?? "The saved attachment could not be removed.").Trim(),
            };
            if (result.Outcome is AttachmentMutationOutcome.Removed or AttachmentMutationOutcome.AlreadyAbsent)
            {
                deletedAttachmentIds.Add(attachment.Id);
                ThreadAttachments.Remove(attachment);
            }
        }).ConfigureAwait(false);
    }

    private async Task PerformArtifactActionAsync(
        ArtifactPartPresentationViewModel part,
        ArtifactActionKind action,
        ArtifactPreviewOwner owner,
        string? threadId)
    {
        if (string.IsNullOrWhiteSpace(part.ActionId)
            || !(action switch
            {
                ArtifactActionKind.Preview => part.CanPreview,
                ArtifactActionKind.Open => part.CanOpen,
                ArtifactActionKind.Reveal => part.CanReveal,
                _ => false,
            })
            || !IsCurrentOwner(new OwnerSnapshot(owner.StatePartitionFingerprint, owner.OwnerGeneration, owner.ConnectionGeneration))
            || !string.Equals(threadId, SelectedThread?.Id ?? Status.ThreadId, StringComparison.Ordinal))
        {
            await OnUiAsync(() => part.ActionStatusText = "This artifact action is no longer available.").ConfigureAwait(false);
            return;
        }

        string? rootBefore = await workspaceDirectoryResolver.TryResolveFromWorkspaceAsync(lifetime.Token).ConfigureAwait(false);
        if (rootBefore is null && action != ArtifactActionKind.Preview)
        {
            await OnUiAsync(() => part.ActionStatusText = "Open the artifact's workspace in Visual Studio before using this action.").ConfigureAwait(false);
            return;
        }

        var stampedOwner = new OwnerSnapshot(owner.StatePartitionFingerprint, owner.OwnerGeneration, owner.ConnectionGeneration);
        ArtifactActionResult result = await bridge.ResolveArtifactActionAsync(
            StampOwner(new ArtifactActionRequest { ActionId = part.ActionId, Action = action }, stampedOwner),
            lifetime.Token).ConfigureAwait(false);
        string? rootAfter = await workspaceDirectoryResolver.TryResolveFromWorkspaceAsync(lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(stampedOwner)
            || !string.Equals(rootBefore, rootAfter, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(threadId, SelectedThread?.Id ?? Status.ThreadId, StringComparison.Ordinal))
        {
            await OnUiAsync(() => part.ActionStatusText = "The workspace or connection changed; the artifact action was discarded.").ConfigureAwait(false);
            return;
        }

        if (action == ArtifactActionKind.Preview)
        {
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(stampedOwner) || !string.Equals(threadId, SelectedThread?.Id ?? Status.ThreadId, StringComparison.Ordinal)) return;
                ArtifactPreviewResult preview = artifactPreviewCache.Create(result.PreviewBytes, result.PreviewMimeType, result.Width, result.Height, owner);
                part.PreviewFragment = preview.Fragment;
                part.ActionStatusText = preview.FailureReason ?? (preview.Fragment is null ? "Image preview is unavailable." : string.Empty);
            }).ConfigureAwait(false);
            return;
        }

        if (!LocalPath.TryCreate(rootAfter, out LocalPath localRoot))
        {
            await OnUiAsync(() => part.ActionStatusText = "The current workspace path is not available for this action.").ConfigureAwait(false);
            return;
        }

        try
        {
            switch (action)
            {
                case ArtifactActionKind.Open:
                    await artifactFileActions.OpenAsync(result, localRoot, lifetime.Token).ConfigureAwait(false);
                    await OnUiAsync(() => part.ActionStatusText = "Artifact opened in Visual Studio.").ConfigureAwait(false);
                    break;
                case ArtifactActionKind.Reveal:
                    await artifactFileActions.RevealAsync(result, localRoot, lifetime.Token).ConfigureAwait(false);
                    await OnUiAsync(() => part.ActionStatusText = "Artifact revealed in File Explorer.").ConfigureAwait(false);
                    break;
                case ArtifactActionKind.Preview:
                    ArtifactPreviewResult preview = artifactPreviewCache.Create(result.PreviewBytes, result.PreviewMimeType, result.Width, result.Height, owner);
                    await OnUiAsync(() =>
                    {
                        part.PreviewFragment = preview.Fragment;
                        part.ActionStatusText = preview.FailureReason ?? (preview.Fragment is null ? "Image preview is unavailable." : string.Empty);
                    }).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            await OnUiAsync(() => part.ActionStatusText = markdown.ToSafeText(ex.Message).Trim()).ConfigureAwait(false);
        }
    }

    private async Task CheckWindowsSandboxReadinessAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        long? generation = Status.Target?.Generation;
        if (windowsSandboxReadinessGeneration == generation && windowsSandboxReadiness is not null)
        {
            return;
        }

        string? solutionRoot = await workspaceDirectoryResolver.TryResolveOpenSolutionDirectoryAsync(lifetime.Token).ConfigureAwait(false);
        await OnUiAsync(() => WindowsSandboxStatusText = "Checking Windows sandbox readiness…").ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }
        WindowsSandboxReadinessResult result = await bridge.GetWindowsSandboxReadinessAsync(
            StampOwner(new WindowsSandboxReadinessRequest(), owner), lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        await OnUiAsync(() =>
        {
            windowsSandboxReadiness = result;
            windowsSandboxReadinessGeneration = generation;
            windowsSandboxSetupSolutionRoot = solutionRoot;
            OnPropertyChanged(nameof(WindowsSandboxSolutionRootText));
            WindowsSandboxStatusText = !result.IsWindows
                ? "The initialized server is not Windows."
                : !result.IsLocalStdio
                    ? "Sandbox setup is available only for a local app-server connection."
                    : markdown.ToSafeText(result.Status).Trim();
            StartWindowsSandboxSetupCommand.RaiseCanExecuteChanged();
        }).ConfigureAwait(false);
    }

    private bool CanStartWindowsSandboxSetup()
        => !IsWindowsSandboxSetupInProgress
            && windowsSandboxReadiness is { IsWindows: true, IsLocalStdio: true }
            && windowsSandboxReadinessGeneration == Status.Target?.Generation
            && windowsSandboxSetupGeneration != Status.Target?.Generation
            && Status.State == WorkerConnectionState.Ready;

    private async Task StartWindowsSandboxSetupAsync()
    {
        WindowsSandboxSetupMode confirmedMode = SelectedWindowsSandboxMode;
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        long? generation = Status.Target?.Generation;
        if (!CanStartWindowsSandboxSetup() || generation is null)
        {
            return;
        }

        string? solutionRoot = await workspaceDirectoryResolver.TryResolveOpenSolutionDirectoryAsync(lifetime.Token).ConfigureAwait(false);
        if (!string.Equals(solutionRoot, windowsSandboxSetupSolutionRoot, StringComparison.OrdinalIgnoreCase))
        {
            await OnUiAsync(() =>
            {
                windowsSandboxSetupSolutionRoot = solutionRoot;
                OnPropertyChanged(nameof(WindowsSandboxSolutionRootText));
                WindowsSandboxStatusText = "The active solution changed. Review the updated working directory and start again.";
            }).ConfigureAwait(false);
            return;
        }

        await OnUiAsync(() =>
        {
            WindowsSandboxStatusText = solutionRoot is null
                ? "Starting setup with no solution working directory. Progress is indeterminate until the server reports completion."
                : $"Starting setup for solution root {SafeMarkdownService.ToSafeLiteralText(solutionRoot)}. Progress is indeterminate until the server reports completion.";
            IsWindowsSandboxSetupInProgress = true;
            windowsSandboxSetupGeneration = generation;
            windowsSandboxSetupState = WindowsSandboxSetupState.Starting;
        }).ConfigureAwait(false);

        string? currentRoot = await workspaceDirectoryResolver.TryResolveOpenSolutionDirectoryAsync(lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner) || confirmedMode != SelectedWindowsSandboxMode || !string.Equals(solutionRoot, currentRoot, StringComparison.OrdinalIgnoreCase))
        {
            await OnUiAsync(() =>
            {
                IsWindowsSandboxSetupInProgress = false;
                windowsSandboxSetupGeneration = null;
                WindowsSandboxStatusText = "The active solution changed before setup started. Check readiness and try again.";
            }).ConfigureAwait(false);
            return;
        }

        WindowsSandboxSetupStartResult result = await bridge.StartWindowsSandboxSetupAsync(
            StampOwner(new WindowsSandboxSetupStartRequest { Mode = confirmedMode, Cwd = solutionRoot }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return;
        }

        await OnUiAsync(() =>
        {
            windowsSandboxSetupState = result.State;
            IsWindowsSandboxSetupInProgress = result.State is WindowsSandboxSetupState.Starting or WindowsSandboxSetupState.Running or WindowsSandboxSetupState.OutcomeUnknown;
            WindowsSandboxStatusText = result.State switch
            {
                WindowsSandboxSetupState.Starting or WindowsSandboxSetupState.Running => "Setup was started. Progress is indeterminate while the server works.",
                WindowsSandboxSetupState.Succeeded => "Windows sandbox setup completed successfully.",
                WindowsSandboxSetupState.OutcomeUnknown => "Setup outcome is unknown. This connection will not start it again.",
                WindowsSandboxSetupState.Unsupported => markdown.ToSafeText(result.Message ?? "Windows sandbox setup is unavailable.").Trim(),
                _ => markdown.ToSafeText(result.Message ?? "Windows sandbox setup did not start.").Trim(),
            };
            StartWindowsSandboxSetupCommand.RaiseCanExecuteChanged();
        }).ConfigureAwait(false);
    }

    private Task OnWindowsSandboxSetupChangedAsync(WorkerNotification<WindowsSandboxSetupCompletedEvent> notification)
        => OnUiAsync(() =>
        {
            if (!IsNotificationCurrent(notification) || windowsSandboxSetupGeneration != Status.Target?.Generation)
            {
                return;
            }

            windowsSandboxSetupState = notification.Value.State;
            IsWindowsSandboxSetupInProgress = notification.Value.State is WindowsSandboxSetupState.Starting or WindowsSandboxSetupState.Running or WindowsSandboxSetupState.OutcomeUnknown;
            WindowsSandboxStatusText = notification.Value.Success
                ? "Windows sandbox setup completed successfully."
                : markdown.ToSafeText(notification.Value.ErrorMessage ?? notification.Value.State.ToString()).Trim();
        });

    private ArtifactPartPresentationViewModel? CreateSavedAttachmentPresentation(ThreadAttachmentMetadata metadata)
    {
        if (!metadata.IsKnownPayload || string.IsNullOrEmpty(metadata.ActionId)) return null;
        OwnerSnapshot captured = CaptureOwnerSnapshot();
        var owner = new ArtifactPreviewOwner(captured.StatePartitionFingerprint, captured.OwnerGeneration, captured.ConnectionGeneration);
        string? threadId = SelectedThread?.Id ?? Status.ThreadId;
        return new ArtifactPartPresentationViewModel(new ArtifactPart
        {
            Kind = ArtifactPartKind.File, DisplayName = metadata.DisplayName, MimeType = metadata.MimeType,
            ActionId = metadata.ActionId, AllowedActions = metadata.AllowedActions,
        }, markdown, (presentation, action) => PerformArtifactActionAsync(presentation, action, owner, threadId));
    }

    private ArtifactPartPresentationViewModel CreateArtifactPartPresentation(
        ArtifactPart part,
        WorkerNotification<ConversationEvent> notification)
    {
        var owner = new ArtifactPreviewOwner(
            notification.StatePartitionFingerprint,
            notification.OwnerGeneration,
            notification.ConnectionGeneration);
        return new ArtifactPartPresentationViewModel(
            part,
            markdown,
            (presentation, action) => PerformArtifactActionAsync(presentation, action, owner, notification.Value.ThreadId));
    }
}
