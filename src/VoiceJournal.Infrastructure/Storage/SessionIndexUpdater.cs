using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>
/// Keeps the index of one recording session up to date from a background task (detail spec 6.2: the
/// audio path never touches the DB). <see cref="RecordingSession.ManifestSaved"/> only signals; the
/// worker waits briefly so a burst of saves becomes one write, then indexes a snapshot of the manifest. A failed
/// write (e.g. DB busy) is retried later; the manifest stays the source of truth meanwhile.
/// </summary>
public sealed class SessionIndexUpdater : IAsyncDisposable
{
    private readonly ISessionIndex _index;
    private readonly RecordingSession _session;
    private readonly TimeSpan _coalesce;
    private readonly TimeSpan _retryDelay;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly List<(long Version, TaskCompletionSource Done)> _flushWaiters = [];
    private readonly Task _worker;
    private long _requested;
    private long _indexed;

    public SessionIndexUpdater(ISessionIndex index, RecordingSession session, TimeSpan? coalesce = null, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(session);
        _index = index;
        _session = session;
        _coalesce = coalesce ?? TimeSpan.FromMilliseconds(500);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        _session.ManifestSaved += OnManifestSaved;
        _worker = Task.Run(RunAsync);
        Request(); // index the session as soon as it exists
    }

    /// <summary>The last indexing failure, or null after a later success. Raised on the worker task.</summary>
    public event EventHandler<Exception>? IndexFailed;

    public Exception? LastError { get; private set; }

    /// <summary>Indexes the current manifest now and waits for it (call after StopAsync).</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _flushWaiters.Add((Interlocked.Increment(ref _requested), tcs));
        _signal.Release();
        return tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _session.ManifestSaved -= OnManifestSaved;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        lock (_gate)
        {
            foreach (var (_, done) in _flushWaiters) done.TrySetCanceled();
            _flushWaiters.Clear();
        }
        _signal.Dispose();
        _cts.Dispose();
    }

    // Called under the session lock, possibly on the writer thread: signal only.
    private void OnManifestSaved(object? sender, EventArgs e) => Request();

    private void Request()
    {
        Interlocked.Increment(ref _requested);
        try { _signal.Release(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            await _signal.WaitAsync(token).ConfigureAwait(false);
            if (!HasFlushWaiters()) await Task.Delay(_coalesce, token).ConfigureAwait(false);
            while (_signal.CurrentCount > 0) await _signal.WaitAsync(token).ConfigureAwait(false); // absorb the burst

            var target = Interlocked.Read(ref _requested);
            if (target <= Interlocked.Read(ref _indexed)) continue;
            try
            {
                // In-memory snapshot: never open manifest.json while the writer may be replacing it.
                _index.Upsert(_session.Directory, _session.SnapshotManifestJson());
                Interlocked.Exchange(ref _indexed, target);
                LastError = null;
                CompleteWaiters(target, null);
            }
#pragma warning disable CA1031 // Any failure leaves the manifest as the source of truth; report and retry.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LastError = ex;
                IndexFailed?.Invoke(this, ex);
                CompleteWaiters(target, ex);
                await Task.Delay(_retryDelay, token).ConfigureAwait(false);
                Request();
            }
        }
    }

    private bool HasFlushWaiters()
    {
        lock (_gate) return _flushWaiters.Count > 0;
    }

    private void CompleteWaiters(long version, Exception? error)
    {
        lock (_gate)
        {
            for (var i = _flushWaiters.Count - 1; i >= 0; i--)
            {
                var (target, done) = _flushWaiters[i];
                if (target > version) continue;
                if (error is null) done.TrySetResult();
                else done.TrySetException(error);
                _flushWaiters.RemoveAt(i);
            }
        }
    }
}
