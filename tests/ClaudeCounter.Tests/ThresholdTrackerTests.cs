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
    // WarnAlertsEnabled defaults to true on AppSettings, so it is implicitly
    // on here too unless a test overrides it.
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
    public void MicrosecondJitterInResetsAtDoesNotReArm()
    {
        // The actual production bug (see task S12): the API recomputes
        // resets_at with microsecond precision on every request. The wall-
        // clock reset boundary is stable, but a naive DateTimeOffset !=
        // comparison sees a different value on every single poll and re-arms
        // every time, so the same crossing alerts again and again forever.
        // Real captured value: 2026-08-11T13:10:00.264965+02:00, jittering by
        // a few hundred microseconds per call while the minute never moves.
        var t = new ThresholdTracker();
        var baseReset = new DateTimeOffset(2026, 8, 11, 13, 10, 0, TimeSpan.FromHours(2))
            .AddTicks(2649650); // .264965s, matching the real captured value

        var first = Assert.Single(t.Evaluate(FiveHour(90, baseReset), Settings()));
        Assert.Equal(AlertLevel.Critical, first.Level);

        // Five more polls, each with resets_at jittered by a few hundred
        // microseconds (1 tick = 100ns, so a few thousand ticks), utilization
        // still climbing but staying at/above Critical the whole time - must
        // not re-alert even once.
        for (var i = 1; i <= 5; i++)
        {
            var jittered = baseReset.AddTicks(i * 3000); // ~300 microseconds per poll
            Assert.Empty(t.Evaluate(FiveHour(90 + i, jittered), Settings()));
        }
    }

    [Fact]
    public void WarnCrossingEmitsOnce()
    {
        var t = new ThresholdTracker();
        var e = Assert.Single(t.Evaluate(FiveHour(80), Settings()));
        Assert.Equal(AlertLevel.Warn, e.Level);
        Assert.Empty(t.Evaluate(FiveHour(85), Settings())); // still warn, no repeat
    }

    [Fact]
    public void WarnThenCriticalEmitsBothExactlyOnce()
    {
        var t = new ThresholdTracker();
        var warn = Assert.Single(t.Evaluate(FiveHour(80), Settings()));
        Assert.Equal(AlertLevel.Warn, warn.Level);
        var critical = Assert.Single(t.Evaluate(FiveHour(91), Settings()));
        Assert.Equal(AlertLevel.Critical, critical.Level);
    }

    [Fact]
    public void JumpingStraightToCriticalSkipsWarn()
    {
        // 50 -> 95 in one poll must emit Critical only, not Warn then Critical -
        // level is computed from the single current reading, not walked
        // through every intermediate tier.
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(50), Settings());
        var e = Assert.Single(t.Evaluate(FiveHour(95), Settings()));
        Assert.Equal(AlertLevel.Critical, e.Level);
    }

    [Fact]
    public void MaxedAfterCriticalEmitsOnce()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91), Settings());
        var e = Assert.Single(t.Evaluate(FiveHour(100), Settings()));
        Assert.Equal(AlertLevel.Maxed, e.Level);
    }

    [Fact]
    public void DipInsideWarnBandEmitsNothing()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(80), Settings());
        Assert.Empty(t.Evaluate(FiveHour(76), Settings())); // 76 still >= WarnThreshold(75)
    }

    [Fact]
    public void DroppingBelowWarnReArmsAndAlertsAgain()
    {
        // The bug this task exists to fix: a window that alerted at Warn and
        // then collapsed toward zero must re-arm through the level-based rule
        // (current level None while LastAlertedLevel is above None), not the
        // old ">= Critical" floor which never true for a Warn-only alert.
        var t = new ThresholdTracker();
        var warn = Assert.Single(t.Evaluate(FiveHour(80, R1), Settings()));
        Assert.Equal(AlertLevel.Warn, warn.Level);
        Assert.Empty(t.Evaluate(FiveHour(5, R1), Settings())); // collapse, same resets_at
        var again = Assert.Single(t.Evaluate(FiveHour(80, R1), Settings()));
        Assert.Equal(AlertLevel.Warn, again.Level);
    }

    [Fact]
    public void WarnAlertsDisabledSuppressesWarnButNotCriticalOrMaxed()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.WarnAlertsEnabled = false;

        Assert.Empty(t.Evaluate(FiveHour(80), s)); // in warn band, suppressed
        Assert.Empty(t.Evaluate(FiveHour(85), s)); // still warn band, still suppressed

        var critical = Assert.Single(t.Evaluate(FiveHour(91), s));
        Assert.Equal(AlertLevel.Critical, critical.Level);

        var maxed = Assert.Single(t.Evaluate(FiveHour(100), s));
        Assert.Equal(AlertLevel.Maxed, maxed.Level);
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
