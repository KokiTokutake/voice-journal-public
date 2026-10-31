using System.Security.Cryptography;

namespace VoiceJournal.Core.Audio;

/// <summary>Outcome of recovering an unfinished chunk.</summary>
/// <param name="RecoveredFrames">Whole frames copied into the recovered WAV.</param>
/// <param name="SidecarFrames">Frames the writer had confirmed on disk, or null when no usable sidecar existed.</param>
/// <param name="DroppedTailBytes">Bytes after the last whole frame that were not copied.</param>
/// <param name="ConsistentWithSidecar">False when the file holds fewer frames than the sidecar confirmed, or the formats differ.</param>
public sealed record PartialRecoveryResult(
    string RecoveredPath,
    AudioFormatInfo Format,
    long StartMs,
    long RecoveredFrames,
    long? SidecarFrames,
    long DroppedTailBytes,
    bool ConsistentWithSidecar,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Recovers the readable part of a <c>.partial</c> chunk into a new WAV (detail spec 19, REC-001).
/// The original <c>.partial</c> and its sidecar are never modified or deleted.
/// </summary>
public static class PartialChunkRecovery
{
    /// <exception cref="InvalidDataException">The partial file has no readable header.</exception>
    public static PartialRecoveryResult Recover(string partialPath, string recoveredPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(partialPath);
        ArgumentException.ThrowIfNullOrEmpty(recoveredPath);
        if (File.Exists(recoveredPath)) throw new IOException($"Refusing to overwrite existing file '{recoveredPath}'.");

        var sidecar = ChunkSidecar.TryLoad(ChunkSidecar.PathFor(partialPath));

        using var source = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var info = WavFile.ReadHeader(source);
        var format = info.Format;
        var available = Math.Max(0, source.Length - info.DataOffset);
        var frames = available / format.BlockAlign;
        var dataBytes = frames * format.BlockAlign;

        var consistent = sidecar is null
            || (sidecar.Format == format && sidecar.DataOffset == info.DataOffset && frames >= sidecar.FramesWritten);

        using (var target = new FileStream(recoveredPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var layout = WavFile.WriteHeader(target, format);
            source.Position = info.DataOffset;
            CopyBytes(source, target, dataBytes);
            WavFile.PatchSizes(target, layout, format, dataBytes);
            target.Flush(flushToDisk: true);
        }

        string sha;
        long size;
        using (var fs = new FileStream(recoveredPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            size = fs.Length;
            sha = Convert.ToHexStringLower(SHA256.HashData(fs));
        }

        return new PartialRecoveryResult(
            recoveredPath,
            format,
            sidecar?.StartMs ?? 0,
            frames,
            sidecar?.FramesWritten,
            available - dataBytes,
            consistent,
            size,
            sha);
    }

    private static void CopyBytes(Stream source, Stream target, long count)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException("Partial chunk shrank while recovering.");
            target.Write(buffer, 0, read);
            count -= read;
        }
    }
}
