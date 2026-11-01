using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Playback;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Infrastructure.Audio;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Application has no Dispose; the mutex and services are released in OnExit.")]
public partial class App : Application
{
    // One instance per user: two instances would recover / record into the same workspace.
    private const string SingleInstanceName = @"Local\VoiceJournal.SingleInstance";

    private Mutex? _singleInstance;
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Voice Journal はすでに起動しています。", "Voice Journal", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Workspace workspace;
        VoiceJournalDatabase database;
        try
        {
            workspace = Workspace.OpenOrCreate(Workspace.DefaultRoot);
            database = VoiceJournalDatabase.Open(workspace.DatabasePath);
        }
        catch (Exception ex) when (ex is WorkspaceRootException or DatabaseSchemaException or DatabaseBusyException
                                       or InvalidDataException or IOException or UnauthorizedAccessException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            // DB-002: never repair automatically; the recordings stay untouched.
            MessageBox.Show(
                $"保存先またはデータベースを開けませんでした。録音済みのファイルは変更していません。\n\n{ex.Message}",
                "Voice Journal", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _services = ConfigureServices(workspace, database);
        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        var index = _services.GetRequiredService<ISessionIndex>();
        await _services.GetRequiredService<MainViewModel>().InitializeAsync(() => WorkspaceStartup.Run(workspace, index));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.Dispose(); // stops and saves a recording still running (e.g. Windows sign-out)
        }
#pragma warning disable CA1031 // Exiting: nothing more can be shown; the files are already consistent.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); }
            catch (ApplicationException) { } // not owned (second instance)
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    private ServiceProvider ConfigureServices(Workspace workspace, VoiceJournalDatabase database)
    {
        var services = new ServiceCollection();
        services.AddSingleton(workspace);
        services.AddSingleton(database);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new RecordingEnvironment(workspace.Root, workspace.Info.Storage.MinFreeBytes));
        services.AddSingleton<ISessionIndex>(sp => new SessionIndex(workspace, database, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAudioDeviceCatalog, WasapiDeviceCatalog>();
        services.AddSingleton<IAudioPlayer, WasapiAudioPlayer>();
        services.AddSingleton<IFreeSpaceProbe, DriveFreeSpaceProbe>();
        services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher(Dispatcher));
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<IPowerService, WindowsPowerService>();
        services.AddSingleton<IShellService, ExplorerShellService>();
        services.AddSingleton<RecordingViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Recording runs on its own threads and keeps its files consistent; report and keep the UI alive.
        MessageBox.Show(
            $"予期しないエラーが発生しました。録音中の場合、音声の保存は続いています。\n\n{e.Exception.GetType().Name}: {e.Exception.Message}",
            "Voice Journal", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
