using System.Text;
using System.Text.Json.Nodes;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Management;

namespace VoiceJournal.Tests.Core.Management;

internal static class M
{
    public static readonly Guid Workspace = Guid.Parse("33333333-3333-4333-8333-333333333333");
    public static readonly Guid Session = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly DateTimeOffset Now = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);
    public static readonly Guid IssuesFile = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    public static readonly Guid TasksFile = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");

    public static Candidate C(string id, string title, CandidateKind kind = CandidateKind.Task, CandidateAction action = CandidateAction.Create,
        Guid? target = null, ItemStatus? status = null, string? assignee = null, DateOnly? due = null, IReadOnlyList<ItemField>? changed = null,
        Guid? segment = null, string description = "") => new()
        {
            CandidateId = id,
            Kind = kind,
            Action = action,
            SuggestedTargetId = target,
            ParentIssueCandidateId = null,
            ParentIssueItemId = null,
            Fields = new CandidateFields { Title = title, Description = description, Status = status, Priority = null, Assignee = assignee, DueDate = due },
            ChangedFields = changed ?? [],
            Certainty = Certainty.Stated,
            DueExpression = null,
            Evidence = [new Evidence { SegmentId = segment ?? Guid.NewGuid(), Quote = "根拠の引用です" }],
            Rationale = "発言",
        };

    public static CandidateReview R(Candidate c, ReviewChoice? choice, Guid? target = null, int? targetRevision = null, bool confirmed = false,
        IReadOnlyList<CandidateConcern>? concerns = null, int analysisRevision = 1) => new()
        {
            Key = new CandidateKey(Session, analysisRevision, c.CandidateId),
            Candidate = c,
            TranscriptRevision = 1,
            Choice = choice,
            TargetItemId = target,
            TargetRevision = targetRevision,
            ConfirmedIndividually = confirmed,
            Concerns = concerns ?? [],
        };

    public static ManagementItem Item(string displayId, string title, CandidateKind kind = CandidateKind.Task, ItemStatus? status = null,
        string? assignee = null, DateOnly? due = null, int revision = 1, Guid? id = null, string description = "") => new()
        {
            Id = id ?? Guid.NewGuid(),
            DisplayId = displayId,
            Kind = kind,
            Title = title,
            Description = description,
            Status = status ?? ItemStatuses.Initial(kind),
            Assignee = assignee,
            DueDate = due,
            Revision = revision,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };

    public static Func<Guid> Ids()
    {
        var n = 0;
        return () => Guid.Parse($"00000000-0000-4000-8000-{++n:000000000000}");
    }

    public static PlanningInput Input(IReadOnlyList<CandidateReview> reviews, IReadOnlyList<ManagementItem>? items = null,
        byte[]? tasks = null, byte[]? issues = null, ManagedFileMode mode = ManagedFileMode.ManagedBlocks,
        IReadOnlySet<CandidateKey>? applied = null, IReadOnlySet<string>? fingerprints = null) => new()
        {
            WorkspaceId = Workspace,
            Items = items ?? [],
            Reviews = reviews,
            Applied = applied ?? new HashSet<CandidateKey>(),
            AppliedFingerprints = fingerprints ?? new HashSet<string>(StringComparer.Ordinal),
            Files =
            [
                new ManagedFile { FileId = IssuesFile, RelativePath = "management/issues.md", Kind = CandidateKind.Issue, Mode = mode, CurrentBytes = issues },
                new ManagedFile { FileId = TasksFile, RelativePath = "management/tasks.md", Kind = CandidateKind.Task, Mode = mode, CurrentBytes = tasks },
            ],
            NextIssueNumber = 1 + (items ?? []).Count(i => i.Kind == CandidateKind.Issue),
            NextTaskNumber = 1 + (items ?? []).Count(i => i.Kind == CandidateKind.Task),
            NowUtc = Now,
        };

    /// <summary>A tasks.md the app made earlier, holding <paramref name="items"/>, with a free note around the blocks.</summary>
    public static byte[] TasksFileWith(IReadOnlyList<ManagementItem> items, string lineEnd = "\r\n", bool bom = false)
    {
        var all = items.ToDictionary(i => i.Id);
        var text = ManagementMarkdown.NewFile(CandidateKind.Task, Workspace, lineEnd) + lineEnd + "自由なメモ: 触らないこと" + lineEnd;
        foreach (var i in items) text += lineEnd + ManagementMarkdown.Block(i, all, lineEnd);
        text += lineEnd + "末尾のメモ" + lineEnd;
        var bytes = Encoding.UTF8.GetBytes(text);
        return bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes;
    }
}

public sealed class ManagementModelTests
{
    [Fact]
    public void DisplayIds_AreSixDigits_ByKind_AndParseBack()
    {
        Assert.Equal("ISS-000001", DisplayIds.Format(CandidateKind.Issue, 1));
        Assert.Equal("TSK-000123", DisplayIds.Format(CandidateKind.Task, 123));
        Assert.True(DisplayIds.TryParse("TSK-000123", out var kind, out var n));
        Assert.Equal((CandidateKind.Task, 123L), (kind, n));
        Assert.False(DisplayIds.TryParse("TSK-123", out _, out _));
        Assert.False(DisplayIds.TryParse("TSK-000000", out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayIds.Format(CandidateKind.Task, 0));
    }

    [Fact]
    public void Statuses_DependOnTheKind()
    {
        Assert.True(ItemStatuses.IsAllowed(CandidateKind.Issue, ItemStatus.Resolved));
        Assert.False(ItemStatuses.IsAllowed(CandidateKind.Issue, ItemStatus.Done));
        Assert.False(ItemStatuses.IsAllowed(CandidateKind.Task, ItemStatus.Open));
        Assert.True(ItemStatuses.IsTerminal(ItemStatus.Cancelled));
        Assert.False(ItemStatuses.IsTerminal(ItemStatus.Blocked));
    }

    [Fact]
    public void AnItem_IsChecked_ForItsStatus_TitleAndParent()
    {
        var issue = M.Item("ISS-000001", "課題", CandidateKind.Issue);
        var task = M.Item("TSK-000001", "タスク");
        var others = new Dictionary<Guid, ManagementItem> { [issue.Id] = issue, [task.Id] = task };
        Assert.Empty((task with { ParentIssueId = issue.Id }).Problems(others));
        Assert.NotEmpty((task with { ParentIssueId = task.Id }).Problems(others)); // itself
        Assert.NotEmpty((task with { ParentIssueId = Guid.NewGuid() }).Problems(others)); // unknown
        Assert.NotEmpty((issue with { ParentIssueId = task.Id }).Problems(others)); // only tasks have parents
        Assert.NotEmpty((task with { Status = ItemStatus.Open }).Problems());
        Assert.NotEmpty((task with { Title = " " }).Problems());
        Assert.NotEmpty((task with { DisplayId = "ISS-000001" }).Problems());
    }
}

public sealed class DuplicateFinderTests
{
    [Fact]
    public void TheSameTitle_AfterNormalization_IsAnExactMatch_AndComesFirst()
    {
        var exact = M.Item("TSK-000002", "ＡＢＣ社への  見積もり送付");
        var similar = M.Item("TSK-000001", "ABC社への見積もりの送付");
        var report = DuplicateFinder.Find(M.C("C0001", "abc社への 見積もり送付"), "未分類", [similar, exact, M.Item("TSK-000003", "議事録の共有")]);

        Assert.Equal(ExplicitTargetState.None, report.Explicit);
        Assert.Equal([exact.Id, similar.Id], report.Matches.Select(m => m.Item.Id));
        Assert.True(report.Matches[0].ExactTitle);
        Assert.True(report.Matches[1].Score >= TitleSimilarity.Check);
    }

    [Fact]
    public void OnlyItemsOfTheSameKind_AreCompared_AndAShortTitleIsAmbiguous()
    {
        var issue = M.Item("ISS-000001", "見積もり送付", CandidateKind.Issue);
        Assert.Empty(DuplicateFinder.Find(M.C("C0001", "見積もり送付"), "未分類", [issue]).Matches);
        var report = DuplicateFinder.Find(M.C("C0001", "A"), "未分類", [M.Item("TSK-000001", "A")]);
        Assert.True(report.Ambiguous);
        Assert.Empty(report.Matches);
    }

    [Fact]
    public void AnExplicitTarget_ThatIsUnknownOrOfTheOtherKind_IsAnError_NotAMatch()
    {
        var issue = M.Item("ISS-000001", "ログイン不具合", CandidateKind.Issue);
        Assert.Equal(ExplicitTargetState.NotFound, DuplicateFinder.Find(M.C("C0001", "x", target: Guid.NewGuid()), "未分類", [issue]).Explicit);
        Assert.Equal(ExplicitTargetState.WrongKind, DuplicateFinder.Find(M.C("C0001", "x", target: issue.Id), "未分類", [issue]).Explicit);
        var found = DuplicateFinder.Find(M.C("C0001", "x", CandidateKind.Issue, target: issue.Id), "未分類", [issue]);
        Assert.Equal((ExplicitTargetState.Found, issue.Id), (found.Explicit, found.ExplicitItem!.Id));
    }

    [Fact]
    public void Differences_InAssignee_DueDate_Negation_AndNumbers_AreShownApart()
    {
        var item = M.Item("TSK-000001", "サーバー3台の更新", assignee: "田中", due: new DateOnly(2026, 11, 20));
        var diffs = DuplicateFinder.Differences(M.C("C0001", "サーバー5台の更新はしない", assignee: "鈴木さん", due: new DateOnly(2026, 11, 27)), item);
        Assert.Equal([DuplicateDifference.Assignee, DuplicateDifference.DueDate, DuplicateDifference.Negation, DuplicateDifference.Numbers], diffs);
        Assert.Empty(DuplicateFinder.Differences(M.C("C0001", "サーバー三台の更新", assignee: "田中さん"), item)); // 「さん」 and kanji digits
    }
}

public sealed class CandidateRulesTests
{
    [Fact]
    public void Completion_Reopen_DueAndAssigneeChanges_NeedAnIndividualCheck()
    {
        var item = M.Item("TSK-000001", "手順書", status: ItemStatus.Done, assignee: "田中", due: new DateOnly(2026, 11, 20));
        var update = M.C("C0001", "手順書", action: CandidateAction.Update, target: item.Id, status: ItemStatus.InProgress, assignee: "鈴木",
            due: new DateOnly(2026, 11, 27), changed: [ItemField.Status, ItemField.Assignee, ItemField.DueDate]);
        var checks = CandidateRules.Checks(M.R(update, ReviewChoice.Update, item.Id, 1), item);
        Assert.Equal([IndividualCheck.Reopen, IndividualCheck.DueDateChange, IndividualCheck.AssigneeChange], checks);

        var done = M.C("C0002", "手順書", action: CandidateAction.Update, target: item.Id, status: ItemStatus.Done, changed: [ItemField.Status]);
        Assert.Empty(CandidateRules.Checks(M.R(done, ReviewChoice.Update, item.Id, 1), item)); // already done: no change
        var cleared = M.C("C0003", "手順書", action: CandidateAction.Update, target: item.Id, changed: [ItemField.DueDate]);
        Assert.Equal([IndividualCheck.Clearing], CandidateRules.Checks(M.R(cleared, ReviewChoice.Update, item.Id, 1), item));
        Assert.Equal([IndividualCheck.Conflict], CandidateRules.Checks(M.R(cleared, ReviewChoice.Update, item.Id, 2), item)); // changed since
        Assert.Equal([IndividualCheck.Completion], CandidateRules.Checks(M.R(M.C("C0004", "x", status: ItemStatus.Done), ReviewChoice.Create), null));
    }

    [Fact]
    public void SelectAll_PicksOnlyPlainNewCandidates()
    {
        Assert.True(CandidateRules.IsSelectableByAll(M.R(M.C("C0001", "新しい作業"), null)));
        Assert.False(CandidateRules.IsSelectableByAll(M.R(M.C("C0002", "新しい作業"), null, concerns: [CandidateConcern.Doubtful])));
        Assert.True(CandidateRules.IsSelectableByAll(M.R(M.C("C0006", "新しい作業"), null, concerns: [CandidateConcern.CheckAudio, CandidateConcern.DueDateNeedsCheck])));
        Assert.False(CandidateRules.IsSelectableByAll(M.R(M.C("C0007", "新しい作業"), null, concerns: [CandidateConcern.CheckAudio, CandidateConcern.NearbyEvidence])));
        Assert.False(CandidateRules.IsSelectableByAll(M.R(M.C("C0003", "完了した作業", status: ItemStatus.Done), null)));
        Assert.False(CandidateRules.IsSelectableByAll(M.R(M.C("C0004", "x", action: CandidateAction.Update), null)));
        Assert.False(CandidateRules.IsSelectableByAll(M.R(M.C("C0005", "x"), ReviewChoice.Reject)));
    }

    [Fact]
    public void ADecisionThatNeedsACheck_IsNotReady_UntilConfirmed()
    {
        var review = M.R(M.C("C0001", "x"), ReviewChoice.Create, concerns: [CandidateConcern.Negation]);
        Assert.False(CandidateRules.IsReadyForPlan(review, null, out var reason));
        Assert.Contains("個別の確認", reason, StringComparison.Ordinal);
        Assert.True(CandidateRules.IsReadyForPlan(review with { ConfirmedIndividually = true }, null, out _));
        Assert.False(CandidateRules.IsReadyForPlan(review with { Choice = ReviewChoice.Defer }, null, out _));
    }
}

public sealed class ManagedFileTests
{
    [Fact]
    public void Text_IsDecodedStrictly_AndWrittenBackInTheSameEncoding()
    {
        foreach (var bytes in new[]
        {
            Encoding.UTF8.GetBytes("a\r\nb\r\n"),
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\nb\n")],
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("課題\r\nメモ\n")],
        })
        {
            var text = ManagedText.Decode(bytes)!;
            Assert.Equal(bytes, text.Encode(text.Text));
        }
        Assert.Equal(ManagedNewline.Crlf, ManagedText.Decode(Encoding.UTF8.GetBytes("a\r\nb\r\n"))!.Newline);
        Assert.Equal(ManagedNewline.MixedPreserved, ManagedText.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("課題\r\nメモ\n")])!.Newline);
        Assert.Null(ManagedText.Decode([0x83, 0x65, 0x83, 0x58])); // Shift_JIS 「テス」: read-only
    }

    [Fact]
    public void BrokenMarkers_MakeTheFileUnsafe()
    {
        var a = Guid.NewGuid();
        string Begin(Guid id) => $"<!-- vj:item-begin id={id:D} revision=1 -->\n";
        string End(Guid id) => $"<!-- vj:item-end id={id:D} -->\n";
        Assert.NotEmpty(ManagementMarkdown.Parse(Begin(a) + Begin(Guid.NewGuid()), M.Workspace).Errors); // nested, no end
        Assert.NotEmpty(ManagementMarkdown.Parse(Begin(a) + End(a) + Begin(a) + End(a), M.Workspace).Errors); // duplicate
        Assert.NotEmpty(ManagementMarkdown.Parse($"<!-- vj:workspace {Guid.NewGuid():D} -->\n", M.Workspace).Errors); // other workspace
        Assert.NotEmpty(ManagementMarkdown.Parse(End(a), M.Workspace).Errors);
        var ok = ManagementMarkdown.Parse($"<!-- vj:workspace {M.Workspace:D} -->\n" + Begin(a) + "x\n" + End(a), M.Workspace);
        Assert.Empty(ok.Errors);
        Assert.Equal(a, ok.Blocks.Single().ItemId);
    }

    [Fact]
    public void AnItemsText_CannotFakeAMarker()
    {
        var item = M.Item("TSK-000001", $"<!-- vj:item-end id={Guid.Empty:D} --> [link](javascript:x)", description: "<!-- vj:workspace x -->");
        var block = ManagementMarkdown.Block(item, new Dictionary<Guid, ManagementItem>(), "\n");
        var layout = ManagementMarkdown.Parse(block, M.Workspace);
        Assert.Empty(layout.Errors);
        Assert.Equal(item.Id, layout.Blocks.Single().ItemId);
        Assert.Contains(@"\[link\](javascript", block, StringComparison.Ordinal); // escaped: text, not a link
    }
}

public sealed class ChangePlannerTests
{
    [Fact]
    public void OnlyTheApprovedCandidates_EnterThePlan_AndTheFileIsCreated() // AT-11
    {
        var reviews = new[]
        {
            M.R(M.C("C0001", "見積書の作成", assignee: "田村"), ReviewChoice.Create),
            M.R(M.C("C0002", "議事録の共有"), ReviewChoice.Defer),
            M.R(M.C("C0003", "会議室の予約"), ReviewChoice.Reject),
            M.R(M.C("C0004", "資料の印刷"), null),
            M.R(M.C("C0005", "ログイン不具合", CandidateKind.Issue), ReviewChoice.Create),
        };
        var plan = ChangePlanner.Plan(M.Input(reviews), M.Ids());

        Assert.Equal(["C0001", "C0005"], plan.Changes.Select(c => c.Key.CandidateId));
        Assert.Equal(["C0002", "C0003", "C0004"], plan.Skipped.Select(s => s.Key.CandidateId));
        Assert.Equal(["TSK-000001", "ISS-000001"], plan.Changes.Select(c => c.After.DisplayId));
        Assert.Equal((2L, 2L), (plan.NextIssueNumber, plan.NextTaskNumber));
        Assert.All(plan.Files, f => Assert.Equal("create", f.Operation));
        var tasks = plan.Files.Single(f => f.FileId == M.TasksFile);
        Assert.Contains("TSK-000001 見積書の作成", tasks.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain("議事録", tasks.AfterText, StringComparison.Ordinal);
        Assert.Equal(ChangePlanner.Sha256(tasks.AfterBytes), tasks.AfterHash);
        Assert.Empty(ManagementMarkdown.Parse(tasks.AfterText, M.Workspace).Errors);
    }

    [Fact]
    public void ThePlanJson_FollowsSpec17_AndItsHashCoversEverythingButTheHash()
    {
        var plan = ChangePlanner.Plan(M.Input([M.R(M.C("C0001", "見積書の作成"), ReviewChoice.Create)]), M.Ids());
        var json = plan.Json!;
        Assert.Equal("1.0", (string?)json["schema_version"]);
        Assert.Equal(0, (int)json["changes"]![0]!["expected_revision"]!);
        Assert.Null(json["changes"]![0]!["before_snapshot_id"]);
        Assert.Null(json["files"]![0]!["before_hash"]);
        var copy = JsonNode.Parse(json.ToJsonString())!.AsObject();
        copy.Remove("plan_hash");
        Assert.Equal(plan.PlanHash, CanonicalJson.Sha256(copy));
        Assert.Matches("^[a-f0-9]{64}$", plan.PlanHash!);
    }

    [Fact]
    public void AppliedOrRepeatedCandidates_AreNotPlannedTwice_ButTheSameNameWithOtherEvidenceIsNew() // AT-12
    {
        var first = M.C("C0001", "見積書の作成");
        var applied = new HashSet<CandidateKey> { new(M.Session, 1, "C0001") };
        var regenerated = first with { CandidateId = "C0007" }; // the same audio processed again: a new candidate id
        var fingerprints = new HashSet<string>(StringComparer.Ordinal) { ChangePlanner.Fingerprint(CandidateKind.Task, CandidateRules.EvidenceHash(first), first.Fields.Title) };
        var otherCase = M.C("C0008", "見積書の作成"); // same name, other evidence: the user may make it new

        var plan = ChangePlanner.Plan(M.Input(
            [M.R(first, ReviewChoice.Create), M.R(regenerated, ReviewChoice.Create, analysisRevision: 2), M.R(otherCase, ReviewChoice.Create), M.R(otherCase, ReviewChoice.Create)],
            applied: applied, fingerprints: fingerprints), M.Ids());

        Assert.Equal(["C0008"], plan.Changes.Select(c => c.Key.CandidateId));
        Assert.Equal(3, plan.Skipped.Count);
    }

    [Fact]
    public void TheSameApproval_GivesTheSameIdempotencyKey() // AT-12: the button pressed twice
    {
        var reviews = new[] { M.R(M.C("C0001", "見積書の作成", segment: Guid.Parse("22222222-2222-4222-8222-222222222222")), ReviewChoice.Create) };
        var a = ChangePlanner.Plan(M.Input(reviews), M.Ids());
        var b = ChangePlanner.Plan(M.Input(reviews), () => Guid.NewGuid());
        Assert.Equal(a.IdempotencyKey, b.IdempotencyKey);
        Assert.NotEqual(a.IdempotencyKey, ChangePlanner.Plan(M.Input([reviews[0] with { Choice = ReviewChoice.Create, Candidate = reviews[0].Candidate with { Fields = reviews[0].Candidate.Fields with { Title = "別" } } }]), M.Ids()).IdempotencyKey);
    }

    [Fact]
    public void AnUpdate_ReplacesOnlyItsBlock_AndKeepsEveryOtherByte()
    {
        var a = M.Item("TSK-000001", "見積書の作成", assignee: "田村");
        var b = M.Item("TSK-000002", "議事録の共有");
        var before = M.TasksFileWith([a, b], "\r\n", bom: true);
        var update = M.C("C0001", "見積書の作成", action: CandidateAction.Update, target: a.Id, description: "金額を直す", changed: [ItemField.Description]);

        var plan = ChangePlanner.Plan(M.Input([M.R(update, ReviewChoice.Update, a.Id, 1)], [a, b], tasks: before), M.Ids());

        var change = plan.Changes.Single();
        Assert.Equal((1, 2, "金額を直す", "田村"), (change.ExpectedRevision, change.After.Revision, change.After.Description, change.After.Assignee));
        Assert.Single(change.After.Evidence); // the candidate's evidence is added
        var file = plan.Files.Single();
        Assert.Equal(("replace", ManagedEncoding.Utf8Bom, ManagedNewline.Crlf), (file.Operation, file.Encoding, file.Newline));
        Assert.Equal(ChangePlanner.Sha256(before), file.BeforeHash);
        var oldText = ManagedText.Decode(before)!.Text;
        var oldLayout = ManagementMarkdown.Parse(oldText, M.Workspace);
        var newLayout = ManagementMarkdown.Parse(file.AfterText, M.Workspace);
        var oldA = oldLayout.Blocks.Single(x => x.ItemId == a.Id);
        var newA = newLayout.Blocks.Single(x => x.ItemId == a.Id);
        Assert.Equal(oldText[..oldA.Start], file.AfterText[..newA.Start]); // everything before the block
        Assert.Equal(oldText[(oldA.Start + oldA.Length)..], file.AfterText[(newA.Start + newA.Length)..]); // and after it
        Assert.Contains("revision=2", file.AfterText[newA.Start..(newA.Start + newA.Length)], StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF], file.AfterBytes[..3]);
    }

    [Fact]
    public void InAnAppendOnlyFile_AnUpdateIsAProposal_AndNewItemsAreAppended() // AT-14
    {
        var a = M.Item("TSK-000001", "見積書の作成");
        var before = Encoding.UTF8.GetBytes("# 私のタスク\n\n- [ ] 見積書の作成（田村）\n- [x] 古い作業\n");
        var update = M.C("C0001", "見積書の作成", action: CandidateAction.Update, target: a.Id, description: "金額を直す", changed: [ItemField.Description]);

        var plan = ChangePlanner.Plan(M.Input([M.R(update, ReviewChoice.Update, a.Id, 1), M.R(M.C("C0002", "請求書の送付"), ReviewChoice.Create)],
            [a], tasks: before, mode: ManagedFileMode.AppendOnly), M.Ids());

        Assert.Equal("C0001", plan.Proposals.Single().Key.CandidateId);
        Assert.Equal("C0002", plan.Changes.Single().Key.CandidateId);
        var file = plan.Files.Single();
        Assert.StartsWith(Encoding.UTF8.GetString(before), file.AfterText, StringComparison.Ordinal); // the free text is kept byte for byte
        Assert.Contains("TSK-000002 請求書の送付", file.AfterText, StringComparison.Ordinal);
        Assert.Contains($"<!-- vj:workspace {M.Workspace:D} -->", file.AfterText, StringComparison.Ordinal);
        Assert.Equal("\n", ManagedText.Decode(file.AfterBytes)!.LineEnd);
    }

    [Fact]
    public void AnUnconfirmedCompletion_OrABrokenFile_KeepsTheCandidateOutOfThePlan()
    {
        var a = M.Item("TSK-000001", "見積書の作成");
        var done = M.C("C0001", "見積書の作成", action: CandidateAction.Update, target: a.Id, status: ItemStatus.Done, changed: [ItemField.Status]);
        var plan = ChangePlanner.Plan(M.Input([M.R(done, ReviewChoice.Update, a.Id, 1)], [a], tasks: M.TasksFileWith([a])), M.Ids());
        Assert.Empty(plan.Changes);
        Assert.Contains("完了・取消", plan.Skipped.Single().Reason, StringComparison.Ordinal);

        var confirmed = ChangePlanner.Plan(M.Input([M.R(done, ReviewChoice.Update, a.Id, 1, confirmed: true)], [a], tasks: M.TasksFileWith([a])), M.Ids());
        Assert.Equal(ItemStatus.Done, confirmed.Changes.Single().After.Status);

        var broken = Encoding.UTF8.GetBytes($"<!-- vj:item-begin id={a.Id:D} revision=1 -->\n");
        var plan2 = ChangePlanner.Plan(M.Input([M.R(M.C("C0002", "請求書の送付"), ReviewChoice.Create)], [a], tasks: broken), M.Ids());
        Assert.Empty(plan2.Changes);
        Assert.Contains("形式に問題", plan2.Skipped.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATaskCanHangUnderAnIssueCreatedInTheSameBatch()
    {
        var issue = M.C("C0001", "ログイン不具合", CandidateKind.Issue);
        var task = M.C("C0002", "ログの調査") with { ParentIssueCandidateId = "C0001" };
        var plan = ChangePlanner.Plan(M.Input([M.R(issue, ReviewChoice.Create), M.R(task, ReviewChoice.Create)]), M.Ids());
        var created = plan.Changes.Select(c => c.After).ToList();
        Assert.Equal(created[0].Id, created[1].ParentIssueId);
        Assert.Contains("関連課題: ISS-000001", plan.Files.Single(f => f.FileId == M.TasksFile).AfterText, StringComparison.Ordinal);
    }
}

public sealed class CanonicalJsonTests
{
    [Fact]
    public void KeysAreSorted_NoWhitespace_AndOnlyQuotesBackslashesAndControlsAreEscaped()
    {
        var node = new JsonObject { ["b"] = 1, ["a"] = new JsonArray(true, null, "課題\"\\\n\u0001") };
        Assert.Equal("{\"a\":[true,null,\"課題\\\"\\\\\\n\\u0001\"],\"b\":1}", CanonicalJson.Serialize(node));
        Assert.Throws<InvalidOperationException>(() => CanonicalJson.Serialize(new JsonObject { ["x"] = 1.5 }));
        Assert.Equal(CanonicalJson.Serialize(JsonNode.Parse("{\"b\":1,\"a\":[true,null,\"x\"]}")), CanonicalJson.Serialize(new JsonObject { ["a"] = new JsonArray(true, null, "x"), ["b"] = 1 }));
    }
}
