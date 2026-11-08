using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.IO;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>
/// 「診断情報」 (detail spec 18.2 UI-03・UI-11, 19, 21): versions, hashes, settings, counts, states and error codes in one
/// JSON file under <c>logs\</c>, to attach to a problem report. It never contains the text of a transcript, a record, a
/// prompt or a management item; only counts, states, codes, paths and hashes.
/// </summary>
public static class DiagnosticsReport
{
    /// <summary>21.3: what the report holds, shown to the user before it is written. Keep it in step with <see cref="Collect"/>.</summary>
    public const string Contents =
        "含めるもの:\n" +
        "・アプリ・OS・.NET の版、CPU の数、メモリと空き容量\n" +
        "・ワークスペースとデータベースのパス（Windows のユーザー名を含みます）、データベースの検査結果\n" +
        "・セッション・文字起こし・記録・課題・タスクなどの件数、処理の状態とエラーコード、管理ファイルのパスとハッシュ\n" +
        "・文字起こしと推論エンジンのツール・モデルのパス・SHA-256・サイズ、設定\n" +
        "・最近の操作の種類と時刻（20 件）\n\n" +
        "含めないもの: 原音、文字起こし・記録・課題・タスクの本文、プロンプト、タイトル\n\n" +
        "この PC の logs フォルダーに保存するだけで、どこにも送信しません。";

    private static readonly string[] CountedTables =
        ["sessions", "transcript_revisions", "segments", "analyses", "records", "candidates", "items", "item_evidence", "apply_batches", "candidate_reviews"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Collect(Workspace workspace, VoiceJournalDatabase db, string appRoot, string appVersion, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(appRoot);
        var report = new JsonObject
        {
            ["created_at"] = (time ?? TimeProvider.System).GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["app_version"] = appVersion,
            ["os"] = RuntimeInformation.OSDescription,
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
            ["processors"] = Environment.ProcessorCount,
            ["memory_available_mb"] = MemoryStatus.AvailablePhysicalMb(),
            ["memory_total_mb"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
            ["workspace"] = new JsonObject
            {
                ["id"] = workspace.Info.WorkspaceId.ToString("D"),
                ["root"] = workspace.Root,
                ["free_disk_mb"] = FreeDiskMb(workspace.Root),
            },
        };
        report["database"] = Database(db);
        var tools = new JsonObject();
        foreach (var name in new[] { "stt-tools.json", "llm-tools.json" })
            tools[name] = RegisteredFiles(Path.Combine(appRoot, "config", name));
        report["tools"] = tools;
        var settings = new JsonObject();
        var config = Path.Combine(appRoot, "config");
        if (Directory.Exists(config))
            foreach (var file in Directory.GetFiles(config, "*.json").Where(f => !Path.GetFileName(f).EndsWith("-tools.json", StringComparison.OrdinalIgnoreCase)))
                settings[Path.GetFileName(file)] = TryParse(file);
        report["settings"] = settings;
        return report;
    }

    /// <summary>Writes the report to <c>logs\diagnostics-&lt;time&gt;.json</c> and returns its path.</summary>
    public static string Write(Workspace workspace, VoiceJournalDatabase db, string appRoot, string appVersion, TimeProvider? time = null)
    {
        var report = Collect(workspace, db, appRoot, appVersion, time);
        var folder = Path.Combine(appRoot, "logs");
        Directory.CreateDirectory(folder);
        var stamp = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"diagnostics-{stamp}.json");
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(report, Indented));
        return path;
    }

    private static JsonObject Database(VoiceJournalDatabase db) => db.Read(c =>
    {
        var node = new JsonObject { ["path"] = db.FilePath };
        node["integrity"] = Scalar(c, "PRAGMA quick_check");
        node["migrations"] = Rows(c, "SELECT version, name, app_version, migration_hash FROM schema_migrations ORDER BY version");
        var counts = new JsonObject();
        foreach (var table in CountedTables)
            counts[table] = Count(c, $"SELECT COUNT(*) FROM {table}");
        node["counts"] = counts;
        node["stt_jobs"] = Rows(c, "SELECT state, error_code, COUNT(*) AS n FROM jobs GROUP BY state, error_code ORDER BY state");
        node["record_jobs"] = Rows(c, "SELECT state, error_code, COUNT(*) AS n FROM record_jobs GROUP BY state, error_code ORDER BY state");
        node["apply_batches"] = Rows(c, "SELECT state, error_code, COUNT(*) AS n FROM apply_batches GROUP BY state, error_code ORDER BY state");
        node["items"] = Rows(c, "SELECT kind, status, COUNT(*) AS n FROM items GROUP BY kind, status ORDER BY kind, status");
        node["managed_files"] = Rows(c, "SELECT kind, path, mode, last_hash FROM managed_files ORDER BY kind");
        node["recent_audit"] = Rows(c, "SELECT timestamp, event_type, entity_type FROM audit_events ORDER BY timestamp DESC LIMIT 20");
        return node;
    });

    // The registered executables and models: path, recorded hash, and whether the file is there with some size.
    // Hashes are not recomputed (a model is gigabytes); the app checks them before every start.
    private static JsonNode? RegisteredFiles(string path)
    {
        if (!File.Exists(path)) return null;
        var parsed = TryParse(path);
        if (parsed is null) return "読めません";
        var files = new JsonArray();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["path"] is JsonValue p && p.TryGetValue<string>(out var file))
                        files.Add(new JsonObject
                        {
                            ["path"] = file,
                            ["sha256"] = (string?)o["sha256"],
                            ["exists"] = File.Exists(file),
                            ["size"] = File.Exists(file) ? new FileInfo(file).Length : null,
                        });
                    foreach (var (_, v) in o) Walk(v);
                    break;
                case JsonArray a:
                    foreach (var v in a) Walk(v);
                    break;
            }
        }
        Walk(parsed);
        return files;
    }

    private static JsonNode? TryParse(string path)
    {
        try
        {
            ReadOnlySpan<byte> bytes = AtomicFile.ReadAllBytes(path);
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..]; // a file saved by an editor may have a BOM
            return JsonNode.Parse(bytes);
        }
        catch (JsonException) { return "読めません"; }
        catch (IOException) { return "読めません"; }
    }

    private static long? FreeDiskMb(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace / (1024 * 1024); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long? Count(SqliteConnection c, string sql)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return null; // a table of a later phase
        }
    }

    private static JsonArray Rows(SqliteConnection c, string sql)
    {
        var rows = new JsonArray();
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new JsonObject();
                for (var i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? null : JsonValue.Create(r.GetValue(i) is long l ? (object)l : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture));
                rows.Add(row);
            }
        }
        catch (SqliteException)
        {
            // a table of a later phase
        }
        return rows;
    }
}
