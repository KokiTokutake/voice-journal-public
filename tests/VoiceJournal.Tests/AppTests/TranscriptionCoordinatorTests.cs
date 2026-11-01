using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.AppTests;

internal sealed class MemoryUiSettingsStore : IUiSettingsStore
{
    public UiSettings Current { get; set; } = new();

    public UiSettings Load() => Current;

    public void Save(UiSettings settings) => Current = settings;
}

public sealed class TranscriptIndexTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-tx-");
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly SessionIndex _sessions;
    private readonly TranscriptIndex _transcripts;
    private readonly SttJobProcessor _processor = new(new FakeSttInputBuilder(), new FakeSttEngine());

    public TranscriptIndexTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        _db = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        _sessions = new SessionIndex(_workspace, _db);
        _transcripts = new TranscriptIndex(_workspace, _db);
    }

    public void Dispose() => _dir.Dispose();

    private (string Dir, Guid Id) Session()
    {
        var (dir, id) = JobSession.Create(Path.Combine(_workspace.SessionsRoot, "2026", "11"));
        _sessions.Upsert(dir);
        return (dir, id);
    }

    private async Task<SttJob> Transcribe(string dir, Guid id)
    {
        var job = JobSession.NewJob(id);
        SttJobStore.Save(dir, job);
        await _processor.RunAsync(dir, job, new SttPauseGate(), null, CancellationToken.None);
        return job;
    }

    private long Count(string sql) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    });

    [Fact]
    public async Task Jobs_AndRevisions_AreIndexed_AndTheLibraryShowsTheProcessingState()
    {
        var (dir, id) = Session();
        var queued = JobSession.NewJob(id);
        SttJobStore.Save(dir, queued);
        _transcripts.UpsertJob(dir, queued);
        Assert.Equal(ProcessingState.Queued, Assert.Single(_sessions.ListSessions()).ProcessingState);
        Assert.Equal([queued.JobId], _transcripts.UnfinishedJobs().Select(j => j.Job.JobId));

        await _processor.RunAsync(dir, queued, new SttPauseGate(), null, CancellationToken.None);
        _transcripts.UpsertJob(dir, queued);
        Assert.Equal(1, _transcripts.UpsertRevisions(dir));
        Assert.Equal(0, _transcripts.UpsertRevisions(dir)); // unchanged files are skipped

        var row = Assert.Single(_sessions.ListSessions());
        Assert.Equal(ProcessingState.Transcribed, row.ProcessingState);
        Assert.Equal(1, row.LatestTranscriptRevision);
        Assert.Empty(_transcripts.UnfinishedJobs());
        Assert.Equal(6, Count("SELECT COUNT(*) FROM segments"));
        Assert.Equal(1, Count($"SELECT COUNT(*) FROM transcript_revisions WHERE source_job_id = '{queued.JobId:D}' AND kind = 'raw'"));
    }

    [Fact]
    public async Task Rebuild_RestoresEverythingFromTheFiles_IntoAnEmptyDatabase()
    {
        var (dir, id) = Session();
        var done = await Transcribe(dir, id);
        var store = new TranscriptStore(dir);
        store.Save(TranscriptEditing.EditText(store.Load(1), 2, store.Load(1).Segments[0].SegmentId, "修正", DateTimeOffset.UnixEpoch));
        var (other, otherId) = Session();
        var pending = JobSession.NewJob(otherId);
        pending.State = ProcessingState.Transcribing; // left running by a crash
        SttJobStore.Save(other, pending);

        // A brand-new database next to the files.
        var fresh = VoiceJournalDatabase.Open(Path.Combine(_dir.Path, "fresh.db"));
        var sessions = new SessionIndex(_workspace, fresh);
        sessions.Rebuild();
        var report = new TranscriptIndex(_workspace, fresh).Rebuild();

        Assert.Empty(report.Failed);
        Assert.Equal(2, report.Jobs);
        Assert.Equal(2, report.Revisions);
        var rows = sessions.ListSessions().ToDictionary(r => r.SessionId);
        Assert.Equal(ProcessingState.Transcribed, rows[id].ProcessingState);
        Assert.Equal(2, rows[id].LatestTranscriptRevision);
        Assert.Equal(ProcessingState.Transcribing, rows[otherId].ProcessingState);
        Assert.Null(rows[otherId].LatestTranscriptRevision);
        Assert.Equal([pending.JobId], new TranscriptIndex(_workspace, fresh).UnfinishedJobs().Select(j => j.Job.JobId));
        Assert.NotEqual(done.JobId, pending.JobId);
    }
}

public sealed class TranscriptionCoordinatorTests : IAsyncDisposable
{
    private readonly TempDirectory _dir = new("vj-coord-");
    private readonly ManualTimeProvider _clock = new();
    private readonly FakeDeviceCatalog _catalog = new();
    private readonly QueueDispatcher _ui = new();
    private readonly MemoryUiSettingsStore _settings = new();
    private readonly FakeSttEngine _engine = new();
    private readonly Workspace _workspace;
    private readonly SessionIndex _index;
    private readonly RecordingViewModel _vm;
    private readonly TranscriptionCoordinator _coordinator;

    public TranscriptionCoordinatorTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        var db = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        _index = new SessionIndex(_workspace, db, _clock);
        _vm = new RecordingViewModel(_catalog, _index, new FakeFreeSpace(100L * 1024 * 1024 * 1024), new FakePower(), new FakeDialogs(), new FakeShell(), _ui, _clock,
            new RecordingEnvironment(_workspace.Root, MinFreeBytes: 2L * 1024 * 1024 * 1024));
        _coordinator = new TranscriptionCoordinator(_vm, new SttJobProcessor(new FakeSttInputBuilder { DurationMs = 2_000 }, _engine), new TranscriptIndex(_workspace, db),
            _settings, () => JobSession.Config);
    }

    public ValueTask DisposeAsync()
    {
        _coordinator.Dispose();
        _vm.Dispose();
        _ui.Drain();
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RecordTwoSeconds()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        var session = _vm.SessionForTesting!;
        foreach (var source in _catalog.Sources) source.AfterPush = () => session.ThrottleForTesting(source.Source);
        Assert.True(_coordinator.Runner.IsPaused); // already while preparing (detail spec 3.1)
        _catalog.Latest(AudioSource.Mic).PushFor(_clock, TimeSpan.FromSeconds(2));
        _vm.Tick();
        Assert.Equal(RecorderStatus.Recording, _vm.Status);
        Assert.True(_coordinator.Runner.IsPaused);
        await _vm.StopCommand.ExecuteAsync(null);
        _ui.Drain();
        Assert.Equal(RecorderStatus.Saved, _vm.Status);
        Assert.False(_coordinator.Runner.IsPaused);
    }

    [Fact]
    public async Task ASavedRecording_IsQueued_Transcribed_AndIndexed()
    {
        await RecordTwoSeconds();

        await JobSession.Until(() => _index.ListSessions().SingleOrDefault()?.LatestTranscriptRevision == 1, "transcript indexed");
        var row = _index.ListSessions().Single();
        Assert.Equal(ProcessingState.Transcribed, row.ProcessingState);
        Assert.Equal(1, new TranscriptStore(_vm.LastSavedDirectory!).Revisions().Single());
        Assert.NotEmpty(_engine.Runs);
    }

    [Fact]
    public async Task WithTheOptionOff_NothingIsQueued_AndTheChoiceIsRemembered()
    {
        _vm.TranscribeAfterSaving = false;
        Assert.False(_settings.Current.TranscribeAfterSaving);

        await RecordTwoSeconds();
        await Task.Delay(300);

        Assert.Empty(_engine.Runs);
        Assert.Equal(ProcessingState.None, _index.ListSessions().Single().ProcessingState);
    }

    [Fact]
    public void TheOption_IsLoadedFromTheSettings()
    {
        var settings = new MemoryUiSettingsStore { Current = new UiSettings { TranscribeAfterSaving = false } };
        using var coordinator = new TranscriptionCoordinator(_vm, new SttJobProcessor(new FakeSttInputBuilder(), _engine),
            new TranscriptIndex(_workspace, VoiceJournalDatabase.Open(_workspace.DatabasePath)), settings, () => JobSession.Config);

        Assert.False(_vm.TranscribeAfterSaving);
    }
}
