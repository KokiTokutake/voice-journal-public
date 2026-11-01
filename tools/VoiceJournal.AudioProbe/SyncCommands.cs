// AT-03 tools (detail spec 22: "両音源へ校正信号、60分録音 … ずれ100ms以内目標").
//
//   AudioProbe beeps <seconds> [--device <id|name part>] [--interval <s>] [--tone <hz>] [--length <s>] [--level <dBFS>]
//       Plays short tone bursts on a render device: the loopback track records them digitally and the
//       microphone hears them from the speaker, so both tracks contain the same events.
//   AudioProbe analyze-sync <sessionDir> [--tone <hz>]
//       Finds the bursts in both tracks and reports the mic − loopback offset over time, on the sample
//       timeline (uncorrected) and through the per-chunk time map (clock_start_ms / clock_end_ms).
using System.Globalization;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.AudioProbe;

internal static class SyncCommands
{
    private const double WindowSeconds = 0.010;
    private const double HopSeconds = 0.0025;

    public static int Beeps(string[] args, JsonSerializerOptions json)
    {
        var seconds = double.Parse(args[1], CultureInfo.InvariantCulture);
        var interval = Number(args, "--interval", 20);
        var tone = Number(args, "--tone", 1000);
        var length = Number(args, "--length", 0.15);
        var level = Math.Pow(10, Number(args, "--level", -12) / 20);
        var wanted = Option(args, "--device");

        using var enumerator = new MMDeviceEnumerator();
        using var device = wanted is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.ID == wanted || d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"no render device matches '{wanted}'");
        int rate;
        using (var client = device.CreateAudioClient()) rate = client.MixFormat.SampleRate;

        var provider = new BurstProvider(rate, 2, seconds, interval, tone, length, level);
        using var player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().Build();
        using var done = new ManualResetEventSlim();
        player.PlaybackStopped += (_, _) => done.Set();
        player.Init(new SampleToWaveProvider(provider));
        player.Play();
        Console.WriteLine($"beeping on {device.FriendlyName}: {tone} Hz, {length * 1000:0} ms every {interval} s for {seconds} s");
        done.Wait(TimeSpan.FromSeconds(seconds + 10));
        Console.WriteLine(JsonSerializer.Serialize(new { device = device.FriendlyName, bursts = provider.BurstsPlayed }, json));
        return 0;
    }

    /// <summary><c>AudioProbe devices</c>: active endpoints as JSON (for scripts).</summary>
    public static int Devices(JsonSerializerOptions json)
    {
        using var catalog = new VoiceJournal.Infrastructure.Audio.WasapiDeviceCatalog();
        object List(AudioSource s) => catalog.GetDevices(s).Select(d => new { id = d.Id, name = d.Name, is_default = d.IsDefault });
        Console.WriteLine(JsonSerializer.Serialize(new { mic = List(AudioSource.Mic), loopback = List(AudioSource.Loopback) }, json));
        return 0;
    }

    public static int Analyze(string[] args, JsonSerializerOptions json)
    {
        var directory = Path.GetFullPath(args[1]);
        var tone = Number(args, "--tone", 1000);
        var manifest = SessionManifest.Load(directory);
        var mic = manifest.Tracks.SingleOrDefault(t => t.Source == AudioSource.Mic);
        var loop = manifest.Tracks.Single(t => t.Source == AudioSource.Loopback);

        var loopFrames = ToneFrames(directory, loop, tone);
        var micFrames = mic is null ? [] : ToneFrames(directory, mic, tone);

        // Loopback is digital: burst onsets are where the tone rises from (near) nothing.
        var loopOnsets = Onsets(loopFrames, absoluteThresholdDb: -50, relativeDb: 0);
        if (mic is null)
        {
            // Loopback-only run: checks the burst detection itself.
            Console.WriteLine(JsonSerializer.Serialize(new { loopback_bursts = loopOnsets.Count, onsets_s = loopOnsets.Select(o => Math.Round(o.RawMs / 1000, 3)) }, json));
            return 0;
        }
        var pairs = new List<(double At, double Raw, double Corrected)>();
        foreach (var onset in loopOnsets)
        {
            // Search the mic around the same moment; the room adds a few ms of acoustic delay.
            var from = onset.RawMs - 300;
            var to = onset.RawMs + 700;
            var micOnset = FirstRise(micFrames, from, to);
            if (micOnset is null) continue;
            pairs.Add((onset.RawMs / 1000.0, micOnset.Value.RawMs - onset.RawMs, micOnset.Value.ClockMs - onset.ClockMs));
        }

        object Stats(Func<(double At, double Raw, double Corrected), double> pick)
        {
            if (pairs.Count == 0) return new { };
            var values = pairs.Select(pick).ToList();
            var first = values.Take(5).Average();
            var last = values.TakeLast(5).Average();
            var slope = Slope([.. pairs.Select(p => p.At / 3600)], values); // ms per hour
            return new
            {
                median_ms = Math.Round(Median(values), 1),
                min_ms = Math.Round(values.Min(), 1),
                max_ms = Math.Round(values.Max(), 1),
                spread_ms = Math.Round(values.Max() - values.Min(), 1),
                first5_avg_ms = Math.Round(first, 1),
                last5_avg_ms = Math.Round(last, 1),
                drift_ms_per_hour = Math.Round(slope, 1),
            };
        }

        var corrected = pairs.Count == 0 ? double.NaN : pairs.Max(p => p.Corrected) - pairs.Min(p => p.Corrected);
        var report = new
        {
            session_directory = directory,
            duration_minutes = Math.Round(loopFrames.Count == 0 ? 0 : loopFrames[^1].RawMs / 60000.0, 1),
            loopback_bursts = loopOnsets.Count,
            matched_in_mic = pairs.Count,
            mic_minus_loopback_uncorrected = Stats(p => p.Raw),
            mic_minus_loopback_time_map = Stats(p => p.Corrected),
            target_spread_ms = 100,
            pass = pairs.Count >= Math.Max(3, loopOnsets.Count * 0.8) && corrected <= 100,
            offsets = pairs.Select(p => new { at_s = Math.Round(p.At, 1), raw_ms = Math.Round(p.Raw, 1), time_map_ms = Math.Round(p.Corrected, 1) }),
        };
        Console.WriteLine(JsonSerializer.Serialize(report, json));
        return report.pass ? 0 : 6;
    }

    /// <summary>Tone magnitude (dB) per hop along a track, with both timeline and time-map times.</summary>
    private static List<(double RawMs, double ClockMs, double Db)> ToneFrames(string directory, TrackManifest track, double tone)
    {
        var frames = new List<(double, double, double)>();
        foreach (var chunk in track.Chunks.Where(c => c.State is ChunkState.Completed or ChunkState.Recovered).OrderBy(c => c.Sequence))
        {
            using var reader = new AudioFileReader(SessionPaths.Resolve(directory, chunk.File));
            var rate = reader.WaveFormat.SampleRate;
            var channels = reader.WaveFormat.Channels;
            var mono = new float[chunk.FrameCount];
            var buffer = new float[8192 * channels];
            long position = 0;
            int read;
            while ((read = reader.Read(buffer.AsSpan())) > 0 && position < mono.Length)
            {
                for (var i = 0; i + channels <= read && position < mono.Length; i += channels)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++) sum += buffer[i + c];
                    mono[position++] = sum / channels;
                }
            }

            var window = (int)(rate * WindowSeconds);
            var hop = (int)(rate * HopSeconds);
            var coefficient = 2 * Math.Cos(2 * Math.PI * tone / rate);
            var clockSpan = chunk.ClockEndMs - chunk.ClockStartMs;
            for (var start = 0; start + window <= position; start += hop)
            {
                double s1 = 0, s2 = 0;
                for (var i = 0; i < window; i++)
                {
                    var s0 = mono[start + i] + coefficient * s1 - s2;
                    s2 = s1;
                    s1 = s0;
                }
                var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
                var amplitude = 2 * Math.Sqrt(Math.Max(power, 0)) / window;
                var db = amplitude <= 1e-9 ? -180 : 20 * Math.Log10(amplitude);
                var rawMs = chunk.StartMs + start * 1000.0 / rate;
                var clockMs = chunk.ClockStartMs is { } cs && clockSpan is { } span && chunk.FrameCount > 0
                    ? cs + (double)start / chunk.FrameCount * span
                    : rawMs;
                frames.Add((rawMs, clockMs, db));
            }
        }
        return frames;
    }

    private static List<(double RawMs, double ClockMs)> Onsets(List<(double RawMs, double ClockMs, double Db)> frames, double absoluteThresholdDb, double relativeDb)
    {
        var onsets = new List<(double, double)>();
        var quietSince = double.NegativeInfinity;
        var inBurst = false;
        foreach (var (raw, clock, db) in frames)
        {
            if (db >= absoluteThresholdDb + relativeDb)
            {
                if (!inBurst && raw - quietSince >= 1000) onsets.Add((raw, clock));
                inBurst = true;
            }
            else
            {
                if (inBurst) quietSince = raw;
                if (double.IsNegativeInfinity(quietSince)) quietSince = raw - 1000;
                inBurst = false;
            }
        }
        return onsets;
    }

    /// <summary>First hop in [from, to] where the mic tone level rises 15 dB above its median of the preceding second.</summary>
    private static (double RawMs, double ClockMs)? FirstRise(List<(double RawMs, double ClockMs, double Db)> frames, double from, double to)
    {
        var baseline = frames.Where(f => f.RawMs >= from - 1000 && f.RawMs < from).Select(f => f.Db).ToList();
        if (baseline.Count == 0) return null;
        var threshold = Median(baseline) + 15;
        foreach (var f in frames)
        {
            if (f.RawMs < from) continue;
            if (f.RawMs > to) break;
            if (f.Db >= threshold) return (f.RawMs, f.ClockMs);
        }
        return null;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static double Slope(List<double> x, List<double> y)
    {
        if (x.Count < 2) return 0;
        var mx = x.Average();
        var my = y.Average();
        var num = x.Zip(y).Sum(p => (p.First - mx) * (p.Second - my));
        var den = x.Sum(v => (v - mx) * (v - mx));
        return den == 0 ? 0 : num / den;
    }

    private static string? Option(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    private static double Number(string[] args, string name, double fallback) =>
        Option(args, name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    /// <summary>Silence with a short faded sine burst every <c>interval</c> seconds, ending after <c>total</c> seconds.</summary>
    private sealed class BurstProvider(int rate, int channels, double total, double interval, double tone, double length, double level) : ISampleProvider
    {
        private long _frame;
        private readonly long _end = (long)(total * rate);
        private readonly long _period = (long)(interval * rate);
        private readonly long _burst = (long)(length * rate);
        private readonly long _fade = (long)(0.005 * rate);

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

        public int BurstsPlayed { get; private set; }

        public int Read(Span<float> buffer)
        {
            var frames = Math.Min(buffer.Length / channels, _end - _frame);
            for (var i = 0; i < frames; i++, _frame++)
            {
                // First burst after 2 s, so the recording has a quiet lead-in to measure the noise floor.
                var t = _frame - 2 * rate;
                float value = 0;
                if (t >= 0)
                {
                    var inPeriod = t % _period;
                    if (inPeriod == 0) BurstsPlayed++;
                    if (inPeriod < _burst)
                    {
                        var envelope = Math.Min(1.0, Math.Min(inPeriod, _burst - inPeriod) / (double)_fade);
                        value = (float)(level * envelope * Math.Sin(2 * Math.PI * tone * inPeriod / rate));
                    }
                }
                for (var c = 0; c < channels; c++) buffer[i * channels + c] = value;
            }
            return (int)frames * channels;
        }
    }
}
