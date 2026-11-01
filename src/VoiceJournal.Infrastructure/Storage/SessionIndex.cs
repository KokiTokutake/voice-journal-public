using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Serialization;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>
/// SQLite index of the sessions in a workspace (detail spec 15.2 / 15.3). Every write re-reads a
/// manifest.json and replaces that session's rows in one transaction, so the index can always be
/// rebuilt from the files. Never called from the audio path.
/// </summary>
public sealed class SessionIndex : ISessionIndex
{
    private const int JsonColumnSchemaVersion = 1;
    private static readonly JsonSerializerOptions CompactJson = new(VoiceJournalJson.Options) { WriteIndented = false };

    // Children first: the foreign keys have no CASCADE (detail spec 15.3).
    private static readonly string[] DeleteChildrenSql =
    [
        "DELETE FROM audio_events WHERE track_id IN (SELECT id FROM audio_tracks WHERE session_id = $id)",
        "DELETE FROM audio_chunks WHERE track_id IN (SELECT id FROM audio_tracks WHERE session_id = $id)",
        "DELETE FROM audio_tracks WHERE session_id = $id",
    ];

    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly TimeProvider _time;
    private readonly string _workspaceId;

    public SessionIndex(Workspace workspace, VoiceJournalDatabase db, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(db);
        _workspace = workspace;
        _db = db;
        _time = time ?? TimeProvider.System;
        _workspaceId = DbText.Id(workspace.Info.WorkspaceId);
        EnsureWorkspaceRow();
    }

    public bool Upsert(string sessionDirectory) => UpsertCore(sessionDirectory, null).Changed;

    public bool Upsert(string sessionDirectory, ReadOnlySpan<byte> manifestJson) => UpsertCore(sessionDirectory, manifestJson.ToArray()).Changed;

    public IndexRebuildReport Rebuild()
    {
        int indexed = 0, unchanged = 0;
        var failed = new List<IndexFailure>();
        var needsRecovery = new List<string>();
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in SessionDirectories())
        {
            var relative = _workspace.RelativePathOf(dir);
            onDisk.Add(relative);
            if (!File.Exists(SessionManifest.PathIn(dir)))
            {
                failed.Add(new IndexFailure(relative, "manifest.json is missing"));
                continue;
            }
            try
            {
                var (changed, manifest) = UpsertCore(dir, null);
                if (changed) indexed++;
                else unchanged++;
                if (SessionRecovery.NeedsRecovery(manifest)) needsRecovery.Add(relative);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SqliteException)
            {
                // SqliteException here is a constraint violation (busy is DatabaseBusyException and propagates).
                failed.Add(new IndexFailure(relative, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        var missing = _db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, relative_path FROM sessions WHERE workspace_id = $ws ORDER BY relative_path";
            cmd.Parameters.AddWithValue("$ws", _workspaceId);
            using var reader = cmd.ExecuteReader();
            var list = new List<Guid>();
            while (reader.Read())
            {
                if (!onDisk.Contains(reader.GetString(1))) list.Add(Guid.Parse(reader.GetString(0)));
            }
            return list;
        });
        return new IndexRebuildReport(indexed, unchanged, failed, missing, needsRecovery);
    }

    public IReadOnlyList<SessionSummary> ListSessions() => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.title, s.project, s.started_at, s.ended_at, s.timezone, s.capture_mode, s.capture_state, s.relative_path,
                   COALESCE((SELECT MAX(ch.start_ms + ch.frame_count * 1000 / t.sample_rate)
                             FROM audio_chunks ch JOIN audio_tracks t ON t.id = ch.track_id
                             WHERE t.session_id = s.id), 0),
                   EXISTS (SELECT 1 FROM audio_events e JOIN audio_tracks t ON t.id = e.track_id
                           WHERE t.session_id = s.id AND e.kind IN ('queue_overflow', 'device_disconnected', 'write_failed'))
            FROM sessions s
            WHERE s.workspace_id = $ws
            ORDER BY s.started_at DESC
            """;
        cmd.Parameters.AddWithValue("$ws", _workspaceId);
        using var r = cmd.ExecuteReader();
        var list = new List<SessionSummary>();
        while (r.Read())
        {
            list.Add(new SessionSummary(
                Guid.Parse(r.GetString(0)),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                DbText.ParseUtc(r.GetString(3)),
                r.IsDBNull(4) ? null : DbText.ParseUtc(r.GetString(4)),
                r.GetString(5),
                DbText.ParseEnum<CaptureMode>(r.GetString(6)),
                DbText.ParseEnum<CaptureState>(r.GetString(7)),
                r.GetInt64(9),
                r.GetBoolean(10),
                r.GetString(8)));
        }
        return list;
    });

    private (bool Changed, SessionManifest Manifest) UpsertCore(string sessionDirectory, byte[]? manifestJson)
    {
        var directory = Path.GetFullPath(sessionDirectory);
        var relative = _workspace.RelativePathOf(directory);
        var bytes = manifestJson ?? VoiceJournal.Core.IO.AtomicFile.ReadAllBytes(SessionManifest.PathIn(directory));
        var manifest = SessionManifest.Parse(bytes, directory);
        if (!string.Equals(Path.GetFileName(directory), manifest.SessionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The folder name does not match the session id in its manifest ({relative}).");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var changed = _db.Write((c, tx) =>
        {
            var sessionId = DbText.Id(manifest.SessionId);
            using (var existing = Command(c, tx, "SELECT manifest_sha256 FROM sessions WHERE id = $id"))
            {
                existing.Parameters.AddWithValue("$id", sessionId);
                if (existing.ExecuteScalar() as string == sha) return false;
            }

            using (var upsert = Command(c, tx, """
                INSERT INTO sessions (id, workspace_id, relative_path, title, project, started_at, ended_at, timezone,
                                      capture_mode, mic_mode, capture_state, manifest_sha256, indexed_at)
                VALUES ($id, $ws, $path, $title, $project, $started, $ended, $tz, $mode, $mic, $state, $sha, $now)
                ON CONFLICT (id) DO UPDATE SET
                    relative_path = excluded.relative_path, title = excluded.title, project = excluded.project,
                    started_at = excluded.started_at, ended_at = excluded.ended_at, timezone = excluded.timezone,
                    capture_mode = excluded.capture_mode, mic_mode = excluded.mic_mode, capture_state = excluded.capture_state,
                    manifest_sha256 = excluded.manifest_sha256, indexed_at = excluded.indexed_at
                """))
            {
                upsert.Parameters.AddWithValue("$id", sessionId);
                upsert.Parameters.AddWithValue("$ws", _workspaceId);
                upsert.Parameters.AddWithValue("$path", relative);
                upsert.Parameters.AddWithValue("$title", (object?)manifest.Title ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$project", (object?)manifest.Project ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$started", DbText.Utc(manifest.StartedAtUtc));
                upsert.Parameters.AddWithValue("$ended", manifest.EndedAtUtc is { } ended ? DbText.Utc(ended) : DBNull.Value);
                upsert.Parameters.AddWithValue("$tz", manifest.TimeZoneId);
                upsert.Parameters.AddWithValue("$mode", DbText.Enum(manifest.CaptureMode));
                upsert.Parameters.AddWithValue("$mic", manifest.MicMode is { } mic ? DbText.Enum(mic) : DBNull.Value);
                upsert.Parameters.AddWithValue("$state", DbText.Enum(manifest.CaptureState));
                upsert.Parameters.AddWithValue("$sha", sha);
                upsert.Parameters.AddWithValue("$now", DbText.Utc(_time.GetUtcNow()));
                upsert.ExecuteNonQuery();
            }

            // Children are replaced wholesale; ids are derived from the manifest, so they stay stable.
            foreach (var sql in DeleteChildrenSql)
            {
                using var delete = Command(c, tx, sql);
                delete.Parameters.AddWithValue("$id", sessionId);
                delete.ExecuteNonQuery();
            }

            foreach (var track in manifest.Tracks) InsertTrack(c, tx, sessionId, track);
            return true;
        });
        return (changed, manifest);
    }

    private static void InsertTrack(SqliteConnection c, SqliteTransaction tx, string sessionId, TrackManifest track)
    {
        var format = JsonSerializer.SerializeToNode(track.Format, CompactJson)!.AsObject();
        format.Insert(0, "schema_version", JsonColumnSchemaVersion);

        using (var cmd = Command(c, tx, """
            INSERT INTO audio_tracks (id, session_id, source, device_id, device_name, format_json, sample_rate, start_offset_ms, state)
            VALUES ($id, $session, $source, $device, $name, $format, $rate, $offset, $state)
            """))
        {
            cmd.Parameters.AddWithValue("$id", DbText.Id(track.TrackId));
            cmd.Parameters.AddWithValue("$session", sessionId);
            cmd.Parameters.AddWithValue("$source", DbText.Enum(track.Source));
            cmd.Parameters.AddWithValue("$device", track.DeviceId);
            cmd.Parameters.AddWithValue("$name", track.DeviceName);
            cmd.Parameters.AddWithValue("$format", format.ToJsonString(CompactJson));
            cmd.Parameters.AddWithValue("$rate", track.Format.SampleRate);
            cmd.Parameters.AddWithValue("$offset", track.StartOffsetMs);
            cmd.Parameters.AddWithValue("$state", DbText.Enum(track.State));
            cmd.ExecuteNonQuery();
        }

        foreach (var chunk in track.Chunks)
        {
            using var cmd = Command(c, tx, """
                INSERT INTO audio_chunks (id, track_id, sequence, relative_path, start_ms, frame_count, size, sha256, clock_start_ms, clock_end_ms, state)
                VALUES ($id, $track, $seq, $path, $start, $frames, $size, $sha, $cs, $ce, $state)
                """);
            cmd.Parameters.AddWithValue("$id", DbText.Id(DbText.DerivedId(track.TrackId, $"chunk/{chunk.Sequence}")));
            cmd.Parameters.AddWithValue("$track", DbText.Id(track.TrackId));
            cmd.Parameters.AddWithValue("$seq", chunk.Sequence);
            cmd.Parameters.AddWithValue("$path", chunk.File);
            cmd.Parameters.AddWithValue("$start", chunk.StartMs);
            cmd.Parameters.AddWithValue("$frames", chunk.FrameCount);
            cmd.Parameters.AddWithValue("$size", chunk.SizeBytes);
            cmd.Parameters.AddWithValue("$sha", (object?)chunk.Sha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cs", (object?)chunk.ClockStartMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ce", (object?)chunk.ClockEndMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", DbText.Enum(chunk.State));
            cmd.ExecuteNonQuery();
        }

        var ordinal = 0;
        foreach (var e in track.Events)
        {
            ordinal++;
            using var cmd = Command(c, tx, """
                INSERT INTO audio_events (id, track_id, ordinal, start_ms, end_ms, kind, detail_json)
                VALUES ($id, $track, $ord, $start, $end, $kind, $detail)
                """);
            cmd.Parameters.AddWithValue("$id", DbText.Id(DbText.DerivedId(track.TrackId, $"event/{ordinal}")));
            cmd.Parameters.AddWithValue("$track", DbText.Id(track.TrackId));
            cmd.Parameters.AddWithValue("$ord", ordinal);
            cmd.Parameters.AddWithValue("$start", e.StartMs);
            cmd.Parameters.AddWithValue("$end", (object?)e.EndMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", DbText.Enum(e.Kind));
            cmd.Parameters.AddWithValue("$detail", e.Detail is null
                ? DBNull.Value
                : new JsonObject { ["schema_version"] = JsonColumnSchemaVersion, ["detail"] = e.Detail }.ToJsonString(CompactJson));
            cmd.ExecuteNonQuery();
        }
    }

    private void EnsureWorkspaceRow() => _db.Write((c, tx) =>
    {
        using var cmd = Command(c, tx, """
            INSERT INTO workspaces (id, name, schema_version, root_path, created_at) VALUES ($id, $name, $schema, $root, $created)
            ON CONFLICT (id) DO UPDATE SET name = excluded.name, schema_version = excluded.schema_version, root_path = excluded.root_path
            """);
        cmd.Parameters.AddWithValue("$id", _workspaceId);
        cmd.Parameters.AddWithValue("$name", _workspace.Info.Name);
        cmd.Parameters.AddWithValue("$schema", _workspace.Info.SchemaVersion);
        cmd.Parameters.AddWithValue("$root", _workspace.Root);
        cmd.Parameters.AddWithValue("$created", DbText.Utc(_workspace.Info.CreatedAtUtc));
        return cmd.ExecuteNonQuery();
    });

    /// <summary><c>sessions/yyyy/MM/&lt;id&gt;</c> folders, in path order.</summary>
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
