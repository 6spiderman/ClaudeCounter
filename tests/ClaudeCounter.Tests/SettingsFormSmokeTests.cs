using System.Security.Cryptography;
using ClaudeBackup;
using ClaudeCounter.Settings;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// This suite otherwise never constructs a Form, because window creation in the
/// test host is flaky. That rule let a real crash ship: InfoButton assigned a
/// transparent BackColor without opting into SupportsTransparentBackColor, so
/// Control.set_BackColor threw and the app died the moment the user opened
/// Settings. Nothing static caught it - every field round-tripped, every value
/// was correct, and the constructor simply threw.
///
/// So one narrow exception to the rule: prove the dialog can be built at all.
/// Construction runs on a dedicated STA thread and is joined with a timeout, so
/// a hang fails the test rather than wedging the run.
/// </summary>
public class SettingsFormSmokeTests
{
    // S17-fix: every SettingsForm construction in this suite must pass its
    // own GUID-suffixed, never-existing temp path here instead of letting
    // the constructor default to BackupConfig.DefaultPath() - BuildBackupPage
    // unconditionally calls BackupConfig.Load on construction, and Load
    // SAVES the file when it migrates a stale BackupConfigVersion (see
    // BackupConfig.Migrate). Without this, constructing SettingsForm on a
    // machine with a real, stale %APPDATA%\ClaudeCounter\backup.json
    // silently rewrites the developer's live config on every test run. A
    // path that never exists makes Load return Default() without ever
    // calling Migrate/Save, so nothing is written and no cleanup is needed -
    // unlike BackupConfigTests' own temp paths, which do get written to and
    // are cleaned up there. Unique per call (not shared) so this suite stays
    // safe under xUnit's parallel execution.
    private static string UniqueBackupConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"settingsform-smoke-backup-{Guid.NewGuid():N}.json");

    // S17c: same seam, same reasoning, for backup-status.json -
    // RefreshDestinationsSummary (BuildBackupPage) now also calls
    // BackupStatus.Load on construction.
    private static string UniqueBackupStatusPath() =>
        Path.Combine(Path.GetTempPath(), $"settingsform-smoke-status-{Guid.NewGuid():N}.json");

    private static Exception? ConstructOnStaThread(Func<Form> build)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = build();
                // Force handle creation: several paint-path styles are only
                // validated when the window is actually realised.
                _ = form.Handle;
            }
            catch (Exception e)
            {
                captured = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Form construction did not complete within 30s.");
        return captured;
    }

    [Fact]
    public void SettingsFormConstructsWithoutThrowing()
    {
        var error = ConstructOnStaThread(() => new SettingsForm(new AppSettings(), UniqueBackupConfigPath(), UniqueBackupStatusPath()));
        Assert.Null(error);
    }

    [Fact]
    public void SettingsFormConstructsWithNonDefaultSettings()
    {
        // Exercises the initialise-from-settings paths rather than only defaults.
        var settings = new AppSettings
        {
            PollIntervalMinutes = 30,
            WarnThreshold = 60,
            CriticalThreshold = 80,
            AutostartEnabled = false,
            CheckForUpdates = false,
            WarnAlertsEnabled = false,
            CriticalAlertsEnabled = false,
            MaxedAlertsEnabled = false,
            AlertSevenDayOpus = true,
            AlertSevenDaySonnet = true,
            PopupPlacement = PopupPlacement.Centered,
            PopupAutoDismissSeconds = 0,
            AlertRepeatMinutes = 15,
        };
        settings.Normalize();

        var error = ConstructOnStaThread(() => new SettingsForm(settings, UniqueBackupConfigPath(), UniqueBackupStatusPath()));
        Assert.Null(error);
    }

    [Fact]
    public void BackupHelpDialogConstructsWithoutThrowing()
    {
        // The help dialog is only reachable by clicking Help on the Backup tab,
        // so it would otherwise never be exercised at all.
        var error = ConstructOnStaThread(() => new BackupHelpDialog(Theme.Current()));
        Assert.Null(error);
    }

    // S14b: the sync-folder Detect dialog is only reachable by clicking
    // Detect... on the Backup tab's Drive block, so - like BackupHelpDialog
    // above - it would otherwise never be constructed by any test at all.
    // Exercised with an empty candidate list (the "nothing found" state,
    // where "Use this folder" must start disabled rather than letting a
    // no-op OK through) and a populated one (the normal path).
    [Fact]
    public void SyncFolderDetectDialogConstructsWithNoCandidatesWithoutThrowing()
    {
        var error = ConstructOnStaThread(() =>
            new SyncFolderDetectDialog(Theme.Current(), Array.Empty<SyncFolderCandidate>()));
        Assert.Null(error);
    }

    [Fact]
    public void SyncFolderDetectDialogConstructsWithCandidatesWithoutThrowing()
    {
        var candidates = new[]
        {
            new SyncFolderCandidate("OneDrive", @"C:\Users\someone\OneDrive"),
            new SyncFolderCandidate("NAS share (M: -> \\\\192.168.1.210\\media)", @"\\192.168.1.210\media"),
        };
        var error = ConstructOnStaThread(() => new SyncFolderDetectDialog(Theme.Current(), candidates));
        Assert.Null(error);
    }

    // S6: the file picker is only reachable by clicking "Choose files..." on
    // the Backup tab, so - like BackupHelpDialog above - it would otherwise
    // never be constructed by any test at all. A populated tree (some files,
    // some directories, an existing Include list mixing a representable and
    // a non-representable pattern) exercises the constructor's real
    // work - loading patterns, the eager root-level EnsureChildrenLoaded,
    // building TreeNodes, kicking off background size computation, and
    // populating the read-only patterns box - rather than only the trivial
    // empty-directory path.
    [Fact]
    public void BackupPickerDialogConstructsWithAPopulatedTreeWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"picker-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "commands"));
        Directory.CreateDirectory(Path.Combine(root, "skills", "one"));
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(root, ".credentials.json"), "secret");
        File.WriteAllText(Path.Combine(root, "commands", "a.md"), "a");
        File.WriteAllText(Path.Combine(root, "skills", "one", "SKILL.md"), "s");
        try
        {
            var error = ConstructOnStaThread(() => new BackupPickerDialog(
                Theme.Current(), "GitHub", root,
                new[] { "settings.json", "commands/**", "**/*.md" }));
            Assert.Null(error);
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    // S7/S8: the Advanced dialog is only reachable by clicking "Advanced..."
    // on the Backup tab, so - like BackupHelpDialog and BackupPickerDialog
    // above - it would otherwise never be constructed by any test at all.
    // Exercised with both a fresh (all-defaults) ScheduleConfig and one with
    // every optional/non-default value set, since the retry-interval
    // NumericUpDown controls are seeded from those values at construction
    // time. S17c: retention moved off this dialog's own constructor (onto
    // BackupDestinationEditDialog instead - see that dialog's own smoke
    // tests below), so it no longer takes a DriveTarget argument.
    [Fact]
    public void BackupAdvancedDialogConstructsWithDefaultsWithoutThrowing()
    {
        var error = ConstructOnStaThread(() =>
            new BackupAdvancedDialog(Theme.Current(), new ScheduleConfig()));
        Assert.Null(error);
    }

    [Fact]
    public void BackupAdvancedDialogConstructsWithNonDefaultValuesWithoutThrowing()
    {
        var schedule = new ScheduleConfig
        {
            StartWhenAvailable = false,
            RunOnlyIfNetworkAvailable = false,
            DisallowStartIfOnBatteries = true,
            StopIfGoingOnBatteries = true,
            RestartOnFailure = false,
            RestartIntervalMinutes = 30,
            RestartCount = 5,
            BackupStaleAfterDays = 7,
        };

        var error = ConstructOnStaThread(() => new BackupAdvancedDialog(Theme.Current(), schedule));
        Assert.Null(error);
    }

    // S11b: the Advanced dialog is only reachable by clicking "Advanced..."
    // on the Backup tab, so BackupStaleAfterDays' NumericUpDown (like every
    // other control here) is only exercised by an STA smoke test. This one
    // goes further than "does not throw" - it captures the constructed
    // dialog's BackupStaleAfterDays property from inside the STA thread
    // (mirroring SettingsFormHeightStaysWithinTheDisplayBudget's pattern for
    // reading a value before the `using` disposes the form) and asserts it
    // actually reflects the seeded ScheduleConfig value, proving the new row
    // is wired up rather than merely present on screen.
    [Fact]
    public void BackupAdvancedDialogExposesTheSeededBackupStaleAfterDays()
    {
        var schedule = new ScheduleConfig { BackupStaleAfterDays = 9 };
        int? observed = null;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new BackupAdvancedDialog(Theme.Current(), schedule);
                _ = dialog.Handle;
                observed = dialog.BackupStaleAfterDays;
            }
            catch (Exception e)
            {
                captured = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Dialog construction did not complete within 30s.");
        Assert.Null(captured);
        Assert.Equal(9, observed);
    }

    // S9b: fake IProcessRunner for RestoreDialogConstructsWithAPopulatedSnapshotListWithoutThrowing
    // below - handles just enough of "git" (clone/fetch/log) and "rclone"
    // (lsjson) to make RestoreGitSource.ListSnapshots and
    // RestoreZipSource.ListSnapshots each return one real snapshot, so the
    // dialog constructs with something actually populated in its snapshot
    // list rather than only the trivial empty case. Never a real process:
    // see RestoreGitSourceTests.FakeRunner / RestoreZipSourceTests.FakeRunner
    // for the same pattern used to test the engine itself.
    private sealed class FakeRestoreRunner : IProcessRunner
    {
        // Fix round 1 (review): simulates a `git log` call throwing (rather
        // than returning a non-zero ProcessResult) AFTER a successful clone
        // has already populated the staging directory - the exact scenario
        // the review flagged for RestoreDialog.LoadSnapshots' exception guard.
        public bool ThrowOnLog { get; set; }

        public bool Exists(string file) => true;

        public ProcessResult Run(string file, IReadOnlyList<string> args, string? workingDir = null)
        {
            if (args.Count > 0 && args[0] == "clone")
            {
                if (workingDir is not null)
                    Directory.CreateDirectory(Path.Combine(workingDir, ".git"));
                return new ProcessResult(0, "", "");
            }
            if (args.Count > 0 && args[0] == "fetch")
                return new ProcessResult(0, "", "");
            if (args.Count > 0 && args[0] == "log")
            {
                if (ThrowOnLog)
                    throw new InvalidOperationException("simulated git log failure");
                const string sha = "1111111111111111111111111111111111aaaa";
                var timestamp = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
                return new ProcessResult(0, $"{sha}{timestamp:o}Backup 2026-01-01 09:00:00\n", "");
            }
            if (args.Count > 0 && args[0] == "lsjson")
            {
                const string json = """
                    [{"Name":"claude-backup-2026-01-01T090000Z.zip","ModTime":"2026-01-01T09:00:00Z","Size":1234,"IsDir":false}]
                    """;
                return new ProcessResult(0, json, "");
            }
            return new ProcessResult(0, "", "");
        }
    }

    // S9b: the restore dialog is only reachable by clicking "Restore..." on
    // the Backup tab, so - like BackupPickerDialog and BackupAdvancedDialog
    // above - it would otherwise never be constructed by any test at all.
    // Constructed with BOTH destinations enabled (exercises the source
    // selector combo, not just the single-destination label path) and a
    // populated snapshot list from each (see FakeRestoreRunner above) -
    // RestoreDialog lists synchronously during construction specifically so
    // this is possible without any message-pump gymnastics. scratchRoot
    // redirects the git-clone side effect LoadSnapshots triggers into a temp
    // directory instead of the real user's %LOCALAPPDATA%\ClaudeCounter -
    // see RestoreDialog's own doc comment on that constructor parameter.
    [Fact]
    public void RestoreDialogConstructsWithAPopulatedSnapshotListWithoutThrowing()
    {
        var scratchRoot = Path.Combine(Path.GetTempPath(), $"restore-smoke-{Guid.NewGuid():N}");
        // S17b: RestoreDialog reads BackupConfig.Destinations directly now,
        // not the Github/Drive shim - a config built via a plain object
        // initializer (as here) never syncs the shim into Destinations on
        // its own (that only happens on Save()/Load()), so this constructs
        // Destinations directly with the well-known "github"/"drive" ids.
        var config = new BackupConfig
        {
            Destinations = new()
            {
                new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true, RemoteUrl = "git@example.com:org/repo.git", Branch = "main" },
                new BackupDestination { Id = "drive", Name = "Google Drive (rclone)", Kind = DestinationKind.Rclone, Enabled = true, RcloneRemote = "gdrive:ClaudeBackups" },
            },
        };
        var runner = new FakeRestoreRunner();

        try
        {
            var error = ConstructOnStaThread(() => new RestoreDialog(Theme.Current(), config, runner, scratchRoot));
            Assert.Null(error);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    // Same construction, but with only ONE destination enabled - exercises
    // the "Restoring from X" label path (no combo box) rather than the
    // selector.
    [Fact]
    public void RestoreDialogConstructsWithASingleDestinationWithoutThrowing()
    {
        var scratchRoot = Path.Combine(Path.GetTempPath(), $"restore-smoke-{Guid.NewGuid():N}");
        var config = new BackupConfig
        {
            Destinations = new()
            {
                new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true, RemoteUrl = "git@example.com:org/repo.git", Branch = "main" },
            },
        };
        var runner = new FakeRestoreRunner();

        try
        {
            var error = ConstructOnStaThread(() => new RestoreDialog(Theme.Current(), config, runner, scratchRoot));
            Assert.Null(error);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    // Fix round 1 (review): the scenario the reviewer specifically called
    // out - EnsureRepo's `git clone` succeeds (so _gitStagingDir is already
    // a real, populated git checkout on disk) and the SUBSEQUENT `git log`
    // call throws instead of returning a failing ProcessResult. Before the
    // fix, this exception was unguarded in LoadSnapshots (called
    // synchronously from the constructor), so it would have propagated out
    // of `new RestoreDialog(...)` entirely, leaving nothing to ever Dispose
    // or fire FormClosed - and therefore never running
    // CleanupStagingDirectories - so the cloned staging directory would be
    // left on disk indefinitely. Asserts both that construction survives
    // (the crash is gone) AND that the clone's directory genuinely exists
    // afterwards (proving this test exercises the "already populated before
    // the throw" case the reviewer described, not a trivial empty one).
    [Fact]
    public void RestoreDialogConstructsWhenListingThrowsAfterASuccessfulCloneWithoutThrowing()
    {
        var scratchRoot = Path.Combine(Path.GetTempPath(), $"restore-smoke-{Guid.NewGuid():N}");
        var config = new BackupConfig
        {
            Destinations = new()
            {
                new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true, RemoteUrl = "git@example.com:org/repo.git", Branch = "main" },
            },
        };
        var runner = new FakeRestoreRunner { ThrowOnLog = true };

        try
        {
            var error = ConstructOnStaThread(() => new RestoreDialog(Theme.Current(), config, runner, scratchRoot));
            Assert.Null(error);

            // S17b: RestoreGitSource's own clone now lives under this
            // destination's subdirectory of the staging root (keyed by its
            // Id "github"), not directly at the root - see RestoreDialog.
            // GitSourceFor's own doc comment for why.
            var expectedGitStagingDir = Path.Combine(scratchRoot, "ClaudeCounter", "restore-git-repo", "github");
            Assert.True(Directory.Exists(Path.Combine(expectedGitStagingDir, ".git")),
                "the fake clone should have populated the staging directory before `git log` threw");
        }
        finally
        {
            try { Directory.Delete(scratchRoot, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    // No destination enabled at all - RestoreDialog must degrade to an
    // informational state rather than crash (SettingsForm's own
    // OnOpenRestoreDialog guards against this case before ever constructing
    // the dialog, but the dialog itself must not assume that guard is
    // always in front of it). Still passes a scratchRoot even though this
    // path never lists anything (no destination to list) - construction
    // computes the staging paths unconditionally, and this keeps every
    // RestoreDialog construction in this suite off the real
    // %LOCALAPPDATA%\ClaudeCounter, with nothing left depending on whether
    // Dispose() happens to raise FormClosed.
    [Fact]
    public void RestoreDialogConstructsWithNoDestinationEnabledWithoutThrowing()
    {
        var scratchRoot = Path.Combine(Path.GetTempPath(), $"restore-smoke-{Guid.NewGuid():N}");
        try
        {
            var error = ConstructOnStaThread(() =>
                new RestoreDialog(Theme.Current(), new BackupConfig(), new FakeRestoreRunner(), scratchRoot));
            Assert.Null(error);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    // S6: re-measure after adding the Backup tab's "Choose files..." button
    // inline on the Include row (see SettingsForm.AddChooseFilesButton) - the
    // Backup tab was 654px against a ~687px budget on a 1366x768 display
    // before this feature (see the design spec), only 33px of margin.
    // Measured (via this very test, temporarily made to fail with the
    // actual number) at 671px after adding the button: +17px, 16px of
    // margin left. This is the actual, load-bearing check that the button
    // did not push the dialog's real, measured ClientSize.Height back over
    // budget - not just a hand-computed guess in a comment.
    //
    // S7/S8: the "Advanced..." button (schedule-robustness and Drive-
    // retention settings) was placed beside the existing Help button
    // (same NewFlatButton control, same row) rather than beside the
    // shorter destination combo box one row down - the combo-box
    // placement was measured (via this same technique) to push the
    // height to 686px, a single pixel under budget. Beside Help, the
    // measured height is unchanged at 671px: the row's height is already
    // governed by the taller of two identical buttons, so adding the
    // second one costs zero extra pixels.
    //
    // S14b: added a "Transport" combo row plus a sync-folder-path row
    // (label + Browse.../Detect... inline + textbox) to the Drive block.
    // Kept to a row-SWAP against the existing rclone-remote row (see
    // SettingsForm.BuildDriveBlock/BuildSyncFolderRow/BuildRcloneRow - the
    // same "both built, only one Visible" trick BuildBackupPage already
    // uses for the GitHub/Drive blocks themselves, nested one level deeper)
    // rather than stacking both rows permanently. Re-measured (via this same
    // technique) at 671px - EXACTLY unchanged: the Drive block was already
    // shorter than the GitHub block (which has an extra "Remote URL" +
    // privacy-caption + branch row), and Math.Max(githubBlockHeight,
    // driveBlockHeight) was already being governed by GitHub's height before
    // this change, with enough spare margin that the new Transport row plus
    // the taller of the two swapped rows still does not exceed it.
    //
    // S16: replaced the two nested dropdowns (GitHub/Drive selector, then -
    // only once "Drive" was picked - the "Transport" combo removed above)
    // with one flat "Back up to" selector naming all six destinations
    // directly, plus a new one-line note stating the one-at-a-time rule (see
    // BuildBackupPage). Deleting the Transport combo did NOT give back a row
    // here, despite removing a whole control: the Drive block was already
    // shorter than the GitHub block by more than one row's worth (see the
    // S14b paragraph above), so Math.Max(...) was already being governed by
    // GitHub's height, not Drive's - shrinking Drive further changes
    // nothing about that Math.Max. The new note line is the ONLY net
    // addition, and it is added once, shared, above both blocks - not
    // per-block - so its height is not absorbed by that same margin. First
    // pass (unabridged note text, standard RowGap spacing) measured 694px,
    // 7px over budget; shortening the note to one line and tightening the
    // handful of pixels of spacing directly around it (not touched
    // anywhere else in this file) brought it down to 684px - 3px of margin,
    // thinner than this suite's history but genuinely under budget.
    //
    // S17c: every per-destination connection field (the GitHub/Drive blocks,
    // the transport rows, the "Back up to" selector and its one-line
    // one-at-a-time note) moved OUT of this tab entirely, into
    // BackupDestinationsDialog/BackupDestinationEditDialog (see
    // SettingsForm.BuildBackupPage) - replaced by a compact, fixed-height
    // read-only summary ListView plus a single "Manage destinations..."
    // button. Re-measured (via this same technique) at 439px - a 245px drop
    // from 684px, 248px of margin against the 687px budget, the largest this
    // suite has ever recorded.
    [Fact]
    public void SettingsFormHeightStaysWithinTheDisplayBudget()
    {
        // Fix round 1, Important 3: the Backup tab (and this test's whole
        // reason to exist) is only built when BackupTaskManager.WorkerAvailable()
        // is true - true under `dotnet test` today only because the test
        // project references ClaudeBackup.csproj, which copies
        // ClaudeBackup.exe next to the test host. Without this assertion, a
        // future host/runner change that stops satisfying that could make
        // this test measure only General/Alerts and keep passing with zero
        // signal on the thing it exists to guard.
        Assert.True(BackupTaskManager.WorkerAvailable(),
            "Backup tab not built - the height assertion below would be vacuous.");

        // Not built on ConstructOnStaThread: that helper disposes the Form
        // before returning (it only ever reports whether construction
        // threw), and ClientSize is not safe to read afterward. The height
        // has to be captured from inside the same STA thread, before the
        // `using` in the thread body disposes the form.
        int? measuredHeight = null;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new SettingsForm(new AppSettings(), UniqueBackupConfigPath(), UniqueBackupStatusPath());
                _ = form.Handle; // force handle creation, same as ConstructOnStaThread
                measuredHeight = form.ClientSize.Height;
            }
            catch (Exception e)
            {
                captured = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Form construction did not complete within 30s.");
        Assert.Null(captured);
        Assert.NotNull(measuredHeight);

        // 1366x768 at 100% DPI minus taskbar/title bar/window chrome - the
        // same real-world budget the design spec's own comments measure
        // against (687px was that budget; 654px was the Backup tab before
        // this feature). This is the load-bearing check that adding the
        // "Choose files..." button (see SettingsForm.AddChooseFilesButton)
        // did not push the dialog's real, measured height back over it.
        Assert.True(measuredHeight!.Value <= 687,
            $"SettingsForm.ClientSize.Height was {measuredHeight}px, over the ~687px display budget.");
    }

    private static string? Sha256OrNull(string path) =>
        File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    /// <summary>
    /// S17-fix regression guard. The bug this exists to catch: BuildBackupPage
    /// used to call BackupConfig.Load(BackupConfig.DefaultPath()) unconditionally,
    /// and Load SAVES the file whenever it migrates a stale BackupConfigVersion
    /// (see BackupConfig.Migrate) - so constructing a SettingsForm on this
    /// developer's machine, via nothing more than
    /// SettingsFormConstructsWithoutThrowing above, silently rewrote the real
    /// %APPDATA%\ClaudeCounter\backup.json the moment a v1-&gt;v2 migration was
    /// added. TrayApplicationContext.EvaluateBackupHealth has the same
    /// DefaultPath() coupling for %LOCALAPPDATA%\ClaudeCounter\backup-status.json,
    /// checked here too even though nothing in this suite currently constructs
    /// a TrayApplicationContext - so this test also catches that becoming
    /// reachable later without anyone updating this guard.
    ///
    /// This is a REAL check, not a tautology: every SettingsForm construction
    /// in this class already passes a GUID temp path for exactly this reason
    /// (see UniqueBackupConfigPath), so this test only fails if that seam is
    /// bypassed - e.g. BuildBackupPage reverted to calling
    /// BackupConfig.DefaultPath() directly and ignoring the constructor
    /// parameter, or a future call site inside SettingsForm/RestoreDialog/
    /// TrayApplicationContext is added without routing through the same seam.
    /// Skips cleanly (rather than failing) when the real file does not exist,
    /// so this passes on a clean CI machine that has never run ClaudeCounter
    /// for real - there is nothing to protect and nothing this test could
    /// meaningfully assert about a file that was never there.
    /// </summary>
    [Fact]
    public void SuiteNeverTouchesTheRealBackupConfigOrStatusFiles()
    {
        var configPath = BackupConfig.DefaultPath();
        var statusPath = BackupStatus.DefaultPath();

        var configExisted = File.Exists(configPath);
        var statusExisted = File.Exists(statusPath);
        if (!configExisted && !statusExisted)
            return; // nothing real on this machine to protect - see doc comment above

        var configWriteBefore = configExisted ? File.GetLastWriteTimeUtc(configPath) : (DateTime?)null;
        var configHashBefore = Sha256OrNull(configPath);
        var statusWriteBefore = statusExisted ? File.GetLastWriteTimeUtc(statusPath) : (DateTime?)null;
        var statusHashBefore = Sha256OrNull(statusPath);

        // Exercises the exact construction path every other test in this
        // class already exercises (BuildBackupPage's BackupConfig.Load call),
        // through the same injected seam.
        var error = ConstructOnStaThread(() => new SettingsForm(new AppSettings(), UniqueBackupConfigPath(), UniqueBackupStatusPath()));
        Assert.Null(error);

        if (configExisted)
        {
            Assert.Equal(configWriteBefore, File.GetLastWriteTimeUtc(configPath));
            Assert.Equal(configHashBefore, Sha256OrNull(configPath));
        }
        else
        {
            Assert.False(File.Exists(configPath), "backup.json was created by this run and must not have been.");
        }

        if (statusExisted)
        {
            Assert.Equal(statusWriteBefore, File.GetLastWriteTimeUtc(statusPath));
            Assert.Equal(statusHashBefore, Sha256OrNull(statusPath));
        }
        else
        {
            Assert.False(File.Exists(statusPath), "backup-status.json was created by this run and must not have been.");
        }
    }

    // --- S17c: the destinations dialog family --------------------------

    // The kind picker (BackupDestinationsDialog's "Add..." first step) is
    // only reachable that way, so - like BackupHelpDialog above - it would
    // otherwise never be constructed by any test at all.
    [Fact]
    public void BackupDestinationKindDialogConstructsWithoutThrowing()
    {
        var error = ConstructOnStaThread(() => new BackupDestinationKindDialog(Theme.Current()));
        Assert.Null(error);
    }

    private static string UniqueSourceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"destination-edit-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    // BackupDestinationEditDialog builds a genuinely different set of
    // controls per Kind (see BuildGitHubFields/BuildSyncFolderFields/
    // BuildRcloneFields) - each is exercised once, for both the Add (isNew:
    // true, blank seed) and Edit (isNew: false, populated seed) paths, since
    // isNew gates the sync-folder auto-detect-on-add behaviour.
    [Fact]
    public void BackupDestinationEditDialogConstructsForANewGitHubDestinationWithoutThrowing()
    {
        var root = UniqueSourceRoot();
        try
        {
            var seed = new BackupDestination { Id = BackupDestination.NewId(), Name = "GitHub", Kind = DestinationKind.GitHub };
            var error = ConstructOnStaThread(() =>
                new BackupDestinationEditDialog(Theme.Current(), DestinationKind.GitHub, SyncProvider.Other, root, seed, isNew: true));
            Assert.Null(error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BackupDestinationEditDialogConstructsForAnExistingGitHubDestinationWithoutThrowing()
    {
        var root = UniqueSourceRoot();
        try
        {
            var seed = new BackupDestination
            {
                Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true,
                RemoteUrl = "git@example.com:org/repo.git", Branch = "main",
                Include = new() { "settings.json" }, Exclude = new() { "projects/**" },
            };
            var error = ConstructOnStaThread(() =>
                new BackupDestinationEditDialog(Theme.Current(), DestinationKind.GitHub, SyncProvider.Other, root, seed, isNew: false));
            Assert.Null(error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BackupDestinationEditDialogConstructsForANewSyncFolderDestinationWithoutThrowing()
    {
        var root = UniqueSourceRoot();
        try
        {
            var seed = new BackupDestination { Id = BackupDestination.NewId(), Name = "NAS / network share", Kind = DestinationKind.SyncFolder, SyncProvider = SyncProvider.Nas };
            // isNew: true exercises the one-candidate auto-fill path
            // (SyncFolderScanner.Detect against the real machine, never
            // throws) as well as construction itself.
            var error = ConstructOnStaThread(() =>
                new BackupDestinationEditDialog(Theme.Current(), DestinationKind.SyncFolder, SyncProvider.Nas, root, seed, isNew: true));
            Assert.Null(error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BackupDestinationEditDialogConstructsForAnExistingSyncFolderDestinationWithRetentionWithoutThrowing()
    {
        var root = UniqueSourceRoot();
        try
        {
            var seed = new BackupDestination
            {
                Id = "drive", Name = "Home NAS", Kind = DestinationKind.SyncFolder, SyncProvider = SyncProvider.Nas,
                Enabled = true, FolderPath = @"\\nas\backups", KeepLastCount = 10, DeleteOlderThanDays = 30,
            };
            var error = ConstructOnStaThread(() =>
                new BackupDestinationEditDialog(Theme.Current(), DestinationKind.SyncFolder, SyncProvider.Nas, root, seed, isNew: false));
            Assert.Null(error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BackupDestinationEditDialogConstructsForARcloneDestinationWithoutThrowing()
    {
        var root = UniqueSourceRoot();
        try
        {
            var seed = new BackupDestination { Id = "rclone-1", Name = "rclone remote", Kind = DestinationKind.Rclone, RcloneRemote = "gdrive:ClaudeBackups" };
            var error = ConstructOnStaThread(() =>
                new BackupDestinationEditDialog(Theme.Current(), DestinationKind.Rclone, SyncProvider.Other, root, seed, isNew: false));
            Assert.Null(error);
        }
        finally { Directory.Delete(root, true); }
    }

    // BackupDestinationsDialog is only reachable via "Manage destinations..."
    // on the Backup tab. Exercised with zero destinations (the empty-list
    // state) and with several, including two of the same Kind - the exact
    // scenario the task brief's "distinguishable default names" requirement
    // is about.
    [Fact]
    public void BackupDestinationsDialogConstructsWithNoDestinationsWithoutThrowing()
    {
        var configPath = UniqueBackupConfigPath();
        var statusPath = UniqueBackupStatusPath();
        var error = ConstructOnStaThread(() => new BackupDestinationsDialog(Theme.Current(), configPath, statusPath));
        Assert.Null(error);
    }

    [Fact]
    public void BackupDestinationsDialogConstructsWithSeveralDestinationsIncludingTwoOfTheSameKindWithoutThrowing()
    {
        var configPath = UniqueBackupConfigPath();
        var statusPath = UniqueBackupStatusPath();
        var config = new BackupConfig
        {
            Destinations = new()
            {
                new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true, RemoteUrl = "git@example.com:org/repo.git" },
                new BackupDestination { Id = "nas-1", Name = "Home NAS", Kind = DestinationKind.SyncFolder, SyncProvider = SyncProvider.Nas, Enabled = true, FolderPath = @"\\nas1\backups" },
                new BackupDestination { Id = "nas-2", Name = "Office NAS", Kind = DestinationKind.SyncFolder, SyncProvider = SyncProvider.Nas, Enabled = false, FolderPath = @"\\nas2\backups" },
            },
        };
        config.Save(configPath);

        try
        {
            var error = ConstructOnStaThread(() => new BackupDestinationsDialog(Theme.Current(), configPath, statusPath));
            Assert.Null(error);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
            if (File.Exists(statusPath)) File.Delete(statusPath);
        }
    }
}
