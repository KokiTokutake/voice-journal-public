using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceJournal.Core.Analysis;

namespace VoiceJournal.Infrastructure.Llm;

/// <summary>
/// The LM Studio base URL, accepted only on loopback (detail spec 9.1): <c>http://127.0.0.1:1234/v1</c>,
/// <c>http://[::1]:1234/v1</c>, or <c>localhost</c> when every address it resolves to is loopback.
/// </summary>
public sealed record LlmEndpoint(Uri BaseUri)
{
    public const string Default = "http://127.0.0.1:1234/v1";

    /// <exception cref="ArgumentException">Not an http(s) URL on loopback.</exception>
    public static LlmEndpoint Parse(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("LM Studio の URL は http://127.0.0.1:1234/v1 の形で指定してください。", nameof(url));
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("LM Studio の URL に利用者名・クエリ・フラグメントは使えません。", nameof(url));
        if (IPAddress.TryParse(uri.IdnHost, out var ip))
        {
            if (!IPAddress.IsLoopback(ip)) throw new ArgumentException("LM Studio にはこの PC の中（127.0.0.1 または ::1）でだけ接続します。", nameof(url));
        }
        else if (!string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("LM Studio にはこの PC の中（127.0.0.1 または ::1）でだけ接続します。", nameof(url));
        }
        else if (Dns.GetHostAddresses(uri.IdnHost) is var addresses && (addresses.Length == 0 || !addresses.All(IPAddress.IsLoopback)))
        {
            throw new ArgumentException("localhost がこの PC の外のアドレスに解決されます。127.0.0.1 を指定してください。", nameof(url));
        }
        var text = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return new LlmEndpoint(new Uri(text, UriKind.Absolute));
    }
}

/// <summary>
/// OpenAI-compatible client for LM Studio (detail spec 9.1, 21.3): no proxy, no redirects, every socket checked to
/// be loopback, no tools, structured output with a strict JSON Schema. Nothing of the prompt or answer is logged.
/// </summary>
public sealed class LmStudioClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly LlmEndpoint _endpoint;
    private readonly TimeSpan _requestTimeout;

    public LmStudioClient(LlmEndpoint endpoint, TimeSpan requestTimeout, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        _requestTimeout = requestTimeout;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>No proxy, no redirect, short connect timeout, and a socket-level loopback check (a hostile DNS answer cannot redirect it).</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            var target = addresses.FirstOrDefault(IPAddress.IsLoopback)
                ?? throw new HttpRequestException("LM Studio への接続先がループバックではありません。");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public async Task<LlmModelInfo> GetModelAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        using var models = await SendAsync(HttpMethod.Get, "models", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var ids = models.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToList()
            : [];
        if (!ids.Contains(modelId, StringComparer.Ordinal))
            throw new LlmUnavailableException($"LM Studio にモデル「{modelId}」が読み込まれていません。");

        // LM Studio's own REST API reports the loaded context length; the OpenAI-compatible one does not.
        int? loaded = null, max = null;
        bool? isLoaded = null;
        try
        {
            var native = new Uri(_endpoint.BaseUri, "../api/v0/models/" + Uri.EscapeDataString(modelId));
            using var info = await SendAsync(HttpMethod.Get, native, null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            loaded = TryInt(info.RootElement, "loaded_context_length");
            max = TryInt(info.RootElement, "max_context_length");
            if (info.RootElement.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
                isLoaded = state.GetString() == "loaded";
        }
        catch (Exception ex) when (ex is LlmProtocolException or LlmUnavailableException)
        {
            // Older or newer servers without this endpoint: the settings value is used and shown to the user.
        }
        return new LlmModelInfo(modelId, isLoaded == false ? null : loaded, max, isLoaded);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray([.. request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role switch { LlmRole.System => "system", LlmRole.Assistant => "assistant", _ => "user" },
                ["content"] = m.Content,
            })]),
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
            // No thinking: Gemma 4 thinks by default in LM Studio and spends about three quarters of the output on it
            // (2026-11-04). Only "reasoning_effort": "none" turns it off there; models without thinking ignore it.
            ["reasoning_effort"] = "none",
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.Schema),
                },
            },
        };
        var watch = Stopwatch.StartNew();
        using var response = await SendAsync(HttpMethod.Post, "chat/completions", body.ToJsonString(), _requestTimeout, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        var root = response.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new LlmProtocolException("LM Studio の応答に choices がありません。");
        var choice = choices[0];
        var content = choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()!
            : throw new LlmProtocolException("LM Studio の応答に本文がありません。");
        var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        var usage = root.TryGetProperty("usage", out var u) ? u : default;
        return new LlmResponse
        {
            Content = content,
            FinishReason = finish,
            PromptTokens = usage.ValueKind == JsonValueKind.Object ? TryInt(usage, "prompt_tokens") ?? 0 : 0,
            CompletionTokens = usage.ValueKind == JsonValueKind.Object ? TryInt(usage, "completion_tokens") ?? 0 : 0,
            Elapsed = watch.Elapsed,
        };
    }

    private Task<JsonDocument> SendAsync(HttpMethod method, string relative, string? json, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(method, new Uri(_endpoint.BaseUri, relative), json, timeout, cancellationToken);

    private async Task<JsonDocument> SendAsync(HttpMethod method, Uri uri, string? json, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, uri);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmProtocolException($"LM Studio が {timeout.TotalSeconds:0} 秒以内に応答しませんでした。");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException("LM Studio のサーバーに接続できません。LM Studio を起動し、サーバーを開始してください。", ex);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new LlmProtocolException($"LM Studio がリダイレクト（HTTP {status}）を返しました。従いません。");
            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorMessage(text);
                if (error.Contains("context", StringComparison.OrdinalIgnoreCase) || error.Contains("memory", StringComparison.OrdinalIgnoreCase))
                    throw new LlmContextException($"LM Studio が要求を処理できませんでした（HTTP {status}、コンテキスト長またはメモリ）。");
                if (response.StatusCode is HttpStatusCode.NotFound || error.Contains("model", StringComparison.OrdinalIgnoreCase) && error.Contains("load", StringComparison.OrdinalIgnoreCase))
                    throw new LlmUnavailableException($"LM Studio でモデルが読み込まれていません（HTTP {status}）。");
                throw new LlmProtocolException($"LM Studio がエラーを返しました（HTTP {status}）。");
            }
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new LlmProtocolException("LM Studio の応答を JSON として読めません。", ex);
            }
        }
    }

    // The server's error text is only searched for keywords; it is never logged (it can echo the prompt).
    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
        }
        return body.Length > 2000 ? body[..2000] : body;
    }

    private static int? TryInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public void Dispose() => _http.Dispose();
}
