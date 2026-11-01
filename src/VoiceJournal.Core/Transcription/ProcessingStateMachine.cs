namespace VoiceJournal.Core.Transcription;

/// <summary>
/// Post-processing state of a session (detail spec 7). Phase C stops at <see cref="Transcribed"/>; Phase D
/// continues with Organizing → Validating → ReadyForReview.
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
}

public static class ProcessingStateMachine
{
    private static readonly Dictionary<ProcessingState, ProcessingState[]> Allowed = new()
    {
        [ProcessingState.None] = [ProcessingState.Queued],
        [ProcessingState.Queued] = [ProcessingState.Normalizing, ProcessingState.Cancelled, ProcessingState.Failed],
        [ProcessingState.Normalizing] = [ProcessingState.Transcribing, ProcessingState.Cancelled, ProcessingState.Failed, ProcessingState.Queued],
        // Back to Queued when the app restarts mid-job or recording preempts it: the job resumes from verified pieces.
        [ProcessingState.Transcribing] = [ProcessingState.Transcribed, ProcessingState.Cancelled, ProcessingState.Failed, ProcessingState.Queued],
        // Reprocessing (other settings, AT-26) or retrying starts a new job.
        [ProcessingState.Transcribed] = [ProcessingState.Queued],
        [ProcessingState.Failed] = [ProcessingState.Queued],
        [ProcessingState.Cancelled] = [ProcessingState.Queued],
    };

    public static bool CanTransition(ProcessingState from, ProcessingState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static bool IsActive(ProcessingState state) =>
        state is ProcessingState.Queued or ProcessingState.Normalizing or ProcessingState.Transcribing;

    /// <summary>Returns <paramref name="to"/> or throws when the transition is not allowed.</summary>
    public static ProcessingState Transition(ProcessingState from, ProcessingState to) =>
        CanTransition(from, to) ? to : throw new InvalidOperationException($"Processing state cannot change from {from} to {to}.");
}
