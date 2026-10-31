using Microsoft.Data.Sqlite;

namespace VoiceJournal.Tests.Environment;

/// <summary>
/// Phase A: verifies the WAL-related behaviour the detail spec relies on (14.2 / 15):
/// journal_mode=WAL, synchronous=FULL, busy_timeout=5000, readers not blocked by a writer,
/// a second writer getting SQLITE_BUSY, and Online Backup capturing un-checkpointed WAL content
/// where a plain copy of the .db file does not.
/// Uses a real file under %TEMP% (local NTFS) because WAL is not available for :memory: databases.
/// </summary>
public sealed class SqliteWalTests : IDisposable
{
    private const int SqliteBusy = 5;

    private readonly string _dir = Directory.CreateTempSubdirectory("vj-wal-").FullName;
    private readonly string _dbPath;

    public SqliteWalTests() => _dbPath = Path.Combine(_dir, "voicejournal.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void JournalModeWal_IsPersistedInTheFile()
    {
        using (var c = Open())
        {
            Assert.Equal("wal", Scalar(c, "PRAGMA journal_mode = WAL;"));
            Execute(c, "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1);");
            Assert.True(File.Exists(_dbPath + "-wal"));
        }

        SqliteConnection.ClearAllPools();
        using var reopened = Open();
        Assert.Equal("wal", Scalar(reopened, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void WriterConnectionPragmas_CanBeApplied()
    {
        using var c = Open();
        Execute(c, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;");

        Assert.Equal(1L, Scalar(c, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, Scalar(c, "PRAGMA synchronous;")); // 2 = FULL
        Assert.Equal(5000L, Scalar(c, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public void Reader_SeesLastCommittedSnapshot_WhileWriterHoldsTransaction()
    {
        using var writer = Open();
        Execute(writer, "PRAGMA journal_mode = WAL; CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1);");

        using var tx = writer.BeginTransaction();
        Execute(writer, "UPDATE t SET x = 2;", tx);

        using var reader = Open();
        Assert.Equal(1L, Scalar(reader, "SELECT x FROM t;")); // not blocked, sees committed value

        tx.Commit();
        Assert.Equal(2L, Scalar(reader, "SELECT x FROM t;"));
    }

    [Fact]
    public void SecondWriter_GetsBusy_WhileFirstWriterHoldsLock()
    {
        using var first = Open();
        Execute(first, "PRAGMA journal_mode = WAL; CREATE TABLE t(x INTEGER);");
        using var tx = first.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: takes the write lock now
        Execute(first, "INSERT INTO t VALUES (1);", tx);

        // Default Timeout bounds Microsoft.Data.Sqlite's own busy retry loop so the test stays fast.
        using var second = Open("Default Timeout=1");
        Execute(second, "PRAGMA busy_timeout = 200;");
        var ex = Assert.Throws<SqliteException>(() => Execute(second, "INSERT INTO t VALUES (2);"));
        Assert.Equal(SqliteBusy, ex.SqliteErrorCode);

        tx.Commit();
        Execute(second, "INSERT INTO t VALUES (2);"); // succeeds once the lock is released
        Assert.Equal(2L, Scalar(second, "SELECT COUNT(*) FROM t;"));
    }

    [Fact]
    public void OnlineBackup_IncludesUncheckpointedWal_ButPlainFileCopyDoesNot()
    {
        using var live = Open();
        Execute(live, "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0;");
        Execute(live, "CREATE TABLE t(x INTEGER);");
        for (var i = 0; i < 100; i++) Execute(live, $"INSERT INTO t VALUES ({i});");

        // Online Backup API: consistent copy including committed pages still in the -wal file.
        var backupPath = Path.Combine(_dir, "backup.db");
        using (var dest = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
        {
            dest.Open();
            live.BackupDatabase(dest);
        }
        using (var restored = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
        {
            restored.Open();
            Assert.Equal("ok", Scalar(restored, "PRAGMA integrity_check;"));
            Assert.Equal(100L, Scalar(restored, "SELECT COUNT(*) FROM t;"));
        }

        // Copying only the .db while the WAL holds un-checkpointed data loses it (spec 15: do not do this).
        var copyPath = Path.Combine(_dir, "plain-copy.db");
        File.Copy(_dbPath, copyPath);
        using var copy = new SqliteConnection($"Data Source={copyPath};Pooling=False");
        copy.Open();
        Assert.Equal(0L, Scalar(copy, "SELECT COUNT(*) FROM sqlite_master WHERE name = 't';"));
    }

    private SqliteConnection Open(string? extra = null)
    {
        var c = new SqliteConnection($"Data Source={_dbPath};Pooling=False" + (extra is null ? "" : ";" + extra));
        c.Open();
        return c;
    }

    private static void Execute(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
