namespace VoiceJournal.Tests.TestSupport;

/// <summary>Deterministic clock: monotonic timestamps and UTC time only move when the test advances them.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcStart;
    private long _ticks = TimeSpan.TicksPerSecond * 1000; // never 0, like a real QPC value

    public ManualTimeProvider(DateTimeOffset? utcStart = null) => _utcStart = utcStart ?? new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => _utcStart + TimeSpan.FromTicks(Interlocked.Read(ref _ticks) - TimeSpan.TicksPerSecond * 1000);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
