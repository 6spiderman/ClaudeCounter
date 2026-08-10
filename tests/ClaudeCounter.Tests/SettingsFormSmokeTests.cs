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
}
