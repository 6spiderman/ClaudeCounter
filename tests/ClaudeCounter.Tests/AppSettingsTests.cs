using ClaudeCounter.Settings;
using Xunit;

namespace ClaudeCounter.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultsAreSafe()
    {
        var s = new AppSettings();
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
}
