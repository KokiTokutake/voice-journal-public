using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.Infrastructure.Stt;

/// <summary>
/// Runs whisper.cpp's <c>whisper-cli.exe</c> (detail spec 8.1): registered executable and models only, verified by
/// SHA-256; arguments passed one by one without a shell; stdout and stderr read asynchronously; the exit code, the
/// JSON output and the segment times are checked; the native JSON is converted, never used as the app contract.
/// </summary>
public sealed partial class WhisperCliEngine : ISttEngine
{
    public const string EngineName = "whisper.cpp";

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), string> HashCache = new();

    private readonly SttToolSettings _tools;
    private readonly Func<long?> _availableMemoryMb;
    private string? _version;

    /// <summary>Test seam: replaces the recognizer arguments (to run a stand-in process).</summary>
    internal Func<SttRunRequest, IReadOnlyList<string>>? ArgumentsOverride { get; init; }

    /// <summary>Test seam: skips running <c>--version</c>.</summary>
    internal string? KnownVersion { init => _version = value; }

    /// <param name="availableMemoryMb">Free physical memory in MB, or null when unknown (defaults to the OS value).</param>
    public WhisperCliEngine(SttToolSettings tools, Func<long?>? availableMemoryMb = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _availableMemoryMb = availableMemoryMb ?? MemoryStatus.AvailablePhysicalMb;
    }

    public async Task<SttEngineInfo> VerifyAsync(SttConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!_tools.Models.TryGetValue(configuration.ModelName, out var model))
            throw new SttException(SttErrorCode.ToolMissing, $"Model '{configuration.ModelName}' is not registered.");
        await Task.Run(() =>
        {
            Check(_tools.WhisperCli.Path, _tools.WhisperCli.Sha256, "whisper-cli");
            Check(model.Path, model.Sha256, configuration.ModelName);
            if (configuration.UseVad) Check(_tools.VadModel.Path, _tools.VadModel.Sha256, "VAD model");
        }, cancellationToken).ConfigureAwait(false);
        _version ??= await ReadVersionAsync(cancellationToken).ConfigureAwait(false);
        return new SttEngineInfo(EngineName, _version, _tools.WhisperCli.Sha256, configuration.ModelName, model.Sha256);
    }

    private static void Check(string path, string sha256, string what)
    {
        if (!File.Exists(path)) throw new SttException(SttErrorCode.ToolMissing, $"{what} was not found at '{path}'.");
        if (!string.Equals(HashOf(path), sha256, StringComparison.OrdinalIgnoreCase))
            throw new SttException(SttErrorCode.ToolMissing, $"{what} at '{path}' does not match its registered SHA-256.");
    }

    private static string HashOf(string path)
    {
        var info = new FileInfo(path);
        return HashCache.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), _ => SttInputBuilder.HashFile(path));
    }

    private async Task<string> ReadVersionAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_tools.WhisperCli.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new SttException(SttErrorCode.ToolMissing, "whisper-cli could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var text = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
        return VersionLine().Match(text) is { Success: true } m ? m.Groups[1].Value : "unknown";
    }

    [GeneratedRegex(@"whisper\.cpp version:\s*(\S+)")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"progress\s*=\s*(\d+)%")]
    private static partial Regex ProgressLine();

    /// <summary>The command line for <paramref name="request"/>, relative to the run's output folder.</summary>
    internal IReadOnlyList<string> Arguments(SttRunRequest request)
    {
        var c = request.Configuration;
        var model = _tools.Models[c.ModelName];
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-m", model.Path,
            // Relative paths keep non-ASCII workspace paths away from the CLI's narrow-character argv.
            "-f", Path.GetRelativePath(request.OutputDirectory, request.AudioPath),
            "-l", c.Language,
            "-t", c.Threads.ToString(inv),
            "-bs", c.BeamSize.ToString(inv),
            "-bo", c.BestOf.ToString(inv),
            "-oj",
            "-of", request.OutputName,
            "-pp",
        };
        if (c.UseVad)
        {
            args.AddRange([
                "--vad", "-vm", _tools.VadModel.Path,
                "-vt", c.VadThreshold.ToString("0.###", inv),
                "-vsd", c.VadMinSilenceMs.ToString(inv),
                "-vp", c.VadSpeechPadMs.ToString(inv),
            ]);
        }
        return args;
    }

    public async Task<SttRunResult> RunAsync(SttRunRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await VerifyAsync(request.Configuration, cancellationToken).ConfigureAwait(false);
        var model = _tools.Models[request.Configuration.ModelName];
        if (_availableMemoryMb() is { } free && free < model.RequiredMemoryMb)
            throw new SttException(SttErrorCode.NotEnoughMemory, $"{free} MB of memory is free; {model.RequiredMemoryMb} MB is needed for {request.Configuration.ModelName}.");

        Directory.CreateDirectory(request.OutputDirectory);
        var jsonPath = Path.Combine(request.OutputDirectory, request.OutputName + ".json");
        if (File.Exists(jsonPath)) File.Delete(jsonPath); // left by an interrupted run of the same piece
        var start = new ProcessStartInfo(_tools.WhisperCli.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.OutputDirectory,
        };
        var args = ArgumentsOverride?.Invoke(request) ?? Arguments(request);
        foreach (var a in args) start.ArgumentList.Add(a);
        File.WriteAllText(Path.Combine(request.OutputDirectory, request.OutputName + ".args.json"),
            JsonSerializer.Serialize(new { executable = _tools.WhisperCli.Path, arguments = args, started_at_utc = DateTimeOffset.UtcNow }), Encoding.UTF8);

        // The log keeps whisper's diagnostics (system info, progress). The text goes to the JSON only.
        using var log = new StreamWriter(Path.Combine(request.OutputDirectory, request.OutputName + ".log"), append: false, Encoding.UTF8);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var logGate = new Lock();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (logGate) log.WriteLine(e.Data);
            if (ProgressLine().Match(e.Data) is { Success: true } m)
                progress?.Report(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0);
        };
        process.OutputDataReceived += (_, _) => { };

        try
        {
            if (!process.Start()) throw new SttException(SttErrorCode.RunFailed, "whisper-cli did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new SttException(SttErrorCode.ToolMissing, "whisper-cli could not be started.", ex);
        }
        TrySetPriority(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var active = Stopwatch.StartNew();
        var suspended = false;
        var pause = request.Pause;
        void OnPause(object? sender, EventArgs e) => suspended = ApplyPause(process, pause!.IsPaused, suspended, active);
        if (pause is not null)
        {
            pause.Changed += OnPause;
            suspended = ApplyPause(process, pause.IsPaused, suspended, active);
        }
        try
        {
            var exit = process.WaitForExitAsync(CancellationToken.None);
            while (!exit.IsCompleted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Kill(process, suspended);
                    throw new SttException(SttErrorCode.Cancelled, "The recognition was cancelled.");
                }
                if (active.Elapsed > request.Timeout)
                {
                    Kill(process, suspended);
                    throw new SttException(SttErrorCode.RunFailed, $"whisper-cli did not finish within {request.Timeout.TotalMinutes:0} minutes.");
                }
                await Task.WhenAny(exit, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
            }
            await exit.ConfigureAwait(false);
        }
        finally
        {
            if (pause is not null) pause.Changed -= OnPause;
        }
        process.WaitForExit(); // flush the redirected streams
        active.Stop();
        lock (logGate) log.Flush();

        if (process.ExitCode != 0) throw new SttException(SttErrorCode.RunFailed, $"whisper-cli exited with code {process.ExitCode}.");
        var segments = ReadJson(jsonPath, request.DurationMs);
        progress?.Report(1);
        return new SttRunResult { Segments = segments, NativeFile = request.OutputName + ".json", Elapsed = active.Elapsed };
    }

    /// <summary>Converts whisper's JSON into piece-local segments and checks the times.</summary>
    internal static IReadOnlyList<SttRawSegment> ReadJson(string path, long durationMs)
    {
        if (!File.Exists(path)) throw new SttException(SttErrorCode.RunFailed, "whisper-cli wrote no JSON output.");
        try
        {
            ReadOnlyMemory<byte> bytes = File.ReadAllBytes(path);
            if (bytes.Span.StartsWith("﻿"u8)) bytes = bytes[3..];
            using var doc = JsonDocument.Parse(bytes);
            var list = new List<SttRawSegment>();
            var index = 0;
            foreach (var item in doc.RootElement.GetProperty("transcription").EnumerateArray())
            {
                var offsets = item.GetProperty("offsets");
                var from = offsets.GetProperty("from").GetInt64();
                var to = offsets.GetProperty("to").GetInt64();
                var text = (item.GetProperty("text").GetString() ?? "").Trim();
                var i = index++;
                if (text.Length == 0) continue;
                // whisper rounds to 10 ms frames and can run a little past the end of the audio.
                if (from < 0 || from > durationMs || to > durationMs + 1_000)
                    throw new SttException(SttErrorCode.RunFailed, $"whisper-cli returned a segment outside the audio ({from}–{to} ms of {durationMs} ms).");
                to = Math.Min(to, durationMs);
                if (to <= from) to = Math.Min(durationMs, from + 10);
                if (to <= from) continue;
                list.Add(new SttRawSegment(from, to, text, i));
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SttException(SttErrorCode.RunFailed, "whisper-cli wrote JSON in an unexpected shape.", ex);
        }
    }

    private static bool ApplyPause(Process process, bool pause, bool suspended, Stopwatch active)
    {
        if (process.HasExited || pause == suspended) return suspended;
        var status = pause ? NativeMethods.NtSuspendProcess(process.Handle) : NativeMethods.NtResumeProcess(process.Handle);
        if (status != 0)
        {
            // Could not pause: the job runner falls back to cancelling and redoing this piece.
            if (pause) throw new SttException(SttErrorCode.RunFailed, $"whisper-cli could not be paused (NTSTATUS 0x{status:X8}).");
            return suspended;
        }
        if (pause) active.Stop();
        else active.Start();
        return pause;
    }

    private static void Kill(Process process, bool suspended)
    {
        try
        {
            // A suspended process cannot be terminated cleanly; if resuming fails, Kill still ends it.
            if (suspended) _ = NativeMethods.NtResumeProcess(process.Handle);
            // Only the tree this engine started (detail spec 7). whisper-cli has no graceful stop request on Windows.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static void TrySetPriority(Process process)
    {
        try
        {
            // Keep the UI and any recording responsive (detail spec 3.1: recording comes first).
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static class NativeMethods
    {
        [DllImport("ntdll.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int NtResumeProcess(IntPtr processHandle);
    }
}

/// <summary>Free physical memory (detail spec 3.1: check before starting the recognizer).</summary>
public static class MemoryStatus
{
    public static long? AvailablePhysicalMb()
    {
        var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? (long)(status.AvailablePhysical / (1024 * 1024)) : null;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
