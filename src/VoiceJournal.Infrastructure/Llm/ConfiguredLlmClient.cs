using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Infrastructure.Llm;

/// <summary>
/// An <see cref="ILlmClient"/> that follows the settings: each call reads the current URL and timeout and uses an
/// <see cref="LmStudioClient"/> for them, so a change in UI-12 applies to the next request without a restart.
/// A URL that is not on loopback is refused as LLM-001 (nothing is sent).
/// </summary>
public sealed class ConfiguredLlmClient : ILlmClient, IDisposable
{
    private readonly Func<(string Url, TimeSpan Timeout)> _settings;
    private readonly Lock _lock = new();
    // Replaced clients may still serve a request in flight; they are disposed with this object.
    private readonly List<LmStudioClient> _retired = [];
    private (string Url, TimeSpan Timeout) _key;
    private LmStudioClient? _client;
    private bool _disposed;

    public ConfiguredLlmClient(Func<(string Url, TimeSpan Timeout)> settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public Task<LlmModelInfo> GetModelAsync(string modelId, CancellationToken cancellationToken) =>
        Current().GetModelAsync(modelId, cancellationToken);

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
        Current().CompleteAsync(request, cancellationToken);

    private LmStudioClient Current()
    {
        var key = _settings();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is not null && _key == key) return _client;
            LlmEndpoint endpoint;
            try
            {
                endpoint = LlmEndpoint.Parse(key.Url);
            }
            catch (ArgumentException ex)
            {
                throw new LlmUnavailableException(ex.Message, ex);
            }
            if (_client is not null) _retired.Add(_client);
            _client = new LmStudioClient(endpoint, key.Timeout);
            _key = key;
            return _client;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _client?.Dispose();
            foreach (var c in _retired) c.Dispose();
            _retired.Clear();
        }
    }
}
