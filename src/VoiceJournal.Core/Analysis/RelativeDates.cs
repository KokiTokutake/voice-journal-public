using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Core.Analysis;

public enum DateResolutionKind
{
    /// <summary>One calendar date, on or after the session date.</summary>
    Resolved,

    /// <summary>Several dates fit (「金曜」「来週中」「15日」 said after the 15th): the user decides.</summary>
    Ambiguous,

    /// <summary>A date is named but doubted or only planned (「金曜かも」「目安は」).</summary>
    Hedged,

    /// <summary>The date is before the session: not a due date.</summary>
    Past,

    /// <summary>No date expression the app understands.</summary>
    Unparsed,
}

/// <param name="Date">The date when one is named (also for Hedged and Past, as a hint); null otherwise.</param>
public sealed record DateResolution(DateResolutionKind Kind, DateOnly? Date)
{
    /// <summary>The value a candidate may carry as its due date: only an unambiguous, undoubted, future date.</summary>
    public DateOnly? DueDate => Kind == DateResolutionKind.Resolved ? Date : null;
}

/// <summary>
/// Reads due-date expressions relative to the session's local date (detail spec 9.3). The LLM's date is never
/// taken on its own; when this cannot name one date, the due date stays null and the candidate is flagged.
/// Weeks start on Monday (今週・来週).
/// </summary>
public static partial class RelativeDates
{
    private const string Weekdays = "月火水木金土日";

    public static string WeekdayName(DateOnly date) => Weekdays[((int)date.DayOfWeek + 6) % 7] + "曜日";

    public static DateResolution Resolve(string? expression, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(expression)) return new(DateResolutionKind.Unparsed, null);
        var text = Prepare(expression);
        var result = ResolveCore(text, today);
        if (result.Kind == DateResolutionKind.Resolved && result.Date < today) result = result with { Kind = DateResolutionKind.Past };
        if (ReviewHints.HasHedge(expression) && result.Kind is DateResolutionKind.Resolved or DateResolutionKind.Ambiguous)
            result = result with { Kind = DateResolutionKind.Hedged };
        return result;
    }

    private static DateResolution ResolveCore(string s, DateOnly today)
    {
        if (FullDate().Match(s) is { Success: true } full)
            return Exact(Int(full, "y"), Int(full, "m"), Int(full, "d"));

        if (MonthDay().Match(s) is { Success: true } md)
        {
            var (month, day) = (Int(md, "m"), Int(md, "d"));
            if (TryDate(today.Year, month, day) is not { } thisYear) return new(DateResolutionKind.Unparsed, null);
            if (thisYear >= today) return new(DateResolutionKind.Resolved, thisYear);
            // 1月10日 said in late December means next January, 10月20日 said in November is a past reference;
            // 4月3日 said in November could be either (next April, or last spring), so the user decides.
            if (TryDate(today.Year + 1, month, day) is { } next && next.DayNumber - today.DayNumber <= 92) return new(DateResolutionKind.Resolved, next);
            return today.DayNumber - thisYear.DayNumber <= 92 ? new(DateResolutionKind.Past, thisYear) : new(DateResolutionKind.Ambiguous, null);
        }

        if (NextMonthDay().Match(s) is { Success: true } nmd)
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
            return TryDate(first.Year, first.Month, Int(nmd, "d")) is { } d ? new(DateResolutionKind.Resolved, d) : new(DateResolutionKind.Unparsed, null);
        }

        if (MonthEnd().Match(s) is { Success: true } end)
        {
            var offset = end.Groups["w"].Value switch { "来月" => 1, "再来月" => 2, _ => 0 };
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(offset + 1);
            return new(DateResolutionKind.Resolved, first.AddDays(-1));
        }

        if (WeekDay().Match(s) is { Success: true } wd)
        {
            var weeks = wd.Groups["w"].Value switch { "今週" => 0, "来週" => 1, "再来週" => 2, _ => -1 };
            var index = Weekdays.IndexOf(wd.Groups["d"].Value[0], StringComparison.Ordinal);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            if (weeks < 0) return new(DateResolutionKind.Ambiguous, null); // 「金曜」alone: this week or next
            return new(DateResolutionKind.Resolved, monday.AddDays(7 * weeks + index));
        }

        if (After().Match(s) is { Success: true } after)
        {
            var n = Int(after, "n");
            return after.Groups["u"].Value switch
            {
                "日" => new(DateResolutionKind.Resolved, today.AddDays(n)),
                "週間" => new(DateResolutionKind.Resolved, today.AddDays(7 * n)),
                _ => new(DateResolutionKind.Ambiguous, null), // N か月後: the day is not said
            };
        }

        if (DayOnly().Match(s) is { Success: true } dayOnly)
        {
            var day = Int(dayOnly, "d");
            if (day >= today.Day && TryDate(today.Year, today.Month, day) is { } d) return new(DateResolutionKind.Resolved, d);
            return new(DateResolutionKind.Ambiguous, null); // this month has passed it: next month, or a slip?
        }

        if (s.Contains("明後日", StringComparison.Ordinal) || s.Contains("あさって", StringComparison.Ordinal)) return new(DateResolutionKind.Resolved, today.AddDays(2));
        if (s.Contains("明日", StringComparison.Ordinal) || s.Contains("あした", StringComparison.Ordinal)) return new(DateResolutionKind.Resolved, today.AddDays(1));
        if (s.Contains("今日", StringComparison.Ordinal) || s.Contains("本日", StringComparison.Ordinal)) return new(DateResolutionKind.Resolved, today);
        if (Vague().IsMatch(s)) return new(DateResolutionKind.Ambiguous, null);
        return new(DateResolutionKind.Unparsed, null);
    }

    private static DateResolution Exact(int year, int month, int day) =>
        TryDate(year, month, day) is { } d ? new(DateResolutionKind.Resolved, d) : new(DateResolutionKind.Unparsed, null);

    private static DateOnly? TryDate(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;

    private static int Int(Match m, string group) => int.Parse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

    // NFKC (全角数字), kanji numerals to digits, no spaces.
    private static string Prepare(string expression)
    {
        var s = expression.Normalize(NormalizationForm.FormKC);
        s = KanjiNumber().Replace(s, m => CerNormalizer.KanjiToNumber(m.Value));
        var builder = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) builder.Append(c);
        return builder.ToString();
    }

    [GeneratedRegex("[〇一二三四五六七八九十]+(?=[年月日週か])")]
    private static partial Regex KanjiNumber();

    [GeneratedRegex(@"(?<y>\d{4})[年/\-](?<m>\d{1,2})[月/\-](?<d>\d{1,2})日?")]
    private static partial Regex FullDate();

    [GeneratedRegex(@"(?<!\d)(?<m>\d{1,2})(?:月|/)(?<d>\d{1,2})(?:日|(?!\d))")]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"来月の?(?<d>\d{1,2})日")]
    private static partial Regex NextMonthDay();

    // 今月末・来月末・月末. Not 週末 or 年末.
    [GeneratedRegex(@"(?:(?<w>再来月|来月|今月)|月)末")]
    private static partial Regex MonthEnd();

    [GeneratedRegex(@"(?<w>再来週|来週|今週)?の?(?<d>[月火水木金土日])曜")]
    private static partial Regex WeekDay();

    [GeneratedRegex(@"(?<n>\d{1,3})(?<u>日|週間|か月|ヶ月|ヵ月)後")]
    private static partial Regex After();

    [GeneratedRegex(@"(?<!\d|月)(?<d>\d{1,2})日")]
    private static partial Regex DayOnly();

    [GeneratedRegex("来週|今週|再来週|週明け|週末|来月|今月|月内|年内|年度内|今度|近いうち|早め|そのうち|いずれ|後で|あとで")]
    private static partial Regex Vague();
}
