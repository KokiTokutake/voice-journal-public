using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.App.ViewModels;

/// <summary>
/// UI-12 settings, the transcription part (detail spec 18.2): model, VAD, gain and threads. Saved at once; applied
/// from the next job (a queued job keeps its snapshot). "ツールを確認" checks the registered executable and models.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISttSettingsStore _store;
    private readonly ISttEngine _engine;
    private bool _loading;

    public SettingsViewModel(ISttSettingsStore store, ISttEngine engine, SttToolSettings tools, RecordingEnvironment environment)
    {
        _store = store;
        _engine = engine;
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
        _loading = false;
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
