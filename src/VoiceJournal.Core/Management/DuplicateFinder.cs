using System.Text;
using System.Text.RegularExpressions;
using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Core.Management;

/// <summary>
/// Similarity of a candidate to an existing item (detail spec 12.2-4, 5): character 2-gram Dice of the normalized titles,
/// the share of common keywords in the bodies, and whether the project is the same. A score, not a probability.
/// </summary>
public static partial class TitleSimilarity
{
    public const double TitleWeight = 0.65;
    public const double BodyWeight = 0.25;
    public const double ProjectWeight = 0.10;

    /// <summary>At or above: a strong suspicion of a duplicate (12.2-5, initial value).</summary>
    public const double Strong = 0.85;

    /// <summary>At or above (and below <see cref="Strong"/>): worth showing as a possible duplicate.</summary>
    public const double Check = 0.65;

    /// <summary>12.2-3: NFKC, trimmed, runs of spaces collapsed, ASCII case ignored. Negation, numbers and symbols stay.</summary>
    public static string Normalize(string title) => EvidenceText.NormalizeTitle(title);

    /// <summary>A title shorter than 2 characters (after normalization) cannot be compared: search by hand.</summary>
    public static bool IsAmbiguous(string title) => Normalize(title).Replace(" ", string.Empty, StringComparison.Ordinal).Length < 2;

    public static double Dice(string a, string b)
    {
        var x = Bigrams(Normalize(a));
        var y = Bigrams(Normalize(b));
        if (x.Count == 0 || y.Count == 0) return 0;
        var common = x.Count(y.Contains);
        return 2.0 * common / (x.Count + y.Count);
    }

    /// <summary>Jaccard of the keywords (runs of 2+ kanji, katakana, latin letters or digits) of two texts.</summary>
    public static double KeywordOverlap(string a, string b)
    {
        var x = Keywords(a);
        var y = Keywords(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var common = x.Count(y.Contains);
        return (double)common / (x.Count + y.Count - common);
    }

    public static double Score(string titleA, string bodyA, string projectA, string titleB, string bodyB, string projectB) =>
        (TitleWeight * Dice(titleA, titleB))
        + (BodyWeight * KeywordOverlap(titleA + "\n" + bodyA, titleB + "\n" + bodyB))
        + (ProjectWeight * (string.Equals(Normalize(projectA), Normalize(projectB), StringComparison.Ordinal) ? 1 : 0));

    private static HashSet<string> Bigrams(string text)
    {
        var t = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < t.Length; i++) set.Add(t.Substring(i, 2));
        return set;
    }

    internal static HashSet<string> Keywords(string text) =>
        Keyword().Matches(EvidenceText.NormalizeTitle(text ?? string.Empty)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}々〆]{2,}|[\p{IsKatakana}ー]{2,}|[a-z0-9][a-z0-9._\-]+")]
    private static partial Regex Keyword();
}

/// <summary>What the candidate names explicitly (12.2-1).</summary>
public enum ExplicitTargetState
{
    /// <summary>The candidate names no existing item.</summary>
    None,

    Found,

    /// <summary>The id is unknown in this workspace: an error candidate, never matched automatically.</summary>
    NotFound,

    /// <summary>The id is an item of the other kind: an error candidate.</summary>
    WrongKind,
}

/// <summary>How a similar item differs from the candidate (12.2-6): shown apart from the score.</summary>
public enum DuplicateDifference
{
    Assignee,
    DueDate,
    Status,
    Negation,
    Numbers,
}

public sealed record DuplicateMatch
{
    public required ManagementItem Item { get; init; }

    /// <summary>The score of 12.2-5 (0..1). Not the probability that both are the same.</summary>
    public required double Score { get; init; }

    public required bool ExactTitle { get; init; }

    public required bool Strong { get; init; }

    public required IReadOnlyList<DuplicateDifference> Differences { get; init; }
}

public sealed record DuplicateReport
{
    public required ExplicitTargetState Explicit { get; init; }

    public ManagementItem? ExplicitItem { get; init; }

    /// <summary>The candidate title is too short to compare: the user searches by hand.</summary>
    public required bool Ambiguous { get; init; }

    /// <summary>Up to <see cref="DuplicateFinder.MaxMatches"/> items, exact titles first, then by score.</summary>
    public required IReadOnlyList<DuplicateMatch> Matches { get; init; }
}

/// <summary>
/// Finds existing items a candidate may be (detail spec 12.2). Never decides: even an explicit id, an identical
/// title or a high score only proposes; the user chooses new, update, mention or skip.
/// </summary>
public static partial class DuplicateFinder
{
    public const int MaxMatches = 5;

    public static DuplicateReport Find(Candidate candidate, string project, IEnumerable<ManagementItem> items)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();

        var state = ExplicitTargetState.None;
        ManagementItem? target = null;
        if (candidate.SuggestedTargetId is { } id)
        {
            target = list.FirstOrDefault(i => i.Id == id);
            state = target is null ? ExplicitTargetState.NotFound
                : target.Kind != candidate.Kind ? ExplicitTargetState.WrongKind
                : ExplicitTargetState.Found;
            if (state != ExplicitTargetState.Found) target = null;
        }

        var title = candidate.Fields.Title;
        var ambiguous = TitleSimilarity.IsAmbiguous(title);
        var key = TitleSimilarity.Normalize(title);
        var matches = ambiguous ? [] : list
            .Where(i => i.Kind == candidate.Kind)
            .Select(i =>
            {
                var exact = string.Equals(TitleSimilarity.Normalize(i.Title), key, StringComparison.Ordinal);
                var score = TitleSimilarity.Score(title, candidate.Fields.Description, project, i.Title, i.Description, i.Project);
                return new DuplicateMatch
                {
                    Item = i,
                    Score = Math.Round(score, 3),
                    ExactTitle = exact,
                    Strong = exact || score >= TitleSimilarity.Strong,
                    Differences = Differences(candidate, i),
                };
            })
            .Where(m => m.ExactTitle || m.Score >= TitleSimilarity.Check)
            .OrderByDescending(m => m.ExactTitle).ThenByDescending(m => m.Score).ThenBy(m => m.Item.DisplayId, StringComparer.Ordinal)
            .Take(MaxMatches)
            .ToList();

        return new DuplicateReport { Explicit = state, ExplicitItem = target, Ambiguous = ambiguous, Matches = matches };
    }

    public static IReadOnlyList<DuplicateDifference> Differences(Candidate candidate, ManagementItem item)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(item);
        var f = candidate.Fields;
        var list = new List<DuplicateDifference>();
        if (f.Assignee is not null && !string.Equals(Name(f.Assignee), Name(item.Assignee), StringComparison.Ordinal)) list.Add(DuplicateDifference.Assignee);
        if (f.DueDate is not null && f.DueDate != item.DueDate) list.Add(DuplicateDifference.DueDate);
        if (f.Status is { } s && s != item.Status) list.Add(DuplicateDifference.Status);
        var a = f.Title + "\n" + f.Description;
        var b = item.Title + "\n" + item.Description;
        if (Negation().IsMatch(a) != Negation().IsMatch(b)) list.Add(DuplicateDifference.Negation);
        if (!Numbers(a).SetEquals(Numbers(b))) list.Add(DuplicateDifference.Numbers);
        return list;
    }

    private static string Name(string? name)
    {
        var n = EvidenceText.NormalizeTitle(name ?? string.Empty);
        return n.EndsWith("さん", StringComparison.Ordinal) ? n[..^2] : n;
    }

    private static HashSet<string> Numbers(string text)
    {
        var digits = KanjiNumber().Replace(text.Normalize(NormalizationForm.FormKC), m => Transcription.CerNormalizer.KanjiToNumber(m.Value));
        return Number().Matches(digits).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex("[〇一二三四五六七八九十百千]+")]
    private static partial Regex KanjiNumber();

    [GeneratedRegex("ない|ません|しない|不要|中止|やめ|見送")]
    private static partial Regex Negation();

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Number();
}
