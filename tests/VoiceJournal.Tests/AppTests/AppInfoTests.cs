using VoiceJournal.App.Services;

namespace VoiceJournal.Tests.AppTests;

/// <summary>The version shown in the settings screen and written to the diagnostics report.</summary>
public sealed class AppInfoTests
{
    [Theory]
    [InlineData("1.0.0-beta.1+7b7c370abcdef0123456789", "1.0.0-beta.1（7b7c370）")]
    [InlineData("1.0.0-beta.1+abc", "1.0.0-beta.1（abc）")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta.1")]
    [InlineData("1.0.0+", "1.0.0")]
    public void Format_shows_the_version_and_a_short_commit(string informational, string expected) =>
        Assert.Equal(expected, AppInfo.Format(informational));

    [Fact]
    public void The_build_carries_a_version_from_Directory_Build_props()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", AppInfo.Version);
        Assert.StartsWith(AppInfo.Version, AppInfo.InformationalVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupOptions_ParseTheSelfTestReport()
    {
        Assert.Equal(@"C:\t\self-test.json", StartupOptions.Parse(["--self-test", @"C:\t\self-test.json"]).SelfTestReport);
        Assert.Null(StartupOptions.Parse(["--self-test"]).SelfTestReport);
        Assert.Null(StartupOptions.Parse([]).SelfTestReport);
    }
}
