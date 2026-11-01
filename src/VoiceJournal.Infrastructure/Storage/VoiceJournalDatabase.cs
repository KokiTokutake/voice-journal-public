using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VoiceJournal.Infrastructure.Storage;

public sealed record DatabaseOptions
{
    /// <summary>SQLite <c>busy_timeout</c> and the library's own retry window (Phase A: both must be set).</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Extra attempts of a whole transaction after the busy timeout expired.</summary>
    public int BusyRetries { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Recorded in schema_migrations.</summary>
    public string AppVersion { get; init; } = typeof(DatabaseOptions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

/// <summary>The database stayed locked by another writer after all retries.</summary>
public sealed class DatabaseBusyException : Exception
{
    public DatabaseBusyException() { }

    public DatabaseBusyException(string message) : base(message) { }

    public DatabaseBusyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The database schema is newer than this app or does not match the known migrations.</summary>
public sealed class DatabaseSchemaException : Exception
{
    public DatabaseSchemaException() { }

    public DatabaseSchemaException(string message) : base(message) { }

    public DatabaseSchemaException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The workspace SQLite database (detail spec 15): WAL, <c>foreign_keys=ON</c>, <c>synchronous=FULL</c>,
/// busy timeout. One short-lived connection per unit of work; writes use <c>BEGIN IMMEDIATE</c> and are
/// retried as a whole while another writer holds the lock.
/// </summary>
public sealed class VoiceJournalDatabase
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly string _connectionString;

    private VoiceJournalDatabase(string path, DatabaseOptions options)
    {
        FilePath = path;
        Options = options;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false, // no file handles left behind; the app opens connections rarely
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
        }.ToString();
    }

    public string FilePath { get; }

    public DatabaseOptions Options { get; }

    /// <summary>Opens (creating if needed) the database, switches it to WAL and applies pending migrations.</summary>
    /// <exception cref="DatabaseSchemaException">The schema is newer than this app or was altered.</exception>
    public static VoiceJournalDatabase Open(string path, DatabaseOptions? options = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var db = new VoiceJournalDatabase(full, options ?? new DatabaseOptions());
        db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = cmd.ExecuteScalar() as string;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The database could not be switched to WAL (journal_mode={mode}).");
            return 0;
        });
        Migrator.Apply(db, time ?? TimeProvider.System);
        return db;
    }

    /// <summary>Opens a connection with the per-connection settings applied. The caller disposes it.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = string.Create(
                CultureInfo.InvariantCulture,
                $"PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout={(int)Options.BusyTimeout.TotalMilliseconds};");
            cmd.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="work"/> in an immediate transaction, retrying the whole unit while the DB is busy.</summary>
    /// <exception cref="DatabaseBusyException">Still locked after all retries.</exception>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WithRetry(() =>
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
            var result = work(connection, transaction);
            transaction.Commit();
            return result;
        });
    }

    /// <exception cref="DatabaseBusyException">Still locked after all retries.</exception>
    public T Read<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WithRetry(() =>
        {
            using var connection = OpenConnection();
            return work(connection);
        });
    }

    private T WithRetry<T>(Func<T> unit)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return unit();
            }
            catch (SqliteException ex) when (IsBusy(ex))
            {
                if (attempt >= Options.BusyRetries)
                    throw new DatabaseBusyException($"The database is locked by another writer (after {attempt + 1} attempts).", ex);
                Thread.Sleep(Options.RetryDelay * (attempt + 1));
            }
        }
    }

    private static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is SqliteBusy or SqliteLocked;
}
