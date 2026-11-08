using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.AudioProbe;

/// <summary>
/// The backup restore drill (detail spec 14.4, AT-20, Phase G): takes a snapshot of a workspace, restores it into a
/// new folder next to it and checks it (every hash, the database, the last applied hashes). The workspace itself is
/// only read (the snapshot is added under its <c>backups\</c>). Close the app first.
/// </summary>
internal static class RestoreDrillCommand
{
    public static int Run(string[] args, JsonSerializerOptions json)
    {
        var root = Path.GetFullPath(args[1]);
        var workspace = Workspace.OpenOrCreate(root);
        var db = VoiceJournalDatabase.Open(workspace.DatabasePath);
        var snapshot = WorkspaceBackup.Create(workspace, db);
        var target = WorkspaceBackup.RestoreTarget(workspace);
        var problems = WorkspaceBackup.Restore(snapshot.Directory, target);
        SqliteConnection.ClearAllPools();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            workspace = root,
            snapshot = snapshot.Directory,
            files = snapshot.Files,
            bytes = snapshot.Bytes,
            restored_to = target,
            problems,
        }, json));
        return problems.Count == 0 ? 0 : 3;
    }
}
