using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.ViewModels;

/// <summary>One job in UI-03: a transcription or a record generation.</summary>
public sealed class JobRowViewModel
{
    public JobRowViewModel(JobSummary job, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(job);
        JobId = job.JobId;
        IsRecord = false;
        Directory = DirectoryOf(workspaceRoot, job.RelativePath);
        SessionText = Session(job.Title, job.SessionStartedAtUtc);
        KindText = job.Kind == SttJobKind.Full ? "文字起こし" : "区間の再認識";
        ModelText = UserMessages.ModelLabel(job.ModelName);
        State = job.State;
        StateText = job.State == ProcessingState.Transcribed ? $"完了（Rev {job.ResultRevision}）" : UserMessages.ProcessingStateName(job.State);
        ErrorText = job.State is ProcessingState.Failed or ProcessingState.Cancelled ? UserMessages.SttError(job.Error) : string.Empty;
        CreatedAtUtc = job.CreatedAtUtc;
    }

    public JobRowViewModel(RecordJobSummary job, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(job);
        JobId = job.JobId;
        IsRecord = true;
        Directory = DirectoryOf(workspaceRoot, job.RelativePath);
        SessionText = Session(job.Title, job.SessionStartedAtUtc);
        KindText = "記録の作成";
        ModelText = job.ModelId;
        State = job.State;
        StateText = job.State switch
        {
            ProcessingState.ReadyForReview => $"完了（記録 Rev {job.RecordRevision}）" + (job.FailedPieces > 0 ? "・部分生成" : string.Empty),
            _ => UserMessages.ProcessingStateName(job.State),
        };
        ErrorText = job.State is ProcessingState.Failed or ProcessingState.Cancelled ? UserMessages.RecordError(job.Error, null)
            : job.State == ProcessingState.ReadyForReview && job.ElapsedSeconds is { } s
                ? $"{job.PieceCount} 区間、{s:0} 秒" + (job.FailedPieces > 0 ? $"（{job.FailedPieces} 区間を整理できませんでした）" : string.Empty)
            : string.Empty;
        CreatedAtUtc = job.CreatedAtUtc;
    }

    public Guid JobId { get; }

    /// <summary>A record generation (LLM); otherwise a transcription (whisper.cpp).</summary>
    public bool IsRecord { get; }

    public string Directory { get; }

    public string SessionText { get; }

    public string KindText { get; }

    public string ModelText { get; }

    public ProcessingState State { get; }

    public string StateText { get; }

    public string ErrorText { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string TimeText => CreatedAtUtc.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);

    public bool CanCancel => ProcessingStateMachine.IsActive(State);

    public bool CanRetry => State is ProcessingState.Failed or ProcessingState.Cancelled;

    private static string DirectoryOf(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Session(string? title, DateTimeOffset startedUtc) =>
        (string.IsNullOrWhiteSpace(title) ? "（無題）" : title) + "  " + startedUtc.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// UI-03 processing status (detail spec 18.2): the running job with its stage, piece and progress (no invented
/// remaining time, detail spec 3.2), whether it is paused for a recording, and recent jobs (transcription and
/// record generation) with cancel / retry.
/// </summary>
public sealed partial class ProcessingViewModel : ObservableObject
{
    private readonly TranscriptionCoordinator _transcription;
    private readonly TranscriptIndex _index;
    private readonly RecordCoordinator? _records;
    private readonly RecordIndex? _recordIndex;
    private readonly IUiDispatcher _ui;
    private readonly string _workspaceRoot;
    private bool _refreshQueued;

    public ProcessingViewModel(TranscriptionCoordinator transcription, TranscriptIndex index, IUiDispatcher ui, RecordingEnvironment environment,
        RecordCoordinator? records = null, RecordIndex? recordIndex = null)
    {
        _transcription = transcription;
        _index = index;
        _records = records;
        _recordIndex = recordIndex;
        _ui = ui;
        _workspaceRoot = environment.WorkspaceRoot;
        _transcription.Progress += (_, p) => _ui.Post(() => OnProgress(p));
        _transcription.JobChanged += (_, _) => QueueRefresh();
        _transcription.IndexError += (_, ex) => _ui.Post(() => ShowIndexError(ex));
        if (_records is not null)
        {
            _records.Progress += (_, p) => _ui.Post(() => OnRecordProgress(p));
            _records.JobChanged += (_, _) => QueueRefresh();
            _records.IndexError += (_, ex) => _ui.Post(() => ShowIndexError(ex));
        }
    }

    private void ShowIndexError(Exception ex) =>
        Message = $"処理の記録を更新できませんでした（{ex.Message}）。ファイルは保存されています。次回の起動時に作り直します。";

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

    /// <summary>UI-03 キュー保留: queued transcriptions and records wait; the running one finishes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoldLabel))]
    public partial bool IsQueueHeld { get; set; }

    public string HoldLabel => IsQueueHeld ? "保留を解除(_H)" : "順番待ちを保留(_H)";

    [RelayCommand]
    private void ToggleHold()
    {
        var held = !IsQueueHeld;
        _transcription.Runner.SetHeld(held);
        _records?.Runner.SetHeld(held);
        IsQueueHeld = held;
        UpdatePaused();
        Message = held
            ? "順番待ちの処理を保留しました。実行中の処理は最後まで続け、次の処理は始めません（アプリを起動し直すと保留は解けます）。"
            : "保留を解除しました。順番待ちの処理を続けます。";
    }

    /// <summary>Set by the app: writes the diagnostics report (no transcript or item text) and returns its path.</summary>
    public Func<string>? WriteDiagnostics { get; set; }

    /// <summary>Set by the app: shows a folder in Explorer.</summary>
    public Action<string>? ShowFolder { get; set; }

    /// <summary>Set by the app: asks before writing (21.3: the user sees what the report holds). Null writes without asking.</summary>
    public Func<string, string, bool>? Confirm { get; set; }

    /// <summary>UI-03 「診断情報を書き出す」: versions, hashes, settings, counts and error codes for a problem report.</summary>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (WriteDiagnostics is not { } write) return;
        if (Confirm is { } confirm && !confirm("診断情報の書き出し", "問題の報告に添えるための診断情報を、この PC に保存します。\n\n" + Infrastructure.Storage.DiagnosticsReport.Contents + "\n\n書き出しますか？"))
            return;
        try
        {
            var path = await Task.Run(write);
            Message = $"診断情報を書き出しました（文字起こしや課題・タスクの本文は含みません）: {path}";
            ShowFolder?.Invoke(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Message = $"診断情報を書き出せませんでした（{ex.Message}）。";
        }
    }

    private void OnProgress(SttJobProgress p)
    {
        IsRunning = true;
        ProgressPercent = Math.Round(p.Fraction * 100, 1);
        var title = Jobs.FirstOrDefault(j => j.JobId == p.JobId)?.SessionText ?? "セッション";
        var stage = p.State == ProcessingState.Normalizing
            ? "音声を 16kHz に変換中"
            : $"文字起こし中（{(p.Source is { } s ? UserMessages.SourceName(s) + " " : string.Empty)}区間 {p.PieceNumber}/{p.PieceCount}）";
        CurrentText = $"{title}：{stage} {ProgressPercent:0}%";
        UpdatePaused();
    }

    private void OnRecordProgress(GenerationProgress p)
    {
        IsRunning = true;
        ProgressPercent = p.Total > 0 ? Math.Round(100.0 * p.Done / p.Total, 1) : 0;
        var title = _records?.CurrentJobId is { } id ? Jobs.FirstOrDefault(j => j.JobId == id)?.SessionText ?? "セッション" : "セッション";
        var stage = p.Step switch
        {
            "extract" => $"記録の作成（区間ごとに整理 {p.Done}/{p.Total}）",
            "integrate" => "記録の作成（全体をまとめる）",
            "validate" => "記録の作成（検証）",
            "done" => "記録の作成（保存）",
            _ => "記録の作成",
        };
        CurrentText = $"{title}：{stage}";
        UpdatePaused();
    }

    private void UpdatePaused() =>
        PausedText = _transcription.Runner.IsPaused ? "録音中のため一時停止しています。録音が終わると続きから再開します。"
            : IsQueueHeld ? "順番待ちを保留しています。［保留を解除］を押すと続きを始めます。"
            : null;

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
            var rows = await Task.Run(() =>
            {
                var list = _index.RecentJobs(50).Select(j => new JobRowViewModel(j, _workspaceRoot)).ToList();
                if (_recordIndex is not null) list.AddRange(_recordIndex.RecentJobs(50).Select(j => new JobRowViewModel(j, _workspaceRoot)));
                return list.OrderByDescending(j => j.CreatedAtUtc).Take(50).ToList();
            });
            var selected = Selected?.JobId;
            Jobs.Clear();
            foreach (var j in rows) Jobs.Add(j);
            Selected = Jobs.FirstOrDefault(j => j.JobId == selected);
            if (_transcription.Runner.CurrentJobId is null && _records?.CurrentJobId is null)
            {
                IsRunning = false;
                ProgressPercent = 0;
                var queued = _transcription.Runner.QueuedCount + (_records?.Runner.QueuedCount ?? 0);
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
            if (row.IsRecord) _records?.Cancel(row.Directory, row.JobId);
            else _transcription.Cancel(row.Directory, row.JobId);
            Message = row.IsRecord ? "取り消しました。終わった区間と文字起こしは残っています。" : "取り消しました。終わった区間と録音は残っています。";
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
            if (row.IsRecord) _records?.Retry(row.Directory, row.JobId);
            else _transcription.Retry(row.Directory, row.JobId);
            Message = "順番待ちに戻しました。終わった区間はそのまま使い、残りだけを実行します。" +
                (row.IsRecord ? "推論エンジンを使えなかったときは、設定画面の「接続を確認」で確かめてから押してください。" : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = $"やり直せませんでした（{ex.Message}）。";
        }
    }
}
