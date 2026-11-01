using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using VoiceJournal.App.ViewModels;

namespace VoiceJournal.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeConfirmed;
    private bool _asking;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    // While recording, ask "停止して保存" / "戻る" (detail spec 18.1); the window closes only after the
    // recording is saved. When idle it closes right away.
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_closeConfirmed || !_viewModel.IsRecordingActive)
        {
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
}
