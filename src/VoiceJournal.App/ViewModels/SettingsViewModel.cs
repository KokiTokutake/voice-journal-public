using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.App.ViewModels;

/// <summary>
/// UI-12 settings (detail spec 18.2): transcription (model, VAD, gain, threads) and record generation on the bundled
/// llama.cpp server (model id, context length C, output reserve O, piece size). Saved at once; applied from the next
/// job (a queued job keeps its snapshot). "ツールを確認" checks the registered executable and models; "接続を確認"
/// starts the bundled server for the model.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISttSettingsStore _store;
    private readonly ISttEngine _engine;
    private readonly ILlmSettingsStore? _llmStore;
    private readonly ILlmClient? _llm;
    private readonly IFreeSpaceProbe? _disk;
    private readonly LlmToolSettings? _llmTools;
    private readonly long _minFreeBytes;
    private bool _loading;

    public SettingsViewModel(ISttSettingsStore store, ISttEngine engine, SttToolSettings tools, RecordingEnvironment environment,
        ILlmSettingsStore? llmStore = null, ILlmClient? llm = null, IFreeSpaceProbe? disk = null, LlmToolSettings? llmTools = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _store = store;
        _engine = engine;
        _llmStore = llmStore;
        _llm = llm;
        _disk = disk;
        _llmTools = llmTools;
        _minFreeBytes = environment.MinFreeBytes;
        Tools = tools;
        WorkspaceRoot = environment.WorkspaceRoot;
        ModelOptions = [.. tools.Models.Keys.Order(StringComparer.Ordinal).Select(m => new TranscriptViewModel.ModelOption(m, UserMessages.ModelLabel(m)))];
        ThreadOptions = [.. Enumerable.Range(1, Environment.ProcessorCount)];
        _loading = true;
        var c = store.Load();
        ModelName = tools.Models.ContainsKey(c.ModelName) ? c.ModelName : JsonSttSettingsStore.Defaults.ModelName;
        UseVad = c.UseVad;
        NormalizeGain = c.NormalizeGain;
        Threads = Math.Clamp(c.Threads, 1, Environment.ProcessorCount);
        if (llmStore is not null) ShowLlm(llmStore.Load());
        _loading = false;
        FreeSpaceText = FreeSpace();
    }

    /// <summary>The free space of the workspace's drive and the minimum a recording needs.</summary>
    [ObservableProperty]
    public partial string FreeSpaceText { get; set; } = string.Empty;

    private string FreeSpace()
    {
        if (_disk is null) return string.Empty;
        try
        {
            return $"空き容量 {UserMessages.Bytes(_disk.GetAvailableBytes(WorkspaceRoot))}（録音を始めるには {UserMessages.Bytes(_minFreeBytes)} 以上が必要）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "空き容量 不明";
        }
    }

    public ObservableCollection<string> EnvironmentStatus { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckEnvironmentCommand))]
    public partial bool IsCheckingEnvironment { get; set; }

    private bool CanCheckEnvironment() => !IsCheckingEnvironment;

    /// <summary>
    /// UI-01: checks in one go what the app needs, without the network: the workspace can be written and has room, the
    /// selected whisper model and the record engine's files are here and match their registered SHA-256 (the engine is
    /// not started). When all pass, the app works offline (recording, transcription and records).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckEnvironment))]
    private async Task CheckEnvironmentAsync()
    {
        IsCheckingEnvironment = true;
        EnvironmentStatus.Clear();
        EnvironmentStatus.Add("確認しています…（モデルの SHA-256 の計算に数秒かかります）");
        var lines = new List<string>();
        var ok = true;
        var probe = Path.Combine(WorkspaceRoot, $".write-check-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(probe, [1]);
            lines.Add($"✓ 保存先に書き込めます（{WorkspaceRoot}）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ok = false;
            lines.Add($"✗ 保存先に書き込めません（{ex.Message}）。");
        }
        finally
        {
            try { File.Delete(probe); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (_disk is not null)
        {
            FreeSpaceText = FreeSpace();
            long? free = null;
            try { free = _disk.GetAvailableBytes(WorkspaceRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            if (free is not { } f) lines.Add("⚠ 空き容量を調べられませんでした。");
            else if (f < _minFreeBytes)
            {
                ok = false;
                lines.Add($"✗ {UserMessages.NotEnoughSpace(f, _minFreeBytes)}");
            }
            else lines.Add($"✓ 空き容量 {UserMessages.Bytes(f)}");
        }
        try
        {
            var info = await _engine.VerifyAsync(new SttConfiguration { ModelName = ModelName, UseVad = UseVad }, CancellationToken.None);
            lines.Add($"✓ 文字起こし: whisper-cli（{info.EngineName} {info.EngineVersion}）と {UserMessages.ModelLabel(ModelName)}" + (UseVad ? "、VAD モデル" : string.Empty));
        }
        catch (SttException ex)
        {
            ok = false;
            lines.Add($"✗ 文字起こし: {UserMessages.SttError(ex.Code)}（{ex.Message}）");
        }
        if (_llmTools is not null && _llmStore is not null)
        {
            var modelId = _llmStore.Load().ModelId;
            try
            {
                if (!_llmTools.Models.TryGetValue(modelId, out var model))
                    throw new LlmUnavailableException($"モデル「{modelId}」は推論エンジンに登録されていません。");
                await Task.Run(() => LlmToolCheck.Verify(_llmTools.LlamaServer, model));
                lines.Add($"✓ 記録の作成: 推論エンジン（llama.cpp {_llmTools.LlamaServer.Version}）とモデル {modelId}（起動はしていません。［接続を確認］で起動を試せます）");
            }
            catch (LlmUnavailableException ex)
            {
                ok = false;
                lines.Add($"✗ 記録の作成: {ex.Message}");
            }
        }
        lines.Add(ok
            ? "✓ 必要なファイルはすべてこの PC にあります。インターネットに接続しなくても、録音・文字起こし・記録の作成ができます（このアプリは外部と通信しません）。"
            : "✗ の項目を直すまで、その機能は使えません。原音の録音は、文字起こしや記録のツールがなくてもできます。");
        EnvironmentStatus.Clear();
        foreach (var l in lines) EnvironmentStatus.Add(l);
        IsCheckingEnvironment = false;
    }

    private void ShowLlm(LlmSettings settings)
    {
        LlmModelId = settings.ModelId;
        LlmContextLength = settings.ContextLength;
        LlmOutputReserve = settings.OutputReserve;
        LlmPieceTokens = settings.MaxPieceTokens;
    }

    public SttToolSettings Tools { get; }

    public string WorkspaceRoot { get; }

    public string WhisperPath => Tools.WhisperCli.Path;

    public IReadOnlyList<TranscriptViewModel.ModelOption> ModelOptions { get; }

    public IReadOnlyList<int> ThreadOptions { get; }

    [ObservableProperty]
    public partial string ModelName { get; set; } = JsonSttSettingsStore.Defaults.ModelName;

    [ObservableProperty]
    public partial bool UseVad { get; set; } = true;

    [ObservableProperty]
    public partial bool NormalizeGain { get; set; } = true;

    [ObservableProperty]
    public partial int Threads { get; set; } = 1;

    [ObservableProperty]
    public partial string? Message { get; set; }

    public ObservableCollection<string> ToolStatus { get; } = [];

    public bool HasLlm => _llmStore is not null;

    [ObservableProperty]
    public partial string LlmModelId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int LlmContextLength { get; set; }

    [ObservableProperty]
    public partial int LlmOutputReserve { get; set; }

    [ObservableProperty]
    public partial int LlmPieceTokens { get; set; }

    [ObservableProperty]
    public partial string? LlmMessage { get; set; }

    [ObservableProperty]
    public partial string? LlmStatus { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckLlmCommand))]
    public partial bool IsCheckingLlm { get; set; }

    partial void OnLlmModelIdChanged(string value) => SaveLlm();

    partial void OnLlmContextLengthChanged(int value) => SaveLlm();

    partial void OnLlmOutputReserveChanged(int value) => SaveLlm();

    partial void OnLlmPieceTokensChanged(int value) => SaveLlm();

    /// <summary>Saves the record settings; out-of-range numbers are corrected.</summary>
    private void SaveLlm()
    {
        if (_loading || _llmStore is null) return;
        var wanted = _llmStore.Load() with
        {
            ModelId = LlmModelId,
            ContextLength = LlmContextLength,
            OutputReserve = LlmOutputReserve,
            MaxPieceTokens = LlmPieceTokens,
        };
        var saved = wanted.Sanitized();
        try
        {
            _llmStore.Save(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LlmMessage = $"設定を保存できませんでした（{ex.Message}）。";
            return;
        }
        LlmMessage = saved == wanted
            ? "保存しました。次に始める記録の作成から使われます。"
            : "使えない値を直して保存しました（コンテキスト長は 4096 以上、出力の予約はその半分以下、区間は 500〜32768）。";
        if (saved != wanted)
        {
            _loading = true;
            ShowLlm(saved);
            _loading = false;
        }
    }

    private bool CanCheckLlm() => _llm is not null && !IsCheckingLlm;

    /// <summary>Starts the bundled server for the model and reports the context length it got (LLM-001).</summary>
    [RelayCommand(CanExecute = nameof(CanCheckLlm))]
    private async Task CheckLlmAsync()
    {
        if (_llm is null || _llmStore is null) return;
        IsCheckingLlm = true;
        LlmStatus = "確認しています…";
        var settings = _llmStore.Load();
        try
        {
            var model = await _llm.GetModelAsync(settings.ModelId, CancellationToken.None);
            LlmStatus = $"✓ 推論エンジン（llama.cpp）でモデル {model.Id} を起動できました" + (model.LoadedContextLength is { } n ? $"（コンテキスト長 {n}）。" : "。") + "使わなければ 1 分ほどで終了し、メモリを返します。" +
                (model.LoadedContextLength is { } loaded && loaded < settings.ContextLength
                    ? $"\n⚠ 設定のコンテキスト長 {settings.ContextLength} より短いため、{loaded} で分割します。"
                    : string.Empty);
        }
        catch (LlmUnavailableException ex)
        {
            LlmStatus = ex.Message.Contains("空きメモリ", StringComparison.Ordinal)
                ? $"✗ LLM-001: {ex.Message}"
                : $"✗ LLM-001: {ex.Message}\n推論エンジンは %LOCALAPPDATA%\\VoiceJournal\\tools\\llama.cpp、モデルは models\\llm に置きます（登録は config\\llm-tools.json）。";
        }
        catch (LlmProtocolException ex)
        {
            LlmStatus = $"✗ 推論エンジンの応答を確かめられませんでした（{ex.Message}）。";
        }
        finally
        {
            IsCheckingLlm = false;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyToolsCommand))]
    public partial bool IsVerifying { get; set; }

    partial void OnModelNameChanged(string value) => Save();

    partial void OnUseVadChanged(bool value) => Save();

    partial void OnNormalizeGainChanged(bool value) => Save();

    partial void OnThreadsChanged(int value) => Save();

    private void Save()
    {
        if (_loading) return;
        try
        {
            _store.Save(_store.Load() with { ModelName = ModelName, UseVad = UseVad, NormalizeGain = NormalizeGain, Threads = Threads });
            Message = "保存しました。次に始める文字起こしから使われます（順番待ちのものは、入れたときの設定のままです）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"設定を保存できませんでした（{ex.Message}）。";
        }
    }

    private bool CanVerify() => !IsVerifying;

    /// <summary>Checks whisper-cli and every registered model against their SHA-256 (STT-001).</summary>
    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyToolsAsync()
    {
        IsVerifying = true;
        ToolStatus.Clear();
        ToolStatus.Add("確認しています…（モデルの SHA-256 計算に数秒かかります）");
        var lines = new List<string>();
        foreach (var model in Tools.Models.Keys.Order(StringComparer.Ordinal))
        {
            try
            {
                var info = await _engine.VerifyAsync(new SttConfiguration { ModelName = model, UseVad = UseVad }, CancellationToken.None);
                if (lines.Count == 0) lines.Add($"✓ whisper-cli（{info.EngineName} {info.EngineVersion}）");
                lines.Add($"✓ {UserMessages.ModelLabel(model)}");
            }
            catch (SttException ex)
            {
                lines.Add($"✗ {UserMessages.ModelLabel(model)}: {UserMessages.SttError(ex.Code)}（{ex.Message}）");
            }
        }
        if (UseVad) lines.Add(lines.Any(l => l.StartsWith('✓')) ? "✓ VAD モデル（Silero v6.2.0）" : "VAD モデルは上の確認に含まれています");
        ToolStatus.Clear();
        foreach (var l in lines) ToolStatus.Add(l);
        IsVerifying = false;
    }
}
