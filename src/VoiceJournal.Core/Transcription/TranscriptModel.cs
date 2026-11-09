using VoiceJournal.Core.Capture;

namespace VoiceJournal.Core.Transcription;

/// <summary>Quality flags a segment can carry (detail spec 8.2). Shown with emphasis in review; never used to delete text.</summary>
public enum SegmentQuality
{
    /// <summary>The audio under the segment is (almost) silent: likely a recognizer hallucination.</summary>
    Silence,

    /// <summary>The text repeats itself, or repeats the previous segments (a common hallucination pattern).</summary>
    Repetition,

    /// <summary>Very short utterance.</summary>
    TooShort,

    /// <summary>Near a split boundary and possibly cut; both candidates were kept for review.</summary>
    Boundary,

    /// <summary>Overlaps audio that is missing (device disconnected, overflow, crash tail).</summary>
    AudioGap,

    /// <summary>Changed by the user (text edit, re-recognition or revert).</summary>
    UserEdited,

    /// <summary>
    /// A microphone segment whose words are, at the same time, in the PC audio: the far side heard through the
    /// speakers. Kept in the text, left out of the record (the PC audio already has it).
    /// </summary>
    Echo,
}

public enum TranscriptRevisionKind
{
    /// <summary>Recognizer output (immutable). A new raw revision is made for every (re)processing of the audio.</summary>
    Raw,

    /// <summary>User changes on top of a parent revision.</summary>
    Edited,
}

public enum TranscriptChangeKind
{
    TextEdited,
    RevertedToOriginal,
    ReRecognized,
}

/// <summary>
/// One recognized utterance. Times are milliseconds on the session timeline (the same zero as the raw audio).
/// Segments of different sources may overlap. <see cref="SegmentId"/> stays the same while only the text is
/// edited, so a reference is always the pair (revision, segment id).
/// </summary>
public sealed record TranscriptSegment
{
    public required Guid SegmentId { get; init; }

    public required AudioSource Source { get; init; }

    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public required string Text { get; init; }

    public IReadOnlyList<SegmentQuality> QualityFlags { get; init; } = [];

    /// <summary>Where the recognizer output came from (e.g. <c>stt/native/&lt;job&gt;/mic-0001.json#12</c>). Diagnostic only.</summary>
    public string? NativeRef { get; init; }

    /// <summary>Raw-revision segments this one was derived from; null for a raw segment (its own id is its origin).</summary>
    public IReadOnlyList<Guid>? OriginSegmentIds { get; init; }

    public long DurationMs => EndMs - StartMs;

    public IReadOnlyList<Guid> Origins => OriginSegmentIds ?? [SegmentId];

    public bool Has(SegmentQuality flag) => QualityFlags.Contains(flag);

    public TranscriptSegment WithFlag(SegmentQuality flag) =>
        Has(flag) ? this : this with { QualityFlags = [.. QualityFlags, flag] };
}

/// <summary>What produced a raw revision (or a re-recognized part): enough to tell whether two results are comparable.</summary>
public sealed record SttProvenance
{
    /// <summary>Hash of the STT input: the raw chunk hashes plus the conversion settings.</summary>
    public required string InputHash { get; init; }

    public required string EngineName { get; init; }

    public required string EngineVersion { get; init; }

    public required string EngineSha256 { get; init; }

    public required string ModelName { get; init; }

    public required string ModelSha256 { get; init; }

    /// <summary>Hash of the settings that can change the output (<see cref="SttConfiguration.ComputeHash"/>).</summary>
    public required string ConfigHash { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public Guid? JobId { get; init; }
}

public sealed record TimeRange(long StartMs, long EndMs)
{
    public long DurationMs => EndMs - StartMs;

    public bool Overlaps(long startMs, long endMs) => startMs < EndMs && StartMs < endMs;

    public long OverlapMs(long startMs, long endMs) => Math.Max(0, Math.Min(EndMs, endMs) - Math.Max(StartMs, startMs));
}

/// <summary>Per-source outcome of a raw revision.</summary>
public sealed record TranscriptSourceResult
{
    public required AudioSource Source { get; init; }

    /// <summary>Parts of the timeline where the recognizer returned nothing (STT-003: shown as "not recognized").</summary>
    public IReadOnlyList<TimeRange> UnrecognizedRanges { get; init; } = [];
}

/// <summary>One user change recorded in an edited revision. Holds ids and settings, never the text itself.</summary>
public sealed record TranscriptChange
{
    public required TranscriptChangeKind Kind { get; init; }

    /// <summary>Segments of the parent revision that were changed or replaced.</summary>
    public required IReadOnlyList<Guid> SegmentIds { get; init; }

    /// <summary>Segments in this revision that replaced them (same ids for a text edit).</summary>
    public required IReadOnlyList<Guid> NewSegmentIds { get; init; }

    /// <summary>Re-recognition only: the range that was recognized again and what recognized it.</summary>
    public TimeRange? Range { get; init; }

    public AudioSource? Source { get; init; }

    public SttProvenance? Provenance { get; init; }
}

/// <summary>
/// One transcript revision (<c>transcripts/rev-NNNN/transcript.raw.json</c> or <c>transcript.edited.json</c>).
/// Revisions are never rewritten; a change produces the next revision with a full copy of the segments.
/// </summary>
public sealed class TranscriptDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid SessionId { get; init; }

    public required int Revision { get; init; }

    public required TranscriptRevisionKind Kind { get; init; }

    /// <summary>Edited only: the revision these changes were made on.</summary>
    public int? ParentRevision { get; init; }

    /// <summary>The raw revision this text ultimately comes from (itself for a raw revision).</summary>
    public required int RawRevision { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Raw only.</summary>
    public SttProvenance? Provenance { get; init; }

    /// <summary>Raw only.</summary>
    public IReadOnlyList<TranscriptSourceResult> Sources { get; init; } = [];

    /// <summary>Edited only: the changes relative to <see cref="ParentRevision"/>.</summary>
    public IReadOnlyList<TranscriptChange> Changes { get; init; } = [];

    /// <summary>All segments, ordered by start time then source.</summary>
    public required IReadOnlyList<TranscriptSegment> Segments { get; init; }

    public TranscriptSegment? Find(Guid segmentId) => Segments.FirstOrDefault(s => s.SegmentId == segmentId);

    /// <summary>Sort order used everywhere: start, then source, then end.</summary>
    public static IReadOnlyList<TranscriptSegment> Order(IEnumerable<TranscriptSegment> segments) =>
        [.. segments.OrderBy(s => s.StartMs).ThenBy(s => s.Source).ThenBy(s => s.EndMs)];
}
