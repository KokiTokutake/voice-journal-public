using System.Text;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Management;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.Core.Management;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Storage;

/// <summary>Stops the engine at one step, as if the app ended there (AT-17).</summary>
internal sealed class CrashAt(ApplyStep point, int sequence = -1) : IApplyFaults
{
    public void At(ApplyStep step, int seq)
    {
        if (step == point && (sequence < 0 || seq == sequence)) throw new SimulatedCrashException($"{step}/{seq}");
    }
}

internal sealed class SimulatedCrashException(string message) : Exception(message);

public sealed class ApplyEngineTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-apply-");
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly ManagementStore _store;

    public ApplyEngineTests()
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

    private ApplyEngine Engine(IApplyFaults? faults = null) => new(_workspace, _db, _store, TimeProvider.System, faults);

    private string Tasks => _store.FullPath(ManagementStore.TasksPath);

    private string Issues => _store.FullPath(ManagementStore.IssuesPath);

    private SavedPlan Plan(params CandidateReview[] reviews)
    {
        var (issue, task) = _store.NextNumbers();
        var (keys, prints) = _store.Applied();
        var (pending, pendingPrints) = _store.Pending();
        var plan = ChangePlanner.Plan(new PlanningInput
        {
            WorkspaceId = _workspace.Info.WorkspaceId,
            Items = _store.Items(),
            Reviews = reviews,
            Applied = keys,
            AppliedFingerprints = prints,
            Pending = pending,
            PendingFingerprints = pendingPrints,
            Files = _store.Files(),
            NextIssueNumber = issue,
            NextTaskNumber = task,
            NowUtc = M.Now,
        }, Guid.CreateVersion7);
        Assert.NotEmpty(plan.Changes);
        return _store.SavePlan(plan);
    }

    private string State(Guid batch) => _store.Batches().Single(b => b.BatchId == batch).State;

    [Fact]
    public void ANewPlan_CreatesTheFiles_ConfirmsTheItems_AndIsAppliedOnce() // AT-12
    {
        var saved = Plan(M.R(M.C("C0001", "見積書の作成", assignee: "田村"), ReviewChoice.Create), M.R(M.C("C0002", "ログイン不具合", CandidateKind.Issue), ReviewChoice.Create));

        var result = Engine().Apply(saved.BatchId);

        Assert.Equal(ApplyOutcome.Committed, result.Outcome);
        Assert.Equal("committed", State(saved.BatchId));
        Assert.Contains("TSK-000001 見積書の作成", File.ReadAllText(Tasks), StringComparison.Ordinal);
        Assert.Contains("ISS-000001 ログイン不具合", File.ReadAllText(Issues), StringComparison.Ordinal);
        Assert.Equal(["ISS-000001", "TSK-000001"], _store.Items().Select(i => i.DisplayId));
        Assert.Equal(2, _store.Applied().Keys.Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Tasks)!, "*.tmp"));

        Assert.Equal(ApplyOutcome.AlreadyCommitted, Engine().Apply(saved.BatchId).Outcome); // the button pressed again
        Assert.Equal(2, _store.Items().Count);
    }

    [Fact]
    public void AnUpdate_KeepsEveryByteOutsideItsBlock_InUtf16WithMixedLineEnds() // AT-13
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create), M.R(M.C("C0002", "議事録の共有"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        // The user rewrites the file in UTF-16LE with a BOM, mixed line ends and free notes around the blocks.
        var text = File.ReadAllText(Tasks).Replace("\r\n", "\n", StringComparison.Ordinal);
        text = "メモ: 手で書いた行\r\n" + text.Replace("vj:item のブロック", "vj:item のブロック\r\n（自由なメモ）", StringComparison.Ordinal) + "末尾のメモ\r\n";
        File.WriteAllBytes(Tasks, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]);
        var target = _store.Items().Single(i => i.DisplayId == "TSK-000001");
        var update = M.C("C0003", "見積書の作成", action: CandidateAction.Update, target: target.Id, description: "金額を直す", changed: [ItemField.Description]);

        var saved = Plan(M.R(update, ReviewChoice.Update, target.Id, target.Revision));
        Assert.Equal(ApplyOutcome.Committed, Engine().Apply(saved.BatchId).Outcome);

        var bytes = File.ReadAllBytes(Tasks);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        var after = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        var oldLayout = ManagementMarkdown.Parse(text, _workspace.Info.WorkspaceId);
        var newLayout = ManagementMarkdown.Parse(after, _workspace.Info.WorkspaceId);
        var o = oldLayout.Blocks.Single(b => b.ItemId == target.Id);
        var n = newLayout.Blocks.Single(b => b.ItemId == target.Id);
        Assert.Equal(text[..o.Start], after[..n.Start]);
        Assert.Equal(text[(o.Start + o.Length)..], after[(n.Start + n.Length)..]);
        Assert.Equal(2, _store.Items().Single(i => i.Id == target.Id).Revision);
    }

    [Fact]
    public void AnEditAfterTheApproval_StopsTheApply_AndTheEditIsKept() // AT-15
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var target = _store.Items().Single();
        var saved = Plan(M.R(M.C("C0002", "見積書", action: CandidateAction.Update, target: target.Id, description: "x", changed: [ItemField.Description]), ReviewChoice.Update, target.Id, 1));
        File.AppendAllText(Tasks, "外部で足した行\r\n");
        var edited = File.ReadAllBytes(Tasks);

        var result = Engine().Apply(saved.BatchId);

        Assert.Equal(ApplyOutcome.Conflict, result.Outcome);
        Assert.Contains("外部での編集", Assert.Single(result.Problems), StringComparison.Ordinal);
        Assert.Equal(edited, File.ReadAllBytes(Tasks));
        Assert.Equal("planned", State(saved.BatchId));
        Assert.Equal(1, _store.Items().Single().Revision);
    }

    private SavedPlan Revert(Guid batchId, List<string> conflicts)
    {
        var current = _store.Items().ToDictionary(i => i.Id);
        var reviews = RevertBuilder.Reviews(batchId, _store.AppliedChanges(batchId), current, conflicts);
        var (issue, task) = _store.NextNumbers();
        var (keys, prints) = _store.Applied();
        var plan = ChangePlanner.Plan(new PlanningInput
        {
            WorkspaceId = _workspace.Info.WorkspaceId,
            Items = [.. current.Values],
            Reviews = reviews,
            Applied = keys,
            AppliedFingerprints = prints,
            Files = _store.Files(),
            NextIssueNumber = issue,
            NextTaskNumber = task,
            NowUtc = M.Now,
        }, Guid.CreateVersion7);
        return plan.Changes.Count == 0 ? null! : _store.SavePlan(plan);
    }

    private (SavedPlan Saved, byte[] TasksBefore) StoppedForRecovery()
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create), M.R(M.C("C0002", "ログイン不具合", CandidateKind.Issue), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var task = _store.Items().Single(i => i.Kind == CandidateKind.Task);
        var issue = _store.Items().Single(i => i.Kind == CandidateKind.Issue);
        var saved = Plan(
            M.R(M.C("C0003", "x", action: CandidateAction.Update, target: issue.Id, description: "原因はキャッシュ", changed: [ItemField.Description], kind: CandidateKind.Issue), ReviewChoice.Update, issue.Id, 1),
            M.R(M.C("C0004", "x", action: CandidateAction.Update, target: task.Id, description: "金額を直す", changed: [ItemField.Description]), ReviewChoice.Update, task.Id, 1));
        var tasksBefore = File.ReadAllBytes(Tasks);
        Assert.Throws<SimulatedCrashException>(() => Engine(new CrashAt(ApplyStep.AfterReplace, 1)).Apply(saved.BatchId));
        File.AppendAllText(Tasks, "外部で足した行\r\n");
        Assert.Equal(RecoveryAction.RecoveryRequired, Assert.Single(new ApplyRecovery(_db, _store, Engine()).RecoverAll()).Action);
        return (saved, tasksBefore);
    }

    [Fact]
    public void AfterTheUserUndoesTheExternalEdit_TheStoppedBatchIsFinished() // AT-18: consistent after manual resolution
    {
        var (saved, tasksBefore) = StoppedForRecovery();
        File.WriteAllBytes(Tasks, tasksBefore); // the user removes the line they added

        var report = new ApplyRecovery(_db, _store, Engine()).Recover(saved.BatchId);

        Assert.Equal(RecoveryAction.Completed, report.Action);
        Assert.Equal("committed", State(saved.BatchId));
        Assert.All(_store.Items(), i => Assert.Equal(2, i.Revision));
        Assert.Contains("金額を直す", File.ReadAllText(Tasks), StringComparison.Ordinal);
    }

    [Fact]
    public void AStoppedBatchGivenUp_LeavesTheReplacedBlocksToBeTakenIn() // AT-18
    {
        var (saved, _) = StoppedForRecovery();

        _store.Abandon(saved.BatchId);

        Assert.Equal("abandoned", State(saved.BatchId));
        var text = ManagedText.Decode(File.ReadAllBytes(Issues))!;
        var edited = Assert.Single(ManagementMarkdown.EditedBlocks(text.Text, ManagementMarkdown.Parse(text.Text, _workspace.Info.WorkspaceId), _store.Items().ToDictionary(i => i.Id), text.LineEnd));
        Assert.Equal("ISS-000001", edited.Item.DisplayId); // replaced before the stop, not confirmed: offered to take in
        Assert.Contains("外部で足した行", File.ReadAllText(Tasks), StringComparison.Ordinal); // the external edit is kept
    }

    [Fact]
    public void AWorkspaceSnapshot_RestoresIntoANewFolder_WithTheSameHashes_AndAConsistentDatabase() // AT-20, 14.4
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create), M.R(M.C("C0002", "ログイン不具合", CandidateKind.Issue), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var session = Directory.CreateDirectory(Path.Combine(_workspace.SessionsRoot, "2026", "11", Guid.NewGuid().ToString("D"))).FullName;
        File.WriteAllBytes(Path.Combine(session, "000001.wav"), [1, 2, 3, 4]); // stands for original audio

        var snapshot = WorkspaceBackup.Create(_workspace, _db);
        Assert.Empty(WorkspaceBackup.Verify(snapshot.Directory));

        var restored = Path.Combine(_dir.Path, "Restored");
        Assert.Empty(WorkspaceBackup.Restore(snapshot.Directory, restored));
        Assert.Equal(File.ReadAllBytes(Tasks), File.ReadAllBytes(Path.Combine(restored, "management", "tasks.md")));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(restored, Path.GetRelativePath(_workspace.Root, session), "000001.wav")));
        var db2 = VoiceJournalDatabase.Open(Path.Combine(restored, "db", "voicejournal.db"));
        var store2 = new ManagementStore(Workspace.OpenOrCreate(restored, syncRoots: []), db2);
        Assert.Equal(_store.Items().Select(i => (i.DisplayId, i.Revision)), store2.Items().Select(i => (i.DisplayId, i.Revision)));

        File.AppendAllText(Path.Combine(restored, "management", "tasks.md"), "x");
        Assert.Contains(WorkspaceBackup.Verify(restored, Path.Combine(snapshot.Directory, WorkspaceBackup.HashesFile)), p => p.Contains("tasks.md", StringComparison.Ordinal));
        Assert.Throws<IOException>(() => WorkspaceBackup.Restore(snapshot.Directory, restored)); // never over an existing folder
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void AnAppliedUpdate_CanBeUndone_AsANewBatch_Once() // AT-20
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成", assignee: "田村"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var item = _store.Items().Single();
        var update = Plan(M.R(M.C("C0002", "x", action: CandidateAction.Update, target: item.Id, assignee: "鈴木", description: "金額を直す",
            changed: [ItemField.Assignee, ItemField.Description]), ReviewChoice.Update, item.Id, 1, confirmed: true));
        Engine().Apply(update.BatchId);
        Assert.Equal("鈴木", _store.Items().Single().Assignee);

        var conflicts = new List<string>();
        var undo = Revert(update.BatchId, conflicts);
        Assert.Empty(conflicts);
        Assert.Equal(ApplyOutcome.Committed, Engine().Apply(undo.BatchId).Outcome);

        var back = _store.Items().Single();
        Assert.Equal(("田村", "", 3), (back.Assignee, back.Description, back.Revision)); // the old values, as a new revision
        Assert.All(back.Evidence, e => Assert.Equal(M.Session, e.SessionId)); // only real sessions as evidence
        Assert.Contains("- 担当: 田村", File.ReadAllText(Tasks), StringComparison.Ordinal);
        Assert.Equal(3, _store.Batches().Count); // history is kept, not erased
        Assert.Null(Revert(update.BatchId, [])); // undone once only
    }

    [Fact]
    public void UndoingABatch_KeepsLaterChanges_AndCancelsCreatedItemsInsteadOfDeleting() // AT-20
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create), M.R(M.C("C0002", "請求書の送付"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var later = _store.Items().Single(i => i.DisplayId == "TSK-000001");
        var update = Plan(M.R(M.C("C0003", "x", action: CandidateAction.Update, target: later.Id, description: "後の変更", changed: [ItemField.Description]), ReviewChoice.Update, later.Id, 1));
        Engine().Apply(update.BatchId);

        var conflicts = new List<string>();
        var undo = Revert(first.BatchId, conflicts);
        Assert.Contains("TSK-000001", Assert.Single(conflicts), StringComparison.Ordinal);
        Assert.Equal(ApplyOutcome.Committed, Engine().Apply(undo.BatchId).Outcome);

        var items = _store.Items().ToDictionary(i => i.DisplayId);
        Assert.Equal(("後の変更", ItemStatus.Todo), (items["TSK-000001"].Description, items["TSK-000001"].Status)); // the later change stays
        Assert.Equal(ItemStatus.Cancelled, items["TSK-000002"].Status); // created by the batch: cancelled, not deleted
        Assert.Contains("TSK-000002 請求書の送付", File.ReadAllText(Tasks), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockEditedByHand_IsNeverWrittenOver() // 11.5
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var target = _store.Items().Single();
        File.WriteAllText(Tasks, File.ReadAllText(Tasks).Replace("- 担当: 未設定", "- 担当: 佐藤（手で書いた）", StringComparison.Ordinal));
        var edited = File.ReadAllBytes(Tasks);
        var (issue, task) = _store.NextNumbers();
        var plan = ChangePlanner.Plan(new PlanningInput
        {
            WorkspaceId = _workspace.Info.WorkspaceId,
            Items = _store.Items(),
            Reviews = [M.R(M.C("C0002", "x", action: CandidateAction.Update, target: target.Id, description: "金額を直す", changed: [ItemField.Description]), ReviewChoice.Update, target.Id, 1)],
            Files = _store.Files(),
            NextIssueNumber = issue,
            NextTaskNumber = task,
            NowUtc = M.Now,
        }, Guid.CreateVersion7);

        Assert.Empty(plan.Changes);
        Assert.Contains("手で編集", Assert.Single(plan.Skipped).Reason, StringComparison.Ordinal);
        Assert.Equal(edited, File.ReadAllBytes(Tasks));
    }

    private SavedPlan PlanEdit(ManagementItem item, string block, bool takeIn)
    {
        var review = ExternalEditBuilder.Review(Guid.CreateVersion7(), 1, item, block, takeIn, out var problem);
        Assert.Null(problem);
        var (issue, task) = _store.NextNumbers();
        var plan = ChangePlanner.Plan(new PlanningInput
        {
            WorkspaceId = _workspace.Info.WorkspaceId,
            Items = _store.Items(),
            Reviews = [review!],
            AcceptEditedBlocks = new HashSet<Guid> { item.Id },
            Files = _store.Files(),
            NextIssueNumber = issue,
            NextTaskNumber = task,
            NowUtc = M.Now,
        }, Guid.CreateVersion7);
        return _store.SavePlan(plan);
    }

    private (ManagementItem Item, string Block) EditByHand(string from, string to)
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        File.WriteAllText(Tasks, "手前のメモ\r\n" + File.ReadAllText(Tasks).Replace(from, to, StringComparison.Ordinal));
        var text = ManagedText.Decode(File.ReadAllBytes(Tasks))!;
        var items = _store.Items().ToDictionary(i => i.Id);
        var edited = Assert.Single(ManagementMarkdown.EditedBlocks(text.Text, ManagementMarkdown.Parse(text.Text, _workspace.Info.WorkspaceId), items, text.LineEnd));
        return (edited.Item, edited.Block);
    }

    [Fact]
    public void AnEditByHand_CanBeTakenIn_AndTheBlockIsRewrittenInTheAppsForm() // 11.2, 11.5
    {
        var (item, block) = EditByHand("- 担当: 未設定\r\n- 期限: 未設定", "- 担当: 佐藤\r\n- 期限: 2026-12-01");

        Assert.Equal(ApplyOutcome.Committed, Engine().Apply(PlanEdit(item, block, takeIn: true).BatchId).Outcome);

        var now = _store.Items().Single();
        Assert.Equal(("佐藤", new DateOnly(2026, 12, 1), 2), (now.Assignee, now.DueDate, now.Revision));
        Assert.Equal(item.Evidence.Count, now.Evidence.Count);
        Assert.All(now.Evidence, e => Assert.Equal(M.Session, e.SessionId));
        var text = File.ReadAllText(Tasks);
        Assert.StartsWith("手前のメモ", text, StringComparison.Ordinal); // outside the block: kept
        Assert.Contains("revision=2", text, StringComparison.Ordinal);
        var check = ManagedText.Decode(File.ReadAllBytes(Tasks))!;
        Assert.Empty(ManagementMarkdown.EditedBlocks(check.Text, ManagementMarkdown.Parse(check.Text, _workspace.Info.WorkspaceId), _store.Items().ToDictionary(i => i.Id), check.LineEnd));
    }

    [Fact]
    public void AnEditByHand_CanBeDiscarded_ByRewritingTheBlockFromTheItem()
    {
        var (item, block) = EditByHand("- 担当: 未設定", "- 担当: 佐藤");

        Assert.Equal(ApplyOutcome.Committed, Engine().Apply(PlanEdit(item, block, takeIn: false).BatchId).Outcome);

        Assert.Null(_store.Items().Single().Assignee);
        Assert.Contains("- 担当: 未設定", File.ReadAllText(Tasks), StringComparison.Ordinal);
        Assert.DoesNotContain("佐藤", File.ReadAllText(Tasks), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditThatCannotBeRead_IsNotTakenIn()
    {
        var item = M.Item("TSK-000001", "見積書");
        var block = ManagementMarkdown.Block(item, new Dictionary<Guid, ManagementItem>(), "\n").Replace("- 状態: todo", "- 状態: 進行中", StringComparison.Ordinal);
        Assert.Null(ExternalEditBuilder.Review(Guid.NewGuid(), 1, item with { Evidence = [new ItemEvidence { SessionId = M.Session, TranscriptRevision = 1, SegmentId = Guid.NewGuid(), Quote = "q" }] }, block, true, out var problem));
        Assert.Contains("進行中", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileHeldOpenByAnotherProgram_IsNotWritten() // AT-16
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var saved = Plan(M.R(M.C("C0002", "請求書の送付"), ReviewChoice.Create));
        var before = File.ReadAllBytes(Tasks);
        using (new FileStream(Tasks, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) // an editor keeps it open for writing
        {
            Assert.Throws<IOException>(() => Engine().Apply(saved.BatchId));
        }
        Assert.Equal(before, File.ReadAllBytes(Tasks));
        Assert.Equal("planned", State(saved.BatchId));
        Assert.Throws<UnauthorizedAccessException>(() => ManagedFileGuard.CheckLocation(Path.Combine(_dir.Path, "outside.md"), _workspace.Root));
    }

    [Fact]
    public void InAnAppendOnlyFile_UpdatesAreProposals_MentionsStayInTheDatabase_AndNewItemsAreAppended() // AT-14
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var item = _store.Items().Single();
        _store.SetMode(CandidateKind.Task, ManagedFileMode.AppendOnly);
        var before = File.ReadAllText(Tasks);

        var update = Plan(M.R(M.C("C0002", "x", action: CandidateAction.Update, target: item.Id, description: "金額を直す", changed: [ItemField.Description]), ReviewChoice.Update, item.Id, 1),
            M.R(M.C("C0003", "請求書の送付"), ReviewChoice.Create));
        Assert.Equal(ApplyOutcome.Committed, Engine().Apply(update.BatchId).Outcome);

        var after = File.ReadAllText(Tasks);
        Assert.StartsWith(before, after, StringComparison.Ordinal); // nothing existing is rewritten
        Assert.Contains("TSK-000002 請求書の送付", after, StringComparison.Ordinal);
        Assert.DoesNotContain("金額を直す", after, StringComparison.Ordinal);
        Assert.Equal(("", 1), (_store.Items().Single(i => i.Id == item.Id).Description, _store.Items().Single(i => i.Id == item.Id).Revision));

        var mention = Plan(M.R(M.C("C0004", "見積書", action: CandidateAction.Mention, target: item.Id), ReviewChoice.Mention, item.Id, 1));
        Assert.Equal(ApplyOutcome.Committed, Engine().Apply(mention.BatchId).Outcome);
        Assert.Equal(after, File.ReadAllText(Tasks)); // a database-only batch
        var mentioned = _store.Items().Single(i => i.Id == item.Id);
        Assert.Equal((2, 2), (mentioned.Revision, mentioned.Evidence.Count));
    }

    /// <summary>Another program writes the target at a given step (instead of the app stopping there).</summary>
    private sealed class WriteAt(ApplyStep point, int sequence, string path, string text) : IApplyFaults
    {
        public void At(ApplyStep step, int seq)
        {
            if (step == point && seq == sequence) File.WriteAllText(path, text);
        }
    }

    [Fact]
    public void AFileSwappedJustBeforeTheReplacement_StopsTheApply_AndTheSwappedContentIsKept() // AT-16: path swap
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var item = _store.Items().Single();
        var saved = Plan(M.R(M.C("C0002", "x", action: CandidateAction.Update, target: item.Id, description: "金額を直す", changed: [ItemField.Description]), ReviewChoice.Update, item.Id, 1));

        var result = Engine(new WriteAt(ApplyStep.BeforeReplace, 1, Tasks, "別のプログラムが置いた内容")).Apply(saved.BatchId);

        Assert.Equal(ApplyOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal("recovery_required", State(saved.BatchId));
        var kept = Directory.GetFiles(Path.Combine(_store.FullPath(_store.Batches().Single(b => b.BatchId == saved.BatchId).PlanPath), "replaced"));
        Assert.Equal("別のプログラムが置いた内容", File.ReadAllText(Assert.Single(kept))); // never lost
        Assert.Equal(1, _store.Items().Single().Revision); // the database is not confirmed
    }

    [Theory]
    [InlineData(@"\\server\share\management\tasks.md", @"\\server\share")]
    [InlineData(@"\\?\UNC\server\share\management\tasks.md", @"\\?\UNC\server\share")]
    public void ANetworkPath_IsNotWritten(string path, string root) => // AT-16: network
        Assert.Throws<UnauthorizedAccessException>(() => ManagedFileGuard.CheckLocation(path, root));

    [Fact]
    public void AFolderRedirectedByAJunction_IsNotWritten() // AT-16: reparse point
    {
        var outside = Directory.CreateDirectory(Path.Combine(_dir.Path, "elsewhere")).FullName;
        var management = Path.GetDirectoryName(Tasks)!;
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{management}\" \"{outside}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        {
            mklink!.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        var saved = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create));

        Assert.Throws<UnauthorizedAccessException>(() => Engine().Apply(saved.BatchId));

        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal("planned", State(saved.BatchId));
        Directory.Delete(management); // the junction only
    }

    public static TheoryData<ApplyStep, int> CrashPoints() => new()
    {
        { ApplyStep.Verified, 0 }, { ApplyStep.Locked, 0 }, { ApplyStep.BackupWritten, 1 }, { ApplyStep.TempWritten, 1 },
        { ApplyStep.TempWritten, 2 }, { ApplyStep.Prepared, 0 }, { ApplyStep.BeforeReplace, 1 }, { ApplyStep.AfterReplace, 1 },
        { ApplyStep.BeforeReplace, 2 }, { ApplyStep.AfterReplace, 2 }, { ApplyStep.BeforeDbCommit, 0 }, { ApplyStep.Committed, 0 },
    };

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void ACrashAtAnyStep_IsRecovered_WithoutWritingUnknownBytes_OrCommittingTwice(ApplyStep point, int sequence) // AT-17
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create), M.R(M.C("C0002", "ログイン不具合", CandidateKind.Issue), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var task = _store.Items().Single(i => i.Kind == CandidateKind.Task);
        var issue = _store.Items().Single(i => i.Kind == CandidateKind.Issue);
        var saved = Plan(
            M.R(M.C("C0003", "x", action: CandidateAction.Update, target: issue.Id, description: "原因はキャッシュ", changed: [ItemField.Description], kind: CandidateKind.Issue), ReviewChoice.Update, issue.Id, 1),
            M.R(M.C("C0004", "x", action: CandidateAction.Update, target: task.Id, description: "金額を直す", changed: [ItemField.Description]), ReviewChoice.Update, task.Id, 1));

        Assert.Throws<SimulatedCrashException>(() => Engine(new CrashAt(point, sequence)).Apply(saved.BatchId));

        var engine = Engine();
        var reports = new ApplyRecovery(_db, _store, engine).RecoverAll();
        if (State(saved.BatchId) == "planned") Assert.Equal(ApplyOutcome.Committed, engine.Apply(saved.BatchId).Outcome);
        Assert.Equal("committed", State(saved.BatchId));
        Assert.All(reports, r => Assert.NotEqual(RecoveryAction.RecoveryRequired, r.Action));
        Assert.Contains("原因はキャッシュ", File.ReadAllText(Issues), StringComparison.Ordinal);
        Assert.Contains("金額を直す", File.ReadAllText(Tasks), StringComparison.Ordinal);
        Assert.All(_store.Items(), i => Assert.Equal(2, i.Revision)); // confirmed once
        Assert.Equal(4, _store.Applied().Keys.Count);
    }

    [Fact]
    public void AnExternalEditAfterOneFileWasReplaced_StopsTheRecovery_AndKeepsBothVersions() // AT-18
    {
        var first = Plan(M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create), M.R(M.C("C0002", "ログイン不具合", CandidateKind.Issue), ReviewChoice.Create));
        Engine().Apply(first.BatchId);
        var task = _store.Items().Single(i => i.Kind == CandidateKind.Task);
        var issue = _store.Items().Single(i => i.Kind == CandidateKind.Issue);
        var saved = Plan(
            M.R(M.C("C0003", "x", action: CandidateAction.Update, target: issue.Id, description: "原因はキャッシュ", changed: [ItemField.Description], kind: CandidateKind.Issue), ReviewChoice.Update, issue.Id, 1),
            M.R(M.C("C0004", "x", action: CandidateAction.Update, target: task.Id, description: "金額を直す", changed: [ItemField.Description]), ReviewChoice.Update, task.Id, 1));
        Assert.Throws<SimulatedCrashException>(() => Engine(new CrashAt(ApplyStep.AfterReplace, 1)).Apply(saved.BatchId));
        File.AppendAllText(Tasks, "外部で足した行\r\n"); // the second file is edited before the app comes back
        var edited = File.ReadAllBytes(Tasks);

        var report = Assert.Single(new ApplyRecovery(_db, _store, Engine()).RecoverAll());

        Assert.Equal(RecoveryAction.RecoveryRequired, report.Action);
        Assert.Equal("recovery_required", State(saved.BatchId));
        Assert.Equal(edited, File.ReadAllBytes(Tasks));
        Assert.Contains("原因はキャッシュ", File.ReadAllText(Issues), StringComparison.Ordinal); // the partial apply is visible, not undone
        Assert.All(_store.Items(), i => Assert.Equal(1, i.Revision)); // the database is not confirmed
    }

    [Fact]
    public void OldBackups_AreRemovedAfterTheRetention_ExceptTheLatestTenAndUnfinishedBatches() // 14.4
    {
        string[] titles = ["見積書の作成", "請求書の送付", "議事録の共有", "会議室の予約", "資料の印刷", "契約書の確認", "出張の手配",
            "名刺の発注", "備品の購入", "研修の申込", "面談の日程調整", "経費の精算"];
        var batches = new List<Guid>();
        for (var i = 0; i < titles.Length; i++)
        {
            var saved = Plan(M.R(M.C($"C{i:0000}", titles[i]), ReviewChoice.Create));
            Assert.Equal(ApplyOutcome.Committed, Engine().Apply(saved.BatchId).Outcome);
            batches.Add(saved.BatchId);
        }
        var stuck = Plan(M.R(M.C("C9999", "未反映の計画"), ReviewChoice.Create)); // planned, never applied
        string Backup(Guid id) => _store.FullPath($"transactions/{id:D}/backup");
        Assert.All(batches, b => Assert.True(Directory.Exists(Backup(b))));

        var soon = new ManagementStore(_workspace, _db, new ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Empty(soon.PruneApplyBackups());

        var later = new ManagementStore(_workspace, _db, new ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(100)));
        var pruned = later.PruneApplyBackups();

        Assert.Equal(batches.Take(3).Order(), pruned.Order()); // 13 batches: the latest 10 (one of them planned) keep theirs
        Assert.All(batches.Take(3), b => Assert.False(Directory.Exists(Backup(b))));
        Assert.All(batches.Skip(3), b => Assert.True(Directory.Exists(Backup(b))));
        Assert.All(batches.Take(3), b => Assert.True(File.Exists(_store.FullPath($"transactions/{b:D}/plan.json")))); // the history stays
        Assert.Equal("planned", State(stuck.BatchId));
        Assert.Equal(titles.Length, _store.Items().Count);
        Assert.Empty(later.PruneApplyBackups()); // nothing left to remove
    }
}
