using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;

namespace VoiceJournal.Core.Transcription;

/// <summary>Who chose the revision in use: the job that wrote it, or the user (an edit, a re-recognition, a choice).</summary>
public enum TranscriptAdoptedBy
{
    Automatic,
    User,
}

/// <summary><c>transcripts/current.json</c>: the revision in use. Rewritten in place; it only points at revisions.</summary>
public sealed record TranscriptAdoption
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required int Revision { get; init; }

    public required TranscriptAdoptedBy By { get; init; }

    public required DateTimeOffset AdoptedAtUtc { get; init; }
}

/// <summary>
/// Transcript revisions of one session folder (plan 3.2):
/// <c>transcripts/rev-NNNN/transcript.raw.json</c> (+ <c>transcript.raw.txt</c>) or <c>transcript.edited.json</c>,
/// and <c>transcript.md</c> at the session root rendered from the revision in use. A revision is written once
/// and never replaced.
/// </summary>
/// <remarks>
/// The revision in use (<see cref="CurrentRevision"/>) is what edits, re-recognition and later steps build on.
/// Edits must start from it and become it. A new raw revision becomes it only while nothing chosen by the user
/// would be hidden: a full job that finishes after the user edited (a resumed or late job) leaves the edits in use.
/// Without <c>current.json</c> (sessions from before it existed) the latest revision is the one in use.
/// </remarks>
public sealed class TranscriptStore
{
    public const string FolderName = "transcripts";
    public const string RawFileName = "transcript.raw.json";
    public const string RawTextFileName = "transcript.raw.txt";
    public const string EditedFileName = "transcript.edited.json";
    public const string MarkdownFileName = "transcript.md";
    public const string CurrentFileName = "current.json";

    // One writer per session folder at a time (the job runner and UI edits can race for the next number).
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _sessionDirectory;

    public TranscriptStore(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        _sessionDirectory = Path.GetFullPath(sessionDirectory);
    }

    public string RootDirectory => Path.Combine(_sessionDirectory, FolderName);

    public static string RevisionFolderName(int revision) => "rev-" + revision.ToString("0000", CultureInfo.InvariantCulture);

    public string RevisionDirectory(int revision) => Path.Combine(RootDirectory, RevisionFolderName(revision));

    /// <summary>Revision numbers that have a document, ascending.</summary>
    public IReadOnlyList<int> Revisions()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var list = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories(RootDirectory, "rev-*"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) continue;
            if (File.Exists(Path.Combine(dir, RawFileName)) || File.Exists(Path.Combine(dir, EditedFileName))) list.Add(n);
        }
        list.Sort();
        return list;
    }

    public int NextRevision() => Revisions() is { Count: > 0 } r ? r[^1] + 1 : 1;

    /// <exception cref="FileNotFoundException">The revision does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is broken or does not match its folder.</exception>
    public TranscriptDocument Load(int revision)
    {
        var dir = RevisionDirectory(revision);
        var path = File.Exists(Path.Combine(dir, RawFileName)) ? Path.Combine(dir, RawFileName) : Path.Combine(dir, EditedFileName);
        TranscriptDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<TranscriptDocument>(AtomicFile.ReadAllBytes(path), VoiceJournalJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid transcript revision {revision}.", ex);
        }
        if (doc is null || doc.SchemaVersion != TranscriptDocument.CurrentSchemaVersion || doc.Revision != revision)
            throw new InvalidDataException($"Transcript revision {revision} is empty, of an unknown schema, or numbered differently.");
        return doc;
    }

    public TranscriptDocument? LoadLatest() => Revisions() is { Count: > 0 } r ? Load(r[^1]) : null;

    /// <summary>The recorded choice, or null when there is none (or it points at a revision that does not exist).</summary>
    public TranscriptAdoption? ReadAdoption()
    {
        var path = Path.Combine(RootDirectory, CurrentFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var adoption = JsonSerializer.Deserialize<TranscriptAdoption>(AtomicFile.ReadAllBytes(path), VoiceJournalJson.Options);
            return adoption is { SchemaVersion: TranscriptAdoption.CurrentSchemaVersion } && Revisions().Contains(adoption.Revision) ? adoption : null;
        }
        catch (JsonException)
        {
            return null; // a broken pointer falls back to the latest revision; the revisions themselves are intact
        }
    }

    /// <summary>The revision in use: the recorded choice, else the latest; null when there is no transcript.</summary>
    public int? CurrentRevision() => ReadAdoption()?.Revision ?? (Revisions() is { Count: > 0 } r ? r[^1] : null);

    public TranscriptDocument? LoadCurrent() => CurrentRevision() is { } revision ? Load(revision) : null;

    /// <summary>Makes an existing revision the one in use (the user's choice) and re-renders <c>transcript.md</c> from it.</summary>
    /// <exception cref="FileNotFoundException">The revision does not exist.</exception>
    public void Adopt(int revision, DateTimeOffset nowUtc)
    {
        lock (Gates.GetOrAdd(_sessionDirectory, _ => new object()))
        {
            if (!Revisions().Contains(revision)) throw new FileNotFoundException($"Transcript revision {revision} does not exist.");
            WriteAdoption(Load(revision), TranscriptAdoptedBy.User, nowUtc);
        }
    }

    /// <summary>
    /// Writes a new revision. Fails when the revision already exists (revisions are immutable), when it is not the
    /// next number, when the segments are invalid, or when an edited revision does not start from the revision in use.
    /// Returns whether the new revision became the one in use (see the class remarks); <c>transcript.md</c> follows it.
    /// </summary>
    /// <param name="durationMs">Session length; segments must end within it. Null skips that check.</param>
    public bool Save(TranscriptDocument document, long? durationMs = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document, durationMs);
        lock (Gates.GetOrAdd(_sessionDirectory, _ => new object()))
        {
            var expected = NextRevision();
            if (document.Revision != expected)
                throw new InvalidOperationException($"Transcript revision {document.Revision} cannot be saved; the next revision is {expected}.");
            var adoption = ReadAdoption();
            var current = adoption?.Revision ?? (expected > 1 ? expected - 1 : (int?)null);
            bool adopt;
            if (document.Kind == TranscriptRevisionKind.Edited)
            {
                if (document.ParentRevision != current)
                    throw new InvalidOperationException($"Transcript revision {document.Revision} starts from revision {document.ParentRevision}, but revision {current} is in use.");
                adopt = true;
            }
            else
            {
                adopt = adoption is null
                    ? current is null || Load(current.Value).Kind == TranscriptRevisionKind.Raw
                    : adoption.By == TranscriptAdoptedBy.Automatic;
            }
            var dir = RevisionDirectory(document.Revision);
            // No document yet (it is next), so anything here is left over from a save that crashed midway.
            if (Directory.Exists(dir))
                foreach (var stale in Directory.EnumerateFiles(dir)) File.Delete(stale);
            Directory.CreateDirectory(dir);
            if (document.Kind == TranscriptRevisionKind.Raw)
                WriteNew(Path.Combine(dir, RawTextFileName), Encoding.UTF8.GetBytes(TranscriptRendering.ToRawText(document)));
            // The JSON is written last: a revision exists only once its document does.
            WriteNew(Path.Combine(dir, document.Kind == TranscriptRevisionKind.Raw ? RawFileName : EditedFileName),
                JsonSerializer.SerializeToUtf8Bytes(document, VoiceJournalJson.Options));
            if (adopt)
                WriteAdoption(document, document.Kind == TranscriptRevisionKind.Edited ? TranscriptAdoptedBy.User : TranscriptAdoptedBy.Automatic, document.CreatedAtUtc);
            else if (adoption is null && current is { } kept)
                // No choice on file meant "the latest", which is now this raw revision: write down the edits that stay in use.
                WriteAdoption(Load(kept), TranscriptAdoptedBy.User, document.CreatedAtUtc);
            return adopt;
        }
    }

    private void WriteAdoption(TranscriptDocument document, TranscriptAdoptedBy by, DateTimeOffset nowUtc)
    {
        var adoption = new TranscriptAdoption { Revision = document.Revision, By = by, AdoptedAtUtc = nowUtc };
        AtomicFile.WriteAllBytes(Path.Combine(RootDirectory, CurrentFileName), JsonSerializer.SerializeToUtf8Bytes(adoption, VoiceJournalJson.Options));
        AtomicFile.WriteAllBytes(Path.Combine(_sessionDirectory, MarkdownFileName), Encoding.UTF8.GetBytes(TranscriptRendering.ToMarkdown(document)));
    }

    /// <exception cref="ArgumentException">The segments break the contract (detail spec 8.2).</exception>
    public static void Validate(TranscriptDocument document, long? durationMs)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Revision <= 0) throw new ArgumentException("Revision must be 1 or more.", nameof(document));
        if (document.Kind == TranscriptRevisionKind.Raw && (document.Provenance is null || document.RawRevision != document.Revision || document.ParentRevision is not null))
            throw new ArgumentException("A raw revision needs provenance, no parent, and itself as raw revision.", nameof(document));
        if (document.Kind == TranscriptRevisionKind.Edited && (document.ParentRevision is not { } parent || parent >= document.Revision || document.RawRevision > parent))
            throw new ArgumentException("An edited revision needs an earlier parent.", nameof(document));
        var ids = new HashSet<Guid>();
        foreach (var s in document.Segments)
        {
            if (!ids.Add(s.SegmentId)) throw new ArgumentException($"Segment id {s.SegmentId} appears twice.", nameof(document));
            if (s.StartMs < 0 || s.EndMs <= s.StartMs) throw new ArgumentException($"Segment {s.SegmentId} has an invalid time range.", nameof(document));
            if (durationMs is { } d && s.EndMs > d) throw new ArgumentException($"Segment {s.SegmentId} ends after the recording.", nameof(document));
        }
    }

    private static void WriteNew(string path, byte[] content)
    {
        if (File.Exists(path)) throw new IOException($"'{path}' already exists; transcript revisions are never replaced.");
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: false);
    }
}

/// <summary>Text renderings of a revision (<c>transcript.raw.txt</c>, <c>transcript.md</c>).</summary>
public static class TranscriptRendering
{
    public static string SourceLabel(AudioSource source) => source switch
    {
        AudioSource.Mic => "マイク",
        AudioSource.Loopback => "PC内部音声",
        _ => source.ToString(),
    };

    public static string FlagLabel(SegmentQuality flag) => flag switch
    {
        SegmentQuality.Silence => "無音",
        SegmentQuality.Repetition => "反復",
        SegmentQuality.TooShort => "短い",
        SegmentQuality.Boundary => "境界",
        SegmentQuality.AudioGap => "欠損",
        SegmentQuality.UserEdited => "修正",
        _ => flag.ToString(),
    };

    public static string Time(long ms, bool withMilliseconds)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        var text = string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");
        return withMilliseconds ? text + "." + t.Milliseconds.ToString("000", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>One line per segment: <c>[00:01:02.300 → 00:01:05.000] マイク: text</c>.</summary>
    public static string ToRawText(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        foreach (var s in document.Segments)
            text.Append('[').Append(Time(s.StartMs, true)).Append(" → ").Append(Time(s.EndMs, true)).Append("] ")
                .Append(SourceLabel(s.Source)).Append(": ").Append(s.Text.Trim()).Append('\n');
        return text.ToString();
    }

    /// <summary>The readable transcript (revision in use) with quality flags shown inline.</summary>
    public static string ToMarkdown(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        text.Append("# 文字起こし\n\n");
        text.Append(CultureInfo.InvariantCulture, $"- Revision: {document.Revision}（{(document.Kind == TranscriptRevisionKind.Raw ? "原文" : $"修正、元の原文は Revision {document.RawRevision}")}）\n");
        text.Append(CultureInfo.InvariantCulture, $"- 作成: {document.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}Z\n\n");
        foreach (var s in document.Segments)
        {
            // Stable anchor for evidence links from the record (detail spec 10.3): transcript.md#seg-<segment id>.
            text.Append(CultureInfo.InvariantCulture, $"<a id=\"seg-{s.SegmentId:D}\"></a>\n");
            text.Append("**").Append(Time(s.StartMs, false)).Append(' ').Append(SourceLabel(s.Source)).Append("**");
            if (s.QualityFlags.Count > 0) text.Append(" ⚠").Append(string.Join("・", s.QualityFlags.Select(FlagLabel)));
            text.Append("  \n").Append(s.Text.Trim()).Append("\n\n");
        }
        return text.ToString();
    }
}
