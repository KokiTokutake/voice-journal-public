using VoiceJournal.Core.Capture;

namespace VoiceJournal.Core.Transcription;

/// <summary>Error codes of detail spec 19.</summary>
public enum SttErrorCode
{
    /// <summary>STT-001: recognizer executable or model missing, or its hash does not match.</summary>
    ToolMissing,

    /// <summary>STT-002: the recognizer exited abnormally, timed out, or wrote invalid output.</summary>
    RunFailed,

    /// <summary>The run was cancelled (by the user, or to make room for a recording when pausing was not possible).</summary>
    Cancelled,

    /// <summary>Not enough free memory to start the recognizer.</summary>
    NotEnoughMemory,
}

public sealed class SttException : Exception
{
    public SttException() : this(SttErrorCode.RunFailed, "Speech recognition failed.") { }

    public SttException(string message) : this(SttErrorCode.RunFailed, message) { }

    public SttException(string message, Exception inner) : this(SttErrorCode.RunFailed, message, inner) { }

    public SttException(SttErrorCode code, string message, Exception? inner = null) : base(message, inner) => Code = code;

    public SttErrorCode Code { get; }
}

/// <summary>
/// Lets the job runner pause recognition while a recording runs (plan 3.6). The engine suspends the recognizer
/// process while <see cref="IsPaused"/> is true and does not count that time against the timeout.
/// </summary>
public sealed class SttPauseGate
{
    private readonly Lock _gate = new();
    private bool _paused;

    public event EventHandler? Changed;

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    public void Pause() => Set(true);

    public void Resume() => Set(false);

    private void Set(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused) return;
            _paused = paused;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One recognizer run over one 16 kHz piece.</summary>
public sealed record SttRunRequest
{
    public required string AudioPath { get; init; }

    /// <summary>Folder for the recognizer's own output (<c>stt/native/&lt;job-id&gt;</c>).</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>File name prefix inside <see cref="OutputDirectory"/> (e.g. <c>mic-0001</c>).</summary>
    public required string OutputName { get; init; }

    public required SttConfiguration Configuration { get; init; }

    /// <summary>Length of the audio in <see cref="AudioPath"/>; segments must end within it.</summary>
    public required long DurationMs { get; init; }

    /// <summary>Limit for this run, not counting time spent paused (detail spec 3.2: 60 minutes per piece).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(60);

    public SttPauseGate? Pause { get; init; }
}

/// <summary>A recognized segment in piece-local time, before ids and session offsets are added.</summary>
public sealed record SttRawSegment(long StartMs, long EndMs, string Text, int NativeIndex);

public sealed record SttRunResult
{
    public required IReadOnlyList<SttRawSegment> Segments { get; init; }

    /// <summary>Path of the native JSON, relative to the run's <see cref="SttRunRequest.OutputDirectory"/>.</summary>
    public required string NativeFile { get; init; }

    /// <summary>Wall time spent running, excluding pauses.</summary>
    public required TimeSpan Elapsed { get; init; }
}

/// <summary>The registered recognizer (whisper.cpp CLI) and its models, verified by SHA-256.</summary>
public sealed record SttEngineInfo(string EngineName, string EngineVersion, string EngineSha256, string ModelName, string ModelSha256);

public interface ISttEngine
{
    /// <summary>Checks that the executable and the model of <paramref name="configuration"/> exist and match their hashes.</summary>
    /// <exception cref="SttException"><see cref="SttErrorCode.ToolMissing"/>.</exception>
    Task<SttEngineInfo> VerifyAsync(SttConfiguration configuration, CancellationToken cancellationToken);

    /// <exception cref="SttException">The run failed, timed out or was cancelled.</exception>
    Task<SttRunResult> RunAsync(SttRunRequest request, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>A 16 kHz mono piece of one source, ready for the recognizer.</summary>
public sealed record SttInputPiece(AudioSource Source, SttPiece Piece, string Path, string Sha256);

/// <summary>The STT input of one session (<c>derived/stt-input/&lt;input-hash&gt;</c>).</summary>
public sealed record SttInput
{
    public required string InputHash { get; init; }

    public required string Directory { get; init; }

    public required long DurationMs { get; init; }

    public required IReadOnlyList<SttInputPiece> Pieces { get; init; }

    /// <summary>Gain applied per source, in dB (0 when gain normalization is off).</summary>
    public required IReadOnlyDictionary<AudioSource, double> GainDb { get; init; }

    /// <summary>RMS level (dBFS, before gain) of a source over a range; null when unknown.</summary>
    public required Func<AudioSource, long, long, double?> LevelDbfs { get; init; }
}

public interface ISttInputBuilder
{
    /// <summary>
    /// Builds (or reuses, when the same input already exists and verifies) the 16 kHz pieces for every source
    /// of the session.
    /// </summary>
    Task<SttInput> BuildAsync(string sessionDirectory, SttConfiguration configuration, IProgress<double>? progress, CancellationToken cancellationToken);
}
