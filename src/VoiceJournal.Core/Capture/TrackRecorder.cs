using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Core.Capture;

/// <summary>
/// Records one source into chunk files (detail spec 6.2 / 6.3):
/// capture callback → copy into a bounded queue → dedicated writer thread → chunk files.
/// Positions are frames on the session timeline (frame 0 = session start).
/// </summary>
internal sealed class TrackRecorder : IDisposable
{
    private readonly RecordingSession _session;
    private readonly IAudioCaptureSource _source;
    private readonly TrackManifest _track;
    private readonly AudioFormatInfo _format;
    private readonly RecordingOptions _options;
    private readonly TimeProvider _time;
    private readonly long _originTimestamp;
    private readonly long _chunkFrames;
    private readonly long _capacityBytes;
    private readonly long _gapToleranceFrames;
    private readonly long _freeSpaceCheckFrames;
    private readonly long _maxTimestampSkewFrames;
    private readonly bool _isLoopback;
    private readonly ConcurrentQueue<Packet> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TaskCompletionSource _writerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _sourceStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    private long _queuedBytes;
    private volatile bool _accepting = true;
    private volatile bool _completing;
    private long _overflowTimestamp;
    private int _overflowed;
    private float _peak;
    private long _packetsReceived;
    private long _completeAtTimestamp;

    // Writer-thread state.
    private WavChunkWriter? _writer;
    private ChunkManifest? _chunk;
    private long _positionFrames;
    private bool _positionKnown;
    private long _framesSinceSpaceCheck;

    // Time map: the timeline position where the latest device-timestamped packet starts, and the
    // monotonic-clock frame at which it was captured.
    private bool _clockKnown;
    private bool _sawPacket;
    private long _clockAnchorPosition;
    private long _clockAnchorFrames;

    public TrackRecorder(
        RecordingSession session,
        IAudioCaptureSource source,
        TrackManifest track,
        RecordingOptions options,
        TimeProvider time,
        long originTimestamp,
        long startPositionFrames)
    {
        _session = session;
        _source = source;
        _track = track;
        _format = track.Format;
        _options = options;
        _time = time;
        _originTimestamp = originTimestamp;
        _isLoopback = track.Source == AudioSource.Loopback;
        _chunkFrames = Math.Max(1, (long)(options.ChunkDuration.TotalSeconds * _format.SampleRate));
        _capacityBytes = Math.Max(_format.BlockAlign, (long)(options.QueueCapacity.TotalSeconds * _format.AverageBytesPerSecond));
        _gapToleranceFrames = (long)(options.LoopbackGapTolerance.TotalSeconds * _format.SampleRate);
        _freeSpaceCheckFrames = Math.Max(1, (long)(options.FreeSpaceCheckInterval.TotalSeconds * _format.SampleRate));
        _maxTimestampSkewFrames = (long)(MaxTimestampSkew.TotalSeconds * _format.SampleRate);

        // Loopback timeline starts at the given position and silence is filled from there;
        // mic timeline starts where its first packet lands.
        _positionFrames = startPositionFrames;
        _positionKnown = _isLoopback;

        // Loopback silence is filled from the session clock itself, so until a timestamped packet
        // arrives the time map is the identity (position = clock).
        _clockKnown = _isLoopback;
        _clockAnchorPosition = startPositionFrames;
        _clockAnchorFrames = startPositionFrames;

        _thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = $"vj-writer-{SessionPaths.SourceFolderName(track.Source)}",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>A device timestamp further than this from the arrival time is ignored (wrong clock domain, glitch).</summary>
    internal static TimeSpan MaxTimestampSkew { get; } = TimeSpan.FromSeconds(2);

    public AudioSource Source => _track.Source;

    public TrackManifest Track => _track;

    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    public bool HasOverflowed => Volatile.Read(ref _overflowed) == 1;

    public Task WriterCompletion => _writerDone.Task;

    /// <summary>Test seam: when set, the writer waits on it before handling each packet.</summary>
    internal ManualResetEventSlim? WriterGate { get; set; }

    /// <summary>Test seam: lets accelerated tests feed audio faster than real time without overflowing.</summary>
    internal void WaitForQueueBelowHalf()
    {
        var spin = new SpinWait();
        while (Interlocked.Read(ref _queuedBytes) > _capacityBytes / 2 && !_writerDone.Task.IsCompleted) spin.SpinOnce(sleep1Threshold: 10);
    }

    public void Start()
    {
        _source.DataAvailable += OnData;
        _source.Stopped += OnSourceStopped;
        _thread.Start();
        try
        {
            _source.StartCapture();
        }
        catch
        {
            _source.DataAvailable -= OnData;
            _source.Stopped -= OnSourceStopped;
            RequestComplete(_time.GetTimestamp());
            throw;
        }
    }

    /// <summary>Peak absolute sample value since the last call (0..1), for level meters.</summary>
    public float ReadAndResetPeak() => Interlocked.Exchange(ref _peak, 0f);

    /// <summary>Stops the source, drains the queue and finalizes the current chunk.</summary>
    public async Task StopAsync(long stopTimestamp)
    {
        _accepting = false;
        try { _source.StopCapture(); }
        catch (InvalidOperationException) { }
        await Task.WhenAny(_sourceStopped.Task, Task.Delay(_options.SourceStopTimeout, _time)).ConfigureAwait(false);
        RequestComplete(stopTimestamp);
        await _writerDone.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _source.DataAvailable -= OnData;
        _source.Stopped -= OnSourceStopped;
        _signal.Dispose();
        while (_queue.TryDequeue(out var late)) ArrayPool<byte>.Shared.Return(late.Buffer);
    }

    // ---- capture thread ---------------------------------------------------------------------

    private void OnData(ReadOnlySpan<byte> data, AudioPacketStatus flags, long captureTimestamp)
    {
        if (!_accepting || data.IsEmpty) return;
        var now = _time.GetTimestamp();
        var length = data.Length - data.Length % _format.BlockAlign;
        if (length == 0) return;

        if (Interlocked.Add(ref _queuedBytes, length) > _capacityBytes)
        {
            Interlocked.Add(ref _queuedBytes, -length);
            if (Interlocked.Exchange(ref _overflowed, 1) == 0)
            {
                _overflowTimestamp = now;
                _accepting = false;
                _session.OnTrackOverflow(this);
            }
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        data[..length].CopyTo(buffer);
        if ((flags & AudioPacketStatus.Silent) == 0) UpdatePeak(buffer.AsSpan(0, length));
        Interlocked.Increment(ref _packetsReceived);
        _queue.Enqueue(new Packet(buffer, length, flags, now, captureTimestamp));
        try { _signal.Release(); }
        catch (ObjectDisposedException) { } // a late callback racing with Dispose; the packet is reclaimed there
    }

    private void OnSourceStopped(object? sender, CaptureSourceStoppedEventArgs e)
    {
        _sourceStopped.TrySetResult();
        if (e.Error is not null && _accepting)
        {
            // Device lost while recording (CAP-002): close this track, keep the others running.
            _accepting = false;
            RequestComplete(_time.GetTimestamp());
            _session.OnTrackSourceLost(this, e.Error);
        }
    }

    private void UpdatePeak(ReadOnlySpan<byte> data)
    {
        float peak = 0;
        if (_format.Encoding == AudioSampleEncoding.IeeeFloat && _format.BitsPerSample == 32)
        {
            foreach (var s in MemoryMarshal.Cast<byte, float>(data)) peak = Math.Max(peak, Math.Abs(s));
        }
        else if (_format.Encoding == AudioSampleEncoding.Pcm && _format.BitsPerSample == 16)
        {
            foreach (var s in MemoryMarshal.Cast<byte, short>(data)) peak = Math.Max(peak, Math.Abs(s / 32768f));
        }
        if (peak > _peak) _peak = peak; // single capture thread per source; a lost race only affects the meter
    }

    private void RequestComplete(long timestamp)
    {
        if (_completing) return;
        Interlocked.CompareExchange(ref _completeAtTimestamp, timestamp, 0);
        _completing = true;
        try { _signal.Release(); }
        catch (ObjectDisposedException) { }
    }

    // ---- writer thread ----------------------------------------------------------------------

    private void WriterLoop()
    {
        try
        {
            while (true)
            {
                _signal.Wait();
                while (_queue.TryDequeue(out var packet))
                {
                    try
                    {
                        WriterGate?.Wait();
                        HandlePacket(packet);
                    }
                    finally
                    {
                        Interlocked.Add(ref _queuedBytes, -packet.Length);
                        ArrayPool<byte>.Shared.Return(packet.Buffer);
                    }
                }
                if (_completing && _queue.IsEmpty) break;
            }
            Finish();
            _writerDone.TrySetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            HandleWriteFailure(ex);
            _writerDone.TrySetResult();
        }
#pragma warning disable CA1031 // The writer thread must never crash the process; report and stop instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HandleWriteFailure(ex);
            _writerDone.TrySetResult();
        }
    }

    private void HandlePacket(Packet packet)
    {
        var frames = packet.Length / _format.BlockAlign;
        var arrivalFrames = ToFrames(packet.Timestamp);
        var packetStart = Math.Max(0, arrivalFrames - frames);
        var timestamped = false;
        if (packet.CaptureTimestamp != 0)
        {
            // Prefer the device's capture time: arrival times jitter by the buffer length (~100 ms).
            var captured = ToFrames(packet.CaptureTimestamp);
            if (Math.Abs(arrivalFrames - (captured + frames)) <= _maxTimestampSkewFrames)
            {
                packetStart = Math.Max(0, captured);
                timestamped = true;
            }
        }

        if (!_positionKnown)
        {
            _positionFrames = packetStart;
            _positionKnown = true;
            if (_track.Chunks.Count == 0) _track.StartOffsetMs = FramesToMs(packetStart);
        }

        // The first packet of a stream (also after a reconnect) is commonly flagged; only later
        // discontinuities are real gaps.
        var firstPacket = !_sawPacket;
        _sawPacket = true;
        if ((packet.Flags & AudioPacketStatus.Discontinuity) != 0 && !firstPacket)
        {
            _session.AddEvent(_track, new AudioEventManifest { Kind = AudioEventKind.Discontinuity, StartMs = FramesToMs(_positionFrames) });
        }

        var fill = _isLoopback && packetStart - _positionFrames > _gapToleranceFrames ? packetStart - _positionFrames : 0;
        if (timestamped)
        {
            // Anchor before filling, so a chunk that starts inside the fill already has its clock time.
            _clockKnown = true;
            _clockAnchorPosition = _positionFrames + fill;
            _clockAnchorFrames = packetStart;
        }

        if (fill > 0) FillSilence(fill, recordEvent: true);

        if ((packet.Flags & AudioPacketStatus.Silent) != 0) WriteSilenceFrames(frames);
        else WriteFrames(packet.Buffer.AsSpan(0, packet.Length));
    }

    private void Finish()
    {
        if (_isLoopback && _positionKnown)
        {
            var end = ToFrames(Interlocked.Read(ref _completeAtTimestamp));
            if (end - _positionFrames > 0) FillSilence(end - _positionFrames, recordEvent: end - _positionFrames > _gapToleranceFrames);
        }

        if (HasOverflowed)
        {
            _session.AddEvent(_track, new AudioEventManifest
            {
                Kind = AudioEventKind.QueueOverflow,
                StartMs = FramesToMs(_positionFrames),
                EndMs = FramesToMs(ToFrames(Interlocked.Read(ref _completeAtTimestamp))),
                Detail = "bounded queue overflow; audio in this range was not recorded",
            });
        }

        CompleteCurrentChunk();
    }

    private void FillSilence(long frames, bool recordEvent)
    {
        var start = _positionFrames;
        WriteSilenceFrames(frames);
        if (recordEvent)
        {
            _session.AddEvent(_track, new AudioEventManifest { Kind = AudioEventKind.LoopbackSilence, StartMs = FramesToMs(start), EndMs = FramesToMs(_positionFrames) });
        }
    }

    private void WriteFrames(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var writer = EnsureChunk();
            var room = _chunkFrames - writer.FramesWritten;
            var frames = (int)Math.Min(room, data.Length / _format.BlockAlign);
            writer.Append(data[..(frames * _format.BlockAlign)]);
            data = data[(frames * _format.BlockAlign)..];
            Advance(frames);
        }
    }

    private void WriteSilenceFrames(long frames)
    {
        while (frames > 0)
        {
            var writer = EnsureChunk();
            var n = Math.Min(frames, _chunkFrames - writer.FramesWritten);
            writer.AppendSilence(n);
            frames -= n;
            Advance(n);
        }
    }

    private void Advance(long frames)
    {
        _positionFrames += frames;
        _framesSinceSpaceCheck += frames;
        if (_writer!.FramesWritten >= _chunkFrames) CompleteCurrentChunk();
        if (_framesSinceSpaceCheck >= _freeSpaceCheckFrames)
        {
            _framesSinceSpaceCheck = 0;
            _session.CheckFreeSpace(this);
        }
    }

    private WavChunkWriter EnsureChunk()
    {
        if (_writer is not null) return _writer;
        var sequence = _track.Chunks.Count == 0 ? 1 : _track.Chunks.Max(c => c.Sequence) + 1;
        var relative = SessionPaths.PartialChunkFile(_track.Source, sequence);
        var chunk = new ChunkManifest { Sequence = sequence, File = relative, StartMs = FramesToMs(_positionFrames), ClockStartMs = ClockMsAt(_positionFrames), State = ChunkState.Partial };
        _writer = WavChunkWriter.Create(SessionPaths.Resolve(_session.Directory, relative), _format, chunk.StartMs, _time);
        _chunk = chunk;
        _session.UpdateManifest(() => _track.Chunks.Add(chunk));
        return _writer;
    }

    private void CompleteCurrentChunk()
    {
        if (_writer is null || _chunk is null) return;
        var writer = _writer;
        var chunk = _chunk;
        _writer = null;
        _chunk = null;

        if (writer.FramesWritten == 0)
        {
            var partialPath = writer.PartialPath;
            writer.Dispose();
            File.Delete(partialPath);
            File.Delete(ChunkSidecar.PathFor(partialPath));
            _session.UpdateManifest(() => _track.Chunks.Remove(chunk));
            return;
        }

        var relative = SessionPaths.ChunkFile(_track.Source, chunk.Sequence);
        var done = writer.Complete(SessionPaths.Resolve(_session.Directory, relative));
        writer.Dispose();
        var clockEnd = ClockMsAt(_positionFrames);
        _session.UpdateManifest(() =>
        {
            chunk.File = relative;
            chunk.ClockStartMs ??= ClockMsAt(_positionFrames - done.FrameCount);
            chunk.ClockEndMs = clockEnd;
            chunk.FrameCount = done.FrameCount;
            chunk.SizeBytes = done.SizeBytes;
            chunk.Sha256 = done.Sha256;
            chunk.State = ChunkState.Completed;
        });
    }

    private void HandleWriteFailure(Exception ex)
    {
        _accepting = false;
        try { _writer?.Dispose(); } // keeps the .partial and its last good sidecar
        catch (IOException) { }
        _writer = null;
        while (_queue.TryDequeue(out var packet)) ArrayPool<byte>.Shared.Return(packet.Buffer);
        _session.OnTrackWriteFailed(this, ex, FramesToMs(_positionFrames));
    }

    private long ToFrames(long timestamp) =>
        (long)(_time.GetElapsedTime(_originTimestamp, timestamp).TotalSeconds * _format.SampleRate);

    private long FramesToMs(long frames) => frames * 1000 / _format.SampleRate;

    /// <summary>Clock time of a timeline position, extrapolated at the nominal rate from the latest timestamped packet.</summary>
    private long? ClockMsAt(long position) => _clockKnown ? FramesToMs(_clockAnchorFrames + (position - _clockAnchorPosition)) : null;

    private readonly record struct Packet(byte[] Buffer, int Length, AudioPacketStatus Flags, long Timestamp, long CaptureTimestamp);
}
