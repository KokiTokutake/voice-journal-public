using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceJournal.Core.Playback;

namespace VoiceJournal.Infrastructure.Audio;

/// <summary>
/// Minimal library playback (Phase B): the chunks of one track back to back on the default render device,
/// resampled to the device mix rate when needed. The original files are only read.
/// </summary>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private readonly Lock _gate = new();
    private WasapiPlayer? _player;
    private List<AudioFileReader> _readers = [];
    private bool _disposed;

    public bool IsPlaying
    {
        get
        {
            lock (_gate) return _player?.PlaybackState == PlaybackState.Playing;
        }
    }

    public event EventHandler<PlaybackEndedEventArgs>? Ended;

    public void Play(IReadOnlyList<string> wavFiles)
    {
        ArgumentNullException.ThrowIfNull(wavFiles);
        if (wavFiles.Count == 0) throw new ArgumentException("Nothing to play.", nameof(wavFiles));
        StopPlayback();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var readers = wavFiles.Select(f => new AudioFileReader(f)).ToList();
            try
            {
                var first = readers[0].WaveFormat;
                if (readers.Any(r => r.WaveFormat.SampleRate != first.SampleRate || r.WaveFormat.Channels != first.Channels))
                    throw new InvalidDataException("The chunks of a track must share one format.");
                ISampleProvider chain = new ConcatenatingSampleProvider(readers);

                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using (var client = device.CreateAudioClient())
                {
                    var mixRate = client.MixFormat.SampleRate;
                    if (mixRate != first.SampleRate) chain = new WdlResamplingSampleProvider(chain, mixRate);
                }
                var player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().Build();
                player.PlaybackStopped += OnPlaybackStopped;
                player.Init(new SampleToWaveProvider(chain));
                player.Play();
                _player = player;
                _readers = readers;
            }
            catch
            {
                foreach (var r in readers) r.Dispose();
                throw;
            }
        }
    }

    public void StopPlayback()
    {
        WasapiPlayer? player;
        List<AudioFileReader> readers;
        lock (_gate)
        {
            player = _player;
            readers = _readers;
            _player = null;
            _readers = [];
        }
        if (player is null) return;
        player.PlaybackStopped -= OnPlaybackStopped;
        player.Dispose();
        foreach (var r in readers) r.Dispose();
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
        WasapiPlayer? player;
        List<AudioFileReader> readers;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _player)) return;
            player = _player;
            readers = _readers;
            _player = null;
            _readers = [];
        }
        player!.PlaybackStopped -= OnPlaybackStopped;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            player.Dispose();
            foreach (var r in readers) r.Dispose();
        });
        Ended?.Invoke(this, new PlaybackEndedEventArgs(e.Exception));
    }
}
