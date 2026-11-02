using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>One forward-only schema step. Its SQL must never change once released (the hash is checked).</summary>
internal sealed record Migration(int Version, string Name, string Sql)
{
    public string Hash { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
}

/// <summary>Schema of the workspace database (detail spec 15.2). Tables are added phase by phase.</summary>
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
        // Phase C: processing jobs and transcript revisions. Indexes of stt/native/*/job.json and transcripts/rev-*.
        new(2, "jobs_and_transcripts", """
            CREATE TABLE jobs (
                id               TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                session_id       TEXT    NOT NULL REFERENCES sessions (id),
                stage            TEXT    NOT NULL CHECK (stage IN ('stt')),
                kind             TEXT    NOT NULL CHECK (kind IN ('full', 'range')),
                input_hash       TEXT    CHECK (input_hash IS NULL OR (length(input_hash) = 64 AND input_hash NOT GLOB '*[^0-9a-f]*')),
                config_hash      TEXT    NOT NULL CHECK (length(config_hash) = 64 AND config_hash NOT GLOB '*[^0-9a-f]*'),
                model_name       TEXT    NOT NULL,
                state            TEXT    NOT NULL CHECK (state IN ('queued', 'normalizing', 'transcribing', 'transcribed', 'failed', 'cancelled')),
                checkpoint_json  TEXT    NOT NULL CHECK (json_valid(checkpoint_json)),
                attempt          INTEGER NOT NULL CHECK (attempt >= 0),
                error_code       TEXT,
                result_revision  INTEGER CHECK (result_revision IS NULL OR result_revision >= 1),
                created_at       TEXT    NOT NULL,
                started_at       TEXT,
                ended_at         TEXT,
                ledger_sha256    TEXT    NOT NULL CHECK (length(ledger_sha256) = 64)
            ) STRICT;
            CREATE INDEX ix_jobs_state ON jobs (state, created_at);
            CREATE INDEX ix_jobs_session ON jobs (session_id, created_at);

            CREATE TABLE transcript_revisions (
                id               TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                session_id       TEXT    NOT NULL REFERENCES sessions (id),
                revision         INTEGER NOT NULL CHECK (revision >= 1),
                parent_revision  INTEGER CHECK (parent_revision IS NULL OR parent_revision < revision),
                raw_revision     INTEGER NOT NULL CHECK (raw_revision >= 1 AND raw_revision <= revision),
                kind             TEXT    NOT NULL CHECK (kind IN ('raw', 'edited')),
                source_job_id    TEXT    CHECK (source_job_id IS NULL OR length(source_job_id) = 36),
                path             TEXT    NOT NULL,
                sha256           TEXT    NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
                created_at       TEXT    NOT NULL,
                UNIQUE (session_id, revision)
            ) STRICT;

            CREATE TABLE segments (
                id                      TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                transcript_revision_id  TEXT    NOT NULL REFERENCES transcript_revisions (id),
                stable_segment_id       TEXT    NOT NULL CHECK (length(stable_segment_id) = 36),
                source                  TEXT    NOT NULL CHECK (source IN ('mic', 'loopback', 'import')),
                start_ms                INTEGER NOT NULL CHECK (start_ms >= 0),
                end_ms                  INTEGER NOT NULL CHECK (end_ms > start_ms),
                text                    TEXT    NOT NULL,
                flags_json              TEXT    NOT NULL CHECK (json_valid(flags_json)),
                UNIQUE (transcript_revision_id, stable_segment_id)
            ) STRICT;
            CREATE INDEX ix_segments_time ON segments (transcript_revision_id, start_ms);
            """),

        // Phase D: record generation. Indexes of analysis/work/*/job.json, analysis/rev-*.json, records/rev-*/record.json,
        // records/review.json and transcripts/current.json (the transcript revision in use).
        new(3, "records", """
            ALTER TABLE sessions ADD COLUMN transcript_current_revision INTEGER CHECK (transcript_current_revision IS NULL OR transcript_current_revision >= 1);

            CREATE TABLE record_jobs (
                id                   TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                session_id           TEXT    NOT NULL REFERENCES sessions (id),
                state                TEXT    NOT NULL CHECK (state IN ('queued', 'organizing', 'validating', 'ready_for_review', 'not_analyzable', 'failed', 'cancelled')),
                model_id             TEXT    NOT NULL,
                config_hash          TEXT    NOT NULL CHECK (length(config_hash) = 64 AND config_hash NOT GLOB '*[^0-9a-f]*'),
                transcript_revision  INTEGER CHECK (transcript_revision IS NULL OR transcript_revision >= 1),
                analysis_revision    INTEGER CHECK (analysis_revision IS NULL OR analysis_revision >= 1),
                record_revision      INTEGER CHECK (record_revision IS NULL OR record_revision >= 1),
                piece_count          INTEGER NOT NULL CHECK (piece_count >= 0),
                failed_pieces        INTEGER NOT NULL CHECK (failed_pieces >= 0),
                attempt              INTEGER NOT NULL CHECK (attempt >= 0),
                error_code           TEXT,
                elapsed_seconds      REAL,
                created_at           TEXT    NOT NULL,
                started_at           TEXT,
                ended_at             TEXT,
                ledger_sha256        TEXT    NOT NULL CHECK (length(ledger_sha256) = 64)
            ) STRICT;
            CREATE INDEX ix_record_jobs_state ON record_jobs (state, created_at);
            CREATE INDEX ix_record_jobs_session ON record_jobs (session_id, created_at);

            CREATE TABLE analyses (
                id                   TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                session_id           TEXT    NOT NULL REFERENCES sessions (id),
                revision             INTEGER NOT NULL CHECK (revision >= 1),
                transcript_revision  INTEGER NOT NULL CHECK (transcript_revision >= 1),
                schema_version       TEXT    NOT NULL,
                model_id             TEXT    NOT NULL,
                model_sha256         TEXT    CHECK (model_sha256 IS NULL OR (length(model_sha256) = 64 AND model_sha256 NOT GLOB '*[^0-9a-f]*')),
                prompt_version       TEXT    NOT NULL,
                config_hash          TEXT    NOT NULL CHECK (length(config_hash) = 64 AND config_hash NOT GLOB '*[^0-9a-f]*'),
                validation_state     TEXT    NOT NULL CHECK (validation_state IN ('valid', 'partial')),
                path                 TEXT    NOT NULL,
                sha256               TEXT    NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
                created_at           TEXT    NOT NULL,
                UNIQUE (session_id, revision)
            ) STRICT;

            CREATE TABLE records (
                id                   TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                session_id           TEXT    NOT NULL REFERENCES sessions (id),
                revision             INTEGER NOT NULL CHECK (revision >= 1),
                parent_revision      INTEGER CHECK (parent_revision IS NULL OR parent_revision < revision),
                kind                 TEXT    NOT NULL CHECK (kind IN ('generated', 'edited', 'section_regenerated')),
                analysis_revision    INTEGER CHECK (analysis_revision IS NULL OR analysis_revision >= 1),
                transcript_revision  INTEGER NOT NULL CHECK (transcript_revision >= 1),
                title                TEXT    NOT NULL,
                path                 TEXT    NOT NULL,
                sha256               TEXT    NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
                approved_at          TEXT,
                created_at           TEXT    NOT NULL,
                UNIQUE (session_id, revision)
            ) STRICT;

            CREATE TABLE candidates (
                id                   TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                analysis_id          TEXT    NOT NULL REFERENCES analyses (id),
                local_candidate_id   TEXT    NOT NULL CHECK (local_candidate_id GLOB 'C[0-9][0-9][0-9][0-9]'),
                kind                 TEXT    NOT NULL CHECK (kind IN ('issue', 'task')),
                action               TEXT    NOT NULL CHECK (action IN ('create', 'update', 'mention')),
                title                TEXT    NOT NULL,
                payload_json         TEXT    NOT NULL CHECK (json_valid(payload_json)),
                evidence_hash        TEXT    NOT NULL CHECK (length(evidence_hash) = 64 AND evidence_hash NOT GLOB '*[^0-9a-f]*'),
                concerns_json        TEXT    NOT NULL CHECK (json_valid(concerns_json)),
                decision             TEXT    NOT NULL DEFAULT 'proposed' CHECK (decision IN ('proposed', 'accepted', 'edited', 'rejected', 'deferred', 'applied')),
                UNIQUE (analysis_id, local_candidate_id)
            ) STRICT;
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
