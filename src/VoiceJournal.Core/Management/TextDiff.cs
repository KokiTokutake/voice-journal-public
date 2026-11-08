using System.Globalization;
using System.Text;

namespace VoiceJournal.Core.Management;

/// <summary>A line-based unified diff for the final check of a change plan (detail spec 13.2, UI-09). For display only.</summary>
public static class TextDiff
{
    /// <summary>Above this many lines on either side the diff is not computed (the files are shown as replaced).</summary>
    public const int MaxLines = 5000;

    public static string Unified(string before, string after, string path, int context = 3)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var a = Lines(before);
        var b = Lines(after);
        var sb = new StringBuilder();
        sb.Append("--- ").Append(before.Length == 0 ? "/dev/null" : "a/" + path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');
        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            sb.Append("@@ ファイルが大きいので差分を省略しました（").Append(a.Length.ToString(CultureInfo.InvariantCulture))
              .Append(" 行 → ").Append(b.Length.ToString(CultureInfo.InvariantCulture)).Append(" 行） @@\n");
            return sb.ToString();
        }

        // Longest common subsequence, then the edit script as (kind, line) with ' ', '-' and '+'.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var script = new List<(char Kind, string Line, int A, int B)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { script.Add((' ', a[x], x, y)); x++; y++; }
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1])) { script.Add(('-', a[x], x, y)); x++; }
            else { script.Add(('+', b[y], x, y)); y++; }
        }

        // Hunks: changed lines with up to `context` unchanged lines around them.
        var changed = script.Select((s, i) => (s, i)).Where(p => p.s.Kind != ' ').Select(p => p.i).ToList();
        var k = 0;
        while (k < changed.Count)
        {
            var start = Math.Max(0, changed[k] - context);
            var end = Math.Min(script.Count - 1, changed[k] + context);
            while (k + 1 < changed.Count && changed[k + 1] - context <= end + 1)
            {
                k++;
                end = Math.Min(script.Count - 1, changed[k] + context);
            }
            var hunk = script.GetRange(start, end - start + 1);
            var aCount = hunk.Count(h => h.Kind != '+');
            var bCount = hunk.Count(h => h.Kind != '-');
            var aStart = hunk[0].A + (aCount > 0 ? 1 : 0);
            var bStart = hunk[0].B + (bCount > 0 ? 1 : 0);
            sb.Append(CultureInfo.InvariantCulture, $"@@ -{aStart},{aCount} +{bStart},{bCount} @@\n");
            foreach (var h in hunk) sb.Append(h.Kind).Append(h.Line).Append('\n');
            k++;
        }
        return sb.ToString();
    }

    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}
