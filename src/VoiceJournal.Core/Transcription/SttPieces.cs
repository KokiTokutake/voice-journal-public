namespace VoiceJournal.Core.Transcription;

/// <summary>
/// One recognition piece of a source track. The recognizer sees [<see cref="StartMs"/>, <see cref="EndMs"/>);
/// the piece owns [<see cref="PrimaryStartMs"/>, <see cref="PrimaryEndMs"/>) — the audio range minus the overlap.
/// </summary>
public sealed record SttPiece(int Index, long StartMs, long EndMs, long PrimaryStartMs, long PrimaryEndMs)
{
    public long DurationMs => EndMs - StartMs;
}

/// <summary>Recognized segments of one piece, already shifted to session time.</summary>
public sealed record SttPieceResult(SttPiece Piece, IReadOnlyList<TranscriptSegment> Segments);

public static class SttPiecePlanner
{
    /// <summary>
    /// Splits a timeline of <paramref name="durationMs"/> into pieces of <paramref name="pieceMs"/> with
    /// <paramref name="overlapMs"/> of extra audio on each inner side (detail spec 8.1: 30 minutes, 2 seconds).
    /// </summary>
    public static IReadOnlyList<SttPiece> Plan(long durationMs, long pieceMs, long overlapMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pieceMs);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlapMs * 2, pieceMs);
        if (durationMs <= 0) return [];
        var pieces = new List<SttPiece>();
        for (long start = 0, index = 1; start < durationMs; start += pieceMs, index++)
        {
            var primaryEnd = Math.Min(start + pieceMs, durationMs);
            pieces.Add(new SttPiece((int)index, Math.Max(0, start - overlapMs), Math.Min(durationMs, primaryEnd + overlapMs), start, primaryEnd));
        }
        return pieces;
    }
}

/// <summary>Joins the pieces of one source into one segment list (detail spec 8.1).</summary>
public static class SttPieceMerger
{
    /// <summary>
    /// A segment that ends (or starts) within this distance of its piece's audio edge is treated as possibly
    /// cut by the split. whisper aligns segment ends to its 10 ms frames, so a few frames of slack are needed.
    /// </summary>
    public const long CutToleranceMs = 250;

    /// <summary>
    /// Keeps each segment whose midpoint lies in its piece's primary range. When a segment next to an inner
    /// boundary looks cut, the overlapping segments of both pieces are all kept and flagged
    /// <see cref="SegmentQuality.Boundary"/>: an uncertain duplicate is never removed by string matching.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Merge(IReadOnlyList<SttPieceResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var ordered = results.OrderBy(r => r.Piece.Index).ToList();
        var kept = new List<TranscriptSegment>();
        var flagged = new HashSet<Guid>();

        foreach (var result in ordered)
            kept.AddRange(result.Segments.Where(s => Midpoint(s) >= result.Piece.PrimaryStartMs && Midpoint(s) < result.Piece.PrimaryEndMs));

        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var left = ordered[i];
            var right = ordered[i + 1];
            var boundary = left.Piece.PrimaryEndMs;
            // Segments that touch the edge of the audio the recognizer saw, on the inner side.
            var cut = left.Segments.Where(s => s.EndMs >= left.Piece.EndMs - CutToleranceMs)
                .Concat(right.Segments.Where(s => s.StartMs <= right.Piece.StartMs + CutToleranceMs))
                .ToList();
            if (cut.Count == 0) continue;
            var from = Math.Min(cut.Min(s => s.StartMs), boundary);
            var to = Math.Max(cut.Max(s => s.EndMs), boundary);
            foreach (var s in left.Segments.Concat(right.Segments))
            {
                if (s.StartMs >= to || s.EndMs <= from) continue;
                flagged.Add(s.SegmentId);
                if (!kept.Any(k => k.SegmentId == s.SegmentId)) kept.Add(s);
            }
        }

        return TranscriptDocument.Order(kept.Select(s => flagged.Contains(s.SegmentId) ? s.WithFlag(SegmentQuality.Boundary) : s));
    }

    private static double Midpoint(TranscriptSegment s) => (s.StartMs + s.EndMs) / 2.0;
}
