using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Infrastructure.Audio;

namespace VoiceJournal.Tests.Infrastructure.Audio;

public sealed class WasapiInteropTests
{
    [Fact]
    public void SharedModeMixFormat_IsKeptAsExtensibleFloat()
    {
        // What WASAPI reports on this machine: 48 kHz / 32 bit float / 2 ch, WAVE_FORMAT_EXTENSIBLE.
        var mix = new WaveFormatExtensible(48000, 32, 2, true, 32, 0x3);

        var info = WasapiInterop.ToAudioFormat(mix);

        Assert.Equal(AudioFormatInfo.IeeeFloat32Bit(48000, 2), info);
    }

    [Fact]
    public void Extensible24BitPcm_KeepsValidBitsAndChannelMask()
    {
        var format = new WaveFormatExtensible(44100, 32, 4, false, 24, 0x33);

        var info = WasapiInterop.ToAudioFormat(format);

        Assert.Equal(AudioSampleEncoding.Pcm, info.Encoding);
        Assert.Equal(32, info.BitsPerSample);
        Assert.Equal(24, info.ValidBitsPerSample);
        Assert.Equal(0x33u, info.ChannelMask);
        Assert.Equal(16, info.BlockAlign);
    }

    [Fact]
    public void PlainPcmAndFloat_AreMapped()
    {
        Assert.Equal(AudioFormatInfo.Pcm16Bit(16000, 1), WasapiInterop.ToAudioFormat(new WaveFormat(16000, 16, 1)));
        Assert.Equal(AudioFormatInfo.IeeeFloat32Bit(44100, 2, extensible: false), WasapiInterop.ToAudioFormat(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)));
    }

    [Fact]
    public void CompressedFormats_AreRejected()
    {
        var ex = Assert.Throws<AudioDeviceException>(() => WasapiInterop.ToAudioFormat(WaveFormat.CreateMuLawFormat(8000, 1)));
        Assert.Equal(AudioDeviceErrorKind.UnsupportedFormat, ex.Kind);
    }

    [Fact]
    public void BufferFlags_MapToPacketStatus()
    {
        Assert.Equal(AudioPacketStatus.None, WasapiInterop.ToStatus(AudioClientBufferFlags.None));
        Assert.Equal(AudioPacketStatus.Silent | AudioPacketStatus.Discontinuity, WasapiInterop.ToStatus(AudioClientBufferFlags.Silent | AudioClientBufferFlags.DataDiscontinuity));
        Assert.Equal(AudioPacketStatus.None, WasapiInterop.ToStatus(AudioClientBufferFlags.TimestampError));
    }

    [Fact]
    public void QpcPosition_IsConvertedToStopwatchTicks()
    {
        const long qpc100ns = 8_177_236_085_175; // a real value from the B3 measurement (~9.5 days of uptime)
        Assert.Equal((long)((Int128)qpc100ns * Stopwatch.Frequency / 10_000_000), WasapiInterop.ToCaptureTimestamp(qpc100ns, AudioClientBufferFlags.None));

        // The same clock as TimeProvider.System: "now" converts to (almost) now.
        var now100ns = (long)((Int128)Stopwatch.GetTimestamp() * 10_000_000 / Stopwatch.Frequency);
        var converted = WasapiInterop.ToCaptureTimestamp(now100ns, AudioClientBufferFlags.None);
        Assert.InRange(TimeProvider.System.GetElapsedTime(converted).TotalMilliseconds, -1, 1000);
    }

    [Fact]
    public void UnreliableTimestamps_AreDropped()
    {
        Assert.Equal(0, WasapiInterop.ToCaptureTimestamp(0, AudioClientBufferFlags.None));
        Assert.Equal(0, WasapiInterop.ToCaptureTimestamp(-5, AudioClientBufferFlags.None));
        Assert.Equal(0, WasapiInterop.ToCaptureTimestamp(123_456_789, AudioClientBufferFlags.TimestampError));
    }

    [Theory]
    [InlineData(0x80070005u, AudioDeviceErrorKind.AccessDenied)]
    [InlineData(0x80070490u, AudioDeviceErrorKind.DeviceNotFound)]
    [InlineData(0x88890004u, AudioDeviceErrorKind.DeviceInvalidated)]
    [InlineData(0x8889000Au, AudioDeviceErrorKind.DeviceInUse)]
    [InlineData(0x88890010u, AudioDeviceErrorKind.AudioServiceNotRunning)]
    [InlineData(0x88890008u, AudioDeviceErrorKind.UnsupportedFormat)]
    [InlineData(0x80004005u, AudioDeviceErrorKind.Unknown)]
    public void HResults_AreClassified(uint hresult, AudioDeviceErrorKind expected)
    {
        Assert.Equal(expected, WasapiInterop.ClassifyHResult(unchecked((int)hresult)));
    }

    [Fact]
    public void RawModeUnsupported_IsClassified()
    {
        Assert.Equal(AudioDeviceErrorKind.RawModeUnsupported, WasapiInterop.ClassifyHResult((int)NAudio.CoreAudioApi.Interfaces.AudioClientErrorCode.RawModeUnsupported));
    }

    [Fact]
    public void Wrap_LooksThroughInnerExceptions_AndKeepsTheCode()
    {
        // Exceptions as the runtime creates them for failed COM calls.
        var inner = Marshal.GetExceptionForHR(unchecked((int)0x88890004))!;
        var wrapped = WasapiInterop.Wrap(new InvalidOperationException("capture failed", inner), "Capture");
        Assert.Equal(AudioDeviceErrorKind.DeviceInvalidated, wrapped.Kind);
        Assert.Same(inner, wrapped.InnerException!.InnerException);

        var direct = WasapiInterop.Wrap(Marshal.GetExceptionForHR(unchecked((int)0x8889000A))!, "Starting capture");
        Assert.Equal(AudioDeviceErrorKind.DeviceInUse, direct.Kind);
        Assert.Contains("0x8889000A", direct.Message, StringComparison.Ordinal);

        // E_ACCESSDENIED surfaces as UnauthorizedAccessException.
        var denied = WasapiInterop.Wrap(Marshal.GetExceptionForHR(unchecked((int)0x80070005))!, "Starting capture");
        Assert.Equal(AudioDeviceErrorKind.AccessDenied, denied.Kind);
    }
}
