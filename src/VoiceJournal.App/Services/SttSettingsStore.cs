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

/// <summary><c>%LOCALAPPDATA%\VoiceJournal\config\stt-settings.json</c>. Missing or broken means the defaults (small, VAD on).</summary>
public sealed class JsonSttSettingsStore(string path) : ISttSettingsStore
{
    public static readonly SttConfiguration Defaults = new() { ModelName = "ggml-small.bin" };

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
