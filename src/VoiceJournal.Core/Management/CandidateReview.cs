using System.Security.Cryptography;
using System.Text.Json;
using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Core.Management;

/// <summary>Identifies one candidate for the applied ledger (detail spec 12.2-2): session + analysis revision + candidate id.</summary>
public sealed record CandidateKey(Guid SessionId, int AnalysisRevision, string CandidateId)
{
    public override string ToString() => $"{SessionId:D}/{AnalysisRevision}/{CandidateId}";
}

/// <summary>What the user chose for a candidate (detail spec 12.2, 13.2). No choice yet means unreviewed.</summary>
public enum ReviewChoice
{
    /// <summary>A new item.</summary>
    Create,

    /// <summary>Change fields of an existing item.</summary>
    Update,

    /// <summary>Only add the evidence to an existing item (no field changes).</summary>
    Mention,

    Reject,

    /// <summary>Decide later. Stays out of the plan.</summary>
    Defer,
}

/// <summary>Why a decision must be checked one by one and never by 「すべて選択」 (12.3, 13.2).</summary>
public enum IndividualCheck
{
    /// <summary>Into done / resolved / closed / cancelled.</summary>
    Completion,

    /// <summary>Out of a terminal status.</summary>
    Reopen,

    DueDateChange,

    AssigneeChange,

    /// <summary>The named target is unknown, of the other kind, or changed since the candidate was reviewed.</summary>
    Conflict,

    /// <summary>The candidate carries a concern from the record (negation, doubt, dropped assignee…).</summary>
    Concern,

    /// <summary>A field would be cleared (an explicit null): the user picks it again, never the model (12.3).</summary>
    Clearing,
}

/// <summary>One candidate under review with the user's choice and edits.</summary>
public sealed record CandidateReview
{
    public required CandidateKey Key { get; init; }

    public required Candidate Candidate { get; init; }

    /// <summary>The transcript revision the candidate's evidence is from.</summary>
    public required int TranscriptRevision { get; init; }

    public string Project { get; init; } = ManagementItem.Unclassified;

    public IReadOnlyList<CandidateConcern> Concerns { get; init; } = [];

    public ReviewChoice? Choice { get; init; }

    /// <summary>The existing item for <see cref="ReviewChoice.Update"/> and <see cref="ReviewChoice.Mention"/>.</summary>
    public Guid? TargetItemId { get; init; }

    /// <summary>The target revision the user saw; a different current revision is a conflict (13.3).</summary>
    public int? TargetRevision { get; init; }

    /// <summary>The fields after the user's edits (the candidate's fields when not edited).</summary>
    public CandidateFields? EditedFields { get; init; }

    /// <summary>For an update: the fields to change. Fields not listed are never changed (12.3).</summary>
    public IReadOnlyList<ItemField>? FieldsToChange { get; init; }

    /// <summary>For a task: the parent issue chosen by the user (an existing item).</summary>
    public Guid? ParentIssueId { get; init; }

    /// <summary>
    /// The evidence as item evidence, for reviews the app makes from an existing item (an undo, a hand edit). When null,
    /// the evidence comes from the candidate with the session of <see cref="Key"/>.
    /// </summary>
    public IReadOnlyList<ItemEvidence>? ItemEvidence { get; init; }

    /// <summary>The user confirmed this one individually (required when <see cref="CandidateRules.Checks"/> is not empty).</summary>
    public bool ConfirmedIndividually { get; init; }

    public CandidateFields Fields => EditedFields ?? Candidate.Fields;

    public IReadOnlyList<ItemField> ChangedFields => FieldsToChange ?? Candidate.ChangedFields;
}

/// <summary>Rules of the management review (detail spec 12.3, 13.2, 13.3).</summary>
public static class CandidateRules
{
    /// <summary>The reasons the decision needs an individual check against the current item (if any).</summary>
    public static IReadOnlyList<IndividualCheck> Checks(CandidateReview review, ManagementItem? target)
    {
        ArgumentNullException.ThrowIfNull(review);
        var list = new List<IndividualCheck>();
        if (review.Concerns.Any(NeedsIndividualCheck)) list.Add(IndividualCheck.Concern);
        var f = review.Fields;
        switch (review.Choice)
        {
            case ReviewChoice.Create:
                if (f.Status is { } s && ItemStatuses.IsTerminal(s)) list.Add(IndividualCheck.Completion);
                break;
            case ReviewChoice.Update or ReviewChoice.Mention:
                if (target is null || target.Kind != review.Candidate.Kind || (review.TargetRevision is { } r && r != target.Revision))
                {
                    list.Add(IndividualCheck.Conflict);
                    break;
                }
                if (review.Choice == ReviewChoice.Mention) break;
                var changed = review.ChangedFields;
                if (changed.Contains(ItemField.Status) && f.Status is { } status && status != target.Status)
                {
                    if (ItemStatuses.IsTerminal(status)) list.Add(IndividualCheck.Completion);
                    if (ItemStatuses.IsTerminal(target.Status)) list.Add(IndividualCheck.Reopen);
                }
                if (changed.Contains(ItemField.DueDate) && f.DueDate != target.DueDate) list.Add(f.DueDate is null ? IndividualCheck.Clearing : IndividualCheck.DueDateChange);
                if (changed.Contains(ItemField.Assignee) && !string.Equals(f.Assignee, target.Assignee, StringComparison.Ordinal))
                    list.Add(f.Assignee is null ? IndividualCheck.Clearing : IndividualCheck.AssigneeChange);
                if (changed.Contains(ItemField.Priority) && f.Priority is null && target.Priority is not null) list.Add(IndividualCheck.Clearing);
                break;
        }
        return [.. list.Distinct()];
    }

    /// <summary>
    /// Concerns that could make a wrong item if approved in bulk (a doubt, a negation, a finished status, an assignee or
    /// date taken from the next segment): the user confirms them one by one. The others only show that something was
    /// left out or should be heard again (CheckAudio, DueDateNeedsCheck, AssigneeDropped…) and do not block 「すべて選択」.
    /// </summary>
    public static bool NeedsIndividualCheck(CandidateConcern concern) => concern is CandidateConcern.Doubtful or CandidateConcern.Negation
        or CandidateConcern.TerminalStatus or CandidateConcern.StatusDropped or CandidateConcern.NearbyEvidence;

    /// <summary>「すべて選択」 picks only plain new candidates (13.2): no blocking concern, no terminal status, not already decided.</summary>
    public static bool IsSelectableByAll(CandidateReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return review.Choice is null
            && review.Candidate.Action == CandidateAction.Create
            && Checks(review with { Choice = ReviewChoice.Create }, null).Count == 0;
    }

    /// <summary>Whether a choice may enter a plan: decided, and individually confirmed when it needs to be.</summary>
    public static bool IsReadyForPlan(CandidateReview review, ManagementItem? target, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(review);
        reason = null;
        if (review.Choice is not (ReviewChoice.Create or ReviewChoice.Update or ReviewChoice.Mention))
        {
            reason = "新規・更新・関連のどれも選ばれていません";
            return false;
        }
        if (review.Candidate.Evidence.Count == 0)
        {
            reason = "根拠がないので承認できません（根拠確認が必要）";
            return false;
        }
        var checks = Checks(review, target);
        if (checks.Contains(IndividualCheck.Conflict))
        {
            reason = "対象の項目が見つからないか、レビューの後に変わりました。選び直してください";
            return false;
        }
        if (checks.Count > 0 && !review.ConfirmedIndividually)
        {
            reason = "個別の確認が必要です（" + string.Join("・", checks.Select(Label)) + "）";
            return false;
        }
        if (review.Choice == ReviewChoice.Update && review.ChangedFields.Count == 0)
        {
            reason = "更新する項目がありません";
            return false;
        }
        return true;
    }

    public static string Label(IndividualCheck check) => check switch
    {
        IndividualCheck.Completion => "完了・取消",
        IndividualCheck.Reopen => "再開",
        IndividualCheck.DueDateChange => "期限の変更",
        IndividualCheck.AssigneeChange => "担当の変更",
        IndividualCheck.Conflict => "競合",
        IndividualCheck.Concern => "要確認の目印",
        IndividualCheck.Clearing => "値の消去",
        _ => check.ToString(),
    };

    /// <summary>SHA-256 of the candidate's evidence (the same bytes as the DB index), for the plan and the ledger.</summary>
    public static string EvidenceHash(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(candidate.Evidence, AnalysisJson.Compact)));
    }
}

/// <summary>The candidates of a record, as reviews not yet decided.</summary>
public static class ReviewSource
{
    /// <summary>
    /// One review per candidate of <paramref name="record"/>. The key uses the record's analysis revision (12.2-2); a record
    /// without one (made by hand) uses its own revision. The project is the session's (「未分類」 when it has none).
    /// </summary>
    public static IReadOnlyList<CandidateReview> FromRecord(Records.RecordDocument record, string? project)
    {
        ArgumentNullException.ThrowIfNull(record);
        var analysis = record.Provenance.AnalysisRevision ?? record.Revision;
        return [.. record.Candidates.Select(c => new CandidateReview
        {
            Key = new CandidateKey(record.SessionId, analysis, c.Candidate.CandidateId),
            Candidate = c.Candidate,
            TranscriptRevision = record.TranscriptRevision,
            Project = string.IsNullOrWhiteSpace(project) ? ManagementItem.Unclassified : project.Trim(),
            Concerns = c.Flags,
        })];
    }
}
