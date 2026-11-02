using System.Text.Json.Serialization;
using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Core.Records;

/// <summary>Who wrote a statement or heading (detail spec 13.1: keep the origin of AI text and human edits).</summary>
public enum TextOrigin
{
    Ai,
    UserEdited,
    UserWritten,
}

public enum RecordRevisionKind
{
    /// <summary>Built from an analysis.</summary>
    Generated,

    /// <summary>User changes on top of the parent revision.</summary>
    Edited,

    /// <summary>One section replaced by a new generation from its evidence.</summary>
    SectionRegenerated,
}

/// <summary>
/// One statement of a record. The id stays the same through edits. Evidence is the pair (record's transcript
/// revision, segment id); after a user edit it is the evidence of the AI text the user started from.
/// </summary>
public sealed record RecordStatement
{
    public required Guid Id { get; init; }

    public required string Text { get; init; }

    public required Certainty Certainty { get; init; }

    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public required TextOrigin Origin { get; init; }
}

public sealed record RecordSection
{
    public required Guid Id { get; init; }

    public required SectionKind Kind { get; init; }

    public required string Heading { get; init; }

    public required TextOrigin HeadingOrigin { get; init; }

    public required IReadOnlyList<RecordStatement> Statements { get; init; }
}

/// <summary>A candidate as shown in the record: always 「未反映」 in Phase D (detail spec 10.2).</summary>
public sealed record RecordCandidate
{
    public required Candidate Candidate { get; init; }

    public required IReadOnlyList<CandidateConcern> Flags { get; init; }
}

public sealed record RecordProvenance
{
    public required int? AnalysisRevision { get; init; }

    public required string? ModelId { get; init; }

    public required string? ModelSha256 { get; init; }

    public required string? PromptVersion { get; init; }

    public required string? ConfigHash { get; init; }

    public required Guid? JobId { get; init; }
}

/// <summary><c>records/rev-NNNN/record.json</c>. Written once; every edit or regeneration is the next revision.</summary>
public sealed record RecordDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid SessionId { get; init; }

    public required int Revision { get; init; }

    public required int? ParentRevision { get; init; }

    public required RecordRevisionKind Kind { get; init; }

    /// <summary>The transcript revision all evidence refers to.</summary>
    public required int TranscriptRevision { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required RecordProvenance Provenance { get; init; }

    public required string Title { get; init; }

    public required TextOrigin TitleOrigin { get; init; }

    public required IReadOnlyList<RecordType> RecordTypes { get; init; }

    public required IReadOnlyList<RecordStatement> Summary { get; init; }

    public required IReadOnlyList<RecordSection> Sections { get; init; }

    public required IReadOnlyList<RecordCandidate> Candidates { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Time ranges the generation could not cover (partial generation).</summary>
    public required IReadOnlyList<FailedPiece> MissingPieces { get; init; }

    /// <summary>Model statements without verifiable evidence, listed for the reviewer as 「根拠確認が必要」.</summary>
    public required IReadOnlyList<DroppedItem> Unsupported { get; init; }

    [JsonIgnore]
    public IEnumerable<RecordStatement> AllStatements => Summary.Concat(Sections.SelectMany(s => s.Statements));
}

public static class RecordFactory
{
    public static RecordDocument FromAnalysis(Guid sessionId, AnalysisDocument analysis, int revision, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var a = analysis.Analysis;
        var notes = analysis.CandidateNotes.ToDictionary(n => n.CandidateId, n => n.Flags, StringComparer.Ordinal);
        return new RecordDocument
        {
            SessionId = sessionId,
            Revision = revision,
            ParentRevision = null,
            Kind = RecordRevisionKind.Generated,
            TranscriptRevision = a.TranscriptRevision,
            CreatedAtUtc = nowUtc,
            Provenance = new RecordProvenance
            {
                AnalysisRevision = analysis.Revision,
                ModelId = analysis.Provenance.ModelId,
                ModelSha256 = analysis.Provenance.ModelSha256,
                PromptVersion = analysis.Provenance.PromptVersion,
                ConfigHash = analysis.Provenance.ConfigHash,
                JobId = analysis.Provenance.JobId,
            },
            Title = a.Title,
            TitleOrigin = TextOrigin.Ai,
            RecordTypes = a.RecordTypes,
            Summary = [.. a.Summary.Select(Statement)],
            Sections = [.. a.Sections.Select(Section)],
            Candidates = [.. a.Candidates.Select(c => new RecordCandidate { Candidate = c, Flags = notes.GetValueOrDefault(c.CandidateId) ?? [] })],
            Warnings = a.Warnings,
            MissingPieces = analysis.FailedPieces,
            Unsupported = analysis.Dropped,
        };
    }

    public static RecordStatement Statement(Statement s) => new()
    {
        Id = Guid.CreateVersion7(),
        Text = s.Text,
        Certainty = s.Certainty,
        Evidence = s.Evidence,
        Origin = TextOrigin.Ai,
    };

    public static RecordSection Section(Section s) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = s.SectionKind,
        Heading = s.Heading,
        HeadingOrigin = TextOrigin.Ai,
        Statements = [.. s.Statements.Select(Statement)],
    };
}

/// <summary>
/// Structured edits (plan 0, 3.6): each returns the next revision with the change and the origin recorded.
/// The record's Markdown is always rendered from this data; it is never parsed back.
/// </summary>
public static class RecordEditing
{
    public static RecordDocument EditTitle(RecordDocument doc, int next, string title, DateTimeOffset nowUtc)
    {
        var text = Required(title, 200, nameof(title));
        return Next(doc, next, nowUtc) with { Title = text, TitleOrigin = Edited(doc.TitleOrigin) };
    }

    public static RecordDocument EditStatement(RecordDocument doc, int next, Guid statementId, string text, DateTimeOffset nowUtc)
    {
        var value = Required(text, 4000, nameof(text));
        return MapStatements(Next(doc, next, nowUtc), statementId, s => [s with { Text = value, Origin = Edited(s.Origin) }]);
    }

    public static RecordDocument DeleteStatement(RecordDocument doc, int next, Guid statementId, DateTimeOffset nowUtc)
    {
        var result = MapStatements(Next(doc, next, nowUtc), statementId, _ => []);
        // A section left without statements is removed (no empty sections).
        return result with { Sections = [.. result.Sections.Where(s => s.Statements.Count > 0)] };
    }

    /// <param name="sectionId">Null adds to the summary.</param>
    /// <param name="afterStatementId">Null adds at the end.</param>
    public static RecordDocument AddStatement(RecordDocument doc, int next, Guid? sectionId, Guid? afterStatementId, string text, DateTimeOffset nowUtc)
    {
        var statement = new RecordStatement { Id = Guid.CreateVersion7(), Text = Required(text, 4000, nameof(text)), Certainty = Certainty.Stated, Evidence = [], Origin = TextOrigin.UserWritten };
        var d = Next(doc, next, nowUtc);
        if (sectionId is null) return d with { Summary = Insert(d.Summary, afterStatementId, statement) };
        var found = false;
        var sections = d.Sections.Select(s =>
        {
            if (s.Id != sectionId) return s;
            found = true;
            return s with { Statements = Insert(s.Statements, afterStatementId, statement) };
        }).ToList();
        if (!found) throw new ArgumentException("The section does not exist.", nameof(sectionId));
        return d with { Sections = sections };
    }

    public static RecordDocument EditHeading(RecordDocument doc, int next, Guid sectionId, string heading, DateTimeOffset nowUtc)
    {
        var value = Heading(doc, heading, sectionId);
        return MapSection(Next(doc, next, nowUtc), sectionId, s => s with { Heading = value, HeadingOrigin = Edited(s.HeadingOrigin) });
    }

    /// <summary>Adds a section with one statement (a section is never empty).</summary>
    public static RecordDocument AddSection(RecordDocument doc, int next, int index, string heading, string firstStatement, DateTimeOffset nowUtc)
    {
        var section = new RecordSection
        {
            Id = Guid.CreateVersion7(),
            Kind = SectionKind.Custom,
            Heading = Heading(doc, heading, null),
            HeadingOrigin = TextOrigin.UserWritten,
            Statements = [new RecordStatement { Id = Guid.CreateVersion7(), Text = Required(firstStatement, 4000, nameof(firstStatement)), Certainty = Certainty.Stated, Evidence = [], Origin = TextOrigin.UserWritten }],
        };
        var sections = doc.Sections.ToList();
        sections.Insert(Math.Clamp(index, 0, sections.Count), section);
        return Next(doc, next, nowUtc) with { Sections = sections };
    }

    public static RecordDocument DeleteSection(RecordDocument doc, int next, Guid sectionId, DateTimeOffset nowUtc)
    {
        if (doc.Sections.All(s => s.Id != sectionId)) throw new ArgumentException("The section does not exist.", nameof(sectionId));
        return Next(doc, next, nowUtc) with { Sections = [.. doc.Sections.Where(s => s.Id != sectionId)] };
    }

    public static RecordDocument MoveSection(RecordDocument doc, int next, Guid sectionId, int offset, DateTimeOffset nowUtc)
    {
        var sections = doc.Sections.ToList();
        var index = sections.FindIndex(s => s.Id == sectionId);
        if (index < 0) throw new ArgumentException("The section does not exist.", nameof(sectionId));
        var target = Math.Clamp(index + offset, 0, sections.Count - 1);
        var section = sections[index];
        sections.RemoveAt(index);
        sections.Insert(target, section);
        return Next(doc, next, nowUtc) with { Sections = sections };
    }

    /// <summary>Replaces one section with a regenerated one (from the same transcript revision).</summary>
    public static RecordDocument ReplaceSection(RecordDocument doc, int next, Guid sectionId, RecordSection replacement, RecordProvenance provenance, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Statements.Count == 0) throw new ArgumentException("A section needs statements.", nameof(replacement));
        var d = MapSection(Next(doc, next, nowUtc), sectionId, s => replacement with { Id = s.Id });
        return d with { Kind = RecordRevisionKind.SectionRegenerated, Provenance = provenance };
    }

    private static RecordDocument Next(RecordDocument doc, int next, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (next <= doc.Revision) throw new ArgumentOutOfRangeException(nameof(next), "The next revision must be after the parent.");
        return doc with { Revision = next, ParentRevision = doc.Revision, Kind = RecordRevisionKind.Edited, CreatedAtUtc = nowUtc };
    }

    private static TextOrigin Edited(TextOrigin origin) => origin == TextOrigin.UserWritten ? TextOrigin.UserWritten : TextOrigin.UserEdited;

    private static RecordDocument MapStatements(RecordDocument doc, Guid statementId, Func<RecordStatement, IEnumerable<RecordStatement>> map)
    {
        var found = false;
        IReadOnlyList<RecordStatement> Map(IReadOnlyList<RecordStatement> list) => [.. list.SelectMany(s =>
        {
            if (s.Id != statementId) return [s];
            found = true;
            return map(s);
        })];
        var summary = Map(doc.Summary);
        var sections = doc.Sections.Select(s => s with { Statements = Map(s.Statements) }).ToList();
        if (!found) throw new ArgumentException("The statement does not exist.", nameof(statementId));
        return doc with { Summary = summary, Sections = sections };
    }

    private static RecordDocument MapSection(RecordDocument doc, Guid sectionId, Func<RecordSection, RecordSection> map)
    {
        if (doc.Sections.All(s => s.Id != sectionId)) throw new ArgumentException("The section does not exist.", nameof(sectionId));
        return doc with { Sections = [.. doc.Sections.Select(s => s.Id == sectionId ? map(s) : s)] };
    }

    private static List<RecordStatement> Insert(IReadOnlyList<RecordStatement> list, Guid? after, RecordStatement statement)
    {
        var result = list.ToList();
        var index = after is null ? result.Count : result.FindIndex(s => s.Id == after) + 1;
        if (index == 0 && after is not null) throw new ArgumentException("The statement does not exist.", nameof(after));
        result.Insert(index, statement);
        return result;
    }

    private static string Heading(RecordDocument doc, string heading, Guid? self)
    {
        var value = Required(heading, 100, nameof(heading));
        if (ExtractionConverter.ReservedHeadings.Contains(value)) throw new ArgumentException($"「{value}」はアプリが使う見出しです。", nameof(heading));
        if (doc.Sections.Any(s => s.Id != self && s.Heading == value)) throw new ArgumentException($"「{value}」という見出しはすでにあります。", nameof(heading));
        return value;
    }

    private static string Required(string text, int max, string name)
    {
        var value = ExtractionConverter.Clean(text);
        if (value.Length == 0) throw new ArgumentException("空にはできません。", name);
        if (value.Length > max) throw new ArgumentException($"{max} 文字までです。", name);
        return value;
    }
}
