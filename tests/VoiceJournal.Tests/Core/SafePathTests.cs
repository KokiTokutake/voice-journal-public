using VoiceJournal.Core.IO;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core;

/// <summary>21.1: paths built from stored or imported text stay plain relative paths under their root.</summary>
public sealed class SafePathTests
{
    [Theory]
    [InlineData("management/issues.md")]
    [InlineData(@"transactions\01a1b962-4d78-7fe8-86e5-b85210a054f2\backup")]
    [InlineData("imports/x/original.md")]
    [InlineData("management/CONSOLE.md")]
    public void PlainRelativePaths_AreAccepted(string path)
    {
        Assert.Null(SafePath.Problem(path));
        using var dir = new TempDirectory();
        Assert.StartsWith(dir.Path, SafePath.Under(dir.Path, path), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside.md")]
    [InlineData(@"management\..\..\outside.md")]
    [InlineData("./issues.md")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\\?\C:\x.md")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\server\share\x.md")]
    [InlineData("management/issues.md:hidden")] // alternate data stream
    [InlineData("C:issues.md")]
    [InlineData("management/CON")]
    [InlineData("management/nul.md")]
    [InlineData("COM1.txt")]
    [InlineData("management/issues.md.")]
    [InlineData("management/issues ")]
    [InlineData("management//issues.md")]
    [InlineData("management/is*sues.md")]
    [InlineData("management/is\u0001sues.md")]
    public void UnsafePaths_AreRefused(string path)
    {
        Assert.NotNull(SafePath.Problem(path));
        using var dir = new TempDirectory();
        Assert.Throws<UnauthorizedAccessException>(() => SafePath.Under(dir.Path, path));
    }

    [Theory]
    [InlineData(@"C:\docs\tasks.md", ".md")]
    [InlineData(@"C:\docs\tasks.markdown", ".markdown")]
    [InlineData(@"C:\docs\tasks.md:evil", ".md")]
    [InlineData(@"C:\docs\tasks", ".md")]
    [InlineData(@"C:\docs\tasks.m d", ".md")]
    public void AnExtensionReusedInAFileName_IsLettersAndDigitsOnly(string path, string expected) =>
        Assert.Equal(expected, SafePath.Extension(path, ".md"));

    [Fact]
    public void TheManagementStore_RefusesAStoredPathOutsideTheRules()
    {
        using var dir = new TempDirectory();
        var workspace = Workspace.OpenOrCreate(dir.File("Workspace"), syncRoots: []);
        var db = VoiceJournalDatabase.Open(workspace.DatabasePath);
        var store = new ManagementStore(workspace, db);

        Assert.EndsWith(Path.Combine("management", "issues.md"), store.FullPath(ManagementStore.IssuesPath), StringComparison.Ordinal);
        Assert.Throws<UnauthorizedAccessException>(() => store.FullPath("management/issues.md:x"));
        Assert.Throws<UnauthorizedAccessException>(() => store.FullPath("../other/issues.md"));
        Assert.Throws<UnauthorizedAccessException>(() => ManagedFileGuard.CheckLocation(Path.Combine(workspace.Root, "management", "NUL.md"), workspace.Root));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
