using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Infrastructure.Storage;

public sealed record RecoveredSession(string RelativePath, SessionRecoveryResult Result);

public sealed record StartupReport(IndexRebuildReport Index, IReadOnlyList<RecoveredSession> Recovered, IReadOnlyList<IndexFailure> RecoveryFailures);

/// <summary>
/// Runs at app start (detail spec 19 REC-001): brings the index up to date from the manifests, recovers
/// sessions left in Recording / Stopping by a crash (<c>.partial</c> → <c>.recovered.wav</c>, originals kept),
/// and re-indexes them. Must run before this app instance records anything, and only one instance may run.
/// </summary>
public static class WorkspaceStartup
{
    public static StartupReport Run(Workspace workspace, ISessionIndex index, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(index);
        var report = index.Rebuild();
        var recovered = new List<RecoveredSession>();
        var failures = new List<IndexFailure>();
        foreach (var relative in report.NeedsRecovery)
        {
            var directory = Path.Combine(workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var result = SessionRecovery.Recover(directory, time);
                index.Upsert(directory);
                recovered.Add(new RecoveredSession(relative, result));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failures.Add(new IndexFailure(relative, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return new StartupReport(report, recovered, failures);
    }
}
