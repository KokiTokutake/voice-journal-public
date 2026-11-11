using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Core.Management;

/// <summary>How the app may change a management file (detail spec 11.4).</summary>
public enum ManagedFileMode
{
    /// <summary>The app updates only its own blocks and keeps every other byte (a file the app created).</summary>
    ManagedBlocks,

    /// <summary>The app only appends new items; updates of existing items are saved as proposals (AT-14).</summary>
    AppendOnly,
}

/// <summary>A registered management file. The path is resolved by the app, never chosen by a model (17).</summary>
public sealed record ManagedFile
{
    public required Guid FileId { get; init; }

    /// <summary>Relative to the workspace (e.g. <c>management/issues.md</c>).</summary>
    public required string RelativePath { get; init; }

    public required CandidateKind Kind { get; init; }

    public ManagedFileMode Mode { get; init; } = ManagedFileMode.ManagedBlocks;

    /// <summary>The bytes now on disk; null when the file does not exist yet (it will be created).</summary>
    public byte[]? CurrentBytes { get; init; }
}

public sealed record PlanningInput
{
    public required Guid WorkspaceId { get; init; }

    /// <summary>The confirmed items (their current revisions).</summary>
    public required IReadOnlyList<ManagementItem> Items { get; init; }

    /// <summary>The reviews the user approved, in the order they are shown.</summary>
    public required IReadOnlyList<CandidateReview> Reviews { get; init; }

    /// <summary>The applied ledger (12.2-2): candidates already applied in an earlier batch.</summary>
    public IReadOnlySet<CandidateKey> Applied { get; init; } = new HashSet<CandidateKey>();

    /// <summary>Candidates in a saved plan that is not applied yet: planning them again would apply them twice (AT-12).</summary>
    public IReadOnlySet<CandidateKey> Pending { get; init; } = new HashSet<CandidateKey>();

    /// <summary>Items whose block edited by hand may be rewritten: the user chose to take the edit in or to discard it (11.5).</summary>
    public IReadOnlySet<Guid> AcceptEditedBlocks { get; init; } = new HashSet<Guid>();

    /// <summary>Fingerprints of the creations in saved plans not applied yet: the same candidate from a regenerated record (AT-12).</summary>
    public IReadOnlySet<string> PendingFingerprints { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Kind + evidence hash + normalized title of applied creations: the same candidate from a regenerated analysis (AT-12).</summary>
    public IReadOnlySet<string> AppliedFingerprints { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public required IReadOnlyList<ManagedFile> Files { get; init; }

    /// <summary>The next display numbers (never reused, 11.1).</summary>
    public required long NextIssueNumber { get; init; }

    public required long NextTaskNumber { get; init; }

    public required DateTimeOffset NowUtc { get; init; }
}

public sealed record PlannedChange
{
    public required CandidateKey Key { get; init; }

    public required Guid CandidateUuid { get; init; }

    public required ReviewChoice Action { get; init; }

    public ManagementItem? Before { get; init; }

    public required ManagementItem After { get; init; }

    public required string EvidenceHash { get; init; }

    public required int ExpectedRevision { get; init; }

    public Guid? BeforeSnapshotId { get; init; }

    public required Guid AfterSnapshotId { get; init; }

    /// <summary>For a creation: kind, evidence and normalized title, kept in the applied ledger (AT-12).</summary>
    public string? Fingerprint { get; init; }

    /// <summary>The file the change is written to; null for a change kept in the database only (a mention in an append-only file).</summary>
    public Guid? FileId { get; init; }
}

/// <summary>An update the app does not apply: saved for the user to apply by hand (AT-14, append-only mode).</summary>
public sealed record PlannedProposal(CandidateKey Key, ManagementItem Target, ManagementItem Proposed, string Reason);

public sealed record SkippedCandidate(CandidateKey Key, string Title, string Reason);

public sealed record PlannedFile
{
    public required Guid FileId { get; init; }

    public required string RelativePath { get; init; }

    /// <summary><c>create</c> or <c>replace</c>.</summary>
    public required string Operation { get; init; }

    public string? BeforeHash { get; init; }

    public required string BeforeText { get; init; }

    public required string AfterText { get; init; }

    public required byte[] AfterBytes { get; init; }

    public required string AfterHash { get; init; }

    public required ManagedEncoding Encoding { get; init; }

    public required ManagedNewline Newline { get; init; }

    public required Guid StagedBlobId { get; init; }
}

/// <summary>A change plan (detail spec 17): made by the app from approved reviews, reviewed, then fixed. Applying it is Phase F.</summary>
public sealed record ChangePlan
{
    public required Guid BatchId { get; init; }

    public required Guid ApprovalId { get; init; }

    /// <summary>Derived from what was approved: the same approval asked twice gives the same key (13.2, AT-12).</summary>
    public required Guid IdempotencyKey { get; init; }

    public required Guid WorkspaceId { get; init; }

    public required IReadOnlyList<PlannedChange> Changes { get; init; }

    public required IReadOnlyList<PlannedProposal> Proposals { get; init; }

    public required IReadOnlyList<SkippedCandidate> Skipped { get; init; }

    public required IReadOnlyList<PlannedFile> Files { get; init; }

    /// <summary>Item snapshots by id (canonical JSON objects, stored immutable).</summary>
    public required IReadOnlyDictionary<Guid, JsonObject> Snapshots { get; init; }

    /// <summary>The plan JSON of detail spec 17 (with no files for a database-only batch); null when nothing is planned.</summary>
    public JsonObject? Json { get; init; }

    public string? PlanHash { get; init; }

    public required long NextIssueNumber { get; init; }

    public required long NextTaskNumber { get; init; }

    public bool IsEmpty => Changes.Count == 0 && Proposals.Count == 0;
}

/// <summary>Builds a <see cref="ChangePlan"/> from approved reviews (detail spec 12.3, 13.2, 14.2-1, 17).</summary>
public static class ChangePlanner
{
    public const int MaxChanges = 200;
    public const int MaxFiles = 20;

    public static ChangePlan Plan(PlanningInput input, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(newId);
        var original = input.Items.ToDictionary(i => i.Id);
        var working = new Dictionary<Guid, ManagementItem>(original);
        var changes = new List<PlannedChange>();
        var proposals = new List<PlannedProposal>();
        var skipped = new List<SkippedCandidate>();
        var snapshots = new Dictionary<Guid, JsonObject>();
        var seen = new HashSet<CandidateKey>();
        var createdByCandidate = new Dictionary<(Guid, int, string), Guid>();
        var nextIssue = input.NextIssueNumber;
        var nextTask = input.NextTaskNumber;

        // The files, decoded and parsed once. A file that cannot be read or has broken markers takes no change (11.5).
        var files = new Dictionary<CandidateKind, (ManagedFile File, ManagedText? Text, ManagedFileLayout? Layout, string? Problem)>();
        foreach (var file in input.Files)
        {
            if (files.ContainsKey(file.Kind)) continue;
            if (file.CurrentBytes is null)
            {
                files[file.Kind] = (file, null, null, null);
                continue;
            }
            var text = ManagedText.Decode(file.CurrentBytes);
            if (text is null)
            {
                files[file.Kind] = (file, null, null, "管理ファイルの文字コードが UTF-8・UTF-16LE（BOM 付き）のどれでもありません");
                continue;
            }
            var layout = ManagementMarkdown.Parse(text.Text, input.WorkspaceId);
            files[file.Kind] = (file, text, layout, layout.Errors.Count > 0 ? "管理ファイルの形式に問題があります: " + string.Join("、", layout.Errors) : null);
        }

        Guid Snapshot(ManagementItem item)
        {
            var id = newId();
            snapshots[id] = ItemJson.ToNode(item);
            return id;
        }

        foreach (var review in input.Reviews)
        {
            var key = review.Key;
            var title = review.Fields.Title;
            void Skip(string reason) => skipped.Add(new SkippedCandidate(key, title, reason));

            if (input.Applied.Contains(key)) { Skip("この候補はすでに反映済みです"); continue; }
            if (input.Pending.Contains(key)) { Skip("保存済みの変更計画（反映待ち）に入っています"); continue; }
            if (!seen.Add(key)) { Skip("同じ候補が 2 回選ばれています"); continue; }
            if (changes.Count >= MaxChanges) { Skip($"1 回の反映は {MaxChanges} 件までです"); continue; }

            var kind = review.Candidate.Kind;
            var evidenceHash = CandidateRules.EvidenceHash(review.Candidate);
            original.TryGetValue(review.TargetItemId ?? Guid.Empty, out var target);
            if (!CandidateRules.IsReadyForPlan(review, target, out var notReady)) { Skip(notReady!); continue; }
            if (review.Choice == ReviewChoice.Create && input.AppliedFingerprints.Contains(Fingerprint(kind, evidenceHash, title)))
            {
                Skip("同じ根拠・同じタイトルの候補が、前に反映されています（同じ音声の再処理）。別の項目として登録するときはタイトルを変えてください");
                continue;
            }
            if (review.Choice == ReviewChoice.Create && input.PendingFingerprints.Contains(Fingerprint(kind, evidenceHash, title)))
            {
                Skip("同じ根拠・同じタイトルの候補が、保存済みの変更計画（反映待ち）に入っています（記録の作り直し）");
                continue;
            }
            if (!files.TryGetValue(kind, out var file)) { Skip("保存先の管理ファイルが登録されていません"); continue; }
            if (file.Problem is { } problem) { Skip(problem); continue; }

            var evidence = review.ItemEvidence?.ToList() ?? [.. review.Candidate.Evidence.Select(e => new ItemEvidence
            {
                SessionId = key.SessionId,
                TranscriptRevision = review.TranscriptRevision,
                SegmentId = e.SegmentId,
                Quote = e.Quote,
            })];
            var candidateUuid = CandidateUuid(key);

            switch (review.Choice)
            {
                case ReviewChoice.Create:
                {
                    var number = kind == CandidateKind.Issue ? nextIssue : nextTask;
                    Guid? parent = review.ParentIssueId;
                    if (parent is null && kind == CandidateKind.Task && review.Candidate.ParentIssueCandidateId is { } pc
                        && createdByCandidate.TryGetValue((key.SessionId, key.AnalysisRevision, pc), out var createdParent))
                        parent = createdParent;
                    var f = review.Fields;
                    var item = new ManagementItem
                    {
                        Id = newId(),
                        DisplayId = DisplayIds.Format(kind, number),
                        Kind = kind,
                        Project = review.Project,
                        Title = ExtractionConverter.Clean(f.Title),
                        Description = f.Description.Trim(),
                        Status = f.Status ?? ItemStatuses.Initial(kind),
                        Priority = f.Priority,
                        Assignee = f.Assignee,
                        DueDate = f.DueDate,
                        ParentIssueId = parent,
                        Revision = 1,
                        CreatedAtUtc = input.NowUtc,
                        UpdatedAtUtc = input.NowUtc,
                        Evidence = evidence,
                    };
                    var problems = item.Problems(working);
                    if (problems.Count > 0) { Skip(string.Join("、", problems)); continue; }
                    if (kind == CandidateKind.Issue) nextIssue++;
                    else nextTask++;
                    working[item.Id] = item;
                    createdByCandidate[(key.SessionId, key.AnalysisRevision, key.CandidateId)] = item.Id;
                    changes.Add(new PlannedChange
                    {
                        Key = key, CandidateUuid = candidateUuid, Action = ReviewChoice.Create, After = item, EvidenceHash = evidenceHash,
                        ExpectedRevision = 0, AfterSnapshotId = Snapshot(item), FileId = file.File.FileId, Fingerprint = Fingerprint(kind, evidenceHash, title),
                    });
                    break;
                }
                case ReviewChoice.Update or ReviewChoice.Mention:
                {
                    if (file.File.Mode == ManagedFileMode.ManagedBlocks && !input.AcceptEditedBlocks.Contains(target!.Id) && file.Text is { } current && file.Layout is { } layout
                        && layout.Blocks.FirstOrDefault(b => b.ItemId == target!.Id) is { } block
                        && !SameBlock(current.Text.Substring(block.Start, block.Length), ManagementMarkdown.Block(original[target!.Id], original, current.LineEnd)))
                    {
                        // 11.5: an edit by hand inside a block is taken in first; the app never writes over it.
                        Skip($"{target!.DisplayId} の管理ブロックが手で編集されています。先にその編集を取り込むか元に戻してください（上書きはしません）");
                        continue;
                    }
                    var before = working[target!.Id];
                    var after = review.Choice == ReviewChoice.Update ? Apply(before, review) : before;
                    after = after with
                    {
                        Revision = before.Revision + 1,
                        UpdatedAtUtc = input.NowUtc,
                        Evidence = [.. before.Evidence, .. evidence.Where(e => !before.Evidence.Any(b => b.SegmentId == e.SegmentId && b.SessionId == e.SessionId))],
                    };
                    var problems = after.Problems(working);
                    if (problems.Count > 0) { Skip(string.Join("、", problems)); continue; }
                    if (file.File.Mode == ManagedFileMode.AppendOnly && review.Choice == ReviewChoice.Update)
                    {
                        proposals.Add(new PlannedProposal(key, before, after, "追記専用の管理ファイルなので、既存の項目は書き換えず、更新の提案として保存します"));
                        continue;
                    }
                    working[after.Id] = after;
                    changes.Add(new PlannedChange
                    {
                        Key = key, CandidateUuid = candidateUuid, Action = review.Choice.Value, Before = before, After = after, EvidenceHash = evidenceHash,
                        ExpectedRevision = before.Revision, BeforeSnapshotId = Snapshot(before), AfterSnapshotId = Snapshot(after),
                        FileId = file.File.Mode == ManagedFileMode.AppendOnly ? null : file.File.FileId,
                    });
                    break;
                }
            }
        }

        var planned = new List<PlannedFile>();
        foreach (var (kind, file) in files)
        {
            var touched = changes.Where(c => c.FileId == file.File.FileId).ToList();
            if (touched.Count == 0 || file.Problem is not null) continue;
            var text = file.Text ?? new ManagedText(string.Empty, ManagedEncoding.Utf8, ManagedNewline.Crlf);
            var lineEnd = file.Text is null ? "\r\n" : text.LineEnd;
            var after = file.Text is null ? ManagementMarkdown.NewFile(kind, input.WorkspaceId, lineEnd) : text.Text;
            var layout = file.Text is null ? ManagementMarkdown.Parse(after, input.WorkspaceId) : file.Layout!;

            // Replace the blocks of changed items from the end, so earlier offsets stay valid; append the others.
            var latest = touched.GroupBy(c => c.After.Id).Select(g => g.Last().After).ToList();
            var blocks = layout.Blocks.ToDictionary(b => b.ItemId);
            foreach (var item in latest.Where(i => blocks.ContainsKey(i.Id)).OrderByDescending(i => blocks[i.Id].Start))
            {
                var b = blocks[item.Id];
                after = after[..b.Start] + ManagementMarkdown.Block(item, working, lineEnd) + after[(b.Start + b.Length)..];
            }
            var appended = latest.Where(i => !blocks.ContainsKey(i.Id)).ToList();
            if (appended.Count > 0)
            {
                if (after.Length > 0 && !after.EndsWith('\n')) after += lineEnd;
                if (layout.WorkspaceId is null) after += ManagementMarkdown.AppendArea(input.WorkspaceId, lineEnd);
                foreach (var item in appended) after += lineEnd + ManagementMarkdown.Block(item, working, lineEnd);
            }
            var bytes = text.Encode(after);
            planned.Add(new PlannedFile
            {
                FileId = file.File.FileId,
                RelativePath = file.File.RelativePath,
                Operation = file.File.CurrentBytes is null ? "create" : "replace",
                BeforeHash = file.File.CurrentBytes is null ? null : Sha256(file.File.CurrentBytes),
                BeforeText = file.Text?.Text ?? string.Empty,
                AfterText = after,
                AfterBytes = bytes,
                AfterHash = Sha256(bytes),
                Encoding = text.Encoding,
                Newline = file.Text is null ? ManagedNewline.Crlf : text.Newline,
                StagedBlobId = newId(),
            });
        }

        var idempotency = IdempotencyKey(input, changes, planned);
        var batchId = newId();
        var approvalId = newId();
        JsonObject? json = null;
        string? hash = null;
        // A batch without file operations (a mention in an append-only file) keeps the same JSON with no files: the same
        // approval, revision and idempotency rules apply to it (17).
        if (planned.Count <= MaxFiles && changes.Count > 0)
        {
            json = new JsonObject
            {
                ["schema_version"] = "1.0",
                ["batch_id"] = batchId.ToString("D"),
                ["workspace_id"] = input.WorkspaceId.ToString("D"),
                ["approval_id"] = approvalId.ToString("D"),
                ["idempotency_key"] = idempotency.ToString("D"),
                ["files"] = new JsonArray([.. planned.Select(f => (JsonNode)new JsonObject
                {
                    ["file_id"] = f.FileId.ToString("D"),
                    ["operation"] = f.Operation,
                    ["before_hash"] = f.BeforeHash,
                    ["after_hash"] = f.AfterHash,
                    ["encoding"] = ManagedText.Label(f.Encoding),
                    ["newline"] = ManagedText.Label(f.Newline),
                    ["staged_blob_id"] = f.StagedBlobId.ToString("D"),
                })]),
                ["changes"] = new JsonArray([.. changes.Select(c => (JsonNode)new JsonObject
                {
                    ["candidate_id"] = c.CandidateUuid.ToString("D"),
                    ["candidate_revision"] = c.Key.AnalysisRevision,
                    ["item_id"] = c.After.Id.ToString("D"),
                    ["action"] = c.Action.ToString().ToLowerInvariant(),
                    ["expected_revision"] = c.ExpectedRevision,
                    ["before_snapshot_id"] = c.BeforeSnapshotId?.ToString("D"),
                    ["after_snapshot_id"] = c.AfterSnapshotId.ToString("D"),
                    ["evidence_hash"] = c.EvidenceHash,
                })]),
            };
            hash = CanonicalJson.Sha256(json);
            json["plan_hash"] = hash;
        }

        return new ChangePlan
        {
            BatchId = batchId,
            ApprovalId = approvalId,
            IdempotencyKey = idempotency,
            WorkspaceId = input.WorkspaceId,
            Changes = changes,
            Proposals = proposals,
            Skipped = skipped,
            Files = planned,
            Snapshots = snapshots,
            Json = json,
            PlanHash = hash,
            NextIssueNumber = nextIssue,
            NextTaskNumber = nextTask,
        };
    }

    /// <summary>Whether a block in the file is what the app wrote for the item (line ends aside).</summary>
    public static bool SameBlock(string inFile, string rendered) =>
        string.Equals(inFile.Replace("\r\n", "\n", StringComparison.Ordinal), rendered.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

    /// <summary>The applied-ledger fingerprint of a creation: kind, evidence and normalized title (AT-12).</summary>
    public static string Fingerprint(CandidateKind kind, string evidenceHash, string title) =>
        $"{kind}|{evidenceHash}|{TitleSimilarity.Normalize(title)}";

    /// <summary>A stable UUID for a candidate key (the plan's <c>candidate_id</c> must be a UUID).</summary>
    public static Guid CandidateUuid(CandidateKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return StableId("candidate/" + key);
    }

    public static Guid StableId(string name)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        h[6] = (byte)((h[6] & 0x0F) | 0x80); // version 8 (name-based, SHA-256)
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ManagementItem Apply(ManagementItem item, CandidateReview review)
    {
        var f = review.Fields;
        var changed = review.ChangedFields;
        return item with
        {
            Title = changed.Contains(ItemField.Title) ? ExtractionConverter.Clean(f.Title) : item.Title,
            Description = changed.Contains(ItemField.Description) ? f.Description.Trim() : item.Description,
            Status = changed.Contains(ItemField.Status) && f.Status is { } s ? s : item.Status,
            Priority = changed.Contains(ItemField.Priority) ? f.Priority : item.Priority,
            Assignee = changed.Contains(ItemField.Assignee) ? f.Assignee : item.Assignee,
            DueDate = changed.Contains(ItemField.DueDate) ? f.DueDate : item.DueDate,
            ParentIssueId = review.ParentIssueId ?? item.ParentIssueId,
        };
    }

    // What was approved, not when: the same approval sent twice gives the same key, so the batch is applied once.
    private static Guid IdempotencyKey(PlanningInput input, IReadOnlyList<PlannedChange> changes, IReadOnlyList<PlannedFile> files)
    {
        var sb = new StringBuilder().Append(input.WorkspaceId.ToString("D"));
        foreach (var c in changes)
        {
            sb.Append('|').Append(c.Key).Append('|').Append(c.Action).Append('|').Append(c.ExpectedRevision.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(c.EvidenceHash).Append('|').Append(CanonicalJson.Serialize(ItemJson.ToNode(c.After with { Id = Guid.Empty, CreatedAtUtc = default, UpdatedAtUtc = default })));
        }
        foreach (var f in files.OrderBy(f => f.FileId)) sb.Append('|').Append(f.FileId.ToString("D")).Append('=').Append(f.BeforeHash ?? "new");
        return StableId("idempotency/" + sb);
    }
}

/// <summary>One change of an applied batch, as needed to undo it.</summary>
public sealed record AppliedChange(ManagementItem? Before, ManagementItem After);

/// <summary>
/// 「反映を取り消す」 (detail spec 14.4): not a deletion of history but a new batch that sets the items back. It is made of
/// ordinary reviews, so it goes through the same plan, diff and apply as any other change. An item changed again after
/// the batch is not set back (the later change is kept; the user resolves it). v1 deletes nothing: an item the batch
/// created is cancelled (task) or closed (issue).
/// </summary>
public static class RevertBuilder
{
    public static IReadOnlyList<CandidateReview> Reviews(Guid batchId, IReadOnlyList<AppliedChange> changes, IReadOnlyDictionary<Guid, ManagementItem> current,
        List<string> conflicts)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(conflicts);
        var reviews = new List<CandidateReview>();
        var n = 0;
        // The last change of each item is the state the batch left it in.
        foreach (var group in changes.GroupBy(c => c.After.Id))
        {
            var first = group.First();
            var last = group.Last();
            if (!current.TryGetValue(last.After.Id, out var now))
            {
                conflicts.Add($"{last.After.DisplayId} が見つかりません。");
                continue;
            }
            if (now.Revision != last.After.Revision)
            {
                conflicts.Add($"{now.DisplayId} は反映の後に変更されています（Revision {last.After.Revision} → {now.Revision}）。後の変更を残すため、戻しません。");
                continue;
            }
            var target = first.Before;
            var fields = target is null
                ? new CandidateFields
                {
                    Title = now.Title, Description = now.Description,
                    Status = now.Kind == CandidateKind.Issue ? ItemStatus.Closed : ItemStatus.Cancelled,
                    Priority = now.Priority, Assignee = now.Assignee, DueDate = now.DueDate,
                }
                : new CandidateFields
                {
                    Title = target.Title, Description = target.Description, Status = target.Status,
                    Priority = target.Priority, Assignee = target.Assignee, DueDate = target.DueDate,
                };
            IReadOnlyList<ItemField> changed = target is null ? [ItemField.Status]
                : [.. new[] { ItemField.Title, ItemField.Description, ItemField.Status, ItemField.Priority, ItemField.Assignee, ItemField.DueDate }
                    .Where(f => Differs(f, target, now))];
            if (changed.Count == 0 && target is not null) continue; // a mention only added evidence: the evidence stays
            var candidate = new Candidate
            {
                CandidateId = $"U{++n:0000}",
                Kind = now.Kind,
                Action = CandidateAction.Update,
                SuggestedTargetId = now.Id,
                ParentIssueCandidateId = null,
                ParentIssueItemId = null,
                Fields = fields,
                ChangedFields = changed,
                Certainty = Certainty.Stated,
                DueExpression = null,
                Evidence = [.. now.Evidence.Select(e => new Evidence { SegmentId = e.SegmentId, Quote = e.Quote })],
                Rationale = "反映の取消",
            };
            reviews.Add(new CandidateReview
            {
                Key = new CandidateKey(batchId, 0, candidate.CandidateId),
                Candidate = candidate,
                TranscriptRevision = (now.Evidence.Count > 0 ? now.Evidence[0].TranscriptRevision : 1),
                Project = now.Project,
                Choice = ReviewChoice.Update,
                TargetItemId = now.Id,
                TargetRevision = now.Revision,
                ConfirmedIndividually = true, // the user asked for this undo item by item in the final check
                ItemEvidence = now.Evidence, // the item's own evidence, never the batch id as a session
                UsesExistingItemProvenance = true,
            });
        }
        return reviews;
    }

    private static bool Differs(ItemField field, ManagementItem a, ManagementItem b) => field switch
    {
        ItemField.Title => a.Title != b.Title,
        ItemField.Description => a.Description != b.Description,
        ItemField.Status => a.Status != b.Status,
        ItemField.Priority => a.Priority != b.Priority,
        ItemField.Assignee => a.Assignee != b.Assignee,
        _ => a.DueDate != b.DueDate,
    };
}

/// <summary>
/// An edit made by hand inside a block (11.2, 11.5) becomes a review: 「取り込む」 updates the item with the fields read
/// from the block, 「アプリの内容に戻す」 rewrites the block from the item (a mention: no field changes). Either way the
/// block is rewritten in the app's form with the next revision, through the usual plan, diff and apply.
/// </summary>
public static class ExternalEditBuilder
{
    public static CandidateReview? Review(Guid operationId, int sequence, ManagementItem item, string block, bool takeIn, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(block);
        problem = null;
        var fields = new CandidateFields
        {
            Title = item.Title, Description = item.Description, Status = item.Status, Priority = item.Priority, Assignee = item.Assignee, DueDate = item.DueDate,
        };
        var changed = new List<ItemField>();
        if (takeIn)
        {
            if (ManagementMarkdown.ReadFields(block, item.Kind, out _, out problem) is not { } read) return null;
            fields = read;
            if (read.Title != item.Title) changed.Add(ItemField.Title);
            if (read.Description != item.Description) changed.Add(ItemField.Description);
            if (read.Status != item.Status) changed.Add(ItemField.Status);
            if (read.Priority != item.Priority) changed.Add(ItemField.Priority);
            if (read.Assignee != item.Assignee) changed.Add(ItemField.Assignee);
            if (read.DueDate != item.DueDate) changed.Add(ItemField.DueDate);
        }
        var candidate = new Candidate
        {
            CandidateId = $"E{sequence:0000}",
            Kind = item.Kind,
            Action = changed.Count > 0 ? CandidateAction.Update : CandidateAction.Mention,
            SuggestedTargetId = item.Id,
            ParentIssueCandidateId = null,
            ParentIssueItemId = null,
            Fields = fields,
            ChangedFields = changed,
            Certainty = Certainty.Stated,
            DueExpression = null,
            Evidence = [.. item.Evidence.Select(e => new Evidence { SegmentId = e.SegmentId, Quote = e.Quote })],
            Rationale = takeIn ? "手編集の取込み" : "手編集を戻す",
        };
        return new CandidateReview
        {
            Key = new CandidateKey(operationId, 0, candidate.CandidateId),
            Candidate = candidate,
            TranscriptRevision = item.Evidence.Count > 0 ? item.Evidence[0].TranscriptRevision : 1,
            Project = item.Project,
            Choice = changed.Count > 0 ? ReviewChoice.Update : ReviewChoice.Mention,
            TargetItemId = item.Id,
            TargetRevision = item.Revision,
            ConfirmedIndividually = true, // the user picked this block and saw its diff
            ItemEvidence = item.Evidence, // the item's own evidence, never the batch id as a session
            UsesExistingItemProvenance = true,
        };
    }
}

/// <summary>The canonical JSON of an item snapshot (fields and evidence).</summary>
public static class ItemJson
{
    /// <summary>Reads a snapshot written by <see cref="ToNode"/> back (Phase F applies the after snapshots).</summary>
    public static ManagementItem FromNode(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string S(string name) => (string?)node[name] ?? throw new FormatException($"The snapshot has no {name}.");
        string? N(string name) => (string?)node[name];
        var kind = S("kind") == "issue" ? CandidateKind.Issue : CandidateKind.Task;
        return new ManagementItem
        {
            Id = Guid.Parse(S("id")),
            DisplayId = S("display_id"),
            Kind = kind,
            Project = S("project"),
            Title = S("title"),
            Description = S("description"),
            Status = ParseStatus(S("status")),
            Priority = N("priority") is { } p ? Enum.Parse<ItemPriority>(p, ignoreCase: true) : null,
            Assignee = N("assignee"),
            DueDate = N("due_date") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            ParentIssueId = N("parent_issue_id") is { } parent ? Guid.Parse(parent) : null,
            Revision = (int?)node["revision"] ?? throw new FormatException("The snapshot has no revision."),
            CreatedAtUtc = DateTimeOffset.Parse(S("created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            UpdatedAtUtc = DateTimeOffset.Parse(S("updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            Evidence = [.. (node["evidence"] as JsonArray ?? []).Select(e => new ItemEvidence
            {
                SessionId = Guid.Parse((string)e!["session_id"]!),
                TranscriptRevision = (int)e["transcript_revision"]!,
                SegmentId = Guid.Parse((string)e["segment_id"]!),
                Quote = (string)e["quote"]!,
            })],
        };
    }

    private static ItemStatus ParseStatus(string label) =>
        Enum.GetValues<ItemStatus>().FirstOrDefault(s => ItemStatuses.Label(s) == label) is var s && ItemStatuses.Label(s) == label
            ? s : throw new FormatException($"Unknown status {label}.");

    public static JsonObject ToNode(ManagementItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new JsonObject
        {
            ["schema_version"] = "1.0",
            ["id"] = item.Id.ToString("D"),
            ["display_id"] = item.DisplayId,
            ["kind"] = item.Kind == CandidateKind.Issue ? "issue" : "task",
            ["project"] = item.Project,
            ["title"] = item.Title,
            ["description"] = item.Description,
            ["status"] = ItemStatuses.Label(item.Status),
            ["priority"] = item.Priority?.ToString().ToLowerInvariant(),
            ["assignee"] = item.Assignee,
            ["due_date"] = item.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["parent_issue_id"] = item.ParentIssueId?.ToString("D"),
            ["revision"] = item.Revision,
            ["created_at"] = item.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["updated_at"] = item.UpdatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["evidence"] = new JsonArray([.. item.Evidence.Select(e => (JsonNode)new JsonObject
            {
                ["session_id"] = e.SessionId.ToString("D"),
                ["transcript_revision"] = e.TranscriptRevision,
                ["segment_id"] = e.SegmentId.ToString("D"),
                ["quote"] = e.Quote,
            })]),
        };
    }
}
