using System.Text.Json;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;

namespace VoiceJournal.Core.Audio;

/// <summary>
/// Progress record written next to a <c>.partial</c> chunk about once per second of audio
/// (detail spec 6.2). <see cref="FramesWritten"/> only counts frames already flushed to disk,
/// so recovery can tell whether the file lost data the writer had acknowledged.
/// </summary>
public sealed record ChunkSidecar
{
    public int SchemaVersion { get; init; } = 1;

    public required AudioFormatInfo Format { get; init; }

    /// <summary>Position of the chunk's first frame on the track timeline, in milliseconds.</summary>
    public required long StartMs { get; init; }

    public required long DataOffset { get; init; }

    public required long FramesWritten { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public static string PathFor(string partialPath) => partialPath + ".json";

    public void Save(string path) => AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, VoiceJournalJson.Options));

    /// <summary>Returns null when the sidecar does not exist or cannot be parsed.</summary>
    public static ChunkSidecar? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ChunkSidecar>(File.ReadAllBytes(path), VoiceJournalJson.Options) : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
