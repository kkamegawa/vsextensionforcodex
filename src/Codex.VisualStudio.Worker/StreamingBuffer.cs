using System.Collections.Concurrent;
using System.Text;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed class StreamingBuffer : IAsyncDisposable
{
    public const int ReasoningLimit = 512 * 1024;
    public const int CommandOutputLimit = 2 * 1024 * 1024;
    public const int DiffLimit = 5 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly Func<ConversationEvent, CancellationToken, Task> sink;
    private readonly string overflowDirectory;
    private readonly PeriodicTimer timer = new(TimeSpan.FromMilliseconds(75));
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task flushLoop;

    public StreamingBuffer(Func<ConversationEvent, CancellationToken, Task> sink, string overflowDirectory)
    {
        this.sink = sink;
        this.overflowDirectory = overflowDirectory;
        Directory.CreateDirectory(overflowDirectory);
        flushLoop = Task.Run(() => FlushLoopAsync(lifetime.Token));
    }

    public void Append(string key, ConversationEvent template, string text, int limit)
    {
        Entry entry = entries.GetOrAdd(key, _ => new Entry(template, limit));
        lock (entry)
        {
            int remaining = Math.Max(0, entry.Limit - entry.VisibleWritten);
            if (remaining > 0)
            {
                int visibleLength = Math.Min(remaining, text.Length);
                entry.Visible.Append(text.AsSpan(0, visibleLength));
                entry.VisibleWritten += visibleLength;
            }

            if (text.Length > remaining)
            {
                entry.Overflow.Append(text.AsSpan(remaining));
                entry.Template.Truncated = true;
            }

            entry.Dirty = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        timer.Dispose();
        try
        {
            await flushLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lifetime.Dispose();
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach ((string key, Entry entry) in entries)
            {
                ConversationEvent? outgoing = null;
                string? overflow = null;
                lock (entry)
                {
                    if (!entry.Dirty)
                    {
                        continue;
                    }

                    outgoing = Clone(entry.Template);
                    outgoing.Text = entry.Visible.ToString();
                    entry.Visible.Clear();
                    entry.Dirty = false;
                    if (entry.Overflow.Length > 0)
                    {
                        overflow = entry.Overflow.ToString();
                        entry.Overflow.Clear();
                    }
                }

                if (overflow is not null)
                {
                    string path = Path.Combine(overflowDirectory, SanitizeFileName(key) + ".log");
                    await File.AppendAllTextAsync(path, overflow, cancellationToken).ConfigureAwait(false);
                    outgoing.OverflowFile = path;
                }

                await sink(outgoing, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ConversationEvent Clone(ConversationEvent value) => new()
    {
        Kind = value.Kind,
        ThreadId = value.ThreadId,
        TurnId = value.TurnId,
        ItemId = value.ItemId,
        PayloadJson = value.PayloadJson,
        Truncated = value.Truncated,
        OverflowFile = value.OverflowFile,
    };

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }

    private sealed class Entry
    {
        public Entry(ConversationEvent template, int limit)
        {
            Template = template;
            Limit = limit;
        }

        public ConversationEvent Template { get; }

        public int Limit { get; }

        public StringBuilder Visible { get; } = new();

        public StringBuilder Overflow { get; } = new();

        public int VisibleWritten { get; set; }

        public bool Dirty { get; set; }
    }
}
