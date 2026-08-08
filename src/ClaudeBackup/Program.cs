using ClaudeCounter.Core;

namespace ClaudeBackup;

internal static class Program
{
    private static int Main()
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
}
