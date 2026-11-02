using System.Text.RegularExpressions;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Core.Analysis;

/// <param name="Path">JSON-pointer-like location, e.g. <c>/candidates/2/evidence/0</c>.</param>
public sealed record AnalysisIssue(string Code, string Path, string Message);

/// <summary>
/// Checks beyond the JSON Schema (detail spec 16.2) on a final analysis against the transcript revision it claims
/// to analyse. Any issue rejects the analysis (VAL-001 / LLM-003): nothing is shown as a valid record and nothing
/// may reach the management files. Suspicious text (URIs, reserved markers) is not an issue but a warning, because
/// people may say such things; the renderer neutralizes it.
/// </summary>
public static partial class AnalysisValidator
{
    public const int MaxSummaryCharacters = 600;

    public static IReadOnlyList<AnalysisIssue> Validate(AnalysisResult analysis, TranscriptDocument transcript)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(transcript);
        var issues = new List<AnalysisIssue>();
        void Add(string code, string path, string message) => issues.Add(new AnalysisIssue(code, path, message));

        if (analysis.SchemaVersion != AnalysisResult.CurrentSchemaVersion) Add("schema_version", "/schema_version", "未対応のスキーマの版です");
        if (analysis.SessionId != transcript.SessionId) Add("session_mismatch", "/session_id", "セッション ID が入力と違います");
        if (analysis.TranscriptRevision != transcript.Revision) Add("revision_mismatch", "/transcript_revision", "文字起こしの Revision が入力と違います");

        if (analysis.RecordTypes.Count is < 1 or > 4 || analysis.RecordTypes.Distinct().Count() != analysis.RecordTypes.Count)
            Add("record_types", "/record_types", "記録の種別は重複なしで 1〜4 個です");
        Text(analysis.Title, 200, "/title", required: true);
        if (analysis.Summary.Count is < 1 or > 7) Add("summary_count", "/summary", "要約は 1〜7 項目です");
        if (analysis.Sections.Count > 30) Add("section_count", "/sections", "章が多すぎます");
        if (analysis.Candidates.Count > 200) Add("candidate_count", "/candidates", "候補が多すぎます");
        if (analysis.Warnings.Count > 100) Add("warning_count", "/warnings", "警告が多すぎます");
        for (var i = 0; i < analysis.Warnings.Count; i++) Text(analysis.Warnings[i], 1000, $"/warnings/{i}", required: true);

        for (var i = 0; i < analysis.Summary.Count; i++) StatementChecks(analysis.Summary[i], $"/summary/{i}");
        var headings = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < analysis.Sections.Count; i++)
        {
            var section = analysis.Sections[i];
            var path = $"/sections/{i}";
            Text(section.Heading, 100, path + "/heading", required: true);
            if (ExtractionConverter.ReservedHeadings.Contains(section.Heading.Trim())) Add("reserved_heading", path + "/heading", "予約された見出しです");
            if (!headings.Add(section.Heading.Trim())) Add("duplicate_heading", path + "/heading", "同じ見出しが重複しています");
            if (section.Statements.Count is < 1 or > 100) Add("statement_count", path + "/statements", "章の文は 1〜100 個です");
            for (var j = 0; j < section.Statements.Count; j++) StatementChecks(section.Statements[j], $"{path}/statements/{j}");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < analysis.Candidates.Count; i++) CandidateChecks(analysis.Candidates[i], $"/candidates/{i}");
        foreach (var c in analysis.Candidates)
            if (c.ParentIssueCandidateId is { } parent && analysis.Candidates.FirstOrDefault(x => x.CandidateId == parent) is var p && (p is null || p.Kind != CandidateKind.Issue || p == c))
                Add("parent_candidate", $"/candidates/{analysis.Candidates.ToList().IndexOf(c)}/parent_issue_candidate_id", "親の候補が課題の候補として存在しません");

        var summaryLength = analysis.Summary.Sum(s => s.Text.Length);
        if (summaryLength > MaxSummaryCharacters * 2) Add("summary_length", "/summary", $"要約が長すぎます（{summaryLength} 字）");
        return issues;

        void StatementChecks(Statement s, string path)
        {
            Text(s.Text, 4000, path + "/text", required: true);
            EvidenceChecks(s.Evidence, path + "/evidence");
        }

        void EvidenceChecks(IReadOnlyList<Evidence> evidence, string path)
        {
            if (evidence.Count is < 1 or > 30) Add("evidence_count", path, "根拠は 1〜30 個です");
            for (var k = 0; k < evidence.Count; k++)
            {
                var e = evidence[k];
                var segment = transcript.Find(e.SegmentId);
                if (segment is null)
                    Add("unknown_segment", $"{path}/{k}/segment_id", "根拠の区間がこの Revision にありません");
                else if (!EvidenceText.IsQuoteOf(e.Quote, segment.Text))
                    Add("quote_mismatch", $"{path}/{k}/quote", "引用が区間の本文にありません");
                Text(e.Quote, 2000, $"{path}/{k}/quote", required: true);
            }
        }

        void CandidateChecks(Candidate c, string path)
        {
            if (!CandidateIdPattern().IsMatch(c.CandidateId)) Add("candidate_id", path + "/candidate_id", "候補 ID の形式が違います");
            else if (!ids.Add(c.CandidateId)) Add("duplicate_candidate", path + "/candidate_id", "候補 ID が重複しています");
            switch (c.Action)
            {
                case CandidateAction.Create:
                    if (c.SuggestedTargetId is not null || c.ChangedFields.Count > 0) Add("create_target", path, "新規の候補は対象も変更項目も持ちません");
                    break;
                case CandidateAction.Update:
                    if (c.ChangedFields.Count == 0) Add("update_fields", path + "/changed_fields", "更新の候補には変更項目が要ります");
                    // Phase D hands the model no existing items, so no target can be valid (16.2-5).
                    Add("unknown_target", path + "/suggested_target_id", "更新・言及の対象がアプリの渡した候補にありません");
                    break;
                case CandidateAction.Mention:
                    if (c.ChangedFields.Count > 0) Add("mention_fields", path + "/changed_fields", "言及の候補は項目を変えません");
                    Add("unknown_target", path + "/suggested_target_id", "更新・言及の対象がアプリの渡した候補にありません");
                    break;
            }
            if (c.ChangedFields.Distinct().Count() != c.ChangedFields.Count) Add("changed_fields", path + "/changed_fields", "変更項目が重複しています");
            if (c.ParentIssueCandidateId is not null && c.ParentIssueItemId is not null) Add("parent_both", path, "親の指定は 1 つだけです");
            if (c.Kind == CandidateKind.Issue && (c.ParentIssueCandidateId is not null || c.ParentIssueItemId is not null)) Add("issue_parent", path, "課題には親を指定できません");
            if (c.ParentIssueCandidateId is { } pc && !CandidateIdPattern().IsMatch(pc)) Add("parent_candidate", path + "/parent_issue_candidate_id", "親の候補 ID の形式が違います");
            if (c.ParentIssueItemId is not null) Add("unknown_parent", path + "/parent_issue_item_id", "親の課題がアプリの渡した候補にありません");
            if (c.Fields.Status is { } status && !StatusAllowed(c.Kind, status)) Add("status_kind", path + "/fields/status", "種別に合わない状態です");
            Text(c.Fields.Title, 200, path + "/fields/title", required: true);
            Text(c.Fields.Description, 8000, path + "/fields/description", required: false);
            if (c.Fields.Assignee is not null) Text(c.Fields.Assignee, 200, path + "/fields/assignee", required: true);
            if (c.DueExpression is not null) Text(c.DueExpression, 200, path + "/due_expression", required: true);
            Text(c.Rationale, 1000, path + "/rationale", required: true);
            EvidenceChecks(c.Evidence, path + "/evidence");
        }

        void Text(string? value, int max, string path, bool required)
        {
            if (value is null)
            {
                Add("missing_text", path, "値がありません");
                return;
            }
            if (required && string.IsNullOrWhiteSpace(value)) Add("blank_text", path, "空白だけの文字列です");
            if (value.Length > max) Add("text_length", path, $"{max} 文字を超えています");
            if (value.Any(ch => char.IsControl(ch) && ch is not '\n' and not '\t')) Add("control_character", path, "制御文字を含みます");
        }
    }

    /// <summary>Issue and task status sets (detail spec 11.1): issues open → resolved/closed, tasks todo → done.</summary>
    public static bool StatusAllowed(CandidateKind kind, ItemStatus status) => kind switch
    {
        CandidateKind.Issue => status is ItemStatus.Open or ItemStatus.InProgress or ItemStatus.Blocked or ItemStatus.Resolved or ItemStatus.Closed,
        _ => status is ItemStatus.Todo or ItemStatus.InProgress or ItemStatus.Blocked or ItemStatus.Done or ItemStatus.Cancelled,
    };

    /// <summary>Warnings for text a person may have said that the renderer must neutralize (AT-23).</summary>
    public static IReadOnlyList<string> SuspiciousText(AnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var texts = analysis.Summary.Select(s => s.Text)
            .Concat(analysis.Sections.SelectMany(s => s.Statements.Select(x => x.Text).Prepend(s.Heading)))
            .Concat(analysis.Candidates.SelectMany(c => new[] { c.Fields.Title, c.Fields.Description, c.Rationale }))
            .Append(analysis.Title);
        var warnings = new List<string>();
        foreach (var t in texts)
        {
            if (UriPattern().IsMatch(t)) warnings.Add("URI のような文字列を含む文があります。リンクにはせず、文字として表示します。");
            if (t.Contains("<!--", StringComparison.Ordinal) || t.Contains("-->", StringComparison.Ordinal)) warnings.Add("管理ブロックの区切りに似た文字列を含む文があります。無害化して表示します。");
        }
        return [.. warnings.Distinct()];
    }

    [GeneratedRegex(@"^C[0-9]{4}$")]
    private static partial Regex CandidateIdPattern();

    [GeneratedRegex(@"(?i)\b(?:https?|file|javascript|vbscript|data|ms-[a-z\-]+|mailto|ftp)\s*:|www\.")]
    private static partial Regex UriPattern();
}
