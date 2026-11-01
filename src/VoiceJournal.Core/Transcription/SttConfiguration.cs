using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VoiceJournal.Core.Transcription;

/// <summary>
/// Recognition settings for one job. A snapshot is taken when the job is queued (detail spec 18.2 UI-12:
/// changes apply from the next job). <see cref="ComputeHash"/> covers only what can change the output.
/// </summary>
public sealed record SttConfiguration
{
    /// <summary>Model file name registered in the settings (e.g. <c>ggml-small.bin</c>).</summary>
    public required string ModelName { get; init; }

    /// <summary>Recognition language; <c>auto</c> lets the recognizer detect it.</summary>
    public string Language { get; init; } = "ja";

    public bool UseVad { get; init; } = true;

    public double VadThreshold { get; init; } = 0.5;

    public int VadMinSilenceMs { get; init; } = 100;

    public int VadSpeechPadMs { get; init; } = 30;

    public int BeamSize { get; init; } = 5;

    public int BestOf { get; init; } = 5;

    /// <summary>Gain applied when making the 16 kHz input: off, or peak to −3 dBFS with a cap (plan 3.3).</summary>
    public bool NormalizeGain { get; init; } = true;

    public double MaxGainDb { get; init; } = 30;

    /// <summary>Length of one recognition piece and the overlap on each side (detail spec 8.1).</summary>
    public long PieceMs { get; init; } = 30 * 60_000;

    public long OverlapMs { get; init; } = 2_000;

    /// <summary>CPU threads. Recorded, but not part of the hash: it changes speed, not the text.</summary>
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>The settings as text, sorted by key, as stored in <see cref="SttProvenance.Parameters"/>.</summary>
    public IReadOnlyDictionary<string, string> ToParameters()
    {
        var c = CultureInfo.InvariantCulture;
        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = ModelName,
            ["language"] = Language,
            ["vad"] = UseVad ? "on" : "off",
            ["vad_threshold"] = VadThreshold.ToString("0.###", c),
            ["vad_min_silence_ms"] = VadMinSilenceMs.ToString(c),
            ["vad_speech_pad_ms"] = VadSpeechPadMs.ToString(c),
            ["beam_size"] = BeamSize.ToString(c),
            ["best_of"] = BestOf.ToString(c),
            ["normalize_gain"] = NormalizeGain ? "on" : "off",
            ["max_gain_db"] = MaxGainDb.ToString("0.###", c),
            ["piece_ms"] = PieceMs.ToString(c),
            ["overlap_ms"] = OverlapMs.ToString(c),
            ["threads"] = Threads.ToString(c),
        };
    }

    /// <summary>Lower-case SHA-256 of the output-relevant settings (everything except <c>threads</c>).</summary>
    public string ComputeHash()
    {
        var text = new StringBuilder();
        foreach (var (key, value) in ToParameters())
        {
            if (key == "threads") continue;
            text.Append(key).Append('=').Append(value).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
