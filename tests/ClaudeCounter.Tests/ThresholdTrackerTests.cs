using ClaudeCounter.Core;
using ClaudeCounter.Notifications;
using ClaudeCounter.Settings;
using Xunit;

namespace ClaudeCounter.Tests;

public class ThresholdTrackerTests
{
    private static readonly DateTimeOffset R1 = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset R2 = new(2026, 8, 8, 17, 0, 0, TimeSpan.Zero);

    // All windows enabled so tests exercise the tracker, not the filter.
    private static AppSettings Settings() => new()
    {
        CriticalThreshold = 90,
        AlertFiveHour = true, AlertSevenDay = true,
        AlertSevenDayOpus = true, AlertSevenDaySonnet = true,
    };

    private static UsageSnapshot FiveHour(double util, DateTimeOffset? reset = null) =>
        new(new UsageWindow(util, reset ?? R1), null, null, null, null);

    [Fact]
    public void BelowCriticalEmitsNothing()
    {
        var t = new ThresholdTracker();
        Assert.Empty(t.Evaluate(FiveHour(50), Settings()));
    }

    [Fact]
    public void CriticalCrossingEmitsOnce()
    {
        var t = new ThresholdTracker();
        var e = Assert.Single(t.Evaluate(FiveHour(91), Settings()));
        Assert.Equal(AlertLevel.Critical, e.Level);
        Assert.Equal("five_hour", e.WindowKey);
        Assert.Empty(t.Evaluate(FiveHour(93), Settings())); // still critical, no repeat
    }

    [Fact]
    public void MaxedEmitsOnceAndEscalatesFromCritical()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91), Settings());
        var e = Assert.Single(t.Evaluate(FiveHour(100), Settings()));
        Assert.Equal(AlertLevel.Maxed, e.Level);
        Assert.Empty(t.Evaluate(FiveHour(100), Settings()));
    }

    [Fact]
    public void DipBelowDoesNotReArm()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91), Settings());
        Assert.Empty(t.Evaluate(FiveHour(80), Settings())); // dropped but same window
        Assert.Empty(t.Evaluate(FiveHour(91), Settings())); // back up, no repeat
    }

    [Fact]
    public void WindowResetReArms()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91, R1), Settings());
        Assert.Empty(t.Evaluate(FiveHour(5, R2), Settings())); // reset, back to normal
        var e = Assert.Single(t.Evaluate(FiveHour(91, R2), Settings()));
        Assert.Equal(AlertLevel.Critical, e.Level);
    }

    [Fact]
    public void SharpDropCountsAsResetWhenResetsAtUnchanged()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(95, R1), Settings());
        Assert.Empty(t.Evaluate(FiveHour(5, R1), Settings())); // collapse, same resets_at
        Assert.Single(t.Evaluate(FiveHour(95, R1), Settings())); // re-armed
    }

    [Fact]
    public void ResetLandingBetweenWarnAndCriticalIsNotDetected()
    {
        // Documents a known limitation, not desired behavior: a genuine window
        // reset whose resets_at does not change and whose post-reset reading
        // lands in [WarnThreshold, CriticalThreshold) - here 80, with the
        // default WarnThreshold of 75 and CriticalThreshold of 90 - is not
        // recognized as a reset. LastAlertedLevel stays Critical from the prior
        // cycle, so a subsequent climb back to Critical in what is actually a
        // new window is silently suppressed. Real-world rollovers start near
        // zero (well below WarnThreshold) and are always caught; this gap only
        // applies to the unrealistic case of a fresh window instantly reading
        // 80%. See the reset-detection comment in ThresholdTracker.Eval.
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(95, R1), Settings());
        Assert.Empty(t.Evaluate(FiveHour(80, R1), Settings())); // same resets_at, lands between warn and critical
        Assert.Empty(t.Evaluate(FiveHour(95, R1), Settings())); // gap: re-crossing critical is suppressed
    }

    [Fact]
    public void DisabledWindowEmitsNothing()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertFiveHour = false;
        Assert.Empty(t.Evaluate(FiveHour(100), s));
    }

    [Fact]
    public void NullResetsAtIsHandled()
    {
        var t = new ThresholdTracker();
        var snap = new UsageSnapshot(null, null, null,
            new UsageWindow(100, null), null); // sonnet maxed, null resets_at
        var e = Assert.Single(t.Evaluate(snap, Settings()));
        Assert.Equal("seven_day_sonnet", e.WindowKey);
        Assert.Null(e.ResetsAt);
    }

    [Fact]
    public void StateRoundTripsThroughJson()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91), Settings());
        var json = System.Text.Json.JsonSerializer.Serialize(t.State);
        var restored = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, WindowAlertState>>(json)!;
        var resumed = new ThresholdTracker(restored);
        Assert.Empty(resumed.Evaluate(FiveHour(91), Settings())); // remembers it alerted
    }
}
