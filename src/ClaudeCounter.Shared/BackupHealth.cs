namespace ClaudeBackup;

/// <summary>Overall backup health - see the design spec's "Health states" table.</summary>
public enum BackupHealthState
{
    /// <summary>No destination enabled. Produces silence - never nag a user who does not use backup.</summary>
    NotConfigured,

    /// <summary>Configured, but no enabled destination has ever recorded an attempt.</summary>
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
        var destinations = new List<DestinationHealth>();
        if (config.Github.Enabled)
            destinations.Add(EvaluateDestination("GitHub", status.Github, now, staleAfterDays));
        if (config.Drive.Enabled)
            destinations.Add(EvaluateDestination("Google Drive", status.Drive, now, staleAfterDays));

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
