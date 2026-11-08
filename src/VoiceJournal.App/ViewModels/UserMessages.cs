using System.Globalization;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.App.ViewModels;

/// <summary>User-facing texts (Japanese) for capture states, errors and warnings (detail spec 19).</summary>
internal static class UserMessages
{
    public static string SourceName(AudioSource source) => source switch
    {
        AudioSource.Mic => "マイク",
        AudioSource.Loopback => "PC 内部音声",
        _ => source.ToString(),
    };

    public static string ModeName(CaptureMode mode) => mode switch
    {
        CaptureMode.Mic => "マイク",
        CaptureMode.Loopback => "PC 内部音声",
        CaptureMode.Both => "両方",
        _ => mode.ToString(),
    };

    public static string CaptureStateName(CaptureState state) => state switch
    {
        CaptureState.Ready => "準備中",
        CaptureState.Recording => "録音中",
        CaptureState.Partial => "一部音源停止",
        CaptureState.Stopping => "停止中",
        CaptureState.Finalized => "保存済み",
        CaptureState.Interrupted => "中断（回復済み）",
        CaptureState.Failed => "失敗",
        _ => state.ToString(),
    };

    /// <summary>Processing state for the library and the processing screen.</summary>
    public static string ProcessingStateName(ProcessingState state, int? revision = null) => state switch
    {
        ProcessingState.None => revision is null ? "未処理" : $"済み（Rev {revision}）",
        ProcessingState.Queued => "順番待ち",
        ProcessingState.Normalizing => "音声を変換中",
        ProcessingState.Transcribing => "文字起こし中",
        ProcessingState.Transcribed => revision is null ? "文字起こし済み" : $"済み（Rev {revision}）",
        ProcessingState.Failed => "失敗",
        ProcessingState.Cancelled => "取り消し",
        ProcessingState.Organizing => "記録を作成中",
        ProcessingState.Validating => "記録を検証中",
        ProcessingState.ReadyForReview => "記録のレビュー待ち",
        ProcessingState.NotAnalyzable => "記録にする内容なし",
        _ => state.ToString(),
    };

    /// <summary>Review state of a session's record (library, UI-07).</summary>
    public static string RecordStateName(RecordReviewState state) => state switch
    {
        RecordReviewState.None => "記録なし",
        RecordReviewState.Draft => "草稿（未承認）",
        RecordReviewState.Deferred => "保留",
        RecordReviewState.Approved => "承認済み",
        RecordReviewState.ApprovedOutdated => "承認済み（古い）",
        _ => state.ToString(),
    };

    /// <summary>Error code (detail spec 19) of a record job and what the user can do.</summary>
    public static string RecordError(RecordJobError? error, string? detail) => error switch
    {
        null => string.Empty,
        RecordJobError.LlmUnavailable => "LLM-001: 推論エンジンを使えませんでした（起動できない、ファイルが登録と違う、または空きメモリが足りない）。文字起こしは残っています。" +
            "設定画面の「接続を確認」で原因を確かめてから「やり直す」を押してください。" +
            (string.IsNullOrEmpty(detail) ? string.Empty : $"（{detail}）"),
        RecordJobError.GenerationFailed => "LLM-002/003: 記録を作れませんでした。終わった区間は残っています。「やり直す」で残りだけ実行します。" +
            (string.IsNullOrEmpty(detail) ? string.Empty : $"（{detail}）"),
        RecordJobError.NoTranscript => "文字起こしがまだありません。先に文字起こしをしてください。",
        RecordJobError.Cancelled => "取り消しました。終わった区間と文字起こしは残っています。",
        _ => error.ToString()!,
    };

    public static string OriginName(TextOrigin origin) => origin switch
    {
        TextOrigin.Ai => "AI",
        TextOrigin.UserEdited => "修正",
        TextOrigin.UserWritten => "手入力",
        _ => origin.ToString(),
    };

    public static string CertaintyName(Certainty certainty) => certainty switch
    {
        Certainty.Tentative => "案・未確定",
        Certainty.Unclear => "不明確",
        _ => string.Empty,
    };

    public static string CandidateKindName(CandidateKind kind) => kind == CandidateKind.Issue ? "課題" : "タスク";

    public static string ItemStatusName(ItemStatus status) => status switch
    {
        ItemStatus.Open => "未対応",
        ItemStatus.Todo => "未着手",
        ItemStatus.InProgress => "対応中",
        ItemStatus.Blocked => "停滞",
        ItemStatus.Resolved => "解決",
        ItemStatus.Closed => "終了",
        ItemStatus.Done => "完了",
        ItemStatus.Cancelled => "中止",
        _ => status.ToString(),
    };

    public static string PriorityName(ItemPriority priority) => priority switch
    {
        ItemPriority.Low => "低",
        ItemPriority.Medium => "中",
        ItemPriority.High => "高",
        ItemPriority.Critical => "緊急",
        _ => priority.ToString(),
    };

    /// <summary>What the app checked about a candidate (shown as 要確認).</summary>
    public static string DifferenceName(Core.Management.DuplicateDifference difference) => difference switch
    {
        Core.Management.DuplicateDifference.Assignee => "担当が違う",
        Core.Management.DuplicateDifference.DueDate => "期限が違う",
        Core.Management.DuplicateDifference.Status => "状態が違う",
        Core.Management.DuplicateDifference.Negation => "否定の有無が違う",
        _ => "数値が違う",
    };

    public static string ConcernName(CandidateConcern concern) => concern switch
    {
        CandidateConcern.Doubtful => "推測・提案の言い方",
        CandidateConcern.Negation => "否定を含む",
        CandidateConcern.DueDateNeedsCheck => "期限を確認",
        CandidateConcern.DueDateDropped => "根拠のない期限を外した",
        CandidateConcern.AssigneeDropped => "根拠のない担当を外した",
        CandidateConcern.TerminalStatus => "完了などの状態を確認",
        CandidateConcern.StatusDropped => "否定と矛盾する状態を外した",
        CandidateConcern.CheckAudio => "数字・日付・名前を音声で確認",
        CandidateConcern.Merged => "複数の区間から統合",
        CandidateConcern.NearbyEvidence => "担当・期限は隣の区間から",
        _ => concern.ToString(),
    };

    /// <summary>Error code (detail spec 19) and what the user can do.</summary>
    public static string SttError(SttErrorCode? code) => code switch
    {
        null => string.Empty,
        SttErrorCode.ToolMissing => "STT-001: whisper-cli またはモデルが見つからないか、登録した SHA-256 と一致しません。設定画面の「ツールを確認」で確かめてください。録音は残っています。",
        SttErrorCode.RunFailed => "STT-002: 文字起こしが異常終了したか、時間内に終わりませんでした。終わった区間は残っています。「やり直す」で残りだけ実行します。",
        SttErrorCode.NotEnoughMemory => "STT-002: 空きメモリが足りません。ほかのアプリを閉じてからやり直してください。",
        SttErrorCode.Cancelled => "取り消しました。終わった区間と録音は残っています。",
        SttErrorCode.Preempted => "録音のために中断しました。録音が終わると続きから再開します。",
        SttErrorCode.Conflict => "再認識している間に同じ範囲が修正されたため、結果を反映しませんでした。もう一度実行してください。",
        _ => code.ToString()!,
    };

    public static string ModelLabel(string modelName) => modelName switch
    {
        "ggml-small.bin" => "small（既定・速い）",
        "ggml-medium.bin" => "medium（高精度・約 3 倍の時間）",
        _ => modelName,
    };

    public static string StatusText(RecorderStatus status) => status switch
    {
        RecorderStatus.Idle => "待機中",
        RecorderStatus.Preparing => "準備中（音声の取得を待っています）",
        RecorderStatus.Recording => "録音中",
        RecorderStatus.PartiallyStopped => "一部の音源が停止中（ほかの音源は録音を続けています）",
        RecorderStatus.Stopping => "停止して保存しています…",
        RecorderStatus.Saved => "保存しました",
        RecorderStatus.Failed => "録音できませんでした",
        _ => status.ToString(),
    };

    /// <summary>One line of the capture test (UI-01) for a source.</summary>
    public static string CaptureCheck(CaptureCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var who = $"{SourceName(result.Source)}（{result.DeviceName}）";
        if (result.Error is { } error) return $"✗ {who}: {StartFailure(error)}";
        var level = double.IsNegativeInfinity(result.PeakDbfs) ? "無音" : string.Create(CultureInfo.InvariantCulture, $"最大 {result.PeakDbfs:0} dBFS");
        var gaps = result.Discontinuities > 0 ? $"（途切れ {result.Discontinuities} 回）" : string.Empty;
        var arrived = string.Create(CultureInfo.InvariantCulture, $"{result.Received.TotalSeconds:0.0} 秒分");
        if (result.Source == AudioSource.Loopback)
        {
            return result.Audible
                ? $"✓ {who}: PC の音が届きました（{level}）{gaps}。"
                : $"⚠ {who}: PC の音が届きませんでした（{arrived}、{level}）。PC で音が鳴っていない間は届かないので、動画などを再生しながらもう一度試してください。";
        }
        if (!result.Arrived)
            return $"⚠ {who}: {result.Expected.TotalSeconds:0} 秒のうち {arrived}しか届きませんでした{gaps}。デバイスの接続を確かめてください。";
        if (!result.Audible)
            return $"⚠ {who}: 音は届きましたが、ほぼ無音でした（{level}）。マイクのミュート、Windows の入力音量、選んだマイクが正しいかを確かめてください。";
        return result.Peak >= 0.99f
            ? $"⚠ {who}: 音が大きすぎて割れています（{level}）{gaps}。Windows の入力音量を下げてください。"
            : $"✓ {who}: 音が届きました（{arrived}、{level}）{gaps}。";
    }

    /// <summary>Why recording could not start, with what the user can do about it.</summary>
    public static string StartFailure(Exception error)
    {
        var device = error as AudioDeviceException ?? error.InnerException as AudioDeviceException;
        var prefix = error is CaptureStartException { FailedSource: { } failed } ? $"{SourceName(failed)}: " : string.Empty;
        if (device is null) return prefix + $"録音を開始できませんでした（{error.Message}）。";
        return prefix + device.Kind switch
        {
            AudioDeviceErrorKind.AccessDenied =>
                "マイクへのアクセスが許可されていません（CAP-001）。Windows の［設定］→［プライバシーとセキュリティ］→［マイク］で、" +
                "「マイクへのアクセス」と「デスクトップ アプリがマイクにアクセスできるようにする」をオンにしてから、もう一度開始してください。",
            AudioDeviceErrorKind.RawModeUnsupported =>
                "このマイクは「音声処理を使わない録音（raw）」に対応していません。［マイクの音声処理を使わない］のチェックを外して、もう一度開始してください。",
            AudioDeviceErrorKind.DeviceInUse =>
                "ほかのアプリがこのデバイスを専有しています（CAP-002）。そのアプリを閉じてから、もう一度開始してください。",
            AudioDeviceErrorKind.DeviceNotFound or AudioDeviceErrorKind.DeviceInvalidated =>
                "選択したデバイスが見つかりません。接続を確かめてから［デバイス一覧を更新］を押してください。",
            AudioDeviceErrorKind.UnsupportedFormat =>
                "このデバイスの音声形式には対応していません。別のデバイスを選んでください。",
            AudioDeviceErrorKind.AudioServiceNotRunning =>
                "Windows のオーディオ サービスが動いていません。PC を再起動してから、もう一度開始してください。",
            _ => $"デバイスを開けませんでした（{device.Message}）。",
        };
    }

    public static string NotEnoughSpace(long available, long required) =>
        $"保存先の空き容量が足りないため、録音を開始できません（FS-001）。必要: {Bytes(required)} 以上、現在: {Bytes(available)}。" +
        "不要なファイルを整理してから開始してください。録音済みの音声は自動では削除しません。";

    public static string Warning(RecordingWarningEventArgs e, TimeSpan at, string? deviceName) => e.Kind switch
    {
        RecordingWarningKind.DeviceLost =>
            $"{Clock(at)} に{SourceName(e.Source)}{(deviceName is null ? string.Empty : $"（{deviceName}）")}が切断されました（CAP-002）。" +
            "ほかの音源は録音を続けています。接続し直したら［再接続］を押してください。切断中の音声は欠損として記録されます。",
        RecordingWarningKind.QueueOverflow =>
            $"{Clock(at)} に書き込みが追いつかず、{SourceName(e.Source)}の音声の一部が欠けました（CAP-003）。安全のため録音を停止し、それまでの音声を保存しました。",
        RecordingWarningKind.WriteFailed =>
            $"{Clock(at)} に{SourceName(e.Source)}の保存でエラーが発生したため、録音を停止しました。保存済みのチャンクは残っています。",
        RecordingWarningKind.DiskSpaceLow =>
            $"{Clock(at)} に空き容量が不足したため、録音を停止して保存しました（FS-001）。音声は自動では削除しません。",
        _ => $"{Clock(at)} {e.Kind}",
    };

    public static string MicSilent(TimeSpan at) =>
        $"{Clock(at)} 時点で、マイクの音が 10 秒間届いていません。ミュートやマイクの接続を確かめてください（録音は続いています）。";

    public const string LoopbackNotice = "PC 内部音声には、選んだ出力先から鳴る通知音なども含まれます。";

    public const string IndexFailed = "ライブラリの一覧を更新できませんでした。録音は保存されています（次回の起動時に一覧を作り直します）。";

    public static string Clock(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
    };
}
