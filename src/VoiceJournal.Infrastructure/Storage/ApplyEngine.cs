using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Management;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>The steps of detail spec 14.2 that leave a trace, for fault injection in tests (AT-17).</summary>
public enum ApplyStep
{
    Verified,
    Locked,
    BackupWritten,
    TempWritten,
    Prepared,
    BeforeReplace,
    AfterReplace,
    BeforeDbCommit,
    Committed,
}

/// <summary>Lets a test stop the engine at a step, as if the app ended there (AT-17).</summary>
public interface IApplyFaults
{
    void At(ApplyStep point, int sequence);
}

public enum ApplyOutcome
{
    Committed,

    /// <summary>The same batch was committed before: nothing was done again (AT-12).</summary>
    AlreadyCommitted,

    /// <summary>Something changed since the plan (a file, an item revision, the plan itself): nothing was replaced (AT-15).</summary>
    Conflict,

    /// <summary>A file did not match what was expected while replacing: the rest is stopped and both versions are kept.</summary>
    RecoveryRequired,
}

public sealed record ApplyResult(ApplyOutcome Outcome, string Message, IReadOnlyList<string> Problems);

/// <summary>
/// Applies a planned batch (detail spec 14.2, steps 2–8): re-verify, lock, back up and stage, record the prepared ledger,
/// replace file by file (checking what was replaced), then confirm the items in one database transaction. Files and the
/// database cannot change together atomically; the ledger and the hashes let <see cref="ApplyRecovery"/> finish or stop
/// a batch after a crash. Unknown content is never overwritten.
/// </summary>
public sealed class ApplyEngine
{
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly ManagementStore _store;
    private readonly TimeProvider _time;
    private readonly IApplyFaults? _faults;

    public ApplyEngine(Workspace workspace, VoiceJournalDatabase db, ManagementStore store, TimeProvider? time = null, IApplyFaults? faults = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
        _faults = faults;
    }

    public ApplyResult Apply(Guid batchId)
    {
        using var mutex = new Mutex(false, $@"Local\VoiceJournal-apply-{_workspace.Info.WorkspaceId:N}");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) return new(ApplyOutcome.Conflict, "別の反映が実行中です。終わってからやり直してください。", []);
        }
        catch (AbandonedMutexException)
        {
            // A previous run ended while holding it: the ledger tells what happened; recovery decides.
        }
        try
        {
            return ApplyLocked(batchId);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private ApplyResult ApplyLocked(Guid batchId)
    {
        var batch = BatchLedger.Load(_db, _store, batchId);
        if (batch.State == "committed") return new(ApplyOutcome.AlreadyCommitted, "この変更計画はすでに反映済みです。", []);
        if (batch.State is "abandoned" or "recovery_required") return new(ApplyOutcome.Conflict, $"この変更計画は反映できません（状態: {batch.State}）。", []);
        if (batch.State is "prepared" or "applying")
            return new(ApplyOutcome.RecoveryRequired, "この変更計画は前回の反映が途中です。起動時の確認（復旧）で続きを判定します。", []);

        // 2. Re-verify: the plan, the files and the item revisions are what was approved.
        var problems = Verify(batch);
        if (problems.Count > 0) return Conflict(batch, problems);
        _faults?.At(ApplyStep.Verified, 0);

        // 3. Lock and re-read each file without sharing: its bytes must still be the before bytes (AT-15).
        foreach (var op in batch.Files)
        {
            var path = _store.FullPath(op.RelativePath);
            ManagedFileGuard.CheckLocation(path, _workspace.Root);
            var now = ManagedFileGuard.ReadExclusive(path);
            if (!Matches(now, op.BeforeHash))
                return Conflict(batch, [$"{op.RelativePath} が承認の後に変更されています（外部での編集）。反映を中止しました。外部の編集はそのまま残しています。"]);
        }
        _faults?.At(ApplyStep.Locked, 0);

        // 4. Back up the before bytes (immutable) and stage the after bytes next to each target, verified by reading back.
        var directory = _store.FullPath(batch.PlanPath);
        Directory.CreateDirectory(Path.Combine(directory, "backup"));
        foreach (var op in batch.Files)
        {
            var path = _store.FullPath(op.RelativePath);
            string? backup = null;
            if (op.BeforeHash is not null)
            {
                backup = Path.Combine(directory, "backup", $"{op.Sequence:00}-{op.FileId:N}.before");
                AtomicFile.WriteAllBytes(backup, ManagedFileGuard.ReadExclusive(path)!);
                if (!Matches(File.ReadAllBytes(backup), op.BeforeHash)) return Conflict(batch, [$"{op.RelativePath} のバックアップを確かめられません。"]);
            }
            _faults?.At(ApplyStep.BackupWritten, op.Sequence);
            var after = File.ReadAllBytes(_store.FullPath(op.StagedPath));
            if (Sha(after) != op.AfterHash) return Conflict(batch, [$"{op.RelativePath} の変更後の内容が計画と一致しません。"]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = Path.Combine(Path.GetDirectoryName(path)!, $".vj-{batch.BatchId:N}-{op.Sequence:00}.tmp");
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(after);
                stream.Flush(flushToDisk: true);
            }
            var check = File.ReadAllBytes(temp);
            if (Sha(check) != op.AfterHash || ManagedText.Decode(check) is not { } text || ManagementMarkdown.Parse(text.Text, _workspace.Info.WorkspaceId).Errors.Count > 0)
                return Conflict(batch, [$"{op.RelativePath} の一時ファイルを確かめられません。"]);
            op.BackupPath = backup;
            op.TempPath = temp;
            _faults?.At(ApplyStep.TempWritten, op.Sequence);
        }

        // 5. Prepared: the ledger knows every hash, backup and temp before anything is replaced.
        _db.Write((c, tx) =>
        {
            foreach (var op in batch.Files)
                Exec(c, tx, "UPDATE apply_file_ops SET backup_path = $b, temp_path = $t, state = 'prepared' WHERE batch_id = $batch AND sequence = $seq",
                    ("$b", (object?)Relative(op.BackupPath) ?? DBNull.Value), ("$t", Relative(op.TempPath!)!), ("$batch", DbText.Id(batch.BatchId)), ("$seq", op.Sequence));
            Exec(c, tx, "UPDATE apply_batches SET state = 'prepared' WHERE id = $id AND state = 'planned'", ("$id", DbText.Id(batch.BatchId)));
            Audit(c, tx, batch.BatchId, "batch_prepared", new { files = batch.Files.Count });
            return 0;
        });
        _faults?.At(ApplyStep.Prepared, 0);

        // 6. Replace file by file; what was replaced must be the before bytes, or everything stops (unknown content is kept).
        _db.Write((c, tx) => Exec(c, tx, "UPDATE apply_batches SET state = 'applying' WHERE id = $id", ("$id", DbText.Id(batch.BatchId))));
        foreach (var op in batch.Files)
        {
            _faults?.At(ApplyStep.BeforeReplace, op.Sequence);
            var result = ReplaceOne(batch, op);
            if (result is not null) return result;
            _faults?.At(ApplyStep.AfterReplace, op.Sequence);
        }

        // 7. Every file is the after bytes: confirm the items, the ledger and the history together.
        _faults?.At(ApplyStep.BeforeDbCommit, 0);
        Commit(batch);
        _faults?.At(ApplyStep.Committed, 0);

        // 8. Clean up what is no longer needed (the backups stay).
        foreach (var op in batch.Files) TryDelete(op.TempPath);
        return new(ApplyOutcome.Committed, $"{batch.Files.Count} 個の管理ファイルに反映しました（項目 {batch.Changes.Count} 件）。", []);
    }

    /// <summary>Replaces one file (or creates it) and checks the replaced bytes. Null when it went well.</summary>
    internal ApplyResult? ReplaceOne(BatchLedger batch, BatchFileOp op)
    {
        var path = _store.FullPath(op.RelativePath);
        var directory = _store.FullPath(batch.PlanPath);
        if (op.BeforeHash is null)
        {
            if (File.Exists(path))
                return Recovery(batch, op, [$"{op.RelativePath} は新規作成の予定でしたが、すでにあります。上書きしませんでした。"]);
            File.Move(op.TempPath!, path, overwrite: false);
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(directory, "replaced"));
            var replaced = Path.Combine(directory, "replaced", $"{op.Sequence:00}-{op.FileId:N}.replaced");
            File.Replace(op.TempPath!, path, replaced, ignoreMetadataErrors: false);
            if (!Matches(File.ReadAllBytes(replaced), op.BeforeHash))
            {
                // Someone wrote the file between the check and the replacement: keep their version next to ours and stop.
                return Recovery(batch, op, [$"{op.RelativePath} が置き換えの直前に外部で変更されていました。外部の内容は {Relative(replaced)} に残しています。"]);
            }
        }
        if (!Matches(File.ReadAllBytes(path), op.AfterHash)) return Recovery(batch, op, [$"{op.RelativePath} が置き換えの後に期待した内容になっていません。"]);
        _db.Write((c, tx) => Exec(c, tx, "UPDATE apply_file_ops SET state = 'verified' WHERE batch_id = $batch AND sequence = $seq",
            ("$batch", DbText.Id(batch.BatchId)), ("$seq", op.Sequence)));
        return null;
    }

    /// <summary>Step 7: items, evidence, versions, the applied ledger, file hashes and the batch state, in one transaction.</summary>
    internal void Commit(BatchLedger batch)
    {
        var now = DbText.Utc(_time.GetUtcNow());
        _db.Write((c, tx) =>
        {
            using (var state = Command(c, tx, "SELECT state FROM apply_batches WHERE id = $id"))
            {
                state.Parameters.AddWithValue("$id", DbText.Id(batch.BatchId));
                if ((string?)state.ExecuteScalar() == "committed") return 0; // committed once only (AT-17)
            }
            foreach (var change in batch.Changes)
            {
                var after = change.After;
                Exec(c, tx, """
                    INSERT INTO items (id, workspace_id, display_id, kind, project, title, description, status, priority, assignee, due_date,
                                       parent_issue_id, revision, created_at, updated_at)
                    VALUES ($id, $ws, $display, $kind, $project, $title, $description, $status, $priority, $assignee, $due, $parent, $rev, $created, $updated)
                    ON CONFLICT (id) DO UPDATE SET project = excluded.project, title = excluded.title, description = excluded.description,
                        status = excluded.status, priority = excluded.priority, assignee = excluded.assignee, due_date = excluded.due_date,
                        parent_issue_id = excluded.parent_issue_id, revision = excluded.revision, updated_at = excluded.updated_at
                    """,
                    ("$id", DbText.Id(after.Id)), ("$ws", DbText.Id(_workspace.Info.WorkspaceId)), ("$display", after.DisplayId),
                    ("$kind", ManagementStore.KindText(after.Kind)), ("$project", after.Project), ("$title", after.Title), ("$description", after.Description),
                    ("$status", ItemStatuses.Label(after.Status)), ("$priority", (object?)after.Priority?.ToString().ToLowerInvariant() ?? DBNull.Value),
                    ("$assignee", (object?)after.Assignee ?? DBNull.Value), ("$due", (object?)after.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value),
                    ("$parent", after.ParentIssueId is { } p ? DbText.Id(p) : DBNull.Value), ("$rev", after.Revision),
                    ("$created", DbText.Utc(after.CreatedAtUtc)), ("$updated", DbText.Utc(after.UpdatedAtUtc)));
                foreach (var e in after.Evidence)
                    Exec(c, tx, """
                        INSERT INTO item_evidence (id, item_id, session_id, transcript_revision, segment_id, quote, batch_id)
                        VALUES ($id, $item, $session, $rev, $segment, $quote, $batch) ON CONFLICT DO NOTHING
                        """,
                        ("$id", DbText.Id(DbText.DerivedId(after.Id, $"evidence/{e.SessionId:D}/{e.SegmentId:D}"))), ("$item", DbText.Id(after.Id)),
                        ("$session", DbText.Id(e.SessionId)), ("$rev", e.TranscriptRevision), ("$segment", DbText.Id(e.SegmentId)), ("$quote", e.Quote),
                        ("$batch", DbText.Id(batch.BatchId)));
                Exec(c, tx, "INSERT INTO item_versions (item_id, revision, batch_id, before_json, after_json) VALUES ($item, $rev, $batch, $before, $after)",
                    ("$item", DbText.Id(after.Id)), ("$rev", after.Revision), ("$batch", DbText.Id(batch.BatchId)),
                    ("$before", change.Before is { } b ? CanonicalJson.Serialize(ItemJson.ToNode(b)) : DBNull.Value), ("$after", CanonicalJson.Serialize(ItemJson.ToNode(after))));
                Exec(c, tx, "INSERT INTO candidate_applications (candidate_key, fingerprint, batch_id, item_id, decision_hash) VALUES ($key, $print, $batch, $item, $hash)",
                    ("$key", change.Key), ("$print", change.Fingerprint ?? string.Empty), ("$batch", DbText.Id(batch.BatchId)), ("$item", DbText.Id(after.Id)), ("$hash", change.EvidenceHash));
            }
            foreach (var op in batch.Files)
                Exec(c, tx, "UPDATE managed_files SET last_hash = $hash WHERE id = $id", ("$hash", op.AfterHash), ("$id", DbText.Id(op.FileId)));
            Exec(c, tx, "UPDATE apply_batches SET state = 'committed', committed_at = $at WHERE id = $id", ("$at", now), ("$id", DbText.Id(batch.BatchId)));
            Exec(c, tx, "UPDATE approvals SET state = 'applied' WHERE id = (SELECT approval_id FROM apply_batches WHERE id = $id)", ("$id", DbText.Id(batch.BatchId)));
            Audit(c, tx, batch.BatchId, "batch_committed", new { changes = batch.Changes.Count, files = batch.Files.Count });
            return 0;
        });
    }

    private List<string> Verify(BatchLedger batch)
    {
        var problems = new List<string>();
        if (batch.PlanHash is { } expected && batch.PlanJson is { } json)
        {
            var copy = JsonNode.Parse(json.ToJsonString())!.AsObject();
            copy.Remove("plan_hash");
            if (CanonicalJson.Sha256(copy) != expected) problems.Add("変更計画のファイルが承認の後に変わっています。");
        }
        var items = _store.Items().ToDictionary(i => i.Id);
        var (applied, _) = _store.Applied();
        foreach (var change in batch.Changes)
        {
            if (ManagementStore.ParseKey(change.Key) is { } key && applied.Contains(key)) problems.Add($"候補 {change.Key} はすでに反映済みです。");
            var current = items.GetValueOrDefault(change.After.Id);
            if (change.ExpectedRevision == 0 && current is not null) problems.Add($"{change.After.DisplayId} はすでにあります。");
            if (change.ExpectedRevision > 0 && current?.Revision != change.ExpectedRevision)
                problems.Add($"{change.After.DisplayId} が計画の後に変わっています（Revision {change.ExpectedRevision} → {current?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "なし"}）。");
        }
        return problems;
    }

    private ApplyResult Conflict(BatchLedger batch, IReadOnlyList<string> problems)
    {
        _db.Write((c, tx) =>
        {
            Audit(c, tx, batch.BatchId, "batch_conflict", new { problems });
            return 0;
        });
        return new(ApplyOutcome.Conflict, "反映を中止しました。管理ファイルは変わっていません。最新の内容で計画を作り直してください。", problems);
    }

    private ApplyResult Recovery(BatchLedger batch, BatchFileOp op, IReadOnlyList<string> problems)
    {
        _db.Write((c, tx) =>
        {
            Exec(c, tx, "UPDATE apply_batches SET state = 'recovery_required', error_code = 'APPLY-RECOVERY' WHERE id = $id", ("$id", DbText.Id(batch.BatchId)));
            Audit(c, tx, batch.BatchId, "batch_recovery_required", new { file = op.RelativePath, problems });
            return 0;
        });
        return new(ApplyOutcome.RecoveryRequired, "反映の途中で止めました。どちらの内容も残しています。履歴・復旧の画面で確かめてください。", problems);
    }

    private static bool Matches(byte[]? bytes, string? hash) => hash is null ? bytes is null : bytes is not null && Sha(bytes) == hash;

    internal static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string? Relative(string? full) => full is null ? null : Path.GetRelativePath(_workspace.Root, full).Replace(Path.DirectorySeparatorChar, '/');

    private void Audit(SqliteConnection c, SqliteTransaction tx, Guid batchId, string type, object details) =>
        Exec(c, tx, """
            INSERT INTO audit_events (id, timestamp, event_type, entity_type, entity_id, batch_id, details_json)
            VALUES ($id, $at, $type, 'apply_batch', $batch, $batch, $details)
            """, ("$id", DbText.Id(Guid.CreateVersion7())), ("$at", DbText.Utc(_time.GetUtcNow())), ("$type", type), ("$batch", DbText.Id(batchId)),
            ("$details", JsonSerializer.Serialize(details)));

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    internal static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(c, tx, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>Where the app may write a management file (detail spec 14.1).</summary>
public static class ManagedFileGuard
{
    /// <summary>Only a regular local file inside the workspace, with no reparse point on the way (AT-16).</summary>
    public static void CheckLocation(string path, string workspaceRoot)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("管理ファイルがワークスペースの外にあります。");
        if (Core.IO.SafePath.Problem(Path.GetRelativePath(root, full)) is { } problem) throw new UnauthorizedAccessException($"管理ファイルのパスが使えません: {problem}");
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new UnauthorizedAccessException("ネットワーク上のファイルは自動では更新しません。");
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType != DriveType.Fixed) throw new UnauthorizedAccessException("固定ディスク以外（ネットワーク・取り外し可能）のファイルは自動では更新しません。");
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)!); dir is not null && dir.FullName.Length >= root.Length - 1; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("リパースポイントを通る場所のファイルは自動では更新しません。");
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("リンクになっているファイルは自動では更新しません。");
    }

    /// <summary>The bytes, read without sharing (a writer elsewhere makes this fail); null when the file does not exist.</summary>
    public static byte[]? ReadExclusive(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

/// <summary>A file operation of a batch as recorded in <c>apply_file_ops</c>.</summary>
public sealed class BatchFileOp
{
    public required int Sequence { get; init; }

    public required Guid FileId { get; init; }

    public required string RelativePath { get; init; }

    public required string? BeforeHash { get; init; }

    public required string AfterHash { get; init; }

    public required string StagedPath { get; init; }

    public string? BackupPath { get; set; }

    public string? TempPath { get; set; }

    public required string State { get; init; }
}

/// <summary>A change of a batch: the candidate, the item before and after.</summary>
public sealed record BatchChange(string Key, string? Fingerprint, int ExpectedRevision, ManagementItem? Before, ManagementItem After, string EvidenceHash);

/// <summary>Everything about a saved batch, read back from the database and its transaction folder.</summary>
public sealed class BatchLedger
{
    public required Guid BatchId { get; init; }

    public required string State { get; init; }

    public required string PlanPath { get; init; }

    public required string? PlanHash { get; init; }

    public required JsonObject? PlanJson { get; init; }

    public required IReadOnlyList<BatchFileOp> Files { get; init; }

    public required IReadOnlyList<BatchChange> Changes { get; init; }

    public static BatchLedger Load(VoiceJournalDatabase db, ManagementStore store, Guid batchId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(store);
        var (state, planPath, planHash, candidates) = db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT b.state, b.plan_path, a.plan_hash, a.candidate_revisions_json FROM apply_batches b JOIN approvals a ON a.id = b.approval_id WHERE b.id = $id
                """;
            cmd.Parameters.AddWithValue("$id", DbText.Id(batchId));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException($"Batch {batchId:D} does not exist.");
            return (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3));
        });
        var files = db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT o.sequence, o.file_id, f.path, o.before_hash, o.after_hash, o.staged_path, o.backup_path, o.temp_path, o.state
                FROM apply_file_ops o JOIN managed_files f ON f.id = o.file_id WHERE o.batch_id = $id ORDER BY o.sequence
                """;
            cmd.Parameters.AddWithValue("$id", DbText.Id(batchId));
            using var r = cmd.ExecuteReader();
            var list = new List<BatchFileOp>();
            while (r.Read())
                list.Add(new BatchFileOp
                {
                    Sequence = r.GetInt32(0), FileId = Guid.Parse(r.GetString(1)), RelativePath = r.GetString(2),
                    BeforeHash = r.IsDBNull(3) ? null : r.GetString(3), AfterHash = r.GetString(4), StagedPath = r.GetString(5),
                    BackupPath = r.IsDBNull(6) ? null : store.FullPath(r.GetString(6)), TempPath = r.IsDBNull(7) ? null : store.FullPath(r.GetString(7)),
                    State = r.GetString(8),
                });
            return list;
        });
        var directory = store.FullPath(planPath);
        var planFile = Path.Combine(directory, "plan.json");
        var plan = File.Exists(planFile) ? JsonNode.Parse(File.ReadAllText(planFile, Encoding.UTF8))!.AsObject() : null;
        var ledger = JsonNode.Parse(candidates)!.AsArray();
        var changes = new List<BatchChange>();
        if (plan?["changes"] is JsonArray planned)
        {
            for (var i = 0; i < planned.Count; i++)
            {
                var ch = planned[i]!;
                JsonNode Snapshot(string id) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "snapshots", id + ".json"), Encoding.UTF8))!;
                var before = (string?)ch["before_snapshot_id"] is { } b ? ItemJson.FromNode(Snapshot(b)) : null;
                var after = ItemJson.FromNode(Snapshot((string)ch["after_snapshot_id"]!));
                var entry = i < ledger.Count ? ledger[i] : null;
                changes.Add(new BatchChange((string?)entry?["key"] ?? (string)ch["candidate_id"]!, (string?)entry?["fingerprint"], (int)ch["expected_revision"]!, before, after, (string)ch["evidence_hash"]!));
            }
        }
        return new BatchLedger { BatchId = batchId, State = state, PlanPath = planPath, PlanHash = planHash, PlanJson = plan, Files = files, Changes = changes };
    }
}

/// <summary>What <see cref="ApplyRecovery"/> did with an unfinished batch (detail spec 14.3).</summary>
public enum RecoveryAction
{
    /// <summary>Every file still had its before bytes: the batch is planned again and can be applied.</summary>
    Reset,

    /// <summary>Every file had its after bytes: the database was confirmed once.</summary>
    CommittedDatabase,

    /// <summary>Some files were replaced: the rest were replaced with the approved bytes and the batch was confirmed.</summary>
    Completed,

    /// <summary>A file had bytes that are neither: nothing was changed; the user resolves it.</summary>
    RecoveryRequired,
}

public sealed record RecoveryReport(Guid BatchId, RecoveryAction Action, IReadOnlyList<string> Notes);

/// <summary>
/// Decides what to do with batches left in <c>prepared</c> / <c>applying</c> by a crash (detail spec 14.3). Run at
/// start-up and before applying. It compares each target file with the before and after hashes of the ledger and never
/// writes over bytes it does not know.
/// </summary>
public sealed class ApplyRecovery
{
    private readonly VoiceJournalDatabase _db;
    private readonly ManagementStore _store;
    private readonly ApplyEngine _engine;

    public ApplyRecovery(VoiceJournalDatabase db, ManagementStore store, ApplyEngine engine)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public IReadOnlyList<RecoveryReport> RecoverAll()
    {
        var unfinished = _db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id FROM apply_batches WHERE state IN ('prepared', 'applying') ORDER BY created_at";
            using var r = cmd.ExecuteReader();
            var ids = new List<Guid>();
            while (r.Read()) ids.Add(Guid.Parse(r.GetString(0)));
            return ids;
        });
        return [.. unfinished.Select(Recover)];
    }

    public RecoveryReport Recover(Guid batchId)
    {
        var batch = BatchLedger.Load(_db, _store, batchId);
        var states = batch.Files.Select(op =>
        {
            var path = _store.FullPath(op.RelativePath);
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var hash = bytes is null ? null : ApplyEngine.Sha(bytes);
            return (Op: op, IsBefore: hash == op.BeforeHash, IsAfter: hash == op.AfterHash);
        }).ToList();

        if (states.Any(s => !s.IsBefore && !s.IsAfter))
        {
            var notes = states.Where(s => !s.IsBefore && !s.IsAfter).Select(s => $"{s.Op.RelativePath} が承認前とも承認後とも違う内容です（外部で編集された可能性）。どちらも上書きしていません。").ToList();
            SetState(batchId, "recovery_required", notes);
            return new RecoveryReport(batchId, RecoveryAction.RecoveryRequired, notes);
        }
        if (states.All(s => s.IsBefore))
        {
            _db.Write((c, tx) =>
            {
                ApplyEngine.Exec(c, tx, "UPDATE apply_file_ops SET state = 'planned', backup_path = NULL, temp_path = NULL WHERE batch_id = $id", ("$id", DbText.Id(batchId)));
                ApplyEngine.Exec(c, tx, "UPDATE apply_batches SET state = 'planned' WHERE id = $id", ("$id", DbText.Id(batchId)));
                return 0;
            });
            foreach (var s in states) TryDelete(s.Op.TempPath);
            return new RecoveryReport(batchId, RecoveryAction.Reset, ["どのファイルもまだ置き換えていませんでした。反映をやり直せます。"]);
        }
        var completed = false;
        foreach (var s in states.Where(s => !s.IsAfter))
        {
            // Forward: the approved after bytes are staged in the transaction folder.
            if (s.Op.TempPath is null || !File.Exists(s.Op.TempPath) || ApplyEngine.Sha(File.ReadAllBytes(s.Op.TempPath)) != s.Op.AfterHash)
            {
                var temp = Path.Combine(Path.GetDirectoryName(_store.FullPath(s.Op.RelativePath))!, $".vj-{batchId:N}-{s.Op.Sequence:00}.tmp");
                File.Copy(_store.FullPath(s.Op.StagedPath), temp, overwrite: true);
                s.Op.TempPath = temp;
            }
            if (_engine.ReplaceOne(batch, s.Op) is { } failed)
                return new RecoveryReport(batchId, RecoveryAction.RecoveryRequired, failed.Problems);
            completed = true;
        }
        _engine.Commit(batch);
        foreach (var s in states) TryDelete(s.Op.TempPath);
        return completed
            ? new RecoveryReport(batchId, RecoveryAction.Completed, ["途中まで置き換えたファイルを、承認した内容まで進めて反映を終えました。"])
            : new RecoveryReport(batchId, RecoveryAction.CommittedDatabase, ["ファイルは反映済みでした。データベースの確定だけを行いました。"]);
    }

    private void SetState(Guid batchId, string state, IReadOnlyList<string> notes) => _db.Write((c, tx) =>
    {
        ApplyEngine.Exec(c, tx, "UPDATE apply_batches SET state = $state, error_code = 'APPLY-RECOVERY' WHERE id = $id", ("$state", state), ("$id", DbText.Id(batchId)));
        ApplyEngine.Exec(c, tx, """
            INSERT INTO audit_events (id, timestamp, event_type, entity_type, entity_id, batch_id, details_json)
            VALUES ($id, $at, 'batch_recovery_required', 'apply_batch', $batch, $batch, $details)
            """, ("$id", DbText.Id(Guid.CreateVersion7())), ("$at", DbText.Utc(DateTimeOffset.UtcNow)), ("$batch", DbText.Id(batchId)),
            ("$details", JsonSerializer.Serialize(new { notes })));
        return 0;
    });

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
