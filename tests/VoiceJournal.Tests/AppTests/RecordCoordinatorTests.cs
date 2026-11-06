using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.Core.Analysis;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.AppTests;

public sealed class RecordCoordinatorTests : IAsyncDisposable
{
    private static readonly GenerationSettings Settings = new() { ModelId = "gemma-3-4b-it", ContextLength = 8192, OutputReserve = 2048 };

    private readonly TempDirectory _dir = new("vj-rec-");
    private readonly ManualTimeProvider _clock = new();
    private readonly FakeDeviceCatalog _catalog = new();
    private readonly QueueDispatcher _ui = new();
    private readonly MemoryUiSettingsStore _settings = new();
    private readonly FakeSttEngine _engine = new();
    private readonly EchoLlm _llm = new();
    private readonly InferenceGate _gate = new();
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly SessionIndex _index;
    private readonly RecordIndex _records;
    private readonly RecordingViewModel _vm;
    private readonly TranscriptionCoordinator _transcription;
    private readonly RecordCoordinator _coordinator;

    public RecordCoordinatorTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        _db = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        _index = new SessionIndex(_workspace, _db, _clock);
        _records = new RecordIndex(_workspace, _db);
        _vm = new RecordingViewModel(_catalog, _index, new FakeFreeSpace(100L * 1024 * 1024 * 1024), new FakePower(), new FakeDialogs(), new FakeShell(), _ui, _clock,
            new RecordingEnvironment(_workspace.Root, MinFreeBytes: 2L * 1024 * 1024 * 1024));
        _transcription = new TranscriptionCoordinator(_vm, new SttJobProcessor(new FakeSttInputBuilder(), _engine),
            new TranscriptIndex(_workspace, _db), _settings, () => JobSession.Config, inference: _gate);
        var generator = new RecordGenerator(_llm, new AnalysisSchemaValidator(), TimeProvider.System);
        _coordinator = new RecordCoordinator(_vm, _transcription, new RecordJobProcessor(generator, TimeProvider.System), _gate, _records, _settings, () => Settings);
    }

    public ValueTask DisposeAsync()
    {
        _coordinator.Dispose();
        _transcription.Dispose();
        _vm.Dispose();
        _ui.Drain();
        _gate.Dispose();
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string> RecordTwoSeconds()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        var session = _vm.SessionForTesting!;
        foreach (var source in _catalog.Sources) source.AfterPush = () => session.ThrottleForTesting(source.Source);
        _catalog.Latest(AudioSource.Mic).PushFor(_clock, TimeSpan.FromSeconds(2));
        _vm.Tick();
        await _vm.StopCommand.ExecuteAsync(null);
        _ui.Drain();
        Assert.Equal(RecorderStatus.Saved, _vm.Status);
        return _vm.LastSavedDirectory!;
    }

    private ProcessingState? SessionState() => _index.ListSessions().SingleOrDefault()?.ProcessingState;

    private long Count(string sql) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    });

    [Fact]
    public async Task ASavedRecording_IsTranscribed_ThenOrganizedIntoARecord_AndIndexed()
    {
        var dir = await RecordTwoSeconds();

        await JobSession.Until(() => SessionState() == ProcessingState.ReadyForReview, "record ready for review");

        var job = Assert.Single(RecordJobStore.List(dir));
        Assert.Equal(ProcessingState.ReadyForReview, job.State);
        Assert.Equal(1, job.TranscriptRevision);
        Assert.NotNull(new RecordStore(dir).LoadLatest());
        Assert.Equal(1, Count("SELECT COUNT(*) FROM records"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM record_jobs WHERE state = 'ready_for_review'"));
        Assert.Contains(_llm.Requests, r => r.SchemaName == LlmSchemas.ExtractionName);
    }

    [Fact]
    public async Task TheProcessingScreen_ListsTheRecordJob_NextToTheTranscription_AndCanRetryIt()
    {
        _llm.Unavailable = true; // the inference engine cannot start (LLM-001)
        var dir = await RecordTwoSeconds();
        await JobSession.Until(() => RecordJobStore.List(dir).SingleOrDefault()?.State == ProcessingState.Failed
            && _records.RecentJobs(10).Any(j => j.State == ProcessingState.Failed), "record job fails");
        var processing = new ProcessingViewModel(_transcription, new TranscriptIndex(_workspace, _db), _ui, new RecordingEnvironment(_workspace.Root, 0), _coordinator, _records);

        await processing.RefreshAsync();

        Assert.Equal(["記録の作成", "文字起こし"], processing.Jobs.Select(j => j.KindText));
        var row = processing.Jobs[0];
        Assert.True(row.IsRecord);
        Assert.Equal("失敗", row.StateText);
        Assert.StartsWith("LLM-001", row.ErrorText, StringComparison.Ordinal);
        Assert.Equal("gemma-3-4b-it", row.ModelText);

        _llm.Unavailable = false; // the user fixed the cause (e.g. closed other apps)
        processing.Selected = row;
        processing.RetryCommand.Execute(null);
        await JobSession.Until(() => RecordJobStore.List(dir).Single().State == ProcessingState.ReadyForReview
            && _records.RecentJobs(10).Any(j => j.State == ProcessingState.ReadyForReview), "retried record");
        await processing.RefreshAsync();
        Assert.StartsWith("完了（記録 Rev 1）", processing.Jobs[0].StateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheOptionOff_OnlyTheTranscriptIsMade_AndTheChoiceIsRemembered()
    {
        _vm.GenerateRecordAfterTranscription = false;
        Assert.False(_settings.Current.GenerateRecordAfterTranscription);

        var dir = await RecordTwoSeconds();
        await JobSession.Until(() => SessionState() == ProcessingState.Transcribed, "transcribed");
        await Task.Delay(300);

        Assert.Empty(RecordJobStore.List(dir));
        Assert.Empty(_llm.Requests);
        Assert.Equal(ProcessingState.Transcribed, SessionState());
    }

    [Fact]
    public async Task ReTranscribingASessionThatHasARecord_DoesNotQueueAnotherOne()
    {
        var dir = await RecordTwoSeconds();
        await JobSession.Until(() => SessionState() == ProcessingState.ReadyForReview, "first record");

        var again = _transcription.Enqueue(dir);
        await JobSession.Until(() => SttJobStore.Load(dir, again.JobId).State == ProcessingState.Transcribed, "re-transcribed");
        await Task.Delay(300);

        Assert.Single(RecordJobStore.List(dir));
    }

    [Fact]
    public async Task Startup_ResumesARecordJobLeftRunning()
    {
        _vm.GenerateRecordAfterTranscription = false;
        var dir = await RecordTwoSeconds();
        await JobSession.Until(() => SessionState() == ProcessingState.Transcribed, "transcribed");
        var left = new RecordJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = Assert.Single(_index.ListSessions()).SessionId,
            Settings = Settings,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            State = ProcessingState.Organizing, // the app was closed mid-job
        };
        RecordJobStore.Save(dir, left);

        var report = _coordinator.Startup();

        Assert.Empty(report.Failed);
        await JobSession.Until(() => RecordJobStore.Load(dir, left.JobId).State == ProcessingState.ReadyForReview, "resumed");
        await JobSession.Until(() => SessionState() == ProcessingState.ReadyForReview, "indexed");
    }

    [Fact]
    public async Task Recording_StopsTheRecordQueue_UntilItIsSaved()
    {
        Assert.False(_coordinator.Runner.CurrentJobId.HasValue);
        var dir = await RecordTwoSeconds();
        await JobSession.Until(() => SessionState() == ProcessingState.ReadyForReview, "first record");
        var requests = _llm.Requests.Count;

        await _vm.StartCommand.ExecuteAsync(null); // preparing: the runner must not start a queued job
        var job = _coordinator.Enqueue(dir);
        await Task.Delay(300);
        Assert.Equal(ProcessingState.Queued, RecordJobStore.Load(dir, job.JobId).State);
        Assert.Equal(requests, _llm.Requests.Count);

        var session = _vm.SessionForTesting!;
        foreach (var source in _catalog.Sources) source.AfterPush = () => session.ThrottleForTesting(source.Source);
        _catalog.Latest(AudioSource.Mic).PushFor(_clock, TimeSpan.FromSeconds(2));
        _vm.Tick();
        await _vm.StopCommand.ExecuteAsync(null);
        _ui.Drain();

        await JobSession.Until(() => RecordJobStore.Load(dir, job.JobId).State == ProcessingState.ReadyForReview, "ran after the recording");
    }
}
