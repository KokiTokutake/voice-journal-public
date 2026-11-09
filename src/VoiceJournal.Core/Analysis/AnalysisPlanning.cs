using System.Globalization;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Core.Analysis;

/// <summary>
/// Conservative token count for text sent to the model (detail spec 9.2: never take Japanese characters as tokens
/// without a margin). One token per CJK or kana character and about three ASCII characters per token, times a
/// factor learned from <c>usage.prompt_tokens</c> of earlier requests.
/// </summary>
public sealed class TokenEstimator
{
    public TokenEstimator(double factor = 1.0)
    {
        if (!(factor > 0) || double.IsInfinity(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        Factor = factor;
    }

    public double Factor { get; }

    public int Estimate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        double tokens = 0;
        foreach (var c in text)
        {
            if (char.IsLowSurrogate(c)) continue;
            tokens += c < 0x80 ? (char.IsLetterOrDigit(c) ? 0.34 : 0.5) : 1.0;
        }
        return (int)Math.Ceiling(tokens * Factor);
    }

    /// <summary>A new factor from one request: never below the measured ratio, never above 3.</summary>
    public static double Calibrate(int estimated, int actual) =>
        estimated <= 0 || actual <= 0 ? 1.0 : Math.Clamp((double)actual / estimated * 1.1, 0.3, 3.0);
}

/// <summary>Context budget (detail spec 9.2): input + overhead (system, schema, session) + output reserve ≤ 0.8 × C.</summary>
public sealed record ContextBudget(int ContextLength, int OutputReserve, int OverheadTokens)
{
    public const double UsableRatio = 0.8;

    public int InputTokens => (int)Math.Floor(UsableRatio * ContextLength) - OutputReserve - OverheadTokens;

    /// <exception cref="InvalidOperationException">Nothing is left for the transcript (LLM-002: shorten the reserve or load a longer context).</exception>
    public int RequireInputTokens(int minimum)
    {
        var input = InputTokens;
        if (input < minimum)
            throw new InvalidOperationException($"コンテキスト長 {ContextLength} では入力に {input} トークンしか使えません（出力予約 {OutputReserve}、固定分 {OverheadTokens}）。");
        return input;
    }
}

public enum CoverageStatus
{
    /// <summary>Sent to the model in a piece.</summary>
    Included,

    /// <summary>No text.</summary>
    Empty,

    /// <summary>Flagged as silence (a likely recognizer hallucination); not given to the model.</summary>
    Silence,

    /// <summary>A microphone segment flagged as echo of the PC audio; the PC audio's segment is given instead.</summary>
    Echo,
}

/// <summary>A segment that is not part of any piece, and why.</summary>
public sealed record SkippedSegment(Guid SegmentId, CoverageStatus Status);

/// <summary>One request's worth of consecutive segments. Short ids restart at S0001 in every piece.</summary>
public sealed record AnalysisPiece
{
    public required int Index { get; init; }

    public required IReadOnlyList<Guid> SegmentIds { get; init; }

    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public required int EstimatedTokens { get; init; }

    /// <summary>A single segment longer than the budget: it cannot be sent and the piece fails (partial result).</summary>
    public bool Oversize { get; init; }

    public static string ShortId(int position) => "S" + (position + 1).ToString("0000", CultureInfo.InvariantCulture);
}

/// <summary>Coverage ledger (detail spec 9.2): every segment of the revision is in exactly one piece or skipped with a reason.</summary>
public sealed record AnalysisPlan
{
    public required Guid SessionId { get; init; }

    public required int TranscriptRevision { get; init; }

    public required int InputTokenBudget { get; init; }

    public required IReadOnlyList<AnalysisPiece> Pieces { get; init; }

    public required IReadOnlyList<SkippedSegment> Skipped { get; init; }

    /// <summary>No segment worth analysing (silence, nothing recognized): the job ends as "not analysable" (16.1).</summary>
    public bool IsEmpty => Pieces.Count == 0;
}

public static class AnalysisPlanner
{
    /// <summary>Tokens of the JSON around one segment (<c>{"id":"S0001","time":"00:01:10","source":"mic","text":""},</c>).</summary>
    public const int SegmentOverheadTokens = 24;

    public static AnalysisPlan Plan(TranscriptDocument transcript, int inputTokenBudget, TokenEstimator estimator)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputTokenBudget, SegmentOverheadTokens + 1);
        var pieces = new List<AnalysisPiece>();
        var skipped = new List<SkippedSegment>();
        var current = new List<TranscriptSegment>();
        var tokens = 0;

        void Flush(bool oversize = false)
        {
            if (current.Count == 0) return;
            pieces.Add(new AnalysisPiece
            {
                Index = pieces.Count + 1,
                SegmentIds = [.. current.Select(s => s.SegmentId)],
                StartMs = current.Min(s => s.StartMs),
                EndMs = current.Max(s => s.EndMs),
                EstimatedTokens = tokens,
                Oversize = oversize,
            });
            current.Clear();
            tokens = 0;
        }

        foreach (var segment in transcript.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                skipped.Add(new(segment.SegmentId, CoverageStatus.Empty));
                continue;
            }
            if (segment.Has(SegmentQuality.Silence))
            {
                skipped.Add(new(segment.SegmentId, CoverageStatus.Silence));
                continue;
            }
            if (segment.Has(SegmentQuality.Echo))
            {
                skipped.Add(new(segment.SegmentId, CoverageStatus.Echo));
                continue;
            }
            var cost = estimator.Estimate(segment.Text.Trim()) + SegmentOverheadTokens;
            if (cost > inputTokenBudget)
            {
                Flush();
                current.Add(segment);
                tokens = cost;
                Flush(oversize: true);
                continue;
            }
            if (tokens + cost > inputTokenBudget) Flush();
            current.Add(segment);
            tokens += cost;
        }
        Flush();
        return new AnalysisPlan
        {
            SessionId = transcript.SessionId,
            TranscriptRevision = transcript.Revision,
            InputTokenBudget = inputTokenBudget,
            Pieces = pieces,
            Skipped = skipped,
        };
    }

    /// <summary>The model's input for one piece: segments with short ids, in transcript order.</summary>
    public static ExtractionInput BuildInput(AnalysisPiece piece, TranscriptDocument transcript, WireSession session)
    {
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(transcript);
        var byId = transcript.Segments.ToDictionary(s => s.SegmentId);
        var segments = new List<WireSegment>(piece.SegmentIds.Count);
        for (var i = 0; i < piece.SegmentIds.Count; i++)
        {
            var s = byId[piece.SegmentIds[i]];
            segments.Add(new WireSegment
            {
                Id = AnalysisPiece.ShortId(i),
                Time = TranscriptRendering.Time(s.StartMs, false),
                Source = s.Source == AudioSource.Loopback ? "loopback" : "mic",
                Text = s.Text.Trim(),
            });
        }
        return new ExtractionInput { Session = session, Segments = segments };
    }

    public static WireSession Session(DateTimeOffset startedAtUtc, TimeZoneInfo zone, string? title)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(startedAtUtc, zone);
        return new WireSession
        {
            RecordedAt = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            Weekday = RelativeDates.WeekdayName(DateOnly.FromDateTime(local.DateTime)),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
        };
    }
}
