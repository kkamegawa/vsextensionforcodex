using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class StreamingBufferTests
{
    [TestMethod]
    public async Task Burst_IsBatchedAndOverflowIsWritten()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CodexVsTests", Guid.NewGuid().ToString("N"));
        var events = new List<ConversationEvent>();
        await using var buffer = new StreamingBuffer(
            (value, _) =>
            {
                lock (events)
                {
                    events.Add(value);
                }

                return Task.CompletedTask;
            },
            directory);
        var template = new ConversationEvent
        {
            Kind = ConversationEventKind.CommandOutputDelta,
            ItemId = "command-1",
        };

        for (int i = 0; i < 1_000; i++)
        {
            buffer.Append("command-1", template, "x", 100);
        }

        await WaitUntilAsync(() => CountEvents(events) >= 1, TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, events.Count);
        ConversationEvent result = events[0];
        Assert.AreEqual(100, result.Text?.Length);
        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(File.Exists(result.OverflowFile));
    }

    [TestMethod]
    public async Task MemoryBound_ReasoningLimitTriggersOverflow()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CodexVsTests", Guid.NewGuid().ToString("N"));
        var events = new List<ConversationEvent>();
        await using var buffer = new StreamingBuffer(
            (value, _) =>
            {
                lock (events)
                {
                    events.Add(value);
                }

                return Task.CompletedTask;
            },
            directory);

        var template = new ConversationEvent
        {
            Kind = ConversationEventKind.ReasoningSummaryDelta,
            ItemId = "reasoning-1",
        };

        // Exceed ReasoningLimit (512 KB) by appending 1 KB chunks 600 times.
        string chunk = new('r', 1024);
        for (int i = 0; i < 600; i++)
        {
            buffer.Append("reasoning-1", template, chunk, StreamingBuffer.ReasoningLimit);
        }

        await WaitUntilAsync(() => CountEvents(events) >= 1, TimeSpan.FromSeconds(2));
        Assert.IsTrue(events.Count >= 1, "Expected at least one emitted event.");
        ConversationEvent last = events[^1];
        Assert.IsTrue(last.Truncated, "Expected Truncated = true after exceeding memory limit.");
        Assert.IsNotNull(last.OverflowFile, "Expected an overflow file path.");
        Assert.IsTrue(File.Exists(last.OverflowFile), "Overflow file must exist on disk.");
    }

    [TestMethod]
    public async Task MultipleSources_TrackedIndependently()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CodexVsTests", Guid.NewGuid().ToString("N"));
        var events = new List<ConversationEvent>();
        await using var buffer = new StreamingBuffer(
            (value, _) =>
            {
                lock (events)
                {
                    events.Add(value);
                }

                return Task.CompletedTask;
            },
            directory);

        var templateA = new ConversationEvent { Kind = ConversationEventKind.AgentMessageDelta, ItemId = "item-a" };
        var templateB = new ConversationEvent { Kind = ConversationEventKind.CommandOutputDelta, ItemId = "item-b" };

        buffer.Append("item-a", templateA, "hello ", StreamingBuffer.CommandOutputLimit);
        buffer.Append("item-b", templateB, "world", StreamingBuffer.CommandOutputLimit);

        await WaitUntilAsync(() => CountEvents(events) >= 2, TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, events.Count);
        Assert.IsTrue(events.Any(e => e.ItemId == "item-a"));
        Assert.IsTrue(events.Any(e => e.ItemId == "item-b"));
    }

    private static int CountEvents(List<ConversationEvent> events)
    {
        lock (events)
        {
            return events.Count;
        }
    }

    // Polls instead of a fixed sleep: the StreamingBuffer flushes on a 75ms PeriodicTimer, so a
    // fixed delay races the timer under CI load. Returns as soon as the condition is met.
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Condition not met within {timeout}.");
            }

            await Task.Delay(25);
        }
    }
}
