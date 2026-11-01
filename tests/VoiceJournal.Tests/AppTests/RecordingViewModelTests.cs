using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.TestSupport;
using CaptureState = VoiceJournal.Core.Capture.CaptureState;

namespace VoiceJournal.Tests.AppTests;

public sealed class RecordingViewModelTests : IAsyncDisposable
{
    private readonly TempDirectory _dir = new("vj-vm-");
    private readonly ManualTimeProvider _clock = new();
    private readonly FakeDeviceCatalog _catalog = new();
    private readonly FakeFreeSpace _disk = new(100L * 1024 * 1024 * 1024);
    private readonly FakePower _power = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeShell _shell = new();
    private readonly QueueDispatcher _ui = new();
    private readonly Workspace _workspace;
    private readonly SessionIndex _index;
    private readonly RecordingViewModel _vm;

    public RecordingViewModelTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        _index = new SessionIndex(_workspace, VoiceJournalDatabase.Open(_workspace.DatabasePath), _clock);
        _vm = new RecordingViewModel(_catalog, _index, _disk, _power, _dialogs, _shell, _ui, _clock,
            new RecordingEnvironment(_workspace.Root, MinFreeBytes: 2L * 1024 * 1024 * 1024));
    }

    public ValueTask DisposeAsync()
    {
        _vm.Dispose();
        _ui.Drain();
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Defaults_FollowTheSpec()
    {
        Assert.Equal(CaptureMode.Both, _vm.Mode);
        Assert.True(_vm.UseRawMic); // Phase A: raw mode halves the CER
        Assert.Equal("{mic-1}", _vm.SelectedMic?.Id); // the Windows default, resolved to a fixed id
        Assert.Equal("{out-1}", _vm.SelectedLoopback?.Id);
        Assert.Equal(RecorderStatus.Idle, _vm.Status);
        Assert.True(_vm.StartCommand.CanExecute(null));
        Assert.False(_vm.StopCommand.CanExecute(null));
        Assert.Equal("空き容量 100.0 GB", _vm.FreeSpaceText);
        Assert.Equal(string.Empty, _vm.IndicatorText);
    }

    [Fact]
    public async Task StartShowsPreparingUntilAudioArrives_ThenRecording_AndStopSavesAndIndexes() // AT-01 (UI part)
    {
        _vm.Title = " 定例会議 ";
        _vm.Project = "VJ";
        var completed = 0;
        _vm.RecordingCompleted += (_, _) => completed++;

        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        Assert.Equal(RecorderStatus.Preparing, _vm.Status); // not "録音中" before the first packet (18.3)
        Assert.Equal("● 準備中", _vm.IndicatorText);
        Assert.False(_vm.CanEditSettings);
        Assert.Equal(1, _power.ActiveRequests); // no idle sleep while recording
        Assert.Equal([(AudioSource.Mic, "{mic-1}", MicProcessingMode.Raw), (AudioSource.Loopback, "{out-1}", MicProcessingMode.Raw)], _catalog.Created);

        _vm.Tick();
        Assert.Equal(RecorderStatus.Preparing, _vm.Status);
        _catalog.Latest(AudioSource.Mic).PushFor(_clock, TimeSpan.FromSeconds(2));
        _vm.Tick();
        Assert.Equal(RecorderStatus.Recording, _vm.Status);
        Assert.Equal("00:00:02", _vm.ElapsedText);
        Assert.Equal("● 録音中 00:00:02", _vm.IndicatorText);
        Assert.True(_vm.MicLevel > 0);
        Assert.EndsWith("dBFS", _vm.MicLevelText, StringComparison.Ordinal);

        await _vm.StopCommand.ExecuteAsync(null);
        _ui.Drain();

        Assert.Equal(RecorderStatus.Saved, _vm.Status);
        Assert.Equal(0, _power.ActiveRequests);
        Assert.Equal(1, completed);
        Assert.True(_vm.CanEditSettings);
        var manifest = SessionManifest.Load(_vm.LastSavedDirectory!);
        Assert.Equal("定例会議", manifest.Title);
        Assert.Equal("VJ", manifest.Project);
        Assert.Equal(CaptureState.Finalized, manifest.CaptureState);
        var row = Assert.Single(_index.ListSessions());
        Assert.Equal(CaptureState.Finalized, row.CaptureState);
        Assert.All(_catalog.Sources, s => Assert.False(s.IsCapturing));

        _vm.OpenLastSavedCommand.Execute(null);
        Assert.Equal([_vm.LastSavedDirectory!], _shell.Opened);
    }

    [Fact]
    public async Task MicPermissionDenied_DoesNotStart_AndExplainsTheSetting() // CAP-001 / AT-04
    {
        _catalog.CreateErrors[AudioSource.Mic] = new AudioDeviceException(AudioDeviceErrorKind.AccessDenied, "denied");

        await _vm.StartCommand.ExecuteAsync(null);
        _ui.Drain();

        Assert.Equal(RecorderStatus.Failed, _vm.Status);
        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("CAP-001", error, StringComparison.Ordinal);
        Assert.Contains("プライバシーとセキュリティ", error, StringComparison.Ordinal);
        Assert.Equal(0, _power.ActiveRequests);
        Assert.Empty(Directory.GetDirectories(_workspace.SessionsRoot));
        Assert.True(_vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task RawModeUnsupported_SuggestsNormalMode()
    {
        _catalog.StartErrors[AudioSource.Mic] = new AudioDeviceException(AudioDeviceErrorKind.RawModeUnsupported, "raw");

        await _vm.StartCommand.ExecuteAsync(null);
        _ui.Drain();

        Assert.Equal(RecorderStatus.Failed, _vm.Status);
        Assert.Contains("チェックを外して", Assert.Single(_dialogs.Errors), StringComparison.Ordinal);
        Assert.All(_catalog.Sources, s => Assert.False(s.IsCapturing)); // both mode: loopback was not left running (AT-04)
        Assert.Equal(0, _power.ActiveRequests);
        Assert.Equal(CaptureState.Failed, SessionManifest.Load(Directory.GetDirectories(_workspace.SessionsRoot, "*", SearchOption.AllDirectories).Single(d => File.Exists(SessionManifest.PathIn(d)))).CaptureState);
    }

    [Fact]
    public async Task NotEnoughSpace_DoesNotStart() // FS-001
    {
        _disk.AvailableBytes = 500L * 1024 * 1024;

        await _vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(RecorderStatus.Failed, _vm.Status);
        Assert.Contains("FS-001", Assert.Single(_dialogs.Errors), StringComparison.Ordinal);
        Assert.Empty(_catalog.Created);
    }

    [Fact]
    public async Task DeviceLost_KeepsTheOtherSource_AndReconnectResumes() // CAP-002
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        PushBoth(TimeSpan.FromSeconds(2));
        _vm.Tick();

        _catalog.Latest(AudioSource.Mic).Fail(new AudioDeviceException(AudioDeviceErrorKind.DeviceInvalidated, "gone"));
        _ui.Drain();
        Assert.Equal(RecorderStatus.PartiallyStopped, _vm.Status);
        Assert.True(_vm.HasDisconnectedSource);
        Assert.Contains(_vm.Warnings, w => w.Contains("CAP-002", StringComparison.Ordinal) && w.Contains("USB マイク", StringComparison.Ordinal));
        Assert.Contains("一部停止", _vm.IndicatorText, StringComparison.Ordinal);

        _catalog.Latest(AudioSource.Loopback).PushFor(_clock, TimeSpan.FromSeconds(2));
        _vm.ReconnectCommand.Execute(null);
        Throttle();
        _ui.Drain();
        Assert.Equal(RecorderStatus.Recording, _vm.Status);
        Assert.False(_vm.HasDisconnectedSource);
        Assert.Equal(2, _catalog.Sources.Count(s => s.Source == AudioSource.Mic));
        PushBoth(TimeSpan.FromSeconds(1));

        await _vm.StopCommand.ExecuteAsync(null);
        _ui.Drain();
        Assert.Equal(RecorderStatus.Saved, _vm.Status);
        var mic = SessionManifest.Load(_vm.LastSavedDirectory!).Tracks.Single(t => t.Source == AudioSource.Mic);
        Assert.Equal(2, mic.Chunks.Count);
        Assert.True(Assert.Single(_index.ListSessions()).HasAudioLoss);
    }

    [Fact]
    public async Task Reconnect_WaitsUntilTheDeviceIsBack()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        PushBoth(TimeSpan.FromSeconds(1));
        _catalog.Latest(AudioSource.Mic).Fail(new AudioDeviceException(AudioDeviceErrorKind.DeviceInvalidated, "gone"));
        _ui.Drain();
        var mic = _catalog.Mics[0];
        _catalog.Mics.RemoveAt(0); // still unplugged

        _vm.ReconnectCommand.Execute(null);

        Assert.True(_vm.HasDisconnectedSource);
        Assert.Contains(_vm.Warnings, w => w.Contains("まだ接続されていません", StringComparison.Ordinal));
        _catalog.Mics.Insert(0, mic);
    }

    [Fact]
    public async Task MicSilentForTenSeconds_ShowsGuidanceOnce_WithoutStopping() // detail spec 18.3
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        var mic = _catalog.Latest(AudioSource.Mic);
        mic.PushFor(_clock, TimeSpan.FromSeconds(1), value: 0);
        _vm.Tick();
        Assert.Equal(RecorderStatus.Recording, _vm.Status);
        Assert.Equal("無音", _vm.MicLevelText);

        mic.PushFor(_clock, TimeSpan.FromSeconds(9.5), value: 0);
        _vm.Tick();
        Assert.Empty(_vm.Warnings);
        mic.PushFor(_clock, TimeSpan.FromSeconds(1), value: 0);
        _vm.Tick();
        _vm.Tick();
        Assert.Single(_vm.Warnings, w => w.Contains("10 秒間", StringComparison.Ordinal));
        Assert.Equal(RecorderStatus.Recording, _vm.Status); // no automatic stop
    }

    [Fact]
    public async Task DiskFillingUp_StopsAndSaves_WithAWarning() // FS-001 while recording
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        PushBoth(TimeSpan.FromSeconds(2));
        _disk.AvailableBytes = 100L * 1024 * 1024;
        PushBoth(TimeSpan.FromSeconds(10)); // the writer re-checks free space every 10 s of audio

        await WaitUntil(() => _vm.Status == RecorderStatus.Saved);
        Assert.Contains(_vm.Warnings, w => w.Contains("FS-001", StringComparison.Ordinal));
        Assert.Equal(0, _power.ActiveRequests);
    }

    [Fact]
    public async Task Closing_AsksWhileRecording()
    {
        Assert.True(await _vm.ConfirmCloseAsync()); // idle: no question
        Assert.Equal(0, _dialogs.CloseQuestions);

        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        PushBoth(TimeSpan.FromSeconds(1));

        _dialogs.CloseAnswer = CloseChoice.Back;
        Assert.False(await _vm.ConfirmCloseAsync());
        Assert.True(_vm.IsActive);

        _dialogs.CloseAnswer = CloseChoice.StopAndSave;
        Assert.True(await _vm.ConfirmCloseAsync());
        _ui.Drain();
        Assert.Equal(RecorderStatus.Saved, _vm.Status);
        Assert.Equal(2, _dialogs.CloseQuestions);
    }

    [Fact]
    public async Task AppExitWhileRecording_StopsAndSaves() // e.g. Windows sign-out
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        PushBoth(TimeSpan.FromSeconds(2));
        var directory = _vm.SessionForTesting!.Directory;

        _vm.Dispose();

        Assert.Equal(CaptureState.Finalized, SessionManifest.Load(directory).CaptureState);
        Assert.Equal(0, _power.ActiveRequests);
        Assert.All(_catalog.Sources, s => Assert.False(s.IsCapturing));
    }

    [Fact]
    public async Task Suspend_StopsAndSaves_ThenAsksOnResume()
    {
        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        PushBoth(TimeSpan.FromSeconds(3));

        _power.RaiseSuspending();
        await WaitUntil(() => _vm.Status == RecorderStatus.Saved);
        var manifest = SessionManifest.Load(_vm.LastSavedDirectory!);
        Assert.Equal(CaptureState.Finalized, manifest.CaptureState);
        Assert.All(manifest.Tracks, t => Assert.Contains(t.Events, e => e.Kind == AudioEventKind.Interrupted && e.Detail == "system_suspend"));

        _dialogs.ResumeAnswer = ResumeChoice.StartNew;
        _power.RaiseResumed();
        await WaitUntil(() => _vm.Status == RecorderStatus.Preparing);
        Assert.Equal(1, _dialogs.ResumeQuestions);
    }

    [Fact]
    public async Task Settings_AreFixedWhileRecording_AndModeControlsTheSources()
    {
        _vm.IsMicMode = true;
        Assert.False(_vm.NeedsLoopback);
        await _vm.StartCommand.ExecuteAsync(null);
        Throttle();
        Assert.Equal([AudioSource.Mic], _catalog.Created.Select(c => c.Source));
        Assert.False(_vm.CanEditSettings);
        Assert.False(_vm.StartCommand.CanExecute(null));
        Assert.True(_vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public void DeviceListChanges_KeepTheSelection()
    {
        _vm.SelectedMic = _vm.MicDevices[1];
        _catalog.Mics.Add(new AudioDeviceInfo("{mic-3}", "会議用マイク", AudioSource.Mic, false));
        _catalog.RaiseChanged();
        _ui.Drain();

        Assert.Equal(3, _vm.MicDevices.Count);
        Assert.Equal("{mic-2}", _vm.SelectedMic?.Id);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 1)]
    [InlineData(0.001f, 0)]     // -60 dBFS
    [InlineData(0.0316228f, 0.5)] // -30 dBFS
    public void Meter_IsScaledInDecibels(float peak, double expected)
    {
        Assert.Equal(expected, RecordingViewModel.ToMeter(peak), 3);
    }

    private void Throttle()
    {
        var session = _vm.SessionForTesting;
        if (session is null) return;
        foreach (var source in _catalog.Sources) source.AfterPush = () => session.ThrottleForTesting(source.Source);
    }

    private void PushBoth(TimeSpan duration)
    {
        var mic = _catalog.Latest(AudioSource.Mic);
        var loop = _catalog.Latest(AudioSource.Loopback);
        var p = TimeSpan.FromMilliseconds(10);
        var bufA = new byte[(int)(mic.Format.SampleRate * p.TotalSeconds) * mic.Format.BlockAlign];
        var bufB = new byte[(int)(loop.Format.SampleRate * p.TotalSeconds) * loop.Format.BlockAlign];
        Array.Fill<byte>(bufA, 0x11);
        Array.Fill<byte>(bufB, 0x11);
        for (var i = 0; i < (long)Math.Round(duration / p); i++)
        {
            _clock.Advance(p);
            mic.Push(bufA);
            loop.Push(bufB);
        }
    }

    private async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            _ui.Drain();
            if (condition()) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"condition not met (status {_vm.Status})");
            await Task.Delay(20);
        }
    }
}
