using System.Text.Json;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;

namespace VoiceJournal.Core.Sessions;

/// <summary>
/// manifest.json of a session folder. The raw audio files plus this manifest are the source of truth
/// for a recording (detail spec 11.2); the database only indexes them and can be rebuilt from here.
/// </summary>
public sealed class SessionManifest
{
    public const string FileName = "manifest.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid SessionId { get; init; }

    public string? Title { get; set; }

    public string? Project { get; set; }

    public required CaptureMode CaptureMode { get; init; }

    public CaptureState CaptureState { get; set; } = CaptureState.Ready;

    public MicProcessingMode? MicMode { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Windows time zone id in effect when recording started (display only; timeline uses a monotonic clock).</summary>
    public required string TimeZoneId { get; init; }

    public DateTimeOffset? EndedAtUtc { get; set; }

    public IList<TrackManifest> Tracks { get; init; } = [];

    public static string PathIn(string sessionDirectory) => Path.Combine(sessionDirectory, FileName);

    /// <summary>Atomically replaces manifest.json in <paramref name="sessionDirectory"/>.</summary>
    public void Save(string sessionDirectory)
    {
        foreach (var track in Tracks)
            foreach (var chunk in track.Chunks)
                SessionPaths.EnsureSafeRelativePath(chunk.File);
        AtomicFile.WriteAllBytes(PathIn(sessionDirectory), ToJsonBytes());
    }

    /// <summary>The exact bytes <see cref="Save"/> writes.</summary>
    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, VoiceJournalJson.Options);

    /// <exception cref="InvalidDataException">The manifest is missing required data or has an unknown schema version.</exception>
    public static SessionManifest Load(string sessionDirectory) => Parse(AtomicFile.ReadAllBytes(PathIn(sessionDirectory)), sessionDirectory);

    /// <summary>Parses manifest bytes already read (e.g. to hash exactly what was parsed).</summary>
    /// <exception cref="InvalidDataException">The manifest is missing required data or has an unknown schema version.</exception>
    public static SessionManifest Parse(ReadOnlySpan<byte> json, string sessionDirectory)
    {
        SessionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SessionManifest>(json, VoiceJournalJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid manifest in '{sessionDirectory}'.", ex);
        }
        if (manifest is null) throw new InvalidDataException($"Empty manifest in '{sessionDirectory}'.");
        if (manifest.SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException($"Unsupported manifest schema version {manifest.SchemaVersion}.");
        foreach (var track in manifest.Tracks)
            foreach (var chunk in track.Chunks)
                SessionPaths.EnsureSafeRelativePath(chunk.File);
        return manifest;
    }
}

public sealed class TrackManifest
{
    public required Guid TrackId { get; init; }

    public required AudioSource Source { get; init; }

    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required AudioFormatInfo Format { get; init; }

    /// <summary>Offset of this track's first frame from the session start, in milliseconds.</summary>
    public long StartOffsetMs { get; set; }

    public TrackState State { get; set; } = TrackState.Recording;

    public IList<ChunkManifest> Chunks { get; init; } = [];

    public IList<AudioEventManifest> Events { get; init; } = [];
}

public sealed class ChunkManifest
{
    public required int Sequence { get; init; }

    /// <summary>Path relative to the session folder, with forward slashes (e.g. <c>audio/mic/000001.wav</c>).</summary>
    public required string File { get; set; }

    /// <summary>Start of the chunk on the track timeline, in milliseconds.</summary>
    public required long StartMs { get; init; }

    public long FrameCount { get; set; }

    /// <summary>
    /// Monotonic-clock time (ms since session start) at which the chunk's first frame was captured, from the
    /// device's capture timestamps; null when the source gave none. <see cref="StartMs"/> counts samples, so the
    /// two drift apart by the device clock error. Together with <see cref="ClockEndMs"/> this is the per-track
    /// time map used to line up mic and loopback (detail spec 6.3). The audio itself is never resampled.
    /// </summary>
    public long? ClockStartMs { get; set; }

    /// <summary>Monotonic-clock time just after the chunk's last frame; see <see cref="ClockStartMs"/>.</summary>
    public long? ClockEndMs { get; set; }

    public long SizeBytes { get; set; }

    public string? Sha256 { get; set; }

    public ChunkState State { get; set; } = ChunkState.Partial;
}

public sealed class AudioEventManifest
{
    public required AudioEventKind Kind { get; init; }

    public required long StartMs { get; init; }

    public long? EndMs { get; set; }

    /// <summary>Free-form diagnostic detail. Must not contain transcript text or personal data (detail spec 21.3).</summary>
    public string? Detail { get; init; }
}
