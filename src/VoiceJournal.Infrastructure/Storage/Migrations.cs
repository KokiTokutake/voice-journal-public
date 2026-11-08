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

        // Phase E: management review. Unlike the tables above, these are the source of truth (detail spec 11.2), not an
        // index: they are never rebuilt from files. Candidates are named by their key (session/analysis revision/candidate
        // id, 12.2-2) because the candidates table above is an index that is deleted and written again.
        new(4, "management", """
            CREATE TABLE display_counters (
                workspace_id  TEXT    NOT NULL REFERENCES workspaces (id),
                kind          TEXT    NOT NULL CHECK (kind IN ('issue', 'task')),
                next_number   INTEGER NOT NULL CHECK (next_number >= 1 AND next_number <= 1000000),
                PRIMARY KEY (workspace_id, kind)
            ) STRICT;

            CREATE TABLE items (
                id               TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                workspace_id     TEXT    NOT NULL REFERENCES workspaces (id),
                display_id       TEXT    NOT NULL CHECK (display_id GLOB 'ISS-[0-9][0-9][0-9][0-9][0-9][0-9]' OR display_id GLOB 'TSK-[0-9][0-9][0-9][0-9][0-9][0-9]'),
                kind             TEXT    NOT NULL CHECK (kind IN ('issue', 'task')),
                project          TEXT    NOT NULL,
                title            TEXT    NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
                description      TEXT    NOT NULL CHECK (length(description) <= 8000),
                status           TEXT    NOT NULL CHECK ((kind = 'issue' AND status IN ('open', 'in_progress', 'blocked', 'resolved', 'closed'))
                                                      OR (kind = 'task' AND status IN ('todo', 'in_progress', 'blocked', 'done', 'cancelled'))),
                priority         TEXT    CHECK (priority IS NULL OR priority IN ('low', 'medium', 'high', 'critical')),
                assignee         TEXT,
                due_date         TEXT    CHECK (due_date IS NULL OR due_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
                parent_issue_id  TEXT    REFERENCES items (id) CHECK (parent_issue_id IS NULL OR (kind = 'task' AND parent_issue_id <> id)),
                revision         INTEGER NOT NULL CHECK (revision >= 1),
                created_at       TEXT    NOT NULL,
                updated_at       TEXT    NOT NULL CHECK (updated_at >= created_at),
                UNIQUE (workspace_id, display_id)
            ) STRICT;
            CREATE INDEX ix_items_search ON items (workspace_id, kind, status, due_date);

            CREATE TABLE item_evidence (
                id                   TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                item_id              TEXT    NOT NULL REFERENCES items (id),
                session_id           TEXT    NOT NULL CHECK (length(session_id) = 36),
                transcript_revision  INTEGER NOT NULL CHECK (transcript_revision >= 1),
                segment_id           TEXT    NOT NULL CHECK (length(segment_id) = 36),
                quote                TEXT    NOT NULL,
                batch_id             TEXT    CHECK (batch_id IS NULL OR length(batch_id) = 36),
                UNIQUE (item_id, session_id, segment_id)
            ) STRICT;

            CREATE TABLE managed_files (
                id            TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                workspace_id  TEXT NOT NULL REFERENCES workspaces (id),
                kind          TEXT NOT NULL CHECK (kind IN ('issue', 'task')),
                path          TEXT NOT NULL,
                mode          TEXT NOT NULL CHECK (mode IN ('managed_blocks', 'append_only')),
                last_hash     TEXT CHECK (last_hash IS NULL OR (length(last_hash) = 64 AND last_hash NOT GLOB '*[^0-9a-f]*')),
                created_at    TEXT NOT NULL,
                UNIQUE (workspace_id, path),
                UNIQUE (workspace_id, kind)
            ) STRICT;

            CREATE TABLE candidate_reviews (
                candidate_key        TEXT    NOT NULL PRIMARY KEY,
                session_id           TEXT    NOT NULL CHECK (length(session_id) = 36),
                analysis_revision    INTEGER NOT NULL CHECK (analysis_revision >= 1),
                candidate_id         TEXT    NOT NULL CHECK (candidate_id GLOB 'C[0-9][0-9][0-9][0-9]'),
                choice               TEXT    CHECK (choice IS NULL OR choice IN ('create', 'update', 'mention', 'reject', 'defer')),
                target_item_id       TEXT    CHECK (target_item_id IS NULL OR length(target_item_id) = 36),
                target_revision      INTEGER CHECK (target_revision IS NULL OR target_revision >= 1),
                edited_fields_json   TEXT    CHECK (edited_fields_json IS NULL OR json_valid(edited_fields_json)),
                fields_to_change     TEXT    CHECK (fields_to_change IS NULL OR json_valid(fields_to_change)),
                parent_issue_id      TEXT    CHECK (parent_issue_id IS NULL OR length(parent_issue_id) = 36),
                confirmed            INTEGER NOT NULL DEFAULT 0 CHECK (confirmed IN (0, 1)),
                updated_at           TEXT    NOT NULL,
                UNIQUE (session_id, analysis_revision, candidate_id)
            ) STRICT;

            CREATE TABLE approvals (
                id                         TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                workspace_id               TEXT NOT NULL REFERENCES workspaces (id),
                plan_hash                  TEXT CHECK (plan_hash IS NULL OR (length(plan_hash) = 64 AND plan_hash NOT GLOB '*[^0-9a-f]*')),
                candidate_revisions_json   TEXT NOT NULL CHECK (json_valid(candidate_revisions_json)),
                approved_at                TEXT NOT NULL,
                actor                      TEXT NOT NULL,
                state                      TEXT NOT NULL CHECK (state IN ('planned', 'applied', 'expired', 'abandoned'))
            ) STRICT;

            CREATE TABLE apply_batches (
                id               TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                approval_id      TEXT NOT NULL UNIQUE REFERENCES approvals (id),
                idempotency_key  TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) = 36),
                state            TEXT NOT NULL CHECK (state IN ('planned', 'prepared', 'applying', 'committed', 'recovery_required', 'abandoned')),
                plan_path        TEXT NOT NULL,
                created_at       TEXT NOT NULL,
                committed_at     TEXT,
                error_code       TEXT
            ) STRICT;

            CREATE TABLE apply_file_ops (
                batch_id      TEXT    NOT NULL REFERENCES apply_batches (id),
                sequence      INTEGER NOT NULL CHECK (sequence >= 1),
                file_id       TEXT    NOT NULL REFERENCES managed_files (id),
                operation     TEXT    NOT NULL CHECK (operation IN ('create', 'replace')),
                before_hash   TEXT    CHECK (before_hash IS NULL OR (length(before_hash) = 64 AND before_hash NOT GLOB '*[^0-9a-f]*')),
                after_hash    TEXT    NOT NULL CHECK (length(after_hash) = 64 AND after_hash NOT GLOB '*[^0-9a-f]*'),
                staged_path   TEXT    NOT NULL,
                backup_path   TEXT,
                temp_path     TEXT,
                state         TEXT    NOT NULL CHECK (state IN ('planned', 'prepared', 'replaced', 'verified')),
                PRIMARY KEY (batch_id, sequence)
            ) STRICT;

            CREATE TABLE item_versions (
                item_id      TEXT    NOT NULL REFERENCES items (id),
                revision     INTEGER NOT NULL CHECK (revision >= 1),
                batch_id     TEXT    REFERENCES apply_batches (id),
                before_json  TEXT    CHECK (before_json IS NULL OR json_valid(before_json)),
                after_json   TEXT    NOT NULL CHECK (json_valid(after_json)),
                PRIMARY KEY (item_id, revision)
            ) STRICT;

            CREATE TABLE candidate_applications (
                candidate_key  TEXT NOT NULL UNIQUE,
                fingerprint    TEXT NOT NULL,
                batch_id       TEXT NOT NULL REFERENCES apply_batches (id),
                item_id        TEXT NOT NULL REFERENCES items (id),
                decision_hash  TEXT NOT NULL
            ) STRICT;
            CREATE INDEX ix_candidate_applications_fingerprint ON candidate_applications (fingerprint);

            CREATE TABLE item_proposals (
                id              TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                batch_id        TEXT NOT NULL REFERENCES apply_batches (id),
                candidate_key   TEXT NOT NULL,
                target_item_id  TEXT NOT NULL REFERENCES items (id),
                proposed_json   TEXT NOT NULL CHECK (json_valid(proposed_json)),
                reason          TEXT NOT NULL,
                created_at      TEXT NOT NULL
            ) STRICT;

            CREATE TABLE audit_events (
                id            TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36),
                timestamp     TEXT NOT NULL,
                event_type    TEXT NOT NULL,
                entity_type   TEXT NOT NULL,
                entity_id     TEXT NOT NULL,
                batch_id      TEXT,
                details_json  TEXT NOT NULL CHECK (json_valid(details_json))
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

        // AT-24 / 14.4: a database that already has a schema is backed up (Online Backup API, consistent under WAL)
        // before it is migrated, so a failed or unwanted migration leaves the old version.
        var pending = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();
        if (pending.Count > 0 && applied.Count > 0) Backup(db, time, applied.Keys.Max());

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

    /// <summary>The folder next to the database where backups before migrations are kept.</summary>
    public static string BackupFolder(VoiceJournalDatabase db) => Path.Combine(Path.GetDirectoryName(db.FilePath)!, "backups");

    private static void Backup(VoiceJournalDatabase db, TimeProvider time, int version)
    {
        var folder = BackupFolder(db);
        Directory.CreateDirectory(folder);
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"voicejournal-v{version}-{stamp}.db");
        using var source = db.OpenConnection();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
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
