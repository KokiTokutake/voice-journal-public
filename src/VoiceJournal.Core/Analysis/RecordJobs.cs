using System.Text.Json;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Core.Analysis;

/// <summary>Error codes of detail spec 19 for record generation.</summary>
public enum RecordJobError
{
    /// <summary>LLM-001: the inference server could not be started (files, memory) or reached.</summary>
    LlmUnavailable,

    /// <summary>LLM-002 / LLM-003 / VAL-001: every piece failed, or the result did not validate.</summary>
    GenerationFailed,

    /// <summary>No transcript to organize yet.</summary>
    NoTranscript,

    Cancelled,
}

/// <summary>
/// Ledger of one record generation (<c>analysis/work/&lt;job&gt;/job.json</c>), the source of truth for the
/// <c>record_jobs</c> index. Settings are a snapshot taken when the job was queued (detail spec 18.2 UI-12).
/// </summary>
public sealed class RecordJob
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid JobId { get; init; }

    public required Guid SessionId { get; init; }

    public ProcessingState State { get; set; } = ProcessingState.Queued;

    public required GenerationSettings Settings { get; init; }

    public string? ModelSha256 { get; init; }

    /// <summary>Only this section of the latest record is regenerated (plan 3.6); null for a whole record.</summary>
    public Guid? SectionId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? EndedAtUtc { get; set; }

    public int Attempt { get; set; }

    /// <summary>The transcript revision in use when the generation started.</summary>
    public int? TranscriptRevision { get; set; }

    public int? AnalysisRevision { get; set; }

    public int? RecordRevision { get; set; }

    public int PieceCount { get; set; }

    public IList<FailedPiece> FailedPieces { get; init; } = [];

    public double? ElapsedSeconds { get; set; }

    /// <summary>Learned token factor from this run (the next job may start from it).</summary>
    public double? MeasuredTokenFactor { get; set; }

    public RecordJobError? Error { get; set; }

    /// <summary>A short reason for the screen; never prompt or transcript text.</summary>
    public string? ErrorDetail { get; set; }
}

public static class RecordJobStore
{
    public const string FileName = "job.json";

    public static string JobDirectory(string sessionDirectory, Guid jobId) => new AnalysisStore(sessionDirectory).WorkDirectory(jobId);

    public static void Save(string sessionDirectory, RecordJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var dir = JobDirectory(sessionDirectory, job.JobId);
        Directory.CreateDirectory(dir);
        AtomicFile.WriteAllBytes(Path.Combine(dir, FileName), JsonSerializer.SerializeToUtf8Bytes(job, AnalysisJson.Options));
    }

    /// <exception cref="InvalidDataException">The ledger is broken.</exception>
    public static RecordJob Load(string sessionDirectory, Guid jobId)
    {
        var path = Path.Combine(JobDirectory(sessionDirectory, jobId), FileName);
        try
        {
            var job = JsonSerializer.Deserialize<RecordJob>(AtomicFile.ReadAllBytes(path), AnalysisJson.Options);
            return job is { SchemaVersion: RecordJob.CurrentSchemaVersion } && job.JobId == jobId ? job : throw new InvalidDataException($"Record job {jobId} has an unknown schema.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Record job {jobId} is broken.", ex);
        }
    }

    /// <summary>All record jobs of a session, oldest first; broken ledgers are skipped.</summary>
    public static IReadOnlyList<RecordJob> List(string sessionDirectory)
    {
        var root = Path.Combine(new AnalysisStore(sessionDirectory).RootDirectory, "work");
        if (!Directory.Exists(root)) return [];
        var jobs = new List<RecordJob>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParse(Path.GetFileName(dir), out var id) || !File.Exists(Path.Combine(dir, FileName))) continue;
            try { jobs.Add(Load(sessionDirectory, id)); }
            catch (InvalidDataException) { }
            catch (IOException) { }
        }
        return [.. jobs.OrderBy(j => j.CreatedAtUtc)];
    }
}

/// <summary>
/// One inference at a time across the transcription and record queues (detail spec 3.2): the recognizer and the
/// LLM never compete for CPU, GPU and memory.
/// </summary>
public sealed class InferenceGate : IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_slot);
    }

    public void Dispose() => _slot.Dispose();

    private sealed class Releaser(SemaphoreSlim slot) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slot.Release();
        }
    }
}

/// <summary>Runs one record job: the transcript in use, the generator, and the ledger's state changes.</summary>
public sealed class RecordJobProcessor
{
    private readonly RecordGenerator _generator;
    private readonly TimeProvider _time;

    public RecordJobProcessor(RecordGenerator generator, TimeProvider time)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Runs the job and leaves it in a final state, or back in Queued when <paramref name="cancellationToken"/> was
    /// a preemption (recording, app closing) and <paramref name="isPreemption"/> says so. Finished pieces stay in the work folder.
    /// </summary>
    public async Task RunAsync(string sessionDirectory, RecordJob job, Func<bool> isPreemption, Action<RecordJob> changed,
        IProgress<GenerationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(isPreemption);
        ArgumentNullException.ThrowIfNull(changed);
        void Set(ProcessingState state)
        {
            job.State = ProcessingStateMachine.Transition(job.State, state);
            RecordJobStore.Save(sessionDirectory, job);
            changed(job);
        }

        TranscriptDocument? transcript;
        try
        {
            // A section is regenerated from the transcript revision its record was built on (the evidence points there).
            transcript = job.SectionId is null
                ? new TranscriptStore(sessionDirectory).LoadCurrent()
                : new RecordStore(sessionDirectory).LoadLatest() is { } record ? new TranscriptStore(sessionDirectory).Load(record.TranscriptRevision) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
        {
            transcript = null;
        }
        if (transcript is null)
        {
            Fail(RecordJobError.NoTranscript, job.SectionId is null ? "文字起こしがまだありません。" : "記録の元の文字起こしを読み込めません。");
            return;
        }
        var manifest = SessionManifest.Load(sessionDirectory);
        job.Attempt++;
        job.StartedAtUtc ??= _time.GetUtcNow();
        if (job.TranscriptRevision is { } used && used != transcript.Revision)
            foreach (var stale in Directory.EnumerateFiles(RecordJobStore.JobDirectory(sessionDirectory, job.JobId), "*.json").Where(f => !Path.GetFileName(f).Equals(RecordJobStore.FileName, StringComparison.Ordinal)))
                File.Delete(stale); // the transcript in use changed while the job waited: nothing earlier applies
        job.TranscriptRevision = transcript.Revision;
        Set(ProcessingState.Organizing);
        var validating = false;
        var reporter = new InlineReporter(p =>
        {
            if (!validating && p.Step is "validate")
            {
                validating = true;
                Set(ProcessingState.Validating);
            }
            progress?.Report(p);
        });
        try
        {
            var result = await _generator.GenerateAsync(new GenerationRequest
            {
                SessionDirectory = sessionDirectory,
                Transcript = transcript,
                StartedAtUtc = manifest.StartedAtUtc,
                TimeZoneId = manifest.TimeZoneId,
                Title = manifest.Title,
                JobId = job.JobId,
                Settings = job.Settings,
                ModelSha256 = job.ModelSha256,
                SectionId = job.SectionId,
            }, reporter, cancellationToken).ConfigureAwait(false);
            job.PieceCount = result.PieceCount;
            job.FailedPieces.Clear();
            foreach (var f in result.FailedPieces) job.FailedPieces.Add(f);
            job.MeasuredTokenFactor = result.MeasuredTokenFactor;
            job.ElapsedSeconds = Math.Round(result.Elapsed.TotalSeconds, 1);
            switch (result.Outcome)
            {
                case GenerationOutcome.Generated:
                    job.AnalysisRevision = result.AnalysisRevision;
                    job.RecordRevision = result.RecordRevision;
                    if (!validating) Set(ProcessingState.Validating);
                    job.EndedAtUtc = _time.GetUtcNow();
                    Set(ProcessingState.ReadyForReview);
                    break;
                case GenerationOutcome.NotAnalyzable:
                    job.EndedAtUtc = _time.GetUtcNow();
                    Set(ProcessingState.NotAnalyzable);
                    break;
                default:
                    Fail(RecordJobError.GenerationFailed, result.Error ?? "記録を作れませんでした。");
                    break;
            }
        }
        catch (LlmUnavailableException ex)
        {
            Fail(RecordJobError.LlmUnavailable, ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("コンテキスト長", StringComparison.Ordinal) || job.SectionId is not null)
        {
            // LLM-002: the settings leave no room for the transcript; or the section to regenerate is gone or has no evidence.
            Fail(RecordJobError.GenerationFailed, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isPreemption())
            {
                job.State = ProcessingStateMachine.Transition(job.State, ProcessingState.Queued);
                RecordJobStore.Save(sessionDirectory, job);
                changed(job);
            }
            else
            {
                Fail(RecordJobError.Cancelled, "キャンセルしました。", ProcessingState.Cancelled);
            }
        }

        void Fail(RecordJobError error, string detail, ProcessingState state = ProcessingState.Failed)
        {
            job.Error = error;
            job.ErrorDetail = detail;
            job.EndedAtUtc = _time.GetUtcNow();
            if (job.State == ProcessingState.Queued && state == ProcessingState.Failed)
            {
                job.State = ProcessingState.Failed; // failed before it started (no transcript)
                RecordJobStore.Save(sessionDirectory, job);
                changed(job);
                return;
            }
            Set(state);
        }
    }

    private sealed class InlineReporter(Action<GenerationProgress> report) : IProgress<GenerationProgress>
    {
        public void Report(GenerationProgress value) => report(value);
    }
}

/// <summary>A record job and the session folder it belongs to.</summary>
public sealed record RecordJobChange(string SessionDirectory, RecordJob Job);

/// <summary>
/// The record generation queue: one job at a time, never while recording (a running request is cancelled and the
/// job resumes from its finished pieces afterwards), and never at the same time as transcription (<see cref="InferenceGate"/>).
/// </summary>
public sealed class RecordJobRunner : IAsyncDisposable
{
    private readonly RecordJobProcessor _processor;
    private readonly InferenceGate _gate;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly LinkedList<(string Dir, Guid JobId)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private CancellationTokenSource? _current;
    private Guid? _currentJob;
    private bool _recording;
    private bool _preempting;
    private int _disposed;

    public RecordJobRunner(RecordJobProcessor processor, InferenceGate gate, TimeProvider? time = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _time = time ?? TimeProvider.System;
        _worker = Task.Run(WorkAsync);
    }

    public event EventHandler<RecordJobChange>? JobChanged;

    public event EventHandler<GenerationProgress>? Progress;

    public event EventHandler<Exception>? JobError;

    public Guid? CurrentJobId
    {
        get { lock (_lock) return _currentJob; }
    }

    public int QueuedCount
    {
        get { lock (_lock) return _queue.Count; }
    }

    public RecordJob Enqueue(string sessionDirectory, Guid sessionId, GenerationSettings settings, string? modelSha256 = null, Guid? sectionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        var job = new RecordJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = sessionId,
            Settings = settings ?? throw new ArgumentNullException(nameof(settings)),
            ModelSha256 = modelSha256,
            SectionId = sectionId,
            CreatedAtUtc = _time.GetUtcNow(),
        };
        RecordJobStore.Save(sessionDirectory, job);
        Add(sessionDirectory, job, first: false);
        return job;
    }

    /// <summary>Puts unfinished jobs found at startup back in the queue (oldest first).</summary>
    public void Resume(IEnumerable<RecordJobChange> unfinished)
    {
        ArgumentNullException.ThrowIfNull(unfinished);
        foreach (var (dir, job) in unfinished.OrderBy(u => u.Job.CreatedAtUtc))
        {
            if (!ProcessingStateMachine.IsActive(job.State)) continue;
            if (job.State != ProcessingState.Queued)
            {
                job.State = ProcessingStateMachine.Transition(job.State, ProcessingState.Queued);
                RecordJobStore.Save(dir, job);
            }
            lock (_lock)
                if (_queue.Any(q => q.JobId == job.JobId) || _currentJob == job.JobId) continue;
            Add(dir, job, first: false);
        }
    }

    /// <summary>Retries a failed or cancelled job (LLM-001: after the cause is fixed). Finished pieces are reused.</summary>
    public RecordJob Retry(string sessionDirectory, Guid jobId)
    {
        var job = RecordJobStore.Load(sessionDirectory, jobId);
        if (job.State is not (ProcessingState.Failed or ProcessingState.Cancelled))
            throw new InvalidOperationException($"Record job {jobId} is {job.State}; only failed or cancelled jobs can be retried.");
        job.State = ProcessingStateMachine.Transition(job.State, ProcessingState.Queued);
        job.Error = null;
        job.ErrorDetail = null;
        job.EndedAtUtc = null;
        RecordJobStore.Save(sessionDirectory, job);
        Add(sessionDirectory, job, first: false);
        return job;
    }

    public void Cancel(string sessionDirectory, Guid jobId)
    {
        bool removed;
        lock (_lock)
        {
            if (_currentJob == jobId)
            {
                _preempting = false;
                _current?.Cancel();
                return;
            }
            var node = _queue.First;
            while (node is not null && node.Value.JobId != jobId) node = node.Next;
            removed = node is not null;
            if (removed) _queue.Remove(node!);
        }
        if (!removed) return;
        var job = RecordJobStore.Load(sessionDirectory, jobId);
        job.State = ProcessingStateMachine.Transition(job.State, ProcessingState.Cancelled);
        job.Error = RecordJobError.Cancelled;
        job.EndedAtUtc = _time.GetUtcNow();
        RecordJobStore.Save(sessionDirectory, job);
        JobChanged?.Invoke(this, new RecordJobChange(sessionDirectory, job));
    }

    /// <summary>Recording started: the running request is cancelled and the job waits. Stopped: the queue continues.</summary>
    public void SetRecording(bool recording)
    {
        lock (_lock)
        {
            _recording = recording;
            if (recording && _current is not null)
            {
                _preempting = true;
                _current.Cancel();
            }
        }
        if (!recording) _signal.Release();
    }

    private void Add(string dir, RecordJob job, bool first)
    {
        lock (_lock)
        {
            if (first) _queue.AddFirst((dir, job.JobId));
            else _queue.AddLast((dir, job.JobId));
        }
        JobChanged?.Invoke(this, new RecordJobChange(dir, job));
        _signal.Release();
    }

    private async Task WorkAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_lock)
                if (_recording || _queue.Count == 0) continue;

            IDisposable slot;
            try
            {
                slot = await _gate.EnterAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            using (slot)
            {
                (string Dir, Guid JobId) next;
                lock (_lock)
                {
                    if (_recording || _queue.Count == 0) continue; // recording started while waiting for the recognizer
                    next = _queue.First!.Value;
                    _queue.RemoveFirst();
                    _current = CancellationTokenSource.CreateLinkedTokenSource(token);
                    _currentJob = next.JobId;
                    _preempting = false;
                }
                try
                {
                    var job = RecordJobStore.Load(next.Dir, next.JobId);
                    if (job.State != ProcessingState.Queued) continue;
                    var progress = new Progress<GenerationProgress>(p => Progress?.Invoke(this, p));
                    await _processor.RunAsync(next.Dir, job, () => { lock (_lock) return _preempting || token.IsCancellationRequested; },
                        j => JobChanged?.Invoke(this, new RecordJobChange(next.Dir, j)), progress, _current.Token).ConfigureAwait(false);
                    if (job.State == ProcessingState.Queued && !token.IsCancellationRequested)
                        lock (_lock) _queue.AddFirst(next); // preempted by a recording: first in line once it ends
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException)
                {
                    JobError?.Invoke(this, ex);
                }
                finally
                {
                    lock (_lock)
                    {
                        _current?.Dispose();
                        _current = null;
                        _currentJob = null;
                        if (_queue.Count > 0 && !_recording) _signal.Release();
                    }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stopping.Dispose();
        _signal.Dispose();
    }
}
