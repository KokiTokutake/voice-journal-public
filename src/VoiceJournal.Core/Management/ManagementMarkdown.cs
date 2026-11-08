using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Records;

namespace VoiceJournal.Core.Management;

/// <summary>Encodings a management file may have (detail spec 11.3-3). Anything else is read-only.</summary>
public enum ManagedEncoding
{
    Utf8,
    Utf8Bom,
    Utf16LeBom,
}

/// <summary>Line ends of a management file. A mixed file keeps its bytes; new lines use its first line end.</summary>
public enum ManagedNewline
{
    Lf,
    Crlf,
    MixedPreserved,
}

/// <summary>Decoded text of a management file, with what is needed to write it back byte for byte.</summary>
public sealed record ManagedText(string Text, ManagedEncoding Encoding, ManagedNewline Newline)
{
    /// <summary>The line end for new lines.</summary>
    public string LineEnd => Newline == ManagedNewline.Lf ? "\n"
        : Newline == ManagedNewline.Crlf ? "\r\n"
        : Text.IndexOf('\n', StringComparison.Ordinal) is var i and > 0 && Text[i - 1] == '\r' ? "\r\n" : "\n";

    /// <summary>Decodes strictly: invalid bytes or an unknown encoding (Shift_JIS…) return null (the file is read-only, 11.3-3).</summary>
    public static ManagedText? Decode(ReadOnlySpan<byte> bytes)
    {
        string text;
        ManagedEncoding encoding;
        try
        {
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                encoding = ManagedEncoding.Utf8Bom;
                text = Strict(new UTF8Encoding(false, true), bytes[3..]);
            }
            else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            {
                encoding = ManagedEncoding.Utf16LeBom;
                if ((bytes.Length - 2) % 2 != 0) return null;
                text = Strict(new UnicodeEncoding(false, false, true), bytes[2..]);
            }
            else
            {
                encoding = ManagedEncoding.Utf8;
                text = Strict(new UTF8Encoding(false, true), bytes);
            }
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        if (text.Contains('\0', StringComparison.Ordinal)) return null;
        return new ManagedText(text, encoding, NewlineOf(text));
    }

    public byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encoding switch
        {
            ManagedEncoding.Utf8 => new UTF8Encoding(false).GetBytes(text),
            ManagedEncoding.Utf8Bom => [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(text)],
            ManagedEncoding.Utf16LeBom => [0xFF, 0xFE, .. new UnicodeEncoding(false, false).GetBytes(text)],
            _ => throw new InvalidOperationException(),
        };
    }

    public static string Label(ManagedEncoding encoding) => encoding switch
    {
        ManagedEncoding.Utf8 => "utf-8",
        ManagedEncoding.Utf8Bom => "utf-8-bom",
        _ => "utf-16le-bom",
    };

    public static string Label(ManagedNewline newline) => newline switch
    {
        ManagedNewline.Lf => "lf",
        ManagedNewline.Crlf => "crlf",
        _ => "mixed-preserved",
    };

    private static string Strict(Encoding encoding, ReadOnlySpan<byte> bytes) => encoding.GetString(bytes);

    private static ManagedNewline NewlineOf(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }
        return crlf > 0 && lf > 0 ? ManagedNewline.MixedPreserved : crlf > 0 ? ManagedNewline.Crlf : ManagedNewline.Lf;
    }
}

/// <summary>One <c>vj:item</c> block of a management file: the characters from its begin line to the end of its end line.</summary>
public sealed record ManagedBlock(Guid ItemId, int Revision, int Start, int Length);

public sealed record ManagedFileLayout
{
    public Guid? WorkspaceId { get; init; }

    public required IReadOnlyList<ManagedBlock> Blocks { get; init; }

    /// <summary>Nesting, a missing end, a duplicate id or another workspace's marker (11.5): the file must not be updated.</summary>
    public required IReadOnlyList<string> Errors { get; init; }
}

/// <summary>The management block format of detail spec 11.5: rendering items and finding their blocks again.</summary>
public static partial class ManagementMarkdown
{
    public const string NotSet = "未設定";

    public static string FileTitle(CandidateKind kind) => kind == CandidateKind.Issue ? "課題" : "タスク";

    /// <summary>A new management file: a heading, the workspace marker and a note that free notes go outside the blocks.</summary>
    public static string NewFile(CandidateKind kind, Guid workspaceId, string lineEnd) =>
        $"# {FileTitle(kind)}（Voice Journal）{lineEnd}{lineEnd}"
        + $"<!-- vj:workspace {workspaceId:D} -->{lineEnd}{lineEnd}"
        + $"vj:item のブロックの中はアプリが書き換えます。自由なメモはブロックの外に書いてください。{lineEnd}";

    /// <summary>The area appended to a file in append-only mode (11.4), once.</summary>
    public static string AppendArea(Guid workspaceId, string lineEnd) =>
        $"{lineEnd}## Voice Journal で追加した項目{lineEnd}{lineEnd}<!-- vj:workspace {workspaceId:D} -->{lineEnd}";

    /// <summary>The block of one item (11.5). Text is escaped so it can never fake a marker or a link.</summary>
    public static string Block(ManagementItem item, IReadOnlyDictionary<Guid, ManagementItem> items, string lineEnd)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append(lineEnd);
        Line($"<!-- vj:item-begin id={item.Id:D} revision={item.Revision.ToString(CultureInfo.InvariantCulture)} -->");
        Line($"### {item.DisplayId} {RecordMarkdown.Inline(item.Title)}");
        Line($"- 状態: {ItemStatuses.Label(item.Status)}");
        Line($"- 優先度: {(item.Priority is { } p ? p.ToString().ToLowerInvariant() : NotSet)}");
        Line($"- 担当: {(item.Assignee is { } a ? RecordMarkdown.Inline(a) : NotSet)}");
        Line($"- 期限: {(item.DueDate is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : NotSet)}");
        if (item.Kind == CandidateKind.Task)
            Line($"- 関連課題: {(item.ParentIssueId is { } parent && items.TryGetValue(parent, out var issue) ? issue.DisplayId : NotSet)}");
        Line($"- プロジェクト: {RecordMarkdown.Inline(item.Project)}");
        Line($"- 内容: {(item.Description.Length > 0 ? RecordMarkdown.Inline(item.Description) : NotSet)}");
        // A fixed order (session, then segment), so a block reads the same however the evidence was stored.
        foreach (var e in item.Evidence.OrderBy(e => e.SessionId).ThenBy(e => e.SegmentId))
            Line($"- 根拠: session={e.SessionId:D} segment={e.SegmentId:D}");
        Line($"<!-- vj:item-end id={item.Id:D} -->");
        return sb.ToString();
    }

    /// <summary>Finds the workspace marker and the item blocks, and everything that makes the file unsafe to update.</summary>
    public static ManagedFileLayout Parse(string text, Guid workspaceId)
    {
        ArgumentNullException.ThrowIfNull(text);
        var errors = new List<string>();
        var blocks = new List<ManagedBlock>();
        Guid? workspace = null;
        (Guid Id, int Revision, int Start)? open = null;
        var ids = new HashSet<Guid>();
        var pos = 0;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var next = nl < 0 ? text.Length : nl + 1;
            var line = text[pos..next].TrimEnd('\n').TrimEnd('\r');
            if (WorkspaceMarker().Match(line) is { Success: true } w)
            {
                var id = Guid.Parse(w.Groups[1].Value);
                if (id != workspaceId) errors.Add($"別のワークスペースの印があります（{id:D}）");
                else if (workspace is not null) errors.Add("ワークスペースの印が 2 つ以上あります");
                workspace = id;
            }
            else if (BeginMarker().Match(line) is { Success: true } b)
            {
                var id = Guid.Parse(b.Groups[1].Value);
                if (open is not null) errors.Add($"項目のブロックが入れ子になっています（{id:D}）");
                else if (!ids.Add(id)) errors.Add($"項目の ID が重複しています（{id:D}）");
                open = (id, int.Parse(b.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture), pos);
            }
            else if (EndMarker().Match(line) is { Success: true } e)
            {
                var id = Guid.Parse(e.Groups[1].Value);
                if (open is not { } o || o.Id != id) errors.Add($"項目のブロックの終わりが始まりと合いません（{id:D}）");
                else blocks.Add(new ManagedBlock(o.Id, o.Revision, o.Start, next - o.Start));
                open = null;
            }
            else if (line.Contains("vj:item-", StringComparison.Ordinal) || line.Contains("vj:workspace", StringComparison.Ordinal))
            {
                if (line.TrimStart().StartsWith("<!--", StringComparison.Ordinal)) errors.Add("形式の分からない vj の印があります");
            }
            pos = next;
        }
        if (open is { } unclosed) errors.Add($"項目のブロックに終わりがありません（{unclosed.Id:D}）");
        return new ManagedFileLayout { WorkspaceId = workspace, Blocks = blocks, Errors = errors };
    }

    /// <summary>
    /// Reads the fields back from a block edited by hand (11.2: an external change is taken in, never overwritten). Returns
    /// null with <paramref name="problem"/> when a line cannot be read (an unknown status, a date that does not exist…).
    /// </summary>
    public static CandidateFields? ReadFields(string block, CandidateKind kind, out string? project, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(block);
        project = null;
        problem = null;
        string? title = null, description = null, status = null, priority = null, assignee = null, due = null;
        foreach (var raw in block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var rest = line[4..];
                var space = rest.IndexOf(' ', StringComparison.Ordinal);
                title = space < 0 ? string.Empty : Unescape(rest[(space + 1)..]);
            }
            else if (Field().Match(line) is { Success: true } m)
            {
                var value = m.Groups[2].Value.Trim();
                switch (m.Groups[1].Value)
                {
                    case "状態": status = value; break;
                    case "優先度": priority = value; break;
                    case "担当": assignee = value; break;
                    case "期限": due = value; break;
                    case "プロジェクト": project = Unescape(value); break;
                    case "内容": description = value; break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(title)) { problem = "見出しにタイトルがありません"; return null; }
        var parsedStatus = Enum.GetValues<ItemStatus>().Where(s => ItemStatuses.Label(s) == status).Select(s => (ItemStatus?)s).FirstOrDefault();
        if (parsedStatus is null || !ItemStatuses.IsAllowed(kind, parsedStatus.Value)) { problem = $"状態「{status}」は使えません"; return null; }
        ItemPriority? parsedPriority = null;
        if (priority is not null && priority != NotSet)
        {
            if (!Enum.TryParse<ItemPriority>(priority, ignoreCase: true, out var p) || !Enum.IsDefined(p) || int.TryParse(priority, out _)) { problem = $"優先度「{priority}」は使えません"; return null; }
            parsedPriority = p;
        }
        DateOnly? parsedDue = null;
        if (due is not null && due != NotSet)
        {
            if (!DateOnly.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { problem = $"期限「{due}」は日付として読めません"; return null; }
            parsedDue = d;
        }
        return new CandidateFields
        {
            Title = title.Trim(),
            Description = description is null || description == NotSet ? string.Empty : Unescape(description),
            Status = parsedStatus,
            Priority = parsedPriority,
            Assignee = assignee is null || assignee == NotSet ? null : Unescape(assignee),
            DueDate = parsedDue,
        };
    }

    /// <summary>The items whose block in <paramref name="text"/> is not what the app wrote: edited by hand (11.5).</summary>
    public static IReadOnlyList<(ManagementItem Item, string Block, string Expected)> EditedBlocks(
        string text, ManagedFileLayout layout, IReadOnlyDictionary<Guid, ManagementItem> items, string lineEnd)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(items);
        var list = new List<(ManagementItem, string, string)>();
        foreach (var b in layout.Blocks)
        {
            if (!items.TryGetValue(b.ItemId, out var item)) continue;
            var inFile = text.Substring(b.Start, b.Length);
            var expected = Block(item, items, lineEnd);
            if (!ChangePlanner.SameBlock(inFile, expected)) list.Add((item, inFile, expected));
        }
        return list;
    }

    // Undoes RecordMarkdown.Inline's backslashes (and its broken scheme separator).
    private static string Unescape(string text) => InlineEscape().Replace(text.Replace(@":\/\/", "://", StringComparison.Ordinal), "$1");

    [GeneratedRegex(@"^- (状態|優先度|担当|期限|関連課題|プロジェクト|内容|根拠): ?(.*)$")]
    private static partial Regex Field();

    [GeneratedRegex(@"\\([\\`*_\[\]<>&~#|!.])")]
    private static partial Regex InlineEscape();

    [GeneratedRegex(@"^<!-- vj:workspace ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}) -->$")]
    private static partial Regex WorkspaceMarker();

    [GeneratedRegex(@"^<!-- vj:item-begin id=([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}) revision=([1-9][0-9]{0,8}) -->$")]
    private static partial Regex BeginMarker();

    [GeneratedRegex(@"^<!-- vj:item-end id=([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}) -->$")]
    private static partial Regex EndMarker();
}
