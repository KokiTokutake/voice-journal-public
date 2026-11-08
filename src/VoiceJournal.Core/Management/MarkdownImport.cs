using System.Globalization;
using System.Text.RegularExpressions;
using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Core.Management;

/// <summary>One item found in an existing management file (detail spec 11.3).</summary>
public sealed record ImportedLine
{
    /// <summary>1-based line in the file.</summary>
    public required int Line { get; init; }

    public required CandidateKind Kind { get; init; }

    public required string Title { get; init; }

    public ItemStatus? Status { get; init; }

    public string? Assignee { get; init; }

    public DateOnly? DueDate { get; init; }

    /// <summary>The id the file already gives the item (「ABC-12」「#12」), kept as 「元の ID」.</summary>
    public string? ExternalId { get; init; }

    /// <summary>Where it came from: 「チェックリスト」 or 「表」.</summary>
    public required string Source { get; init; }
}

public sealed record ImportScan
{
    public required IReadOnlyList<ImportedLine> Items { get; init; }

    public required IReadOnlyList<string> Headings { get; init; }

    /// <summary>What stops the import (duplicate ids, 11.3-4). The user resolves it in the file first.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>Lines that looked like items but could not be read (shown, not imported).</summary>
    public required IReadOnlyList<string> Skipped { get; init; }
}

/// <summary>
/// Read-only scan of an existing Markdown management file (detail spec 11.3-1): headings, check lists and tables. The
/// file is never changed; the AI is not involved. Kind comes from the nearest heading (課題・問題・issue → issue, else task).
/// Table columns are recognised by their header (タイトル・状態・担当・期限・ID); a table without a title column is skipped.
/// </summary>
public static partial class MarkdownImport
{
    public const int MaxItems = 2000;

    public static ImportScan Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var items = new List<ImportedLine>();
        var headings = new List<string>();
        var skipped = new List<string>();
        var kind = CandidateKind.Task;
        string[]? columns = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var number = i + 1;
            if (line.TrimStart().StartsWith("<!-- vj:", StringComparison.Ordinal)) continue; // the app's own blocks are not imported
            if (Heading().Match(line) is { Success: true } h)
            {
                var title = h.Groups[2].Value.Trim();
                headings.Add(title);
                kind = IssueWord().IsMatch(title) ? CandidateKind.Issue : CandidateKind.Task;
                columns = null;
                continue;
            }
            if (Check().Match(line) is { Success: true } c)
            {
                columns = null;
                var done = c.Groups[1].Value is "x" or "X";
                var body = c.Groups[2].Value.Trim();
                var (title, id, assignee, due) = Details(body);
                if (title.Length == 0) { skipped.Add($"{number} 行目: タイトルがありません"); continue; }
                items.Add(new ImportedLine
                {
                    Line = number, Kind = kind, Title = title, ExternalId = id, Assignee = assignee, DueDate = due, Source = "チェックリスト",
                    Status = done ? (kind == CandidateKind.Issue ? ItemStatus.Resolved : ItemStatus.Done) : null,
                });
                continue;
            }
            if (line.TrimStart().StartsWith('|'))
            {
                var cells = Cells(line);
                if (cells.All(x => Separator().IsMatch(x))) continue; // |---|---|
                if (columns is null)
                {
                    columns = [.. cells.Select(Role)];
                    if (!columns.Contains("title")) { skipped.Add($"{number} 行目: 表にタイトルの列がありません（取り込みません）"); columns = ["none"]; }
                    continue;
                }
                if (columns is ["none"]) continue;
                string? Cell(string role) => Array.IndexOf(columns, role) is var k and >= 0 && k < cells.Count && cells[k].Length > 0 ? cells[k] : null;
                var titleCell = Cell("title");
                if (titleCell is null) { skipped.Add($"{number} 行目: タイトルが空です"); continue; }
                var (title, inlineId, inlineAssignee, inlineDue) = Details(titleCell);
                DateOnly? due = inlineDue;
                if (Cell("due") is { } d)
                {
                    if (ParseDate(d) is { } parsed) due = parsed;
                    else if (!IsUnset(d)) skipped.Add($"{number} 行目: 期限「{d}」は日付として読めないので、期限なしで取り込みます");
                }
                var statusText = Cell("status");
                var status = statusText is null ? null : StatusOf(statusText, kind);
                if (statusText is not null && status is null && !IsUnset(statusText)) skipped.Add($"{number} 行目: 状態「{statusText}」は読めないので、状態なしで取り込みます");
                items.Add(new ImportedLine
                {
                    Line = number, Kind = kind, Title = title, Source = "表", Status = status, DueDate = due,
                    ExternalId = Cell("id") ?? inlineId,
                    Assignee = Cell("assignee") is { } a && !IsUnset(a) ? a : inlineAssignee,
                });
                continue;
            }
            columns = null;
        }

        var problems = new List<string>();
        foreach (var group in items.Where(x => x.ExternalId is not null).GroupBy(x => x.ExternalId!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"元の ID「{group.Key}」が {string.Join("・", group.Select(x => x.Line.ToString(CultureInfo.InvariantCulture)))} 行目で重複しています");
        if (items.Count > MaxItems) problems.Add($"項目が {items.Count} 件あります（1 回の取込みは {MaxItems} 件まで）");
        return new ImportScan { Items = items, Headings = headings, Problems = problems, Skipped = skipped };
    }

    /// <summary>
    /// The scanned items as reviews for the usual plan, diff and apply (they have no audio evidence: the import is their
    /// origin, 11.1 provenance 「import」). The user confirmed them in the preview, so a finished status needs no second check.
    /// </summary>
    public static IReadOnlyList<CandidateReview> Reviews(ImportScan scan, Guid importId, string fileName, string? project)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(fileName);
        return [.. scan.Items.Select((x, n) =>
        {
            var note = $"取込み: {fileName} の {x.Line} 行目（{x.Source}）" + (x.ExternalId is { } id ? $"、元の ID {id}" : string.Empty);
            var candidate = new Candidate
            {
                CandidateId = $"M{(n + 1) % 10000:0000}",
                Kind = x.Kind,
                Action = CandidateAction.Create,
                SuggestedTargetId = null,
                ParentIssueCandidateId = null,
                ParentIssueItemId = null,
                Fields = new CandidateFields
                {
                    Title = x.Title.Length > ManagementItem.MaxTitle ? x.Title[..ManagementItem.MaxTitle] : x.Title,
                    Description = note, Status = x.Status, Priority = null, Assignee = x.Assignee, DueDate = x.DueDate,
                },
                ChangedFields = [],
                Certainty = Certainty.Stated,
                DueExpression = null,
                Evidence = [],
                Rationale = "既存ファイルの取込み",
            };
            return new CandidateReview
            {
                Key = new CandidateKey(importId, 0, candidate.CandidateId),
                Candidate = candidate,
                TranscriptRevision = 1,
                Project = string.IsNullOrWhiteSpace(project) ? ManagementItem.Unclassified : project.Trim(),
                Choice = ReviewChoice.Create,
                ConfirmedIndividually = true,
                ItemEvidence = [],
                FromImport = true,
            };
        })];
    }

    private static (string Title, string? Id, string? Assignee, DateOnly? Due) Details(string body)
    {
        string? id = null, assignee = null;
        DateOnly? due = null;
        var text = body;
        if (LeadingId().Match(text) is { Success: true } m)
        {
            id = m.Groups[1].Value;
            text = text[m.Length..].Trim();
        }
        if (AssigneeNote().Match(text) is { Success: true } a)
        {
            assignee = (a.Groups[1].Success ? a.Groups[1] : a.Groups[2]).Value.Trim();
            text = text.Remove(a.Index, a.Length).Trim();
        }
        if (DateInText().Match(text) is { Success: true } d && ParseDate(d.Value) is { } parsed)
        {
            due = parsed;
            text = DueNote().Replace(text, string.Empty).Trim();
        }
        return (text.Trim(' ', '-', ':', '：'), id, assignee, due);
    }

    private static List<string> Cells(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return [.. t.Split('|').Select(c => c.Trim())];
    }

    private static string Role(string header)
    {
        var h = header.Trim().ToLowerInvariant();
        if (h is "id" or "no" or "no." or "番号" or "#") return "id";
        if (h.Contains("タイトル", StringComparison.Ordinal) || h.Contains("件名", StringComparison.Ordinal) || h.Contains("項目", StringComparison.Ordinal)
            || h.Contains("内容", StringComparison.Ordinal) || h.Contains("タスク", StringComparison.Ordinal) || h.Contains("課題", StringComparison.Ordinal)
            || h is "title" or "task" or "issue" or "summary") return "title";
        if (h.Contains("状態", StringComparison.Ordinal) || h.Contains("ステータス", StringComparison.Ordinal) || h.Contains("進捗", StringComparison.Ordinal) || h == "status") return "status";
        if (h.Contains("担当", StringComparison.Ordinal) || h is "owner" or "assignee") return "assignee";
        if (h.Contains("期限", StringComparison.Ordinal) || h.Contains("締切", StringComparison.Ordinal) || h.Contains("期日", StringComparison.Ordinal) || h is "due" or "deadline") return "due";
        return "other";
    }

    private static ItemStatus? StatusOf(string text, CandidateKind kind)
    {
        var t = text.Trim().ToLowerInvariant();
        ItemStatus? s = t switch
        {
            "完了" or "済" or "済み" or "done" or "closed" when kind == CandidateKind.Task => ItemStatus.Done,
            "完了" or "済" or "済み" or "done" or "終了" or "closed" => ItemStatus.Closed,
            "解決" or "resolved" => kind == CandidateKind.Issue ? ItemStatus.Resolved : ItemStatus.Done,
            "対応中" or "進行中" or "作業中" or "in progress" or "in_progress" or "doing" => ItemStatus.InProgress,
            "保留" or "停滞" or "blocked" => ItemStatus.Blocked,
            "中止" or "取消" or "cancelled" or "canceled" => kind == CandidateKind.Task ? ItemStatus.Cancelled : ItemStatus.Closed,
            "未着手" or "todo" or "to do" => kind == CandidateKind.Task ? ItemStatus.Todo : ItemStatus.Open,
            "未対応" or "open" or "新規" => kind == CandidateKind.Issue ? ItemStatus.Open : ItemStatus.Todo,
            _ => null,
        };
        return s is { } v && ItemStatuses.IsAllowed(kind, v) ? v : null;
    }

    private static bool IsUnset(string text) => text.Trim() is "" or "-" or "－" or "未設定" or "未定" or "なし";

    private static DateOnly? ParseDate(string text)
    {
        var m = DateInText().Match(text);
        if (!m.Success) return null;
        var y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mo = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var d = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return mo is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, mo) ? new DateOnly(y, mo, d) : null;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"課題|問題|不具合|イシュー|issue", RegexOptions.IgnoreCase)]
    private static partial Regex IssueWord();

    [GeneratedRegex(@"^\s*[-*+]\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex Check();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^([A-Za-z]{2,6}-\d{1,6}|#\d{1,6})\s*[:：]?\s*")]
    private static partial Regex LeadingId();

    [GeneratedRegex(@"[（(]\s*担当\s*[:：]\s*([^）)]+)[）)]|@([^\s（(]+)")]
    private static partial Regex AssigneeNote();

    [GeneratedRegex(@"(\d{4})[-/年](\d{1,2})[-/月](\d{1,2})日?")]
    private static partial Regex DateInText();

    [GeneratedRegex(@"[（(]?\s*(期限|締切|〆切)?\s*[:：]?\s*\d{4}[-/年]\d{1,2}[-/月]\d{1,2}日?\s*(まで)?\s*[）)]?")]
    private static partial Regex DueNote();
}
