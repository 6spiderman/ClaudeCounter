using ClaudeCounter.Notifications;
using ClaudeCounter.Settings;
using Xunit;

namespace ClaudeCounter.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultsAreSafe()
    {
        var s = new AppSettings();
        Assert.True(s.WarnAlertsEnabled);
        Assert.True(s.CriticalAlertsEnabled);
        Assert.True(s.MaxedAlertsEnabled);
        Assert.True(s.AlertFiveHour);
        Assert.True(s.AlertSevenDay);
        Assert.False(s.AlertSevenDayOpus);
        Assert.False(s.AlertSevenDaySonnet);
        Assert.Equal(PopupPlacement.NearTray, s.PopupPlacement);
        Assert.NotNull(s.NotificationState);
    }

    [Fact]
    public void NormalizeKeepsExistingBehavior()
    {
        var s = new AppSettings { WarnThreshold = 95, CriticalThreshold = 90 };
        s.Normalize(); // warn >= critical resets both to defaults
        Assert.Equal(75, s.WarnThreshold);
        Assert.Equal(90, s.CriticalThreshold);
    }

    [Fact]
    public void NormalizeRestoresNullNotificationState()
    {
        // A settings.json containing "NotificationState": null deserializes to a
        // null dictionary. Left unfixed, ThresholdTracker's initialState ?? new()
        // fallback allocates its own dictionary instead of aliasing this one, so
        // every save writes null again and every restart re-pops every alert.
        var s = new AppSettings { NotificationState = null! };
        s.Normalize();
        Assert.NotNull(s.NotificationState);
        Assert.Empty(s.NotificationState);
    }

    [Fact]
    public void NormalizeClearsStaleNotificationStateAndStampsVersion()
    {
        // Simulates a settings.json written before Warn existed: the property
        // is absent from old JSON, so NotificationStateVersion deserializes to
        // its default, 0. AlertLevel's int encoding shifted when Warn was
        // inserted (Critical 1 -> 2, Maxed 2 -> 3), so any old stored state
        // would now be misread rather than discarded - Normalize must clear it
        // outright and stamp the current version so this only happens once.
        var s = new AppSettings
        {
            NotificationStateVersion = 0,
            NotificationState = new()
            {
                ["five_hour"] = new WindowAlertState { LastAlertedLevel = AlertLevel.Critical },
            },
        };
        s.Normalize();
        Assert.Empty(s.NotificationState);
        Assert.Equal(AppSettings.CurrentNotificationStateVersion, s.NotificationStateVersion);
    }

    [Fact]
    public void NormalizeLeavesCurrentVersionStateAlone()
    {
        var s = new AppSettings
        {
            NotificationStateVersion = AppSettings.CurrentNotificationStateVersion,
            NotificationState = new()
            {
                ["five_hour"] = new WindowAlertState { LastAlertedLevel = AlertLevel.Warn },
            },
        };
        s.Normalize();
        Assert.Single(s.NotificationState);
        Assert.Equal(AppSettings.CurrentNotificationStateVersion, s.NotificationStateVersion);
    }
}
