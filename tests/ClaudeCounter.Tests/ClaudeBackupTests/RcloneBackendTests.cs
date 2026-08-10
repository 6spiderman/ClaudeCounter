// tests/ClaudeCounter.Tests/ClaudeBackupTests/RcloneBackendTests.cs
using System.IO.Compression;
using ClaudeBackup;
using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class RcloneBackendTests : IDisposable
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool RclonePresent { get; set; } = true;
        public bool CopyShouldFail { get; set; }
        public string CopyStdErr { get; set; } = "";

        // Populated the moment "rclone copy" is invoked, by actually opening
        // the zip at that path - the same moment a real rclone would read it,
        // and before RcloneBackend's finally block deletes it. This is what
        // proves the zip was not empty (I-4) and that a guard rejecting an
        // entry actually kept it out (I-3), rather than merely proving
        // Run() called something named "rclone copy".
        public List<string>? ZipEntriesAtCopyTime { get; private set; }

        // When set, "rclone copy" opens the zip with a share mode that
        // excludes FileShare.Delete and keeps the handle open past the
        // call - simulating an AV scanner or indexer holding a lock on a
        // just-written file, the real-world cause of RcloneBackend's own
        // File.Delete failing in its finally block. The test that uses this
        // must dispose it once done asserting.
        public bool LockZipDuringCopy { get; set; }
        public FileStream? HeldLock { get; private set; }

        // S8: retention pruning support - lsjson listing (either canned JSON
        // or a forced failure) and every deletefile call's remote path, so a
        // test can assert exactly which names were pruned without a real
        // rclone remote.
        public string LsJsonResponse { get; set; } = "[]";
        public bool LsJsonShouldFail { get; set; }
        public List<string> DeletedRemotePaths { get; } = new();

        public bool Exists(string file) => file == "rclone" ? RclonePresent : true;

        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");

            if (args.Count > 1 && args[0] == "copy")
            {
                using (var zip = ZipFile.OpenRead(args[1]))
                    ZipEntriesAtCopyTime = zip.Entries.Select(e => e.FullName).ToList();

                if (LockZipDuringCopy)
                    HeldLock = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);

                if (CopyShouldFail)
                    return new ProcessResult(1, "", CopyStdErr);
            }

            if (args.Count > 0 && args[0] == "lsjson")
                return LsJsonShouldFail
                    ? new ProcessResult(1, "", "lsjson failed")
                    : new ProcessResult(0, LsJsonResponse, "");

            if (args.Count > 1 && args[0] == "deletefile")
            {
                DeletedRemotePaths.Add(args[1]);
                return new ProcessResult(0, "", "");
            }

            return new ProcessResult(0, "", "");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rcsrc-{Guid.NewGuid():N}");
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"rctmp-{Guid.NewGuid():N}");

    public RcloneBackendTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    [Fact]
    public void MissingRcloneFailsCleanly()
    {
        var runner = new FakeRunner { RclonePresent = false };
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });
        Assert.False(result.Ok);
        Assert.Contains("rclone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZipsAndCopies()
    {
        var runner = new FakeRunner();
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });
        Assert.True(result.Ok);
        Assert.Contains(runner.Calls, c => c.StartsWith("rclone copy"));
    }

    // I-3/I-4 combined: creates a REAL escaping file on disk (unlike the old
    // version of this test, which passed "../escape.json" without ever
    // creating it, so the guard being deleted entirely still passed via
    // File.Exists(src) == false), plus a real nested file, and inspects the
    // zip's actual entries at the moment rclone would read them - not just
    // that "rclone copy" was called. This fails if RelativePathGuard is
    // removed (escape.json now exists on disk, so an unguarded backend would
    // archive it under an escaping name) and fails if the zip were empty or
    // missing the legitimate nested entry.
    [Fact]
    public void ZipContainsExactlyTheSafeSelectedFilesNotAnEscapingOne()
    {
        Directory.CreateDirectory(Path.Combine(_root, "commands"));
        File.WriteAllText(Path.Combine(_root, "commands", "a.md"), "hello");

        var escapePath = Path.Combine(Path.GetDirectoryName(_root)!, "escape.json");
        File.WriteAllText(escapePath, "should never be archived");
        try
        {
            var runner = new FakeRunner();
            var result = new RcloneBackend(runner, _tmp).Run(
                _root,
                new[] { "settings.json", "commands/a.md", "../escape.json" },
                new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

            Assert.True(result.Ok);
            Assert.NotNull(runner.ZipEntriesAtCopyTime);
            Assert.Equal(
                new[] { "commands/a.md", "settings.json" },
                runner.ZipEntriesAtCopyTime!.OrderBy(e => e, StringComparer.Ordinal));
        }
        finally
        {
            File.Delete(escapePath);
        }
    }

    // A leftover zip is a plaintext copy of the user's Claude config sitting
    // on disk - the temp directory must be empty again once a successful
    // run returns.
    [Fact]
    public void SuccessfulRunLeavesNoTempZipBehind()
    {
        var runner = new FakeRunner();
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

        Assert.True(result.Ok);
        Assert.True(Directory.Exists(_tmp));
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    // The other half of the same guarantee: an upload failure must still
    // report a failed BackendResult AND must not leave the zip on disk.
    [Fact]
    public void UploadFailureReturnsFailedResultAndDeletesTempZip()
    {
        var runner = new FakeRunner { CopyShouldFail = true, CopyStdErr = "connection refused" };
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

        Assert.False(result.Ok);
        Assert.Contains("connection refused", result.Message);
        Assert.True(Directory.Exists(_tmp));
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    // rclone's stderr can echo a remote spec verbatim on failure (e.g. a
    // WebDAV/S3 remote URL with an embedded credential) - that must never
    // reach the BackendResult.Message that Run returns.
    [Fact]
    public void UploadFailureScrubsCredentialFromRemoteSpecInResultMessage()
    {
        var runner = new FakeRunner
        {
            CopyShouldFail = true,
            CopyStdErr = "failed to copy: couldn't list directory: " +
                         "https://user:supersecrettoken@example.com/remote-dav/: 401 Unauthorized",
        };
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "webdav:X" });

        Assert.False(result.Ok);
        Assert.DoesNotContain("supersecrettoken", result.Message);
        Assert.Contains("example.com", result.Message);
    }

    // Fail-closed backstop at the point of write (mirrors GitBackend's
    // MirrorFiles check): FileSelector and BackupRunner both already filter
    // secrets out upstream, but Run is public and takes an arbitrary file
    // list, so a secret-named entry reaching this loop directly must never
    // be archived.
    [Fact]
    public void RefusesToArchiveSecretNamedFileEvenIfPassedDirectly()
    {
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        var runner = new FakeRunner();

        var result = new RcloneBackend(runner, _tmp).Run(
            _root,
            new[] { "settings.json", ".credentials.json" },
            new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

        Assert.True(result.Ok);
        Assert.NotNull(runner.ZipEntriesAtCopyTime);
        Assert.DoesNotContain(".credentials.json", runner.ZipEntriesAtCopyTime);
    }

    // M-2: Run is public and takes an arbitrary file list - two entries that
    // collide on the same zip entry name must not silently overwrite one
    // archive entry with another.
    [Fact]
    public void RefusesDuplicateZipEntryName()
    {
        var runner = new FakeRunner();

        var result = new RcloneBackend(runner, _tmp).Run(
            _root,
            new[] { "settings.json", "settings.json" },
            new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

        Assert.True(result.Ok);
        Assert.NotNull(runner.ZipEntriesAtCopyTime);
        Assert.Single(runner.ZipEntriesAtCopyTime!, e => e == "settings.json");
    }

    // I-2 (fix round 2): a zip left behind by a previous run (its own delete
    // having failed) must not linger forever - but only once it is old
    // enough that it cannot plausibly belong to a still-running instance.
    // LastWriteTimeUtc is set explicitly, back beyond the 24-hour age gate,
    // rather than relying on wall-clock timing to make the file "old".
    [Fact]
    public void SweepsGenuinelyOldStaleZipFromPreviousRun()
    {
        Directory.CreateDirectory(_tmp);
        var stale = Path.Combine(_tmp, "claude-backup-20200101-000000-deadbeefdeadbeefdeadbeefdeadbeef.zip");
        File.WriteAllText(stale, "leftover plaintext from a previous run");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(25));

        var runner = new FakeRunner();
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

        Assert.True(result.Ok);
        Assert.False(File.Exists(stale));
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    // The other half of the same fix: the age gate exists specifically
    // because the zip file name now carries a GUID (M-1), which means a
    // naive "delete anything matching the pattern" sweep would just as
    // happily delete a CONCURRENTLY RUNNING instance's own in-flight
    // archive - e.g. a scheduled run racing a tray "back up now" - as a
    // genuinely abandoned one. A zip younger than the threshold must survive
    // the sweep even though it matches the glob, while the current run's
    // own zip is still cleaned up normally.
    [Fact]
    public void RecentZipMatchingThePatternSurvivesTheSweep()
    {
        Directory.CreateDirectory(_tmp);
        var recent = Path.Combine(_tmp, "claude-backup-20990101-000000-cafebabecafebabecafebabecafebabe.zip");
        File.WriteAllText(recent, "plausibly a concurrently running instance's own zip");
        // No explicit SetLastWriteTimeUtc: File.WriteAllText just now leaves
        // it at "now", well inside the 24-hour age gate.

        try
        {
            var runner = new FakeRunner();
            var result = new RcloneBackend(runner, _tmp)
                .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

            Assert.True(result.Ok);
            Assert.True(File.Exists(recent));
            // The run's own zip was still cleaned up normally - the recent
            // file is the ONLY thing left in the temp dir.
            Assert.Equal(new[] { recent }, Directory.GetFiles(_tmp));
        }
        finally
        {
            if (File.Exists(recent)) File.Delete(recent);
        }
    }

    // M-1: two zips created in the same wall-clock second (e.g. a scheduled
    // run racing a tray "back up now") must not collide on file name - a
    // collision would make the second ZipFile.Open throw, and would risk the
    // first run's finally block deleting the SECOND run's still-uploading
    // zip out from under it. Simulated here by running twice back to back
    // and asserting both succeed and the directory ends up clean, which
    // would not hold if the second run's zip creation threw because the
    // first run's file (if not yet deleted) still occupied the same name.
    [Fact]
    public void ConsecutiveRunsInTheSameSecondDoNotCollideOnZipName()
    {
        var runner = new FakeRunner();
        var backend = new RcloneBackend(runner, _tmp);
        var target = new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" };

        var first = backend.Run(_root, new[] { "settings.json" }, target);
        var second = backend.Run(_root, new[] { "settings.json" }, target);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    // I-2: a failed temp-zip delete must be loud, not swallowed - it is the
    // one failure in this class that most needs to be visible, since the
    // file left behind is a plaintext copy of the user's Claude config.
    [Fact]
    public void FailedZipDeleteIsLoggedLoudlyAndDoesNotOverrideTheUploadResult()
    {
        var runner = new FakeRunner { LockZipDuringCopy = true };
        var backend = new RcloneBackend(runner, _tmp);
        var target = new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" };

        var before = ReadLog().Length;
        try
        {
            var result = backend.Run(_root, new[] { "settings.json" }, target);
            var written = ReadLog()[before..];

            // The upload itself succeeded; only cleanup failed - Run's
            // result must reflect the former, not the latter.
            Assert.True(result.Ok);
            Assert.Single(Directory.GetFiles(_tmp)); // delete failed -> zip still present
            Assert.Contains("failed to delete temp zip", written);
            Assert.Contains("plaintext copy of the backup remains", written);
        }
        finally
        {
            runner.HeldLock?.Dispose();
            foreach (var leftover in Directory.GetFiles(_tmp))
                File.Delete(leftover);
        }
    }

    // S8: retention pruning is a no-op (no "lsjson" call at all) when
    // neither DriveTarget.KeepLastCount nor DeleteOlderThanDays is
    // configured - the common case, and the reason a user who never opens
    // the Advanced dialog pays no extra rclone call per run.
    [Fact]
    public void PruningIsSkippedEntirelyWhenNeitherRetentionSettingIsConfigured()
    {
        var runner = new FakeRunner();
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

        Assert.True(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("rclone lsjson"));
        Assert.Empty(runner.DeletedRemotePaths);
    }

    // Retention safety rule 1 (design spec, Part 2): pruning runs ONLY after
    // a successful upload - a failed rclone copy must never even attempt to
    // list the remote, let alone delete anything from it.
    [Fact]
    public void FailedUploadPerformsNoPruning()
    {
        var runner = new FakeRunner { CopyShouldFail = true, CopyStdErr = "connection refused" };
        var target = new DriveTarget
        {
            Enabled = true,
            RcloneRemote = "gdrive:X",
            KeepLastCount = 1,
            DeleteOlderThanDays = 1,
        };

        var result = new RcloneBackend(runner, _tmp).Run(_root, new[] { "settings.json" }, target);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("rclone lsjson"));
        Assert.Empty(runner.DeletedRemotePaths);
    }

    // A failed `rclone lsjson` must be logged and pruning skipped for this
    // run - but the run itself, which already succeeded (the upload went
    // through), must still be reported as successful.
    [Fact]
    public void FailedListingLogsAndLeavesTheRunSuccessful()
    {
        var runner = new FakeRunner { LsJsonShouldFail = true };
        var target = new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X", KeepLastCount = 1 };

        var before = ReadLog().Length;
        var result = new RcloneBackend(runner, _tmp).Run(_root, new[] { "settings.json" }, target);
        var written = ReadLog()[before..];

        Assert.True(result.Ok);
        Assert.Empty(runner.DeletedRemotePaths);
        Assert.Contains("lsjson failed", written);
        Assert.Contains("skipping retention pruning", written);
    }

    // The integration path: a successful upload, a listing with entries
    // beyond the keep-last window, and the doomed ones actually deleted via
    // `rclone deletefile` against the right remote path.
    [Fact]
    public void SuccessfulUploadPrunesOldEntriesPastTheKeepLastWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var lsJson = $$"""
            [
              {"Name": "claude-backup-newest.zip", "ModTime": "{{now:o}}", "IsDir": false},
              {"Name": "claude-backup-oldest.zip", "ModTime": "{{now.AddDays(-10):o}}", "IsDir": false}
            ]
            """;
        var runner = new FakeRunner { LsJsonResponse = lsJson };
        var target = new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X", KeepLastCount = 1 };

        var result = new RcloneBackend(runner, _tmp).Run(_root, new[] { "settings.json" }, target);

        Assert.True(result.Ok);
        Assert.Contains(runner.Calls, c => c.StartsWith("rclone lsjson gdrive:X"));
        Assert.Equal(new[] { "gdrive:X/claude-backup-oldest.zip" }, runner.DeletedRemotePaths);
    }

    // Retention safety rule 3 (design spec, Part 2): only claude-backup-*.zip
    // entries are candidates - something else living in the same remote
    // folder must survive pruning even under aggressive settings.
    [Fact]
    public void PruningNeverDeletesAFileThatIsNotOurs()
    {
        var now = DateTimeOffset.UtcNow;
        // Two genuine candidates plus a non-matching file, all equally
        // stale, under maximally aggressive settings (keep 0, older-than 0
        // days) - proves both that the non-matching file is never touched
        // AND that pruning still does its real job on the entries that ARE
        // ours (all but the newest of the two).
        var lsJson = $$"""
            [
              {"Name": "claude-backup-a.zip", "ModTime": "{{now.AddDays(-100):o}}", "IsDir": false},
              {"Name": "claude-backup-b.zip", "ModTime": "{{now.AddDays(-200):o}}", "IsDir": false},
              {"Name": "some-other-file.txt", "ModTime": "{{now.AddDays(-100):o}}", "IsDir": false}
            ]
            """;
        var runner = new FakeRunner { LsJsonResponse = lsJson };
        var target = new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X", KeepLastCount = 0, DeleteOlderThanDays = 0 };

        var result = new RcloneBackend(runner, _tmp).Run(_root, new[] { "settings.json" }, target);

        Assert.True(result.Ok);
        Assert.Equal(new[] { "gdrive:X/claude-backup-b.zip" }, runner.DeletedRemotePaths);
    }

    private static string ReadLog()
    {
        if (Log.FilePath is not { } path || !File.Exists(path))
            return string.Empty;

        // Opened share-all: the logger may be touched by other tests running
        // in parallel (see FileSelectorTests.cs for the same pattern).
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
