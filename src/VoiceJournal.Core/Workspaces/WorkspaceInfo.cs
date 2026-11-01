using System.Text.Json;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;

namespace VoiceJournal.Core.Workspaces;

/// <summary><c>workspace.json</c> at the workspace root (detail spec 15.1): identity, schema and storage policy.</summary>
public sealed class WorkspaceInfo
{
    public const string FileName = "workspace.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid WorkspaceId { get; init; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public StoragePolicy Storage { get; init; } = new();

    public static string PathIn(string root) => Path.Combine(root, FileName);

    public void Save(string root) =>
        AtomicFile.WriteAllBytes(PathIn(root), JsonSerializer.SerializeToUtf8Bytes(this, VoiceJournalJson.Options));

    /// <exception cref="InvalidDataException">The file is not a valid workspace description.</exception>
    public static WorkspaceInfo Load(string root)
    {
        WorkspaceInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<WorkspaceInfo>(AtomicFile.ReadAllBytes(PathIn(root)), VoiceJournalJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid {FileName} in '{root}'.", ex);
        }
        if (info is null) throw new InvalidDataException($"Empty {FileName} in '{root}'.");
        if (info.SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException($"Unsupported workspace schema version {info.SchemaVersion}.");
        if (info.WorkspaceId == Guid.Empty) throw new InvalidDataException($"{FileName} has no workspace id.");
        return info;
    }
}

/// <summary>How the workspace keeps audio. Audio is never deleted automatically (detail spec 19 FS-001).</summary>
public sealed record StoragePolicy
{
    /// <summary>Recording does not start, and stops safely, below this much free space.</summary>
    public long MinFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public bool AutoDeleteAudio { get; init; }
}
