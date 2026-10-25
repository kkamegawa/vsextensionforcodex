using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

// Compaction is complete only on thread/compacted or a completed contextCompaction item. An RPC
// error, an error notification, or a turn that ends first means it failed; nothing is retried and
// no marker is synthesized, because displayed history always comes from the server.
public sealed partial class ChatViewModel
{
    internal const string CompactionFailedText = "Compaction failed; the conversation history is unchanged.";

    internal const string CompressedHistoryResumeNote =
        "If this conversation was compacted, Codex CLI 0.164.0 or later can resume compressed histories.";

    private string? pendingCompactionThreadId;

    private void BeginCompactionTracking(string threadId) => pendingCompactionThreadId = threadId;

    private void CompleteCompactionTracking(string threadId)
    {
        if (string.Equals(pendingCompactionThreadId, threadId, StringComparison.Ordinal))
        {
            pendingCompactionThreadId = null;
        }
    }

    // Runs on the UI thread for every current conversation event, before thread filtering.
    private void ObserveCompactionOutcome(ConversationEvent value)
    {
        if (pendingCompactionThreadId is null
            || !string.Equals(pendingCompactionThreadId, value.ThreadId, StringComparison.Ordinal)
            || value.Kind is not (ConversationEventKind.Error or ConversationEventKind.TurnCompleted))
        {
            return;
        }

        pendingCompactionThreadId = null;
        SlashCommands.ShowFailure(CompactionFailedText);
        Items.Add(new ChatItemViewModel("Error", CompactionFailedText, ConversationEventKind.Error));
    }

    private string DescribeResumeFailure(Exception error)
    {
        string message = markdown.ToSafeText(error.Message).Trim();
        if (message.Length > 512)
        {
            message = string.Concat(message.AsSpan(0, 512), "…");
        }

        return Status.FeatureTier < CodexFeatureTier.Tier164
            ? $"The conversation could not be resumed: {message} {CompressedHistoryResumeNote}"
            : $"The conversation could not be resumed: {message}";
    }
}
