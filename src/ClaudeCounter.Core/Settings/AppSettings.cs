using System.Text.Json.Serialization;
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

    public void Normalize()
    {
        if (!IntervalPresets.Contains(PollIntervalMinutes))
            PollIntervalMinutes = 5;
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
