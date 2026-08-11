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

/// <summary>Overall outcome of one BackupRunner.RunDetailed call - see BackupRunner's exit-code contract.</summary>
public sealed record BackupRunResult(int ExitCode, DestinationAttempt Github, DestinationAttempt Drive);
