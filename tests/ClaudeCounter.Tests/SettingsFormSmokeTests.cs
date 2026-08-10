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
        var error = ConstructOnStaThread(() => new SettingsForm(new AppSettings()));
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
        };
        settings.Normalize();

        var error = ConstructOnStaThread(() => new SettingsForm(settings));
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
    // Exercised with both a fresh (all-defaults) ScheduleConfig/DriveTarget
    // and one with every optional/non-default value set, since the retry-
    // interval and retention NumericUpDown controls are seeded from those
    // values at construction time.
    [Fact]
    public void BackupAdvancedDialogConstructsWithDefaultsWithoutThrowing()
    {
        var error = ConstructOnStaThread(() =>
            new BackupAdvancedDialog(Theme.Current(), new ScheduleConfig(), new DriveTarget()));
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
        };
        var drive = new DriveTarget { KeepLastCount = 14, DeleteOlderThanDays = 60 };

        var error = ConstructOnStaThread(() => new BackupAdvancedDialog(Theme.Current(), schedule, drive));
        Assert.Null(error);
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
        var config = new BackupConfig
        {
            Github = new GitTarget { Enabled = true, RemoteUrl = "git@example.com:org/repo.git", Branch = "main" },
            Drive = new DriveTarget { Enabled = true, RcloneRemote = "gdrive:ClaudeBackups" },
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
            Github = new GitTarget { Enabled = true, RemoteUrl = "git@example.com:org/repo.git", Branch = "main" },
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
                using var form = new SettingsForm(new AppSettings());
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
}
