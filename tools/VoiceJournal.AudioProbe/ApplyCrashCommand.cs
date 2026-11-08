using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Management;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.AudioProbe;

/// <summary>
/// AT-17 with real process ends: for each step of detail spec 14.2, a child process applies a batch and ends itself
/// there: it tells this process and waits, and this process kills it (TerminateProcess: no finally, no disposal, no
/// error report window), then this process recovers as the
/// app does at start-up and checks the result: committed once, files and items at the approved state.
/// </summary>
internal static class ApplyCrashCommand
{
    private static readonly (ApplyStep Step, int Sequence)[] Points =
    [
        (ApplyStep.Verified, 0), (ApplyStep.Locked, 0), (ApplyStep.BackupWritten, 1), (ApplyStep.TempWritten, 1), (ApplyStep.TempWritten, 2),
        (ApplyStep.Prepared, 0), (ApplyStep.BeforeReplace, 1), (ApplyStep.AfterReplace, 1), (ApplyStep.BeforeReplace, 2), (ApplyStep.AfterReplace, 2),
        (ApplyStep.BeforeDbCommit, 0), (ApplyStep.Committed, 0),
    ];

    /// <summary><c>apply-crash &lt;root&gt;</c>: runs every point in a fresh workspace under the root.</summary>
    public static int Run(string[] args, JsonSerializerOptions json)
    {
        var root = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var results = new List<object>();
        var failed = 0;
        foreach (var (step, sequence) in Points)
        {
            var dir = Path.Combine(root, $"{step}-{sequence}");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            var batch = Prepare(dir);
            SqliteConnection.ClearAllPools();

            using var child = Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { "apply-batch", dir, batch.ToString("D"), step.ToString(), sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!;
            // The child prints STOP when it reaches the point and waits; it is then killed like a power cut of the app.
            var line = child.StandardOutput.ReadLine();
            if (line?.StartsWith("STOP", StringComparison.Ordinal) == true) child.Kill(entireProcessTree: true);
            child.WaitForExit();
            var exitCode = child.ExitCode;

            var (ok, detail) = RecoverAndCheck(dir, batch);
            if (!ok) failed++;
            results.Add(new { step = step.ToString(), sequence, child_exit = exitCode, crashed = exitCode != 0, ok, detail });
            Console.Error.WriteLine($"{(ok ? "ok  " : "FAIL")} {step}/{sequence} exit={exitCode} {detail}");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { points = results.Count, failed, results }, json));
        return failed == 0 ? 0 : 3;
    }

    /// <summary><c>apply-batch &lt;workspace&gt; &lt;batch&gt; &lt;step&gt; &lt;sequence&gt;</c>: the child; stops at the step to be killed.</summary>
    public static int ApplyBatch(string[] args)
    {
        var workspace = Workspace.OpenOrCreate(args[1], syncRoots: []);
        var db = VoiceJournalDatabase.Open(workspace.DatabasePath);
        var store = new ManagementStore(workspace, db);
        var faults = new StopAt(Enum.Parse<ApplyStep>(args[3]), int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
        var result = new ApplyEngine(workspace, db, store, TimeProvider.System, faults).Apply(Guid.Parse(args[2]));
        Console.WriteLine(result.Outcome);
        return 0;
    }

    private sealed class StopAt(ApplyStep point, int sequence) : IApplyFaults
    {
        public void At(ApplyStep step, int seq)
        {
            if (step != point || seq != sequence) return;
            Console.Out.WriteLine($"STOP {step}/{seq}");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite); // killed by the parent here
        }
    }

    // A workspace with an issue and a task applied, and a planned update of both (two files).
    private static Guid Prepare(string dir)
    {
        var workspace = Workspace.OpenOrCreate(dir, syncRoots: []);
        var db = VoiceJournalDatabase.Open(workspace.DatabasePath);
        var store = new ManagementStore(workspace, db);
        var engine = new ApplyEngine(workspace, db, store);
        var session = Guid.CreateVersion7();
        var first = Save(store, workspace, session,
            Review(session, "C0001", "見積書の作成", CandidateKind.Task, ReviewChoice.Create, null),
            Review(session, "C0002", "ログイン不具合", CandidateKind.Issue, ReviewChoice.Create, null));
        if (engine.Apply(first).Outcome != ApplyOutcome.Committed) throw new InvalidOperationException("setup apply failed");
        var items = store.Items();
        var task = items.Single(i => i.Kind == CandidateKind.Task);
        var issue = items.Single(i => i.Kind == CandidateKind.Issue);
        return Save(store, workspace, session,
            Review(session, "C0003", "x", CandidateKind.Issue, ReviewChoice.Update, issue, "原因はキャッシュ"),
            Review(session, "C0004", "x", CandidateKind.Task, ReviewChoice.Update, task, "金額を直す"));
    }

    private static Guid Save(ManagementStore store, Workspace workspace, Guid session, params CandidateReview[] reviews)
    {
        var (issue, task) = store.NextNumbers();
        var (keys, prints) = store.Applied();
        var plan = ChangePlanner.Plan(new PlanningInput
        {
            WorkspaceId = workspace.Info.WorkspaceId,
            Items = store.Items(),
            Reviews = reviews,
            Applied = keys,
            AppliedFingerprints = prints,
            Files = store.Files(),
            NextIssueNumber = issue,
            NextTaskNumber = task,
            NowUtc = DateTimeOffset.UtcNow,
        }, Guid.CreateVersion7);
        return store.SavePlan(plan).BatchId;
    }

    private static CandidateReview Review(Guid session, string id, string title, CandidateKind kind, ReviewChoice choice, ManagementItem? target, string description = "")
    {
        var candidate = new Candidate
        {
            CandidateId = id,
            Kind = kind,
            Action = choice == ReviewChoice.Create ? CandidateAction.Create : CandidateAction.Update,
            SuggestedTargetId = target?.Id,
            ParentIssueCandidateId = null,
            ParentIssueItemId = null,
            Fields = new CandidateFields { Title = target?.Title ?? title, Description = description, Status = null, Priority = null, Assignee = null, DueDate = null },
            ChangedFields = choice == ReviewChoice.Update ? [ItemField.Description] : [],
            Certainty = Certainty.Stated,
            DueExpression = null,
            Evidence = [new Evidence { SegmentId = Guid.CreateVersion7(), Quote = "根拠の引用です" }],
            Rationale = "試験",
        };
        return new CandidateReview
        {
            Key = new CandidateKey(session, 1, id),
            Candidate = candidate,
            TranscriptRevision = 1,
            Choice = choice,
            TargetItemId = target?.Id,
            TargetRevision = target?.Revision,
        };
    }

    private static (bool Ok, string Detail) RecoverAndCheck(string dir, Guid batch)
    {
        var workspace = Workspace.OpenOrCreate(dir, syncRoots: []);
        var db = VoiceJournalDatabase.Open(workspace.DatabasePath);
        var store = new ManagementStore(workspace, db);
        var engine = new ApplyEngine(workspace, db, store);
        var reports = new ApplyRecovery(db, store, engine).RecoverAll();
        var state = store.Batches().Single(b => b.BatchId == batch).State;
        if (state == "planned") engine.Apply(batch);
        state = store.Batches().Single(b => b.BatchId == batch).State;
        var items = store.Items();
        var tasks = File.ReadAllText(store.FullPath(ManagementStore.TasksPath));
        var issues = File.ReadAllText(store.FullPath(ManagementStore.IssuesPath));
        var leftovers = Directory.GetFiles(Path.GetDirectoryName(store.FullPath(ManagementStore.TasksPath))!, ".vj-*.tmp");
        var problems = new List<string>();
        if (state != "committed") problems.Add($"state={state}");
        if (items.Any(i => i.Revision != 2)) problems.Add("revisions=" + string.Join(",", items.Select(i => i.Revision)));
        if (!tasks.Contains("金額を直す", StringComparison.Ordinal) || !issues.Contains("原因はキャッシュ", StringComparison.Ordinal)) problems.Add("files not at the approved state");
        if (store.Applied().Keys.Count != 4) problems.Add($"applied={store.Applied().Keys.Count}");
        if (leftovers.Length > 0) problems.Add($"temp files left: {leftovers.Length}");
        if (reports.Any(r => r.Action == RecoveryAction.RecoveryRequired)) problems.Add("recovery required");
        SqliteConnection.ClearAllPools();
        var actions = string.Join(",", reports.Select(r => r.Action));
        return (problems.Count == 0, problems.Count == 0 ? $"recovery={(actions.Length == 0 ? "none" : actions)}" : string.Join("; ", problems));
    }
}
