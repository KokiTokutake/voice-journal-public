using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Audio;

public sealed class WavFileTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> Formats => ["pcm16-mono", "float-stereo", "float-ext-stereo", "pcm16-ext-mono"];

    private static AudioFormatInfo FormatFor(string name) => name switch
    {
        "pcm16-mono" => AudioFormatInfo.Pcm16Bit(16000, 1),
        "float-stereo" => AudioFormatInfo.IeeeFloat32Bit(48000, 2, extensible: false),
        "float-ext-stereo" => AudioFormatInfo.IeeeFloat32Bit(48000, 2),
        "pcm16-ext-mono" => AudioFormatInfo.Pcm16Bit(44100, 1) with { Extensible = true, ValidBitsPerSample = 16, ChannelMask = 0x4 },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void HeaderRoundTrips_AndNAudioReadsTheFile(string name)
    {
        var format = FormatFor(name);
        var path = _dir.File($"{name}.wav");
        var data = Enumerable.Range(0, format.BlockAlign * 100).Select(i => (byte)i).ToArray();

        using (var fs = File.Create(path))
        {
            var layout = WavFile.WriteHeader(fs, format);
            fs.Write(data);
            WavFile.PatchSizes(fs, layout, format, data.Length);
        }

        using (var fs = File.OpenRead(path))
        {
            var info = WavFile.ReadHeader(fs);
            Assert.Equal(format, info.Format);
            Assert.Equal((uint)data.Length, info.DeclaredDataSize);
        }

        // Independent reader: the file must be a standard WAV that other tools accept.
        using var reader = new WaveFileReader(path);
        Assert.Equal(format.SampleRate, reader.WaveFormat.SampleRate);
        Assert.Equal(format.Channels, reader.WaveFormat.Channels);
        Assert.Equal(format.BitsPerSample, reader.WaveFormat.BitsPerSample);
        Assert.Equal(data.Length, reader.Length);
        var read = new byte[data.Length];
        reader.ReadExactly(read);
        Assert.Equal(data, read);
    }

    [Fact]
    public void OddDataSize_GetsRiffPadByte()
    {
        var format = new AudioFormatInfo { SampleRate = 8000, Channels = 1, BitsPerSample = 8, Encoding = AudioSampleEncoding.Pcm };
        var path = _dir.File("odd.wav");
        using (var fs = File.Create(path))
        {
            var layout = WavFile.WriteHeader(fs, format);
            fs.Write([1, 2, 3]);
            WavFile.PatchSizes(fs, layout, format, 3);
            Assert.Equal(layout.DataOffset + 4, fs.Length);
        }

        using var reader = new WaveFileReader(path);
        Assert.Equal(3, reader.Length);
    }

    [Fact]
    public void ReadHeader_RejectsNonWav()
    {
        using var ms = new MemoryStream("not a wav file at all"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => WavFile.ReadHeader(ms));
    }

    [Fact]
    public void ReadHeader_RejectsTruncatedHeader()
    {
        using var ms = new MemoryStream("RIFF\0\0\0\0WAVEfmt "u8.ToArray());
        Assert.Throws<InvalidDataException>(() => WavFile.ReadHeader(ms));
    }

    [Theory]
    [InlineData(0, 2, 16)]       // sample rate
    [InlineData(48000, 0, 16)]   // channels
    [InlineData(48000, 2, 12)]   // bits not a whole byte
    public void Validate_RejectsUnsupportedFormats(int rate, int channels, int bits)
    {
        var format = new AudioFormatInfo { SampleRate = rate, Channels = channels, BitsPerSample = bits, Encoding = AudioSampleEncoding.Pcm };
        Assert.ThrowsAny<ArgumentException>(format.Validate);
    }

    [Fact]
    public void BlockAlign_AndByteRate_FollowFormat()
    {
        var format = AudioFormatInfo.IeeeFloat32Bit(48000, 2);
        Assert.Equal(8, format.BlockAlign);
        Assert.Equal(384_000, format.AverageBytesPerSecond);
    }
}
