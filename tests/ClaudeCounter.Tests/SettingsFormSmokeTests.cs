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
