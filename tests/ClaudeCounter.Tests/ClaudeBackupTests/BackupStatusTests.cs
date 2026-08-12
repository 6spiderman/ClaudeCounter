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
                Destinations = new()
                {
                    ["github"] = new DestinationStatus
                    {
                        LastAttemptUtc = now,
                        LastSuccessUtc = now,
                        LastOutcome = BackupOutcome.Success,
                        LastMessage = "Backup completed successfully.",
                        WasEnabled = true,
                    },
                    ["drive"] = new DestinationStatus
                    {
                        LastAttemptUtc = now,
                        LastSuccessUtc = now.AddDays(-5),
                        LastOutcome = BackupOutcome.Failed,
                        LastMessage = "rclone: connection refused",
                        WasEnabled = true,
                    },
                },
                LastExitCode = 2,
                LastRunUtc = now,
            };

            status.Save(path);
            var loaded = BackupStatus.Load(path);

            Assert.Equal(status.For("github").LastAttemptUtc, loaded.For("github").LastAttemptUtc);
            Assert.Equal(status.For("github").LastSuccessUtc, loaded.For("github").LastSuccessUtc);
            Assert.Equal(status.For("github").LastOutcome, loaded.For("github").LastOutcome);
            Assert.Equal(status.For("github").LastMessage, loaded.For("github").LastMessage);
            Assert.True(loaded.For("github").WasEnabled);

            Assert.Equal(status.For("drive").LastSuccessUtc, loaded.For("drive").LastSuccessUtc);
            Assert.Equal(BackupOutcome.Failed, loaded.For("drive").LastOutcome);
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
        Assert.Null(loaded.For("github").LastAttemptUtc);
        Assert.Null(loaded.For("github").LastOutcome);
        Assert.Null(loaded.For("drive").LastAttemptUtc);
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
            Assert.Null(loaded.For("github").LastAttemptUtc);
            Assert.Null(loaded.For("drive").LastAttemptUtc);
        }
        finally { File.Delete(path); }
    }

    // S17a: backup-status.json had NO version field at all before this - a
    // file like this (literal top-level "Github"/"Drive" keys, no
    // "BackupStatusVersion") is exactly what every real machine has on disk
    // right now. This is now a MIGRATION test: Load must fold both legacy
    // keys onto the well-known "github"/"drive" ids without throwing, even
    // when both are explicitly null rather than merely absent.
    [Fact]
    public void NullDestinationFieldsInJsonDoNotThrowOnLoad()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """{ "Github": null, "Drive": null }""");

            var loaded = BackupStatus.Load(path);

            Assert.NotNull(loaded.For("github"));
            Assert.NotNull(loaded.For("drive"));
            Assert.Null(loaded.For("github").LastAttemptUtc);
            Assert.Equal(BackupStatus.CurrentBackupStatusVersion, loaded.BackupStatusVersion);
        }
        finally { File.Delete(path); }
    }

    // The full legacy shape (not just null fields): a real pre-S17a
    // backup-status.json, with actual recorded history under the literal
    // "Github"/"Drive" keys and no version field. Must migrate onto the
    // well-known "github"/"drive" ids so BackupConfig's own migration (which
    // assigns the SAME two ids) lines up deterministically.
    [Fact]
    public void LegacyTwoDestinationJsonMigratesOntoWellKnownIds()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": {
                    "LastAttemptUtc": "2026-08-12T05:58:37.543966+00:00",
                    "LastSuccessUtc": "2026-08-12T05:58:37.543966+00:00",
                    "LastOutcome": 0,
                    "LastMessage": "Backup completed successfully.",
                    "WasEnabled": true
                  },
                  "Drive": {
                    "LastAttemptUtc": "2026-08-11T17:00:22.398904+00:00",
                    "LastSuccessUtc": "2026-08-11T17:00:22.398904+00:00",
                    "LastOutcome": 0,
                    "LastMessage": "Backup completed successfully.",
                    "WasEnabled": false
                  },
                  "LastExitCode": 0,
                  "LastRunUtc": "2026-08-12T05:58:37.543966+00:00"
                }
                """);

            var loaded = BackupStatus.Load(path);

            Assert.Equal(BackupOutcome.Success, loaded.For("github").LastOutcome);
            Assert.True(loaded.For("github").WasEnabled);
            Assert.Equal(BackupOutcome.Success, loaded.For("drive").LastOutcome);
            Assert.False(loaded.For("drive").WasEnabled);
            Assert.Equal(0, loaded.LastExitCode);
            Assert.Equal(BackupStatus.CurrentBackupStatusVersion, loaded.BackupStatusVersion);

            // Load's legacy migration is in-memory only (see its own doc
            // comment - it is called on every tray poll and must stay a
            // cheap, side-effect-free read) - the file on disk is untouched
            // until a real write happens.
            var rawAfterLoad = File.ReadAllText(path);
            Assert.DoesNotContain("\"Destinations\"", rawAfterLoad);
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
    // when no destination was actually attempted (e.g. "no backup
    // destinations enabled") - every terminating path has an exit code and a
    // time, unlike the per-destination entries, which can legitimately carry
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
        Assert.Null(result.For("github").LastAttemptUtc);
        Assert.Null(result.For("drive").LastAttemptUtc);
    }

    // S17a: WithRun must handle an arbitrary number of ids, not just the two
    // well-known ones - three destinations, two of them the same Kind, all
    // recorded independently in one run.
    [Fact]
    public void WithRunHandlesNDestinationsIncludingTwoOfTheSameKind()
    {
        var previous = new BackupStatus();
        var now = DateTimeOffset.UtcNow;
        var attempts = new Dictionary<string, DestinationAttempt>
        {
            ["github"] = DestinationAttempt.Ok(),
            ["nas-1"] = DestinationAttempt.Ok(),
            ["nas-2"] = DestinationAttempt.Failed("disk full"),
        };

        var result = previous.WithRun(new BackupRunResult(2, attempts), now);

        Assert.Equal(3, result.Destinations.Count);
        Assert.Equal(BackupOutcome.Success, result.For("github").LastOutcome);
        Assert.Equal(BackupOutcome.Success, result.For("nas-1").LastOutcome);
        Assert.Equal(BackupOutcome.Failed, result.For("nas-2").LastOutcome);
        Assert.Equal("disk full", result.For("nas-2").LastMessage);
    }

    // Removal semantics (S17a, wired up by S17c): dropping a destination's
    // status must remove exactly that entry and leave every other one alone.
    [Fact]
    public void WithoutDestinationRemovesOnlyThatEntry()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new BackupStatus
        {
            Destinations = new()
            {
                ["github"] = new DestinationStatus { LastAttemptUtc = now, WasEnabled = true },
                ["drive"] = new DestinationStatus { LastAttemptUtc = now, WasEnabled = true },
            },
        };

        var result = status.WithoutDestination("drive");

        Assert.True(result.Destinations.ContainsKey("github"));
        Assert.False(result.Destinations.ContainsKey("drive"));
    }

    [Fact]
    public void WithoutDestinationIsANoOpWhenTheIdIsNotPresent()
    {
        var status = new BackupStatus { Destinations = new() { ["github"] = new DestinationStatus() } };

        var result = status.WithoutDestination("does-not-exist");

        Assert.Single(result.Destinations);
        Assert.True(result.Destinations.ContainsKey("github"));
    }

    // Orphan pruning (S17a, wired up by S17c): an id with no matching
    // destination in the current config must be dropped by PruneOrphaned,
    // while every still-valid id survives.
    [Fact]
    public void PruneOrphanedDropsIdsNotInTheValidSet()
    {
        var status = new BackupStatus
        {
            Destinations = new()
            {
                ["github"] = new DestinationStatus { WasEnabled = true },
                ["removed-nas"] = new DestinationStatus { WasEnabled = true },
            },
        };

        var result = status.PruneOrphaned(new[] { "github" });

        Assert.True(result.Destinations.ContainsKey("github"));
        Assert.False(result.Destinations.ContainsKey("removed-nas"));
    }

    [Fact]
    public void RemoveDestinationLoadsMutatesAndSaves()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow;
            new BackupStatus
            {
                Destinations = new()
                {
                    ["github"] = new DestinationStatus { LastAttemptUtc = now, WasEnabled = true },
                    ["drive"] = new DestinationStatus { LastAttemptUtc = now, WasEnabled = true },
                },
            }.Save(path);

            BackupStatus.RemoveDestination(path, "drive");

            var loaded = BackupStatus.Load(path);
            Assert.True(loaded.Destinations.ContainsKey("github"));
            Assert.False(loaded.Destinations.ContainsKey("drive"));
        }
        finally { File.Delete(path); }
    }
}
