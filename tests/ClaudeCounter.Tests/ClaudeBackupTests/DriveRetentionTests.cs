// tests/ClaudeCounter.Tests/ClaudeBackupTests/DriveRetentionTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

/// <summary>
/// DriveRetention.SelectForDeletion is the pure selection logic behind Drive
/// backup retention (design spec, Part 2) - no rclone, no I/O, so every rule
/// combination and every safety guarantee is exercised directly against
/// plain (name, modified) input rather than through RcloneBackend. See
/// RcloneBackendTests for the integration-level coverage (pruning only after
/// a successful upload, a failed listing not failing the run, actual
/// deletefile calls).
/// </summary>
public class DriveRetentionTests
{
    private static DriveRetention.RemoteFile F(string name, int daysAgo) =>
        new(name, DateTimeOffset.UtcNow.AddDays(-daysAgo));

    [Fact]
    public void BothSettingsNullSelectsNothing()
    {
        var files = new[] { F("claude-backup-1.zip", 1), F("claude-backup-2.zip", 400) };
        var doomed = DriveRetention.SelectForDeletion(files, null, null);
        Assert.Empty(doomed);
    }

    [Fact]
    public void EmptyListingSelectsNothing()
    {
        var doomed = DriveRetention.SelectForDeletion(Array.Empty<DriveRetention.RemoteFile>(), 1, 1);
        Assert.Empty(doomed);
    }

    [Fact]
    public void SingleMatchingFileIsNeverSelectedRegardlessOfSettings()
    {
        var files = new[] { F("claude-backup-only.zip", 9999) };
        var doomed = DriveRetention.SelectForDeletion(files, keepLastCount: 0, deleteOlderThanDays: 0);
        Assert.Empty(doomed);
    }

    [Fact]
    public void KeepLastCountAloneDeletesEverythingButTheNewestN()
    {
        var files = new[]
        {
            F("claude-backup-newest.zip", 0),
            F("claude-backup-mid.zip", 5),
            F("claude-backup-oldest.zip", 10),
        };
        var doomed = DriveRetention.SelectForDeletion(files, keepLastCount: 1, deleteOlderThanDays: null);
        Assert.Equal(new[] { "claude-backup-mid.zip", "claude-backup-oldest.zip" },
            doomed.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void DeleteOlderThanDaysAloneDeletesOnlyEntriesPastTheCutoff()
    {
        var files = new[]
        {
            F("claude-backup-recent.zip", 1),
            F("claude-backup-borderline.zip", 29),
            F("claude-backup-old.zip", 31),
        };
        var doomed = DriveRetention.SelectForDeletion(files, keepLastCount: null, deleteOlderThanDays: 30);
        Assert.Equal(new[] { "claude-backup-old.zip" }, doomed);
    }

    // Design spec, Part 2: "a zip is pruned if EITHER rule says so - the
    // union". A file kept by one rule but doomed by the other must still be
    // deleted.
    [Fact]
    public void BothRulesSetTakesTheUnion()
    {
        var files = new[]
        {
            F("claude-backup-newest.zip", 0),      // kept by both rules
            F("claude-backup-old-but-kept.zip", 40), // doomed by age only (outside keep-last-3 window? no: within top 3 by recency)
            F("claude-backup-mid.zip", 2),
            F("claude-backup-very-old.zip", 100),  // doomed by age, and also outside keep-last
        };
        // keepLastCount=3 keeps the 3 most recent (newest, mid, old-but-kept);
        // deleteOlderThanDays=30 dooms old-but-kept (40d) and very-old (100d).
        // Union: very-old (both rules) and old-but-kept (age rule only).
        var doomed = DriveRetention.SelectForDeletion(files, keepLastCount: 3, deleteOlderThanDays: 30);
        Assert.Equal(
            new[] { "claude-backup-old-but-kept.zip", "claude-backup-very-old.zip" },
            doomed.OrderBy(n => n, StringComparer.Ordinal));
    }

    // Rule 2 (non-negotiable): never delete the last remaining backup,
    // whatever the rules say - even the most aggressive possible settings.
    [Fact]
    public void NeverDeletesTheLastRemainingBackupUnderMaximallyAggressiveSettings()
    {
        var files = new[]
        {
            F("claude-backup-a.zip", 500),
            F("claude-backup-b.zip", 600),
            F("claude-backup-c.zip", 700),
        };
        var doomed = DriveRetention.SelectForDeletion(files, keepLastCount: 0, deleteOlderThanDays: 0);
        Assert.Equal(2, doomed.Count);
        // The newest of the three (least stale) is the one spared.
        Assert.DoesNotContain("claude-backup-a.zip", doomed);
    }

    [Fact]
    public void KeepLastCountZeroWithASingleFileDeletesNothing()
    {
        var files = new[] { F("claude-backup-only.zip", 5) };
        var doomed = DriveRetention.SelectForDeletion(files, keepLastCount: 0, deleteOlderThanDays: null);
        Assert.Empty(doomed);
    }

    // Rule 3 (non-negotiable): only claude-backup-*.zip candidates are ever
    // considered - a user pointing the remote at a folder with other content
    // must not lose it.
    [Fact]
    public void NonMatchingFilenamesAreNeverSelected()
    {
        var files = new[]
        {
            F("claude-backup-old.zip", 999),
            F("readme.txt", 999),
            F("some-other-backup.zip", 999),
            F("claude-backup-not-a-zip.txt", 999),
        };
        var doomed = DriveRetention.SelectForDeletion(files, keepLastCount: 0, deleteOlderThanDays: 0);
        Assert.All(doomed, name => Assert.StartsWith("claude-backup-", name));
        Assert.All(doomed, name => Assert.EndsWith(".zip", name));
    }

    [Fact]
    public void NonMatchingFilenamesDoNotCountTowardsTheLastRemainingFloor()
    {
        // Only ONE real candidate exists; the rest are not ours. The floor
        // must still protect that one candidate even though the raw listing
        // has several entries.
        var files = new[]
        {
            F("claude-backup-only.zip", 999),
            F("unrelated-1.zip", 1),
            F("unrelated-2.zip", 1),
        };
        var doomed = DriveRetention.SelectForDeletion(files, keepLastCount: 0, deleteOlderThanDays: 0);
        Assert.DoesNotContain("claude-backup-only.zip", doomed);
    }

    [Theory]
    [InlineData("claude-backup-20240101-000000-deadbeefdeadbeefdeadbeefdeadbeef.zip", true)]
    [InlineData("CLAUDE-BACKUP-20240101.zip", false)] // case-sensitive - never matches an unexpected casing
    [InlineData("claude-backup-.txt", false)]
    [InlineData("notclaude-backup-x.zip", false)]
    public void IsOursMatchesExactlyTheNamingSchemeRcloneBackendWrites(string name, bool expected) =>
        Assert.Equal(expected, DriveRetention.IsOurs(name));
}
