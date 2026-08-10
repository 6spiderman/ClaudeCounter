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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        if (Directory.Exists(_destDir)) Directory.Delete(_destDir, true);
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
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir);

        Assert.False(result.Ok);
        Assert.Contains("rclone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaterializeFailsCleanlyWhenCopyFails()
    {
        var runner = new FakeRunner { CopyShouldFail = true, CopyStdErr = "connection refused" };
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir);

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
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir);

        Assert.False(result.Ok);
        Assert.Contains("zip", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaterializeRejectsAnInvalidSnapshotId()
    {
        var runner = new FakeRunner();
        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "../escape.zip", _tempDir, _destDir);

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
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir);

        Assert.True(result.Ok);
        Assert.Equal(_destDir, result.StagedRoot);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_destDir, "commands", "a.md")));
        // The downloaded zip is cleaned up from tempDir - it is a second
        // plaintext copy of the user's Claude config and nothing else will
        // ever remove it.
        Assert.False(File.Exists(Path.Combine(_tempDir, "claude-backup-a.zip")));
    }

    // Restore rule 5: a zip-slip entry must be refused and nothing written
    // outside the destination, while legitimate sibling entries still
    // extract normally.
    [Fact]
    public void RefusesZipSlipEntryAndStillExtractsLegitimateEntries()
    {
        var zipBytes = BuildZip(zip =>
        {
            AddEntry(zip, "settings.json", "{}");
            AddEntry(zip, "../evil.json", "should never be written");
        });
        var runner = new FakeRunner { BytesToWriteOnCopy = zipBytes };

        var result = new RestoreZipSource(runner).Materialize(
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir);

        Assert.True(result.Ok);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));

        var escapedPath = Path.Combine(Path.GetDirectoryName(_destDir)!, "evil.json");
        Assert.False(File.Exists(escapedPath));
        Assert.False(File.Exists(Path.Combine(_destDir, "evil.json")));

        // Nothing written under destDir beyond the one legitimate entry -
        // proves the zip-slip entry did not land anywhere inside destDir
        // under some other name either.
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
            new DriveTarget { RcloneRemote = "gdrive:X" }, "claude-backup-a.zip", _tempDir, _destDir);

        Assert.True(result.Ok);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_destDir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_destDir, ".credentials.json")));
    }
}
