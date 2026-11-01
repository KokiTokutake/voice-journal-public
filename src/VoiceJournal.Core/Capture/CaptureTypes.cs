namespace VoiceJournal.Core.Capture;

/// <summary>What the user chose to record (detail spec 6.1).</summary>
public enum CaptureMode
{
    Mic,
    Loopback,
    Both,
}

/// <summary>Source label of a track. Never interpreted as a person (detail spec 6.3).</summary>
public enum AudioSource
{
    Mic,
    Loopback,
}

/// <summary>Whether the mic stream bypasses the system capture effects (Phase A: raw is the default).</summary>
public enum MicProcessingMode
{
    Raw,
    Normal,
}

/// <summary>Session capture state (detail spec 7).</summary>
public enum CaptureState
{
    Ready,
    Recording,
    /// <summary>At least one source stopped (e.g. device unplugged) while the others keep recording.</summary>
    Partial,
    Stopping,
    Finalized,
    /// <summary>The app ended unexpectedly or the PC slept; the user decides to resume or finish.</summary>
    Interrupted,
    Failed,
}

/// <summary>State of one source's track.</summary>
public enum TrackState
{
    Recording,
    Disconnected,
    Stopped,
    Failed,
}

/// <summary>State of one chunk file.</summary>
public enum ChunkState
{
    /// <summary><c>.partial</c> still being written (or left behind by a crash).</summary>
    Partial,
    /// <summary>Finalized <c>.wav</c> with a verified header and SHA-256.</summary>
    Completed,
    /// <summary>Readable part of a crashed <c>.partial</c>, saved as a separate recovered WAV.</summary>
    Recovered,
}

/// <summary>Timeline events stored per track (audio_events, detail spec 15.2).</summary>
public enum AudioEventKind
{
    /// <summary>Loopback delivered no packets because nothing was playing; silence was inserted. Not a failure.</summary>
    LoopbackSilence,
    /// <summary>The bounded queue overflowed; audio in the range was lost and recording stopped safely.</summary>
    QueueOverflow,
    DeviceDisconnected,
    DeviceReconnected,
    /// <summary>Recording was cut off by a crash, power event or sleep.</summary>
    Interrupted,
    /// <summary>A partial chunk was recovered into a separate WAV at startup.</summary>
    Recovered,
    DiskSpaceLow,
    /// <summary>Writing a chunk failed (I/O error); the track stopped and earlier chunks were kept.</summary>
    WriteFailed,
    /// <summary>The device flagged a discontinuity; nothing was inserted.</summary>
    Discontinuity,
    ClockCorrection,
}
