using System.Text;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Storage;

public sealed class DiagnosticsReportTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-diag-");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    [Fact]
    public void TheReport_HasVersionsCountsAndTools_ButNoTextOfTranscriptsOrItems()
    {
        var workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        var db = VoiceJournalDatabase.Open(workspace.DatabasePath);
        var (dir, id) = JobSession.Create(Path.Combine(workspace.SessionsRoot, "2026", "11"));
        new SessionIndex(workspace, db, TimeProvider.System).Upsert(dir);
        new TranscriptStore(dir).Save(T.Raw(id, 1, T.Seg(0, 4_000, "秘密の会議の内容です")));
        new TranscriptIndex(workspace, db).UpsertRevisions(dir); // the segment text goes into the index
        new ManagementStore(workspace, db).Files();
        using (var c = db.OpenConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO items (id, workspace_id, display_id, kind, project, title, description, status, revision, created_at, updated_at)
                VALUES ('{Guid.NewGuid():D}', '{workspace.Info.WorkspaceId:D}', 'TSK-000001', 'task', '未分類', '秘密の項目', '秘密の説明', 'todo', 1,
                        '2026-11-01T00:00:00.0000000Z', '2026-11-01T00:00:00.0000000Z')
                """;
            cmd.ExecuteNonQuery();
        }
        var appRoot = Directory.CreateDirectory(_dir.File("App")).FullName;
        Directory.CreateDirectory(Path.Combine(appRoot, "config"));
        var exe = _dir.File("llama-server.exe");
        File.WriteAllBytes(exe, [1, 2, 3]);
        File.WriteAllText(Path.Combine(appRoot, "config", "llm-tools.json"),
            "{\"engine\": {\"path\": " + System.Text.Json.JsonSerializer.Serialize(exe) + ", \"sha256\": \"" + new string('a', 64) + "\"}}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(appRoot, "config", "stt.json"), "{\"model\": \"small\"}", Encoding.UTF8);

        var path = DiagnosticsReport.Write(workspace, db, appRoot, "1.2.3");
        var text = File.ReadAllText(path, Encoding.UTF8);

        Assert.StartsWith(Path.Combine(appRoot, "logs"), path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"app_version\": \"1.2.3\"", text, StringComparison.Ordinal);
        Assert.Contains("\"integrity\": \"ok\"", text, StringComparison.Ordinal);
        Assert.Contains("management", text, StringComparison.Ordinal); // migration 4
        Assert.Contains("\"exists\": true", text, StringComparison.Ordinal);
        Assert.Contains("\"model\": \"small\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("秘密", text, StringComparison.Ordinal); // no transcript, item title or description
    }
}
