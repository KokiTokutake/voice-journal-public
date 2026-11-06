namespace VoiceJournal.Core.Analysis;

public enum LlmRole
{
    System,
    User,
    Assistant,
}

public sealed record LlmMessage(LlmRole Role, string Content);

/// <summary>One chat completion with a strict JSON Schema answer (detail spec 9.1). No tools are ever sent.</summary>
public sealed record LlmRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    public required string SchemaName { get; init; }

    /// <summary>The JSON Schema text for <c>response_format.json_schema.schema</c>.</summary>
    public required string Schema { get; init; }

    public double Temperature { get; init; } = 0.1;

    public required int MaxTokens { get; init; }
}

public sealed record LlmResponse
{
    public required string Content { get; init; }

    /// <summary><c>stop</c> when the answer is complete; <c>length</c> means it was cut (a failure, 9.2).</summary>
    public required string? FinishReason { get; init; }

    public required int PromptTokens { get; init; }

    public required int CompletionTokens { get; init; }

    public required TimeSpan Elapsed { get; init; }
}

/// <summary>
/// The model the server reports, and the context length it is loaded with when the server says so.
/// <paramref name="Loaded"/> is false when the server lists the model but has not loaded it (on-demand loading
/// or an unload after idle); null when the server does not say.
/// </summary>
public sealed record LlmModelInfo(string Id, int? LoadedContextLength, int? MaxContextLength, bool? Loaded = null);

/// <summary>LLM-001: the server is not running, not reachable on loopback, or the model is not loaded.</summary>
public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException() { }

    public LlmUnavailableException(string message) : base(message) { }

    public LlmUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>LLM-002: the request does not fit (context exceeded, out of memory).</summary>
public sealed class LlmContextException : Exception
{
    public LlmContextException() { }

    public LlmContextException(string message) : base(message) { }

    public LlmContextException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The server answered in a way the app does not accept (HTTP error, redirect, missing content).</summary>
public sealed class LlmProtocolException : Exception
{
    public LlmProtocolException() { }

    public LlmProtocolException(string message) : base(message) { }

    public LlmProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The local LLM server (the bundled llama-server, OpenAI-compatible API on loopback only). Implementations never log prompts
/// or answers, never follow redirects and never use a proxy (detail spec 9.1, 21.3).
/// </summary>
public interface ILlmClient
{
    /// <exception cref="LlmUnavailableException">No server, or <paramref name="modelId"/> is not loaded.</exception>
    Task<LlmModelInfo> GetModelAsync(string modelId, CancellationToken cancellationToken);

    /// <exception cref="LlmUnavailableException">LLM-001.</exception>
    /// <exception cref="LlmContextException">LLM-002.</exception>
    /// <exception cref="LlmProtocolException">An HTTP or protocol error.</exception>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>Validates a serialized analysis against <see cref="AnalysisSchema"/> (Draft 2020-12, formats asserted).</summary>
public interface IAnalysisSchemaValidator
{
    /// <summary>Empty when valid; otherwise locations and messages (no content).</summary>
    IReadOnlyList<string> Validate(string json);
}
