using System.Text.Json;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

// Compaction is complete only on thread/compacted or a completed contextCompaction item. An RPC
// error, an error notification, or a turn that ends first means it failed; nothing is retried and
// no marker is synthesized, because displayed history always comes from the server. Whether a
// failure changed the server history depends on the tier, so the message does not claim either.
public sealed partial class ChatViewModel
{
    internal const string CompactionFailedText = "Compaction failed.";

    internal const string CompressedHistoryResumeNote =
        "If this conversation was compacted, Codex CLI 0.164.0 or later can resume compressed histories.";

    // Registered before thread/compact/start is dispatched: the outcome notification can arrive
    // while that request is still awaiting its response. All access is on the UI thread.
    private CompactionOperation? pendingCompaction;

    private sealed class CompactionOperation(string threadId)
    {
        public string ThreadId { get; } = threadId;
    }

    private CompactionOperation BeginCompactionTracking(string threadId)
    {
        var operation = new CompactionOperation(threadId);
        pendingCompaction = operation;
        return operation;
    }

    // Returns true when this operation was still pending, i.e. no outcome notification resolved it.
    private bool EndCompactionTracking(CompactionOperation operation)
    {
        if (!ReferenceEquals(pendingCompaction, operation))
        {
            return false;
        }

        pendingCompaction = null;
        return true;
    }

    private void CompleteCompactionTracking(string threadId)
    {
        if (string.Equals(pendingCompaction?.ThreadId, threadId, StringComparison.Ordinal))
        {
            pendingCompaction = null;
        }
    }

    // Runs on the UI thread for every current conversation event, before thread filtering.
    private void ObserveCompactionOutcome(ConversationEvent value)
    {
        if (pendingCompaction is null
            || string.IsNullOrEmpty(value.ThreadId)
            || !string.Equals(pendingCompaction.ThreadId, value.ThreadId, StringComparison.Ordinal)
            || value.Kind is not (ConversationEventKind.Error or ConversationEventKind.TurnCompleted))
        {
            return;
        }

        pendingCompaction = null;
        ReportCompactionFailure(value.ThreadId);
    }

    private void ReportCompactionFailure(string threadId)
    {
        if (!IsCompactionThreadSelected(threadId))
        {
            return;
        }

        SlashCommands.ShowFailure(CompactionFailedText);
        Items.Add(new ChatItemViewModel("Error", CompactionFailedText, ConversationEventKind.Error));
    }

    private bool IsCompactionThreadSelected(string threadId)
        => string.Equals(SelectedThread?.Id, threadId, StringComparison.Ordinal);

    private async Task<bool> HandleCompactionResultAsync(
        string threadId,
        CompactThreadResult result,
        OwnerSnapshot owner,
        CompactionOperation operation)
    {
        bool started = false;
        await OnUiAsync(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            bool isCurrentOperation = ReferenceEquals(pendingCompaction, operation);
            if (!result.IsSupported)
            {
                unavailableSlashCommands.Add(SlashCommandId.Compact);
                string reason = string.IsNullOrWhiteSpace(result.UnavailableReason)
                    ? "The app-server does not support this command."
                    : result.UnavailableReason;
                if (isCurrentOperation && IsCompactionThreadSelected(threadId))
                {
                    string safeMessage = markdown.ToSafeText(reason);
                    SlashCommands.ShowFailure(safeMessage);
                    Items.Add(new ChatItemViewModel("Error", safeMessage, ConversationEventKind.Error));
                }

                UpdateComposerSuggestions(ComposerText);
                return;
            }

            started = true;
            if (isCurrentOperation && IsCompactionThreadSelected(threadId))
            {
                string message = markdown.ToSafeText("Context compaction started.");
                SlashCommands.ShowStatus(message);
                Items.Add(new ChatItemViewModel("Status", message, ConversationEventKind.ItemCompleted));
            }
        }).ConfigureAwait(false);

        return started;
    }

    private string DescribeResumeFailure(Exception error)
    {
        if (GetProviderErrorCode(error) is { } providerErrorCode)
        {
            string providerMessage = providerErrorCode switch
            {
                "modelProviderMismatch" => "The conversation's saved model uses a provider that does not match the current configuration.",
                "modelProviderNotConfigured" => "The provider required by this conversation is not configured.",
                "modelProviderPolicyConflict" => "The conversation's provider conflicts with the current policy.",
                _ => "The app-server could not resume this conversation with its current provider configuration.",
            };
            return markdown.ToSafeText(
                $"{providerMessage} The transcript is opening read-only. You can still interrupt an active turn and use /goal pause or /goal clear.");
        }

        string message = markdown.ToSafeText(error.Message).Trim();
        if (message.Length > 512)
        {
            message = string.Concat(message.AsSpan(0, 512), "…");
        }

        return Status.FeatureTier < CodexFeatureTier.Tier164
            ? $"The conversation could not be resumed: {message} {CompressedHistoryResumeNote}"
            : $"The conversation could not be resumed: {message}";
    }

    private static string? GetProviderErrorCode(Exception error)
    {
        if (error is not StreamJsonRpc.RemoteInvocationException remote
            || remote.ErrorCode != WorkerErrorCodes.UpstreamOperationFailed
            || remote.ErrorData is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(remote.ErrorData.ToString() ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("providerErrorCode", out JsonElement codeElement)
                || codeElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return codeElement.GetString() switch
            {
                "modelProviderMismatch" => "modelProviderMismatch",
                "modelProviderNotConfigured" => "modelProviderNotConfigured",
                "modelProviderPolicyConflict" => "modelProviderPolicyConflict",
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
