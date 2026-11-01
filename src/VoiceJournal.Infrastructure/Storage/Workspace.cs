using System.Security.AccessControl;
using System.Security.Principal;
using VoiceJournal.Core.Workspaces;

namespace VoiceJournal.Infrastructure.Storage;

/// <summary>Why a folder cannot hold a workspace (detail spec 14.1 / 21.1).</summary>
public enum WorkspaceRootIssue
{
    NotAbsolute,
    DevicePath,
    NetworkPath,
    InsideSyncFolder,
    NotFixedDrive,
    NotNtfs,
    ReparsePoint,
    NotADirectory,
}

public sealed class WorkspaceRootException : Exception
{
    public WorkspaceRootException() { }

    public WorkspaceRootException(string message) : base(message) { }

    public WorkspaceRootException(string message, Exception inner) : base(message, inner) { }

    public WorkspaceRootException(IReadOnlyList<WorkspaceRootIssue> issues)
        : base($"The folder cannot be used as a workspace: {string.Join(", ", issues ?? [])}.") => Issues = issues ?? [];

    public IReadOnlyList<WorkspaceRootIssue> Issues { get; } = [];
}

/// <summary>
/// The workspace folder (detail spec 15.1): <c>workspace.json</c>, <c>db/</c> and <c>sessions/</c>.
/// Must be a local NTFS folder outside any sync folder such as OneDrive.
/// </summary>
public sealed class Workspace
{
    private Workspace(string root, WorkspaceInfo info)
    {
        Root = root;
        Info = info;
    }

    public string Root { get; }

    public WorkspaceInfo Info { get; }

    public string DatabasePath => Path.Combine(Root, "db", "voicejournal.db");

    public string SessionsRoot => Path.Combine(Root, "sessions");

    /// <summary><c>%LOCALAPPDATA%\VoiceJournal\Workspace</c>.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceJournal", "Workspace");

    /// <summary>Opens the workspace at <paramref name="root"/>, creating it (with a private ACL) when it does not exist.</summary>
    /// <param name="syncRoots">Sync folders to refuse; defaults to the OneDrive folders of the current user.</param>
    /// <exception cref="WorkspaceRootException">The folder is not acceptable.</exception>
    /// <exception cref="InvalidDataException">workspace.json exists but is invalid.</exception>
    public static Workspace OpenOrCreate(string root, string? name = null, TimeProvider? time = null, IReadOnlyList<string>? syncRoots = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var issues = WorkspaceRootPolicy.Check(root, syncRoots);
        if (issues.Count > 0) throw new WorkspaceRootException(issues);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(full))
        {
            Directory.CreateDirectory(full);
            WorkspaceRootPolicy.ApplyPrivateAcl(full);
        }
        Directory.CreateDirectory(Path.Combine(full, "db"));
        Directory.CreateDirectory(Path.Combine(full, "sessions"));

        WorkspaceInfo info;
        if (File.Exists(WorkspaceInfo.PathIn(full)))
        {
            info = WorkspaceInfo.Load(full);
        }
        else
        {
            var now = (time ?? TimeProvider.System).GetUtcNow();
            info = new WorkspaceInfo
            {
                WorkspaceId = Guid.CreateVersion7(now),
                Name = string.IsNullOrWhiteSpace(name) ? "Voice Journal" : name,
                CreatedAtUtc = now,
            };
            info.Save(full);
        }
        return new Workspace(full, info);
    }

    /// <summary>Path of a folder inside the workspace relative to the root, with forward slashes.</summary>
    /// <exception cref="ArgumentException">The folder is outside the workspace.</exception>
    public string RelativePathOf(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var relative = Path.GetRelativePath(Root, full);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException($"'{directory}' is not inside the workspace.", nameof(directory));
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}

/// <summary>Checks and protections for the workspace folder.</summary>
public static class WorkspaceRootPolicy
{
    private static readonly string[] SyncFolderVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    /// <summary>OneDrive folders of the current user, from the variables the OneDrive client sets.</summary>
    public static IReadOnlyList<string> DefaultSyncRoots() =>
        [.. SyncFolderVariables
            .Select(Environment.GetEnvironmentVariable)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.TrimEndingDirectorySeparator(Path.GetFullPath(v!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    public static IReadOnlyList<WorkspaceRootIssue> Check(string root, IReadOnlyList<string>? syncRoots = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.StartsWith(@"\\?\", StringComparison.Ordinal) || root.StartsWith(@"\\.\", StringComparison.Ordinal)) return [WorkspaceRootIssue.DevicePath];
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return [WorkspaceRootIssue.NetworkPath];
        if (!Path.IsPathFullyQualified(root)) return [WorkspaceRootIssue.NotAbsolute];

        var issues = new List<WorkspaceRootIssue>();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var sync in syncRoots ?? DefaultSyncRoots())
        {
            if (IsSameOrUnder(full, sync))
            {
                issues.Add(WorkspaceRootIssue.InsideSyncFolder);
                break;
            }
        }

        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType == DriveType.Network) issues.Add(WorkspaceRootIssue.NetworkPath);
        else if (drive.DriveType != DriveType.Fixed) issues.Add(WorkspaceRootIssue.NotFixedDrive);
        else if (drive.IsReady && !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) issues.Add(WorkspaceRootIssue.NotNtfs);

        if (File.Exists(full)) issues.Add(WorkspaceRootIssue.NotADirectory);
        // A junction or symlink anywhere on the way could redirect the data elsewhere (e.g. into a sync folder).
        for (var dir = new DirectoryInfo(full); dir.Parent is not null; dir = dir.Parent)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                issues.Add(WorkspaceRootIssue.ReparsePoint);
                break;
            }
        }
        return issues;
    }

    /// <summary>
    /// Restricts a newly created folder to the current user, SYSTEM and Administrators, without inheriting
    /// the parent's entries (detail spec 21.1: no shared read/write by default).
    /// </summary>
    public static void ApplyPrivateAcl(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using var identity = WindowsIdentity.GetCurrent();
        var principals = new[]
        {
            identity.User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        foreach (var sid in principals)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
