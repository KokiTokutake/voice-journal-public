using Microsoft.Data.Sqlite;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Management;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.Core.Management;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.AppTests;

public sealed class ManagementViewModelTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-manage-vm-");
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly ManagementStore _store;
    private readonly FakeDialogs _dialogs = new();
    private readonly Guid _sessionId;

    public ManagementViewModelTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        _db = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        _store = new ManagementStore(_workspace, _db);
        var (dir, id) = JobSession.Create(Path.Combine(_workspace.SessionsRoot, "2026", "11"));
        _sessionId = id;
        new SessionIndex(_workspace, _db, TimeProvider.System).Upsert(dir);
        var a = T.Seg(0, 4_000, "見積書は田村さんが明日までに作ります");
        var b = T.Seg(5_000, 9_000, "ログイン画面の不具合は原因がまだ分かりません");
        var c = T.Seg(10_000, 14_000, "議事録の共有はやらなくていいかもしれません");
        new TranscriptStore(dir).Save(T.Raw(id, 1, a, b, c));
        var record = new RecordDocument
        {
            SessionId = id,
            Revision = 1,
            ParentRevision = null,
            Kind = RecordRevisionKind.Generated,
            TranscriptRevision = 1,
            CreatedAtUtc = M.Now,
            Provenance = new RecordProvenance { AnalysisRevision = 1, ModelId = "gemma", ModelSha256 = null, PromptVersion = "p", ConfigHash = new string('a', 64), JobId = null },
            Title = "定例",
            TitleOrigin = TextOrigin.Ai,
            RecordTypes = [RecordType.Meeting],
            Summary = [],
            Sections = [],
            Candidates =
            [
                new RecordCandidate { Candidate = M.C("C0001", "見積書の作成", assignee: "田村", segment: a.SegmentId), Flags = [] },
                new RecordCandidate { Candidate = M.C("C0002", "ログイン不具合の原因調査", CandidateKind.Issue, segment: b.SegmentId), Flags = [] },
                new RecordCandidate { Candidate = M.C("C0003", "議事録の共有", segment: c.SegmentId), Flags = [CandidateConcern.Doubtful, CandidateConcern.Negation] },
            ],
            Warnings = [],
            MissingPieces = [],
            Unsupported = [],
        };
        new RecordStore(dir).Save(record, _ => "# 定例\n");
        new RecordIndex(_workspace, _db).UpsertSession(dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private ManagementViewModel Vm()
    {
        var engine = new ApplyEngine(_workspace, _db, _store);
        var vm = new ManagementViewModel(_store, _dialogs, TimeProvider.System, engine, new ApplyRecovery(_db, _store, engine));
        vm.Refresh();
        return vm;
    }

    [Fact]
    public void TheSessionsCandidates_AreShownAsCards_WithTheirEvidenceAndConcerns()
    {
        var vm = Vm();
        Assert.Equal(_sessionId, Assert.Single(vm.Sessions).Session.SessionId);
        Assert.Equal(["見積書の作成", "ログイン不具合の原因調査", "議事録の共有"], vm.Cards.Select(c => c.Title));
        Assert.Contains("田村", vm.Cards[0].DetailText, StringComparison.Ordinal);
        Assert.StartsWith("00:00:00 「", vm.Cards[0].Evidence.Single(), StringComparison.Ordinal);
        Assert.Contains("要確認", vm.Cards[2].ConcernText, StringComparison.Ordinal);
        Assert.All(vm.Cards, c => Assert.Equal("未確認", c.StateText));
        Assert.False(vm.Cards[0].Choices[1].IsEnabled); // no existing items: nothing to update
        Assert.Contains("承認されていません", vm.Message, StringComparison.Ordinal); // the record itself is not approved (AT-11)
    }

    [Fact]
    public void SelectAll_PicksOnlyThePlainNewCandidates()
    {
        var vm = Vm();
        vm.SelectPlainCommand.Execute(null);
        Assert.Equal([ReviewChoice.Create, ReviewChoice.Create, null], vm.Cards.Select(c => c.Review.Choice));
        Assert.Equal("承認 2 件／候補 3 件", vm.ReadyText);
    }

    [Fact]
    public void Choices_AreSaved_AndComeBack()
    {
        var vm = Vm();
        vm.Cards[0].Choices[0].IsChecked = true;
        vm.Cards[1].Choices[4].IsChecked = true;
        vm.Cards[2].Choices[0].IsChecked = true;
        Assert.True(vm.Cards[2].NeedsConfirmation);
        Assert.Contains("個別の確認", vm.Cards[2].StateText, StringComparison.Ordinal);
        vm.Cards[2].ConfirmedIndividually = true;
        vm.Cards[0].Title = "見積書の作成と送付";

        var again = Vm();
        Assert.Equal([ReviewChoice.Create, ReviewChoice.Defer, ReviewChoice.Create], again.Cards.Select(c => c.Review.Choice));
        Assert.True(again.Cards[2].ConfirmedIndividually);
        Assert.Equal("見積書の作成と送付", again.Cards[0].Title);
        Assert.True(again.Cards[0].Choices[0].IsChecked);
        Assert.False(again.Cards[0].Choices[4].IsChecked);
    }

    [Fact]
    public void ThePlan_HoldsOnlyTheApprovedCards_ShowsTheDiff_AndIsSavedOnce() // AT-11, AT-12
    {
        var vm = Vm();
        vm.Cards[0].Choices[0].IsChecked = true; // new
        vm.Cards[1].Choices[3].IsChecked = true; // rejected
        vm.Cards[2].Choices[0].IsChecked = true; // new, but not confirmed individually

        vm.BuildPlanCommand.Execute(null);
        Assert.True(vm.HasPlan);
        var file = Assert.Single(vm.PlannedFiles);
        Assert.Equal("management/tasks.md（新規作成）", file.Header);
        Assert.Contains("+### TSK-000001 見積書の作成", file.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("議事録", file.Diff, StringComparison.Ordinal);
        Assert.Contains("議事録の共有", vm.PlanNotes, StringComparison.Ordinal);

        vm.SavePlanCommand.Execute(null);
        Assert.Contains("保存しました", vm.Message, StringComparison.Ordinal);
        Assert.All(_store.Files(), f => Assert.Null(f.CurrentBytes)); // the management files are not written in Phase E

        vm.BuildPlanCommand.Execute(null); // the same choices again: already in the saved plan
        Assert.False(vm.HasPlan);
        Assert.Contains("反映待ち", vm.PlanNotes, StringComparison.Ordinal);
        Assert.Single(_store.Batches());
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void TheSameApproval_SavedFromTwoWindows_IsOneBatch() // AT-12
    {
        var vm = Vm();
        vm.Cards[0].Choices[0].IsChecked = true;
        var other = Vm();
        vm.BuildPlanCommand.Execute(null);
        other.BuildPlanCommand.Execute(null); // the same approval, built before either is saved
        vm.SavePlanCommand.Execute(null);
        other.SavePlanCommand.Execute(null);
        Assert.Contains("すでに保存されています", other.Message, StringComparison.Ordinal);
        Assert.Single(_store.Batches());
    }

    [Fact]
    public void ApplyingThePlan_WritesTheFile_ListsTheItem_AndTheCandidateCannotBeAppliedAgain() // AT-11, AT-12
    {
        var vm = Vm();
        vm.Cards[0].Choices[0].IsChecked = true;
        vm.Cards[1].Choices[4].IsChecked = true; // deferred: stays out
        vm.BuildPlanCommand.Execute(null);
        vm.SaveAndApplyCommand.Execute(null);

        Assert.Contains("反映しました", vm.Message, StringComparison.Ordinal);
        Assert.Equal("TSK-000001", Assert.Single(vm.Items).DisplayId);
        Assert.Equal("反映済み", Assert.Single(vm.Batches).StateText);
        Assert.Contains("見積書の作成", File.ReadAllText(_store.FullPath(ManagementStore.TasksPath)), StringComparison.Ordinal);
        Assert.False(File.Exists(_store.FullPath(ManagementStore.IssuesPath))); // the deferred issue was not written

        vm.BuildPlanCommand.Execute(null);
        Assert.False(vm.HasPlan);
        Assert.Contains("反映済み", vm.PlanNotes, StringComparison.Ordinal);
    }

    [Fact]
    public void ASavedPlan_CanBeAbandoned_AndTheCandidatesPlannedAgain()
    {
        var vm = Vm();
        vm.Cards[0].Choices[0].IsChecked = true;
        vm.BuildPlanCommand.Execute(null);
        vm.SavePlanCommand.Execute(null);
        var row = Assert.Single(vm.Batches);
        Assert.Equal("反映待ち", row.StateText);

        vm.AbandonBatchCommand.Execute(row);
        Assert.Equal("破棄", Assert.Single(vm.Batches).StateText);
        vm.BuildPlanCommand.Execute(null);
        Assert.True(vm.HasPlan);
        Assert.Contains("TSK-000002", vm.PlannedFiles.Single().Diff, StringComparison.Ordinal); // TSK-000001 stays used
    }

    [Fact]
    public void NothingApproved_GivesNoPlan()
    {
        var vm = Vm();
        vm.BuildPlanCommand.Execute(null);
        Assert.False(vm.HasPlan);
        Assert.Contains("ありません", vm.Message, StringComparison.Ordinal);
    }
}
