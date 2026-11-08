using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Management;

namespace VoiceJournal.Tests.Core.Management;

public sealed class MarkdownImportTests
{
    private const string Sample = """
        # 私の管理メモ

        自由に書いたメモはそのまま。

        ## タスク
        - [ ] 見積書の作成（担当: 田村）2026-11-20まで
        - [x] 古い作業
        * [ ] @鈴木 議事録の共有

        ## 課題
        - [ ] ABC-12: ログイン不具合
        - [ ] #7 印刷が遅い

        ### 進行中の作業
        | ID | 件名 | 状態 | 担当 | 期限 |
        |----|------|------|------|------|
        | T-1 | 請求書の送付 | 対応中 | 佐藤 | 2026/12/01 |
        | T-2 | 契約の更新 | 完了 | - | 未定 |
        | T-3 |  | 未着手 | | |

        | 日付 | メモ |
        |---|---|
        | 11/1 | 雑談 |
        """;

    [Fact]
    public void CheckListsAndTables_AreRead_WithKindFromTheHeading()
    {
        var scan = MarkdownImport.Scan(Sample);

        Assert.Empty(scan.Problems);
        var items = scan.Items;
        Assert.Equal(["見積書の作成", "古い作業", "議事録の共有", "ログイン不具合", "印刷が遅い", "請求書の送付", "契約の更新"], items.Select(i => i.Title));
        var first = items[0];
        Assert.Equal((CandidateKind.Task, "田村", new DateOnly(2026, 11, 20), (ItemStatus?)null), (first.Kind, first.Assignee, first.DueDate, first.Status));
        Assert.Equal(ItemStatus.Done, items[1].Status);
        Assert.Equal("鈴木", items[2].Assignee);
        Assert.Equal((CandidateKind.Issue, "ABC-12"), (items[3].Kind, items[3].ExternalId));
        Assert.Equal("#7", items[4].ExternalId);
        Assert.Equal((CandidateKind.Task, "T-1", ItemStatus.InProgress, "佐藤", new DateOnly(2026, 12, 1)), (items[5].Kind, items[5].ExternalId, items[5].Status, items[5].Assignee, items[5].DueDate));
        Assert.Equal((ItemStatus.Done, (string?)null, (DateOnly?)null), (items[6].Status, items[6].Assignee, items[6].DueDate));
        Assert.Contains(scan.Skipped, s => s.Contains("タイトルが空", StringComparison.Ordinal)); // T-3
        Assert.Contains(scan.Skipped, s => s.Contains("タイトルの列がありません", StringComparison.Ordinal)); // the memo table
        Assert.Equal(["私の管理メモ", "タスク", "課題", "進行中の作業"], scan.Headings);
    }

    [Fact]
    public void DuplicateIds_StopTheImport_AndTheAppsOwnBlocksAreNotImported() // 11.3-4
    {
        var scan = MarkdownImport.Scan("- [ ] AB-1 一つ目\n- [ ] AB-1 二つ目\n<!-- vj:item-begin id=x revision=1 -->\n- [ ] 中身\n");
        Assert.Contains("AB-1", Assert.Single(scan.Problems), StringComparison.Ordinal);
        Assert.Equal(3, scan.Items.Count); // the check list line inside the block is still a line; only the marker is skipped
    }

    [Fact]
    public void ImportedItems_GoThroughThePlan_WithoutAudioEvidence_AndKeepWhereTheyCameFrom()
    {
        var scan = MarkdownImport.Scan(Sample);
        var reviews = MarkdownImport.Reviews(scan, Guid.NewGuid(), "memo.md", "基幹");
        Assert.All(reviews, r => Assert.True(CandidateRules.IsReadyForPlan(r, null, out _)));

        var plan = ChangePlanner.Plan(M.Input(reviews), M.Ids());

        Assert.Equal(7, plan.Changes.Count);
        Assert.Empty(plan.Skipped);
        var done = plan.Changes.Single(c => c.After.Title == "古い作業").After;
        Assert.Equal((ItemStatus.Done, "基幹"), (done.Status, done.Project));
        Assert.Contains("memo.md の 7 行目", done.Description, StringComparison.Ordinal);
        Assert.Contains("元の ID ABC-12", plan.Changes.Single(c => c.After.Title == "ログイン不具合").After.Description, StringComparison.Ordinal);
        Assert.All(plan.Changes, c => Assert.Empty(c.After.Evidence));
        Assert.Equal(2, plan.Files.Count); // issues.md and tasks.md
    }
}
