using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.Core.Analysis;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Storage;

public sealed class RecordIndexTests : IDisposable
{
    private static readonly GenerationSettings Settings = new() { ModelId = "gemma-3-4b-it", ContextLength = 8192, OutputReserve = 2048 };

    private static readonly string[] IndexTables = ["candidates", "analyses", "records", "record_jobs"];

    private readonly TempDirectory _dir = new("vj-records-");
    private readonly ManualTimeProvider _clock = new();
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly RecordIndex _index;

    public RecordIndexTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        _db = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        _index = new RecordIndex(_workspace, _db);
    }

    public void Dispose() => _dir.Dispose();

    private async Task<(string Dir, RecordJob Job)> Generated()
    {
        var (dir, id) = JobSession.Create(Path.Combine(_workspace.SessionsRoot, "2026", "11"));
        new SessionIndex(_workspace, _db, _clock).Upsert(dir);
        new TranscriptStore(dir).Save(T.Raw(id, 1, [.. Enumerable.Range(0, 8).Select(i => T.Seg(i * 5_000L, i * 5_000L + 4_000, $"議題{i}について説明します"))]));
        var job = new RecordJob { JobId = Guid.CreateVersion7(), SessionId = id, Settings = Settings, CreatedAtUtc = _clock.GetUtcNow() };
        RecordJobStore.Save(dir, job);
        var processor = new RecordJobProcessor(new RecordGenerator(new EchoLlm(), new AnalysisSchemaValidator(), TimeProvider.System), TimeProvider.System);
        await processor.RunAsync(dir, job, () => false, _ => { }, null, CancellationToken.None);
        return (dir, job);
    }

    private long Count(string sql) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + sql;
        return (long)cmd.ExecuteScalar()!;
    });

    private string? Text(string sql) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    });

    [Fact]
    public async Task JobsAnalysesRecordsAndCandidates_AreIndexed_AndRebuildGivesTheSameRows()
    {
        var (dir, job) = await Generated();
        Assert.Equal(ProcessingState.ReadyForReview, job.State);

        _index.UpsertJob(dir, job);
        Assert.Equal((1, 1), _index.UpsertSession(dir));

        Assert.Equal(1, Count("record_jobs WHERE state = 'ready_for_review' AND record_revision = 1"));
        Assert.Equal(1, Count("analyses WHERE validation_state = 'valid' AND transcript_revision = 1"));
        Assert.Equal(1, Count("records WHERE kind = 'generated' AND approved_at IS NULL"));
        Assert.True(Count("candidates WHERE decision = 'proposed'") > 0);
        Assert.Equal("ready_for_review", Text("SELECT processing_state FROM sessions"));
        Assert.Equal("draft", Text("SELECT record_review_state FROM sessions"));
        Assert.Equal(1, Count("sessions WHERE transcript_current_revision = 1"));
        Assert.Equal((0, 0), _index.UpsertSession(dir)); // unchanged files are not written again

        var ids = Text("SELECT group_concat(id) FROM (SELECT id FROM candidates ORDER BY id)");
        _db.Write((c, tx) =>
        {
            foreach (var table in IndexTables)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM " + table;
                cmd.ExecuteNonQuery();
            }
            return 0;
        });
        var report = _index.Rebuild();
        Assert.Equal((1, 1, 1, 1), (report.Sessions, report.Jobs, report.Analyses, report.Records));
        Assert.Empty(report.Failed);
        Assert.Equal(ids, Text("SELECT group_concat(id) FROM (SELECT id FROM candidates ORDER BY id)"));
    }

    [Fact]
    public async Task Approval_AndANewTranscript_AreReflected()
    {
        var (dir, _) = await Generated();
        _index.UpsertSession(dir);

        new RecordStore(dir).Approve(1, 1, _clock.GetUtcNow());
        Assert.Equal((0, 1), _index.UpsertSession(dir)); // the approval changes the record's row
        Assert.Equal("approved", Text("SELECT record_review_state FROM sessions"));
        Assert.NotNull(Text("SELECT approved_at FROM records"));

        var store = new TranscriptStore(dir);
        store.Save(TranscriptEditing.EditText(store.LoadCurrent()!, 2, store.LoadCurrent()!.Segments[0].SegmentId, "直した", _clock.GetUtcNow()));
        _index.UpsertSession(dir);
        Assert.Equal("approved_outdated", Text("SELECT record_review_state FROM sessions")); // AT-26
        Assert.Equal(1, Count("sessions WHERE transcript_current_revision = 2"));
    }

    [Fact]
    public async Task UnfinishedJobs_AreFoundForResume()
    {
        var (dir, id) = JobSession.Create(Path.Combine(_workspace.SessionsRoot, "2026", "11"));
        new SessionIndex(_workspace, _db, _clock).Upsert(dir);
        var queued = new RecordJob { JobId = Guid.CreateVersion7(), SessionId = id, Settings = Settings, CreatedAtUtc = _clock.GetUtcNow() };
        var organizing = new RecordJob { JobId = Guid.CreateVersion7(), SessionId = id, Settings = Settings, CreatedAtUtc = _clock.GetUtcNow(), State = ProcessingState.Organizing };
        var failed = new RecordJob { JobId = Guid.CreateVersion7(), SessionId = id, Settings = Settings, CreatedAtUtc = _clock.GetUtcNow(), State = ProcessingState.Failed, Error = RecordJobError.LlmUnavailable };
        foreach (var j in new[] { queued, organizing, failed })
        {
            RecordJobStore.Save(dir, j);
            _index.UpsertJob(dir, j);
        }

        Assert.Equal(new[] { queued.JobId, organizing.JobId }.Order(), _index.UnfinishedJobs().Select(u => u.Job.JobId).Order());
        var recent = _index.RecentJobs(10);
        Assert.Equal(3, recent.Count);
        Assert.Equal(RecordJobError.LlmUnavailable, recent.Single(r => r.JobId == failed.JobId).Error);
        await Task.CompletedTask;
    }
}
