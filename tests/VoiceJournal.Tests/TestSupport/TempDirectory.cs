namespace VoiceJournal.Tests.TestSupport;

/// <summary>A fresh directory under %TEMP% (local NTFS) that is deleted when the test ends.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "vj-test-") => Path = Directory.CreateTempSubdirectory(prefix).FullName;

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
