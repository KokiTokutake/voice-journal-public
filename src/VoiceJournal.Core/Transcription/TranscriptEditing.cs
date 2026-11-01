using VoiceJournal.Core.Capture;

namespace VoiceJournal.Core.Transcription;

/// <summary>
/// Builds the next (edited) revision from a parent revision (detail spec 7, 8.2). The parent is never changed:
/// every method returns a new document whose <see cref="TranscriptDocument.Revision"/> is <c>revision</c>.
/// </summary>
public static class TranscriptEditing
{
    /// <summary>Changes the text of one segment. The segment keeps its id; it gets <see cref="SegmentQuality.UserEdited"/>.</summary>
    public static TranscriptDocument EditText(TranscriptDocument parent, int revision, Guid segmentId, string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(text);
        var target = parent.Find(segmentId) ?? throw new ArgumentException($"Segment {segmentId} is not in revision {parent.Revision}.", nameof(segmentId));
        var edited = target with
        {
            Text = text,
            QualityFlags = [.. target.QualityFlags.Where(f => f != SegmentQuality.UserEdited), SegmentQuality.UserEdited],
            OriginSegmentIds = target.Origins,
        };
        var change = new TranscriptChange { Kind = TranscriptChangeKind.TextEdited, SegmentIds = [segmentId], NewSegmentIds = [segmentId] };
        return Next(parent, revision, now, parent.Segments.Select(s => s.SegmentId == segmentId ? edited : s), change);
    }

    /// <summary>
    /// Puts the raw text back for <paramref name="segmentId"/>: the segment, and any other segment derived from the
    /// same raw segments, is replaced by those raw segments. This is a new revision, not an undo.
    /// </summary>
    public static TranscriptDocument RevertToRaw(TranscriptDocument parent, TranscriptDocument raw, int revision, Guid segmentId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Kind != TranscriptRevisionKind.Raw || raw.Revision != parent.RawRevision)
            throw new ArgumentException($"Revision {raw.Revision} is not the raw revision of revision {parent.Revision}.", nameof(raw));
        var target = parent.Find(segmentId) ?? throw new ArgumentException($"Segment {segmentId} is not in revision {parent.Revision}.", nameof(segmentId));
        var origins = target.Origins.ToHashSet();
        var replaced = parent.Segments.Where(s => s.Origins.Any(origins.Contains)).ToList();
        // Pull in everything that shares an origin with the replaced segments, so no raw text appears twice.
        foreach (var s in replaced) origins.UnionWith(s.Origins);
        replaced = [.. parent.Segments.Where(s => s.Origins.Any(origins.Contains))];
        var restored = raw.Segments.Where(s => origins.Contains(s.SegmentId)).ToList();
        if (restored.Count == 0) throw new InvalidOperationException($"The raw revision has none of the origins of segment {segmentId}.");

        var replacedIds = replaced.Select(s => s.SegmentId).ToHashSet();
        var change = new TranscriptChange
        {
            Kind = TranscriptChangeKind.RevertedToOriginal,
            SegmentIds = [.. replacedIds],
            NewSegmentIds = [.. restored.Select(s => s.SegmentId)],
        };
        return Next(parent, revision, now, parent.Segments.Where(s => !replacedIds.Contains(s.SegmentId)).Concat(restored), change);
    }

    /// <summary>
    /// Replaces the segments of <paramref name="source"/> that overlap <paramref name="range"/> with a new recognition
    /// of that range (AT-07). New segments get new ids, remember the raw segments they replace, and are flagged
    /// <see cref="SegmentQuality.UserEdited"/>. <paramref name="recognized"/> must lie within the range.
    /// </summary>
    public static TranscriptDocument ReplaceRange(
        TranscriptDocument parent, int revision, AudioSource source, TimeRange range,
        IReadOnlyList<TranscriptSegment> recognized, SttProvenance provenance, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(recognized);
        ArgumentNullException.ThrowIfNull(provenance);
        if (range.DurationMs <= 0) throw new ArgumentException("The range is empty.", nameof(range));
        if (recognized.Any(s => s.Source != source || s.StartMs < range.StartMs || s.EndMs > range.EndMs))
            throw new ArgumentException("Recognized segments must be of the same source and inside the range.", nameof(recognized));

        var replaced = parent.Segments.Where(s => s.Source == source && range.Overlaps(s.StartMs, s.EndMs)).ToList();
        var origins = replaced.SelectMany(s => s.Origins).Distinct().ToList();
        var fresh = recognized.Select(s => s with
        {
            SegmentId = Guid.CreateVersion7(),
            OriginSegmentIds = origins,
            QualityFlags = [.. s.QualityFlags.Where(f => f != SegmentQuality.UserEdited), SegmentQuality.UserEdited],
        }).ToList();

        var replacedIds = replaced.Select(s => s.SegmentId).ToHashSet();
        var change = new TranscriptChange
        {
            Kind = TranscriptChangeKind.ReRecognized,
            SegmentIds = [.. replacedIds],
            NewSegmentIds = [.. fresh.Select(s => s.SegmentId)],
            Range = range,
            Source = source,
            Provenance = provenance,
        };
        return Next(parent, revision, now, parent.Segments.Where(s => !replacedIds.Contains(s.SegmentId)).Concat(fresh), change);
    }

    private static TranscriptDocument Next(TranscriptDocument parent, int revision, DateTimeOffset now, IEnumerable<TranscriptSegment> segments, TranscriptChange change)
    {
        if (revision <= parent.Revision) throw new ArgumentOutOfRangeException(nameof(revision), revision, "The new revision must come after its parent.");
        return new TranscriptDocument
        {
            SessionId = parent.SessionId,
            Revision = revision,
            Kind = TranscriptRevisionKind.Edited,
            ParentRevision = parent.Revision,
            RawRevision = parent.RawRevision,
            CreatedAtUtc = now,
            Changes = [change],
            Segments = TranscriptDocument.Order(segments),
        };
    }
}
