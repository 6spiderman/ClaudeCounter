// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupConfigTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupConfigTests
{
    [Fact]
    public void DefaultExcludesProjectsHistory()
    {
        var c = BackupConfig.Default();
        Assert.Contains("projects/**", c.Exclude);
        Assert.Contains("settings.json", c.Include);
    }

    // Fix round 1 / I6, I7: "plugins/**/*.json" pulled in the entire plugin
    // tree (measured at 144 files on a real machine), most of it
    // re-downloadable third-party content, some of it carrying secret-shaped
    // keys the file-name denylist cannot see inside. The default now only
    // reaches the top-level plugin manifests. "**/*cache*" only matched the
    // final path segment, so a directory named e.g. "plugins/cache" survived
    // untouched - "**/cache/**" prunes the whole subtree.
    [Fact]
    public void DefaultDoesNotDeepIncludePluginTreeAndPrunesCacheDirectories()
    {
        var c = BackupConfig.Default();
        Assert.Contains("plugins/*.json", c.Include);
        Assert.DoesNotContain("plugins/**/*.json", c.Include);
        Assert.Contains("**/cache/**", c.Exclude);
    }

    [Fact]
    public void RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            var c = BackupConfig.Default();
            c.Github.Enabled = true;
            c.Github.RemoteUrl = "git@github.com:me/claude-backup.git";
            c.Save(path);
            var back = BackupConfig.Load(path);
            Assert.True(back.Github.Enabled);
            Assert.Equal("git@github.com:me/claude-backup.git", back.Github.RemoteUrl);
            Assert.Equal("main", back.Github.Branch);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadMissingReturnsDefault()
    {
        var c = BackupConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        Assert.NotNull(c);
        Assert.Contains("settings.json", c.Include);
    }
}
