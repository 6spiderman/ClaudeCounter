using ClaudeCounter.Core;

// InternalsVisibleTo("ClaudeCounter.Tests") is declared once, assembly-wide,
// in GitBackend.cs.

namespace ClaudeBackup;

/// <summary>
/// Orchestrates one backup run: validate each enabled destination's own
/// configuration (remote URL/branch for GitHub, rclone remote for Drive),
/// select files PER ENABLED-AND-CONFIGURED DESTINATION (GitHub and Drive
/// each own an independent include/exclude selection - see
/// GitTarget.Include / DriveTarget.Include), log what the secret denylist
/// withheld per destination, re-assert the denylist as a fail-closed
/// backstop against EACH destination's selection independently, then run
/// each such destination's backend independently so one destination being
/// misconfigured, having nothing to upload, or its backend failing (or
/// throwing) does not prevent a sibling destination that IS configured and
/// has files from running.
///
/// Exit codes (meaningful to Task Scheduler, which records them):
///   0 - success: every enabled, configured destination that had anything
///       to upload succeeded.
///   1 - configuration/selection error: no destination enabled, a denylist
///       offender detected in either destination's selection (see the
///       DELIBERATE comment at that call site - this one IS a whole-run
///       abort), or NO enabled destination ended up both configured and
///       having something selected to upload. An individual destination
///       being misconfigured (e.g. GitHub enabled with a blank RemoteUrl)
///       or selecting nothing is NOT this case by itself - it is logged and
///       that destination is skipped, but a sibling destination that IS
///       configured and has files still runs and can still bring the run
///       to exit 0. See the per-destination handling below.
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
    /// method), but whether THIS method calls it, before either backend
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
    /// caller into forgetting which target's lists it was supposed to read.
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
    /// visibility). Every return path below sets both destinations'
    /// attempts, even the early-abort ones - see each branch's own comment
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
        var githubAttempt = DestinationAttempt.NotAttempted(config.Github.Enabled);
        var driveAttempt = DestinationAttempt.NotAttempted(config.Drive.Enabled);

        if (!config.Github.Enabled && !config.Drive.Enabled)
        {
            Log.Warn("BackupRunner: no backup destinations enabled.");
            return new BackupRunResult(1, githubAttempt, driveAttempt);
        }

        // Fix round 2: an enabled destination with no remote configured is
        // THAT DESTINATION'S OWN configuration mistake, not a backend
        // failure and not grounds to abort a sibling destination that IS
        // fully configured - GitHub left half set up (e.g. a blank
        // RemoteUrl) must not silently stop a properly configured Drive
        // from backing up anything at all, and vice versa. This mirrors the
        // per-destination "nothing selected" philosophy further down: a
        // problem scoped to one destination stays scoped to it. Each guard
        // below only clears that destination's own "configured" flag (and
        // logs which destination and why) rather than returning 1
        // immediately - GitBackend/RcloneBackend would eventually report a
        // failure for a bad remote spec too (an empty git remote or rclone
        // remote), but that would surface as exit 2 ("a backend failed")
        // when the truth is exit 1-shaped ("fix your config"), so this
        // still runs before either backend ever sees the bad config -
        // it just no longer takes the other destination down with it.
        var githubConfigured = true;
        if (config.Github.Enabled && string.IsNullOrWhiteSpace(config.Github.RemoteUrl))
        {
            Log.Warn("BackupRunner: GitHub backup is enabled but RemoteUrl is not configured; skipping this destination.");
            githubConfigured = false;
            githubAttempt = DestinationAttempt.Failed("GitHub backup is enabled but the remote URL is not configured.");
        }
        // Same reasoning as the RemoteUrl guard above: SettingsForm trims the
        // branch textbox, so clearing it and saving persists "". Without this
        // check that empty string reaches 'git init -b ""' inside GitBackend,
        // which fails as a backend error (exit 2) instead of the
        // configuration error it actually is.
        if (config.Github.Enabled && githubConfigured && string.IsNullOrWhiteSpace(config.Github.Branch))
        {
            Log.Warn("BackupRunner: GitHub backup is enabled but Branch is not configured; skipping this destination.");
            githubConfigured = false;
            githubAttempt = DestinationAttempt.Failed("GitHub backup is enabled but the branch is not configured.");
        }

        var driveConfigured = true;
        if (config.Drive.Enabled && string.IsNullOrWhiteSpace(config.Drive.RcloneRemote))
        {
            Log.Warn("BackupRunner: Google Drive backup is enabled but RcloneRemote is not configured; skipping this destination.");
            driveConfigured = false;
            driveAttempt = DestinationAttempt.Failed("Google Drive backup is enabled but the rclone remote is not configured.");
        }

        // "Active" = enabled AND configured. Everything from here on - the
        // secret-denylist backstop, "nothing selected", and which backend
        // gets a chance to run - is gated on this, not on Enabled alone, so
        // a misconfigured destination behaves exactly like a disabled one
        // for the rest of the run: it simply is not there.
        var githubActive = config.Github.Enabled && githubConfigured;
        var driveActive = config.Drive.Enabled && driveConfigured;

        // Each active destination is selected against its OWN Include/
        // Exclude and independently re-checked against the secret denylist
        // below - collapsing this back to a single selection/check (as a
        // single shared Include/Exclude used to allow) would silently stop
        // covering whichever destination's selection was not the one
        // checked.
        //
        // DELIBERATE: TrySelect returning false here (an offender found)
        // aborts the WHOLE run (return 1) immediately, before the other
        // destination is even selected, let alone either backend runs - it
        // is NOT scoped to just the offending destination. This is
        // intentional and must stay this way: FileSelector already strips
        // anything SecretDenylist flags, so Offenders() firing at all means
        // that invariant was somehow violated - something is genuinely
        // broken, not merely misconfigured. The conservative reaction to an
        // assumption breaking is to upload nothing anywhere, not to proceed
        // with whichever destination happens to look clean via a selection
        // pipeline that just proved it cannot be trusted. Do not "fix" this
        // into per-destination scoping - this is unlike the config guards
        // above and the "nothing selected" handling below, both of which
        // ARE deliberately scoped per destination.
        IReadOnlyList<string> githubFiles = Array.Empty<string>();
        if (githubActive)
        {
            if (!TrySelect(select, "GitHub", config.SourceRoot, config.Github.Include, config.Github.Exclude, out githubFiles))
            {
                // Whole-run abort (see the DELIBERATE comment above): neither
                // backend runs, so every ACTIVE destination - not just
                // GitHub, whose selection actually tripped the offender
                // check - failed to back up anything this run. Drive's own
                // selection has not even happened yet at this point, but it
                // never will either, so it is just as much a failed attempt.
                const string msg = "Backup aborted: a secret-shaped file was detected in the GitHub selection.";
                return new BackupRunResult(1,
                    DestinationAttempt.Failed(msg),
                    driveActive ? DestinationAttempt.Failed(msg) : driveAttempt);
            }
        }

        IReadOnlyList<string> driveFiles = Array.Empty<string>();
        if (driveActive)
        {
            if (!TrySelect(select, "Google Drive", config.SourceRoot, config.Drive.Include, config.Drive.Exclude, out driveFiles))
            {
                // Mirror of the GitHub case above: GitHub's own selection
                // already succeeded by this point (or GitHub was never
                // active), but the whole run still aborts before either
                // backend runs, so an active GitHub is just as much a failed
                // attempt as Drive is.
                const string msg = "Backup aborted: a secret-shaped file was detected in the Google Drive selection.";
                return new BackupRunResult(1,
                    githubActive ? DestinationAttempt.Failed(msg) : githubAttempt,
                    DestinationAttempt.Failed(msg));
            }
        }

        // "Nothing selected" is only a whole-run failure when NO active
        // (enabled AND configured) destination has anything to upload. One
        // destination with an empty selection - or one that turned out to
        // be misconfigured above - while another is active and has files is
        // that destination's own problem (already logged), not grounds to
        // fail a run that can otherwise proceed.
        var githubHasFiles = githubActive && githubFiles.Count > 0;
        var driveHasFiles = driveActive && driveFiles.Count > 0;
        if (!githubHasFiles && !driveHasFiles)
        {
            Log.Warn("BackupRunner: nothing selected to back up on any enabled, configured destination.");
            const string msg = "Nothing selected to back up.";
            return new BackupRunResult(1,
                githubActive ? DestinationAttempt.Failed(msg) : githubAttempt,
                driveActive ? DestinationAttempt.Failed(msg) : driveAttempt);
        }

        var anyFailed = false;

        if (githubActive)
        {
            if (githubFiles.Count == 0)
            {
                Log.Warn("BackupRunner: nothing selected for GitHub; skipping this destination.");
                githubAttempt = DestinationAttempt.Failed("Nothing selected to back up for GitHub.");
            }
            else
            {
                var ok = RunBackend("GitHub",
                    () => new GitBackend(runner, stagingDir).Run(config.SourceRoot, githubFiles, config.Github),
                    out var message);
                anyFailed |= !ok;
                githubAttempt = ok ? DestinationAttempt.Ok() : DestinationAttempt.Failed(message);
            }
        }

        // Deliberately not an "else if" and not short-circuited by the
        // GitHub result above: each active backend must get a chance to run
        // regardless of whether the other one failed, threw, was
        // misconfigured, or had nothing selected.
        if (driveActive)
        {
            if (driveFiles.Count == 0)
            {
                Log.Warn("BackupRunner: nothing selected for Google Drive; skipping this destination.");
                driveAttempt = DestinationAttempt.Failed("Nothing selected to back up for Google Drive.");
            }
            else
            {
                var ok = RunBackend("Google Drive",
                    () => new RcloneBackend(runner, tempDir).Run(config.SourceRoot, driveFiles, config.Drive),
                    out var message);
                anyFailed |= !ok;
                driveAttempt = ok ? DestinationAttempt.Ok() : DestinationAttempt.Failed(message);
            }
        }

        return new BackupRunResult(anyFailed ? 2 : 0, githubAttempt, driveAttempt);
    }

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
    /// BackupRunner.Run, skip whatever the other enabled backend would have
    /// done, and crash the whole process instead of returning exit code 2.
    /// A tray-supplied IProcessRunner (landing in a later task) is exactly
    /// the kind of implementation this needs to be defensive against, since
    /// it is not one BackupRunner controls or can assume is well-behaved.
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
