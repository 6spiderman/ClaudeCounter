using ClaudeCounter.Notifications;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

public class AlertContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CriticalMentionsWindowAndPercent()
    {
        var e = new AlertEvent("five_hour", "5-hour session", AlertLevel.Critical, 92,
            Now.AddHours(2));
        var (title, body) = AlertContent.For(e, Now);
        Assert.Contains("5-hour session", title);
        Assert.Contains("92%", body);
        Assert.Contains("resets in", body);
    }

    [Fact]
    public void MaxedSaysTouchGrass()
    {
        var e = new AlertEvent("five_hour", "5-hour session", AlertLevel.Maxed, 100, null);
        var (title, _) = AlertContent.For(e, Now);
        Assert.Contains("touch some grass", title, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullResetsAtOmitsCountdown()
    {
        var e = new AlertEvent("seven_day_sonnet", "Weekly (Sonnet)", AlertLevel.Critical, 91, null);
        var (_, body) = AlertContent.For(e, Now);
        Assert.DoesNotContain("resets in", body);
    }
}
