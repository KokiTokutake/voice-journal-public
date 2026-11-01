using System.Text;
using VoiceJournal.Core.IO;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.IO;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-atomic-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Replace_WritesTheNewContent_WithoutLeavingTempFiles()
    {
        var path = _dir.File("manifest.json");
        AtomicFile.WriteAllBytes(path, "old"u8);
        AtomicFile.WriteAllBytes(path, "new"u8);

        Assert.Equal("new", Encoding.UTF8.GetString(AtomicFile.ReadAllBytes(path)));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Replace_RetriesWhileAnotherProcessBrieflyLocksTheFile()
    {
        var path = _dir.File("sidecar.json");
        AtomicFile.WriteAllBytes(path, "old"u8);

        // Like an antivirus scan: opened without delete sharing for a moment.
        var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(100);
            await locker.DisposeAsync();
        });

        AtomicFile.WriteAllBytes(path, "new"u8);
        await release;
        Assert.Equal("new", Encoding.UTF8.GetString(AtomicFile.ReadAllBytes(path)));
    }

    [Fact]
    public void Replace_GivesUpWhenTheFileStaysLocked_AndCleansUp()
    {
        var path = _dir.File("locked.json");
        AtomicFile.WriteAllBytes(path, "old"u8);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllBytes(path, "new"u8));
        }

        Assert.Equal("old", Encoding.UTF8.GetString(AtomicFile.ReadAllBytes(path)));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task ConcurrentReaders_NeverBreakTheWriter()
    {
        var path = _dir.File("manifest.json");
        AtomicFile.WriteAllBytes(path, "0"u8);
        using var cts = new CancellationTokenSource();
        var reads = 0;
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                AtomicFile.ReadAllBytes(path); // a reader polling the file, like a sync client or a viewer
                reads++;
                Thread.Sleep(1);
            }
        });

        for (var i = 1; i <= 300; i++) AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await cts.CancelAsync();
        await reader;

        Assert.Equal("300", Encoding.UTF8.GetString(AtomicFile.ReadAllBytes(path)));
        Assert.True(reads > 0);
    }
}
