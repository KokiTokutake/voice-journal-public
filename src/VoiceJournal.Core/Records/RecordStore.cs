using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.IO;

namespace VoiceJournal.Core.Records;

public sealed record RecordApproval
{
    public required int Revision { get; init; }

    public required DateTimeOffset ApprovedAtUtc { get; init; }

    /// <summary>The transcript revision in use when the record was approved (detail spec 13.1).</summary>
    public required int TranscriptRevision { get; init; }

    /// <summary>SHA-256 of the <c>record.md</c> the app wrote; a different file means someone edited it outside the app.</summary>
    public required string MarkdownSha256 { get; init; }
}

/// <summary><c>records/review.json</c>: the last approval and whether the user deferred the record (保留).</summary>
public sealed record RecordReview
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public RecordApproval? Approved { get; init; }

    public DateTimeOffset? DeferredSinceUtc { get; init; }
}

public enum RecordReviewState
{
    /// <summary>No record yet.</summary>
    None,

    /// <summary>A draft waits for review.</summary>
    Draft,

    Deferred,

    /// <summary>The latest revision is approved and still built on the transcript in use.</summary>
    Approved,

    /// <summary>Approved, but a newer draft exists or the transcript in use changed since (detail spec 13.3, AT-26).</summary>
    ApprovedOutdated,
}

public sealed record RecordStatus(RecordReviewState State, int? LatestRevision, int? ApprovedRevision, bool TranscriptChanged, bool NewerDraft);

/// <summary>Result of an approval: where an externally edited <c>record.md</c> was kept, if there was one.</summary>
public sealed record ApprovalResult(RecordApproval Approval, string? PreservedExternalEdit);

/// <summary>
/// Records of one session (plan 3.2): <c>records/rev-NNNN/record.json</c> and <c>record.md</c> (written once),
/// <c>records/review.json</c>, <c>record.draft.md</c> (the latest revision) and <c>record.md</c> (the approved one)
/// at the session root. The approved Markdown is never overwritten blindly: an outside edit is kept in
/// <c>records/external/</c> before the app writes a new one (detail spec 10.3).
/// </summary>
public sealed class RecordStore
{
    public const string FolderName = "records";
    public const string DocumentFileName = "record.json";
    public const string MarkdownFileName = "record.md";
    public const string DraftFileName = "record.draft.md";
    public const string ReviewFileName = "review.json";

    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _sessionDirectory;

    public RecordStore(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        _sessionDirectory = Path.GetFullPath(sessionDirectory);
    }

    public string RootDirectory => Path.Combine(_sessionDirectory, FolderName);

    public string ApprovedMarkdownPath => Path.Combine(_sessionDirectory, MarkdownFileName);

    public string DraftMarkdownPath => Path.Combine(_sessionDirectory, DraftFileName);

    public string RevisionDirectory(int revision) => Path.Combine(RootDirectory, "rev-" + revision.ToString("0000", CultureInfo.InvariantCulture));

    public IReadOnlyList<int> Revisions()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var list = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories(RootDirectory, "rev-*"))
        {
            if (int.TryParse(Path.GetFileName(dir).AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                && File.Exists(Path.Combine(dir, DocumentFileName)))
                list.Add(n);
        }
        list.Sort();
        return list;
    }

    public int NextRevision() => Revisions() is { Count: > 0 } r ? r[^1] + 1 : 1;

    /// <exception cref="InvalidDataException">The file is broken or numbered differently.</exception>
    public RecordDocument Load(int revision)
    {
        RecordDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<RecordDocument>(AtomicFile.ReadAllBytes(Path.Combine(RevisionDirectory(revision), DocumentFileName)), AnalysisJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid record revision {revision}.", ex);
        }
        if (doc is null || doc.SchemaVersion != RecordDocument.CurrentSchemaVersion || doc.Revision != revision)
            throw new InvalidDataException($"Record revision {revision} is empty, of an unknown schema, or numbered differently.");
        return doc;
    }

    public RecordDocument? LoadLatest() => Revisions() is { Count: > 0 } r ? Load(r[^1]) : null;

    /// <summary>
    /// Writes the next revision and re-renders <c>record.draft.md</c>. An edit must start from the latest revision
    /// (no branches); a new generation may start fresh. The approved <c>record.md</c> is not touched.
    /// </summary>
    public void Save(RecordDocument document, Func<RecordDocument, string> render)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(render);
        lock (Gates.GetOrAdd(_sessionDirectory, _ => new object()))
        {
            var expected = NextRevision();
            if (document.Revision != expected)
                throw new InvalidOperationException($"Record revision {document.Revision} cannot be saved; the next revision is {expected}.");
            if (document.Kind != RecordRevisionKind.Generated && document.ParentRevision != expected - 1)
                throw new InvalidOperationException($"Record revision {document.Revision} starts from revision {document.ParentRevision}, but the latest is {expected - 1}.");
            var dir = RevisionDirectory(document.Revision);
            if (Directory.Exists(dir))
                foreach (var stale in Directory.EnumerateFiles(dir)) File.Delete(stale); // left over from a crashed save
            Directory.CreateDirectory(dir);
            var markdown = Encoding.UTF8.GetBytes(render(document));
            WriteOnce.Write(Path.Combine(dir, MarkdownFileName), markdown);
            WriteOnce.Write(Path.Combine(dir, DocumentFileName), JsonSerializer.SerializeToUtf8Bytes(document, AnalysisJson.Options));
            AtomicFile.WriteAllBytes(DraftMarkdownPath, markdown);
        }
    }

    public RecordReview ReadReview()
    {
        var path = Path.Combine(RootDirectory, ReviewFileName);
        if (!File.Exists(path)) return new RecordReview();
        try
        {
            return JsonSerializer.Deserialize<RecordReview>(AtomicFile.ReadAllBytes(path), AnalysisJson.Options) is { SchemaVersion: RecordReview.CurrentSchemaVersion } review
                ? review
                : throw new InvalidDataException("Unknown review file.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid review file.", ex);
        }
    }

    /// <summary>
    /// Approves a revision: <c>record.md</c> becomes its rendering (from <c>records/rev-NNNN/record.md</c>).
    /// An existing <c>record.md</c> that differs from what the app last wrote is moved to <c>records/external/</c> first.
    /// </summary>
    public ApprovalResult Approve(int revision, int transcriptRevisionInUse, DateTimeOffset nowUtc)
    {
        lock (Gates.GetOrAdd(_sessionDirectory, _ => new object()))
        {
            if (!Revisions().Contains(revision)) throw new FileNotFoundException($"Record revision {revision} does not exist.");
            var markdown = AtomicFile.ReadAllBytes(Path.Combine(RevisionDirectory(revision), MarkdownFileName));
            var review = ReadReview();
            var preserved = PreserveExternalEdit(review.Approved, nowUtc);
            AtomicFile.WriteAllBytes(ApprovedMarkdownPath, markdown);
            var approval = new RecordApproval
            {
                Revision = revision,
                ApprovedAtUtc = nowUtc,
                TranscriptRevision = transcriptRevisionInUse,
                MarkdownSha256 = Sha256(markdown),
            };
            WriteReview(review with { Approved = approval, DeferredSinceUtc = null });
            return new ApprovalResult(approval, preserved);
        }
    }

    public void SetDeferred(bool deferred, DateTimeOffset nowUtc)
    {
        lock (Gates.GetOrAdd(_sessionDirectory, _ => new object()))
        {
            var review = ReadReview();
            WriteReview(review with { DeferredSinceUtc = deferred ? review.DeferredSinceUtc ?? nowUtc : null });
        }
    }

    /// <summary>Whether <c>record.md</c> was changed outside the app since it was approved.</summary>
    public bool HasExternalEdit()
    {
        var approved = ReadReview().Approved;
        return File.Exists(ApprovedMarkdownPath) && (approved is null || Sha256(AtomicFile.ReadAllBytes(ApprovedMarkdownPath)) != approved.MarkdownSha256);
    }

    public RecordStatus Status(int? transcriptRevisionInUse)
    {
        var latest = Revisions() is { Count: > 0 } r ? r[^1] : (int?)null;
        if (latest is null) return new RecordStatus(RecordReviewState.None, null, null, false, false);
        var review = ReadReview();
        var approved = review.Approved;
        var transcriptChanged = approved is not null && transcriptRevisionInUse is { } t && t != approved.TranscriptRevision;
        var newer = approved is not null && latest > approved.Revision;
        var state = review.DeferredSinceUtc is not null ? RecordReviewState.Deferred
            : approved is null ? RecordReviewState.Draft
            : transcriptChanged || newer ? RecordReviewState.ApprovedOutdated
            : RecordReviewState.Approved;
        return new RecordStatus(state, latest, approved?.Revision, transcriptChanged, newer);
    }

    private string? PreserveExternalEdit(RecordApproval? approved, DateTimeOffset nowUtc)
    {
        if (!File.Exists(ApprovedMarkdownPath)) return null;
        var current = AtomicFile.ReadAllBytes(ApprovedMarkdownPath);
        if (approved is not null && Sha256(current) == approved.MarkdownSha256) return null;
        var dir = Path.Combine(RootDirectory, "external");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "record-" + nowUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".md");
        WriteOnce.Write(path, current);
        return path;
    }

    private void WriteReview(RecordReview review)
    {
        Directory.CreateDirectory(RootDirectory);
        AtomicFile.WriteAllBytes(Path.Combine(RootDirectory, ReviewFileName), JsonSerializer.SerializeToUtf8Bytes(review, AnalysisJson.Options));
    }

    public static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
