using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceJournal.Core.IO;

namespace VoiceJournal.Core.Analysis;

/// <summary>What produced an analysis: enough to tell whether two analyses are comparable (NFR-10, AT-26).</summary>
public sealed record AnalysisProvenance
{
    public required string ModelId { get; init; }

    /// <summary>SHA-256 of the GGUF when the app can identify the file; null when only the id is known.</summary>
    public required string? ModelSha256 { get; init; }

    public required string PromptVersion { get; init; }

    /// <summary>Hash of the settings that can change the output (model, context, reserve, temperature, prompts, schemas).</summary>
    public required string ConfigHash { get; init; }

    public required int ContextLength { get; init; }

    public required int OutputReserve { get; init; }

    public required Guid? JobId { get; init; }
}

/// <summary>A piece the model could not handle; its time range is missing from the record (partial generation, 9.2).</summary>
public sealed record FailedPiece(int Piece, long StartMs, long EndMs, string Reason);

/// <summary><c>analysis/rev-NNNN.json</c>: a validated analysis with what the schema has no room for. Written once.</summary>
public sealed record AnalysisDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required int Revision { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required AnalysisProvenance Provenance { get; init; }

    /// <summary>The contract of detail spec 16; valid against the full schema and 16.2.</summary>
    public required AnalysisResult Analysis { get; init; }

    public required IReadOnlyList<CandidateNote> CandidateNotes { get; init; }

    /// <summary>Statements and candidates without verifiable evidence (「根拠確認が必要」); never approvable.</summary>
    public required IReadOnlyList<DroppedItem> Dropped { get; init; }

    public required IReadOnlyList<FailedPiece> FailedPieces { get; init; }

    public required int PieceCount { get; init; }

    [JsonIgnore]
    public bool IsPartial => FailedPieces.Count > 0;
}

/// <summary>Analyses of one session: <c>analysis/rev-NNNN.json</c>, never replaced (plan 3.2).</summary>
public sealed class AnalysisStore
{
    public const string FolderName = "analysis";

    private readonly string _root;

    public AnalysisStore(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        _root = Path.Combine(Path.GetFullPath(sessionDirectory), FolderName);
    }

    public string RootDirectory => _root;

    public string WorkDirectory(Guid jobId) => Path.Combine(_root, "work", jobId.ToString("D"));

    public static string FileName(int revision) => "rev-" + revision.ToString("0000", CultureInfo.InvariantCulture) + ".json";

    public IReadOnlyList<int> Revisions()
    {
        if (!Directory.Exists(_root)) return [];
        var list = new List<int>();
        foreach (var file in Directory.EnumerateFiles(_root, "rev-*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) list.Add(n);
        }
        list.Sort();
        return list;
    }

    public int NextRevision() => Revisions() is { Count: > 0 } r ? r[^1] + 1 : 1;

    /// <exception cref="InvalidDataException">The file is broken or numbered differently.</exception>
    public AnalysisDocument Load(int revision)
    {
        AnalysisDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<AnalysisDocument>(AtomicFile.ReadAllBytes(Path.Combine(_root, FileName(revision))), AnalysisJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid analysis revision {revision}.", ex);
        }
        if (doc is null || doc.SchemaVersion != AnalysisDocument.CurrentSchemaVersion || doc.Revision != revision)
            throw new InvalidDataException($"Analysis revision {revision} is empty, of an unknown schema, or numbered differently.");
        return doc;
    }

    public AnalysisDocument? LoadLatest() => Revisions() is { Count: > 0 } r ? Load(r[^1]) : null;

    /// <summary>Writes the next revision. The caller has validated it (schema and 16.2) beforehand.</summary>
    public void Save(AnalysisDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var expected = NextRevision();
        if (document.Revision != expected)
            throw new InvalidOperationException($"Analysis revision {document.Revision} cannot be saved; the next revision is {expected}.");
        Directory.CreateDirectory(_root);
        WriteOnce.Write(Path.Combine(_root, FileName(document.Revision)), JsonSerializer.SerializeToUtf8Bytes(document, AnalysisJson.Options));
    }
}

/// <summary>Write-once files: temp file flushed to disk, then moved without overwrite.</summary>
public static class WriteOnce
{
    /// <exception cref="IOException">The file already exists.</exception>
    public static void Write(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (File.Exists(path)) throw new IOException($"'{path}' already exists; revisions are never replaced.");
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: false);
    }
}

/// <summary>Writes small atomic JSON files with <see cref="AnalysisJson"/> (work files of a job).</summary>
public static class AnalysisFiles
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, AnalysisJson.Options));
    }

    /// <summary>Null when the file is missing or unreadable (a work file from a crash is simply redone).</summary>
    public static T? TryRead<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(AtomicFile.ReadAllBytes(path), AnalysisJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
