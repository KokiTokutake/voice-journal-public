using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>One forward-only schema step. Its SQL must never change once released (the hash is checked).</summary>
internal sealed record Migration(int Version, string Name, string Sql)
{
    public string Hash { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
}

/// <summary>Schema of the workspace database (detail spec 15.2). Only the tables Phase B needs exist so far.</summary>
internal static class Migrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "sessions_and_audio", """
            CREATE TABLE workspaces (
                id              TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                name            TEXT    NOT NULL,
                schema_version  INTEGER NOT NULL CHECK (schema_version >= 1),
                root_path       TEXT    NOT NULL,
                created_at      TEXT    NOT NULL
            ) STRICT;

            CREATE TABLE sessions (
                id                   TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                workspace_id         TEXT NOT NULL REFERENCES workspaces (id),
                relative_path        TEXT NOT NULL UNIQUE,
                title                TEXT,
                project              TEXT,
                started_at           TEXT NOT NULL,
                ended_at             TEXT CHECK (ended_at IS NULL OR ended_at >= started_at),
                timezone             TEXT NOT NULL,
                capture_mode         TEXT NOT NULL CHECK (capture_mode IN ('mic', 'loopback', 'both')),
                mic_mode             TEXT CHECK (mic_mode IS NULL OR mic_mode IN ('raw', 'normal')),
                capture_state        TEXT NOT NULL CHECK (capture_state IN ('ready', 'recording', 'partial', 'stopping', 'finalized', 'interrupted', 'failed')),
                processing_state     TEXT NOT NULL DEFAULT 'none',
                record_review_state  TEXT NOT NULL DEFAULT 'none',
                manifest_sha256      TEXT NOT NULL CHECK (length(manifest_sha256) = 64 AND manifest_sha256 NOT GLOB '*[^0-9a-f]*'),
                indexed_at           TEXT NOT NULL
            ) STRICT;
            CREATE INDEX ix_sessions_started_at ON sessions (workspace_id, started_at);
            CREATE INDEX ix_sessions_project ON sessions (workspace_id, project, started_at);

            CREATE TABLE audio_tracks (
                id               TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                session_id       TEXT    NOT NULL REFERENCES sessions (id),
                source           TEXT    NOT NULL CHECK (source IN ('mic', 'loopback', 'import')),
                device_id        TEXT    NOT NULL,
                device_name      TEXT    NOT NULL,
                format_json      TEXT    NOT NULL CHECK (json_valid(format_json)),
                sample_rate      INTEGER NOT NULL CHECK (sample_rate > 0),
                start_offset_ms  INTEGER NOT NULL CHECK (start_offset_ms >= 0),
                state            TEXT    NOT NULL CHECK (state IN ('recording', 'disconnected', 'stopped', 'failed')),
                UNIQUE (session_id, source)
            ) STRICT;

            CREATE TABLE audio_chunks (
                id             TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                track_id       TEXT    NOT NULL REFERENCES audio_tracks (id),
                sequence       INTEGER NOT NULL CHECK (sequence >= 1),
                relative_path  TEXT    NOT NULL,
                start_ms       INTEGER NOT NULL CHECK (start_ms >= 0),
                frame_count    INTEGER NOT NULL CHECK (frame_count >= 0),
                size           INTEGER NOT NULL CHECK (size >= 0),
                sha256         TEXT    CHECK (sha256 IS NULL OR (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*')),
                clock_start_ms INTEGER,
                clock_end_ms   INTEGER CHECK (clock_end_ms IS NULL OR clock_start_ms IS NULL OR clock_end_ms >= clock_start_ms),
                state          TEXT    NOT NULL CHECK (state IN ('partial', 'completed', 'recovered')),
                UNIQUE (track_id, sequence)
            ) STRICT;

            CREATE TABLE audio_events (
                id           TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                track_id     TEXT    NOT NULL REFERENCES audio_tracks (id),
                ordinal      INTEGER NOT NULL CHECK (ordinal >= 1),
                start_ms     INTEGER NOT NULL,
                end_ms       INTEGER CHECK (end_ms IS NULL OR end_ms >= start_ms),
                kind         TEXT    NOT NULL,
                detail_json  TEXT    CHECK (detail_json IS NULL OR json_valid(detail_json)),
                UNIQUE (track_id, ordinal)
            ) STRICT;
            CREATE INDEX ix_audio_events_kind ON audio_events (kind, track_id);
            """),
    ];
}

/// <summary>Applies pending migrations in order; refuses databases written by a newer or altered schema.</summary>
internal static class Migrator
{
    private const string Bootstrap = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version         INTEGER NOT NULL PRIMARY KEY,
            name            TEXT    NOT NULL,
            applied_at      TEXT    NOT NULL,
            app_version     TEXT    NOT NULL,
            migration_hash  TEXT    NOT NULL CHECK (length(migration_hash) = 64)
        ) STRICT;
        """;

    /// <returns>The number of migrations applied now.</returns>
    public static int Apply(VoiceJournalDatabase db, TimeProvider time, IReadOnlyList<Migration>? migrations = null)
    {
        migrations ??= Migrations.All;
        db.Write((c, tx) => Execute(c, tx, Bootstrap));
        var applied = db.Read(ReadApplied);

        var known = migrations.ToDictionary(m => m.Version);
        foreach (var (version, hash) in applied)
        {
            if (!known.TryGetValue(version, out var migration))
                throw new DatabaseSchemaException($"The database has schema version {version}, which this app does not know. It was written by a newer version.");
            if (!string.Equals(migration.Hash, hash, StringComparison.Ordinal))
                throw new DatabaseSchemaException($"Migration {version} in the database does not match this app (hash differs).");
        }

        var count = 0;
        foreach (var migration in migrations.OrderBy(m => m.Version).Where(m => !applied.ContainsKey(m.Version)))
        {
            var ran = db.Write((c, tx) =>
            {
                // Another process may have applied it between the read and this write lock.
                using (var check = c.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = $v";
                    check.Parameters.AddWithValue("$v", migration.Version);
                    if (Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0) return false;
                }
                Execute(c, tx, migration.Sql);
                using var insert = c.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO schema_migrations (version, name, applied_at, app_version, migration_hash) VALUES ($v, $n, $at, $app, $h)";
                insert.Parameters.AddWithValue("$v", migration.Version);
                insert.Parameters.AddWithValue("$n", migration.Name);
                insert.Parameters.AddWithValue("$at", DbText.Utc(time.GetUtcNow()));
                insert.Parameters.AddWithValue("$app", db.Options.AppVersion);
                insert.Parameters.AddWithValue("$h", migration.Hash);
                insert.ExecuteNonQuery();
                return true;
            });
            if (ran) count++;
        }
        return count;
    }

    private static Dictionary<int, string> ReadApplied(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version, migration_hash FROM schema_migrations";
        using var reader = cmd.ExecuteReader();
        var applied = new Dictionary<int, string>();
        while (reader.Read()) applied[reader.GetInt32(0)] = reader.GetString(1);
        return applied;
    }

    private static int Execute(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }
}
