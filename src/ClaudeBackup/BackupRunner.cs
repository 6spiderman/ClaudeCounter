using ClaudeCounter.Core;

// InternalsVisibleTo("ClaudeCounter.Tests") is declared once, assembly-wide,
// in GitBackend.cs.

namespace ClaudeBackup;

/// <summary>
/// Orchestrates one backup run: select files, log what the secret denylist
/// withheld, re-assert the denylist as a fail-closed backstop, then run each
/// enabled backend independently so one backend failing (or throwing) does
/// not prevent the other from running.
///
/// Exit codes (meaningful to Task Scheduler, which records them):
///   0 - success: every enabled backend succeeded.
///   1 - configuration/selection error: no destination enabled, an enabled
///       destination is not configured, nothing selected, or a denylist
///       offender detected in the selection.
///   2 - at least one enabled backend failed.
/// </summary>
public static class BackupRunner
{
    public static int Run(BackupConfig config, IProcessRunner runner, string stagingDir, string tempDir)
        => Run(config, runner, stagingDir, tempDir, SelectFiles);

    /// <summary>
    /// Internal overload that takes the file-selection step as a delegate.
    /// This is the actual risk surface worth testing: not whether
    /// SecretDenylist.Offenders(files) reports offenders correctly (that is
    /// SecretDenylistTests' job, on an already-public, already-tested
    /// method), but whether THIS method calls it, before either backend
    /// runs, and returns the right exit code when it does. FileSelector
    /// itself never hands back a secret (it filters via the same
    /// SecretDenylist before Run's public overload ever sees the list), so
    /// there is no way to prove that wiring by calling the public overload
    /// with real files - a fake selector is the only way to put a
    /// known-bad list in front of the check without needing FileSelector to
    /// first fail at its own job.
    /// </summary>
    internal static int Run(
        BackupConfig config,
        IProcessRunner runner,
        string stagingDir,
        string tempDir,
        Func<BackupConfig, (IReadOnlyList<string> Files, List<string> Withheld)> select)
    {
        if (!config.Github.Enabled && !config.Drive.Enabled)
        {
            Log.Warn("BackupRunner: no backup destinations enabled.");
            return 1;
        }

        // An enabled destination with no remote configured is a
        // configuration mistake, not a backend failure - GitBackend/
        // RcloneBackend would eventually report a failure for it (an empty
        // git remote or rclone remote spec), but that would surface as exit
        // 2 ("a backend failed") when the truth is exit 1 ("fix your
        // config"). Catch it before either backend ever runs.
        if (config.Github.Enabled && string.IsNullOrWhiteSpace(config.Github.RemoteUrl))
        {
            Log.Warn("BackupRunner: GitHub backup is enabled but RemoteUrl is not configured.");
            return 1;
        }
        // Same reasoning as the RemoteUrl guard above: SettingsForm trims the
        // branch textbox, so clearing it and saving persists "". Without this
        // check that empty string reaches 'git init -b ""' inside GitBackend,
        // which fails as a backend error (exit 2) instead of the
        // configuration error it actually is (exit 1).
        if (config.Github.Enabled && string.IsNullOrWhiteSpace(config.Github.Branch))
        {
            Log.Warn("BackupRunner: GitHub backup is enabled but Branch is not configured.");
            return 1;
        }
        if (config.Drive.Enabled && string.IsNullOrWhiteSpace(config.Drive.RcloneRemote))
        {
            Log.Warn("BackupRunner: Google Drive backup is enabled but RcloneRemote is not configured.");
            return 1;
        }

        // Always use the 4-arg FileSelector.Select overload (via the real
        // SelectFiles delegate below): the 3-arg convenience overload
        // discards the withheld list, which makes silently omitting files
        // the user asked for the path of least resistance. A backup tool
        // that silently drops files is a correctness bug, so what the
        // denylist withheld is logged explicitly below.
        var (files, withheld) = select(config);

        if (withheld.Count > 0)
        {
            Log.Warn(
                $"BackupRunner: {withheld.Count} file(s) withheld by the secret denylist: " +
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
            Log.Error($"BackupRunner: aborting - secret file(s) in selection: {string.Join(", ", offenders)}");
            return 1;
        }

        if (files.Count == 0)
        {
            Log.Warn("BackupRunner: nothing selected to back up.");
            return 1;
        }

        var anyFailed = false;

        if (config.Github.Enabled)
        {
            var ok = RunBackend("GitHub",
                () => new GitBackend(runner, stagingDir).Run(config.SourceRoot, files, config.Github));
            anyFailed |= !ok;
        }

        // Deliberately not an "else if" and not short-circuited by the
        // GitHub result above: each enabled backend must get a chance to
        // run regardless of whether the other one failed OR threw.
        if (config.Drive.Enabled)
        {
            var ok = RunBackend("Google Drive",
                () => new RcloneBackend(runner, tempDir).Run(config.SourceRoot, files, config.Drive));
            anyFailed |= !ok;
        }

        return anyFailed ? 2 : 0;
    }

    /// <summary>
    /// Real file-selection step used by the public <see cref="Run(BackupConfig,IProcessRunner,string,string)"/>
    /// overload: the 4-arg <see cref="FileSelector.Select"/> overload, wired
    /// through so the withheld list is never silently discarded.
    /// </summary>
    private static (IReadOnlyList<string> Files, List<string> Withheld) SelectFiles(BackupConfig config)
    {
        var files = new FileSelector().Select(config.SourceRoot, config.Include, config.Exclude, out var withheld);
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
    /// </summary>
    private static bool RunBackend(string name, Func<BackendResult> run)
    {
        try
        {
            var r = run();
            if (!r.Ok)
            {
                Log.Error($"BackupRunner: {name} backend failed: {CredentialScrubber.Scrub(r.Message)}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"BackupRunner: {name} backend threw: {CredentialScrubber.Scrub(ex.Message)}");
            return false;
        }
    }
}
