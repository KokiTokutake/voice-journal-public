using System.Globalization;
using System.Text.Json;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Llm;

namespace VoiceJournal.AudioProbe;

// Phase D2: build a record from a session's transcript in use with LM Studio, and print timing and sizes only.
//   AudioProbe organize <sessionDir> [--model gemma-3-4b-it] [--context 16384] [--reserve 3072] [--url http://127.0.0.1:1234/v1]
//                       [--factor 1.0] [--temperature 0.1] [--parallel 1] [--piece-tokens 3000] [--out <json>]
// The analysis and the record draft are saved in the session folder (analysis/, records/, record.draft.md).
internal static class OrganizeCommand
{
    /// <summary>The generation options shared with eval-record. Defaults are the app's (docs/phase-d-plan.md 0).</summary>
    public static GenerationSettings Settings(string[] args)
    {
        string Option(string name, string fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        return new GenerationSettings
        {
            ModelId = Option("--model", "gemma-3-4b-it"),
            ContextLength = int.Parse(Option("--context", "16384"), CultureInfo.InvariantCulture),
            OutputReserve = int.Parse(Option("--reserve", "3072"), CultureInfo.InvariantCulture),
            TokenFactor = double.Parse(Option("--factor", "1.0"), CultureInfo.InvariantCulture),
            Temperature = double.Parse(Option("--temperature", "0.1"), CultureInfo.InvariantCulture),
            Parallelism = int.Parse(Option("--parallel", "1"), CultureInfo.InvariantCulture),
            MaxPieceTokens = int.Parse(Option("--piece-tokens", "3000"), CultureInfo.InvariantCulture),
        };
    }

    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions json)
    {
        var sessionDir = Path.GetFullPath(args[1]);
        string Option(string name, string fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        var settings = Settings(args);
        var manifest = SessionManifest.Load(sessionDir);
        var transcript = new TranscriptStore(sessionDir).LoadCurrent() ?? throw new InvalidOperationException("文字起こしがありません。");
        using var client = new LmStudioClient(LlmEndpoint.Parse(Option("--url", LlmEndpoint.Default)), TimeSpan.FromMinutes(10));
        var model = await client.GetModelAsync(settings.ModelId, CancellationToken.None).ConfigureAwait(false);
        Console.Error.WriteLine($"model {model.Id}: loaded context {model.LoadedContextLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}, max {model.MaxContextLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        if (model.LoadedContextLength is { } loaded && loaded != settings.ContextLength)
            Console.Error.WriteLine($"warning: loaded context {loaded} differs from --context {settings.ContextLength}");

        var generator = new RecordGenerator(client, new AnalysisSchemaValidator(), TimeProvider.System);
        var progress = new Progress<GenerationProgress>(p => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {p.Step} {p.Done}/{p.Total}"));
        var result = await generator.GenerateAsync(new GenerationRequest
        {
            SessionDirectory = sessionDir,
            Transcript = transcript,
            StartedAtUtc = manifest.StartedAtUtc,
            TimeZoneId = manifest.TimeZoneId,
            Title = manifest.Title,
            JobId = Option("--job", "") is { Length: > 0 } job ? Guid.Parse(job) : Guid.CreateVersion7(),
            Settings = settings,
        }, progress, CancellationToken.None).ConfigureAwait(false);

        var summary = new
        {
            session = sessionDir,
            transcript_revision = transcript.Revision,
            segments = transcript.Segments.Count,
            characters = transcript.Segments.Sum(s => s.Text.Length),
            settings.ContextLength,
            settings.OutputReserve,
            outcome = result.Outcome.ToString(),
            result.AnalysisRevision,
            result.RecordRevision,
            result.PieceCount,
            failed = result.FailedPieces,
            result.DroppedEvidence,
            result.DroppedItems,
            measured_token_factor = Math.Round(result.MeasuredTokenFactor, 3),
            elapsed_seconds = Math.Round(result.Elapsed.TotalSeconds, 1),
            requests = result.Requests,
            prompt_tokens = result.Requests.Sum(r => r.PromptTokens),
            completion_tokens = result.Requests.Sum(r => r.CompletionTokens),
            result.Error,
        };
        var text = JsonSerializer.Serialize(summary, json);
        Console.WriteLine(text);
        if (Option("--out", "") is { Length: > 0 } outPath) await File.WriteAllTextAsync(outPath, text).ConfigureAwait(false);
        return result.Outcome == GenerationOutcome.Failed ? 1 : 0;
    }
}
