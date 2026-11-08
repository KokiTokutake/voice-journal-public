using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceJournal.Core.IO;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.Services;

/// <summary>The workspace folder the app opens at start (detail spec 15.1: the user may move it to a local, unsynced folder).</summary>
public interface IWorkspaceLocationStore
{
    /// <summary>The chosen folder, or null for the default (<see cref="Workspace.DefaultRoot"/>).</summary>
    string? Load();

    /// <summary>Saves the folder used from the next start; null goes back to the default.</summary>
    /// <exception cref="IOException">The setting could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The setting could not be written.</exception>
    void Save(string? root);
}

/// <summary><c>%LOCALAPPDATA%\VoiceJournal\config\workspace.json</c>. A missing or unreadable file means the default.</summary>
public sealed class JsonWorkspaceLocationStore(string path) : IWorkspaceLocationStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceJournal", "config", "workspace.json");

    private sealed record Stored([property: JsonPropertyName("root")] string? Root);

    public string? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var root = JsonSerializer.Deserialize<Stored>(AtomicFile.ReadAllBytes(path))?.Root;
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string? root)
    {
        if (root is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new Stored(Path.GetFullPath(root))));
    }
}

/// <summary>Checks a folder chosen as the new workspace (15.1, 14.1): the same rules as at start, plus empty or a workspace.</summary>
public static class WorkspaceChoice
{
    /// <summary>Why the folder cannot be the workspace, or null. <paramref name="syncRoots"/> defaults to the user's OneDrive folders.</summary>
    public static string? Problem(string folder, IReadOnlyList<string>? syncRoots = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var issues = WorkspaceRootPolicy.Check(folder, syncRoots);
        if (issues.Count > 0) return string.Join("、", issues.Select(ViewModels.UserMessages.WorkspaceIssue));
        if (IsWorkspace(folder)) return null;
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return "空のフォルダーか、Voice Journal のワークスペース（workspace.json のあるフォルダー）を選んでください（ほかのファイルと混ざらないようにするため）。";
        return null;
    }

    public static bool IsWorkspace(string folder) => File.Exists(Core.Workspaces.WorkspaceInfo.PathIn(folder));
}
