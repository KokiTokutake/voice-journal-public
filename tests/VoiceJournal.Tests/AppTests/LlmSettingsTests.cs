using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Tests.Core.Analysis;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.AppTests;

public sealed class LlmSettingsTests
{
    [Fact]
    public void Defaults_AreTheValuesMeasuredInPhaseD2()
    {
        var generation = new LlmSettings().ToGeneration();

        Assert.Equal(("gemma-4-e2b-it", 16384, 4096, 3000), (generation.ModelId, generation.ContextLength, generation.OutputReserve, generation.MaxPieceTokens));
        Assert.Equal(1, generation.Parallelism);
    }

    [Fact]
    public void TheStore_RoundTrips_AndFallsBackToTheDefaultsForAMissingOrBrokenFile()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "config", "llm.json");
        var store = new JsonLlmSettingsStore(path);
        Assert.Equal(new LlmSettings(), store.Load());

        store.Save(new LlmSettings { ContextLength = 8192, OutputReserve = 2048, MaxPieceTokens = 2000 });
        Assert.Equal((8192, 2048, 2000), (store.Load().ContextLength, store.Load().OutputReserve, store.Load().MaxPieceTokens));

        File.WriteAllText(path, "{ broken");
        Assert.Equal(new LlmSettings(), store.Load());
    }

    [Fact]
    public void HandEditedValues_OutsideTheWorkableRange_AreReplaced()
    {
        var sanitized = new LlmSettings { Url = " ", ModelId = "", ContextLength = 100, OutputReserve = 999_999, MaxPieceTokens = 0, Temperature = 5, RequestTimeoutMinutes = 0 }.Sanitized();

        Assert.Equal(new LlmSettings(), sanitized);
        Assert.Equal(4096, new LlmSettings { ContextLength = 8192, OutputReserve = 4096 }.Sanitized().OutputReserve); // at most half of C
        Assert.Equal(4096, new LlmSettings { ContextLength = 8192, OutputReserve = 6000 }.Sanitized().OutputReserve);
    }

    [Theory]
    [InlineData("http://192.168.1.10:1234/v1")]
    [InlineData("http://example.com/v1")]
    [InlineData("not a url")]
    public async Task AUrlOffLoopback_IsRefusedAsUnavailable_WithoutSendingAnything(string url)
    {
        using var client = new ConfiguredLlmClient(() => (url, TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.GetModelAsync("gemma-3-4b-it", CancellationToken.None));
    }

    [Fact]
    public async Task AChangedUrl_IsUsedForTheNextRequest()
    {
        var url = "http://192.168.1.10:1234/v1";
        using var client = new ConfiguredLlmClient(() => (url, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.GetModelAsync("m", CancellationToken.None));

        url = "http://127.0.0.1:9/v1"; // loopback, nothing listening (discard port)
        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.GetModelAsync("m", CancellationToken.None));
        Assert.Contains("接続できません", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class LlmSettingsViewModelTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-llmset-");
    private readonly MemoryLlmSettingsStore _store = new();
    private readonly EchoLlm _llm = new() { LoadedContext = 8192 };
    private readonly VoiceJournal.App.ViewModels.SettingsViewModel _vm;

    public LlmSettingsViewModelTests()
    {
        _vm = new VoiceJournal.App.ViewModels.SettingsViewModel(new MemorySttSettingsStore(), new VoiceJournal.Tests.Core.Transcription.FakeSttEngine(),
            VoiceJournal.Infrastructure.Stt.SttToolSettings.Defaults(_dir.Path), new RecordingEnvironment(_dir.Path, 0), _store, _llm);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TheSavedValues_AreShown_AndAChangeIsSavedForTheNextJob()
    {
        Assert.True(_vm.HasLlm);
        Assert.Equal((LlmEndpoint.Default, "gemma-4-e2b-it", 16384, 4096, 3000), (_vm.LlmUrl, _vm.LlmModelId, _vm.LlmContextLength, _vm.LlmOutputReserve, _vm.LlmPieceTokens));

        _vm.LlmPieceTokens = 2000;

        Assert.Equal(2000, _store.Current.MaxPieceTokens);
        Assert.Contains("次に始める記録の作成から", _vm.LlmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AUrlOffLoopback_IsNotSaved()
    {
        _vm.UseLmStudio = true;
        _vm.LlmUrl = "http://192.168.1.20:1234/v1";

        Assert.Equal(LlmEndpoint.Default, _store.Current.Url);
        Assert.Contains("保存していません", _vm.LlmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnworkableValue_IsCorrected_AndShownCorrected()
    {
        _vm.LlmContextLength = 8192;
        _vm.LlmOutputReserve = 6000; // more than half of C

        Assert.Equal(4096, _store.Current.OutputReserve);
        Assert.Equal(4096, _vm.LlmOutputReserve);
        Assert.Contains("直して保存しました", _vm.LlmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckingTheConnection_ReportsTheLoadedContext_AndWarnsWhenItIsShorter()
    {
        _vm.UseLmStudio = true;
        await _vm.CheckLlmCommand.ExecuteAsync(null);

        Assert.StartsWith("✓", _vm.LlmStatus, StringComparison.Ordinal);
        Assert.Contains("コンテキスト長 8192", _vm.LlmStatus, StringComparison.Ordinal);
        Assert.Contains("⚠", _vm.LlmStatus, StringComparison.Ordinal);

        _llm.Unloaded = true;
        await _vm.CheckLlmCommand.ExecuteAsync(null);
        Assert.Contains("自動で読み込みます", _vm.LlmStatus, StringComparison.Ordinal);

        _llm.Unavailable = true;
        await _vm.CheckLlmCommand.ExecuteAsync(null);
        Assert.StartsWith("✗ LLM-001", _vm.LlmStatus, StringComparison.Ordinal);
        Assert.Contains("LM Studio を起動し", _vm.LlmStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBundledEngine_IsTheDefault_ItsCheckStartsIt_AndTheEngineChoiceIsSaved()
    {
        Assert.True(_vm.UseBundledEngine);
        await _vm.CheckLlmCommand.ExecuteAsync(null);
        Assert.StartsWith("✓ 推論エンジン", _vm.LlmStatus, StringComparison.Ordinal);

        _llm.Unavailable = true;
        await _vm.CheckLlmCommand.ExecuteAsync(null);
        Assert.Contains("llm-tools.json", _vm.LlmStatus, StringComparison.Ordinal);

        _vm.UseLmStudio = true;
        Assert.Equal(VoiceJournal.App.Services.LlmEngine.LmStudio, _store.Current.Engine);
        _vm.UseBundledEngine = true;
        Assert.Equal(VoiceJournal.App.Services.LlmEngine.Bundled, _store.Current.Engine);
    }

    [Fact]
    public void ThePlaceholderUrl_IsNotCheckedWhileTheBundledEngineIsUsed()
    {
        _vm.LlmUrl = "http://192.168.1.20:1234/v1"; // ignored: the bundled engine has no URL

        Assert.DoesNotContain("保存していません", _vm.LlmMessage ?? "", StringComparison.Ordinal);
    }
}
