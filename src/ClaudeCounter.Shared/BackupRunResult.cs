namespace ClaudeBackup;

/// <summary>
/// One destination's outcome for a single backup run - the shape
/// BackupRunner.RunDetailed (ClaudeBackup.csproj) reports and
/// BackupStatusWriter/DestinationStatus.WithAttempt consume to update
/// backup-status.json. Lives in ClaudeCounter.Shared (not ClaudeBackup.csproj)
/// alongside BackupStatus, for the same reason: the status-writing types must
/// not require the tray to reference the worker project.
/// </summary>
/// <param name="Enabled">Whether this destination is enabled in the config this run used.</param>
/// <param name="Attempted">
/// Whether this run actually has something to say about this destination.
/// False means "carry the previous status forward unchanged" - used both for
/// a destination that is simply disabled, and for one that never got a
/// chance to run because the whole run aborted before reaching it in a way
/// that did not implicate this destination (see BackupRunner.RunDetailed's
/// per-branch comments for exactly when each destination is marked attempted).
/// </param>
/// <param name="Success">Meaningful only when <see cref="Attempted"/> is true.</param>
/// <param name="Message">
/// Free text describing the outcome - not yet scrubbed (DestinationStatus.WithAttempt
/// scrubs it before storing), so callers may pass raw backend/exception text.
/// </param>
public sealed record DestinationAttempt(bool Enabled, bool Attempted, bool Success, string Message)
{
    /// <summary>A destination this run never touched - status for it is carried forward as-is.</summary>
    public static DestinationAttempt NotAttempted(bool enabled) => new(enabled, false, false, "");

    /// <summary>A successful attempt.</summary>
    public static DestinationAttempt Ok(string message = "Backup completed successfully.") => new(true, true, true, message);

    /// <summary>A failed attempt - always Enabled: true, since only an enabled destination can fail an attempt.</summary>
    public static DestinationAttempt Failed(string message) => new(true, true, false, message);
}

/// <summary>
/// Overall outcome of one BackupRunner.RunDetailed call - see BackupRunner's
/// exit-code contract. S17a: generalized from exactly two positional
/// destinations (Github, Drive) to an id-keyed dictionary over N, so
/// BackupStatusWriter can merge a run's outcome into backup-status.json's own
/// id-keyed shape (see BackupStatus.Destinations) without hardcoding a
/// destination count anywhere. <see cref="Attempts"/> is keyed by the same
/// <see cref="BackupDestination.Id"/> values BackupConfig.Destinations and
/// BackupStatus.Destinations use.
/// </summary>
public sealed record BackupRunResult(int ExitCode, IReadOnlyDictionary<string, DestinationAttempt> Attempts)
{
    /// <summary>
    /// Back-compat constructor for BackupRunner.RunDetailed, which is out of
    /// scope for this task (S17b migrates it to build <see cref="Attempts"/>
    /// directly over N destinations instead of two positional ones). Keys
    /// the two attempts under the same well-known "github"/"drive" ids
    /// BackupConfig's v1-&gt;v2 migration assigns to the same two
    /// destinations, so a result built this way still merges correctly into
    /// the id-keyed <see cref="BackupStatus"/>.
    /// </summary>
    public BackupRunResult(int exitCode, DestinationAttempt github, DestinationAttempt drive)
        : this(exitCode, new Dictionary<string, DestinationAttempt> { ["github"] = github, ["drive"] = drive })
    {
    }

    /// <summary>
    /// Convenience read accessor for the well-known "github" id - kept for
    /// BackupRunnerTests, which still asserts against the pre-S17a
    /// two-destination shape (BackupRunner itself is unchanged this task -
    /// see this record's own remarks). Falls back to NotAttempted(false)
    /// rather than throwing when "github" has no entry (e.g. a result built
    /// directly over an arbitrary id set that does not include it).
    /// </summary>
    public DestinationAttempt Github => Attempts.TryGetValue("github", out var attempt) ? attempt : DestinationAttempt.NotAttempted(false);

    /// <summary>Convenience read accessor for the well-known "drive" id - see <see cref="Github"/>'s doc comment.</summary>
    public DestinationAttempt Drive => Attempts.TryGetValue("drive", out var attempt) ? attempt : DestinationAttempt.NotAttempted(false);
}
