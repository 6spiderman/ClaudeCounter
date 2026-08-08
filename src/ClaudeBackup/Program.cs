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

        // Task Scheduler only ever sees this method's return value. Without
        // this guard, anything that escapes BackupConfig.Load or
        // BackupRunner.Run - an UnauthorizedAccessException from an
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
            var config = BackupConfig.Load(BackupConfig.DefaultPath());
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var staging = Path.Combine(local, "ClaudeCounter", "backup-repo");
            var temp = Path.Combine(local, "ClaudeCounter", "backup-tmp");

            Log.Info("ClaudeBackup starting.");
            var code = BackupRunner.Run(config, new ProcessRunner(), staging, temp);
            Log.Info($"ClaudeBackup finished with exit code {code}.");
            return code;
        }
        catch (Exception ex)
        {
            // Scrubbed: an exception thrown deep in a process-runner or
            // config layer can carry a remote spec (a URL with an embedded
            // credential) in its message or stack trace.
            Log.Error($"ClaudeBackup: unhandled exception: {CredentialScrubber.Scrub(ex.ToString())}");
            return 2;
        }
    }
}
