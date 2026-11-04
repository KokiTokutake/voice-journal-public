using System.ComponentModel;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.Services;

/// <summary>
/// Connects recording and transcription (detail spec 3.1, 18.3): pauses the queue while a recording is being prepared,
/// recorded or saved; queues the saved session when "保存後に文字起こし" is on; keeps the job and transcript index up to
/// date; and resumes unfinished jobs at startup. Owns the job runner.
/// </summary>
public sealed class TranscriptionCoordinator : IDisposable
{
    private readonly RecordingViewModel _recording;
    private readonly TranscriptIndex _index;
    private readonly IUiSettingsStore _settings;
    private readonly Func<SttConfiguration> _configuration;
    private readonly SttJobRunner _runner;
    private bool _disposed;

    public TranscriptionCoordinator(
        RecordingViewModel recording, SttJobProcessor processor, TranscriptIndex index, IUiSettingsStore settings,
        Func<SttConfiguration> configuration, TimeProvider? time = null, Core.Analysis.InferenceGate? inference = null)
    {
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _runner = new SttJobRunner(processor, time, inference);
        _runner.JobChanged += OnJobChanged;
        _runner.Progress += (_, p) => Progress?.Invoke(this, p);
        _runner.JobError += (_, ex) => IndexError?.Invoke(this, ex);

        _recording.TranscribeAfterSaving = _settings.Load().TranscribeAfterSaving;
        _recording.PropertyChanged += OnRecordingChanged;
        _recording.RecordingCompleted += OnRecordingCompleted;
        _runner.SetRecording(IsActive(_recording.Status));
    }

    /// <summary>A job changed (raised on the worker thread, after the index was updated).</summary>
    public event EventHandler<SttJobChange>? JobChanged;

    public event EventHandler<SttJobProgress>? Progress;

    /// <summary>The index could not be updated, or a ledger could not be read. The files are unaffected.</summary>
    public event EventHandler<Exception>? IndexError;

    public SttJobRunner Runner => _runner;

    /// <summary>Startup, after the session index: rebuilds the job / transcript index and resumes unfinished jobs.</summary>
    public TranscriptRebuildReport Startup()
    {
        var report = _index.Rebuild();
        _runner.Resume(_index.UnfinishedJobs());
        return report;
    }

    /// <summary>Queues a whole-session transcription with the current settings (also used to reprocess, AT-26).</summary>
    public SttJob Enqueue(string sessionDirectory)
    {
        var manifest = SessionManifest.Load(sessionDirectory);
        return _runner.Enqueue(sessionDirectory, manifest.SessionId, _configuration());
    }

    /// <summary>Queues re-recognition of a range (AT-07), with the current settings except for <paramref name="modelName"/>.</summary>
    public SttJob EnqueueRange(string sessionDirectory, Core.Capture.AudioSource source, TimeRange range, int parentRevision, string? modelName = null)
    {
        var manifest = SessionManifest.Load(sessionDirectory);
        var configuration = _configuration();
        if (modelName is not null) configuration = configuration with { ModelName = modelName };
        return _runner.EnqueueRange(sessionDirectory, manifest.SessionId, configuration, source, range, parentRevision);
    }

    public void Cancel(string sessionDirectory, Guid jobId) => _runner.Cancel(sessionDirectory, jobId);

    /// <summary>Resumes a failed or cancelled job from its finished pieces (STT-002).</summary>
    public SttJob Retry(string sessionDirectory, Guid jobId) => _runner.Retry(sessionDirectory, jobId);

    /// <summary>A revision was saved from the UI (an edit): index it off the UI thread and tell the library.</summary>
    public Task RevisionSavedAsync(string sessionDirectory) => Task.Run(() =>
    {
        try
        {
            _index.UpsertRevisions(sessionDirectory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or DatabaseBusyException or Microsoft.Data.Sqlite.SqliteException)
        {
            IndexError?.Invoke(this, ex);
        }
        RevisionsChanged?.Invoke(this, sessionDirectory);
    });

    /// <summary>The revisions of a session changed (an edit was indexed). Raised on a background thread.</summary>
    public event EventHandler<string>? RevisionsChanged;

    /// <summary>The settings a job queued now would use.</summary>
    public SttConfiguration CurrentConfiguration => _configuration();

    /// <summary>Preparing, recording or saving: neither recognizer nor LLM may run (detail spec 3.1).</summary>
    internal static bool IsActive(RecorderStatus status) =>
        status is RecorderStatus.Preparing or RecorderStatus.Recording or RecorderStatus.PartiallyStopped or RecorderStatus.Stopping;

    private void OnRecordingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingViewModel.Status)) _runner.SetRecording(IsActive(_recording.Status));
        else if (e.PropertyName == nameof(RecordingViewModel.TranscribeAfterSaving))
            _settings.Save(_settings.Load() with { TranscribeAfterSaving = _recording.TranscribeAfterSaving });
    }

    private void OnRecordingCompleted(object? sender, EventArgs e)
    {
        _runner.SetRecording(false);
        if (!_recording.TranscribeAfterSaving || _recording.Status != RecorderStatus.Saved || _recording.LastSavedDirectory is not { } dir) return;
        try
        {
            Enqueue(dir);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            IndexError?.Invoke(this, ex);
        }
    }

    private void OnJobChanged(object? sender, SttJobChange change)
    {
        try
        {
            _index.UpsertJob(change.SessionDirectory, change.Job);
            if (change.Job.ResultRevision is not null) _index.UpsertRevisions(change.SessionDirectory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or DatabaseBusyException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The ledger and revision files are written; the next start rebuilds the index from them.
            IndexError?.Invoke(this, ex);
        }
        JobChanged?.Invoke(this, change);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recording.PropertyChanged -= OnRecordingChanged;
        _recording.RecordingCompleted -= OnRecordingCompleted;
        // The service provider disposes synchronously; a running job goes back to Queued and resumes at the next start.
        _runner.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15));
    }
}
