using System.Globalization;
using System.Text.RegularExpressions;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Core.Analysis;

/// <summary>Why a candidate needs the user's eye before anything is done with it (detail spec 12, 13.2, 16.2-3).</summary>
public enum CandidateConcern
{
    /// <summary>Doubt or a suggestion in the evidence (「かも」「しようか」): certainty lowered to tentative.</summary>
    Doubtful,

    /// <summary>The evidence contains a negation; check that the candidate does not reverse it.</summary>
    Negation,

    /// <summary>A due date was mentioned but could not be fixed to one date (ambiguous, hedged, past, or not in the evidence).</summary>
    DueDateNeedsCheck,

    /// <summary>
    /// The model's date differed from the app's reading or had nothing to support it; it was dropped. Only in analyses
    /// made before prompt 2026-11-05.1: since then the model writes no date and the app reads the expression alone.
    /// </summary>
    DueDateDropped,

    /// <summary>The assignee is not named in the evidence; it was dropped.</summary>
    AssigneeDropped,

    /// <summary>The model proposed done / resolved / closed / cancelled: individual confirmation required.</summary>
    TerminalStatus,

    /// <summary>The model proposed a finished status against a negation (「完了していない」); it was dropped.</summary>
    StatusDropped,

    /// <summary>The evidence has numbers, dates or names that the recognizer often gets wrong.</summary>
    CheckAudio,

    /// <summary>Merged with a candidate of the same kind and title from another piece.</summary>
    Merged,

    /// <summary>
    /// The assignee or the due date was found in the segment just before or after the evidence (same source), not in
    /// it: the model quoted the problem but not the sentence that named the person or the date.
    /// </summary>
    NearbyEvidence,
}

/// <summary>Something the model said that could not be tied to the transcript: shown as 「根拠確認が必要」, never approvable.</summary>
public sealed record DroppedItem
{
    public required string Kind { get; init; }

    public required int Piece { get; init; }

    public required string Text { get; init; }

    public required string Reason { get; init; }
}

/// <summary>A candidate before ids are assigned. <see cref="Fields"/> already holds only what the transcript supports.</summary>
public sealed record DraftCandidate
{
    public required CandidateKind Kind { get; init; }

    public required CandidateFields Fields { get; init; }

    public required Certainty Certainty { get; init; }

    public required string? DueExpression { get; init; }

    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public required string Rationale { get; init; }

    public required IReadOnlyList<CandidateConcern> Flags { get; init; }

    public required int Piece { get; init; }
}

/// <summary>Stage-1 result of one piece, in segment UUIDs.</summary>
public sealed record PieceAnalysis
{
    public required int Piece { get; init; }

    public required IReadOnlyList<Statement> SummaryPoints { get; init; }

    public required IReadOnlyList<Section> Topics { get; init; }

    public required IReadOnlyList<DraftCandidate> Candidates { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required IReadOnlyList<DroppedItem> Dropped { get; init; }

    /// <summary>Evidence entries removed (unknown segment or a quote not in the text).</summary>
    public required int DroppedEvidence { get; init; }
}

/// <summary>A record built by an integration step (an intermediate one, or the final one).</summary>
public sealed record IntegratedRecord
{
    public required IReadOnlyList<RecordType> RecordTypes { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<Statement> Summary { get; init; }

    public required IReadOnlyList<Section> Sections { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required IReadOnlyList<DroppedItem> Dropped { get; init; }
}

/// <summary>Notes on one final candidate, stored next to the analysis (the schema has no place for them).</summary>
public sealed record CandidateNote(string CandidateId, IReadOnlyList<CandidateConcern> Flags);

/// <summary>Turns stage-1 answers into <see cref="PieceAnalysis"/>: short ids back to segments, evidence verified (16.2-2).</summary>
public static partial class ExtractionConverter
{
    /// <summary>Headings the record renderer uses itself (detail spec 16.2-8: no reserved heading twice).</summary>
    public static readonly IReadOnlySet<string> ReservedHeadings = new HashSet<string>(StringComparer.Ordinal) { "要約", "課題・タスク候補", "関連資料" };

    /// <summary>The reason shown for a candidate left out because the evidence decides not to do it.</summary>
    public const string RefusedReason = "やらないと決めたことなので、候補にしません";

    /// <summary>The reason shown for a candidate left out because its evidence is an instruction to the AI or the app.</summary>
    public const string InjectedReason = "文字起こしの中の、AI やアプリへの命令なので、候補にしません";

    /// <summary>The reason shown for a candidate left out because its evidence only says a reply is awaited.</summary>
    public const string WaitingReason = "他の人からの返事を待っているだけなので、候補にしません";

    public static PieceAnalysis Convert(ExtractionOutput output, AnalysisPiece piece, TranscriptDocument transcript, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(transcript);
        var segments = piece.SegmentIds.Select(id => transcript.Find(id) ?? throw new ArgumentException($"Segment {id} is not in the transcript.", nameof(transcript))).ToList();
        var dropped = new List<DroppedItem>();
        var droppedEvidence = 0;

        IReadOnlyList<Evidence> Evidence(IReadOnlyList<WireEvidence> wire)
        {
            var list = new List<Evidence>();
            foreach (var e in wire)
            {
                var segment = Resolve(e.Segment, segments);
                if (segment is null || !EvidenceText.IsQuoteOf(e.Quote, segment.Text))
                {
                    droppedEvidence++;
                    continue;
                }
                var item = new Evidence { SegmentId = segment.SegmentId, Quote = e.Quote.Trim() };
                if (!list.Contains(item)) list.Add(item);
            }
            return list;
        }

        Statement? ToStatement(WireStatement s, string kind)
        {
            var text = Clean(s.Text);
            if (text.Length == 0) return null;
            var evidence = Evidence(s.Evidence);
            if (evidence.Count == 0)
            {
                dropped.Add(new DroppedItem { Kind = kind, Piece = piece.Index, Text = text, Reason = "根拠の区間または引用を確認できません" });
                return null;
            }
            return new Statement { Text = text, Certainty = s.Certainty, Evidence = evidence };
        }

        var summary = output.SummaryPoints.Select(s => ToStatement(s, "summary")).OfType<Statement>().ToList();
        var topics = new List<Section>();
        foreach (var topic in output.Topics)
        {
            var statements = topic.Statements.Select(s => ToStatement(s, "statement")).OfType<Statement>().ToList();
            if (statements.Count == 0) continue; // no empty sections (AT-08)
            topics.Add(new Section { SectionKind = topic.SectionKind, Heading = SafeHeading(topic.Heading), Statements = statements });
        }
        var candidates = new List<DraftCandidate>();
        foreach (var c in output.Candidates)
        {
            var evidence = Evidence(c.Evidence);
            var title = Clean(c.Title);
            if (evidence.Count == 0 || title.Length == 0)
            {
                dropped.Add(new DroppedItem { Kind = "candidate", Piece = piece.Index, Text = title, Reason = "根拠の区間または引用を確認できません" });
                continue;
            }
            // 「対応しないことにします」: what was decided not to do is a decision, not a candidate (2026-11-04, user).
            // It stays visible to the reviewer in the 「根拠確認が必要」 list.
            if (CandidateChecker.IsRefusal(evidence))
            {
                dropped.Add(new DroppedItem { Kind = "candidate", Piece = piece.Index, Text = title, Reason = RefusedReason });
                continue;
            }
            // AT-23: an instruction aimed at the AI or the app inside the transcript is never a candidate, even when
            // the model lists it (Gemma 4 E2B did, 2026-11-05). It stays visible in the 「根拠確認が必要」 list.
            if (CandidateChecker.IsInjectedCommand(evidence, transcript))
            {
                dropped.Add(new DroppedItem { Kind = "candidate", Piece = piece.Index, Text = title, Reason = InjectedReason });
                continue;
            }
            // 「まだ返事が来ていません」「回答を待っている状態です」: nobody has said they will do anything. Gemma 4 E2B
            // listed it although the prompt says not to (2026-11-05, tuning-noise). It stays visible for review.
            if (CandidateChecker.IsOnlyWaiting(evidence))
            {
                dropped.Add(new DroppedItem { Kind = "candidate", Piece = piece.Index, Text = title, Reason = WaitingReason });
                continue;
            }
            candidates.Add(CandidateChecker.Check(c, evidence, transcript, sessionDate, piece.Index));
        }
        return new PieceAnalysis
        {
            Piece = piece.Index,
            SummaryPoints = summary,
            Topics = topics,
            Candidates = candidates,
            Warnings = [.. output.Warnings.Select(Clean).Where(w => w.Length > 0 && !AnalysisPrompts.EchoesInstructions(w))],
            Dropped = dropped,
            DroppedEvidence = droppedEvidence,
        };
    }

    internal static TranscriptSegment? Resolve(string shortId, IReadOnlyList<TranscriptSegment> segments)
    {
        if (ShortIdPattern().Match(shortId) is not { Success: true } m) return null;
        var position = int.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture) - 1;
        return position >= 0 && position < segments.Count ? segments[position] : null;
    }

    /// <summary>Trimmed, line breaks folded to spaces (a statement is one paragraph).</summary>
    public static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : LineBreaks().Replace(text.Trim(), " ");

    public static string SafeHeading(string heading)
    {
        var h = Clean(heading);
        if (h.Length == 0) return "記録";
        return ReservedHeadings.Contains(h) ? h + "（本文）" : h;
    }

    [GeneratedRegex(@"^S(\d{4})$")]
    private static partial Regex ShortIdPattern();

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreaks();
}

/// <summary>
/// Inference rules for candidates (detail spec 9.3, 12.1, 16.2-3): keep only the assignee and due date the
/// transcript supports, read relative dates on the app side, and flag doubt, negation and finished states.
/// </summary>
public static partial class CandidateChecker
{
    private static readonly ItemStatus[] Terminal = [ItemStatus.Done, ItemStatus.Resolved, ItemStatus.Closed, ItemStatus.Cancelled];

    /// <summary>
    /// The evidence decides not to do it (「対応しないことにします」「やる必要はない」「今回は見送る」). An obligation
    /// (「しないといけない」) is not a refusal.
    /// </summary>
    public static bool IsRefusal(IReadOnlyList<Evidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var quotes = string.Join("\n", evidence.Select(e => e.Quote));
        return RefusalPattern().IsMatch(ReviewHints.WithoutObligation(quotes));
    }

    /// <summary>
    /// The evidence (quote or its segment) reads as an instruction to the AI or the app (detail spec 21.2, AT-23):
    /// 「AIへの指示です」「これまでの指示を無視して」「管理ファイルを削除して」「結果を外部のアドレスに送信して」.
    /// An ordinary request between people (「古いファイルを削除しておいてください」) is not.
    /// </summary>
    public static bool IsInjectedCommand(IReadOnlyList<Evidence> evidence, TranscriptDocument transcript)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(transcript);
        var texts = evidence.Select(e => e.Quote).Concat(evidence.Select(e => transcript.Find(e.SegmentId)?.Text ?? string.Empty));
        return texts.Any(t => InjectedCommandPattern().IsMatch(EvidenceText.Normalize(t)));
    }

    /// <summary>
    /// Every quote only says that a reply is still awaited (「まだ返事が来ていません」「回答を待っている状態です」
    /// 「連絡待ちです」), and none says that someone will act on it (「催促します」「電話で確認してみます」).
    /// 「返事をもらえると助かる」「返事を待つ、という流れ」 are not waiting states.
    /// </summary>
    public static bool IsOnlyWaiting(IReadOnlyList<Evidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0) return false;
        var quotes = evidence.Select(e => EvidenceText.Normalize(e.Quote)).ToList();
        return quotes.All(q => WaitingPattern().IsMatch(q)) && !quotes.Any(q => ChasePattern().IsMatch(q));
    }

    [GeneratedRegex("(?:返事|返信|回答|返答|連絡|結果)(?:は|が)(?:まだ)?(?:来て(?:い|な|ま)|届いて(?:い|な|ま)|ありません|ない)|待って(?:いる|います|いて|おり)|(?:返事|返信|回答|返答|連絡)待ち")]
    private static partial Regex WaitingPattern();

    [GeneratedRegex("催促|問い合わせ|聞いて(?:み|お)|確認し(?:ます|てみ|てお)|連絡し(?:ます|てみ|てお)|送り(?:ます|直)|電話し")]
    private static partial Regex ChasePattern();

    [GeneratedRegex("(?:AI|人工知能|アシスタント|システム)(?:へ|に)の?(?:指示|命令)|(?:これまで|以前|上記|前)の(?:指示|命令)を無視|管理ファイル.{0,10}(?:削除|消)|外部の?(?:アドレス|URL|サーバー|サイト).{0,6}送")]
    private static partial Regex InjectedCommandPattern();

    public static DraftCandidate Check(WireCandidate c, IReadOnlyList<Evidence> evidence, TranscriptDocument transcript, DateOnly sessionDate, int piece)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(transcript);
        var flags = new List<CandidateConcern>();
        var segmentTexts = evidence.Select(e => transcript.Find(e.SegmentId)!.Text).Distinct().ToList();
        // IsRefusal candidates never reach here from the converter; the certainty rule below stays for direct callers.
        var quotes = string.Join("\n", evidence.Select(e => e.Quote));
        var context = string.Join("\n", segmentTexts);

        var certainty = c.Certainty;
        var expression = string.IsNullOrWhiteSpace(c.DueExpression) ? null : ExtractionConverter.Clean(c.DueExpression);
        if (ReviewHints.HasDoubt(quotes) || (expression is not null && ReviewHints.HasDoubt(expression)))
        {
            flags.Add(CandidateConcern.Doubtful);
            if (certainty == Certainty.Stated) certainty = Certainty.Tentative;
        }
        var negation = ReviewHints.HasNegation(quotes);
        if (negation)
        {
            flags.Add(CandidateConcern.Negation);
            // 「やる必要はない」「対応しないことにします」: a candidate built on a refusal is not confirmed, whatever its
            // kind (AT-09: shown for checking). 「しないといけない」 is an obligation, not a refusal.
            if (RefusalPattern().IsMatch(ReviewHints.WithoutObligation(quotes))) certainty = Certainty.Unclear;
        }

        // The segments just before and after the evidence, same source. A 4B model often quotes the problem
        // (「添付ファイルの移行が未完了です」) but not the next sentence with the person and the date
        // (「山本さんが来週の水曜日までに終わらせます」). Taken from there only when that sentence is not a suggestion or a guess.
        var nearby = Neighbors(evidence, transcript)
            .Where(t => !ReviewHints.HasSuggestion(t) && !ReviewHints.HasHedge(t)).ToList();

        // Due date (detail spec 9.3): the app reads the words said; the model writes no date (a 4B model miscounts
        // weekdays). Only words found in the evidence segments (or next to them) and naming one date fix it.
        DateOnly? due = null;
        if (expression is not null)
        {
            var resolution = RelativeDates.Resolve(expression, sessionDate);
            var key = DateKey(expression);
            var supported = DateKey(context).Contains(key, StringComparison.Ordinal);
            var near = !supported && nearby.Any(t => DateKey(t).Contains(key, StringComparison.Ordinal));
            // 「十一月十六日の夜を予定しています」: the words are a date, the sentence only a plan or a guess.
            var hedged = segmentTexts.Any(t => DateKey(t).Contains(key, StringComparison.Ordinal) && ReviewHints.HasHedge(t));
            if ((supported || near) && !hedged && resolution.DueDate is { } d)
            {
                due = d;
                if (near) flags.Add(CandidateConcern.NearbyEvidence);
            }
            else
            {
                flags.Add(CandidateConcern.DueDateNeedsCheck);
            }
        }

        // Assignee: only a name that appears in the evidence segments (or next to them), and not in a mere suggestion
        // (「山口さんにお願いしようか」「佐藤さんに聞いてみようか」: nobody has agreed to do it; detail spec 9.3).
        string? assignee = null;
        if (!string.IsNullOrWhiteSpace(c.Assignee))
        {
            var name = ExtractionConverter.Clean(c.Assignee);
            var core = Honorific().Replace(name, string.Empty);
            var target = EvidenceText.Normalize(core);
            var usable = core.Length > 0 && !ReviewHints.HasSuggestion(quotes) && !NotAName().IsMatch(name);
            if (usable && (EvidenceText.Normalize(context).Contains(target, StringComparison.Ordinal) || IntroducedJustBefore(core, evidence, transcript)))
            {
                assignee = name;
            }
            else if (usable && nearby.Any(t => NamedAsDoer(EvidenceText.Normalize(t), target)))
            {
                assignee = name;
                if (!flags.Contains(CandidateConcern.NearbyEvidence)) flags.Add(CandidateConcern.NearbyEvidence);
            }
            else
            {
                flags.Add(CandidateConcern.AssigneeDropped);
            }
        }

        var status = c.Status;
        if (status is { } wrong && !AnalysisValidator.StatusAllowed(c.Kind, wrong))
        {
            status = null; // e.g. "done" for an issue: not a state an issue can have
            flags.Add(CandidateConcern.StatusDropped);
        }
        if (status is { } s && Array.IndexOf(Terminal, s) >= 0)
        {
            if (negation)
            {
                status = null;
                flags.Add(CandidateConcern.StatusDropped);
            }
            else
            {
                flags.Add(CandidateConcern.TerminalStatus);
            }
        }

        if (ReviewHints.HasNumber(context) || ReviewHints.HasDate(context) || ReviewHints.HasPerson(context)) flags.Add(CandidateConcern.CheckAudio);

        return new DraftCandidate
        {
            Kind = c.Kind,
            Fields = new CandidateFields
            {
                Title = ExtractionConverter.Clean(c.Title),
                Description = ExtractionConverter.Clean(c.Description),
                Status = status,
                Priority = c.Priority,
                Assignee = assignee,
                DueDate = due,
            },
            Certainty = certainty,
            DueExpression = expression,
            Evidence = evidence,
            Rationale = ExtractionConverter.Clean(c.Rationale) is { Length: > 0 } r ? r : "（理由の記載なし）",
            Flags = flags,
            Piece = piece,
        };
    }

    /// <summary>A real calendar date in <c>YYYY-MM-DD</c>, else null (2026-02-30 is not a date).</summary>
    public static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    // Loose key so 「来週の金曜日までに」 matches 「来週金曜までに」 in the transcript.
    private static string DateKey(string text)
    {
        // 「十一月二十日」 said, 「11月20日」 written by the model: compare in digits.
        var s = RelativeDates.KanjiDigits(EvidenceText.Normalize(text));
        s = WeekdayNote().Replace(s, string.Empty); // 「11月13日(金)」: the model adds the weekday, the speaker may not
        s = s.Replace("曜日", "曜", StringComparison.Ordinal).Replace("の", string.Empty, StringComparison.Ordinal);
        return DateSuffix().Replace(s, string.Empty);
    }

    [GeneratedRegex(@"[(（][月火水木金土日](?:曜日?)?[)）]|(?<=日)[月火水木金土日]曜日?")]
    private static partial Regex WeekdayNote();

    [GeneratedRegex("(?:までには|までに|まで|には|中|頃|ごろ)$")]
    private static partial Regex DateSuffix();

    [GeneratedRegex("(?:さん|様|さま|氏|くん|君|ちゃん|部長|課長|係長|社長|先生)$")]
    private static partial Regex Honorific();

    // 「不要」 only as a verdict (「不要です」), not 「不要品の処分」.
    // 「作らないことにします」「やらないと決めた」 as well as 「やらない」「しない」.
    [GeneratedRegex("必要は?ない|必要ありません|やらない|しない|ないことに(?:し|す|な)|ないと決め|やめ|不要(?:です|だ|と|に|で)|見送")]
    private static partial Regex RefusalPattern();

    /// <summary>
    /// In a neighbouring segment the name must be the one who acts (「山本さんが」「中村は」「木村さんお願いします」
    /// 「鈴木さんにお願いする」), not a mere mention (「伊藤さんの件ですが」).
    /// </summary>
    private static bool NamedAsDoer(string text, string name)
    {
        for (var at = text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
            if (DoerAfterName().IsMatch(text.AsSpan(at + name.Length).ToString())) return true;
        return false;
    }

    [GeneratedRegex("^(?:さん|様|さま|氏|くん|君)?(?:が|は|、?お願い|にお願い|に頼)")]
    private static partial Regex DoerAfterName();

    /// <summary>Texts of the segments right before and after each evidence segment, same source, not evidence themselves.</summary>
    private static List<string> Neighbors(IReadOnlyList<Evidence> evidence, TranscriptDocument transcript)
    {
        var segments = transcript.Segments;
        var ids = evidence.Select(e => e.SegmentId).ToHashSet();
        var result = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            if (!ids.Contains(segments[i].SegmentId)) continue;
            foreach (var j in new[] { i - 1, i + 1 })
                if (j >= 0 && j < segments.Count && segments[j].Source == segments[i].Source && !ids.Contains(segments[j].SegmentId))
                    result.Add(segments[j].Text);
        }
        return result;
    }

    /// <summary>
    /// The speaker named themself in one of the two segments before an evidence segment, from the same source
    /// (「伊藤です。…」 then 「今日中にプルリクエストを出します」 in a stand-up). Only this form counts, not any mention.
    /// </summary>
    private static bool IntroducedJustBefore(string name, IReadOnlyList<Evidence> evidence, TranscriptDocument transcript)
    {
        var target = EvidenceText.Normalize(name);
        foreach (var e in evidence)
        {
            var segments = transcript.Segments;
            var index = -1;
            for (var k = 0; k < segments.Count && index < 0; k++)
                if (segments[k].SegmentId == e.SegmentId) index = k;
            if (index < 0) continue;
            var source = segments[index].Source;
            for (var i = index - 1; i >= 0 && i >= index - 2; i--)
            {
                var before = transcript.Segments[i];
                if (before.Source != source) break;
                if (SelfIntroduction().Match(before.Text) is { Success: true } m && EvidenceText.Normalize(m.Groups["n"].Value) == target) return true;
                // 「木村さん、お願いします」: the chair gives the floor; the next segments are that person speaking.
                if (GivesTheFloor().Match(before.Text) is { Success: true } f && EvidenceText.Normalize(f.Groups["n"].Value) == target) return true;
            }
        }
        return false;
    }

    [GeneratedRegex(@"^(?:はい、?)?(?<n>\p{IsCJKUnifiedIdeographs}{1,4})(?:です|と申します)[。、]")]
    private static partial Regex SelfIntroduction();

    [GeneratedRegex(@"(?<n>\p{IsCJKUnifiedIdeographs}{1,4})(?:さん|様|さま|くん|君)、?(?:お願いします|どうぞ)[。！!]?$")]
    private static partial Regex GivesTheFloor();

    // 「皆さんも〜してください」 (a lecturer's advice) or 「私が」: words for people, not a name to put as assignee.
    [GeneratedRegex("^(?:皆さん|皆様|みなさん|みんな|全員|各自|誰か|だれか|私|わたし|自分|僕|俺|担当者|話者|不明|未定|なし|null)$")]
    private static partial Regex NotAName();
}

/// <summary>Stage 2: statements of the pieces as numbered items, and the model's integration back to evidence.</summary>
public static partial class IntegrationConverter
{
    public static string ItemId(int position) => "I" + (position + 1).ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>One item per statement: summary points first within each piece, then topic statements.</summary>
    public static IReadOnlyList<(WireItem Item, Statement Statement)> Items(IEnumerable<PieceAnalysis> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var list = new List<(SectionKind?, string?, Statement)>();
        foreach (var piece in pieces.OrderBy(p => p.Piece))
        {
            list.AddRange(piece.SummaryPoints.Select(s => ((SectionKind?)null, (string?)null, s)));
            foreach (var topic in piece.Topics) list.AddRange(topic.Statements.Select(s => ((SectionKind?)topic.SectionKind, (string?)topic.Heading, s)));
        }
        return Number(list);
    }

    /// <summary>Items of an intermediate record, for the next level of a hierarchical integration.</summary>
    public static IReadOnlyList<(WireItem Item, Statement Statement)> Items(IEnumerable<IntegratedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = new List<(SectionKind?, string?, Statement)>();
        foreach (var record in records)
        {
            list.AddRange(record.Summary.Select(s => ((SectionKind?)null, (string?)null, s)));
            foreach (var section in record.Sections) list.AddRange(section.Statements.Select(s => ((SectionKind?)section.SectionKind, (string?)section.Heading, s)));
        }
        return Number(list);
    }

    private static List<(WireItem, Statement)> Number(List<(SectionKind? Kind, string? Heading, Statement Statement)> list) =>
        [.. list.Select((x, i) => (new WireItem { Id = ItemId(i), SectionKind = x.Kind, Heading = x.Heading, Text = x.Statement.Text, Certainty = x.Statement.Certainty }, x.Statement))];

    /// <summary>Splits items so each group fits the budget (a hierarchical integration when there is more than one).</summary>
    public static IReadOnlyList<IReadOnlyList<(WireItem Item, Statement Statement)>> Group(
        IReadOnlyList<(WireItem Item, Statement Statement)> items, int inputTokenBudget, TokenEstimator estimator)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(estimator);
        var groups = new List<IReadOnlyList<(WireItem, Statement)>>();
        var current = new List<(WireItem, Statement)>();
        var tokens = 0;
        foreach (var item in items)
        {
            var cost = estimator.Estimate(item.Item.Text + (item.Item.Heading ?? string.Empty)) + 30;
            if (current.Count > 0 && tokens + cost > inputTokenBudget)
            {
                groups.Add(Renumber(current));
                current = [];
                tokens = 0;
            }
            current.Add(item);
            tokens += cost;
        }
        if (current.Count > 0) groups.Add(Renumber(current));
        return groups;
    }

    private static List<(WireItem, Statement)> Renumber(List<(WireItem Item, Statement Statement)> group) =>
        [.. group.Select((x, i) => (x.Item with { Id = ItemId(i) }, x.Statement))];

    public static IntegrationInput BuildInput(IReadOnlyList<(WireItem Item, Statement Statement)> items, WireSession session) =>
        new() { Session = session, Items = [.. items.Select(x => x.Item)] };

    /// <summary>
    /// Maps the model's statements back to evidence: the union of the evidence of the items in <c>from</c>.
    /// Certainty is never stronger than the weakest source. A statement with no valid source is dropped.
    /// </summary>
    public static IntegratedRecord Convert(IntegrationOutput output, IReadOnlyList<(WireItem Item, Statement Statement)> items, int level)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(items);
        var byId = items.ToDictionary(x => x.Item.Id, x => x.Statement, StringComparer.Ordinal);
        var dropped = new List<DroppedItem>();

        Statement? ToStatement(WireDerived d, string kind)
        {
            // The model sometimes writes the item ids into the text (「I0012 によって確認された」): they mean nothing to a reader.
            var text = ExtractionConverter.Clean(ItemIdInText().Replace(d.Text, string.Empty));
            if (text.Length == 0) return null;
            var sources = d.From.Distinct(StringComparer.Ordinal).Select(id => byId.GetValueOrDefault(id)).OfType<Statement>().ToList();
            if (sources.Count == 0)
            {
                dropped.Add(new DroppedItem { Kind = kind, Piece = 0, Text = text, Reason = $"統合（段 {level}）で元の要点を確認できません" });
                return null;
            }
            var weakest = sources.Max(s => s.Certainty);
            var evidence = sources.SelectMany(s => s.Evidence).Distinct().Take(30).ToList();
            return new Statement { Text = text, Certainty = (Certainty)Math.Max((int)d.Certainty, (int)weakest), Evidence = evidence };
        }

        var summary = output.Summary.Select(s => ToStatement(s, "summary")).OfType<Statement>().ToList();
        var sections = new List<Section>();
        foreach (var section in output.Sections)
        {
            var statements = section.Statements.Select(s => ToStatement(s, "statement")).OfType<Statement>().ToList();
            if (statements.Count == 0) continue;
            sections.Add(new Section { SectionKind = section.SectionKind, Heading = ExtractionConverter.SafeHeading(section.Heading), Statements = statements });
        }
        return new IntegratedRecord
        {
            RecordTypes = [.. output.RecordTypes.Distinct().Take(4)],
            Title = ExtractionConverter.Clean(output.Title),
            Summary = summary,
            Sections = sections,
            Warnings = [.. output.Warnings.Select(ExtractionConverter.Clean).Where(w => w.Length > 0 && !AnalysisPrompts.EchoesInstructions(w))],
            Dropped = dropped,
        };
    }

    // 「I0012」「(I0003, I0004)」 and a following 「によって確認された」.
    [GeneratedRegex(@"[(（]?\s*I\d{4}(?:\s*[、,・]\s*I\d{4})*\s*[)）]?(?:\s*(?:によって|により|で)確認(?:された|されている|済み)[。]?)?")]
    private static partial Regex ItemIdInText();
}

/// <summary>The final <see cref="AnalysisResult"/>: the integrated record plus the candidates of all pieces.</summary>
public static class AnalysisAssembler
{
    public const int MaxCandidates = 200;

    public static (AnalysisResult Analysis, IReadOnlyList<CandidateNote> Notes) Assemble(
        Guid sessionId, int transcriptRevision, IntegratedRecord record, IEnumerable<PieceAnalysis> pieces, IEnumerable<string> appWarnings)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(appWarnings);
        var pieceList = pieces.OrderBy(p => p.Piece).ToList();

        // Candidate ledger (9.2): kept from the pieces, never rewritten by the integration. Same kind + same
        // normalized title from different pieces is one candidate with the evidence of both.
        var merged = new List<DraftCandidate>();
        foreach (var c in pieceList.SelectMany(p => p.Candidates))
        {
            var key = EvidenceText.NormalizeTitle(c.Fields.Title);
            var index = merged.FindIndex(m => m.Kind == c.Kind && EvidenceText.NormalizeTitle(m.Fields.Title) == key);
            if (index < 0)
            {
                merged.Add(c);
                continue;
            }
            var m = merged[index];
            merged[index] = m with
            {
                Evidence = [.. m.Evidence.Concat(c.Evidence).Distinct().Take(30)],
                Certainty = (Certainty)Math.Max((int)m.Certainty, (int)c.Certainty),
                Flags = [.. m.Flags.Concat(c.Flags).Append(CandidateConcern.Merged).Distinct()],
            };
        }

        var warnings = new List<string>(appWarnings);
        if (merged.Count > MaxCandidates)
        {
            warnings.Add($"候補が多すぎるため、先頭の {MaxCandidates} 件だけを残しました（全 {merged.Count} 件）。");
            merged = merged.Take(MaxCandidates).ToList();
        }
        var candidates = new List<Candidate>(merged.Count);
        var notes = new List<CandidateNote>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var c = merged[i];
            var id = "C" + (i + 1).ToString("0000", CultureInfo.InvariantCulture);
            candidates.Add(new Candidate
            {
                CandidateId = id,
                Kind = c.Kind,
                Action = CandidateAction.Create, // Phase D: no existing items to update or mention
                SuggestedTargetId = null,
                ParentIssueCandidateId = null,
                ParentIssueItemId = null,
                Fields = c.Fields,
                ChangedFields = [],
                Certainty = c.Certainty,
                DueExpression = c.DueExpression,
                Evidence = c.Evidence,
                Rationale = c.Rationale,
            });
            notes.Add(new CandidateNote(id, c.Flags));
        }
        warnings.AddRange(pieceList.SelectMany(p => p.Warnings));
        warnings.AddRange(record.Warnings);

        // One section per heading (16.2-8): the model may name two sections alike; their statements are kept together.
        var sections = new List<Section>();
        foreach (var section in record.Sections)
        {
            var index = sections.FindIndex(s => s.Heading == section.Heading);
            if (index < 0) sections.Add(section);
            else sections[index] = sections[index] with { Statements = [.. sections[index].Statements, .. section.Statements] };
        }
        if (sections.Count > 30) warnings.Add($"章が多すぎるため、先頭の 30 章だけを残しました（全 {sections.Count} 章）。");
        sections = [.. sections.Take(30).Select(s => s.Statements.Count > 100 ? s with { Statements = [.. s.Statements.Take(100)] } : s)];
        var summary = record.Summary.Take(7).ToList();
        if (summary.Sum(s => s.Text.Length) > AnalysisValidator.MaxSummaryCharacters)
            warnings.Add($"要約が {AnalysisValidator.MaxSummaryCharacters} 字を超えています。レビューで短くしてください。");

        var analysis = new AnalysisResult
        {
            SessionId = sessionId,
            TranscriptRevision = transcriptRevision,
            RecordTypes = record.RecordTypes.Count > 0 ? record.RecordTypes : [RecordType.Other],
            Title = record.Title.Length > 0 ? Truncate(record.Title, 200) : "（題名なし）",
            Summary = summary,
            Sections = sections,
            Candidates = candidates,
            Warnings = [.. warnings.Select(w => Truncate(w, 1000)).Distinct().Take(100)],
        };
        return (analysis, notes);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
