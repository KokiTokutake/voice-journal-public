using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VoiceJournal.App.Services;
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
        RecordReviewViewModel? record = null, ManagementViewModel? management = null)
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
