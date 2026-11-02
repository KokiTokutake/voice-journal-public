using System.Text.Json;
using Corvus.Json;
using Corvus.Json.Validator;
using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Infrastructure.Llm;

/// <summary>Draft 2020-12 validation of an analysis against the full schema of detail spec 16 (Corvus.Json.Validator, Phase A).</summary>
public sealed class AnalysisSchemaValidator : IAnalysisSchemaValidator
{
    // Compiling the schema takes a moment; once per process.
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(AnalysisSchema.Json, "https://voicejournal.invalid/schemas/analysis-1.0.json"));

    public IReadOnlyList<string> Validate(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = JsonGuard.MaxDepth + 1 });
        var result = Schema.Value.Validate(doc.RootElement, ValidationLevel.Detailed);
        return result.IsValid ? [] : [.. result.Results.Where(r => !r.Valid).Select(r => $"{r.Location}: {r.Message}").Distinct().Take(50)];
    }
}
