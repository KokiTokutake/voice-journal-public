using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using VoiceJournal.Core.Workspaces;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Storage;

public sealed class WorkspaceTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-ws-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void OpenOrCreate_CreatesTheLayout_AndKeepsItsIdentity()
    {
        var root = _dir.File("Workspace");
        var clock = new ManualTimeProvider();

        var created = Workspace.OpenOrCreate(root, "テスト", clock, syncRoots: []);
        Assert.True(File.Exists(Path.Combine(root, WorkspaceInfo.FileName)));
        Assert.True(Directory.Exists(Path.Combine(root, "db")));
        Assert.True(Directory.Exists(Path.Combine(root, "sessions")));
        Assert.Equal("テスト", created.Info.Name);
        Assert.Equal(clock.GetUtcNow(), created.Info.CreatedAtUtc);
        Assert.Equal(Path.Combine(root, "db", "voicejournal.db"), created.DatabasePath);

        var reopened = Workspace.OpenOrCreate(root + Path.DirectorySeparatorChar, "別名", syncRoots: []);
        Assert.Equal(created.Info.WorkspaceId, reopened.Info.WorkspaceId);
        Assert.Equal("テスト", reopened.Info.Name); // the stored name wins
        Assert.Equal(created.Root, reopened.Root);
    }

    [Fact]
    public void NewRoot_GetsAPrivateAcl()
    {
        var root = _dir.File("Private");
        Workspace.OpenOrCreate(root, syncRoots: []);

        var security = new DirectoryInfo(root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected); // nothing inherited from the parent
        var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToHashSet();
        using var me = WindowsIdentity.GetCurrent();
        var expected = new HashSet<SecurityIdentifier>
        {
            me.User!,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        Assert.Equal(expected, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), allowed);
    }

    [Fact]
    public void FolderInsideASyncRoot_IsRefused() // detail spec 14.1: never under OneDrive
    {
        var sync = _dir.File("OneDrive");
        Directory.CreateDirectory(sync);

        var ex = Assert.Throws<WorkspaceRootException>(() => Workspace.OpenOrCreate(Path.Combine(sync, "Docs", "Workspace"), syncRoots: [sync]));
        Assert.Contains(WorkspaceRootIssue.InsideSyncFolder, ex.Issues);
        Assert.False(Directory.Exists(Path.Combine(sync, "Docs")));
        Assert.Contains(WorkspaceRootIssue.InsideSyncFolder, WorkspaceRootPolicy.Check(sync, [sync + Path.DirectorySeparatorChar]));
        Assert.Empty(WorkspaceRootPolicy.Check(_dir.File("OneDriveBackup"), [sync])); // a sibling with a similar name is fine
    }

    [Fact]
    public void TheRealOneDriveFolder_IsRefusedByDefault()
    {
        var oneDrive = System.Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrEmpty(oneDrive)) return; // OneDrive not set up on this machine
        Assert.Contains(WorkspaceRootIssue.InsideSyncFolder, WorkspaceRootPolicy.Check(Path.Combine(oneDrive, "VoiceJournal")));
    }

    [Theory]
    [InlineData("relative\\Workspace", WorkspaceRootIssue.NotAbsolute)]
    [InlineData("C:Workspace", WorkspaceRootIssue.NotAbsolute)]
    [InlineData(@"\\server\share\Workspace", WorkspaceRootIssue.NetworkPath)]
    [InlineData(@"\\?\C:\Workspace", WorkspaceRootIssue.DevicePath)]
    [InlineData(@"\\.\PhysicalDrive0", WorkspaceRootIssue.DevicePath)]
    public void UnsafePaths_AreRefused(string root, WorkspaceRootIssue expected)
    {
        Assert.Equal([expected], WorkspaceRootPolicy.Check(root, []));
    }

    [Fact]
    public void PathThroughAJunction_IsRefused()
    {
        var target = _dir.File("target");
        var link = _dir.File("link");
        Directory.CreateDirectory(target);
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        Assert.Contains(WorkspaceRootIssue.ReparsePoint, WorkspaceRootPolicy.Check(Path.Combine(link, "Workspace"), []));
        Directory.Delete(link); // removes the junction only
    }

    [Fact]
    public void AFile_IsNotAWorkspace()
    {
        var file = _dir.File("file.txt");
        File.WriteAllText(file, "x");
        Assert.Contains(WorkspaceRootIssue.NotADirectory, WorkspaceRootPolicy.Check(file, []));
    }

    [Fact]
    public void BrokenWorkspaceJson_IsReported()
    {
        var root = _dir.File("Broken");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, WorkspaceInfo.FileName), "{ not json");

        Assert.Throws<InvalidDataException>(() => Workspace.OpenOrCreate(root, syncRoots: []));
    }

    [Fact]
    public void RelativePathOf_UsesForwardSlashes_AndRejectsOutsideFolders()
    {
        var ws = Workspace.OpenOrCreate(_dir.File("W"), syncRoots: []);
        Assert.Equal("sessions/2026/11/abc", ws.RelativePathOf(Path.Combine(ws.Root, "sessions", "2026", "11", "abc")));
        Assert.Throws<ArgumentException>(() => ws.RelativePathOf(_dir.File("elsewhere")));
        Assert.Throws<ArgumentException>(() => ws.RelativePathOf(ws.Root));
    }
}
