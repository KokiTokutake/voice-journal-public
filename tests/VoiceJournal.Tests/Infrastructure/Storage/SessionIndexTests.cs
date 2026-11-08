using System.Text;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.TestSupport;
using CaptureState = VoiceJournal.Core.Capture.CaptureState;

namespace VoiceJournal.Tests.Infrastructure.Storage;

public sealed class SessionIndexTests : IDisposable
{
    private static readonly AudioFormatInfo MicFormat = AudioFormatInfo.Pcm16Bit(8000, 1);
    private static readonly AudioFormatInfo LoopbackFormat = AudioFormatInfo.IeeeFloat32Bit(8000, 2);

    private readonly TempDirectory _dir = new("vj-index-");
    private readonly ManualTimeProvider _clock = new();
    private readonly Workspace _workspace;
    private readonly VoiceJournalDatabase _db;
    private readonly SessionIndex _index;

    public SessionIndexTests()
    {
        _workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), "Index test", _clock, syncRoots: []);
        _db = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        _index = new SessionIndex(_workspace, _db, _clock);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Upsert_IndexesSessionTracksChunksAndEvents()
    {
        var dir = await RecordBoth(TimeSpan.FromSeconds(150), title: "定例", project: "VJ");

        Assert.True(_index.Upsert(dir));

        var summary = Assert.Single(_index.ListSessions());
        Assert.Equal("定例", summary.Title);
        Assert.Equal("VJ", summary.Project);
        Assert.Equal(CaptureMode.Both, summary.CaptureMode);
        Assert.Equal(CaptureState.Finalized, summary.CaptureState);
        Assert.Equal(150_000, summary.DurationMs);
        Assert.False(summary.HasAudioLoss);
        Assert.Equal(_workspace.RelativePathOf(dir), summary.RelativePath);
        Assert.StartsWith("sessions/", summary.RelativePath, StringComparison.Ordinal);

        var manifest = SessionManifest.Load(dir);
        Assert.Equal(manifest.SessionId, summary.SessionId);
        Assert.Equal(2L, Count("audio_tracks"));
        Assert.Equal(6L, Count("audio_chunks")); // 1-minute chunks: 3 per track
        Assert.Equal(manifest.Tracks.Sum(t => t.Events.Count), Count("audio_events"));
        Assert.Equal(6L, Count("audio_chunks WHERE state = 'completed' AND length(sha256) = 64"));

        var format = _db.Read(c => (string)Scalar(c, "SELECT format_json FROM audio_tracks WHERE source = 'loopback'")!);
        Assert.Contains("\"schema_version\":1", format, StringComparison.Ordinal);
        Assert.Contains("\"encoding\":\"ieee_float\"", format, StringComparison.Ordinal);
        Assert.Equal("loopback_silence", _db.Read(c => (string?)Scalar(c, "SELECT kind FROM audio_events LIMIT 1")));
    }

    [Fact]
    public async Task Upsert_IsIdempotent_AndFollowsManifestChanges()
    {
        var dir = await RecordBoth(TimeSpan.FromSeconds(10));
        Assert.True(_index.Upsert(dir));
        var before = Dump();

        Assert.False(_index.Upsert(dir)); // same manifest bytes: nothing to do
        Assert.Equal(before, Dump());

        var manifest = SessionManifest.Load(dir);
        manifest.Title = "あとから付けた題名";
        manifest.Save(dir);
        Assert.True(_index.Upsert(dir));
        Assert.Equal("あとから付けた題名", Assert.Single(_index.ListSessions()).Title);
        Assert.Equal(2L, Count("audio_tracks"));
    }

    [Fact]
    public async Task Rebuild_FromManifests_ReproducesTheSameRows() // detail spec 15.3: the index can be rebuilt
    {
        var a = await RecordBoth(TimeSpan.FromSeconds(70));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var b = await RecordMicWithUnplug();
        _index.Upsert(a);
        _index.Upsert(b);
        var original = Dump();

        File.Delete(_workspace.DatabasePath);
        File.Delete(_workspace.DatabasePath + "-wal");
        File.Delete(_workspace.DatabasePath + "-shm");
        var freshDb = VoiceJournalDatabase.Open(_workspace.DatabasePath);
        var fresh = new SessionIndex(_workspace, freshDb, _clock);
        var report = fresh.Rebuild();

        Assert.Equal(2, report.Indexed);
        Assert.Empty(report.Failed);
        Assert.Empty(report.MissingOnDisk);
        Assert.Equal(original, Dump(freshDb));
        Assert.Equal(2, fresh.ListSessions().Count);

        var second = fresh.Rebuild();
        Assert.Equal(0, second.Indexed);
        Assert.Equal(2, second.Unchanged);
    }

    [Fact]
    public async Task ListSessions_IsNewestFirst_AndFlagsAudioLoss()
    {
        var older = await RecordBoth(TimeSpan.FromSeconds(5), title: "古い");
        _clock.Advance(TimeSpan.FromHours(1));
        var newer = await RecordMicWithUnplug();
        _index.Rebuild();

        var list = _index.ListSessions();
        Assert.Equal([SessionManifest.Load(newer).SessionId, SessionManifest.Load(older).SessionId], list.Select(s => s.SessionId));
        Assert.True(list[0].HasAudioLoss);  // device disconnected
        Assert.False(list[1].HasAudioLoss);
    }

    [Fact]
    public async Task Rebuild_ReportsBrokenMissingAndUnrecoveredSessions()
    {
        var good = await RecordBoth(TimeSpan.FromSeconds(5));
        var gone = await RecordBoth(TimeSpan.FromSeconds(5));
        _index.Rebuild();
        var goneId = SessionManifest.Load(gone).SessionId;
        Directory.Delete(gone, recursive: true);

        var broken = Path.Combine(_workspace.SessionsRoot, "2026", "11", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(broken);
        File.WriteAllText(SessionManifest.PathIn(broken), "{ broken", Encoding.UTF8);
        var noManifest = Path.Combine(_workspace.SessionsRoot, "2026", "11", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(noManifest);
        var renamed = Path.Combine(_workspace.SessionsRoot, "2026", "11", Guid.NewGuid().ToString("D"));
        CopyDirectory(good, renamed); // folder name no longer matches the session id

        // A recording that never stopped (the app crashed).
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        var crashed = RecordingSession.Create(_workspace.Root, new RecordingOptions { Mode = CaptureMode.Mic }, [mic], _clock, new FakeFreeSpace(long.MaxValue));
        crashed.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(1));

        var report = _index.Rebuild();

        Assert.Equal([goneId], report.MissingOnDisk);
        Assert.DoesNotContain(_index.ListSessions(), x => x.SessionId == goneId); // the index follows the files
        Assert.Equal(3, report.Failed.Count);
        Assert.Contains(report.Failed, f => f.RelativePath == _workspace.RelativePathOf(broken) && f.Reason.StartsWith(nameof(InvalidDataException), StringComparison.Ordinal));
        Assert.Contains(report.Failed, f => f.RelativePath == _workspace.RelativePathOf(noManifest) && f.Reason.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(report.Failed, f => f.RelativePath == _workspace.RelativePathOf(renamed));
        Assert.Equal([_workspace.RelativePathOf(crashed.Directory)], report.NeedsRecovery);
        Assert.Contains(_index.ListSessions(), s => s.CaptureState == CaptureState.Recording);

        await crashed.StopAsync();
        await crashed.DisposeAsync();
    }

    [Fact]
    public void DerivedIds_AreStableAndDistinct()
    {
        var track = Guid.Parse("0193a5b1-0000-7000-8000-000000000001");
        var a = DbText.DerivedId(track, "chunk/1");
        Assert.Equal(a, DbText.DerivedId(track, "chunk/1"));
        Assert.NotEqual(a, DbText.DerivedId(track, "chunk/2"));
        Assert.NotEqual(a, DbText.DerivedId(Guid.Parse("0193a5b1-0000-7000-8000-000000000002"), "chunk/1"));
        Assert.Equal(8, a.Version);
    }

    [Fact]
    public async Task Updater_KeepsTheIndexCurrentWhileRecording()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        var loop = new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat);
        await using var session = RecordingSession.Create(
            _workspace.Root, new RecordingOptions { Mode = CaptureMode.Both, ChunkDuration = TimeSpan.FromSeconds(30) }, [mic, loop], _clock, new FakeFreeSpace(long.MaxValue));
        Throttle(session, mic, loop);
        await using var updater = new SessionIndexUpdater(_index, session, coalesce: TimeSpan.FromMilliseconds(20));
        session.Start();

        PushBoth(mic, loop, TimeSpan.FromSeconds(65));
        await WaitUntil(() => _index.ListSessions() is [{ CaptureState: CaptureState.Recording }] && Count("audio_chunks WHERE state = 'completed'") >= 4);

        await session.StopAsync();
        await updater.FlushAsync();

        var summary = Assert.Single(_index.ListSessions());
        Assert.Equal(CaptureState.Finalized, summary.CaptureState);
        Assert.Equal(65_000, summary.DurationMs);
        Assert.Equal(6L, Count("audio_chunks WHERE state = 'completed'"));
        Assert.Null(updater.LastError);
    }

    [Fact]
    public async Task Updater_RetriesAfterTheDatabaseWasBusy()
    {
        var busyDb = VoiceJournalDatabase.Open(_workspace.DatabasePath, new DatabaseOptions { BusyTimeout = TimeSpan.FromSeconds(1), BusyRetries = 0 });
        var index = new SessionIndex(_workspace, busyDb, _clock);
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        await using var session = RecordingSession.Create(_workspace.Root, new RecordingOptions { Mode = CaptureMode.Mic }, [mic], _clock, new FakeFreeSpace(long.MaxValue));
        Throttle(session, mic);

        using var holder = busyDb.OpenConnection();
        var held = holder.BeginTransaction(deferred: false); // another writer keeps the DB locked
        await using var updater = new SessionIndexUpdater(index, session, coalesce: TimeSpan.FromMilliseconds(10), retryDelay: TimeSpan.FromMilliseconds(100));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        updater.IndexFailed += (_, e) => failed.TrySetResult(e);
        session.Start();
        mic.PushFor(_clock, TimeSpan.FromSeconds(1));

        Assert.IsType<DatabaseBusyException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        held.Commit();
        held.Dispose();
        await session.StopAsync();
        await RetryFlush(updater);

        Assert.Equal(CaptureState.Finalized, Assert.Single(index.ListSessions()).CaptureState);
        Assert.Null(updater.LastError);
    }

    // ---- helpers ------------------------------------------------------------------------

    private async Task<string> RecordBoth(TimeSpan duration, string? title = null, string? project = null)
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        var loop = new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat);
        await using var session = RecordingSession.Create(
            _workspace.Root,
            new RecordingOptions { Mode = CaptureMode.Both, Title = title, Project = project, ChunkDuration = TimeSpan.FromMinutes(1) },
            [mic, loop], _clock, new FakeFreeSpace(long.MaxValue));
        Throttle(session, mic, loop);
        session.Start();
        _clock.Advance(TimeSpan.FromSeconds(1)); // loopback silent at first
        mic.PushFor(_clock, TimeSpan.FromSeconds(1));
        PushBoth(mic, loop, duration - TimeSpan.FromSeconds(2));
        await session.StopAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        return session.Directory;
    }

    private async Task<string> RecordMicWithUnplug()
    {
        var mic = new FakeCaptureSource(AudioSource.Mic, MicFormat);
        var loop = new FakeCaptureSource(AudioSource.Loopback, LoopbackFormat);
        await using var session = RecordingSession.Create(_workspace.Root, new RecordingOptions { Mode = CaptureMode.Both }, [mic, loop], _clock, new FakeFreeSpace(long.MaxValue));
        Throttle(session, mic, loop);
        session.Start();
        PushBoth(mic, loop, TimeSpan.FromSeconds(2));
        mic.Fail(new AudioDeviceException(AudioDeviceErrorKind.DeviceInvalidated, "unplugged"));
        loop.PushFor(_clock, TimeSpan.FromSeconds(2));
        await session.StopAsync();
        return session.Directory;
    }

    private static void Throttle(RecordingSession session, params FakeCaptureSource[] sources)
    {
        foreach (var s in sources) s.AfterPush = () => session.ThrottleForTesting(s.Source);
    }

    private void PushBoth(FakeCaptureSource a, FakeCaptureSource b, TimeSpan duration)
    {
        var p = TimeSpan.FromMilliseconds(10);
        var bufA = new byte[(int)(a.Format.SampleRate * p.TotalSeconds) * a.Format.BlockAlign];
        var bufB = new byte[(int)(b.Format.SampleRate * p.TotalSeconds) * b.Format.BlockAlign];
        for (var i = 0; i < (long)Math.Round(duration / p); i++)
        {
            var captured = _clock.GetTimestamp();
            _clock.Advance(p);
            a.Push(bufA, AudioPacketStatus.None, captured);
            b.Push(bufB, AudioPacketStatus.None, captured);
        }
    }

    private long Count(string tableAndWhere) => _db.Read(c => (long)Scalar(c, "SELECT COUNT(*) FROM " + tableAndWhere)!);

    /// <summary>Every row of every index table, except the time it was indexed.</summary>
    private string Dump(VoiceJournalDatabase? db = null) => (db ?? _db).Read(c =>
    {
        var sb = new StringBuilder();
        foreach (var (table, order) in new[] { ("workspaces", "id"), ("sessions", "id"), ("audio_tracks", "id"), ("audio_chunks", "id"), ("audio_events", "id") })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} ORDER BY {order}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                sb.Append(table).Append(':');
                for (var i = 0; i < r.FieldCount; i++)
                {
                    if (r.GetName(i) == "indexed_at") continue;
                    sb.Append(r.IsDBNull(i) ? "null" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    });

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static async Task RetryFlush(SessionIndexUpdater updater)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await updater.FlushAsync().WaitAsync(TimeSpan.FromSeconds(20));
                return;
            }
            catch (DatabaseBusyException) when (attempt < 5)
            {
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target, StringComparison.Ordinal));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal));
    }
}
