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
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
