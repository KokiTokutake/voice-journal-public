// Phase B3 device check: records with the real pipeline (RecordingSession + WasapiCaptureSource).
//
//   AudioProbe session <mic|loopback|both> <seconds> [--workspace <dir>] [--normal] [--chunk-seconds <n>] [--reconnect]
//
// --reconnect   when a device is lost, wait for the same endpoint to come back and resume it as new
//               chunks (what the "reconnect" button will do in the app).
// Prints progress lines while recording, then one JSON summary (manifest + verification of every chunk).
// Exit code: 0 = all completed chunks verified, 4 = verification failed.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Infrastructure.Audio;
using CaptureState = VoiceJournal.Core.Capture.CaptureState;

namespace VoiceJournal.AudioProbe;

internal static class SessionCommand
{
    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions json)
    {
        var mode = args[1] switch
        {
            "mic" => CaptureMode.Mic,
            "loopback" => CaptureMode.Loopback,
            "both" => CaptureMode.Both,
            _ => throw new ArgumentException("mode must be mic, loopback or both"),
        };
        var seconds = double.Parse(args[2], CultureInfo.InvariantCulture);
        var workspace = Option(args, "--workspace")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceJournal", "verify", "b3-workspace");
        var micMode = args.Contains("--normal") ? MicProcessingMode.Normal : MicProcessingMode.Raw;
        var chunkSeconds = Option(args, "--chunk-seconds");
        var autoReconnect = args.Contains("--reconnect");

        using var catalog = new WasapiDeviceCatalog();
        var sources = new List<IAudioCaptureSource>();
        var needed = mode switch
        {
            CaptureMode.Mic => new[] { AudioSource.Mic },
            CaptureMode.Loopback => [AudioSource.Loopback],
            _ => [AudioSource.Mic, AudioSource.Loopback],
        };
        try
        {
            foreach (var source in needed)
            {
                var device = catalog.GetDefault(source) ?? throw new InvalidOperationException($"no default {source} device");
                var created = catalog.CreateSource(source, device.Id, micMode);
                sources.Add(created);
                Console.WriteLine($"{source}: {created.DeviceName} | {Describe(created.Format)}");
            }

            var options = new RecordingOptions { Mode = mode, MicMode = micMode, Title = "B3 device check" };
            if (chunkSeconds is not null) options = options with { ChunkDuration = TimeSpan.FromSeconds(double.Parse(chunkSeconds, CultureInfo.InvariantCulture)) };

            var clock = Stopwatch.StartNew();
            string Stamp() => clock.Elapsed.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
            var lost = new System.Collections.Concurrent.ConcurrentQueue<AudioSource>();
            await using var session = RecordingSession.Create(workspace, options, sources);
            session.StateChanged += (_, state) => Console.WriteLine($"[{Stamp()}] state: {state}");
            session.Warning += (_, w) =>
            {
                Console.WriteLine($"[{Stamp()}] warning: {w.Kind} {w.Source} {(w.Error is AudioDeviceException d ? d.Kind.ToString() : w.Error?.GetType().Name)}");
                if (w.Kind == RecordingWarningKind.DeviceLost) lost.Enqueue(w.Source);
            };

            clock.Restart(); // wall time = Start() .. StopAsync() request, as AT-01 compares
            session.Start();
            var flowingReported = false;
            var waitingForReconnect = new HashSet<AudioSource>();
            var nextLevel = TimeSpan.FromSeconds(1);
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds) && session.State is not (CaptureState.Finalized or CaptureState.Failed))
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (!flowingReported && session.IsAudioFlowing)
                {
                    flowingReported = true;
                    Console.WriteLine($"[{Stamp()}] audio flowing");
                }
                if (clock.Elapsed >= nextLevel)
                {
                    nextLevel += TimeSpan.FromSeconds(1);
                    var levels = needed.Select(s => $"{s} {Dbfs(session.ReadPeak(s)),6:0.0} dBFS");
                    Console.WriteLine($"[{Stamp()}] {string.Join("  ", levels)}");
                }
                while (lost.TryDequeue(out var source)) waitingForReconnect.Add(source);
                if (autoReconnect) TryReconnect(session, catalog, sources, waitingForReconnect, micMode, Stamp);
            }

            Console.WriteLine($"[{Stamp()}] stopping");
            var wallMs = (long)clock.Elapsed.TotalMilliseconds;
            await session.StopAsync().ConfigureAwait(false);
            var (summary, ok) = Summarize(session.Directory, wallMs);
            Console.WriteLine(JsonSerializer.Serialize(summary, json));
            return ok ? 0 : 4;
        }
        finally
        {
            foreach (var s in sources) s.Dispose();
        }
    }

    private static void TryReconnect(RecordingSession session, WasapiDeviceCatalog catalog, List<IAudioCaptureSource> sources, HashSet<AudioSource> waiting, MicProcessingMode micMode, Func<string> stamp)
    {
        foreach (var source in waiting.ToList())
        {
            var deviceId = session.GetTracks().Single(t => t.Source == source).DeviceId;
            if (!catalog.GetDevices(source).Any(d => d.Id == deviceId)) continue; // not back yet
            IAudioCaptureSource? created = null;
            try
            {
                created = catalog.CreateSource(source, deviceId, micMode);
                session.Reconnect(created);
                sources.Add(created);
                waiting.Remove(source);
                Console.WriteLine($"[{stamp()}] reconnected {source}");
            }
            catch (Exception ex) when (ex is AudioDeviceException or InvalidOperationException)
            {
                created?.Dispose();
                Console.WriteLine($"[{stamp()}] reconnect {source} failed: {ex.Message}");
            }
        }
    }

    private static (object Summary, bool Ok) Summarize(string directory, long wallMs)
    {
        var manifest = SessionManifest.Load(directory);
        var ok = true;
        var tracks = new List<object>();
        foreach (var track in manifest.Tracks)
        {
            var rate = track.Format.SampleRate;
            var chunks = new List<object>();
            foreach (var chunk in track.Chunks)
            {
                string verdict;
                if (chunk.State != ChunkState.Completed)
                {
                    verdict = $"not completed ({chunk.State})";
                    ok = false;
                }
                else
                {
                    var path = SessionPaths.Resolve(directory, chunk.File);
                    using var reader = new WaveFileReader(path);
                    var frames = reader.Length / reader.WaveFormat.BlockAlign;
                    reader.Dispose();
                    var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                    verdict = frames != chunk.FrameCount ? $"frame count mismatch ({frames})"
                        : sha != chunk.Sha256 ? "sha256 mismatch"
                        : "ok";
                    if (verdict != "ok") ok = false;
                }
                chunks.Add(new
                {
                    chunk.Sequence,
                    chunk.File,
                    chunk.StartMs,
                    seconds = Math.Round((double)chunk.FrameCount / rate, 3),
                    chunk.ClockStartMs,
                    chunk.ClockEndMs,
                    verdict,
                });
            }

            var totalFrames = track.Chunks.Sum(c => c.FrameCount);
            var first = track.Chunks.FirstOrDefault();
            var last = track.Chunks.LastOrDefault();
            double? clockPpm = null;
            if (first?.ClockStartMs is { } cs && last?.ClockEndMs is { } ce && last.StartMs + last.FrameCount * 1000 / rate - first.StartMs is var timelineMs and > 0)
                clockPpm = Math.Round(((double)(ce - cs) / timelineMs - 1) * 1e6, 1);
            tracks.Add(new
            {
                track.Source,
                device = track.DeviceName,
                format = Describe(track.Format),
                track.State,
                track.StartOffsetMs,
                recorded_seconds = Math.Round((double)totalFrames / rate, 3),
                timeline_end_ms = last is null ? 0 : last.StartMs + last.FrameCount * 1000 / rate,
                timeline_end_minus_wall_ms = last is null ? 0 : last.StartMs + last.FrameCount * 1000 / rate - wallMs,
                clock_vs_samples_ppm = clockPpm,
                chunks,
                events = track.Events.Select(e => new { e.Kind, e.StartMs, e.EndMs, e.Detail }),
            });
        }
        return (new
        {
            session_directory = directory,
            manifest.CaptureMode,
            manifest.MicMode,
            manifest.CaptureState,
            wall_ms = wallMs,
            tracks,
            verified = ok,
        }, ok);
    }

    private static string? Option(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    private static double Dbfs(float peak) => peak <= 0 ? -120 : 20 * Math.Log10(peak);

    private static string Describe(AudioFormatInfo f) =>
        $"{f.SampleRate} Hz / {f.BitsPerSample} bit / {f.Channels} ch / {f.Encoding}{(f.Extensible ? " (extensible)" : string.Empty)}";
}
