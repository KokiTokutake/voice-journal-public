namespace VoiceJournal.Core.Audio;

/// <summary>Sample encoding of an audio stream as stored in a WAV file.</summary>
public enum AudioSampleEncoding
{
    Pcm,
    IeeeFloat,
}

/// <summary>
/// Audio format of a captured stream, independent of NAudio. Captured audio is stored exactly in
/// this format (detail spec 6.2: float stays float, no lossy conversion).
/// </summary>
public sealed record AudioFormatInfo
{
    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required int BitsPerSample { get; init; }

    public required AudioSampleEncoding Encoding { get; init; }

    /// <summary>Write the format as WAVE_FORMAT_EXTENSIBLE (what WASAPI reports for its mix format).</summary>
    public bool Extensible { get; init; }

    /// <summary>Valid bits per sample for extensible formats; 0 means the same as <see cref="BitsPerSample"/>.</summary>
    public int ValidBitsPerSample { get; init; }

    /// <summary>Speaker position mask for extensible formats; 0 means unspecified.</summary>
    public uint ChannelMask { get; init; }

    public int BlockAlign => Channels * (BitsPerSample / 8);

    public int AverageBytesPerSecond => SampleRate * BlockAlign;

    public static AudioFormatInfo Pcm16Bit(int sampleRate, int channels) => new()
    {
        SampleRate = sampleRate,
        Channels = channels,
        BitsPerSample = 16,
        Encoding = AudioSampleEncoding.Pcm,
    };

    public static AudioFormatInfo IeeeFloat32Bit(int sampleRate, int channels, bool extensible = true) => new()
    {
        SampleRate = sampleRate,
        Channels = channels,
        BitsPerSample = 32,
        Encoding = AudioSampleEncoding.IeeeFloat,
        Extensible = extensible,
        ValidBitsPerSample = extensible ? 32 : 0,
        ChannelMask = extensible ? DefaultChannelMask(channels) : 0,
    };

    /// <summary>Throws when the combination cannot be stored as a WAV stream.</summary>
    public void Validate()
    {
        if (SampleRate is < 1000 or > 768_000) throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, "Unsupported sample rate.");
        if (Channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(Channels), Channels, "Unsupported channel count.");
        if (BitsPerSample % 8 != 0 || BitsPerSample is < 8 or > 64) throw new ArgumentOutOfRangeException(nameof(BitsPerSample), BitsPerSample, "Bits per sample must be a whole number of bytes.");
        if (Encoding == AudioSampleEncoding.IeeeFloat && BitsPerSample is not (32 or 64)) throw new ArgumentException("IEEE float must be 32 or 64 bit.", nameof(BitsPerSample));
        if (ValidBitsPerSample < 0 || ValidBitsPerSample > BitsPerSample) throw new ArgumentOutOfRangeException(nameof(ValidBitsPerSample), ValidBitsPerSample, "Valid bits exceed container size.");
        if (!Extensible && (ValidBitsPerSample != 0 && ValidBitsPerSample != BitsPerSample || ChannelMask != 0)) throw new ArgumentException("Valid bits and channel mask require an extensible format.", nameof(Extensible));
    }

    private static uint DefaultChannelMask(int channels) => channels switch
    {
        1 => 0x4,  // FRONT_CENTER
        2 => 0x3,  // FRONT_LEFT | FRONT_RIGHT
        _ => 0,
    };
}
