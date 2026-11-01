using System.Globalization;
using VoiceJournal.Core.Capture;
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
        _ => state.ToString(),
    };

    /// <summary>Error code (detail spec 19) and what the user can do.</summary>
    public static string SttError(SttErrorCode? code) => code switch
    {
        null => string.Empty,
        SttErrorCode.ToolMissing => "STT-001: whisper-cli またはモデルが見つからないか、登録した SHA-256 と一致しません。設定画面の「ツールを確認」で確かめてください。録音は残っています。",
        SttErrorCode.RunFailed => "STT-002: 文字起こしが異常終了したか、時間内に終わりませんでした。終わった区間は残っています。「やり直す」で残りだけ実行します。",
        SttErrorCode.NotEnoughMemory => "STT-002: 空きメモリが足りません。LM Studio などでモデルを読み込んでいたら、アンロードしてからやり直してください。",
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
