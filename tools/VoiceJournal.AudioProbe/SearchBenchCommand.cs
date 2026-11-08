using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.AudioProbe;

/// <summary>
/// AT-21: a workspace with 1,000 sessions and 10,000 items, and the time of the searches the app does: the library
/// (index read and the in-memory filter by title, project and date) and the management list (short words, parts of
/// words, status and due date). Reported as JSON (median of 5 runs, milliseconds).
/// </summary>
internal static class SearchBenchCommand
{
    private static readonly string[] Words =
    [
        "見積書", "議事録", "ログイン", "請求書", "設計レビュー", "結合テスト", "リリース判定", "問い合わせ", "パスワード規則", "バックアップ",
        "性能改善", "説明会", "移行スクリプト", "マニュアル", "予算", "採用面接", "勉強会", "棚卸し", "契約更新", "障害報告",
    ];

    private static readonly string[] People = ["田村", "鈴木", "佐藤", "高橋", "中村", "木村", "小林", "山口"];

    public static int Run(string[] args, JsonSerializerOptions json)
    {
        var root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var workspace = Workspace.OpenOrCreate(root, syncRoots: []);
        var db = VoiceJournalDatabase.Open(workspace.DatabasePath);
        var index = new SessionIndex(workspace, db, TimeProvider.System);
        var store = new ManagementStore(workspace, db);
        var random = new Random(20261108);

        var setup = Stopwatch.StartNew();
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 1000; i++)
        {
            var id = Guid.CreateVersion7();
            var started = start.AddHours(i * 17);
            var dir = Path.Combine(workspace.SessionsRoot, started.ToString("yyyy", CultureInfo.InvariantCulture), started.ToString("MM", CultureInfo.InvariantCulture), id.ToString("D"));
            Directory.CreateDirectory(dir);
            new SessionManifest
            {
                SessionId = id,
                Title = $"{Words[random.Next(Words.Length)]}の打ち合わせ {i + 1}",
                Project = i % 3 == 0 ? "基幹システム" : i % 3 == 1 ? "採用" : null,
                CaptureMode = CaptureMode.Mic,
                CaptureState = CaptureState.Finalized,
                StartedAtUtc = started,
                EndedAtUtc = started.AddMinutes(30),
                TimeZoneId = "Tokyo Standard Time",
            }.Save(dir);
        }
        index.Rebuild();
        store.Files();
        db.Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO items (id, workspace_id, display_id, kind, project, title, description, status, assignee, due_date, revision, created_at, updated_at)
                VALUES ($id, $ws, $display, $kind, $project, $title, $description, $status, $assignee, $due, 1, $at, $at)
                """;
            string[] taskStatuses = ["todo", "in_progress", "blocked", "done", "cancelled"];
            string[] issueStatuses = ["open", "in_progress", "blocked", "resolved", "closed"];
            for (var i = 0; i < 10_000; i++)
            {
                var issue = i % 4 == 0;
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString("D"));
                cmd.Parameters.AddWithValue("$ws", workspace.Info.WorkspaceId.ToString("D"));
                cmd.Parameters.AddWithValue("$display", (issue ? "ISS-" : "TSK-") + (i + 1).ToString("000000", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$kind", issue ? "issue" : "task");
                cmd.Parameters.AddWithValue("$project", i % 3 == 0 ? "基幹システム" : "未分類");
                cmd.Parameters.AddWithValue("$title", $"{Words[random.Next(Words.Length)]}の{(issue ? "問題" : "作業")} {i + 1}");
                cmd.Parameters.AddWithValue("$description", $"{Words[random.Next(Words.Length)]}について{People[random.Next(People.Length)]}さんと確認する");
                cmd.Parameters.AddWithValue("$status", (issue ? issueStatuses : taskStatuses)[random.Next(5)]);
                cmd.Parameters.AddWithValue("$assignee", random.Next(3) == 0 ? DBNull.Value : People[random.Next(People.Length)]);
                cmd.Parameters.AddWithValue("$due", random.Next(2) == 0 ? DBNull.Value : new DateOnly(2026, 1, 1).AddDays(random.Next(365)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$at", "2026-01-01T00:00:00.0000000Z");
                cmd.ExecuteNonQuery();
            }
            return 0;
        });
        setup.Stop();

        static (double Ms, int Count) Measure(Func<int> run)
        {
            var times = new List<double>();
            var count = 0;
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                count = run();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return (Math.Round(times[2], 1), count);
        }

        IReadOnlyList<SessionSummary>? sessions = null;
        bool Library(SessionSummary s, string q) =>
            (s.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) || (s.Project?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || s.StartedAtUtc.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal);
        var cases = new (string Name, Func<int> Run)[]
        {
            ("library: index read (1,000 sessions)", () => (sessions = index.ListSessions()).Count),
            ("library: short word「会」", () => sessions!.Count(s => Library(s, "会"))),
            ("library: part of a word「レビュー」", () => sessions!.Count(s => Library(s, "レビュー"))),
            ("library: date「2024/06」", () => sessions!.Count(s => Library(s, "2024/06"))),
            ("items: short word「見」", () => store.Search("見", limit: 10_000).Count),
            ("items: part of a word「スクリプト」", () => store.Search("スクリプト", limit: 10_000).Count),
            ("items: assignee「中村」", () => store.Search("中村", limit: 10_000).Count),
            ("items: status done", () => store.Search(null, ItemStatus.Done, limit: 10_000).Count),
            ("items: due in 2026-03", () => store.Search(null, dueFrom: new DateOnly(2026, 3, 1), dueTo: new DateOnly(2026, 3, 31), limit: 10_000).Count),
            ("items: word + status + due", () => store.Search("レビュー", ItemStatus.Todo, dueFrom: new DateOnly(2026, 1, 1), dueTo: new DateOnly(2026, 6, 30), limit: 10_000).Count),
            ("items: first page「の」（500 件まで）", () => store.Search("の").Count),
        };
        var results = cases.Select(c =>
        {
            var (ms, count) = Measure(c.Run);
            Console.Error.WriteLine($"{ms,8:0.0} ms  {count,6}  {c.Name}");
            return new { name = c.Name, median_ms = ms, results = count };
        }).ToList();
        SqliteConnection.ClearAllPools();
        Console.WriteLine(JsonSerializer.Serialize(new { sessions = 1000, items = 10_000, setup_seconds = Math.Round(setup.Elapsed.TotalSeconds, 1), cases = results }, json));
        return 0;
    }
}
