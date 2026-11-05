using VoiceJournal.Core.Analysis;

namespace VoiceJournal.App.Services;

/// <summary>
/// Sends each call to the engine the settings name (docs/phase-d-engine-plan.md): the bundled llama.cpp server, or
/// LM Studio while it is still selectable. A change in the settings applies to the next call.
/// </summary>
public sealed class SelectingLlmClient(Func<LlmEngine> engine, ILlmClient bundled, ILlmClient lmStudio) : ILlmClient
{
    private ILlmClient Current => engine() == LlmEngine.LmStudio ? lmStudio : bundled;

    public Task<LlmModelInfo> GetModelAsync(string modelId, CancellationToken cancellationToken) => Current.GetModelAsync(modelId, cancellationToken);

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) => Current.CompleteAsync(request, cancellationToken);
}
