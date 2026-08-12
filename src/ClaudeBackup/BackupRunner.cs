using ClaudeCounter.Core;

// InternalsVisibleTo("ClaudeCounter.Tests") is declared once, assembly-wide,
// in GitBackend.cs.

namespace ClaudeBackup;

/// <summary>
/// Orchestrates one backup run: validate each enabled destination's own
/// configuration (remote URL/branch for GitHub, folder path for SyncFolder,
/// rclone remote for Rclone), select files PER ENABLED-AND-CONFIGURED
/// DESTINATION (every destination owns an independent include/exclude
/// selection - see BackupDestination.Include), log what the secret denylist
/// withheld per destination, re-assert the denylist as a fail-closed
/// backstop against EACH destination's selection independently, then run
/// each such destination's backend independently so one destination being
/// misconfigured, having nothing to upload, or its backend failing (or
/// throwing) does not prevent a sibling destination that IS configured and
/// has files from running.
///
/// S17b: generalized from exactly two hardcoded destinations (Github, Drive)
/// to a loop over <see cref="BackupConfig.Destinations"/> - see this class's
/// private helpers below for how each of the old two-destination behaviours
/// (independence, the whole-run offender abort, per-kind config validation,
/// per-destination scratch isolation) carries over to N.
///
/// Exit codes (meaningful to Task Scheduler, which records them):
///   0 - success: every enabled, configured destination that had anything
///       to upload succeeded.
///   1 - configuration/selection error: no destination enabled, a denylist
///       offender detected in any destination's selection (see the
///       DELIBERATE comment at that call site - this one IS a whole-run
///       abort), or NO enabled destination ended up both configured and
///       having something selected to upload. An individual destination
///       being misconfigured (e.g. a GitHub destination enabled with a
///       blank RemoteUrl) or selecting nothing is NOT this case by itself -
///       it is logged and that destination is skipped, but a sibling
///       destination that IS configured and has files still runs and can
///       still bring the run to exit 0. See the per-destination handling
///       below.
///   2 - at least one enabled, configured destination that had something to
///       upload actually failed to upload it.
/// </summary>
public static class BackupRunner
{
    public static int Run(BackupConfig config, IProcessRunner runner, string stagingDir, string tempDir)
        => RunDetailed(config, runner, stagingDir, tempDir, SelectFiles).ExitCode;

    /// <summary>
    /// Production entry point for callers (Program.RunWorker) that need more
    /// than the bare exit code - per-destination outcome, for
    /// BackupStatusWriter to record. Delegates to the internal 5-arg overload
    /// with the real file-selection step, exactly like the public <see
    /// cref="Run(BackupConfig,IProcessRunner,string,string)"/> above.
    /// </summary>
    internal static BackupRunResult RunDetailed(BackupConfig config, IProcessRunner runner, string stagingDir, string tempDir)
        => RunDetailed(config, runner, stagingDir, tempDir, SelectFiles);

    /// <summary>
    /// Internal overload that takes the file-selection step as a delegate.
    /// This is the actual risk surface worth testing: not whether
    /// SecretDenylist.Offenders(files) reports offenders correctly (that is
    /// SecretDenylistTests' job, on an already-public, already-tested
    /// method), but whether THIS method calls it, before any backend
    /// runs, for EACH destination's selection independently, and returns
    /// the right exit code when it does. FileSelector itself never hands
    /// back a secret (it filters via the same SecretDenylist before Run's
    /// public overload ever sees the list), so there is no way to prove
    /// that wiring by calling the public overload with real files - a fake
    /// selector is the only way to put a known-bad list in front of the
    /// check without needing FileSelector to first fail at its own job.
    ///
    /// The delegate mirrors FileSelector.Select's own (root, include,
    /// exclude) shape rather than taking the whole BackupConfig, precisely
    /// so it can be called once per destination with that destination's own
    /// Include/Exclude - a delegate keyed on the whole config would tempt a
    /// caller into forgetting which destination's lists it was supposed to
    /// read.
    /// </summary>
    internal static int Run(
        BackupConfig config,
        IProcessRunner runner,
        string stagingDir,
        string tempDir,
        Func<string, IEnumerable<string>, IEnumerable<string>, (IReadOnlyList<string> Files, List<string> Withheld)> select)
        => RunDetailed(config, runner, stagingDir, tempDir, select).ExitCode;

    /// <summary>
    /// The real orchestration logic (see this class's own doc comment),
    /// returning a <see cref="BackupRunResult"/> - the exit code plus a
    /// per-destination <see cref="DestinationAttempt"/> for
    /// BackupStatusWriter/Program.RunWorker to record (S11a: backup health
    /// visibility). Every return path below sets every destination's
    /// attempt, even the early-abort ones - see each branch's own comment
    /// for exactly which destinations it marks Attempted and why. A
    /// destination that is not currently enabled, or that this particular
    /// run never got far enough to say anything new about, is reported via
    /// <see cref="DestinationAttempt.NotAttempted"/> so BackupStatusWriter
    /// carries its previous status forward unchanged rather than guessing.
    /// </summary>
    internal static BackupRunResult RunDetailed(
        BackupConfig config,
        IProcessRunner runner,
        string stagingDir,
        string tempDir,
        Func<string, IEnumerable<string>, IEnumerable<string>, (IReadOnlyList<string> Files, List<string> Withheld)> select)
    {
        // Not-yet-attempted defaults - overwritten below wherever this run
        // actually has something new to say about a destination. Enabled is
        // recorded even when nothing else changes, so BackupStatus.WasEnabled
        // stays current for a destination the user has since turned off.
        var states = config.Destinations.Select(d => new DestinationRunState(d)).ToList();

        if (!states.Any(s => s.Destination.Enabled))
        {
            Log.Warn("BackupRunner: no backup destinations enabled.");
            return new BackupRunResult(1, ToAttempts(states));
        }

        // Fix round 2 (generalized): an enabled destination with an invalid
        // configuration is THAT DESTINATION'S OWN configuration mistake, not
        // a backend failure and not grounds to abort a sibling destination
        // that IS fully configured - one destination left half set up (e.g.
        // a blank RemoteUrl) must not silently stop a properly configured
        // sibling from backing up anything at all. This mirrors the
        // per-destination "nothing selected" philosophy further down: a
        // problem scoped to one destination stays scoped to it. Each
        // destination below only clears its own Configured flag (and logs
        // which destination and why) rather than returning 1 immediately -
        // the corresponding backend would eventually report a failure for a
        // bad remote spec too, but that would surface as exit 2 ("a backend
        // failed") when the truth is exit 1-shaped ("fix your config"), so
        // this still runs before any backend ever sees the bad config - it
        // just no longer takes a sibling destination down with it.
        foreach (var s in states)
        {
            if (!s.Destination.Enabled)
                continue; // stays Configured=false / Active=false; Attempt stays NotAttempted(false)

            s.Configured = ValidateConfigured(s.Destination, out var failureMessage);
            if (!s.Configured)
                s.Attempt = DestinationAttempt.Failed(failureMessage);
        }

        // Each active destination is selected against its OWN Include/
        // Exclude and independently re-checked against the secret denylist
        // below - collapsing this back to a single selection/check would
        // silently stop covering whichever destination's selection was not
        // the one checked.
        //
        // DELIBERATE: an offender found here aborts the WHOLE run (return 1)
        // immediately, before any further destination is even selected, let
        // alone any backend runs - it is NOT scoped to just the offending
        // destination. This is intentional and must stay this way:
        // FileSelector already strips anything SecretDenylist flags, so
        // Offenders() firing at all means that invariant was somehow
        // violated - something is genuinely broken, not merely
        // misconfigured. The conservative reaction to an assumption breaking
        // is to upload nothing anywhere, not to proceed with whichever
        // destination happens to look clean via a selection pipeline that
        // just proved it cannot be trusted. Do not "fix" this into
        // per-destination scoping - this is unlike the config guards above
        // and the "nothing selected" handling below, both of which ARE
        // deliberately scoped per destination.
        string? offenderMessage = null;
        foreach (var s in states.Where(s => s.Active))
        {
            if (!TrySelect(select, s.Destination.Name, config.SourceRoot, s.Destination.Include, s.Destination.Exclude, out var files))
            {
                offenderMessage = $"Backup aborted: a secret-shaped file was detected in the {s.Destination.Name} selection.";
                break; // stop selecting further destinations - the run is aborting regardless
            }
            s.Files = files;
        }

        if (offenderMessage is not null)
        {
            // Whole-run abort: no backend runs, so every ACTIVE destination -
            // not just the one whose selection actually tripped the offender
            // check - failed to back up anything this run. Any destination
            // whose selection had not even run yet (it never will now) is
            // just as much a failed attempt. Every destination that is not
            // Active (disabled, or misconfigured above) keeps whatever
            // attempt it already had - the offender abort does not implicate
            // a destination that was never going to run anyway.
            foreach (var s in states.Where(s => s.Active))
                s.Attempt = DestinationAttempt.Failed(offenderMessage);
            return new BackupRunResult(1, ToAttempts(states));
        }

        // "Nothing selected" is only a whole-run failure when NO active
        // (enabled AND configured) destination has anything to upload. One
        // destination with an empty selection - or one that turned out to
        // be misconfigured above - while another is active and has files is
        // that destination's own problem (already logged), not grounds to
        // fail a run that can otherwise proceed.
        if (!states.Any(s => s.HasFiles))
        {
            Log.Warn("BackupRunner: nothing selected to back up on any enabled, configured destination.");
            foreach (var s in states.Where(s => s.Active))
                s.Attempt = DestinationAttempt.Failed("Nothing selected to back up.");
            return new BackupRunResult(1, ToAttempts(states));
        }

        var anyFailed = false;

        // Every active destination gets a chance to run regardless of
        // whether a sibling failed, threw, was misconfigured, or had
        // nothing selected - see this class's own doc comment.
        foreach (var s in states.Where(s => s.Active))
        {
            if (s.Files.Count == 0)
            {
                Log.Warn($"BackupRunner: nothing selected for {s.Destination.Name}; skipping this destination.");
                s.Attempt = DestinationAttempt.Failed($"Nothing selected to back up for {s.Destination.Name}.");
                continue;
            }

            var ok = RunBackend(
                s.Destination.Name,
                () => RunBackendFor(s.Destination, runner, stagingDir, tempDir, config.SourceRoot, s.Files),
                out var message);
            anyFailed |= !ok;
            s.Attempt = ok ? DestinationAttempt.Ok() : DestinationAttempt.Failed(message);
        }

        return new BackupRunResult(anyFailed ? 2 : 0, ToAttempts(states));
    }

    /// <summary>
    /// Per-run, per-destination bookkeeping - replaces the old parallel
    /// githubAttempt/driveAttempt, xConfigured, xActive, xFiles, xHasFiles
    /// quintuple of local variables with one object per destination in <see
    /// cref="BackupConfig.Destinations"/>, so RunDetailed's logic reads as a
    /// loop instead of two copy-pasted blocks.
    /// </summary>
    private sealed class DestinationRunState
    {
        public DestinationRunState(BackupDestination destination)
        {
            Destination = destination;
            Attempt = DestinationAttempt.NotAttempted(destination.Enabled);
        }

        public BackupDestination Destination { get; }
        public DestinationAttempt Attempt { get; set; }
        public bool Configured { get; set; }

        /// <summary>"Active" = enabled AND configured. Everything past config validation - the secret-denylist backstop, "nothing selected", and which backend gets a chance to run - is gated on this, not on Enabled alone, so a misconfigured destination behaves exactly like a disabled one for the rest of the run: it simply is not there.</summary>
        public bool Active => Destination.Enabled && Configured;

        public IReadOnlyList<string> Files { get; set; } = Array.Empty<string>();
        public bool HasFiles => Active && Files.Count > 0;
    }

    private static Dictionary<string, DestinationAttempt> ToAttempts(List<DestinationRunState> states) =>
        states.ToDictionary(s => s.Destination.Id, s => s.Attempt);

    /// <summary>
    /// Per-kind config validation (generalized from the old separate
    /// GitHub-shaped and Drive-shaped inline blocks): GitHub needs a
    /// non-blank RemoteUrl and Branch; SyncFolder needs a non-blank, rooted
    /// FolderPath (the heavier checks - overlap with SourceRoot, actually
    /// creating the directory - happen inside SyncFolderBackend itself, at
    /// the point of write, exactly like RcloneBackend's own deeper checks
    /// are not duplicated up here either); Rclone needs a non-blank
    /// RcloneRemote. Failure messages name the destination by its own <see
    /// cref="BackupDestination.Name"/> rather than a generic per-kind label -
    /// with N destinations there can be several of the same kind, so "Google
    /// Drive backup is enabled but..." is no longer specific enough to tell
    /// the user which one.
    /// </summary>
    private static bool ValidateConfigured(BackupDestination destination, out string failureMessage)
    {
        switch (destination.Kind)
        {
            case DestinationKind.GitHub:
                if (string.IsNullOrWhiteSpace(destination.RemoteUrl))
                {
                    Log.Warn($"BackupRunner: {destination.Name} backup is enabled but RemoteUrl is not configured; skipping this destination.");
                    failureMessage = $"{destination.Name} backup is enabled but the remote URL is not configured.";
                    return false;
                }
                // SettingsForm trims the branch textbox, so clearing it and
                // saving persists "". Without this check that empty string
                // reaches 'git init -b ""' inside GitBackend, which fails as
                // a backend error (exit 2) instead of the configuration
                // error it actually is.
                if (string.IsNullOrWhiteSpace(destination.Branch))
                {
                    Log.Warn($"BackupRunner: {destination.Name} backup is enabled but Branch is not configured; skipping this destination.");
                    failureMessage = $"{destination.Name} backup is enabled but the branch is not configured.";
                    return false;
                }
                break;

            case DestinationKind.SyncFolder:
                if (string.IsNullOrWhiteSpace(destination.FolderPath) || !Path.IsPathRooted(destination.FolderPath))
                {
                    Log.Warn($"BackupRunner: {destination.Name} backup is enabled (sync folder transport) but FolderPath is not configured or not a full path; skipping this destination.");
                    failureMessage = $"{destination.Name} backup is enabled but the sync folder path is not configured.";
                    return false;
                }
                break;

            case DestinationKind.Rclone:
            default:
                if (string.IsNullOrWhiteSpace(destination.RcloneRemote))
                {
                    Log.Warn($"BackupRunner: {destination.Name} backup is enabled but RcloneRemote is not configured; skipping this destination.");
                    failureMessage = $"{destination.Name} backup is enabled but the rclone remote is not configured.";
                    return false;
                }
                break;
        }

        failureMessage = "";
        return true;
    }

    /// <summary>
    /// Dispatches to the right backend for <paramref name="destination"/>'s
    /// <see cref="BackupDestination.Kind"/>, adapting it into the GitTarget/
    /// DriveTarget shape each backend still takes (<see
    /// cref="BackupDestination.ToGitTarget"/>/<see
    /// cref="BackupDestination.ToDriveTarget"/> - those two backend/restore
    /// classes were out of this task's scope to change).
    ///
    /// PER-DESTINATION SCRATCH ISOLATION (S17b - a genuinely new problem two
    /// hardcoded destinations never had): the two scratch roots the caller
    /// supplies (<paramref name="stagingDir"/> for git, <paramref
    /// name="tempDir"/> for the two zip kinds) are shared across every
    /// destination of that shape. Two enabled GitHub destinations pointed at
    /// the SAME stagingDir would both drive GitBackend's `git init`/`git
    /// checkout -B`/MirrorFiles against literally the same working tree -
    /// the second destination to run would checkout ITS OWN branch into a
    /// tree MirrorFiles had just mirrored to the FIRST destination's
    /// selection, and (if the two remotes are unrelated repos) `git push`
    /// could easily push the wrong destination's history onto the wrong
    /// remote. Two zip-kind destinations sharing tempDir have the milder but
    /// still real problem that BackupArchiveWriter.SweepStaleZips (which
    /// runs at the START of every RcloneBackend/SyncFolderBackend.Run to
    /// clean up a previous crashed run's leftover zip) could race-delete a
    /// SIBLING destination's own just-written, not-yet-uploaded zip if both
    /// destinations' Run calls happen to interleave around that sweep.
    ///
    /// The fix: give every destination its own subdirectory of the shared
    /// root, named after its stable <see cref="BackupDestination.Id"/> -
    /// Path.Combine(stagingDir, destination.Id) for GitHub,
    /// Path.Combine(tempDir, destination.Id) for the two zip kinds. Id is
    /// documented as unique per destination and is always either a
    /// well-known lowercase word ("github", "drive") or a
    /// Guid.ToString("N") hex string (see BackupDestination.NewId) - never
    /// containing a path separator or any other character Path.Combine
    /// would choke on - so no further sanitization is needed. The
    /// subdirectory is pure scratch: it is recreated on demand by each
    /// backend, is not itself part of any persisted state (BackupStatus
    /// keys by Id, never by a filesystem path), and CleanupStagingDirectories
    /// callers (RestoreDialog's own equivalent) already delete the shared
    /// root recursively, which sweeps every destination's subdirectory in
    /// one call.
    /// </summary>
    private static BackendResult RunBackendFor(
        BackupDestination destination, IProcessRunner runner, string stagingDir, string tempDir,
        string sourceRoot, IReadOnlyList<string> files) => destination.Kind switch
    {
        DestinationKind.GitHub => new GitBackend(runner, Path.Combine(stagingDir, destination.Id))
            .Run(sourceRoot, files, destination.ToGitTarget()),
        DestinationKind.SyncFolder => new SyncFolderBackend(Path.Combine(tempDir, destination.Id))
            .Run(sourceRoot, files, destination.ToDriveTarget()),
        _ => new RcloneBackend(runner, Path.Combine(tempDir, destination.Id))
            .Run(sourceRoot, files, destination.ToDriveTarget()),
    };

    /// <summary>
    /// Runs the selection delegate for one destination, logs what the secret
    /// denylist withheld (named, and naming the destination it applies to -
    /// a bare count would not let a user tell which backup a withheld file
    /// was omitted from), and re-asserts the fail-closed
    /// SecretDenylist.Offenders backstop against the result. Returns false
    /// (having already logged why) when an offender is found, which the
    /// caller treats as an immediate whole-run abort - the same fail-closed
    /// behaviour the pre-per-destination single-selection check had, just
    /// now performed once per destination instead of once overall.
    /// </summary>
    private static bool TrySelect(
        Func<string, IEnumerable<string>, IEnumerable<string>, (IReadOnlyList<string> Files, List<string> Withheld)> select,
        string destinationName,
        string sourceRoot,
        IEnumerable<string> include,
        IEnumerable<string> exclude,
        out IReadOnlyList<string> files)
    {
        // Always use the 4-arg FileSelector.Select overload (via the real
        // SelectFiles delegate below): the 3-arg convenience overload
        // discards the withheld list, which makes silently omitting files
        // the user asked for the path of least resistance. A backup tool
        // that silently drops files is a correctness bug, so what the
        // denylist withheld is logged explicitly below.
        List<string> withheld;
        (files, withheld) = select(sourceRoot, include, exclude);

        if (withheld.Count > 0)
        {
            Log.Warn(
                $"BackupRunner: {withheld.Count} file(s) withheld from {destinationName} by the secret denylist: " +
                string.Join(", ", withheld));
        }

        // Fail-closed backstop: even though FileSelector already strips
        // anything SecretDenylist flags, this re-assertion is what actually
        // protects an upload if that invariant is ever broken by a future
        // change - to FileSelector, or to whatever selector a caller passes
        // to the internal overload above. GitBackend.MirrorFiles and
        // RcloneBackend's zip loop each repeat this same check right at the
        // point bytes get written, as a second, independent layer.
        var offenders = SecretDenylist.Offenders(files);
        if (offenders.Count > 0)
        {
            Log.Error(
                $"BackupRunner: aborting - secret file(s) in {destinationName} selection: {string.Join(", ", offenders)}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Real file-selection step used by the public <see cref="Run(BackupConfig,IProcessRunner,string,string)"/>
    /// overload: the 4-arg <see cref="FileSelector.Select"/> overload, wired
    /// through so the withheld list is never silently discarded.
    /// </summary>
    private static (IReadOnlyList<string> Files, List<string> Withheld) SelectFiles(
        string root, IEnumerable<string> include, IEnumerable<string> exclude)
    {
        var files = new FileSelector().Select(root, include, exclude, out var withheld);
        return (files, withheld);
    }

    /// <summary>
    /// Runs one backend and turns both an unsuccessful <see cref="BackendResult"/>
    /// and an exception thrown by the backend into "this backend failed,
    /// logged, move on" - making backend independence structural instead of
    /// incidental. Without this, an exception thrown by a backend (e.g.
    /// IProcessRunner.Exists throwing before GitBackend's own try block even
    /// starts - see GitBackend.Run and RcloneBackend.Run, both of which call
    /// Exists outside their try) would propagate straight out of
    /// BackupRunner.Run, skip whatever the other enabled destinations would
    /// have done, and crash the whole process instead of returning exit code
    /// 2. A tray-supplied IProcessRunner is exactly the kind of
    /// implementation this needs to be defensive against, since it is not
    /// one BackupRunner controls or can assume is well-behaved.
    ///
    /// <paramref name="message"/> carries the same already-scrubbed text that
    /// gets logged on failure ("" on success) - S11a: RunDetailed uses it to
    /// build this destination's DestinationAttempt for BackupStatusWriter,
    /// without a second, separate source of truth for what went wrong.
    /// </summary>
    private static bool RunBackend(string name, Func<BackendResult> run, out string message)
    {
        try
        {
            var r = run();
            if (!r.Ok)
            {
                var scrubbed = CredentialScrubber.Scrub(r.Message);
                Log.Error($"BackupRunner: {name} backend failed: {scrubbed}");
                message = scrubbed;
                return false;
            }
            message = "";
            return true;
        }
        catch (Exception ex)
        {
            var scrubbed = CredentialScrubber.Scrub(ex.Message);
            Log.Error($"BackupRunner: {name} backend threw: {scrubbed}");
            message = scrubbed;
            return false;
        }
    }
}
