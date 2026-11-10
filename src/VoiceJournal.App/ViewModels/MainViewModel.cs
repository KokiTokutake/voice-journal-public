using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.ViewModels;

public enum AppPage
{
    Recording,
    Library,
    Transcript,
    Record,
    Management,
    Processing,
    Settings,
}

/// <summary>Window shell (detail spec 18.1): navigation, the always-visible recording indicator, startup and closing.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public MainViewModel(
        RecordingViewModel recording, LibraryViewModel library, IDialogService dialogs,
        TranscriptViewModel? transcript = null, ProcessingViewModel? processing = null, SettingsViewModel? settings = null,
        RecordReviewViewModel? record = null, ManagementViewModel? management = null,
        IRecordService? records = null, IUiDispatcher? ui = null)
    {
        Recording = recording;
        Library = library;
        Transcript = transcript;
        Record = record;
        Processing = processing;
        Settings = settings;
        Management = management;
        _dialogs = dialogs;
        Recording.PropertyChanged += OnRecordingChanged;
        Recording.RecordingCompleted += async (_, _) => await Library.RefreshAsync();
        Library.OpenTranscriptRequested += (_, row) => OpenTranscript(row);
        Library.OpenRecordRequested += (_, row) => OpenRecord(row);
        if (Record is not null) Record.OpenManagementRequested += (_, sessionId) => OpenManagement(sessionId);
        if (Management is not null) Management.IsRecording = () => Recording.IsActive;
        if (Management is not null && Record is not null) Record.CandidateStates = Management.StateLookup;
        if (records is not null && ui is not null) records.JobChanged += (_, change) => ui.Post(() => OnRecordJobChanged(change));
    }

    // ---- completion notice (a record job of a whole session finished) ---------------------------------------------

    /// <summary>
    /// A whole-session record job finished (made, nothing to record, or failed). The window flashes the taskbar
    /// button when it is not in front: the record is often made 30–60 minutes after the recording, minimized.
    /// </summary>
    public event EventHandler? RecordFinished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? NoticeText { get; set; }

    /// <summary>True for a failure (caution colour, and the button opens 「処理状況」).</summary>
    [ObservableProperty]
    public partial bool NoticeIsProblem { get; set; }

    [ObservableProperty]
    public partial string NoticeActionLabel { get; set; } = "記録を開く";

    public bool HasNotice => NoticeText is not null;

    private string? _noticeDirectory;

    internal void OnRecordJobChanged(RecordJobChange change)
    {
        var job = change.Job;
        // Section regenerations are asked for on the record page itself, which reports them.
        if (job.SectionId is not null) return;
        if (job.State is not (Core.Transcription.ProcessingState.ReadyForReview or Core.Transcription.ProcessingState.NotAnalyzable or Core.Transcription.ProcessingState.Failed)) return;
        RecordFinished?.Invoke(this, EventArgs.Empty);
        var directory = change.SessionDirectory;
        // The record page already showing this session reports the result itself.
        if (CurrentPage == AppPage.Record && Record?.SessionDirectory is { } shown && string.Equals(shown, directory, StringComparison.OrdinalIgnoreCase))
            return;
        var name = SessionName(directory);
        _noticeDirectory = directory;
        NoticeIsProblem = job.State == Core.Transcription.ProcessingState.Failed;
        (NoticeText, NoticeActionLabel) = job.State switch
        {
            Core.Transcription.ProcessingState.ReadyForReview when job.FailedPieces.Count > 0 =>
                ($"記録ができました: {name}（{job.FailedPieces.Count} 区間は整理できず、記録に入っていません）。内容と根拠を確かめてください。", "記録を開く"),
            Core.Transcription.ProcessingState.ReadyForReview => ($"記録ができました: {name}。内容と根拠を確かめてください。", "記録を開く"),
            Core.Transcription.ProcessingState.NotAnalyzable => ($"{name} の文字起こしには、記録にする内容がありませんでした（無音や認識なし）。", "記録を開く"),
            _ => ($"記録を作れませんでした: {name}。理由は「処理状況」で確かめられます。", "処理状況を開く"),
        };
    }

    /// <summary>"題名（開始日時）" from the library, or the folder name when the library does not list the session.</summary>
    private string SessionName(string directory)
    {
        var row = Library.Sessions.FirstOrDefault(r => string.Equals(r.Directory, directory, StringComparison.OrdinalIgnoreCase));
        return row is null ? $"「{Path.GetFileName(directory)}」" : $"「{row.TitleText}」（{row.StartedText}）";
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenNotice()
    {
        var directory = _noticeDirectory;
        var problem = NoticeIsProblem;
        DismissNotice();
        if (directory is null) return;
        if (problem)
        {
            CurrentPage = AppPage.Processing;
            return;
        }
        if (Record is null) return;
        var row = Library.Sessions.FirstOrDefault(r => string.Equals(r.Directory, directory, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            OpenRecord(row);
            return;
        }
        Record.Open(directory, Path.GetFileName(directory));
        CurrentPage = AppPage.Record;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DismissNotice()
    {
        NoticeText = null;
        _noticeDirectory = null;
    }

    public RecordingViewModel Recording { get; }

    public LibraryViewModel Library { get; }

    public TranscriptViewModel? Transcript { get; }

    public ProcessingViewModel? Processing { get; }

    public RecordReviewViewModel? Record { get; }

    public SettingsViewModel? Settings { get; }

    public ManagementViewModel? Management { get; }

    /// <summary>UI-06 for the session chosen in the library.</summary>
    public void OpenTranscript(SessionRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Transcript is null) return;
        Transcript.Open(row.Directory, $"{row.TitleText}（{row.StartedText}）");
        CurrentPage = AppPage.Transcript;
    }

    /// <summary>UI-07 for the session chosen in the library.</summary>
    public void OpenRecord(SessionRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Record is null) return;
        Record.Open(row.Directory, $"{row.TitleText}（{row.StartedText}）");
        CurrentPage = AppPage.Record;
    }

    /// <summary>UI-08 for the session of the record page.</summary>
    public void OpenManagement(Guid sessionId)
    {
        if (Management is null) return;
        Management.Open(sessionId);
        CurrentPage = AppPage.Management;
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        if (value == AppPage.Processing && Processing is not null) _ = Processing.RefreshAsync();
        if (value == AppPage.Management && Management is not null) Management.Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPageIndex))]
    public partial AppPage CurrentPage { get; set; } = AppPage.Recording;

    /// <summary>For the tab control.</summary>
    public int SelectedPageIndex
    {
        get => (int)CurrentPage;
        set => CurrentPage = (AppPage)value;
    }

    public string IndicatorText => Recording.IndicatorText;

    public bool IsRecordingActive => Recording.IsActive;

    [ObservableProperty]
    public partial string? StartupMessage { get; set; }

    /// <summary>
    /// Index rebuild and crash recovery (REC-001) off the UI thread, then the library. Shows what was recovered.
    /// </summary>
    public async Task InitializeAsync(Func<StartupReport> runStartup)
    {
        ArgumentNullException.ThrowIfNull(runStartup);
        StartupMessage = "ライブラリを準備しています…";
        StartupReport report;
        try
        {
            report = await Task.Run(runStartup);
        }
        catch (Exception ex) when (ex is DatabaseBusyException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            StartupMessage = null;
            _dialogs.ShowError("ライブラリを準備できません", $"起動時の確認に失敗しました（{ex.Message}）。録音は使えます。");
            await Library.RefreshAsync();
            return;
        }
        StartupMessage = null;
        await Library.RefreshAsync();

        var problems = report.Index.Failed.Count + report.RecoveryFailures.Count;
        if (report.Recovered.Count > 0)
        {
            _dialogs.ShowInfo(
                "前回の録音を回復しました",
                $"前回、録音中にアプリが終了したセッションが {report.Recovered.Count} 件ありました。保存されていた範囲を回復し、" +
                "ライブラリに「中断（回復済み）」として表示しています。\n最後の最大 5 分のチャンクには、保存しきれなかった音声があり得ます。元のファイルは消さずに残しています。");
        }
        if (problems > 0)
        {
            Library.Message = $"読み込めなかったセッションが {problems} 件あります（manifest.json の破損など）。ファイルは変更していません。";
        }
    }

    /// <summary>Window closing: true when it may close (asks first while recording).</summary>
    public Task<bool> ConfirmCloseAsync() => Recording.ConfirmCloseAsync();

    private void OnRecordingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecordingViewModel.IndicatorText)) OnPropertyChanged(nameof(IndicatorText));
        if (e.PropertyName is nameof(RecordingViewModel.Status)) OnPropertyChanged(nameof(IsRecordingActive));
    }
}
