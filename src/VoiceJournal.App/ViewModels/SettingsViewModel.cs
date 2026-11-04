using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.App.ViewModels;

/// <summary>
/// UI-12 settings (detail spec 18.2): transcription (model, VAD, gain, threads) and record generation (LM Studio URL,
/// model id, context length C, output reserve O, piece size). Saved at once; applied from the next job (a queued job
/// keeps its snapshot). "ツールを確認" checks the registered executable and models; "接続を確認" asks LM Studio.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISttSettingsStore _store;
    private readonly ISttEngine _engine;
    private readonly ILlmSettingsStore? _llmStore;
    private readonly ILlmClient? _llm;
    private bool _loading;

    public SettingsViewModel(ISttSettingsStore store, ISttEngine engine, SttToolSettings tools, RecordingEnvironment environment,
        ILlmSettingsStore? llmStore = null, ILlmClient? llm = null)
    {
        _store = store;
        _engine = engine;
        _llmStore = llmStore;
        _llm = llm;
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
    }

    private void ShowLlm(LlmSettings settings)
    {
        LlmUrl = settings.Url;
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
    public partial string LlmUrl { get; set; } = LlmEndpoint.Default;

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

    partial void OnLlmUrlChanged(string value) => SaveLlm();

    partial void OnLlmModelIdChanged(string value) => SaveLlm();

    partial void OnLlmContextLengthChanged(int value) => SaveLlm();

    partial void OnLlmOutputReserveChanged(int value) => SaveLlm();

    partial void OnLlmPieceTokensChanged(int value) => SaveLlm();

    /// <summary>Saves the LM Studio settings; a URL off loopback is refused (detail spec 9.1) and out-of-range numbers are corrected.</summary>
    private void SaveLlm()
    {
        if (_loading || _llmStore is null) return;
        try
        {
            LlmEndpoint.Parse(LlmUrl);
        }
        catch (ArgumentException ex)
        {
            LlmMessage = ex.Message + "（保存していません）";
            return;
        }
        var wanted = _llmStore.Load() with
        {
            Url = LlmUrl,
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

    /// <summary>Asks LM Studio whether it runs and has the model, and with which context length (LLM-001).</summary>
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
            LlmStatus = model.Loaded switch
            {
                false => $"✓ LM Studio に接続できました。モデル {model.Id} は今は読み込まれていません（記録を作るときに自動で読み込みます）。",
                _ => $"✓ LM Studio に接続できました。モデル {model.Id} は読み込み済みです" +
                    (model.LoadedContextLength is { } c ? $"（コンテキスト長 {c}）。" : "。") +
                    (model.LoadedContextLength is { } loaded && loaded < settings.ContextLength
                        ? $"\n⚠ 設定のコンテキスト長 {settings.ContextLength} より短いため、{loaded} で分割します。LM Studio で {settings.ContextLength} で読み込むと速くなります。"
                        : string.Empty),
            };
        }
        catch (LlmUnavailableException ex)
        {
            LlmStatus = $"✗ LLM-001: {ex.Message}\nLM Studio を起動し、Developer の画面でサーバーを開始（Status: Running）して、モデル「{settings.ModelId}」を読み込んでください。";
        }
        catch (LlmProtocolException ex)
        {
            LlmStatus = $"✗ LM Studio の応答を確かめられませんでした（{ex.Message}）。";
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
