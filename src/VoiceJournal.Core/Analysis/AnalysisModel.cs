using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceJournal.Core.Analysis;

// The analysis contract of detail spec 16 (urn:voicejournal:analysis:1.0). These types serialize to exactly the
// shape of that schema with AnalysisJson.Options (snake_case, nulls written); the Infrastructure validator checks
// the serialized form against AnalysisSchema.Json, and AnalysisValidator adds the checks of 16.2.

public enum RecordType
{
    Meeting,
    Idea,
    Learning,
    Work,
    Reflection,
    Consultation,
    Mixed,
    Other,
}

/// <summary>How firmly the conversation states something (detail spec 9.3): a fact or decision, a tentative idea, or unclear.</summary>
public enum Certainty
{
    Stated,
    Tentative,
    Unclear,
}

public enum SectionKind
{
    Context,
    Topic,
    Decision,
    OpenQuestion,
    Idea,
    Explanation,
    Result,
    Reflection,
    NextStep,
    Custom,
}

public enum CandidateKind
{
    Issue,
    Task,
}

public enum CandidateAction
{
    Create,
    Update,
    Mention,
}

public enum ItemStatus
{
    Open,
    Todo,
    InProgress,
    Blocked,
    Resolved,
    Closed,
    Done,
    Cancelled,
}

public enum ItemPriority
{
    Low,
    Medium,
    High,
    Critical,
}

public enum ItemField
{
    Title,
    Description,
    Status,
    Priority,
    Assignee,
    DueDate,
}

/// <summary>A quote from one segment of the analysed transcript revision.</summary>
public sealed record Evidence
{
    public required Guid SegmentId { get; init; }

    public required string Quote { get; init; }
}

public sealed record Statement
{
    public required string Text { get; init; }

    public required Certainty Certainty { get; init; }

    public required IReadOnlyList<Evidence> Evidence { get; init; }
}

public sealed record Section
{
    public required SectionKind SectionKind { get; init; }

    public required string Heading { get; init; }

    public required IReadOnlyList<Statement> Statements { get; init; }
}

public sealed record CandidateFields
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required ItemStatus? Status { get; init; }

    public required ItemPriority? Priority { get; init; }

    public required string? Assignee { get; init; }

    public required DateOnly? DueDate { get; init; }
}

public sealed record Candidate
{
    public required string CandidateId { get; init; }

    public required CandidateKind Kind { get; init; }

    public required CandidateAction Action { get; init; }

    public required Guid? SuggestedTargetId { get; init; }

    public required string? ParentIssueCandidateId { get; init; }

    public required Guid? ParentIssueItemId { get; init; }

    public required CandidateFields Fields { get; init; }

    public required IReadOnlyList<ItemField> ChangedFields { get; init; }

    public required Certainty Certainty { get; init; }

    public required string? DueExpression { get; init; }

    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public required string Rationale { get; init; }
}

/// <summary>The final analysis of one transcript revision (detail spec 16). A proposal, never a file-update command.</summary>
public sealed record AnalysisResult
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid SessionId { get; init; }

    public required int TranscriptRevision { get; init; }

    public required IReadOnlyList<RecordType> RecordTypes { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<Statement> Summary { get; init; }

    public required IReadOnlyList<Section> Sections { get; init; }

    public required IReadOnlyList<Candidate> Candidates { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// JSON settings for the analysis contract and the LLM wire format: snake_case names and enum values, and nulls
/// written (the schema requires every nullable field to be present). Unknown members are rejected.
/// </summary>
public static class AnalysisJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    /// <summary>Same as <see cref="Options"/> without indentation (prompts and hashing).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = indented,
            // Files and prompts only; never embedded in HTML. Keeps Japanese readable and token counts low.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
