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
        Assert.Contains("projects/**", c.Github.Exclude);
        Assert.Contains("settings.json", c.Github.Include);
        Assert.Contains("projects/**", c.Drive.Exclude);
        Assert.Contains("settings.json", c.Drive.Include);
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
        foreach (var target in new[] { (IReadOnlyList<string>)c.Github.Include, c.Drive.Include })
        {
            Assert.Contains("plugins/*.json", target);
            Assert.DoesNotContain("plugins/**/*.json", target);
        }
        foreach (var target in new[] { (IReadOnlyList<string>)c.Github.Exclude, c.Drive.Exclude })
        {
            Assert.Contains("**/cache/**", target);
            Assert.DoesNotContain("**/*cache*", target);
        }
    }

    // S5: Default() now stamps the current schema version up front, exactly
    // like SettingsStore's fresh.Normalize() stamps NotificationStateVersion
    // before a fresh AppSettings' first save - otherwise a freshly created
    // config, saved and reloaded, would be mistaken by Load() for a
    // pre-per-destination file the next time it is read.
    [Fact]
    public void DefaultStampsCurrentBackupConfigVersion()
    {
        var c = BackupConfig.Default();
        Assert.Equal(BackupConfig.CurrentBackupConfigVersion, c.BackupConfigVersion);
    }

    // S5: Default() no longer populates the legacy top-level Include/Exclude
    // at all - GitTarget/DriveTarget own the real selection from the start
    // for a brand new config, so there is nothing left for the legacy
    // fields to carry.
    [Fact]
    public void DefaultLeavesLegacyIncludeExcludeEmpty()
    {
        var c = BackupConfig.Default();
        Assert.Empty(c.Include);
        Assert.Empty(c.Exclude);
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
            c.Github.Include = new() { "settings.json" };
            c.Drive.Include = new() { "CLAUDE.md" };
            c.Save(path);
            var back = BackupConfig.Load(path);
            Assert.True(back.Github.Enabled);
            Assert.Equal("git@github.com:me/claude-backup.git", back.Github.RemoteUrl);
            Assert.Equal("main", back.Github.Branch);
            // Independent selections round-trip independently - proves the
            // two targets are not accidentally aliased to the same list.
            Assert.Equal(new[] { "settings.json" }, back.Github.Include);
            Assert.Equal(new[] { "CLAUDE.md" }, back.Drive.Include);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadMissingReturnsDefault()
    {
        var c = BackupConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        Assert.NotNull(c);
        Assert.Contains("settings.json", c.Github.Include);
        Assert.Contains("settings.json", c.Drive.Include);
    }
}

/// <summary>
/// S5: BackupConfigVersion migration. Modeled on AppSettingsTests'
/// NotificationStateVersion coverage - a legacy file (version 0, top-level
/// Include/Exclude non-empty) must migrate that ONE selection onto BOTH
/// destinations exactly once, and a file already at the current shape must
/// be left alone.
/// </summary>
public class BackupConfigMigrationTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"bkcfg-migrate-{Guid.NewGuid():N}.json");

    // Hand-written, not built via BackupConfig.Default() + Save(): this must
    // reproduce exactly the JSON shape a pre-S5 backup.json actually had on
    // disk (no BackupConfigVersion field, no per-target Include/Exclude),
    // not whatever the current type's serializer happens to produce today.
    private const string LegacyJson = """
        {
          "SourceRoot": "C:\\Users\\test\\.claude",
          "Include": ["settings.json", "CLAUDE.md"],
          "Exclude": ["projects/**"],
          "Github": { "Enabled": true, "RemoteUrl": "git@github.com:me/repo.git", "Branch": "main" },
          "Drive": { "Enabled": false, "RcloneRemote": "" },
          "Schedule": { "Frequency": "daily", "Time": "09:00" }
        }
        """;

    [Fact]
    public void LoadMigratesLegacySelectionOntoBothTargets()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, LegacyJson);

            var loaded = BackupConfig.Load(path);

            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, loaded.Github.Include);
            Assert.Equal(new[] { "projects/**" }, loaded.Github.Exclude);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, loaded.Drive.Include);
            Assert.Equal(new[] { "projects/**" }, loaded.Drive.Exclude);
            Assert.Empty(loaded.Include);
            Assert.Empty(loaded.Exclude);
            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, loaded.BackupConfigVersion);

            // Migration persists immediately - the file on disk must already
            // reflect the migrated shape, not just the in-memory object.
            var onDisk = BackupConfig.Load(path);
            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, onDisk.BackupConfigVersion);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, onDisk.Github.Include);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SecondLoadDoesNotReMigrateOrClobberACustomization()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, LegacyJson);

            // First load migrates and persists.
            var first = BackupConfig.Load(path);
            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, first.BackupConfigVersion);

            // Customize GitHub's selection only, independently of Drive, and
            // save - simulating a user editing the Backup tab after
            // upgrading.
            first.Github.Include = new() { "settings.json", "CLAUDE.md", "agents/**" };
            first.Save(path);

            // A second Load() must not re-run the legacy-copy branch (the
            // legacy fields are already empty and the version is already
            // current) - if it did, nothing would change here since the
            // legacy lists are empty, but critically it also must not
            // silently overwrite the customization with anything stale.
            var second = BackupConfig.Load(path);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md", "agents/**" }, second.Github.Include);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, second.Drive.Include);
            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, second.BackupConfigVersion);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConfigAlreadyAtCurrentVersionIsUntouchedEvenWithLeftoverLegacyFields()
    {
        var path = TempPath();
        try
        {
            // A pathological file: already stamped at the current version
            // (so Load() must treat it as already migrated) but still
            // carrying non-empty legacy fields and per-target selections
            // that deliberately differ from them and from each other -
            // proves the version gate alone decides whether the copy runs,
            // not merely "legacy fields are non-empty".
            var json = """
                {
                  "SourceRoot": "C:\\Users\\test\\.claude",
                  "Include": ["should-not-be-copied.txt"],
                  "Exclude": ["should-not-be-copied-either/**"],
                  "Github": { "Enabled": true, "RemoteUrl": "git@github.com:me/repo.git", "Branch": "main",
                              "Include": ["github-only.txt"], "Exclude": [] },
                  "Drive": { "Enabled": false, "RcloneRemote": "",
                             "Include": ["drive-only.txt"], "Exclude": [] },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """;
            File.WriteAllText(path, json);

            var loaded = BackupConfig.Load(path);

            Assert.Equal(new[] { "github-only.txt" }, loaded.Github.Include);
            Assert.Equal(new[] { "drive-only.txt" }, loaded.Drive.Include);
            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, loaded.BackupConfigVersion);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LegacyVersionZeroWithEmptyLegacyFieldsDoesNotThrowAndLeavesTargetsEmpty()
    {
        var path = TempPath();
        try
        {
            // A user who cleared both boxes before this feature existed:
            // version 0, but nothing to copy. Must not throw, and the
            // per-target selections (absent from the JSON, so the type's
            // own field initializer applies) come back empty - the same end
            // state copying two empty lists onto them would have produced.
            var json = """
                {
                  "SourceRoot": "C:\\Users\\test\\.claude",
                  "Include": [],
                  "Exclude": [],
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": false, "RcloneRemote": "" },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" }
                }
                """;
            File.WriteAllText(path, json);

            var loaded = BackupConfig.Load(path);

            Assert.Empty(loaded.Github.Include);
            Assert.Empty(loaded.Drive.Include);
        }
        finally { File.Delete(path); }
    }
}
