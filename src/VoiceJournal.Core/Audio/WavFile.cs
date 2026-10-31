using System.Buffers.Binary;
using System.Text;

namespace VoiceJournal.Core.Audio;

/// <summary>Where the size fields and sample data live in a WAV header written by <see cref="WavFile"/>.</summary>
public sealed record WavHeaderLayout(long FactSampleCountOffset, long DataSizeOffset, long DataOffset);

/// <summary>Result of parsing a WAV header.</summary>
/// <param name="DeclaredDataSize">Data chunk size from the header. Unreliable for an unfinished <c>.partial</c> file.</param>
public sealed record WavInfo(AudioFormatInfo Format, long DataOffset, uint DeclaredDataSize);

/// <summary>
/// Minimal RIFF/WAVE reader and writer. Kept in Core (not NAudio) so the recovery path can parse
/// headers of unfinished files exactly and the whole pipeline is testable without audio devices.
/// </summary>
public static class WavFile
{
    private const ushort FormatPcm = 0x0001;
    private const ushort FormatIeeeFloat = 0x0003;
    private const ushort FormatExtensible = 0xFFFE;

    internal static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Writes a header with zero sizes at the current position (expected 0). Sizes are patched by <see cref="PatchSizes"/>.</summary>
    public static WavHeaderLayout WriteHeader(Stream stream, AudioFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        var fmt = BuildFmtChunk(format);
        var hasFact = format.Encoding != AudioSampleEncoding.Pcm;

        Span<byte> u32 = stackalloc byte[4];
        WriteId(stream, "RIFF");
        WriteUInt32(stream, 0, u32);
        WriteId(stream, "WAVE");

        WriteId(stream, "fmt ");
        WriteUInt32(stream, (uint)fmt.Length, u32);
        stream.Write(fmt);

        long factOffset = -1;
        if (hasFact)
        {
            WriteId(stream, "fact");
            WriteUInt32(stream, 4, u32);
            factOffset = stream.Position;
            WriteUInt32(stream, 0, u32);
        }

        WriteId(stream, "data");
        var dataSizeOffset = stream.Position;
        WriteUInt32(stream, 0, u32);
        return new WavHeaderLayout(factOffset, dataSizeOffset, stream.Position);
    }

    /// <summary>Writes the final RIFF, fact and data sizes. Leaves the stream position at the end of the data.</summary>
    public static void PatchSizes(Stream stream, WavHeaderLayout layout, AudioFormatInfo format, long dataBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegative(dataBytes);

        var riffSize = layout.DataOffset - 8 + dataBytes + (dataBytes & 1);
        if (riffSize > uint.MaxValue) throw new InvalidOperationException("WAV data exceeds the 4 GiB RIFF limit.");

        Span<byte> u32 = stackalloc byte[4];
        if ((dataBytes & 1) == 1)
        {
            stream.Position = layout.DataOffset + dataBytes;
            stream.WriteByte(0); // RIFF pad byte for odd-sized chunks
        }

        stream.Position = 4;
        WriteUInt32(stream, (uint)riffSize, u32);
        if (layout.FactSampleCountOffset >= 0)
        {
            stream.Position = layout.FactSampleCountOffset;
            WriteUInt32(stream, (uint)(dataBytes / format.BlockAlign), u32);
        }
        stream.Position = layout.DataSizeOffset;
        WriteUInt32(stream, (uint)dataBytes, u32);
        stream.Position = layout.DataOffset + dataBytes;
    }

    /// <summary>Parses the header up to the start of the data chunk.</summary>
    /// <exception cref="InvalidDataException">The stream is not a supported WAV file.</exception>
    public static WavInfo ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> head = stackalloc byte[12];
        ReadExactly(stream, head);
        if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8)) throw new InvalidDataException("Not a RIFF/WAVE file.");

        AudioFormatInfo? format = null;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (true)
        {
            ReadExactly(stream, chunkHeader);
            var id = chunkHeader[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            if (id.SequenceEqual("fmt "u8))
            {
                if (size is < 16 or > 1024) throw new InvalidDataException($"Unexpected fmt chunk size {size}.");
                var fmt = new byte[size];
                ReadExactly(stream, fmt);
                format = ParseFmtChunk(fmt);
                if ((size & 1) == 1) stream.Position++;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format is null) throw new InvalidDataException("data chunk precedes fmt chunk.");
                return new WavInfo(format, stream.Position, size);
            }
            else
            {
                stream.Position += size + (size & 1);
                if (stream.Position > stream.Length) throw new InvalidDataException("Chunk extends past end of file.");
            }
        }
    }

    private static byte[] BuildFmtChunk(AudioFormatInfo f)
    {
        var size = f.Extensible ? 40 : f.Encoding == AudioSampleEncoding.Pcm ? 16 : 18;
        var fmt = new byte[size];
        var tag = f.Extensible ? FormatExtensible : f.Encoding == AudioSampleEncoding.Pcm ? FormatPcm : FormatIeeeFloat;
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)f.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)f.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)f.AverageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)f.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)f.BitsPerSample);
        if (size >= 18) BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), (ushort)(size - 18)); // cbSize
        if (f.Extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(18), (ushort)(f.ValidBitsPerSample == 0 ? f.BitsPerSample : f.ValidBitsPerSample));
            BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(20), f.ChannelMask);
            (f.Encoding == AudioSampleEncoding.Pcm ? PcmSubFormat : FloatSubFormat).TryWriteBytes(fmt.AsSpan(24));
        }
        return fmt;
    }

    private static AudioFormatInfo ParseFmtChunk(ReadOnlySpan<byte> fmt)
    {
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

        AudioFormatInfo format;
        switch (tag)
        {
            case FormatPcm:
                format = new AudioFormatInfo { SampleRate = rate, Channels = channels, BitsPerSample = bits, Encoding = AudioSampleEncoding.Pcm };
                break;
            case FormatIeeeFloat:
                format = new AudioFormatInfo { SampleRate = rate, Channels = channels, BitsPerSample = bits, Encoding = AudioSampleEncoding.IeeeFloat };
                break;
            case FormatExtensible when fmt.Length >= 40:
                var sub = new Guid(fmt.Slice(24, 16));
                var encoding = sub == PcmSubFormat ? AudioSampleEncoding.Pcm
                    : sub == FloatSubFormat ? AudioSampleEncoding.IeeeFloat
                    : throw new InvalidDataException($"Unsupported extensible sub-format {sub}.");
                format = new AudioFormatInfo
                {
                    SampleRate = rate,
                    Channels = channels,
                    BitsPerSample = bits,
                    Encoding = encoding,
                    Extensible = true,
                    ValidBitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[18..]),
                    ChannelMask = BinaryPrimitives.ReadUInt32LittleEndian(fmt[20..]),
                };
                break;
            default:
                throw new InvalidDataException($"Unsupported WAV format tag 0x{tag:X4}.");
        }

        try { format.Validate(); }
        catch (ArgumentException ex) { throw new InvalidDataException("Invalid WAV format.", ex); }
        if (format.BlockAlign != blockAlign) throw new InvalidDataException($"Block align {blockAlign} does not match format ({format.BlockAlign}).");
        return format;
    }

    private static void WriteId(Stream s, string id) => s.Write(Encoding.ASCII.GetBytes(id));

    private static void WriteUInt32(Stream s, uint value, Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        s.Write(buffer);
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        try { s.ReadExactly(buffer); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Unexpected end of WAV header.", ex); }
    }
}
