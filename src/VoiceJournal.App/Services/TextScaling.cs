using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VoiceJournal.App.Services;

/// <summary>
/// Windows 「テキストのサイズ」 (Settings → Accessibility → Text size, 100–225 %) for WPF, which does not apply it by
/// itself (detail spec 18.1, AT-25: text enlarged to 200 %). Every font size the app and the Fluent theme use is a
/// resource; they are multiplied by the factor, and again whenever the setting changes.
/// </summary>
public static class TextScaling
{
    public const int MinPercent = 100;
    public const int MaxPercent = 225;

    /// <summary>Font size resources of the WPF Fluent theme (PresentationFramework.Fluent).</summary>
    public static readonly IReadOnlyList<string> ThemeKeys =
    [
        "ControlContentThemeFontSize", "ContentControlFontSize", "BodyTextBlockFontSize", "CaptionTextBlockFontSize",
        "SubtitleTextBlockFontSize", "TitleTextBlockFontSize", "TitleLargeTextBlockFontSize", "DisplayTextBlockFontSize",
        "DefaultDataGridFontSize", "GroupBoxHeaderFontSize", "TreeViewItemFontSize", "StatusFontSize",
        "DefaultIconFontSize", "TextBoxIconFontSize", "DataGridSortIndicatorIconFontSize",
    ];

    /// <summary>The percent applied last (columns of lists follow it when they are shown).</summary>
    public static int CurrentPercent { get; private set; } = MinPercent;

    private sealed record OriginalColumn(DataGridLength Width, double MinWidth);

    private static readonly ConditionalWeakTable<DataGridColumn, OriginalColumn> ColumnWidths = [];

    /// <summary>Prefixes of the app's own resources that grow with the text (App.xaml): font sizes and minimum page heights.</summary>
    public static readonly IReadOnlyList<string> AppKeyPrefixes = ["VjFont", "VjSize"];

    /// <summary>The Windows text size in percent (100 when unset or unreadable).</summary>
    public static int SystemPercent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int value ? Math.Clamp(value, MinPercent, MaxPercent) : MinPercent;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return MinPercent;
        }
    }

    /// <summary>
    /// Sets every font size resource to its original value × <paramref name="percent"/> / 100. The originals are read
    /// once (from the app and the theme) and kept in <paramref name="originals"/>, so applying again does not compound.
    /// </summary>
    public static void Apply(ResourceDictionary target, Func<string, object?> find, Dictionary<string, double> originals, int percent)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(originals);
        if (originals.Count == 0)
        {
            foreach (var key in ThemeKeys.Concat(AppKeys(target)))
                if (find(key) is double size) originals[key] = size;
        }
        CurrentPercent = Math.Clamp(percent, MinPercent, MaxPercent);
        var factor = CurrentPercent / 100.0;
        foreach (var (key, size) in originals) target[key] = Math.Round(size * factor, 1);
    }

    /// <summary>Width a star column gets per unit of weight while the text is enlarged.</summary>
    public const double StarWidth = 220;

    /// <summary>
    /// The width of a list column at <paramref name="percent"/>: fixed widths grow with the text. While enlarged, a star
    /// column becomes fixed too (weight × <see cref="StarWidth"/> × factor): with a star column the list squeezes every
    /// column down to its minimum instead of scrolling sideways. At 100 % everything stays as designed.
    /// </summary>
    public static DataGridLength ScaledWidth(DataGridLength original, int percent)
    {
        var factor = Math.Clamp(percent, MinPercent, MaxPercent) / 100.0;
        if (original.IsAbsolute) return new DataGridLength(Math.Round(original.Value * factor));
        if (original.IsStar && percent > MinPercent) return new DataGridLength(Math.Round(original.Value * StarWidth * factor));
        return original;
    }

    /// <summary>
    /// Attached to a list (<c>services:TextScaling.ScaleColumns="True"</c>): its fixed-width columns widen with the text
    /// each time it is shown (a class handler for Loaded is not reliable: WPF only raises Loaded where a handler is attached).
    /// </summary>
    public static readonly DependencyProperty ScaleColumnsProperty = DependencyProperty.RegisterAttached(
        "ScaleColumns", typeof(bool), typeof(TextScaling), new PropertyMetadata(false, OnScaleColumnsChanged));

    public static bool GetScaleColumns(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(ScaleColumnsProperty);

    public static void SetScaleColumns(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(ScaleColumnsProperty, value);

    private static void OnScaleColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.Loaded -= OnGridLoaded;
        if (e.NewValue is true) grid.Loaded += OnGridLoaded;
    }

    private static void OnGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        foreach (var column in grid.Columns)
        {
            var saved = ColumnWidths.GetValue(column, c => new OriginalColumn(c.Width, c.MinWidth));
            var original = saved.Width;
            if (!original.IsAbsolute && !original.IsStar) continue;
            var target = ScaledWidth(original, CurrentPercent);
            if (!column.Width.Equals(target)) column.Width = target; // setting even the same width makes the list share out its columns again
            // A list with a star column squeezes the other columns down to their minimum when it is narrow; keep them at
            // their width and let the list scroll sideways instead.
            // back to the original minimum when the text size goes down again (review 2026-11-08)
            var minimum = target.IsAbsolute && original.IsAbsolute ? Math.Max(saved.MinWidth, target.Value) : saved.MinWidth;
            if (column.MinWidth != minimum) column.MinWidth = minimum;
        }
    }

    private static List<string> AppKeys(ResourceDictionary resources) =>
        resources.Keys.OfType<string>().Where(k => AppKeyPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList();

    /// <summary>Follows the Windows setting while the app runs (unless a fixed percent was given at start).</summary>
    public static void Follow(Application app, int? fixedPercent)
    {
        ArgumentNullException.ThrowIfNull(app);
        var originals = new Dictionary<string, double>(StringComparer.Ordinal);
        Apply(app.Resources, app.TryFindResource, originals, fixedPercent ?? SystemPercent());

        if (fixedPercent is not null) return;
        var current = SystemPercent();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.Accessibility or UserPreferenceCategory.General or UserPreferenceCategory.Window)) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                var percent = SystemPercent();
                if (percent == current) return;
                current = percent;
                Apply(app.Resources, app.TryFindResource, originals, percent);
            });
        };
    }
}
