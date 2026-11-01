namespace VoiceJournal.Core.Playback;

/// <summary>Plays recorded chunk files one after another on the default output device.</summary>
public interface IAudioPlayer : IDisposable
{
    bool IsPlaying { get; }

    /// <summary>Stops whatever is playing and starts the given WAV files in order.</summary>
    /// <returns>The name of the output device the sound goes to (the Windows default at this moment).</returns>
    string Play(IReadOnlyList<string> wavFiles);

    void StopPlayback();

    /// <summary>Raised when playback ends or fails (Error is set then). May be raised on a background thread.</summary>
    event EventHandler<PlaybackEndedEventArgs>? Ended;
}

public sealed class PlaybackEndedEventArgs(Exception? error) : EventArgs
{
    public Exception? Error { get; } = error;
}
