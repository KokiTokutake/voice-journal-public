using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceJournal.Core.Analysis;

/// <summary>
/// Limits on a JSON answer before it is parsed into types (detail spec 16.2-1): at most 5 MiB, depth 32, no
/// duplicate keys, a single value. System.Text.Json would otherwise keep the last duplicate silently.
/// </summary>
public static class JsonGuard
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public const int MaxDepth = 32;

    /// <summary>Returns null when the text passes, otherwise a short reason (no content).</summary>
    public static string? Check(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxBytes) return $"応答が大きすぎます（{utf8.Length} バイト、上限 {MaxBytes}）";
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxDepth + 1, CommentHandling = JsonCommentHandling.Disallow });
        var keys = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        keys.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        keys.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        keys.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (!keys.Peek()!.Add(reader.GetString()!)) return "同じキーが重複しています";
                        break;
                }
                if (reader.CurrentDepth >= MaxDepth) return $"入れ子が深すぎます（上限 {MaxDepth}）";
            }
        }
        catch (JsonException)
        {
            return "JSON として読めません";
        }
        return reader.BytesConsumed == 0 ? "空の応答です" : null;
    }

    public static string? Check(string text) => Check(Encoding.UTF8.GetBytes(text));
}

/// <summary>Text comparison for evidence quotes (plan 3.5): NFKC and whitespace only, so a paraphrase never matches.</summary>
public static partial class EvidenceText
{
    /// <summary>Quotes shorter than this (after normalization) prove nothing, unless they are the whole segment.</summary>
    public const int MinQuoteLength = 4;

    private const string EdgePunctuation = "「」『』\"'“”‘’。、，．,.・…";

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) builder.Append(c);
        return builder.ToString();
    }

    /// <summary>Whether <paramref name="quote"/> is a substring of <paramref name="segmentText"/>; quote marks at its ends are ignored.</summary>
    public static bool IsQuoteOf(string quote, string segmentText)
    {
        var q = Normalize(quote).Trim(EdgePunctuation.ToCharArray());
        var s = Normalize(segmentText);
        if (q.Length == 0) return false;
        if (q.Length < MinQuoteLength && q.Length < s.Trim(EdgePunctuation.ToCharArray()).Length) return false;
        return s.Contains(q, StringComparison.Ordinal);
    }

    /// <summary>Hiragana at the end of a quote that may differ from the segment (「表示できる」 for 「表示できます」).</summary>
    public const int MaxEndingDifference = 2;

    /// <summary>What must remain of a quote, after its ending is cut, for it to still prove something.</summary>
    public const int MinQuoteLengthWithoutEnding = 8;

    /// <summary>
    /// The quote to keep as evidence, or null when <paramref name="quote"/> is not in <paramref name="segmentText"/>.
    /// A quote that differs only in up to <see cref="MaxEndingDifference"/> hiragana at its end is kept without them
    /// (D5, 2026-11-07: the model wrote 「…検索の手順を表示できる」 for 「…表示できます」 and the key point was lost),
    /// as long as <see cref="MinQuoteLengthWithoutEnding"/> characters remain and neither the cut ending nor what follows
    /// in the segment is a negation (「できない」 for 「できます」 is not the same thing). What is kept is always a
    /// substring of the segment, so <see cref="IsQuoteOf"/> (the validator) stays strict.
    /// </summary>
    public static string? MatchQuote(string quote, string segmentText)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(segmentText);
        var trimmed = quote.Trim();
        if (IsQuoteOf(trimmed, segmentText)) return trimmed;
        var body = trimmed.TrimEnd([.. EdgePunctuation, ' ', '　']);
        var segment = Normalize(segmentText);
        for (var cut = 1; cut <= MaxEndingDifference && cut < body.Length; cut++)
        {
            var ending = body[^cut..];
            if (!ending.All(IsHiragana)) break;
            var kept = body[..^cut];
            var key = Normalize(kept);
            if (key.Length < MinQuoteLengthWithoutEnding) break;
            var at = segment.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) continue;
            // The end of what is kept joined to what follows: 「できま」+「せん」.
            var from = Math.Max(at, at + key.Length - 3);
            var around = segment[from..Math.Min(segment.Length, at + key.Length + 5)];
            if (Negation().IsMatch(ending) || Negation().IsMatch(around)) return null;
            return kept;
        }
        return null;
    }

    private static bool IsHiragana(char c) => c is >= 'ぁ' and <= 'ゟ';

    [GeneratedRegex("ない|なかっ|なく|ません|ず|ぬ")]
    private static partial Regex Negation();

    /// <summary>Equality used for titles and names: NFKC, trimmed, runs of spaces collapsed, ASCII case ignored (detail spec 12.2-3).</summary>
    public static string NormalizeTitle(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        }
        return builder.ToString();
    }
}

/// <summary>
/// Words that make a statement worth checking against the audio (UI-06, 13.2): numbers, dates, negation, names.
/// The STT misreads these (午前→午後, 月額→合計; Phase C report 3), and they decide due dates and assignees.
/// </summary>
public static partial class ReviewHints
{
    [GeneratedRegex(@"[0-9０-９]|[〇一二三四五六七八九十百千万]+(?:時|分|日|月|年|円|人|件|個|回|割|％|%)")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"来週|今週|再来週|先週|明日|明後日|昨日|月末|週末|期限|締め切り|まで(?:に)?|[月火水木金土日]曜")]
    private static partial Regex DatePattern();

    // ず[、。] is 「行かず、」, not 「まず、」「必ず、」.
    [GeneratedRegex(@"ない|ません|なかった|(?<![ま必])ず[、。]|ではなく|じゃなく|しないで|やめ")]
    private static partial Regex NegationPattern();

    // 「送らないといけません」「出さなければならない」「やらなきゃ」: an obligation, not a negation.
    // Only the obligation itself is removed, so 「送らないといけないわけではありません」 keeps its negation.
    [GeneratedRegex(@"(?:ないと|なければ|なくては|なくちゃ)(?:いけ|なら)(?:ない|ません|ぬ)|(?:ないと|なくては|なくちゃ)(?:だめ|ダメ|困)|なきゃ")]
    private static partial Regex ObligationPattern();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}{1,4}(?:さん|様|氏|部長|課長)")]
    private static partial Regex PersonPattern();

    [GeneratedRegex(@"かも|たぶん|多分|おそらく|恐らく|できれば|できたら|ようか|ましょうか|[?？]")]
    private static partial Regex DoubtPattern();

    [GeneratedRegex(@"未定|予定|目安|頃|ごろ|くらい|ぐらい|程度|前後")]
    private static partial Regex ApproximatePattern();

    public static bool HasNumber(string text) => NumberPattern().IsMatch(text);

    public static bool HasDate(string text) => DatePattern().IsMatch(text);

    public static bool HasNegation(string text) => NegationPattern().IsMatch(WithoutObligation(text));

    /// <summary>The text with obligations (「送らないといけません」) blanked out, so their ない is not read as a negation.</summary>
    public static string WithoutObligation(string text) => ObligationPattern().Replace(text, " ");

    public static bool HasPerson(string text) => PersonPattern().IsMatch(text);

    /// <summary>Doubt or a mere suggestion: 「金曜かも」「聞いてみようか」. Such a statement is never a confirmed fact.</summary>
    public static bool HasDoubt(string text) => DoubtPattern().IsMatch(text);

    /// <summary>A suggestion still to be agreed on (「お願いしようか」「聞いてみましょうか」): it names nobody as assignee.</summary>
    public static bool HasSuggestion(string text) => SuggestionPattern().IsMatch(text);

    [GeneratedRegex(@"ようか|ましょうか")]
    private static partial Regex SuggestionPattern();

    /// <summary>Doubt, or a date that is only approximate or planned (「目安は金曜」「15日頃」): not a due date to record as fixed.</summary>
    public static bool HasHedge(string text) => HasDoubt(text) || ApproximatePattern().IsMatch(text);

    /// <summary>Labels joined with ・ (数字・期日・否定・人名), empty when nothing needs checking.</summary>
    public static string For(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hints = new List<string>(4);
        if (HasNumber(text)) hints.Add("数字");
        if (HasDate(text)) hints.Add("期日");
        if (HasNegation(text)) hints.Add("否定");
        if (HasPerson(text)) hints.Add("人名");
        return string.Join("・", hints);
    }
}
