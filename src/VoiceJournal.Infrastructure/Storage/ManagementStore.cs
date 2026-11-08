using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Management;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>A saved change plan: its batch, where its files are, and whether an earlier identical approval was reused (AT-12).</summary>
public sealed record SavedPlan(Guid BatchId, Guid ApprovalId, string Directory, bool Reused, string State);

public sealed record ReviewableSession(Guid SessionId, string Directory, string? Title, string? Project, DateTimeOffset StartedAtUtc, int Candidates);

public sealed record BatchSummary(Guid BatchId, Guid ApprovalId, string State, DateTimeOffset CreatedAtUtc, string PlanPath, int Changes, int Files);

/// <summary>A user's choice for a candidate as stored (without the candidate, which lives in the record).</summary>
public sealed record StoredReview(
    CandidateKey Key, ReviewChoice? Choice, Guid? TargetItemId, int? TargetRevision, CandidateFields? EditedFields,
    IReadOnlyList<ItemField>? FieldsToChange, Guid? ParentIssueId, bool Confirmed);

/// <summary>
/// The management tables (detail spec 15.2, Phase E). These are the source of truth for items (11.2), not an index.
/// Phase E saves reviews and change plans; applying a plan to the files and the items is Phase F (14.2).
/// </summary>
public sealed class ManagementStore
{
    public const string IssuesPath = "management/issues.md";
    public const string TasksPath = "management/tasks.md";
    public const string TransactionsFolder = "transactions";

    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly TimeProvider _time;

    public ManagementStore(Workspace workspace, VoiceJournalDatabase db, TimeProvider? time = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = time ?? TimeProvider.System;
    }

    private string WorkspaceId => DbText.Id(_workspace.Info.WorkspaceId);

    public Guid WorkspaceGuid => _workspace.Info.WorkspaceId;

    /// <summary>The management files, registered on first use (the app creates them: no existing files, CLAUDE.md), with their bytes now.</summary>
    public IReadOnlyList<ManagedFile> Files()
    {
        EnsureWorkspaceRow();
        _db.Write((c, tx) =>
        {
            foreach (var (kind, path) in new[] { (CandidateKind.Issue, IssuesPath), (CandidateKind.Task, TasksPath) })
            {
                using var cmd = Command(c, tx, """
                    INSERT INTO managed_files (id, workspace_id, kind, path, mode, created_at) VALUES ($id, $ws, $kind, $path, 'managed_blocks', $at)
                    ON CONFLICT DO NOTHING
                    """);
                cmd.Parameters.AddWithValue("$id", DbText.Id(DbText.DerivedId(_workspace.Info.WorkspaceId, "managed-file/" + path)));
                cmd.Parameters.AddWithValue("$ws", WorkspaceId);
                cmd.Parameters.AddWithValue("$kind", KindText(kind));
                cmd.Parameters.AddWithValue("$path", path);
                cmd.Parameters.AddWithValue("$at", DbText.Utc(_time.GetUtcNow()));
                cmd.ExecuteNonQuery();
            }
            return 0;
        });
        return _db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, kind, path, mode FROM managed_files WHERE workspace_id = $ws ORDER BY kind";
            cmd.Parameters.AddWithValue("$ws", WorkspaceId);
            using var r = cmd.ExecuteReader();
            var list = new List<ManagedFile>();
            while (r.Read())
            {
                var path = r.GetString(2);
                var full = FullPath(path);
                list.Add(new ManagedFile
                {
                    FileId = Guid.Parse(r.GetString(0)),
                    Kind = ParseKind(r.GetString(1)),
                    RelativePath = path,
                    Mode = r.GetString(3) == "append_only" ? ManagedFileMode.AppendOnly : ManagedFileMode.ManagedBlocks,
                    CurrentBytes = File.Exists(full) ? AtomicFile.ReadAllBytes(full) : null,
                });
            }
            return list;
        });
    }

    /// <summary>Switches a file between managed blocks and append-only (11.4).</summary>
    public void SetMode(CandidateKind kind, ManagedFileMode mode)
    {
        Files();
        _db.Write((c, tx) =>
        {
            using var cmd = Command(c, tx, "UPDATE managed_files SET mode = $mode WHERE workspace_id = $ws AND kind = $kind");
            cmd.Parameters.AddWithValue("$mode", mode == ManagedFileMode.AppendOnly ? "append_only" : "managed_blocks");
            cmd.Parameters.AddWithValue("$ws", WorkspaceId);
            cmd.Parameters.AddWithValue("$kind", KindText(kind));
            return cmd.ExecuteNonQuery();
        });
    }

    public string FullPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var full = Path.GetFullPath(Path.Combine(_workspace.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(_workspace.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The management file is outside the workspace.");
        return full;
    }

    /// <summary>The confirmed items with their evidence.</summary>
    public IReadOnlyList<ManagementItem> Items() => _db.Read(c =>
    {
        var evidence = new Dictionary<Guid, List<ItemEvidence>>();
        using (var e = c.CreateCommand())
        {
            e.CommandText = "SELECT item_id, session_id, transcript_revision, segment_id, quote FROM item_evidence ORDER BY rowid";
            using var r = e.ExecuteReader();
            while (r.Read())
            {
                var id = Guid.Parse(r.GetString(0));
                if (!evidence.TryGetValue(id, out var list)) evidence[id] = list = [];
                list.Add(new ItemEvidence { SessionId = Guid.Parse(r.GetString(1)), TranscriptRevision = r.GetInt32(2), SegmentId = Guid.Parse(r.GetString(3)), Quote = r.GetString(4) });
            }
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, display_id, kind, project, title, description, status, priority, assignee, due_date, parent_issue_id, revision, created_at, updated_at
            FROM items WHERE workspace_id = $ws ORDER BY display_id
            """;
        cmd.Parameters.AddWithValue("$ws", WorkspaceId);
        using var reader = cmd.ExecuteReader();
        var items = new List<ManagementItem>();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0));
            items.Add(new ManagementItem
            {
                Id = id,
                DisplayId = reader.GetString(1),
                Kind = ParseKind(reader.GetString(2)),
                Project = reader.GetString(3),
                Title = reader.GetString(4),
                Description = reader.GetString(5),
                Status = DbText.ParseEnum<ItemStatus>(reader.GetString(6)),
                Priority = reader.IsDBNull(7) ? null : DbText.ParseEnum<ItemPriority>(reader.GetString(7)),
                Assignee = reader.IsDBNull(8) ? null : reader.GetString(8),
                DueDate = reader.IsDBNull(9) ? null : DateOnly.ParseExact(reader.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ParentIssueId = reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
                Revision = reader.GetInt32(11),
                CreatedAtUtc = DbText.ParseUtc(reader.GetString(12)),
                UpdatedAtUtc = DbText.ParseUtc(reader.GetString(13)),
                Evidence = evidence.GetValueOrDefault(id) ?? [],
            });
        }
        return items;
    });

    /// <summary>The next display numbers: never below a number already used or reserved by a plan (11.1, never reused).</summary>
    public (long Issue, long Task) NextNumbers() => _db.Read(c =>
    {
        long Next(CandidateKind kind)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT MAX(n) FROM (
                    SELECT next_number AS n FROM display_counters WHERE workspace_id = $ws AND kind = $kind
                    UNION ALL SELECT CAST(substr(display_id, 5) AS INTEGER) + 1 FROM items WHERE workspace_id = $ws AND kind = $kind
                    UNION ALL SELECT 1)
                """;
            cmd.Parameters.AddWithValue("$ws", WorkspaceId);
            cmd.Parameters.AddWithValue("$kind", KindText(kind));
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return (Next(CandidateKind.Issue), Next(CandidateKind.Task));
    });

    /// <summary>The applied ledger (12.2-2) and the fingerprints of applied creations (AT-12).</summary>
    public (IReadOnlySet<CandidateKey> Keys, IReadOnlySet<string> Fingerprints) Applied() => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT candidate_key, fingerprint FROM candidate_applications";
        using var r = cmd.ExecuteReader();
        var keys = new HashSet<CandidateKey>();
        var prints = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read())
        {
            if (ParseKey(r.GetString(0)) is { } key) keys.Add(key);
            prints.Add(r.GetString(1));
        }
        return ((IReadOnlySet<CandidateKey>)keys, (IReadOnlySet<string>)prints);
    });

    /// <summary>Candidates in saved plans that are not applied or abandoned yet.</summary>
    public IReadOnlySet<CandidateKey> Pending() => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT json_extract(j.value, '$.key') FROM apply_batches b JOIN approvals a ON a.id = b.approval_id, json_each(a.candidate_revisions_json) j
            WHERE b.state IN ('planned', 'prepared', 'applying', 'recovery_required')
            """;
        using var r = cmd.ExecuteReader();
        var keys = new HashSet<CandidateKey>();
        while (r.Read())
            if (!r.IsDBNull(0) && ParseKey(r.GetString(0)) is { } key) keys.Add(key);
        return (IReadOnlySet<CandidateKey>)keys;
    });

    public IReadOnlyDictionary<CandidateKey, StoredReview> Reviews(Guid sessionId) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, analysis_revision, candidate_id, choice, target_item_id, target_revision, edited_fields_json, fields_to_change, parent_issue_id, confirmed
            FROM candidate_reviews WHERE session_id = $session
            """;
        cmd.Parameters.AddWithValue("$session", DbText.Id(sessionId));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<CandidateKey, StoredReview>();
        while (r.Read())
        {
            var key = new CandidateKey(Guid.Parse(r.GetString(0)), r.GetInt32(1), r.GetString(2));
            map[key] = new StoredReview(
                key,
                r.IsDBNull(3) ? null : DbText.ParseEnum<ReviewChoice>(r.GetString(3)),
                r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(6) ? null : JsonSerializer.Deserialize<CandidateFields>(r.GetString(6), AnalysisJson.Compact),
                r.IsDBNull(7) ? null : JsonSerializer.Deserialize<List<ItemField>>(r.GetString(7), AnalysisJson.Compact),
                r.IsDBNull(8) ? null : Guid.Parse(r.GetString(8)),
                r.GetInt64(9) == 1);
        }
        return (IReadOnlyDictionary<CandidateKey, StoredReview>)map;
    });

    public void SaveReview(CandidateReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        _db.Write((c, tx) =>
        {
            using var cmd = Command(c, tx, """
                INSERT INTO candidate_reviews (candidate_key, session_id, analysis_revision, candidate_id, choice, target_item_id, target_revision,
                                               edited_fields_json, fields_to_change, parent_issue_id, confirmed, updated_at)
                VALUES ($key, $session, $analysis, $candidate, $choice, $target, $targetRev, $edited, $fields, $parent, $confirmed, $at)
                ON CONFLICT (candidate_key) DO UPDATE SET choice = excluded.choice, target_item_id = excluded.target_item_id,
                    target_revision = excluded.target_revision, edited_fields_json = excluded.edited_fields_json,
                    fields_to_change = excluded.fields_to_change, parent_issue_id = excluded.parent_issue_id,
                    confirmed = excluded.confirmed, updated_at = excluded.updated_at
                """);
            cmd.Parameters.AddWithValue("$key", review.Key.ToString());
            cmd.Parameters.AddWithValue("$session", DbText.Id(review.Key.SessionId));
            cmd.Parameters.AddWithValue("$analysis", review.Key.AnalysisRevision);
            cmd.Parameters.AddWithValue("$candidate", review.Key.CandidateId);
            cmd.Parameters.AddWithValue("$choice", review.Choice is { } ch ? DbText.Enum(ch) : DBNull.Value);
            cmd.Parameters.AddWithValue("$target", review.TargetItemId is { } t ? DbText.Id(t) : DBNull.Value);
            cmd.Parameters.AddWithValue("$targetRev", (object?)review.TargetRevision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$edited", review.EditedFields is { } f ? JsonSerializer.Serialize(f, AnalysisJson.Compact) : DBNull.Value);
            cmd.Parameters.AddWithValue("$fields", review.FieldsToChange is { } list ? JsonSerializer.Serialize(list, AnalysisJson.Compact) : DBNull.Value);
            cmd.Parameters.AddWithValue("$parent", review.ParentIssueId is { } p ? DbText.Id(p) : DBNull.Value);
            cmd.Parameters.AddWithValue("$confirmed", review.ConfirmedIndividually ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", DbText.Utc(_time.GetUtcNow()));
            return cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Saves a plan under <c>transactions/&lt;batch-id&gt;/</c> and records it as <c>planned</c>. The same approval saved twice
    /// (same idempotency key) returns the first batch and writes nothing (13.2, AT-12). Display numbers used by the plan are
    /// reserved, so they are never handed out again even if the plan is abandoned.
    /// </summary>
    public SavedPlan SavePlan(ChangePlan plan, string actor = "user")
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Changes.Count == 0 && plan.Proposals.Count == 0) throw new InvalidOperationException("The plan has nothing to save.");
        if (FindBatch(plan.IdempotencyKey) is { } existing) return existing with { Reused = true };

        var relative = $"{TransactionsFolder}/{plan.BatchId:D}";
        var directory = FullPath(relative);
        Directory.CreateDirectory(Path.Combine(directory, "blobs"));
        Directory.CreateDirectory(Path.Combine(directory, "snapshots"));
        foreach (var file in plan.Files) AtomicFile.WriteAllBytes(Path.Combine(directory, "blobs", file.StagedBlobId.ToString("D") + ".bin"), file.AfterBytes);
        foreach (var (id, node) in plan.Snapshots) AtomicFile.WriteAllBytes(Path.Combine(directory, "snapshots", id.ToString("D") + ".json"), Encoding.UTF8.GetBytes(CanonicalJson.Serialize(node)));
        if (plan.Json is { } json) AtomicFile.WriteAllBytes(Path.Combine(directory, "plan.json"), Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        AtomicFile.WriteAllBytes(Path.Combine(directory, "review.json"), JsonSerializer.SerializeToUtf8Bytes(Summary(plan), AnalysisJson.Options));

        var now = DbText.Utc(_time.GetUtcNow());
        var saved = _db.Write((c, tx) =>
        {
            // Another window may have saved the same approval between the check above and this lock.
            using (var check = Command(c, tx, "SELECT id, approval_id, plan_path, state FROM apply_batches WHERE idempotency_key = $key"))
            {
                check.Parameters.AddWithValue("$key", DbText.Id(plan.IdempotencyKey));
                using var r = check.ExecuteReader();
                if (r.Read()) return new SavedPlan(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), FullPath(r.GetString(2)), true, r.GetString(3));
            }
            Exec(c, tx, """
                INSERT INTO approvals (id, workspace_id, plan_hash, candidate_revisions_json, approved_at, actor, state)
                VALUES ($id, $ws, $hash, $candidates, $at, $actor, 'planned')
                """, ("$id", DbText.Id(plan.ApprovalId)), ("$ws", WorkspaceId), ("$hash", (object?)plan.PlanHash ?? DBNull.Value),
                ("$candidates", JsonSerializer.Serialize(plan.Changes.Select(ch => new { key = ch.Key.ToString(), revision = ch.Key.AnalysisRevision, ch.EvidenceHash }))),
                ("$at", now), ("$actor", actor));
            Exec(c, tx, """
                INSERT INTO apply_batches (id, approval_id, idempotency_key, state, plan_path, created_at) VALUES ($id, $approval, $key, 'planned', $path, $at)
                """, ("$id", DbText.Id(plan.BatchId)), ("$approval", DbText.Id(plan.ApprovalId)), ("$key", DbText.Id(plan.IdempotencyKey)), ("$path", relative), ("$at", now));
            var sequence = 0;
            foreach (var f in plan.Files)
            {
                Exec(c, tx, """
                    INSERT INTO apply_file_ops (batch_id, sequence, file_id, operation, before_hash, after_hash, staged_path, state)
                    VALUES ($batch, $seq, $file, $op, $before, $after, $staged, 'planned')
                    """, ("$batch", DbText.Id(plan.BatchId)), ("$seq", ++sequence), ("$file", DbText.Id(f.FileId)), ("$op", f.Operation),
                    ("$before", (object?)f.BeforeHash ?? DBNull.Value), ("$after", f.AfterHash), ("$staged", $"{relative}/blobs/{f.StagedBlobId:D}.bin"));
            }
            foreach (var p in plan.Proposals)
            {
                Exec(c, tx, """
                    INSERT INTO item_proposals (id, batch_id, candidate_key, target_item_id, proposed_json, reason, created_at)
                    VALUES ($id, $batch, $key, $target, $json, $reason, $at)
                    """, ("$id", DbText.Id(DbText.DerivedId(plan.BatchId, "proposal/" + p.Key))), ("$batch", DbText.Id(plan.BatchId)), ("$key", p.Key.ToString()),
                    ("$target", DbText.Id(p.Target.Id)), ("$json", CanonicalJson.Serialize(ItemJson.ToNode(p.Proposed))), ("$reason", p.Reason), ("$at", now));
            }
            foreach (var (kind, next) in new[] { (CandidateKind.Issue, plan.NextIssueNumber), (CandidateKind.Task, plan.NextTaskNumber) })
            {
                Exec(c, tx, """
                    INSERT INTO display_counters (workspace_id, kind, next_number) VALUES ($ws, $kind, $next)
                    ON CONFLICT (workspace_id, kind) DO UPDATE SET next_number = MAX(next_number, excluded.next_number)
                    """, ("$ws", WorkspaceId), ("$kind", KindText(kind)), ("$next", next));
            }
            Exec(c, tx, """
                INSERT INTO audit_events (id, timestamp, event_type, entity_type, entity_id, batch_id, details_json)
                VALUES ($id, $at, 'plan_saved', 'apply_batch', $batch, $batch, $details)
                """, ("$id", DbText.Id(DbText.DerivedId(plan.BatchId, "audit/plan_saved"))), ("$at", now), ("$batch", DbText.Id(plan.BatchId)),
                ("$details", JsonSerializer.Serialize(new { changes = plan.Changes.Count, proposals = plan.Proposals.Count, skipped = plan.Skipped.Count, files = plan.Files.Count, plan_hash = plan.PlanHash })));
            return new SavedPlan(plan.BatchId, plan.ApprovalId, directory, false, "planned");
        });
        if (saved.Reused) TryDelete(directory);
        return saved;
    }

    public IReadOnlyList<BatchSummary> Batches() => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT b.id, b.approval_id, b.state, b.created_at, b.plan_path, json_array_length(a.candidate_revisions_json),
                   (SELECT COUNT(*) FROM apply_file_ops f WHERE f.batch_id = b.id)
            FROM apply_batches b JOIN approvals a ON a.id = b.approval_id ORDER BY b.created_at DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<BatchSummary>();
        while (r.Read())
            list.Add(new BatchSummary(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), DbText.ParseUtc(r.GetString(3)), r.GetString(4), r.GetInt32(5), r.GetInt32(6)));
        return list;
    });

    /// <summary>Sessions that have a record (newest first), with the number of candidates of their latest analysis.</summary>
    public IReadOnlyList<ReviewableSession> SessionsWithRecords() => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.relative_path, s.title, s.project, s.started_at,
                   (SELECT COUNT(*) FROM candidates k JOIN analyses a ON a.id = k.analysis_id
                     WHERE a.session_id = s.id AND a.revision = (SELECT MAX(revision) FROM analyses WHERE session_id = s.id))
            FROM sessions s
            WHERE s.workspace_id = $ws AND EXISTS (SELECT 1 FROM records r WHERE r.session_id = s.id)
            ORDER BY s.started_at DESC
            """;
        cmd.Parameters.AddWithValue("$ws", WorkspaceId);
        using var r = cmd.ExecuteReader();
        var list = new List<ReviewableSession>();
        while (r.Read())
            list.Add(new ReviewableSession(Guid.Parse(r.GetString(0)), Path.Combine(_workspace.Root, r.GetString(1).Replace('/', Path.DirectorySeparatorChar)),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), DbText.ParseUtc(r.GetString(4)), r.GetInt32(5)));
        return list;
    });

    /// <summary>Marks a planned batch as abandoned (the user re-plans). Its reserved display numbers stay used.</summary>
    public void Abandon(Guid batchId) => _db.Write((c, tx) =>
    {
        Exec(c, tx, "UPDATE apply_batches SET state = 'abandoned' WHERE id = $id AND state = 'planned'", ("$id", DbText.Id(batchId)));
        Exec(c, tx, "UPDATE approvals SET state = 'abandoned' WHERE id = (SELECT approval_id FROM apply_batches WHERE id = $id) AND state = 'planned'", ("$id", DbText.Id(batchId)));
        return 0;
    });

    private SavedPlan? FindBatch(Guid idempotencyKey) => _db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, approval_id, plan_path, state FROM apply_batches WHERE idempotency_key = $key";
        cmd.Parameters.AddWithValue("$key", DbText.Id(idempotencyKey));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SavedPlan(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), FullPath(r.GetString(2)), false, r.GetString(3)) : null;
    });

    private void EnsureWorkspaceRow() => _db.Write((c, tx) =>
    {
        Exec(c, tx, """
            INSERT INTO workspaces (id, name, schema_version, root_path, created_at) VALUES ($id, $name, $schema, $root, $created) ON CONFLICT DO NOTHING
            """, ("$id", WorkspaceId), ("$name", _workspace.Info.Name), ("$schema", _workspace.Info.SchemaVersion), ("$root", _workspace.Root),
            ("$created", DbText.Utc(_workspace.Info.CreatedAtUtc)));
        return 0;
    });

    private static object Summary(ChangePlan plan) => new
    {
        batch_id = plan.BatchId,
        approval_id = plan.ApprovalId,
        idempotency_key = plan.IdempotencyKey,
        plan_hash = plan.PlanHash,
        changes = plan.Changes.Select(c => new { key = c.Key.ToString(), action = c.Action.ToString().ToLowerInvariant(), item = c.After.DisplayId, title = c.After.Title, c.ExpectedRevision, file_id = c.FileId }),
        proposals = plan.Proposals.Select(p => new { key = p.Key.ToString(), item = p.Target.DisplayId, p.Reason }),
        skipped = plan.Skipped.Select(s => new { key = s.Key.ToString(), s.Title, s.Reason }),
    };

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string KindText(CandidateKind kind) => kind == CandidateKind.Issue ? "issue" : "task";

    internal static CandidateKind ParseKind(string text) => text == "issue" ? CandidateKind.Issue : CandidateKind.Task;

    internal static CandidateKey? ParseKey(string text)
    {
        var parts = text.Split('/');
        return parts.Length == 3 && Guid.TryParse(parts[0], out var session) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rev)
            ? new CandidateKey(session, rev, parts[2]) : null;
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(c, tx, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }
}
