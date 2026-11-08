using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.TestSupport;
using CaptureState = VoiceJournal.Core.Capture.CaptureState;

namespace VoiceJournal.Tests.AppTests;

public sealed class LibraryAndStartupTests : IDisposable
{
    private static readonly AudioFormatInfo MicFormat = AudioFormatInfo.Pcm16Bit(8000, 1);
    private static readonly AudioFormatInfo LoopbackFormat = AudioFormatInfo.IeeeFloat32Bit(8000, 2);

    private readonly TempDirectory _dir = new("vj-lib-");
    private readonly ManualTimeProvider _clock = new();
    private readonly QueueDispatcher _ui = new();
    private readonly FakePlayer _player = new();
    private readonly FakeShell _shell = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Workspace _workspace;
    private readonly SessionIndex _index;
    private readonly LibraryViewModel _library;

    public LibraryAndStartupTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        _index = new SessionIndex(_workspace, VoiceJournalDatabase.Open(_workspace.DatabasePath), _clock);
        _library = new LibraryViewModel(_index, new RecordingEnvironment(_workspace.Root, 0), _player, _shell, _ui);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Library_ListsSessionsNewestFirst_WithLossWarnings_AndFilters()
    {
        await Record("週次定例", "VJ", CaptureMode.Both, TimeSpan.FromSeconds(75));
        _clock.Advance(TimeSpan.FromHours(2));
        await Record(null, null, CaptureMode.Mic, TimeSpan.FromSeconds(5), unplug: true);
        _index.Rebuild();

        await _library.RefreshAsync();

        Assert.Equal(2, _library.Sessions.Count);
        var newest = _library.Sessions[0];
        Assert.Equal("（無題）", newest.TitleText);
        Assert.Equal("⚠ 欠損あり", newest.LossText); // text, not only a color
        Assert.Equal("マイク", newest.ModeText);
        var older = _library.Sessions[1];
        Assert.Equal("週次定例", older.TitleText);
        Assert.Equal("1:15", older.DurationText);
        Assert.Equal("両方", older.ModeText);
        Assert.Equal("保存済み", older.StateText);
        Assert.Equal(string.Empty, older.LossText);
        Assert.Equal("2026/11/01 10:00", older.StartedText); // UTC 01:00 shown in the recording time zone (Tokyo)

        _library.SearchText = "定例";
        Assert.Equal([older], _library.Sessions);
        Assert.Equal("1 / 2 件", _library.CountText);
        _library.SearchText = "vj";
        Assert.Single(_library.Sessions);
        _library.SearchText = string.Empty;
        Assert.Equal(2, _library.Sessions.Count);
    }

    [Fact]
    public async Task Export_WritesTheRecordAndTranscript_IntoAFolderNamedAfterTheSession() // UI-04
    {
        var dir = await Record("週次定例", "VJ", CaptureMode.Mic, TimeSpan.FromSeconds(5));
        File.WriteAllText(Path.Combine(dir, "record.draft.md"), "# 草稿");
        File.WriteAllText(Path.Combine(dir, "transcript.md"), "# 文字起こし");
        _index.Rebuild();
        await _library.RefreshAsync();
        _library.Dialogs = _dialogs;
        _dialogs.PickedFolder = Directory.CreateDirectory(_dir.File("Exports")).FullName;
        _library.Selected = _library.Sessions.Single();

        _library.ExportCommand.Execute(null);

        var target = Assert.Single(Directory.GetDirectories(_dialogs.PickedFolder));
        Assert.Contains("週次定例", Path.GetFileName(target), StringComparison.Ordinal);
        Assert.Equal(["record.draft.md", "transcript.md"], Directory.GetFiles(target).Select(Path.GetFileName).Order());
        Assert.Contains("2 個のファイル", _library.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_AsksFirst_AndMovesTheSessionToTrash_WithoutErasingIt() // UI-04, 14.4
    {
        var dir = await Record("消す録音", null, CaptureMode.Mic, TimeSpan.FromSeconds(5));
        _index.Rebuild();
        await _library.RefreshAsync();
        _library.Dialogs = _dialogs;
        _library.ItemReferences = _ => 2;
        _library.Selected = _library.Sessions.Single();

        _dialogs.ConfirmAnswer = false;
        await _library.DeleteCommand.ExecuteAsync(null);
        Assert.True(Directory.Exists(dir)); // declined: nothing moves
        Assert.Contains("課題・タスク 2 件の根拠", Assert.Single(_dialogs.Confirmations), StringComparison.Ordinal);

        _dialogs.ConfirmAnswer = true;
        await _library.DeleteCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(Path.Combine(_workspace.Root, "trash", Path.GetFileName(dir))));
        Assert.Empty(_library.Sessions);
    }

    [Fact]
    public async Task Library_PlaysATrack_AndOpensTheFolder()
    {
        await Record("再生テスト", null, CaptureMode.Both, TimeSpan.FromSeconds(70), chunk: TimeSpan.FromSeconds(30));
        _index.Rebuild();
        await _library.RefreshAsync();
        _library.Selected = _library.Sessions[0];

        Assert.True(_library.PlayMicCommand.CanExecute(null));
        _library.PlayMicCommand.Execute(null);
        Assert.True(_library.IsPlaying);
        Assert.Equal(3, _player.LastPlayed!.Count); // 30 s chunks: 3 files in order
        Assert.All(_player.LastPlayed, f => Assert.Contains($"{Path.DirectorySeparatorChar}mic{Path.DirectorySeparatorChar}", f, StringComparison.Ordinal));
        Assert.Contains("再生テスト", _library.NowPlaying, StringComparison.Ordinal);
        Assert.EndsWith("→ 出力先: テスト用スピーカー", _library.NowPlaying, StringComparison.Ordinal); // where the sound goes

        _library.StopPlaybackCommand.Execute(null);
        _ui.Drain();
        Assert.False(_library.IsPlaying);

        _library.OpenFolderCommand.Execute(null);
        Assert.Equal([_library.Sessions[0].Directory], _shell.Opened);
    }

    [Fact]
    public async Task Library_MicOnlySession_CannotPlayLoopback()
    {
        await Record(null, null, CaptureMode.Mic, TimeSpan.FromSeconds(3));
        _index.Rebuild();
        await _library.RefreshAsync();
        _library.Selected = _library.Sessions[0];

        Assert.True(_library.PlayMicCommand.CanExecute(null));
        Assert.False(_library.PlayLoopbackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Startup_RecoversACrashedSession_AndTellsTheUser() // REC-001 / AT-19
    {
        await Record("無事な録音", null, CaptureMode.Mic, TimeSpan.FromSeconds(3));
        var crashedDir = await RecordAndCrash();

        var recording = new RecordingViewModel(new FakeDeviceCatalog(), _index, new FakeFreeSpace(long.MaxValue), new FakePower(), _dialogs, _shell, _ui, _clock,
            new RecordingEnvironment(_workspace.Root, 0));
        var main = new MainViewModel(recording, _library, _dialogs);
        await main.InitializeAsync(() => WorkspaceStartup.Run(_workspace, _index, _clock));

        var info = Assert.Single(_dialogs.Infos);
        Assert.Contains("1 件", info, StringComparison.Ordinal);
        Assert.Contains("最後の最大 5 分", info, StringComparison.Ordinal);
        Assert.Null(main.StartupMessage);
        Assert.Equal(2, _library.Sessions.Count);
        var recovered = _library.Sessions.Single(s => s.Directory == crashedDir);
        Assert.Equal("中断（回復済み）", recovered.StateText);
        Assert.Equal("⚠ 末尾に欠損の可能性", recovered.LossText); // AT-19: the loss is shown
        Assert.Equal(CaptureState.Interrupted, SessionManifest.Load(crashedDir).CaptureState);
        Assert.NotEmpty(Directory.GetFiles(crashedDir, "*.recovered.wav", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(crashedDir, "*.partial", SearchOption.AllDirectories)); // originals kept

        // A second start finds nothing more to recover.
        _dialogs.Infos.Clear();
        await main.InitializeAsync(() => WorkspaceStartup.Run(_workspace, _index, _clock));
        Assert.Empty(_dialogs.Infos);
    }

    [Fact]
    public async Task Startup_ReportsUnreadableSessions_InTheLibrary()
    {
        var broken = Path.Combine(_workspace.SessionsRoot, "2026", "11", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(broken);
        File.WriteAllText(SessionManifest.PathIn(broken), "{");
        var main = new MainViewModel(
            new RecordingViewModel(new FakeDeviceCatalog(), _index, new FakeFreeSpace(long.MaxValue), new FakePower(), _dialogs, _shell, _ui, _clock, new RecordingEnvironment(_workspace.Root, 0)),
            _library, _dialogs);

        await main.InitializeAsync(() => WorkspaceStartup.Run(_workspace, _index, _clock));

        Assert.Contains("1 件", _library.Message, StringComparison.Ordinal);
        Assert.Empty(_dialogs.Errors);
    }

    // ---- helpers ------------------------------------------------------------------------

    private async Task<string> Record(string? title, string? project, CaptureMode mode, TimeSpan duration, bool unplug = false, TimeSpan? chunk = null)
    {
        var sources = new List<FakeCaptureSource>();
        if (mode != CaptureMode.Loopback) sources.Add(new FakeCaptureSource(AudioSource.Mic, MicFormat));
        if (mode != CaptureMode.Mic) sources.Add(new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat));
        await using var session = RecordingSession.Create(_workspace.Root,
            new RecordingOptions { Mode = mode, Title = title, Project = project, ChunkDuration = chunk ?? TimeSpan.FromMinutes(5) },
            sources, _clock, new FakeFreeSpace(long.MaxValue));
        foreach (var s in sources) s.AfterPush = () => session.ThrottleForTesting(s.Source);
        session.Start();
        Push(sources, duration);
        if (unplug) sources[0].Fail(new AudioDeviceException(AudioDeviceErrorKind.DeviceInvalidated, "gone"));
        await session.StopAsync();
        return session.Directory;
    }

    private async Task<string> RecordAndCrash()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        var session = RecordingSession.Create(_workspace.Root, new RecordingOptions { Mode = CaptureMode.Mic }, [mic], _clock, new FakeFreeSpace(long.MaxValue));
        mic.AfterPush = () => session.ThrottleForTesting(AudioSource.Mic);
        session.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(3));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ChunkSidecar.TryLoad(ChunkSidecar.PathFor(Directory.GetFiles(session.Directory, "*.partial", SearchOption.AllDirectories).Single()))?.FramesWritten < 2 * 8000)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("sidecar not written");
            await Task.Delay(20);
        }

        // Simulate the process dying: copy the folder as it is now, then let the original finish elsewhere.
        var crashed = Path.Combine(_workspace.SessionsRoot, "2026", "10", Guid.NewGuid().ToString("D"));
        foreach (var file in Directory.GetFiles(session.Directory, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".tmp", StringComparison.Ordinal)) continue; // a write in flight; recovery ignores these too
            var dest = Path.Combine(crashed, Path.GetRelativePath(session.Directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = File.Create(dest);
                await input.CopyToAsync(output);
            }
            catch (FileNotFoundException)
            {
                // replaced between listing and opening
            }
        }
        await session.StopAsync();
        await session.DisposeAsync();
        Directory.Delete(session.Directory, recursive: true);

        // The copy's folder name must match its session id.
        var id = SessionManifest.Load(crashed).SessionId.ToString("D");
        var target = Path.Combine(Path.GetDirectoryName(crashed)!, id);
        Directory.Move(crashed, target);
        return target;
    }

    private void Push(List<FakeCaptureSource> sources, TimeSpan duration)
    {
        var p = TimeSpan.FromMilliseconds(10);
        var buffers = sources.Select(s => new byte[(int)(s.Format.SampleRate * p.TotalSeconds) * s.Format.BlockAlign]).ToList();
        foreach (var b in buffers) Array.Fill<byte>(b, 0x11);
        for (var i = 0; i < (long)Math.Round(duration / p); i++)
        {
            var captured = _clock.GetTimestamp();
            _clock.Advance(p);
            for (var k = 0; k < sources.Count; k++) sources[k].Push(buffers[k], AudioPacketStatus.None, captured);
        }
    }
}
