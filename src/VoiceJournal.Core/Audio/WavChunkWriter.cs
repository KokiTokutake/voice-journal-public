using System.Security.Cryptography;

namespace VoiceJournal.Core.Audio;

/// <summary>A chunk that was finalized, verified and renamed to <c>.wav</c>.</summary>
public sealed record CompletedChunk(string Path, long StartMs, long FrameCount, long SizeBytes, string Sha256);

/// <summary>
/// Writes one raw-audio chunk (detail spec 6.2): appends to <c>&lt;name&gt;.partial</c>, records progress
/// in a sidecar about once per second of audio, and on <see cref="Complete"/> patches the header,
/// flushes, re-reads the header to verify it, renames to <c>.wav</c> and hashes the result.
/// If disposed without completing, the <c>.partial</c> and its sidecar stay on disk for recovery.
/// Not thread-safe: one writer thread owns an instance.
/// </summary>
public sealed class WavChunkWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly WavHeaderLayout _layout;
    private readonly TimeProvider _time;
    private readonly long _sidecarIntervalFrames;
    private long _framesAtLastSidecar;
    private bool _completed;
    private bool _disposed;

    private WavChunkWriter(string partialPath, AudioFormatInfo format, long startMs, TimeProvider time)
    {
        PartialPath = partialPath;
        Format = format;
        StartMs = startMs;
        _time = time;
        _sidecarIntervalFrames = format.SampleRate; // about one second of audio
        _stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 64 * 1024);
        try
        {
            _layout = WavFile.WriteHeader(_stream, format);
            WriteSidecar(); // header + empty sidecar on disk before any audio is accepted
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string PartialPath { get; }

    public string SidecarPath => ChunkSidecar.PathFor(PartialPath);

    public AudioFormatInfo Format { get; }

    public long StartMs { get; }

    public long FramesWritten { get; private set; }

    public long DataBytes => FramesWritten * Format.BlockAlign;

    /// <summary>Creates <paramref name="partialPath"/> (must not exist) and writes the header.</summary>
    public static WavChunkWriter Create(string partialPath, AudioFormatInfo format, long startMs, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(partialPath);
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();
        if (!partialPath.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Chunk path must end with .partial.", nameof(partialPath));
        return new WavChunkWriter(partialPath, format, startMs, time ?? TimeProvider.System);
    }

    /// <summary>Appends whole frames. The length must be a multiple of the block align.</summary>
    public void Append(ReadOnlySpan<byte> frames)
    {
        ThrowIfClosed();
        if (frames.Length % Format.BlockAlign != 0) throw new ArgumentException($"Length {frames.Length} is not a multiple of block align {Format.BlockAlign}.", nameof(frames));
        _stream.Write(frames);
        FramesWritten += frames.Length / Format.BlockAlign;
        WriteSidecarIfDue();
    }

    /// <summary>Appends digital silence (zero bytes are silence for both PCM and IEEE float).</summary>
    public void AppendSilence(long frameCount)
    {
        ThrowIfClosed();
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        Span<byte> zeros = stackalloc byte[4096 - 4096 % Format.BlockAlign];
        zeros.Clear();
        var remaining = frameCount * Format.BlockAlign;
        while (remaining > 0)
        {
            var n = (int)Math.Min(remaining, zeros.Length);
            _stream.Write(zeros[..n]);
            remaining -= n;
        }
        FramesWritten += frameCount;
        WriteSidecarIfDue();
    }

    /// <summary>Flushes audio to disk, then records the flushed frame count in the sidecar.</summary>
    public void WriteSidecar()
    {
        ThrowIfClosed();
        WriteSidecarUnchecked();
    }

    private void WriteSidecarUnchecked()
    {
        _stream.Flush(flushToDisk: true);
        new ChunkSidecar
        {
            Format = Format,
            StartMs = StartMs,
            DataOffset = _layout.DataOffset,
            FramesWritten = FramesWritten,
            UpdatedAtUtc = _time.GetUtcNow(),
        }.Save(SidecarPath);
        _framesAtLastSidecar = FramesWritten;
    }

    /// <summary>
    /// Finalizes the chunk: patch sizes, flush to disk, verify the header, rename to
    /// <paramref name="finalPath"/> (must not exist), delete the sidecar and hash the file.
    /// </summary>
    public CompletedChunk Complete(string finalPath)
    {
        ThrowIfClosed();
        ArgumentException.ThrowIfNullOrEmpty(finalPath);
        if (File.Exists(finalPath)) throw new IOException($"Refusing to overwrite existing chunk '{finalPath}'.");

        WavFile.PatchSizes(_stream, _layout, Format, DataBytes);
        _stream.Flush(flushToDisk: true);
        _stream.Position = 0;
        var info = WavFile.ReadHeader(_stream);
        if (info.DeclaredDataSize != DataBytes || info.Format != Format) throw new InvalidDataException("Chunk header verification failed after finalization.");
        _stream.Dispose();
        _completed = true;

        File.Move(PartialPath, finalPath);
        File.Delete(SidecarPath);

        string sha;
        long size;
        using (var fs = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            size = fs.Length;
            sha = Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        return new CompletedChunk(finalPath, StartMs, FramesWritten, size, sha);
    }

    /// <summary>Closes without finalizing; the <c>.partial</c> and an up-to-date sidecar remain for recovery.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_completed) return;
        try
        {
            WriteSidecarUnchecked();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full / device gone: the last good sidecar still describes what is safely on disk.
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private void WriteSidecarIfDue()
    {
        if (FramesWritten - _framesAtLastSidecar >= _sidecarIntervalFrames) WriteSidecarUnchecked();
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Chunk is already completed.");
    }
}
