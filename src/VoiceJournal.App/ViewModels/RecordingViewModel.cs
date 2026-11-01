using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.ViewModels;

public enum RecorderStatus
{
    Idle,
    Preparing,
    Recording,
    PartiallyStopped,
    Stopping,
    Saved,
    Failed,
}

/// <summary>
/// UI-02 recording screen (detail spec 18.3). Owns at most one <see cref="RecordingSession"/> at a time.
/// All members are used on the UI thread; session events arrive on other threads and are posted back.
/// </summary>
public sealed partial class RecordingViewModel : ObservableObject, IDisposable
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SilenceHintAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FreeSpaceInterval = TimeSpan.FromSeconds(10);
    private const float SilentPeak = 1e-5f; // below -100 dBFS: digital silence (muted or blocked)

    private readonly IAudioDeviceCatalog _catalog;
    private readonly ISessionIndex _index;
    private readonly IFreeSpaceProbe _disk;
    private readonly IPowerService _power;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly RecordingEnvironment _environment;
    private readonly Lock _completionGate = new();

    private RecordingSession? _session;
    private SessionIndexUpdater? _updater;
    private readonly List<IAudioCaptureSource> _sources = [];
    private IDisposable? _sleep;
    private ITimer? _ticker;
    private Task? _completion;
    private TimeSpan _lastMicSound;
    private bool _silenceHintShown;
    private long _lastFreeSpaceCheck;
    private volatile bool _stoppedBySuspend;

    public RecordingViewModel(
        IAudioDeviceCatalog catalog,
        ISessionIndex index,
        IFreeSpaceProbe disk,
        IPowerService power,
        IDialogService dialogs,
        IShellService shell,
        IUiDispatcher ui,
        TimeProvider time,
        RecordingEnvironment environment)
    {
        _catalog = catalog;
        _index = index;
        _disk = disk;
        _power = power;
        _dialogs = dialogs;
        _shell = shell;
        _ui = ui;
        _time = time;
        _environment = environment;
        _catalog.DevicesChanged += (_, _) => _ui.Post(() => { if (!IsActive) RefreshDevices(); });
        _power.Suspending += (_, _) => OnSuspending();
        _power.Resumed += (_, _) => _ui.Post(OnResumed);
        RefreshDevices();
        UpdateFreeSpace();
    }

    /// <summary>Raised on the UI thread after a recording was saved (or failed) and indexed.</summary>
    public event EventHandler? RecordingCompleted;

    /// <summary>
    /// Raised once per recording on the UI thread, when audio first flows ("準備中" → "録音中"). The window
    /// switches to the mini bar then (the user's choice, 2026-11-01); not on start failures or reconnects.
    /// </summary>
    public event EventHandler? RecordingStarted;

    // ---- settings (fixed while recording) -------------------------------------------------

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Project { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMicMode), nameof(IsLoopbackMode), nameof(IsBothMode), nameof(NeedsMic), nameof(NeedsLoopback))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ToggleCommand))]
    public partial CaptureMode Mode { get; set; } = CaptureMode.Both;

    public bool IsMicMode
    {
        get => Mode == CaptureMode.Mic;
        set { if (value) Mode = CaptureMode.Mic; }
    }

    public bool IsLoopbackMode
    {
        get => Mode == CaptureMode.Loopback;
        set { if (value) Mode = CaptureMode.Loopback; }
    }

    public bool IsBothMode
    {
        get => Mode == CaptureMode.Both;
        set { if (value) Mode = CaptureMode.Both; }
    }

    public bool NeedsMic => Mode != CaptureMode.Loopback;

    public bool NeedsLoopback => Mode != CaptureMode.Mic;

    public ObservableCollection<AudioDeviceInfo> MicDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> LoopbackDevices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ToggleCommand))]
    public partial AudioDeviceInfo? SelectedMic { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ToggleCommand))]
    public partial AudioDeviceInfo? SelectedLoopback { get; set; }

    /// <summary>Raw capture (no noise suppression / AGC) is the default (Phase A: half the CER).</summary>
    [ObservableProperty]
    public partial bool UseRawMic { get; set; } = true;

    public string LoopbackNotice { get; } = UserMessages.LoopbackNotice;

    public string SaveLocation => Path.Combine(_environment.WorkspaceRoot, "sessions");

    // ---- live state ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShortStatusText), nameof(IsActive), nameof(CanEditSettings), nameof(IndicatorText), nameof(ToggleLabel))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ToggleCommand))]
    public partial RecorderStatus Status { get; set; } = RecorderStatus.Idle;

    /// <summary>"保存後に文字起こし" (detail spec 18.3). Read by the transcription coordinator when a recording is saved.</summary>
    [ObservableProperty]
    public partial bool TranscribeAfterSaving { get; set; } = true;

    public string StatusText => UserMessages.StatusText(Status);

    /// <summary>One or two words for the mini bar.</summary>
    public string ShortStatusText => Status switch
    {
        RecorderStatus.Preparing => "準備中",
        RecorderStatus.Recording => "録音中",
        RecorderStatus.PartiallyStopped => "一部の音源が停止中",
        RecorderStatus.Stopping => "保存中…",
        RecorderStatus.Saved => "保存しました",
        RecorderStatus.Failed => "録音できませんでした",
        _ => string.Empty,
    };

    /// <summary>A recording exists (preparing, recording, partially stopped or stopping).</summary>
    public bool IsActive => Status is RecorderStatus.Preparing or RecorderStatus.Recording or RecorderStatus.PartiallyStopped or RecorderStatus.Stopping;

    public bool CanEditSettings => !IsActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText), nameof(IndicatorText))]
    public partial TimeSpan Elapsed { get; set; }

    public string ElapsedText => UserMessages.Clock(Elapsed);

    /// <summary>Always-visible indicator for the window header (text, not only color).</summary>
    public string IndicatorText => Status switch
    {
        RecorderStatus.Preparing => "● 準備中",
        RecorderStatus.Recording => $"● 録音中 {ElapsedText}",
        RecorderStatus.PartiallyStopped => $"● 録音中（一部停止） {ElapsedText}",
        RecorderStatus.Stopping => "● 保存中",
        _ => string.Empty,
    };

    /// <summary>Incremented on every meter update, so the level history scrolls even through silence.</summary>
    [ObservableProperty]
    public partial long MeterTick { get; set; }

    [ObservableProperty]
    public partial double MicLevel { get; set; }

    [ObservableProperty]
    public partial double LoopbackLevel { get; set; }

    [ObservableProperty]
    public partial string MicLevelText { get; set; } = "—";

    [ObservableProperty]
    public partial string LoopbackLevelText { get; set; } = "—";

    [ObservableProperty]
    public partial string FreeSpaceText { get; set; } = string.Empty;

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenLastSavedCommand))]
    public partial string? LastSavedDirectory { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    public partial bool HasDisconnectedSource { get; set; }

    // ---- commands ----------------------------------------------------------------------------

    private bool CanStart() =>
        !IsActive && (!NeedsMic || SelectedMic is not null) && (!NeedsLoopback || SelectedLoopback is not null);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Warnings.Clear();
        LastSavedDirectory = null;
        HasDisconnectedSource = false;
        _stoppedBySuspend = false;
        _silenceHintShown = false;
        _lastMicSound = TimeSpan.Zero;

        var available = SafeAvailableBytes();
        if (available is { } free && free < _environment.MinFreeBytes)
        {
            Status = RecorderStatus.Failed;
            _dialogs.ShowError("録音を開始できません", UserMessages.NotEnoughSpace(free, _environment.MinFreeBytes));
            return;
        }

        Status = RecorderStatus.Preparing;
        try
        {
            foreach (var source in RequiredSources(Mode))
            {
                var device = source == AudioSource.Mic ? SelectedMic! : SelectedLoopback!;
                _sources.Add(_catalog.CreateSource(source, device.Id, UseRawMic ? MicProcessingMode.Raw : MicProcessingMode.Normal));
            }
            var options = new RecordingOptions
            {
                Mode = Mode,
                MicMode = UseRawMic ? MicProcessingMode.Raw : MicProcessingMode.Normal,
                Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
                Project = string.IsNullOrWhiteSpace(Project) ? null : Project.Trim(),
                MinFreeBytes = _environment.MinFreeBytes,
            };
            var session = RecordingSession.Create(_environment.WorkspaceRoot, options, _sources, _time, _disk);
            _session = session;
            session.StateChanged += OnSessionStateChanged;
            session.Warning += OnSessionWarning;
            _updater = new SessionIndexUpdater(_index, session);
            _sleep = TryPreventSleep();
            session.Start();
        }
        catch (Exception ex) when (ex is AudioDeviceException or CaptureStartException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("録音を開始できません", UserMessages.StartFailure(ex));
            await CompleteAsync(startFailed: true);
            return;
        }

        _lastFreeSpaceCheck = _time.GetTimestamp();
        _ticker = _time.CreateTimer(_ => _ui.Post(Tick), null, TickInterval, TickInterval);
    }

    private bool CanStop() => IsActive && Status != RecorderStatus.Stopping;

    private bool CanToggle() => IsActive ? CanStop() : CanStart();

    /// <summary>The big record button: starts when idle, stops and saves while recording.</summary>
    [RelayCommand(CanExecute = nameof(CanToggle))]
    private Task ToggleAsync() => IsActive ? StopAsync() : StartAsync();

    /// <summary>Name of the big button for screen readers and its tooltip.</summary>
    public string ToggleLabel => IsActive ? "停止して保存" : "録音開始";

    /// <summary>Stops, finalizes and indexes the recording. Safe to call more than once.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    public async Task StopAsync()
    {
        var session = _session;
        if (session is null) return;
        Status = RecorderStatus.Stopping;
        await session.StopAsync();
        await EnsureCompletedAsync();
    }

    [RelayCommand(CanExecute = nameof(HasDisconnectedSource))]
    private void Reconnect()
    {
        if (_session is not { } session) return;
        foreach (var track in session.GetTracks().Where(t => t.State == TrackState.Disconnected))
        {
            if (!_catalog.GetDevices(track.Source).Any(d => d.Id == track.DeviceId))
            {
                Warnings.Add($"{UserMessages.SourceName(track.Source)}（{track.DeviceName}）はまだ接続されていません。接続してから、もう一度［再接続］を押してください。");
                continue;
            }
            IAudioCaptureSource? source = null;
            try
            {
                source = _catalog.CreateSource(track.Source, track.DeviceId, UseRawMic ? MicProcessingMode.Raw : MicProcessingMode.Normal);
                session.Reconnect(source);
                _sources.Add(source);
                Warnings.Add($"{UserMessages.Clock(session.Elapsed)} に{UserMessages.SourceName(track.Source)}を再接続しました。ここから新しいチャンクとして録音を続けます。");
            }
            catch (Exception ex) when (ex is AudioDeviceException or InvalidOperationException)
            {
                source?.Dispose();
                Warnings.Add($"{UserMessages.SourceName(track.Source)}を再接続できませんでした: {UserMessages.StartFailure(ex)}");
            }
        }
        HasDisconnectedSource = session.GetTracks().Any(t => t.State == TrackState.Disconnected);
    }

    [RelayCommand]
    public void RefreshDevices()
    {
        Replace(MicDevices, SafeDevices(AudioSource.Mic));
        Replace(LoopbackDevices, SafeDevices(AudioSource.Loopback));
        SelectedMic = Pick(MicDevices, SelectedMic);
        SelectedLoopback = Pick(LoopbackDevices, SelectedLoopback);
    }

    private bool CanOpenLastSaved() => LastSavedDirectory is not null;

    [RelayCommand(CanExecute = nameof(CanOpenLastSaved))]
    private void OpenLastSaved()
    {
        try { _shell.OpenFolder(LastSavedDirectory!); }
        catch (IOException ex) { _dialogs.ShowError("フォルダーを開けません", ex.Message); }
    }

    /// <summary>Window closing (detail spec 18.1): true when the window may close.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsActive) return true;
        if (_dialogs.AskStopBeforeClose() == CloseChoice.Back) return false;
        await StopAsync();
        return true;
    }

    /// <summary>
    /// App exit, including Windows sign-out: stops and saves a running recording without going through the UI.
    /// Blocking is safe here because Core and the index updater never resume on the UI thread.
    /// </summary>
    public void Dispose()
    {
        _ticker?.Dispose();
        _ticker = null;
        var session = _session;
        if (session is not null)
        {
            session.StopAsync().Wait(TimeSpan.FromSeconds(15));
            if (_updater is { } updater)
            {
                try
                {
                    updater.FlushAsync().Wait(TimeSpan.FromSeconds(5));
                }
#pragma warning disable CA1031 // Exiting: the index is rebuilt from the manifest at the next start.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
                updater.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            _session = null;
            _updater = null;
        }
        foreach (var source in _sources) source.Dispose();
        _sources.Clear();
        _sleep?.Dispose();
        _sleep = null;
    }

    // ---- internals -------------------------------------------------------------------------

    internal static IReadOnlyList<AudioSource> RequiredSources(CaptureMode mode) => mode switch
    {
        CaptureMode.Mic => [AudioSource.Mic],
        CaptureMode.Loopback => [AudioSource.Loopback],
        _ => [AudioSource.Mic, AudioSource.Loopback],
    };

    /// <summary>Peak (0..1) to a meter position: -60 dBFS..0 dBFS → 0..1 (raw mics are quiet, so the scale is in dB).</summary>
    internal static double ToMeter(float peak) => peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);

    private static string LevelText(float peak) => peak < SilentPeak ? "無音" : $"{20 * Math.Log10(peak):0} dBFS";

    /// <summary>Test seam: the live session (to throttle fake sources).</summary>
    internal RecordingSession? SessionForTesting => _session;

    /// <summary>Refreshes elapsed time, levels and free space. Runs every <see cref="TickInterval"/> on the UI thread.</summary>
    internal void Tick()
    {
        if (_session is not { } session) return;
        Elapsed = session.Elapsed;
        if (Status == RecorderStatus.Preparing && session.IsAudioFlowing)
        {
            Status = RecorderStatus.Recording;
            _lastMicSound = Elapsed; // silence is counted from when audio started to flow
            RecordingStarted?.Invoke(this, EventArgs.Empty);
        }

        if (NeedsMic)
        {
            var peak = session.ReadPeak(AudioSource.Mic);
            MicLevel = ToMeter(peak);
            MicLevelText = LevelText(peak);
            if (peak >= SilentPeak || Status != RecorderStatus.Recording) _lastMicSound = Elapsed;
            else if (!_silenceHintShown && Elapsed - _lastMicSound >= SilenceHintAfter)
            {
                _silenceHintShown = true; // guidance only: no automatic stop or device switch (detail spec 18.3)
                Warnings.Add(UserMessages.MicSilent(Elapsed));
            }
        }
        if (NeedsLoopback)
        {
            var peak = session.ReadPeak(AudioSource.Loopback);
            LoopbackLevel = ToMeter(peak);
            LoopbackLevelText = LevelText(peak);
        }
        MeterTick++;
        if (_time.GetElapsedTime(_lastFreeSpaceCheck) >= FreeSpaceInterval)
        {
            _lastFreeSpaceCheck = _time.GetTimestamp();
            UpdateFreeSpace();
        }
    }

    // Raised under the session lock: only post.
    private void OnSessionStateChanged(object? sender, CaptureState state) => _ui.Post(() => OnStateChanged(state));

    private void OnStateChanged(CaptureState state)
    {
        if (_session is null) return;
        switch (state)
        {
            case CaptureState.Recording when Status == RecorderStatus.PartiallyStopped:
                Status = RecorderStatus.Recording;
                break;
            case CaptureState.Partial:
                Status = RecorderStatus.PartiallyStopped;
                break;
            case CaptureState.Stopping:
                Status = RecorderStatus.Stopping;
                break;
            case CaptureState.Finalized or CaptureState.Failed:
                _ = EnsureCompletedAsync(); // stopped by itself (overflow, disk, write error) or by the user
                break;
        }
    }

    private void OnSessionWarning(object? sender, RecordingWarningEventArgs e)
    {
        var session = (RecordingSession)sender!;
        var at = session.Elapsed;
        var device = session.GetTracks().FirstOrDefault(t => t.Source == e.Source)?.DeviceName;
        _ui.Post(() =>
        {
            Warnings.Add(UserMessages.Warning(e, at, device));
            if (e.Kind == RecordingWarningKind.DeviceLost) HasDisconnectedSource = true;
        });
    }

    private Task EnsureCompletedAsync()
    {
        lock (_completionGate) return _completion ??= CompleteAsync(startFailed: false);
    }

    private async Task CompleteAsync(bool startFailed)
    {
        _ticker?.Dispose();
        _ticker = null;
        var session = _session;
        var finalState = CaptureState.Failed;
        if (session is not null)
        {
            if (!startFailed) await session.StopAsync();
            finalState = session.State;
            if (_updater is { } updater)
            {
                try
                {
                    await updater.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
#pragma warning disable CA1031 // The recording is safe on disk; the index is rebuilt at the next start.
                catch (Exception)
#pragma warning restore CA1031
                {
                    Warnings.Add(UserMessages.IndexFailed);
                }
                await updater.DisposeAsync();
            }
            session.StateChanged -= OnSessionStateChanged;
            session.Warning -= OnSessionWarning;
            await session.DisposeAsync();
            LastSavedDirectory = session.Directory;
        }
        foreach (var source in _sources) source.Dispose();
        _sources.Clear();
        _sleep?.Dispose();
        _sleep = null;
        _updater = null;
        _session = null;
        lock (_completionGate) _completion = null;

        HasDisconnectedSource = false;
        MicLevel = LoopbackLevel = 0;
        MicLevelText = LoopbackLevelText = "—";
        Status = !startFailed && finalState == CaptureState.Finalized ? RecorderStatus.Saved : RecorderStatus.Failed;
        UpdateFreeSpace();
        if (!startFailed || session is not null) RecordingCompleted?.Invoke(this, EventArgs.Empty);
    }

    // Raised on a system thread right before suspend: mark and stop without blocking it.
    private void OnSuspending()
    {
        var session = _session;
        if (session is null) return;
        _stoppedBySuspend = true;
        session.MarkInterruption("system_suspend");
        _ = session.StopAsync();
    }

    private async void OnResumed()
    {
        if (!_stoppedBySuspend) return;
        _stoppedBySuspend = false;
        if (_completion is { } completion) await completion;
        if (_dialogs.AskAfterSuspend() == ResumeChoice.StartNew && StartCommand.CanExecute(null)) await StartCommand.ExecuteAsync(null);
    }

    private IDisposable? TryPreventSleep()
    {
        try
        {
            return _power.PreventSleep();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Warnings.Add("スリープの抑止を設定できませんでした。録音中は PC がスリープしないよう注意してください。");
            return null;
        }
    }

    private void UpdateFreeSpace()
    {
        FreeSpaceText = SafeAvailableBytes() is { } free ? $"空き容量 {UserMessages.Bytes(free)}" : "空き容量 不明";
    }

    private long? SafeAvailableBytes()
    {
        try
        {
            Directory.CreateDirectory(_environment.WorkspaceRoot);
            return _disk.GetAvailableBytes(_environment.WorkspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IReadOnlyList<AudioDeviceInfo> SafeDevices(AudioSource source)
    {
        try { return _catalog.GetDevices(source); }
        catch (Exception ex) when (ex is AudioDeviceException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    private static void Replace(ObservableCollection<AudioDeviceInfo> target, IReadOnlyList<AudioDeviceInfo> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    // Keep the user's choice when the device is still there; otherwise the Windows default (listed first).
    private static AudioDeviceInfo? Pick(ObservableCollection<AudioDeviceInfo> devices, AudioDeviceInfo? current) =>
        devices.FirstOrDefault(d => d.Id == current?.Id) ?? devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
}
