using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.App.Views;

namespace VoiceJournal.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan SavedNoticeTime = TimeSpan.FromSeconds(3);

    private readonly MainViewModel _viewModel;
    private readonly IUiSettingsStore _settings;
    private readonly DispatcherTimer _hideMiniBar;
    private MiniBarWindow? _miniBar;
    private bool _closeConfirmed;
    private bool _asking;

    public MainWindow(MainViewModel viewModel, IUiSettingsStore settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        _hideMiniBar = new DispatcherTimer { Interval = SavedNoticeTime };
        _hideMiniBar.Tick += (_, _) =>
        {
            _hideMiniBar.Stop();
            _miniBar?.Hide();
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRecordingActive)) UpdateMiniBar();
        };
        // Once audio flows, get out of the way: minimize, which brings up the mini bar.
        _viewModel.Recording.RecordingStarted += (_, _) => WindowState = WindowState.Minimized;
        if (System.Environment.GetEnvironmentVariable("VJ_UI_TIMING") is { Length: > 0 } timing) MeasurePageSwitches(timing);
    }

    /// <summary>
    /// NFR-02 measurement (only with VJ_UI_TIMING=&lt;file&gt;): from the page change (before its refresh) until the dispatcher
    /// is idle again after layout and rendering, one line per switch: page, milliseconds.
    /// </summary>
    private void MeasurePageSwitches(string path)
    {
        _viewModel.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.CurrentPage)) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                try
                {
                    File.AppendAllText(path, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_viewModel.CurrentPage},{watch.Elapsed.TotalMilliseconds:0.0}\n"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            });
        };
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMiniBar();
    }

    // While recording, ask "停止して保存" / "戻る" (detail spec 18.1); the window closes only after the
    // recording is saved. When idle it closes right away.
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_closeConfirmed || !_viewModel.IsRecordingActive)
        {
            _miniBar?.Close();
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (_asking) return;
        _asking = true;
        // Close() must not be called from inside Closing: ask and close after this event has returned.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; // the question needs a visible owner
                if (await _viewModel.ConfirmCloseAsync())
                {
                    _closeConfirmed = true;
                    Close();
                }
            }
            finally
            {
                _asking = false;
            }
        }));
    }

    /// <summary>
    /// Mini bar while recording and minimized (the user's choice, 2026-11-01). When the recording ends while
    /// minimized, the bar shows "保存しました" for a moment and then disappears.
    /// </summary>
    private void UpdateMiniBar()
    {
        if (MiniBarPlacement.ShouldShow(WindowState, _viewModel.IsRecordingActive))
        {
            _hideMiniBar.Stop();
            ShowMiniBar();
        }
        else if (_miniBar is { IsVisible: true })
        {
            if (WindowState == WindowState.Minimized) _hideMiniBar.Start(); // recording just ended: brief notice
            else _miniBar.Hide();
        }
    }

    private void ShowMiniBar()
    {
        if (_miniBar is null)
        {
            _miniBar = new MiniBarWindow { DataContext = _viewModel.Recording };
            _miniBar.RestoreRequested += (_, _) =>
            {
                WindowState = WindowState.Normal;
                Activate();
            };
            _miniBar.Moved += (_, _) => _settings.Save(_settings.Load() with { MiniBarLeft = _miniBar.Left, MiniBarTop = _miniBar.Top });
        }
        if (_miniBar.IsVisible) return;

        // Measure first so the default corner position accounts for the bar's real size.
        _miniBar.Left = -10_000;
        _miniBar.Show();
        _miniBar.UpdateLayout();
        var size = new Size(_miniBar.ActualWidth, _miniBar.ActualHeight);
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var place = MiniBarPlacement.Resolve(_settings.Load(), SystemParameters.WorkArea, virtualScreen, size, MiniBarWindow.ShadowMargin);
        _miniBar.Left = place.X;
        _miniBar.Top = place.Y;
    }
}
