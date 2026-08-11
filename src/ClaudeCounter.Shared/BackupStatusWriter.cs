using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Writes backup-status.json from the worker after every terminating run.
/// Enforces rule 3 from the design spec ("a failed status write never fails
/// the run"): every method here swallows its own exceptions after logging,
/// so a status-file problem (a locked file, a denied ACL, a full disk) can
/// never turn a real backup success into a reported failure, and can never
/// crash the worker in a way that would prevent it returning its own,
/// already-decided exit code.
/// </summary>
public static class BackupStatusWriter
{
    /// <summary>
    /// Records an ordinary run's result: loads whatever is on disk (or
    /// starts fresh if missing/corrupt - see <see cref="BackupStatus.Load"/>),
    /// merges in <paramref name="result"/> via <see cref="BackupStatus.WithRun"/>
    /// (which is what actually enforces "last success is never overwritten by
    /// a failure"), and writes the merged status back to <paramref name="path"/>.
    /// </summary>
    public static void Record(string path, BackupRunResult result, DateTimeOffset now)
    {
        try
        {
            var previous = BackupStatus.Load(path);
            var next = previous.WithRun(result, now);
            next.Save(path);
        }
        catch (Exception ex)
        {
            // Deliberately not scrubbed further - ex.Message here is a
            // filesystem-layer failure (IOException/UnauthorizedAccessException
            // from Save's own File.Move/CreateDirectory), not backend output,
            // so it is not expected to carry a credential the way a git/rclone
            // failure message can.
            Log.Warn($"BackupStatusWriter: failed to write backup status: {ex.Message}");
        }
    }

    /// <summary>
    /// Records a run that failed before BackupRunner ever produced a proper
    /// <see cref="BackupRunResult"/> - Program.RunWorker's outer catch-all,
    /// covering an exception thrown by BackupConfig.Load itself or by
    /// anything else escaping BackupRunner.RunDetailed. Marks every
    /// destination this method can confirm was enabled as a failed attempt
    /// with <paramref name="scrubbedMessage"/>, so an unhandled exception is
    /// exactly as visible to BackupHealth as an ordinary backend failure, not
    /// a silent gap in the status file.
    ///
    /// <paramref name="bestEffortConfig"/> is whatever config the caller
    /// already had in hand - possibly null, when the exception came from
    /// BackupConfig.Load itself before any config existed. Fix round 1
    /// (Important 1): a null config here used to mean "treat both
    /// destinations as NotAttempted", which carries the PREVIOUS run's
    /// outcome forward unchanged via DestinationStatus.WithAttempt - so a
    /// destination that had last succeeded, and has now been failing on
    /// every subsequent run (e.g. an ACL-locked backup.json throwing
    /// UnauthorizedAccessException before BackupConfig.Load could even
    /// return, every single run), would keep reading Success/Healthy
    /// forever. That is precisely the bug this whole feature exists to
    /// prevent. When <paramref name="bestEffortConfig"/> cannot say whether a
    /// destination is enabled, this now falls back to that destination's own
    /// persisted <see cref="DestinationStatus.WasEnabled"/> - written by
    /// every prior run that DID have a config - rather than guessing
    /// "unknown means not enabled". Only a destination with no config AND no
    /// prior history at all (a status file that has never seen that
    /// destination enabled) stays <see cref="DestinationAttempt.NotAttempted"/>.
    /// </summary>
    public static void RecordUnhandledException(
        string path, BackupConfig? bestEffortConfig, string scrubbedMessage, DateTimeOffset now)
    {
        var previous = BackupStatus.Load(path);
        var githubEnabled = bestEffortConfig?.Github.Enabled ?? previous.Github.WasEnabled;
        var driveEnabled = bestEffortConfig?.Drive.Enabled ?? previous.Drive.WasEnabled;

        var github = githubEnabled ? DestinationAttempt.Failed(scrubbedMessage) : DestinationAttempt.NotAttempted(false);
        var drive = driveEnabled ? DestinationAttempt.Failed(scrubbedMessage) : DestinationAttempt.NotAttempted(false);

        Record(path, new BackupRunResult(2, github, drive), now);
    }
}
