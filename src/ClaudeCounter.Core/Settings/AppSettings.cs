using System.Text.Json.Serialization;
using ClaudeBackup;
using ClaudeCounter.Notifications;

namespace ClaudeCounter.Settings;

public enum PopupPlacement { NearTray, Centered }

public sealed class AppSettings
{
    public static readonly int[] IntervalPresets = [1, 2, 5, 10, 15, 30, 60];

    public int PollIntervalMinutes { get; set; } = 5;
    public int WarnThreshold { get; set; } = 75;
    public int CriticalThreshold { get; set; } = 90;
    public bool AutostartEnabled { get; set; } = true;

    public bool WarnAlertsEnabled { get; set; } = true;
    public bool CriticalAlertsEnabled { get; set; } = true;
    public bool MaxedAlertsEnabled { get; set; } = true;
    public bool AlertFiveHour { get; set; } = true;
    public bool AlertSevenDay { get; set; } = true;
    public bool AlertSevenDayOpus { get; set; }        // default false
    public bool AlertSevenDaySonnet { get; set; }      // default false
    public PopupPlacement PopupPlacement { get; set; } = PopupPlacement.NearTray;

    /// <summary>
    /// How long a near-tray popup waits before auto-dismissing itself. Only
    /// governs near-tray popups - a centered popup (which includes every
    /// Maxed popup, always centered regardless of PopupPlacement) takes focus
    /// and always waits for explicit dismissal, so this value has no effect
    /// on it. 0 is a valid, meaningful setting ("never auto-dismiss"), not
    /// "unset" - see Normalize, which must not coerce it to the default.
    /// </summary>
    public int PopupAutoDismissSeconds { get; set; } = 12;

    /// <summary>
    /// S12 part B: how often (in minutes) an already-alerted window that is
    /// STILL at or above the level it alerted at gets re-notified, on top of
    /// the always-on "once per crossing" behavior. 0 (the default) means
    /// "only once per crossing, until reset" - i.e. repeat is off - which is
    /// also the behavior restored by the S12 part A fix to reset detection.
    /// Like PopupAutoDismissSeconds, 0 is a meaningful, deliberate value
    /// here, not "unset" - see Normalize, which clamps but must not coerce
    /// it away from 0. ThresholdTracker.Eval is where this is actually
    /// consulted; escalating to a higher level (e.g. Warn -> Critical)
    /// always fires immediately and is never delayed by this interval.
    /// </summary>
    public int AlertRepeatMinutes { get; set; }

    // Persisted alert dedupe state so restarts do not re-pop.
    public Dictionary<string, WindowAlertState> NotificationState { get; set; } = new();

    /// <summary>
    /// Schema version of <see cref="NotificationState"/>'s <c>AlertLevel</c>
    /// encoding. AlertLevel is persisted as a plain int, and inserting Warn
    /// between None and Critical renumbered Critical from 1 to 2 and Maxed
    /// from 2 to 3 - a settings.json written before Warn existed would
    /// otherwise be silently misread (a stored 1, meaning Critical, would come
    /// back as Warn). Left at its default (0) for a pre-Warn file, since the
    /// property is simply absent from old JSON; bumped to
    /// <see cref="CurrentNotificationStateVersion"/> by <see cref="Normalize"/>
    /// once the mismatch has been handled. Do not remap old int values - the
    /// mapping is ambiguous once Maxed is involved.
    /// </summary>
    public int NotificationStateVersion { get; set; }

    /// <summary>Current schema version for <see cref="NotificationState"/>. Bump when AlertLevel's int encoding changes again.</summary>
    public const int CurrentNotificationStateVersion = 1;

    /// <summary>
    /// True when the most recent <see cref="Normalize"/> call found a stale
    /// <see cref="NotificationStateVersion"/> and cleared <see cref="NotificationState"/>.
    /// Transient (not persisted, not written by JSON deserialization) - exists
    /// only so a caller such as <c>TrayApplicationContext</c> can tell "the
    /// version was just bumped in memory" from "it was already current" and
    /// persist the bump immediately, rather than leaving it to reach disk via
    /// some unrelated later save. <see cref="Normalize"/> itself does no I/O.
    /// </summary>
    [JsonIgnore]
    public bool NotificationStateJustMigrated { get; private set; }

    /// <summary>Ask GitHub once a day whether a newer release exists.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last update check ran, so a restart does not re-check.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>A version the user dismissed; do not nag about it again.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// False on a settings file written before onboarding existed, so an
    /// upgrading user is walked through sign-in once rather than silently
    /// dropped onto the bootstrap path.
    /// </summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>
    /// S11b: the most recently OBSERVED overall backup health state - not
    /// merely the last one a popup actually fired for. BackupHealthPresenter.
    /// ShouldNotify compares this against each new poll's state to detect a
    /// transition INTO a problem state; tracking every observed state (not
    /// just ones that popped up - a popup can be suppressed by a modal
    /// dialog, see TrayApplicationContext.EvaluateBackupHealthNotification)
    /// is what lets a later real failure re-arm correctly after a recovery to
    /// Healthy. Null before the first-ever backup poll, or forever on a
    /// machine with no backup worker installed (BackupTaskManager.
    /// WorkerAvailable) since that path never touches this field. Persisted
    /// alongside NotificationState so a tray restart does not re-announce an
    /// already-known failure.
    /// </summary>
    public BackupHealthState? LastBackupHealthState { get; set; }

    public void Normalize()
    {
        if (!IntervalPresets.Contains(PollIntervalMinutes))
            PollIntervalMinutes = 5;
        // 0 ("never auto-dismiss") is a meaningful in-range value, so this is
        // a plain clamp, not a fallback-to-default like the thresholds below -
        // coercing 0 to 12 here would silently revert a deliberate choice.
        PopupAutoDismissSeconds = Math.Clamp(PopupAutoDismissSeconds, 0, 300);
        // Same "0 is meaningful, not a fallback" convention as
        // PopupAutoDismissSeconds just above: 0 ("only once per crossing") is
        // a deliberate in-range value and must survive Normalize unchanged.
        // Ceiling of 1440 (24 hours) is generous enough for any repeat cadence
        // a user would actually want while still rejecting garbage input.
        AlertRepeatMinutes = Math.Clamp(AlertRepeatMinutes, 0, 1440);
        WarnThreshold = Math.Clamp(WarnThreshold, 1, 100);
        CriticalThreshold = Math.Clamp(CriticalThreshold, 1, 100);
        if (WarnThreshold >= CriticalThreshold)
        {
            WarnThreshold = 75;
            CriticalThreshold = 90;
        }
        // A settings.json with "NotificationState": null deserializes to a null
        // dictionary. ThresholdTracker's initialState ?? new(...) fallback would
        // then allocate its own dictionary instead of aliasing this one, so
        // SaveSettings() writes null forever and every restart re-pops every
        // alert. Restore it here, before ThresholdTracker ever sees it.
        NotificationState ??= new();

        // See NotificationStateVersion's doc comment: a file written before
        // Warn existed reads back with the wrong AlertLevel meanings. The
        // dedupe state is only bookkeeping (worst case: one extra alert per
        // window, once), so it is simplest and safest to discard it outright
        // rather than attempt an ambiguous remap.
        if (NotificationStateVersion < CurrentNotificationStateVersion)
        {
            NotificationState.Clear();
            NotificationStateVersion = CurrentNotificationStateVersion;
            NotificationStateJustMigrated = true;
        }
    }
}
