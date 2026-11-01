using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceJournal.Core.Playback;

namespace VoiceJournal.Infrastructure.Audio;

/// <summary>
/// Minimal library playback (Phase B): the chunks of one track back to back on the current Windows default
/// render device, resampled to the device mix rate when needed. The original files are only read.
/// The device objects stay alive until playback ends (the player uses them on its own thread).
/// </summary>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private readonly Lock _gate = new();
    private Playback? _current;
    private bool _disposed;

    public bool IsPlaying
    {
        get
        {
            lock (_gate) return _current?.Player.PlaybackState == PlaybackState.Playing;
        }
    }

    public event EventHandler<PlaybackEndedEventArgs>? Ended;

    public string Play(IReadOnlyList<string> wavFiles)
    {
        ArgumentNullException.ThrowIfNull(wavFiles);
        if (wavFiles.Count == 0) throw new ArgumentException("Nothing to play.", nameof(wavFiles));
        StopPlayback();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var readers = new List<AudioFileReader>();
            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            WasapiPlayer? player = null;
            try
            {
                foreach (var file in wavFiles) readers.Add(new AudioFileReader(file));
                var first = readers[0].WaveFormat;
                if (readers.Any(r => r.WaveFormat.SampleRate != first.SampleRate || r.WaveFormat.Channels != first.Channels))
                    throw new InvalidDataException("The chunks of a track must share one format.");
                ISampleProvider chain = new ConcatenatingSampleProvider(readers);

                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using (var client = device.CreateAudioClient())
                {
                    var mixRate = client.MixFormat.SampleRate;
                    if (mixRate != first.SampleRate) chain = new WdlResamplingSampleProvider(chain, mixRate);
                }
                player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().Build();
                player.PlaybackStopped += OnPlaybackStopped;
                player.Init(new SampleToWaveProvider(chain));
                player.Play();
                _current = new Playback(player, device, enumerator, readers);
                return device.FriendlyName;
            }
            catch
            {
                if (player is not null)
                {
                    player.PlaybackStopped -= OnPlaybackStopped;
                    player.Dispose();
                }
                device?.Dispose();
                enumerator?.Dispose();
                foreach (var r in readers) r.Dispose();
                throw;
            }
        }
    }

    public void StopPlayback()
    {
        Playback? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
        }
        if (current is null) return;
        current.Player.PlaybackStopped -= OnPlaybackStopped;
        current.Dispose();
        Ended?.Invoke(this, new PlaybackEndedEventArgs(null));
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopPlayback();
        _disposed = true;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Playback? current;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _current?.Player)) return;
            current = _current;
            _current = null;
        }
        current!.Player.PlaybackStopped -= OnPlaybackStopped;
        ThreadPool.QueueUserWorkItem(_ => current.Dispose()); // not on the player's own thread
        Ended?.Invoke(this, new PlaybackEndedEventArgs(e.Exception));
    }

    private sealed record Playback(WasapiPlayer Player, MMDevice Device, MMDeviceEnumerator Enumerator, List<AudioFileReader> Readers) : IDisposable
    {
        public void Dispose()
        {
            Player.Dispose();
            foreach (var r in Readers) r.Dispose();
            Device.Dispose();
            Enumerator.Dispose();
        }
    }
}
