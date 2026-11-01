using System.Text.Json;
using System.Windows;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;

namespace VoiceJournal.App.Services;

/// <summary>Window placement and other per-user UI preferences (not recording data).</summary>
public sealed record UiSettings
{
    public double? MiniBarLeft { get; init; }

    public double? MiniBarTop { get; init; }
}

public interface IUiSettingsStore
{
    UiSettings Load();

    void Save(UiSettings settings);
}

/// <summary><c>%LOCALAPPDATA%\VoiceJournal\config\ui.json</c>. A missing or broken file means defaults.</summary>
public sealed class JsonUiSettingsStore(string path) : IUiSettingsStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceJournal", "config", "ui.json");

    public UiSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UiSettings>(AtomicFile.ReadAllBytes(path), VoiceJournalJson.Options) ?? new UiSettings()
                : new UiSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UiSettings();
        }
    }

    public void Save(UiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(settings, VoiceJournalJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that cannot be saved is not worth interrupting the user for.
        }
    }
}

/// <summary>Where the mini bar goes and when it shows (pure functions, unit tested).</summary>
public static class MiniBarPlacement
{
    public const double Margin = 16;

    /// <summary>Only while recording and minimized; otherwise the window minimizes to the taskbar as usual.</summary>
    public static bool ShouldShow(WindowState state, bool recordingActive) => state == WindowState.Minimized && recordingActive;

    /// <summary>
    /// The saved position when at least half of the bar is still on screen (monitors may have changed since),
    /// nudged back inside the screen; otherwise the bottom-right corner of the work area, above the taskbar.
    /// </summary>
    /// <param name="slack">
    /// Transparent margin around the visible bar (room for its shadow). It may hang off the screen edge, so a bar
    /// dragged into a corner keeps its place (2026-11-01: a strict "fully on screen" check reset it every time).
    /// </param>
    public static Point Resolve(UiSettings settings, Rect workArea, Rect virtualScreen, Size size, double slack = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MiniBarLeft is { } left && settings.MiniBarTop is { } top)
        {
            var bar = new Rect(left, top, size.Width, size.Height);
            var visible = Rect.Intersect(bar, virtualScreen);
            if (!visible.IsEmpty && visible.Width * visible.Height >= bar.Width * bar.Height / 2)
            {
                var minX = virtualScreen.Left - slack;
                var minY = virtualScreen.Top - slack;
                var maxX = Math.Max(minX, virtualScreen.Right + slack - size.Width);
                var maxY = Math.Max(minY, virtualScreen.Bottom + slack - size.Height);
                return new Point(Math.Clamp(left, minX, maxX), Math.Clamp(top, minY, maxY));
            }
        }
        return new Point(workArea.Right - size.Width - Margin, workArea.Bottom - size.Height - Margin);
    }
}
