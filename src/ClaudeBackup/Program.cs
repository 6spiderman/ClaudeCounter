using ClaudeCounter.Core;

namespace ClaudeBackup;

internal static class Program
{
    private static int Main()
    {
        // Single instance: ClaudeBackup.exe has no other concurrency guard.
        // "Back up now" is reachable from both the tray menu and Settings,
        // and the scheduled task can fire at any moment - two instances
        // racing against the same hardcoded staging dir would interleave
        // GitBackend.MirrorFiles's delete-then-recopy with another
        // instance's add/commit/push, and because the mirror is deliberately
        // designed so deletions propagate, the loser's commit would delete
        // the winner's files from the remote backup, silently, at exit code
        // 0. Mirrors the tray's own mutex pattern (see
        // src/ClaudeCounter/Program.cs). The mutex must stay referenced for
        // the process lifetime, hence `using`. A second instance finding the
        // mutex already held is an expected, benign race - not a failure -
        // so it logs and exits 0 rather than reporting exit 1 or 2.
        using var mutex = new Mutex(initiallyOwned: true,
            @"Local\ClaudeCounter_Backup_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log.Info("ClaudeBackup: another backup is already in progress; exiting.");
            return 0;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var staging = Path.Combine(local, "ClaudeCounter", "backup-repo");
        var temp = Path.Combine(local, "ClaudeCounter", "backup-tmp");

        return RunWorker(BackupConfig.DefaultPath(), BackupStatus.DefaultPath(), new ProcessRunner(), staging, temp);
    }

    /// <summary>
    /// The actual worker run, factored out of Main so tests can drive it
    /// against temp config/status paths and a fake IProcessRunner - never a
    /// real process, never the user's real %LOCALAPPDATA%. Also the S11a
    /// status-writing seam: status is written exactly twice here, covering
    /// every terminating path documented on BackupRunner (success, config
    /// error, backend failure, and the fail-closed offender abort all come
    /// back as a BackupRunResult from RunDetailed and are written by the
    /// first BackupStatusWriter.Record call below) plus the one path
    /// BackupRunner cannot itself cover: an exception escaping this method's
    /// own try, written by the catch block's RecordUnhandledException call.
    /// </summary>
    internal static int RunWorker(
        string configPath, string statusPath, IProcessRunner runner, string stagingDir, string tempDir)
        => RunWorker(configPath, statusPath, runner, stagingDir, tempDir, select: null);

    /// <summary>
    /// Test seam for the "exception escaping Main" path: a caller-supplied
    /// <paramref name="select"/> that throws propagates out of
    /// BackupRunner.RunDetailed (TrySelect calls it unguarded - see
    /// BackupRunner's own comments) and is caught here exactly like an
    /// unforeseen exception from anywhere else in the try would be.
    /// Production code always goes through the 5-arg overload above (select:
    /// null), which uses BackupRunner's real file-selection step.
    /// </summary>
    internal static int RunWorker(
        string configPath, string statusPath, IProcessRunner runner, string stagingDir, string tempDir,
        Func<string, IEnumerable<string>, IEnumerable<string>, (IReadOnlyList<string> Files, List<string> Withheld)>? select)
    {
        var now = DateTimeOffset.UtcNow;
        BackupConfig? config = null;

        // Task Scheduler only ever sees this method's return value. Without
        // this guard, anything that escapes BackupConfig.Load or
        // BackupRunner.RunDetailed - an UnauthorizedAccessException from an
        // ACL-locked backup.json (does NOT derive from IOException, so
        // BackupConfig.Load's own catch does not see it), a
        // NotSupportedException from JsonSerializer, or any other unforeseen
        // exception - would exit the process with the CLR's unhandled-
        // exception code instead of one of the documented 0/1/2 codes, and
        // with nothing in the log explaining why. Log itself never throws
        // (every failure inside it is swallowed - see Log.Write), so this
        // catch can always record what happened before returning a
        // meaningful "a backend failed" style code.
        try
        {
            config = BackupConfig.Load(configPath);
            Log.Info("ClaudeBackup starting.");
            var result = select is null
                ? BackupRunner.RunDetailed(config, runner, stagingDir, tempDir)
                : BackupRunner.RunDetailed(config, runner, stagingDir, tempDir, select);
            BackupStatusWriter.Record(statusPath, result, now);
            Log.Info($"ClaudeBackup finished with exit code {result.ExitCode}.");
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            // Scrubbed: an exception thrown deep in a process-runner or
            // config layer can carry a remote spec (a URL with an embedded
            // credential) in its message or stack trace.
            var scrubbed = CredentialScrubber.Scrub(ex.ToString());
            Log.Error($"ClaudeBackup: unhandled exception: {scrubbed}");
            // config may be null (BackupConfig.Load itself threw) or the
            // config that was in hand when something later threw -
            // RecordUnhandledException uses it best-effort to mark whichever
            // destinations it confirms were enabled as a failed attempt, so
            // even this path is visible to BackupHealth, not a silent gap.
            BackupStatusWriter.RecordUnhandledException(statusPath, config, scrubbed, now);
            return 2;
        }
    }
}
