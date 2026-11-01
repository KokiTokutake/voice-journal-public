using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Infrastructure.Storage;

public sealed record TranscriptRebuildReport(int Sessions, int Jobs, int Revisions, IReadOnlyList<IndexFailure> Failed);

/// <summary>A job row for the processing screen (UI-03).</summary>
public sealed record JobSummary(
    Guid JobId, Guid SessionId, string RelativePath, string? Title, DateTimeOffset SessionStartedAtUtc, SttJobKind Kind,
    ProcessingState State, SttErrorCode? Error, string ModelName, DateTimeOffset CreatedAtUtc, DateTimeOffset? EndedAtUtc, int? ResultRevision);

/// <summary>
/// Indexes processing jobs and transcript revisions (detail spec 15.2 <c>jobs</c> / <c>transcript_revisions</c> /
/// <c>segments</c>) and keeps <c>sessions.processing_state</c> up to date. The ledgers and revision files are the
/// source of truth; everything here can be rebuilt from them. The session row must exist (see <see cref="SessionIndex"/>).
/// </summary>
public sealed class TranscriptIndex
{
    private static readonly JsonSerializerOptions CompactJson = new(VoiceJournalJson.Options) { WriteIndented = false };

    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;

    public TranscriptIndex(Workspace workspace, VoiceJournalDatabase db)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Writes the job's row and the session's processing state (from all of its ledgers).</summary>
    public void UpsertJob(string sessionDirectory, SttJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var state = SttJobStore.SessionState(SttJobStore.List(sessionDirectory).Where(j => j.JobId != job.JobId).Append(job));
        var ledger = JsonSerializer.SerializeToUtf8Bytes(job, VoiceJournalJson.Options);
        _db.Write((c, tx) =>
        {
            WriteJob(c, tx, job, ledger);
            SetProcessingState(c, tx, job.SessionId, state);
            return 0;
        });
    }

    /// <summary>Indexes the revisions of a session that are not indexed yet (or changed on disk).</summary>
    /// <returns>The number of revisions written.</returns>
    public int UpsertRevisions(string sessionDirectory)
    {
        var store = new TranscriptStore(sessionDirectory);
        var count = 0;
        foreach (var revision in store.Revisions())
        {
            var dir = store.RevisionDirectory(revision);
            var file = File.Exists(Path.Combine(dir, TranscriptStore.RawFileName)) ? TranscriptStore.RawFileName : TranscriptStore.EditedFileName;
            var bytes = AtomicFile.ReadAllBytes(Path.Combine(dir, file));
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var doc = store.Load(revision);
            var id = DbText.Id(DbText.DerivedId(doc.SessionId, "transcript/" + revision));
            var written = _db.Write((c, tx) =>
            {
                using (var existing = Command(c, tx, "SELECT sha256 FROM transcript_revisions WHERE id = $id"))
                {
                    existing.Parameters.AddWithValue("$id", id);
                    if (existing.ExecuteScalar() as string == sha) return false;
                }
                using (var delete = Command(c, tx, "DELETE FROM segments WHERE transcript_revision_id = $id"))
                {
                    delete.Parameters.AddWithValue("$id", id);
                    delete.ExecuteNonQuery();
                }
                using (var upsert = Command(c, tx, """
                    INSERT INTO transcript_revisions (id, session_id, revision, parent_revision, raw_revision, kind, source_job_id, path, sha256, created_at)
                    VALUES ($id, $session, $rev, $parent, $raw, $kind, $job, $path, $sha, $created)
                    ON CONFLICT (id) DO UPDATE SET parent_revision = excluded.parent_revision, raw_revision = excluded.raw_revision,
                        kind = excluded.kind, source_job_id = excluded.source_job_id, path = excluded.path, sha256 = excluded.sha256,
                        created_at = excluded.created_at
                    """))
                {
                    upsert.Parameters.AddWithValue("$id", id);
                    upsert.Parameters.AddWithValue("$session", DbText.Id(doc.SessionId));
                    upsert.Parameters.AddWithValue("$rev", doc.Revision);
                    upsert.Parameters.AddWithValue("$parent", (object?)doc.ParentRevision ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("$raw", doc.RawRevision);
                    upsert.Parameters.AddWithValue("$kind", DbText.Enum(doc.Kind));
                    var job = doc.Provenance?.JobId ?? doc.Changes.Select(ch => ch.Provenance?.JobId).FirstOrDefault(j => j is not null);
                    upsert.Parameters.AddWithValue("$job", job is { } j ? DbText.Id(j) : DBNull.Value);
                    upsert.Parameters.AddWithValue("$path", _workspace.RelativePathOf(Path.Combine(dir, file)));
                    upsert.Parameters.AddWithValue("$sha", sha);
                    upsert.Parameters.AddWithValue("$created", DbText.Utc(doc.CreatedAtUtc));
                    upsert.ExecuteNonQuery();
                }
                using var insert = Command(c, tx, """
                    INSERT INTO segments (id, transcript_revision_id, stable_segment_id, source, start_ms, end_ms, text, flags_json)
                    VALUES ($id, $rev, $stable, $source, $start, $end, $text, $flags)
                    """);
                var pId = insert.Parameters.Add("$id", SqliteType.Text);
                insert.Parameters.AddWithValue("$rev", id);
                var pStable = insert.Parameters.Add("$stable", SqliteType.Text);
                var pSource = insert.Parameters.Add("$source", SqliteType.Text);
                var pStart = insert.Parameters.Add("$start", SqliteType.Integer);
                var pEnd = insert.Parameters.Add("$end", SqliteType.Integer);
                var pText = insert.Parameters.Add("$text", SqliteType.Text);
                var pFlags = insert.Parameters.Add("$flags", SqliteType.Text);
                foreach (var s in doc.Segments)
                {
                    pId.Value = DbText.Id(DbText.DerivedId(Guid.Parse(id), s.SegmentId.ToString("D")));
                    pStable.Value = DbText.Id(s.SegmentId);
                    pSource.Value = DbText.Enum(s.Source);
                    pStart.Value = s.StartMs;
                    pEnd.Value = s.EndMs;
                    pText.Value = s.Text;
                    pFlags.Value = JsonSerializer.Serialize(new { schema_version = 1, flags = s.QualityFlags }, CompactJson);
                    insert.ExecuteNonQuery();
                }
                return true;
            });
            if (written) count++;
        }
        return count;
    }

    /// <summary>Re-indexes every session's jobs and revisions from the files.</summary>
    public TranscriptRebuildReport Rebuild()
    {
        int sessions = 0, jobs = 0, revisions = 0;
        var failed = new List<IndexFailure>();
        foreach (var dir in SessionDirectories())
        {
            var relative = _workspace.RelativePathOf(dir);
            try
            {
                if (!SessionIndexed(dir)) continue; // the session index reports it
                var list = SttJobStore.List(dir);
                foreach (var job in list) UpsertJob(dir, job);
                jobs += list.Count;
                revisions += UpsertRevisions(dir);
                if (list.Count == 0)
                    _db.Write((c, tx) =>
                    {
                        SetProcessingState(c, tx, SessionIdOf(dir), ProcessingState.None);
                        return 0;
                    });
                sessions++;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or SqliteException)
            {
                failed.Add(new IndexFailure(relative, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return new TranscriptRebuildReport(sessions, jobs, revisions, failed);
    }

    /// <summary>Jobs left Queued / Normalizing / Transcribing (to resume at startup), read from their ledgers.</summary>
    public IReadOnlyList<SttJobChange> UnfinishedJobs()
    {
        var rows = _db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT j.id, s.relative_path FROM jobs j JOIN sessions s ON s.id = j.session_id
                WHERE j.state IN ('queued', 'normalizing', 'transcribing') ORDER BY j.created_at
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<(Guid, string)>();
            while (r.Read()) list.Add((Guid.Parse(r.GetString(0)), r.GetString(1)));
            return list;
        });
        var result = new List<SttJobChange>();
        foreach (var (id, relative) in rows)
        {
            var dir = Path.Combine(_workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var job = SttJobStore.Load(dir, id);
                if (ProcessingStateMachine.IsActive(job.State)) result.Add(new SttJobChange(dir, job));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // A ledger that cannot be read is not resumed; the rebuild reports it.
            }
        }
        return result;
    }

    /// <summary>The newest jobs, newest first.</summary>
    public IReadOnlyList<JobSummary> RecentJobs(int limit) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT j.id, j.session_id, s.relative_path, s.title, s.started_at, j.kind, j.state, j.error_code, j.model_name,
                   j.created_at, j.ended_at, j.result_revision
            FROM jobs j JOIN sessions s ON s.id = j.session_id
            ORDER BY j.created_at DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<JobSummary>();
        while (r.Read())
        {
            list.Add(new JobSummary(
                Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                DbText.ParseUtc(r.GetString(4)), DbText.ParseEnum<SttJobKind>(r.GetString(5)), DbText.ParseEnum<ProcessingState>(r.GetString(6)),
                r.IsDBNull(7) ? null : DbText.ParseEnum<SttErrorCode>(r.GetString(7)), r.GetString(8), DbText.ParseUtc(r.GetString(9)),
                r.IsDBNull(10) ? null : DbText.ParseUtc(r.GetString(10)), r.IsDBNull(11) ? null : r.GetInt32(11)));
        }
        return list;
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

    private static void WriteJob(SqliteConnection c, SqliteTransaction tx, SttJob job, byte[] ledger)
    {
        var checkpoint = new
        {
            schema_version = 1,
            pieces = job.Pieces.Select(p => new { source = DbText.Enum(p.Source), p.Index, state = DbText.Enum(p.State), p.Attempts, elapsed_ms = p.ElapsedMs }),
        };
        using var cmd = Command(c, tx, """
            INSERT INTO jobs (id, session_id, stage, kind, input_hash, config_hash, model_name, state, checkpoint_json, attempt,
                              error_code, result_revision, created_at, started_at, ended_at, ledger_sha256)
            VALUES ($id, $session, 'stt', $kind, $input, $config, $model, $state, $checkpoint, $attempt,
                    $error, $result, $created, $started, $ended, $sha)
            ON CONFLICT (id) DO UPDATE SET input_hash = excluded.input_hash, state = excluded.state, checkpoint_json = excluded.checkpoint_json,
                attempt = excluded.attempt, error_code = excluded.error_code, result_revision = excluded.result_revision,
                started_at = excluded.started_at, ended_at = excluded.ended_at, ledger_sha256 = excluded.ledger_sha256
            """);
        cmd.Parameters.AddWithValue("$id", DbText.Id(job.JobId));
        cmd.Parameters.AddWithValue("$session", DbText.Id(job.SessionId));
        cmd.Parameters.AddWithValue("$kind", DbText.Enum(job.Kind));
        cmd.Parameters.AddWithValue("$input", (object?)job.InputHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$config", job.Configuration.ComputeHash());
        cmd.Parameters.AddWithValue("$model", job.Configuration.ModelName);
        cmd.Parameters.AddWithValue("$state", DbText.Enum(job.State));
        cmd.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint, CompactJson));
        cmd.Parameters.AddWithValue("$attempt", job.Attempt);
        cmd.Parameters.AddWithValue("$error", job.Error is { } e ? DbText.Enum(e) : DBNull.Value);
        cmd.Parameters.AddWithValue("$result", (object?)job.ResultRevision ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", DbText.Utc(job.CreatedAtUtc));
        cmd.Parameters.AddWithValue("$started", job.StartedAtUtc is { } s ? DbText.Utc(s) : DBNull.Value);
        cmd.Parameters.AddWithValue("$ended", job.EndedAtUtc is { } en ? DbText.Utc(en) : DBNull.Value);
        cmd.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(ledger)));
        cmd.ExecuteNonQuery();
    }

    private static void SetProcessingState(SqliteConnection c, SqliteTransaction tx, Guid sessionId, ProcessingState state)
    {
        using var cmd = Command(c, tx, "UPDATE sessions SET processing_state = $state WHERE id = $id");
        cmd.Parameters.AddWithValue("$state", DbText.Enum(state));
        cmd.Parameters.AddWithValue("$id", DbText.Id(sessionId));
        cmd.ExecuteNonQuery();
    }

    private IEnumerable<string> SessionDirectories()
    {
        if (!Directory.Exists(_workspace.SessionsRoot)) return [];
        return Directory.EnumerateDirectories(_workspace.SessionsRoot)
            .SelectMany(Directory.EnumerateDirectories)
            .SelectMany(Directory.EnumerateDirectories)
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
}
