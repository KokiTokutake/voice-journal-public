using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Management;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.Core.Management;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Storage;

public sealed class ManagementStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-management-");
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly ManagementStore _store;

    public ManagementStoreTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        _db = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        _store = new ManagementStore(_workspace, _db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private PlanningInput Input(params CandidateReview[] reviews)
    {
        var (issue, task) = _store.NextNumbers();
        var (keys, prints) = _store.Applied();
        return new PlanningInput
        {
            WorkspaceId = _workspace.Info.WorkspaceId,
            Items = _store.Items(),
            Reviews = reviews,
            Applied = keys,
            AppliedFingerprints = prints,
            Files = _store.Files(),
            NextIssueNumber = issue,
            NextTaskNumber = task,
            NowUtc = M.Now,
        };
    }

    [Fact]
    public void TheTwoManagementFiles_AreRegistered_InsideTheWorkspace_AndDoNotExistYet()
    {
        var files = _store.Files();
        Assert.Equal([(CandidateKind.Issue, ManagementStore.IssuesPath), (CandidateKind.Task, ManagementStore.TasksPath)], files.Select(f => (f.Kind, f.RelativePath)));
        Assert.All(files, f => Assert.Null(f.CurrentBytes));
        Assert.Equal(files.Select(f => f.FileId), _store.Files().Select(f => f.FileId)); // registered once
        Assert.Throws<UnauthorizedAccessException>(() => _store.FullPath("../outside.md")); // 21.1

        _store.SetMode(CandidateKind.Task, ManagedFileMode.AppendOnly);
        Assert.Equal(ManagedFileMode.AppendOnly, _store.Files().Single(f => f.Kind == CandidateKind.Task).Mode);
    }

    [Fact]
    public void AReview_IsSavedAndReadBack()
    {
        var review = M.R(M.C("C0003", "見積書の作成"), ReviewChoice.Update, Guid.NewGuid(), 4, confirmed: true) with
        {
            EditedFields = new CandidateFields { Title = "見積書の作成（改）", Description = "", Status = ItemStatus.InProgress, Priority = null, Assignee = "田村", DueDate = new DateOnly(2026, 11, 20) },
            FieldsToChange = [ItemField.Title, ItemField.DueDate],
        };
        _store.SaveReview(review);
        _store.SaveReview(review with { Choice = ReviewChoice.Defer });

        var stored = Assert.Single(_store.Reviews(M.Session)).Value;
        Assert.Equal((ReviewChoice.Defer, review.TargetItemId, 4, true), (stored.Choice, stored.TargetItemId, stored.TargetRevision, stored.Confirmed));
        Assert.Equal(review.EditedFields, stored.EditedFields);
        Assert.Equal([ItemField.Title, ItemField.DueDate], stored.FieldsToChange);
        Assert.Empty(_store.Reviews(Guid.NewGuid()));
    }

    [Fact]
    public void APlan_IsSavedAsPlanned_WithItsFiles_AndTheManagementFilesAreNotTouched() // AT-11
    {
        var plan = ChangePlanner.Plan(Input(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create), M.R(M.C("C0002", "議事録"), ReviewChoice.Defer)), () => Guid.NewGuid());
        var saved = _store.SavePlan(plan);

        Assert.False(saved.Reused);
        Assert.Equal("planned", saved.State);
        Assert.True(File.Exists(Path.Combine(saved.Directory, "plan.json")));
        Assert.True(File.Exists(Path.Combine(saved.Directory, "review.json")));
        var blob = Path.Combine(saved.Directory, "blobs", plan.Files.Single().StagedBlobId.ToString("D") + ".bin");
        Assert.Equal(plan.Files.Single().AfterBytes, File.ReadAllBytes(blob));
        Assert.All(_store.Files(), f => Assert.Null(f.CurrentBytes)); // nothing is written to the management files in Phase E
        Assert.Empty(_store.Items());
        var batch = Assert.Single(_store.Batches());
        Assert.Equal((plan.BatchId, "planned", 1, 1), (batch.BatchId, batch.State, batch.Changes, batch.Files));
        Assert.Equal((1L, 2L), _store.NextNumbers()); // TSK-000001 is reserved
    }

    [Fact]
    public void TheSameApproval_SavedTwice_ReturnsTheFirstBatch() // AT-12
    {
        var reviews = new[] { M.R(M.C("C0001", "見積書の作成", segment: Guid.Parse("22222222-2222-4222-8222-222222222222")), ReviewChoice.Create) };
        var first = _store.SavePlan(ChangePlanner.Plan(Input(reviews), () => Guid.NewGuid()));
        var input = Input(reviews) with { NextTaskNumber = 1 }; // the same numbers as the first plan
        var again = _store.SavePlan(ChangePlanner.Plan(input, () => Guid.NewGuid()));

        Assert.True(again.Reused);
        Assert.Equal(first.BatchId, again.BatchId);
        Assert.Single(_store.Batches());
        Assert.Single(Directory.GetDirectories(_store.FullPath(ManagementStore.TransactionsFolder)));
    }

    [Fact]
    public void ACandidateInASavedPlan_IsNotPlannedAgain_EvenFromARegeneratedRecord() // AT-12
    {
        var candidate = M.C("C0001", "見積書の作成");
        _store.SavePlan(ChangePlanner.Plan(Input(M.R(candidate, ReviewChoice.Create)), () => Guid.NewGuid()));
        var (keys, prints) = _store.Pending();

        var again = ChangePlanner.Plan(Input(M.R(candidate, ReviewChoice.Create), M.R(candidate with { CandidateId = "C0004" }, ReviewChoice.Create, analysisRevision: 2))
            with { Pending = keys, PendingFingerprints = prints }, () => Guid.NewGuid());

        Assert.Empty(again.Changes);
        Assert.All(again.Skipped, s => Assert.Contains("反映待ち", s.Reason, StringComparison.Ordinal));

        _store.Abandon(_store.Batches().Single().BatchId); // once abandoned, it can be planned again
        var (k2, p2) = _store.Pending();
        Assert.Empty(k2);
        Assert.Empty(p2);
    }

    [Fact]
    public void AnAbandonedPlan_KeepsItsDisplayNumbersUsed()
    {
        var saved = _store.SavePlan(ChangePlanner.Plan(Input(M.R(M.C("C0001", "ログイン不具合", CandidateKind.Issue), ReviewChoice.Create)), () => Guid.NewGuid()));
        _store.Abandon(saved.BatchId);
        Assert.Equal("abandoned", _store.Batches().Single().State);
        Assert.Equal(2L, _store.NextNumbers().Issue);
    }

    private void InsertItem(string displayId, string kind, string title, string status, string? assignee = null, string? due = null, string description = "")
    {
        using var c = _db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items (id, workspace_id, display_id, kind, project, title, description, status, assignee, due_date, revision, created_at, updated_at)
            VALUES ($id, $ws, $display, $kind, '未分類', $title, $description, $status, $assignee, $due, 1, '2026-11-01T00:00:00.0000000Z', '2026-11-01T00:00:00.0000000Z')
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$ws", _workspace.Info.WorkspaceId.ToString("D"));
        cmd.Parameters.AddWithValue("$display", displayId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$assignee", (object?)assignee ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$due", (object?)due ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Search_FindsShortWords_PartsOfWords_AndFiltersByStatusKindAndDueDate() // AT-21
    {
        _store.Files();
        InsertItem("TSK-000001", "task", "見積書の作成", "todo", "田村", "2026-11-20");
        InsertItem("TSK-000002", "task", "議事録の共有", "done", due: "2026-11-05", description: "見出しを直す");
        InsertItem("ISS-000001", "issue", "ログイン不具合（100% 再現）", "open");
        InsertItem("TSK-000003", "task", "a_b の確認", "in_progress");

        string[] Ids(IReadOnlyList<ManagementItem> items) => [.. items.Select(i => i.DisplayId)];
        Assert.Equal(["TSK-000001", "TSK-000002"], Ids(_store.Search("見"))); // one character: title or description
        Assert.Equal(["TSK-000001"], Ids(_store.Search("積書")));
        Assert.Equal(["TSK-000001"], Ids(_store.Search("田村")));
        Assert.Equal(["ISS-000001"], Ids(_store.Search("100%"))); // 「%」 is a character, not a wildcard
        Assert.Equal(["TSK-000003"], Ids(_store.Search("a_b")));
        Assert.Empty(_store.Search("ab"));
        Assert.Equal(["TSK-000002"], Ids(_store.Search(null, ItemStatus.Done)));
        Assert.Equal(["ISS-000001"], Ids(_store.Search("", kind: CandidateKind.Issue)));
        Assert.Equal(["TSK-000001"], Ids(_store.Search(null, dueFrom: new DateOnly(2026, 11, 10), dueTo: new DateOnly(2026, 11, 30))));
        Assert.Equal(["TSK-000002"], Ids(_store.Search("TSK-000002")));
    }

    [Fact]
    public void ItemsWrittenByPhaseF_AreReadWithTheirEvidence_AndNumbersContinueAfterThem()
    {
        _store.Files();
        var id = Guid.NewGuid();
        using (var c = _db.OpenConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO items (id, workspace_id, display_id, kind, project, title, description, status, priority, assignee, due_date, revision, created_at, updated_at)
                VALUES ('{id:D}', '{_workspace.Info.WorkspaceId:D}', 'TSK-000007', 'task', '未分類', '見積書', '', 'in_progress', 'high', '田村', '2026-11-20', 3,
                        '2026-11-01T00:00:00.0000000Z', '2026-11-02T00:00:00.0000000Z');
                INSERT INTO item_evidence (id, item_id, session_id, transcript_revision, segment_id, quote)
                VALUES ('{Guid.NewGuid():D}', '{id:D}', '{M.Session:D}', 2, '{Guid.NewGuid():D}', '見積書は明日までに作ります');
                """;
            cmd.ExecuteNonQuery();
        }
        var item = Assert.Single(_store.Items());
        Assert.Equal(("TSK-000007", ItemStatus.InProgress, ItemPriority.High, "田村", new DateOnly(2026, 11, 20), 3), (item.DisplayId, item.Status, item.Priority, item.Assignee, item.DueDate, item.Revision));
        Assert.Equal("見積書は明日までに作ります", item.Evidence.Single().Quote);
        Assert.Equal(8L, _store.NextNumbers().Task);
        Assert.Empty(item.Problems());
    }
}
