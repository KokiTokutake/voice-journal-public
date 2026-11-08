using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.Tests.Infrastructure.Storage;

/// <summary>Runs only with VJ_NETWORK_DRIVE set to a mapped network drive (e.g. <c>net use Z: \\localhost\C$</c>, then <c>Z:</c>).</summary>
public sealed class NetworkDriveFactAttribute : FactAttribute
{
    public NetworkDriveFactAttribute()
    {
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VJ_NETWORK_DRIVE"))) Skip = "Set VJ_NETWORK_DRIVE to a mapped network drive (e.g. Z:) to run.";
    }
}

/// <summary>AT-16 (network): a workspace or a management file on a mapped network drive is refused; nothing is written there.</summary>
public sealed class NetworkDriveTests
{
    private static string Drive => System.Environment.GetEnvironmentVariable("VJ_NETWORK_DRIVE")!.TrimEnd('\\', ':') + @":\";

    /// <summary>A folder that maps to the local temp folder through the share (the share is \\localhost\C$).</summary>
    private static (string ThroughDrive, string Local) Folder()
    {
        var local = Path.Combine(Path.GetTempPath(), "vj-at16-" + Guid.NewGuid().ToString("N"));
        var throughDrive = Drive + local[Path.GetPathRoot(local)!.Length..];
        return (throughDrive, local);
    }

    [NetworkDriveFact]
    public void TheDriveIsANetworkDrive() => Assert.Equal(DriveType.Network, new DriveInfo(Drive).DriveType);

    [NetworkDriveFact]
    public void AWorkspaceOnTheDrive_IsRefused_AndNothingIsCreated()
    {
        var (folder, local) = Folder();

        var error = Assert.Throws<WorkspaceRootException>(() => Workspace.OpenOrCreate(folder, syncRoots: []));

        Assert.Contains(WorkspaceRootIssue.NetworkPath, error.Issues);
        Assert.False(Directory.Exists(local));
    }

    [NetworkDriveFact]
    public void AManagementFileOnTheDrive_IsRefused_BeforeAnythingIsWritten()
    {
        var (folder, local) = Folder();
        Directory.CreateDirectory(Path.Combine(local, "management"));
        try
        {
            var file = Path.Combine(folder, "management", "tasks.md");
            Assert.Throws<UnauthorizedAccessException>(() => ManagedFileGuard.CheckLocation(file, folder));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(local, "management")));
        }
        finally
        {
            Directory.Delete(local, recursive: true);
        }
    }
}
