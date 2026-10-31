namespace VoiceJournal.Core.Capture;

/// <summary>
/// Allowed capture-state transitions (detail spec 7):
/// Ready → Recording → Stopping → Finalized, with Partial / Interrupted / Failed as exceptions.
/// </summary>
public static class CaptureStateMachine
{
    private static readonly Dictionary<CaptureState, CaptureState[]> Allowed = new()
    {
        [CaptureState.Ready] = [CaptureState.Recording, CaptureState.Failed],
        [CaptureState.Recording] = [CaptureState.Partial, CaptureState.Stopping, CaptureState.Interrupted, CaptureState.Failed],
        [CaptureState.Partial] = [CaptureState.Recording, CaptureState.Stopping, CaptureState.Interrupted, CaptureState.Failed],
        [CaptureState.Stopping] = [CaptureState.Finalized, CaptureState.Interrupted, CaptureState.Failed],
        // After an interruption the user chooses to resume (new chunks) or to finish.
        [CaptureState.Interrupted] = [CaptureState.Recording, CaptureState.Finalized],
        [CaptureState.Finalized] = [],
        [CaptureState.Failed] = [],
    };

    public static bool CanTransition(CaptureState from, CaptureState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static bool IsTerminal(CaptureState state) => Allowed[state].Length == 0;

    /// <summary>Returns <paramref name="to"/> or throws when the transition is not allowed.</summary>
    public static CaptureState Transition(CaptureState from, CaptureState to) =>
        CanTransition(from, to) ? to : throw new InvalidOperationException($"Capture state cannot change from {from} to {to}.");
}
