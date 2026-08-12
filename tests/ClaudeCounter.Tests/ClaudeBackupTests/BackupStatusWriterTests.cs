// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupStatusWriterTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupStatusWriterTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"bkstatus-{Guid.NewGuid():N}.json");

    // S17a: RecordUnhandledException reads BackupConfig.Destinations (the
    // real N-destination list) - the Github/Drive shim it used to read via
    // was deleted entirely in S17c. Building the config via Destinations
    // directly here is the realistic shape.
    private static BackupConfig ConfigWithGithubDriveEnabled(bool githubEnabled, bool driveEnabled) => new()
    {
        Destinations = new()
        {
            new BackupDestination { Id = "github", Kind = DestinationKind.GitHub, Enabled = githubEnabled },
            new BackupDestination { Id = "drive", Kind = DestinationKind.Rclone, Enabled = driveEnabled },
        },
    };

    // S17b: BackupRunResult's back-compat 3-arg (exitCode, github, drive)
    // constructor was deleted alongside BackupRunner's own migration to N
    // destinations (see BackupRunResult.cs's remarks) - this local helper
    // keeps every existing test below reading exactly like it did against
    // that constructor, just spelled as the id-keyed dictionary shape
    // BackupRunResult now requires everywhere.
    private static BackupRunResult GithubDriveResult(int exitCode, DestinationAttempt github, DestinationAttempt drive) =>
        new(exitCode, new Dictionary<string, DestinationAttempt> { ["github"] = github, ["drive"] = drive });

    [Fact]
    public void RecordWritesAndPreservesLastSuccessAcrossAFollowingFailure()
    {
        var path = TempPath();
        try
        {
            var firstRun = DateTimeOffset.UtcNow;
            BackupStatusWriter.Record(path, GithubDriveResult(0, DestinationAttempt.Ok(), DestinationAttempt.NotAttempted(false)), firstRun);

            var afterFirst = BackupStatus.Load(path);
            Assert.Equal(firstRun, afterFirst.For("github").LastSuccessUtc);

            var secondRun = firstRun.AddDays(1);
            BackupStatusWriter.Record(path, GithubDriveResult(2, DestinationAttempt.Failed("backend failed"), DestinationAttempt.NotAttempted(false)), secondRun);

            var afterSecond = BackupStatus.Load(path);
            Assert.Equal(firstRun, afterSecond.For("github").LastSuccessUtc); // preserved
            Assert.Equal(secondRun, afterSecond.For("github").LastAttemptUtc); // moved
            Assert.Equal(BackupOutcome.Failed, afterSecond.For("github").LastOutcome);
            Assert.Equal(2, afterSecond.LastExitCode);
        }
        finally { File.Delete(path); }
    }

    // Rule 3: a failed status write must never fail the run. Simulated here
    // by pointing the status path at a location where the containing
    // "directory" is actually an existing file - Directory.CreateDirectory
    // (inside BackupStatus.Save) then throws, and Record must swallow it.
    [Fact]
    public void RecordSwallowsAWriteFailureWithoutThrowing()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"bkstatus-block-{Guid.NewGuid():N}");
        File.WriteAllText(blockingFile, "not a directory");
        try
        {
            var badPath = Path.Combine(blockingFile, "backup-status.json");

            // Must not throw.
            BackupStatusWriter.Record(badPath, GithubDriveResult(0, DestinationAttempt.Ok(), DestinationAttempt.NotAttempted(false)), DateTimeOffset.UtcNow);
        }
        finally { File.Delete(blockingFile); }
    }

    [Fact]
    public void RecordUnhandledExceptionMarksOnlyEnabledDestinationsFailed()
    {
        var path = TempPath();
        try
        {
            var config = ConfigWithGithubDriveEnabled(githubEnabled: true, driveEnabled: false);

            BackupStatusWriter.RecordUnhandledException(path, config, "unhandled: boom", DateTimeOffset.UtcNow);

            var status = BackupStatus.Load(path);
            Assert.Equal(BackupOutcome.Failed, status.For("github").LastOutcome);
            Assert.Equal("unhandled: boom", status.For("github").LastMessage);
            Assert.Null(status.For("drive").LastOutcome); // never attempted - disabled
            Assert.Equal(2, status.LastExitCode);
        }
        finally { File.Delete(path); }
    }

    // Fix round 1: renamed from "...LeavesBothDestinationsUntouched" and
    // narrowed to describe exactly what this covers - a null config with NO
    // prior history at all (a fresh status file that has never recorded
    // either destination as enabled). This is genuinely "nothing to say" and
    // stays NotAttempted. See the test immediately below for the scenario
    // that used to be silently wrong.
    [Fact]
    public void RecordUnhandledExceptionWithNullConfigAndNoPriorHistoryLeavesBothDestinationsUntouched()
    {
        var path = TempPath();
        try
        {
            BackupStatusWriter.RecordUnhandledException(path, null, "unhandled: boom", DateTimeOffset.UtcNow);

            var status = BackupStatus.Load(path);
            Assert.Null(status.For("github").LastOutcome);
            Assert.Null(status.For("drive").LastOutcome);
            Assert.Equal(2, status.LastExitCode); // the run itself is still recorded
        }
        finally { File.Delete(path); }
    }

    // Fix round 1 (Important 1) - the precise bug this feature exists to
    // prevent, reproduced end to end exactly as the reviewer probed it: a
    // destination that succeeded once, then starts failing on every run
    // before a config is even available (config == null reaching
    // RecordUnhandledException - e.g. an ACL-locked backup.json throwing
    // UnauthorizedAccessException, though that specific type is now also
    // caught inside BackupConfig.Load itself per Important 2 - this covers
    // whatever else can still reach here with no config in hand), must NOT
    // keep reading Healthy forever. Falling back to the destination's own
    // persisted WasEnabled (written by the earlier successful run) is what
    // fixes it.
    [Fact]
    public void RecordUnhandledExceptionWithNullConfigFallsBackToPersistedWasEnabledAndReportsFailedHealth()
    {
        var path = TempPath();
        try
        {
            var firstRun = DateTimeOffset.UtcNow.AddDays(-1);
            BackupStatusWriter.Record(
                path, GithubDriveResult(0, DestinationAttempt.Ok(), DestinationAttempt.NotAttempted(false)), firstRun);

            var afterSuccess = BackupStatus.Load(path);
            Assert.Equal(BackupOutcome.Success, afterSuccess.For("github").LastOutcome);
            Assert.True(afterSuccess.For("github").WasEnabled);

            // Every subsequent run now fails before any config is loaded.
            var now = DateTimeOffset.UtcNow;
            BackupStatusWriter.RecordUnhandledException(path, null, "unhandled: config unreadable", now);

            var status = BackupStatus.Load(path);
            Assert.Equal(BackupOutcome.Failed, status.For("github").LastOutcome); // not silently carried forward as Success
            Assert.Equal("unhandled: config unreadable", status.For("github").LastMessage);
            Assert.Equal(now, status.For("github").LastAttemptUtc);
            Assert.Equal(firstRun, status.For("github").LastSuccessUtc); // still preserved (rule 4)
            Assert.Equal(2, status.LastExitCode);

            // The reviewer-probed end-to-end assertion: health must actually
            // report Failed, not Healthy, once this status is evaluated.
            var config = ConfigWithGithubDriveEnabled(githubEnabled: true, driveEnabled: false);
            var health = BackupHealth.Evaluate(status, config, now, staleAfterDays: 3);
            Assert.Equal(BackupHealthState.Failed, health.State);
        }
        finally { File.Delete(path); }
    }

    // S17a: RecordUnhandledException must generalize past exactly two ids -
    // three destinations, only some enabled, none of them named
    // "github"/"drive".
    [Fact]
    public void RecordUnhandledExceptionHandlesNDestinationsBeyondTheWellKnownTwo()
    {
        var path = TempPath();
        try
        {
            var config = new BackupConfig
            {
                Destinations = new()
                {
                    new BackupDestination { Id = "nas-1", Kind = DestinationKind.SyncFolder, Enabled = true },
                    new BackupDestination { Id = "nas-2", Kind = DestinationKind.SyncFolder, Enabled = false },
                    new BackupDestination { Id = "rclone-1", Kind = DestinationKind.Rclone, Enabled = true },
                },
            };

            BackupStatusWriter.RecordUnhandledException(path, config, "unhandled: boom", DateTimeOffset.UtcNow);

            var status = BackupStatus.Load(path);
            Assert.Equal(BackupOutcome.Failed, status.For("nas-1").LastOutcome);
            Assert.Null(status.For("nas-2").LastOutcome); // disabled - never attempted
            Assert.Equal(BackupOutcome.Failed, status.For("rclone-1").LastOutcome);
        }
        finally { File.Delete(path); }
    }
}
