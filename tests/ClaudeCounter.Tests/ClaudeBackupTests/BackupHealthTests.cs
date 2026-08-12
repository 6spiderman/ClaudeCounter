// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupHealthTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    // S17a: BackupHealth.Evaluate now iterates BackupConfig.Destinations
    // directly (not the Github/Drive shim, which only syncs into
    // Destinations on Save()/Load() - see BackupConfig.Github's own doc
    // comment) - so tests build destinations directly too.
    private static BackupConfig Config(bool githubEnabled = false, bool driveEnabled = false, DriveTransport driveTransport = DriveTransport.Rclone) => new()
    {
        Destinations = new()
        {
            new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = githubEnabled, RemoteUrl = "url", Branch = "main" },
            new BackupDestination
            {
                Id = "drive",
                Name = driveTransport == DriveTransport.SyncFolder ? "Sync folder" : "Google Drive (rclone)",
                Kind = driveTransport == DriveTransport.SyncFolder ? DestinationKind.SyncFolder : DestinationKind.Rclone,
                Enabled = driveEnabled,
                RcloneRemote = "remote",
                FolderPath = driveTransport == DriveTransport.SyncFolder ? @"D:\SyncFolder" : "",
            },
        },
    };

    private static BackupStatus Status(DestinationStatus? github = null, DestinationStatus? drive = null)
    {
        var destinations = new Dictionary<string, DestinationStatus>();
        if (github is not null) destinations["github"] = github;
        if (drive is not null) destinations["drive"] = drive;
        return new BackupStatus { Destinations = destinations };
    }

    private static DestinationStatus Succeeded(DateTimeOffset successUtc) => new()
    {
        LastAttemptUtc = successUtc,
        LastSuccessUtc = successUtc,
        LastOutcome = BackupOutcome.Success,
        WasEnabled = true,
    };

    private static DestinationStatus Failed(DateTimeOffset attemptUtc, DateTimeOffset? lastSuccessUtc = null) => new()
    {
        LastAttemptUtc = attemptUtc,
        LastSuccessUtc = lastSuccessUtc,
        LastOutcome = BackupOutcome.Failed,
        LastMessage = "backend failed",
        WasEnabled = true,
    };

    // NotConfigured must produce silence - never nag a user who does not use
    // backup, regardless of what junk might be sitting in the status file.
    [Fact]
    public void NoDestinationEnabledIsNotConfiguredEvenWithStaleStatusData()
    {
        var status = Status(github: Failed(Now.AddDays(-100)));
        var result = BackupHealth.Evaluate(status, Config(), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.NotConfigured, result.State);
        Assert.Empty(result.Destinations);
    }

    [Fact]
    public void ConfiguredButNeverAttemptedIsNeverRun()
    {
        var status = new BackupStatus(); // fresh - never run
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.NeverRun, result.State);
    }

    [Fact]
    public void RecentSuccessWithinThresholdIsHealthy()
    {
        var status = Status(github: Succeeded(Now.AddHours(-1)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    [Fact]
    public void BothDestinationsRecentlySucceededIsHealthy()
    {
        var status = Status(github: Succeeded(Now.AddHours(-1)), drive: Succeeded(Now.AddHours(-2)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    [Fact]
    public void LastAttemptFailedIsFailed()
    {
        var status = Status(github: Failed(Now.AddMinutes(-5), lastSuccessUtc: Now.AddHours(-1)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Failed, result.State);
    }

    [Fact]
    public void SuccessOlderThanThresholdIsStale()
    {
        var status = Status(github: Succeeded(Now.AddDays(-10)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Stale, result.State);
    }

    // Failed outranks Stale when both apply: GitHub is failing right now,
    // Drive is merely stale - the overall state must be Failed, the more
    // actionable signal.
    [Fact]
    public void FailedOutranksStaleWhenBothApply()
    {
        var status = Status(
            github: Failed(Now.AddMinutes(-1), lastSuccessUtc: Now.AddDays(-1)),
            drive: Succeeded(Now.AddDays(-30)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Failed, result.State);
    }

    // 0 means never warn about staleness - a success from a year ago is
    // still Healthy when the threshold is 0.
    [Fact]
    public void ZeroThresholdMeansNeverWarnAboutStaleness()
    {
        var status = Status(github: Succeeded(Now.AddDays(-400)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 0);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    // Staleness boundary: exactly at the threshold is still Healthy; one
    // second past it is Stale; one second short of it is Healthy.
    [Fact]
    public void ExactlyAtThresholdIsHealthy()
    {
        var status = Status(github: Succeeded(Now.AddDays(-3)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    [Fact]
    public void OneSecondPastThresholdIsStale()
    {
        var status = Status(github: Succeeded(Now.AddDays(-3).AddSeconds(-1)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Stale, result.State);
    }

    [Fact]
    public void OneSecondShortOfThresholdIsHealthy()
    {
        var status = Status(github: Succeeded(Now.AddDays(-3).AddSeconds(1)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    // A destination the user has since disabled must not drag health down,
    // even if its last recorded status was a failure.
    [Fact]
    public void DisabledDestinationIsIgnoredEvenIfItsLastStatusWasFailed()
    {
        var status = Status(github: Succeeded(Now.AddHours(-1)), drive: Failed(Now.AddDays(-50)));
        // Drive disabled in the CURRENT config - only GitHub is relevant.
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: false), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
        Assert.Single(result.Destinations);
        Assert.Equal("GitHub", result.Destinations[0].Name);
    }

    // Per-destination detail must be retained so a caller can say which
    // destination is broken and when it last worked.
    [Fact]
    public void PerDestinationDetailNamesWhichDestinationFailed()
    {
        var status = Status(github: Failed(Now.AddMinutes(-1)), drive: Succeeded(Now.AddHours(-1)));
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: true), Now, staleAfterDays: 3);

        var github = result.Destinations.Single(d => d.Name == "GitHub");
        var drive = result.Destinations.Single(d => d.Name == "Google Drive (rclone)");
        Assert.Equal(DestinationHealthState.Failed, github.State);
        Assert.Equal(DestinationHealthState.Healthy, drive.State);
    }

    // Fix round 1 (Important 3): pins the one branch in Evaluate with no
    // prior coverage - a healthy destination alongside one that has simply
    // never run rolls up into overall Stale (not Healthy, not NeverRun). A
    // future edit that flipped this to Healthy would silently hide a
    // destination that has never once backed up anything, with the suite
    // still green - this test exists specifically to catch that.
    [Fact]
    public void MixedHealthyAndNeverRunRollsUpToStaleWithNeverRunDestinationVisible()
    {
        var status = Status(github: Succeeded(Now.AddHours(-1))); // drive: never run (no entry at all)
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Stale, result.State);
        var drive = result.Destinations.Single(d => d.Name == "Google Drive (rclone)");
        Assert.Equal(DestinationHealthState.NeverRun, drive.State);
        Assert.Null(drive.LastSuccessUtc);
    }

    // Fix round 1 (Important 4): NeverRun must warrant exactly the same
    // surfacing treatment as Failed/Stale - a scheduled task that never
    // registered produces NeverRun forever, which is the spec's own opening
    // scenario for why silence is unacceptable. Healthy and NotConfigured
    // are the only two states that should stay quiet.
    [Theory]
    [InlineData(BackupHealthState.NeverRun, true)]
    [InlineData(BackupHealthState.Failed, true)]
    [InlineData(BackupHealthState.Stale, true)]
    [InlineData(BackupHealthState.Healthy, false)]
    [InlineData(BackupHealthState.NotConfigured, false)]
    public void WarrantsAttentionMatchesTheSurfacingContract(BackupHealthState state, bool expected)
    {
        Assert.Equal(expected, state.WarrantsAttention());
    }

    // S14: the Drive destination's display name is transport-aware - a
    // sync-folder-configured destination must be named "Sync folder", not
    // "Google Drive (rclone)", so a failure/staleness message names what the
    // user actually configured (they might be pointed at OneDrive, Dropbox,
    // or a NAS share, not Google Drive at all). S17a: Name now lives directly
    // on BackupDestination (set at migration/creation time via
    // BackupHealth.DriveDisplayName - see BackupConfig's own migration) -
    // Evaluate itself just trusts whatever Name is stored.
    [Fact]
    public void SyncFolderTransportUsesSyncFolderDisplayName()
    {
        var status = Status(drive: Failed(Now.AddMinutes(-1)));
        var config = Config(driveEnabled: true, driveTransport: DriveTransport.SyncFolder);
        var result = BackupHealth.Evaluate(status, config, Now, staleAfterDays: 3);

        var drive = result.Destinations.Single(d => d.Name == "Sync folder");
        Assert.Equal(DestinationHealthState.Failed, drive.State);
    }

    // The default (Rclone) transport must keep its own disambiguated name -
    // not just "Google Drive" - so the two transports read symmetrically.
    [Fact]
    public void RcloneTransportUsesGoogleDriveRcloneDisplayName()
    {
        var status = Status(drive: Succeeded(Now.AddHours(-1)));
        var result = BackupHealth.Evaluate(status, Config(driveEnabled: true), Now, staleAfterDays: 3);

        var drive = result.Destinations.Single(d => d.Name == "Google Drive (rclone)");
        Assert.Equal(DestinationHealthState.Healthy, drive.State);
    }

    // S17a: N destinations, including two of the same Kind - the roll-up
    // must not assume "at most two".
    [Fact]
    public void ThreeDestinationsIncludingTwoOfTheSameKindAreAllEvaluatedIndependently()
    {
        var config = new BackupConfig
        {
            Destinations = new()
            {
                new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true },
                new BackupDestination { Id = "nas-1", Name = "Home NAS", Kind = DestinationKind.SyncFolder, Enabled = true },
                new BackupDestination { Id = "nas-2", Name = "Office NAS", Kind = DestinationKind.SyncFolder, Enabled = true },
            },
        };
        var status = new BackupStatus
        {
            Destinations = new()
            {
                ["github"] = Succeeded(Now.AddHours(-1)),
                ["nas-1"] = Succeeded(Now.AddHours(-2)),
                ["nas-2"] = Failed(Now.AddMinutes(-1)),
            },
        };

        var result = BackupHealth.Evaluate(status, config, Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Failed, result.State);
        Assert.Equal(3, result.Destinations.Count);
        Assert.Equal(DestinationHealthState.Healthy, result.Destinations.Single(d => d.Name == "Home NAS").State);
        Assert.Equal(DestinationHealthState.Failed, result.Destinations.Single(d => d.Name == "Office NAS").State);
    }

    // Orphaned status entries (an id in the status file with no matching
    // destination in config) must be ignored by health entirely - not
    // evaluated, not counted, not surfaced.
    [Fact]
    public void OrphanedStatusEntryIsIgnoredByHealth()
    {
        var config = Config(githubEnabled: true);
        var status = new BackupStatus
        {
            Destinations = new()
            {
                ["github"] = Succeeded(Now.AddHours(-1)),
                ["removed-nas"] = Failed(Now.AddDays(-90)), // no longer in config
            },
        };

        var result = BackupHealth.Evaluate(status, config, Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
        Assert.Single(result.Destinations);
        Assert.Equal("GitHub", result.Destinations[0].Name);
    }
}
