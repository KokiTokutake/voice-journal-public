using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.Infrastructure.Llm;

/// <summary>What the bundled server is started with. A different value restarts it.</summary>
/// <param name="Parallel">Server slots. Above 1 the slots share one KV buffer of <c>ContextLength × Parallel</c>, each limited to <c>ContextLength</c>.</param>
public sealed record LlamaServerLaunch(string ModelId, int ContextLength, int Parallel = 1);

/// <summary>A running server: where to send requests, with which key.</summary>
/// <param name="Notice">Set when memory was short at start: inference may push free memory under 4 GiB (NFR-05).</param>
public sealed record LlamaServerEndpoint(LlmEndpoint Endpoint, string ApiKey, LlamaServerLaunch Launch, string? Notice = null);

/// <summary>
/// Runs the bundled llama.cpp <c>llama-server.exe</c> as a child process (docs/phase-d-engine-plan.md): only while
/// records are made, on loopback with a random port and a random API key per run, with the settings the app passes
/// (context length, one slot, full GPU offload, no mmap, no thinking, MTP when registered). It stops when idle or when
/// asked (recording starts), so all its memory is given back each time. A Windows job object ends it with the app.
/// Nothing the server prints is logged (it can contain text of the prompt at higher verbosity).
/// </summary>
public sealed class LlamaServerHost : IDisposable
{
    private const int TailLines = 20;

    /// <summary>NFR-05: the free memory to keep while inferring.</summary>
    public const int LowMemoryMarginMb = 4096;

    private readonly Func<LlmToolSettings> _tools;
    private readonly Func<long?> _availableMemoryMb;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _startTimeout;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Queue<string> _tail = new();
    private readonly ITimer _idleTimer;
    private readonly JobObject _job = new();
    private Process? _process;
    private LlamaServerEndpoint? _running;
    private int _inUse;
    private bool _stopWhenReleased;
    private DateTimeOffset _lastUsed;
    private bool _disposed;

    public LlamaServerHost(Func<LlmToolSettings> tools, Func<long?>? availableMemoryMb = null, TimeSpan? idleTimeout = null, TimeSpan? startTimeout = null, TimeProvider? time = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _availableMemoryMb = availableMemoryMb ?? MemoryStatus.AvailablePhysicalMb;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(2);
        _startTimeout = startTimeout ?? TimeSpan.FromMinutes(3);
        _time = time ?? TimeProvider.System;
        _idleTimer = _time.CreateTimer(_ => StopIfIdle(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>True while a server process is running.</summary>
    public bool IsRunning
    {
        get { lock (_lock) return _process is { HasExited: false }; }
    }

    /// <summary>
    /// Starts the server for <paramref name="launch"/> (or returns the running one) and holds it until the returned
    /// lease is disposed; an idle server is stopped after the idle timeout.
    /// </summary>
    /// <exception cref="LlmUnavailableException">The engine or the model is not registered, not as registered, memory is short, or it did not start.</exception>
    public async Task<(LlamaServerEndpoint Endpoint, IDisposable Lease)> AcquireAsync(LlamaServerLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running is null || _running.Launch != launch || !IsRunning)
            {
                StopCore();
                _running = await StartAsync(launch, cancellationToken).ConfigureAwait(false);
            }
            lock (_lock)
            {
                _inUse++;
                _stopWhenReleased = false;
                _lastUsed = _time.GetUtcNow();
            }
            return (_running, new Lease(this));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the server now (recording started, or the app closes). A request in flight fails and is retried by its job.</summary>
    public void Stop()
    {
        _gate.Wait();
        try
        {
            StopCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the server as soon as no request holds it (recording started: the record job cancels its request first,
    /// so the job is put back in the queue rather than failing on a server that went away).
    /// </summary>
    public void StopWhenIdle()
    {
        lock (_lock)
        {
            if (_process is null) return;
            if (_inUse > 0)
            {
                _stopWhenReleased = true;
                return;
            }
        }
        _ = Task.Run(StopIfUnused);
    }

    private void Release()
    {
        bool stop;
        lock (_lock)
        {
            _inUse--;
            _lastUsed = _time.GetUtcNow();
            stop = _inUse == 0 && _stopWhenReleased;
            if (stop) _stopWhenReleased = false;
        }
        if (stop) _ = Task.Run(StopIfUnused);
    }

    private void StopIfUnused()
    {
        _gate.Wait();
        try
        {
            lock (_lock)
                if (_inUse > 0 || _disposed) return;
            StopCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopIfIdle()
    {
        lock (_lock)
        {
            if (_process is null || _inUse > 0 || _time.GetUtcNow() - _lastUsed < _idleTimeout) return;
        }
        if (!_gate.Wait(0)) return; // starting or stopping: try again on the next tick
        try
        {
            lock (_lock)
                if (_inUse > 0) return;
            StopCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LlamaServerEndpoint> StartAsync(LlamaServerLaunch launch, CancellationToken cancellationToken)
    {
        var tools = _tools();
        if (!tools.Models.TryGetValue(launch.ModelId, out var model))
            throw new LlmUnavailableException($"モデル「{launch.ModelId}」は推論エンジンに登録されていません。");
        await Task.Run(() => LlmToolCheck.Verify(tools.LlamaServer, model), cancellationToken).ConfigureAwait(false);
        if (_availableMemoryMb() is { } free && free < model.Model.RequiredMemoryMb)
            throw new LlmUnavailableException($"空きメモリが {free} MB で、モデル「{launch.ModelId}」には {model.Model.RequiredMemoryMb} MB 必要です。ほかのアプリを閉じてから、記録の作成をやり直してください。");
        // NFR-05 (free physical memory 4 GiB or more while inferring): the engine still starts, the record says so.
        var notice = _availableMemoryMb() is { } before && before < model.Model.RequiredMemoryMb + LowMemoryMarginMb
            ? $"記録を作り始めたときの空きメモリは {before} MB でした。推論エンジンが約 {model.Model.RequiredMemoryMb} MB 使うため、推論中は空きが 4 GB を下回った可能性があります。ほかのアプリを閉じると安定します。"
            : null;

        var port = FreeLoopbackPort();
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(tools.LlamaServer.ServerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = tools.LlamaServer.Directory,
        };
        foreach (var a in Arguments(launch, model, port)) start.ArgumentList.Add(a);
        // The server reads LLAMA_ARG_* (e.g. LLAMA_ARG_HOST=0.0.0.0) from its environment: none of the user's may reach it.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);
        start.Environment["LLAMA_API_KEY"] = key; // not on the command line, which other processes can read

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new LlmUnavailableException("推論エンジンを起動できませんでした。");
        }
        catch (Win32Exception ex)
        {
            throw new LlmUnavailableException("推論エンジンを起動できませんでした。", ex);
        }
        _job.Assign(process);
        TrySetPriority(process);
        lock (_lock)
        {
            _tail.Clear();
            _process = process;
        }
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var endpoint = LlmEndpoint.Parse($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/v1");
        try
        {
            await WaitUntilReadyAsync(process, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            StopCore();
            throw;
        }
        return new LlamaServerEndpoint(endpoint, key, launch, notice);
    }

    /// <summary>The command line (no secrets: the key goes through the environment).</summary>
    public static IReadOnlyList<string> Arguments(LlamaServerLaunch launch, LlmModelFiles model, int port)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(model);
        var c = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-m", model.Model.Path,
            "--host", "127.0.0.1",
            "--port", port.ToString(c),
            "--no-webui",
            "-np", Math.Max(1, launch.Parallel).ToString(c),
            "-c", (launch.ContextLength * Math.Max(1, launch.Parallel)).ToString(c),
            "-ngl", "999",
            "-lm", "none", // no mmap: with it the weights were held twice (file mapping and GPU copy) in LM Studio
            "--reasoning", "off",
            "--alias", launch.ModelId,
        };
        if (launch.Parallel > 1) args.AddRange(["-kvu", "--kv-unified-per-slot", launch.ContextLength.ToString(c)]);
        if (model.Mtp is { } mtp) args.AddRange(["--spec-type", "draft-mtp", "-md", mtp.Path, "-ngld", "999"]);
        return args;
    }

    private async Task WaitUntilReadyAsync(Process process, LlmEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(OpenAiCompatibleClient.CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };
        var health = new Uri(endpoint.BaseUri, "../health");
        var deadline = _time.GetUtcNow() + _startTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new LlmUnavailableException($"推論エンジンが起動の途中で終了しました（終了コード {process.ExitCode.ToString(CultureInfo.InvariantCulture)}）。{LastError()}");
            try
            {
                using var response = await http.GetAsync(health, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    if (doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "ok") return;
                }
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // a slow answer while the model loads
            }
            catch (JsonException)
            {
            }
            if (_time.GetUtcNow() > deadline)
                throw new LlmUnavailableException($"推論エンジンが {_startTimeout.TotalSeconds:0} 秒以内に準備できませんでした。{LastError()}");
            await Task.Delay(TimeSpan.FromMilliseconds(250), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Keep(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_lock)
        {
            _tail.Enqueue(line.Length > 300 ? line[..300] : line);
            while (_tail.Count > TailLines) _tail.Dequeue();
        }
    }

    // Only lines llama.cpp marks as errors (" E "), for the user's message; never written to a log.
    private string LastError()
    {
        lock (_lock)
        {
            var errors = _tail.Where(l => l.Contains(" E ", StringComparison.Ordinal) || l.Contains("error", StringComparison.OrdinalIgnoreCase)).TakeLast(2).ToList();
            return errors.Count == 0 ? "" : " " + string.Join(" / ", errors);
        }
    }

    private void StopCore()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
            _running = null;
        }
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (Win32Exception)
        {
            // exiting
        }
        finally
        {
            process.Dispose();
        }
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void TrySetPriority(Process process)
    {
        try
        {
            // Recording comes first (detail spec 3.1).
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _idleTimer.Dispose();
        _gate.Wait();
        try
        {
            _disposed = true;
            StopCore();
        }
        finally
        {
            _gate.Release();
        }
        _job.Dispose();
    }

    private sealed class Lease(LlamaServerHost host) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) host.Release();
        }
    }

    /// <summary>A job object that kills its processes when the app's handle to it is closed (the app exits or crashes).</summary>
    private sealed class JobObject : IDisposable
    {
        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformation = 9;
        private readonly IntPtr _handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);

        public JobObject()
        {
            if (_handle == IntPtr.Zero) return;
            var info = new NativeMethods.JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = KillOnJobClose;
            var size = Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                _ = NativeMethods.SetInformationJobObject(_handle, ExtendedLimitInformation, buffer, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Assign(Process process)
        {
            // Best effort: without the job the server still stops with Stop() and the idle timeout.
            if (_handle != IntPtr.Zero) _ = NativeMethods.AssignProcessToJobObject(_handle, process.Handle);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) _ = NativeMethods.CloseHandle(_handle);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

/// <summary>
/// An <see cref="ILlmClient"/> on the bundled server: each call starts it if needed (for the requested model and the
/// settings' context length) and holds it for the call. The model id selects the registered GGUF.
/// </summary>
public sealed class BundledLlmClient : ILlmClient, IDisposable
{
    private readonly LlamaServerHost _host;
    private readonly Func<(int ContextLength, TimeSpan Timeout)> _settings;
    private readonly Func<int> _parallel;
    private readonly Lock _lock = new();
    private readonly List<OpenAiCompatibleClient> _retired = [];
    private (LlamaServerEndpoint Endpoint, TimeSpan Timeout)? _key;
    private OpenAiCompatibleClient? _client;

    /// <param name="parallel">Server slots (the record generator's parallelism); 1 when not given.</param>
    public BundledLlmClient(LlamaServerHost host, Func<(int ContextLength, TimeSpan Timeout)> settings, Func<int>? parallel = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _parallel = parallel ?? (() => 1);
    }

    public async Task<LlmModelInfo> GetModelAsync(string modelId, CancellationToken cancellationToken)
    {
        var (client, lease) = await ClientAsync(modelId, cancellationToken).ConfigureAwait(false);
        using (lease)
        {
            var info = await client.GetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
            return _key?.Endpoint.Notice is { } notice ? info with { Notice = notice } : info;
        }
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (client, lease) = await ClientAsync(request.Model, cancellationToken).ConfigureAwait(false);
        using (lease)
            return await client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(OpenAiCompatibleClient Client, IDisposable Lease)> ClientAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var (context, timeout) = _settings();
        var (endpoint, lease) = await _host.AcquireAsync(new LlamaServerLaunch(modelId, context, Math.Max(1, _parallel())), cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (_client is null || _key != (endpoint, timeout))
            {
                if (_client is not null) _retired.Add(_client);
                _client = new OpenAiCompatibleClient(endpoint.Endpoint, timeout, apiKey: endpoint.ApiKey);
                _key = (endpoint, timeout);
            }
            return (_client, lease);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _client?.Dispose();
            foreach (var c in _retired) c.Dispose();
            _retired.Clear();
        }
    }
}
