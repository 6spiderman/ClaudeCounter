// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupHealthTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static BackupConfig Config(bool githubEnabled = false, bool driveEnabled = false) => new()
    {
        Github = new GitTarget { Enabled = githubEnabled, RemoteUrl = "url", Branch = "main" },
        Drive = new DriveTarget { Enabled = driveEnabled, RcloneRemote = "remote" },
    };

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
        var status = new BackupStatus { Github = Failed(Now.AddDays(-100)) };
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
        var status = new BackupStatus { Github = Succeeded(Now.AddHours(-1)) };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    [Fact]
    public void BothDestinationsRecentlySucceededIsHealthy()
    {
        var status = new BackupStatus
        {
            Github = Succeeded(Now.AddHours(-1)),
            Drive = Succeeded(Now.AddHours(-2)),
        };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    [Fact]
    public void LastAttemptFailedIsFailed()
    {
        var status = new BackupStatus { Github = Failed(Now.AddMinutes(-5), lastSuccessUtc: Now.AddHours(-1)) };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Failed, result.State);
    }

    [Fact]
    public void SuccessOlderThanThresholdIsStale()
    {
        var status = new BackupStatus { Github = Succeeded(Now.AddDays(-10)) };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Stale, result.State);
    }

    // Failed outranks Stale when both apply: GitHub is failing right now,
    // Drive is merely stale - the overall state must be Failed, the more
    // actionable signal.
    [Fact]
    public void FailedOutranksStaleWhenBothApply()
    {
        var status = new BackupStatus
        {
            Github = Failed(Now.AddMinutes(-1), lastSuccessUtc: Now.AddDays(-1)),
            Drive = Succeeded(Now.AddDays(-30)),
        };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Failed, result.State);
    }

    // 0 means never warn about staleness - a success from a year ago is
    // still Healthy when the threshold is 0.
    [Fact]
    public void ZeroThresholdMeansNeverWarnAboutStaleness()
    {
        var status = new BackupStatus { Github = Succeeded(Now.AddDays(-400)) };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 0);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    // Staleness boundary: exactly at the threshold is still Healthy; one
    // second past it is Stale; one second short of it is Healthy.
    [Fact]
    public void ExactlyAtThresholdIsHealthy()
    {
        var status = new BackupStatus { Github = Succeeded(Now.AddDays(-3)) };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    [Fact]
    public void OneSecondPastThresholdIsStale()
    {
        var status = new BackupStatus { Github = Succeeded(Now.AddDays(-3).AddSeconds(-1)) };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Stale, result.State);
    }

    [Fact]
    public void OneSecondShortOfThresholdIsHealthy()
    {
        var status = new BackupStatus { Github = Succeeded(Now.AddDays(-3).AddSeconds(1)) };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Healthy, result.State);
    }

    // A destination the user has since disabled must not drag health down,
    // even if its last recorded status was a failure.
    [Fact]
    public void DisabledDestinationIsIgnoredEvenIfItsLastStatusWasFailed()
    {
        var status = new BackupStatus
        {
            Github = Succeeded(Now.AddHours(-1)),
            Drive = Failed(Now.AddDays(-50)),
        };
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
        var status = new BackupStatus
        {
            Github = Failed(Now.AddMinutes(-1)),
            Drive = Succeeded(Now.AddHours(-1)),
        };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: true), Now, staleAfterDays: 3);

        var github = result.Destinations.Single(d => d.Name == "GitHub");
        var drive = result.Destinations.Single(d => d.Name == "Google Drive");
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
        var status = new BackupStatus
        {
            Github = Succeeded(Now.AddHours(-1)), // healthy
            Drive = new DestinationStatus(), // never run
        };
        var result = BackupHealth.Evaluate(status, Config(githubEnabled: true, driveEnabled: true), Now, staleAfterDays: 3);

        Assert.Equal(BackupHealthState.Stale, result.State);
        var drive = result.Destinations.Single(d => d.Name == "Google Drive");
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
}
