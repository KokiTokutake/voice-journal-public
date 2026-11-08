using System.Collections.Concurrent;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Playback;

namespace VoiceJournal.Tests.TestSupport;

/// <summary>UI dispatcher that queues posts until the test drains them (like the WPF dispatcher).</summary>
public sealed class QueueDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    public void Drain()
    {
        while (_queue.TryDequeue(out var action)) action();
    }
}

public sealed class FakeDeviceCatalog : IAudioDeviceCatalog
{
    public static readonly AudioFormatInfo MicFormat = AudioFormatInfo.Pcm16Bit(8000, 1);
    public static readonly AudioFormatInfo LoopbackFormat = AudioFormatInfo.IeeeFloat32Bit(8000, 2);

    public List<AudioDeviceInfo> Mics { get; } = [new("{mic-1}", "USB マイク", AudioSource.Mic, true), new("{mic-2}", "内蔵マイク", AudioSource.Mic, false)];

    public List<AudioDeviceInfo> Outputs { get; } = [new("{out-1}", "ヘッドホン", AudioSource.Loopback, true)];

    /// <summary>Thrown by <see cref="CreateSource"/> for that source.</summary>
    public Dictionary<AudioSource, Exception> CreateErrors { get; } = [];

    /// <summary>Thrown by <see cref="FakeCaptureSource.StartCapture"/> of new sources.</summary>
    public Dictionary<AudioSource, Exception> StartErrors { get; } = [];

    public List<(AudioSource Source, string DeviceId, MicProcessingMode Mode)> Created { get; } = [];

    public List<FakeCaptureSource> Sources { get; } = [];

    public FakeCaptureSource Latest(AudioSource source) => Sources.Last(s => s.Source == source);

    public event EventHandler<AudioDeviceChangedEventArgs>? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioSource source) => source == AudioSource.Mic ? [.. Mics] : [.. Outputs];

    public AudioDeviceInfo? GetDefault(AudioSource source) => GetDevices(source).FirstOrDefault(d => d.IsDefault);

    public IAudioCaptureSource CreateSource(AudioSource source, string deviceId, MicProcessingMode micMode)
    {
        if (CreateErrors.TryGetValue(source, out var error)) throw error;
        Created.Add((source, deviceId, micMode));
        var name = GetDevices(source).Single(d => d.Id == deviceId).Name;
        var fake = new FakeCaptureSource(source, source == AudioSource.Mic ? MicFormat : LoopbackFormat, name)
        {
            ThrowOnStart = StartErrors.GetValueOrDefault(source),
            DeviceId = deviceId,
        };
        Sources.Add(fake);
        return fake;
    }

    public void RaiseChanged() => DevicesChanged?.Invoke(this, new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.Added, "{new}"));

    public void Dispose() { }
}

public sealed class FakeDialogs : IDialogService
{
    public CloseChoice CloseAnswer { get; set; } = CloseChoice.Back;

    public ResumeChoice ResumeAnswer { get; set; } = ResumeChoice.Dismiss;

    public int CloseQuestions { get; private set; }

    public int ResumeQuestions { get; private set; }

    public List<string> Errors { get; } = [];

    public List<string> Infos { get; } = [];

    public CloseChoice AskStopBeforeClose()
    {
        CloseQuestions++;
        return CloseAnswer;
    }

    public ResumeChoice AskAfterSuspend()
    {
        ResumeQuestions++;
        return ResumeAnswer;
    }

    public void ShowError(string title, string message) => Errors.Add($"{title}: {message}");

    public void ShowInfo(string title, string message) => Infos.Add($"{title}: {message}");

    /// <summary>The file a picker returns (null: cancelled).</summary>
    public string? PickedFile { get; set; }

    public string? PickFile(string title, string filter) => PickedFile;

    public string? PickedFolder { get; set; }

    public string? PickFolder(string title) => PickedFolder;

    public bool ConfirmAnswer { get; set; }

    public List<string> Confirmations { get; } = [];

    public bool Confirm(string title, string message)
    {
        Confirmations.Add($"{title}: {message}");
        return ConfirmAnswer;
    }
}

public sealed class FakePower : IPowerService
{
    private int _active;

    public int ActiveRequests => Volatile.Read(ref _active);

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public IDisposable PreventSleep()
    {
        Interlocked.Increment(ref _active);
        return new Release(this);
    }

    public void RaiseSuspending() => Suspending?.Invoke(this, EventArgs.Empty);

    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);

    private sealed class Release(FakePower owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._active);
        }
    }
}

public sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = [];

    public void OpenFolder(string path) => Opened.Add(path);
}

public sealed class FakePlayer : IAudioPlayer
{
    public IReadOnlyList<string>? LastPlayed { get; private set; }

    public TimeSpan LastStart { get; private set; }

    public TimeSpan? LastDuration { get; private set; }

    public bool IsPlaying { get; private set; }

    public event EventHandler<PlaybackEndedEventArgs>? Ended;

    public string Play(IReadOnlyList<string> wavFiles, TimeSpan startInFirstFile = default, TimeSpan? duration = null)
    {
        LastPlayed = wavFiles;
        LastStart = startInFirstFile;
        LastDuration = duration;
        IsPlaying = true;
        return "テスト用スピーカー";
    }

    public void StopPlayback()
    {
        IsPlaying = false;
        Ended?.Invoke(this, new PlaybackEndedEventArgs(null));
    }

    public void Dispose() { }
}
