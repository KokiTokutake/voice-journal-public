using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Transcription;

internal sealed class FakeSttInputBuilder : ISttInputBuilder
{
    public long DurationMs { get; set; } = 70_000;

    public int Builds { get; private set; }

    public List<(AudioSource Source, TimeRange Range)> Extracts { get; } = [];

    public Task<SttInput> BuildAsync(string sessionDirectory, SttConfiguration configuration, IProgress<double>? progress, CancellationToken cancellationToken, SttPauseGate? pause = null)
    {
        Builds++;
        cancellationToken.ThrowIfCancellationRequested();
        var plan = SttPiecePlanner.Plan(DurationMs, configuration.PieceMs, configuration.OverlapMs);
        progress?.Report(1);
        return Task.FromResult(new SttInput
        {
            InputHash = new string('d', 64),
            Directory = sessionDirectory,
            DurationMs = DurationMs,
            Pieces = [.. new[] { AudioSource.Mic, AudioSource.Loopback }.SelectMany(s => plan.Select(p => new SttInputPiece(s, p, $"{s}-{p.Index}.wav", "x")))],
            GainDb = new Dictionary<AudioSource, double>(),
            LevelDbfs = (_, _, _) => null,
        });
    }

    public Task ExtractAsync(SttInput input, AudioSource source, TimeRange range, string outputPath, CancellationToken cancellationToken)
    {
        Extracts.Add((source, range));
        File.WriteAllText(outputPath, "clip");
        return Task.CompletedTask;
    }
}

/// <summary>Answers each run with <see cref="OnRun"/>; by default one utterance at 1.0–3.0 s of the audio it was given.</summary>
internal sealed class FakeSttEngine : ISttEngine
{
    private readonly Lock _gate = new();

    public Func<SttRunRequest, SttRunResult>? OnRun { get; set; }

    public List<SttRunRequest> Runs { get; } = [];

    public Task<SttEngineInfo> VerifyAsync(SttConfiguration configuration, CancellationToken cancellationToken) =>
        Task.FromResult(new SttEngineInfo("whisper.cpp", "1.9.5", new string('b', 64), configuration.ModelName, new string('c', 64)));

    public Task<SttRunResult> RunAsync(SttRunRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        lock (_gate) Runs.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.5);
        return Task.FromResult(OnRun?.Invoke(request) ?? Utterance(request, 1_000, 3_000, request.OutputName + " の発話"));
    }

    public static SttRunResult Utterance(SttRunRequest request, long from, long to, string text) =>
        new() { Segments = [new SttRawSegment(from, to, text, 0)], NativeFile = request.OutputName + ".json", Elapsed = TimeSpan.FromSeconds(1) };

    public static SttRunResult Nothing(SttRunRequest request) =>
        new() { Segments = [], NativeFile = request.OutputName + ".json", Elapsed = TimeSpan.FromSeconds(1) };
}

internal static class JobSession
{
    public static readonly SttConfiguration Config = new() { ModelName = "ggml-small.bin", PieceMs = 30_000, OverlapMs = 2_000 };

    /// <summary>A finalized two-source session of 70 s whose chunks cover the whole timeline (no audio files needed).</summary>
    public static (string Dir, Guid Id) Create(string parent)
    {
        var id = Guid.CreateVersion7();
        var dir = Path.Combine(parent, id.ToString("D"));
        Directory.CreateDirectory(dir);
        var manifest = new SessionManifest
        {
            SessionId = id,
            CaptureMode = CaptureMode.Both,
            CaptureState = CaptureState.Finalized,
            StartedAtUtc = new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero),
            TimeZoneId = "Tokyo Standard Time",
        };
        foreach (var source in new[] { AudioSource.Mic, AudioSource.Loopback })
        {
            var track = new TrackManifest
            {
                TrackId = Guid.NewGuid(),
                Source = source,
                DeviceId = "d",
                DeviceName = "テスト",
                Format = AudioFormatInfo.IeeeFloat32Bit(48_000, 1),
                State = TrackState.Stopped,
            };
            track.Chunks.Add(new ChunkManifest { Sequence = 1, File = $"audio/{source.ToString().ToLowerInvariant()}/000001.wav", StartMs = 0, FrameCount = 70L * 48_000, State = ChunkState.Completed, Sha256 = new string('e', 64) });
            manifest.Tracks.Add(track);
        }
        manifest.Save(dir);
        return (dir, id);
    }

    public static SttJob NewJob(Guid sessionId, SttJobKind kind = SttJobKind.Full) => new()
    {
        JobId = Guid.CreateVersion7(),
        SessionId = sessionId,
        Kind = kind,
        Configuration = Config,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    // 30 s: the app-level tests take about 11 s while the long recording tests run beside them.
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 30_000)
    {
        var deadline = System.Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (System.Environment.TickCount64 > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }
}

public sealed class SttJobProcessorTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeSttInputBuilder _builder = new();
    private readonly FakeSttEngine _engine = new();
    private readonly SttJobProcessor _processor;
    private readonly string _session;
    private readonly Guid _sessionId;

    public SttJobProcessorTests()
    {
        _processor = new SttJobProcessor(_builder, _engine, new ManualTimeProvider());
        (_session, _sessionId) = JobSession.Create(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private async Task<SttJob> Run(SttJob job, CancellationToken token = default)
    {
        SttJobStore.Save(_session, job);
        await _processor.RunAsync(_session, job, new SttPauseGate(), null, token);
        return job;
    }

    [Fact]
    public async Task FullJob_TranscribesEveryPiece_AndWritesOneRawRevisionWithProvenance()
    {
        var job = await Run(JobSession.NewJob(_sessionId));

        Assert.Equal(ProcessingState.Transcribed, job.State);
        Assert.Equal(6, _engine.Runs.Count); // 3 pieces × 2 sources
        Assert.Equal(1, job.ResultRevision);
        Assert.All(job.Pieces, p => Assert.Equal(SttPieceState.Done, p.State));
        var doc = new TranscriptStore(_session).Load(1);
        Assert.Equal(TranscriptRevisionKind.Raw, doc.Kind);
        Assert.Equal(job.JobId, doc.Provenance!.JobId);
        Assert.Equal(new string('c', 64), doc.Provenance.ModelSha256);
        Assert.Equal(JobSession.Config.ComputeHash(), doc.Provenance.ConfigHash);
        Assert.Equal(new string('d', 64), doc.Provenance.InputHash);
        // One utterance per piece, shifted to session time: 1 s, 29 s, 59 s on both sources.
        Assert.Equal([1_000L, 1_000, 29_000, 29_000, 59_000, 59_000], doc.Segments.Select(s => s.StartMs));
        Assert.All(doc.Segments, s => Assert.Empty(s.QualityFlags));
        Assert.StartsWith($"stt/native/{job.JobId:D}/mic-0001.json#", doc.Segments.First(s => s.Source == AudioSource.Mic).NativeRef, StringComparison.Ordinal);
        Assert.Equal(job.JobId, SttJobStore.Load(_session, job.JobId).JobId);
        Assert.Equal(ProcessingState.Transcribed, SttJobStore.SessionState(SttJobStore.List(_session)));
    }

    [Fact]
    public async Task Preempted_KeepsFinishedPieces_AndTheNextRunDoesOnlyTheRest()
    {
        var calls = 0;
        _engine.OnRun = r => ++calls == 3 ? throw new SttException(SttErrorCode.Preempted, "recording") : FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        var job = await Run(JobSession.NewJob(_sessionId));
        Assert.Equal(ProcessingState.Queued, job.State);
        Assert.Equal(2, job.Pieces.Count(p => p.State == SttPieceState.Done));

        _engine.Runs.Clear();
        _engine.OnRun = null;
        await _processor.RunAsync(_session, job, new SttPauseGate(), null, CancellationToken.None);

        Assert.Equal(ProcessingState.Transcribed, job.State);
        Assert.Equal(4, _engine.Runs.Count);
        Assert.DoesNotContain(_engine.Runs, r => r.OutputName is "mic-0001" or "loopback-0001");
        Assert.Equal(2, job.Attempt);
    }

    [Fact]
    public async Task FailedRun_IsRetriedOnce_ThenTheJobFailsWithTheCode()
    {
        var failures = 0;
        _engine.OnRun = r => r.OutputName == "mic-0002" && failures++ < 1 ? throw new SttException("exit 3") : FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        var retried = await Run(JobSession.NewJob(_sessionId));
        Assert.Equal(ProcessingState.Transcribed, retried.State);
        Assert.Equal(2, retried.Pieces.Single(p => p is { Source: AudioSource.Mic, Index: 2 }).Attempts);

        _engine.OnRun = r => r.OutputName == "loopback-0001" ? throw new SttException("exit 3") : FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        var failed = await Run(JobSession.NewJob(_sessionId));
        Assert.Equal(ProcessingState.Failed, failed.State);
        Assert.Equal(SttErrorCode.RunFailed, failed.Error);
        Assert.Equal(1, failed.Pieces.Count(p => p.State == SttPieceState.Done)); // mic-0001 is kept for a retry
        Assert.Equal([1], new TranscriptStore(_session).Revisions()); // no revision from the failed job
    }

    [Fact]
    public async Task MissingTool_FailsWithoutRetrying()
    {
        _engine.OnRun = _ => throw new SttException(SttErrorCode.ToolMissing, "model missing");
        var job = await Run(JobSession.NewJob(_sessionId));

        Assert.Equal(ProcessingState.Failed, job.State);
        Assert.Equal(SttErrorCode.ToolMissing, job.Error);
        Assert.Single(_engine.Runs);
    }

    [Fact]
    public async Task PieceWithNothingRecognized_IsReportedAsUnrecognized()
    {
        _engine.OnRun = r => r.OutputName == "loopback-0002" ? FakeSttEngine.Nothing(r) : FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        var job = await Run(JobSession.NewJob(_sessionId));

        Assert.Equal(SttPieceState.NoSpeech, job.Pieces.Single(p => p is { Source: AudioSource.Loopback, Index: 2 }).State);
        var doc = new TranscriptStore(_session).Load(1);
        Assert.Equal([new TimeRange(30_000, 60_000)], doc.Sources.Single(s => s.Source == AudioSource.Loopback).UnrecognizedRanges);
        Assert.Empty(doc.Sources.Single(s => s.Source == AudioSource.Mic).UnrecognizedRanges);
    }

    [Fact]
    public async Task ResumingAfterTheRevisionWasWritten_AdoptsIt_InsteadOfWritingItTwice()
    {
        var job = await Run(JobSession.NewJob(_sessionId));
        // As if the app died right after saving the revision: the ledger still says Transcribing.
        job.State = ProcessingState.Queued;
        job.ResultRevision = null;
        SttJobStore.Save(_session, job);

        await _processor.RunAsync(_session, job, new SttPauseGate(), null, CancellationToken.None);

        Assert.Equal(ProcessingState.Transcribed, job.State);
        Assert.Equal(1, job.ResultRevision);
        Assert.Equal([1], new TranscriptStore(_session).Revisions());
        Assert.Equal(6, _engine.Runs.Count); // every piece verified, none run again
    }

    [Fact]
    public async Task APieceResultThatNoLongerVerifies_IsRunAgain()
    {
        var job = await Run(JobSession.NewJob(_sessionId));
        var dir = SttJobStore.JobDirectory(_session, job.JobId);
        await File.AppendAllTextAsync(Path.Combine(dir, "mic-0002.segments.json"), " ");
        Directory.Delete(new TranscriptStore(_session).RootDirectory, recursive: true);
        job.State = ProcessingState.Queued;
        _engine.Runs.Clear();

        await _processor.RunAsync(_session, job, new SttPauseGate(), null, CancellationToken.None);

        Assert.Equal(["mic-0002"], _engine.Runs.Select(r => r.OutputName));
        Assert.Equal(ProcessingState.Transcribed, job.State);
    }

    [Fact]
    public async Task Cancelling_EndsTheJobAsCancelled_AndKeepsWhatWasDone()
    {
        using var cts = new CancellationTokenSource();
        _engine.OnRun = r =>
        {
            if (r.OutputName == "mic-0002") cts.Cancel();
            return FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        };
        var job = await Run(JobSession.NewJob(_sessionId), cts.Token);

        Assert.Equal(ProcessingState.Cancelled, job.State);
        Assert.Equal(SttErrorCode.Cancelled, job.Error);
        Assert.Empty(new TranscriptStore(_session).Revisions());
    }

    [Fact]
    public async Task RangeJob_ReRecognizesTheRange_IntoANewRevision()
    {
        await Run(JobSession.NewJob(_sessionId));
        var range = new TimeRange(28_500, 32_000);
        // The clip starts 1 s earlier (padding); answer with one utterance inside the range and one in the padding.
        _engine.OnRun = r => new SttRunResult
        {
            Segments = [new SttRawSegment(0, 400, "前の余白", 0), new SttRawSegment(1_200, 3_800, "直した発話", 1)],
            NativeFile = "range.json",
            Elapsed = TimeSpan.FromSeconds(1),
        };
        var job = JobSession.NewJob(_sessionId, SttJobKind.Range);
        job = new SttJob
        {
            JobId = job.JobId,
            SessionId = _sessionId,
            Kind = SttJobKind.Range,
            Configuration = JobSession.Config with { ModelName = "ggml-medium.bin" },
            CreatedAtUtc = job.CreatedAtUtc,
            RangeSource = AudioSource.Mic,
            Range = range,
            ParentRevision = 1,
        };
        await Run(job);

        Assert.Equal(ProcessingState.Transcribed, job.State);
        Assert.Equal([(AudioSource.Mic, new TimeRange(27_500, 33_000))], _builder.Extracts);
        var store = new TranscriptStore(_session);
        Assert.Equal([1, 2], store.Revisions());
        var next = store.Load(2);
        var mic = next.Segments.Where(s => s.Source == AudioSource.Mic && range.Overlaps(s.StartMs, s.EndMs)).ToList();
        Assert.Equal("直した発話", Assert.Single(mic).Text);
        Assert.Equal(28_700, mic[0].StartMs);
        Assert.Equal(job.JobId, next.Changes.Single().Provenance!.JobId);
        Assert.Equal("ggml-medium.bin", next.Changes.Single().Provenance!.ModelName);
        Assert.Equal(3, next.Segments.Count(s => s.Source == AudioSource.Loopback)); // other source untouched
    }

    [Fact]
    public async Task RangeJob_BuildsOnTheRevisionInUse_NotOnANewerRawThatIsNotUsed()
    {
        await Run(JobSession.NewJob(_sessionId));
        var store = new TranscriptStore(_session);
        var edited = store.Load(1).Segments.First(s => s.Source == AudioSource.Mic && s.StartMs == 1_000);
        store.Save(TranscriptEditing.EditText(store.Load(1), 2, edited.SegmentId, "利用者の修正", DateTimeOffset.UnixEpoch));
        var late = await Run(JobSession.NewJob(_sessionId)); // e.g. resumed after a restart
        Assert.Equal(3, late.ResultRevision);
        Assert.Equal(2, store.CurrentRevision());

        _engine.OnRun = r => FakeSttEngine.Utterance(r, 1_200, 3_000, "再認識");
        var job = new SttJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = _sessionId,
            Kind = SttJobKind.Range,
            Configuration = JobSession.Config,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            RangeSource = AudioSource.Mic,
            Range = new TimeRange(28_500, 32_000),
            ParentRevision = 2,
        };
        await Run(job);

        Assert.Equal(ProcessingState.Transcribed, job.State);
        Assert.Equal(4, job.ResultRevision);
        var next = store.Load(4);
        Assert.Equal(2, next.ParentRevision);
        Assert.Equal(1, next.RawRevision);
        Assert.Equal("利用者の修正", next.Find(edited.SegmentId)!.Text);
        Assert.Equal(4, store.CurrentRevision());
    }

    [Fact]
    public async Task RangeJob_DoesNotOverwrite_WhenTheRangeWasEditedMeanwhile()
    {
        await Run(JobSession.NewJob(_sessionId));
        var store = new TranscriptStore(_session);
        var target = store.Load(1).Segments.First(s => s.Source == AudioSource.Mic && s.StartMs == 29_000);
        _engine.OnRun = r =>
        {
            // The user edits the same utterance while the recognizer runs.
            store.Save(TranscriptEditing.EditText(store.Load(1), 2, target.SegmentId, "利用者の修正", DateTimeOffset.UnixEpoch));
            return FakeSttEngine.Utterance(r, 1_200, 3_000, "再認識");
        };
        var job = new SttJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = _sessionId,
            Kind = SttJobKind.Range,
            Configuration = JobSession.Config,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            RangeSource = AudioSource.Mic,
            Range = new TimeRange(28_500, 32_000),
            ParentRevision = 1,
        };
        await Run(job);

        Assert.Equal(ProcessingState.Failed, job.State);
        Assert.Equal(SttErrorCode.Conflict, job.Error);
        Assert.Equal([1, 2], store.Revisions());
        Assert.Equal("利用者の修正", store.Load(2).Find(target.SegmentId)!.Text);
    }
}

public sealed class SttJobRunnerTests : IAsyncDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeSttInputBuilder _builder = new() { DurationMs = 20_000 }; // one piece per source
    private readonly FakeSttEngine _engine = new();
    private readonly SttJobRunner _runner;
    private readonly List<(Guid JobId, ProcessingState State)> _changes = [];
    private readonly Lock _gate = new();

    public SttJobRunnerTests()
    {
        _runner = new SttJobRunner(new SttJobProcessor(_builder, _engine));
        // The job object keeps changing; record the state at the time of each notification.
        _runner.JobChanged += (_, c) => { lock (_gate) _changes.Add((c.Job.JobId, c.Job.State)); };
    }

    public async ValueTask DisposeAsync()
    {
        await _runner.DisposeAsync();
        _dir.Dispose();
    }

    private (string Dir, Guid Id) Session() => JobSession.Create(_dir.Path);

    private static ProcessingState StateOf(string dir, SttJob job) => SttJobStore.Load(dir, job.JobId).State;

    [Fact]
    public async Task RunsJobsOneAtATime_InTheOrderTheyWereQueued()
    {
        var running = 0;
        var overlap = false;
        var order = new List<Guid>();
        _engine.OnRun = r =>
        {
            if (Interlocked.Increment(ref running) > 1) overlap = true;
            Thread.Sleep(30);
            Interlocked.Decrement(ref running);
            return FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        };
        var (a, aId) = Session();
        var (b, bId) = Session();
        var first = _runner.Enqueue(a, aId, JobSession.Config);
        var second = _runner.Enqueue(b, bId, JobSession.Config);

        await JobSession.Until(() => StateOf(b, second) == ProcessingState.Transcribed, "second job");

        Assert.False(overlap);
        Assert.Equal(ProcessingState.Transcribed, StateOf(a, first));
        lock (_gate)
        {
            var finished = _changes.Where(c => c.State == ProcessingState.Transcribed).Select(c => c.JobId).ToList();
            Assert.Equal([first.JobId, second.JobId], finished);
        }
    }

    [Fact]
    public async Task WhileRecording_NoJobStarts_AndItStartsWhenTheRecordingEnds()
    {
        _runner.SetRecording(true);
        var (dir, id) = Session();
        var job = _runner.Enqueue(dir, id, JobSession.Config);

        await Task.Delay(300);
        Assert.Empty(_engine.Runs);
        Assert.Equal(ProcessingState.Queued, StateOf(dir, job));

        _runner.SetRecording(false);
        await JobSession.Until(() => StateOf(dir, job) == ProcessingState.Transcribed, "job after recording");
    }

    [Fact]
    public async Task WhileTheQueueIsHeld_NoJobStarts_AndItStartsWhenTheHoldIsLifted() // UI-03 キュー保留
    {
        _runner.SetHeld(true);
        var (dir, id) = Session();
        var job = _runner.Enqueue(dir, id, JobSession.Config);

        await Task.Delay(300);
        Assert.True(_runner.IsHeld);
        Assert.Empty(_engine.Runs);
        Assert.Equal(ProcessingState.Queued, StateOf(dir, job));
        _runner.SetRecording(true);
        _runner.SetRecording(false); // the end of a recording does not lift the hold
        await Task.Delay(200);
        Assert.Empty(_engine.Runs);

        _runner.SetHeld(false);
        await JobSession.Until(() => StateOf(dir, job) == ProcessingState.Transcribed, "job after the hold");
    }

    [Fact]
    public async Task ARecordingPausesTheRunningRecognizer_ThroughTheGate()
    {
        using var started = new ManualResetEventSlim();
        var sawPause = false;
        _engine.OnRun = r =>
        {
            started.Set();
            var deadline = System.Environment.TickCount64 + 5_000;
            while (!r.Pause!.IsPaused && System.Environment.TickCount64 < deadline) Thread.Sleep(10);
            sawPause = r.Pause.IsPaused;
            while (r.Pause.IsPaused && System.Environment.TickCount64 < deadline) Thread.Sleep(10);
            return FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        };
        var (dir, id) = Session();
        var job = _runner.Enqueue(dir, id, JobSession.Config);
        Assert.True(started.Wait(5_000));

        _runner.SetRecording(true);
        await Task.Delay(100);
        _runner.SetRecording(false);
        _engine.OnRun = null;

        await JobSession.Until(() => StateOf(dir, job) == ProcessingState.Transcribed, "paused job");
        Assert.True(sawPause);
    }

    [Fact]
    public async Task AJobPreemptedByARecording_RunsFirst_WhenTheRecordingEnds()
    {
        var preempt = true;
        _engine.OnRun = r =>
        {
            if (preempt)
            {
                preempt = false;
                _runner.SetRecording(true);
                throw new SttException(SttErrorCode.Preempted, "could not pause");
            }
            return FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        };
        var (a, aId) = Session();
        var (b, bId) = Session();
        var first = _runner.Enqueue(a, aId, JobSession.Config);
        await JobSession.Until(() => StateOf(a, first) == ProcessingState.Queued && _engine.Runs.Count == 1, "preempted");
        var second = _runner.Enqueue(b, bId, JobSession.Config);

        _runner.SetRecording(false);
        await JobSession.Until(() => StateOf(b, second) == ProcessingState.Transcribed, "both jobs");

        Assert.Equal(ProcessingState.Transcribed, StateOf(a, first));
        Assert.Equal("mic-0001", _engine.Runs[1].OutputName); // the preempted job's piece came back first
        Assert.Equal(a, Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_engine.Runs[1].OutputDirectory))));
    }

    [Fact]
    public async Task Cancel_RemovesAQueuedJob_AndMarksItCancelled()
    {
        _runner.SetRecording(true); // keep everything queued
        var (dir, id) = Session();
        var job = _runner.Enqueue(dir, id, JobSession.Config);

        _runner.Cancel(dir, job.JobId);
        _runner.SetRecording(false);
        await Task.Delay(300);

        Assert.Equal(ProcessingState.Cancelled, StateOf(dir, job));
        Assert.Empty(_engine.Runs);
    }

    [Fact]
    public async Task ClosingTheApp_PutsTheRunningJobBackInTheQueue_ForTheNextStart()
    {
        using var started = new ManualResetEventSlim();
        _engine.OnRun = r =>
        {
            started.Set();
            Thread.Sleep(300);
            throw new OperationCanceledException();
        };
        var (dir, id) = Session();
        var job = _runner.Enqueue(dir, id, JobSession.Config);
        Assert.True(started.Wait(5_000));

        await _runner.DisposeAsync();

        var saved = SttJobStore.Load(dir, job.JobId);
        Assert.Equal(ProcessingState.Queued, saved.State);
        Assert.Null(saved.Error);
    }

    [Fact]
    public async Task Resume_RequeuesUnfinishedJobs_FoundAtStartup()
    {
        var (dir, id) = Session();
        var job = JobSession.NewJob(id);
        job.State = ProcessingState.Transcribing; // the app died mid-job
        SttJobStore.Save(dir, job);

        _runner.Resume([new SttJobChange(dir, job)]);

        await JobSession.Until(() => StateOf(dir, job) == ProcessingState.Transcribed, "resumed job");
        Assert.Equal(1, SttJobStore.Load(dir, job.JobId).Attempt);
    }
}
