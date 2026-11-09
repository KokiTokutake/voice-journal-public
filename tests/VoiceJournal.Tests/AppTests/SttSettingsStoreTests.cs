using VoiceJournal.App.Services;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Tests.AppTests;

/// <summary>The default recognition model, and what a job uses when the chosen one is not registered.</summary>
public sealed class SttSettingsStoreTests
{
    [Fact]
    public void The_default_model_is_medium() =>
        Assert.Equal("ggml-medium.bin", JsonSttSettingsStore.Defaults.ModelName);

    [Theory]
    [InlineData("ggml-small.bin", new[] { "ggml-small.bin", "ggml-medium.bin" }, "ggml-small.bin")] // the user's choice is kept
    [InlineData("ggml-gone.bin", new[] { "ggml-small.bin", "ggml-medium.bin" }, "ggml-medium.bin")] // else the default
    [InlineData("ggml-medium.bin", new[] { "ggml-small.bin" }, "ggml-small.bin")] // medium not installed: small
    [InlineData("ggml-medium.bin", new[] { "ggml-z.bin", "ggml-a.bin" }, "ggml-a.bin")] // else the first registered
    [InlineData("ggml-medium.bin", new string[0], "ggml-medium.bin")] // nothing registered: unchanged
    public void A_registered_model_is_chosen(string chosen, string[] registered, string expected)
    {
        var configuration = new SttConfiguration { ModelName = chosen, Threads = 3 };

        var result = JsonSttSettingsStore.WithRegisteredModel(configuration, registered);

        Assert.Equal(expected, result.ModelName);
        Assert.Equal(3, result.Threads);
    }

    [Fact]
    public void A_missing_settings_file_means_the_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vj-stt-{Guid.NewGuid():N}.json");
        Assert.Equal("ggml-medium.bin", new JsonSttSettingsStore(path).Load().ModelName);
    }
}
