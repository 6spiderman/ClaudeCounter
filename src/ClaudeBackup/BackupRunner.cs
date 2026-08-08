using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Orchestrates one backup run: select files, log what the secret denylist
/// withheld, re-assert the denylist as a fail-closed backstop, then run each
/// enabled backend independently so one backend failing does not prevent the
/// other from running.
///
/// Exit codes (meaningful to Task Scheduler, which records them):
///   0 - success: every enabled backend succeeded.
///   1 - configuration/selection error: no destination enabled, nothing
///       selected, or a denylist offender detected in the selection.
///   2 - at least one enabled backend failed.
/// </summary>
public static class BackupRunner
{
    public static int Run(BackupConfig config, IProcessRunner runner, string stagingDir, string tempDir)
    {
        if (!config.Github.Enabled && !config.Drive.Enabled)
        {
            Log.Warn("BackupRunner: no backup destinations enabled.");
            return 1;
        }

        // Always use the 4-arg overload: the 3-arg convenience overload
        // discards the withheld list, which makes silently omitting files
        // the user asked for the path of least resistance. A backup tool
        // that silently drops files is a correctness bug, so what the
        // denylist withheld is logged explicitly below.
        var files = new FileSelector().Select(
            config.SourceRoot, config.Include, config.Exclude, out var withheld);

        if (withheld.Count > 0)
        {
            Log.Warn(
                $"BackupRunner: {withheld.Count} file(s) withheld by the secret denylist: " +
                string.Join(", ", withheld));
        }

        if (TryAbortForOffenders(files, out var offenderExitCode))
            return offenderExitCode;

        if (files.Count == 0)
        {
            Log.Warn("BackupRunner: nothing selected to back up.");
            return 1;
        }

        var anyFailed = false;

        if (config.Github.Enabled)
        {
            var r = new GitBackend(runner, stagingDir).Run(config.SourceRoot, files, config.Github);
            if (!r.Ok)
            {
                Log.Error($"BackupRunner: GitHub backend failed: {r.Message}");
                anyFailed = true;
            }
        }

        // Deliberately not an "else if" and not short-circuited by the
        // GitHub result above: each enabled backend must get a chance to
        // run regardless of whether the other one failed.
        if (config.Drive.Enabled)
        {
            var r = new RcloneBackend(runner, tempDir).Run(config.SourceRoot, files, config.Drive);
            if (!r.Ok)
            {
                Log.Error($"BackupRunner: Google Drive backend failed: {r.Message}");
                anyFailed = true;
            }
        }

        return anyFailed ? 2 : 0;
    }

    /// <summary>
    /// Fail-closed backstop: even though <see cref="FileSelector"/> already
    /// strips anything <see cref="SecretDenylist"/> flags, this
    /// re-assertion is what actually protects an upload if that invariant
    /// is ever broken by a future change to FileSelector. Factored out
    /// (internal, not private) so its own exit-code/logging behavior can be
    /// exercised directly in tests without needing FileSelector to first
    /// fail to do its job - mirroring how GitBackend.IsWithinDirectory is
    /// tested directly as a second, independent layer behind
    /// IsSafeRelativePath.
    /// </summary>
    internal static bool TryAbortForOffenders(IReadOnlyList<string> files, out int exitCode)
    {
        var offenders = SecretDenylist.Offenders(files);
        if (offenders.Count == 0)
        {
            exitCode = 0;
            return false;
        }

        Log.Error($"BackupRunner: aborting - secret file(s) in selection: {string.Join(", ", offenders)}");
        exitCode = 1;
        return true;
    }
}
