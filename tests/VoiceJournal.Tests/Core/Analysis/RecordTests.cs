using System.Text;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Analysis;

internal static class R
{
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);

    public static (TranscriptDocument Transcript, AnalysisDocument Analysis) Analysis(string title = "保存先の決定", params string[] extraTexts)
    {
        var transcript = A.Transcript(["保存先はローカルに決めました", "来週の金曜までに田中さんが手順書を書きます", .. extraTexts]);
        var s0 = transcript.Segments[0].SegmentId;
        var s1 = transcript.Segments[1].SegmentId;
        var analysis = new AnalysisResult
        {
            SessionId = transcript.SessionId,
            TranscriptRevision = transcript.Revision,
            RecordTypes = [RecordType.Meeting, RecordType.Work],
            Title = title,
            Summary = [new Statement { Text = "保存先をローカルに決めた", Certainty = Certainty.Stated, Evidence = [new Evidence { SegmentId = s0, Quote = "ローカルに決めました" }] }],
            Sections =
            [
                new Section { SectionKind = SectionKind.Decision, Heading = "決定事項", Statements = [new Statement { Text = "保存先はローカル", Certainty = Certainty.Stated, Evidence = [new Evidence { SegmentId = s0, Quote = "ローカルに決めました" }] }] },
                new Section { SectionKind = SectionKind.NextStep, Heading = "次にやること", Statements = [new Statement { Text = "手順書を書く", Certainty = Certainty.Tentative, Evidence = [new Evidence { SegmentId = s1, Quote = "手順書を書きます" }] }] },
            ],
            Candidates =
            [
                new Candidate
                {
                    CandidateId = "C0001", Kind = CandidateKind.Task, Action = CandidateAction.Create, SuggestedTargetId = null, ParentIssueCandidateId = null, ParentIssueItemId = null,
                    Fields = new CandidateFields { Title = "手順書を書く", Description = "", Status = null, Priority = null, Assignee = "田中さん", DueDate = new DateOnly(2026, 11, 13) },
                    ChangedFields = [], Certainty = Certainty.Stated, DueExpression = "来週の金曜まで",
                    Evidence = [new Evidence { SegmentId = s1, Quote = "手順書を書きます" }], Rationale = "r",
                },
            ],
            Warnings = [],
        };
        var doc = new AnalysisDocument
        {
            Revision = 1,
            CreatedAtUtc = Now,
            Provenance = new AnalysisProvenance { ModelId = "gemma-3-4b-it", ModelSha256 = null, PromptVersion = AnalysisPrompts.Version, ConfigHash = new string('d', 64), ContextLength = 8192, OutputReserve = 4096, JobId = Guid.NewGuid() },
            Analysis = analysis,
            CandidateNotes = [new CandidateNote("C0001", [CandidateConcern.CheckAudio])],
            Dropped = [],
            FailedPieces = [],
            PieceCount = 1,
        };
        return (transcript, doc);
    }

    public static RecordContext Context(TranscriptDocument transcript, bool approved = false) =>
        new(new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero), "Tokyo Standard Time", transcript, approved);
}

public sealed class RecordMarkdownTests
{
    [Fact]
    public void Render_FollowsSpec10_Structure()
    {
        var (t, a) = R.Analysis();
        var record = RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now);

        var md = RecordMarkdown.Render(record, R.Context(t));

        Assert.StartsWith("---\nschema_version: \"1.0\"\n", md, StringComparison.Ordinal);
        Assert.Contains("recorded_at: \"2026-11-02T10:00:00+09:00\"\n", md, StringComparison.Ordinal);
        Assert.Contains("types: [\"meeting\", \"work\"]\n", md, StringComparison.Ordinal);
        Assert.Contains("review_status: \"draft\"\n", md, StringComparison.Ordinal);
        var title = md.IndexOf("# 保存先の決定\n", StringComparison.Ordinal);
        var summary = md.IndexOf("## 要約\n", StringComparison.Ordinal);
        Assert.True(title > 0 && summary > title && md.IndexOf("## 決定事項", StringComparison.Ordinal) > summary);
        Assert.Contains($"  - 根拠: [00:00:00 mic](transcript.md#seg-{t.Segments[0].SegmentId:D})\n", md, StringComparison.Ordinal);
        Assert.Contains("- 手順書を書く（案・未確定）\n", md, StringComparison.Ordinal);
        Assert.Contains("- ［タスク］手順書を書く（未反映・担当: 田中さん・期限: 2026-11-13・要確認）\n", md, StringComparison.Ordinal);
        Assert.EndsWith("## 関連資料\n- [文字起こし](transcript.md)\n- 原音はアプリのセッション画面から参照できる。\n", md, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "\\<script\\>alert(1)\\</script\\>")]
    [InlineData("[押して](javascript:alert(1))", "\\[押して\\](javascript:alert(1))")]
    [InlineData("![x](file:///C:/secret.png)", "\\!\\[x\\](file:\\/\\//C:/secret.png)")]
    [InlineData("https://example.com を開く", "https:\\/\\/example.com を開く")]
    [InlineData("www.example.com", "www\\.example.com")]
    [InlineData("<!-- vj:item TSK-000001 -->", "\\<\\!-- vj:item TSK-000001 --\\>")]
    [InlineData("**太字** と `code` と ~~消し~~", "\\*\\*太字\\*\\* と \\`code\\` と \\~\\~消し\\~\\~")]
    [InlineData("1行目\n# 見出し", "1行目 \\# 見出し")]
    public void Inline_NeutralizesMarkup(string text, string expected) => Assert.Equal(expected, RecordMarkdown.Inline(text));

    [Fact]
    public void PartialGeneration_IsShown()
    {
        var (t, a) = R.Analysis();
        var record = RecordFactory.FromAnalysis(t.SessionId, a with { FailedPieces = [new FailedPiece(2, 60_000, 120_000, "timeout")] }, 1, R.Now);
        Assert.Contains("部分生成: 次の範囲は記録を作れませんでした。00:01:00〜00:02:00", RecordMarkdown.Render(record, R.Context(t)), StringComparison.Ordinal);
    }
}

public sealed class RecordEditingTests
{
    [Fact]
    public void Edits_MakeTheNextRevision_AndRecordTheOrigin()
    {
        var (t, a) = R.Analysis();
        var r1 = RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now);
        var statement = r1.Sections[0].Statements[0];

        var r2 = RecordEditing.EditStatement(r1, 2, statement.Id, "保存先はローカルの SSD", R.Now);

        Assert.Equal((2, 1, RecordRevisionKind.Edited), (r2.Revision, r2.ParentRevision, r2.Kind));
        var edited = r2.Sections[0].Statements[0];
        Assert.Equal((statement.Id, TextOrigin.UserEdited, "保存先はローカルの SSD"), (edited.Id, edited.Origin, edited.Text));
        Assert.Equal(statement.Evidence, edited.Evidence);
        Assert.Equal("保存先はローカル", r1.Sections[0].Statements[0].Text); // the parent is unchanged
    }

    [Fact]
    public void DeletingTheLastStatement_RemovesTheSection()
    {
        var (t, a) = R.Analysis();
        var r1 = RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now);
        var r2 = RecordEditing.DeleteStatement(r1, 2, r1.Sections[0].Statements[0].Id, R.Now);
        Assert.Equal(["次にやること"], r2.Sections.Select(s => s.Heading));
    }

    [Fact]
    public void SectionsAndHeadings_CanBeChanged_ButNotToReservedOrDuplicateHeadings()
    {
        var (t, a) = R.Analysis();
        var r1 = RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now);
        var r2 = RecordEditing.AddSection(r1, 2, 0, "背景", "前回の会議の続き", R.Now);
        var r3 = RecordEditing.MoveSection(r2, 3, r2.Sections[0].Id, 5, R.Now);
        var r4 = RecordEditing.EditHeading(r3, 4, r3.Sections[0].Id, "決まったこと", R.Now);
        var r5 = RecordEditing.AddStatement(r4, 5, null, null, "追記した要約", R.Now);

        Assert.Equal(["決まったこと", "次にやること", "背景"], r4.Sections.Select(s => s.Heading));
        Assert.Equal(TextOrigin.UserEdited, r4.Sections[0].HeadingOrigin);
        Assert.Equal(TextOrigin.UserWritten, r5.Summary[^1].Origin);
        Assert.Empty(r5.Summary[^1].Evidence);
        Assert.Throws<ArgumentException>(() => RecordEditing.EditHeading(r1, 2, r1.Sections[0].Id, "要約", R.Now));
        Assert.Throws<ArgumentException>(() => RecordEditing.EditHeading(r1, 2, r1.Sections[0].Id, "次にやること", R.Now));
        Assert.Throws<ArgumentException>(() => RecordEditing.EditTitle(r1, 2, "  ", R.Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordEditing.EditTitle(r1, 1, "題名", R.Now));
    }

    [Fact]
    public void UserWrittenStatement_IsMarkedInTheMarkdown()
    {
        var (t, a) = R.Analysis();
        var r1 = RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now);
        var r2 = RecordEditing.AddStatement(r1, 2, r1.Sections[1].Id, null, "担当は田中さん", R.Now);
        Assert.Contains("- 担当は田中さん（利用者の追記）\n", RecordMarkdown.Render(r2, R.Context(t)), StringComparison.Ordinal);
    }
}

public sealed class RecordStoreTests
{
    [Fact]
    public void Revisions_AreWrittenOnce_AndEditsStartFromTheLatest()
    {
        using var dir = new TempDirectory();
        var (t, a) = R.Analysis();
        var store = new RecordStore(dir.Path);
        string Render(RecordDocument d) => RecordMarkdown.Render(d, R.Context(t));
        var r1 = RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now);

        store.Save(r1, Render);
        var r2 = RecordEditing.EditTitle(r1, 2, "新しい題名", R.Now);
        store.Save(r2, Render);

        Assert.Equal([1, 2], store.Revisions());
        Assert.Equal("新しい題名", store.LoadLatest()!.Title);
        Assert.Contains("# 新しい題名", File.ReadAllText(store.DraftMarkdownPath), StringComparison.Ordinal);
        Assert.False(File.Exists(store.ApprovedMarkdownPath)); // nothing approved yet
        Assert.Throws<InvalidOperationException>(() => store.Save(r2, Render)); // no overwrite
        Assert.Throws<InvalidOperationException>(() => store.Save(RecordEditing.EditTitle(r1, 3, "分岐", R.Now), Render)); // must start from 2
        var bytes = File.ReadAllBytes(store.DraftMarkdownPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // no BOM
        Assert.Equal(r1.Summary[0].Id, store.Load(1).Summary[0].Id);
    }

    [Fact]
    public void Approval_IsTiedToTheTranscript_AndGoesStaleWhenItChanges()
    {
        using var dir = new TempDirectory();
        var (t, a) = R.Analysis();
        var store = new RecordStore(dir.Path);
        string Render(RecordDocument d) => RecordMarkdown.Render(d, R.Context(t));
        var r1 = RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now);
        store.Save(r1, Render);
        Assert.Equal(RecordReviewState.Draft, store.Status(3).State);

        var result = store.Approve(1, 3, R.Now);

        Assert.Null(result.PreservedExternalEdit);
        Assert.Equal(File.ReadAllBytes(Path.Combine(store.RevisionDirectory(1), "record.md")), File.ReadAllBytes(store.ApprovedMarkdownPath));
        Assert.Equal(RecordReviewState.Approved, store.Status(3).State);
        // AT-26: the transcript in use changed after the approval.
        var stale = store.Status(4);
        Assert.Equal(RecordReviewState.ApprovedOutdated, stale.State);
        Assert.True(stale.TranscriptChanged);
        // A newer draft also makes the approval outdated, and the approved file stays as it was.
        store.Save(RecordEditing.EditTitle(r1, 2, "直した", R.Now), Render);
        Assert.Equal((RecordReviewState.ApprovedOutdated, true), (store.Status(3).State, store.Status(3).NewerDraft));
        Assert.DoesNotContain("直した", File.ReadAllText(store.ApprovedMarkdownPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TheApprovedFile_IsRenderedAsApproved_WhileTheRevisionKeepsItsDraftRendering()
    {
        using var dir = new TempDirectory();
        var (t, a) = R.Analysis();
        var store = new RecordStore(dir.Path);
        store.Save(RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now), d => RecordMarkdown.Render(d, R.Context(t)));

        store.Approve(1, 3, R.Now, d => RecordMarkdown.Render(d, R.Context(t) with { Approved = true }));

        Assert.Contains("review_status: \"approved\"", File.ReadAllText(store.ApprovedMarkdownPath), StringComparison.Ordinal);
        Assert.Contains("review_status: \"draft\"", File.ReadAllText(Path.Combine(store.RevisionDirectory(1), "record.md")), StringComparison.Ordinal);
        Assert.False(store.HasExternalEdit());
    }

    [Fact]
    public void ExternalEdit_IsKept_BeforeANewApproval()
    {
        using var dir = new TempDirectory();
        var (t, a) = R.Analysis();
        var store = new RecordStore(dir.Path);
        string Render(RecordDocument d) => RecordMarkdown.Render(d, R.Context(t));
        var r1 = RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now);
        store.Save(r1, Render);
        store.Approve(1, 3, R.Now);
        File.AppendAllText(store.ApprovedMarkdownPath, "\n外部のエディターで追記\n", new UTF8Encoding(false));
        Assert.True(store.HasExternalEdit());
        store.Save(RecordEditing.EditTitle(r1, 2, "直した", R.Now), Render);

        var result = store.Approve(2, 3, R.Now.AddMinutes(1));

        Assert.NotNull(result.PreservedExternalEdit);
        Assert.Contains("外部のエディターで追記", File.ReadAllText(result.PreservedExternalEdit), StringComparison.Ordinal);
        Assert.Contains("直した", File.ReadAllText(store.ApprovedMarkdownPath), StringComparison.Ordinal);
        Assert.False(store.HasExternalEdit());
    }

    [Fact]
    public void Deferred_IsShown_UntilApproved()
    {
        using var dir = new TempDirectory();
        var (t, a) = R.Analysis();
        var store = new RecordStore(dir.Path);
        store.Save(RecordFactory.FromAnalysis(t.SessionId, a, 1, R.Now), d => RecordMarkdown.Render(d, R.Context(t)));

        store.SetDeferred(true, R.Now);
        Assert.Equal(RecordReviewState.Deferred, store.Status(3).State);
        store.Approve(1, 3, R.Now);
        Assert.Equal(RecordReviewState.Approved, store.Status(3).State);
    }

    [Fact]
    public void AnalysisStore_WritesOnce_AndRoundTrips()
    {
        using var dir = new TempDirectory();
        var (_, a) = R.Analysis();
        var store = new AnalysisStore(dir.Path);

        store.Save(a);

        Assert.Equal([1], store.Revisions());
        var loaded = store.Load(1);
        Assert.Equal(a.Analysis.Candidates[0].Fields, loaded.Analysis.Candidates[0].Fields);
        Assert.Equal(CandidateConcern.CheckAudio, loaded.CandidateNotes[0].Flags[0]);
        Assert.Throws<InvalidOperationException>(() => store.Save(a));
    }
}

public sealed class ProcessingStateTests
{
    [Theory]
    [InlineData(ProcessingState.Transcribed, ProcessingState.Organizing)]
    [InlineData(ProcessingState.Organizing, ProcessingState.Validating)]
    [InlineData(ProcessingState.Organizing, ProcessingState.NotAnalyzable)]
    [InlineData(ProcessingState.Organizing, ProcessingState.Queued)] // preempted by recording
    [InlineData(ProcessingState.Validating, ProcessingState.ReadyForReview)]
    [InlineData(ProcessingState.ReadyForReview, ProcessingState.Organizing)] // regenerate
    [InlineData(ProcessingState.Failed, ProcessingState.Organizing)] // LLM-001: retry only the organizing step
    public void RecordGeneration_Transitions_AreAllowed(ProcessingState from, ProcessingState to) =>
        Assert.True(ProcessingStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(ProcessingState.Transcribing, ProcessingState.Organizing)]
    [InlineData(ProcessingState.Organizing, ProcessingState.ReadyForReview)] // always validated first
    [InlineData(ProcessingState.None, ProcessingState.Organizing)]
    public void Shortcuts_AreNotAllowed(ProcessingState from, ProcessingState to) =>
        Assert.False(ProcessingStateMachine.CanTransition(from, to));

    [Fact]
    public void Organizing_IsActive() => Assert.True(ProcessingStateMachine.IsActive(ProcessingState.Organizing));
}
