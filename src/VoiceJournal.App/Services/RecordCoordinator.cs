using System.ComponentModel;
using System.Text.Json;
using VoiceJournal.App.ViewModels;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.Services;

/// <summary>What the record screens need from the record queue (UI-07, processing).</summary>
public interface IRecordService
{
    /// <summary>A record job changed (raised on a worker thread, after the index was updated).</summary>
    event EventHandler<RecordJobChange>? JobChanged;

    event EventHandler<GenerationProgress>? Progress;

    /// <summary>The job running now, if any.</summary>
    Guid? CurrentJobId { get; }

    /// <summary>Queues a record from the transcript in use, with the current settings.</summary>
    RecordJob Enqueue(string sessionDirectory);

    /// <summary>A record was edited, approved or deferred from the UI: index it off the UI thread.</summary>
    Task RecordSavedAsync(string sessionDirectory);
}

/// <summary>
/// Connects transcription and record generation (detail spec 3.1, 3.2, 18.3): stops the LLM while a recording is
/// being prepared, recorded or saved; queues a record when a whole-session transcription finishes and "文字起こしの後に
/// 記録を作る" is on; keeps the record index up to date; and resumes unfinished jobs at startup. Owns the record runner.
/// </summary>
/// <remarks>
/// The automatic request is made only for a session that has no record job yet. Re-transcribing a session that
/// already has a record does not regenerate it (the user decides, D4); a session recorded before Phase D gets one
/// when it is transcribed. A request lost to a crash between the two steps is made again from the screen.
/// </remarks>
public sealed class RecordCoordinator : IRecordService, IDisposable
{
    private readonly RecordingViewModel _recording;
    private readonly TranscriptionCoordinator _transcription;
    private readonly RecordIndex _index;
    private readonly IUiSettingsStore _settings;
    private readonly Func<GenerationSettings> _configuration;
    private readonly RecordJobRunner _runner;
    private readonly Lock _enqueueLock = new();
    private volatile bool _generateAfterTranscription;
    private bool _disposed;

    public RecordCoordinator(
        RecordingViewModel recording, TranscriptionCoordinator transcription, RecordJobProcessor processor, InferenceGate gate,
        RecordIndex index, IUiSettingsStore settings, Func<GenerationSettings> configuration, TimeProvider? time = null)
    {
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _runner = new RecordJobRunner(processor, gate, time);
        _runner.JobChanged += OnJobChanged;
        _runner.Progress += (_, p) => Progress?.Invoke(this, p);
        _runner.JobError += (_, ex) => IndexError?.Invoke(this, ex);

        _generateAfterTranscription = _settings.Load().GenerateRecordAfterTranscription;
        _recording.GenerateRecordAfterTranscription = _generateAfterTranscription;
        _recording.PropertyChanged += OnRecordingChanged;
        _recording.RecordingCompleted += OnRecordingCompleted;
        _transcription.JobChanged += OnTranscriptionChanged;
        _transcription.RevisionsChanged += OnRevisionsChanged;
        _runner.SetRecording(TranscriptionCoordinator.IsActive(_recording.Status));
    }

    /// <summary>A record job changed (raised on the worker thread, after the index was updated).</summary>
    public event EventHandler<RecordJobChange>? JobChanged;

    public event EventHandler<GenerationProgress>? Progress;

    /// <summary>The index could not be updated, or a ledger could not be read. The files are unaffected.</summary>
    public event EventHandler<Exception>? IndexError;

    /// <summary>The records or review state of a session changed in the index. Raised on a background thread.</summary>
    public event EventHandler<string>? RecordsChanged;

    public RecordJobRunner Runner => _runner;

    public Guid? CurrentJobId => _runner.CurrentJobId;

    /// <summary>The settings a job queued now would use.</summary>
    public GenerationSettings CurrentConfiguration => _configuration();

    /// <summary>Startup, after the transcription index: rebuilds the record index and resumes unfinished jobs.</summary>
    public RecordRebuildReport Startup()
    {
        var report = _index.Rebuild();
        _runner.Resume(_index.UnfinishedJobs());
        return report;
    }

    /// <summary>Queues a record from the transcript in use, with the current settings (also used to regenerate).</summary>
    public RecordJob Enqueue(string sessionDirectory)
    {
        var manifest = SessionManifest.Load(sessionDirectory);
        return _runner.Enqueue(sessionDirectory, manifest.SessionId, _configuration());
    }

    public void Cancel(string sessionDirectory, Guid jobId) => _runner.Cancel(sessionDirectory, jobId);

    /// <summary>Retries a failed or cancelled job from its finished pieces (LLM-001: after LM Studio is started).</summary>
    public RecordJob Retry(string sessionDirectory, Guid jobId) => _runner.Retry(sessionDirectory, jobId);

    /// <summary>A record was edited, approved or deferred from the UI: index it off the UI thread.</summary>
    public Task RecordSavedAsync(string sessionDirectory) => Task.Run(() => IndexSession(sessionDirectory));

    private void OnRecordingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingViewModel.Status)) _runner.SetRecording(TranscriptionCoordinator.IsActive(_recording.Status));
        else if (e.PropertyName == nameof(RecordingViewModel.GenerateRecordAfterTranscription))
        {
            _generateAfterTranscription = _recording.GenerateRecordAfterTranscription;
            _settings.Save(_settings.Load() with { GenerateRecordAfterTranscription = _generateAfterTranscription });
        }
    }

    private void OnRecordingCompleted(object? sender, EventArgs e) => _runner.SetRecording(false);

    private void OnTranscriptionChanged(object? sender, SttJobChange change)
    {
        if (change.Job.State != ProcessingState.Transcribed) return;
        IndexSession(change.SessionDirectory); // the transcript in use may have changed (an approved record goes out of date)
        if (!_generateAfterTranscription || change.Job.Range is not null) return;
        try
        {
            lock (_enqueueLock)
            {
                if (RecordJobStore.List(change.SessionDirectory).Count > 0) return;
                _runner.Enqueue(change.SessionDirectory, change.Job.SessionId, _configuration());
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            IndexError?.Invoke(this, ex);
        }
    }

    private void OnRevisionsChanged(object? sender, string sessionDirectory) => IndexSession(sessionDirectory);

    private void OnJobChanged(object? sender, RecordJobChange change)
    {
        // The record rows first: a session shown as "ready for review" always has its record in the index.
        if (change.Job.State is ProcessingState.ReadyForReview or ProcessingState.NotAnalyzable) IndexSession(change.SessionDirectory);
        try
        {
            _index.UpsertJob(change.SessionDirectory, change.Job);
        }
        catch (Exception ex) when (IsIndexFailure(ex))
        {
            IndexError?.Invoke(this, ex);
        }
        // The runner keeps changing the same ledger object; screens handle the event later on the UI thread.
        JobChanged?.Invoke(this, change with { Job = Snapshot(change.Job) });
    }

    private static RecordJob Snapshot(RecordJob job) =>
        JsonSerializer.Deserialize<RecordJob>(JsonSerializer.SerializeToUtf8Bytes(job, AnalysisJson.Options), AnalysisJson.Options)!;

    private void IndexSession(string sessionDirectory)
    {
        try
        {
            _index.UpsertSession(sessionDirectory);
        }
        catch (Exception ex) when (IsIndexFailure(ex))
        {
            // The analysis and record files are written; the next start rebuilds the index from them.
            IndexError?.Invoke(this, ex);
            return;
        }
        RecordsChanged?.Invoke(this, sessionDirectory);
    }

    private static bool IsIndexFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or DatabaseBusyException or Microsoft.Data.Sqlite.SqliteException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recording.PropertyChanged -= OnRecordingChanged;
        _recording.RecordingCompleted -= OnRecordingCompleted;
        _transcription.JobChanged -= OnTranscriptionChanged;
        _transcription.RevisionsChanged -= OnRevisionsChanged;
        // A running job goes back to Queued and resumes from its finished pieces at the next start.
        _runner.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15));
    }
}
