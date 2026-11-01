using System.Diagnostics;
using System.Text;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Stt;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Stt;

/// <summary>
/// Process handling of <see cref="WhisperCliEngine"/> with PowerShell standing in for whisper-cli: it is registered
/// with its real hash, and the test replaces the arguments with a script that behaves like the recognizer.
/// </summary>
public sealed class WhisperCliEngineTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(System.Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    private readonly TempDirectory _dir = new();
    private readonly SttConfiguration _config = new() { ModelName = "test.bin", UseVad = false };

    public void Dispose() => _dir.Dispose();

    private SttToolSettings Tools(string? exeHash = null)
    {
        var model = _dir.File("test.bin");
        File.WriteAllText(model, "model");
        var vad = _dir.File("vad.bin");
        File.WriteAllText(vad, "vad");
        return new SttToolSettings
        {
            WhisperCli = new RegisteredFile { Path = PowerShell, Sha256 = exeHash ?? SttInputBuilder.HashFile(PowerShell) },
            Models = new Dictionary<string, RegisteredModel>
            {
                ["test.bin"] = new() { Path = model, Sha256 = SttInputBuilder.HashFile(model), RequiredMemoryMb = 1 },
            },
            VadModel = new RegisteredFile { Path = vad, Sha256 = SttInputBuilder.HashFile(vad) },
        };
    }

    /// <summary>A stand-in recognizer: reports progress, waits, then writes the JSON whisper would write.</summary>
    private WhisperCliEngine Engine(string body, SttToolSettings? tools = null, Func<long?>? memory = null) =>
        new(tools ?? Tools(), memory ?? (() => 64_000))
        {
            KnownVersion = "test",
            ArgumentsOverride = r => ["-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Error.WriteLine('progress = 50%'); " + body.Replace("{json}", Path.Combine(r.OutputDirectory, r.OutputName + ".json"), StringComparison.Ordinal)],
        };

    private const string WriteJson =
        "[IO.File]::WriteAllText('{json}', '{\"transcription\":[{\"offsets\":{\"from\":100,\"to\":900},\"text\":\" テストです\"},{\"offsets\":{\"from\":1000,\"to\":1000},\"text\":\" \"}]}')";

    private SttRunRequest Request(TimeSpan? timeout = null, SttPauseGate? pause = null) => new()
    {
        AudioPath = _dir.File("part-0001.wav"),
        OutputDirectory = Path.Combine(_dir.Path, "native"),
        OutputName = "mic-0001",
        Configuration = _config,
        DurationMs = 5_000,
        Timeout = timeout ?? TimeSpan.FromMinutes(1),
        Pause = pause,
    };

    [Fact]
    public async Task Run_ReadsTheJson_ReportsProgress_AndKeepsTheLog()
    {
        var reported = new List<double>();
        var progress = new SynchronousProgress(reported.Add);

        var result = await Engine("Start-Sleep -Milliseconds 200; " + WriteJson).RunAsync(Request(), progress, CancellationToken.None);

        Assert.Equal([new SttRawSegment(100, 900, "テストです", 0)], result.Segments);
        Assert.Equal("mic-0001.json", result.NativeFile);
        Assert.Contains(0.5, reported);
        Assert.Equal(1, reported[^1]);
        Assert.Contains("progress = 50%", await File.ReadAllTextAsync(Path.Combine(_dir.Path, "native", "mic-0001.log")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "native", "mic-0001.args.json")));
    }

    [Fact]
    public async Task Pause_SuspendsTheProcess_AndThePausedTimeDoesNotCount()
    {
        var gate = new SttPauseGate();
        gate.Pause();
        var wall = Stopwatch.StartNew();
        var run = Engine("Start-Sleep -Milliseconds 1500; " + WriteJson).RunAsync(Request(TimeSpan.FromSeconds(20), gate), null, CancellationToken.None);

        await Task.Delay(4_000);
        Assert.False(run.IsCompleted); // suspended: the 1.5 s script cannot have finished
        gate.Resume();
        var result = await run;

        Assert.Single(result.Segments);
        Assert.True(wall.Elapsed - result.Elapsed > TimeSpan.FromSeconds(3), $"wall {wall.Elapsed}, active {result.Elapsed}");
    }

    [Fact]
    public async Task Timeout_KillsTheProcess_AndReportsRunFailed()
    {
        var wall = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<SttException>(() => Engine("Start-Sleep -Seconds 60").RunAsync(Request(TimeSpan.FromSeconds(2)), null, CancellationToken.None));

        Assert.Equal(SttErrorCode.RunFailed, ex.Code);
        Assert.True(wall.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Cancel_KillsTheProcess_EvenWhilePaused()
    {
        var gate = new SttPauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var run = Engine("Start-Sleep -Seconds 60").RunAsync(Request(pause: gate), null, cts.Token);
        await Task.Delay(1_000);
        gate.Pause();

        var ex = await Assert.ThrowsAsync<SttException>(() => run);
        Assert.Equal(SttErrorCode.Cancelled, ex.Code);
    }

    [Fact]
    public async Task NonZeroExit_AndMissingJson_AreRunFailures()
    {
        var exit = await Assert.ThrowsAsync<SttException>(() => Engine("exit 3").RunAsync(Request(), null, CancellationToken.None));
        Assert.Equal(SttErrorCode.RunFailed, exit.Code);
        Assert.Contains("code 3", exit.Message, StringComparison.Ordinal);

        var noJson = await Assert.ThrowsAsync<SttException>(() => Engine("exit 0").RunAsync(Request(), null, CancellationToken.None));
        Assert.Equal(SttErrorCode.RunFailed, noJson.Code);
    }

    [Fact]
    public async Task UnregisteredModel_HashMismatch_AndLowMemory_StopBeforeStarting()
    {
        var unknown = await Assert.ThrowsAsync<SttException>(() => Engine(WriteJson).RunAsync(Request() with { Configuration = _config with { ModelName = "other.bin" } }, null, CancellationToken.None));
        Assert.Equal(SttErrorCode.ToolMissing, unknown.Code);

        var mismatch = await Assert.ThrowsAsync<SttException>(() => Engine(WriteJson, Tools(new string('0', 64))).RunAsync(Request(), null, CancellationToken.None));
        Assert.Equal(SttErrorCode.ToolMissing, mismatch.Code);

        var memory = await Assert.ThrowsAsync<SttException>(() => Engine(WriteJson, memory: () => 0).RunAsync(Request(), null, CancellationToken.None));
        Assert.Equal(SttErrorCode.NotEnoughMemory, memory.Code);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "native")));
    }

    [Fact]
    public void Arguments_UseRelativeAudioAndOutputPaths_AndAddVadOnlyWhenEnabled()
    {
        var engine = new WhisperCliEngine(Tools());
        var withVad = engine.Arguments(Request() with { Configuration = _config with { UseVad = true, Threads = 6 } });
        var without = engine.Arguments(Request());

        Assert.Equal(@"..\part-0001.wav", withVad[withVad.ToList().IndexOf("-f") + 1]);
        Assert.Equal("mic-0001", withVad[withVad.ToList().IndexOf("-of") + 1]);
        Assert.Equal("6", withVad[withVad.ToList().IndexOf("-t") + 1]);
        Assert.Contains("--vad", withVad);
        Assert.Equal(_dir.File("vad.bin"), withVad[withVad.ToList().IndexOf("-vm") + 1]);
        Assert.DoesNotContain("--vad", without);
        Assert.Contains("-oj", without);
        Assert.Equal("ja", without[without.ToList().IndexOf("-l") + 1]);
    }

    [Fact]
    public void ReadJson_SkipsABom_AndEmptyText_ClampsTheEnd_AndRejectsTimesOutsideTheAudio()
    {
        var path = _dir.File("out.json");
        File.WriteAllText(path, """{"transcription":[{"offsets":{"from":0,"to":500},"text":" a"},{"offsets":{"from":600,"to":700},"text":"  "},{"offsets":{"from":4800,"to":5400},"text":"b"}]}""", new UTF8Encoding(true));
        Assert.Equal([new SttRawSegment(0, 500, "a", 0), new SttRawSegment(4_800, 5_000, "b", 2)], WhisperCliEngine.ReadJson(path, 5_000));

        File.WriteAllText(path, """{"transcription":[{"offsets":{"from":0,"to":9000},"text":"c"}]}""");
        Assert.Equal(SttErrorCode.RunFailed, Assert.Throws<SttException>(() => WhisperCliEngine.ReadJson(path, 5_000)).Code);

        File.WriteAllText(path, """{"result":{}}""");
        Assert.Equal(SttErrorCode.RunFailed, Assert.Throws<SttException>(() => WhisperCliEngine.ReadJson(path, 5_000)).Code);
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        private readonly Lock _gate = new();

        public void Report(double value)
        {
            lock (_gate) report(value);
        }
    }
}

/// <summary>Runs only where whisper-cli and the small model are installed (the developer PC).</summary>
public sealed class WhisperFactAttribute : FactAttribute
{
    public static string AppRoot => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "VoiceJournal");

    public WhisperFactAttribute()
    {
        var tools = SttToolSettings.Defaults(AppRoot);
        if (!File.Exists(tools.WhisperCli.Path) || !File.Exists(tools.Models["ggml-small.bin"].Path) || !File.Exists(tools.VadModel.Path)
            || !File.Exists(Path.Combine(AppRoot, "verify", "audio", "test_ja_tts.wav")))
            Skip = "whisper-cli, the small model, the VAD model or the Phase A test speech is not installed.";
    }
}

/// <summary>The real recognizer on a session built from the Phase A test speech, placed 10 s into the recording.</summary>
public sealed class WhisperIntegrationTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [WhisperFact]
    public async Task SmallModelWithVad_ReturnsSessionTimes_ForSpeechThatStartsTenSecondsIn()
    {
        using var tts = new NAudio.Wave.AudioFileReader(Path.Combine(WhisperFactAttribute.AppRoot, "verify", "audio", "test_ja_tts.wav"));
        var rate = tts.WaveFormat.SampleRate;
        var speech = new float[tts.Length / 4 / tts.WaveFormat.Channels];
        var frame = new float[tts.WaveFormat.Channels];
        for (var i = 0; i < speech.Length && tts.Read(frame.AsSpan()) == frame.Length; i++) speech[i] = frame[0];
        var audio = new float[10 * rate].Concat(speech).Concat(new float[5 * rate]).ToArray();
        SttTestSession.Save(_dir.Path, CaptureMode.Mic,
            SttTestSession.Track(AudioSource.Mic, rate, 1, SttTestSession.Chunk(_dir.Path, AudioSource.Mic, 1, 0, rate, 1, audio)));
        var config = new SttConfiguration { ModelName = "ggml-small.bin", Threads = Math.Max(1, System.Environment.ProcessorCount / 2) };

        var input = await new SttInputBuilder().BuildAsync(_dir.Path, config, null, CancellationToken.None);
        var piece = input.Pieces.Single();
        var result = await new WhisperCliEngine(SttToolSettings.Defaults(WhisperFactAttribute.AppRoot)).RunAsync(new SttRunRequest
        {
            AudioPath = piece.Path,
            OutputDirectory = Path.Combine(_dir.Path, "stt", "native", "test"),
            OutputName = "mic-0001",
            Configuration = config,
            DurationMs = piece.Piece.DurationMs,
        }, null, CancellationToken.None);

        Assert.NotEmpty(result.Segments);
        // The TTS clip has a short lead-in; the first words land just after 10 s, never in the silence before.
        Assert.InRange(result.Segments[0].StartMs, 9_700, 11_500);
        Assert.Contains("定例会議", string.Concat(result.Segments.Select(s => s.Text)), StringComparison.Ordinal);
    }
}
