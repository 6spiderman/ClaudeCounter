namespace ClaudeBackup;

/// <summary>Overall backup health - see the design spec's "Health states" table.</summary>
public enum BackupHealthState
{
    /// <summary>No destination enabled. Produces silence - never nag a user who does not use backup.</summary>
    NotConfigured,

    /// <summary>
    /// Configured, but no enabled destination has ever recorded an attempt.
    /// Fix round 1 (Important 4): this is a WARNING state, not a quiet one -
    /// treat it exactly like Failed/Stale for surfacing purposes (see <see
    /// cref="BackupHealthStateExtensions.WarrantsAttention"/>). A scheduled
    /// task that never registered, or a machine that is always asleep at the
    /// scheduled time, produces exactly this state forever: it is the
    /// opening scenario the whole design spec exists to fix, so it must not
    /// end up invisible again by being left out of S11b's badge/popup
    /// trigger.
    /// </summary>
    NeverRun,

    /// <summary>Every enabled destination succeeded within the staleness threshold.</summary>
    Healthy,

    /// <summary>The last attempt for an enabled destination failed. Outranks Stale when both apply.</summary>
    Failed,

    /// <summary>
    /// No failure, but the newest success for an enabled destination (or the
    /// fact that one has never run at all) is older than the threshold - or
    /// there simply is no success yet for a destination sitting alongside an
    /// already-healthy one. See BackupHealth.Evaluate's remarks for exactly
    /// when a per-destination NeverRun rolls up into overall Stale rather
    /// than overall NeverRun.
    /// </summary>
    Stale,
}

/// <summary>
/// Fix round 1 (Important 4): tells a caller (S11b's tray) which overall
/// states warrant a badge/tooltip/popup, so that decision lives in one place
/// instead of being re-derived (and potentially mis-derived, e.g. forgetting
/// NeverRun) at every surfacing call site.
/// </summary>
public static class BackupHealthStateExtensions
{
    /// <summary>
    /// True for every state that should surface something to the user -
    /// everything except <see cref="BackupHealthState.Healthy"/> and <see
    /// cref="BackupHealthState.NotConfigured"/>, the two "nothing to see"
    /// states. <see cref="BackupHealthState.NeverRun"/> is deliberately
    /// included - see that state's own doc comment for why leaving it out
    /// would silently recreate the exact failure mode this feature exists to
    /// fix.
    /// </summary>
    public static bool WarrantsAttention(this BackupHealthState state) => state switch
    {
        BackupHealthState.NeverRun or BackupHealthState.Failed or BackupHealthState.Stale => true,
        BackupHealthState.Healthy or BackupHealthState.NotConfigured => false,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}

/// <summary>One enabled destination's own contribution to the overall <see cref="BackupHealthState"/>.</summary>
public enum DestinationHealthState { NeverRun, Healthy, Failed, Stale }

/// <summary>
/// Per-destination detail behind a <see cref="BackupHealthResult"/>, so a
/// caller (the tray, in a later task) can say which destination is broken
/// and when it last worked, not just the aggregate state.
/// </summary>
public sealed record DestinationHealth(
    string Name,
    DestinationHealthState State,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastAttemptUtc,
    string LastMessage);

public sealed record BackupHealthResult(BackupHealthState State, IReadOnlyList<DestinationHealth> Destinations);

/// <summary>
/// Pure function from (status, config, now, staleAfterDays) to a
/// <see cref="BackupHealthResult"/> - see the design spec's "Health states"
/// table. No I/O, no Form, no process: the caller supplies an already-loaded
/// <see cref="BackupStatus"/> and <see cref="BackupConfig"/>, and the
/// staleness threshold as its own parameter (mirroring
/// BackupConfig.ScheduleConfig.BackupStaleAfterDays, but not read from config
/// internally - the spec calls for four independent inputs, not three).
/// </summary>
public static class BackupHealth
{
    public static BackupHealthResult Evaluate(BackupStatus status, BackupConfig config, DateTimeOffset now, int staleAfterDays)
    {
        // S17a: iterates BackupConfig.Destinations (the real N-destination
        // list) instead of two hardcoded Github.Enabled/Drive.Enabled checks
        // - the roll-up logic below this is unchanged and already
        // collection-based. Looking a destination's id up in
        // status.Destinations (rather than reading status.Github/status.Drive
        // directly) is also what makes an ORPHANED status entry - an id with
        // no matching destination here - naturally invisible to health: this
        // loop only ever enumerates ids that exist in config, so an orphan is
        // never looked at, let alone evaluated. status.For(id) supplies a
        // fresh (NeverRun-reading) DestinationStatus for a destination with
        // no status entry yet, exactly like a missing status file already did
        // for the old two-destination shape.
        var destinations = new List<DestinationHealth>();
        foreach (var destination in config.Destinations.Where(d => d.Enabled))
            destinations.Add(EvaluateDestination(destination.Name, status.For(destination.Id), now, staleAfterDays));

        // No destination enabled: show nothing at all - never nag a user who
        // does not use backup. Checked before anything else so a NeverRun/
        // Stale/Failed destination left over from a config the user has
        // since disabled cannot leak through.
        if (destinations.Count == 0)
            return new BackupHealthResult(BackupHealthState.NotConfigured, destinations);

        // Failed outranks Stale (and NeverRun) when both apply - a real
        // failure is always the more actionable signal.
        if (destinations.Any(d => d.State == DestinationHealthState.Failed))
            return new BackupHealthResult(BackupHealthState.Failed, destinations);

        // Every enabled destination is unproven: this is a fresh setup, not
        // staleness - NeverRun, not Stale.
        if (destinations.All(d => d.State == DestinationHealthState.NeverRun))
            return new BackupHealthResult(BackupHealthState.NeverRun, destinations);

        // A mix of NeverRun and Healthy (or an explicit Stale) among enabled
        // destinations: not every destination is currently proven healthy,
        // but nothing has outright failed either - that is what Stale means
        // at the aggregate level. A destination that has quietly never run
        // while a sibling destination has been happily succeeding is exactly
        // the kind of thing staleness exists to surface, so it rolls up into
        // Stale here rather than being invisible.
        if (destinations.Any(d => d.State is DestinationHealthState.Stale or DestinationHealthState.NeverRun))
            return new BackupHealthResult(BackupHealthState.Stale, destinations);

        return new BackupHealthResult(BackupHealthState.Healthy, destinations);
    }

    /// <summary>
    /// S14: the single Drive destination now has two transports (see the
    /// design doc's "Key structural decision" - a transport switch, not a
    /// third destination), so its health/failure display name is
    /// transport-aware: "Sync folder" for the no-auth folder transport,
    /// "Google Drive (rclone)" for the original one. The original bare
    /// "Google Drive" is retired for BOTH transports, not just replaced for
    /// the new one - a sync-folder user might just as well be pointed at
    /// OneDrive, Dropbox, or a NAS share, where "Google Drive" would be
    /// actively wrong, and disambiguating the rclone case too keeps the two
    /// names symmetric instead of one being oddly specific and the other not.
    ///
    /// Public so other transport-aware call sites (RestoreDialog's "restore
    /// from" list, most notably - it would be actively wrong to offer to
    /// restore from "Google Drive" when the configured transport is a NAS
    /// share) reuse this exact mapping instead of growing a second,
    /// possibly-drifting copy of the same switch.
    /// </summary>
    public static string DriveDisplayName(DriveTarget target) => target.Transport switch
    {
        DriveTransport.SyncFolder => "Sync folder",
        _ => "Google Drive (rclone)",
    };

    private static DestinationHealth EvaluateDestination(string name, DestinationStatus status, DateTimeOffset now, int staleAfterDays)
    {
        if (status.LastAttemptUtc is null)
            return new DestinationHealth(name, DestinationHealthState.NeverRun, null, null, "");

        if (status.LastOutcome == BackupOutcome.Failed)
            return new DestinationHealth(name, DestinationHealthState.Failed, status.LastSuccessUtc, status.LastAttemptUtc, status.LastMessage);

        if (status.LastSuccessUtc is not { } lastSuccess)
        {
            // Defensive only: DestinationStatus.WithAttempt always sets
            // LastSuccessUtc together with a Success outcome, so this should
            // be unreachable from a status file this codebase wrote. Treat a
            // hand-crafted or corrupted file with a Success outcome but no
            // success timestamp as NeverRun rather than throwing or claiming
            // Healthy on no evidence.
            return new DestinationHealth(name, DestinationHealthState.NeverRun, null, status.LastAttemptUtc, status.LastMessage);
        }

        // 0 means never warn about staleness - the destination is Healthy
        // regardless of how old the last success is. Otherwise: within the
        // threshold (inclusive - exactly at the threshold is still Healthy,
        // see the design spec's staleness-boundary test requirement) is
        // Healthy; anything older is Stale.
        if (staleAfterDays > 0 && now - lastSuccess > TimeSpan.FromDays(staleAfterDays))
            return new DestinationHealth(name, DestinationHealthState.Stale, lastSuccess, status.LastAttemptUtc, status.LastMessage);

        return new DestinationHealth(name, DestinationHealthState.Healthy, lastSuccess, status.LastAttemptUtc, status.LastMessage);
    }
}
