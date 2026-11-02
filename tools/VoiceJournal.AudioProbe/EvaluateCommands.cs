// Phase C5 evaluation: runs the app's transcription pipeline (SttJobProcessor + SttInputBuilder + WhisperCliEngine)
// on a recorded session, and scores a revision against reference scripts.
//
//   AudioProbe transcribe <sessionDir> [--model small|medium] [--no-vad] [--no-gain] [--threads <n>] [--app-root <dir>] [--out <json>]
//       One full job, exactly as the app queues it; every run produces a new raw revision of the session (AT-26).
//       Prints progress, then a JSON summary with times, hashes and quality-flag counts (no transcript text).
//
//   AudioProbe cer <sessionDir> --refs <file>;<file>... [--marks <ms>;<ms>...] [--revision <n>] [--source mic|loopback]
//                  [--hyp-out <file>] [--out <json>]
//       CER (detail spec 22.1, normalization fixed in CerNormalizer) of one revision. With several references,
//       --marks are the session times where each script ended (Enter presses of `session --prompt-files`); a segment
//       belongs to the script during which it started. Also reports the CER of all scripts joined.
//       --hyp-out writes the recognized text per script, for reading the errors (verification data only).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.AudioProbe;

internal static class EvaluateCommands
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static async Task<int> TranscribeAsync(string[] args, JsonSerializerOptions json)
    {
        var directory = Path.GetFullPath(args[1]);
        var appRoot = Option(args, "--app-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceJournal");
        var defaults = new SttConfiguration { ModelName = ModelFile("small") };
        var configuration = defaults with
        {
            ModelName = ModelFile(Option(args, "--model") ?? "small"),
            UseVad = !args.Contains("--no-vad"),
            NormalizeGain = !args.Contains("--no-gain"),
            Threads = Option(args, "--threads") is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : defaults.Threads,
        };

        var manifest = SessionManifest.Load(directory);
        var store = new TranscriptStore(directory);
        var processor = new SttJobProcessor(new SttInputBuilder(), new WhisperCliEngine(SttToolSettings.LoadOrCreate(appRoot)));
        var job = new SttJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = manifest.SessionId,
            Kind = SttJobKind.Full,
            Configuration = configuration,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        SttJobStore.Save(directory, job);
        Console.WriteLine($"transcribe: model={configuration.ModelName} vad={configuration.UseVad} gain={configuration.NormalizeGain} threads={configuration.Threads} job={job.JobId}");

        var clock = Stopwatch.StartNew();
        var shown = string.Empty;
        var progress = new InlineProgress<SttJobProgress>(p =>
        {
            var step = $"{p.State} {p.Source} {p.PieceNumber}/{p.PieceCount} {(int)(p.Fraction * 10) * 10}%";
            if (step == shown) return;
            shown = step;
            Console.WriteLine($"[{clock.Elapsed:mm\\:ss}] {step}");
        });
        await processor.RunAsync(directory, job, new SttPauseGate(), progress, CancellationToken.None).ConfigureAwait(false);
        var wallMs = clock.ElapsedMilliseconds;

        object? result = null;
        if (job.State == ProcessingState.Transcribed && job.ResultRevision is { } revision)
        {
            var document = store.Load(revision);
            var input = ReadInputInfo(directory, job.InputHash!);
            var audioMs = input?.GetProperty("duration_ms").GetInt64() ?? 0;
            result = new
            {
                revision,
                kind = document.Kind,
                provenance = document.Provenance,
                audio_ms = audioMs,
                gain = input?.GetProperty("sources").EnumerateArray().Select(s => new
                {
                    source = s.GetProperty("source").GetString(),
                    gain_db = s.GetProperty("gain_db").GetDouble(),
                    peak_dbfs = s.GetProperty("peak_dbfs").GetDouble(),
                    pieces = s.GetProperty("pieces").GetArrayLength(),
                }),
                transcribe_ms = job.TranscribeElapsedMs,
                rtf_wall = audioMs > 0 ? Math.Round((double)wallMs / audioMs, 3) : (double?)null,
                rtf_transcribe = audioMs > 0 ? Math.Round((double)job.TranscribeElapsedMs / audioMs, 3) : (double?)null,
                segments = document.Segments.Count,
                flagged_segments = document.Segments.Count(s => s.QualityFlags.Count > 0),
                flags = Enum.GetValues<SegmentQuality>().ToDictionary(f => f.ToString(), f => document.Segments.Count(s => s.Has(f))),
                unrecognized_ranges = document.Sources.Sum(s => s.UnrecognizedRanges.Count),
                characters = document.Segments.Sum(s => s.Text.Length),
            };
        }

        var summary = new
        {
            session_directory = directory,
            job_id = job.JobId,
            state = job.State,
            error = job.Error,
            error_detail = job.ErrorDetail,
            configuration = new
            {
                model = configuration.ModelName,
                vad = configuration.UseVad,
                gain = configuration.NormalizeGain,
                threads = configuration.Threads,
                config_hash = configuration.ComputeHash(),
            },
            input_hash = job.InputHash,
            engine = job.Engine,
            wall_ms = wallMs,
            result,
        };
        var text = JsonSerializer.Serialize(summary, json);
        Console.WriteLine(text);
        if (Option(args, "--out") is { } outFile) File.WriteAllText(outFile, text, Utf8);
        return result is null ? 3 : 0;
    }

    public static int Cer(string[] args, JsonSerializerOptions json)
    {
        var directory = Path.GetFullPath(args[1]);
        var refs = (Option(args, "--refs") ?? throw new ArgumentException("--refs is required"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var marks = (Option(args, "--marks") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => long.Parse(m, CultureInfo.InvariantCulture)).ToArray();
        if (refs.Length > 1 && marks.Length < refs.Length - 1)
            throw new ArgumentException($"{refs.Length} references need at least {refs.Length - 1} marks");
        var source = (Option(args, "--source") ?? "mic") switch
        {
            "mic" => AudioSource.Mic,
            "loopback" => AudioSource.Loopback,
            var other => throw new ArgumentException($"unknown source '{other}'"),
        };

        var store = new TranscriptStore(directory);
        var document = Option(args, "--revision") is { } r ? store.Load(int.Parse(r, CultureInfo.InvariantCulture))
            : store.LoadLatest() ?? throw new InvalidOperationException("the session has no transcript");
        var segments = document.Segments.Where(s => s.Source == source).OrderBy(s => s.StartMs).ToList();

        int ScriptOf(TranscriptSegment s)
        {
            for (var i = 0; i < refs.Length - 1; i++)
                if (s.StartMs < marks[i]) return i;
            return refs.Length - 1;
        }

        var hypotheses = Enumerable.Range(0, refs.Length).Select(_ => new StringBuilder()).ToArray();
        var counts = new int[refs.Length];
        var flagged = new int[refs.Length];
        foreach (var segment in segments)
        {
            var i = ScriptOf(segment);
            hypotheses[i].AppendLine(segment.Text);
            counts[i]++;
            if (segment.QualityFlags.Count > 0) flagged[i]++;
        }

        var references = refs.Select(f => File.ReadAllText(f, Utf8)).ToArray();
        var scripts = new List<object>();
        for (var i = 0; i < refs.Length; i++)
        {
            var cer = CharacterErrorRate.Compute(references[i], hypotheses[i].ToString());
            scripts.Add(new { reference = Path.GetFileName(refs[i]), segments = counts[i], flagged_segments = flagged[i], cer = Describe(cer) });
        }
        var all = CharacterErrorRate.Compute(string.Concat(references), string.Concat(hypotheses.Select(h => h.ToString())));

        if (Option(args, "--hyp-out") is { } hypOut)
        {
            var text = new StringBuilder();
            for (var i = 0; i < refs.Length; i++)
                text.Append(CultureInfo.InvariantCulture, $"### {Path.GetFileName(refs[i])}\n{hypotheses[i]}\n");
            File.WriteAllText(hypOut, text.ToString(), Utf8);
        }

        var summary = new
        {
            session_directory = directory,
            revision = document.Revision,
            kind = document.Kind,
            raw_revision = document.RawRevision,
            source,
            marks_ms = marks,
            scripts,
            all = Describe(all),
        };
        var json2 = JsonSerializer.Serialize(summary, json);
        Console.WriteLine(json2);
        if (Option(args, "--out") is { } outFile) File.WriteAllText(outFile, json2, Utf8);
        return 0;
    }

    private static object Describe(CerResult r) => new
    {
        rate = Math.Round(r.Rate, 4),
        reference_chars = r.ReferenceLength,
        errors = r.Errors,
        substitutions = r.Substitutions,
        deletions = r.Deletions,
        insertions = r.Insertions,
    };

    private static JsonElement? ReadInputInfo(string sessionDirectory, string inputHash)
    {
        var path = Path.Combine(sessionDirectory, SttInputBuilder.DerivedFolder.Replace('/', Path.DirectorySeparatorChar), inputHash[..16], SttInputBuilder.InfoFileName);
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }

    /// <summary>"small" → "ggml-small.bin" (models are registered by file name in config\stt.json).</summary>
    private static string ModelFile(string name) => name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? name : $"ggml-{name}.bin";

    private static string? Option(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
