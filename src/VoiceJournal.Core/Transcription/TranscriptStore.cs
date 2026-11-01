using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;

namespace VoiceJournal.Core.Transcription;

/// <summary>
/// Transcript revisions of one session folder (plan 3.2):
/// <c>transcripts/rev-NNNN/transcript.raw.json</c> (+ <c>transcript.raw.txt</c>) or <c>transcript.edited.json</c>,
/// and <c>transcript.md</c> at the session root rendered from the latest revision. A revision is written once
/// and never replaced.
/// </summary>
public sealed class TranscriptStore
{
    public const string FolderName = "transcripts";
    public const string RawFileName = "transcript.raw.json";
    public const string RawTextFileName = "transcript.raw.txt";
    public const string EditedFileName = "transcript.edited.json";
    public const string MarkdownFileName = "transcript.md";

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

    /// <summary>
    /// Writes a new revision. Fails when the revision already exists (revisions are immutable), when it is not the
    /// next number, or when the segments are invalid. Then re-renders <c>transcript.md</c> from this revision.
    /// </summary>
    /// <param name="durationMs">Session length; segments must end within it. Null skips that check.</param>
    public void Save(TranscriptDocument document, long? durationMs = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document, durationMs);
        lock (Gates.GetOrAdd(_sessionDirectory, _ => new object()))
        {
            var expected = NextRevision();
            if (document.Revision != expected)
                throw new InvalidOperationException($"Transcript revision {document.Revision} cannot be saved; the next revision is {expected}.");
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
            AtomicFile.WriteAllBytes(Path.Combine(_sessionDirectory, MarkdownFileName), Encoding.UTF8.GetBytes(TranscriptRendering.ToMarkdown(document)));
        }
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

    /// <summary>The readable transcript (latest revision) with quality flags shown inline.</summary>
    public static string ToMarkdown(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        text.Append("# 文字起こし\n\n");
        text.Append(CultureInfo.InvariantCulture, $"- Revision: {document.Revision}（{(document.Kind == TranscriptRevisionKind.Raw ? "原文" : $"修正、元の原文は Revision {document.RawRevision}")}）\n");
        text.Append(CultureInfo.InvariantCulture, $"- 作成: {document.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}Z\n\n");
        foreach (var s in document.Segments)
        {
            text.Append("**").Append(Time(s.StartMs, false)).Append(' ').Append(SourceLabel(s.Source)).Append("**");
            if (s.QualityFlags.Count > 0) text.Append(" ⚠").Append(string.Join("・", s.QualityFlags.Select(FlagLabel)));
            text.Append("  \n").Append(s.Text.Trim()).Append("\n\n");
        }
        return text.ToString();
    }
}
