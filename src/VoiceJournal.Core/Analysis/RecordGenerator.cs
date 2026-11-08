using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Core.Analysis;

/// <summary>Settings that can change a generated record; their hash is the analysis config hash (AT-26).</summary>
public sealed record GenerationSettings
{
    public required string ModelId { get; init; }

    /// <summary>The loaded context length C (detail spec 9.2).</summary>
    public required int ContextLength { get; init; }

    /// <summary>The output reserve O, also the <c>max_tokens</c> of every request.</summary>
    public required int OutputReserve { get; init; }

    public double Temperature { get; init; } = 0.1;

    /// <summary>Learned factor for <see cref="TokenEstimator"/> (not part of the hash: it only changes the split).</summary>
    public double TokenFactor { get; init; } = 1.0;

    /// <summary>Retries of a transient failure with the same input (detail spec 19: at most 2).</summary>
    public int TransientRetries { get; init; } = 2;

    /// <summary>
    /// Upper bound of transcript tokens per piece. The answer size is bounded by the schema, so a piece much larger
    /// than this would be summarized too coarsely; much smaller pieces repeat the fixed cost of every request.
    /// </summary>
    public int MaxPieceTokens { get; init; } = 3000;

    /// <summary>Pieces sent at the same time (the bundled server is started with one slot). Not in the hash: it does not change answers.</summary>
    public int Parallelism { get; init; } = 1;

    public string ComputeHash()
    {
        var text = string.Join("\n",
            ModelId, ContextLength.ToString(CultureInfo.InvariantCulture), OutputReserve.ToString(CultureInfo.InvariantCulture),
            MaxPieceTokens.ToString(CultureInfo.InvariantCulture),
            Temperature.ToString("R", CultureInfo.InvariantCulture), AnalysisPrompts.Version,
            Hash(AnalysisPrompts.Extraction + AnalysisPrompts.Integration + AnalysisPrompts.Repair), Hash(LlmSchemas.Extraction + LlmSchemas.Integration));
        return Hash(text);
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Everything one generation needs. <see cref="Transcript"/> is the revision in use (Phase C hand-over).</summary>
public sealed record GenerationRequest
{
    public required string SessionDirectory { get; init; }

    public required TranscriptDocument Transcript { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required string TimeZoneId { get; init; }

    public required string? Title { get; init; }

    public required Guid JobId { get; init; }

    public required GenerationSettings Settings { get; init; }

    public string? ModelSha256 { get; init; }

    /// <summary>
    /// Regenerate only this section of the latest record (plan 3.6) from its evidence segments; then
    /// <see cref="Transcript"/> is the revision the record was built on.
    /// </summary>
    public Guid? SectionId { get; init; }
}

public enum GenerationOutcome
{
    /// <summary>An analysis and a record draft were saved (possibly partial).</summary>
    Generated,

    /// <summary>Nothing to organize (silence, nothing recognized): no analysis is made up (16.1).</summary>
    NotAnalyzable,

    /// <summary>Every piece failed; nothing was saved except the work files.</summary>
    Failed,
}

/// <summary>Timing and size of one request, for the report (NFR-07). No content.</summary>
public sealed record RequestStat(string Step, int PromptTokens, int CompletionTokens, double Seconds, string? FinishReason, int EstimatedPromptTokens);

public sealed record GenerationResult
{
    public required GenerationOutcome Outcome { get; init; }

    public int? AnalysisRevision { get; init; }

    public int? RecordRevision { get; init; }

    public required int PieceCount { get; init; }

    public required IReadOnlyList<FailedPiece> FailedPieces { get; init; }

    public required IReadOnlyList<RequestStat> Requests { get; init; }

    public required int DroppedEvidence { get; init; }

    public required int DroppedItems { get; init; }

    /// <summary>A new <see cref="GenerationSettings.TokenFactor"/> measured from this run, for the next one.</summary>
    public required double MeasuredTokenFactor { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public string? Error { get; init; }
}

public sealed record GenerationProgress(string Step, int Done, int Total);

/// <summary>The work folder's plan: a resumed job reuses finished pieces only if nothing that shapes them changed.</summary>
public sealed record GenerationWorkPlan
{
    public required int TranscriptRevision { get; init; }

    public required string ConfigHash { get; init; }

    public required int InputTokenBudget { get; init; }

    public required AnalysisPlan Plan { get; init; }
}

/// <summary>One finished piece in the work folder (<c>piece-NNNN.json</c>).</summary>
public sealed record GenerationWorkPiece
{
    public required int Piece { get; init; }

    public PieceAnalysis? Result { get; init; }

    public FailedPiece? Failure { get; init; }

    public required IReadOnlyList<RequestStat> Requests { get; init; }
}

/// <summary>
/// Builds a record from the transcript in use (detail spec 9.2): extraction per piece, hierarchical integration,
/// assembly, validation against 16.2 and the full schema, then the analysis revision and a record draft.
/// Work files under <c>analysis/work/&lt;job&gt;/</c> let a cancelled or crashed job resume from finished pieces.
/// The inference server being unavailable aborts the job (it is retried later); a bad answer only fails its piece.
/// </summary>
public sealed class RecordGenerator
{
    private const int MaxSplitDepth = 2;
    private const int MaxIntegrationLevels = 4;

    private readonly ILlmClient _llm;
    private readonly IAnalysisSchemaValidator _schema;
    private readonly TimeProvider _time;

    public RecordGenerator(ILlmClient llm, IAnalysisSchemaValidator schema, TimeProvider time)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <exception cref="LlmUnavailableException">The inference server cannot be started or reached (LLM-001).</exception>
    /// <exception cref="OperationCanceledException">Cancelled (recording started, user cancel); finished pieces are kept.</exception>
    public async Task<GenerationResult> GenerateAsync(GenerationRequest request, IProgress<GenerationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SectionId is { } sectionId) return await RegenerateSectionAsync(request, sectionId, progress, cancellationToken).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        var settings = request.Settings;
        var appWarnings = new List<string>();
        settings = await PrepareModelAsync(settings, appWarnings, cancellationToken).ConfigureAwait(false);
        var estimator = new TokenEstimator(settings.TokenFactor);
        var zone = FindZone(request.TimeZoneId);
        var session = AnalysisPlanner.Session(request.StartedAtUtc, zone, request.Title);
        var sessionDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.StartedAtUtc, zone).DateTime);
        var transcript = request.Transcript;
        var configHash = settings.ComputeHash();
        var analysisStore = new AnalysisStore(request.SessionDirectory);
        var work = analysisStore.WorkDirectory(request.JobId);

        var sessionTokens = estimator.Estimate(JsonSerializer.Serialize(session, AnalysisJson.Compact)) + 64;
        var extractionBudget = Math.Min(settings.MaxPieceTokens, new ContextBudget(settings.ContextLength, settings.OutputReserve,
            Overhead(estimator, AnalysisPrompts.Extraction, LlmSchemas.Extraction) + sessionTokens).RequireInputTokens(200));
        var plan = LoadOrCreatePlan(work, transcript, configHash, extractionBudget, estimator);
        if (plan.IsEmpty)
            return new GenerationResult { Outcome = GenerationOutcome.NotAnalyzable, PieceCount = 0, FailedPieces = [], Requests = [], DroppedEvidence = 0, DroppedItems = 0, MeasuredTokenFactor = settings.TokenFactor, Elapsed = watch.Elapsed };

        var stats = new List<RequestStat>();
        var pieces = new List<PieceAnalysis>();
        var failed = new List<FailedPiece>();
        var finished = 0;
        progress?.Report(new GenerationProgress("extract", 0, plan.Pieces.Count));
        using var gate = new SemaphoreSlim(Math.Max(1, settings.Parallelism));
        var works = await Task.WhenAll(plan.Pieces.Select(async piece =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(work, $"piece-{piece.Index:0000}.json");
                var done = AnalysisFiles.TryRead<GenerationWorkPiece>(path);
                if (done is null)
                {
                    var requests = new List<RequestStat>();
                    PieceAnalysis? result = null;
                    FailedPiece? failure = null;
                    if (piece.Oversize)
                        failure = new FailedPiece(piece.Index, piece.StartMs, piece.EndMs, "区間が長すぎて 1 回の要求に入りません");
                    else
                        (result, failure) = await ExtractAsync(piece, transcript, session, sessionDate, settings, requests, 0, cancellationToken).ConfigureAwait(false);
                    done = new GenerationWorkPiece { Piece = piece.Index, Result = result, Failure = failure, Requests = requests };
                    AnalysisFiles.Write(path, done);
                }
                progress?.Report(new GenerationProgress("extract", Interlocked.Increment(ref finished), plan.Pieces.Count));
                return done;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        foreach (var done in works.OrderBy(w => w.Piece))
        {
            stats.AddRange(done.Requests);
            if (done.Result is { } r) pieces.Add(r);
            if (done.Failure is { } f) failed.Add(f);
        }

        var dropped = pieces.SelectMany(p => p.Dropped).ToList();
        var droppedEvidence = pieces.Sum(p => p.DroppedEvidence);
        if (pieces.Count == 0)
            return Result(GenerationOutcome.Failed, null, null, plan, failed, stats, droppedEvidence, dropped.Count, estimator, watch, "すべての区間で記録を作れませんでした。");
        if (pieces.All(p => p.SummaryPoints.Count == 0 && p.Topics.Count == 0 && p.Candidates.Count == 0))
            return Result(GenerationOutcome.NotAnalyzable, null, null, plan, failed, stats, droppedEvidence, dropped.Count, estimator, watch, null);

        progress?.Report(new GenerationProgress("integrate", 0, 1));
        var record = await IntegrateAsync(pieces, session, settings, estimator, stats, appWarnings, work, cancellationToken).ConfigureAwait(false);
        dropped.AddRange(record.Dropped);
        record = IntegrationConverter.AddUncitedDecisions(record, plan.Pieces.SelectMany(p => p.SegmentIds).Distinct().Select(id => transcript.Find(id)).OfType<TranscriptSegment>(), pieces);
        if (failed.Count > 0) appWarnings.Add($"{failed.Count} 個の区間で記録を作れませんでした（部分生成）。");

        var (analysis, notes) = AnalysisAssembler.Assemble(transcript.SessionId, transcript.Revision, record, pieces, appWarnings);
        analysis = analysis with { Warnings = [.. analysis.Warnings.Concat(AnalysisValidator.SuspiciousText(analysis)).Distinct().Take(100)] };
        progress?.Report(new GenerationProgress("validate", 0, 1));
        var issues = AnalysisValidator.Validate(analysis, transcript).Select(i => $"{i.Path}: {i.Code}")
            .Concat(_schema.Validate(JsonSerializer.Serialize(analysis, AnalysisJson.Options))).ToList();
        if (issues.Count > 0)
        {
            AnalysisFiles.Write(Path.Combine(work, "rejected.json"), new { issues, analysis });
            return Result(GenerationOutcome.Failed, null, null, plan, failed, stats, droppedEvidence, dropped.Count, estimator, watch,
                "生成した記録が検証に通りませんでした（VAL-001）: " + string.Join("; ", issues.Take(5)));
        }

        var now = _time.GetUtcNow();
        var document = new AnalysisDocument
        {
            Revision = analysisStore.NextRevision(),
            CreatedAtUtc = now,
            Provenance = new AnalysisProvenance
            {
                ModelId = settings.ModelId,
                ModelSha256 = request.ModelSha256,
                PromptVersion = AnalysisPrompts.Version,
                ConfigHash = configHash,
                ContextLength = settings.ContextLength,
                OutputReserve = settings.OutputReserve,
                JobId = request.JobId,
            },
            Analysis = analysis,
            CandidateNotes = notes,
            Dropped = dropped,
            FailedPieces = failed,
            PieceCount = plan.Pieces.Count,
        };
        analysisStore.Save(document);
        var records = new RecordStore(request.SessionDirectory);
        var recordDocument = RecordFactory.FromAnalysis(transcript.SessionId, document, records.NextRevision(), now);
        records.Save(recordDocument, d => RecordMarkdown.Render(d, new RecordContext(request.StartedAtUtc, request.TimeZoneId, transcript, Approved: false)));
        progress?.Report(new GenerationProgress("done", 1, 1));
        return Result(GenerationOutcome.Generated, document.Revision, recordDocument.Revision, plan, failed, stats, droppedEvidence, dropped.Count, estimator, watch, null);
    }

    /// <summary>Most statements one regenerated section keeps (a section is one topic, not a second record).</summary>
    internal const int MaxSectionStatements = 12;

    /// <summary>
    /// Regenerates one section from the segments its statements rest on (and the segments between them, when they
    /// fit one request): one extraction, then its statements replace the section's in a new record revision. The
    /// heading, kind and candidates stay; nothing else of the record changes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The section is gone, or has no evidence to start from.</exception>
    private async Task<GenerationResult> RegenerateSectionAsync(GenerationRequest request, Guid sectionId, IProgress<GenerationProgress>? progress, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var records = new RecordStore(request.SessionDirectory);
        var record = records.LoadLatest() ?? throw new InvalidOperationException("記録がまだありません。");
        var section = record.Sections.FirstOrDefault(s => s.Id == sectionId) ?? throw new InvalidOperationException("作り直す章が記録にありません（削除された可能性があります）。");
        var transcript = request.Transcript;
        var evidence = section.Statements.SelectMany(s => s.Evidence).Select(e => e.SegmentId).ToHashSet();
        var evidenceSegments = transcript.Segments.Where(s => evidence.Contains(s.SegmentId)).ToList();
        if (evidenceSegments.Count == 0) throw new InvalidOperationException("この章には根拠の区間がないため、作り直せません（手入力の章など）。");

        var settings = await PrepareModelAsync(request.Settings, [], cancellationToken).ConfigureAwait(false);
        var estimator = new TokenEstimator(settings.TokenFactor);
        var zone = FindZone(request.TimeZoneId);
        var session = AnalysisPlanner.Session(request.StartedAtUtc, zone, request.Title);
        var sessionDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.StartedAtUtc, zone).DateTime);
        var budget = Math.Min(settings.MaxPieceTokens, new ContextBudget(settings.ContextLength, settings.OutputReserve,
            Overhead(estimator, AnalysisPrompts.Extraction, LlmSchemas.Extraction) + estimator.Estimate(JsonSerializer.Serialize(session, AnalysisJson.Compact)) + 64).RequireInputTokens(200));

        // The span from the first to the last evidence keeps what was said between them; too long, the evidence alone.
        var from = evidenceSegments.Min(s => s.StartMs);
        var to = evidenceSegments.Max(s => s.EndMs);
        var span = transcript.Segments.Where(s => s.StartMs >= from && s.EndMs <= to && !string.IsNullOrWhiteSpace(s.Text) && !s.Has(SegmentQuality.Silence)).ToList();
        static int Cost(IEnumerable<TranscriptSegment> segments, TokenEstimator e) => segments.Sum(s => e.Estimate(s.Text.Trim()) + AnalysisPlanner.SegmentOverheadTokens);
        var chosen = Cost(span, estimator) <= budget ? span : evidenceSegments;
        var piece = new AnalysisPiece
        {
            Index = 1,
            SegmentIds = [.. chosen.Select(s => s.SegmentId)],
            StartMs = chosen.Min(s => s.StartMs),
            EndMs = chosen.Max(s => s.EndMs),
            EstimatedTokens = Cost(chosen, estimator),
        };

        progress?.Report(new GenerationProgress("extract", 0, 1));
        var stats = new List<RequestStat>();
        var (result, failure) = await ExtractAsync(piece, transcript, session, sessionDate, settings, stats, 0, cancellationToken).ConfigureAwait(false);
        progress?.Report(new GenerationProgress("extract", 1, 1));
        var plan = new AnalysisPlan { SessionId = transcript.SessionId, TranscriptRevision = transcript.Revision, InputTokenBudget = budget, Pieces = [piece], Skipped = [] };
        if (result is null)
            return Result(GenerationOutcome.Failed, null, null, plan, failure is null ? [] : [failure], stats, 0, 0, estimator, watch, failure?.Reason ?? "章を作り直せませんでした。");

        // Topics first (they carry the detail), then the summary points when the model put everything there.
        var statements = result.Topics.SelectMany(t => t.Statements).Concat(result.Topics.Count == 0 ? result.SummaryPoints : [])
            .Where(s => s.Evidence.Count > 0).Take(MaxSectionStatements).ToList();
        if (statements.Count == 0)
            return Result(GenerationOutcome.NotAnalyzable, null, null, plan, [], stats, result.DroppedEvidence, result.Dropped.Count, estimator, watch, null);

        progress?.Report(new GenerationProgress("validate", 0, 1));
        var replacement = new RecordSection
        {
            Id = section.Id,
            Kind = section.Kind,
            Heading = section.Heading,
            HeadingOrigin = section.HeadingOrigin,
            Statements = [.. statements.Select(RecordFactory.Statement)],
        };
        var provenance = record.Provenance with { ModelId = settings.ModelId, ModelSha256 = request.ModelSha256, PromptVersion = AnalysisPrompts.Version, ConfigHash = settings.ComputeHash(), JobId = request.JobId };
        // The user may have edited the record while the model worked: replace the section in the latest revision.
        var latest = records.LoadLatest()!;
        if (latest.Sections.All(s => s.Id != sectionId)) throw new InvalidOperationException("作り直す章が記録にありません（削除された可能性があります）。");
        var next = RecordEditing.ReplaceSection(latest, records.NextRevision(), sectionId, replacement, provenance, _time.GetUtcNow());
        records.Save(next, d => RecordMarkdown.Render(d, new RecordContext(request.StartedAtUtc, request.TimeZoneId, transcript, Approved: false)));
        progress?.Report(new GenerationProgress("done", 1, 1));
        return Result(GenerationOutcome.Generated, null, next.Revision, plan, [], stats, result.DroppedEvidence, result.Dropped.Count, estimator, watch, null);
    }

    /// <summary>
    /// Makes sure the model is loaded and uses the context length it is really loaded with (detail spec 9.2: read
    /// C when possible). A server that reports the model as not loaded (an OpenAI-compatible server with on-demand
    /// loading; the bundled one always reports it loaded) is woken by a one-token request before C is read.
    /// </summary>
    private async Task<GenerationSettings> PrepareModelAsync(GenerationSettings settings, List<string> warnings, CancellationToken cancellationToken)
    {
        var model = await _llm.GetModelAsync(settings.ModelId, cancellationToken).ConfigureAwait(false);
        if (model.Loaded == false)
        {
            await _llm.CompleteAsync(new LlmRequest
            {
                Model = settings.ModelId,
                Messages = [new(LlmRole.User, "ok")],
                SchemaName = "voice_journal_wake",
                Schema = """{"type":"object","additionalProperties":false,"required":["ok"],"properties":{"ok":{"type":"boolean"}}}""",
                MaxTokens = 8,
            }, cancellationToken).ConfigureAwait(false);
            model = await _llm.GetModelAsync(settings.ModelId, cancellationToken).ConfigureAwait(false);
        }
        if (model.Notice is { Length: > 0 } notice) warnings.Add(notice);
        if (model.LoadedContextLength is { } loaded && loaded < settings.ContextLength)
        {
            warnings.Add($"推論エンジンのコンテキスト長が {loaded} のため、設定の {settings.ContextLength} ではなく {loaded} で区間を分けました。空きメモリが足りなかった可能性があります。");
            return settings with { ContextLength = loaded };
        }
        return settings;
    }

    /// <summary>
    /// Fixed part of a request (H of detail spec 9.2). The server enforces the schema as a grammar and does not put
    /// it in the prompt (Phase D2: 838 prompt tokens measured where the schema alone would be ~1,300), so a margin
    /// for the chat template and the response-format bookkeeping replaces the schema's size.
    /// </summary>
    internal static int Overhead(TokenEstimator estimator, string systemPrompt, string schema) =>
        estimator.Estimate(systemPrompt) + Math.Min(estimator.Estimate(schema), SchemaMarginTokens);

    internal const int SchemaMarginTokens = 256;

    private static GenerationResult Result(GenerationOutcome outcome, int? analysis, int? record, AnalysisPlan plan, List<FailedPiece> failed, List<RequestStat> stats,
        int droppedEvidence, int droppedItems, TokenEstimator estimator, Stopwatch watch, string? error)
    {
        var measured = stats.Where(s => s.PromptTokens > 0 && s.EstimatedPromptTokens > 0).ToList();
        var factor = measured.Count == 0 ? estimator.Factor
            : estimator.Factor * TokenEstimator.Calibrate(measured.Sum(s => s.EstimatedPromptTokens), measured.Sum(s => s.PromptTokens));
        return new GenerationResult
        {
            Outcome = outcome,
            AnalysisRevision = analysis,
            RecordRevision = record,
            PieceCount = plan.Pieces.Count,
            FailedPieces = failed,
            Requests = stats,
            DroppedEvidence = droppedEvidence,
            DroppedItems = droppedItems,
            MeasuredTokenFactor = Math.Clamp(factor, 0.3, 3.0),
            Elapsed = watch.Elapsed,
            Error = error,
        };
    }

    private static AnalysisPlan LoadOrCreatePlan(string work, TranscriptDocument transcript, string configHash, int budget, TokenEstimator estimator)
    {
        var path = Path.Combine(work, "plan.json");
        if (AnalysisFiles.TryRead<GenerationWorkPlan>(path) is { } saved && saved.TranscriptRevision == transcript.Revision && saved.ConfigHash == configHash)
            return saved.Plan;
        if (Directory.Exists(work))
            foreach (var stale in Directory.EnumerateFiles(work, "*.json").Where(f => !Path.GetFileName(f).Equals(RecordJobStore.FileName, StringComparison.Ordinal)))
                File.Delete(stale); // other input: no piece is reusable (the job ledger stays)
        var plan = AnalysisPlanner.Plan(transcript, budget, estimator);
        AnalysisFiles.Write(path, new GenerationWorkPlan { TranscriptRevision = transcript.Revision, ConfigHash = configHash, InputTokenBudget = budget, Plan = plan });
        return plan;
    }

    // One piece; a cut answer or a context error splits it in halves (detail spec 9.2: "区間を分ける").
    private async Task<(PieceAnalysis?, FailedPiece?)> ExtractAsync(AnalysisPiece piece, TranscriptDocument transcript, WireSession session, DateOnly sessionDate,
        GenerationSettings settings, List<RequestStat> stats, int depth, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(AnalysisPlanner.BuildInput(piece, transcript, session), AnalysisJson.Compact);
        var estimate = new TokenEstimator(settings.TokenFactor);
        var messages = new List<LlmMessage> { new(LlmRole.System, AnalysisPrompts.Extraction), new(LlmRole.User, input) };
        var estimated = Overhead(estimate, AnalysisPrompts.Extraction, LlmSchemas.Extraction) + estimate.Estimate(input);
        var (output, failure) = await AskAsync<ExtractionOutput>($"extract-{piece.Index}", messages, LlmSchemas.ExtractionName, LlmSchemas.Extraction, settings, estimated, stats, cancellationToken).ConfigureAwait(false);
        if (output is not null) return (ExtractionConverter.Convert(output, piece, transcript, sessionDate), null);
        if (failure == AskFailure.TooLong && depth < MaxSplitDepth && piece.SegmentIds.Count > 1)
        {
            var half = piece.SegmentIds.Count / 2;
            AnalysisPiece Part(IEnumerable<Guid> ids)
            {
                var list = ids.ToList();
                var segments = list.Select(id => transcript.Find(id)!).ToList();
                return piece with { SegmentIds = list, StartMs = segments.Min(s => s.StartMs), EndMs = segments.Max(s => s.EndMs) };
            }
            var (a, fa) = await ExtractAsync(Part(piece.SegmentIds.Take(half)), transcript, session, sessionDate, settings, stats, depth + 1, cancellationToken).ConfigureAwait(false);
            var (b, fb) = await ExtractAsync(Part(piece.SegmentIds.Skip(half)), transcript, session, sessionDate, settings, stats, depth + 1, cancellationToken).ConfigureAwait(false);
            if (a is null || b is null)
                return (a ?? b, new FailedPiece(piece.Index, (fa ?? fb)!.StartMs, (fa ?? fb)!.EndMs, (fa ?? fb)!.Reason));
            return (Merge(a, b), null);
        }
        var reason = failure switch
        {
            AskFailure.TooLong => "応答が出力の上限で切れました（LLM-002）",
            AskFailure.Invalid => "応答が JSON のスキーマに合いませんでした（LLM-003）",
            _ => "推論エンジンの一時的な障害が続きました",
        };
        return (null, new FailedPiece(piece.Index, piece.StartMs, piece.EndMs, reason));
    }

    private static PieceAnalysis Merge(PieceAnalysis a, PieceAnalysis b) => a with
    {
        SummaryPoints = [.. a.SummaryPoints, .. b.SummaryPoints],
        Topics = [.. a.Topics, .. b.Topics],
        Candidates = [.. a.Candidates, .. b.Candidates],
        Warnings = [.. a.Warnings, .. b.Warnings],
        Dropped = [.. a.Dropped, .. b.Dropped],
        DroppedEvidence = a.DroppedEvidence + b.DroppedEvidence,
    };

    private async Task<IntegratedRecord> IntegrateAsync(IReadOnlyList<PieceAnalysis> pieces, WireSession session, GenerationSettings settings, TokenEstimator estimator,
        List<RequestStat> stats, List<string> warnings, string work, CancellationToken cancellationToken)
    {
        var budget = new ContextBudget(settings.ContextLength, settings.OutputReserve,
            Overhead(estimator, AnalysisPrompts.Integration, LlmSchemas.Integration) + 200).RequireInputTokens(200);
        var items = IntegrationConverter.Items(pieces);
        for (var level = 1; level <= MaxIntegrationLevels; level++)
        {
            var groups = IntegrationConverter.Group(items, budget, estimator);
            var records = new List<IntegratedRecord>();
            for (var g = 0; g < groups.Count; g++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(work, $"integrate-{level}-{g + 1:000}.json");
                var record = AnalysisFiles.TryRead<IntegratedRecord>(path);
                if (record is null)
                {
                    var input = JsonSerializer.Serialize(IntegrationConverter.BuildInput(groups[g], session), AnalysisJson.Compact);
                    var messages = new List<LlmMessage> { new(LlmRole.System, AnalysisPrompts.Integration), new(LlmRole.User, input) };
                    var estimated = Overhead(estimator, AnalysisPrompts.Integration, LlmSchemas.Integration) + estimator.Estimate(input);
                    var (output, _) = await AskAsync<IntegrationOutput>($"integrate-{level}-{g + 1}", messages, LlmSchemas.IntegrationName, LlmSchemas.Integration, settings, estimated, stats, cancellationToken).ConfigureAwait(false);
                    if (output is null)
                    {
                        warnings.Add("記録の統合に失敗したため、区間ごとの要点をそのまま並べています。");
                        return Fallback(pieces);
                    }
                    record = IntegrationConverter.Convert(output, groups[g], level);
                    AnalysisFiles.Write(path, record);
                }
                records.Add(record);
            }
            if (records.Count == 1) return Complete(records[0], pieces, warnings);
            items = IntegrationConverter.Items(records);
        }
        warnings.Add("記録の統合が深くなりすぎたため、区間ごとの要点をそのまま並べています。");
        return Fallback(pieces);
    }

    // The integration must leave a summary (the schema needs 1–7 items) and a title.
    private static IntegratedRecord Complete(IntegratedRecord record, IReadOnlyList<PieceAnalysis> pieces, List<string> warnings)
    {
        if (record.Summary.Count > 0 && record.Title.Length > 0) return record with { Summary = [.. record.Summary.Take(7)] };
        warnings.Add("統合で要約か題名が作られなかったため、区間ごとの要点を使っています。");
        var fallback = Fallback(pieces);
        return record with
        {
            Summary = record.Summary.Count > 0 ? [.. record.Summary.Take(7)] : fallback.Summary,
            Title = record.Title.Length > 0 ? record.Title : fallback.Title,
        };
    }

    private static IntegratedRecord Fallback(IReadOnlyList<PieceAnalysis> pieces)
    {
        var summary = pieces.SelectMany(p => p.SummaryPoints).Take(7).ToList();
        var sections = pieces.SelectMany(p => p.Topics).ToList();
        if (summary.Count == 0) summary = [.. sections.SelectMany(s => s.Statements).Take(3)];
        if (summary.Count == 0) summary = [.. pieces.SelectMany(p => p.Candidates).Take(3).Select(c => new Statement { Text = c.Fields.Title, Certainty = c.Certainty, Evidence = c.Evidence })];
        return new IntegratedRecord
        {
            RecordTypes = [RecordType.Other],
            Title = "（題名未生成）",
            Summary = summary,
            Sections = sections,
            Warnings = [],
            Dropped = [],
        };
    }

    private enum AskFailure
    {
        None,
        TooLong,
        Invalid,
        Transient,
    }

    // One question with the schema: transient errors retried with the same input, one repair for a bad answer.
    private async Task<(T?, AskFailure)> AskAsync<T>(string step, List<LlmMessage> messages, string schemaName, string schema, GenerationSettings settings,
        int estimatedPrompt, List<RequestStat> stats, CancellationToken cancellationToken) where T : class
    {
        var repaired = false;
        var transient = 0;
        while (true)
        {
            LlmResponse response;
            try
            {
                response = await _llm.CompleteAsync(new LlmRequest
                {
                    Model = settings.ModelId,
                    Messages = messages,
                    SchemaName = schemaName,
                    Schema = schema,
                    Temperature = settings.Temperature,
                    MaxTokens = settings.OutputReserve,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmContextException)
            {
                stats.Add(new RequestStat(step, 0, 0, 0, "context", estimatedPrompt));
                return (null, AskFailure.TooLong);
            }
            catch (LlmProtocolException)
            {
                stats.Add(new RequestStat(step, 0, 0, 0, "error", estimatedPrompt));
                if (++transient > settings.TransientRetries) return (null, AskFailure.Transient);
                continue;
            }
            stats.Add(new RequestStat(step, response.PromptTokens, response.CompletionTokens, response.Elapsed.TotalSeconds, response.FinishReason, estimatedPrompt));
            if (response.FinishReason == "length") return (null, AskFailure.TooLong);
            var parsed = Parse<T>(response.Content, out var error);
            if (parsed is not null) return (parsed, AskFailure.None);
            if (repaired) return (null, AskFailure.Invalid);
            repaired = true;
            messages = [.. messages, new(LlmRole.Assistant, response.Content), new(LlmRole.User, AnalysisPrompts.Repair + "\n理由: " + error)];
        }
    }

    private static T? Parse<T>(string content, out string error) where T : class
    {
        if (JsonGuard.Check(content) is { } guard)
        {
            error = guard;
            return null;
        }
        try
        {
            var value = JsonSerializer.Deserialize<T>(content, AnalysisJson.Options);
            error = value is null ? "空の応答です" : string.Empty;
            return value;
        }
        catch (JsonException ex)
        {
            // The path and kind of error, never the content.
            error = "スキーマに合いません（" + (ex.Path ?? "$") + "）";
            return null;
        }
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }
}
