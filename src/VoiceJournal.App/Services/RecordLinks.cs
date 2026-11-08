using VoiceJournal.Core.Records;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.Services;

/// <summary>
/// 14.2-8: the record's candidate list says which candidates became which management items ("反映済み: TSK-000001").
/// Records are rendered with the links, and re-rendered after a batch is committed. A failure here never undoes or
/// repeats the apply.
/// </summary>
public static class RecordLinks
{
    public static string Render(ManagementStore? store, string sessionDirectory, RecordDocument record, bool approved)
    {
        ArgumentNullException.ThrowIfNull(record);
        var applied = store is not null && record.Provenance.AnalysisRevision is { } analysis ? store.AppliedDisplayIds(record.SessionId, analysis) : null;
        return RecordMarkdown.RenderFor(sessionDirectory, record, approved, applied is { Count: > 0 } ? applied : null);
    }

    /// <summary>Re-renders the records of the sessions a batch applied candidates from. Returns a note for the user, or null.</summary>
    public static string? RefreshAfter(ManagementStore store, Guid batchId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var skipped = new List<string>();
        var failed = new List<string>();
        foreach (var session in store.SessionsAppliedIn(batchId))
        {
            var name = session.Title ?? session.StartedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                var result = new RecordStore(session.Directory).RefreshRenderings((d, approved) => Render(store, session.Directory, d, approved));
                if (result.ApprovedSkipped) skipped.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failed.Add(name);
            }
        }
        var notes = new List<string>();
        if (skipped.Count > 0)
            notes.Add($"「{string.Join("」「", skipped)}」の record.md はアプリの外で編集されているため、反映済みの印を書き込みませんでした。");
        if (failed.Count > 0)
            notes.Add($"「{string.Join("」「", failed)}」の記録に反映済みの印を書き込めませんでした（反映そのものは完了しています）。");
        return notes.Count > 0 ? string.Join("\n", notes) : null;
    }
}
