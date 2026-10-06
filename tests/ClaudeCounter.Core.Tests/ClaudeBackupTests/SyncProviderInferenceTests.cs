using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

/// <summary>
/// S16: SyncProviderInference.InferFromPath is the pure, testable core
/// behind the Backup tab's display-only healing of a legacy
/// (Transport=SyncFolder, SyncProvider=Other) config - see
/// SettingsForm.InitialDestinationIndex, which is the only production
/// caller. Every case here is a plain string in, enum out - no environment,
/// registry, or filesystem access.
/// </summary>
public class SyncProviderInferenceTests
{
    [Fact]
    public void UncPathInfersNas()
    {
        Assert.Equal(SyncProvider.Nas, SyncProviderInference.InferFromPath(@"\\192.168.1.210\media\ClaudeBackups"));
    }

    [Theory]
    [InlineData(@"D:\OneDrive\OneDrive - Go2Cloud (PTY) LTD")]
    [InlineData(@"C:\Users\someone\OneDrive")]
    [InlineData(@"C:\Users\someone\OneDrive - Personal")]
    public void OneDrivePathInfersOneDrive(string path) =>
        Assert.Equal(SyncProvider.OneDrive, SyncProviderInference.InferFromPath(path));

    [Theory]
    [InlineData(@"C:\Users\someone\Google Drive")]
    [InlineData(@"G:\My Drive")]
    public void GoogleDrivePathInfersGoogleDrive(string path) =>
        Assert.Equal(SyncProvider.GoogleDrive, SyncProviderInference.InferFromPath(path));

    [Fact]
    public void DropboxPathInfersDropbox()
    {
        Assert.Equal(SyncProvider.Dropbox, SyncProviderInference.InferFromPath(@"C:\Users\someone\Dropbox"));
    }

    [Theory]
    [InlineData(@"D:\SomeCustomBackupFolder")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnrecognisedOrBlankPathInfersOther(string? path) =>
        Assert.Equal(SyncProvider.Other, SyncProviderInference.InferFromPath(path));

    // UNC-ness is checked first, per the design brief's own rule of thumb
    // ("UNC means NAS") - a NAS share's folder name could coincidentally
    // contain another provider's marker word.
    [Fact]
    public void UncPathTakesPriorityOverAConflictingSubstring()
    {
        Assert.Equal(SyncProvider.Nas, SyncProviderInference.InferFromPath(@"\\nas\share\OneDrive-Style-Backup"));
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        Assert.Equal(SyncProvider.OneDrive, SyncProviderInference.InferFromPath(@"D:\ONEDRIVE\Backups"));
        Assert.Equal(SyncProvider.Dropbox, SyncProviderInference.InferFromPath(@"D:\DROPBOX\Backups"));
    }
}
