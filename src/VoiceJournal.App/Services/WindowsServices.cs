using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using VoiceJournal.App.Views;
using VoiceJournal.Infrastructure.Power;

namespace VoiceJournal.App.Services;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}

public sealed class WpfDialogService : IDialogService
{
    public CloseChoice AskStopBeforeClose() =>
        ChoiceDialog.Ask(
            "録音中です",
            "録音を停止して保存してから終了しますか？",
            ["停止して保存", "戻る"],
            defaultIndex: 1) == 0 ? CloseChoice.StopAndSave : CloseChoice.Back;

    public ResumeChoice AskAfterSuspend() =>
        ChoiceDialog.Ask(
            "録音を中断しました",
            "PC がスリープに入ったため、録音を停止して保存しました。中断した時刻はセッションに記録されています。\n同じ設定で新しい録音を始めますか？",
            ["新しく録音を開始", "閉じる"],
            defaultIndex: 1) == 0 ? ResumeChoice.StartNew : ResumeChoice.Dismiss;

    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static Window Owner() => Application.Current.MainWindow;
}

/// <summary>Sleep prevention via a power request, plus suspend / resume from <see cref="SystemEvents"/>.</summary>
public sealed class WindowsPowerService : IPowerService, IDisposable
{
    public WindowsPowerService() => SystemEvents.PowerModeChanged += OnPowerModeChanged;

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public IDisposable PreventSleep() => SleepBlocker.Acquire("Voice Journal: 録音中");

    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Suspending?.Invoke(this, EventArgs.Empty);
        else if (e.Mode == PowerModes.Resume) Resumed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ExplorerShellService : IShellService
{
    public void OpenFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Folder not found: {full}");
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(full);
        using var _ = Process.Start(start);
    }
}
