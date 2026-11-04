using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public partial interface ICodexSessionService
{
    Task<ThreadMemoryResult> ReadThreadMemoryAsync(string threadId, CancellationToken cancellationToken);
}

public sealed partial class CodexSessionService
{
    private const int MaximumThreadMemoryCharacters = 64 * 1024;
    public async Task<ThreadMemoryResult> ReadThreadMemoryAsync(string threadId, CancellationToken cancellationToken)
    {
        ValidateHistoryId(threadId, nameof(threadId));
        ConnectionContext context = RequireContext();
        if (context.FeatureTier < CodexFeatureTier.Tier165 || !context.ExperimentalApi)
        {
            return new ThreadMemoryResult
            {
                IsSupported = false,
                UnavailableReason = "Conversation memory requires Codex CLI 0.165.0 or later with the Experimental API enabled.",
                ThreadId = threadId,
            };
        }

        if (!string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal))
        {
            return new ThreadMemoryResult
            {
                IsSupported = false,
                UnavailableReason = "Join the conversation before reading its memory.",
                ThreadId = threadId,
            };
        }

        JsonElement response = await SendReadOnlyAsync(
            "thread/memory/read",
            new { threadId },
            cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);

        string? summary = response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("summary", out JsonElement summaryElement)
            && summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString()
                : null;
        if (summary is { Length: > MaximumThreadMemoryCharacters })
        {
            summary = string.Concat(summary.AsSpan(0, MaximumThreadMemoryCharacters), Environment.NewLine, "[Memory summary truncated]");
        }
        return new ThreadMemoryResult
        {
            ThreadId = threadId,
            Summary = summary,
        };
    }
}