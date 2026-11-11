using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Core.Transcription;

/// <summary>Progress of the running job, for the processing screen.</summary>
public sealed record SttJobProgress(Guid JobId, Guid SessionId, SttJobKind Kind, ProcessingState State, double Fraction, int PieceNumber, int PieceCount, AudioSource? Source);

/// <summary>
/// Runs one job to its end (detail spec 7, 8): Normalizing (16 kHz input) → Transcribing (piece by piece, each result
/// saved with a hash) → merge, quality flags → a new transcript revision. Resumes from the verified pieces. Never
/// touches the original audio. Returns with the job in its final state; a preempted job comes back as Queued.
/// </summary>
public sealed class SttJobProcessor
{
    /// <summary>A failed recognizer run is retried once (detail spec 19: at most 2 retries for a temporary failure).</summary>
    public const int RunsPerPiece = 2;

    private const long RangePaddingMs = 1_000;

    private readonly ISttInputBuilder _builder;
    private readonly ISttEngine _engine;
    private readonly TimeProvider _time;

    public SttJobProcessor(ISttInputBuilder builder, ISttEngine engine, TimeProvider? time = null)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _time = time ?? TimeProvider.System;
    }

    public async Task RunAsync(string sessionDirectory, SttJob job, SttPauseGate pause, IProgress<SttJobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(pause);
        job.Attempt++;
        job.StartedAtUtc ??= _time.GetUtcNow();
        job.Error = null;
        job.ErrorDetail = null;
        try
        {
            Move(sessionDirectory, job, ProcessingState.Normalizing);
            var normalizing = new InlineProgress<double>(f => progress?.Report(new SttJobProgress(job.JobId, job.SessionId, job.Kind, ProcessingState.Normalizing, f, 0, 0, null)));
            var input = await _builder.BuildAsync(sessionDirectory, job.Configuration, normalizing, cancellationToken, pause).ConfigureAwait(false);
            if (job.InputHash is not null && job.InputHash != input.InputHash) job.Pieces.Clear(); // other audio: nothing to reuse
            job.InputHash = input.InputHash;
            job.Engine = await _engine.VerifyAsync(job.Configuration, cancellationToken).ConfigureAwait(false);
            Move(sessionDirectory, job, ProcessingState.Transcribing);

            if (job.Kind == SttJobKind.Full) await RunFullAsync(sessionDirectory, job, input, pause, progress, cancellationToken).ConfigureAwait(false);
            else await RunRangeAsync(sessionDirectory, job, input, pause, progress, cancellationToken).ConfigureAwait(false);

            job.EndedAtUtc = _time.GetUtcNow();
            Move(sessionDirectory, job, ProcessingState.Transcribed);
        }
        catch (SttException ex) when (ex.Code == SttErrorCode.Preempted)
        {
            // Make room for the recording; finished pieces are kept and the job resumes afterwards.
            Move(sessionDirectory, job, ProcessingState.Queued);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is SttException { Code: SttErrorCode.Cancelled })
        {
            Finish(sessionDirectory, job, ProcessingState.Cancelled, SttErrorCode.Cancelled, null);
        }
        catch (SttException ex)
        {
            Finish(sessionDirectory, job, ProcessingState.Failed, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Finish(sessionDirectory, job, ProcessingState.Failed, SttErrorCode.RunFailed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Finish(string sessionDirectory, SttJob job, ProcessingState state, SttErrorCode code, string? detail)
    {
        job.Error = code;
        job.ErrorDetail = detail;
        job.EndedAtUtc = _time.GetUtcNow();
        Move(sessionDirectory, job, state);
    }

    private static void Move(string sessionDirectory, SttJob job, ProcessingState state)
    {
        if (job.State != state) job.State = ProcessingStateMachine.Transition(job.State, state);
        SttJobStore.Save(sessionDirectory, job);
    }

    private static string PieceName(AudioSource source, int index) =>
        source.ToString().ToLowerInvariant() + "-" + index.ToString("0000", CultureInfo.InvariantCulture);

    private async Task RunFullAsync(string sessionDirectory, SttJob job, SttInput input, SttPauseGate pause, IProgress<SttJobProgress>? progress, CancellationToken cancellationToken)
    {
        var jobDir = SttJobStore.JobDirectory(sessionDirectory, job.JobId);
        var inputs = input.Pieces.OrderBy(p => p.Piece.Index).ThenBy(p => p.Source).ToList();
        ReconcilePieces(job, inputs, jobDir);
        SttJobStore.Save(sessionDirectory, job);

        for (var n = 0; n < inputs.Count; n++)
        {
            var inputPiece = inputs[n];
            var piece = job.Pieces.Single(p => p.Source == inputPiece.Source && p.Index == inputPiece.Piece.Index);
            if (piece.State != SttPieceState.Pending) continue;
            var number = n + 1;
            var pieceProgress = new InlineProgress<double>(f => progress?.Report(new SttJobProgress(
                job.JobId, job.SessionId, job.Kind, ProcessingState.Transcribing, (n + f) / inputs.Count, number, inputs.Count, inputPiece.Source)));
            var name = PieceName(inputPiece.Source, inputPiece.Piece.Index);
            var result = await RecognizeAsync(job, piece, new SttRunRequest
            {
                AudioPath = inputPiece.Path,
                OutputDirectory = jobDir,
                OutputName = name,
                Configuration = job.Configuration,
                DurationMs = inputPiece.Piece.DurationMs,
                Pause = pause,
            }, pieceProgress, cancellationToken).ConfigureAwait(false);

            var segments = result.Segments.Select(r => new TranscriptSegment
            {
                SegmentId = Guid.CreateVersion7(),
                Source = inputPiece.Source,
                StartMs = inputPiece.Piece.StartMs + r.StartMs,
                EndMs = inputPiece.Piece.StartMs + r.EndMs,
                Text = r.Text,
                NativeRef = $"{SttJobStore.NativeFolder}/{job.JobId:D}/{result.NativeFile}#{r.NativeIndex}",
            }).ToList();
            var file = name + ".segments.json";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(segments, VoiceJournalJson.Options);
            AtomicFile.WriteAllBytes(Path.Combine(jobDir, file), bytes);
            piece.SegmentsFile = file;
            piece.SegmentsSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            piece.State = segments.Count == 0 ? SttPieceState.NoSpeech : SttPieceState.Done;
            SttJobStore.Save(sessionDirectory, job);
        }

        var store = new TranscriptStore(sessionDirectory);
        // A crash after the revision was written but before the ledger said so: adopt it instead of writing a duplicate.
        if (store.LoadLatest() is { Kind: TranscriptRevisionKind.Raw } latest && latest.Provenance?.JobId == job.JobId)
        {
            store.CompleteRawSave(latest.Revision);
            job.ResultRevision = latest.Revision;
            return;
        }

        var bySource = inputs.GroupBy(p => p.Source).ToList();
        var merged = new List<TranscriptSegment>();
        var sources = new List<TranscriptSourceResult>();
        foreach (var group in bySource)
        {
            var results = group.Select(p => new SttPieceResult(p.Piece, ReadSegments(jobDir, job.Pieces.Single(x => x.Source == p.Source && x.Index == p.Piece.Index)))).ToList();
            merged.AddRange(SttPieceMerger.Merge(results));
            var unrecognized = group.Where(p => job.Pieces.Single(x => x.Source == p.Source && x.Index == p.Piece.Index).State == SttPieceState.NoSpeech)
                .Select(p => new TimeRange(p.Piece.PrimaryStartMs, p.Piece.PrimaryEndMs)).ToList();
            sources.Add(new TranscriptSourceResult { Source = group.Key, UnrecognizedRanges = JoinRanges(unrecognized) });
        }

        var flaggedSegments = TranscriptDocument.Order(QualityFlagger.Apply(TranscriptDocument.Order(merged), Contexts(sessionDirectory, input)));
        var saved = store.SaveNextRaw(revision => new TranscriptDocument
        {
            SessionId = job.SessionId,
            Revision = revision,
            Kind = TranscriptRevisionKind.Raw,
            RawRevision = revision,
            CreatedAtUtc = _time.GetUtcNow(),
            Provenance = Provenance(job),
            Sources = sources,
            Segments = flaggedSegments,
        }, input.DurationMs);
        job.ResultRevision = saved.Revision;
    }

    private async Task RunRangeAsync(string sessionDirectory, SttJob job, SttInput input, SttPauseGate pause, IProgress<SttJobProgress>? progress, CancellationToken cancellationToken)
    {
        if (job.Range is not { } range || job.RangeSource is not { } source || job.ParentRevision is not { } parentRevision)
            throw new InvalidDataException("A range job needs a range, a source and a parent revision.");
        var jobDir = SttJobStore.JobDirectory(sessionDirectory, job.JobId);
        var store = new TranscriptStore(sessionDirectory);
        var current = store.LoadCurrent() ?? throw new InvalidDataException("The session has no transcript to re-recognize.");
        if (current.Changes.Any(c => c.Provenance?.JobId == job.JobId))
        {
            job.ResultRevision = current.Revision; // applied before a crash
            return;
        }

        if (job.Pieces.Count == 0) job.Pieces.Add(new SttJobPiece { Source = source, Index = 0 });
        var piece = job.Pieces[0];
        var clip = new TimeRange(Math.Max(0, range.StartMs - RangePaddingMs), Math.Min(input.DurationMs, range.EndMs + RangePaddingMs));
        var clipPath = Path.Combine(jobDir, "range.wav");
        Directory.CreateDirectory(jobDir);
        await _builder.ExtractAsync(input, source, clip, clipPath, cancellationToken).ConfigureAwait(false);
        var pieceProgress = new InlineProgress<double>(f => progress?.Report(new SttJobProgress(job.JobId, job.SessionId, job.Kind, ProcessingState.Transcribing, f, 1, 1, source)));
        var result = await RecognizeAsync(job, piece, new SttRunRequest
        {
            AudioPath = clipPath,
            OutputDirectory = jobDir,
            OutputName = "range",
            Configuration = job.Configuration,
            DurationMs = clip.DurationMs,
            Pause = pause,
        }, pieceProgress, cancellationToken).ConfigureAwait(false);

        // The padding is only context: keep what is centred in the range, clamped to it.
        var recognized = result.Segments
            .Select(r => (Start: clip.StartMs + r.StartMs, End: clip.StartMs + r.EndMs, r.Text, r.NativeIndex))
            .Where(r => (r.Start + r.End) / 2.0 >= range.StartMs && (r.Start + r.End) / 2.0 < range.EndMs)
            .Select(r => new TranscriptSegment
            {
                SegmentId = Guid.CreateVersion7(),
                Source = source,
                StartMs = Math.Max(range.StartMs, r.Start),
                EndMs = Math.Min(range.EndMs, r.End),
                Text = r.Text,
                NativeRef = $"{SttJobStore.NativeFolder}/{job.JobId:D}/{result.NativeFile}#{r.NativeIndex}",
            })
            .Where(s => s.EndMs > s.StartMs)
            .ToList();
        piece.State = recognized.Count == 0 ? SttPieceState.NoSpeech : SttPieceState.Done;
        SttJobStore.Save(sessionDirectory, job);

        // Re-read: the user may have edited, or chosen another revision, while the recognizer ran. The result goes on
        // top of the revision in use, and only if the range there is still what the user asked to re-recognize.
        current = store.LoadCurrent()!;
        var parent = store.Load(parentRevision);
        if (!SameInRange(parent, current, source, range))
            throw new SttException(SttErrorCode.Conflict, "The range was edited while it was being re-recognized.");
        var flagged = QualityFlagger.Apply(recognized, Contexts(sessionDirectory, input));
        if (source == AudioSource.Mic) flagged = QualityFlagger.WithEcho(flagged, current.Segments);
        var next = TranscriptEditing.ReplaceRange(current, store.NextRevision(), source, range, flagged, Provenance(job), _time.GetUtcNow());
        try
        {
            store.Save(next, input.DurationMs);
        }
        catch (InvalidOperationException ex)
        {
            // Another revision was saved or chosen between the check and the save.
            throw new SttException(SttErrorCode.Conflict, ex.Message);
        }
        job.ResultRevision = next.Revision;
    }

    private async Task<SttRunResult> RecognizeAsync(SttJob job, SttJobPiece piece, SttRunRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        for (var run = 1; ; run++)
        {
            piece.Attempts++;
            try
            {
                var result = await _engine.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
                piece.ElapsedMs += (long)result.Elapsed.TotalMilliseconds;
                return result;
            }
            catch (SttException ex) when (ex.Code == SttErrorCode.RunFailed && run < RunsPerPiece)
            {
                job.ErrorDetail = ex.Message; // kept if the retry fails too; cleared on success at the next start
            }
        }
    }

    /// <summary>Lines the ledger up with the input: keeps finished pieces whose segment file still verifies.</summary>
    private static void ReconcilePieces(SttJob job, IReadOnlyList<SttInputPiece> inputs, string jobDir)
    {
        var existing = job.Pieces.ToDictionary(p => (p.Source, p.Index));
        job.Pieces.Clear();
        foreach (var input in inputs)
        {
            if (!existing.TryGetValue((input.Source, input.Piece.Index), out var piece))
                piece = new SttJobPiece { Source = input.Source, Index = input.Piece.Index };
            if (piece.State != SttPieceState.Pending && !Verifies(jobDir, piece))
            {
                piece.State = SttPieceState.Pending;
                piece.SegmentsFile = null;
                piece.SegmentsSha256 = null;
            }
            job.Pieces.Add(piece);
        }
    }

    private static bool Verifies(string jobDir, SttJobPiece piece)
    {
        if (piece.SegmentsFile is null || piece.SegmentsSha256 is null) return false;
        var path = Path.Combine(jobDir, piece.SegmentsFile);
        if (!File.Exists(path)) return false;
        return Convert.ToHexStringLower(SHA256.HashData(AtomicFile.ReadAllBytes(path))) == piece.SegmentsSha256;
    }

    private static List<TranscriptSegment> ReadSegments(string jobDir, SttJobPiece piece)
    {
        if (!Verifies(jobDir, piece)) throw new InvalidDataException($"The result of piece {piece.Source} {piece.Index} no longer verifies.");
        return JsonSerializer.Deserialize<List<TranscriptSegment>>(AtomicFile.ReadAllBytes(Path.Combine(jobDir, piece.SegmentsFile!)), VoiceJournalJson.Options) ?? [];
    }

    private static Dictionary<AudioSource, TrackQualityContext> Contexts(string sessionDirectory, SttInput input)
    {
        var manifest = SessionManifest.Load(sessionDirectory);
        return manifest.Tracks.ToDictionary(
            t => t.Source,
            t => TrackQualityContext.FromTrack(t, input.DurationMs) with { LevelDbfs = (s, e) => input.LevelDbfs(t.Source, s, e) });
    }

    private static SttProvenance Provenance(SttJob job)
    {
        var engine = job.Engine ?? throw new InvalidOperationException("The engine was not verified.");
        return new SttProvenance
        {
            InputHash = job.InputHash!,
            EngineName = engine.EngineName,
            EngineVersion = engine.EngineVersion,
            EngineSha256 = engine.EngineSha256,
            ModelName = engine.ModelName,
            ModelSha256 = engine.ModelSha256,
            ConfigHash = job.Configuration.ComputeHash(),
            Parameters = job.Configuration.ToParameters(),
            JobId = job.JobId,
        };
    }

    private static bool SameInRange(TranscriptDocument a, TranscriptDocument b, AudioSource source, TimeRange range)
    {
        static IEnumerable<(Guid, string)> Pick(TranscriptDocument d, AudioSource source, TimeRange range) =>
            d.Segments.Where(s => s.Source == source && range.Overlaps(s.StartMs, s.EndMs)).Select(s => (s.SegmentId, s.Text));
        return Pick(a, source, range).SequenceEqual(Pick(b, source, range));
    }

    private static List<TimeRange> JoinRanges(List<TimeRange> ranges)
    {
        var joined = new List<TimeRange>();
        foreach (var r in ranges.OrderBy(r => r.StartMs))
        {
            if (joined.Count > 0 && joined[^1].EndMs >= r.StartMs) joined[^1] = joined[^1] with { EndMs = Math.Max(joined[^1].EndMs, r.EndMs) };
            else joined.Add(r);
        }
        return joined;
    }
}

/// <summary>Unlike <see cref="Progress{T}"/>, reports on the calling thread, in order (no synchronization context).</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
