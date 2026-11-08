using System.Runtime.InteropServices;
using VoiceJournal.Core.Audio;

namespace VoiceJournal.Core.Capture;

/// <summary>Peak level of a buffer of captured audio (0..1). Formats the recorder does not meter give 0.</summary>
public static class AudioPeak
{
    public static float Of(ReadOnlySpan<byte> data, AudioFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(format);
        float peak = 0;
        if (format.Encoding == AudioSampleEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            foreach (var s in MemoryMarshal.Cast<byte, float>(data)) peak = Math.Max(peak, Math.Abs(s));
        }
        else if (format.Encoding == AudioSampleEncoding.Pcm && format.BitsPerSample == 16)
        {
            foreach (var s in MemoryMarshal.Cast<byte, short>(data)) peak = Math.Max(peak, Math.Abs(s / 32768f));
        }
        return peak;
    }
}

/// <summary>What a short capture test found for one source (UI-01 「10 秒録音テスト」).</summary>
/// <param name="Received">Audio that arrived, from the byte count (silent packets included).</param>
/// <param name="Peak">Highest level of the non-silent packets (0..1).</param>
/// <param name="Error">Why the device could not be opened or stopped during the test.</param>
public sealed record CaptureCheckResult(
    AudioSource Source, string DeviceName, TimeSpan Expected, TimeSpan Received, float Peak, int Discontinuities, Exception? Error)
{
    /// <summary>About −50 dBFS: below this the input is muted, unplugged or turned all the way down.</summary>
    public const float AudibleLevel = 0.003f;

    public double PeakDbfs => Peak <= 0 ? double.NegativeInfinity : 20 * Math.Log10(Peak);

    /// <summary>At least 90 % of the test's length arrived (WASAPI loopback sends nothing while nothing plays).</summary>
    public bool Arrived => Error is null && Received >= Expected * 0.9;

    public bool Audible => Error is null && Peak >= AudibleLevel;
}

/// <summary>
/// UI-01: opens a capture source for a short time and measures what arrives, without writing anything to disk (the
/// test is not a recording: nothing of it is kept, and no session is made).
/// </summary>
public static class CaptureCheck
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(10);

    public static async Task<CaptureCheckResult> RunAsync(IAudioCaptureSource source, TimeSpan duration, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);
        var gate = new Lock();
        long bytes = 0;
        float peak = 0;
        var discontinuities = 0;
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnData(ReadOnlySpan<byte> data, AudioPacketStatus flags, long captureTimestamp)
        {
            var level = (flags & AudioPacketStatus.Silent) == 0 ? AudioPeak.Of(data, source.Format) : 0f;
            lock (gate)
            {
                bytes += data.Length;
                if ((flags & AudioPacketStatus.Discontinuity) != 0) discontinuities++;
                if (level > peak) peak = level;
            }
        }

        void OnStopped(object? sender, CaptureSourceStoppedEventArgs e) => stopped.TrySetResult(e.Error);

        source.DataAvailable += OnData;
        source.Stopped += OnStopped;
        Exception? error = null;
        try
        {
            try
            {
                source.StartCapture();
            }
            catch (AudioDeviceException ex)
            {
                error = ex;
            }
            if (error is null)
            {
                var finished = await Task.WhenAny(Task.Delay(duration, time, cancellationToken), stopped.Task).ConfigureAwait(false);
                if (finished == stopped.Task) error = await stopped.Task.ConfigureAwait(false) ?? new AudioDeviceException(AudioDeviceErrorKind.DeviceInvalidated, "The device stopped during the test.");
                else await finished.ConfigureAwait(false); // cancellation surfaces here
            }
        }
        finally
        {
            if (error is null) source.StopCapture();
            source.DataAvailable -= OnData;
            source.Stopped -= OnStopped;
        }
        lock (gate)
        {
            var received = source.Format.AverageBytesPerSecond > 0 ? TimeSpan.FromSeconds((double)bytes / source.Format.AverageBytesPerSecond) : TimeSpan.Zero;
            return new CaptureCheckResult(source.Source, source.DeviceName, duration, received, peak, discontinuities, error);
        }
    }
}
