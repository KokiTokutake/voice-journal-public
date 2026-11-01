using System.Text.Json;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;

namespace VoiceJournal.Core.Transcription;

public enum SttJobKind
{
    /// <summary>Transcribe the whole session into a new raw revision.</summary>
    Full,

    /// <summary>Re-recognize one range of one source into a new edited revision (AT-07).</summary>
    Range,
}

public enum SttPieceState
{
    Pending,
    Done,

    /// <summary>The recognizer returned nothing for this piece (STT-003).</summary>
    NoSpeech,
}

/// <summary>Progress of one piece. A finished piece keeps its converted segments in a file with a hash, so a resumed job reuses it.</summary>
public sealed class SttJobPiece
{
    public required AudioSource Source { get; init; }

    public required int Index { get; init; }

    public SttPieceState State { get; set; } = SttPieceState.Pending;

    /// <summary>Converted segments (session time), relative to the job folder.</summary>
    public string? SegmentsFile { get; set; }

    public string? SegmentsSha256 { get; set; }

    public int Attempts { get; set; }

    public long ElapsedMs { get; set; }
}

/// <summary>
/// One processing job (<c>stt/native/&lt;job-id&gt;/job.json</c>, detail spec 7): input hash, settings snapshot, engine and
/// model hashes, per-piece progress, attempts and the error code. The file is the ledger; the database only indexes it.
/// Holds no transcript text.
/// </summary>
public sealed class SttJob
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid JobId { get; init; }

    public required Guid SessionId { get; init; }

    public required SttJobKind Kind { get; init; }

    public ProcessingState State { get; set; } = ProcessingState.Queued;

    /// <summary>Settings snapshot taken when the job was queued.</summary>
    public required SttConfiguration Configuration { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? EndedAtUtc { get; set; }

    /// <summary>How many times the job was started (resumes count).</summary>
    public int Attempt { get; set; }

    public string? InputHash { get; set; }

    public SttEngineInfo? Engine { get; set; }

    public IList<SttJobPiece> Pieces { get; init; } = [];

    public SttErrorCode? Error { get; set; }

    /// <summary>Short diagnostic (no transcript text, detail spec 21.3).</summary>
    public string? ErrorDetail { get; set; }

    /// <summary>The transcript revision this job produced.</summary>
    public int? ResultRevision { get; set; }

    /// <summary>Range only: what to re-recognize, and the revision the user was looking at.</summary>
    public AudioSource? RangeSource { get; init; }

    public TimeRange? Range { get; init; }

    public int? ParentRevision { get; init; }

    /// <summary>Wall time spent recognizing, excluding pauses.</summary>
    public long TranscribeElapsedMs => Pieces.Sum(p => p.ElapsedMs);
}

/// <summary>Reads and writes job ledgers under <c>stt/native/</c> of a session.</summary>
public static class SttJobStore
{
    public const string NativeFolder = "stt/native";
    public const string FileName = "job.json";

    public static string JobDirectory(string sessionDirectory, Guid jobId) =>
        Path.Combine(sessionDirectory, NativeFolder.Replace('/', Path.DirectorySeparatorChar), jobId.ToString("D"));

    public static void Save(string sessionDirectory, SttJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var dir = JobDirectory(sessionDirectory, job.JobId);
        Directory.CreateDirectory(dir);
        AtomicFile.WriteAllBytes(Path.Combine(dir, FileName), JsonSerializer.SerializeToUtf8Bytes(job, VoiceJournalJson.Options));
    }

    /// <exception cref="InvalidDataException">The ledger is broken or of an unknown schema.</exception>
    public static SttJob Load(string sessionDirectory, Guid jobId)
    {
        var path = Path.Combine(JobDirectory(sessionDirectory, jobId), FileName);
        SttJob? job;
        try
        {
            job = JsonSerializer.Deserialize<SttJob>(AtomicFile.ReadAllBytes(path), VoiceJournalJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid job ledger {jobId}.", ex);
        }
        if (job is null || job.SchemaVersion != SttJob.CurrentSchemaVersion || job.JobId != jobId)
            throw new InvalidDataException($"Job ledger {jobId} is empty, of an unknown schema, or for another job.");
        return job;
    }

    /// <summary>All readable jobs of a session, oldest first. Broken ledgers are skipped.</summary>
    public static IReadOnlyList<SttJob> List(string sessionDirectory)
    {
        var root = Path.Combine(sessionDirectory, NativeFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(root)) return [];
        var jobs = new List<SttJob>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "D", out var id) || !File.Exists(Path.Combine(dir, FileName))) continue;
            try
            {
                jobs.Add(Load(sessionDirectory, id));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // Reported by the index rebuild; a broken ledger never blocks the others.
            }
        }
        return [.. jobs.OrderBy(j => j.CreatedAtUtc)];
    }

    /// <summary>
    /// The session's processing state: that of the newest whole-session job, or <see cref="ProcessingState.None"/>.
    /// Range jobs do not change it.
    /// </summary>
    public static ProcessingState SessionState(IEnumerable<SttJob> jobs) =>
        jobs.Where(j => j.Kind == SttJobKind.Full).OrderBy(j => j.CreatedAtUtc).LastOrDefault()?.State ?? ProcessingState.None;
}
