// Playback check: plays WAV files with the app's player (WasapiAudioPlayer) while measuring what the
// default render device actually outputs (loopback). Nothing is written to disk.
//
//   AudioProbe play <wav> [<wav>...]
using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceJournal.Infrastructure.Audio;

namespace VoiceJournal.AudioProbe;

internal static class PlayCommand
{
    public static int Run(string[] args, JsonSerializerOptions json)
    {
        var files = args.Skip(1).Select(Path.GetFullPath).ToList();
        if (files.Count == 0) throw new ArgumentException("no files");
        var expectedSeconds = files.Sum(f => { using var r = new AudioFileReader(f); return r.TotalTime.TotalSeconds; });

        using var enumerator = new MMDeviceEnumerator();
        using var output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var loopback = new WasapiRecorderBuilder().WithDevice(output).WithSharedMode().WithEventSync().WithLoopbackCapture().Build();
        var format = loopback.WaveFormat;
        float peak = 0;
        long audiblePackets = 0, packets = 0;
        loopback.DataAvailable += (buffer, flags, _, _) =>
        {
            if (buffer.IsEmpty) return;
            packets++;
            if (flags.HasFlag(AudioClientBufferFlags.Silent)) return;
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            var p = 0f;
            foreach (var s in samples) p = Math.Max(p, Math.Abs(s));
            if (p > 0.001f) audiblePackets++;
            if (p > peak) peak = p;
        };
        loopback.StartRecording();

        using var player = new WasapiAudioPlayer();
        var ended = new ManualResetEventSlim();
        Exception? error = null;
        player.Ended += (_, e) => { error = e.Error; ended.Set(); };
        var clock = Stopwatch.StartNew();
        var playedOn = player.Play(files);
        var playingAfterStart = player.IsPlaying;
        Thread.Sleep(500);
        var playingAfterHalfSecond = player.IsPlaying;
        var finished = ended.Wait(TimeSpan.FromSeconds(expectedSeconds + 5));
        var elapsed = clock.Elapsed.TotalSeconds;
        loopback.StopRecording();

        var report = new
        {
            output_device = output.FriendlyName,
            player_device = playedOn,
            output_format = $"{format.SampleRate} Hz / {format.Channels} ch",
            files = files.Count,
            expected_seconds = Math.Round(expectedSeconds, 1),
            playing_after_start = playingAfterStart,
            playing_after_500ms = playingAfterHalfSecond,
            ended_event = finished,
            ended_after_seconds = Math.Round(elapsed, 1),
            ended_error = error?.ToString(),
            loopback_packets = packets,
            loopback_audible_packets = audiblePackets,
            loopback_peak_dbfs = peak <= 0 ? -120 : Math.Round(20 * Math.Log10(peak), 1),
        };
        Console.WriteLine(JsonSerializer.Serialize(report, json));
        return audiblePackets > 0 ? 0 : 5;
    }

    /// <summary><c>AudioProbe listen &lt;seconds&gt;</c>: peak level of what the default render device outputs. Nothing is saved.</summary>
    public static int Listen(string[] args, JsonSerializerOptions json)
    {
        var seconds = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        using var enumerator = new MMDeviceEnumerator();
        using var output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var loopback = new WasapiRecorderBuilder().WithDevice(output).WithSharedMode().WithEventSync().WithLoopbackCapture().Build();
        float peak = 0;
        long audiblePackets = 0;
        loopback.DataAvailable += (buffer, flags, _, _) =>
        {
            if (buffer.IsEmpty || flags.HasFlag(AudioClientBufferFlags.Silent)) return;
            var p = 0f;
            foreach (var s in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer)) p = Math.Max(p, Math.Abs(s));
            if (p > 0.001f) audiblePackets++;
            if (p > peak) peak = p;
        };
        loopback.StartRecording();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        loopback.StopRecording();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            output_device = output.FriendlyName,
            output_master_volume_percent = Math.Round(output.AudioEndpointVolume.MasterVolumeLevelScalar * 100),
            output_muted = output.AudioEndpointVolume.Mute,
            audible_packets = audiblePackets,
            peak_dbfs = peak <= 0 ? -120 : Math.Round(20 * Math.Log10(peak), 1),
        }, json));
        return 0;
    }
}
