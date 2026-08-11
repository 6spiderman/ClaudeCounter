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

    [Fact]
    public void RecordUnhandledExceptionWithNullConfigLeavesBothDestinationsUntouched()
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
}
