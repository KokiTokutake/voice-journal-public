using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;

namespace VoiceJournal.Infrastructure.Audio;

/// <summary>Conversions between NAudio / WASAPI values and the Core model.</summary>
internal static class WasapiInterop
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private const int AccessDenied = unchecked((int)0x80070005);   // E_ACCESSDENIED (privacy settings)
    private const int NotFound = unchecked((int)0x80070490);       // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)

    /// <summary>The capture format as stored in the WAV chunks (float stays float, detail spec 6.2).</summary>
    /// <exception cref="AudioDeviceException">The format is not PCM or IEEE float.</exception>
    public static AudioFormatInfo ToAudioFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        AudioFormatInfo info;
        if (format is WaveFormatExtensible x)
        {
            var encoding = x.SubFormat == PcmSubFormat ? AudioSampleEncoding.Pcm
                : x.SubFormat == IeeeFloatSubFormat ? AudioSampleEncoding.IeeeFloat
                : throw Unsupported(format);
            info = new AudioFormatInfo
            {
                SampleRate = x.SampleRate,
                Channels = x.Channels,
                BitsPerSample = x.BitsPerSample,
                Encoding = encoding,
                Extensible = true,
                ValidBitsPerSample = x.ValidBitsPerSample,
                ChannelMask = unchecked((uint)x.ChannelMask),
            };
        }
        else
        {
            var encoding = format.Encoding switch
            {
                WaveFormatEncoding.Pcm => AudioSampleEncoding.Pcm,
                WaveFormatEncoding.IeeeFloat => AudioSampleEncoding.IeeeFloat,
                _ => throw Unsupported(format),
            };
            info = new AudioFormatInfo { SampleRate = format.SampleRate, Channels = format.Channels, BitsPerSample = format.BitsPerSample, Encoding = encoding };
        }

        try
        {
            info.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new AudioDeviceException(AudioDeviceErrorKind.UnsupportedFormat, $"Unsupported capture format: {format}", ex);
        }
        return info;
    }

    public static AudioPacketStatus ToStatus(AudioClientBufferFlags flags)
    {
        var status = AudioPacketStatus.None;
        if ((flags & AudioClientBufferFlags.Silent) != 0) status |= AudioPacketStatus.Silent;
        if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) status |= AudioPacketStatus.Discontinuity;
        return status;
    }

    /// <summary>
    /// WASAPI reports the capture time of a packet as a QPC value in 100 ns units. Converted to
    /// <see cref="Stopwatch"/> ticks it shares the clock of <see cref="TimeProvider.System"/>
    /// (Phase B3: matched Stopwatch within 1 ms over 38 s). 0 when the device flags it as unreliable.
    /// </summary>
    public static long ToCaptureTimestamp(long qpcPosition100ns, AudioClientBufferFlags flags)
    {
        if (qpcPosition100ns <= 0 || (flags & AudioClientBufferFlags.TimestampError) != 0) return 0;
        return (long)((Int128)qpcPosition100ns * Stopwatch.Frequency / 10_000_000);
    }

    /// <summary>Classifies a WASAPI / COM failure, looking through wrapped exceptions.</summary>
    public static AudioDeviceErrorKind Classify(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is AudioDeviceException known && known.Kind != AudioDeviceErrorKind.Unknown) return known.Kind;
            if (e is UnauthorizedAccessException) return AudioDeviceErrorKind.AccessDenied;
            var kind = ClassifyHResult(e.HResult);
            if (kind != AudioDeviceErrorKind.Unknown) return kind;
        }
        return AudioDeviceErrorKind.Unknown;
    }

    public static AudioDeviceErrorKind ClassifyHResult(int hresult) => hresult switch
    {
        AccessDenied => AudioDeviceErrorKind.AccessDenied,
        NotFound => AudioDeviceErrorKind.DeviceNotFound,
        (int)AudioClientErrorCode.DeviceInvalidated => AudioDeviceErrorKind.DeviceInvalidated,
        (int)AudioClientErrorCode.ResourcesInvalidated => AudioDeviceErrorKind.DeviceInvalidated,
        (int)AudioClientErrorCode.DeviceInUse => AudioDeviceErrorKind.DeviceInUse,
        (int)AudioClientErrorCode.ExclusiveModeNotAllowed => AudioDeviceErrorKind.DeviceInUse,
        (int)AudioClientErrorCode.RawModeUnsupported => AudioDeviceErrorKind.RawModeUnsupported,
        (int)AudioClientErrorCode.UnsupportedFormat => AudioDeviceErrorKind.UnsupportedFormat,
        (int)AudioClientErrorCode.ServiceNotRunning => AudioDeviceErrorKind.AudioServiceNotRunning,
        _ => AudioDeviceErrorKind.Unknown,
    };

    /// <summary>Wraps a device failure as <see cref="AudioDeviceException"/>; the message carries no personal data.</summary>
    public static AudioDeviceException Wrap(Exception error, string operation)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is AudioDeviceException known) return known;
        var kind = Classify(error);
        var code = error is ExternalException ? $" (0x{error.HResult:X8})" : string.Empty;
        return new AudioDeviceException(kind, $"{operation} failed: {kind}{code}", error);
    }

    private static AudioDeviceException Unsupported(WaveFormat format) =>
        new(AudioDeviceErrorKind.UnsupportedFormat, $"Unsupported capture format: {format}");
}
