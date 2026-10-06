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

    // --- S12 part B: AlertRepeatMinutes cadence ---

    [Fact]
    public void RepeatMinutesZeroMeansOnlyOncePerCrossingEvenWithTimePassing()
    {
        // Default (0) must reproduce exactly the restored "once per crossing"
        // behavior, regardless of how much wall-clock time passes.
        var t = new ThresholdTracker();
        var s = Settings();
        Assert.Equal(0, s.AlertRepeatMinutes);
        var start = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Single(t.Evaluate(FiveHour(91), s, start));
        Assert.Empty(t.Evaluate(FiveHour(91), s, start.AddHours(10))); // way past any interval, still no repeat
    }

    [Fact]
    public void RepeatFiresAfterIntervalElapsedButNotBefore()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;
        var start = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        var first = Assert.Single(t.Evaluate(FiveHour(91), s, start));
        Assert.Equal(AlertLevel.Critical, first.Level);

        Assert.Empty(t.Evaluate(FiveHour(91), s, start.AddMinutes(10))); // too soon
        Assert.Empty(t.Evaluate(FiveHour(91), s, start.AddMinutes(14))); // still too soon

        var repeat = Assert.Single(t.Evaluate(FiveHour(91), s, start.AddMinutes(15)));
        Assert.Equal(AlertLevel.Critical, repeat.Level);

        // Cadence continues from the last notification, not the first crossing.
        Assert.Empty(t.Evaluate(FiveHour(91), s, start.AddMinutes(29)));
        Assert.Single(t.Evaluate(FiveHour(91), s, start.AddMinutes(30)));
    }

    [Fact]
    public void EscalationFiresImmediatelyWithoutWaitingForRepeatInterval()
    {
        // Crossing Warn -> Critical must not be delayed by whatever is left
        // of the repeat interval that started when Warn first alerted.
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 60;
        var start = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        var warn = Assert.Single(t.Evaluate(FiveHour(80), s, start));
        Assert.Equal(AlertLevel.Warn, warn.Level);

        // One minute later - nowhere near the 60-minute repeat interval - but
        // this is an escalation, so it must still fire.
        var critical = Assert.Single(t.Evaluate(FiveHour(91), s, start.AddMinutes(1)));
        Assert.Equal(AlertLevel.Critical, critical.Level);
    }

    [Fact]
    public void RepeatDoesNotFireForDippedBelowLastAlertedLevel()
    {
        // Sitting below the last-alerted level (but still above None) must
        // stay silent even with repeat enabled - matches DipBelowDoesNotReArm.
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;
        var start = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Single(t.Evaluate(FiveHour(91), s, start)); // Critical
        Assert.Empty(t.Evaluate(FiveHour(80), s, start.AddMinutes(20))); // dropped to Warn band, past interval, still silent
    }

    [Fact]
    public void RepeatRespectsWarnAlertsDisabledToggle()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;
        s.WarnAlertsEnabled = false;
        var start = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Empty(t.Evaluate(FiveHour(80), s, start)); // warn suppressed entirely
        Assert.Empty(t.Evaluate(FiveHour(80), s, start.AddMinutes(30))); // still suppressed, no repeat either
    }

    [Fact]
    public void ResetClearsRepeatCadenceSoNewWindowWaitsAFullInterval()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;
        var start = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var r2 = new DateTimeOffset(2026, 8, 11, 17, 0, 0, TimeSpan.Zero);

        Assert.Single(t.Evaluate(FiveHour(91, start), s, start)); // Critical, notified at `start`
        Assert.Empty(t.Evaluate(FiveHour(5, r2), s, start.AddMinutes(1))); // window resets, back to normal

        // Immediately re-crosses Critical in the new window: this is a fresh
        // crossing (escalation from None), so it fires right away...
        var again = Assert.Single(t.Evaluate(FiveHour(91, r2), s, start.AddMinutes(2)));
        Assert.Equal(AlertLevel.Critical, again.Level);

        // ...but the repeat clock restarted at the new crossing, not the old
        // one - 14 minutes later (which would have been >15 min since the
        // very first alert) must still be silent.
        Assert.Empty(t.Evaluate(FiveHour(91, r2), s, start.AddMinutes(16)));
    }

    [Fact]
    public void LastNotifiedAtRoundTripsThroughJsonSoRestartDoesNotImmediatelyRepeat()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;
        var start = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Single(t.Evaluate(FiveHour(91), s, start));
        var json = System.Text.Json.JsonSerializer.Serialize(t.State);
        var restored = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, WindowAlertState>>(json)!;
        var resumed = new ThresholdTracker(restored);

        // Restart happens 1 minute after the original alert - must not
        // immediately re-notify just because the process restarted.
        Assert.Empty(resumed.Evaluate(FiveHour(91), s, start.AddMinutes(1)));

        // But the cadence itself survived the round-trip: 15 minutes after
        // the ORIGINAL notification, it repeats normally.
        Assert.Single(resumed.Evaluate(FiveHour(91), s, start.AddMinutes(15)));
    }

    // --- S13 part B: 5-hour window anchors the repeat cadence for everyone ---

    private static readonly DateTimeOffset Start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FhReset1 = new(2026, 8, 11, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FhReset2 = new(2026, 8, 11, 18, 0, 0, TimeSpan.Zero); // FhReset1 + 5h

    // Five-hour utilization kept low (10) throughout so it never itself
    // crosses a threshold - these tests isolate the CROSS-window anchoring
    // effect on seven_day/seven_day_opus, not five_hour's own alerting.
    private static UsageSnapshot SnapFiveHourAndWeekly(
        DateTimeOffset fiveHourReset, double sevenDayUtil, double? sevenDayOpusUtil = null) =>
        new(new UsageWindow(10, fiveHourReset), new UsageWindow(sevenDayUtil, R1),
            sevenDayOpusUtil is { } opus ? new UsageWindow(opus, R1) : null, null, null);

    [Fact]
    public void FiveHourRolloverReAnchorsAWeeklyWindowsRepeatClock()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;

        // Weekly crosses Critical at Start; five-hour has not rolled over yet.
        var first = Assert.Single(
            t.Evaluate(SnapFiveHourAndWeekly(FhReset1, 91), s, Start), e => e.WindowKey == "seven_day");
        Assert.Equal(AlertLevel.Critical, first.Level);

        // 20 minutes later the 5-hour window genuinely rolls over (resets_at
        // moves forward 5 hours, well past the jitter tolerance). Without the
        // anchor, seven_day's own clock (started at Start) would already be
        // overdue by 5 minutes and would fire a repeat right here. With the
        // anchor, it must NOT fire on this poll - its clock resets to this
        // rollover moment instead.
        var atRollover = t.Evaluate(SnapFiveHourAndWeekly(FhReset2, 91), s, Start.AddMinutes(20));
        Assert.DoesNotContain(atRollover, e => e.WindowKey == "seven_day");

        // 14 minutes after the rollover: still too soon.
        Assert.Empty(t.Evaluate(SnapFiveHourAndWeekly(FhReset2, 91), s, Start.AddMinutes(34))
            .Where(e => e.WindowKey == "seven_day"));

        // 15 minutes after the ROLLOVER (not after the original crossing at
        // Start, and not the stale 15-minutes-after-Start mark either):
        // seven_day repeats.
        var repeat = Assert.Single(
            t.Evaluate(SnapFiveHourAndWeekly(FhReset2, 91), s, Start.AddMinutes(35)),
            e => e.WindowKey == "seven_day");
        Assert.Equal(AlertLevel.Critical, repeat.Level);
    }

    [Fact]
    public void AWeeklyWindowsOwnResetDoesNotReAnchorOtherWindows()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;
        var sdReset1 = R1;
        var sdReset2 = R1.AddDays(7);

        UsageSnapshot Snap(double sevenDayUtil, DateTimeOffset sevenDayReset, double opusUtil) =>
            new(new UsageWindow(10, FhReset1), new UsageWindow(sevenDayUtil, sevenDayReset),
                new UsageWindow(opusUtil, R1), null, null);

        // Both seven_day and seven_day_opus cross Critical at Start.
        var firstEvents = t.Evaluate(Snap(91, sdReset1, 91), s, Start);
        Assert.Equal(2, firstEvents.Count);

        // 20 minutes later, seven_day itself genuinely resets (its own
        // resets_at rolls forward, usage collapses) - five_hour does NOT
        // roll over this poll. seven_day_opus sits unchanged at Critical.
        var atReset = t.Evaluate(Snap(5, sdReset2, 91), s, Start.AddMinutes(20));

        // seven_day: reset detected, back below threshold, no event.
        Assert.DoesNotContain(atReset, e => e.WindowKey == "seven_day");

        // seven_day_opus must NOT have had its clock re-anchored by
        // seven_day's own reset - only a five_hour rollover re-anchors other
        // windows. Its 15-minute clock (started at Start) is genuinely
        // overdue by now (20 >= 15), so it correctly fires its own repeat.
        var opusEvents = atReset.Where(e => e.WindowKey == "seven_day_opus").ToList();
        var opusRepeat = Assert.Single(opusEvents);
        Assert.Equal(AlertLevel.Critical, opusRepeat.Level);
    }

    [Fact]
    public void RepeatMinutesZeroMeansFiveHourRolloverTouchesNothing()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        Assert.Equal(0, s.AlertRepeatMinutes);

        t.Evaluate(SnapFiveHourAndWeekly(FhReset1, 91), s, Start);
        var notifiedAtCrossing = t.State["seven_day"].LastNotifiedAt;

        // Five-hour rolls over; with repeat off this must be a complete
        // no-op for every other window - no event, and no state write either
        // (persisted state stays byte-identical to pre-S13 behavior).
        var atRollover = t.Evaluate(SnapFiveHourAndWeekly(FhReset2, 91), s, Start.AddMinutes(20));
        Assert.DoesNotContain(atRollover, e => e.WindowKey == "seven_day");
        Assert.Equal(notifiedAtCrossing, t.State["seven_day"].LastNotifiedAt);

        // ...and no repeat ever fires afterward either, no matter how much
        // time passes - matches RepeatMinutesZeroMeansOnlyOncePerCrossingEvenWithTimePassing.
        Assert.Empty(t.Evaluate(SnapFiveHourAndWeekly(FhReset2, 91), s, Start.AddDays(30))
            .Where(e => e.WindowKey == "seven_day"));
    }

    [Fact]
    public void FiveHourRolloverDoesNotReArmAWeeklyWindowsCrossingDedupe()
    {
        // The anti-nag property: re-anchoring the repeat TIMER must never
        // re-arm a window's crossing dedupe (LastAlertedLevel). If it did, a
        // weekly window parked at 95% would re-alert every single 5-hour
        // rollover forever - exactly the nagging this feature exists to stop.
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;

        var first = Assert.Single(
            t.Evaluate(SnapFiveHourAndWeekly(FhReset1, 95), s, Start), e => e.WindowKey == "seven_day");
        Assert.Equal(AlertLevel.Critical, first.Level);

        // 1 minute later (nowhere near the 15-minute repeat interval) the
        // 5-hour window rolls over. If the rollover incorrectly cleared
        // seven_day's LastAlertedLevel back to None, this poll (still at
        // 95%, Critical > None) would fire an ESCALATION event right here,
        // regardless of the repeat interval - the exact bug this test guards
        // against.
        var atRollover = t.Evaluate(SnapFiveHourAndWeekly(FhReset2, 95), s, Start.AddMinutes(1));
        Assert.DoesNotContain(atRollover, e => e.WindowKey == "seven_day");
    }

    [Fact]
    public void FiveHourRolloverWhileFiveHourDisabledDoesNotAnchorOtherWindows()
    {
        // settings.AlertFiveHour = false means Eval never even looks at
        // five_hour's own state, so no rollover can ever be observed while
        // it is off - the cross-window anchor is simply inactive, and every
        // window's repeat clock keeps running off its own LastNotifiedAt,
        // exactly as before S13.
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertRepeatMinutes = 15;
        s.AlertFiveHour = false;

        var first = Assert.Single(
            t.Evaluate(SnapFiveHourAndWeekly(FhReset1, 91), s, Start), e => e.WindowKey == "seven_day");
        Assert.Equal(AlertLevel.Critical, first.Level);

        // A resets_at value that WOULD have been a genuine 5-hour rollover if
        // AlertFiveHour were on. With it off, this changes nothing about
        // seven_day's own clock.
        Assert.Empty(t.Evaluate(SnapFiveHourAndWeekly(FhReset2, 91), s, Start.AddMinutes(10))
            .Where(e => e.WindowKey == "seven_day")); // too soon on seven_day's own clock

        var repeat = Assert.Single(
            t.Evaluate(SnapFiveHourAndWeekly(FhReset2, 91), s, Start.AddMinutes(15)),
            e => e.WindowKey == "seven_day"); // seven_day's own 15-minute clock, unaffected
        Assert.Equal(AlertLevel.Critical, repeat.Level);
    }
}
