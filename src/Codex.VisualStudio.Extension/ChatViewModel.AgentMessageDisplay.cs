using Codex.VisualStudio.Contracts;

using System.Text;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    private const int MaximumCompletedAgentMessageKeys = 1024;
    private const int MaximumConsumedAgentRawTurnKeys = 1024;

    private readonly HashSet<string> pendingAgentRawKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedAgentMessageKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> completedAgentMessageKeyOrder = new();
    // Keep consumed turn IDs across owner changes so reconnect replay cannot recreate a consumed prompt.
    private readonly HashSet<string> consumedAgentRawTurnKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> consumedAgentRawTurnKeyOrder = new();

    private bool RequiresBufferedAgentMessageDisplay()
        => Status.FeatureTier != CodexFeatureTier.Baseline;

    private static string GetHistoryDisplayText(ThreadHistoryItem historyItem)
    {
        if (historyItem.Type != "agentMessage")
        {
            return historyItem.Text ?? string.Empty;
        }

        AgentMessageDisplayDecision? decision = historyItem.AgentMessageDisplayDecision;
        return decision is null
            ? historyItem.Text ?? string.Empty
            : decision.GetDisplayText(historyItem.Text) ?? string.Empty;
    }

    private void RenderCompletedAgentMessage(
        ConversationEvent value,
        WorkerNotification<ConversationEvent> notification)
    {
        // A completion without an item ID cannot be safely correlated with deltas or duplicates.
        // Tier 170+ keeps those deltas hidden, so fail closed and leave them for turn cleanup.
        if (string.IsNullOrWhiteSpace(value.ItemId))
        {
            return;
        }

        bool firstCompletion = MarkAgentMessageCompleted(value);

        string agentRawKey = GetAgentRawKey(value);
        ChatItemViewModel? existing = FindCurrentTranscriptItem(value, notification);
        if (existing?.IsHistoryCompleted == true)
        {
            // Completion keys are bounded. If an old key was evicted, avoid appending an
            // already-approved body again when the completed transcript row is still present.
            bool hasRawText = agentRawText.TryGetValue(agentRawKey, out _);
            if (!IsAgentRawTurnConsumed(value)
                && (firstCompletion || pendingAgentRawKeys.Contains(agentRawKey) || !hasRawText))
            {
                RemoveAgentRaw(agentRawKey);
                AppendApprovedAgentRaw(agentRawKey, value);
            }

            return;
        }

        RemoveAgentRaw(agentRawKey);

        AgentMessageDisplayDecision? decision = value.AgentMessageDisplayDecision;
        bool mayDisplayParts = decision is null or { Mode: AgentMessageDisplayMode.Show };
        string? displayText = decision is null ? value.Text : decision.GetDisplayText(value.Text);
        if (string.IsNullOrEmpty(displayText) && (!mayDisplayParts || value.Parts.Count == 0))
        {
            return;
        }

        SafeMarkdownRenderResult rendered = markdown.ToSafeTextAndBlocks(displayText ?? string.Empty);
        OwnerSnapshot owner = NotificationOwner(notification);
        string historyKey = string.Concat(
            owner.StatePartitionFingerprint,
            ":",
            owner.OwnerGeneration,
            ":",
            owner.ConnectionGeneration,
            ":",
            value.ThreadId,
            ":",
            value.TurnId,
            ":",
            value.ItemId);
        if (existing is null)
        {
            var item = new ChatItemViewModel("Codex", rendered.Text, ConversationEventKind.AgentMessageDelta)
            {
                ItemId = value.ItemId,
                ThreadId = value.ThreadId,
                TurnId = value.TurnId,
                HistoryKey = historyKey,
                IsHistoryCompleted = true,
                StatePartitionFingerprint = owner.StatePartitionFingerprint,
                OwnerGeneration = owner.OwnerGeneration,
                ConnectionGeneration = owner.ConnectionGeneration,
                IsTruncated = value.Truncated,
                OverflowFile = value.OverflowFile,
            };
            item.ApplyTiming(value.StartedAtMs, value.CompletedAtMs);
            item.UpdateBlocks(rendered.Blocks);
            ApplyAgentMessageParts(item, value, notification, mayDisplayParts);
            Items.Add(item);
        }
        else
        {
            SetTranscriptText(existing, rendered.Text);
            existing.UpdateBlocks(rendered.Blocks);
            ApplyAgentMessageParts(existing, value, notification, mayDisplayParts);
            existing.HistoryKey = historyKey;
            existing.IsHistoryCompleted = true;
            existing.IsTruncated |= value.Truncated;
            existing.OverflowFile = value.OverflowFile ?? existing.OverflowFile;
            existing.ApplyTiming(value.StartedAtMs, value.CompletedAtMs);
        }

        AppendApprovedAgentRaw(agentRawKey, value);

        TrimHistoryWindow();
    }

    private void AppendApprovedAgentRaw(string key, ConversationEvent value)
    {
        if (IsAgentRawTurnConsumed(value))
        {
            // Late/replayed completion can still render in the transcript, but a terminal turn
            // has already spent its choice-detection window.
            return;
        }

        if (value.AgentMessageDisplayDecision is null or { Mode: AgentMessageDisplayMode.Show })
        {
            if (!string.IsNullOrEmpty(value.Text))
            {
                // Keep only the completed, approved body for end-of-turn choice detection.
                AppendAgentRaw(key, value.Text);
            }
        }
    }

    private bool IsAgentMessageCompleted(ConversationEvent value)
        => completedAgentMessageKeys.Contains(GetAgentCompletionKey(value));

    private bool MarkAgentMessageCompleted(ConversationEvent value)
    {
        if (string.IsNullOrWhiteSpace(value.ItemId))
        {
            return false;
        }

        string key = GetAgentCompletionKey(value);
        if (!completedAgentMessageKeys.Add(key))
        {
            return false;
        }

        completedAgentMessageKeyOrder.Enqueue(key);
        while (completedAgentMessageKeyOrder.Count > MaximumCompletedAgentMessageKeys)
        {
            completedAgentMessageKeys.Remove(completedAgentMessageKeyOrder.Dequeue());
        }

        return true;
    }

    private bool IsAgentRawTurnConsumed(ConversationEvent value)
    {
        string? key = GetAgentRawTurnKey(value);
        return key is not null && consumedAgentRawTurnKeys.Contains(key);
    }

    private void MarkAgentRawTurnConsumed(ConversationEvent value)
    {
        string? key = GetAgentRawTurnKey(value);
        if (key is null || !consumedAgentRawTurnKeys.Add(key))
        {
            return;
        }

        consumedAgentRawTurnKeyOrder.Enqueue(key);
        while (consumedAgentRawTurnKeyOrder.Count > MaximumConsumedAgentRawTurnKeys)
        {
            consumedAgentRawTurnKeys.Remove(consumedAgentRawTurnKeyOrder.Dequeue());
        }
    }

    private static string? GetAgentRawTurnKey(ConversationEvent value)
    {
        if (string.IsNullOrWhiteSpace(value.ThreadId) || string.IsNullOrWhiteSpace(value.TurnId))
        {
            return null;
        }

        string threadId = value.ThreadId;
        string turnId = value.TurnId;
        return string.Concat(threadId.Length, ":", threadId, turnId.Length, ":", turnId);
    }

    private static string GetAgentCompletionKey(ConversationEvent value)
    {
        string threadId = value.ThreadId ?? string.Empty;
        string turnId = value.TurnId ?? string.Empty;
        string itemId = value.ItemId ?? string.Empty;
        return string.Concat(
            threadId.Length,
            ":",
            threadId,
            turnId.Length,
            ":",
            turnId,
            itemId.Length,
            ":",
            itemId);
    }

    private void ApplyAgentMessageParts(
        ChatItemViewModel item,
        ConversationEvent value,
        WorkerNotification<ConversationEvent> notification,
        bool mayDisplayParts)
    {
        if (!mayDisplayParts)
        {
            item.ReplaceArtifactParts([]);
            return;
        }

        ArtifactPartPresentationViewModel[] parts = value.Parts
            .Where(part => !(part.Kind == ArtifactPartKind.Text
                && value.Text is not null
                && string.Equals(part.Text, value.Text, StringComparison.Ordinal)))
            .Take(MaximumArtifactParts)
            .Select(part => CreateArtifactPartPresentation(part, notification))
            .ToArray();
        item.ReplaceArtifactParts(parts);
    }

    private void DiscardPendingAgentRawText()
    {
        bool lastMessageWasPending = lastAgentRawKey is not null && pendingAgentRawKeys.Contains(lastAgentRawKey);
        foreach (string key in pendingAgentRawKeys)
        {
            agentRawText.Remove(key);
        }

        pendingAgentRawKeys.Clear();
        if (lastMessageWasPending)
        {
            lastAgentRawKey = FindLatestApprovedAgentRawKey();
        }
    }

    private void TryDiscardPendingAgentRawTextAfterConnectionLoss()
    {
        try
        {
            DiscardPendingAgentRawText();
        }
        catch (Exception ex)
        {
            // A cleanup failure must not prevent reconnect attempts.
            ExtensionDiagnostics.Write("Pending assistant text cleanup failed after connection loss", ex);
        }
    }

    private void RemoveAgentRaw(string key)
    {
        bool wasLastMessage = string.Equals(lastAgentRawKey, key, StringComparison.Ordinal);
        agentRawText.Remove(key);
        pendingAgentRawKeys.Remove(key);
        if (wasLastMessage)
        {
            lastAgentRawKey = FindLatestApprovedAgentRawKey();
        }
    }

    private string? FindLatestApprovedAgentRawKey()
    {
        foreach (KeyValuePair<string, StringBuilder> entry in agentRawText.Entries.Reverse())
        {
            if (!pendingAgentRawKeys.Contains(entry.Key))
            {
                return entry.Key;
            }
        }

        return null;
    }
}
