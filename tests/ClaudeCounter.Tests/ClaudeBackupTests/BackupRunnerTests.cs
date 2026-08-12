// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupRunnerTests.cs
using ClaudeBackup;
using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

// S17b: migrated from the pre-N-destination shape (c.Github/c.Drive shim
// properties, BackupRunResult's 3-arg constructor and .Github/.Drive
// accessors) to BackupConfig.Destinations directly - BackupRunner now reads
// Destinations exclusively (see BackupRunner.cs's own remarks), and a config
// built via a plain object initializer (as every test here does - none of
// these go through BackupConfig.Save/Load) never populates the Github/Drive
// shim's synced Destinations entries on its own. Gh(c)/Dr(c) below are a
// thin, mechanical stand-in for the old c.Github/c.Drive property access, so
// every existing test's BODY (what it configures and asserts) is unchanged -
// only how it reaches the "github"/"drive" destination changed.
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

    // S17b: rclone missing (git present) - used by
    // ThreeDestinationsMiddleOneFailingStillRunsTheOthers to fail exactly
    // the ONE Rclone-kind destination in a three-destination run without
    // touching the GitHub or SyncFolder destinations either side of it (the
    // sync-folder kind invokes no external process at all, so it is
    // immune to this runner's Exists("rclone") = false).
    private sealed class RcloneMissingRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool Exists(string file) => file != "rclone";
        public ProcessResult Run(string f, IReadOnlyList<string> a, string? wd = null)
        {
            Calls.Add($"{f} {string.Join(' ', a)}");
            if (a.Count > 0 && a[0] == "diff")
                return new(1, "", "");
            if (f == "git" && a.Count > 0 && a[0] == "init" && wd is not null)
                Directory.CreateDirectory(Path.Combine(wd, ".git"));
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

    // Both destinations get the same "settings.json" selection by default,
    // mirroring what the old single shared Include/Exclude used to produce
    // for both destinations - most of the existing tests below only care
    // about one destination at a time and should not have to think about
    // the other's selection to keep passing.
    private BackupConfig Config() => new()
    {
        SourceRoot = _root,
        Destinations = new()
        {
            new BackupDestination
            {
                Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub,
                Enabled = true, RemoteUrl = "url", Branch = "main",
                Include = new() { "settings.json" }, Exclude = new(),
            },
            new BackupDestination
            {
                Id = "drive", Name = "Google Drive", Kind = DestinationKind.Rclone,
                Enabled = false,
                Include = new() { "settings.json" }, Exclude = new(),
            },
        },
    };

    // Mechanical stand-ins for the old c.Github/c.Drive shim property access
    // - see this file's own header comment.
    private static BackupDestination Gh(BackupConfig c) => c.Destinations.First(d => d.Id == "github");
    private static BackupDestination Dr(BackupConfig c) => c.Destinations.First(d => d.Id == "drive");

    [Fact]
    public void NoDestinationsIsConfigError()
    {
        var c = Config();
        Gh(c).Enabled = false;
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
        Gh(c).RemoteUrl = "";
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
        Gh(c).Branch = "   ";
        var runner = new OkRunner();

        Assert.Equal(1, BackupRunner.Run(c, runner, _stg, _tmp));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void EnabledDriveWithoutRcloneRemoteIsConfigError()
    {
        var c = Config();
        Gh(c).Enabled = false;
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "";
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
        Gh(c).RemoteUrl = "";
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
        Dr(c).Include = new() { "settings.json" };
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
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "";
        var runner = new OkRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(0, code);
        Assert.Contains(runner.Calls, call => call.StartsWith("git push"));
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("rclone copy"));
    }

    // S14: the sync-folder transport's own "not configured" check - a blank
    // FolderPath - is a config error (exit 1), just like a blank
    // RcloneRemote is for the original transport.
    [Fact]
    public void EnabledDriveSyncFolderWithoutFolderPathIsConfigError()
    {
        var c = Config();
        Gh(c).Enabled = false;
        Dr(c).Enabled = true;
        Dr(c).Kind = DestinationKind.SyncFolder;
        Dr(c).FolderPath = "";
        var runner = new OkRunner();

        Assert.Equal(1, BackupRunner.Run(c, runner, _stg, _tmp));
        Assert.Empty(runner.Calls);
    }

    // A relative FolderPath is caught at the same "not configured" gate as a
    // blank one - not left for SyncFolderBackend to fail at exit 2, mirroring
    // how a blank GitHub Branch is caught before GitBackend ever runs.
    [Fact]
    public void EnabledDriveSyncFolderWithRelativeFolderPathIsConfigError()
    {
        var c = Config();
        Gh(c).Enabled = false;
        Dr(c).Enabled = true;
        Dr(c).Kind = DestinationKind.SyncFolder;
        Dr(c).FolderPath = "relative\\path";
        var runner = new OkRunner();

        Assert.Equal(1, BackupRunner.Run(c, runner, _stg, _tmp));
        Assert.Empty(runner.Calls);
    }

    // The dispatch wiring itself: a Drive destination configured for the
    // sync-folder transport must invoke SyncFolderBackend (an archive
    // actually lands in FolderPath) and must never touch rclone.
    [Fact]
    public void SyncFolderTransportCopiesArchiveIntoFolderPathWithoutInvokingRclone()
    {
        var c = Config();
        Gh(c).Enabled = false;
        Dr(c).Enabled = true;
        Dr(c).Kind = DestinationKind.SyncFolder;
        var syncFolder = Path.Combine(Path.GetTempPath(), $"brsync-{Guid.NewGuid():N}");
        Dr(c).FolderPath = syncFolder;
        Dr(c).Include = new() { "settings.json" };
        var runner = new OkRunner();

        try
        {
            var code = BackupRunner.Run(c, runner, _stg, _tmp);

            Assert.Equal(0, code);
            Assert.DoesNotContain(runner.Calls, call => call.StartsWith("rclone"));
            Assert.Single(Directory.GetFiles(syncFolder, "claude-backup-*.zip"));
        }
        finally
        {
            if (Directory.Exists(syncFolder)) Directory.Delete(syncFolder, true);
        }
    }

    // Backend independence must hold across transports too: GitHub enabled
    // but half configured must not stop a properly configured sync-folder
    // Drive destination from running.
    [Fact]
    public void EnabledGithubWithoutRemoteUrlDoesNotStopConfiguredSyncFolderDriveFromRunning()
    {
        var c = Config();
        Gh(c).RemoteUrl = "";
        Dr(c).Enabled = true;
        Dr(c).Kind = DestinationKind.SyncFolder;
        var syncFolder = Path.Combine(Path.GetTempPath(), $"brsync-{Guid.NewGuid():N}");
        Dr(c).FolderPath = syncFolder;
        Dr(c).Include = new() { "settings.json" };
        var runner = new OkRunner();

        try
        {
            var code = BackupRunner.Run(c, runner, _stg, _tmp);

            Assert.Equal(0, code);
            Assert.DoesNotContain(runner.Calls, call => call.StartsWith("git push"));
            Assert.Single(Directory.GetFiles(syncFolder, "claude-backup-*.zip"));
        }
        finally
        {
            if (Directory.Exists(syncFolder)) Directory.Delete(syncFolder, true);
        }
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
        Gh(c).Include = new() { "does-not-exist/**" };
        Assert.Equal(1, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
    }

    // S5: with both destinations enabled but empty selections, the run must
    // still fail closed - "nothing selected on any enabled destination" is
    // exit 1 regardless of how many destinations are enabled.
    [Fact]
    public void BothDestinationsEnabledButNothingSelectedAnywhereIsConfigError()
    {
        var c = Config();
        Gh(c).Include = new() { "does-not-exist/**" };
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
        Dr(c).Include = new() { "also-does-not-exist/**" };
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
        Gh(c).Include = new() { "does-not-exist/**" };
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
        Dr(c).Include = new() { "settings.json" };
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
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
        Dr(c).Include = new() { "does-not-exist/**" };
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
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
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
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
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
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
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
        Gh(c).Include.Add(".credentials.json");
        // Selector already drops secrets; the pre-flight scan is the backstop.
        // Force the scenario by asserting no secret is ever staged: run returns 0
        // and the staging dir must not contain the secret.
        Assert.Equal(0, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
        Assert.False(File.Exists(Path.Combine(_stg, "github", ".credentials.json")));
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
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
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
        Gh(c).Include = new() { "github-clean-marker" };
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
        Dr(c).Include = new() { "drive-offender-marker" };
        var runner = new OkRunner();

        var code = BackupRunner.Run(
            c, runner, _stg, _tmp,
            (_, include, _) => include.Contains("drive-offender-marker")
                ? (new[] { "session.dat" }, new List<string>())
                : (new[] { "settings.json" }, new List<string>()));

        Assert.Equal(1, code);
        Assert.Empty(runner.Calls);
    }

    // Fix round 1 (Minor, BackupRunner.cs:214): the two offender-abort
    // branches used to build their DestinationAttempt results with a
    // DIFFERENT ternary expression each (asymmetric source, textually) -
    // "GitHub offending, is Drive active?" and "Drive offending, is GitHub
    // active?" were not textually symmetric, and neither one was exercised
    // via RunDetailed before this round. These two prove the LOOP version
    // (S17b) reproduces that behaviour (not the asymmetry): BOTH active
    // destinations are reported as a failed, attempted attempt, regardless
    // of which destination's selection actually tripped the offender check.
    [Fact]
    public void GithubOffenderAbortMarksBothActiveDestinationsFailedInDetailedResult()
    {
        var c = Config();
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
        var runner = new OkRunner();

        var result = BackupRunner.RunDetailed(
            c, runner, _stg, _tmp,
            (_, _, _) => (new[] { ".credentials.json" }, new List<string>()));

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Attempts["github"].Attempted);
        Assert.False(result.Attempts["github"].Success);
        Assert.True(result.Attempts["drive"].Attempted);
        Assert.False(result.Attempts["drive"].Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void DriveOffenderAbortMarksBothActiveDestinationsFailedInDetailedResult()
    {
        var c = Config();
        Gh(c).Include = new() { "github-clean-marker" };
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
        Dr(c).Include = new() { "drive-offender-marker" };
        var runner = new OkRunner();

        var result = BackupRunner.RunDetailed(
            c, runner, _stg, _tmp,
            (_, include, _) => include.Contains("drive-offender-marker")
                ? (new[] { "session.dat" }, new List<string>())
                : (new[] { "settings.json" }, new List<string>()));

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Attempts["github"].Attempted);
        Assert.False(result.Attempts["github"].Success);
        Assert.True(result.Attempts["drive"].Attempted);
        Assert.False(result.Attempts["drive"].Success);
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
        Gh(c).Include.Add(".credentials.json");

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
        Gh(c).Enabled = false;
        Dr(c).Enabled = true;
        Dr(c).RcloneRemote = "gdrive:X";
        Dr(c).Include.Add(".credentials.json");

        var before = ReadLog().Length;
        BackupRunner.Run(c, new OkRunner(), _stg, _tmp);
        var written = ReadLog()[before..];

        Assert.Contains("withheld from Google Drive by the secret denylist", written);
    }

    // --- S17b: genuinely new N-destination behaviour ------------------------

    // Three destinations, only the MIDDLE one (by Destinations list order)
    // failing at the backend: GitHub (first) succeeds, an Rclone destination
    // (second) fails because rclone is "missing" on PATH, and a SyncFolder
    // destination (third, no external process at all) succeeds. Proves
    // destination independence generalizes past exactly two - a failure
    // sandwiched between two successes must not take either neighbour down,
    // and the run must still report exit 2 (a backend failed), not 1 or 0.
    [Fact]
    public void ThreeDestinationsMiddleOneFailingStillRunsTheOthersAndReturnsTwo()
    {
        var c = Config();
        Gh(c).Include = new() { "settings.json" };
        // Renaming Id must happen last - Dr(c)/Gh(c) look destinations up by
        // Id, so a mid-sequence rename would break a LATER Dr(c) call. Grab
        // the reference once and mutate it directly instead.
        var drive = Dr(c);
        drive.Id = "rclone-mid";
        drive.Name = "Rclone Mid";
        drive.Enabled = true;
        drive.RcloneRemote = "gdrive:X";
        drive.Include = new() { "settings.json" };

        var syncFolder = Path.Combine(Path.GetTempPath(), $"brsync-{Guid.NewGuid():N}");
        c.Destinations.Add(new BackupDestination
        {
            Id = "sync-last", Name = "Sync Last", Kind = DestinationKind.SyncFolder,
            Enabled = true, FolderPath = syncFolder,
            Include = new() { "settings.json" }, Exclude = new(),
        });

        var runner = new RcloneMissingRunner();
        try
        {
            var result = BackupRunner.RunDetailed(c, runner, _stg, _tmp);

            Assert.Equal(2, result.ExitCode);
            Assert.True(result.Attempts["github"].Success);
            Assert.False(result.Attempts["rclone-mid"].Success);
            Assert.Contains("rclone", result.Attempts["rclone-mid"].Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(result.Attempts["sync-last"].Success);
            Assert.Contains(runner.Calls, call => call.StartsWith("git push"));
            Assert.DoesNotContain(runner.Calls, call => call.StartsWith("rclone"));
            Assert.Single(Directory.GetFiles(syncFolder, "claude-backup-*.zip"));
        }
        finally
        {
            if (Directory.Exists(syncFolder)) Directory.Delete(syncFolder, true);
        }
    }

    // Two GitHub-kind destinations: without per-destination scratch
    // isolation, GitBackend's `git init`/checkout/MirrorFiles for the second
    // destination would run against the SAME staging directory the first
    // destination just populated and pushed from. Asserts each destination's
    // own subdirectory of the shared stagingDir root (keyed by
    // BackupDestination.Id) independently became a real git checkout - proof
    // the two never shared one working tree.
    [Fact]
    public void TwoGithubDestinationsUseSeparateStagingSubdirectories()
    {
        var c = Config();
        var github = Gh(c); // see the rename note in ThreeDestinationsMiddleOneFailingStillRunsTheOthersAndReturnsTwo
        github.Id = "gh-a";
        github.RemoteUrl = "url-a";
        c.Destinations.Add(new BackupDestination
        {
            Id = "gh-b", Name = "GitHub B", Kind = DestinationKind.GitHub,
            Enabled = true, RemoteUrl = "url-b", Branch = "main",
            Include = new() { "settings.json" }, Exclude = new(),
        });
        var runner = new OkRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(0, code);
        Assert.True(Directory.Exists(Path.Combine(_stg, "gh-a", ".git")), "gh-a's own staging subdirectory should be a git checkout");
        Assert.True(Directory.Exists(Path.Combine(_stg, "gh-b", ".git")), "gh-b's own staging subdirectory should be a git checkout");
    }

    // Two Rclone-kind destinations: without per-destination scratch
    // isolation, both would build their zip under the SAME tempDir, where
    // BackupArchiveWriter.SweepStaleZips (run at the start of every
    // RcloneBackend.Run) could race-delete a sibling's just-written archive.
    // Asserts each destination's "rclone copy" call references ITS OWN
    // subdirectory of the shared tempDir root, and that the two paths are
    // distinct.
    [Fact]
    public void TwoRcloneDestinationsUseSeparateTempSubdirectories()
    {
        var c = Config();
        Gh(c).Enabled = false;
        var drive = Dr(c); // see the rename note in ThreeDestinationsMiddleOneFailingStillRunsTheOthersAndReturnsTwo
        drive.Id = "rclone-a";
        drive.Enabled = true;
        drive.RcloneRemote = "gdrive:A";
        c.Destinations.Add(new BackupDestination
        {
            Id = "rclone-b", Name = "Rclone B", Kind = DestinationKind.Rclone,
            Enabled = true, RcloneRemote = "gdrive:B",
            Include = new() { "settings.json" }, Exclude = new(),
        });
        var runner = new OkRunner();

        var code = BackupRunner.Run(c, runner, _stg, _tmp);

        Assert.Equal(0, code);
        var copyA = Assert.Single(runner.Calls, call => call.StartsWith("rclone copy") && call.Contains("gdrive:A"));
        var copyB = Assert.Single(runner.Calls, call => call.StartsWith("rclone copy") && call.Contains("gdrive:B"));
        Assert.Contains(Path.Combine(_tmp, "rclone-a"), copyA);
        Assert.Contains(Path.Combine(_tmp, "rclone-b"), copyB);
    }

    // Offender abort must still be a WHOLE-run abort with three
    // destinations, even when the offending selection belongs to the LAST
    // one checked - the two clean destinations selected before it (already
    // marked Active) must still be reported Failed, and no backend anywhere
    // may run.
    [Fact]
    public void OffenderInThirdDestinationAbortsWholeRun()
    {
        var c = Config();
        Gh(c).Include = new() { "settings.json" };
        var drive = Dr(c); // see the rename note in ThreeDestinationsMiddleOneFailingStillRunsTheOthersAndReturnsTwo
        drive.Id = "rclone-mid";
        drive.Enabled = true;
        drive.RcloneRemote = "gdrive:X";
        drive.Include = new() { "settings.json" };
        c.Destinations.Add(new BackupDestination
        {
            Id = "sync-third", Name = "Sync Third", Kind = DestinationKind.SyncFolder,
            Enabled = true, FolderPath = Path.Combine(Path.GetTempPath(), $"brsync-{Guid.NewGuid():N}"),
            Include = new() { "third-offender-marker" }, Exclude = new(),
        });
        var runner = new OkRunner();

        var result = BackupRunner.RunDetailed(
            c, runner, _stg, _tmp,
            (_, include, _) => include.Contains("third-offender-marker")
                ? (new[] { "session.dat" }, new List<string>())
                : (new[] { "settings.json" }, new List<string>()));

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Attempts["github"].Attempted);
        Assert.False(result.Attempts["github"].Success);
        Assert.True(result.Attempts["rclone-mid"].Attempted);
        Assert.False(result.Attempts["rclone-mid"].Success);
        Assert.True(result.Attempts["sync-third"].Attempted);
        Assert.False(result.Attempts["sync-third"].Success);
        Assert.Empty(runner.Calls);
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
