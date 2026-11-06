using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Infrastructure.Llm;

namespace VoiceJournal.Tests.Infrastructure.Llm;

internal sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Seen.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class OpenAiCompatibleClientTests
{
    private static readonly LlmRequest Request = new()
    {
        Model = "gemma-4-e2b-it",
        Messages = [new(LlmRole.System, "指示"), new(LlmRole.User, "{}")],
        SchemaName = LlmSchemas.ExtractionName,
        Schema = LlmSchemas.Extraction,
        MaxTokens = 2048,
    };

    private static OpenAiCompatibleClient Client(FakeHandler handler) => new(LlmEndpoint.Parse("http://127.0.0.1:1234/v1"), TimeSpan.FromSeconds(5), handler);

    [Theory]
    [InlineData("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1/")]
    [InlineData("http://[::1]:1234/v1/", "http://[::1]:1234/v1/")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/v1/")]
    public void Loopback_IsAccepted(string url, string expected) => Assert.Equal(expected, LlmEndpoint.Parse(url).BaseUri.ToString());

    [Theory]
    [InlineData("http://192.168.1.10:1234/v1")]
    [InlineData("http://example.com/v1")]
    [InlineData("http://0.0.0.0:1234/v1")]
    [InlineData("ftp://127.0.0.1/v1")]
    [InlineData("http://user:pw@127.0.0.1:1234/v1")]
    [InlineData("http://127.0.0.1:1234/v1?x=1")]
    [InlineData("127.0.0.1:1234")]
    public void OtherHosts_AreRejected(string url) => Assert.Throws<ArgumentException>(() => LlmEndpoint.Parse(url));

    [Fact]
    public void DefaultHandler_UsesNoProxy_AndFollowsNoRedirects()
    {
        using var handler = OpenAiCompatibleClient.CreateHandler();
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task Complete_SendsAStrictSchema_AndNoTools()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"choices":[{"message":{"content":"{\"a\":1}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":120,"completion_tokens":30}}"""));
        using var client = Client(handler);

        var response = await client.CompleteAsync(Request, CancellationToken.None);

        Assert.Equal(("{\"a\":1}", "stop", 120, 30), (response.Content, response.FinishReason, response.PromptTokens, response.CompletionTokens));
        var (request, body) = handler.Seen.Single();
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", request.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(["system", "user"], root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Redirect_IsRefused()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.com/") }, Content = new StringContent("") });
        using var client = Client(handler);
        await Assert.ThrowsAsync<LlmProtocolException>(() => client.CompleteAsync(Request, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"error":"The number of tokens to keep from the initial prompt is greater than the context length"}""", typeof(LlmContextException))]
    [InlineData("""{"error":{"message":"Model is not loaded"}}""", typeof(LlmUnavailableException))]
    [InlineData("""{"error":"something else"}""", typeof(LlmProtocolException))]
    public async Task Errors_AreClassified(string body, Type expected)
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(body, HttpStatusCode.BadRequest));
        using var client = Client(handler);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.CompleteAsync(Request, CancellationToken.None));
        Assert.IsType(expected, ex);
        Assert.DoesNotContain("initial prompt", ex.Message, StringComparison.Ordinal); // the server text is not echoed
    }

    [Fact]
    public async Task ServerDown_IsUnavailable()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("refused"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task GetModel_ReadsTheContextTheServerWasStartedWith()
    {
        var handler = new FakeHandler((r, _) => r.RequestUri!.AbsolutePath == "/props"
            ? FakeHandler.Json("""{"default_generation_settings":{"n_ctx":16384},"total_slots":1}""")
            : FakeHandler.Json("{}", HttpStatusCode.NotFound));
        using var client = Client(handler);

        Assert.Equal(new LlmModelInfo("gemma-4-e2b-it", 16384, 16384, true), await client.GetModelAsync("gemma-4-e2b-it", CancellationToken.None));
    }
}
