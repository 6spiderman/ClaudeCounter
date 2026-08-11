// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupStatusTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupStatusTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"bkstatus-{Guid.NewGuid():N}.json");

    [Fact]
    public void RoundTripsThroughFile()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var status = new BackupStatus
            {
                Github = new DestinationStatus
                {
                    LastAttemptUtc = now,
                    LastSuccessUtc = now,
                    LastOutcome = BackupOutcome.Success,
                    LastMessage = "Backup completed successfully.",
                    WasEnabled = true,
                },
                Drive = new DestinationStatus
                {
                    LastAttemptUtc = now,
                    LastSuccessUtc = now.AddDays(-5),
                    LastOutcome = BackupOutcome.Failed,
                    LastMessage = "rclone: connection refused",
                    WasEnabled = true,
                },
                LastExitCode = 2,
                LastRunUtc = now,
            };

            status.Save(path);
            var loaded = BackupStatus.Load(path);

            Assert.Equal(status.Github.LastAttemptUtc, loaded.Github.LastAttemptUtc);
            Assert.Equal(status.Github.LastSuccessUtc, loaded.Github.LastSuccessUtc);
            Assert.Equal(status.Github.LastOutcome, loaded.Github.LastOutcome);
            Assert.Equal(status.Github.LastMessage, loaded.Github.LastMessage);
            Assert.True(loaded.Github.WasEnabled);

            Assert.Equal(status.Drive.LastSuccessUtc, loaded.Drive.LastSuccessUtc);
            Assert.Equal(BackupOutcome.Failed, loaded.Drive.LastOutcome);
            Assert.Equal(2, loaded.LastExitCode);
            Assert.Equal(status.LastRunUtc, loaded.LastRunUtc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFileDegradesToNeverRun()
    {
        var loaded = BackupStatus.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.NotNull(loaded);
        Assert.Null(loaded.Github.LastAttemptUtc);
        Assert.Null(loaded.Github.LastOutcome);
        Assert.Null(loaded.Drive.LastAttemptUtc);
        Assert.Null(loaded.LastExitCode);
    }

    [Fact]
    public void CorruptFileDegradesToNeverRunRatherThanThrowing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json ][");

            var loaded = BackupStatus.Load(path);

            Assert.NotNull(loaded);
            Assert.Null(loaded.Github.LastAttemptUtc);
            Assert.Null(loaded.Drive.LastAttemptUtc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NullDestinationFieldsInJsonDoNotThrowOnLoad()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """{ "Github": null, "Drive": null }""");

            var loaded = BackupStatus.Load(path);

            Assert.NotNull(loaded.Github);
            Assert.NotNull(loaded.Drive);
            Assert.Null(loaded.Github.LastAttemptUtc);
        }
        finally { File.Delete(path); }
    }

    // The property staleness depends on: a failing run after a successful
    // one must leave the success timestamp intact - only the attempt time
    // and outcome move.
    [Fact]
    public void FailingAttemptAfterSuccessPreservesLastSuccessTimestamp()
    {
        var firstSuccess = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var afterSuccess = new DestinationStatus().WithAttempt(DestinationAttempt.Ok(), firstSuccess);
        Assert.Equal(firstSuccess, afterSuccess.LastSuccessUtc);

        var laterFailureTime = firstSuccess.AddDays(3);
        var afterFailure = afterSuccess.WithAttempt(DestinationAttempt.Failed("backend failed"), laterFailureTime);

        Assert.Equal(firstSuccess, afterFailure.LastSuccessUtc); // unchanged
        Assert.Equal(laterFailureTime, afterFailure.LastAttemptUtc); // moved
        Assert.Equal(BackupOutcome.Failed, afterFailure.LastOutcome);
    }

    [Fact]
    public void SuccessfulAttemptAdvancesBothAttemptAndSuccessTimestamps()
    {
        var t1 = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var result = new DestinationStatus().WithAttempt(DestinationAttempt.Ok(), t1);

        Assert.Equal(t1, result.LastAttemptUtc);
        Assert.Equal(t1, result.LastSuccessUtc);
        Assert.Equal(BackupOutcome.Success, result.LastOutcome);
    }

    // A destination this run never touched (currently disabled, or the run
    // aborted before reaching it) must carry every field forward unchanged
    // except WasEnabled - a run that says nothing about a destination must
    // not silently erase what its last real attempt said.
    [Fact]
    public void NotAttemptedCarriesForwardEverythingExceptWasEnabled()
    {
        var t1 = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var previous = new DestinationStatus().WithAttempt(DestinationAttempt.Ok("ok"), t1);

        var later = t1.AddDays(1);
        var result = previous.WithAttempt(DestinationAttempt.NotAttempted(enabled: false), later);

        Assert.Equal(previous.LastAttemptUtc, result.LastAttemptUtc);
        Assert.Equal(previous.LastSuccessUtc, result.LastSuccessUtc);
        Assert.Equal(previous.LastOutcome, result.LastOutcome);
        Assert.Equal(previous.LastMessage, result.LastMessage);
        Assert.False(result.WasEnabled);
    }

    // A message containing a credential-shaped URL must never survive into
    // the stored status - WithAttempt is the one choke point every message
    // passes through before being persisted.
    [Fact]
    public void CredentialShapedMessageIsStoredScrubbed()
    {
        const string raw = "fatal: unable to access 'https://ghp_supersecrettoken123@github.com/me/repo.git/'";
        var result = new DestinationStatus().WithAttempt(DestinationAttempt.Failed(raw), DateTimeOffset.UtcNow);

        Assert.DoesNotContain("ghp_supersecrettoken123", result.LastMessage);
        Assert.Contains("https://github.com", result.LastMessage);
    }

    // BackupStatus.WithRun always advances LastExitCode/LastRunUtc, even
    // when neither destination was actually attempted (e.g. "no backup
    // destinations enabled") - every terminating path has an exit code and a
    // time, unlike the per-destination fields, which can legitimately carry
    // forward unchanged.
    [Fact]
    public void WithRunAlwaysAdvancesExitCodeAndRunTimeEvenWithNoAttempts()
    {
        var previous = new BackupStatus();
        var now = DateTimeOffset.UtcNow;

        var result = previous.WithRun(
            new BackupRunResult(1, DestinationAttempt.NotAttempted(false), DestinationAttempt.NotAttempted(false)),
            now);

        Assert.Equal(1, result.LastExitCode);
        Assert.Equal(now, result.LastRunUtc);
        Assert.Null(result.Github.LastAttemptUtc);
        Assert.Null(result.Drive.LastAttemptUtc);
    }
}
