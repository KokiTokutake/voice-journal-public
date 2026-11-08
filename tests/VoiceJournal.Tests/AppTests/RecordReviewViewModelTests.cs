using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Tests.Core.Analysis;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.AppTests;

internal sealed class FakeRecordService : IRecordService
{
    public List<string> Enqueued { get; } = [];

    public List<string> Saved { get; } = [];

    public Guid? CurrentJobId { get; set; }

    public event EventHandler<RecordJobChange>? JobChanged;

    public event EventHandler<GenerationProgress>? Progress;

    public RecordJob Enqueue(string sessionDirectory)
    {
        Enqueued.Add(sessionDirectory);
        return new RecordJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = Guid.Empty,
            Settings = new GenerationSettings { ModelId = "m", ContextLength = 8192, OutputReserve = 2048 },
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };
    }

    public List<Guid> Sections { get; } = [];

    public RecordJob EnqueueSection(string sessionDirectory, Guid sectionId)
    {
        Sections.Add(sectionId);
        return Enqueue(sessionDirectory);
    }

    public Task RecordSavedAsync(string sessionDirectory)
    {
        Saved.Add(sessionDirectory);
        return Task.CompletedTask;
    }

    public void Raise(RecordJobChange change) => JobChanged?.Invoke(this, change);

    public void Report(GenerationProgress progress) => Progress?.Invoke(this, progress);
}

public sealed class RecordReviewViewModelTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-review-");
    private readonly FakeRecordService _records = new();
    private readonly QueueDispatcher _ui = new();
    private readonly FakePlayer _player = new();
    private readonly RecordReviewViewModel _vm;
    private readonly string _session;
    private readonly TranscriptDocument _transcript;

    public RecordReviewViewModelTests()
    {
        _session = Path.Combine(_dir.Path, "session");
        Directory.CreateDirectory(_session);
        var (t, a) = R.Analysis();
        _transcript = T.Raw(t.SessionId, 1, [.. t.Segments]);
        new SessionManifest
        {
            SessionId = t.SessionId,
            CaptureMode = CaptureMode.Mic,
            StartedAtUtc = new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero),
            TimeZoneId = "Tokyo Standard Time",
        }.Save(_session);
        new TranscriptStore(_session).Save(_transcript);
        var analysis = a with { Analysis = a.Analysis with { TranscriptRevision = 1 } };
        new RecordStore(_session).Save(RecordFactory.FromAnalysis(t.SessionId, analysis, 1, R.Now), d => RecordMarkdown.RenderFor(_session, d, approved: false));
        _vm = new RecordReviewViewModel(_records, _player, _ui);
    }

    public void Dispose() => _dir.Dispose();

    private RecordStore Store => new(_session);

    private StatementRowViewModel Statement(int block, int index = 0) => _vm.Blocks[block].Statements[index];

    [Fact]
    public void Open_ShowsTheSummarySectionsCandidatesAndTheTranscriptTheRecordIsBuiltOn()
    {
        _vm.Open(_session, "会議");

        Assert.True(_vm.HasRecord);
        Assert.True(_vm.IsLatest);
        Assert.Equal("保存先の決定", _vm.RecordTitle);
        Assert.Equal(["要約", "決定事項", "次にやること"], _vm.Blocks.Select(b => b.Heading));
        Assert.True(_vm.Blocks[0].IsSummary);
        Assert.False(_vm.Blocks[0].IsSectionEditable);
        Assert.True(_vm.Blocks[1].IsSectionEditable);
        Assert.Equal("AI・案・未確定", Statement(2).OriginText);
        var evidence = Assert.Single(Statement(0).Evidence);
        Assert.True(evidence.Found);
        Assert.Equal("00:00:00 マイク", evidence.Label);
        var candidate = Assert.Single(_vm.Candidates);
        Assert.Equal(("タスク", "手順書を書く"), (candidate.KindText, candidate.Title));
        Assert.Contains("担当: 田中さん", candidate.DetailText, StringComparison.Ordinal);
        Assert.Contains("期限: 2026/11/13", candidate.DetailText, StringComparison.Ordinal);
        Assert.Equal("要確認: 数字・日付・名前を音声で確認", candidate.ConcernText);
        Assert.Equal(2, _vm.Segments.Count);
        Assert.Contains("草稿", _vm.StatusText, StringComparison.Ordinal);
        Assert.True(_vm.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public void AnEvidenceLink_SelectsAndPlaysItsSegment()
    {
        _vm.Open(_session, "会議");

        _vm.ShowEvidenceCommand.Execute(Statement(2).Evidence[0]);

        Assert.Equal(_transcript.Segments[1].SegmentId, _vm.SelectedSegment?.SegmentId);
        // No audio in this session: the screen says so instead of failing.
        Assert.Contains("再生できる音声がありません", _vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingAStatement_SavesTheNextRevision_KeepsItsEvidence_AndMarksItEdited()
    {
        _vm.Open(_session, "会議");
        var row = Statement(1);

        row.Text = "保存先はローカル PC";

        var saved = Store.Load(2);
        var statement = saved.Sections[0].Statements[0];
        Assert.Equal(("保存先はローカル PC", TextOrigin.UserEdited), (statement.Text, statement.Origin));
        Assert.Single(statement.Evidence);
        Assert.Equal("修正", row.OriginText);
        Assert.Equal("根拠は AI の生成時のもの", row.EvidenceNote);
        Assert.Equal(2, _vm.Revisions.Count);
        Assert.Equal(2, _vm.SelectedRevision?.Revision);
        Assert.Same(row, Statement(1)); // edited in place: the focus is not lost
        Assert.Equal([_session], _records.Saved);
        Assert.Contains("保存先はローカル PC", File.ReadAllText(Store.DraftMarkdownPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyText_IsRefused_AndTheOldTextComesBack()
    {
        _vm.Open(_session, "会議");

        Statement(1).Text = "   ";
        _vm.RecordTitle = "";

        Assert.Equal([1], Store.Revisions());
        Assert.Equal("保存先はローカル", Statement(1).Text);
        Assert.Equal("保存先の決定", _vm.RecordTitle);
    }

    [Fact]
    public void AnOlderRevision_IsReadOnly_AndShowsHowTheLatestDiffers()
    {
        _vm.Open(_session, "会議");
        Statement(1).Text = "保存先はローカル PC";
        _vm.DeleteStatementCommand.Execute(Statement(2));

        _vm.SelectedRevision = _vm.Revisions.Single(r => r.Revision == 1);

        Assert.False(_vm.IsLatest);
        Assert.True(Statement(1).IsReadOnly);
        Assert.Equal("→ 最新: 保存先はローカル PC", Statement(1).CompareText);
        Assert.Equal("→ 最新では削除", Statement(2).CompareText);
        Assert.False(_vm.ApproveCommand.CanExecute(null));
        Assert.False(_vm.AddSectionCommand.CanExecute(null));
        Statement(1).Text = "変えられない";
        Assert.Equal([1, 2, 3], Store.Revisions());
    }

    [Fact]
    public void Sections_CanBeAdded_Moved_AndDeleted_EachAsANewRevision()
    {
        _vm.Open(_session, "会議");
        _vm.NewSectionHeading = "補足";
        _vm.NewSectionText = "次回は 11 月 9 日";
        _vm.AddSectionCommand.Execute(null);
        Assert.Equal(["要約", "決定事項", "次にやること", "補足"], _vm.Blocks.Select(b => b.Heading));
        Assert.Equal(("", ""), (_vm.NewSectionHeading, _vm.NewSectionText));
        Assert.Equal("手入力", Statement(3).OriginText);

        _vm.MoveSectionUpCommand.Execute(_vm.Blocks[3]);
        Assert.Equal(["要約", "決定事項", "補足", "次にやること"], _vm.Blocks.Select(b => b.Heading));
        _vm.MoveSectionUpCommand.Execute(_vm.Blocks[1]); // already first: nothing happens

        _vm.DeleteSectionCommand.Execute(_vm.Blocks[1]);
        Assert.Equal(["要約", "補足", "次にやること"], _vm.Blocks.Select(b => b.Heading));

        _vm.Blocks[1].Heading = "補足事項";
        _vm.Blocks[0].NewStatementText = "要約を足す";
        _vm.AddStatementCommand.Execute(_vm.Blocks[0]);

        Assert.Equal([1, 2, 3, 4, 5, 6], Store.Revisions());
        var latest = Store.LoadLatest()!;
        Assert.Equal(["補足事項", "次にやること"], latest.Sections.Select(s => s.Heading));
        Assert.Equal("要約を足す", latest.Summary[^1].Text);
    }

    [Fact]
    public void Approve_WritesTheApprovedRecord_AndALaterEditMakesItOutdated()
    {
        _vm.Open(_session, "会議");

        _vm.ApproveCommand.Execute(null);

        Assert.Contains("review_status: \"approved\"", File.ReadAllText(Store.ApprovedMarkdownPath), StringComparison.Ordinal);
        Assert.Equal(RecordReviewState.Approved, Store.Status(1).State);
        Assert.Contains("承認済み", _vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("「管理」タブで反映するまで管理ファイルには入りません", _vm.Message, StringComparison.Ordinal);
        Assert.Contains("［承認済み］", _vm.Revisions.Single().Label, StringComparison.Ordinal);
        Assert.False(_vm.IsOutdated);

        Statement(1).Text = "保存先はローカル PC";

        Assert.True(_vm.IsOutdated);
        Assert.Contains("新しい草稿 Rev 2", _vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Defer_TogglesTheDeferredMark()
    {
        _vm.Open(_session, "会議");

        _vm.DeferCommand.Execute(null);
        Assert.True(_vm.IsDeferred);
        Assert.Equal(RecordReviewState.Deferred, Store.Status(1).State);

        _vm.DeferCommand.Execute(null);
        Assert.False(_vm.IsDeferred);
        Assert.Equal(RecordReviewState.Draft, Store.Status(1).State);
    }

    [Fact]
    public void Generate_QueuesAJob_ShowsItsProgress_AndReloadsWhenItIsReady()
    {
        _vm.Open(_session, "会議");

        _vm.GenerateCommand.Execute(null);

        Assert.Equal([_session], _records.Enqueued);
        Assert.True(_vm.IsGenerating);
        Assert.False(_vm.GenerateCommand.CanExecute(null));
        var job = RecordJobStore.List(_session); // the fake does not write a ledger
        Assert.Empty(job);

        var running = new RecordJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = _transcript.SessionId,
            Settings = new GenerationSettings { ModelId = "m", ContextLength = 8192, OutputReserve = 2048 },
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            State = ProcessingState.Organizing,
        };
        _records.Raise(new RecordJobChange(_session, running));
        _records.CurrentJobId = running.JobId;
        _records.Report(new GenerationProgress("extract", 1, 3));
        _ui.Drain();
        Assert.Equal("記録の作成: 区間ごとに整理（1/3）", _vm.JobText);

        // The new generation saved revision 2.
        var (t, a) = R.Analysis("作り直した記録");
        Store.Save(RecordFactory.FromAnalysis(t.SessionId, a with { Analysis = a.Analysis with { SessionId = _transcript.SessionId, TranscriptRevision = 1 } }, 2, R.Now),
            d => RecordMarkdown.RenderFor(_session, d, approved: false));
        running.State = ProcessingState.ReadyForReview;
        running.RecordRevision = 2;
        running.ElapsedSeconds = 42;
        _records.Raise(new RecordJobChange(_session, running));
        _ui.Drain();

        Assert.False(_vm.IsGenerating);
        Assert.Null(_vm.JobText);
        Assert.Equal("作り直した記録", _vm.RecordTitle);
        Assert.Contains("記録 Rev 2 を作りました", _vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegeneratingASection_QueuesItsId_AndASectionWithoutEvidenceIsRefused()
    {
        _vm.Open(_session, "会議");

        _vm.RegenerateSectionCommand.Execute(_vm.Blocks[1]);

        Assert.Equal([_vm.Blocks[1].Id!.Value], _records.Sections);
        Assert.True(_vm.IsGenerating);
        Assert.False(_vm.RegenerateSectionCommand.CanExecute(_vm.Blocks[2]));

        _vm.IsGenerating = false;
        _vm.NewSectionHeading = "補足";
        _vm.NewSectionText = "手で書いた文";
        _vm.AddSectionCommand.Execute(null);
        _vm.RegenerateSectionCommand.Execute(_vm.Blocks[^1]);
        Assert.Single(_records.Sections);
        Assert.Contains("根拠の区間がない", _vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedJob_ExplainsLlm001()
    {
        _vm.Open(_session, "会議");
        _vm.GenerateCommand.Execute(null);
        var failed = new RecordJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = _transcript.SessionId,
            Settings = new GenerationSettings { ModelId = "m", ContextLength = 8192, OutputReserve = 2048 },
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            State = ProcessingState.Organizing,
        };
        _records.Raise(new RecordJobChange(_session, failed));
        _ui.Drain(); // the runner raises the same ledger object for each state
        failed.State = ProcessingState.Failed;
        failed.Error = RecordJobError.LlmUnavailable;
        _records.Raise(new RecordJobChange(_session, failed));
        _ui.Drain();

        Assert.False(_vm.IsGenerating);
        Assert.StartsWith("LLM-001", _vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionWithoutARecord_OffersToMakeOne()
    {
        var empty = Path.Combine(_dir.Path, "empty");
        Directory.CreateDirectory(empty);
        new SessionManifest { SessionId = Guid.NewGuid(), CaptureMode = CaptureMode.Mic, StartedAtUtc = DateTimeOffset.UnixEpoch, TimeZoneId = "Tokyo Standard Time" }.Save(empty);

        _vm.Open(empty, "空");

        Assert.False(_vm.HasRecord);
        Assert.True(_vm.HasNoRecord);
        Assert.Equal("記録を作る(_W)", _vm.GenerateLabel);
        _vm.GenerateCommand.Execute(null);
        Assert.Contains("文字起こしがまだありません", _vm.Message, StringComparison.Ordinal);
        Assert.Empty(_records.Enqueued);
    }
}
