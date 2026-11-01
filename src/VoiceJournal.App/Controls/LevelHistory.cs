using System.Windows;
using System.Windows.Media;

namespace VoiceJournal.App.Controls;

/// <summary>
/// A scrolling "waveform" of recent levels: every change of <see cref="Tick"/> adds a bar for the current
/// <see cref="Level"/> (0..1) on the right, so repeated equal values (silence) still scroll. Clearing
/// <see cref="IsRunning"/> empties the history.
/// Bars at or above <see cref="HotThreshold"/> (near clipping) use <see cref="HotBrush"/>.
/// Purely visual; the numeric level is shown next to it as text.
/// </summary>
public sealed class LevelHistory : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelHistory), new PropertyMetadata(0.0));

    public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
        nameof(Tick), typeof(long), typeof(LevelHistory), new PropertyMetadata(0L, OnTickChanged));

    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning), typeof(bool), typeof(LevelHistory), new PropertyMetadata(false, OnIsRunningChanged));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(LevelHistory), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HotBrushProperty = DependencyProperty.Register(
        nameof(HotBrush), typeof(Brush), typeof(LevelHistory), new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IdleBrushProperty = DependencyProperty.Register(
        nameof(IdleBrush), typeof(Brush), typeof(LevelHistory), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarCountProperty = DependencyProperty.Register(
        nameof(BarCount), typeof(int), typeof(LevelHistory), new FrameworkPropertyMetadata(48, FrameworkPropertyMetadataOptions.AffectsRender, OnBarCountChanged));

    private double[] _history = new double[48];
    private int _next;

    public LevelHistory() => SnapsToDevicePixels = true;

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public long Tick
    {
        get => (long)GetValue(TickProperty);
        set => SetValue(TickProperty, value);
    }

    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush HotBrush
    {
        get => (Brush)GetValue(HotBrushProperty);
        set => SetValue(HotBrushProperty, value);
    }

    public Brush IdleBrush
    {
        get => (Brush)GetValue(IdleBrushProperty);
        set => SetValue(IdleBrushProperty, value);
    }

    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    public double HotThreshold { get; set; } = 0.95; // about -3 dBFS on the -60..0 dB meter scale

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var count = _history.Length;
        if (count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var slot = ActualWidth / count;
        var barWidth = Math.Max(1, slot * 0.6);
        var middle = ActualHeight / 2;
        for (var i = 0; i < count; i++)
        {
            var level = _history[(_next + i) % count];
            var height = Math.Max(2, level * ActualHeight);
            var brush = level <= 0 ? IdleBrush : level >= HotThreshold ? HotBrush : BarBrush;
            var rect = new Rect(i * slot + (slot - barWidth) / 2, middle - height / 2, barWidth, height);
            drawingContext.DrawRoundedRectangle(brush, null, rect, barWidth / 2, barWidth / 2);
        }
    }

    private static void OnTickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (LevelHistory)d;
        self._history[self._next] = Math.Clamp(self.Level, 0, 1);
        self._next = (self._next + 1) % self._history.Length;
        self.InvalidateVisual();
    }

    private static void OnIsRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (LevelHistory)d;
        Array.Clear(self._history);
        self._next = 0;
        self.InvalidateVisual();
    }

    private static void OnBarCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (LevelHistory)d;
        self._history = new double[Math.Max(1, (int)e.NewValue)];
        self._next = 0;
    }
}
