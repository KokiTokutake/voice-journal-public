using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Playback;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.ViewModels;

/// <summary>One row of the library list (UI-04, minimal for Phase B).</summary>
public sealed class SessionRowViewModel
{
    public SessionRowViewModel(SessionSummary summary, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Summary = summary;
        Directory = Path.Combine(workspaceRoot, summary.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var zone = FindZone(summary.TimeZoneId);
        StartedLocal = TimeZoneInfo.ConvertTime(summary.StartedAtUtc, zone);
        StartedText = StartedLocal.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    public SessionSummary Summary { get; }

    public string Directory { get; }

    public DateTimeOffset StartedLocal { get; }

    public string StartedText { get; }

    public string TitleText => string.IsNullOrWhiteSpace(Summary.Title) ? "（無題）" : Summary.Title;

    public string ProjectText => Summary.Project ?? string.Empty;

    public string DurationText => UserMessages.Duration(TimeSpan.FromMilliseconds(Summary.DurationMs));

    public TimeSpan Duration => TimeSpan.FromMilliseconds(Summary.DurationMs);

    public string ModeText => UserMessages.ModeName(Summary.CaptureMode);

    public string StateText => UserMessages.CaptureStateName(Summary.CaptureState);

    public string ProcessingText => UserMessages.ProcessingStateName(Summary.ProcessingState, Summary.LatestTranscriptRevision);

    public bool HasTranscript => Summary.LatestTranscriptRevision is not null;

    public string RecordText => UserMessages.RecordStateName(Summary.RecordState);

    public bool IsProcessing => ProcessingStateMachine.IsActive(Summary.ProcessingState);

    /// <summary>
    /// Text, not only a color (detail spec 18.1). A session recovered after a crash may miss its last seconds
    /// (AT-19 "欠損表示"; detail spec 19: the last chunk may hold unsaved audio).
    /// </summary>
    public string LossText => Summary.HasAudioLoss ? "⚠ 欠損あり"
        : Summary.CaptureState == CaptureState.Interrupted ? "⚠ 末尾に欠損の可能性"
        : string.Empty;

    public bool HasMic => Summary.CaptureMode != CaptureMode.Loopback;

    public bool HasLoopback => Summary.CaptureMode != CaptureMode.Mic;

    public bool Matches(string query) =>
        TitleText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || ProjectText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || StartedText.Contains(query, StringComparison.Ordinal);

    private static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }
}

/// <summary>UI-04 library: list, loss warning, processing state, search, open folder, play a track, transcribe, open the transcript.</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ISessionIndex _index;
    private readonly RecordingEnvironment _environment;
    private readonly IAudioPlayer _player;
    private readonly IShellService _shell;
    private readonly IUiDispatcher _ui;
    private readonly TranscriptionCoordinator? _transcription;
    private List<SessionRowViewModel> _all = [];
    private bool _refreshQueued;

    public LibraryViewModel(ISessionIndex index, RecordingEnvironment environment, IAudioPlayer player, IShellService shell, IUiDispatcher ui,
        TranscriptionCoordinator? transcription = null)
    {
        _index = index;
        _environment = environment;
        _player = player;
        _shell = shell;
        _ui = ui;
        _transcription = transcription;
        if (_transcription is not null)
        {
            // Job state changes (not progress) refresh the list; bursts collapse into one refresh.
            _transcription.JobChanged += (_, _) => QueueRefresh();
            _transcription.RevisionsChanged += (_, _) => QueueRefresh();
        }
        _player.Ended += (_, e) => _ui.Post(() =>
        {
            IsPlaying = false;
            NowPlaying = null;
            if (e.Error is not null) Message = $"再生が止まりました（{e.Error.Message}）。";
        });
    }

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand), nameof(PlayMicCommand), nameof(PlayLoopbackCommand), nameof(TranscribeCommand), nameof(OpenTranscriptCommand), nameof(OpenRecordCommand),
        nameof(ExportCommand), nameof(DeleteCommand))]
    public partial SessionRowViewModel? Selected { get; set; }

    /// <summary>Set by the app: asks and answers (pick a folder, confirm a deletion).</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Set by the app: how many management items use the session as evidence (shown before deleting).</summary>
    public Func<Guid, int>? ItemReferences { get; set; }

    /// <summary>
    /// UI-04 「書き出す」: the record (approved, else the draft) and the transcript as Markdown into a folder the user picks,
    /// under 「日時 タイトル」. The original audio stays in the workspace.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Export()
    {
        if (Selected is not { } row || Dialogs?.PickFolder("書き出す先のフォルダーを選ぶ") is not { } folder) return;
        var name = string.Concat($"{row.StartedText} {row.TitleText}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var target = Path.Combine(folder, name);
        try
        {
            Directory.CreateDirectory(target);
            var written = 0;
            foreach (var file in new[] { "record.md", "record.draft.md", "transcript.md" })
            {
                var source = Path.Combine(row.Directory, file);
                if (!File.Exists(source)) continue;
                if (file == "record.draft.md" && File.Exists(Path.Combine(row.Directory, "record.md"))) continue; // the approved one is enough
                File.Copy(source, Path.Combine(target, file), overwrite: false);
                written++;
            }
            Message = written == 0 ? "書き出せる記録・文字起こしがまだありません。" : $"{written} 個のファイルを書き出しました: {target}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"書き出せませんでした（{ex.Message}）。同じ名前のフォルダーがあるときは、別の場所を選んでください。";
        }
    }

    /// <summary>
    /// UI-04 「削除」 with confirmation. Nothing is erased (14.4): the session folder moves to <c>trash\</c> in the workspace
    /// and leaves the index; moving it back restores it. A session being recorded cannot be deleted.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row || Dialogs is null) return;
        if (row.Summary.CaptureState is CaptureState.Recording or CaptureState.Stopping or CaptureState.Ready)
        {
            Message = "録音中のセッションは削除できません。";
            return;
        }
        var references = ItemReferences?.Invoke(row.Summary.SessionId) ?? 0;
        var question = $"「{row.TitleText}」（{row.StartedText}）を削除しますか？\n\n原音・文字起こし・記録はすぐには消さず、ワークスペースの trash フォルダーへ移します（戻せば元に戻ります）。"
            + (references > 0 ? $"\n\nこのセッションは課題・タスク {references} 件の根拠になっています。項目は残りますが、根拠の区間は開けなくなります。" : string.Empty);
        if (!Dialogs.Confirm("セッションの削除", question)) return;
        try
        {
            var trash = Path.Combine(_environment.WorkspaceRoot, "trash", Path.GetFileName(Path.TrimEndingDirectorySeparator(row.Directory)));
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            Directory.Move(row.Directory, trash);
            await Task.Run(_index.Rebuild);
            Message = $"削除しました（trash へ移しました: {trash}）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"削除できませんでした（{ex.Message}）。ファイルを開いているアプリを閉じてから、やり直してください。";
            return;
        }
        await RefreshAsync();
    }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopPlaybackCommand))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial string? NowPlaying { get; set; }

    public string CountText => _all.Count == Sessions.Count ? $"{Sessions.Count} 件" : $"{Sessions.Count} / {_all.Count} 件";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var list = await Task.Run(_index.ListSessions);
            var selectedId = Selected?.Summary.SessionId;
            _all = [.. list.Select(s => new SessionRowViewModel(s, _environment.WorkspaceRoot))];
            ApplyFilter();
            Selected = Sessions.FirstOrDefault(s => s.Summary.SessionId == selectedId);
            Message = null;
        }
        catch (Exception ex) when (ex is DatabaseBusyException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            Message = $"一覧を読み込めませんでした（{ex.Message}）。録音データはそのまま残っています。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool HasSelection() => Selected is not null;

    /// <summary>The user asked to open the transcript of a session (the main window switches to UI-06).</summary>
    public event EventHandler<SessionRowViewModel>? OpenTranscriptRequested;

    /// <summary>The user asked to open the record of a session (the main window switches to UI-07).</summary>
    public event EventHandler<SessionRowViewModel>? OpenRecordRequested;

    private void QueueRefresh()
    {
        lock (_all)
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
        }
        _ui.Post(async () =>
        {
            lock (_all) _refreshQueued = false;
            await RefreshAsync();
        });
    }

    private bool CanTranscribe() => _transcription is not null && Selected is { IsProcessing: false } row && row.Summary.CaptureState is not CaptureState.Recording;

    /// <summary>Queues transcription with the current settings; for a transcribed session this makes a new raw revision (AT-26).</summary>
    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task Transcribe()
    {
        if (_transcription is null || Selected is not { } row) return;
        try
        {
            var job = _transcription.Enqueue(row.Directory);
            await RefreshAsync(); // clears the message, so the notice comes after it
            Message = (row.HasTranscript ? "今の設定で文字起こしし直します（以前の結果も残ります）。" : "文字起こしを順番待ちに入れました。")
                + $"モデル: {UserMessages.ModelLabel(job.Configuration.ModelName)}。進み具合は「処理状況」で確認できます。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"文字起こしを始められませんでした（{ex.Message}）。";
        }
    }

    private bool CanOpenTranscript() => Selected?.HasTranscript == true;

    [RelayCommand(CanExecute = nameof(CanOpenTranscript))]
    private void OpenTranscript()
    {
        if (Selected is { } row) OpenTranscriptRequested?.Invoke(this, row);
    }

    /// <summary>UI-07: also for a session without a record yet (the review screen offers to make one).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTranscript))]
    private void OpenRecord()
    {
        if (Selected is { } row) OpenRecordRequested?.Invoke(this, row);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder()
    {
        try { _shell.OpenFolder(Selected!.Directory); }
        catch (IOException ex) { Message = $"フォルダーを開けません（{ex.Message}）。"; }
    }

    private bool CanPlayMic() => Selected?.HasMic == true;

    private bool CanPlayLoopback() => Selected?.HasLoopback == true;

    [RelayCommand(CanExecute = nameof(CanPlayMic))]
    private void PlayMic() => Play(AudioSource.Mic);

    [RelayCommand(CanExecute = nameof(CanPlayLoopback))]
    private void PlayLoopback() => Play(AudioSource.Loopback);

    [RelayCommand(CanExecute = nameof(IsPlaying))]
    private void StopPlayback() => _player.StopPlayback();

    /// <summary>Saved chunk files of one track, in order (completed and recovered chunks only).</summary>
    internal static IReadOnlyList<string> PlayableFiles(string sessionDirectory, AudioSource source)
    {
        var manifest = SessionManifest.Load(sessionDirectory);
        var track = manifest.Tracks.FirstOrDefault(t => t.Source == source);
        if (track is null) return [];
        return [.. track.Chunks
            .Where(c => c.State is ChunkState.Completed or ChunkState.Recovered)
            .OrderBy(c => c.Sequence)
            .Select(c => SessionPaths.Resolve(sessionDirectory, c.File))
            .Where(File.Exists)];
    }

    /// <summary>
    /// The saved chunk files of a track from session time <paramref name="atMs"/> on: the chunk that contains it (or the
    /// next one, when <paramref name="atMs"/> falls into a gap) and the offset into that first file.
    /// </summary>
    internal static (IReadOnlyList<string> Files, TimeSpan Offset) PlayableFrom(string sessionDirectory, AudioSource source, long atMs)
    {
        var manifest = SessionManifest.Load(sessionDirectory);
        var track = manifest.Tracks.FirstOrDefault(t => t.Source == source);
        if (track is null) return ([], TimeSpan.Zero);
        var chunks = track.Chunks
            .Where(c => c.State is ChunkState.Completed or ChunkState.Recovered && File.Exists(SessionPaths.Resolve(sessionDirectory, c.File)))
            .OrderBy(c => c.Sequence)
            .Select(c => (Chunk: c, EndMs: c.StartMs + c.FrameCount * 1000 / track.Format.SampleRate))
            .SkipWhile(c => c.EndMs <= atMs)
            .ToList();
        if (chunks.Count == 0) return ([], TimeSpan.Zero);
        var offset = TimeSpan.FromMilliseconds(Math.Max(0, atMs - chunks[0].Chunk.StartMs));
        return ([.. chunks.Select(c => SessionPaths.Resolve(sessionDirectory, c.Chunk.File))], offset);
    }

    private void Play(AudioSource source)
    {
        if (Selected is not { } row) return;
        try
        {
            var files = PlayableFiles(row.Directory, source);
            if (files.Count == 0)
            {
                Message = $"{UserMessages.SourceName(source)}の再生できる音声がありません。";
                return;
            }
            var device = _player.Play(files);
            IsPlaying = true;
            NowPlaying = $"再生中: {row.StartedText} {row.TitleText}（{UserMessages.SourceName(source)}）→ 出力先: {device}";
            Message = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or FormatException)
        {
            Message = $"再生できませんでした（{ex.Message}）。";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        // FR-11: the title, project and date, or the text of the transcript in use.
        IReadOnlySet<Guid> inText;
        try
        {
            inText = query.Length == 0 ? new HashSet<Guid>() : _index.SessionsWithText(query);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or VoiceJournal.Infrastructure.Storage.DatabaseBusyException)
        {
            inText = new HashSet<Guid>();
        }
        Sessions.Clear();
        foreach (var row in _all.Where(r => query.Length == 0 || r.Matches(query) || inText.Contains(r.Summary.SessionId))) Sessions.Add(row);
        OnPropertyChanged(nameof(CountText));
    }
}
