using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.AppTests;

public sealed class LlmSettingsTests
{
    [Fact]
    public void Defaults_AreTheValuesMeasuredInPhaseD2()
    {
        var generation = new LlmSettings().ToGeneration();

        Assert.Equal(("gemma-3-4b-it", 16384, 3072, 3000), (generation.ModelId, generation.ContextLength, generation.OutputReserve, generation.MaxPieceTokens));
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
        Assert.Equal(3072, new LlmSettings { ContextLength = 8192, OutputReserve = 6000 }.Sanitized().OutputReserve);
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
