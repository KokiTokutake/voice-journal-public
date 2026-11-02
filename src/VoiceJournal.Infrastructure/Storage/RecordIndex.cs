using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Infrastructure.Storage;

public sealed record RecordRebuildReport(int Sessions, int Jobs, int Analyses, int Records, IReadOnlyList<IndexFailure> Failed);

public sealed record RecordJobSummary(
    Guid JobId, Guid SessionId, string RelativePath, string? Title, DateTimeOffset SessionStartedAtUtc, ProcessingState State,
    RecordJobError? Error, string ModelId, int PieceCount, int FailedPieces, DateTimeOffset CreatedAtUtc, DateTimeOffset? EndedAtUtc,
    int? RecordRevision, double? ElapsedSeconds);

/// <summary>
/// Indexes record generation (detail spec 15.2 <c>analyses</c> / <c>records</c> / <c>candidates</c>, plus
/// <c>record_jobs</c>) and keeps <c>sessions.transcript_current_revision</c> and <c>record_review_state</c> up to
/// date. The files are the source of truth; everything here can be rebuilt from them.
/// </summary>
public sealed class RecordIndex
{
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;

    public RecordIndex(Workspace workspace, VoiceJournalDatabase db)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Writes the job's row; when it is the session's newest job, the session's processing state follows it.</summary>
    public void UpsertJob(string sessionDirectory, RecordJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var ledger = JsonSerializer.SerializeToUtf8Bytes(job, AnalysisJson.Options);
        var newestStt = SttJobStore.List(sessionDirectory).Select(j => (DateTimeOffset?)j.CreatedAtUtc).Max();
        var newestRecord = RecordJobStore.List(sessionDirectory).Where(j => j.JobId != job.JobId).Select(j => (DateTimeOffset?)j.CreatedAtUtc).Append(job.CreatedAtUtc).Max();
        var leads = job.CreatedAtUtc == newestRecord && (newestStt is null || job.CreatedAtUtc >= newestStt);
        _db.Write((c, tx) =>
        {
            using var cmd = Command(c, tx, """
                INSERT INTO record_jobs (id, session_id, state, model_id, config_hash, transcript_revision, analysis_revision, record_revision,
                                         piece_count, failed_pieces, attempt, error_code, elapsed_seconds, created_at, started_at, ended_at, ledger_sha256)
                VALUES ($id, $session, $state, $model, $config, $transcript, $analysis, $record, $pieces, $failed, $attempt, $error, $elapsed,
                        $created, $started, $ended, $sha)
                ON CONFLICT (id) DO UPDATE SET state = excluded.state, transcript_revision = excluded.transcript_revision,
                    analysis_revision = excluded.analysis_revision, record_revision = excluded.record_revision, piece_count = excluded.piece_count,
                    failed_pieces = excluded.failed_pieces, attempt = excluded.attempt, error_code = excluded.error_code,
                    elapsed_seconds = excluded.elapsed_seconds, started_at = excluded.started_at, ended_at = excluded.ended_at,
                    ledger_sha256 = excluded.ledger_sha256
                """);
            cmd.Parameters.AddWithValue("$id", DbText.Id(job.JobId));
            cmd.Parameters.AddWithValue("$session", DbText.Id(job.SessionId));
            cmd.Parameters.AddWithValue("$state", DbText.Enum(job.State));
            cmd.Parameters.AddWithValue("$model", job.Settings.ModelId);
            cmd.Parameters.AddWithValue("$config", job.Settings.ComputeHash());
            cmd.Parameters.AddWithValue("$transcript", (object?)job.TranscriptRevision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$analysis", (object?)job.AnalysisRevision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$record", (object?)job.RecordRevision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pieces", job.PieceCount);
            cmd.Parameters.AddWithValue("$failed", job.FailedPieces.Count);
            cmd.Parameters.AddWithValue("$attempt", job.Attempt);
            cmd.Parameters.AddWithValue("$error", job.Error is { } e ? DbText.Enum(e) : DBNull.Value);
            cmd.Parameters.AddWithValue("$elapsed", (object?)job.ElapsedSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", DbText.Utc(job.CreatedAtUtc));
            cmd.Parameters.AddWithValue("$started", job.StartedAtUtc is { } s ? DbText.Utc(s) : DBNull.Value);
            cmd.Parameters.AddWithValue("$ended", job.EndedAtUtc is { } en ? DbText.Utc(en) : DBNull.Value);
            cmd.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(ledger)));
            cmd.ExecuteNonQuery();
            if (leads)
            {
                using var state = Command(c, tx, "UPDATE sessions SET processing_state = $state WHERE id = $id");
                state.Parameters.AddWithValue("$state", DbText.Enum(job.State));
                state.Parameters.AddWithValue("$id", DbText.Id(job.SessionId));
                state.ExecuteNonQuery();
            }
            return 0;
        });
    }

    /// <summary>
    /// Indexes the session's analyses, records and candidates that are new or changed on disk, and its transcript
    /// in use and review state. Returns (analyses, records) written.
    /// </summary>
    public (int Analyses, int Records) UpsertSession(string sessionDirectory)
    {
        var sessionId = SessionIdOf(sessionDirectory);
        var analyses = new AnalysisStore(sessionDirectory);
        var written = (Analyses: 0, Records: 0);
        foreach (var revision in analyses.Revisions())
        {
            var path = Path.Combine(analyses.RootDirectory, AnalysisStore.FileName(revision));
            var sha = Sha(AtomicFile.ReadAllBytes(path));
            var id = DbText.Id(DbText.DerivedId(sessionId, "analysis/" + revision));
            if (Unchanged("analyses", id, sha)) continue;
            var doc = analyses.Load(revision);
            var notes = doc.CandidateNotes.ToDictionary(n => n.CandidateId, n => n.Flags, StringComparer.Ordinal);
            _db.Write((c, tx) =>
            {
                using (var delete = Command(c, tx, "DELETE FROM candidates WHERE analysis_id = $id"))
                {
                    delete.Parameters.AddWithValue("$id", id);
                    delete.ExecuteNonQuery();
                }
                using (var upsert = Command(c, tx, """
                    INSERT INTO analyses (id, session_id, revision, transcript_revision, schema_version, model_id, model_sha256, prompt_version,
                                          config_hash, validation_state, path, sha256, created_at)
                    VALUES ($id, $session, $rev, $transcript, $schema, $model, $modelSha, $prompt, $config, $validation, $path, $sha, $created)
                    ON CONFLICT (id) DO UPDATE SET transcript_revision = excluded.transcript_revision, schema_version = excluded.schema_version,
                        model_id = excluded.model_id, model_sha256 = excluded.model_sha256, prompt_version = excluded.prompt_version,
                        config_hash = excluded.config_hash, validation_state = excluded.validation_state, path = excluded.path,
                        sha256 = excluded.sha256, created_at = excluded.created_at
                    """))
                {
                    upsert.Parameters.AddWithValue("$id", id);
                    upsert.Parameters.AddWithValue("$session", DbText.Id(sessionId));
                    upsert.Parameters.AddWithValue("$rev", doc.Revision);
                    upsert.Parameters.AddWithValue("$transcript", doc.Analysis.TranscriptRevision);
                    upsert.Parameters.AddWithValue("$schema", doc.Analysis.SchemaVersion);
                    upsert.Parameters.AddWithValue("$model", doc.Provenance.ModelId);
                    upsert.Parameters.AddWithValue("$modelSha", (object?)doc.Provenance.ModelSha256 ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("$prompt", doc.Provenance.PromptVersion);
                    upsert.Parameters.AddWithValue("$config", doc.Provenance.ConfigHash);
                    upsert.Parameters.AddWithValue("$validation", doc.IsPartial ? "partial" : "valid");
                    upsert.Parameters.AddWithValue("$path", _workspace.RelativePathOf(path));
                    upsert.Parameters.AddWithValue("$sha", sha);
                    upsert.Parameters.AddWithValue("$created", DbText.Utc(doc.CreatedAtUtc));
                    upsert.ExecuteNonQuery();
                }
                using var insert = Command(c, tx, """
                    INSERT INTO candidates (id, analysis_id, local_candidate_id, kind, action, title, payload_json, evidence_hash, concerns_json)
                    VALUES ($id, $analysis, $local, $kind, $action, $title, $payload, $evidence, $concerns)
                    """);
                foreach (var candidate in doc.Analysis.Candidates)
                {
                    insert.Parameters.Clear();
                    insert.Parameters.AddWithValue("$id", DbText.Id(DbText.DerivedId(Guid.Parse(id), candidate.CandidateId)));
                    insert.Parameters.AddWithValue("$analysis", id);
                    insert.Parameters.AddWithValue("$local", candidate.CandidateId);
                    insert.Parameters.AddWithValue("$kind", DbText.Enum(candidate.Kind));
                    insert.Parameters.AddWithValue("$action", DbText.Enum(candidate.Action));
                    insert.Parameters.AddWithValue("$title", candidate.Fields.Title);
                    insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(candidate, AnalysisJson.Compact));
                    insert.Parameters.AddWithValue("$evidence", Sha(JsonSerializer.SerializeToUtf8Bytes(candidate.Evidence, AnalysisJson.Compact)));
                    insert.Parameters.AddWithValue("$concerns", JsonSerializer.Serialize(notes.GetValueOrDefault(candidate.CandidateId) ?? [], AnalysisJson.Compact));
                    insert.ExecuteNonQuery();
                }
                return 0;
            });
            written.Analyses++;
        }

        var records = new RecordStore(sessionDirectory);
        var review = records.ReadReview();
        foreach (var revision in records.Revisions())
        {
            var path = Path.Combine(records.RevisionDirectory(revision), RecordStore.DocumentFileName);
            var sha = Sha(AtomicFile.ReadAllBytes(path));
            var id = DbText.Id(DbText.DerivedId(sessionId, "record/" + revision));
            var approvedAt = review.Approved is { } a && a.Revision == revision ? DbText.Utc(a.ApprovedAtUtc) : null;
            if (Unchanged("records", id, sha, approvedAt)) continue;
            var doc = records.Load(revision);
            _db.Write((c, tx) =>
            {
                using var upsert = Command(c, tx, """
                    INSERT INTO records (id, session_id, revision, parent_revision, kind, analysis_revision, transcript_revision, title, path, sha256, approved_at, created_at)
                    VALUES ($id, $session, $rev, $parent, $kind, $analysis, $transcript, $title, $path, $sha, $approved, $created)
                    ON CONFLICT (id) DO UPDATE SET parent_revision = excluded.parent_revision, kind = excluded.kind,
                        analysis_revision = excluded.analysis_revision, transcript_revision = excluded.transcript_revision, title = excluded.title,
                        path = excluded.path, sha256 = excluded.sha256, approved_at = excluded.approved_at, created_at = excluded.created_at
                    """);
                upsert.Parameters.AddWithValue("$id", id);
                upsert.Parameters.AddWithValue("$session", DbText.Id(sessionId));
                upsert.Parameters.AddWithValue("$rev", doc.Revision);
                upsert.Parameters.AddWithValue("$parent", (object?)doc.ParentRevision ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$kind", DbText.Enum(doc.Kind));
                upsert.Parameters.AddWithValue("$analysis", (object?)doc.Provenance.AnalysisRevision ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$transcript", doc.TranscriptRevision);
                upsert.Parameters.AddWithValue("$title", doc.Title);
                upsert.Parameters.AddWithValue("$path", _workspace.RelativePathOf(path));
                upsert.Parameters.AddWithValue("$sha", sha);
                upsert.Parameters.AddWithValue("$approved", (object?)approvedAt ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$created", DbText.Utc(doc.CreatedAtUtc));
                upsert.ExecuteNonQuery();
                return 0;
            });
            written.Records++;
        }

        var current = new TranscriptStore(sessionDirectory).CurrentRevision();
        var status = records.Status(current);
        _db.Write((c, tx) =>
        {
            using var cmd = Command(c, tx, "UPDATE sessions SET transcript_current_revision = $current, record_review_state = $review WHERE id = $id");
            cmd.Parameters.AddWithValue("$current", (object?)current ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$review", DbText.Enum(status.State));
            cmd.Parameters.AddWithValue("$id", DbText.Id(sessionId));
            cmd.ExecuteNonQuery();
            return 0;
        });
        return written;
    }

    /// <summary>Re-indexes every session's record jobs, analyses and records from the files.</summary>
    public RecordRebuildReport Rebuild()
    {
        int sessions = 0, jobs = 0, analyses = 0, records = 0;
        var failed = new List<IndexFailure>();
        foreach (var dir in SessionDirectories())
        {
            try
            {
                if (!SessionIndexed(dir)) continue;
                var list = RecordJobStore.List(dir);
                foreach (var job in list) UpsertJob(dir, job);
                jobs += list.Count;
                var (a, r) = UpsertSession(dir);
                analyses += a;
                records += r;
                sessions++;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or SqliteException)
            {
                failed.Add(new IndexFailure(_workspace.RelativePathOf(dir), $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return new RecordRebuildReport(sessions, jobs, analyses, records, failed);
    }

    /// <summary>Record jobs left Queued / Organizing / Validating (to resume at startup), read from their ledgers.</summary>
    public IReadOnlyList<RecordJobChange> UnfinishedJobs()
    {
        var rows = _db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT j.id, s.relative_path FROM record_jobs j JOIN sessions s ON s.id = j.session_id
                WHERE j.state IN ('queued', 'organizing', 'validating') ORDER BY j.created_at
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<(Guid, string)>();
            while (r.Read()) list.Add((Guid.Parse(r.GetString(0)), r.GetString(1)));
            return list;
        });
        var result = new List<RecordJobChange>();
        foreach (var (id, relative) in rows)
        {
            var dir = Path.Combine(_workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var job = RecordJobStore.Load(dir, id);
                if (ProcessingStateMachine.IsActive(job.State)) result.Add(new RecordJobChange(dir, job));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // A ledger that cannot be read is not resumed; the rebuild reports it.
            }
        }
        return result;
    }

    /// <summary>The newest record jobs, newest first.</summary>
    public IReadOnlyList<RecordJobSummary> RecentJobs(int limit) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT j.id, j.session_id, s.relative_path, s.title, s.started_at, j.state, j.error_code, j.model_id, j.piece_count, j.failed_pieces,
                   j.created_at, j.ended_at, j.record_revision, j.elapsed_seconds
            FROM record_jobs j JOIN sessions s ON s.id = j.session_id
            ORDER BY j.created_at DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<RecordJobSummary>();
        while (r.Read())
        {
            list.Add(new RecordJobSummary(
                Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                DbText.ParseUtc(r.GetString(4)), DbText.ParseEnum<ProcessingState>(r.GetString(5)),
                r.IsDBNull(6) ? null : DbText.ParseEnum<RecordJobError>(r.GetString(6)), r.GetString(7), (int)r.GetInt64(8), (int)r.GetInt64(9),
                DbText.ParseUtc(r.GetString(10)), r.IsDBNull(11) ? null : DbText.ParseUtc(r.GetString(11)),
                r.IsDBNull(12) ? null : (int)r.GetInt64(12), r.IsDBNull(13) ? null : r.GetDouble(13)));
        }
        return list;
    });

    private bool Unchanged(string table, string id, string sha, string? approvedAt = null) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = table == "records"
            ? "SELECT 1 FROM records WHERE id = $id AND sha256 = $sha AND approved_at IS $approved"
            : "SELECT 1 FROM analyses WHERE id = $id AND sha256 = $sha";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sha", sha);
        if (table == "records") cmd.Parameters.AddWithValue("$approved", (object?)approvedAt ?? DBNull.Value);
        return cmd.ExecuteScalar() is not null;
    });

    private bool SessionIndexed(string sessionDirectory) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", DbText.Id(SessionIdOf(sessionDirectory)));
        return cmd.ExecuteScalar() is not null;
    });

    private static Guid SessionIdOf(string sessionDirectory) =>
        Guid.TryParseExact(Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDirectory)), "D", out var id)
            ? id
            : throw new InvalidDataException($"'{sessionDirectory}' is not a session folder.");

    private IEnumerable<string> SessionDirectories()
    {
        if (!Directory.Exists(_workspace.SessionsRoot)) return [];
        return Directory.EnumerateDirectories(_workspace.SessionsRoot)
            .SelectMany(Directory.EnumerateDirectories)
            .SelectMany(Directory.EnumerateDirectories)
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
}
