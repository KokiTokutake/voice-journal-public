using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Serialization;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Infrastructure.Stt;

/// <summary>
/// Makes the recognizer input (detail spec 8.1, plan 3.3): for every source, the saved chunks joined on the session
/// timeline (gaps and the start offset filled with silence, nothing shortened), averaged to mono, resampled to
/// 16 kHz with NAudio's WDL resampler, optionally gain-normalized, and written as 16-bit pieces with overlap.
/// The original audio is only read. The result lives in <c>derived/stt-input/&lt;input-hash&gt;</c> and is reused
/// while the input hash and the piece hashes still match.
/// </summary>
public sealed class SttInputBuilder : ISttInputBuilder
{
    public const int SampleRate = 16_000;
    public const string DerivedFolder = "derived/stt-input";
    public const string InfoFileName = "input.json";

    /// <summary>Bumped when the conversion changes in a way that changes the output.</summary>
    private const string ConversionVersion = "stt-input/1 wdl-resampler average-downmix pcm16 p99.99-peak";

    private const int LevelWindowMs = 100;
    private const int LevelWindowSamples = SampleRate * LevelWindowMs / 1000;
    private const double TargetPeakDbfs = -3;
    private const double PeakPercentile = 0.9999;
    private const int BlockSamples = 16_000;

    public Task<SttInput> BuildAsync(string sessionDirectory, SttConfiguration configuration, IProgress<double>? progress, CancellationToken cancellationToken, SttPauseGate? pause = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        ArgumentNullException.ThrowIfNull(configuration);
        return Task.Run(() => Build(Path.GetFullPath(sessionDirectory), configuration, progress, pause, cancellationToken), cancellationToken);
    }

    public Task ExtractAsync(SttInput input, AudioSource source, TimeRange range, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var piece = input.Pieces.Where(p => p.Source == source && p.Piece.StartMs <= range.StartMs && range.EndMs <= p.Piece.EndMs)
            .OrderBy(p => p.Piece.Index).FirstOrDefault()
            ?? throw new ArgumentException("The range does not lie within one piece of that source.", nameof(range));
        return Task.Run(() =>
        {
            using var reader = new WaveFileReader(piece.Path);
            var offset = (range.StartMs - piece.Piece.StartMs) * SampleRate / 1000 * 2;
            var length = (int)(range.DurationMs * SampleRate / 1000 * 2);
            reader.Position = offset;
            var bytes = new byte[length];
            var read = 0;
            while (read < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = reader.Read(bytes, read, length - read);
                if (n == 0) break;
                read += n;
            }
            var temp = outputPath + ".tmp";
            using (var writer = new WaveFileWriter(temp, new WaveFormat(SampleRate, 16, 1))) writer.Write(bytes, 0, read);
            File.Move(temp, outputPath, overwrite: true);
        }, cancellationToken);
    }

    private static SttInput Build(string sessionDirectory, SttConfiguration configuration, IProgress<double>? progress, SttPauseGate? pause, CancellationToken cancellationToken)
    {
        var manifest = SessionManifest.Load(sessionDirectory);
        var tracks = manifest.Tracks.Where(t => UsableChunks(t).Any()).OrderBy(t => t.Source).ToList();
        if (tracks.Count == 0) throw new SttException(SttErrorCode.RunFailed, "The session has no saved audio to transcribe.");
        var durationMs = tracks.Max(t => UsableChunks(t).Max(c => c.StartMs + c.FrameCount * 1000 / t.Format.SampleRate));
        var plan = SttPiecePlanner.Plan(durationMs, configuration.PieceMs, configuration.OverlapMs);
        var inputHash = InputHash(tracks, durationMs, configuration);
        var root = Path.Combine(sessionDirectory, DerivedFolder.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine(root, inputHash[..16]);

        if (TryReuse(target, inputHash) is { } existing)
        {
            progress?.Report(1);
            return existing;
        }

        var building = target + ".building-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(building);
        try
        {
            var totalSamples = durationMs * SampleRate / 1000;
            var work = (double)totalSamples * tracks.Count * 2;
            var done = 0.0;
            void Advance(long samples)
            {
                done += samples;
                progress?.Report(Math.Min(1, done / work));
            }

            var sources = new List<SourceInfo>();
            foreach (var track in tracks)
            {
                // Pass 1: level envelope and the peak percentile (before gain).
                var histogram = new long[HistogramBins];
                var levels = new List<sbyte>();
                double windowEnergy = 0;
                var windowCount = 0;
                foreach (var block in Mono16k(sessionDirectory, track, totalSamples, pause, cancellationToken))
                {
                    foreach (var x in block)
                    {
                        histogram[Bin(Math.Abs(x))]++;
                        windowEnergy += x * x;
                        if (++windowCount == LevelWindowSamples)
                        {
                            levels.Add(ToLevel(windowEnergy / windowCount));
                            windowEnergy = 0;
                            windowCount = 0;
                        }
                    }
                    Advance(block.Length);
                }
                if (windowCount > 0) levels.Add(ToLevel(windowEnergy / windowCount));
                var peakDbfs = PercentileDbfs(histogram, PeakPercentile);
                var gainDb = configuration.NormalizeGain && peakDbfs > MinDbfs
                    ? Math.Min(configuration.MaxGainDb, TargetPeakDbfs - peakDbfs)
                    : 0;
                var levelFile = $"levels-{Name(track.Source)}.bin";
                File.WriteAllBytes(Path.Combine(building, levelFile), [.. levels.Select(l => unchecked((byte)l))]);

                // Pass 2: write the pieces with the gain.
                var gain = (float)Math.Pow(10, gainDb / 20);
                var pieces = WritePieces(sessionDirectory, track, totalSamples, plan, gain, building, Advance, pause, cancellationToken);
                sources.Add(new SourceInfo
                {
                    Source = track.Source,
                    GainDb = Math.Round(gainDb, 2),
                    PeakDbfs = Math.Round(peakDbfs, 2),
                    LevelFile = levelFile,
                    Chunks = [.. UsableChunks(track).Select(c => new ChunkRef(c.Sequence, c.Sha256 ?? ""))],
                    Pieces = pieces,
                });
            }

            var info = new InputInfo
            {
                InputHash = inputHash,
                Conversion = ConversionVersion,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = durationMs,
                SampleRate = SampleRate,
                PieceMs = configuration.PieceMs,
                OverlapMs = configuration.OverlapMs,
                Sources = sources,
            };
            File.WriteAllBytes(Path.Combine(building, InfoFileName), JsonSerializer.SerializeToUtf8Bytes(info, VoiceJournalJson.Options));

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // stale or broken: it did not verify
            Directory.Move(building, target);
            progress?.Report(1);
            return ToInput(info, target);
        }
        catch
        {
            TryDelete(building);
            throw;
        }
    }

    private static IEnumerable<Core.Sessions.ChunkManifest> UsableChunks(TrackManifest track) =>
        track.Chunks.Where(c => c.State is ChunkState.Completed or ChunkState.Recovered && c.FrameCount > 0).OrderBy(c => c.Sequence);

    private static string Name(AudioSource source) => source.ToString().ToLowerInvariant();

    private static string InputHash(IEnumerable<TrackManifest> tracks, long durationMs, SttConfiguration configuration)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(ConversionVersion).Append('\n');
        text.Append("duration_ms=").Append(durationMs.ToString(c)).Append('\n');
        text.Append("piece_ms=").Append(configuration.PieceMs.ToString(c)).Append(" overlap_ms=").Append(configuration.OverlapMs.ToString(c)).Append('\n');
        text.Append("normalize_gain=").Append(configuration.NormalizeGain ? "on" : "off").Append(" max_gain_db=").Append(configuration.MaxGainDb.ToString("0.###", c)).Append('\n');
        foreach (var track in tracks)
        {
            text.Append("source=").Append(Name(track.Source)).Append('\n');
            foreach (var chunk in UsableChunks(track))
                text.Append(chunk.Sequence.ToString(c)).Append(' ').Append(chunk.StartMs.ToString(c)).Append(' ')
                    .Append(chunk.FrameCount.ToString(c)).Append(' ').Append(chunk.Sha256 ?? "-").Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// The track as 16 kHz mono blocks covering exactly [0, <paramref name="totalSamples"/>): silence before the
    /// first chunk and in gaps; consecutive chunks are joined without a gap.
    /// </summary>
    private static IEnumerable<float[]> Mono16k(string sessionDirectory, TrackManifest track, long totalSamples, SttPauseGate? pause, CancellationToken cancellationToken)
    {
        long position = 0;
        foreach (var chunk in UsableChunks(track))
        {
            var expected = chunk.StartMs * SampleRate / 1000;
            // Chunk times are whole milliseconds; within 2 ms of the previous end the chunks are back to back.
            if (Math.Abs(expected - position) <= SampleRate / 500) expected = position;
            var skip = 0L;
            if (expected > position)
            {
                foreach (var block in Silence(Math.Min(expected, totalSamples) - position)) yield return block;
                position = Math.Min(expected, totalSamples);
            }
            else if (expected < position) skip = position - expected;

            var length = (long)Math.Round(chunk.FrameCount * (double)SampleRate / track.Format.SampleRate);
            var remaining = Math.Min(length - skip, totalSamples - position);
            if (remaining <= 0) continue;
            using var reader = new AudioFileReader(SessionPaths.Resolve(sessionDirectory, chunk.File));
            ISampleProvider mono = reader.WaveFormat.Channels == 1 ? reader : new AverageDownmix(reader);
            ISampleProvider resampled = mono.WaveFormat.SampleRate == SampleRate ? mono : new WdlResamplingSampleProvider(mono, SampleRate);
            var buffer = new float[BlockSamples];
            while (skip > 0)
            {
                var read = resampled.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, skip)));
                if (read == 0) break;
                skip -= read;
            }
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pause?.WaitWhilePaused(cancellationToken); // recording comes first
                var read = resampled.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)));
                if (read == 0)
                {
                    // The resampler can come up a few samples short at the end: keep the timeline exact.
                    foreach (var block in Silence(remaining)) yield return block;
                    position += remaining;
                    remaining = 0;
                    break;
                }
                yield return buffer[..read];
                position += read;
                remaining -= read;
            }
        }
        if (position < totalSamples)
            foreach (var block in Silence(totalSamples - position)) yield return block;
    }

    private static IEnumerable<float[]> Silence(long samples)
    {
        while (samples > 0)
        {
            var n = (int)Math.Min(BlockSamples, samples);
            yield return new float[n];
            samples -= n;
        }
    }

    private static List<PieceInfo> WritePieces(
        string sessionDirectory, TrackManifest track, long totalSamples, IReadOnlyList<SttPiece> plan, float gain,
        string folder, Action<long> advance, SttPauseGate? pause, CancellationToken cancellationToken)
    {
        var name = Name(track.Source);
        Directory.CreateDirectory(Path.Combine(folder, name));
        var format = new WaveFormat(SampleRate, 16, 1);
        var open = new Dictionary<int, WaveFileWriter>();
        var bytes = new byte[BlockSamples * 2];
        long position = 0;
        try
        {
            foreach (var block in Mono16k(sessionDirectory, track, totalSamples, pause, cancellationToken))
            {
                var count = 0;
                for (var i = 0; i < block.Length; i++)
                {
                    var v = Math.Clamp(block[i] * gain, -1f, 1f);
                    var s = (short)Math.Round(v * short.MaxValue);
                    bytes[count++] = (byte)s;
                    bytes[count++] = (byte)(s >> 8);
                }
                // Pieces overlap, so one block can go to two files (each gets only its own range).
                var blockStart = position;
                var blockEnd = position + block.Length;
                foreach (var piece in plan)
                {
                    var from = piece.StartMs * SampleRate / 1000;
                    var to = piece.EndMs * SampleRate / 1000;
                    if (to <= blockStart || from >= blockEnd) continue;
                    if (!open.TryGetValue(piece.Index, out var writer))
                    {
                        writer = new WaveFileWriter(Path.Combine(folder, name, PieceFile(piece)), format);
                        open[piece.Index] = writer;
                    }
                    var a = Math.Max(from, blockStart) - blockStart;
                    var b = Math.Min(to, blockEnd) - blockStart;
                    writer.Write(bytes, (int)a * 2, (int)(b - a) * 2);
                    if (to <= blockEnd)
                    {
                        writer.Dispose();
                        open.Remove(piece.Index);
                    }
                }
                position = blockEnd;
                advance(block.Length);
            }
        }
        finally
        {
            foreach (var writer in open.Values) writer.Dispose();
        }

        return [.. plan.Select(piece =>
        {
            var file = name + "/" + PieceFile(piece);
            return new PieceInfo
            {
                Index = piece.Index,
                StartMs = piece.StartMs,
                EndMs = piece.EndMs,
                PrimaryStartMs = piece.PrimaryStartMs,
                PrimaryEndMs = piece.PrimaryEndMs,
                File = file,
                Sha256 = HashFile(Path.Combine(folder, file)),
            };
        })];
    }

    private static string PieceFile(SttPiece piece) => "part-" + piece.Index.ToString("0000", CultureInfo.InvariantCulture) + ".wav";

    private static SttInput? TryReuse(string folder, string inputHash)
    {
        var path = Path.Combine(folder, InfoFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var info = JsonSerializer.Deserialize<InputInfo>(File.ReadAllBytes(path), VoiceJournalJson.Options);
            if (info is null || info.InputHash != inputHash || info.Conversion != ConversionVersion) return null;
            foreach (var source in info.Sources)
            {
                if (!File.Exists(Path.Combine(folder, source.LevelFile))) return null;
                foreach (var piece in source.Pieces)
                {
                    SessionPaths.EnsureSafeRelativePath(piece.File);
                    var file = Path.Combine(folder, piece.File);
                    if (!File.Exists(file) || HashFile(file) != piece.Sha256) return null;
                }
            }
            return ToInput(info, folder);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static SttInput ToInput(InputInfo info, string folder)
    {
        var levels = info.Sources.ToDictionary(s => s.Source, s => File.ReadAllBytes(Path.Combine(folder, s.LevelFile)));
        return new SttInput
        {
            InputHash = info.InputHash,
            Directory = folder,
            DurationMs = info.DurationMs,
            GainDb = info.Sources.ToDictionary(s => s.Source, s => s.GainDb),
            Pieces = [.. info.Sources.SelectMany(s => s.Pieces.Select(p => new SttInputPiece(
                s.Source,
                new SttPiece(p.Index, p.StartMs, p.EndMs, p.PrimaryStartMs, p.PrimaryEndMs),
                Path.Combine(folder, p.File.Replace('/', Path.DirectorySeparatorChar)),
                p.Sha256)))],
            LevelDbfs = (source, start, end) => levels.TryGetValue(source, out var l) ? AverageLevel(l, start, end) : null,
        };
    }

    /// <summary>Average RMS level (dBFS) of the 100 ms windows overlapping [start, end).</summary>
    internal static double? AverageLevel(byte[] levels, long startMs, long endMs)
    {
        var from = (int)Math.Clamp(startMs / LevelWindowMs, 0, levels.Length);
        var to = (int)Math.Clamp((endMs + LevelWindowMs - 1) / LevelWindowMs, 0, levels.Length);
        if (to <= from) return null;
        double energy = 0;
        for (var i = from; i < to; i++) energy += Math.Pow(10, unchecked((sbyte)levels[i]) / 10.0);
        return 10 * Math.Log10(energy / (to - from));
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for the next build; the name is unique so it never collides.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Levels: mean square → dBFS, stored as whole dB in [-127, 0].
    private const double MinDbfs = -127;

    private static sbyte ToLevel(double meanSquare) =>
        (sbyte)Math.Clamp(Math.Round(meanSquare <= 0 ? MinDbfs : 10 * Math.Log10(meanSquare)), MinDbfs, 0);

    // Histogram of |sample| in 0.1 dB bins from −127 dBFS to 0 dBFS.
    private const int HistogramBins = 1271;

    private static int Bin(float magnitude)
    {
        if (magnitude <= 0) return 0;
        var db = 20 * Math.Log10(Math.Min(1, magnitude));
        return (int)Math.Clamp(Math.Round((db - MinDbfs) * 10), 0, HistogramBins - 1);
    }

    private static double PercentileDbfs(long[] histogram, double percentile)
    {
        var total = histogram.Sum();
        if (total == 0) return MinDbfs;
        var rank = (long)Math.Ceiling(total * percentile);
        long seen = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= rank) return i == 0 ? MinDbfs : MinDbfs + i / 10.0;
        }
        return 0;
    }

    private sealed class AverageDownmix(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var needed = buffer.Length * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = source.Read(_buffer.AsSpan(0, needed));
            var frames = read / _channels;
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
                buffer[f] = sum / _channels;
            }
            return frames;
        }
    }

    internal sealed record ChunkRef(int Sequence, string Sha256);

    internal sealed class PieceInfo
    {
        public required int Index { get; init; }
        public required long StartMs { get; init; }
        public required long EndMs { get; init; }
        public required long PrimaryStartMs { get; init; }
        public required long PrimaryEndMs { get; init; }
        public required string File { get; init; }
        public required string Sha256 { get; init; }
    }

    internal sealed class SourceInfo
    {
        public required AudioSource Source { get; init; }
        public required double GainDb { get; init; }
        public required double PeakDbfs { get; init; }
        public required string LevelFile { get; init; }
        public required IReadOnlyList<ChunkRef> Chunks { get; init; }
        public required IReadOnlyList<PieceInfo> Pieces { get; init; }
    }

    internal sealed class InputInfo
    {
        public int SchemaVersion { get; init; } = 1;
        public required string InputHash { get; init; }
        public required string Conversion { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public required long DurationMs { get; init; }
        public required int SampleRate { get; init; }
        public required long PieceMs { get; init; }
        public required long OverlapMs { get; init; }
        public required IReadOnlyList<SourceInfo> Sources { get; init; }
    }
}
