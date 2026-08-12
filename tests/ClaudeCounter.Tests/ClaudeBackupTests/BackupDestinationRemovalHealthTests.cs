// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupDestinationRemovalHealthTests.cs
//
// S17c: end-to-end proof, at the model layer, of the exact pipeline the task
// brief calls "removing must clear the notification": BackupConfig drops the
// destination, BackupStatus.RemoveDestination drops its status entry, and
// BackupHealth.Evaluate over the result no longer reports (or warns about)
// anything for it. TrayApplicationContext itself is not unit-testable (it
// constructs a real NotifyIcon and touches the real settings store), so this
// is the closest thing to an integration test for "the badge actually
// clears" - it exercises the same three calls BackupDestinationsDialog.OnRemove
// and TrayApplicationContext.EvaluateBackupHealth make, against real temp
// files, never the real %APPDATA%/%LOCALAPPDATA%.
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupDestinationRemovalHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    private static string TempPath(string suffix) => Path.Combine(Path.GetTempPath(), $"bkremoval-{Guid.NewGuid():N}-{suffix}");

    // The scenario the task brief describes explicitly: a single, currently
    // FAILING destination is removed - health must go from Failed straight
    // to NotConfigured (which "by design shows nothing at all"), not linger
    // as Failed and not land on some other non-silent state.
    [Fact]
    public void RemovingTheOnlyFailingDestinationClearsHealthToNotConfigured()
    {
        var configPath = TempPath("config.json");
        var statusPath = TempPath("status.json");
        try
        {
            var config = new BackupConfig
            {
                Destinations = new()
                {
                    new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true, RemoteUrl = "git@example.com:org/repo.git" },
                },
            };
            config.Save(configPath);

            var status = new BackupStatus
            {
                Destinations = new()
                {
                    ["github"] = new DestinationStatus { LastAttemptUtc = Now, LastOutcome = BackupOutcome.Failed, LastMessage = "network error", WasEnabled = true },
                },
            };
            status.Save(statusPath);

            // Sanity check: health is Failed before removal - otherwise this
            // test would not be exercising the transition it claims to.
            var before = BackupHealth.Evaluate(BackupStatus.Load(statusPath), BackupConfig.Load(configPath), Now, staleAfterDays: 3);
            Assert.Equal(BackupHealthState.Failed, before.State);

            // The exact two calls BackupDestinationsDialog.OnRemove makes.
            config.Destinations.RemoveAll(d => d.Id == "github");
            config.Save(configPath);
            BackupStatus.RemoveDestination(statusPath, "github");

            var after = BackupHealth.Evaluate(BackupStatus.Load(statusPath), BackupConfig.Load(configPath), Now, staleAfterDays: 3);
            Assert.Equal(BackupHealthState.NotConfigured, after.State);
            Assert.Empty(after.Destinations);

            // And the removal genuinely dropped the status entry on disk -
            // not merely made it unreachable through this config - so a
            // stale credential-scrubbed failure message does not linger in
            // backup-status.json indefinitely either.
            var statusOnDisk = BackupStatus.Load(statusPath);
            Assert.False(statusOnDisk.Destinations.ContainsKey("github"));
        }
        finally
        {
            File.Delete(configPath);
            File.Delete(statusPath);
        }
    }

    // Removing ONE of several destinations - the other, healthy one must
    // keep reporting Healthy afterward, and the removed one's failure must
    // no longer drag the overall state down to Failed.
    [Fact]
    public void RemovingAFailingDestinationLeavesAHealthySiblingReportingHealthy()
    {
        var configPath = TempPath("config.json");
        var statusPath = TempPath("status.json");
        try
        {
            var config = new BackupConfig
            {
                Destinations = new()
                {
                    new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true, RemoteUrl = "git@example.com:org/repo.git" },
                    new BackupDestination { Id = "nas", Name = "Home NAS", Kind = DestinationKind.SyncFolder, Enabled = true, FolderPath = @"\\nas\backups" },
                },
            };
            config.Save(configPath);

            var status = new BackupStatus
            {
                Destinations = new()
                {
                    ["github"] = new DestinationStatus { LastAttemptUtc = Now, LastOutcome = BackupOutcome.Failed, LastMessage = "auth error", WasEnabled = true },
                    ["nas"] = new DestinationStatus { LastAttemptUtc = Now, LastSuccessUtc = Now, LastOutcome = BackupOutcome.Success, WasEnabled = true },
                },
            };
            status.Save(statusPath);

            var before = BackupHealth.Evaluate(BackupStatus.Load(statusPath), BackupConfig.Load(configPath), Now, staleAfterDays: 3);
            Assert.Equal(BackupHealthState.Failed, before.State);

            config.Destinations.RemoveAll(d => d.Id == "github");
            config.Save(configPath);
            BackupStatus.RemoveDestination(statusPath, "github");

            var after = BackupHealth.Evaluate(BackupStatus.Load(statusPath), BackupConfig.Load(configPath), Now, staleAfterDays: 3);
            Assert.Equal(BackupHealthState.Healthy, after.State);
            Assert.Single(after.Destinations);
            Assert.Equal("Home NAS", after.Destinations[0].Name);
        }
        finally
        {
            File.Delete(configPath);
            File.Delete(statusPath);
        }
    }
}
