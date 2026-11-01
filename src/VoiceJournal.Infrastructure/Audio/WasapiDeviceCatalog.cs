using NAudio.CoreAudioApi;
using VoiceJournal.Core.Capture;

namespace VoiceJournal.Infrastructure.Audio;

/// <summary>Lists WASAPI endpoints and creates <see cref="WasapiCaptureSource"/>s on them.</summary>
public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private bool _disposed;

    public WasapiDeviceCatalog()
    {
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DeviceAdded += (_, e) => Raise(AudioDeviceChangeKind.Added, e.DeviceId);
        _notifications.DeviceRemoved += (_, e) => Raise(AudioDeviceChangeKind.Removed, e.DeviceId);
        _notifications.DeviceStateChanged += (_, e) => Raise(AudioDeviceChangeKind.StateChanged, e.DeviceId);
        _notifications.DefaultDeviceChanged += (_, e) => Raise(AudioDeviceChangeKind.DefaultChanged, e.DeviceId);
    }

    public event EventHandler<AudioDeviceChangedEventArgs>? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var flow = ToFlow(source);
        var defaultId = DefaultId(flow);
        var list = new List<AudioDeviceInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, source, device.ID == defaultId));
        }
        return [.. list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCulture)];
    }

    public AudioDeviceInfo? GetDefault(AudioSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_enumerator.TryGetDefaultAudioEndpoint(ToFlow(source), Role.Console, out var device) || device is null) return null;
        using (device) return new AudioDeviceInfo(device.ID, device.FriendlyName, source, true);
    }

    public IAudioCaptureSource CreateSource(AudioSource source, string deviceId, MicProcessingMode micMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new WasapiCaptureSource(source, deviceId, micMode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    private string? DefaultId(DataFlow flow)
    {
        if (!_enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out var device) || device is null) return null;
        using (device) return device.ID;
    }

    // Notifications come from a Windows audio worker thread holding an internal lock; never call back
    // into the audio stack from there (the UI typically re-enumerates in response).
    private void Raise(AudioDeviceChangeKind kind, string? deviceId)
    {
        if (_disposed) return;
        ThreadPool.QueueUserWorkItem(_ => DevicesChanged?.Invoke(this, new AudioDeviceChangedEventArgs(kind, deviceId)));
    }

    private static DataFlow ToFlow(AudioSource source) => source == AudioSource.Mic ? DataFlow.Capture : DataFlow.Render;
}
