using System.Globalization;
using System.Text.RegularExpressions;
using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Core.Management;

/// <summary>Statuses allowed per kind (detail spec 11.1) and which of them end an item.</summary>
public static class ItemStatuses
{
    private static readonly ItemStatus[] IssueStatuses = [ItemStatus.Open, ItemStatus.InProgress, ItemStatus.Blocked, ItemStatus.Resolved, ItemStatus.Closed];
    private static readonly ItemStatus[] TaskStatuses = [ItemStatus.Todo, ItemStatus.InProgress, ItemStatus.Blocked, ItemStatus.Done, ItemStatus.Cancelled];

    public static IReadOnlyList<ItemStatus> For(CandidateKind kind) => kind == CandidateKind.Issue ? IssueStatuses : TaskStatuses;

    public static bool IsAllowed(CandidateKind kind, ItemStatus status) => For(kind).Contains(status);

    /// <summary>The status a new item starts with when the candidate gives none.</summary>
    public static ItemStatus Initial(CandidateKind kind) => kind == CandidateKind.Issue ? ItemStatus.Open : ItemStatus.Todo;

    /// <summary>done / resolved / closed / cancelled: moving into or out of them needs an individual check (12.3).</summary>
    public static bool IsTerminal(ItemStatus status) => status is ItemStatus.Resolved or ItemStatus.Closed or ItemStatus.Done or ItemStatus.Cancelled;

    public static string Label(ItemStatus status) => status switch
    {
        ItemStatus.Open => "open",
        ItemStatus.Todo => "todo",
        ItemStatus.InProgress => "in_progress",
        ItemStatus.Blocked => "blocked",
        ItemStatus.Resolved => "resolved",
        ItemStatus.Closed => "closed",
        ItemStatus.Done => "done",
        ItemStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

/// <summary>Display ids 「ISS-000001」 / 「TSK-000001」: unique in the workspace and never reused (detail spec 11.1).</summary>
public static partial class DisplayIds
{
    public static string Format(CandidateKind kind, long number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 999_999);
        return Prefix(kind) + number.ToString("000000", CultureInfo.InvariantCulture);
    }

    public static string Prefix(CandidateKind kind) => kind == CandidateKind.Issue ? "ISS-" : "TSK-";

    public static bool TryParse(string? text, out CandidateKind kind, out long number)
    {
        kind = default;
        number = 0;
        var m = Pattern().Match(text ?? string.Empty);
        if (!m.Success) return false;
        kind = m.Groups[1].Value == "ISS" ? CandidateKind.Issue : CandidateKind.Task;
        number = long.Parse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        return number >= 1;
    }

    [GeneratedRegex("^(ISS|TSK)-([0-9]{6})$")]
    private static partial Regex Pattern();
}

/// <summary>Where an item was said: the session, the transcript revision, the segment and the quote (11.1).</summary>
public sealed record ItemEvidence
{
    public required Guid SessionId { get; init; }

    public required int TranscriptRevision { get; init; }

    public required Guid SegmentId { get; init; }

    public required string Quote { get; init; }
}

/// <summary>An issue or a task of the workspace (detail spec 11.1). The confirmed revision in SQLite is its source of truth (11.2).</summary>
public sealed record ManagementItem
{
    public const int MaxTitle = 200;
    public const int MaxDescription = 8000;
    public const string Unclassified = "未分類";

    public required Guid Id { get; init; }

    public required string DisplayId { get; init; }

    public required CandidateKind Kind { get; init; }

    public string Project { get; init; } = Unclassified;

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public required ItemStatus Status { get; init; }

    public ItemPriority? Priority { get; init; }

    public string? Assignee { get; init; }

    public DateOnly? DueDate { get; init; }

    public Guid? ParentIssueId { get; init; }

    public required int Revision { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public IReadOnlyList<ItemEvidence> Evidence { get; init; } = [];

    /// <summary>The problems that make the item invalid (empty when it is valid).</summary>
    public IReadOnlyList<string> Problems(IReadOnlyDictionary<Guid, ManagementItem>? others = null)
    {
        var list = new List<string>();
        if (Title.Trim().Length is 0 or > MaxTitle) list.Add($"タイトルは 1〜{MaxTitle} 文字です");
        if (Description.Length > MaxDescription) list.Add($"内容は {MaxDescription} 文字までです");
        if (!ItemStatuses.IsAllowed(Kind, Status)) list.Add($"{(Kind == CandidateKind.Issue ? "課題" : "タスク")}に状態 {ItemStatuses.Label(Status)} は使えません");
        if (Revision < 1) list.Add("Revision は 1 以上です");
        if (!DisplayIds.TryParse(DisplayId, out var kind, out _) || kind != Kind) list.Add("表示 ID の形式が種別と合いません");
        if (ParentIssueId is { } parent)
        {
            if (Kind != CandidateKind.Task) list.Add("親課題を持てるのはタスクだけです");
            else if (parent == Id) list.Add("自分自身を親課題にできません");
            else if (others is not null && (!others.TryGetValue(parent, out var p) || p.Kind != CandidateKind.Issue)) list.Add("親課題が存在しないか、課題ではありません");
        }
        return list;
    }
}
