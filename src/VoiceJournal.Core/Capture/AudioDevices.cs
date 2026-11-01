namespace VoiceJournal.Core.Capture;

/// <summary>An audio endpoint the user can pick. Mic = capture endpoint; Loopback = render endpoint.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, AudioSource Source, bool IsDefault);

public enum AudioDeviceChangeKind
{
    Added,
    Removed,
    StateChanged,
    DefaultChanged,
}

/// <summary>Raised when the device list may have changed. Raised on a background thread.</summary>
public sealed class AudioDeviceChangedEventArgs(AudioDeviceChangeKind kind, string? deviceId) : EventArgs
{
    public AudioDeviceChangeKind Kind { get; } = kind;

    /// <summary>Endpoint id the change concerns; null when a flow no longer has a default device.</summary>
    public string? DeviceId { get; } = deviceId;
}

/// <summary>
/// Device enumeration and source creation. The default device is resolved to a fixed id when a
/// source is created; a later change of the Windows default never moves a running recording
/// (detail spec 6.4: no silent switching).
/// </summary>
public interface IAudioDeviceCatalog : IDisposable
{
    /// <summary>Active endpoints for a source, default first.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioSource source);

    /// <summary>The current Windows default endpoint, or null when there is none.</summary>
    AudioDeviceInfo? GetDefault(AudioSource source);

    /// <summary>Creates a capture source on a specific endpoint.</summary>
    /// <exception cref="AudioDeviceException">The device does not exist or cannot be opened.</exception>
    IAudioCaptureSource CreateSource(AudioSource source, string deviceId, MicProcessingMode micMode);

    event EventHandler<AudioDeviceChangedEventArgs>? DevicesChanged;
}

/// <summary>Why an audio device could not be used. Drives the message shown to the user.</summary>
public enum AudioDeviceErrorKind
{
    Unknown,
    /// <summary>Windows privacy settings deny microphone access.</summary>
    AccessDenied,
    /// <summary>The endpoint is gone (unplugged, disabled) or was never present.</summary>
    DeviceNotFound,
    /// <summary>The endpoint disappeared while it was being used.</summary>
    DeviceInvalidated,
    /// <summary>Another application holds the device exclusively.</summary>
    DeviceInUse,
    /// <summary>The device does not support raw capture; the user can retry in normal mode.</summary>
    RawModeUnsupported,
    /// <summary>The device format cannot be stored as a WAV stream.</summary>
    UnsupportedFormat,
    /// <summary>The Windows audio service is not running.</summary>
    AudioServiceNotRunning,
}

/// <summary>An audio device failure, classified so the UI can explain it without exposing raw HRESULTs.</summary>
public sealed class AudioDeviceException : Exception
{
    public AudioDeviceException() { }

    public AudioDeviceException(string message) : base(message) { }

    public AudioDeviceException(string message, Exception inner) : base(message, inner) { }

    public AudioDeviceException(AudioDeviceErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    public AudioDeviceErrorKind Kind { get; }
}
