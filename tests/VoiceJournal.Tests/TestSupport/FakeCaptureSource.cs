using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;

namespace VoiceJournal.Tests.TestSupport;

/// <summary>Capture source driven by the test: packets are pushed on the test thread like a capture callback.</summary>
public sealed class FakeCaptureSource(AudioSource source, AudioFormatInfo format, string deviceName = "Fake device") : IAudioCaptureSource
{
    public AudioSource Source { get; } = source;

    public string DeviceId { get; init; } = $"{{fake}}.{{{Guid.NewGuid()}}}";

    public string DeviceName { get; } = deviceName;

    public AudioFormatInfo Format { get; } = format;

    public bool IsCapturing { get; private set; }

    public Exception? ThrowOnStart { get; init; }

    public event AudioDataHandler? DataAvailable;

    public event EventHandler<CaptureSourceStoppedEventArgs>? Stopped;

    public void StartCapture()
    {
        if (ThrowOnStart is not null) throw ThrowOnStart;
        IsCapturing = true;
    }

    public void StopCapture()
    {
        if (!IsCapturing) return;
        IsCapturing = false;
        Stopped?.Invoke(this, new CaptureSourceStoppedEventArgs(null));
    }

    /// <summary>Called after every pushed packet (tests use it to throttle to the writer's pace).</summary>
    public Action? AfterPush { get; set; }

    public void Push(ReadOnlySpan<byte> data, AudioPacketStatus status = AudioPacketStatus.None, long captureTimestamp = 0)
    {
        DataAvailable?.Invoke(data, status, captureTimestamp);
        AfterPush?.Invoke();
    }

    /// <summary>Simulates the device disappearing (unplugged / invalidated).</summary>
    public void Fail(Exception error)
    {
        IsCapturing = false;
        Stopped?.Invoke(this, new CaptureSourceStoppedEventArgs(error));
    }

    /// <summary>
    /// Pushes <paramref name="duration"/> of audio in packets, advancing the clock by each packet's length first.
    /// With <paramref name="stampCaptureTime"/>, each packet carries the clock time its first frame was captured.
    /// </summary>
    public void PushFor(ManualTimeProvider clock, TimeSpan duration, TimeSpan? packet = null, byte value = 0x11, AudioPacketStatus status = AudioPacketStatus.None, bool stampCaptureTime = false)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var packetLength = packet ?? TimeSpan.FromMilliseconds(10);
        var framesPerPacket = (int)(Format.SampleRate * packetLength.TotalSeconds);
        var buffer = new byte[framesPerPacket * Format.BlockAlign];
        Array.Fill(buffer, value);
        var packets = (long)Math.Round(duration / packetLength);
        for (var i = 0; i < packets; i++)
        {
            var captured = clock.GetTimestamp();
            clock.Advance(packetLength);
            Push(buffer, status, stampCaptureTime ? captured : 0);
        }
    }

    public void Dispose() => IsCapturing = false;
}

/// <summary>Settable free-space probe.</summary>
public sealed class FakeFreeSpace(long availableBytes) : IFreeSpaceProbe
{
    public long AvailableBytes { get; set; } = availableBytes;

    public long GetAvailableBytes(string path) => AvailableBytes;
}

/// <summary>Opt-in long-running test: runs only when VJ_LONG_TESTS=1.</summary>
public sealed class LongFactAttribute : FactAttribute
{
    public LongFactAttribute()
    {
        if (System.Environment.GetEnvironmentVariable("VJ_LONG_TESTS") != "1") Skip = "Long test. Set VJ_LONG_TESTS=1 to run.";
    }
}
