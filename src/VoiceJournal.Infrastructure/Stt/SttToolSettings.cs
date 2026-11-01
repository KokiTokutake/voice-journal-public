using System.Text.Json;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;

namespace VoiceJournal.Infrastructure.Stt;

/// <summary>A registered local file with its expected SHA-256 (detail spec 8.1: only pre-registered executables).</summary>
public sealed record RegisteredFile
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }
}

public sealed record RegisteredModel
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>Free physical memory needed to start the recognizer with this model.</summary>
    public required int RequiredMemoryMb { get; init; }
}

/// <summary>
/// The recognizer and models the app may run (<c>%LOCALAPPDATA%\VoiceJournal\config\stt.json</c>). The defaults are
/// the versions fixed in <c>docs/environment-lock.md</c> (chapters 4, 5 and 12).
/// </summary>
public sealed record SttToolSettings
{
    public const string FileName = "stt.json";

    public required RegisteredFile WhisperCli { get; init; }

    /// <summary>Models by file name (the name used in <c>SttConfiguration.ModelName</c>).</summary>
    public required IReadOnlyDictionary<string, RegisteredModel> Models { get; init; }

    public required RegisteredFile VadModel { get; init; }

    public static SttToolSettings Defaults(string appRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(appRoot);
        string P(params string[] parts) => System.IO.Path.Combine([appRoot, .. parts]);
        return new SttToolSettings
        {
            WhisperCli = new RegisteredFile
            {
                Path = P("tools", "whisper", "b5454", "Release", "whisper-cli.exe"),
                Sha256 = "331dba46d6427105d2b802cdbc7eae916ea1c5abf9b0d3b5cfe460d8db8e4366",
            },
            Models = new Dictionary<string, RegisteredModel>(StringComparer.Ordinal)
            {
                ["ggml-small.bin"] = new()
                {
                    Path = P("models", "whisper", "ggml-small.bin"),
                    Sha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
                    RequiredMemoryMb = 1024,
                },
                ["ggml-medium.bin"] = new()
                {
                    Path = P("models", "whisper", "ggml-medium.bin"),
                    Sha256 = "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
                    RequiredMemoryMb = 2560,
                },
            },
            VadModel = new RegisteredFile
            {
                Path = P("models", "vad", "ggml-silero-v6.2.0.bin"),
                Sha256 = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",
            },
        };
    }

    /// <summary>Reads <c>config/stt.json</c> under <paramref name="appRoot"/>, writing the defaults when it does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but cannot be read.</exception>
    public static SttToolSettings LoadOrCreate(string appRoot)
    {
        var path = System.IO.Path.Combine(appRoot, "config", FileName);
        if (!File.Exists(path))
        {
            var defaults = Defaults(appRoot);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(defaults, VoiceJournalJson.Options));
            return defaults;
        }
        try
        {
            return JsonSerializer.Deserialize<SttToolSettings>(AtomicFile.ReadAllBytes(path), VoiceJournalJson.Options)
                ?? throw new InvalidDataException($"'{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not valid.", ex);
        }
    }
}
