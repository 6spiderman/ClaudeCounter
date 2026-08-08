using ClaudeCounter.Core;

namespace ClaudeBackup;

internal static class Program
{
    private static int Main()
    {
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
