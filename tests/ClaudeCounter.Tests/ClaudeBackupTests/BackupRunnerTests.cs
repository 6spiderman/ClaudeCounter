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

    // Exists("git") throws instead of returning false - simulates a
    // misbehaving IProcessRunner (e.g. a future tray-supplied one). Proves
    // backend independence is structural: GitHub blowing up must still let
    // Drive run, and the process must not crash.
    private sealed class GitExistsThrowsRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool Exists(string file)
        {
            if (file == "git")
                throw new InvalidOperationException("simulated Exists() failure");
            return true;
        }
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

    // Both targets get the same "settings.json" selection by default,
    // mirroring what the old single shared Include/Exclude used to produce
    // for both destinations - most of the existing tests below only care
    // about one destination at a time and should not have to think about
    // the other's selection to keep passing.
    private BackupConfig Config() => new()
    {
        SourceRoot = _root,
        Github = new() { Enabled = true, RemoteUrl = "url", Branch = "main", Include = new() { "settings.json" }, Exclude = new() },
        Drive = new() { Enabled = false, Include = new() { "settings.json" }, Exclude = new() },
    };

    [Fact]
    public void NoDestinationsIsConfigError()
    {
        var c = Config();
        c.Github.Enabled = false;
        Assert.Equal(1, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
    }

    // M-3: an enabled destination with no remote configured (the type
    // default is an empty string for both RemoteUrl and RcloneRemote) is a
    // configuration mistake, not a backend failure - it must return 1, not
    // eventually fail a backend and return 2.
    [Fact]
    public void EnabledGithubWithoutRemoteUrlIsConfigError()
    {
        var c = Config();
        c.Github.RemoteUrl = "";
        var runner = new OkRunner();

        Assert.Equal(1, BackupRunner.Run(c, runner, _stg, _tmp));
        Assert.Empty(runner.Calls);
    }

    // I5: SettingsForm trims the branch textbox, so clearing it and saving
    // persists "". Without this guard that reaches 'git init -b ""' and
    // surfaces as exit 2 ("a backend failed") instead of exit 1 ("fix your
    // config") - the same category of bug the RemoteUrl guard above fixes.
    [Fact]
    public void EnabledGithubWithBlankBranchIsConfigError()
    {
        var c = Config();
        c.Github.Branch = "   ";
        var runner = new OkRunner();

        Assert.Equal(1, BackupRunner.Run(c, runner, _stg, _tmp));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void EnabledDriveWithoutRcloneRemoteIsConfigError()
    {
        var c = Config();
        c.Github.Enabled = false;
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "";
        var runner = new OkRunner();

        Assert.Equal(1, BackupRunner.Run(c, runner, _stg, _tmp));
        Assert.Empty(runner.Calls);
    }

    // Fix round 2: the review found this interaction was untested -
    // EnabledGithubWithoutRemoteUrlIsConfigError above only exercises the
    // case where Drive is disabled too, so it never proves GitHub's own
    // misconfiguration does not take a sibling, fully-configured Drive down
    // with it. GitHub is enabled but half set up (blank RemoteUrl); Drive is
    // enabled, configured, and has files - the run must still exit 0 and
    // actually upload via Drive, with GitHub silently skipped (logged, not
    // fatal).
    [Fact]
    public void EnabledGithubWithoutRemoteUrlDoesNotStopConfiguredDriveFromRunning()
    {
        var c = Config();
        c.Github.RemoteUrl = "";
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        c.Drive.Include = new() { "settings.json" };
        var runner = new OkRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(0, code);
        Assert.Contains(runner.Calls, call => call.StartsWith("rclone copy"));
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("git push"));
    }

    // Mirror of the above, in the other direction: Drive enabled but
    // misconfigured (blank RcloneRemote) must not stop a fully configured,
    // file-having GitHub from running.
    [Fact]
    public void EnabledDriveWithoutRcloneRemoteDoesNotStopConfiguredGithubFromRunning()
    {
        var c = Config();
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "";
        var runner = new OkRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(0, code);
        Assert.Contains(runner.Calls, call => call.StartsWith("git push"));
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("rclone copy"));
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
        c.Github.Include = new() { "does-not-exist/**" };
        Assert.Equal(1, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
    }

    // S5: with both destinations enabled but empty selections, the run must
    // still fail closed - "nothing selected on any enabled destination" is
    // exit 1 regardless of how many destinations are enabled.
    [Fact]
    public void BothDestinationsEnabledButNothingSelectedAnywhereIsConfigError()
    {
        var c = Config();
        c.Github.Include = new() { "does-not-exist/**" };
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        c.Drive.Include = new() { "also-does-not-exist/**" };
        var runner = new OkRunner();

        Assert.Equal(1, BackupRunner.Run(c, runner, _stg, _tmp));
        Assert.Empty(runner.Calls);
    }

    // S5: the core per-destination behaviour - GitHub's own selection is
    // empty (a GitHub-side config mistake) while Drive's selection has
    // files. This must NOT fail the whole run: Drive still runs and
    // succeeds, GitHub is skipped, and the run exits 0.
    [Fact]
    public void GithubEmptySelectionDoesNotFailDriveWhichStillRuns()
    {
        var c = Config();
        c.Github.Include = new() { "does-not-exist/**" };
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        c.Drive.Include = new() { "settings.json" };
        var runner = new OkRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(0, code);
        Assert.Contains(runner.Calls, call => call.StartsWith("rclone copy"));
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("git push"));
    }

    // S5, the reverse of the above: Drive's own selection is empty while
    // GitHub's has files. GitHub still runs; Drive is skipped; exit 0.
    [Fact]
    public void DriveEmptySelectionDoesNotFailGithubWhichStillRuns()
    {
        var c = Config();
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        c.Drive.Include = new() { "does-not-exist/**" };
        var runner = new OkRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(0, code);
        Assert.Contains(runner.Calls, call => call.StartsWith("git push"));
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("rclone copy"));
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

    // I-1: backend independence must be structural, not incidental on both
    // backends happening to catch their own exceptions internally. Neither
    // GitBackend.Run nor RcloneBackend.Run guards its own IProcessRunner.Exists
    // call inside its try block, so if Exists throws, BackupRunner itself
    // must be the thing that catches it - otherwise the exception would
    // propagate past the Drive backend entirely and crash the process
    // instead of returning exit code 2.
    [Fact]
    public void GitBackendThrowingDoesNotSkipDriveAndStillReturnsTwo()
    {
        var c = Config();
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        var runner = new GitExistsThrowsRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(2, code);
        Assert.Contains(runner.Calls, call => call.StartsWith("rclone copy"));
    }

    [Fact]
    public void DenylistedForcedIncludeIsNeverStaged()
    {
        var c = Config();
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        c.Github.Include.Add(".credentials.json");
        // Selector already drops secrets; the pre-flight scan is the backstop.
        // Force the scenario by asserting no secret is ever staged: run returns 0
        // and the staging dir must not contain the secret.
        Assert.Equal(0, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
        Assert.False(File.Exists(Path.Combine(_stg, ".credentials.json")));
    }

    // Direct proof of the offender-abort WIRING: a selector that hands back
    // a secret-named file (something FileSelector itself would never do,
    // since it filters through the same SecretDenylist before Run's public
    // overload ever sees the list) must still make Run abort with exit 1
    // and must never let either backend touch the process runner. This is
    // the fact that actually mattered and was previously untested: not
    // whether SecretDenylist.Offenders reports offenders (SecretDenylistTests
    // already covers that on an already-public method), but whether Run
    // calls it, before any backend, and returns the right code when it does.
    [Fact]
    public void OffenderFromSelectorAbortsBeforeAnyBackendRuns()
    {
        var c = Config();
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        var runner = new OkRunner();

        var code = BackupRunner.Run(
            c, runner, _stg, _tmp,
            (_, _, _) => (new[] { ".credentials.json" }, new List<string>()));

        Assert.Equal(1, code);
        Assert.Empty(runner.Calls);
    }

    // S5: the offender backstop must fire per destination, not just once
    // against whichever destination happens to be checked first. This fake
    // selector returns a clean GitHub selection but an offender-bearing
    // Drive selection (distinguished by which Include list it is handed) -
    // if the backstop were ever collapsed back to a single check (e.g. only
    // re-checking GitHub's result), this offender would slip through and the
    // run would incorrectly proceed to run Drive's backend.
    [Fact]
    public void OffenderInDriveOnlySelectionStillAbortsTheWholeRun()
    {
        var c = Config();
        c.Github.Include = new() { "github-clean-marker" };
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        c.Drive.Include = new() { "drive-offender-marker" };
        var runner = new OkRunner();

        var code = BackupRunner.Run(
            c, runner, _stg, _tmp,
            (_, include, _) => include.Contains("drive-offender-marker")
                ? (new[] { "session.dat" }, new List<string>())
                : (new[] { "settings.json" }, new List<string>()));

        Assert.Equal(1, code);
        Assert.Empty(runner.Calls);
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
        c.Github.Include.Add(".credentials.json");

        var before = ReadLog().Length;
        BackupRunner.Run(c, new OkRunner(), _stg, _tmp);
        var written = ReadLog()[before..];

        Assert.Contains("withheld from GitHub by the secret denylist", written);
        Assert.Contains(".credentials.json", written);
    }

    // S5: the withheld-files log line must name which destination it
    // applies to - a user with different GitHub and Drive selections cannot
    // otherwise tell which backup a withheld file was omitted from.
    [Fact]
    public void WithheldFilesLogLineNamesDriveWhenWithheldFromDrive()
    {
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        var c = Config();
        c.Github.Enabled = false;
        c.Drive.Enabled = true;
        c.Drive.RcloneRemote = "gdrive:X";
        c.Drive.Include.Add(".credentials.json");

        var before = ReadLog().Length;
        BackupRunner.Run(c, new OkRunner(), _stg, _tmp);
        var written = ReadLog()[before..];

        Assert.Contains("withheld from Google Drive by the secret denylist", written);
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
