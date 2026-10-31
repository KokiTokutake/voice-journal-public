using System.Security.Cryptography;
using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Audio;

public sealed class PartialChunkRecoveryTests : IDisposable
{
    private static readonly AudioFormatInfo Format = AudioFormatInfo.IeeeFloat32Bit(48000, 2);

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void CrashWithTornLastFrame_RecoversWholeFrames_AndKeepsOriginal()
    {
        var partial = WriteCrashedPartial(seconds: 1.5, startMs: 300_000);
        using (var fs = new FileStream(partial, FileMode.Append)) fs.Write([1, 2, 3]); // torn frame after the crash
        var before = Hash(partial);

        var result = PartialChunkRecovery.Recover(partial, _dir.File("000001.recovered.wav"));

        Assert.Equal(72000, result.RecoveredFrames);
        Assert.Equal(72000, result.SidecarFrames);
        Assert.Equal(3, result.DroppedTailBytes);
        Assert.True(result.ConsistentWithSidecar);
        Assert.Equal(300_000, result.StartMs);
        Assert.Equal(before, Hash(partial)); // original untouched
        Assert.True(File.Exists(ChunkSidecar.PathFor(partial)));

        using var reader = new WaveFileReader(result.RecoveredPath);
        Assert.Equal(72000L * Format.BlockAlign, reader.Length);
        Assert.Equal(result.Sha256, Hash(result.RecoveredPath));
    }

    [Fact]
    public void FileShorterThanSidecar_IsReportedInconsistent()
    {
        var partial = WriteCrashedPartial(seconds: 2, startMs: 0);
        using (var fs = new FileStream(partial, FileMode.Open)) fs.SetLength(fs.Length - 1000 * Format.BlockAlign);

        var result = PartialChunkRecovery.Recover(partial, _dir.File("000001.recovered.wav"));

        Assert.False(result.ConsistentWithSidecar);
        Assert.Equal(96000 - 1000, result.RecoveredFrames);
        Assert.Equal(96000, result.SidecarFrames);
    }

    [Fact]
    public void MissingSidecar_StillRecoversFromHeader()
    {
        var partial = WriteCrashedPartial(seconds: 0.5, startMs: 0);
        File.Delete(ChunkSidecar.PathFor(partial));

        var result = PartialChunkRecovery.Recover(partial, _dir.File("000001.recovered.wav"));

        Assert.Null(result.SidecarFrames);
        Assert.True(result.ConsistentWithSidecar);
        Assert.Equal(24000, result.RecoveredFrames);
    }

    [Fact]
    public void HeaderOnly_RecoversEmptyWav()
    {
        var partial = _dir.File("000001.partial");
        using (WavChunkWriter.Create(partial, Format, startMs: 0)) { }

        var result = PartialChunkRecovery.Recover(partial, _dir.File("000001.recovered.wav"));

        Assert.Equal(0, result.RecoveredFrames);
        using var reader = new WaveFileReader(result.RecoveredPath);
        Assert.Equal(0, reader.Length);
    }

    [Fact]
    public void UnreadableHeader_Throws()
    {
        var partial = _dir.File("000001.partial");
        File.WriteAllBytes(partial, new byte[10]);
        Assert.Throws<InvalidDataException>(() => PartialChunkRecovery.Recover(partial, _dir.File("000001.recovered.wav")));
    }

    [Fact]
    public void RefusesToOverwriteRecoveredFile()
    {
        var partial = WriteCrashedPartial(seconds: 0.1, startMs: 0);
        var target = _dir.File("000001.recovered.wav");
        File.WriteAllText(target, "keep");
        Assert.Throws<IOException>(() => PartialChunkRecovery.Recover(partial, target));
        Assert.Equal("keep", File.ReadAllText(target));
    }

    private string WriteCrashedPartial(double seconds, long startMs)
    {
        var partial = _dir.File("000001.partial");
        using var writer = WavChunkWriter.Create(partial, Format, startMs);
        writer.Append(WavChunkWriterTests.Sine(Format, seconds));
        return partial; // disposed without Complete, as after a crash
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
