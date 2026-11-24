using Codex.VisualStudio.Contracts;

using System.Text;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    private const int MaximumCompletedAgentMessageKeys = 1024;
    private const int MaximumConsumedAgentRawTurnKeys = 1024;
    private const int MaximumRestrictedAgentDisplayDecisions = 4096;

    private readonly HashSet<string> pendingAgentRawKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedAgentMessageKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> completedAgentMessageKeyOrder = new();
    private readonly Dictionary<AgentMessageDisplayIdentity, AgentMessageDisplayDecision> restrictedAgentDisplayDecisions = [];
    private readonly HashSet<AgentMessageDisplayScope> overflowedAgentDisplayDecisionScopes = [];
    // Keep consumed turn IDs across owner changes so reconnect replay cannot recreate a consumed prompt.
    private readonly HashSet<string> consumedAgentRawTurnKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> consumedAgentRawTurnKeyOrder = new();

    private readonly record struct AgentMessageDisplayScope(string StatePartitionFingerprint, long OwnerGeneration);

    private readonly record struct AgentMessageDisplayIdentity(
        string StatePartitionFingerprint,
        long OwnerGeneration,
        string ThreadId,
        string TurnId,
        string ItemId);

    private bool RequiresBufferedAgentMessageDisplay()
        => Status.FeatureTier != CodexFeatureTier.Baseline;

    private static string GetHistoryDisplayText(ThreadHistoryItem historyItem, AgentMessageDisplayDecision? decision)
    {
        if (historyItem.Type != "agentMessage")
        {
            return historyItem.Text ?? string.Empty;
        }

        return decision is null
            ? historyItem.Text ?? string.Empty
            : decision.GetDisplayText(historyItem.Text) ?? string.Empty;
    }

    private static AgentMessageDisplayDecision? ResolveAgentMessageDisplayDecision(
        AgentMessageDisplayDecision? current,
        AgentMessageDisplayDecision? incoming)
    {
        if (current is null)
        {
            return incoming;
        }

        if (incoming is null)
        {
            return current;
        }

        int currentPriority = GetAgentMessageDisplayPriority(current.Mode);
        int incomingPriority = GetAgentMessageDisplayPriority(incoming.Mode);
        if (currentPriority != incomingPriority)
        {
            return currentPriority > incomingPriority ? current : incoming;
        }

        if (currentPriority == 0)
        {
            return incoming;
        }

        string currentText = current.GetDisplayText(null) ?? string.Empty;
        string incomingText = incoming.GetDisplayText(null) ?? string.Empty;
        return StringComparer.Ordinal.Compare(currentText, incomingText) <= 0 ? current : incoming;
    }

    private static int GetAgentMessageDisplayPriority(AgentMessageDisplayMode mode)
        => mode switch
        {
            AgentMessageDisplayMode.Show => 0,
            AgentMessageDisplayMode.Hide => 1,
            AgentMessageDisplayMode.Block => 2,
            AgentMessageDisplayMode.Suppress => 3,
            _ => 4,
        };

    private AgentMessageDisplayDecision? GetRestrictedAgentDisplayDecision(
        OwnerSnapshot owner,
        string? threadId,
        string? turnId,
        string? itemId)
    {
        var scope = new AgentMessageDisplayScope(owner.StatePartitionFingerprint ?? string.Empty, owner.OwnerGeneration);
        if (overflowedAgentDisplayDecisionScopes.Contains(scope))
        {
            return new AgentMessageDisplayDecision { Mode = AgentMessageDisplayMode.Suppress };
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        AgentMessageDisplayDecision? decision = null;
        string partition = owner.StatePartitionFingerprint ?? string.Empty;
        string turnKey = turnId ?? string.Empty;
        string itemKey = itemId ?? string.Empty;
        AgentMessageDisplayIdentity[] candidateKeys =
        [
            new(partition, owner.OwnerGeneration, threadId, turnKey, itemKey),
            new(partition, owner.OwnerGeneration, threadId, turnKey, string.Empty),
            new(partition, owner.OwnerGeneration, threadId, string.Empty, itemKey),
            new(partition, owner.OwnerGeneration, threadId, string.Empty, string.Empty),
        ];
        foreach (AgentMessageDisplayIdentity key in candidateKeys)
        {
            if (restrictedAgentDisplayDecisions.TryGetValue(key, out AgentMessageDisplayDecision? candidate))
            {
                decision = ResolveAgentMessageDisplayDecision(decision, candidate);
            }
        }

        return decision;
    }

    private void RecordRestrictedAgentDisplayDecision(ChatItemViewModel item, AgentMessageDisplayDecision? decision)
    {
        if (decision is not { Mode: not AgentMessageDisplayMode.Show })
        {
            return;
        }

        string rawKey = !string.IsNullOrWhiteSpace(item.ItemId)
            ? item.ItemId
            : item.TurnId ?? "global-agent-message";
        RemoveAgentRaw(rawKey);
        RecordRestrictedAgentDisplayDecision(
            new OwnerSnapshot(item.StatePartitionFingerprint, item.OwnerGeneration, item.ConnectionGeneration),
            item.ThreadId,
            item.TurnId,
            item.ItemId,
            decision);
    }

    private void RecordRestrictedAgentDisplayDecision(
        OwnerSnapshot owner,
        string? threadId,
        string? turnId,
        string? itemId,
        AgentMessageDisplayDecision? decision)
    {
        if (decision is not { Mode: not AgentMessageDisplayMode.Show })
        {
            return;
        }

        threadId = string.IsNullOrWhiteSpace(threadId)
            ? SelectedThread?.Id ?? Status.ThreadId
            : threadId;
        RemoveSyntheticUserInputForAgentMessage(threadId, turnId, itemId);

        var scope = new AgentMessageDisplayScope(owner.StatePartitionFingerprint ?? string.Empty, owner.OwnerGeneration);
        PruneRestrictedAgentDisplayDecisionsToScope(scope);
        if (overflowedAgentDisplayDecisionScopes.Contains(scope))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            MarkAgentDisplayDecisionScopeOverflow(scope);
            return;
        }

        var key = new AgentMessageDisplayIdentity(
            owner.StatePartitionFingerprint ?? string.Empty,
            owner.OwnerGeneration,
            threadId,
            turnId ?? string.Empty,
            itemId ?? string.Empty);
        if (restrictedAgentDisplayDecisions.TryGetValue(key, out AgentMessageDisplayDecision? current))
        {
            decision = ResolveAgentMessageDisplayDecision(current, decision)!;
        }
        else if (restrictedAgentDisplayDecisions.Count >= MaximumRestrictedAgentDisplayDecisions)
        {
            MarkAgentDisplayDecisionScopeOverflow(scope);
            return;
        }

        restrictedAgentDisplayDecisions[key] = new AgentMessageDisplayDecision
        {
            Mode = decision.Mode,
            DisplayText = decision.DisplayText,
        };

        ApplyRestrictedAgentDisplayDecisionToVisibleItems(owner, threadId, turnId, itemId, decision);
        if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(itemId))
        {
            agentRawText.Clear();
            pendingAgentRawKeys.Clear();
            lastAgentRawKey = null;
        }
    }

    private void PruneRestrictedAgentDisplayDecisionsToScope(AgentMessageDisplayScope scope)
    {
        foreach (AgentMessageDisplayIdentity identity in restrictedAgentDisplayDecisions.Keys
            .Where(identity => !string.Equals(identity.StatePartitionFingerprint, scope.StatePartitionFingerprint, StringComparison.Ordinal)
                || identity.OwnerGeneration != scope.OwnerGeneration)
            .ToArray())
        {
            restrictedAgentDisplayDecisions.Remove(identity);
        }

        overflowedAgentDisplayDecisionScopes.RemoveWhere(existingScope => existingScope != scope);
    }

    private void ApplyRestrictedAgentDisplayDecisionToVisibleItems(
        OwnerSnapshot owner,
        string threadId,
        string? turnId,
        string? itemId,
        AgentMessageDisplayDecision decision)
    {
        foreach (ChatItemViewModel item in Items)
        {
            if (!string.Equals(item.Role, "Codex", StringComparison.Ordinal)
                || !string.Equals(item.StatePartitionFingerprint ?? string.Empty, owner.StatePartitionFingerprint ?? string.Empty, StringComparison.Ordinal)
                || item.OwnerGeneration != owner.OwnerGeneration
                || !string.Equals(item.ThreadId, threadId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(itemId))
            {
                if (!string.Equals(item.ItemId, itemId, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(turnId) && !string.Equals(item.TurnId, turnId, StringComparison.Ordinal)))
                {
                    continue;
                }
            }
            else if (!string.IsNullOrWhiteSpace(turnId)
                && !string.Equals(item.TurnId, turnId, StringComparison.Ordinal))
            {
                continue;
            }

            AgentMessageDisplayDecision effectiveDecision = ResolveAgentMessageDisplayDecision(
                item.AgentDisplayDecision,
                decision)!;
            item.AgentDisplayDecision = effectiveDecision;
            SafeMarkdownRenderResult rendered = markdown.ToSafeTextAndBlocks(
                effectiveDecision.GetDisplayText(item.Text) ?? string.Empty);
            SetTranscriptText(item, rendered.Text);
            if (item.UsesBlockRendering)
            {
                item.UpdateBlocks(rendered.Blocks);
            }

            item.IsTruncated = false;
            item.OverflowFile = null;
            item.ReplaceArtifactParts([]);
        }
    }

    private void RemoveSyntheticUserInputForAgentMessage(string? threadId, string? turnId, string? itemId)
    {
        string[] requestIds = PendingInteractions
            .Where(card => card.UserInput is
                {
                    IsSynthetic: true,
                    SourceAgentMessageThreadId: var sourceThreadId,
                    SourceAgentMessageTurnId: var sourceTurnId,
                    SourceAgentMessageItemId: var sourceItemId,
                }
                && (string.IsNullOrWhiteSpace(threadId)
                    || string.Equals(sourceThreadId, threadId, StringComparison.Ordinal))
                && (string.IsNullOrWhiteSpace(turnId)
                    || string.Equals(sourceTurnId, turnId, StringComparison.Ordinal))
                && (string.IsNullOrWhiteSpace(itemId)
                    || string.Equals(sourceItemId, itemId, StringComparison.Ordinal)))
            .Select(static card => card.RequestId)
            .ToArray();
        foreach (string requestId in requestIds)
        {
            RemoveUserInput(requestId);
        }
    }

    private void MarkAgentDisplayDecisionScopeOverflow(AgentMessageDisplayScope scope)
    {
        if (!overflowedAgentDisplayDecisionScopes.Add(scope))
        {
            return;
        }

        foreach (ChatItemViewModel item in Items)
        {
            if (!string.Equals(item.Role, "Codex", StringComparison.Ordinal)
                || !string.Equals(item.StatePartitionFingerprint ?? string.Empty, scope.StatePartitionFingerprint, StringComparison.Ordinal)
                || item.OwnerGeneration != scope.OwnerGeneration)
            {
                continue;
            }

            AgentMessageDisplayDecision decision = ResolveAgentMessageDisplayDecision(
                item.AgentDisplayDecision,
                new AgentMessageDisplayDecision { Mode = AgentMessageDisplayMode.Suppress })!;
            item.AgentDisplayDecision = decision;
            SafeMarkdownRenderResult rendered = markdown.ToSafeTextAndBlocks(decision.GetDisplayText(item.Text) ?? string.Empty);
            SetTranscriptText(item, rendered.Text);
            if (item.UsesBlockRendering)
            {
                item.UpdateBlocks(rendered.Blocks);
            }

            item.IsTruncated = false;
            item.OverflowFile = null;
            item.ReplaceArtifactParts([]);
        }

        ConnectionTargetSnapshot? target = Status.Target;
        if (scope.OwnerGeneration == (target?.OwnerGeneration ?? 0)
            && string.Equals(scope.StatePartitionFingerprint, target?.StatePartitionFingerprint ?? string.Empty, StringComparison.Ordinal))
        {
            agentRawText.Clear();
            pendingAgentRawKeys.Clear();
            lastAgentRawKey = null;
            string[] syntheticInputIds = PendingInteractions
                .Where(static card => card.UserInput?.IsSynthetic == true)
                .Select(static card => card.RequestId)
                .ToArray();
            foreach (string requestId in syntheticInputIds)
            {
                RemoveUserInput(requestId);
            }
        }
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
        OwnerSnapshot owner = NotificationOwner(notification);
        AgentMessageDisplayDecision? decision = ResolveAgentMessageDisplayDecision(
            existing?.AgentDisplayDecision,
            value.AgentMessageDisplayDecision);
        decision = ResolveAgentMessageDisplayDecision(
            decision,
            GetRestrictedAgentDisplayDecision(owner, value.ThreadId, value.TurnId, value.ItemId));
        bool restrictive = decision is { Mode: not AgentMessageDisplayMode.Show };
        if (!firstCompletion && !restrictive)
        {
            return;
        }

        if (!firstCompletion && existing is null)
        {
            RecordRestrictedAgentDisplayDecision(owner, value.ThreadId, value.TurnId, value.ItemId, decision);
            RemoveAgentRaw(agentRawKey);
            return;
        }

        if (existing?.IsHistoryCompleted == true)
        {
            bool historyMayDisplayParts = decision is null or { Mode: AgentMessageDisplayMode.Show };
            if (value.Text is not null || !historyMayDisplayParts)
            {
                string? historyDisplayText = decision is null ? value.Text : decision.GetDisplayText(value.Text);
                SafeMarkdownRenderResult historyRendered = markdown.ToSafeTextAndBlocks(historyDisplayText ?? string.Empty);
                SetTranscriptText(existing, historyRendered.Text);
                existing.UpdateBlocks(historyRendered.Blocks);
            }

            if (!historyMayDisplayParts || value.Parts.Count > 0)
            {
                ApplyAgentMessageParts(existing, value, notification, historyMayDisplayParts);
            }

            existing.AgentDisplayDecision = decision;
            existing.UsesBufferedAgentMessageDisplay = RequiresBufferedAgentMessageDisplay();
            RecordRestrictedAgentDisplayDecision(existing, decision);
            existing.IsTruncated = restrictive ? false : existing.IsTruncated || value.Truncated;
            existing.OverflowFile = restrictive ? null : value.OverflowFile ?? existing.OverflowFile;
            existing.ApplyTiming(value.StartedAtMs, value.CompletedAtMs);

            // Completion keys are bounded. If an old key was evicted, avoid appending an
            // already-approved body again when the completed transcript row is still present.
            bool hasRawText = agentRawText.TryGetValue(agentRawKey, out _);
            if (restrictive)
            {
                RemoveAgentRaw(agentRawKey);
            }
            else if (!IsAgentRawTurnConsumed(value)
                && (firstCompletion || pendingAgentRawKeys.Contains(agentRawKey) || !hasRawText))
            {
                RemoveAgentRaw(agentRawKey);
                AppendApprovedAgentRaw(agentRawKey, value, decision);
            }

            return;
        }

        RemoveAgentRaw(agentRawKey);

        bool mayDisplayParts = decision is null or { Mode: AgentMessageDisplayMode.Show };
        string? displayText = decision is null ? value.Text : decision.GetDisplayText(value.Text);
        if (string.IsNullOrEmpty(displayText) && (!mayDisplayParts || value.Parts.Count == 0))
        {
            return;
        }

        SafeMarkdownRenderResult rendered = markdown.ToSafeTextAndBlocks(displayText ?? string.Empty);
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
                AgentDisplayDecision = decision,
                UsesBufferedAgentMessageDisplay = RequiresBufferedAgentMessageDisplay(),
                HistoryKey = historyKey,
                IsHistoryCompleted = true,
                StatePartitionFingerprint = owner.StatePartitionFingerprint,
                OwnerGeneration = owner.OwnerGeneration,
                ConnectionGeneration = owner.ConnectionGeneration,
                IsTruncated = restrictive ? false : value.Truncated,
                OverflowFile = restrictive ? null : value.OverflowFile,
            };
            item.ApplyTiming(value.StartedAtMs, value.CompletedAtMs);
            item.UpdateBlocks(rendered.Blocks);
            ApplyAgentMessageParts(item, value, notification, mayDisplayParts);
            Items.Add(item);
            RecordRestrictedAgentDisplayDecision(item, decision);
        }
        else
        {
            SetTranscriptText(existing, rendered.Text);
            existing.UpdateBlocks(rendered.Blocks);
            ApplyAgentMessageParts(existing, value, notification, mayDisplayParts);
            existing.AgentDisplayDecision = decision;
            existing.UsesBufferedAgentMessageDisplay = RequiresBufferedAgentMessageDisplay();
            RecordRestrictedAgentDisplayDecision(existing, decision);
            existing.HistoryKey = historyKey;
            existing.IsHistoryCompleted = true;
            existing.IsTruncated = restrictive ? false : existing.IsTruncated || value.Truncated;
            existing.OverflowFile = restrictive ? null : value.OverflowFile ?? existing.OverflowFile;
            existing.ApplyTiming(value.StartedAtMs, value.CompletedAtMs);
        }

        AppendApprovedAgentRaw(agentRawKey, value, decision);

        TrimHistoryWindow();
    }

    private void AppendApprovedAgentRaw(
        string key,
        ConversationEvent value,
        AgentMessageDisplayDecision? decision)
    {
        if (IsAgentRawTurnConsumed(value))
        {
            // Late/replayed completion can still render in the transcript, but a terminal turn
            // has already spent its choice-detection window.
            return;
        }

        if (decision is null or { Mode: AgentMessageDisplayMode.Show })
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
        => GetAgentRawTurnKey(value.ThreadId, value.TurnId);

    private static string? GetAgentRawTurnKey(string? threadId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return null;
        }

        return string.Concat(threadId.Length, ":", threadId, turnId.Length, ":", turnId);
    }

    private static string GetAgentCompletionKey(ConversationEvent value)
        => GetAgentCompletionKey(value.ThreadId, value.TurnId, value.ItemId);

    private static string GetAgentCompletionKey(string? threadId, string? turnId, string? itemId)
    {
        threadId ??= string.Empty;
        turnId ??= string.Empty;
        itemId ??= string.Empty;
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
