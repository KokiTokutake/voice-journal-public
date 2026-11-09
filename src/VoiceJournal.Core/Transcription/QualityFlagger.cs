using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Core.Transcription;

/// <summary>What is known about one source track's audio, for flagging segments.</summary>
public sealed record TrackQualityContext
{
    /// <summary>Ranges known to be digital silence (loopback silence filled in while nothing played).</summary>
    public IReadOnlyList<TimeRange> SilentRanges { get; init; } = [];

    /// <summary>Ranges where audio is missing or may be missing.</summary>
    public IReadOnlyList<TimeRange> GapRanges { get; init; } = [];

    /// <summary>Optional: RMS level (dBFS) of the STT input over a range, or null when unknown.</summary>
    public Func<long, long, double?>? LevelDbfs { get; init; }

    private static readonly AudioEventKind[] LossKinds = [AudioEventKind.QueueOverflow, AudioEventKind.WriteFailed, AudioEventKind.Discontinuity];

    /// <summary>
    /// Builds the context from the manifest: filled loopback silence; and as gaps, every part of
    /// [0, <paramref name="sessionDurationMs"/>) not covered by a saved chunk, loss events, and the tail after
    /// an interruption.
    /// </summary>
    public static TrackQualityContext FromTrack(TrackManifest track, long sessionDurationMs)
    {
        ArgumentNullException.ThrowIfNull(track);
        var silent = track.Events.Where(e => e.Kind == AudioEventKind.LoopbackSilence && e.EndMs is not null)
            .Select(e => new TimeRange(e.StartMs, e.EndMs!.Value)).ToList();

        var gaps = new List<TimeRange>();
        var covered = track.Chunks.Where(c => c.State is ChunkState.Completed or ChunkState.Recovered)
            .Select(c => new TimeRange(c.StartMs, c.StartMs + c.FrameCount * 1000 / Math.Max(1, track.Format.SampleRate)))
            .OrderBy(r => r.StartMs).ToList();
        long cursor = 0;
        foreach (var r in covered)
        {
            // Small gaps are rounding between chunk boundaries, not missing audio.
            if (r.StartMs - cursor > 50) gaps.Add(new TimeRange(cursor, r.StartMs));
            cursor = Math.Max(cursor, r.EndMs);
        }
        if (sessionDurationMs - cursor > 50) gaps.Add(new TimeRange(cursor, sessionDurationMs));
        foreach (var e in track.Events)
        {
            if (Array.IndexOf(LossKinds, e.Kind) >= 0) gaps.Add(new TimeRange(e.StartMs, Math.Max(e.StartMs + 1, e.EndMs ?? e.StartMs + 1)));
            else if (e.Kind == AudioEventKind.Interrupted && e.StartMs < sessionDurationMs) gaps.Add(new TimeRange(e.StartMs, sessionDurationMs));
        }
        return new TrackQualityContext { SilentRanges = silent, GapRanges = gaps };
    }
}

/// <summary>Adds quality flags (detail spec 8.2). Flags only point at text to check; nothing is removed.</summary>
public static class QualityFlagger
{
    public const long TooShortMs = 300;

    /// <summary>Share of a segment lying in known silence that makes it suspicious.</summary>
    public const double SilentShare = 0.8;

    /// <summary>Below this RMS level (dBFS) the input is treated as silence.</summary>
    public const double SilenceDbfs = -60;

    public static IReadOnlyList<TranscriptSegment> Apply(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyDictionary<AudioSource, TrackQualityContext> tracks)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(tracks);
        var result = new List<TranscriptSegment>(segments.Count);
        foreach (var s in segments)
        {
            var flagged = s;
            if (s.DurationMs < TooShortMs) flagged = flagged.WithFlag(SegmentQuality.TooShort);
            if (IsRepetitive(s.Text)) flagged = flagged.WithFlag(SegmentQuality.Repetition);
            if (tracks.TryGetValue(s.Source, out var track))
            {
                var silentMs = track.SilentRanges.Sum(r => r.OverlapMs(s.StartMs, s.EndMs));
                var quiet = s.DurationMs > 0 && silentMs >= s.DurationMs * SilentShare;
                if (!quiet && track.LevelDbfs?.Invoke(s.StartMs, s.EndMs) is { } level && level < SilenceDbfs) quiet = true;
                if (quiet) flagged = flagged.WithFlag(SegmentQuality.Silence);
                if (track.GapRanges.Any(r => r.Overlaps(s.StartMs, s.EndMs))) flagged = flagged.WithFlag(SegmentQuality.AudioGap);
            }
            result.Add(flagged);
        }

        // The same text three or more times in a row on one source is a typical hallucination loop.
        foreach (var group in result.Select((s, i) => (s, i)).GroupBy(x => x.s.Source))
        {
            var run = new List<int>();
            string? previous = null;
            foreach (var (s, i) in group)
            {
                var key = CerNormalizer.Normalize(s.Text);
                if (key.Length >= 4 && key == previous) run.Add(i);
                else
                {
                    FlagRun(result, run);
                    run = [i];
                }
                previous = key;
            }
            FlagRun(result, run);
        }
        FlagEcho(result, result);
        return result;
    }

    /// <summary>
    /// <see cref="SegmentQuality.Echo"/> for microphone segments recognized again on their own (a range), judged
    /// against <paramref name="pcAudio"/>, the PC audio segments of the revision they go into.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> WithEcho(IReadOnlyList<TranscriptSegment> segments, IEnumerable<TranscriptSegment> pcAudio)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(pcAudio);
        var result = segments.ToList();
        FlagEcho(result, pcAudio);
        return result;
    }

    /// <summary>How far around a microphone segment the PC audio is searched for the same words (clock skew and latency).</summary>
    public const long EchoWindowMs = 1_500;

    /// <summary>
    /// Share of a microphone segment's character pairs found in the nearby PC audio that marks it as echo. On a
    /// real one-hour meeting through speakers the shares split into ~0 (the user's own voice) and ≥ 0.6 (echo);
    /// the few in between are mixed, and replies that reuse the other side's words land there, so they are kept.
    /// </summary>
    public const double EchoShare = 0.6;

    private static void FlagEcho(List<TranscriptSegment> segments, IEnumerable<TranscriptSegment> pcAudio)
    {
        var loopback = pcAudio.Where(s => s.Source == AudioSource.Loopback && !s.Has(SegmentQuality.Silence))
            .Select(s => (s.StartMs, s.EndMs, Text: CerNormalizer.Normalize(s.Text)))
            .Where(s => s.Text.Length > 0).OrderBy(s => s.StartMs).ToList();
        if (loopback.Count == 0) return;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.Source != AudioSource.Mic) continue;
            var near = string.Concat(loopback.Where(l => l.EndMs >= s.StartMs - EchoWindowMs && l.StartMs <= s.EndMs + EchoWindowMs).Select(l => l.Text));
            if (SharedPairShare(CerNormalizer.Normalize(s.Text), near) >= EchoShare) segments[i] = s.WithFlag(SegmentQuality.Echo);
        }
    }

    /// <summary>The share of <paramref name="text"/>'s adjacent character pairs (the text itself when it is one character) that occur in <paramref name="other"/>.</summary>
    public static double SharedPairShare(string text, string other)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(other);
        if (text.Length == 0 || other.Length == 0) return 0;
        if (text.Length == 1) return other.Contains(text[0], StringComparison.Ordinal) ? 1 : 0;
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < other.Length; i++) pairs.Add(other.Substring(i, 2));
        var found = 0;
        for (var i = 0; i + 1 < text.Length; i++)
            if (pairs.Contains(text.Substring(i, 2))) found++;
        return (double)found / (text.Length - 1);
    }

    private static void FlagRun(List<TranscriptSegment> segments, List<int> run)
    {
        if (run.Count < 3) return;
        foreach (var i in run) segments[i] = segments[i].WithFlag(SegmentQuality.Repetition);
    }

    /// <summary>
    /// True when a unit of 2–20 characters repeats four or more times back to back (or a single character
    /// eight times), covering at least half of the text.
    /// </summary>
    public static bool IsRepetitive(string text)
    {
        var t = CerNormalizer.Normalize(text);
        if (t.Length < 8) return false;
        for (var unit = 1; unit <= 20 && unit * 4 <= t.Length; unit++)
        {
            var needed = unit == 1 ? 8 : 4;
            for (var start = 0; start + unit * needed <= t.Length; start++)
            {
                var count = 1;
                var pos = start + unit;
                while (pos + unit <= t.Length && string.CompareOrdinal(t, start, t, pos, unit) == 0)
                {
                    count++;
                    pos += unit;
                }
                if (count >= needed && count * unit * 2 >= t.Length) return true;
            }
        }
        return false;
    }
}
