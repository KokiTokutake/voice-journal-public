using Microsoft.Data.Sqlite;

namespace VoiceJournal.Tests.Environment;

/// <summary>
/// Phase A: verifies the bundled SQLite engine supports what the spec relies on
/// (detail spec 14.2 WAL / foreign keys, 15.3 FTS5 trigram for Japanese search).
/// </summary>
public sealed class SqliteCapabilityTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqliteCapabilityTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Fts5Trigram_FindsJapaneseSubstring()
    {
        Execute("CREATE VIRTUAL TABLE t USING fts5(body, tokenize='trigram');");
        Execute("INSERT INTO t(body) VALUES ('ログ収集方式を来週までに調査する'), ('既存PCの性能検証');");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT body FROM t WHERE t MATCH $q;";
        cmd.Parameters.AddWithValue("$q", "収集方式");
        var hit = cmd.ExecuteScalar() as string;

        Assert.Equal("ログ収集方式を来週までに調査する", hit);
    }

    [Fact]
    public void ForeignKeys_CanBeEnabled()
    {
        Execute("PRAGMA foreign_keys = ON;");
        Assert.Equal(1L, Scalar("PRAGMA foreign_keys;"));
    }

    [Fact]
    public void EngineVersion_IsRecorded()
    {
        var version = Scalar("SELECT sqlite_version();") as string;
        Assert.False(string.IsNullOrWhiteSpace(version));
        Console.WriteLine($"sqlite_version={version}");
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
