// tests/ClaudeCounter.Tests/RestoreTests/RestoreZipSourceTests.cs
using System.IO.Compression;
using System.Text;
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

public class RestoreZipSourceTests : IDisposable
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool RclonePresent { get; set; } = true;
        public string LsJson { get; set; } = "[]";
        public bool LsJsonShouldFail { get; set; }
        public bool CopyShouldFail { get; set; }
        public string CopyStdErr { get; set; } = "";
        public byte[]? BytesToWriteOnCopy { get; set; }

        public bool Exists(string file) => file == "rclone" ? RclonePresent : true;

        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");

            if (args.Count > 0 && args[0] == "lsjson")
                return LsJsonShouldFail ? new ProcessResult(1, "", "lsjson failed") : new ProcessResult(0, LsJson, "");

            if (args.Count > 2 && args[0] == "copy")
            {
                if (CopyShouldFail)
                    return new ProcessResult(1, "", CopyStdErr);

                var remoteSource = args[1];
                var destDir = args[2];
                var fileName = remoteSource[(remoteSource.LastIndexOfAny(new[] { '/', ':' }) + 1)..];
                Directory.CreateDirectory(destDir);
                if (BytesToWriteOnCopy is not null)
                    File.WriteAllBytes(Path.Combine(destDir, fileName), BytesToWriteOnCopy);
                return new ProcessResult(0, "", "");
            }

            return new ProcessResult(0, "", "");
        }
    }

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rzstmp-{Guid.NewGuid():N}");
    private readonly string _destDir = Path.Combine(Path.GetTempPath(), $"rzsdst-{Guid.NewGuid():N}");

    // Stands in for the live config root ("~/.claude") in tests - a plain
    // temp dir, deliberately distinct from _destDir, that Materialize must
    // never write to or delete. Never created on disk; only its path is used
    // for the overlap check.
    private readonly string _protected = Path.Combine(Path.GetTempPath(), $"rzsprotected-{Guid.NewGuid():N}");

    // S14: a sync-folder transport's source directory - the equivalent of
    // "the remote" for the rclone tests above, except real backup zips are
    // written to it directly on disk instead of via a faked rclone call.
    private readonly string _syncFolder = Path.Combine(Path.GetTempPath(), $"rzssync-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        if (Directory.Exists(_destDir)) Directory.Delete(_destDir, true);
        if (Directory.Exists(_protected)) Directory.Delete(_protected, true);
        if (Directory.Exists(_syncFolder)) Directory.Delete(_syncFolder, true);
    }

    private static byte[] BuildZip(Action<ZipArchive> populate)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            populate(zip);
        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    // --- Listing ---------------------------------------------------------

    [Fact]
    public void ListSnapshotsFailsCleanlyWhenRcloneMissing()
    {
        var runner = new FakeRunner { RclonePresent = false };
        var result = new RestoreZipSource(runner).ListSnapshots(new DriveTarget { RcloneRemote = "gdrive:X" });

        Assert.False(result.Ok);
        Assert.Contains("rclone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListSnapshotsFailsCleanlyWhenLsJsonFails()
    {
        var runner = new FakeRunner { LsJsonShouldFail = true };
        var result = new RestoreZipSource(runner).ListSnapshots(new DriveTarget { RcloneRemote = "gdrive:X" });

        Assert.False(result.Ok);
        Assert.Contains("lsjson failed", result.Message);
    }

    // Parses the real `rclone lsjson` JSON shape and ignores anything that is
    // not a claude-backup-*.zip entry (a user's own file living in the same
    // remote folder, or a directory listing).
    [Fact]
    public void ListSnapshotsParsesLsJsonFixtureAndIgnoresNonMatchingNames()
    {
        var lsJson = """
            [
              {"Path":"claude-backup-20260101-090000-aaaa.zip","Name":"claude-backup-20260101-090000-aaaa.zip","Size":1234,"ModTime":"2026-01-01T09:00:00.000000000Z","IsDir":false},
              {"Path":"claude-backup-20260201-090000-bbbb.zip","Name":"claude-backup-20260201-090000-bbbb.zip","Size":5678,"ModTime":"2026-02-01T09:00:00.000000000Z","IsDir":false},
              {"Path":"notes.txt","Name":"notes.txt","Size":10,"ModTime":"2026-03-01T09:00:00.000000000Z","IsDir":false},
              {"Path":"subdir","Name":"subdir","Size":0,"ModTime":"2026-01-01T09:00:00.000000000Z","IsDir":true}
            ]
            """;
        var runner = new FakeRunner { LsJson = lsJson };

        var result = new RestoreZipSource(runner).ListSnapshots(new DriveTarget { RcloneRemote = "gdrive:X" });

        Assert.True(result.Ok);
        Assert.Equal(2, result.Snapshots.Count);
        // Newest first.
        Assert.Equal("claude-backup-20260201-090000-bbbb.zip", result.Snapshots[0].Id);
        Assert.Equal("claude-backup-20260101-090000-aaaa.zip", result.Snapshots[1].Id);
        Assert.Equal(5678, result.Snapshots[0].SizeBytes);
        Assert.DoesNotContain(result.Snapshots, s => s.Id == "notes.txt");
        Assert.DoesNotContain(result.Snapshots, s => s.Id == "subdir");
    }

    // --- Materialize -------------------------------------------------------

    [Fact]
    public void MaterializeFailsCleanlyWhenRcloneMissing()
    {
        var runner = new FakeRunner { RclonePresent = false };
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.False(result.Ok);
        Assert.Contains("rclone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaterializeFailsCleanlyWhenCopyFails()
    {
        var runner = new FakeRunner { CopyShouldFail = true, CopyStdErr = "connection refused" };
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.False(result.Ok);
        Assert.Contains("connection refused", result.Message);
    }

    // Materialisation failure path: a corrupt/non-zip file must fail cleanly
    // with a clear message, not throw out of Materialize.
    [Fact]
    public void MaterializeFailsCleanlyOnCorruptZip()
    {
        var runner = new FakeRunner { BytesToWriteOnCopy = Encoding.UTF8.GetBytes("this is not a zip file") };
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.False(result.Ok);
        Assert.Contains("zip", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaterializeRejectsAnInvalidSnapshotId()
    {
        var runner = new FakeRunner();
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "../escape.zip", _tempDir, _destDir, _protected);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("rclone copy"));
    }

    [Fact]
    public void MaterializeExtractsLegitimateEntriesAndCleansUpTheDownloadedZip()
    {
        var zipBytes = BuildZip(zip =>
        {
            AddEntry(zip, "settings.json", "{}");
            AddEntry(zip, "commands/a.md", "hello");
        });
        var runner = new FakeRunner { BytesToWriteOnCopy = zipBytes };

        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.True(result.Ok);
        Assert.Equal(_destDir, result.StagedRoot);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_destDir, "commands", "a.md")));
        // The downloaded zip is cleaned up from tempDir - it is a second
        // plaintext copy of the user's Claude config and nothing else will
        // ever remove it.
        Assert.False(File.Exists(Path.Combine(_tempDir, "claude-backup-a.zip")));
    }

    // Fix round 1, Important 1: materialising snapshot A and then, later,
    // snapshot B into the SAME destinationDir must leave exactly B's files -
    // not the union of A and B. Without clearing destinationDir first, a
    // file unique to A would classify as New/Changed against live and get
    // applied even though the user believes they are restoring B.
    [Fact]
    public void MaterializingASecondSnapshotIntoTheSameDestinationReplacesTheFirstEntirely()
    {
        var runner = new FakeRunner();

        var zipA = BuildZip(zip =>
        {
            AddEntry(zip, "settings.json", "from A");
            AddEntry(zip, "only-in-a.txt", "only A has this");
        });
        runner.BytesToWriteOnCopy = zipA;
        var first = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir, _protected);
        Assert.True(first.Ok);
        Assert.True(File.Exists(Path.Combine(_destDir, "only-in-a.txt")));

        var zipB = BuildZip(zip => AddEntry(zip, "settings.json", "from B"));
        runner.BytesToWriteOnCopy = zipB;
        var second = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-b.zip", _tempDir, _destDir, _protected);

        Assert.True(second.Ok);
        Assert.Equal("from B", File.ReadAllText(Path.Combine(_destDir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_destDir, "only-in-a.txt")));
        Assert.Equal(
            new[] { Path.Combine(_destDir, "settings.json") },
            Directory.GetFiles(_destDir, "*", SearchOption.AllDirectories));
    }

    // Fix round 1, Important 3: Materialize must refuse to write into the
    // live config root, whichever direction the overlap runs.
    [Fact]
    public void MaterializeRefusesADestinationEqualToTheProtectedRoot()
    {
        var runner = new FakeRunner { BytesToWriteOnCopy = BuildZip(zip => AddEntry(zip, "settings.json", "{}")) };
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _protected, _protected);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("rclone copy"));
    }

    [Fact]
    public void MaterializeRefusesADestinationNestedInsideTheProtectedRoot()
    {
        var nested = Path.Combine(_protected, "staging");
        var runner = new FakeRunner { BytesToWriteOnCopy = BuildZip(zip => AddEntry(zip, "settings.json", "{}")) };
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, nested, _protected);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("rclone copy"));
    }

    [Fact]
    public void MaterializeRefusesADestinationThatContainsTheProtectedRoot()
    {
        // _destDir is the parent; _protected (as a subfolder of it) stands in
        // for the live root nested underneath the requested destination -
        // the "ancestor" direction, which combined with the destination-
        // clearing fix would otherwise wipe out everything under _destDir,
        // live root included, before materialising into it.
        var protectedInsideDest = Path.Combine(_destDir, "live-root");
        var runner = new FakeRunner { BytesToWriteOnCopy = BuildZip(zip => AddEntry(zip, "settings.json", "{}")) };
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir, protectedInsideDest);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("rclone copy"));
    }

    // Restore rule 5: a zip-slip entry must be refused and nothing written
    // outside the destination, while legitimate sibling entries still
    // extract normally. Fix round 1, Important 4: covers the absolute-path
    // shape too (a rooted "C:\evil.json" entry name), not just "../" -
    // .NET's ZipArchive.CreateEntry preserves an arbitrary string verbatim
    // as the entry name, so this is a real archive shape to defend against,
    // not a hypothetical one.
    [Fact]
    public void RefusesZipSlipEntryAndStillExtractsLegitimateEntries()
    {
        var zipBytes = BuildZip(zip =>
        {
            AddEntry(zip, "settings.json", "{}");
            AddEntry(zip, "../evil.json", "should never be written");
            AddEntry(zip, @"C:\evil.json", "should never be written either");
        });
        var runner = new FakeRunner { BytesToWriteOnCopy = zipBytes };

        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.True(result.Ok);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));

        var escapedPath = Path.Combine(Path.GetDirectoryName(_destDir)!, "evil.json");
        Assert.False(File.Exists(escapedPath));
        Assert.False(File.Exists(Path.Combine(_destDir, "evil.json")));
        Assert.False(File.Exists(@"C:\evil.json"));

        // Nothing written under destDir beyond the one legitimate entry -
        // proves neither zip-slip entry landed anywhere inside destDir under
        // some other name either.
        Assert.Equal(
            new[] { Path.Combine(_destDir, "settings.json") },
            Directory.GetFiles(_destDir, "*", SearchOption.AllDirectories));
    }

    // Restore rule 4: a denylisted entry must be refused, while legitimate
    // sibling entries still extract normally.
    [Fact]
    public void RefusesDenylistedEntryAndStillExtractsLegitimateEntries()
    {
        var zipBytes = BuildZip(zip =>
        {
            AddEntry(zip, "settings.json", "{}");
            AddEntry(zip, ".credentials.json", "secret");
        });
        var runner = new FakeRunner { BytesToWriteOnCopy = zipBytes };

        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.True(result.Ok);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_destDir, ".credentials.json")));
    }

    // --- S14: sync-folder transport ------------------------------------------
    //
    // No IProcessRunner involved at all for this transport - a real zip file
    // is written directly under _syncFolder, exactly what SyncFolderBackend
    // itself would have left there.

    private DriveTarget SyncTarget() => new() { Transport = DriveTransport.SyncFolder, FolderPath = _syncFolder };

    private string WriteRealZip(string fileName, byte[] bytes, DateTimeOffset? modified = null)
    {
        Directory.CreateDirectory(_syncFolder);
        var path = Path.Combine(_syncFolder, fileName);
        File.WriteAllBytes(path, bytes);
        if (modified is { } m) File.SetLastWriteTimeUtc(path, m.UtcDateTime);
        return path;
    }

    [Fact]
    public void ListSnapshotsFromFolderReturnsEmptySuccessWhenFolderDoesNotExistYet()
    {
        var result = new RestoreZipSource(new FakeRunner()).ListSnapshots(SyncTarget());

        Assert.True(result.Ok);
        Assert.Empty(result.Snapshots);
    }

    [Fact]
    public void ListSnapshotsFromFolderFailsCleanlyWhenFolderPathIsBlank()
    {
        var result = new RestoreZipSource(new FakeRunner())
            .ListSnapshots(new DriveTarget { Transport = DriveTransport.SyncFolder, FolderPath = "" });

        Assert.False(result.Ok);
    }

    [Fact]
    public void ListSnapshotsFromFolderListsRealFilesNewestFirstAndIgnoresNonMatchingNames()
    {
        var now = DateTimeOffset.UtcNow;
        WriteRealZip("claude-backup-20260101-090000-aaaa.zip", Encoding.UTF8.GetBytes("old"), now.AddDays(-10));
        WriteRealZip("claude-backup-20260201-090000-bbbb.zip", Encoding.UTF8.GetBytes("newer"), now);
        WriteRealZip("notes.txt", Encoding.UTF8.GetBytes("not ours"), now);

        var result = new RestoreZipSource(new FakeRunner()).ListSnapshots(SyncTarget());

        Assert.True(result.Ok);
        Assert.Equal(2, result.Snapshots.Count);
        Assert.Equal("claude-backup-20260201-090000-bbbb.zip", result.Snapshots[0].Id);
        Assert.Equal("claude-backup-20260101-090000-aaaa.zip", result.Snapshots[1].Id);
        Assert.DoesNotContain(result.Snapshots, s => s.Id == "notes.txt");
    }

    [Fact]
    public void MaterializeFromFolderRejectsAnInvalidSnapshotId()
    {
        var result = new RestoreZipSource(new FakeRunner())
            .Materialize(SyncTarget(), "../escape.zip", _tempDir, _destDir, _protected);

        Assert.False(result.Ok);
    }

    [Fact]
    public void MaterializeFromFolderFailsCleanlyWhenSnapshotIsMissing()
    {
        Directory.CreateDirectory(_syncFolder);
        var result = new RestoreZipSource(new FakeRunner())
            .Materialize(SyncTarget(), "claude-backup-missing.zip", _tempDir, _destDir, _protected);

        Assert.False(result.Ok);
    }

    // No download step: the zip is opened directly out of FolderPath, and -
    // unlike the rclone transport's disposable temp copy - nothing is ever
    // deleted from FolderPath, because that zip IS the backup.
    [Fact]
    public void MaterializeFromFolderExtractsInPlaceAndNeverDeletesTheSourceZip()
    {
        var zipBytes = BuildZip(zip =>
        {
            AddEntry(zip, "settings.json", "{}");
            AddEntry(zip, "commands/a.md", "hello");
        });
        var zipPath = WriteRealZip("claude-backup-a.zip", zipBytes);

        var result = new RestoreZipSource(new FakeRunner())
            .Materialize(SyncTarget(), "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.True(result.Ok);
        Assert.Equal(_destDir, result.StagedRoot);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_destDir, "commands", "a.md")));
        // The source zip in the sync folder must still be there afterwards -
        // it is the actual backup, not a disposable temp copy.
        Assert.True(File.Exists(zipPath));
    }

    [Fact]
    public void MaterializeFromFolderRefusesADestinationEqualToTheProtectedRoot()
    {
        WriteRealZip("claude-backup-a.zip", BuildZip(zip => AddEntry(zip, "settings.json", "{}")));

        var result = new RestoreZipSource(new FakeRunner())
            .Materialize(SyncTarget(), "claude-backup-a.zip", _tempDir, _protected, _protected);

        Assert.False(result.Ok);
    }

    // Restore rule 5, sync-folder side: a zip-slip entry must be refused
    // while legitimate sibling entries still extract normally - the shared
    // ExtractSafely path, exercised through this transport specifically.
    [Fact]
    public void MaterializeFromFolderRefusesZipSlipEntryAndStillExtractsLegitimateEntries()
    {
        var zipBytes = BuildZip(zip =>
        {
            AddEntry(zip, "settings.json", "{}");
            AddEntry(zip, "../evil.json", "should never be written");
        });
        WriteRealZip("claude-backup-a.zip", zipBytes);

        var result = new RestoreZipSource(new FakeRunner())
            .Materialize(SyncTarget(), "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.True(result.Ok);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));
        var escapedPath = Path.Combine(Path.GetDirectoryName(_destDir)!, "evil.json");
        Assert.False(File.Exists(escapedPath));
    }

    // Restore rule 4, sync-folder side: a denylisted entry must be refused
    // while legitimate sibling entries still extract normally.
    [Fact]
    public void MaterializeFromFolderRefusesDenylistedEntryAndStillExtractsLegitimateEntries()
    {
        var zipBytes = BuildZip(zip =>
        {
            AddEntry(zip, "settings.json", "{}");
            AddEntry(zip, ".credentials.json", "secret");
        });
        WriteRealZip("claude-backup-a.zip", zipBytes);

        var result = new RestoreZipSource(new FakeRunner())
            .Materialize(SyncTarget(), "claude-backup-a.zip", _tempDir, _destDir, _protected);

        Assert.True(result.Ok);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_destDir, ".credentials.json")));
    }
}
