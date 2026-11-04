using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Core.Sessions;

/// <summary>
/// Searchable index of sessions (SQLite in Infrastructure). The files and manifests are the source of
/// truth; the index can always be rebuilt from them (detail spec 15.3).
/// </summary>
public interface ISessionIndex
{
    /// <summary>Re-reads the session's manifest.json and replaces its rows. Returns false when nothing changed.</summary>
    /// <exception cref="InvalidDataException">The manifest is invalid.</exception>
    bool Upsert(string sessionDirectory);

    /// <summary>Same as <see cref="Upsert(string)"/> with manifest bytes the caller already has (e.g. a live session's snapshot).</summary>
    bool Upsert(string sessionDirectory, ReadOnlySpan<byte> manifestJson);

    /// <summary>Indexes every session folder under the workspace. Rows of sessions missing on disk are reported, not deleted.</summary>
    IndexRebuildReport Rebuild();

    /// <summary>Sessions for the library, newest first.</summary>
    IReadOnlyList<SessionSummary> ListSessions();
}

public sealed record SessionSummary(
    Guid SessionId,
    string? Title,
    string? Project,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string TimeZoneId,
    CaptureMode CaptureMode,
    CaptureState CaptureState,
    long DurationMs,
    bool HasAudioLoss,
    string RelativePath,
    ProcessingState ProcessingState = ProcessingState.None,
    int? LatestTranscriptRevision = null,
    Records.RecordReviewState RecordState = Records.RecordReviewState.None);

public sealed record IndexFailure(string RelativePath, string Reason);

public sealed record IndexRebuildReport(
    int Indexed,
    int Unchanged,
    IReadOnlyList<IndexFailure> Failed,
    IReadOnlyList<Guid> MissingOnDisk,
    IReadOnlyList<string> NeedsRecovery);
