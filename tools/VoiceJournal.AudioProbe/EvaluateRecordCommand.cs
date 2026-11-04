using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Llm;

namespace VoiceJournal.AudioProbe;

// Phase D5: build records from the evaluation set (tests/eval/phase-d/*.json) and score them against the expected answers.
//   AudioProbe eval-record <fixture.json>... --workspace <dir> [organize options] [--out <json>]
//   AudioProbe eval-record <fixture.json> --score <sessionDir> [--out <json>]     (score an existing session again)
// Each fixture becomes a session folder (manifest + one raw transcript revision) under <workspace>; the generated
// record stays there for review. Only counts, ids and short candidate titles are printed (no transcript text).
// The format of the expected answers is described in tests/eval/phase-d/README.md.
internal static class EvaluateRecordCommand
{
    private static readonly string[] OpenStatuses = ["open", "todo", "inprogress", "blocked"];

    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions json)
    {
        string Option(string name, string fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        var fixtures = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Path.GetFullPath).ToList();
        if (fixtures.Count == 0) throw new ArgumentException("評価素材の JSON を指定してください。");
        var reports = new List<JsonObject>();

        if (Option("--score", "") is { Length: > 0 } scoreDir)
        {
            var fixture = Fixture.Load(fixtures[0]);
            reports.Add(Score(fixture, Path.GetFullPath(scoreDir), null));
        }
        else
        {
            var workspace = Path.GetFullPath(Option("--workspace", "") is { Length: > 0 } w ? w : throw new ArgumentException("--workspace を指定してください。"));
            var settings = OrganizeCommand.Settings(args);
            using var client = new LmStudioClient(LlmEndpoint.Parse(Option("--url", LlmEndpoint.Default)), TimeSpan.FromMinutes(10));
            var model = await client.GetModelAsync(settings.ModelId, CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine($"model {model.Id}: loaded context {model.LoadedContextLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            var generator = new RecordGenerator(client, new AnalysisSchemaValidator(), TimeProvider.System);
            foreach (var path in fixtures)
            {
                var fixture = Fixture.Load(path);
                var sessionDir = CreateSession(fixture, workspace);
                Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {fixture.Id}: {sessionDir}");
                var manifest = SessionManifest.Load(sessionDir);
                var transcript = new TranscriptStore(sessionDir).LoadCurrent()!;
                var result = await generator.GenerateAsync(new GenerationRequest
                {
                    SessionDirectory = sessionDir,
                    Transcript = transcript,
                    StartedAtUtc = manifest.StartedAtUtc,
                    TimeZoneId = manifest.TimeZoneId,
                    Title = manifest.Title,
                    JobId = Guid.CreateVersion7(),
                    Settings = settings,
                }, null, CancellationToken.None).ConfigureAwait(false);
                reports.Add(Score(fixture, sessionDir, result));
            }
        }

        var all = reports.Select(r => r["candidates"]!.AsObject()).ToList();
        int Sum(string key) => all.Sum(c => c[key]!.GetValue<int>());
        var produced = Sum("produced");
        var expected = Sum("expected");
        var summary = new JsonObject
        {
            ["fixtures"] = new JsonArray([.. reports]),
            ["total"] = new JsonObject
            {
                ["produced"] = produced,
                ["matched_produced"] = Sum("matched_produced"),
                ["expected"] = expected,
                ["recalled"] = Sum("recalled"),
                ["precision"] = produced == 0 ? null : Math.Round((double)Sum("matched_produced") / produced, 3),
                ["recall"] = expected == 0 ? null : Math.Round((double)Sum("recalled") / expected, 3),
                ["violations"] = reports.Sum(r => r["violations"]!.AsArray().Count),
                ["elapsed_seconds"] = Math.Round(reports.Sum(r => r["generation"]?["elapsed_seconds"]?.GetValue<double>() ?? 0), 1),
            },
        };
        var text = summary.ToJsonString(json);
        Console.WriteLine(text);
        if (Option("--out", "") is { Length: > 0 } outPath) await File.WriteAllTextAsync(outPath, text).ConfigureAwait(false);
        return reports.All(r => r["violations"]!.AsArray().Count == 0) ? 0 : 3;
    }

    /// <summary>Makes a session folder with the fixture's segments as transcript revision 1.</summary>
    private static string CreateSession(Fixture fixture, string workspace)
    {
        var dir = Path.Combine(workspace, fixture.Id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var sources = fixture.Segments.Select(s => s.Source).Distinct().ToList();
        var manifest = new SessionManifest
        {
            SessionId = Guid.CreateVersion7(),
            CaptureMode = sources.Count > 1 ? CaptureMode.Both : sources[0] == AudioSource.Mic ? CaptureMode.Mic : CaptureMode.Loopback,
            StartedAtUtc = fixture.StartedAt.ToUniversalTime(),
            TimeZoneId = fixture.TimeZoneId,
        };
        manifest.Save(dir);
        var segments = fixture.Segments.Select((s, i) =>
        {
            var next = i + 1 < fixture.Segments.Count ? fixture.Segments[i + 1].StartMs : long.MaxValue;
            var end = Math.Min(next - 100, s.StartMs + 500 + (s.Text.Length * 170L));
            return new TranscriptSegment { SegmentId = s.SegmentId, Source = s.Source, StartMs = s.StartMs, EndMs = end, Text = s.Text };
        }).ToList();
        var model = "eval-fixture";
        var document = new TranscriptDocument
        {
            SessionId = manifest.SessionId,
            Revision = 1,
            Kind = TranscriptRevisionKind.Raw,
            RawRevision = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Provenance = new SttProvenance
            {
                InputHash = new string('0', 64),
                EngineName = "eval-fixture",
                EngineVersion = "0",
                EngineSha256 = new string('0', 64),
                ModelName = model,
                ModelSha256 = new string('0', 64),
                ConfigHash = new SttConfiguration { ModelName = model }.ComputeHash(),
            },
            Segments = TranscriptDocument.Order(segments),
        };
        new TranscriptStore(dir).Save(document, segments[^1].EndMs + 1000);
        return dir;
    }

    private static JsonObject Score(Fixture fixture, string sessionDir, GenerationResult? result)
    {
        var violations = new List<string>();
        var record = new RecordStore(sessionDir).LoadLatest();
        var transcript = record is null ? null : new TranscriptStore(sessionDir).Load(record.TranscriptRevision);
        var report = new JsonObject { ["id"] = fixture.Id, ["session"] = sessionDir };
        if (result is not null)
        {
            report["generation"] = new JsonObject
            {
                ["outcome"] = result.Outcome.ToString(),
                ["pieces"] = result.PieceCount,
                ["failed_pieces"] = result.FailedPieces.Count,
                ["dropped_evidence"] = result.DroppedEvidence,
                ["dropped_items"] = result.DroppedItems,
                ["elapsed_seconds"] = Math.Round(result.Elapsed.TotalSeconds, 1),
                ["prompt_tokens"] = result.Requests.Sum(r => r.PromptTokens),
                ["completion_tokens"] = result.Requests.Sum(r => r.CompletionTokens),
                ["error"] = result.Error,
            };
        }
        if (record is null || transcript is null)
        {
            violations.Add("記録がありません");
            report["candidates"] = new JsonObject { ["produced"] = 0, ["matched_produced"] = 0, ["expected"] = fixture.Expected.Candidates.Count, ["recalled"] = 0 };
            report["violations"] = new JsonArray([.. violations.Select(v => (JsonNode)v)]);
            return report;
        }
        // The fixture's segment ids are new per session; map evidence back through the time of each segment.
        var startOf = transcript.Segments.ToDictionary(s => s.SegmentId, s => s.StartMs);
        var e = fixture.Expected;

        // AT-08: record types, summary, sections.
        var types = record.RecordTypes.Select(t => t.ToString().ToLowerInvariant()).ToList();
        if (e.RecordTypes.Count > 0 && !types.Intersect(e.RecordTypes).Any()) violations.Add($"記録の種別 {string.Join(",", types)} が {string.Join(",", e.RecordTypes)} のどれも含まない");
        var summaryChars = record.Summary.Sum(s => s.Text.Length);
        if (record.Summary.Count is < 3 or > 7) violations.Add($"要約の項目数 {record.Summary.Count}（3〜7）");
        if (summaryChars > 600) violations.Add($"要約の字数 {summaryChars}（600 以内）");
        if (record.Sections.Any(s => s.Statements.Count == 0)) violations.Add("空の章がある");
        var allText = Norm(string.Join("\n", record.AllStatements.Select(s => s.Text)));
        foreach (var k in e.KeyPoints)
            if (!k.Any(m => allText.Contains(Norm(m), StringComparison.Ordinal))) violations.Add($"要点「{string.Join("/", k)}」が記録にない");
        foreach (var d in e.Decisions)
        {
            var at = Fixture.Ms(d.EvidenceT);
            var found = record.AllStatements.Any(s => d.MatchAny.Any(m => Norm(s.Text).Contains(Norm(m), StringComparison.Ordinal))
                && s.Evidence.Any(ev => startOf.TryGetValue(ev.SegmentId, out var t) && t == at));
            if (!found) violations.Add($"決定「{string.Join("/", d.MatchAny)}」に {d.EvidenceT} の根拠がない");
        }
        var markdown = File.Exists(new RecordStore(sessionDir).DraftMarkdownPath) ? File.ReadAllText(new RecordStore(sessionDir).DraftMarkdownPath) : "";
        foreach (var bad in e.MarkdownMustNotContain)
            if (markdown.Contains(bad, StringComparison.OrdinalIgnoreCase)) violations.Add($"Markdown に「{bad}」が含まれる");

        // Candidates: forbidden first (title only), then expected and optional (title + description), one to one.
        var produced = record.Candidates.Select(rc => rc.Candidate).ToList();
        string Title(Candidate c) => Norm(c.Fields.Title);
        string Body(Candidate c) => Norm(c.Fields.Title + "\n" + c.Fields.Description);
        var assigned = new Dictionary<Candidate, string>();
        foreach (var c in produced)
            foreach (var f in e.Forbidden.Where(f => f.MatchAny.Any(m => Title(c).Contains(Norm(m), StringComparison.Ordinal))))
            {
                // AT-09: no wrong *confirmed* candidate. Shown as unconfirmed it is no violation, but still a false positive.
                if (c.Certainty == Certainty.Stated)
                {
                    violations.Add($"出してはいけない候補「{c.Fields.Title}」を確定で出した（{f.Reason}）");
                    assigned[c] = "forbidden";
                }
                else
                {
                    assigned[c] = "forbidden-unconfirmed";
                }
            }
        var recalled = 0;
        var kindMismatches = new List<string>();
        // The kind (issue / task) is not a matching condition: the line between them is fuzzy, and a wrong kind is
        // reported apart (kind_mismatch) rather than turning a found candidate into a miss and a false positive.
        // A title match wins over a match in the description (「家計管理の課題」 whose description mentions 先取り貯蓄
        // must not take the place of 「先取り貯蓄の開始」).
        bool In(Func<Candidate, string> text, Candidate c, ExpectedCandidate exp) => exp.MatchAny.Any(m => text(c).Contains(Norm(m), StringComparison.Ordinal));
        foreach (var (exp, optional) in e.Candidates.Select(x => (x, false)).Concat(e.Optional.Select(x => (x, true))))
        {
            var hit = produced.FirstOrDefault(c => !assigned.ContainsKey(c) && In(Title, c, exp))
                ?? produced.FirstOrDefault(c => !assigned.ContainsKey(c) && In(Body, c, exp));
            if (hit is null)
            {
                if (!optional) violations.Add($"候補「{string.Join("/", exp.MatchAny)}」が出ていない（recall）");
                continue;
            }
            assigned[hit] = optional ? "optional" : "expected";
            if (!optional) recalled++;
            var label = $"候補「{hit.Fields.Title}」";
            if (!exp.Kinds.Contains(Kind(hit))) kindMismatches.Add($"{label}は {Kind(hit)}（正解 {string.Join(" か ", exp.Kinds)}）");
            if (exp.HasAssignee && !(exp.Assignee is null ? hit.Fields.Assignee is null : hit.Fields.Assignee?.Contains(exp.Assignee, StringComparison.Ordinal) == true))
                violations.Add($"{label}の担当 {hit.Fields.Assignee ?? "null"}（正解 {exp.Assignee ?? "null"}）");
            if (exp.HasDueDate && !exp.DueDates.Contains(Due(hit)))
                violations.Add($"{label}の期限 {Due(hit) ?? "null"}（正解 {string.Join(" か ", exp.DueDates.Select(d => d ?? "null"))}）");
            if (exp.HasStatus && hit.Fields.Status is { } st && !OpenStatuses.Contains(Status(st)))
                violations.Add($"{label}の状態 {Status(st)}（未完了のはず）");
        }
        foreach (var c in produced)
        {
            var label = $"候補「{c.Fields.Title}」";
            bool Hits(IReadOnlyList<string> any) => any.Any(m => Body(c).Contains(Norm(m), StringComparison.Ordinal));
            foreach (var r in e.ForbiddenStatus.Where(r => Hits(r.MatchAny)))
                if (c.Fields.Status is { } st && r.Statuses.Contains(Status(st))) violations.Add($"{label}の状態 {Status(st)}（{r.Reason}）");
            foreach (var r in e.NoAssignee.Where(r => Hits(r.MatchAny)))
                if (c.Fields.Assignee is { } who && (r.Names.Count == 0 || r.Names.Any(n => who.Contains(n, StringComparison.Ordinal))))
                    violations.Add($"{label}の担当 {who}（{r.Reason}）");
            foreach (var r in e.NoDueDate.Where(r => Hits(r.MatchAny)))
                if (c.Fields.DueDate is not null) violations.Add($"{label}の期限 {Due(c)}（{r.Reason}）");
            // Plan 1: no due date or assignee without grounds.
            if (c.Fields.DueDate is not null && string.IsNullOrWhiteSpace(c.DueExpression)) violations.Add($"{label}の期限に元の表現がない");
            if (c.Fields.Assignee is { } a && !Grounded(a, c, transcript)) violations.Add($"{label}の担当 {a} が根拠の区間にない");
        }
        var matched = assigned.Values.Count(v => v is "expected" or "optional");
        report["record"] = new JsonObject
        {
            ["revision"] = record.Revision,
            ["record_types"] = new JsonArray([.. types.Select(t => (JsonNode)t)]),
            ["summary_items"] = record.Summary.Count,
            ["summary_chars"] = summaryChars,
            ["sections"] = new JsonArray([.. record.Sections.Select(s => (JsonNode)$"{s.Kind}: {s.Heading} ({s.Statements.Count})")]),
            ["unsupported"] = record.Unsupported.Count,
            ["missing_pieces"] = record.MissingPieces.Count,
        };
        report["candidates"] = new JsonObject
        {
            ["produced"] = produced.Count,
            ["matched_produced"] = matched,
            ["expected"] = e.Candidates.Count,
            ["recalled"] = recalled,
            ["kind_mismatch"] = new JsonArray([.. kindMismatches.Select(k => (JsonNode)k)]),
            ["list"] = new JsonArray([.. record.Candidates.Select(rc => (JsonNode)new JsonObject
            {
                ["kind"] = Kind(rc.Candidate),
                ["title"] = rc.Candidate.Fields.Title,
                ["assignee"] = rc.Candidate.Fields.Assignee,
                ["due_date"] = Due(rc.Candidate),
                ["due_expression"] = rc.Candidate.DueExpression,
                ["status"] = rc.Candidate.Fields.Status is { } s ? Status(s) : null,
                ["certainty"] = rc.Candidate.Certainty.ToString().ToLowerInvariant(),
                ["flags"] = new JsonArray([.. rc.Flags.Select(f => (JsonNode)f.ToString())]),
                ["match"] = assigned.GetValueOrDefault(rc.Candidate, "unmatched"),
            })]),
        };
        report["violations"] = new JsonArray([.. violations.Select(v => (JsonNode)v)]);
        return report;
    }

    private static bool Grounded(string assignee, Candidate c, TranscriptDocument transcript)
    {
        var name = Norm(assignee).Replace("さん", "", StringComparison.Ordinal).Trim();
        if (name.Length == 0) return false;
        var texts = c.Evidence.Select(ev => ev.Quote)
            .Concat(transcript.Segments.Where(s => c.Evidence.Any(ev => ev.SegmentId == s.SegmentId)).Select(s => s.Text));
        if (texts.Any(t => Norm(t).Contains(name, StringComparison.Ordinal))) return true;
        // The app's rule: the speaker named themself (「伊藤です。」) in one of the two segments before, same source.
        var segments = transcript.Segments;
        for (var i = 0; i < segments.Count; i++)
        {
            if (c.Evidence.All(ev => ev.SegmentId != segments[i].SegmentId)) continue;
            for (var j = i - 1; j >= 0 && j >= i - 2 && segments[j].Source == segments[i].Source; j--)
                if (Norm(segments[j].Text).StartsWith(name + "です", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string Kind(Candidate c) => c.Kind.ToString().ToLowerInvariant();

    private static string Status(ItemStatus s) => s.ToString().ToLowerInvariant();

    private static string? Due(Candidate c) => c.Fields.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Norm(string s) => s.Normalize(NormalizationForm.FormKC);

    private sealed record Seg(Guid SegmentId, long StartMs, AudioSource Source, string Text);

    private sealed record ExpectedCandidate(IReadOnlyList<string> Kinds, IReadOnlyList<string> MatchAny, bool HasAssignee, string? Assignee, bool HasDueDate, IReadOnlyList<string?> DueDates, bool HasStatus);

    private sealed record Rule(IReadOnlyList<string> MatchAny, string Reason, IReadOnlyList<string> Statuses, IReadOnlyList<string> Names);

    private sealed record Decision(IReadOnlyList<string> MatchAny, string EvidenceT);

    private sealed record Expected(
        IReadOnlyList<string> RecordTypes,
        IReadOnlyList<ExpectedCandidate> Candidates,
        IReadOnlyList<ExpectedCandidate> Optional,
        IReadOnlyList<Rule> Forbidden,
        IReadOnlyList<Rule> ForbiddenStatus,
        IReadOnlyList<Rule> NoAssignee,
        IReadOnlyList<Rule> NoDueDate,
        IReadOnlyList<string> MarkdownMustNotContain,
        IReadOnlyList<IReadOnlyList<string>> KeyPoints,
        IReadOnlyList<Decision> Decisions);

    private sealed record Fixture(string Id, DateTimeOffset StartedAt, string TimeZoneId, IReadOnlyList<Seg> Segments, Expected Expected)
    {
        public static long Ms(string t)
        {
            var p = t.Split(':');
            return ((long.Parse(p[0], CultureInfo.InvariantCulture) * 3600) + (long.Parse(p[1], CultureInfo.InvariantCulture) * 60) + long.Parse(p[2], CultureInfo.InvariantCulture)) * 1000;
        }

        public static Fixture Load(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var segments = root["segments"]!.AsArray().Select(s => new Seg(
                Guid.CreateVersion7(),
                Ms(s!["t"]!.GetValue<string>()),
                s["source"]!.GetValue<string>() == "loopback" ? AudioSource.Loopback : AudioSource.Mic,
                s["text"]!.GetValue<string>())).ToList();
            var e = root["expected"]!.AsObject();
            return new Fixture(
                root["id"]!.GetValue<string>(),
                DateTimeOffset.Parse(root["started_at"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                root["time_zone"]!.GetValue<string>(),
                segments,
                new Expected(
                    Strings(e["record_types"]),
                    [.. Array(e, "candidates").Select(Candidate)],
                    [.. Array(e, "optional_candidates").Select(Candidate)],
                    [.. Array(e, "forbidden_candidates").Select(RuleOf)],
                    [.. Array(e, "forbidden_status").Select(RuleOf)],
                    [.. Array(e, "no_assignee").Select(RuleOf)],
                    [.. Array(e, "no_due_date").Select(RuleOf)],
                    Strings(e["markdown_must_not_contain"]),
                    [.. Array(e, "key_points").Select(k => Strings(k["match_any"]))],
                    [.. Array(e, "decisions").Select(d => new Decision(Strings(d["match_any"]), d["evidence_t"]!.GetValue<string>()))]));
        }

        private static IEnumerable<JsonObject> Array(JsonObject o, string key) => o[key]?.AsArray().Select(n => n!.AsObject()) ?? [];

        private static List<string> Strings(JsonNode? node) => node switch
        {
            null => [],
            JsonArray a => [.. a.Select(n => n!.GetValue<string>())],
            _ => [node.GetValue<string>()],
        };

        private static ExpectedCandidate Candidate(JsonObject c)
        {
            var kinds = c["kind"] is { } k ? Strings(k) : ["task", "issue"];
            List<string?> due = c["due_date"] switch
            {
                JsonArray a => [.. a.Select(n => n?.GetValue<string>())],
                null => [null],
                var n => [n.GetValue<string>()],
            };
            return new ExpectedCandidate(kinds, Strings(c["match_any"]), c.ContainsKey("assignee"), c["assignee"]?.GetValue<string>(), c.ContainsKey("due_date"), due, c.ContainsKey("status"));
        }

        private static Rule RuleOf(JsonObject r) => new(Strings(r["match_any"]), r["reason"]?.GetValue<string>() ?? "", Strings(r["status"]), Strings(r["assignee"]));
    }
}
