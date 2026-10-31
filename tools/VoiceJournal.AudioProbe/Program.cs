// Phase A audio-source probe (detail spec 23: 音源単体試験).
//
//   AudioProbe list
//   AudioProbe record <mic|loopback> <seconds> <outBase> [--device <endpointId>] [--raw]
//
// record writes:
//   <outBase>.orig.wav   native WASAPI shared-mode format, untouched (原音)
//   <outBase>.wav        16 kHz / 16 bit / mono for whisper.cpp
//   <outBase>.probe.json device, formats, levels, loopback silence padding
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceJournal.AudioProbe;

internal static class Program
{
    private const int TargetSampleRate = 16000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "list" => List(),
                "record" when args.Length >= 4 => Record(args),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: AudioProbe list | record <mic|loopback> <seconds> <outBase> [--device <id>] [--raw]");
        return 2;
    }

    private static int List()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
        {
            enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out var def);
            Console.WriteLine(flow == DataFlow.Capture ? "[capture / mic]" : "[render / loopback]");
            foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                var mark = d.ID == def?.ID ? "*" : " ";
                using var client = d.CreateAudioClient();
                Console.WriteLine($" {mark} {d.FriendlyName} | {FormatName(client.MixFormat)}");
                Console.WriteLine($"     {d.ID}");
            }
        }
        return 0;
    }

    private static int Record(string[] args)
    {
        var source = args[1];
        if (source is not ("mic" or "loopback")) return Usage();
        var seconds = double.Parse(args[2], CultureInfo.InvariantCulture);
        var outBase = Path.GetFullPath(args[3]);
        var deviceId = args.SkipWhile(a => a != "--device").Skip(1).FirstOrDefault();
        var raw = args.Contains("--raw");
        Directory.CreateDirectory(Path.GetDirectoryName(outBase)!);

        using var enumerator = new MMDeviceEnumerator();
        var flow = source == "mic" ? DataFlow.Capture : DataFlow.Render;
        var device = deviceId is null ? enumerator.GetDefaultAudioEndpoint(flow, Role.Console) : enumerator.GetDevice(deviceId);

        // NAudio 3: WasapiCapture / WasapiLoopbackCapture are obsolete; WasapiRecorder is the replacement.
        var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithEventSync();
        if (source == "loopback") builder = builder.WithLoopbackCapture();
        // --raw: bypass the system capture effects (noise suppression / AGC APOs) to get the unaltered mic signal.
        if (raw) builder = builder.WithRawMode();
        using var capture = builder.Build();
        var format = capture.WaveFormat;
        var origPath = outBase + ".orig.wav";
        var stats = new CaptureStats();

        using (var writer = new WaveFileWriter(origPath, format))
        {
            var stopped = new ManualResetEventSlim();
            Exception? captureError = null;
            var clock = new Stopwatch();
            long? lastEndPosition = null;

            capture.DataAvailable += (buffer, flags, devicePosition, _) =>
            {
                stats.Packets++;
                var frames = buffer.Length / format.BlockAlign;
                if (flags.HasFlag(AudioClientBufferFlags.Silent)) stats.SilentPackets++;
                if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)) stats.Discontinuities++;
                if (lastEndPosition is { } last && devicePosition != last) stats.PositionJumps++; // jumps when the render engine goes idle and resumes
                lastEndPosition = devicePosition + frames;

                // WASAPI loopback delivers no packets while nothing is playing. Fill the gap with
                // silence so the file stays aligned with wall-clock time (needed for Phase B/C sync).
                if (source == "loopback")
                {
                    var expected = AlignDown((long)(clock.Elapsed.TotalSeconds * format.AverageBytesPerSecond), format.BlockAlign) - buffer.Length;
                    var gap = expected - writer.Length;
                    if (gap > format.AverageBytesPerSecond / 10)
                    {
                        WriteSilence(writer, gap);
                        stats.PaddedBytes += gap;
                        stats.GapCount++;
                    }
                }

                // A SILENT packet's contents must be treated as zeros regardless of what the buffer holds.
                if (flags.HasFlag(AudioClientBufferFlags.Silent)) WriteSilence(writer, buffer.Length);
                else writer.Write(buffer);
            };
            capture.RecordingStopped += (_, e) => { captureError = e.Exception; stopped.Set(); };

            Console.WriteLine($"recording {source} for {seconds:0.#}s: {device.FriendlyName} ({FormatName(format)})");
            clock.Start();
            capture.StartRecording();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            capture.StopRecording();
            if (!stopped.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("RecordingStopped was not raised");
            clock.Stop();
            if (captureError is not null) throw new InvalidOperationException("capture failed", captureError);

            if (source == "loopback")
            {
                var tail = AlignDown((long)(clock.Elapsed.TotalSeconds * format.AverageBytesPerSecond), format.BlockAlign) - writer.Length;
                if (tail > 0) { WriteSilence(writer, tail); stats.PaddedBytes += tail; }
            }
            stats.WallSeconds = clock.Elapsed.TotalSeconds;
        }

        var wavPath = outBase + ".wav";
        var level = ConvertForWhisper(origPath, wavPath);

        var report = new
        {
            source,
            raw_mode = raw,
            device = device.FriendlyName,
            device_id = device.ID,
            native_format = FormatName(format),
            requested_seconds = seconds,
            wall_seconds = Math.Round(stats.WallSeconds, 2),
            recorded_seconds = Math.Round(level.Seconds, 2),
            packets = stats.Packets,
            silent_packets = stats.SilentPackets,
            discontinuities = stats.Discontinuities,
            device_position_jumps = stats.PositionJumps,
            loopback_padded_seconds = Math.Round((double)stats.PaddedBytes / format.AverageBytesPerSecond, 2),
            loopback_gap_count = stats.GapCount,
            peak_dbfs = Math.Round(level.PeakDbfs, 1),
            rms_dbfs = Math.Round(level.RmsDbfs, 1),
            clipped_samples = level.Clipped,
            silent = level.PeakDbfs < -60,
            orig_wav = origPath,
            whisper_wav = wavPath,
            recorded_at = DateTimeOffset.Now,
        };
        var json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(outBase + ".probe.json", json);
        Console.WriteLine(json);
        return level.PeakDbfs < -60 ? 3 : 0;
    }

    /// <summary>Downmix to mono, resample to 16 kHz, write 16-bit PCM; measure levels on the native-rate mono signal.</summary>
    private static Level ConvertForWhisper(string origPath, string wavPath)
    {
        double sumSq = 0;
        float peak = 0;
        long count = 0, clipped = 0;

        using (var reader = new AudioFileReader(origPath))
        {
            var mono = new DownmixSampleProvider(reader);
            var metered = new MeteringProvider(mono, s =>
            {
                var a = Math.Abs(s);
                if (a > peak) peak = a;
                if (a >= 0.999f) clipped++;
                sumSq += s * s;
                count++;
            });
            var resampled = new WdlResamplingSampleProvider(metered, TargetSampleRate);
            WaveFileWriter.CreateWaveFile(wavPath, new SampleToWaveProvider16(resampled));
        }

        static double Db(double v) => v <= 0 ? -120 : 20 * Math.Log10(v);
        var rate = new WaveFileReader(origPath).Using(r => r.WaveFormat.SampleRate);
        return new Level(Db(peak), count == 0 ? -120 : Db(Math.Sqrt(sumSq / count)), clipped, (double)count / rate);
    }

    private static void WriteSilence(WaveFileWriter writer, long bytes)
    {
        var zeros = new byte[Math.Min(bytes, 1 << 16)];
        for (var left = bytes; left > 0; left -= zeros.Length)
            writer.Write(zeros, 0, (int)Math.Min(left, zeros.Length));
    }

    private static long AlignDown(long value, int block) => value - (value % block);

    private static string FormatName(WaveFormat f) =>
        $"{f.SampleRate} Hz / {f.BitsPerSample} bit / {f.Channels} ch / {(f is WaveFormatExtensible x ? x.SubFormat.ToString() : f.Encoding.ToString())}";

    private static T Using<TD, T>(this TD disposable, Func<TD, T> f) where TD : IDisposable
    {
        using (disposable) return f(disposable);
    }

    private sealed class CaptureStats
    {
        public long Packets { get; set; }
        public long SilentPackets { get; set; }
        public long Discontinuities { get; set; }
        public long PositionJumps { get; set; }
        public long PaddedBytes { get; set; }
        public int GapCount { get; set; }
        public double WallSeconds { get; set; }
    }

    private readonly record struct Level(double PeakDbfs, double RmsDbfs, long Clipped, double Seconds);
}

/// <summary>Averages all channels into one (handles mic arrays with more than two channels).</summary>
internal sealed class DownmixSampleProvider(ISampleProvider source) : ISampleProvider
{
    private float[] _buffer = [];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(Span<float> buffer)
    {
        var channels = source.WaveFormat.Channels;
        if (channels == 1) return source.Read(buffer);

        var needed = buffer.Length * channels;
        if (_buffer.Length < needed) _buffer = new float[needed];
        var read = source.Read(_buffer.AsSpan(0, needed));
        var frames = read / channels;
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += _buffer[i * channels + c];
            buffer[i] = sum / channels;
        }
        return frames;
    }
}

internal sealed class MeteringProvider(ISampleProvider source, Action<float> onSample) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        var read = source.Read(buffer);
        for (var i = 0; i < read; i++) onSample(buffer[i]);
        return read;
    }
}
