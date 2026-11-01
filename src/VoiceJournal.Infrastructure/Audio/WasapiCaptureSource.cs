using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;

namespace VoiceJournal.Infrastructure.Audio;

/// <summary>
/// <see cref="IAudioCaptureSource"/> over NAudio's <see cref="WasapiRecorder"/> (shared mode, event driven).
/// Fixed to one endpoint id; never follows the Windows default (detail spec 6.4). One-shot: create a new
/// instance to capture again (e.g. after a reconnect).
/// </summary>
public sealed class WasapiCaptureSource : IAudioCaptureSource
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private readonly MMDeviceNotificationClient _notifications;
    private int _stoppedRaised;
    private int _lost;
    private volatile bool _capturing;
    private bool _disposed;

    /// <exception cref="AudioDeviceException">The endpoint is missing or inactive, or its format is unsupported.</exception>
    public WasapiCaptureSource(AudioSource source, string deviceId, MicProcessingMode micMode)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        Source = source;
        _enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        WasapiRecorder? recorder = null;
        try
        {
            try
            {
                device = _enumerator.GetDevice(deviceId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new AudioDeviceException(AudioDeviceErrorKind.DeviceNotFound, "The selected audio device was not found.", ex);
            }
            if (device.State != DeviceState.Active)
                throw new AudioDeviceException(AudioDeviceErrorKind.DeviceNotFound, $"The selected audio device is not active ({device.State}).");
            if (device.DataFlow != (source == AudioSource.Mic ? DataFlow.Capture : DataFlow.Render))
                throw new ArgumentException($"The device cannot be used as {source}.", nameof(deviceId));

            DeviceId = device.ID;
            DeviceName = device.FriendlyName;
            var builder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithMmcssThreadPriority("Audio");
            if (source == AudioSource.Loopback) builder = builder.WithLoopbackCapture();
            if (source == AudioSource.Mic && micMode == MicProcessingMode.Raw) builder = builder.WithRawMode();
            try
            {
                recorder = builder.Build();
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or AudioDeviceException))
            {
                throw WasapiInterop.Wrap(ex, "Opening the audio device");
            }
            Format = WasapiInterop.ToAudioFormat(recorder.WaveFormat);
            _notifications = _enumerator.CreateNotificationClient(false);
        }
        catch
        {
            recorder?.Dispose();
            device?.Dispose();
            _enumerator.Dispose();
            throw;
        }

        _device = device;
        _recorder = recorder;
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += OnRecordingStopped;
        _notifications.DeviceStateChanged += OnDeviceStateChanged;
        _notifications.DeviceRemoved += OnDeviceRemoved;
    }

    public AudioSource Source { get; }

    public string DeviceId { get; } = string.Empty;

    public string DeviceName { get; } = string.Empty;

    public AudioFormatInfo Format { get; }

    public event AudioDataHandler? DataAvailable;

    public event EventHandler<CaptureSourceStoppedEventArgs>? Stopped;

    /// <exception cref="AudioDeviceException">The device could not be opened (permission, in use, raw mode...).</exception>
    public void StartCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capturing || Volatile.Read(ref _stoppedRaised) == 1) throw new InvalidOperationException("A capture source can be started only once.");
        try
        {
            _recorder.StartRecording(); // initializes the audio client synchronously; failures surface here
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw WasapiInterop.Wrap(ex, "Starting capture");
        }
        _capturing = true;
    }

    public void StopCapture()
    {
        if (_disposed || !_capturing) return;
        _recorder.StopRecording();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifications.DeviceStateChanged -= OnDeviceStateChanged;
        _notifications.DeviceRemoved -= OnDeviceRemoved;
        _notifications.Dispose();
        _recorder.DataAvailable -= OnData;
        _recorder.RecordingStopped -= OnRecordingStopped;
        _recorder.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }

    // Capture thread: no allocation, no blocking.
    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.IsEmpty) return; // WasapiRecorder also reports empty reads
        // The device position stops while loopback is idle (B3 measurement), so it is not used as a clock.
        DataAvailable?.Invoke(buffer, WasapiInterop.ToStatus(flags), WasapiInterop.ToCaptureTimestamp(qpcPosition, flags));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _capturing = false;
        RaiseStopped(e.Exception is null ? null : WasapiInterop.Wrap(e.Exception, "Capture"));
    }

    // Endpoint notifications arrive on a Windows audio worker thread that holds an internal lock:
    // hand off to the thread pool before doing anything that could call back into the audio stack.
    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        if (e.NewState != DeviceState.Active && string.Equals(e.DeviceId, DeviceId, StringComparison.Ordinal)) OnDeviceLost($"device state {e.NewState}");
    }

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e)
    {
        if (string.Equals(e.DeviceId, DeviceId, StringComparison.Ordinal)) OnDeviceLost("device removed");
    }

    private void OnDeviceLost(string reason)
    {
        if (!_capturing || Interlocked.Exchange(ref _lost, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            _capturing = false;
            RaiseStopped(new AudioDeviceException(AudioDeviceErrorKind.DeviceInvalidated, $"The audio device was disconnected ({reason})."));
            try
            {
                _recorder.StopRecording();
            }
#pragma warning disable CA1031 // The device is gone; teardown errors are expected and not actionable.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        });
    }

    private void RaiseStopped(Exception? error)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) == 0) Stopped?.Invoke(this, new CaptureSourceStoppedEventArgs(error));
    }
}
