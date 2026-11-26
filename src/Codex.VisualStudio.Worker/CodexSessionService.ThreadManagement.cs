using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class CodexSessionService
{
    private const int MaximumRememberedSearchRequestIds = 512;
    private readonly object searchRequestIdGate = new();
    private readonly HashSet<(long ConnectionGeneration, string RequestId)> usedSearchRequestIds = [];
    private readonly Queue<(long ConnectionGeneration, string RequestId)> searchRequestIdOrder = new();
    private readonly HashSet<(long ConnectionGeneration, string ThreadId)> threadMutationsInFlight = [];
    private readonly HashSet<(long ConnectionGeneration, string ThreadId)> knownBusyThreadIds = [];

    private void ReserveThreadMutation(ConnectionContext context, string threadId)
    {
        lock (turnStateLock)
        {
            if (!IsCurrent(context))
            {
                throw new InvalidOperationException("The connection changed before the conversation could be checked.");
            }

            var key = (context.Generation, threadId);
            if (IsThreadTurnBusyLocked(context.Generation, threadId) || !threadMutationsInFlight.Add(key))
            {
                throw new InvalidOperationException("A turn or conversation operation is already running for this conversation.");
            }
        }
    }

    private void ReleaseThreadMutation(ConnectionContext context, string threadId)
    {
        lock (turnStateLock)
        {
            threadMutationsInFlight.Remove((context.Generation, threadId));
        }
    }

    private bool IsThreadTurnBusyLocked(long generation, string threadId)
        => (ActiveTurnId is not null && string.Equals(ActiveThreadId, threadId, StringComparison.Ordinal))
            || string.Equals(pendingTurnThreadId, threadId, StringComparison.Ordinal)
            || string.Equals(unconfirmedTurnThreadId, threadId, StringComparison.Ordinal)
            || knownBusyThreadIds.Contains((generation, threadId));

    private async Task EnsureThreadIdleAsync(ConnectionContext context, string threadId, CancellationToken cancellationToken)
    {
        EnsureCurrent(context);
        EnsureCredentialReady(context);
        JsonElement response;
        try
        {
            response = await context.Connection.SendReadOnlyRequestAsync(
                "thread/turns/list",
                new { threadId, cursor = (string?)null, limit = 50, itemsView = "summary", sortDirection = "desc" },
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The conversation's turn state could not be verified, so delete and revert are unavailable.", exception);
        }

        EnsureCurrent(context);
        JsonElement turns = RequireArray(response, "data");
        bool busy = turns.EnumerateArray().Any(turn => IsBusyTurnStatus(GetBoundedString(turn, "status", 64)));
        if (busy)
        {
            throw new InvalidOperationException("A turn is running; delete and revert are unavailable for this conversation.");
        }
    }

    private static bool IsBusyTurnStatus(string? status)
        => status is not null
            && (status.Equals("inProgress", StringComparison.OrdinalIgnoreCase)
                || status.Equals("in_progress", StringComparison.OrdinalIgnoreCase)
                || status.Equals("running", StringComparison.OrdinalIgnoreCase)
                || status.Equals("pending", StringComparison.OrdinalIgnoreCase)
                || status.Equals("cancelling", StringComparison.OrdinalIgnoreCase));
    public async Task<ThreadSectionsResult> ListThreadSectionsAsync(CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireContext();
        var sections = new List<ThreadSectionInfo>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int page = 0; page < 50; page++)
        {
            JsonElement response = await SendReadOnlyAsync("threadSection/list", new { cursor, limit = 100 }, cancellationToken).ConfigureAwait(false);
            if (response.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            {
                if (data.GetArrayLength() > 100)
                {
                    throw InvalidHistoryResponse();
                }

                foreach (JsonElement value in data.EnumerateArray())
                {
                    string? id = GetBoundedString(value, "id", 256);
                    string? name = GetBoundedString(value, "name", 512);
                    if (id is not null && name is not null) sections.Add(new ThreadSectionInfo { Id = id, Name = name });
                }
            }

            if (!response.TryGetProperty("nextCursor", out JsonElement nextCursor) || nextCursor.ValueKind == JsonValueKind.Null)
            {
                cursor = null;
            }
            else if (nextCursor.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nextCursor.GetString())
                || Encoding.UTF8.GetByteCount(nextCursor.GetString()!) > 4096)
            {
                throw InvalidHistoryResponse();
            }
            else
            {
                cursor = nextCursor.GetString();
            }

            if (cursor is null) break;
            if (!seenCursors.Add(cursor)) throw InvalidHistoryResponse();
            if (page == 49) throw InvalidHistoryResponse();
        }

        EnsureCurrent(context);
        return new ThreadSectionsResult { Sections = sections };
    }

    public Task<ThreadMutationResult> ArchiveThreadAsync(ThreadMutationRequest request, CancellationToken cancellationToken)
        => MutateThreadAsync("thread/archive", request.ThreadId, null, request.UserConfirmed, "archive", cancellationToken);

    public Task<ThreadMutationResult> UnarchiveThreadAsync(ThreadMutationRequest request, CancellationToken cancellationToken)
        => MutateThreadAsync("thread/unarchive", request.ThreadId, null, request.UserConfirmed, "unarchive", cancellationToken);

    public Task<ThreadMutationResult> DeleteThreadAsync(ThreadMutationRequest request, CancellationToken cancellationToken)
        => MutateThreadAsync("thread/delete", request.ThreadId, null, request.UserConfirmed, "delete", cancellationToken);

    public Task<ThreadMutationResult> RevertThreadAsync(ThreadMutationRequest request, CancellationToken cancellationToken)
        => MutateThreadAsync("thread/revert", request.ThreadId, request.BeforeTurnId, request.UserConfirmed, "revert", cancellationToken);

    private async Task<ThreadMutationResult> MutateThreadAsync(
        string method, string threadId, string? beforeTurnId, bool userConfirmed, string action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (action is "delete" or "revert" && !userConfirmed)
        {
            throw new InvalidOperationException("Thread changes require explicit user confirmation.");
        }
        if (action == "revert") ArgumentException.ThrowIfNullOrWhiteSpace(beforeTurnId);
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        EnsureCredentialReady(context);
        ApprovalPolicyResult policy = approvalPolicy.EvaluateThreadMutation(threadId, action);
        if (policy.IsBlocked) throw new InvalidOperationException(policy.BlockReason ?? "Thread operation is blocked by policy.");

        bool requiresIdleThread = action is "delete" or "revert";
        if (requiresIdleThread)
        {
            ReserveThreadMutation(context, threadId);
        }

        bool mutationDispatched = false;
        try
        {
            if (requiresIdleThread)
            {
                await EnsureThreadIdleAsync(context, threadId, cancellationToken).ConfigureAwait(false);
                lock (turnStateLock)
                {
                    if (IsThreadTurnBusyLocked(context.Generation, threadId))
                    {
                        throw new InvalidOperationException("A turn is running; delete and revert are unavailable for this conversation.");
                    }
                }
            }

            EnsureCurrent(context);
            EnsureCredentialReady(context);
            // A failure after dispatch cannot establish whether the server applied the mutation.
            cancellationToken.ThrowIfCancellationRequested();
            mutationDispatched = true;
            await context.Connection.SendRequestAsync(method,
                action == "revert" ? new { threadId, beforeTurnId } : new { threadId },
                TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException exception)
        {
            if (!IsCurrent(context)) return new ThreadMutationResult { OutcomeUnknown = true };
            return new ThreadMutationResult { Succeeded = false, Message = BoundRedacted(exception.Message, 1024) };
        }
        catch (Exception exception)
        {
            return new ThreadMutationResult
            {
                OutcomeUnknown = mutationDispatched,
                Message = IsCurrent(context) ? BoundRedacted(exception.Message, 1024) : null,
            };
        }
        finally
        {
            if (requiresIdleThread)
            {
                ReleaseThreadMutation(context, threadId);
            }
        }

        if (!IsCurrent(context)) return new ThreadMutationResult { OutcomeUnknown = true };
        return new ThreadMutationResult { Succeeded = true };
    }

    public async Task<ThreadSearchResult> SearchThreadsAsync(ThreadSearchRequest request, CancellationToken cancellationToken)
    {
        string term = request.SearchTerm?.Trim() ?? string.Empty;
        if (term.Length is < 1 or > 512) throw new ArgumentException("Search text must contain 1 to 512 characters.", nameof(request));
        ConnectionContext context = RequireContext();
        EnsureCurrent(context);
        EnsureCredentialReady(context);
        if (!Guid.TryParseExact(request.RequestId, "N", out Guid parsedRequestId) || parsedRequestId == Guid.Empty)
        {
            throw new ArgumentException("Search request IDs must be nonempty GUIDs in N format.", nameof(request));
        }
        string? cursor = request.Cursor is null ? null : ValidateCursor(request.Cursor);
        string requestId = parsedRequestId.ToString("N");
        if (!context.ExperimentalApi)
        {
            EnsureCurrent(context);
            return new ThreadSearchResult { IsSupported = false };
        }
        bool streamSearch = context.FeatureTier >= CodexFeatureTier.Tier171;
        RememberUniqueSearchRequestId(context.Generation, requestId);
        var parameters = new Dictionary<string, object?>
        {
            ["searchTerm"] = term,
            ["archived"] = request.Archived,
            ["limit"] = 50,
            ["sortKey"] = "updated_at",
            ["sortDirection"] = "desc",
        };
        if (cursor is not null)
        {
            parameters["cursor"] = cursor;
        }
        JsonElement response;
        try
        {
            if (streamSearch)
            {
                Func<JsonRpcMessage, CancellationToken, Task> handler = async (message, token) =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref connectionContext), context) || message.Method != "thread/search/partial"
                        || message.Params is not { ValueKind: JsonValueKind.Object } payload
                        || !TryGetSearchRequestId(payload, out string? partialRequestId)
                        || !string.Equals(partialRequestId, requestId, StringComparison.Ordinal)
                        || !payload.TryGetProperty("data", out JsonElement partialData)
                        || partialData.ValueKind != JsonValueKind.Array)
                    {
                        return;
                    }

                    ThreadSearchHit[] hits = partialData.EnumerateArray().Take(50).Select(ReadThreadSearchHit).Where(static hit => hit is not null).Cast<ThreadSearchHit>().ToArray();
                    Func<ThreadSearchPartialEvent, CancellationToken, Task>? handlers = ThreadSearchPartial;
                    if (handlers is null) return;
                    var args = new ThreadSearchPartialEvent
                    {
                        RequestId = requestId,
                        Results = hits,
                        StatePartitionFingerprint = context.StatePartitionFingerprint,
                        OwnerGeneration = context.OwnerGeneration,
                        ConnectionGeneration = context.Generation,
                    };
                    foreach (Func<ThreadSearchPartialEvent, CancellationToken, Task> subscriber in handlers.GetInvocationList().Cast<Func<ThreadSearchPartialEvent, CancellationToken, Task>>())
                    {
                        await subscriber(args, token).ConfigureAwait(false);
                    }
                };
                context.Connection.NotificationReceived += handler;
                try
                {
                    response = await context.Connection.SendCorrelatedRequestAsync(
                        "thread/search/stream", parameters, requestId, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not JsonRpcRemoteException)
                {
                    try
                    {
                        await context.Connection.SendRequestAsync(
                            "thread/search/stream/cancel",
                            new { requestId },
                            TimeSpan.FromSeconds(5),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception cancelException)
                    {
                        WorkerDiagnostics.Write("thread/search/stream cancellation outcome unknown", cancelException);
                    }

                    throw;
                }
                finally
                {
                    context.Connection.NotificationReceived -= handler;
                }
            }
            else
            {
                response = await SendReadOnlyAsync("thread/search", parameters, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonRpcRemoteException exception) when (exception.Code == -32601)
        {
            EnsureCurrent(context);
            return new ThreadSearchResult { IsSupported = false };
        }
        var hits = new List<ThreadSearchHit>();
        if (response.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in data.EnumerateArray().Take(50))
            {
                ThreadSearchHit? hit = ReadThreadSearchHit(value);
                if (hit is not null) hits.Add(hit);
            }
        }
        EnsureCurrent(context);
        return new ThreadSearchResult { Results = hits, NextCursor = ReadOptionalCursor(response) };
    }

    private string BoundRedacted(string value, int maxChars)
    {
        string redacted = redactor.Redact(value);
        return redacted.Length <= maxChars ? redacted : redacted[..maxChars];
    }

    private void RememberUniqueSearchRequestId(long connectionGeneration, string requestId)
    {
        lock (searchRequestIdGate)
        {
            var key = (connectionGeneration, requestId);
            if (!usedSearchRequestIds.Add(key))
            {
                throw new InvalidOperationException("A thread search request ID cannot be reused on the same connection.");
            }

            searchRequestIdOrder.Enqueue(key);
            while (searchRequestIdOrder.Count > MaximumRememberedSearchRequestIds)
            {
                usedSearchRequestIds.Remove(searchRequestIdOrder.Dequeue());
            }
        }
    }
    private ThreadSearchHit? ReadThreadSearchHit(JsonElement value)
    {
        if (!value.TryGetProperty("thread", out JsonElement thread) || thread.ValueKind != JsonValueKind.Object) return null;
        ThreadSummary summary = ReadThread(thread);
        summary.SearchSnippet = GetBoundedString(value, "snippet", 4096);
        return new ThreadSearchHit { Thread = summary, Snippet = summary.SearchSnippet ?? string.Empty };
    }

    private static bool TryGetSearchRequestId(JsonElement payload, out string? requestId)
    {
        requestId = null;
        if (!payload.TryGetProperty("requestId", out JsonElement value)) return false;
        requestId = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out long number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
        return requestId is not null;
    }
}
