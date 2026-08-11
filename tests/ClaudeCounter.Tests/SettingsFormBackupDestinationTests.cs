using ClaudeBackup;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// S16: SettingsForm.InitialDestinationIndex is the pure, testable core
/// behind the Backup tab's "Back up to" selector opening on whichever
/// destination matches the loaded BackupConfig - public and static (no Form)
/// for the same reason as SettingsForm.HasEmbeddedCredential/HasLeadingDash:
/// this project's tests never construct a Form except via the dedicated STA
/// smoke-test helper (SettingsFormSmokeTests).
/// </summary>
public class SettingsFormBackupDestinationTests
{
    [Fact]
    public void GithubEnabledOpensOnGithub()
    {
        var config = new BackupConfig
        {
            Github = new GitTarget { Enabled = true },
            Drive = new DriveTarget { Enabled = true, Transport = DriveTransport.SyncFolder, SyncProvider = SyncProvider.Nas },
        };
        Assert.Equal(0, SettingsForm.InitialDestinationIndex(config));
    }

    [Fact]
    public void NeitherDestinationEnabledFallsBackToGithub()
    {
        Assert.Equal(0, SettingsForm.InitialDestinationIndex(new BackupConfig()));
    }

    [Fact]
    public void GithubDisabledAndDriveEnabledOpensOnDrive()
    {
        var config = new BackupConfig
        {
            Github = new GitTarget { Enabled = false },
            Drive = new DriveTarget { Enabled = true, Transport = DriveTransport.Rclone },
        };
        Assert.NotEqual(0, SettingsForm.InitialDestinationIndex(config));
    }

    [Fact]
    public void RcloneTransportOpensOnTheRcloneOption()
    {
        var config = new BackupConfig { Drive = new DriveTarget { Enabled = true, Transport = DriveTransport.Rclone } };
        Assert.Equal(5, SettingsForm.InitialDestinationIndex(config));
    }

    [Theory]
    [InlineData(SyncProvider.GoogleDrive, 1)]
    [InlineData(SyncProvider.OneDrive, 2)]
    [InlineData(SyncProvider.Dropbox, 3)]
    [InlineData(SyncProvider.Nas, 4)]
    public void SyncFolderTransportOpensOnItsStoredProvider(SyncProvider provider, int expectedIndex)
    {
        var config = new BackupConfig
        {
            Drive = new DriveTarget
            {
                Enabled = true,
                Transport = DriveTransport.SyncFolder,
                SyncProvider = provider,
                // A path that would infer differently if SyncProvider were
                // ignored - proves the stored SyncProvider wins over
                // inference when it is already known.
                FolderPath = @"D:\SomeCustomBackupFolder",
            },
        };
        Assert.Equal(expectedIndex, SettingsForm.InitialDestinationIndex(config));
    }

    // The legacy-healing case: a backup.json with Transport=SyncFolder but
    // SyncProvider still Other (either the pre-S16 build, or genuinely
    // unset) - the display falls back to inferring from FolderPath rather
    // than showing a meaningless option.
    [Fact]
    public void SyncFolderTransportWithUnknownProviderInfersNasFromAUncPath()
    {
        var config = new BackupConfig
        {
            Drive = new DriveTarget
            {
                Enabled = true,
                Transport = DriveTransport.SyncFolder,
                SyncProvider = SyncProvider.Other,
                FolderPath = @"\\192.168.1.210\media",
            },
        };
        Assert.Equal(4, SettingsForm.InitialDestinationIndex(config)); // NAS / network share
    }

    [Fact]
    public void SyncFolderTransportWithUnknownProviderInfersOneDriveFromAnOneDrivePath()
    {
        var config = new BackupConfig
        {
            Drive = new DriveTarget
            {
                Enabled = true,
                Transport = DriveTransport.SyncFolder,
                SyncProvider = SyncProvider.Other,
                FolderPath = @"D:\OneDrive\OneDrive - Go2Cloud (PTY) LTD",
            },
        };
        Assert.Equal(2, SettingsForm.InitialDestinationIndex(config)); // OneDrive
    }

    // Genuinely unrecognisable path: falls back to the Google Drive slot
    // rather than inventing a sixth "Custom folder" item - see
    // InitialDestinationIndex's own doc comment for why.
    [Fact]
    public void SyncFolderTransportWithUnrecognisedPathFallsBackToGoogleDriveSlot()
    {
        var config = new BackupConfig
        {
            Drive = new DriveTarget
            {
                Enabled = true,
                Transport = DriveTransport.SyncFolder,
                SyncProvider = SyncProvider.Other,
                FolderPath = @"D:\SomeCustomBackupFolder",
            },
        };
        Assert.Equal(1, SettingsForm.InitialDestinationIndex(config)); // Google Drive (sync folder)
    }
}
