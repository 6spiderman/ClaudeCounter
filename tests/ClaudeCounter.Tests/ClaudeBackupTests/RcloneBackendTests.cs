// tests/ClaudeCounter.Tests/ClaudeBackupTests/RcloneBackendTests.cs
using ClaudeBackup;
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
        public bool Exists(string file) => file == "rclone" ? RclonePresent : true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");
            if (args.Count > 0 && args[0] == "copy" && CopyShouldFail)
                return new ProcessResult(1, "", CopyStdErr);
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

    // Defense in depth, mirroring GitBackend: an unsafe relative path (here,
    // one escaping the source root) must be skipped rather than either
    // throwing or being written into the zip under an unexpected name.
    [Fact]
    public void RefusesUnsafeRelativePathWithoutThrowing()
    {
        var runner = new FakeRunner();
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json", "../escape.json" },
                new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });

        Assert.True(result.Ok);
        Assert.Contains(runner.Calls, c => c.StartsWith("rclone copy"));
    }
}
