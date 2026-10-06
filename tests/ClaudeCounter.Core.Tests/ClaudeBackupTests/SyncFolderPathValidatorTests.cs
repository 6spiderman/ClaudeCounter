using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

/// <summary>
/// S14b: SyncFolderPathValidator.Validate is the single, load-bearing
/// implementation of the sync-folder path rules - SyncFolderBackend.ValidateFolderPath
/// (exercised by SyncFolderBackendTests.ValidatesRootednessAllowingUncPaths and
/// friends) and SettingsForm.OnSaveBackupSchedule (exercised by
/// SettingsFormValidationTests, indirectly via SettingsForm's own call site)
/// both now forward to it rather than each carrying their own copy of the
/// three rules. This suite pins those rules directly, independent of either
/// caller.
/// </summary>
public class SyncFolderPathValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sfpv-{Guid.NewGuid():N}");

    public SyncFolderPathValidatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankFolderPathIsRejected(string? folderPath)
    {
        var error = SyncFolderPathValidator.Validate(folderPath!, _root);
        Assert.Equal("Sync folder backup is enabled but no folder is configured.", error);
    }

    [Fact]
    public void RelativeFolderPathIsRejected()
    {
        var error = SyncFolderPathValidator.Validate("relative\\path", _root);
        Assert.NotNull(error);
        Assert.Contains("not a full path", error);
    }

    [WindowsOnlyTheory]
    [InlineData(@"C:\Users\someone\Google Drive\ClaudeBackups")]
    [InlineData(@"\\192.168.1.210\media\claude-backups")]
    public void RootedNonOverlappingPathIsAccepted(string folderPath)
    {
        var error = SyncFolderPathValidator.Validate(folderPath, _root);
        Assert.Null(error);
    }

    [UnixOnlyTheory]
    [InlineData("/home/someone/Dropbox/ClaudeBackups")]
    [InlineData("/mnt/nas/claude-backups")]
    [InlineData("/run/user/1000/gvfs/smb-share:server=nas,share=media/claude")]
    public void RootedNonOverlappingUnixPathIsAccepted(string folderPath)
    {
        var error = SyncFolderPathValidator.Validate(folderPath, _root);
        Assert.Null(error);
    }

    [Fact]
    public void ExactSourceRootOverlapIsRejected()
    {
        var error = SyncFolderPathValidator.Validate(_root, _root);
        Assert.NotNull(error);
        Assert.Contains("overlaps", error);
    }

    [Fact]
    public void FolderNestedInsideSourceRootIsRejected()
    {
        var nested = Path.Combine(_root, "nested");
        var error = SyncFolderPathValidator.Validate(nested, _root);
        Assert.NotNull(error);
        Assert.Contains("overlaps", error);
    }

    [Fact]
    public void SourceRootNestedInsideFolderIsRejected()
    {
        var parent = Path.GetDirectoryName(_root)!;
        var error = SyncFolderPathValidator.Validate(parent, _root);
        Assert.NotNull(error);
        Assert.Contains("overlaps", error);
    }

    [Fact]
    public void SiblingDirectoryWithSharedPrefixIsNotTreatedAsOverlapping()
    {
        // "_root-evil" shares a string prefix with _root but is a distinct
        // sibling directory - proves Overlaps is separator-aware, not a naive
        // StartsWith.
        var sibling = _root + "-evil";
        var error = SyncFolderPathValidator.Validate(sibling, _root);
        Assert.Null(error);
    }
}
