using System.Text.Json;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.App.Services;

/// <summary>Recognition settings chosen in UI-12. A job takes a snapshot when it is queued (changes apply to the next job).</summary>
public interface ISttSettingsStore
{
    SttConfiguration Load();

    void Save(SttConfiguration configuration);
}

/// <summary><c>%LOCALAPPDATA%\VoiceJournal\config\stt-settings.json</c>. Missing or broken means the defaults (medium, VAD on).</summary>
public sealed class JsonSttSettingsStore(string path) : ISttSettingsStore
{
    /// <summary>
    /// medium since 2026-11-09: on a real one-hour meeting small missed names and words and looped
    /// ("うんうん…"), medium did not (docs/phase-c-report.md 7 章). small stays as the fallback.
    /// </summary>
    public static readonly SttConfiguration Defaults = new() { ModelName = "ggml-medium.bin" };

    public const string FallbackModelName = "ggml-small.bin";

    /// <summary>
    /// <paramref name="configuration"/> with a model that is registered: its own, else the default, else small,
    /// else the first registered one. Unchanged when none is registered (the engine then reports the missing tool).
    /// </summary>
    public static SttConfiguration WithRegisteredModel(SttConfiguration configuration, IEnumerable<string> registered)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var names = registered.ToHashSet(StringComparer.Ordinal);
        if (names.Contains(configuration.ModelName)) return configuration;
        var model = names.Contains(Defaults.ModelName) ? Defaults.ModelName
            : names.Contains(FallbackModelName) ? FallbackModelName
            : names.Order(StringComparer.Ordinal).FirstOrDefault();
        return model is null ? configuration : configuration with { ModelName = model };
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceJournal", "config", "stt-settings.json");

    public SttConfiguration Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SttConfiguration>(AtomicFile.ReadAllBytes(path), VoiceJournalJson.Options) ?? Defaults
                : Defaults;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Defaults;
        }
    }

    public void Save(SttConfiguration configuration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(configuration, VoiceJournalJson.Options));
    }
}

/// <summary>In memory, for tests.</summary>
public sealed class MemorySttSettingsStore(SttConfiguration? initial = null) : ISttSettingsStore
{
    public SttConfiguration Current { get; private set; } = initial ?? JsonSttSettingsStore.Defaults;

    public SttConfiguration Load() => Current;

    public void Save(SttConfiguration configuration) => Current = configuration;
}
