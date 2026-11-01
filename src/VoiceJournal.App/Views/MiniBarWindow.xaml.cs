using System.Windows;
using System.Windows.Input;

namespace VoiceJournal.App.Views;

/// <summary>Small always-on-top recording bar shown while the main window is minimized during a recording.</summary>
public partial class MiniBarWindow : Window
{
    /// <summary>Transparent margin around the visible bar for its shadow (Margin of the root Border in XAML).</summary>
    public const double ShadowMargin = 12;

    public MiniBarWindow() => InitializeComponent();

    /// <summary>The user asked for the main window back (button or double-click).</summary>
    public event EventHandler? RestoreRequested;

    /// <summary>The user finished dragging the bar to a new place.</summary>
    public event EventHandler? Moved;

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            RestoreRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        DragMove(); // returns when the mouse button is released
        Moved?.Invoke(this, EventArgs.Empty);
    }

    private void OnRestore(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);
}
