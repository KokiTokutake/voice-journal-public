using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceJournal.Core.Transcription;

/// <summary>
/// The fixed normalization for CER (detail spec 22.1, plan 3.8). Applied to reference and hypothesis alike:
/// <list type="number">
/// <item>Unicode NFKC (full-width digits and letters become ASCII, half-width kana become full-width)</item>
/// <item>Kanji numbers become Arabic digits: with unit characters by value (二十五 → 25, 三千 → 3000),
/// otherwise digit by digit (二〇二六 → 2026). Applied everywhere, idioms included (一緒 → 1緒), so both sides
/// change the same way and 一人 / 1人 compare equal</item>
/// <item>Punctuation, symbols, spaces and control characters are removed; ASCII letters are lower-cased</item>
/// </list>
/// </summary>
public static partial class CerNormalizer
{
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Normalize(NormalizationForm.FormKC);
        s = KanjiNumber().Replace(s, m => KanjiToNumber(m.Value));
        var result = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol)
                continue;
            result.Append(rune.IsAscii ? Rune.ToLowerInvariant(rune).ToString() : rune.ToString());
        }
        return result.ToString();
    }

    [GeneratedRegex("[〇零一二三四五六七八九十百千万億兆]+")]
    private static partial Regex KanjiNumber();

    private static int DigitOf(char c) => "〇一二三四五六七八九".IndexOf(c, StringComparison.Ordinal) is var i and >= 0 ? i : c == '零' ? 0 : -1;

    internal static string KanjiToNumber(string kanji)
    {
        if (kanji.All(c => DigitOf(c) >= 0))
            return string.Concat(kanji.Select(c => DigitOf(c).ToString(CultureInfo.InvariantCulture)));

        // Positional: 億・万 split groups of up to 9999 built from 千・百・十 and digits.
        decimal total = 0, group = 0, current = 0;
        var hasCurrent = false;
        foreach (var c in kanji)
        {
            var digit = DigitOf(c);
            if (digit >= 0)
            {
                current = current * 10 + digit;
                hasCurrent = true;
                continue;
            }
            var small = c switch { '十' => 10m, '百' => 100m, '千' => 1000m, _ => 0m };
            if (small > 0)
            {
                group += (hasCurrent ? current : 1) * small;
            }
            else
            {
                var big = c switch { '万' => 10_000m, '億' => 100_000_000m, _ => 1_000_000_000_000m };
                group += hasCurrent ? current : 0;
                total += (group == 0 ? 1 : group) * big;
                group = 0;
            }
            current = 0;
            hasCurrent = false;
        }
        total += group + (hasCurrent ? current : 0);
        return total.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Edit-distance result between a reference and a hypothesis, in normalized characters.</summary>
public sealed record CerResult(int ReferenceLength, int Substitutions, int Deletions, int Insertions)
{
    public int Errors => Substitutions + Deletions + Insertions;

    /// <summary>Errors divided by reference length (can exceed 1 with many insertions).</summary>
    public double Rate => ReferenceLength == 0 ? (Errors == 0 ? 0 : 1) : (double)Errors / ReferenceLength;
}

public static class CharacterErrorRate
{
    /// <summary>CER of <paramref name="hypothesis"/> against <paramref name="reference"/> after <see cref="CerNormalizer"/>.</summary>
    public static CerResult Compute(string reference, string hypothesis)
    {
        var r = CerNormalizer.Normalize(reference).EnumerateRunes().ToArray();
        var h = CerNormalizer.Normalize(hypothesis).EnumerateRunes().ToArray();

        // Two rows of (cost, S, D, I); ties prefer substitution, then deletion, then insertion.
        var prev = new (int Cost, int S, int D, int I)[h.Length + 1];
        var cur = new (int Cost, int S, int D, int I)[h.Length + 1];
        for (var j = 0; j <= h.Length; j++) prev[j] = (j, 0, 0, j);
        for (var i = 1; i <= r.Length; i++)
        {
            cur[0] = (i, 0, i, 0);
            for (var j = 1; j <= h.Length; j++)
            {
                var diag = prev[j - 1];
                (int Cost, int S, int D, int I) best = r[i - 1] == h[j - 1] ? diag : (diag.Cost + 1, diag.S + 1, diag.D, diag.I);
                var up = prev[j];
                if (up.Cost + 1 < best.Cost) best = (up.Cost + 1, up.S, up.D + 1, up.I);
                var left = cur[j - 1];
                if (left.Cost + 1 < best.Cost) best = (left.Cost + 1, left.S, left.D, left.I + 1);
                cur[j] = best;
            }
            (prev, cur) = (cur, prev);
        }
        var end = prev[h.Length];
        return new CerResult(r.Length, end.S, end.D, end.I);
    }
}
