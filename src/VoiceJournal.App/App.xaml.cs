using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VoiceJournal.App.Services;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Playback;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Audio;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Application has no Dispose; the mutex and services are released in OnExit.")]
public partial class App : Application
{
    // One instance per workspace: two instances would recover / record into the same folder.
    private const string SingleInstancePrefix = @"Local\VoiceJournal.SingleInstance.";

    /// <summary>Tools, models and settings live here regardless of the workspace (<c>%LOCALAPPDATA%\VoiceJournal</c>).</summary>
    private static string AppDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceJournal");

    private Mutex? _singleInstance;
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var options = StartupOptions.Parse(e.Args);
        if (options.Theme is { } theme) ThemeMode = theme;
        var root = options.WorkspaceRoot ?? Workspace.DefaultRoot;
        var rootKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..16];
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstancePrefix + rootKey, out var createdNew);
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
            workspace = Workspace.OpenOrCreate(root);
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
        var transcription = _services.GetRequiredService<TranscriptionCoordinator>();
        var records = _services.GetRequiredService<RecordCoordinator>();
        // Recording comes first (detail spec 3.1): the record job cancels its request, then the bundled server stops
        // and gives its memory back; it starts again for the next job.
        var recording = _services.GetRequiredService<RecordingViewModel>();
        var engine = _services.GetRequiredService<LlamaServerHost>();
        recording.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecordingViewModel.Status) && TranscriptionCoordinator.IsActive(recording.Status)) engine.StopWhenIdle();
        };
        await _services.GetRequiredService<MainViewModel>().InitializeAsync(() =>
        {
            var report = WorkspaceStartup.Run(workspace, index);
            // After the sessions are indexed and recovered: jobs reference them. Records reference transcripts.
            transcription.Startup();
            records.Startup();
            // Management files last: a batch a crash left half applied is finished or stopped (detail spec 14.3).
            _services.GetRequiredService<ManagementViewModel>().RecoverAtStartup();
            return report;
        });
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
        services.AddSingleton<IUiSettingsStore>(new JsonUiSettingsStore(JsonUiSettingsStore.DefaultPath));
        services.AddSingleton(_ => SttToolSettings.LoadOrCreate(AppDataRoot));
        services.AddSingleton<ISttEngine>(sp => new WhisperCliEngine(sp.GetRequiredService<SttToolSettings>()));
        services.AddSingleton<ISttInputBuilder, SttInputBuilder>();
        services.AddSingleton(sp => new SttJobProcessor(sp.GetRequiredService<ISttInputBuilder>(), sp.GetRequiredService<ISttEngine>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(new TranscriptIndex(workspace, database));
        // One inference at a time across the recognizer and the LLM (detail spec 3.2).
        services.AddSingleton<InferenceGate>();
        services.AddSingleton(sp => new TranscriptionCoordinator(
            sp.GetRequiredService<RecordingViewModel>(), sp.GetRequiredService<SttJobProcessor>(), sp.GetRequiredService<TranscriptIndex>(),
            sp.GetRequiredService<IUiSettingsStore>(), sp.GetRequiredService<ISttSettingsStore>().Load, sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<InferenceGate>()));
        services.AddSingleton<ILlmSettingsStore>(new JsonLlmSettingsStore(JsonLlmSettingsStore.DefaultPath));
        // The bundled llama.cpp server (docs/phase-d-engine-plan.md), started for a job and stopped when idle.
        services.AddSingleton(_ => LlmToolSettings.LoadOrCreate(AppDataRoot));
        services.AddSingleton(sp => new LlamaServerHost(sp.GetRequiredService<LlmToolSettings>, idleTimeout: TimeSpan.FromSeconds(60)));
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<ILlmSettingsStore>();
            return new BundledLlmClient(sp.GetRequiredService<LlamaServerHost>(), () =>
            {
                var settings = store.Load();
                return (settings.ContextLength, TimeSpan.FromMinutes(settings.RequestTimeoutMinutes));
            });
        });
        services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<BundledLlmClient>());
        services.AddSingleton<IAnalysisSchemaValidator, AnalysisSchemaValidator>();
        services.AddSingleton(sp => new RecordGenerator(sp.GetRequiredService<ILlmClient>(), sp.GetRequiredService<IAnalysisSchemaValidator>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RecordJobProcessor(sp.GetRequiredService<RecordGenerator>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(new RecordIndex(workspace, database));
        services.AddSingleton(sp => new RecordCoordinator(
            sp.GetRequiredService<RecordingViewModel>(), sp.GetRequiredService<TranscriptionCoordinator>(), sp.GetRequiredService<RecordJobProcessor>(),
            sp.GetRequiredService<InferenceGate>(), sp.GetRequiredService<RecordIndex>(), sp.GetRequiredService<IUiSettingsStore>(),
            () => sp.GetRequiredService<ILlmSettingsStore>().Load().ToGeneration(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISttSettingsStore>(new JsonSttSettingsStore(JsonSttSettingsStore.DefaultPath));
        services.AddSingleton<TranscriptViewModel>();
        services.AddSingleton(sp => new RecordReviewViewModel(sp.GetRequiredService<RecordCoordinator>(), sp.GetRequiredService<IAudioPlayer>(),
            sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ProcessingViewModel(sp.GetRequiredService<TranscriptionCoordinator>(), sp.GetRequiredService<TranscriptIndex>(),
            sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<RecordingEnvironment>(), sp.GetRequiredService<RecordCoordinator>(), sp.GetRequiredService<RecordIndex>()));
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton(sp => new ManagementStore(workspace, database, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ApplyEngine(workspace, database, sp.GetRequiredService<ManagementStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ApplyRecovery(database, sp.GetRequiredService<ManagementStore>(), sp.GetRequiredService<ApplyEngine>()));
        services.AddSingleton(sp => new ManagementViewModel(sp.GetRequiredService<ManagementStore>(), sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ApplyEngine>(), sp.GetRequiredService<ApplyRecovery>()));
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
