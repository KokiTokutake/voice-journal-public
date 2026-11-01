using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.ViewModels;

/// <summary>One job in UI-03.</summary>
public sealed class JobRowViewModel(JobSummary job, string workspaceRoot)
{
    public JobSummary Job { get; } = job;

    public string Directory { get; } = Path.Combine(workspaceRoot, job.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public string SessionText => (string.IsNullOrWhiteSpace(Job.Title) ? "（無題）" : Job.Title) +
        "  " + Job.SessionStartedAtUtc.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);

    public string KindText => Job.Kind == SttJobKind.Full ? "文字起こし" : "区間の再認識";

    public string ModelText => UserMessages.ModelLabel(Job.ModelName);

    public string StateText => Job.State == ProcessingState.Transcribed ? $"完了（Rev {Job.ResultRevision}）" : UserMessages.ProcessingStateName(Job.State);

    public string ErrorText => Job.State is ProcessingState.Failed or ProcessingState.Cancelled ? UserMessages.SttError(Job.Error) : string.Empty;

    public string TimeText => Job.CreatedAtUtc.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);

    public bool CanCancel => ProcessingStateMachine.IsActive(Job.State);

    public bool CanRetry => Job.State is ProcessingState.Failed or ProcessingState.Cancelled;
}

/// <summary>
/// UI-03 processing status (detail spec 18.2): the running job with its stage, piece and progress (no invented
/// remaining time, detail spec 3.2), whether it is paused for a recording, and recent jobs with cancel / retry.
/// </summary>
public sealed partial class ProcessingViewModel : ObservableObject
{
    private readonly TranscriptionCoordinator _transcription;
    private readonly TranscriptIndex _index;
    private readonly IUiDispatcher _ui;
    private readonly string _workspaceRoot;
    private bool _refreshQueued;

    public ProcessingViewModel(TranscriptionCoordinator transcription, TranscriptIndex index, IUiDispatcher ui, RecordingEnvironment environment)
    {
        _transcription = transcription;
        _index = index;
        _ui = ui;
        _workspaceRoot = environment.WorkspaceRoot;
        _transcription.Progress += (_, p) => _ui.Post(() => OnProgress(p));
        _transcription.JobChanged += (_, _) => QueueRefresh();
        _transcription.IndexError += (_, ex) => _ui.Post(() => Message = $"処理の記録を更新できませんでした（{ex.Message}）。ファイルは保存されています。次回の起動時に作り直します。");
    }

    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(RetryCommand))]
    public partial JobRowViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string CurrentText { get; set; } = "実行中の処理はありません。";

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? PausedText { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    private void OnProgress(SttJobProgress p)
    {
        IsRunning = true;
        ProgressPercent = Math.Round(p.Fraction * 100, 1);
        var title = Jobs.FirstOrDefault(j => j.Job.JobId == p.JobId)?.SessionText ?? "セッション";
        var stage = p.State == ProcessingState.Normalizing
            ? "音声を 16kHz に変換中"
            : $"文字起こし中（{(p.Source is { } s ? UserMessages.SourceName(s) + " " : string.Empty)}区間 {p.PieceNumber}/{p.PieceCount}）";
        CurrentText = $"{title}：{stage} {ProgressPercent:0}%";
        UpdatePaused();
    }

    private void UpdatePaused() =>
        PausedText = _transcription.Runner.IsPaused ? "録音中のため一時停止しています。録音が終わると続きから再開します。" : null;

    private void QueueRefresh()
    {
        lock (Jobs)
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
        }
        _ui.Post(async () =>
        {
            lock (Jobs) _refreshQueued = false;
            await RefreshAsync();
        });
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var jobs = await Task.Run(() => _index.RecentJobs(50));
            var selected = Selected?.Job.JobId;
            Jobs.Clear();
            foreach (var j in jobs) Jobs.Add(new JobRowViewModel(j, _workspaceRoot));
            Selected = Jobs.FirstOrDefault(j => j.Job.JobId == selected);
            if (_transcription.Runner.CurrentJobId is null)
            {
                IsRunning = false;
                ProgressPercent = 0;
                var queued = _transcription.Runner.QueuedCount;
                CurrentText = queued > 0 ? $"順番待ち {queued} 件" : "実行中の処理はありません。";
            }
            UpdatePaused();
        }
        catch (Exception ex) when (ex is DatabaseBusyException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            Message = $"処理の一覧を読み込めませんでした（{ex.Message}）。";
        }
    }

    private bool CanCancel() => Selected?.CanCancel == true;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (Selected is not { } row) return;
        try
        {
            _transcription.Cancel(row.Directory, row.Job.JobId);
            Message = "取り消しました。終わった区間と録音は残っています。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            Message = $"取り消せませんでした（{ex.Message}）。";
        }
    }

    private bool CanRetry() => Selected?.CanRetry == true;

    /// <summary>Resumes the job: finished pieces are kept and only the rest runs (STT-002).</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        if (Selected is not { } row) return;
        try
        {
            _transcription.Retry(row.Directory, row.Job.JobId);
            Message = "順番待ちに戻しました。終わった区間はそのまま使い、残りだけを実行します。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = $"やり直せませんでした（{ex.Message}）。";
        }
    }
}
