using VoiceJournal.Core.Audio;

namespace VoiceJournal.Core.Capture;

/// <summary>Per-packet flags reported by the capture API.</summary>
[Flags]
public enum AudioPacketStatus
{
    None = 0,
    /// <summary>The packet must be treated as silence regardless of its contents.</summary>
    Silent = 1,
    /// <summary>The device reported a gap before this packet.</summary>
    Discontinuity = 2,
}

/// <summary>Callback for captured audio. The span is only valid during the call.</summary>
/// <param name="data">Whole frames in the source's <see cref="IAudioCaptureSource.Format"/>.</param>
/// <param name="flags">Packet flags reported by the device.</param>
/// <param name="captureTimestamp">
/// When the first frame of the packet was captured, in <see cref="TimeProvider.GetTimestamp"/> units of the
/// clock the session uses (for WASAPI: the packet's QPC position converted to <see cref="System.Diagnostics.Stopwatch"/>
/// ticks). 0 when the source cannot tell; the arrival time is used instead.
/// </param>
public delegate void AudioDataHandler(ReadOnlySpan<byte> data, AudioPacketStatus flags, long captureTimestamp);

/// <summary>Raised once when a source stops; <see cref="Error"/> is null for a requested stop.</summary>
public sealed class CaptureSourceStoppedEventArgs(Exception? error) : EventArgs
{
    public Exception? Error { get; } = error;
}

/// <summary>
/// One capture device (mic or loopback) with a format fixed at creation. Implemented over NAudio in
/// Infrastructure and by fakes in tests. <see cref="DataAvailable"/> is raised on the capture thread;
/// handlers must not block (detail spec 6.2).
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    AudioSource Source { get; }

    /// <summary>Endpoint id resolved when the source was created (never "default").</summary>
    string DeviceId { get; }

    string DeviceName { get; }

    AudioFormatInfo Format { get; }

    event AudioDataHandler? DataAvailable;

    event EventHandler<CaptureSourceStoppedEventArgs>? Stopped;

    /// <summary>Starts capturing. Throws when the device cannot be opened (e.g. permission denied).</summary>
    void StartCapture();

    /// <summary>Requests a stop; <see cref="Stopped"/> follows (possibly on another thread).</summary>
    void StopCapture();
}

/// <summary>Free disk space of the volume that holds a path.</summary>
public interface IFreeSpaceProbe
{
    long GetAvailableBytes(string path);
}

/// <summary><see cref="IFreeSpaceProbe"/> backed by <see cref="DriveInfo"/>.</summary>
public sealed class DriveFreeSpaceProbe : IFreeSpaceProbe
{
    public long GetAvailableBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
}

/// <summary>Settings of one recording. Defaults follow detail spec 6.2 / 19.</summary>
public sealed record RecordingOptions
{
    public required CaptureMode Mode { get; init; }

    public MicProcessingMode MicMode { get; init; } = MicProcessingMode.Raw;

    public string? Title { get; init; }

    public string? Project { get; init; }

    /// <summary>Chunks are finalized at this length (spec: 5 minutes).</summary>
    public TimeSpan ChunkDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Per-source bounded queue (spec: 2 seconds of audio). Overflow stops the recording safely.</summary>
    public TimeSpan QueueCapacity { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Loopback gaps shorter than this are treated as jitter, not as missing packets.</summary>
    public TimeSpan LoopbackGapTolerance { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Recording does not start, and stops safely, below this much free space (FS-001).</summary>
    public long MinFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>How often (in recorded audio time) the writer re-checks free space.</summary>
    public TimeSpan FreeSpaceCheckInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Time to wait for a source to confirm it stopped before draining anyway.</summary>
    public TimeSpan SourceStopTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Raised when recording could not start; nothing keeps running.</summary>
public sealed class CaptureStartException : Exception
{
    public CaptureStartException() { }

    public CaptureStartException(string message) : base(message) { }

    public CaptureStartException(string message, Exception inner) : base(message, inner) { }

    public CaptureStartException(string message, AudioSource? source, Exception? inner) : base(message, inner) => FailedSource = source;

    public AudioSource? FailedSource { get; }
}
