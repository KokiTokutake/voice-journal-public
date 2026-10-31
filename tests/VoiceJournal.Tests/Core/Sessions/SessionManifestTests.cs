using System.Text;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Sessions;

public sealed class SessionManifestTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SaveLoad_RoundTrips_WithSnakeCaseJson()
    {
        var manifest = Sample();
        manifest.Save(_dir.Path);

        var json = File.ReadAllText(SessionManifest.PathIn(_dir.Path), Encoding.UTF8);
        Assert.Contains("\"capture_mode\": \"both\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\": \"loopback\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"loopback_silence\"", json, StringComparison.Ordinal);
        Assert.Contains("定例会議", json, StringComparison.Ordinal); // readable Japanese, not \u escapes

        var loaded = SessionManifest.Load(_dir.Path);
        Assert.Equal(manifest.SessionId, loaded.SessionId);
        Assert.Equal("定例会議", loaded.Title);
        Assert.Equal(CaptureState.Recording, loaded.CaptureState);
        Assert.Equal(MicProcessingMode.Raw, loaded.MicMode);
        Assert.Equal(2, loaded.Tracks.Count);
        Assert.Equal(manifest.Tracks[1].Format, loaded.Tracks[1].Format);
        Assert.Equal("audio/loopback/000001.wav", loaded.Tracks[1].Chunks[0].File);
        Assert.Equal(ChunkState.Completed, loaded.Tracks[1].Chunks[0].State);
        Assert.Equal(1200, loaded.Tracks[1].Events[0].EndMs);
    }

    [Fact]
    public void Save_ReplacesAtomically_WithoutLeftoverTempFiles()
    {
        var manifest = Sample();
        manifest.Save(_dir.Path);
        manifest.CaptureState = CaptureState.Finalized;
        manifest.Save(_dir.Path);

        Assert.Equal(CaptureState.Finalized, SessionManifest.Load(_dir.Path).CaptureState);
        Assert.Equal([SessionManifest.FileName], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("C:/Windows/evil.wav")]
    [InlineData("audio\\mic\\000001.wav")]
    [InlineData("/audio/mic/000001.wav")]
    [InlineData("audio/mic/000001.wav:ads")]
    public void Save_RejectsUnsafeChunkPaths(string path)
    {
        var manifest = Sample();
        manifest.Tracks[0].Chunks[0].File = path;
        Assert.Throws<InvalidDataException>(() => manifest.Save(_dir.Path));
    }

    [Fact]
    public void Load_RejectsUnsafeChunkPathsWrittenByHand()
    {
        Sample().Save(_dir.Path);
        var path = SessionManifest.PathIn(_dir.Path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("audio/mic/000001.wav", "../../secret.wav", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => SessionManifest.Load(_dir.Path));
    }

    [Fact]
    public void Load_RejectsUnknownSchemaVersion()
    {
        Sample().Save(_dir.Path);
        var path = SessionManifest.PathIn(_dir.Path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schema_version\": 1", "\"schema_version\": 99", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => SessionManifest.Load(_dir.Path));
    }

    private static SessionManifest Sample() => new()
    {
        SessionId = Guid.CreateVersion7(),
        Title = "定例会議",
        CaptureMode = CaptureMode.Both,
        CaptureState = CaptureState.Recording,
        MicMode = MicProcessingMode.Raw,
        StartedAtUtc = new DateTimeOffset(2026, 11, 1, 1, 2, 3, TimeSpan.Zero),
        TimeZoneId = "Tokyo Standard Time",
        Tracks =
        [
            new TrackManifest
            {
                TrackId = Guid.CreateVersion7(),
                Source = AudioSource.Mic,
                DeviceId = "{0.0.1.00000000}.{mic}",
                DeviceName = "Microphone Array",
                Format = AudioFormatInfo.IeeeFloat32Bit(48000, 2),
                Chunks = [new ChunkManifest { Sequence = 1, File = SessionPaths.ChunkFile(AudioSource.Mic, 1), StartMs = 0 }],
            },
            new TrackManifest
            {
                TrackId = Guid.CreateVersion7(),
                Source = AudioSource.Loopback,
                DeviceId = "{0.0.0.00000000}.{spk}",
                DeviceName = "Speaker",
                Format = AudioFormatInfo.IeeeFloat32Bit(48000, 2),
                StartOffsetMs = 12,
                Chunks =
                [
                    new ChunkManifest
                    {
                        Sequence = 1,
                        File = SessionPaths.ChunkFile(AudioSource.Loopback, 1),
                        StartMs = 0,
                        FrameCount = 480000,
                        SizeBytes = 3_840_080,
                        Sha256 = new string('a', 64),
                        State = ChunkState.Completed,
                    },
                ],
                Events = [new AudioEventManifest { Kind = AudioEventKind.LoopbackSilence, StartMs = 0, EndMs = 1200 }],
            },
        ],
    };
}
