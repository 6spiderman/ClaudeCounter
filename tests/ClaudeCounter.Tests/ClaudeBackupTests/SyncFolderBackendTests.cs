// tests/ClaudeCounter.Tests/ClaudeBackupTests/SyncFolderBackendTests.cs
using System.IO.Compression;
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

/// <summary>
/// S14: mirrors RcloneBackendTests in rigour for the sync-folder transport -
/// SyncFolderBackend has no IProcessRunner to fake, so tests open the actual
/// copied zip that lands in the sync folder directly, rather than
/// intercepting a "command" the way RcloneBackendTests' FakeRunner does.
/// Every test gets a GUID-suffixed temp path (source root AND sync folder)
/// so parallel xUnit execution can never collide - see this project's own
/// note about the flaky-test history from shared fixed temp paths.
/// </summary>
public class SyncFolderBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sfsrc-{Guid.NewGuid():N}");
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"sftmp-{Guid.NewGuid():N}");
    private readonly string _sync = Path.Combine(Path.GetTempPath(), $"sfdst-{Guid.NewGuid():N}");

    public SyncFolderBackendTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _tmp, _sync })
            if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    private DriveTarget Target(string? folderPath = null) => new()
    {
        Enabled = true,
        Transport = DriveTransport.SyncFolder,
        FolderPath = folderPath ?? _sync,
    };

    private static List<string> ZipEntries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    // --- Basic happy path ---------------------------------------------------

    [Fact]
    public void CopiesArchiveIntoFolderPathAndCreatesItIfAbsent()
    {
        Assert.False(Directory.Exists(_sync));
        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target());

        Assert.True(result.Ok);
        Assert.True(Directory.Exists(_sync));
        Assert.Single(Directory.GetFiles(_sync, "claude-backup-*.zip"));
    }

    [Fact]
    public void SuccessfulRunLeavesNoTempZipBehind()
    {
        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target());

        Assert.True(result.Ok);
        Assert.True(Directory.Exists(_tmp));
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    // --- Zip contents: safe selection, escaping paths, secrets, duplicates ---

    // I-3/I-4 style: a real escaping file on disk plus a real nested file -
    // the archive that actually lands in the sync folder must contain
    // exactly the safe entries, proving both RelativePathGuard.IsSafe (an
    // unguarded backend would archive the escaping file) and that the
    // legitimate nested entry was not lost.
    [Fact]
    public void ZipContainsExactlyTheSafeSelectedFilesNotAnEscapingOne()
    {
        Directory.CreateDirectory(Path.Combine(_root, "commands"));
        File.WriteAllText(Path.Combine(_root, "commands", "a.md"), "hello");

        var escapeFileName = $"escape-{Guid.NewGuid():N}.json";
        var escapePath = Path.Combine(Path.GetDirectoryName(_root)!, escapeFileName);
        File.WriteAllText(escapePath, "should never be archived");
        try
        {
            var result = new SyncFolderBackend(_tmp).Run(
                _root,
                new[] { "settings.json", "commands/a.md", $"../{escapeFileName}" },
                Target());

            Assert.True(result.Ok);
            var zipPath = Directory.GetFiles(_sync, "claude-backup-*.zip").Single();
            Assert.Equal(
                new[] { "commands/a.md", "settings.json" },
                ZipEntries(zipPath).OrderBy(e => e, StringComparer.Ordinal));
        }
        finally
        {
            File.Delete(escapePath);
        }
    }

    // Fail-closed backstop at the point of write: Run is public and takes an
    // arbitrary file list, so a secret-named entry reaching it directly must
    // never be archived even though FileSelector/BackupRunner already filter
    // upstream.
    [Fact]
    public void RefusesToArchiveSecretNamedFileEvenIfPassedDirectly()
    {
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");

        var result = new SyncFolderBackend(_tmp).Run(
            _root, new[] { "settings.json", ".credentials.json" }, Target());

        Assert.True(result.Ok);
        var zipPath = Directory.GetFiles(_sync, "claude-backup-*.zip").Single();
        Assert.DoesNotContain(".credentials.json", ZipEntries(zipPath));
    }

    [Fact]
    public void RefusesDuplicateZipEntryName()
    {
        var result = new SyncFolderBackend(_tmp).Run(
            _root, new[] { "settings.json", "settings.json" }, Target());

        Assert.True(result.Ok);
        var zipPath = Directory.GetFiles(_sync, "claude-backup-*.zip").Single();
        Assert.Single(ZipEntries(zipPath), e => e == "settings.json");
    }

    // --- Folder-path validation ----------------------------------------------

    [Fact]
    public void BlankFolderPathFailsCleanly()
    {
        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target(""));
        Assert.False(result.Ok);
        Assert.Contains("folder is configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelativeFolderPathFailsCleanly()
    {
        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target(@"relative\path"));
        Assert.False(result.Ok);
        Assert.Contains("full path", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The single most important guard (design doc): a FolderPath that is, or
    // is inside, SourceRoot must be rejected - otherwise the archive would be
    // written inside the tree being backed up and every subsequent run would
    // sweep up its own predecessors.
    [Fact]
    public void FolderPathEqualToSourceRootIsRejected()
    {
        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target(_root));
        Assert.False(result.Ok);
        Assert.Contains("overlaps", result.Message, StringComparison.OrdinalIgnoreCase);
        // Nothing must have been written into the source tree as a result.
        Assert.DoesNotContain(Directory.GetFiles(_root), f => f.Contains("claude-backup-"));
    }

    [Fact]
    public void FolderPathNestedInsideSourceRootIsRejected()
    {
        var nested = Path.Combine(_root, "backups");
        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target(nested));
        Assert.False(result.Ok);
        Assert.Contains("overlaps", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceRootNestedInsideFolderPathIsRejected()
    {
        // The other overlap direction: SourceRoot itself sitting underneath
        // the requested sync folder.
        var ancestor = Path.GetDirectoryName(_root)!;
        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target(ancestor));
        Assert.False(result.Ok);
        Assert.Contains("overlaps", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // UNC paths must be accepted (a NAS share is a first-class target for
    // this transport) - validated directly via the side-effect-free helper
    // rather than through a real network copy, which no test environment can
    // rely on having available.
    [Theory]
    [InlineData(@"\\nas\share\claude", true)]
    [InlineData(@"\\192.168.1.210\media\claude-backups", true)]
    [InlineData(@"relative\path", false)]
    [InlineData("", false)]
    public void ValidatesRootednessAllowingUncPaths(string folderPath, bool expectRooted)
    {
        var error = SyncFolderBackend.ValidateFolderPath(folderPath, _root);
        if (expectRooted)
        {
            // A syntactically valid, non-overlapping UNC path must pass -
            // no "not a full path" complaint. (It may still legitimately be
            // rejected by the overlap check in a pathological case, but a
            // NAS UNC path can never overlap an unrelated local _root.)
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
        }
    }

    // --- Copy failure ---------------------------------------------------------

    // Directory.CreateDirectory throws when a plain FILE already occupies the
    // requested path - proves a creation failure fails the run cleanly rather
    // than throwing out of Run.
    [Fact]
    public void FolderPathThatCannotBeCreatedFailsCleanly()
    {
        var blockingFilePath = Path.Combine(Path.GetTempPath(), $"sfblock-{Guid.NewGuid():N}");
        File.WriteAllText(blockingFilePath, "not a directory");
        try
        {
            var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target(blockingFilePath));
            Assert.False(result.Ok);
            Assert.Contains("could not create", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(blockingFilePath);
        }
    }

    // --- Retention ------------------------------------------------------------

    [Fact]
    public void PruningIsSkippedEntirelyWhenNeitherRetentionSettingIsConfigured()
    {
        Directory.CreateDirectory(_sync);
        var preexisting = Path.Combine(_sync, "claude-backup-20200101-000000-deadbeefdeadbeefdeadbeefdeadbeef.zip");
        File.WriteAllText(preexisting, "old backup");

        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, Target());

        Assert.True(result.Ok);
        // The pre-existing zip must still be there - retention never ran.
        Assert.True(File.Exists(preexisting));
    }

    // Retention deletes the right archives (past the keep-last window) and
    // never the single newest remaining one, exercised through a real
    // directory listing rather than a parsed rclone lsjson fixture.
    [Fact]
    public void SuccessfulRunPrunesOldEntriesPastTheKeepLastWindowAndNeverTheNewest()
    {
        Directory.CreateDirectory(_sync);
        var oldest = Path.Combine(_sync, "claude-backup-old-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.zip");
        var mid = Path.Combine(_sync, "claude-backup-mid-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.zip");
        File.WriteAllText(oldest, "old");
        File.WriteAllText(mid, "mid");
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(mid, DateTime.UtcNow.AddDays(-5));

        var target = Target();
        target.KeepLastCount = 1;

        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, target);

        Assert.True(result.Ok);
        // Both pre-existing entries are older than the archive this run just
        // wrote (the newest), so KeepLastCount=1 dooms both of them - the
        // just-written archive is the one entry spared.
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(mid));
        Assert.Single(Directory.GetFiles(_sync, "claude-backup-*.zip"));
    }

    [Fact]
    public void RetentionNeverDeletesTheLastRemainingBackupEvenUnderAggressiveSettings()
    {
        Directory.CreateDirectory(_sync);
        var target = Target();
        target.KeepLastCount = 0;
        target.DeleteOlderThanDays = 0;

        var result = new SyncFolderBackend(_tmp).Run(_root, new[] { "settings.json" }, target);

        Assert.True(result.Ok);
        // Only one candidate ever existed (the archive this run just wrote) -
        // rule 3 (DriveRetention) forbids deleting the single remaining one.
        Assert.Single(Directory.GetFiles(_sync, "claude-backup-*.zip"));
    }

}
