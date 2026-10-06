using ClaudeCounter.Core;
using ClaudeCounter.Settings;

namespace ClaudeCounter.Notifications;

public enum AlertLevel { None = 0, Warn = 1, Critical = 2, Maxed = 3 }

public sealed class WindowAlertState
{
    public AlertLevel LastAlertedLevel { get; set; }
    public DateTimeOffset? LastResetsAt { get; set; }

    /// <summary>
    /// S12 part B: wall-clock time of the most recent notification (initial
    /// crossing OR a repeat re-notify) for this window's current alerted
    /// level. Null when no notification has happened yet for the current
    /// level (including right after a reset clears it - see
    /// ThresholdTracker.Eval). Only consulted when
    /// AppSettings.AlertRepeatMinutes is greater than 0; a missing value on
    /// an old settings.json (property simply absent) reads back as null via
    /// ordinary JSON deserialization, which is exactly the safe default - it
    /// just means "not due to repeat yet, wait a full interval from the next
    /// notification" rather than an immediate repeat on restart. Does not
    /// need a schema version bump: AlertLevel's int encoding is unchanged,
    /// and an absent property on old data is not ambiguous the way a
    /// renumbered enum value would be.
    /// </summary>
    public DateTimeOffset? LastNotifiedAt { get; set; }
}

public sealed record AlertEvent(
    string WindowKey, string WindowLabel, AlertLevel Level,
    double Utilization, DateTimeOffset? ResetsAt);

/// <summary>
/// Pure state machine: one alert per threshold crossing per window. A dip below
/// a threshold does NOT re-arm (no boundary ping-pong); only a window reset
/// (changed resets_at, or usage collapsing all the way back below the lowest
/// alerting tier) clears state so the next crossing alerts again. No UI, no IO -
/// unit-tested in isolation.
/// </summary>
public sealed class ThresholdTracker
{
    private readonly Dictionary<string, WindowAlertState> _state;

    public ThresholdTracker(Dictionary<string, WindowAlertState>? initialState = null) =>
        _state = initialState ?? new Dictionary<string, WindowAlertState>();

    public Dictionary<string, WindowAlertState> State => _state;

    /// <summary>
    /// S12 part B: <paramref name="now"/> defaults to DateTimeOffset.UtcNow
    /// when omitted, so production callers (TrayApplicationContext) do not
    /// have to pass anything, while tests exercising AlertRepeatMinutes'
    /// cadence can supply deterministic timestamps instead of depending on
    /// wall-clock time. Only used for the repeat-notification check below -
    /// reset and crossing detection are otherwise unchanged and still driven
    /// purely by the snapshot's own values.
    /// </summary>
    public List<AlertEvent> Evaluate(UsageSnapshot snapshot, AppSettings settings, DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        var events = new List<AlertEvent>();

        // S13 part B: the 5-hour window is the master clock for repeat-alert
        // cadence. This check is deliberately done up front, BEFORE any of
        // the four Eval calls below run - including five_hour's own - so the
        // cross-window re-anchor does not silently depend on "five_hour
        // happens to be evaluated first" (it currently is, but that ordering
        // is an implementation detail of the Eval calls below, not something
        // this should be fragile against). ReadFiveHourRollover only reads
        // state, never mutates it, so evaluating it first and then still
        // running five_hour's own Eval afterwards (which DOES mutate state)
        // is safe and not a double-count of anything.
        var fiveHourRolledOver = ReadFiveHourRollover(snapshot.FiveHour, settings.AlertFiveHour);
        if (fiveHourRolledOver && settings.AlertRepeatMinutes > 0)
        {
            // Re-anchor every window's repeat clock to this rollover moment -
            // not just five_hour's own. A weekly window sitting mid-alert
            // (e.g. parked at Warn from hours ago) gets "time since last
            // notified" reset to zero here, so its next repeat fires one full
            // AlertRepeatMinutes after the rollover, not whenever its own
            // last notification happened to land. Guarded on
            // AlertRepeatMinutes > 0 so this is a true no-op in the default
            // (repeat off) mode: LastNotifiedAt is otherwise unread when
            // AlertRepeatMinutes is 0 (see the repeat block in Eval), but
            // skipping the write entirely means the persisted state is
            // byte-identical to before this feature, not merely
            // behaviorally identical.
            //
            // Deliberately does NOT touch LastAlertedLevel: re-anchoring the
            // repeat *timer* must not re-arm a window's crossing dedupe. A
            // weekly window already alerted at (say) 95% stays recorded as
            // alerted at 95% - only five_hour's own Eval call (via its own
            // IsGenuineReset/collapsedToNone check) can clear a window's
            // LastAlertedLevel back to None.
            foreach (var state in _state.Values)
                state.LastNotifiedAt = effectiveNow;
        }

        Eval("five_hour", "5-hour session", snapshot.FiveHour, settings.AlertFiveHour, settings, effectiveNow, events);
        Eval("seven_day", "Weekly (all models)", snapshot.SevenDay, settings.AlertSevenDay, settings, effectiveNow, events);
        Eval("seven_day_opus", "Weekly (Opus)", snapshot.SevenDayOpus, settings.AlertSevenDayOpus, settings, effectiveNow, events);
        Eval("seven_day_sonnet", "Weekly (Sonnet)", snapshot.SevenDaySonnet, settings.AlertSevenDaySonnet, settings, effectiveNow, events);
        return events;
    }

    /// <summary>
    /// S13 part B: pure read of whether the 5-hour window just genuinely
    /// rolled over, using the exact same IsGenuineReset test Eval itself
    /// uses for five_hour's own reset detection - not the collapsedToNone
    /// fallback, only IsGenuineReset. Does not touch <see cref="_state"/> at
    /// all (a plain lookup), so calling this ahead of Eval("five_hour", ...)
    /// cannot interfere with or duplicate what that call does to state.
    ///
    /// When the 5-hour window is disabled (settings.AlertFiveHour is false),
    /// Eval returns early for it before ever comparing resets_at - no reset
    /// can be observed while the window is not being evaluated at all, so
    /// this deliberately returns false rather than inventing a rollover.
    /// The cadence anchor is simply inactive while five_hour is disabled;
    /// per-window repeat timers fall back to running off their own
    /// LastNotifiedAt, same as before this feature existed. If the user
    /// re-enables five_hour later, the next observed rollover (compared
    /// against whatever LastResetsAt was last recorded before it was
    /// disabled) resumes the cross-window anchor normally.
    /// </summary>
    private bool ReadFiveHourRollover(UsageWindow? fiveHourWindow, bool fiveHourEnabled)
    {
        if (!fiveHourEnabled || fiveHourWindow is null)
            return false;

        _state.TryGetValue("five_hour", out var state);
        return IsGenuineReset(state?.LastResetsAt, fiveHourWindow.ResetsAt);
    }

    private void Eval(string key, string label, UsageWindow? window, bool enabled,
        AppSettings settings, DateTimeOffset now, List<AlertEvent> events)
    {
        if (window is null || !enabled)
            return;

        if (!_state.TryGetValue(key, out var state))
            _state[key] = state = new WindowAlertState();

        var level = window.Utilization >= 100 ? AlertLevel.Maxed
            : window.Utilization >= settings.CriticalThreshold ? AlertLevel.Critical
            : window.Utilization >= settings.WarnThreshold ? AlertLevel.Warn
            : AlertLevel.None;

        // Reset detection: resets_at genuinely rolled FORWARD, or the window's
        // CURRENT level has collapsed all the way back to None while it was
        // previously alerted above None (covers windows whose resets_at does
        // not change on rollover, and the null-resets_at case where equality
        // never changes). Level-based rather than a fixed utilization floor so
        // every alerting tier - Warn included - re-arms uniformly: a window
        // that alerted at Warn and then dropped to near-zero must not be stuck
        // forever just because it never reached Critical.
        //
        // A genuine rollover is IsGenuineReset returning true (see its own
        // remarks - the boundary must move forward by more than jitter noise);
        // the level-collapse clause below is only a fallback for windows whose
        // timestamp does not move on rollover. Known limitation: if such a
        // window rolls over AND its first reported utilization in the new
        // window lands in [WarnThreshold, CriticalThreshold) - e.g. a fresh
        // window instantly read at 80% - that rollover is missed, and a later
        // re-crossing of CriticalThreshold in the new window stays suppressed
        // until a real dip below WarnThreshold or a resets_at change occurs.
        // This is accepted: a genuine rollover starts a window at (near) zero
        // usage, which is always well below WarnThreshold and so is always
        // caught; a rollover that instantly reads 80% does not correspond to
        // any real-world usage pattern. See
        // ResetLandingBetweenWarnAndCriticalIsNotDetected, which pins this
        // down as documented current behavior rather than a silently
        // rediscovered bug.
        var resetsAtChanged = IsGenuineReset(state.LastResetsAt, window.ResetsAt);
        var collapsedToNone = level == AlertLevel.None && state.LastAlertedLevel > AlertLevel.None;
        if (resetsAtChanged || collapsedToNone)
        {
            state.LastAlertedLevel = AlertLevel.None;
            // S12 part B: a reset also clears the repeat-notification clock -
            // the window is starting over, so any future re-notify timer
            // should count from the NEXT crossing, not from however long ago
            // the old window last repeated.
            state.LastNotifiedAt = null;
        }
        state.LastResetsAt = window.ResetsAt;

        // Warn has a master on/off switch on top of the per-window enabled
        // check above (which already gated the whole method). When it is off,
        // a Warn-level crossing is treated as if the tier did not exist at all
        // - no event, and no state advance - so turning the toggle back on
        // later alerts immediately if usage is still sitting in the Warn band,
        // rather than staying silently suppressed by stale dedupe state.
        var warnGated = level == AlertLevel.Warn && !settings.WarnAlertsEnabled;

        if (!warnGated && level > state.LastAlertedLevel)
        {
            // Escalation (or a fresh crossing): fires immediately regardless
            // of AlertRepeatMinutes - a jump from Warn to Critical must not
            // wait out whatever is left of the repeat interval.
            events.Add(new AlertEvent(key, label, level, window.Utilization, window.ResetsAt));
            state.LastAlertedLevel = level;
            state.LastNotifiedAt = now;
            return;
        }

        // S12 part B repeat cadence: AppSettings.AlertRepeatMinutes == 0 (the
        // default) means "only once per crossing" - the behavior restored by
        // part A's reset-detection fix - so this block is a no-op in that
        // case. When greater than 0, a window sitting AT (not below) its
        // already-alerted level re-notifies every N minutes. warnGated still
        // applies here: if Warn alerts are switched off, a window parked at
        // Warn must not repeat-notify either, same as it cannot initially
        // notify. Critical/Maxed's own master toggles are not checked here -
        // TrayApplicationContext already filters those out of every event
        // (initial or repeat) it receives before showing a popup, exactly as
        // it already did for the initial crossing before this feature
        // existed, so repeat events are never shown for a disabled level.
        // `level == state.LastAlertedLevel` (rather than >=) is deliberate: a
        // window that dipped below its last-alerted level (still > None) is
        // "DipBelowDoesNotReArm" territory and must stay silent, not repeat.
        if (!warnGated && settings.AlertRepeatMinutes > 0 && level != AlertLevel.None
            && level == state.LastAlertedLevel && state.LastNotifiedAt is { } lastNotified
            && now - lastNotified >= TimeSpan.FromMinutes(settings.AlertRepeatMinutes))
        {
            events.Add(new AlertEvent(key, label, level, window.Utilization, window.ResetsAt));
            state.LastNotifiedAt = now;
        }
    }

    /// <summary>
    /// S12: the fix for the actual production bug. The API recomputes
    /// resets_at with microsecond precision on every request - the persisted
    /// value and the freshly-fetched one are almost never byte-identical even
    /// though the wall-clock reset boundary has not moved. A plain != would
    /// therefore see a "change" on every single poll and re-arm every time.
    ///
    /// A genuine rollover moves the boundary FORWARD by the window's full
    /// length (hours or days) - nowhere close to jitter noise - so "later by
    /// more than a tolerance" cleanly separates the two. ResetTolerance is 60
    /// seconds: comfortably above any observed sub-second jitter (hundreds of
    /// microseconds) yet far below the shortest real window length (5 hours),
    /// so there is no realistic value of either that this could misclassify.
    ///
    /// Direction matters, not just magnitude. A backwards jump (the new value
    /// earlier than the stored one) is NOT treated as a reset: resets_at only
    /// ever moves forward as a window counts down to its boundary and then
    /// rolls to the next one, so a value that moved backwards is clock skew
    /// or noise, never a legitimate rollover. Treating it as a reset would
    /// let noise re-arm alerts in either direction, which is the exact bug
    /// this method exists to close - just flipped in sign. If a real rollover
    /// ever does coincide with a backwards-looking resets_at (not observed in
    /// practice), the collapsedToNone fallback in Eval still catches it via
    /// the usage drop that a genuine new window starts with.
    ///
    /// previous == null means this is the first observation for the window
    /// (no prior value to compare against) - not a reset, since there is
    /// nothing for the boundary to have moved forward from; the window's
    /// state already starts at AlertLevel.None regardless. current == null
    /// (a window that previously reported a resets_at now reports none) is
    /// likewise not treated as a reset here for the same "no evidence of
    /// forward movement" reason; collapsedToNone remains the fallback if
    /// usage actually dropped.
    /// </summary>
    private static readonly TimeSpan ResetTolerance = TimeSpan.FromSeconds(60);

    private static bool IsGenuineReset(DateTimeOffset? previous, DateTimeOffset? current)
    {
        if (previous is null || current is null)
            return false;
        return current.Value - previous.Value > ResetTolerance;
    }
}
