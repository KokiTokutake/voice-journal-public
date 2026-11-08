using System.Diagnostics;
using Microsoft.Data.Sqlite;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Storage;

public sealed class VoiceJournalDatabaseTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-db-");

    public void Dispose() => _dir.Dispose();

    private string DbPath => _dir.File("db/voicejournal.db");

    [Fact]
    public void Open_CreatesWalDatabase_WithTheConfiguredSettings()
    {
        var db = VoiceJournalDatabase.Open(DbPath);

        Assert.True(File.Exists(DbPath));
        using var c = db.OpenConnection();
        Assert.Equal("wal", Scalar(c, "PRAGMA journal_mode"));
        Assert.Equal(1L, Scalar(c, "PRAGMA foreign_keys"));
        Assert.Equal(2L, Scalar(c, "PRAGMA synchronous")); // FULL
        Assert.Equal(5000L, Scalar(c, "PRAGMA busy_timeout"));
    }

    [Fact]
    public void Migrations_CreateThePhaseBToETables_AndAreRecorded()
    {
        var db = VoiceJournalDatabase.Open(DbPath, new DatabaseOptions { AppVersion = "1.2.3" });

        using var c = db.OpenConnection();
        var tables = Strings(c, "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name");
        Assert.Equal(["analyses", "apply_batches", "apply_file_ops", "approvals", "audio_chunks", "audio_events", "audio_tracks", "audit_events", "candidate_applications", "candidate_reviews", "candidates", "display_counters", "item_evidence", "item_proposals", "item_versions", "items", "jobs", "managed_files", "record_jobs", "records", "schema_migrations", "segments", "sessions", "transcript_revisions", "workspaces"], tables);
        Assert.Contains("transcript_current_revision", Strings(c, "SELECT name FROM pragma_table_info('sessions')"));

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version, name, app_version, migration_hash FROM schema_migrations";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(1, r.GetInt32(0));
        Assert.Equal("sessions_and_audio", r.GetString(1));
        Assert.Equal("1.2.3", r.GetString(2));
        Assert.Equal(Migrations.All[0].Hash, r.GetString(3));
        Assert.True(r.Read());
        Assert.Equal(2, r.GetInt32(0));
        Assert.Equal("jobs_and_transcripts", r.GetString(1));
        Assert.Equal(Migrations.All[1].Hash, r.GetString(3));
        Assert.True(r.Read());
        Assert.Equal(3, r.GetInt32(0));
        Assert.Equal("records", r.GetString(1));
        Assert.Equal(Migrations.All[2].Hash, r.GetString(3));
        Assert.True(r.Read());
        Assert.Equal(4, r.GetInt32(0));
        Assert.Equal("management", r.GetString(1));
        Assert.Equal(Migrations.All[3].Hash, r.GetString(3));
        Assert.False(r.Read());
    }

    [Fact]
    public void Reopening_AppliesNothingAgain()
    {
        var db = VoiceJournalDatabase.Open(DbPath);
        Assert.Equal(0, Migrator.Apply(db, TimeProvider.System));
        VoiceJournalDatabase.Open(DbPath);
        using var c = db.OpenConnection();
        Assert.Equal((long)Migrations.All.Count, Scalar(c, "SELECT COUNT(*) FROM schema_migrations"));
    }

    [Fact]
    public void LaterMigrations_AreAppliedInOrder()
    {
        var db = VoiceJournalDatabase.Open(DbPath);
        var next = Migrations.All.Append(new Migration(5, "add_notes", "CREATE TABLE notes (id TEXT PRIMARY KEY) STRICT;")).ToList();

        Assert.Equal(1, Migrator.Apply(db, TimeProvider.System, next));
        using var c = db.OpenConnection();
        Assert.Equal([1L, 2L, 3L, 4L, 5L], Longs(c, "SELECT version FROM schema_migrations ORDER BY version"));
    }

    [Fact]
    public void AnExistingDatabase_IsBackedUpBeforeAMigration_AndAFailedMigrationLeavesTheOldVersion() // AT-24
    {
        var db = VoiceJournalDatabase.Open(DbPath);
        Execute(db, "INSERT INTO workspaces VALUES ('" + new string('1', 36) + "', 'w', 1, 'C:\\w', '2026-11-08T00:00:00.0000000Z')");
        var broken = Migrations.All.Append(new Migration(5, "broken", "CREATE TABLE notes (id TEXT PRIMARY KEY) STRICT; INSERT INTO no_such_table VALUES (1);")).ToList();

        Assert.ThrowsAny<SqliteException>(() => Migrator.Apply(db, TimeProvider.System, broken));

        using (var c = db.OpenConnection())
        {
            Assert.Equal([1L, 2L, 3L, 4L], Longs(c, "SELECT version FROM schema_migrations ORDER BY version"));
            Assert.Empty(Strings(c, "SELECT name FROM sqlite_schema WHERE name = 'notes'")); // the failed step left nothing
        }
        var backup = Assert.Single(Directory.GetFiles(Migrator.BackupFolder(db), "voicejournal-v4-*.db"));
        using var b = new SqliteConnection($"Data Source={backup};Pooling=False");
        b.Open();
        Assert.Equal([1L, 2L, 3L, 4L], Longs(b, "SELECT version FROM schema_migrations ORDER BY version"));
        Assert.Equal(["w"], Strings(b, "SELECT name FROM workspaces"));
    }

    [Fact]
    public void ANewDatabase_IsNotBackedUp()
    {
        var db = VoiceJournalDatabase.Open(DbPath);
        Assert.False(Directory.Exists(Migrator.BackupFolder(db)));
    }

    [Fact]
    public void DatabaseFromANewerApp_IsRefused()
    {
        var db = VoiceJournalDatabase.Open(DbPath);
        Execute(db, "INSERT INTO schema_migrations VALUES (99, 'future', '2030-01-01T00:00:00.0000000Z', '9.0', '" + new string('a', 64) + "')");

        var ex = Assert.Throws<DatabaseSchemaException>(() => VoiceJournalDatabase.Open(DbPath));
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AlteredMigration_IsRefused()
    {
        var db = VoiceJournalDatabase.Open(DbPath);
        Execute(db, "UPDATE schema_migrations SET migration_hash = '" + new string('b', 64) + "' WHERE version = 1");

        Assert.Throws<DatabaseSchemaException>(() => VoiceJournalDatabase.Open(DbPath));
    }

    [Theory]
    [InlineData("INSERT INTO sessions (id, workspace_id, relative_path, started_at, timezone, capture_mode, capture_state, manifest_sha256, indexed_at) VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-00000000000f', 'p', 't', 'tz', 'mic', 'finalized', '" + Sha + "', 'n')")] // unknown workspace (FK)
    [InlineData("INSERT INTO workspaces VALUES ('" + Ws + "', 'w', 1, 'r', 'c'); INSERT INTO sessions (id, workspace_id, relative_path, started_at, timezone, capture_mode, capture_state, manifest_sha256, indexed_at) VALUES ('00000000-0000-0000-0000-000000000001', '" + Ws + "', 'p', 't', 'tz', 'video', 'finalized', '" + Sha + "', 'n')")] // unknown capture mode
    [InlineData("INSERT INTO workspaces VALUES ('" + Ws + "', 'w', 1, 'r', 'c'); INSERT INTO sessions (id, workspace_id, relative_path, started_at, timezone, capture_mode, capture_state, manifest_sha256, indexed_at) VALUES ('00000000-0000-0000-0000-000000000001', '" + Ws + "', 'p', 't', 'tz', 'mic', 'finalized', 'ABC', 'n')")] // malformed hash
    public void Constraints_RejectInvalidRows(string sql)
    {
        var db = VoiceJournalDatabase.Open(DbPath);
        var ex = Assert.Throws<SqliteException>(() => Execute(db, sql));
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    private const string Ws = "00000000-0000-0000-0000-00000000000a";
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Write_WaitsForAnotherWriter_AndSucceedsOnceItReleases() // "DB busy 時の再試行"
    {
        var db = VoiceJournalDatabase.Open(DbPath, new DatabaseOptions { BusyTimeout = TimeSpan.FromSeconds(1), BusyRetries = 3, RetryDelay = TimeSpan.FromMilliseconds(100) });
        using var holder = db.OpenConnection();
        using var held = holder.BeginTransaction(deferred: false); // another writer holds the lock
        var release = Task.Run(async () =>
        {
            await Task.Delay(1500);
            held.Commit();
        });

        var result = db.Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO workspaces VALUES ('" + Ws + "', 'w', 1, 'r', 'c')";
            return cmd.ExecuteNonQuery();
        });

        Assert.Equal(1, result);
        await release;
    }

    [Fact]
    public void Write_GivesUpWithATypedError_WhenTheLockIsNeverReleased()
    {
        var db = VoiceJournalDatabase.Open(DbPath, new DatabaseOptions { BusyTimeout = TimeSpan.FromSeconds(1), BusyRetries = 1, RetryDelay = TimeSpan.FromMilliseconds(50) });
        using var holder = db.OpenConnection();
        using var held = holder.BeginTransaction(deferred: false);

        var watch = Stopwatch.StartNew();
        var ex = Assert.Throws<DatabaseBusyException>(() => db.Write((c, tx) => 0));
        Assert.Equal(5, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10)); // 2 attempts × ~1 s, not forever
    }

    [Fact]
    public void Readers_AreNotBlockedByAWriter()
    {
        var db = VoiceJournalDatabase.Open(DbPath);
        Execute(db, "INSERT INTO workspaces VALUES ('" + Ws + "', 'w', 1, 'r', 'c')");
        using var holder = db.OpenConnection();
        using var held = holder.BeginTransaction(deferred: false);
        using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = held;
            cmd.CommandText = "UPDATE workspaces SET name = 'changed'";
            cmd.ExecuteNonQuery();
        }

        var name = db.Read(c => (string)Scalar(c, "SELECT name FROM workspaces")!);
        Assert.Equal("w", name); // committed value, no waiting
    }

    private static void Execute(VoiceJournalDatabase db, string sql) => db.Write((c, tx) =>
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    });

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static List<string> Strings(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static List<long> Longs(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }
}
