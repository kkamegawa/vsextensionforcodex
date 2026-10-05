using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Codex.AppServer.Protocol;

namespace Codex.VisualStudio.Worker;

internal readonly record struct PendingInteractionKey(long Generation, string RequestId);

internal enum PendingInteractionCompletionKind
{
    Respond,
    TimedOut,
    ExternallyResolved,
    GenerationRetired,
}

internal readonly record struct PendingInteractionResult(
    PendingInteractionCompletionKind Kind,
    JsonElement Response)
{
    public bool ShouldSendResponse => Kind is PendingInteractionCompletionKind.Respond or PendingInteractionCompletionKind.TimedOut;
}

/// <summary>
/// One app-server request and its Worker-only wire data. The remote UI receives only the
/// unpredictable <see cref="InteractionId"/>, never the upstream JSON-RPC id or raw parameters.
/// </summary>
internal sealed class PendingInteractionEntry
{
    private ITimer? timeoutTimer;
    private readonly TaskCompletionSource<PendingInteractionResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingInteractionEntry(
        PendingInteractionKey key,
        string method,
        JsonElement originalRequestId,
        JsonElement parameters,
        JsonElement timeoutResponse)
    {
        Key = key;
        Method = method;
        OriginalRequestId = originalRequestId.Clone();
        Parameters = parameters.Clone();
        TimeoutResponse = timeoutResponse.Clone();
        InteractionId = Guid.NewGuid().ToString("N");
    }

    public PendingInteractionKey Key { get; }

    public string InteractionId { get; }

    public string Method { get; }

    public JsonElement OriginalRequestId { get; }

    public JsonElement Parameters { get; }

    public Task<PendingInteractionResult> Completion => completion.Task;

    internal JsonElement TimeoutResponse { get; }

    internal bool TrySetResult(PendingInteractionResult result) => completion.TrySetResult(result);

    internal void SetTimer(ITimer timer) => timeoutTimer = timer;

    internal bool ChangeTimer(TimeSpan dueTime)
    {
        try
        {
            return Volatile.Read(ref timeoutTimer)?.Change(dueTime, Timeout.InfiniteTimeSpan) ?? false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    internal void StopTimer() => Interlocked.Exchange(ref timeoutTimer, null)?.Dispose();
}

/// <summary>
/// Generation-scoped, at-most-once registry shared by all app-server interaction kinds.
/// Validators inspect a snapshot first; completion then atomically claims that exact entry.
/// </summary>
internal sealed class PendingInteractionRegistry : IDisposable
{
    private const int MaximumOutstanding = 128;
    private const int MaximumPayloadBytes = 1024 * 1024;
    private const int MaximumRecentlyCompleted = 1024;
    private const int MaximumRecentlyCompletedQueueEntries = MaximumRecentlyCompleted * 2;
    private readonly ConcurrentDictionary<PendingInteractionKey, PendingInteractionEntry> pending = new();
    private readonly ConcurrentDictionary<(long Generation, string InteractionId), PendingInteractionEntry> byInteractionId = new();
    private readonly ConcurrentDictionary<PendingInteractionKey, byte> externallyResolvedBeforeRegistration = new();
    private readonly Dictionary<PendingInteractionKey, long> recentlyCompleted = new();
    private readonly Queue<(PendingInteractionKey Key, long Version)> recentlyCompletedOrder = new();
    private readonly object registrationGate = new();
    private readonly TimeProvider timeProvider;
    private int disposed;
    private long retiredGenerationCutoff;
    private long completedVersion;

    public PendingInteractionRegistry(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryAdd(
        PendingInteractionKey key,
        string method,
        JsonElement originalRequestId,
        JsonElement parameters,
        TimeSpan timeout,
        JsonElement timeoutResponse,
        out PendingInteractionEntry entry)
    {
        entry = null!;
        if (key.Generation <= 0
            || Volatile.Read(ref disposed) != 0
            || string.IsNullOrWhiteSpace(method)
            || timeout <= TimeSpan.Zero
            || Encoding.UTF8.GetByteCount(originalRequestId.GetRawText()) > 256
            || Encoding.UTF8.GetByteCount(parameters.GetRawText()) > MaximumPayloadBytes
            || Encoding.UTF8.GetByteCount(timeoutResponse.GetRawText()) > MaximumPayloadBytes
            || !JsonRpcRequestId.TryGetKey(originalRequestId, out string canonicalId)
            || !string.Equals(canonicalId, key.RequestId, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = new PendingInteractionEntry(key, method, originalRequestId, parameters, timeoutResponse);
        lock (registrationGate)
        {
            if (Volatile.Read(ref disposed) != 0
                || key.Generation <= retiredGenerationCutoff
                || pending.Count >= MaximumOutstanding)
            {
                return false;
            }

            if (externallyResolvedBeforeRegistration.TryRemove(key, out _))
            {
                candidate.TrySetResult(new PendingInteractionResult(PendingInteractionCompletionKind.ExternallyResolved, default));
                RememberRecentlyCompleted(key);
                entry = candidate;
                return true;
            }

            // A JSON-RPC ID may be reused after its prior request completes. Clear only the
            // completion marker for this key before publishing the new registration.
            recentlyCompleted.Remove(key);

            candidate.SetTimer(timeProvider.CreateTimer(
                static state =>
                {
                    var tuple = ((PendingInteractionRegistry Registry, PendingInteractionEntry Entry))state!;
                    tuple.Registry.TryExpire(tuple.Entry);
                },
                (this, candidate),
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan));

            // Publish the UI alias before the upstream-key entry. The gate prevents external
            // resolution or generation retirement from observing the intermediate state.
            if (!byInteractionId.TryAdd((key.Generation, candidate.InteractionId), candidate))
            {
                candidate.StopTimer();
                return false;
            }

            if (!pending.TryAdd(key, candidate))
            {
                byInteractionId.TryRemove(new KeyValuePair<(long Generation, string InteractionId), PendingInteractionEntry>(
                    (key.Generation, candidate.InteractionId),
                    candidate));
                candidate.StopTimer();
                return false;
            }

            entry = candidate;
            if (!candidate.ChangeTimer(timeout))
            {
                TryComplete(key, candidate, candidate.TimeoutResponse, PendingInteractionCompletionKind.TimedOut);
            }

            return true;
        }
    }

    public bool TryGet(PendingInteractionKey key, out PendingInteractionEntry entry)
        => pending.TryGetValue(key, out entry!);

    public bool TryGetByInteractionId(long generation, string interactionId, out PendingInteractionEntry entry)
    {
        if (generation <= 0 || string.IsNullOrWhiteSpace(interactionId))
        {
            entry = null!;
            return false;
        }

        return byInteractionId.TryGetValue((generation, interactionId), out entry!);
    }

    public bool TryComplete(
        PendingInteractionKey key,
        PendingInteractionEntry expectedEntry,
        JsonElement response,
        PendingInteractionCompletionKind kind = PendingInteractionCompletionKind.Respond)
    {
        if (kind is not (PendingInteractionCompletionKind.Respond or PendingInteractionCompletionKind.TimedOut)
            || Encoding.UTF8.GetByteCount(response.GetRawText()) > MaximumPayloadBytes)
        {
            return false;
        }

        lock (registrationGate)
        {
            if (!TryRemoveExact(key, expectedEntry))
            {
                return false;
            }

            externallyResolvedBeforeRegistration.TryRemove(key, out _);
            RememberRecentlyCompleted(key);
        }

        expectedEntry.TrySetResult(new PendingInteractionResult(kind, response.Clone()));
        return true;
    }

    public bool TryResolveExternally(PendingInteractionKey key, PendingInteractionEntry? expectedEntry = null)
    {
        lock (registrationGate)
        {
            if (expectedEntry is null && !pending.TryGetValue(key, out expectedEntry))
            {
                if (Volatile.Read(ref disposed) == 0
                    && key.Generation > retiredGenerationCutoff
                    && !recentlyCompleted.ContainsKey(key)
                    && externallyResolvedBeforeRegistration.Count < 1024)
                {
                    externallyResolvedBeforeRegistration.TryAdd(key, 0);
                }

                return false;
            }

            if (!TryRemoveExact(key, expectedEntry))
            {
                return false;
            }

            expectedEntry.TrySetResult(new PendingInteractionResult(
                PendingInteractionCompletionKind.ExternallyResolved,
                default));
            RememberRecentlyCompleted(key);
            return true;
        }
    }

    /// <summary>
    /// Records that a server request finished its handler. Requests answered without ever being
    /// registered would otherwise leave an external-resolution marker that is never consumed.
    /// </summary>
    public void MarkHandled(PendingInteractionKey key)
    {
        lock (registrationGate)
        {
            if (Volatile.Read(ref disposed) != 0
                || key.Generation <= retiredGenerationCutoff
                || pending.ContainsKey(key))
            {
                return;
            }

            externallyResolvedBeforeRegistration.TryRemove(key, out _);
            if (!recentlyCompleted.ContainsKey(key))
            {
                RememberRecentlyCompleted(key);
            }
        }
    }

    public void RetireGeneration(long generation)
    {
        lock (registrationGate)
        {
            retiredGenerationCutoff = Math.Max(retiredGenerationCutoff, generation);
            foreach (PendingInteractionKey key in externallyResolvedBeforeRegistration.Keys)
            {
                if (key.Generation == generation)
                {
                    externallyResolvedBeforeRegistration.TryRemove(key, out _);
                }
            }

            foreach (PendingInteractionKey key in recentlyCompleted.Keys.ToArray())
            {
                if (key.Generation == generation)
                {
                    recentlyCompleted.Remove(key);
                }
            }

            foreach ((PendingInteractionKey key, PendingInteractionEntry entry) in pending)
            {
                if (key.Generation == generation && TryRemoveExact(key, entry))
                {
                    entry.TrySetResult(new PendingInteractionResult(PendingInteractionCompletionKind.GenerationRetired, default));
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (registrationGate)
        {
            externallyResolvedBeforeRegistration.Clear();
            recentlyCompleted.Clear();
            recentlyCompletedOrder.Clear();
            retiredGenerationCutoff = long.MaxValue;
            foreach ((PendingInteractionKey key, PendingInteractionEntry entry) in pending)
            {
                if (TryRemoveExact(key, entry))
                {
                    entry.TrySetResult(new PendingInteractionResult(PendingInteractionCompletionKind.GenerationRetired, default));
                }
            }
        }
    }

    private bool TryExpire(PendingInteractionEntry entry)
        => TryComplete(
            entry.Key,
            entry,
            entry.TimeoutResponse,
            PendingInteractionCompletionKind.TimedOut);

    private void RememberRecentlyCompleted(PendingInteractionKey key)
    {
        long version = ++completedVersion;
        recentlyCompleted[key] = version;
        recentlyCompletedOrder.Enqueue((key, version));
        while (recentlyCompleted.Count > MaximumRecentlyCompleted
            || recentlyCompletedOrder.Count > MaximumRecentlyCompletedQueueEntries)
        {
            (PendingInteractionKey expiredKey, long expiredVersion) = recentlyCompletedOrder.Dequeue();
            if (recentlyCompleted.TryGetValue(expiredKey, out long currentVersion)
                && currentVersion == expiredVersion)
            {
                recentlyCompleted.Remove(expiredKey);
            }
        }
    }

    private bool TryRemoveExact(PendingInteractionKey key, PendingInteractionEntry expectedEntry)
    {
        if (!pending.TryRemove(new KeyValuePair<PendingInteractionKey, PendingInteractionEntry>(key, expectedEntry)))
        {
            return false;
        }

        byInteractionId.TryRemove(new KeyValuePair<(long Generation, string InteractionId), PendingInteractionEntry>(
            (key.Generation, expectedEntry.InteractionId),
            expectedEntry));
        expectedEntry.StopTimer();
        return true;
    }
}
