namespace Codex.VisualStudio.Ui.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private DateTimeOffset utcNow = initialUtcNow;
    private long timestamp = initialUtcNow.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override long GetTimestamp() => timestamp;

    public void Advance(TimeSpan duration)
    {
        utcNow += duration;
        timestamp += duration.Ticks;
    }
}
