using System.Globalization;
using VoiceJournal.Core.Capture;

namespace VoiceJournal.Core.Sessions;

/// <summary>Naming of session folders and chunk files (detail spec 15.1) plus relative-path safety checks (21.1).</summary>
public static class SessionPaths
{
    /// <summary><c>sessions/yyyy/MM/&lt;session-id&gt;</c> under the workspace root, using the UTC start month.</summary>
    public static string SessionDirectory(string workspaceRoot, Guid sessionId, DateTimeOffset startedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        var utc = startedAtUtc.ToUniversalTime();
        return Path.Combine(
            workspaceRoot,
            "sessions",
            utc.Year.ToString("D4", CultureInfo.InvariantCulture),
            utc.Month.ToString("D2", CultureInfo.InvariantCulture),
            sessionId.ToString("D"));
    }

    public static string SourceFolderName(AudioSource source) => source switch
    {
        AudioSource.Mic => "mic",
        AudioSource.Loopback => "loopback",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>Relative path of a finalized chunk, e.g. <c>audio/mic/000001.wav</c>.</summary>
    public static string ChunkFile(AudioSource source, int sequence) => ChunkRelative(source, sequence, ".wav");

    public static string PartialChunkFile(AudioSource source, int sequence) => ChunkRelative(source, sequence, ".partial");

    public static string RecoveredChunkFile(AudioSource source, int sequence) => ChunkRelative(source, sequence, ".recovered.wav");

    /// <summary>Resolves a manifest-relative path inside the session folder, rejecting anything that escapes it.</summary>
    public static string Resolve(string sessionDirectory, string relativePath)
    {
        EnsureSafeRelativePath(relativePath);
        var root = Path.GetFullPath(sessionDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Path '{relativePath}' escapes the session folder.");
        return full;
    }

    /// <summary>Rejects absolute, rooted, parent-relative, device, ADS and backslash paths (detail spec 21.1).</summary>
    public static void EnsureSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Contains(':', StringComparison.Ordinal)
            || relativePath.StartsWith('/')
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe relative path '{relativePath}'.");
        }
    }

    private static string ChunkRelative(AudioSource source, int sequence, string extension)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        return $"audio/{SourceFolderName(source)}/{sequence.ToString("D6", CultureInfo.InvariantCulture)}{extension}";
    }
}
