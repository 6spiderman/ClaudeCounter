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
    //
    // Fix round 2: "**/*cache*" was dropped entirely rather than kept
    // alongside "**/cache/**". FileSelector now only prunes a directory
    // wholesale for an exclude pattern that ends in literal "**" (see its
    // doc comment) - "**/*cache*" does not end in "**", so it was never
    // eligible to prune directories, only to match individual file names,
    // and it was already measured to remove almost nothing that way. Keeping
    // it as a shipped default was dead weight with a subtly misleading name
    // ("looks like it prunes cache dirs, does not").
    [Fact]
    public void DefaultDoesNotDeepIncludePluginTreeAndPrunesCacheDirectories()
    {
        var c = BackupConfig.Default();
        Assert.Contains("plugins/*.json", c.Include);
        Assert.DoesNotContain("plugins/**/*.json", c.Include);
        Assert.Contains("**/cache/**", c.Exclude);
        Assert.DoesNotContain("**/*cache*", c.Exclude);
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
