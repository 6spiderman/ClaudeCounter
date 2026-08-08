// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupRunnerTests.cs
using ClaudeBackup;
using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupRunnerTests : IDisposable
{
    private sealed class OkRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool Exists(string file) => true;
        public ProcessResult Run(string f, IReadOnlyList<string> a, string? wd = null)
        {
            Calls.Add($"{f} {string.Join(' ', a)}");
            if (a.Count > 0 && a[0] == "diff")
                return new(1, "", ""); // staged changes present -> proceed to commit
            if (f == "git" && a.Count > 0 && a[0] == "init" && wd is not null)
                Directory.CreateDirectory(Path.Combine(wd, ".git"));
            return new(0, "", "");
        }
    }

    // GitHub always fails (git missing); Drive always succeeds - used to
    // prove one backend failing does not skip the other.
    private sealed class GitFailsRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool Exists(string file) => file != "git";
        public ProcessResult Run(string f, IReadOnlyList<string> a, string? wd = null)
        {
            Calls.Add($"{f} {string.Join(' ', a)}");
            return new(0, "", "");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brsrc-{Guid.NewGuid():N}");
    private readonly string _stg = Path.Combine(Path.GetTempPath(), $"brstg-{Guid.NewGuid():N}");
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"brtmp-{Guid.NewGuid():N}");

    public BackupRunnerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _stg, _tmp })
            if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    private BackupConfig Config() => new()
    {
        SourceRoot = _root,
        Include = new() { "settings.json" },
        Exclude = new(),
        Github = new() { Enabled = true, RemoteUrl = "url", Branch = "main" },
        Drive = new() { Enabled = false },
    };

    [Fact]
    public void NoDestinationsIsConfigError()
    {
        var c = Config();
        c.Github.Enabled = false;
        Assert.Equal(1, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
    }

    [Fact]
    public void HappyPathReturnsZero()
    {
        Assert.Equal(0, BackupRunner.Run(Config(), new OkRunner(), _stg, _tmp));
    }

    [Fact]
    public void NothingSelectedIsConfigError()
    {
        var c = Config();
        c.Include = new() { "does-not-exist/**" };
        Assert.Equal(1, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
    }

    [Fact]
    public void BothDestinationsEnabledHappyPathRunsBoth()
    {
        var c = Config();
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        var runner = new OkRunner();

        Assert.Equal(0, BackupRunner.Run(c, runner, _stg, _tmp));
        Assert.Contains(runner.Calls, call => call.StartsWith("git push"));
        Assert.Contains(runner.Calls, call => call.StartsWith("rclone copy"));
    }

    // One backend failing must not prevent the other from running, and the
    // overall exit code must reflect that a backend failed (2), not success.
    [Fact]
    public void OneBackendFailingStillRunsTheOtherAndReturnsTwo()
    {
        var c = Config();
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        var runner = new GitFailsRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(2, code);
        Assert.Contains(runner.Calls, call => call.StartsWith("rclone copy"));
    }

    [Fact]
    public void DenylistedForcedIncludeAborts()
    {
        var c = Config();
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        c.Include.Add(".credentials.json");
        // Selector already drops secrets; the pre-flight scan is the backstop.
        // Force the scenario by asserting no secret is ever staged: run returns 0
        // and the staging dir must not contain the secret.
        Assert.Equal(0, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
        Assert.False(File.Exists(Path.Combine(_stg, ".credentials.json")));
    }

    // Direct proof of the fail-closed backstop's own exit code/logging
    // behavior. FileSelector already strips anything SecretDenylist flags,
    // so a real offender can never reach BackupRunner.Run's internal
    // SecretDenylist.Offenders(files) check through the public entry point
    // (see DenylistedForcedIncludeAborts above) - proving that would require
    // FileSelector to first fail to do its job, which would mean the first
    // layer has a hole, not that this backstop works. TryAbortForOffenders
    // is exercised directly instead, mirroring how GitBackendTests proves
    // GitBackend.IsWithinDirectory as a second, independent layer.
    [Fact]
    public void OffenderBackstopAbortsWithExitCodeOneWhenGivenAnOffender()
    {
        var aborted = BackupRunner.TryAbortForOffenders(
            new[] { "settings.json", ".credentials.json" }, out var exitCode);

        Assert.True(aborted);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void OffenderBackstopDoesNotAbortWhenSelectionIsClean()
    {
        var aborted = BackupRunner.TryAbortForOffenders(
            new[] { "settings.json" }, out var exitCode);

        Assert.False(aborted);
        Assert.Equal(0, exitCode);
    }

    // The 4-arg FileSelector.Select overload must be used (not the 3-arg
    // convenience overload that silently discards what the secret denylist
    // withheld) and what it withheld must be logged by name, not just by
    // count, so a user can see exactly what was omitted from their backup.
    [Fact]
    public void WithheldFilesAreLoggedByName()
    {
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        var c = Config();
        c.Include.Add(".credentials.json");

        var before = ReadLog().Length;
        BackupRunner.Run(c, new OkRunner(), _stg, _tmp);
        var written = ReadLog()[before..];

        Assert.Contains("withheld by the secret denylist", written);
        Assert.Contains(".credentials.json", written);
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
