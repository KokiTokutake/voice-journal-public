using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoiceJournal.Core.IO;

namespace VoiceJournal.Infrastructure.Storage;

public sealed record SnapshotReport(string Directory, int Files, long Bytes);

/// <summary>
/// Snapshot and restore of a whole workspace (detail spec 14.4, AT-20): every file (manifests, original audio, transcripts,
/// records, management files, change plans) with its SHA-256 in <c>hashes.json</c>, and the database through the SQLite
/// Online Backup API (consistent while the app runs; the <c>.db</c> is never copied as a file under WAL). Take it when no
/// recording, apply or job is running. A restore never writes over a workspace: it makes a new one and checks it.
/// </summary>
public static class WorkspaceBackup
{
    public const string BackupsFolder = "backups";
    public const string HashesFile = "hashes.json";
    private const string DatabaseFile = "db/voicejournal.db";

    /// <summary>Folders that are not part of a snapshot: earlier snapshots, the live database (backed up apart) and caches.</summary>
    private static readonly string[] Excluded = [BackupsFolder, "db", "cache"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static SnapshotReport Create(Workspace workspace, VoiceJournalDatabase db, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(db);
        var stamp = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(workspace.Root, BackupsFolder, $"snapshot-{stamp}-{Guid.CreateVersion7():N}"[..41]);
        Directory.CreateDirectory(target);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(workspace.Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workspace.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (Excluded.Any(e => relative.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase))) continue;
            if (relative.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(relative).StartsWith(".vj-", StringComparison.Ordinal)) continue;
            var destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var (hash, length) = CopyWithHash(file, destination);
            hashes[relative] = hash;
            bytes += length;
        }
        var dbCopy = Path.Combine(target, DatabaseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dbCopy)!);
        using (var source = db.OpenConnection())
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbCopy, Pooling = false }.ToString()))
        {
            copy.Open();
            source.BackupDatabase(copy);
        }
        var dbBytes = File.ReadAllBytes(dbCopy);
        hashes[DatabaseFile] = Sha(dbBytes);
        AtomicFile.WriteAllBytes(Path.Combine(target, HashesFile), JsonSerializer.SerializeToUtf8Bytes(hashes, Indented));
        return new SnapshotReport(target, hashes.Count, bytes + dbBytes.LongLength);
    }

    /// <summary>
    /// Checks a snapshot or a restored workspace: every file has the hash recorded, nothing recorded is missing, and the
    /// database agrees with the management files it last wrote. Empty when everything matches.
    /// </summary>
    public static IReadOnlyList<string> Verify(string root, string? hashesPath = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var problems = new List<string>();
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(hashesPath ?? Path.Combine(root, HashesFile))) ?? [];
        foreach (var (relative, hash) in hashes)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) problems.Add($"{relative} がありません");
            else if (ShaOf(path) != hash) problems.Add($"{relative} の内容がバックアップの記録と違います");
        }
        var dbPath = Path.Combine(root, DatabaseFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(dbPath)) return problems;
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using (var check = c.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            if ((string?)check.ExecuteScalar() != "ok") problems.Add("データベースの整合性の検査に失敗しました");
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, last_hash FROM managed_files WHERE last_hash IS NOT NULL";
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var file = Path.Combine(root, r.GetString(0).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) problems.Add($"{r.GetString(0)} がありません（データベースには反映済みとあります）");
                else if (ShaOf(file) != r.GetString(1)) problems.Add($"{r.GetString(0)} がデータベースの最後の反映と違います（外部の編集か、反映の途中）");
            }
        }
        catch (SqliteException)
        {
            // A database from before Phase E has no management tables: nothing to compare.
        }
        return problems;
    }

    /// <summary>The snapshots of a workspace, newest first.</summary>
    public static IReadOnlyList<string> List(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var folder = Path.Combine(workspace.Root, BackupsFolder);
        return Directory.Exists(folder)
            ? [.. Directory.GetDirectories(folder, "snapshot-*").Where(d => File.Exists(Path.Combine(d, HashesFile))).OrderByDescending(d => d, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>A new folder next to the workspace for a restore (never the workspace itself).</summary>
    public static string RestoreTarget(Workspace workspace, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var stamp = (time ?? TimeProvider.System).GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Root));
        return $"{root}-restored-{stamp}";
    }

    /// <summary>Restores a snapshot into a new, empty folder and checks it (AT-20). The current workspace is never written.</summary>
    public static IReadOnlyList<string> Restore(string snapshot, string newRoot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(newRoot);
        if (Directory.Exists(newRoot) && Directory.EnumerateFileSystemEntries(newRoot).Any())
            throw new IOException("復元先のフォルダーが空ではありません。新しいフォルダーを選んでください。");
        var problems = Verify(snapshot);
        if (problems.Count > 0) return problems;
        foreach (var file in Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(snapshot, file);
            if (relative == HashesFile) continue;
            var destination = Path.Combine(newRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
        return Verify(newRoot, Path.Combine(snapshot, HashesFile));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Hash of a file read as a stream (original audio can be gigabytes).</summary>
    private static string ShaOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Copies a file in chunks while hashing it; the source may be read by others meanwhile.</summary>
    private static (string Hash, long Length) CopyWithHash(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long length = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            length += read;
        }
        output.Flush(flushToDisk: true);
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }
}
