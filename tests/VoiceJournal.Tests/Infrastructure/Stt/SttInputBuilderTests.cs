using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Stt;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Stt;

internal static class SttTestSession
{
    /// <summary>Writes a 32-bit float WAV chunk and returns its manifest entry.</summary>
    public static ChunkManifest Chunk(string sessionDir, AudioSource source, int sequence, long startMs, int rate, int channels, float[] mono)
    {
        var file = $"audio/{source.ToString().ToLowerInvariant()}/{sequence:000000}.wav";
        var path = SessionPaths.Resolve(sessionDir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)))
        {
            var frame = new float[channels];
            foreach (var x in mono)
            {
                Array.Fill(frame, x);
                writer.WriteSamples(frame, 0, channels);
            }
        }
        return new ChunkManifest
        {
            Sequence = sequence,
            File = file,
            StartMs = startMs,
            FrameCount = mono.Length,
            Sha256 = SttInputBuilder.HashFile(path),
            State = ChunkState.Completed,
        };
    }

    public static TrackManifest Track(AudioSource source, int rate, int channels, params ChunkManifest[] chunks)
    {
        var track = new TrackManifest
        {
            TrackId = Guid.NewGuid(),
            Source = source,
            DeviceId = "test",
            DeviceName = "テスト",
            Format = AudioFormatInfo.IeeeFloat32Bit(rate, channels),
            State = TrackState.Stopped,
        };
        foreach (var c in chunks) track.Chunks.Add(c);
        return track;
    }

    public static void Save(string sessionDir, CaptureMode mode, params TrackManifest[] tracks)
    {
        var manifest = new SessionManifest
        {
            SessionId = Guid.CreateVersion7(),
            CaptureMode = mode,
            CaptureState = VoiceJournal.Core.Capture.CaptureState.Finalized,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            TimeZoneId = "Tokyo Standard Time",
        };
        foreach (var t in tracks) manifest.Tracks.Add(t);
        manifest.Save(sessionDir);
    }

    public static float[] Tone(double seconds, int rate, double amplitude, double hz = 1000) =>
        [.. Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate)))];

    public static short[] ReadPcm16(string path)
    {
        using var reader = new WaveFileReader(path);
        Assert.Equal(16_000, reader.WaveFormat.SampleRate);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(1, reader.WaveFormat.Channels);
        var bytes = new byte[reader.Length];
        Assert.Equal(bytes.Length, reader.Read(bytes, 0, bytes.Length));
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }
}

public sealed class SttInputBuilderTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly SttConfiguration _config = new() { ModelName = "ggml-small.bin", PieceMs = 2_000, OverlapMs = 500 };

    public void Dispose() => _dir.Dispose();

    /// <summary>
    /// Mic: −30 dBFS tone 1–2 s and 3–4 s (unplugged in between). Loopback (stereo): silence 0–2 s, −6 dBFS tone 2–4 s.
    /// </summary>
    private void MakeSession()
    {
        var micQuiet = SttTestSession.Tone(1, 48_000, 0.0316);
        var mic = SttTestSession.Track(AudioSource.Mic, 48_000, 1,
            SttTestSession.Chunk(_dir.Path, AudioSource.Mic, 1, 1_000, 48_000, 1, micQuiet),
            SttTestSession.Chunk(_dir.Path, AudioSource.Mic, 2, 3_000, 48_000, 1, micQuiet));
        var loopAudio = new float[2 * 48_000].Concat(SttTestSession.Tone(2, 48_000, 0.5)).ToArray();
        var loop = SttTestSession.Track(AudioSource.Loopback, 48_000, 2,
            SttTestSession.Chunk(_dir.Path, AudioSource.Loopback, 1, 0, 48_000, 2, loopAudio));
        SttTestSession.Save(_dir.Path, CaptureMode.Both, mic, loop);
    }

    [Fact]
    public async Task Build_PlacesAudioOnTheSessionTimeline_WithSilenceForTheOffsetAndGaps()
    {
        MakeSession();

        var input = await new SttInputBuilder().BuildAsync(_dir.Path, _config, null, CancellationToken.None);

        Assert.Equal(4_000, input.DurationMs);
        var mic = input.Pieces.Where(p => p.Source == AudioSource.Mic).ToList();
        Assert.Equal([new SttPiece(1, 0, 2_500, 0, 2_000), new SttPiece(2, 1_500, 4_000, 2_000, 4_000)], mic.Select(p => p.Piece));
        var first = SttTestSession.ReadPcm16(mic[0].Path);
        Assert.Equal(40_000, first.Length);
        Assert.All(first[..16_000], s => Assert.Equal(0, s));                           // before the track started
        Assert.InRange(first[17_000..31_000].Max(s => (int)s) / 32767.0, 0.62, 0.78);   // −30 dBFS raised to about −3 dBFS
        Assert.All(first[32_200..40_000], s => Assert.Equal(0, s));                     // unplugged 2.0–3.0 s
        var second = SttTestSession.ReadPcm16(mic[1].Path);
        Assert.Equal(40_000, second.Length);
        Assert.Equal(first[24_000..40_000], second[..16_000]);                          // the overlap is the same audio
        Assert.InRange(second[25_000..39_000].Max(s => (int)s) / 32767.0, 0.62, 0.78);  // second chunk at 3.0 s

        Assert.InRange(input.GainDb[AudioSource.Mic], 26, 28);
        Assert.InRange(input.GainDb[AudioSource.Loopback], 2.5, 3.5);
        Assert.Equal(-127, input.LevelDbfs(AudioSource.Mic, 0, 900)!.Value, 1);
        Assert.InRange(input.LevelDbfs(AudioSource.Mic, 1_100, 1_900)!.Value, -34.5, -31.5);
        Assert.InRange(input.LevelDbfs(AudioSource.Loopback, 2_100, 3_900)!.Value, -10, -8);
    }

    [Fact]
    public async Task Build_ReusesAVerifiedInput_AndRebuildsWhenAPieceWasChanged()
    {
        MakeSession();
        var builder = new SttInputBuilder();
        var first = await builder.BuildAsync(_dir.Path, _config, null, CancellationToken.None);
        var piece = first.Pieces[0].Path;
        var written = File.GetLastWriteTimeUtc(piece);

        var again = await builder.BuildAsync(_dir.Path, _config, null, CancellationToken.None);
        Assert.Equal(first.Directory, again.Directory);
        Assert.Equal(written, File.GetLastWriteTimeUtc(piece));

        await File.AppendAllTextAsync(piece, "x");
        var rebuilt = await builder.BuildAsync(_dir.Path, _config, null, CancellationToken.None);
        Assert.Equal(first.Directory, rebuilt.Directory);
        Assert.Equal(first.Pieces[0].Sha256, SttInputBuilder.HashFile(piece));
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.GetDirectoryName(first.Directory)!), d => d.Contains(".building-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_GivesADifferentInputHash_WhenTheConversionChanges_AndKeepsTheOriginalAudio()
    {
        MakeSession();
        var originals = Directory.EnumerateFiles(Path.Combine(_dir.Path, "audio"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, SttInputBuilder.HashFile);

        var a = await new SttInputBuilder().BuildAsync(_dir.Path, _config, null, CancellationToken.None);
        var b = await new SttInputBuilder().BuildAsync(_dir.Path, _config with { NormalizeGain = false }, null, CancellationToken.None);

        Assert.NotEqual(a.InputHash, b.InputHash);
        Assert.Equal(0, b.GainDb[AudioSource.Mic]);
        Assert.All(originals, kv => Assert.Equal(kv.Value, SttInputBuilder.HashFile(kv.Key)));
    }
}
