using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Infrastructure.Stt;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.AppTests;

/// <summary>Library, transcript (UI-06), processing (UI-03) and settings (UI-12) view models over a real workspace with a fake recognizer.</summary>
public sealed class TranscriptionUiTests : IAsyncDisposable
{
    private readonly TempDirectory _dir = new("vj-ui-");
    private readonly ManualTimeProvider _clock = new();
    private readonly QueueDispatcher _ui = new();
    private readonly FakePlayer _player = new();
    private readonly FakeSttEngine _engine = new();
    private readonly MemorySttSettingsStore _settings = new(JobSession.Config);
    private readonly Workspace _workspace;
    private readonly SessionIndex _sessions;
    private readonly TranscriptIndex _transcripts;
    private readonly RecordingViewModel _recording;
    private readonly TranscriptionCoordinator _coordinator;
    private readonly SttToolSettings _tools;

    public TranscriptionUiTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        var db = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        _sessions = new SessionIndex(_workspace, db, _clock);
        _transcripts = new TranscriptIndex(_workspace, db);
        _recording = new RecordingViewModel(new FakeDeviceCatalog(), _sessions, new FakeFreeSpace(long.MaxValue), new FakePower(), new FakeDialogs(), new FakeShell(), _ui, _clock,
            new RecordingEnvironment(_workspace.Root, 0));
        _coordinator = new TranscriptionCoordinator(_recording, new SttJobProcessor(new FakeSttInputBuilder(), _engine), _transcripts,
            new MemoryUiSettingsStore(), _settings.Load, _clock);
        _tools = SttToolSettings.Defaults(_dir.Path);
    }

    public ValueTask DisposeAsync()
    {
        _coordinator.Dispose();
        _recording.Dispose();
        _ui.Drain();
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    private (string Dir, Guid Id) Session(bool withAudioFiles = false)
    {
        var (dir, id) = JobSession.Create(Path.Combine(_workspace.SessionsRoot, "2026", "11"));
        if (withAudioFiles)
            foreach (var track in SessionManifest.Load(dir).Tracks)
                foreach (var chunk in track.Chunks)
                {
                    var path = SessionPaths.Resolve(dir, chunk.File);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "wav");
                }
        _sessions.Upsert(dir);
        return (dir, id);
    }

    private async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = System.Environment.TickCount64 + 10_000;
        while (true)
        {
            _ui.Drain();
            if (condition()) return;
            if (System.Environment.TickCount64 > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    private async Task<string> Transcribed()
    {
        var (dir, _) = Session(withAudioFiles: true);
        // A different short text per piece without digits: no review hints and no repetition flags unless a test adds them.
        _engine.OnRun = r => FakeSttEngine.Utterance(r, 1_000, 3_000, (r.OutputName[0] == 'm' ? "こちらの話" : "相手側の声") + (char)('あ' + r.OutputName[^1] - '0'));
        _coordinator.Enqueue(dir);
        await WaitFor(() => new TranscriptStore(dir).Revisions().Count == 1 && _coordinator.Runner.CurrentJobId is null, "first transcript");
        return dir;
    }

    private TranscriptViewModel Transcript() => new(_coordinator, _player, _ui, _tools, _clock);

    [Fact]
    public async Task Transcript_ShowsEchoOnTheMic_ButNotAmongTheRowsToCheck()
    {
        var (dir, _) = Session(withAudioFiles: true);
        // The far side through the speakers: the same words at the same time on both sources.
        _engine.OnRun = r => FakeSttEngine.Utterance(r, 1_000, 3_000, "相手側の声" + (char)('あ' + r.OutputName[^1] - '0'));
        _coordinator.Enqueue(dir);
        await WaitFor(() => new TranscriptStore(dir).Revisions().Count == 1 && _coordinator.Runner.CurrentJobId is null, "first transcript");
        var vm = Transcript();
        vm.Open(dir, "テスト");

        Assert.All(vm.Segments.Where(s => s.Source == AudioSource.Mic), s => Assert.Equal("回り込み", s.FlagsText));
        Assert.All(vm.Segments, s => Assert.False(s.HasFlags));
        vm.FlaggedOnly = true;
        Assert.Empty(vm.Segments);
    }

    [Fact]
    public async Task Library_ShowsTheProcessingState_QueuesTranscription_AndOpensTheTranscript()
    {
        var (dir, _) = Session();
        var library = new LibraryViewModel(_sessions, new RecordingEnvironment(_workspace.Root, 0), _player, new FakeShell(), _ui, _coordinator);
        var main = new MainViewModel(_recording, library, new FakeDialogs(), Transcript());
        await library.RefreshAsync();
        library.Selected = library.Sessions.Single();
        Assert.Equal("未処理", library.Selected.ProcessingText);
        Assert.False(library.OpenTranscriptCommand.CanExecute(null));

        await library.TranscribeCommand.ExecuteAsync(null);
        Assert.Contains("順番待ち", library.Message, StringComparison.Ordinal);

        await WaitFor(() => library.Sessions.SingleOrDefault()?.ProcessingText == "済み（Rev 1）", "library refresh after the job");
        library.Selected = library.Sessions.Single();
        Assert.True(library.OpenTranscriptCommand.CanExecute(null));
        library.OpenTranscriptCommand.Execute(null);

        Assert.Equal(AppPage.Transcript, main.CurrentPage);
        Assert.Equal(dir, main.Transcript!.SessionDirectory);
        Assert.Equal(6, main.Transcript.Segments.Count);
    }

    [Fact]
    public async Task Transcript_Edit_SavesTheNextRevision_AndOlderRevisionsAreReadOnly()
    {
        var dir = await Transcribed();
        var vm = Transcript();
        vm.Open(dir, "テスト");
        Assert.True(vm.IsCurrent);
        Assert.False(vm.AdoptCommand.CanExecute(null));
        Assert.Contains("small", vm.ProvenanceText, StringComparison.Ordinal);
        var row = vm.Segments[0];

        row.Text = "  田中さんが来週の水曜までに 3 件まとめます  ";

        var store = new TranscriptStore(dir);
        Assert.Equal([1, 2], store.Revisions());
        Assert.Equal("田中さんが来週の水曜までに 3 件まとめます", store.Load(2).Find(row.SegmentId)!.Text);
        Assert.Equal("修正", row.EditedText);
        Assert.Equal("数字・期日・人名", row.HintText);
        Assert.Equal(2, vm.SelectedRevision!.Revision);
        Assert.Equal(["Rev 2  修正（本文）　［使用中］", "Rev 1  原文（small（速い・固有名詞に弱い））"], vm.Revisions.Select(r => r.Label));

        vm.SelectedRevision = vm.Revisions.Single(r => r.Revision == 1);
        Assert.False(vm.IsCurrent);
        Assert.Contains("この Revision を使う", vm.ProvenanceText, StringComparison.Ordinal);
        Assert.False(vm.Segments[0].IsEditable);
        vm.Segments[0].Text = "古い版への修正";
        Assert.Equal([1, 2], store.Revisions()); // nothing saved from an old revision
        Assert.NotEqual("古い版への修正", vm.Segments[0].Text);

        await WaitFor(() => _sessions.ListSessions().Single().LatestTranscriptRevision == 2, "edit indexed");
    }

    [Fact]
    public async Task Transcript_Revert_RestoresTheRawText()
    {
        var dir = await Transcribed();
        var vm = Transcript();
        vm.Open(dir, "テスト");
        var id = vm.Segments[0].SegmentId;
        var original = vm.Segments[0].Text;
        vm.Segments[0].Text = "誤った修正";
        vm.Selected = vm.Segments.Single(s => s.SegmentId == id);
        Assert.True(vm.RevertCommand.CanExecute(null));

        vm.RevertCommand.Execute(null);

        Assert.Equal(3, vm.SelectedRevision!.Revision);
        Assert.Equal(original, vm.Segments.Single(s => s.SegmentId == id).Text);
        Assert.Equal(TranscriptChangeKind.RevertedToOriginal, new TranscriptStore(dir).Load(3).Changes.Single().Kind);
    }

    [Fact]
    public async Task Transcript_ReRecognize_QueuesARangeJob_AndShowsTheNewRevisionWhenDone()
    {
        var dir = await Transcribed();
        var vm = Transcript();
        vm.Open(dir, "テスト");
        var micRows = vm.Segments.Where(s => s.Source == AudioSource.Mic).Take(2).ToList();
        vm.ReRecognizeModel = "ggml-medium.bin";
        _engine.OnRun = r => FakeSttEngine.Utterance(r, 1_500, 3_000, "再認識した文");

        vm.ReRecognizeCommand.Execute(micRows);
        Assert.Contains("再認識を順番待ち", vm.Message, StringComparison.Ordinal);

        await WaitFor(() => vm.SelectedRevision?.Revision == 2, "range job applied");
        Assert.Contains("Rev 2", vm.Message, StringComparison.Ordinal);
        var change = new TranscriptStore(dir).Load(2).Changes.Single();
        Assert.Equal(TranscriptChangeKind.ReRecognized, change.Kind);
        Assert.Equal("ggml-medium.bin", change.Provenance!.ModelName);
        Assert.Equal(new TimeRange(micRows[0].Segment.StartMs, micRows[1].Segment.EndMs), change.Range);
    }

    [Fact]
    public async Task Transcript_ARawRevisionThatEndsAfterAnEdit_DoesNotHideTheEdit_UntilTheUserChoosesIt()
    {
        var dir = await Transcribed();
        var vm = Transcript();
        vm.Open(dir, "テスト");
        var id = vm.Segments[0].SegmentId;
        vm.Segments[0].Text = "利用者の修正";

        _coordinator.Enqueue(dir); // e.g. a job queued earlier, or resumed after a restart
        await WaitFor(() => vm.Message?.Contains("新しい原文 Rev 3", StringComparison.Ordinal) == true, "late raw revision reported");

        Assert.Contains("使用中の Rev 2", vm.Message, StringComparison.Ordinal);
        Assert.Equal(2, vm.SelectedRevision!.Revision);
        Assert.True(vm.IsCurrent);
        Assert.Equal("利用者の修正", vm.Segments.Single(s => s.SegmentId == id).Text);
        Assert.Equal(["Rev 3  原文（small（速い・固有名詞に弱い））", "Rev 2  修正（本文）　［使用中］", "Rev 1  原文（small（速い・固有名詞に弱い））"], vm.Revisions.Select(r => r.Label));

        vm.SelectedRevision = vm.Revisions.Single(r => r.Revision == 3);
        Assert.False(vm.IsCurrent);
        Assert.True(vm.AdoptCommand.CanExecute(null));
        vm.AdoptCommand.Execute(null);

        Assert.True(vm.IsCurrent);
        Assert.Contains("Rev 3 を使用中にしました", vm.Message, StringComparison.Ordinal);
        Assert.Equal(3, new TranscriptStore(dir).CurrentRevision());
        Assert.Equal("Rev 3  原文（small（速い・固有名詞に弱い））　［使用中］", vm.Revisions.Single(r => r.Revision == 3).Label);
        vm.Segments[0].Text = "新しい原文への修正";
        Assert.Equal(3, new TranscriptStore(dir).Load(4).ParentRevision);
    }

    [Fact]
    public async Task Transcript_ReRecognize_RefusesMixedSources()
    {
        var dir = await Transcribed();
        var vm = Transcript();
        vm.Open(dir, "テスト");

        vm.ReRecognizeCommand.Execute(vm.Segments.Take(2).ToList()); // one mic, one loopback at 1 s

        Assert.Contains("1 つの音源ごと", vm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_engine.Runs, r => r.OutputName == "range");
    }

    [Fact]
    public async Task Transcript_PlaysASegment_FromItsTimeInTheRightChunk_ForItsLength()
    {
        var dir = await Transcribed();
        var vm = Transcript();
        vm.Open(dir, "テスト");
        var row = vm.Segments.First(s => s.Source == AudioSource.Loopback && s.StartMs == 29_000);

        vm.PlayCommand.Execute(row);

        Assert.Equal(TimeSpan.FromSeconds(29), _player.LastStart);
        Assert.Equal(TimeSpan.FromMilliseconds(2_300), _player.LastDuration);
        Assert.EndsWith(Path.Combine("audio", "loopback", "000001.wav"), _player.LastPlayed![0], StringComparison.Ordinal);
        Assert.Contains("PC 内部音声", vm.NowPlaying, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcript_Filters_BySourceAndByWhatNeedsChecking()
    {
        var dir = await Transcribed();
        var vm = Transcript();
        vm.Open(dir, "テスト");

        vm.Filter = SourceFilter.Mic;
        Assert.Equal(3, vm.Segments.Count);
        Assert.All(vm.Segments, s => Assert.Equal(AudioSource.Mic, s.Source));
        vm.Filter = SourceFilter.All;
        vm.Segments[0].Text = "金曜日までに提出しません";
        vm.FlaggedOnly = true;
        Assert.Equal("期日・否定", Assert.Single(vm.Segments).HintText);
        Assert.Equal("1 区間（全 6）", vm.CountText);
    }

    [Fact]
    public async Task Processing_Diagnostics_ShowWhatTheyHold_AndAreWrittenOnlyWhenTheUserAgrees() // 21.3
    {
        var processing = new ProcessingViewModel(_coordinator, _transcripts, _ui, new RecordingEnvironment(_workspace.Root, 0));
        var written = 0;
        string? asked = null;
        var answer = false;
        processing.WriteDiagnostics = () => { written++; return Path.Combine(_workspace.Root, "diagnostics.json"); };
        processing.Confirm = (_, message) => { asked = message; return answer; };

        await processing.ExportDiagnosticsCommand.ExecuteAsync(null);
        Assert.Equal(0, written);
        Assert.Contains("含めないもの: 原音、文字起こし・記録・課題・タスクの本文", asked, StringComparison.Ordinal);
        Assert.Contains("どこにも送信しません", asked, StringComparison.Ordinal);

        answer = true;
        await processing.ExportDiagnosticsCommand.ExecuteAsync(null);
        Assert.Equal(1, written);
        Assert.Contains("診断情報を書き出しました", processing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Processing_ShowsJobs_AndRetriesAFailedJobFromItsFinishedPieces()
    {
        var (dir, _) = Session();
        var processing = new ProcessingViewModel(_coordinator, _transcripts, _ui, new RecordingEnvironment(_workspace.Root, 0));
        _engine.OnRun = r => r.OutputName == "loopback-0002" ? throw new SttException(SttErrorCode.ToolMissing, "gone") : FakeSttEngine.Utterance(r, 1_000, 3_000, "発話");
        var job = _coordinator.Enqueue(dir);
        // The screen reads the DB index, which is updated just after the ledger: wait for both (flaky under load before).
        await WaitFor(() => SttJobStore.Load(dir, job.JobId).State == ProcessingState.Failed
            && _transcripts.RecentJobs(50).Any(j => j.JobId == job.JobId && j.State == ProcessingState.Failed), "job fails");
        await processing.RefreshAsync();

        var row = Assert.Single(processing.Jobs);
        Assert.Equal("失敗", row.StateText);
        Assert.StartsWith("STT-001", row.ErrorText, StringComparison.Ordinal);
        processing.Selected = row;
        Assert.True(processing.RetryCommand.CanExecute(null));
        Assert.False(processing.CancelCommand.CanExecute(null));

        _engine.Runs.Clear();
        _engine.OnRun = null;
        processing.RetryCommand.Execute(null);
        await WaitFor(() => SttJobStore.Load(dir, job.JobId).State == ProcessingState.Transcribed
            && _transcripts.RecentJobs(50).Any(j => j.JobId == job.JobId && j.State == ProcessingState.Transcribed), "retried job");

        Assert.Equal(["loopback-0002", "mic-0003", "loopback-0003"], _engine.Runs.Select(r => r.OutputName)); // finished pieces kept
        await processing.RefreshAsync();
        Assert.Equal("完了（Rev 1）", processing.Jobs.Single().StateText);
    }

    [Fact]
    public void Settings_AreSavedForTheNextJob()
    {
        var settings = new SettingsViewModel(_settings, _engine, _tools, new RecordingEnvironment(_workspace.Root, 0));
        Assert.Equal("ggml-small.bin", settings.ModelName);

        settings.ModelName = "ggml-medium.bin";
        settings.UseVad = false;
        settings.Threads = 2;

        var saved = _settings.Load();
        Assert.Equal("ggml-medium.bin", saved.ModelName);
        Assert.False(saved.UseVad);
        Assert.Equal(2, saved.Threads);
        Assert.Equal(JobSession.Config.PieceMs, saved.PieceMs); // other values untouched
        Assert.Equal("ggml-medium.bin", _coordinator.CurrentConfiguration.ModelName);
        Assert.Contains("次に始める", settings.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_VerifyTools_ReportsEachModel()
    {
        var settings = new SettingsViewModel(_settings, _engine, _tools, new RecordingEnvironment(_workspace.Root, 0));

        await settings.VerifyToolsCommand.ExecuteAsync(null);

        Assert.Equal("✓ whisper-cli（whisper.cpp 1.9.5）", settings.ToolStatus[0]);
        Assert.Contains("✓ medium（既定・高精度）", settings.ToolStatus);
        Assert.Contains("✓ small（速い・固有名詞に弱い）", settings.ToolStatus);
    }

    [Fact]
    public async Task Settings_CheckEnvironment_ReportsStorageSpaceAndTools_AndOfflineReadiness() // UI-01
    {
        var disk = new FakeFreeSpace(50L * 1024 * 1024 * 1024);
        var settings = new SettingsViewModel(_settings, _engine, _tools, new RecordingEnvironment(_workspace.Root, 2L * 1024 * 1024 * 1024), disk: disk);
        Assert.Equal("空き容量 50.0 GB（録音を始めるには 2.0 GB 以上が必要）", settings.FreeSpaceText);

        await settings.CheckEnvironmentCommand.ExecuteAsync(null);

        Assert.StartsWith("✓ 保存先に書き込めます", settings.EnvironmentStatus[0], StringComparison.Ordinal);
        Assert.Equal("✓ 空き容量 50.0 GB", settings.EnvironmentStatus[1]);
        Assert.StartsWith("✓ 文字起こし: whisper-cli", settings.EnvironmentStatus[2], StringComparison.Ordinal);
        Assert.StartsWith("✓ 必要なファイルはすべてこの PC にあります", settings.EnvironmentStatus[^1], StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_workspace.Root, ".write-check-*")); // the probe is removed

        disk.AvailableBytes = 1L * 1024 * 1024 * 1024;
        await settings.CheckEnvironmentCommand.ExecuteAsync(null);

        Assert.StartsWith("✗ ", settings.EnvironmentStatus[1], StringComparison.Ordinal);
        Assert.StartsWith("✗ の項目を直すまで", settings.EnvironmentStatus[^1], StringComparison.Ordinal);
    }

    private sealed class MemoryLocation : IWorkspaceLocationStore
    {
        public string? Root { get; set; }

        public string? Load() => Root;

        public void Save(string? root) => Root = root;
    }

    [Fact]
    public void Settings_ChangeWorkspace_AcceptsAnEmptyFolderOrAWorkspace_AfterConfirmation_ForTheNextStart() // 15.1
    {
        var dialogs = new FakeDialogs();
        var location = new MemoryLocation();
        var settings = new SettingsViewModel(_settings, _engine, _tools, new RecordingEnvironment(_workspace.Root, 0), dialogs: dialogs, location: location);
        Assert.True(settings.CanChangeWorkspace);

        var busy = _dir.File("busy");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "memo.txt"), "x");
        dialogs.PickedFolder = busy;
        settings.ChangeWorkspaceCommand.Execute(null);
        Assert.Contains("空のフォルダーか", settings.WorkspaceMessage, StringComparison.Ordinal); // a folder with other files
        Assert.Null(location.Root);

        var empty = _dir.File("new-workspace");
        Directory.CreateDirectory(empty);
        dialogs.PickedFolder = empty;
        dialogs.ConfirmAnswer = false;
        settings.ChangeWorkspaceCommand.Execute(null);
        Assert.Null(location.Root); // declined
        Assert.Contains("移さず、そのまま残します", dialogs.Confirmations[^1], StringComparison.Ordinal);

        dialogs.ConfirmAnswer = true;
        settings.ChangeWorkspaceCommand.Execute(null);
        Assert.Equal(empty, location.Root);
        Assert.Equal($"次に起動したときの保存先: {empty}", settings.NextWorkspaceText);

        dialogs.PickedFolder = _workspace.Root; // an existing workspace is opened as it is
        settings.ChangeWorkspaceCommand.Execute(null);
        Assert.Contains("既存のワークスペースを開きます", dialogs.Confirmations[^1], StringComparison.Ordinal);
        Assert.Equal(_workspace.Root, location.Root);
        Assert.Null(settings.NextWorkspaceText); // the one in use

        settings.UseDefaultWorkspaceCommand.Execute(null);
        Assert.Null(location.Root);

        settings.StartedWithWorkspaceOption = true; // --workspace chose the folder: say so instead of a "next start" that would not happen
        Assert.StartsWith("起動オプション（--workspace）で指定した保存先を使っています", settings.NextWorkspaceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_ChecksSurviveAToolThatCannotStart() // review 2026-11-08: the check stayed busy
    {
        _engine.VerifyError = new System.ComponentModel.Win32Exception(1260, "blocked by policy");
        var settings = new SettingsViewModel(_settings, _engine, _tools, new RecordingEnvironment(_workspace.Root, 0));

        await settings.CheckEnvironmentCommand.ExecuteAsync(null);
        await settings.VerifyToolsCommand.ExecuteAsync(null);

        Assert.False(settings.IsCheckingEnvironment);
        Assert.False(settings.IsVerifying);
        Assert.Contains(settings.EnvironmentStatus, l => l.StartsWith("✗ 文字起こし: whisper-cli を確かめられませんでした", StringComparison.Ordinal));
        Assert.Equal(_tools.Models.Count, settings.ToolStatus.Count(l => l.StartsWith("✗ ", StringComparison.Ordinal) && l.Contains("確かめられませんでした", StringComparison.Ordinal)));
        _engine.VerifyError = null;
    }

    [Fact]
    public void WorkspaceChoice_RefusesAFolderInsideOrAroundTheWorkspaceInUse()
    {
        var inside = Path.Combine(_workspace.Root, "backups", "new");
        Directory.CreateDirectory(inside);
        Assert.Contains("ワークスペースの中に別のワークスペース", WorkspaceChoice.Problem(inside, [], _workspace.Root), StringComparison.Ordinal);
        Assert.Contains("ワークスペースの中に別のワークスペース", WorkspaceChoice.Problem(Path.GetDirectoryName(_workspace.Root)!, [], _workspace.Root), StringComparison.Ordinal);
        Assert.Null(WorkspaceChoice.Problem(_dir.File("elsewhere"), [], _workspace.Root));
    }

    [Fact]
    public void Settings_BackupRetention_IsSaved_WithinItsRange() // 14.4, UI-12
    {
        var ui = new MemoryUiSettingsStore();
        var settings = new SettingsViewModel(_settings, _engine, _tools, new RecordingEnvironment(_workspace.Root, 0), ui: ui);
        Assert.True(settings.CanChangeRetention);
        Assert.Equal(90, settings.BackupRetentionDays);

        settings.BackupRetentionDays = 30;
        Assert.Equal(30, ui.Load().BackupRetentionDays);
        Assert.Contains("30 日", settings.RetentionMessage, StringComparison.Ordinal);

        settings.BackupRetentionDays = 1; // too short: kept at the minimum
        Assert.Equal(UiSettings.MinRetentionDays, ui.Load().BackupRetentionDays);
        Assert.Contains("範囲に直して", settings.RetentionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceChoice_RefusesASyncFolder_AndTheLocationIsSavedAsJson()
    {
        var sync = _dir.File("OneDrive");
        Directory.CreateDirectory(Path.Combine(sync, "VJ"));
        Assert.Contains("同期フォルダー", WorkspaceChoice.Problem(Path.Combine(sync, "VJ"), [sync]), StringComparison.Ordinal);
        Assert.Null(WorkspaceChoice.Problem(_dir.File("not-yet-created"), [sync]));

        var store = new JsonWorkspaceLocationStore(_dir.File("config/workspace.json"));
        Assert.Null(store.Load());
        store.Save(_dir.File("ws"));
        Assert.Equal(_dir.File("ws"), store.Load());
        store.Save(null);
        Assert.Null(store.Load());
        File.WriteAllText(_dir.File("config/workspace.json"), "{ broken");
        Assert.Null(store.Load()); // a broken file means the default
    }

    [Theory]
    [InlineData("それでは始めます", "")]
    [InlineData("予算は3000円です", "数字")]
    [InlineData("二十五人が参加", "数字")]
    [InlineData("来週の金曜までにお願いします", "期日")]
    [InlineData("今回は購入しません", "否定")]
    [InlineData("田中さんが調査します", "人名")]
    public void ReviewHints_PointAtNumbersDatesNegationAndNames(string text, string expected) =>
        Assert.Equal(expected, ReviewHints.For(text));
}
