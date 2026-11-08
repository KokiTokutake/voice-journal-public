namespace VoiceJournal.App.Services;

/// <summary>Runs work on the UI thread. View models never touch WPF types directly.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread (never runs it inline).</summary>
    void Post(Action action);
}

public enum CloseChoice
{
    StopAndSave,
    Back,
}

public enum ResumeChoice
{
    StartNew,
    Dismiss,
}

/// <summary>Messages and questions shown to the user.</summary>
public interface IDialogService
{
    /// <summary>Recording is running and the window is being closed (detail spec 18.1).</summary>
    CloseChoice AskStopBeforeClose();

    /// <summary>The recording was stopped and saved because the PC went to sleep.</summary>
    ResumeChoice AskAfterSuspend();

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);

    /// <summary>Asks for an existing file to open (null when cancelled).</summary>
    string? PickFile(string title, string filter) => null;
}

/// <summary>Sleep prevention and suspend / resume notifications.</summary>
public interface IPowerService
{
    /// <summary>Keeps the system awake (the display may still turn off) until the result is disposed.</summary>
    IDisposable PreventSleep();

    /// <summary>The system is about to suspend. Raised on a background thread.</summary>
    event EventHandler? Suspending;

    /// <summary>The system resumed. Raised on a background thread.</summary>
    event EventHandler? Resumed;
}

/// <summary>Explicit "open in Explorer" actions (detail spec 21.2: only from a button).</summary>
public interface IShellService
{
    void OpenFolder(string path);
}

/// <summary>Where recordings go and how much space must stay free.</summary>
public sealed record RecordingEnvironment(string WorkspaceRoot, long MinFreeBytes);
