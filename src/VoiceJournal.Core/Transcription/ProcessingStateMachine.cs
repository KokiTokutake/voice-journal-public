namespace VoiceJournal.Core.Transcription;

/// <summary>
/// Post-processing state of a session (detail spec 7): Queued → Normalizing → Transcribing → Transcribed, then
/// (when a record is requested) Organizing → Validating → ReadyForReview. Exceptions: Failed / Cancelled, and
/// NotAnalyzable when the transcript has nothing to organize (16.1: no analysis is made up).
/// </summary>
public enum ProcessingState
{
    /// <summary>Nothing requested yet.</summary>
    None,
    Queued,
    Normalizing,
    Transcribing,
    Transcribed,
    Failed,
    Cancelled,
    Organizing,
    Validating,
    ReadyForReview,
    NotAnalyzable,
}

public static class ProcessingStateMachine
{
    private static readonly Dictionary<ProcessingState, ProcessingState[]> Allowed = new()
    {
        [ProcessingState.None] = [ProcessingState.Queued],
        [ProcessingState.Queued] = [ProcessingState.Normalizing, ProcessingState.Organizing, ProcessingState.Cancelled, ProcessingState.Failed],
        [ProcessingState.Normalizing] = [ProcessingState.Transcribing, ProcessingState.Cancelled, ProcessingState.Failed, ProcessingState.Queued],
        // Back to Queued when the app restarts mid-job or recording preempts it: the job resumes from verified pieces.
        [ProcessingState.Transcribing] = [ProcessingState.Transcribed, ProcessingState.Cancelled, ProcessingState.Failed, ProcessingState.Queued],
        // Reprocessing (other settings, AT-26) or retrying starts a new job; a record request continues to Organizing.
        [ProcessingState.Transcribed] = [ProcessingState.Queued, ProcessingState.Organizing],
        [ProcessingState.Organizing] = [ProcessingState.Validating, ProcessingState.NotAnalyzable, ProcessingState.Cancelled, ProcessingState.Failed, ProcessingState.Queued],
        [ProcessingState.Validating] = [ProcessingState.ReadyForReview, ProcessingState.Cancelled, ProcessingState.Failed, ProcessingState.Queued],
        // Regenerating (other model or prompt, edited transcript) or re-transcribing starts a new job.
        [ProcessingState.ReadyForReview] = [ProcessingState.Queued, ProcessingState.Organizing],
        [ProcessingState.NotAnalyzable] = [ProcessingState.Queued, ProcessingState.Organizing],
        // Retrying only the organizing step keeps the transcript (LLM-001).
        [ProcessingState.Failed] = [ProcessingState.Queued, ProcessingState.Organizing],
        [ProcessingState.Cancelled] = [ProcessingState.Queued, ProcessingState.Organizing],
    };

    public static bool CanTransition(ProcessingState from, ProcessingState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static bool IsActive(ProcessingState state) =>
        state is ProcessingState.Queued or ProcessingState.Normalizing or ProcessingState.Transcribing
            or ProcessingState.Organizing or ProcessingState.Validating;

    /// <summary>Returns <paramref name="to"/> or throws when the transition is not allowed.</summary>
    public static ProcessingState Transition(ProcessingState from, ProcessingState to) =>
        CanTransition(from, to) ? to : throw new InvalidOperationException($"Processing state cannot change from {from} to {to}.");
}
