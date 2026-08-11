// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupStatusWriterTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupStatusWriterTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"bkstatus-{Guid.NewGuid():N}.json");

    [Fact]
    public void RecordWritesAndPreservesLastSuccessAcrossAFollowingFailure()
    {
        var path = TempPath();
        try
        {
            var firstRun = DateTimeOffset.UtcNow;
            BackupStatusWriter.Record(path, new BackupRunResult(0, DestinationAttempt.Ok(), DestinationAttempt.NotAttempted(false)), firstRun);

            var afterFirst = BackupStatus.Load(path);
            Assert.Equal(firstRun, afterFirst.Github.LastSuccessUtc);

            var secondRun = firstRun.AddDays(1);
            BackupStatusWriter.Record(path, new BackupRunResult(2, DestinationAttempt.Failed("backend failed"), DestinationAttempt.NotAttempted(false)), secondRun);

            var afterSecond = BackupStatus.Load(path);
            Assert.Equal(firstRun, afterSecond.Github.LastSuccessUtc); // preserved
            Assert.Equal(secondRun, afterSecond.Github.LastAttemptUtc); // moved
            Assert.Equal(BackupOutcome.Failed, afterSecond.Github.LastOutcome);
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
            BackupStatusWriter.Record(badPath, new BackupRunResult(0, DestinationAttempt.Ok(), DestinationAttempt.NotAttempted(false)), DateTimeOffset.UtcNow);
        }
        finally { File.Delete(blockingFile); }
    }

    [Fact]
    public void RecordUnhandledExceptionMarksOnlyEnabledDestinationsFailed()
    {
        var path = TempPath();
        try
        {
            var config = new BackupConfig
            {
                Github = new GitTarget { Enabled = true },
                Drive = new DriveTarget { Enabled = false },
            };

            BackupStatusWriter.RecordUnhandledException(path, config, "unhandled: boom", DateTimeOffset.UtcNow);

            var status = BackupStatus.Load(path);
            Assert.Equal(BackupOutcome.Failed, status.Github.LastOutcome);
            Assert.Equal("unhandled: boom", status.Github.LastMessage);
            Assert.Null(status.Drive.LastOutcome); // never attempted - disabled
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
            Assert.Null(status.Github.LastOutcome);
            Assert.Null(status.Drive.LastOutcome);
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
                path, new BackupRunResult(0, DestinationAttempt.Ok(), DestinationAttempt.NotAttempted(false)), firstRun);

            var afterSuccess = BackupStatus.Load(path);
            Assert.Equal(BackupOutcome.Success, afterSuccess.Github.LastOutcome);
            Assert.True(afterSuccess.Github.WasEnabled);

            // Every subsequent run now fails before any config is loaded.
            var now = DateTimeOffset.UtcNow;
            BackupStatusWriter.RecordUnhandledException(path, null, "unhandled: config unreadable", now);

            var status = BackupStatus.Load(path);
            Assert.Equal(BackupOutcome.Failed, status.Github.LastOutcome); // not silently carried forward as Success
            Assert.Equal("unhandled: config unreadable", status.Github.LastMessage);
            Assert.Equal(now, status.Github.LastAttemptUtc);
            Assert.Equal(firstRun, status.Github.LastSuccessUtc); // still preserved (rule 4)
            Assert.Equal(2, status.LastExitCode);

            // The reviewer-probed end-to-end assertion: health must actually
            // report Failed, not Healthy, once this status is evaluated.
            var config = new BackupConfig
            {
                Github = new GitTarget { Enabled = true },
                Drive = new DriveTarget { Enabled = false },
            };
            var health = BackupHealth.Evaluate(status, config, now, staleAfterDays: 3);
            Assert.Equal(BackupHealthState.Failed, health.State);
        }
        finally { File.Delete(path); }
    }
}
