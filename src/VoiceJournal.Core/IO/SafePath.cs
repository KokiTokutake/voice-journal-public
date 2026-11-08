using System.Text.RegularExpressions;

namespace VoiceJournal.Core.IO;

/// <summary>
/// 21.1: paths the app builds from stored or imported text are checked before use. A relative path must stay a plain
/// path under its root: no rooted or drive path, no <c>..</c>, no device path (<c>\\?\</c>, <c>\\.\</c>), no alternate
/// data stream (<c>:</c>), no reserved device name (<c>CON</c>, <c>NUL</c>, <c>COM1</c>…), no wildcard or control
/// character, no segment ending in a dot or space (Windows would silently drop it).
/// </summary>
public static partial class SafePath
{
    /// <summary>Why <paramref name="relativePath"/> cannot be used, or null when it is a plain relative path.</summary>
    public static string? Problem(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "パスが空です。";
        var p = relativePath.Replace('\\', '/');
        if (p.StartsWith('/') || Path.IsPathRooted(relativePath)) return "絶対パスは使えません。";
        if (p.Contains(':', StringComparison.Ordinal)) return "コロン（ドライブ名や代替データストリーム）は使えません。";
        if (p.Any(c => c < 0x20 || c is '*' or '?' or '"' or '<' or '>' or '|')) return "使えない文字が含まれています。";
        foreach (var segment in p.Split('/'))
        {
            if (segment.Length == 0) return "空のフォルダー名があります。";
            if (segment is "." or "..") return "「.」「..」は使えません。";
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return "末尾が「.」か空白の名前は使えません。";
            if (ReservedName().IsMatch(segment)) return $"「{segment}」は Windows のデバイス名のため使えません。";
        }
        return null;
    }

    /// <summary>Combines a checked relative path with its root and makes sure the result is still under the root.</summary>
    /// <exception cref="UnauthorizedAccessException">The path is not a plain relative path under the root.</exception>
    public static string Under(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        if (Problem(relativePath) is { } problem) throw new UnauthorizedAccessException($"パス「{relativePath}」は使えません: {problem}");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"パス「{relativePath}」は想定の場所の外を指しています。");
        return full;
    }

    /// <summary>A file extension safe to reuse in a file name (letters and digits, up to 10), or <paramref name="fallback"/>.</summary>
    public static string Extension(string? path, string fallback)
    {
        var ext = Path.GetExtension(path ?? string.Empty);
        return SimpleExtension().IsMatch(ext) ? ext : fallback;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();

    [GeneratedRegex(@"^\.[A-Za-z0-9]{1,10}$")]
    private static partial Regex SimpleExtension();
}
