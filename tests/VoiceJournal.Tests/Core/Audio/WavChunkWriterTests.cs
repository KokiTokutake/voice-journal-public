using System.Security.Cryptography;
using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Audio;

public sealed class WavChunkWriterTests : IDisposable
{
    private static readonly AudioFormatInfo Format = AudioFormatInfo.IeeeFloat32Bit(48000, 2);

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Complete_RenamesVerifiesAndHashes()
    {
        var partial = _dir.File("000001.partial");
        var final = _dir.File("000001.wav");
        var audio = Sine(Format, seconds: 0.5);

        CompletedChunk done;
        using (var writer = WavChunkWriter.Create(partial, Format, startMs: 1500))
        {
            writer.Append(audio);
            done = writer.Complete(final);
        }

        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(ChunkSidecar.PathFor(partial)));
        Assert.True(File.Exists(final));
        Assert.Equal(1500, done.StartMs);
        Assert.Equal(24000, done.FrameCount);
        Assert.Equal(new FileInfo(final).Length, done.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(final))), done.Sha256);

        // Float stays float, bit-exact (detail spec 6.2).
        using var reader = new WaveFileReader(final);
        Assert.Equal(WaveFormatEncoding.Extensible, reader.WaveFormat.Encoding);
        var read = new byte[audio.Length];
        reader.ReadExactly(read);
        Assert.Equal(audio, read);
    }

    [Fact]
    public void Sidecar_TracksFlushedFrames_AboutOncePerSecond()
    {
        var partial = _dir.File("000001.partial");
        using var writer = WavChunkWriter.Create(partial, Format, startMs: 0);

        var initial = ChunkSidecar.TryLoad(writer.SidecarPath);
        Assert.NotNull(initial);
        Assert.Equal(0, initial.FramesWritten);

        writer.Append(Sine(Format, seconds: 0.5));
        Assert.Equal(0, ChunkSidecar.TryLoad(writer.SidecarPath)!.FramesWritten); // not due yet

        writer.Append(Sine(Format, seconds: 0.6));
        var sidecar = ChunkSidecar.TryLoad(writer.SidecarPath)!;
        Assert.Equal(52800, sidecar.FramesWritten);
        Assert.Equal(Format, sidecar.Format);
        Assert.True(new FileInfo(partial).Length >= sidecar.DataOffset + sidecar.FramesWritten * Format.BlockAlign);
    }

    [Fact]
    public void Dispose_WithoutComplete_LeavesPartialAndCurrentSidecar()
    {
        var partial = _dir.File("000001.partial");
        using (var writer = WavChunkWriter.Create(partial, Format, startMs: 0))
        {
            writer.Append(Sine(Format, seconds: 0.25));
        }

        Assert.True(File.Exists(partial));
        Assert.Equal(12000, ChunkSidecar.TryLoad(ChunkSidecar.PathFor(partial))!.FramesWritten);
    }

    [Fact]
    public void AppendSilence_WritesZeroFrames()
    {
        var partial = _dir.File("000001.partial");
        var final = _dir.File("000001.wav");
        using (var writer = WavChunkWriter.Create(partial, Format, startMs: 0))
        {
            writer.AppendSilence(1000);
            Assert.Equal(1000, writer.FramesWritten);
            writer.Complete(final);
        }

        using var reader = new WaveFileReader(final);
        var read = new byte[reader.Length];
        reader.ReadExactly(read);
        Assert.Equal(1000 * Format.BlockAlign, read.Length);
        Assert.All(read, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Append_RejectsPartialFrames()
    {
        using var writer = WavChunkWriter.Create(_dir.File("000001.partial"), Format, startMs: 0);
        Assert.Throws<ArgumentException>(() => writer.Append(new byte[Format.BlockAlign + 3]));
    }

    [Fact]
    public void Complete_RefusesToOverwriteExistingChunk()
    {
        var final = _dir.File("000001.wav");
        File.WriteAllText(final, "existing");
        using var writer = WavChunkWriter.Create(_dir.File("000001.partial"), Format, startMs: 0);
        Assert.Throws<IOException>(() => writer.Complete(final));
        Assert.Equal("existing", File.ReadAllText(final));
    }

    [Fact]
    public void Create_RefusesExistingPartialAndWrongExtension()
    {
        var partial = _dir.File("000001.partial");
        File.WriteAllText(partial, "x");
        Assert.Throws<IOException>(() => WavChunkWriter.Create(partial, Format, startMs: 0));
        Assert.Throws<ArgumentException>(() => WavChunkWriter.Create(_dir.File("000002.wav"), Format, startMs: 0));
    }

    [Fact]
    public void Append_AfterComplete_Throws()
    {
        using var writer = WavChunkWriter.Create(_dir.File("000001.partial"), Format, startMs: 0);
        writer.Complete(_dir.File("000001.wav"));
        Assert.Throws<InvalidOperationException>(() => writer.Append(new byte[Format.BlockAlign]));
    }

    internal static byte[] Sine(AudioFormatInfo format, double seconds)
    {
        var frames = (int)(format.SampleRate * seconds);
        var bytes = new byte[frames * format.BlockAlign];
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / format.SampleRate));
            for (var c = 0; c < format.Channels; c++) samples[i * format.Channels + c] = v;
        }
        return bytes;
    }
}
