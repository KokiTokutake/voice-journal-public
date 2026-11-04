using System.Text.Json;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;
using VoiceJournal.Infrastructure.Llm;

namespace VoiceJournal.App.Services;

/// <summary>
/// Record generation settings (detail spec 9.1, 9.2, 18.2 UI-12). A job takes a snapshot when it is queued, so a
/// change applies to the next job. The defaults are the values measured in Phase D2 (<c>docs/environment-lock.md</c> 6).
/// </summary>
public sealed record LlmSettings
{
    /// <summary>LM Studio's OpenAI-compatible API, on loopback only.</summary>
    public string Url { get; init; } = LlmEndpoint.Default;

    public string ModelId { get; init; } = "gemma-3-4b-it";

    /// <summary>C: the context length the model is loaded with. When LM Studio reports a shorter one, that one is used.</summary>
    public int ContextLength { get; init; } = 16384;

    /// <summary>O: the output reserve, also every request's <c>max_tokens</c>.</summary>
    public int OutputReserve { get; init; } = 3072;

    public int MaxPieceTokens { get; init; } = 3000;

    public double Temperature { get; init; } = 0.1;

    /// <summary>One request may take this long (a 3,000-token piece takes about 1–2 minutes on the reference PC).</summary>
    public int RequestTimeoutMinutes { get; init; } = 10;

    public GenerationSettings ToGeneration() => new()
    {
        ModelId = ModelId,
        ContextLength = ContextLength,
        OutputReserve = OutputReserve,
        MaxPieceTokens = MaxPieceTokens,
        Temperature = Temperature,
    };

    /// <summary>Values outside what the generator can work with are replaced by the defaults (a hand-edited file).</summary>
    public LlmSettings Sanitized()
    {
        var defaults = new LlmSettings();
        var context = ContextLength is >= 4096 and <= 262144 ? ContextLength : defaults.ContextLength;
        return this with
        {
            Url = string.IsNullOrWhiteSpace(Url) ? defaults.Url : Url.Trim(),
            ModelId = string.IsNullOrWhiteSpace(ModelId) ? defaults.ModelId : ModelId.Trim(),
            ContextLength = context,
            OutputReserve = OutputReserve >= 1024 && OutputReserve <= context / 2 ? OutputReserve : Math.Min(defaults.OutputReserve, context / 2),
            MaxPieceTokens = MaxPieceTokens is >= 500 and <= 32768 ? MaxPieceTokens : defaults.MaxPieceTokens,
            Temperature = Temperature is >= 0 and <= 1 ? Temperature : defaults.Temperature,
            RequestTimeoutMinutes = RequestTimeoutMinutes is >= 1 and <= 60 ? RequestTimeoutMinutes : defaults.RequestTimeoutMinutes,
        };
    }
}

public interface ILlmSettingsStore
{
    LlmSettings Load();

    void Save(LlmSettings settings);
}

/// <summary><c>%LOCALAPPDATA%\VoiceJournal\config\llm.json</c>. Missing or broken means the defaults.</summary>
public sealed class JsonLlmSettingsStore(string path) : ILlmSettingsStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceJournal", "config", "llm.json");

    public LlmSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<LlmSettings>(AtomicFile.ReadAllBytes(path), VoiceJournalJson.Options) ?? new LlmSettings()).Sanitized()
                : new LlmSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LlmSettings();
        }
    }

    public void Save(LlmSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(settings.Sanitized(), VoiceJournalJson.Options));
    }
}

/// <summary>In memory, for tests.</summary>
public sealed class MemoryLlmSettingsStore(LlmSettings? initial = null) : ILlmSettingsStore
{
    public LlmSettings Current { get; private set; } = initial ?? new LlmSettings();

    public LlmSettings Load() => Current;

    public void Save(LlmSettings settings) => Current = settings;
}
