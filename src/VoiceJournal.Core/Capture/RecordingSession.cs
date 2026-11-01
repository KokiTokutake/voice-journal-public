using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Core.Capture;

/// <summary>Kinds of warnings shown while recording.</summary>
public enum RecordingWarningKind
{
    QueueOverflow,
    DeviceLost,
    WriteFailed,
    DiskSpaceLow,
}

public sealed class RecordingWarningEventArgs(RecordingWarningKind kind, AudioSource source, Exception? error) : EventArgs
{
    public RecordingWarningKind Kind { get; } = kind;

    public AudioSource Source { get; } = source;

    public Exception? Error { get; } = error;
}

/// <summary>One track as seen from outside the session.</summary>
public sealed record TrackStatus(AudioSource Source, string DeviceId, string DeviceName, TrackState State);

/// <summary>
/// One recording session (detail spec 6, 7): creates the session folder and manifest, runs one
/// <see cref="TrackRecorder"/> per source, and applies the start/stop/failure rules.
/// The manifest is the only shared mutable state and is updated under a lock and saved atomically.
/// </summary>
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RecordingOptions _options;
    private readonly TimeProvider _time;
    private readonly IFreeSpaceProbe _freeSpace;
    private readonly Dictionary<AudioSource, IAudioCaptureSource> _sources;
    private readonly Dictionary<AudioSource, TrackRecorder> _recorders = [];
    private readonly List<TrackRecorder> _retired = [];
    private long _originTimestamp;
    private Task? _stopTask;
    private bool _started;

    private RecordingSession(string directory, SessionManifest manifest, RecordingOptions options, IReadOnlyList<IAudioCaptureSource> sources, TimeProvider time, IFreeSpaceProbe freeSpace)
    {
        Directory = directory;
        Manifest = manifest;
        _options = options;
        _time = time;
        _freeSpace = freeSpace;
        _sources = sources.ToDictionary(s => s.Source);
    }

    public string Directory { get; }

    /// <summary>Live manifest. Read it only through <see cref="Snapshot"/> from other threads.</summary>
    internal SessionManifest Manifest { get; }

    public Guid SessionId => Manifest.SessionId;

    public CaptureState State
    {
        get { lock (_gate) return Manifest.CaptureState; }
    }

    /// <summary>True when any part of the recording was lost (overflow, device loss, write failure).</summary>
    public bool HasAudioLoss
    {
        get
        {
            lock (_gate)
            {
                return Manifest.Tracks.SelectMany(t => t.Events).Any(e => e.Kind is AudioEventKind.QueueOverflow or AudioEventKind.DeviceDisconnected or AudioEventKind.WriteFailed);
            }
        }
    }

    /// <summary>Raised in order on the thread that changed the state. Handlers must return quickly.</summary>
    /// <summary>
    /// The manifest as it is saved, serialized under the session lock. Lets in-process readers (the DB index)
    /// avoid opening manifest.json while the writer replaces it.
    /// </summary>
    public byte[] SnapshotManifestJson()
    {
        lock (_gate) return Manifest.ToJsonBytes();
    }

    /// <summary>Snapshot of every track (device and state) for display and for choosing what to reconnect.</summary>
    public IReadOnlyList<TrackStatus> GetTracks()
    {
        lock (_gate) return [.. Manifest.Tracks.Select(t => new TrackStatus(t.Source, t.DeviceId, t.DeviceName, t.State))];
    }

    public event EventHandler<CaptureState>? StateChanged;

    /// <summary>
    /// Raised after every manifest save (chunk started or completed, event recorded, state changed), under
    /// the session lock and possibly on the writer thread. Handlers must only signal: the DB index is
    /// updated from another thread by re-reading manifest.json (detail spec 6.2: no DB on the audio path).
    /// </summary>
    public event EventHandler? ManifestSaved;

    public event EventHandler<RecordingWarningEventArgs>? Warning;

    /// <summary>Test seam: forwarded to every track's writer.</summary>
    internal ManualResetEventSlim? WriterGate { get; set; }

    /// <summary>Test seam: blocks until the source's queue is at most half full (accelerated tests only).</summary>
    internal void ThrottleForTesting(AudioSource source)
    {
        TrackRecorder? recorder;
        lock (_gate) _recorders.TryGetValue(source, out recorder);
        recorder?.WaitForQueueBelowHalf();
    }

    /// <summary>Validates sources against the mode and free space, then creates the session folder and manifest.</summary>
    /// <exception cref="CaptureStartException">Sources do not match the mode, or free space is below the minimum.</exception>
    public static RecordingSession Create(
        string workspaceRoot,
        RecordingOptions options,
        IReadOnlyList<IAudioCaptureSource> sources,
        TimeProvider? time = null,
        IFreeSpaceProbe? freeSpace = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sources);
        time ??= TimeProvider.System;
        freeSpace ??= new DriveFreeSpaceProbe();

        var expected = options.Mode switch
        {
            CaptureMode.Mic => new[] { AudioSource.Mic },
            CaptureMode.Loopback => [AudioSource.Loopback],
            CaptureMode.Both => [AudioSource.Mic, AudioSource.Loopback],
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Mode, null),
        };
        if (sources.Count != expected.Length || !expected.All(e => sources.Any(s => s.Source == e)))
            throw new CaptureStartException($"Mode {options.Mode} needs sources [{string.Join(", ", expected)}].");

        System.IO.Directory.CreateDirectory(workspaceRoot);
        if (freeSpace.GetAvailableBytes(workspaceRoot) < options.MinFreeBytes)
            throw new CaptureStartException("Not enough free disk space to start recording (FS-001).");

        var startedAt = time.GetUtcNow();
        var manifest = new SessionManifest
        {
            SessionId = Guid.CreateVersion7(startedAt),
            Title = options.Title,
            Project = options.Project,
            CaptureMode = options.Mode,
            MicMode = options.Mode == CaptureMode.Loopback ? null : options.MicMode,
            StartedAtUtc = startedAt,
            TimeZoneId = time.LocalTimeZone.Id,
        };
        foreach (var source in sources.OrderBy(s => s.Source))
        {
            source.Format.Validate();
            manifest.Tracks.Add(new TrackManifest
            {
                TrackId = Guid.CreateVersion7(startedAt),
                Source = source.Source,
                DeviceId = source.DeviceId,
                DeviceName = source.DeviceName,
                Format = source.Format,
            });
        }

        var directory = SessionPaths.SessionDirectory(workspaceRoot, manifest.SessionId, startedAt);
        foreach (var track in manifest.Tracks)
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "audio", SessionPaths.SourceFolderName(track.Source)));
        manifest.Save(directory);
        return new RecordingSession(directory, manifest, options, sources, time, freeSpace);
    }

    /// <summary>
    /// Starts every source. In Both mode, a failure of either source stops the other and nothing is
    /// recorded (detail spec 6.1); the session ends as Failed.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("Recording already started.");
            _started = true;
            _originTimestamp = _time.GetTimestamp();
        }

        foreach (var track in Manifest.Tracks)
        {
            var recorder = new TrackRecorder(this, _sources[track.Source], track, _options, _time, _originTimestamp, startPositionFrames: 0) { WriterGate = WriterGate };
            try
            {
                recorder.Start();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                recorder.Dispose();
                foreach (var started in _recorders.Values) started.StopAsync(_time.GetTimestamp()).GetAwaiter().GetResult();
                SetState(CaptureState.Failed);
                throw new CaptureStartException($"Could not start {track.Source} ({track.DeviceName}).", track.Source, ex);
            }
            lock (_gate) _recorders[track.Source] = recorder;
        }
        SetState(CaptureState.Recording);
    }

    /// <summary>
    /// "準備中" ends when every mic track has delivered audio. Loopback may legitimately stay silent.
    /// </summary>
    public bool IsAudioFlowing
    {
        get
        {
            lock (_gate) return _recorders.Values.Where(r => r.Source == AudioSource.Mic).All(r => r.PacketsReceived > 0);
        }
    }

    /// <summary>Peak level (0..1) of a source since the last call.</summary>
    public float ReadPeak(AudioSource source)
    {
        lock (_gate) return _recorders.TryGetValue(source, out var r) ? r.ReadAndResetPeak() : 0f;
    }

    /// <summary>
    /// Normal stop: stop capturing → drain queues → finalize chunks → save manifest → Finalized.
    /// Repeated calls return the same task (detail spec 6.2).
    /// </summary>
    /// <summary>Time since <see cref="Start"/> on the monotonic clock (zero before it).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate) return _started && _originTimestamp != 0 ? _time.GetElapsedTime(_originTimestamp) : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Records why the recording ends early (e.g. <c>system_suspend</c>) as an Interrupted event on every
    /// track, before <see cref="StopAsync"/>. The session still finalizes normally.
    /// </summary>
    public void MarkInterruption(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        lock (_gate)
        {
            if (!_started || _stopTask is not null) return;
            var ms = (long)_time.GetElapsedTime(_originTimestamp).TotalMilliseconds;
            foreach (var track in Manifest.Tracks)
                track.Events.Add(new AudioEventManifest { Kind = AudioEventKind.Interrupted, StartMs = ms, Detail = reason });
            TrySaveLocked();
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync();
            return _stopTask;
        }
    }

    /// <summary>Resumes a disconnected source as new chunks; the gap stays missing (detail spec 6.4).</summary>
    public void Reconnect(IAudioCaptureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        TrackRecorder recorder;
        TrackManifest track;
        lock (_gate)
        {
            if (_stopTask is not null) throw new InvalidOperationException("Recording is stopping.");
            track = Manifest.Tracks.Single(t => t.Source == source.Source);
            if (track.State != TrackState.Disconnected) throw new InvalidOperationException($"{source.Source} is not disconnected.");
            if (source.Format != track.Format) throw new InvalidOperationException("The reconnected device uses a different format; start a new recording instead.");

            var now = _time.GetTimestamp();
            var position = (long)(_time.GetElapsedTime(_originTimestamp, now).TotalSeconds * track.Format.SampleRate);
            recorder = new TrackRecorder(this, source, track, _options, _time, _originTimestamp, position) { WriterGate = WriterGate };
            if (_recorders.Remove(source.Source, out var old)) _retired.Add(old);
            _recorders[source.Source] = recorder;
            _sources[source.Source] = source;
            track.State = TrackState.Recording;
            track.Events.Add(new AudioEventManifest { Kind = AudioEventKind.DeviceReconnected, StartMs = position * 1000 / track.Format.SampleRate, Detail = source.DeviceName });
        }
        recorder.Start();
        lock (_gate)
        {
            if (Manifest.CaptureState == CaptureState.Partial && Manifest.Tracks.All(t => t.State == TrackState.Recording))
                SetStateLocked(CaptureState.Recording);
            else
                SaveLocked();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_started) await StopAsync().ConfigureAwait(false);
        foreach (var r in _recorders.Values.Concat(_retired)) r.Dispose();
    }

    // ---- called by TrackRecorder --------------------------------------------------------------

    internal void UpdateManifest(Action change)
    {
        lock (_gate)
        {
            change();
            SaveLocked();
        }
    }

    internal void AddEvent(TrackManifest track, AudioEventManifest e) => UpdateManifest(() => track.Events.Add(e));

    internal void OnTrackOverflow(TrackRecorder recorder)
    {
        Warning?.Invoke(this, new RecordingWarningEventArgs(RecordingWarningKind.QueueOverflow, recorder.Source, null));
        _ = Task.Run(StopAsync); // never stop from the capture thread itself
    }

    internal void OnTrackSourceLost(TrackRecorder recorder, Exception error)
    {
        lock (_gate)
        {
            recorder.Track.State = TrackState.Disconnected;
            var position = (long)(_time.GetElapsedTime(_originTimestamp).TotalSeconds * 1000);
            recorder.Track.Events.Add(new AudioEventManifest { Kind = AudioEventKind.DeviceDisconnected, StartMs = position, Detail = error is AudioDeviceException d ? d.Kind.ToString() : error.GetType().Name });
            if (Manifest.CaptureState == CaptureState.Recording) SetStateLocked(CaptureState.Partial);
            else SaveLocked();
        }
        Warning?.Invoke(this, new RecordingWarningEventArgs(RecordingWarningKind.DeviceLost, recorder.Source, error));
    }

    internal void OnTrackWriteFailed(TrackRecorder recorder, Exception error, long positionMs)
    {
        lock (_gate)
        {
            recorder.Track.State = TrackState.Failed;
            recorder.Track.Events.Add(new AudioEventManifest { Kind = AudioEventKind.WriteFailed, StartMs = positionMs, Detail = error.GetType().Name });
            TrySaveLocked();
        }
        Warning?.Invoke(this, new RecordingWarningEventArgs(RecordingWarningKind.WriteFailed, recorder.Source, error));
        _ = Task.Run(StopAsync);
    }

    internal void CheckFreeSpace(TrackRecorder recorder)
    {
        long available;
        try { available = _freeSpace.GetAvailableBytes(Directory); }
        catch (IOException) { return; }
        if (available >= _options.MinFreeBytes) return;

        lock (_gate)
        {
            if (_stopTask is not null) return;
            var position = (long)(_time.GetElapsedTime(_originTimestamp).TotalSeconds * 1000);
            recorder.Track.Events.Add(new AudioEventManifest { Kind = AudioEventKind.DiskSpaceLow, StartMs = position, Detail = $"available={available}" });
        }
        Warning?.Invoke(this, new RecordingWarningEventArgs(RecordingWarningKind.DiskSpaceLow, recorder.Source, null));
        _ = Task.Run(StopAsync);
    }

    // ---- internals ------------------------------------------------------------------------------

    private async Task StopCoreAsync()
    {
        if (!_started) return;
        lock (_gate)
        {
            if (Manifest.CaptureState is CaptureState.Recording or CaptureState.Partial) SetStateLocked(CaptureState.Stopping);
        }

        var stopAt = _time.GetTimestamp();
        TrackRecorder[] recorders;
        lock (_gate) recorders = [.. _recorders.Values];
        await Task.WhenAll(recorders.Select(r => r.StopAsync(stopAt))).ConfigureAwait(false);
        await Task.WhenAll(_retired.Select(r => r.WriterCompletion)).ConfigureAwait(false);

        lock (_gate)
        {
            foreach (var track in Manifest.Tracks.Where(t => t.State == TrackState.Recording)) track.State = TrackState.Stopped;
            Manifest.EndedAtUtc = _time.GetUtcNow();
            if (Manifest.CaptureState == CaptureState.Stopping) SetStateLocked(CaptureState.Finalized);
            else TrySaveLocked();
        }
    }

    private void SetState(CaptureState state)
    {
        lock (_gate) SetStateLocked(state);
    }

    private void SetStateLocked(CaptureState state)
    {
        Manifest.CaptureState = CaptureStateMachine.Transition(Manifest.CaptureState, state);
        SaveLocked();
        // Raised in order, under the lock. Handlers must not block (the UI should marshal to its dispatcher).
        StateChanged?.Invoke(this, state);
    }

    private void TrySaveLocked()
    {
        try { SaveLocked(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SaveLocked()
    {
        Manifest.Save(Directory);
        ManifestSaved?.Invoke(this, EventArgs.Empty);
    }
}
