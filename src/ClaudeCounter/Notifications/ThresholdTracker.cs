using ClaudeCounter.Core;
using ClaudeCounter.Settings;

namespace ClaudeCounter.Notifications;

public enum AlertLevel { None = 0, Warn = 1, Critical = 2, Maxed = 3 }

public sealed class WindowAlertState
{
    public AlertLevel LastAlertedLevel { get; set; }
    public DateTimeOffset? LastResetsAt { get; set; }
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

    public List<AlertEvent> Evaluate(UsageSnapshot snapshot, AppSettings settings)
    {
        var events = new List<AlertEvent>();
        Eval("five_hour", "5-hour session", snapshot.FiveHour, settings.AlertFiveHour, settings, events);
        Eval("seven_day", "Weekly (all models)", snapshot.SevenDay, settings.AlertSevenDay, settings, events);
        Eval("seven_day_opus", "Weekly (Opus)", snapshot.SevenDayOpus, settings.AlertSevenDayOpus, settings, events);
        Eval("seven_day_sonnet", "Weekly (Sonnet)", snapshot.SevenDaySonnet, settings.AlertSevenDaySonnet, settings, events);
        return events;
    }

    private void Eval(string key, string label, UsageWindow? window, bool enabled,
        AppSettings settings, List<AlertEvent> events)
    {
        if (window is null || !enabled)
            return;

        if (!_state.TryGetValue(key, out var state))
            _state[key] = state = new WindowAlertState();

        var level = window.Utilization >= 100 ? AlertLevel.Maxed
            : window.Utilization >= settings.CriticalThreshold ? AlertLevel.Critical
            : window.Utilization >= settings.WarnThreshold ? AlertLevel.Warn
            : AlertLevel.None;

        // Reset detection: resets_at changed, or the window's CURRENT level has
        // collapsed all the way back to None while it was previously alerted
        // above None (covers windows whose resets_at does not change on
        // rollover, and the null-resets_at case where equality never changes).
        // Level-based rather than a fixed utilization floor so every alerting
        // tier - Warn included - re-arms uniformly: a window that alerted at
        // Warn and then dropped to near-zero must not be stuck forever just
        // because it never reached Critical.
        //
        // resets_at changing is the primary reset signal; the level-collapse
        // clause below is only a fallback for windows whose timestamp does not
        // move on rollover. Known limitation: if such a window rolls over AND
        // its first reported utilization in the new window lands in
        // [WarnThreshold, CriticalThreshold) - e.g. a fresh window instantly
        // read at 80% - that rollover is missed, and a later re-crossing of
        // CriticalThreshold in the new window stays suppressed until a real dip
        // below WarnThreshold or a resets_at change occurs. This is accepted: a
        // genuine rollover starts a window at (near) zero usage, which is
        // always well below WarnThreshold and so is always caught; a rollover
        // that instantly reads 80% does not correspond to any real-world usage
        // pattern. See ResetLandingBetweenWarnAndCriticalIsNotDetected, which
        // pins this down as documented current behavior rather than a silently
        // rediscovered bug.
        var resetsAtChanged = state.LastResetsAt != window.ResetsAt;
        var collapsedToNone = level == AlertLevel.None && state.LastAlertedLevel > AlertLevel.None;
        if (resetsAtChanged || collapsedToNone)
            state.LastAlertedLevel = AlertLevel.None;
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
            events.Add(new AlertEvent(key, label, level, window.Utilization, window.ResetsAt));
            state.LastAlertedLevel = level;
        }
    }
}
