using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Playback;
using VoiceJournal.Core.Sessions;
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

    /// <summary>Text, not only a color (detail spec 18.1).</summary>
    public string LossText => Summary.HasAudioLoss ? "⚠ 欠損あり" : string.Empty;

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

/// <summary>UI-04 library, minimal for Phase B: list, loss warning, search, open folder, play a track.</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ISessionIndex _index;
    private readonly RecordingEnvironment _environment;
    private readonly IAudioPlayer _player;
    private readonly IShellService _shell;
    private readonly IUiDispatcher _ui;
    private List<SessionRowViewModel> _all = [];

    public LibraryViewModel(ISessionIndex index, RecordingEnvironment environment, IAudioPlayer player, IShellService shell, IUiDispatcher ui)
    {
        _index = index;
        _environment = environment;
        _player = player;
        _shell = shell;
        _ui = ui;
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
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand), nameof(PlayMicCommand), nameof(PlayLoopbackCommand))]
    public partial SessionRowViewModel? Selected { get; set; }

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
        Sessions.Clear();
        foreach (var row in _all.Where(r => query.Length == 0 || r.Matches(query))) Sessions.Add(row);
        OnPropertyChanged(nameof(CountText));
    }
}
