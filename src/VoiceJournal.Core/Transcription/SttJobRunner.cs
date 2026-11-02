using VoiceJournal.Core.Capture;

namespace VoiceJournal.Core.Transcription;

/// <summary>A job and the session folder it belongs to.</summary>
public sealed record SttJobChange(string SessionDirectory, SttJob Job);

/// <summary>
/// The processing queue (detail spec 3.1, 3.2: one inference job at a time; recording comes first). Jobs run one by
/// one on a background worker. While recording, no new job starts and the running one is paused (plan 3.6); a job
/// that had to be stopped instead goes back to the front of the queue. Events are raised on the worker thread.
/// </summary>
public sealed class SttJobRunner : IAsyncDisposable
{
    private readonly SttJobProcessor _processor;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly LinkedList<(string Dir, Guid JobId)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SttPauseGate _pause = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private CancellationTokenSource? _current;
    private Guid? _currentJob;
    private bool _recording;
    private int _disposed;

    public SttJobRunner(SttJobProcessor processor, TimeProvider? time = null, Analysis.InferenceGate? inference = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _time = time ?? TimeProvider.System;
        _inference = inference;
        _worker = Task.Run(WorkAsync);
    }

    // Shared with the record queue so the recognizer and the LLM never run at the same time (detail spec 3.2).
    private readonly Analysis.InferenceGate? _inference;

    /// <summary>A job was queued, changed state, or finished.</summary>
    public event EventHandler<SttJobChange>? JobChanged;

    public event EventHandler<SttJobProgress>? Progress;

    /// <summary>A job could not be read or saved; it was dropped from the queue (the ledger is left as it is).</summary>
    public event EventHandler<Exception>? JobError;

    public Guid? CurrentJobId
    {
        get { lock (_gate) return _currentJob; }
    }

    public int QueuedCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>Queues a whole-session transcription with a snapshot of <paramref name="configuration"/>.</summary>
    public SttJob Enqueue(string sessionDirectory, Guid sessionId, SttConfiguration configuration) =>
        Add(sessionDirectory, new SttJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = sessionId,
            Kind = SttJobKind.Full,
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration)),
            CreatedAtUtc = _time.GetUtcNow(),
        });

    /// <summary>Queues re-recognition of <paramref name="range"/> of <paramref name="source"/>, made from <paramref name="parentRevision"/>.</summary>
    public SttJob EnqueueRange(string sessionDirectory, Guid sessionId, SttConfiguration configuration, AudioSource source, TimeRange range, int parentRevision) =>
        Add(sessionDirectory, new SttJob
        {
            JobId = Guid.CreateVersion7(),
            SessionId = sessionId,
            Kind = SttJobKind.Range,
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration)),
            CreatedAtUtc = _time.GetUtcNow(),
            RangeSource = source,
            Range = range,
            ParentRevision = parentRevision,
        });

    private SttJob Add(string sessionDirectory, SttJob job)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        SttJobStore.Save(sessionDirectory, job);
        lock (_gate) _queue.AddLast((sessionDirectory, job.JobId));
        JobChanged?.Invoke(this, new SttJobChange(sessionDirectory, job));
        _signal.Release();
        return job;
    }

    /// <summary>Puts unfinished jobs found at startup back in the queue (oldest first). Their ledgers are reset to Queued.</summary>
    public void Resume(IEnumerable<SttJobChange> unfinished)
    {
        ArgumentNullException.ThrowIfNull(unfinished);
        foreach (var (dir, job) in unfinished.OrderBy(u => u.Job.CreatedAtUtc))
        {
            if (!ProcessingStateMachine.IsActive(job.State)) continue;
            if (job.State != ProcessingState.Queued)
            {
                job.State = ProcessingStateMachine.Transition(job.State, ProcessingState.Queued);
                SttJobStore.Save(dir, job);
            }
            lock (_gate)
            {
                if (_queue.Any(q => q.JobId == job.JobId) || _currentJob == job.JobId) continue;
                _queue.AddLast((dir, job.JobId));
            }
            JobChanged?.Invoke(this, new SttJobChange(dir, job));
            _signal.Release();
        }
    }

    /// <summary>
    /// Puts a failed or cancelled job back in the queue (STT-002: finished pieces are kept, only the rest runs).
    /// </summary>
    /// <exception cref="InvalidOperationException">The job is not failed or cancelled.</exception>
    public SttJob Retry(string sessionDirectory, Guid jobId)
    {
        var job = SttJobStore.Load(sessionDirectory, jobId);
        if (job.State is not (ProcessingState.Failed or ProcessingState.Cancelled))
            throw new InvalidOperationException($"Job {jobId} is {job.State}; only failed or cancelled jobs can be retried.");
        job.State = ProcessingStateMachine.Transition(job.State, ProcessingState.Queued);
        job.EndedAtUtc = null;
        SttJobStore.Save(sessionDirectory, job);
        lock (_gate) _queue.AddLast((sessionDirectory, job.JobId));
        JobChanged?.Invoke(this, new SttJobChange(sessionDirectory, job));
        _signal.Release();
        return job;
    }

    /// <summary>Cancels a queued or running job. Finished pieces, the audio and earlier revisions are kept.</summary>
    public void Cancel(string sessionDirectory, Guid jobId)
    {
        bool removed;
        lock (_gate)
        {
            if (_currentJob == jobId)
            {
                _current?.Cancel();
                return;
            }
            var node = _queue.First;
            while (node is not null && node.Value.JobId != jobId) node = node.Next;
            removed = node is not null;
            if (removed) _queue.Remove(node!);
        }
        if (!removed) return;
        var job = SttJobStore.Load(sessionDirectory, jobId);
        job.State = ProcessingStateMachine.Transition(job.State, ProcessingState.Cancelled);
        job.Error = SttErrorCode.Cancelled;
        job.EndedAtUtc = _time.GetUtcNow();
        SttJobStore.Save(sessionDirectory, job);
        JobChanged?.Invoke(this, new SttJobChange(sessionDirectory, job));
    }

    /// <summary>Recording started or stopped: pauses or resumes recognition (plan 3.6).</summary>
    public void SetRecording(bool recording)
    {
        lock (_gate) _recording = recording;
        if (recording) _pause.Pause();
        else
        {
            _pause.Resume();
            _signal.Release();
        }
    }

    public bool IsPaused => _pause.IsPaused;

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

            lock (_gate)
                if (_recording || _queue.Count == 0) continue; // woken again when recording ends or a job is added
            IDisposable? slot = null;
            if (_inference is not null)
            {
                try
                {
                    slot = await _inference.EnterAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            using var held = slot;

            (string Dir, Guid JobId) next;
            lock (_gate)
            {
                if (_recording || _queue.Count == 0) continue; // woken again when recording ends or a job is added
                next = _queue.First!.Value;
                _queue.RemoveFirst();
                _current = CancellationTokenSource.CreateLinkedTokenSource(token);
                _currentJob = next.JobId;
            }

            SttJob? job = null;
            try
            {
                job = SttJobStore.Load(next.Dir, next.JobId);
                if (job.State != ProcessingState.Queued) continue; // finished or cancelled meanwhile
                var dir = next.Dir;
                var progress = new InlineProgress<SttJobProgress>(p => Progress?.Invoke(this, p));
                await _processor.RunAsync(dir, job, _pause, new StateReporter(this, dir, job, progress), _current.Token).ConfigureAwait(false);
                if (token.IsCancellationRequested && job.State == ProcessingState.Cancelled)
                {
                    // The app is closing, not the user cancelling: resume at the next start.
                    job.State = ProcessingStateMachine.Transition(job.State, ProcessingState.Queued);
                    job.Error = null;
                    job.EndedAtUtc = null;
                    SttJobStore.Save(dir, job);
                }
                JobChanged?.Invoke(this, new SttJobChange(dir, job));
                if (job.State == ProcessingState.Queued && !token.IsCancellationRequested)
                {
                    // Preempted by a recording: first in line once it ends.
                    lock (_gate) _queue.AddFirst(next);
                    _signal.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                JobError?.Invoke(this, ex);
            }
            finally
            {
                lock (_gate)
                {
                    _current?.Dispose();
                    _current = null;
                    _currentJob = null;
                }
                if (_queue.Count > 0) _signal.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        _pause.Resume(); // a suspended recognizer must be resumed to be killed
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

    /// <summary>Reports progress, and the job's state change (Normalizing → Transcribing) as a <see cref="JobChanged"/>.</summary>
    private sealed class StateReporter(SttJobRunner runner, string dir, SttJob job, IProgress<SttJobProgress> inner) : IProgress<SttJobProgress>
    {
        private ProcessingState _last = job.State;

        public void Report(SttJobProgress value)
        {
            if (value.State != _last)
            {
                _last = value.State;
                runner.JobChanged?.Invoke(runner, new SttJobChange(dir, job));
            }
            inner.Report(value);
        }
    }
}
