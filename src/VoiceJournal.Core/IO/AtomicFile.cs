namespace VoiceJournal.Core.IO;

/// <summary>
/// Replaces a file so readers see either the old or the new content, never a half-written one:
/// write a temp file in the same directory, flush it to disk, then move it over the target.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }
            MoveWithRetry(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Reads a file that may be replaced concurrently by <see cref="WriteAllBytes"/>, sharing write and
    /// delete. On Windows an open handle still makes the replace fail until it is closed, so readers must
    /// keep the file open only briefly; the writer retries meanwhile.
    /// </summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    // Another process (antivirus, indexer, backup) may hold the target open without delete sharing for a
    // moment; the replace then fails with a sharing violation. Retry briefly (~0.6 s in total) before giving up.
    private static void MoveWithRetry(string temp, string path)
    {
        const int attempts = 7;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts - 1)
            {
                Thread.Sleep(10 << attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
