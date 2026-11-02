using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Analysis;

public sealed class RecordJobTests : IAsyncDisposable
{
    private static readonly GenerationSettings Settings = new() { ModelId = "gemma-3-4b-it", ContextLength = 8192, OutputReserve = 2048 };

    private readonly TempDirectory _dir = new();
    private readonly EchoLlm _llm = new();
    private readonly InferenceGate _gate = new();
    private readonly RecordJobRunner _runner;
    private readonly List<ProcessingState> _states = [];

    public RecordJobTests()
    {
        var generator = new RecordGenerator(_llm, new AnalysisSchemaValidator(), TimeProvider.System);
        _runner = new RecordJobRunner(new RecordJobProcessor(generator, TimeProvider.System), _gate);
        _runner.JobChanged += (_, c) => { lock (_states) _states.Add(c.Job.State); };
    }

    private string Session(int segments = 400)
    {
        var dir = Path.Combine(_dir.Path, "s1");
        Directory.CreateDirectory(dir);
        var manifest = new SessionManifest
        {
            SessionId = Guid.NewGuid(),
            CaptureMode = CaptureMode.Mic,
            StartedAtUtc = new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero),
            TimeZoneId = "Tokyo Standard Time",
        };
        manifest.Save(dir);
        var transcript = T.Raw(manifest.SessionId, 1, [.. Enumerable.Range(0, segments).Select(i => T.Seg(i * 5_000L, i * 5_000L + 4_000, $"議題{i}について説明します"))]);
        new TranscriptStore(dir).Save(transcript);
        return dir;
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "timed out: " + what);
    }

    [Fact]
    public async Task Job_GoesThroughOrganizingAndValidating_ToReadyForReview()
    {
        var dir = Session(20);
        var job = _runner.Enqueue(dir, Guid.Empty, Settings);

        await WaitFor(() => RecordJobStore.Load(dir, job.JobId).State == ProcessingState.ReadyForReview, "ready");

        var done = RecordJobStore.Load(dir, job.JobId);
        Assert.Equal((1, 1, 1), (done.TranscriptRevision, done.AnalysisRevision, done.RecordRevision));
        Assert.NotNull(new RecordStore(dir).LoadLatest());
        lock (_states) Assert.Equal([ProcessingState.Queued, ProcessingState.Organizing, ProcessingState.Validating, ProcessingState.ReadyForReview], _states);
    }

    [Fact]
    public async Task Recording_PreemptsTheJob_WhichResumesFromFinishedPiecesAfterwards()
    {
        var dir = Session(400);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _llm.Override = (r, n) =>
        {
            if (n == 2)
            {
                started.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(10)); // the second piece is "running" when recording starts
            }
            return null;
        };
        var job = _runner.Enqueue(dir, Guid.Empty, Settings);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _runner.SetRecording(true);
        release.SetResult();
        await WaitFor(() => RecordJobStore.Load(dir, job.JobId).State == ProcessingState.Queued, "back in the queue");
        var before = _llm.Requests.Count;
        await Task.Delay(200);
        Assert.Equal(before, _llm.Requests.Count); // nothing runs while recording

        _runner.SetRecording(false);
        await WaitFor(() => RecordJobStore.Load(dir, job.JobId).State == ProcessingState.ReadyForReview, "ready after recording");
        var pieces = RecordJobStore.Load(dir, job.JobId).PieceCount;
        // Piece 1 was kept; piece 2 ran again (its answer came after the cancel); nothing ran twice beyond that.
        Assert.True(_llm.Requests.Count(r => r.SchemaName == LlmSchemas.ExtractionName) <= pieces + 1);
    }

    [Fact]
    public async Task ServerDown_FailsWithLlm001_AndRetryWorksOnceItIsUp()
    {
        var dir = Session(10);
        _llm.Unavailable = true;
        var job = _runner.Enqueue(dir, Guid.Empty, Settings);
        await WaitFor(() => RecordJobStore.Load(dir, job.JobId).State == ProcessingState.Failed, "failed");
        Assert.Equal(RecordJobError.LlmUnavailable, RecordJobStore.Load(dir, job.JobId).Error);
        Assert.Empty(new AnalysisStore(dir).Revisions());

        _llm.Unavailable = false;
        _runner.Retry(dir, job.JobId);
        await WaitFor(() => RecordJobStore.Load(dir, job.JobId).State == ProcessingState.ReadyForReview, "ready after retry");
        Assert.Null(RecordJobStore.Load(dir, job.JobId).Error);
    }

    [Fact]
    public async Task Job_WaitsForTheInferenceSlot()
    {
        var dir = Session(10);
        var held = await _gate.EnterAsync(CancellationToken.None); // the recognizer is running
        var job = _runner.Enqueue(dir, Guid.Empty, Settings);
        await Task.Delay(300);
        Assert.Equal(ProcessingState.Queued, RecordJobStore.Load(dir, job.JobId).State);
        Assert.Empty(_llm.Requests);

        held.Dispose();
        await WaitFor(() => RecordJobStore.Load(dir, job.JobId).State == ProcessingState.ReadyForReview, "ready after the recognizer");
    }

    [Fact]
    public async Task NoTranscript_Fails()
    {
        var dir = Path.Combine(_dir.Path, "empty");
        Directory.CreateDirectory(dir);
        new SessionManifest { SessionId = Guid.NewGuid(), CaptureMode = CaptureMode.Mic, StartedAtUtc = DateTimeOffset.UnixEpoch, TimeZoneId = "UTC" }.Save(dir);
        var job = _runner.Enqueue(dir, Guid.Empty, Settings);
        await WaitFor(() => RecordJobStore.Load(dir, job.JobId).State == ProcessingState.Failed, "failed");
        Assert.Equal(RecordJobError.NoTranscript, RecordJobStore.Load(dir, job.JobId).Error);
    }

    public async ValueTask DisposeAsync()
    {
        await _runner.DisposeAsync();
        _gate.Dispose();
        _dir.Dispose();
    }
}
