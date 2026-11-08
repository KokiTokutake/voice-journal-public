using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Core.Records;

/// <summary>Session facts the front matter needs (detail spec 10.2).</summary>
/// <param name="Applied">Candidate id → display id of the management item it was applied as (14.2-8); others are 未反映.</param>
public sealed record RecordContext(DateTimeOffset StartedAtUtc, string TimeZoneId, TranscriptDocument? Transcript, bool Approved,
    IReadOnlyDictionary<string, string>? Applied = null);

/// <summary>
/// Renders a record as Markdown (detail spec 10): UTF-8 without BOM and LF (the caller encodes), front matter,
/// title, 要約, the sections that have content, the candidates (未反映), related material. All text from the model
/// or the user is escaped so it cannot become HTML, a link, an image, emphasis or a management marker (21.2, AT-23).
/// </summary>
public static partial class RecordMarkdown
{
    /// <summary>
    /// Renders a saved or edited record of a session: the session facts from <c>manifest.json</c> and the transcript
    /// revision the record was built on (evidence links and times). A missing transcript renders without them.
    /// </summary>
    public static string RenderFor(string sessionDirectory, RecordDocument record, bool approved, IReadOnlyDictionary<string, string>? applied = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        ArgumentNullException.ThrowIfNull(record);
        var manifest = Sessions.SessionManifest.Load(sessionDirectory);
        TranscriptDocument? transcript;
        try
        {
            transcript = new TranscriptStore(sessionDirectory).Load(record.TranscriptRevision);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            transcript = null;
        }
        return Render(record, new RecordContext(manifest.StartedAtUtc, manifest.TimeZoneId, transcript, approved, applied));
    }

    public static string Render(RecordDocument record, RecordContext context)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(context);
        var md = new StringBuilder();
        var zone = FindZone(context.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(context.StartedAtUtc, zone);
        md.Append("---\n");
        md.Append("schema_version: \"1.0\"\n");
        md.Append(CultureInfo.InvariantCulture, $"session_id: \"{record.SessionId:D}\"\n");
        md.Append(CultureInfo.InvariantCulture, $"record_revision: {record.Revision}\n");
        md.Append(CultureInfo.InvariantCulture, $"transcript_revision: {record.TranscriptRevision}\n");
        md.Append("recorded_at: ").Append(Yaml(local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))).Append('\n');
        md.Append("timezone: ").Append(Yaml(context.TimeZoneId)).Append('\n');
        md.Append("types: [").Append(string.Join(", ", record.RecordTypes.Select(t => Yaml(EnumName(t))))).Append("]\n");
        md.Append("review_status: ").Append(Yaml(context.Approved ? "approved" : "draft")).Append('\n');
        md.Append("---\n");
        md.Append("# ").Append(Inline(record.Title)).Append("\n\n");

        md.Append("## 要約\n");
        foreach (var s in record.Summary) Statement(md, s, context.Transcript);
        if (record.MissingPieces.Count > 0)
        {
            md.Append("\n> 部分生成: 次の範囲は記録を作れませんでした。");
            md.Append(string.Join("、", record.MissingPieces.Select(p => $"{TranscriptRendering.Time(p.StartMs, false)}〜{TranscriptRendering.Time(p.EndMs, false)}")));
            md.Append('\n');
        }

        foreach (var section in record.Sections)
        {
            if (section.Statements.Count == 0) continue;
            md.Append("\n## ").Append(Inline(section.Heading)).Append('\n');
            foreach (var s in section.Statements) Statement(md, s, context.Transcript);
        }

        if (record.Candidates.Count > 0)
        {
            md.Append("\n## 課題・タスク候補\n");
            foreach (var c in record.Candidates) Candidate(md, c, context.Applied);
        }

        md.Append("\n## 関連資料\n");
        md.Append("- [文字起こし](transcript.md)\n");
        md.Append("- 原音はアプリのセッション画面から参照できる。\n");
        return md.ToString();
    }

    private static void Statement(StringBuilder md, RecordStatement s, TranscriptDocument? transcript)
    {
        md.Append("- ").Append(Inline(s.Text));
        if (s.Certainty == Certainty.Tentative) md.Append("（案・未確定）");
        else if (s.Certainty == Certainty.Unclear) md.Append("（不明確）");
        if (s.Origin == TextOrigin.UserWritten) md.Append("（利用者の追記）");
        md.Append('\n');
        if (s.Evidence.Count == 0) return;
        md.Append("  - 根拠: ");
        md.Append(string.Join("、", s.Evidence.DistinctBy(e => e.SegmentId).Select(e => Link(e, transcript)))); // one link per segment
        md.Append('\n');
    }

    private static string Link(Evidence e, TranscriptDocument? transcript)
    {
        var segment = transcript?.Find(e.SegmentId);
        var label = segment is null ? "区間" : $"{TranscriptRendering.Time(segment.StartMs, false)} {(segment.Source == AudioSource.Loopback ? "loopback" : "mic")}";
        return $"[{label}](transcript.md#seg-{e.SegmentId:D})";
    }

    private static void Candidate(StringBuilder md, RecordCandidate rc, IReadOnlyDictionary<string, string>? applied)
    {
        var c = rc.Candidate;
        md.Append("- ").Append(c.Kind == CandidateKind.Issue ? "［課題］" : "［タスク］").Append(Inline(c.Fields.Title));
        var parts = new List<string> { applied is not null && applied.TryGetValue(c.CandidateId, out var displayId) ? "反映済み: " + Inline(displayId) : "未反映" };
        parts.Add(c.Fields.Assignee is { } a ? "担当: " + Inline(a) : "担当未確認");
        if (c.Fields.DueDate is { } d) parts.Add("期限: " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        else if (c.DueExpression is { } e) parts.Add("期限未確定（「" + Inline(e) + "」）");
        if (c.Certainty != Certainty.Stated) parts.Add(c.Certainty == Certainty.Tentative ? "案・未確定" : "不明確");
        if (rc.Flags.Count > 0) parts.Add("要確認");
        md.Append('（').Append(string.Join("・", parts)).Append("）\n");
    }

    /// <summary>
    /// Escapes text for one Markdown line: backslash-escapes the characters that start emphasis, code, links,
    /// images, HTML, entities and strike-through, and breaks autolinks (<c>http://</c>, <c>www.</c>).
    /// </summary>
    public static string Inline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var flat = ExtractionConverter.Clean(text);
        var sb = new StringBuilder(flat.Length + 8);
        foreach (var c in flat)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '&' or '~' or '#' or '|' or '!') sb.Append('\\');
            sb.Append(c);
        }
        var s = sb.ToString();
        s = SchemeSeparator().Replace(s, @":\/\/");
        s = WwwPrefix().Replace(s, m => m.Value[..^1] + "\\.");
        return s;
    }

    private static string Yaml(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (char.IsControl(c)) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string EnumName(RecordType type) => type.ToString().ToLowerInvariant();

    private static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    [GeneratedRegex(@":\/\/|：\/\/")]
    private static partial Regex SchemeSeparator();

    [GeneratedRegex(@"(?i)www\.")]
    private static partial Regex WwwPrefix();
}
