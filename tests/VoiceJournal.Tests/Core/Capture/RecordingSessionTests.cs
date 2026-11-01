using System.Security.Cryptography;
using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Tests.TestSupport;
using CaptureState = VoiceJournal.Core.Capture.CaptureState;

namespace VoiceJournal.Tests.Core.Capture;

public sealed class RecordingSessionTests : IDisposable
{
    // Small formats keep the tests fast while exercising the same code paths as 48 kHz float.
    private static readonly AudioFormatInfo MicFormat = AudioFormatInfo.Pcm16Bit(8000, 1);
    private static readonly AudioFormatInfo LoopbackFormat = AudioFormatInfo.IeeeFloat32Bit(8000, 2);

    private readonly TempDirectory _ws = new("vj-rec-");
    private readonly ManualTimeProvider _clock = new();
    private readonly FakeFreeSpace _disk = new(100L * 1024 * 1024 * 1024);

    public void Dispose() => _ws.Dispose();

    [Fact]
    public async Task Mic60Seconds_IsSavedWithMatchingLength() // AT-01 (logic part)
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic);
        session.Start();
        Assert.False(session.IsAudioFlowing); // "準備中" until the first packet

        mic.PushFor(_clock, TimeSpan.FromSeconds(60));
        Assert.True(session.IsAudioFlowing);
        await session.StopAsync();

        var manifest = SessionManifest.Load(session.Directory);
        Assert.Equal(CaptureState.Finalized, manifest.CaptureState);
        Assert.Equal(CaptureMode.Mic, manifest.CaptureMode);
        Assert.Equal(MicProcessingMode.Raw, manifest.MicMode);
        var track = Assert.Single(manifest.Tracks);
        Assert.Equal(TrackState.Stopped, track.State);
        var chunk = Assert.Single(track.Chunks);
        Assert.Equal(ChunkState.Completed, chunk.State);
        Assert.Equal(480_000, chunk.FrameCount);

        // Recorded length vs elapsed time: within 100 ms (AT-01 target).
        var recordedMs = track.StartOffsetMs + chunk.FrameCount * 1000 / MicFormat.SampleRate;
        Assert.InRange(recordedMs, 60_000 - 100, 60_000 + 100);

        var path = SessionPaths.Resolve(session.Directory, chunk.File);
        Assert.Equal(chunk.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        using var reader = new WaveFileReader(path);
        Assert.Equal(TimeSpan.FromSeconds(60), reader.TotalTime);
        Assert.Empty(Directory.GetFiles(session.Directory, "*.partial", SearchOption.AllDirectories));
        Assert.False(session.HasAudioLoss);
    }

    [Fact]
    public async Task Chunks_RotateAtConfiguredLength_OnTheTimeline()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic, o => o with { ChunkDuration = TimeSpan.FromMinutes(1) });
        session.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(150));
        await session.StopAsync();

        var chunks = SessionManifest.Load(session.Directory).Tracks[0].Chunks;
        Assert.Equal([1, 2, 3], chunks.Select(c => c.Sequence));
        Assert.Equal([0L, 60_000L, 120_000L], chunks.Select(c => c.StartMs));
        Assert.Equal([480_000L, 480_000L, 240_000L], chunks.Select(c => c.FrameCount));
        Assert.All(chunks, c => Assert.Equal(ChunkState.Completed, c.State));
        Assert.All(chunks, c => Assert.True(File.Exists(SessionPaths.Resolve(session.Directory, c.File))));
    }

    [Fact]
    public async Task Loopback_FillsSilenceWhenNothingPlays_AndKeepsWallClockLength() // AT-02 (logic part)
    {
        var loop = new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat);
        await using var session = Create(CaptureMode.Loopback, loop);
        session.Start();

        _clock.Advance(TimeSpan.FromSeconds(2));          // nothing playing: no packets at all
        loop.PushFor(_clock, TimeSpan.FromSeconds(1));    // 2s..3s
        _clock.Advance(TimeSpan.FromSeconds(3));          // silent again
        loop.PushFor(_clock, TimeSpan.FromSeconds(1));    // 6s..7s
        _clock.Advance(TimeSpan.FromSeconds(1));          // silent until stop at 8s
        await session.StopAsync();

        var track = SessionManifest.Load(session.Directory).Tracks[0];
        var chunk = Assert.Single(track.Chunks);
        Assert.InRange(chunk.FrameCount, 8 * 8000 - 80, 8 * 8000 + 80);
        var gaps = track.Events.Where(e => e.Kind == AudioEventKind.LoopbackSilence).Select(e => (e.StartMs, e.EndMs)).ToList();
        Assert.Equal(3, gaps.Count);
        Assert.InRange(gaps[0].StartMs, 0, 0);
        Assert.InRange(gaps[0].EndMs!.Value, 1990, 2010);
        Assert.InRange(gaps[1].StartMs, 2990, 3010);
        Assert.InRange(gaps[1].EndMs!.Value, 5990, 6010);
        Assert.InRange(gaps[2].EndMs!.Value, 7990, 8010);
        Assert.False(session.HasAudioLoss); // silence is not loss

        // Inserted silence is real zeros at the right place; played audio is intact.
        using var reader = new WaveFileReader(SessionPaths.Resolve(session.Directory, chunk.File));
        var all = new byte[reader.Length];
        reader.ReadExactly(all);
        var blockAlign = LoopbackFormat.BlockAlign;
        Assert.All(all.AsSpan(0, 2 * 8000 * blockAlign - 2 * blockAlign).ToArray(), b => Assert.Equal(0, b));
        Assert.All(all.AsSpan(2 * 8000 * blockAlign + 80 * blockAlign, 8000 * blockAlign / 2).ToArray(), b => Assert.Equal(0x11, b));
    }

    [Fact]
    public async Task SilentPackets_AreWrittenAsZeros()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic);
        session.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(1), value: 0x7F, status: AudioPacketStatus.Silent);
        await session.StopAsync();

        var chunk = SessionManifest.Load(session.Directory).Tracks[0].Chunks[0];
        using var reader = new WaveFileReader(SessionPaths.Resolve(session.Directory, chunk.File));
        var all = new byte[reader.Length];
        reader.ReadExactly(all);
        Assert.All(all, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task QueueOverflow_RecordsLossAndStopsSafely() // CAP-003
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        using var gate = new ManualResetEventSlim(false); // writer is stuck (e.g. disk stalled)
        await using var session = Create(CaptureMode.Mic, mic);
        session.WriterGate = gate;
        mic.AfterPush = null; // no throttling: the stalled writer must overflow
        var warnings = new List<RecordingWarningKind>();
        session.Warning += (_, e) => { lock (warnings) warnings.Add(e.Kind); };
        session.Start();

        mic.PushFor(_clock, TimeSpan.FromSeconds(3)); // queue holds 2 s
        lock (warnings) Assert.Contains(RecordingWarningKind.QueueOverflow, warnings);

        gate.Set();
        await WaitUntil(() => session.State == CaptureState.Finalized);

        var manifest = SessionManifest.Load(session.Directory);
        var track = manifest.Tracks[0];
        var overflow = Assert.Single(track.Events, e => e.Kind == AudioEventKind.QueueOverflow);
        Assert.NotNull(overflow.EndMs);
        Assert.True(session.HasAudioLoss);
        var recorded = track.Chunks.Sum(c => c.FrameCount);
        Assert.InRange(recorded, 15 * 800, 2 * 8000); // everything accepted before the overflow is kept
        Assert.All(track.Chunks, c => Assert.Equal(ChunkState.Completed, c.State));
    }

    [Fact]
    public async Task Stop_IsIdempotent_AndStatesFollowTheSpec()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic);
        var states = new List<CaptureState>();
        session.StateChanged += (_, s) => { lock (states) states.Add(s); };
        session.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(1));

        var first = session.StopAsync();
        var second = session.StopAsync();
        Assert.Same(first, second);
        await first;

        lock (states) Assert.Equal([CaptureState.Recording, CaptureState.Stopping, CaptureState.Finalized], states);
        Assert.False(mic.IsCapturing);
    }

    [Fact]
    public async Task BothMode_WhenOneSourceFails_NothingKeepsRunning() // AT-04
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        var loop = new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat) { ThrowOnStart = new InvalidOperationException("device busy") };
        await using var session = Create(CaptureMode.Both, mic, loop);

        var ex = Assert.Throws<CaptureStartException>(session.Start);
        Assert.Equal(AudioSource.Loopback, ex.FailedSource);
        Assert.False(mic.IsCapturing);
        Assert.Equal(CaptureState.Failed, SessionManifest.Load(session.Directory).CaptureState);
    }

    [Fact]
    public async Task MicPermissionDenied_FailsToStart() // AT-04 / CAP-001
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat) { ThrowOnStart = new UnauthorizedAccessException("denied") };
        await using var session = Create(CaptureMode.Mic, mic);
        var ex = Assert.Throws<CaptureStartException>(session.Start);
        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
    }

    [Fact]
    public void SourcesMustMatchMode()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        Assert.Throws<CaptureStartException>(() => Create(CaptureMode.Both, mic));
    }

    [Fact]
    public void LowDiskSpace_RefusesToStart() // FS-001
    {
        _disk.AvailableBytes = 1024;
        Assert.Throws<CaptureStartException>(() => Create(CaptureMode.Mic, new FakeCaptureSource(AudioSource.Mic, MicFormat)));
    }

    [Fact]
    public async Task DiskRunningLow_StopsSafelyAndRecordsEvent() // FS-001
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic, o => o with { FreeSpaceCheckInterval = TimeSpan.FromSeconds(1) });
        session.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(2));
        _disk.AvailableBytes = 1024;
        mic.PushFor(_clock, TimeSpan.FromSeconds(2));

        await WaitUntil(() => session.State == CaptureState.Finalized);
        var track = SessionManifest.Load(session.Directory).Tracks[0];
        Assert.Contains(track.Events, e => e.Kind == AudioEventKind.DiskSpaceLow);
        Assert.All(track.Chunks, c => Assert.Equal(ChunkState.Completed, c.State));
    }

    [Fact]
    public async Task DeviceUnplugged_OtherSourceContinues_ReconnectStartsNewChunk() // CAP-002 / AT-19
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        var loop = new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat);
        await using var session = Create(CaptureMode.Both, mic, loop);
        session.Start();

        PushBoth(mic, loop, TimeSpan.FromSeconds(1));
        mic.Fail(new IOException("device removed"));
        Assert.Equal(CaptureState.Partial, session.State);

        loop.PushFor(_clock, TimeSpan.FromSeconds(2)); // loopback keeps recording alone
        var mic2 = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        Throttle(session, mic2);
        session.Reconnect(mic2);
        Assert.Equal(CaptureState.Recording, session.State);
        _clock.Advance(TimeSpan.FromMilliseconds(10));
        mic2.Push(new byte[80 * MicFormat.BlockAlign], AudioPacketStatus.Discontinuity); // a new stream starts flagged
        loop.Push(new byte[80 * LoopbackFormat.BlockAlign]);
        PushBoth(mic2, loop, TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(10));
        await session.StopAsync();

        var manifest = SessionManifest.Load(session.Directory);
        Assert.Equal(CaptureState.Finalized, manifest.CaptureState);
        var micTrack = manifest.Tracks.Single(t => t.Source == AudioSource.Mic);
        Assert.Equal([1, 2], micTrack.Chunks.Select(c => c.Sequence));
        Assert.Contains(micTrack.Events, e => e.Kind == AudioEventKind.DeviceDisconnected);
        Assert.Contains(micTrack.Events, e => e.Kind == AudioEventKind.DeviceReconnected);
        Assert.DoesNotContain(micTrack.Events, e => e.Kind == AudioEventKind.Discontinuity);
        // The gap stays missing: the second chunk starts after the first one ended.
        var firstEnd = micTrack.Chunks[0].StartMs + micTrack.Chunks[0].FrameCount * 1000 / MicFormat.SampleRate;
        Assert.True(micTrack.Chunks[1].StartMs >= firstEnd + 1900);
        var loopTrack = manifest.Tracks.Single(t => t.Source == AudioSource.Loopback);
        Assert.InRange(loopTrack.Chunks.Sum(c => c.FrameCount), 4 * 8000 - 160, 4 * 8000 + 160);
        Assert.True(session.HasAudioLoss);
    }

    [Fact]
    public async Task Crash_IsRecoveredIntoNewFile_AndSessionBecomesInterrupted() // REC-001 / AT-19
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic);
        session.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(3.5));
        await WaitUntil(() => ChunkSidecar.TryLoad(SidecarOf(session))?.FramesWritten >= 3 * 8000);

        // Snapshot the folder while recording, as if the process died right now.
        var crashed = CopyDirectory(session.Directory, Path.Combine(_ws.Path, "crashed"));
        var partialCopy = Directory.GetFiles(crashed, "*.partial", SearchOption.AllDirectories).Single();
        var originalPartialHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(partialCopy)));

        var result = SessionRecovery.Recover(crashed, _clock);

        Assert.Equal(CaptureState.Interrupted, result.NewState);
        Assert.Equal(1, result.RecoveredChunks);
        Assert.Equal(0, result.InconsistentChunks);
        var manifest = SessionManifest.Load(crashed);
        var chunk = Assert.Single(manifest.Tracks[0].Chunks);
        Assert.Equal(ChunkState.Recovered, chunk.State);
        Assert.Equal("audio/mic/000001.recovered.wav", chunk.File);
        Assert.InRange(chunk.FrameCount, 3 * 8000, (long)(3.5 * 8000));
        Assert.Contains(manifest.Tracks[0].Events, e => e.Kind == AudioEventKind.Interrupted);
        Assert.Equal(originalPartialHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(partialCopy)))); // original kept
        using (new WaveFileReader(SessionPaths.Resolve(crashed, chunk.File))) { }

        Assert.False(SessionRecovery.NeedsRecovery(manifest));
        Assert.Equal(0, SessionRecovery.Recover(crashed, _clock).RecoveredChunks); // idempotent
    }

    [Fact]
    public async Task CrashAfterRenameBeforeManifestSave_AdoptsTheWav()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic);
        session.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(1));
        await session.StopAsync();

        // Rewind the manifest to "still writing chunk 1" while the .wav already exists.
        var manifest = SessionManifest.Load(session.Directory);
        manifest.CaptureState = CaptureState.Recording;
        var chunk = manifest.Tracks[0].Chunks[0];
        var expectedSha = chunk.Sha256;
        chunk.State = ChunkState.Partial;
        chunk.File = SessionPaths.PartialChunkFile(AudioSource.Mic, 1);
        chunk.Sha256 = null;
        manifest.Save(session.Directory);

        var result = SessionRecovery.Recover(session.Directory, _clock);

        Assert.Equal(1, result.AdoptedChunks);
        var adopted = SessionManifest.Load(session.Directory).Tracks[0].Chunks[0];
        Assert.Equal(ChunkState.Completed, adopted.State);
        Assert.Equal(expectedSha, adopted.Sha256);
        Assert.Equal(8000, adopted.FrameCount);
    }

    [Fact]
    public async Task ThirtyMinutes_MemoryDoesNotGrowWithDuration() // AT-05 (accelerated, short form)
    {
        var format = new AudioFormatInfo { SampleRate = 1000, Channels = 1, BitsPerSample = 8, Encoding = AudioSampleEncoding.Pcm };
        var mic = new FakeCaptureSource(AudioSource.Mic, format);
        await using var session = Create(CaptureMode.Mic, mic);
        session.Start();

        mic.PushFor(_clock, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(100));
        var baseline = SettledManagedMemory();
        mic.PushFor(_clock, TimeSpan.FromMinutes(25), TimeSpan.FromMilliseconds(100));
        await session.StopAsync();
        var after = SettledManagedMemory();

        Assert.True(after - baseline < 4 * 1024 * 1024, $"managed heap grew by {(after - baseline) / 1024} KiB");
        var chunks = SessionManifest.Load(session.Directory).Tracks[0].Chunks;
        Assert.Equal(6, chunks.Count);
        Assert.Equal(30 * 60 * 1000, chunks.Sum(c => c.FrameCount));
    }

    [Fact]
    public async Task Loopback_UsesCaptureTimestamps_WhenDeliveryIsBursty()
    {
        // WASAPI hands over ~100 ms of 10 ms packets at once, with a varying delay. The capture
        // timestamps keep the silence fill exact even though arrival times jitter.
        var loop = new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat);
        await using var session = Create(CaptureMode.Loopback, loop);
        session.Start();
        var origin = _clock.GetTimestamp();
        var packet = new byte[80 * LoopbackFormat.BlockAlign];
        Array.Fill<byte>(packet, 0x11);
        var random = new Random(1234);

        void Play(double fromSeconds, double seconds)
        {
            for (var burst = 0; burst < seconds * 10; burst++)
            {
                var burstStart = TimeSpan.FromSeconds(fromSeconds) + TimeSpan.FromMilliseconds(burst * 100);
                var arrival = burstStart + TimeSpan.FromMilliseconds(100 + random.Next(0, 80));
                _clock.Advance(TimeSpan.FromTicks(origin + arrival.Ticks - _clock.GetTimestamp()));
                for (var i = 0; i < 10; i++)
                    loop.Push(packet, AudioPacketStatus.None, origin + (burstStart + TimeSpan.FromMilliseconds(i * 10)).Ticks);
            }
        }

        Play(2, 1);  // 2s..3s
        Play(6, 1);  // 6s..7s
        _clock.Advance(TimeSpan.FromTicks(origin + TimeSpan.FromSeconds(8).Ticks - _clock.GetTimestamp()));
        await session.StopAsync();

        var track = SessionManifest.Load(session.Directory).Tracks[0];
        var chunk = Assert.Single(track.Chunks);
        Assert.Equal(8 * 8000, chunk.FrameCount);
        var gaps = track.Events.Where(e => e.Kind == AudioEventKind.LoopbackSilence).Select(e => (e.StartMs, e.EndMs!.Value)).ToList();
        Assert.Equal([(0L, 2000L), (3000L, 6000L), (7000L, 8000L)], gaps);
        Assert.Equal(0, chunk.ClockStartMs);
        Assert.Equal(8000, chunk.ClockEndMs);
    }

    [Fact]
    public async Task Mic_DeviceClockDrift_IsRecordedAsTimeMap() // detail spec 6.3 / AT-03
    {
        // The device clock runs 0.1 % slow: each 80-frame (nominal 10 ms) packet takes 10.01 ms.
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic, o => o with { ChunkDuration = TimeSpan.FromMinutes(1) });
        session.Start();
        var packet = new byte[80 * MicFormat.BlockAlign];
        var packetClock = TimeSpan.FromTicks(100_100);
        for (var i = 0; i < 15_000; i++) // 150 s of samples
        {
            var captured = _clock.GetTimestamp();
            _clock.Advance(packetClock);
            mic.Push(packet, AudioPacketStatus.None, captured);
        }
        await session.StopAsync();

        var track = SessionManifest.Load(session.Directory).Tracks[0];
        Assert.Equal(0, track.StartOffsetMs);
        Assert.Equal([0L, 60_000L, 120_000L], track.Chunks.Select(c => c.StartMs));
        Assert.Equal([0L, 60_060L, 120_120L], track.Chunks.Select(c => c.ClockStartMs!.Value));
        // Chunk ends are extrapolated from the last packet at the nominal rate: within 1 ms.
        Assert.Collection(
            track.Chunks.Select(c => c.ClockEndMs!.Value),
            end => Assert.InRange(end, 60_059L, 60_060L),
            end => Assert.InRange(end, 120_119L, 120_120L),
            end => Assert.InRange(end, 150_149L, 150_150L));
    }

    [Fact]
    public async Task SilentLoopback_HasIdentityTimeMap()
    {
        // Nothing plays during the whole recording: no packet, no device timestamp.
        var loop = new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat);
        await using var session = Create(CaptureMode.Loopback, loop);
        session.Start();
        _clock.Advance(TimeSpan.FromSeconds(5));
        await session.StopAsync();

        var chunk = Assert.Single(SessionManifest.Load(session.Directory).Tracks[0].Chunks);
        Assert.Equal(5 * 8000, chunk.FrameCount);
        Assert.Equal(0, chunk.ClockStartMs);
        Assert.Equal(5000, chunk.ClockEndMs);
    }

    [Fact]
    public async Task CaptureTimestamp_FarFromArrival_IsIgnored()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = Create(CaptureMode.Mic, mic);
        session.Start();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var packet = new byte[80 * MicFormat.BlockAlign];
        for (var i = 0; i < 100; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(10));
            mic.Push(packet, AudioPacketStatus.None, captureTimestamp: 42); // some other clock domain
        }
        await session.StopAsync();

        var track = SessionManifest.Load(session.Directory).Tracks[0];
        Assert.Equal(1000, track.StartOffsetMs); // from the arrival time, not from the bogus timestamp
        Assert.Null(track.Chunks[0].ClockStartMs);
        Assert.Null(track.Chunks[0].ClockEndMs);
    }

    [LongFact]
    public async Task EightHoursBoth_AllChunksCompleteWithoutLoss() // AT-05 (accelerated, full length)
    {
        var format = new AudioFormatInfo { SampleRate = 1000, Channels = 1, BitsPerSample = 8, Encoding = AudioSampleEncoding.Pcm };
        var mic = new FakeCaptureSource(AudioSource.Mic, format);
        var loop = new FakeCaptureSource(AudioSource.Loopback, format);
        await using var session = Create(CaptureMode.Both, mic, loop);
        session.Start();

        var baseline = 0L;
        for (var minute = 0; minute < 8 * 60; minute++)
        {
            PushBoth(mic, loop, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(100));
            if (minute == 30) baseline = SettledManagedMemory();
        }
        await session.StopAsync();
        var after = SettledManagedMemory();

        var manifest = SessionManifest.Load(session.Directory);
        Assert.False(session.HasAudioLoss);
        foreach (var track in manifest.Tracks)
        {
            Assert.Equal(96, track.Chunks.Count);
            Assert.All(track.Chunks, c => Assert.Equal(ChunkState.Completed, c.State));
            Assert.InRange(track.Chunks.Sum(c => c.FrameCount), 8 * 3600 * 1000 - 200, 8 * 3600 * 1000 + 200);
        }
        Assert.True(after - baseline < 8 * 1024 * 1024, $"managed heap grew by {(after - baseline) / 1024} KiB");
    }

    private RecordingSession Create(CaptureMode mode, params IAudioCaptureSource[] sources) => Create(mode, null, sources);

    private RecordingSession Create(CaptureMode mode, FakeCaptureSource source, Func<RecordingOptions, RecordingOptions> configure) =>
        Create(mode, configure, [source]);

    private RecordingSession Create(CaptureMode mode, Func<RecordingOptions, RecordingOptions>? configure, IAudioCaptureSource[] sources)
    {
        var options = new RecordingOptions { Mode = mode, Title = "テスト", MinFreeBytes = 10 * 1024 * 1024 };
        if (configure is not null) options = configure(options);
        var session = RecordingSession.Create(_ws.Path, options, sources, _clock, _disk);
        foreach (var fake in sources.OfType<FakeCaptureSource>()) Throttle(session, fake);
        return session;
    }

    // Tests push audio much faster than real time; wait for the writer so the 2 s queue does not overflow.
    private static void Throttle(RecordingSession session, FakeCaptureSource source)
    {
        source.AfterPush = () => session.ThrottleForTesting(source.Source);
    }

    private void PushBoth(FakeCaptureSource a, FakeCaptureSource b, TimeSpan duration, TimeSpan? packet = null)
    {
        var p = packet ?? TimeSpan.FromMilliseconds(10);
        var bufA = new byte[(int)(a.Format.SampleRate * p.TotalSeconds) * a.Format.BlockAlign];
        var bufB = new byte[(int)(b.Format.SampleRate * p.TotalSeconds) * b.Format.BlockAlign];
        Array.Fill<byte>(bufA, 0x11);
        Array.Fill<byte>(bufB, 0x11);
        for (var i = 0; i < (long)Math.Round(duration / p); i++)
        {
            _clock.Advance(p);
            a.Push(bufA);
            b.Push(bufB);
        }
    }

    private static string SidecarOf(RecordingSession session) =>
        ChunkSidecar.PathFor(SessionPaths.Resolve(session.Directory, SessionPaths.PartialChunkFile(AudioSource.Mic, 1)));

    private static string CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".tmp", StringComparison.Ordinal)) continue; // a write in flight; recovery ignores these too
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = File.Create(dest);
                input.CopyTo(output);
            }
            catch (FileNotFoundException)
            {
                // replaced between listing and opening
            }
        }
        return target;
    }

    private static long SettledManagedMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = System.Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (System.Environment.TickCount64 > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(20);
        }
    }
}
