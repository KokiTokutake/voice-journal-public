#pragma warning disable WPF0001 // ThemeMode is experimental in WPF; the app opts in (see VoiceJournal.App.csproj).
using System.Windows;
using VoiceJournal.App.Services;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.AppTests;

public sealed class UiBehaviorTests : IDisposable
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1032);          // 1080p with a 48 px taskbar
    private static readonly Rect VirtualScreen = new(0, 0, 1920, 1080);
    private static readonly Size Bar = new(360, 80);

    private readonly TempDirectory _dir = new("vj-ui-");

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(WindowState.Minimized, true, true)]
    [InlineData(WindowState.Minimized, false, false)] // not recording: normal minimize to the taskbar
    [InlineData(WindowState.Normal, true, false)]
    [InlineData(WindowState.Maximized, true, false)]
    public void MiniBar_ShowsOnlyWhileRecordingAndMinimized(WindowState state, bool recording, bool expected)
    {
        Assert.Equal(expected, MiniBarPlacement.ShouldShow(state, recording));
    }

    [Fact]
    public void MiniBar_DefaultsToTheBottomRightAboveTheTaskbar()
    {
        var place = MiniBarPlacement.Resolve(new UiSettings(), WorkArea, VirtualScreen, Bar);
        Assert.Equal(new Point(1920 - 360 - 16, 1032 - 80 - 16), place);
    }

    [Fact]
    public void MiniBar_UsesTheSavedPosition_WhenItIsStillOnScreen()
    {
        var place = MiniBarPlacement.Resolve(new UiSettings { MiniBarLeft = 800, MiniBarTop = 20 }, WorkArea, VirtualScreen, Bar);
        Assert.Equal(new Point(800, 20), place);
    }

    [Fact]
    public void MiniBar_KeepsAPositionDraggedIntoTheCorner() // the reported case: top-right corner, 125 % scaling
    {
        var workArea = new Rect(0, 0, 1792, 1072);
        var screen = new Rect(0, 0, 1792, 1120);
        var bar = new Size(362.4, 80.8); // includes the 12 px transparent shadow margin, which hangs off the edge
        var place = MiniBarPlacement.Resolve(new UiSettings { MiniBarLeft = 1441.6, MiniBarTop = 0 }, workArea, screen, bar, slack: 12);
        Assert.Equal(new Point(1441.6, 0), place);
    }

    [Theory]
    [InlineData(1700, 500, 1572, 500)] // mostly on screen but past the right edge: pulled back (12 px may hang off)
    [InlineData(-50, 500, -12, 500)]
    [InlineData(800, -40, 800, -12)]
    public void MiniBar_IsNudgedBackOnScreen(double left, double top, double expectedLeft, double expectedTop)
    {
        var place = MiniBarPlacement.Resolve(new UiSettings { MiniBarLeft = left, MiniBarTop = top }, WorkArea, VirtualScreen, Bar, slack: 12);
        Assert.Equal(new Point(expectedLeft, expectedTop), place);
    }

    [Theory]
    [InlineData(2500, 100)]  // on a monitor that is no longer connected
    [InlineData(1800, 500)]  // less than half visible
    public void MiniBar_FallsBackToTheCorner_WhenMostlyOffScreen(double left, double top)
    {
        var place = MiniBarPlacement.Resolve(new UiSettings { MiniBarLeft = left, MiniBarTop = top }, WorkArea, VirtualScreen, Bar, slack: 12);
        Assert.Equal(new Point(1544, 936), place);
    }

    [Fact]
    public void UiSettings_RoundTrip_AndToleratesABrokenFile()
    {
        var path = _dir.File("config/ui.json");
        var store = new JsonUiSettingsStore(path);
        Assert.Equal(new UiSettings(), store.Load()); // missing file: defaults

        store.Save(new UiSettings { MiniBarLeft = 12.5, MiniBarTop = 400 });
        Assert.Equal(new UiSettings { MiniBarLeft = 12.5, MiniBarTop = 400 }, new JsonUiSettingsStore(path).Load());
        Assert.Contains("\"mini_bar_left\": 12.5", File.ReadAllText(path), StringComparison.Ordinal);

        File.WriteAllText(path, "{ broken");
        Assert.Equal(new UiSettings(), store.Load());
    }

    [Fact]
    public void StartupOptions_ParseWorkspaceAndTheme()
    {
        var options = StartupOptions.Parse(["--workspace", @"C:\tmp\ws", "--theme", "dark"]);
        Assert.Equal(@"C:\tmp\ws", options.WorkspaceRoot);
        Assert.Equal(ThemeMode.Dark, options.Theme);

        Assert.Equal(new StartupOptions(null, null), StartupOptions.Parse([]));
        Assert.Null(StartupOptions.Parse(["--theme", "neon"]).Theme);
        Assert.Null(StartupOptions.Parse(["--workspace"]).WorkspaceRoot); // value missing
    }
}
