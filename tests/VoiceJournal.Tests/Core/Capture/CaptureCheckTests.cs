using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Capture;

/// <summary>UI-01 「10 秒録音テスト」: what arrives from a device, without saving anything.</summary>
public sealed class CaptureCheckTests
{
    private static readonly TimeSpan Length = TimeSpan.FromMilliseconds(300);
    private static readonly AudioFormatInfo Pcm = AudioFormatInfo.Pcm16Bit(8000, 1);

    private static async Task<CaptureCheckResult> Run(FakeCaptureSource source, Action<FakeCaptureSource> feed)
    {
        var task = CaptureCheck.RunAsync(source, Length, TimeProvider.System, CancellationToken.None);
        feed(source);
        return await task;
    }

    private static byte[] Samples(TimeSpan length, short value)
    {
        var frames = (int)(Pcm.SampleRate * length.TotalSeconds);
        var bytes = new byte[frames * Pcm.BlockAlign];
        for (var i = 0; i < frames; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), value);
        return bytes;
    }

    [Fact]
    public async Task SoundThatArrives_IsMeasured_AndTheSourceIsStopped()
    {
        var source = new FakeCaptureSource(AudioSource.Mic, Pcm, "USB マイク");

        var result = await Run(source, s => s.Push(Samples(Length, 3277))); // about −20 dBFS

        Assert.True(result.Arrived);
        Assert.True(result.Audible);
        Assert.Equal(-20, result.PeakDbfs, 0);
        Assert.Equal("USB マイク", result.DeviceName);
        Assert.False(source.IsCapturing);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SilentPacketsAndNearSilence_AreNotAudible()
    {
        var silent = await Run(new FakeCaptureSource(AudioSource.Mic, Pcm), s => s.Push(Samples(Length, 20000), AudioPacketStatus.Silent));
        var quiet = await Run(new FakeCaptureSource(AudioSource.Mic, Pcm), s => s.Push(Samples(Length, 30))); // about −61 dBFS

        Assert.True(silent.Arrived);
        Assert.False(silent.Audible);
        Assert.True(quiet.Arrived);
        Assert.False(quiet.Audible);
    }

    [Fact]
    public async Task TooLittleAudio_HasNotArrived_AndGapsAreCounted()
    {
        var result = await Run(new FakeCaptureSource(AudioSource.Loopback, Pcm), s =>
        {
            s.Push(Samples(Length / 3, 3000));
            s.Push(Samples(Length / 3, 3000), AudioPacketStatus.Discontinuity);
        });

        Assert.False(result.Arrived);
        Assert.Equal(1, result.Discontinuities);
    }

    [Fact]
    public async Task ADeviceThatCannotOpen_OrStopsDuringTheTest_IsAnError()
    {
        var denied = new AudioDeviceException(AudioDeviceErrorKind.AccessDenied, "denied");
        var cannotOpen = await CaptureCheck.RunAsync(new FakeCaptureSource(AudioSource.Mic, Pcm) { ThrowOnStart = denied }, Length, TimeProvider.System, CancellationToken.None);
        var unplugged = await Run(new FakeCaptureSource(AudioSource.Mic, Pcm), s => s.Fail(new AudioDeviceException(AudioDeviceErrorKind.DeviceInvalidated, "gone")));

        Assert.Same(denied, cannotOpen.Error);
        Assert.False(cannotOpen.Arrived);
        Assert.IsType<AudioDeviceException>(unplugged.Error);
        Assert.False(unplugged.Audible);
    }
}
