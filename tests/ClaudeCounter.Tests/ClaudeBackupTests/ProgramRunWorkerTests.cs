// tests/ClaudeCounter.Tests/ClaudeBackupTests/ProgramRunWorkerTests.cs
//
// S11a: proves BackupStatusWriter is actually wired into every terminating
// path of the worker (Program.RunWorker), not just BackupRunner.RunDetailed
// in isolation - success, config error, the fail-closed offender abort, and
// an exception escaping to Main's own catch-all (simulated here via a
// throwing selector, the same seam BackupRunnerTests' offender tests use).
// All against temp config/status paths and a fake IProcessRunner - no real
// process, no real %LOCALAPPDATA%.
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class ProgramRunWorkerTests : IDisposable
{
    private sealed class OkRunner : IProcessRunner
    {
        public bool Exists(string file) => true;
        public ProcessResult Run(string f, IReadOnlyList<string> a, string? wd = null)
        {
            if (a.Count > 0 && a[0] == "diff")
                return new(1, "", ""); // staged changes present -> proceed to commit
            if (f == "git" && a.Count > 0 && a[0] == "init" && wd is not null)
                Directory.CreateDirectory(Path.Combine(wd, ".git"));
            return new(0, "", "");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pworker-src-{Guid.NewGuid():N}");
    private readonly string _stg = Path.Combine(Path.GetTempPath(), $"pworker-stg-{Guid.NewGuid():N}");
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"pworker-tmp-{Guid.NewGuid():N}");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"pworker-cfg-{Guid.NewGuid():N}.json");
    private readonly string _statusPath = Path.Combine(Path.GetTempPath(), $"pworker-status-{Guid.NewGuid():N}.json");

    public ProgramRunWorkerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _stg, _tmp })
            if (Directory.Exists(d)) Directory.Delete(d, true);
        foreach (var f in new[] { _configPath, _statusPath })
            if (File.Exists(f)) File.Delete(f);
    }

    private BackupConfig Config() => new()
    {
        SourceRoot = _root,
        Destinations = new()
        {
            new BackupDestination { Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub, Enabled = true, RemoteUrl = "url", Branch = "main", Include = new() { "settings.json" }, Exclude = new() },
            new BackupDestination { Id = "drive", Name = "Google Drive (rclone)", Kind = DestinationKind.Rclone, Enabled = false, Include = new() { "settings.json" }, Exclude = new() },
        },
    };

    [Fact]
    public void SuccessPathWritesStatusWithSuccessOutcome()
    {
        Config().Save(_configPath);

        var code = Program.RunWorker(_configPath, _statusPath, new OkRunner(), _stg, _tmp);

        Assert.Equal(0, code);
        var status = BackupStatus.Load(_statusPath);
        Assert.Equal(0, status.LastExitCode);
        Assert.NotNull(status.LastRunUtc);
        Assert.Equal(BackupOutcome.Success, status.For("github").LastOutcome);
        Assert.NotNull(status.For("github").LastSuccessUtc);
    }

    [Fact]
    public void ConfigErrorPathStillWritesStatus()
    {
        var c = Config();
        c.Destinations.Single(d => d.Id == "github").Enabled = false; // nothing enabled at all
        c.Save(_configPath);

        var code = Program.RunWorker(_configPath, _statusPath, new OkRunner(), _stg, _tmp);

        Assert.Equal(1, code);
        var status = BackupStatus.Load(_statusPath);
        Assert.Equal(1, status.LastExitCode);
        Assert.NotNull(status.LastRunUtc);
    }

    [Fact]
    public void OffenderAbortPathWritesFailedStatusForTheActiveDestination()
    {
        Config().Save(_configPath);

        var code = Program.RunWorker(
            _configPath, _statusPath, new OkRunner(), _stg, _tmp,
            (_, _, _) => (new[] { ".credentials.json" }, new List<string>()));

        Assert.Equal(1, code);
        var status = BackupStatus.Load(_statusPath);
        Assert.Equal(1, status.LastExitCode);
        Assert.Equal(BackupOutcome.Failed, status.For("github").LastOutcome);
        Assert.Contains("secret-shaped file", status.For("github").LastMessage);
    }

    // The one path BackupRunner cannot cover itself: something throws before
    // producing a BackupRunResult at all. A throwing selector propagates out
    // of BackupRunner.RunDetailed (TrySelect calls it unguarded) exactly like
    // an unforeseen exception anywhere else in RunWorker's try would.
    [Fact]
    public void ExceptionEscapingRunWorkerStillWritesStatusForEnabledDestinations()
    {
        Config().Save(_configPath);

        var code = Program.RunWorker(
            _configPath, _statusPath, new OkRunner(), _stg, _tmp,
            (_, _, _) => throw new InvalidOperationException("simulated selector failure"));

        Assert.Equal(2, code);
        var status = BackupStatus.Load(_statusPath);
        Assert.Equal(2, status.LastExitCode);
        Assert.Equal(BackupOutcome.Failed, status.For("github").LastOutcome);
        Assert.Contains("simulated selector failure", status.For("github").LastMessage);
        Assert.Null(status.For("drive").LastOutcome); // Drive was never enabled - untouched
    }

    // Rule 3, exercised end to end through RunWorker: a status file that
    // cannot be written must not change what the worker itself reports.
    [Fact]
    public void StatusWriteFailureDoesNotChangeTheWorkersExitCode()
    {
        Config().Save(_configPath);
        var blockingFile = Path.Combine(Path.GetTempPath(), $"pworker-block-{Guid.NewGuid():N}");
        File.WriteAllText(blockingFile, "not a directory");
        try
        {
            var badStatusPath = Path.Combine(blockingFile, "backup-status.json");

            var code = Program.RunWorker(_configPath, badStatusPath, new OkRunner(), _stg, _tmp);

            Assert.Equal(0, code); // still a real success, despite the status write failing
        }
        finally { File.Delete(blockingFile); }
    }
}
