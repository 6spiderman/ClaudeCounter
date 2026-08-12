// tests/ClaudeCounter.Tests/ClaudeBackupTests/BackupConfigTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

/// <summary>
/// S17c: rewritten off the now-deleted <c>BackupConfig.Github</c>/<c>Drive</c>
/// shim - every assertion below is unchanged in what it proves, only how it
/// reaches the "github"/"drive" destination changed (via <see cref="Gh"/>/
/// <see cref="Dr"/>, mirroring the identical helper pair BackupRunnerTests
/// already introduced for the same rewrite one task earlier).
/// </summary>
public class BackupConfigTests
{
    private static BackupDestination Gh(BackupConfig c) => c.Destinations.First(d => d.Id == "github");
    private static BackupDestination Dr(BackupConfig c) => c.Destinations.First(d => d.Id == "drive");

    [Fact]
    public void DefaultExcludesProjectsHistory()
    {
        var c = BackupConfig.Default();
        Assert.Contains("projects/**", Gh(c).Exclude);
        Assert.Contains("settings.json", Gh(c).Include);
        Assert.Contains("projects/**", Dr(c).Exclude);
        Assert.Contains("settings.json", Dr(c).Include);
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
        foreach (var target in new[] { (IReadOnlyList<string>)Gh(c).Include, Dr(c).Include })
        {
            Assert.Contains("plugins/*.json", target);
            Assert.DoesNotContain("plugins/**/*.json", target);
        }
        foreach (var target in new[] { (IReadOnlyList<string>)Gh(c).Exclude, Dr(c).Exclude })
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
    // at all - each destination owns the real selection from the start for
    // a brand new config, so there is nothing left for the legacy fields to
    // carry.
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
            Gh(c).Enabled = true;
            Gh(c).RemoteUrl = "git@github.com:me/claude-backup.git";
            Gh(c).Include = new() { "settings.json" };
            Dr(c).Include = new() { "CLAUDE.md" };
            c.Save(path);
            var back = BackupConfig.Load(path);
            Assert.True(Gh(back).Enabled);
            Assert.Equal("git@github.com:me/claude-backup.git", Gh(back).RemoteUrl);
            Assert.Equal("main", Gh(back).Branch);
            // Independent selections round-trip independently - proves the
            // two destinations are not accidentally aliased to the same list.
            Assert.Equal(new[] { "settings.json" }, Gh(back).Include);
            Assert.Equal(new[] { "CLAUDE.md" }, Dr(back).Include);
        }
        finally { File.Delete(path); }
    }

    // S7/S8: the schedule-robustness fields on ScheduleConfig, and the
    // retention fields on a destination, must round-trip like every other
    // setting - including KeepLastCount/DeleteOlderThanDays staying null
    // when never set, so an existing user who has not configured retention
    // is not silently opted into a retention rule.
    [Fact]
    public void ScheduleAndDriveRetentionSettingsRoundTripThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            var c = BackupConfig.Default();
            c.Schedule.StartWhenAvailable = false;
            c.Schedule.RunOnlyIfNetworkAvailable = false;
            c.Schedule.DisallowStartIfOnBatteries = true;
            c.Schedule.StopIfGoingOnBatteries = true;
            c.Schedule.RestartOnFailure = false;
            c.Schedule.RestartIntervalMinutes = 45;
            c.Schedule.RestartCount = 7;
            Dr(c).KeepLastCount = 14;
            Dr(c).DeleteOlderThanDays = 60;
            c.Save(path);

            var back = BackupConfig.Load(path);
            Assert.False(back.Schedule.StartWhenAvailable);
            Assert.False(back.Schedule.RunOnlyIfNetworkAvailable);
            Assert.True(back.Schedule.DisallowStartIfOnBatteries);
            Assert.True(back.Schedule.StopIfGoingOnBatteries);
            Assert.False(back.Schedule.RestartOnFailure);
            Assert.Equal(45, back.Schedule.RestartIntervalMinutes);
            Assert.Equal(7, back.Schedule.RestartCount);
            Assert.Equal(14, Dr(back).KeepLastCount);
            Assert.Equal(60, Dr(back).DeleteOlderThanDays);
        }
        finally { File.Delete(path); }
    }

    // A backup.json written before this feature existed has no
    // KeepLastCount/DeleteOlderThanDays properties at all - deserializing it
    // must leave both null (retention off) rather than throwing or defaulting
    // to some non-null value that would silently start pruning a remote the
    // user never configured for it.
    [Fact]
    public void MissingRetentionFieldsDefaultToNullNotSomeActiveValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": true, "RcloneRemote": "gdrive:X" },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """);
            var loaded = BackupConfig.Load(path);
            Assert.Null(Dr(loaded).KeepLastCount);
            Assert.Null(Dr(loaded).DeleteOlderThanDays);
            // And the schedule-robustness defaults come back as the design
            // spec's defaults, not some JSON-absent zero/false value.
            Assert.True(loaded.Schedule.StartWhenAvailable);
            Assert.True(loaded.Schedule.RunOnlyIfNetworkAvailable);
            Assert.False(loaded.Schedule.DisallowStartIfOnBatteries);
            Assert.False(loaded.Schedule.StopIfGoingOnBatteries);
            Assert.True(loaded.Schedule.RestartOnFailure);
            Assert.Equal(15, loaded.Schedule.RestartIntervalMinutes);
            Assert.Equal(3, loaded.Schedule.RestartCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadMissingReturnsDefault()
    {
        var c = BackupConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        Assert.NotNull(c);
        Assert.Contains("settings.json", Gh(c).Include);
        Assert.Contains("settings.json", Dr(c).Include);
    }

    // S11a: BackupStaleAfterDays defaults to 3 (design spec) and round-trips
    // like every other schedule setting.
    [Fact]
    public void DefaultBackupStaleAfterDaysIsThree()
    {
        var c = BackupConfig.Default();
        Assert.Equal(3, c.Schedule.BackupStaleAfterDays);
    }

    [Fact]
    public void BackupStaleAfterDaysRoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            var c = BackupConfig.Default();
            c.Schedule.BackupStaleAfterDays = 10;
            c.Save(path);

            var back = BackupConfig.Load(path);
            Assert.Equal(10, back.Schedule.BackupStaleAfterDays);
        }
        finally { File.Delete(path); }
    }

    // 0 ("never warn about staleness") is a meaningful, in-range value - see
    // AppSettings.PopupAutoDismissSeconds for the identical shape - so Load's
    // Normalize() call must not coerce it back to the default.
    [Fact]
    public void ZeroBackupStaleAfterDaysSurvivesLoadAsNeverWarn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            var c = BackupConfig.Default();
            c.Schedule.BackupStaleAfterDays = 0;
            c.Save(path);

            var back = BackupConfig.Load(path);
            Assert.Equal(0, back.Schedule.BackupStaleAfterDays);
        }
        finally { File.Delete(path); }
    }

    // Normalize must clamp an out-of-range value (e.g. from a hand-edited
    // file) rather than let it reach BackupHealth.Evaluate unchecked.
    [Fact]
    public void NegativeBackupStaleAfterDaysIsClampedToZeroOnLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": false, "RcloneRemote": "" },
                  "Schedule": { "Frequency": "daily", "Time": "09:00", "BackupStaleAfterDays": -5 },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);
            Assert.Equal(0, loaded.Schedule.BackupStaleAfterDays);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HugeBackupStaleAfterDaysIsClampedOnLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": false, "RcloneRemote": "" },
                  "Schedule": { "Frequency": "daily", "Time": "09:00", "BackupStaleAfterDays": 999999 },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);
            Assert.Equal(365, loaded.Schedule.BackupStaleAfterDays);
        }
        finally { File.Delete(path); }
    }

    // A backup.json written before this field existed has no
    // BackupStaleAfterDays property at all - deserializing it must produce
    // the type default (3), not 0.
    [Fact]
    public void MissingBackupStaleAfterDaysFieldDefaultsToThree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": false, "RcloneRemote": "" },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);
            Assert.Equal(3, loaded.Schedule.BackupStaleAfterDays);
        }
        finally { File.Delete(path); }
    }

    // S14: Transport defaults to Rclone (0) and FolderPath defaults to "" -
    // an existing config with neither field present must load exactly as it
    // did before this feature existed.
    [Fact]
    public void DefaultDriveTransportIsRcloneWithBlankFolderPath()
    {
        var c = BackupConfig.Default();
        Assert.Equal(DestinationKind.Rclone, Dr(c).Kind);
        Assert.Equal("", Dr(c).FolderPath);
    }

    [Fact]
    public void TransportAndFolderPathRoundTripThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            var c = BackupConfig.Default();
            Dr(c).Kind = DestinationKind.SyncFolder;
            Dr(c).FolderPath = @"D:\SyncFolder\ClaudeBackups";
            c.Save(path);

            var back = BackupConfig.Load(path);
            Assert.Equal(DestinationKind.SyncFolder, Dr(back).Kind);
            Assert.Equal(@"D:\SyncFolder\ClaudeBackups", Dr(back).FolderPath);
        }
        finally { File.Delete(path); }
    }

    // A backup.json written before this feature existed has no Transport or
    // FolderPath property at all - deserializing it must produce Rclone/""
    // (unchanged behaviour), not throw and not silently switch transport.
    [Fact]
    public void MissingTransportAndFolderPathFieldsDefaultToRcloneAndBlank()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": true, "RcloneRemote": "gdrive:X" },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);
            Assert.Equal(DestinationKind.Rclone, Dr(loaded).Kind);
            Assert.Equal("", Dr(loaded).FolderPath);
            Assert.Equal("gdrive:X", Dr(loaded).RcloneRemote);
        }
        finally { File.Delete(path); }
    }

    // An explicit JSON null for FolderPath (rather than the field simply
    // being absent) must not NRE - Normalize's defensive guard converts it
    // to "".
    [Fact]
    public void NullFolderPathInJsonDoesNotThrowAndNormalizesToEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": false, "RcloneRemote": "", "FolderPath": null },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);
            Assert.Equal("", Dr(loaded).FolderPath);
        }
        finally { File.Delete(path); }
    }

    // S16: SyncProvider defaults to Other (0), same back-compat shape as
    // Transport - a config with no SyncProvider property at all must load
    // exactly as it did before this field existed.
    [Fact]
    public void DefaultSyncProviderIsOther()
    {
        var c = BackupConfig.Default();
        Assert.Equal(SyncProvider.Other, Dr(c).SyncProvider);
    }

    [Fact]
    public void SyncProviderRoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            var c = BackupConfig.Default();
            Dr(c).Kind = DestinationKind.SyncFolder;
            Dr(c).SyncProvider = SyncProvider.Nas;
            Dr(c).FolderPath = @"\\192.168.1.210\media\ClaudeBackups";
            c.Save(path);

            var back = BackupConfig.Load(path);
            Assert.Equal(SyncProvider.Nas, Dr(back).SyncProvider);
            Assert.Equal(@"\\192.168.1.210\media\ClaudeBackups", Dr(back).FolderPath);
        }
        finally { File.Delete(path); }
    }

    // A backup.json written before this field existed - including one
    // written by the 1.2 sync-folder-transport build, which had Transport
    // and FolderPath but no SyncProvider property at all - must load with
    // SyncProvider defaulting to Other, not throw and not silently guess a
    // provider (that guessing is SyncProviderInference's job, and it is
    // display-only).
    [Fact]
    public void MissingSyncProviderFieldDefaultsToOther()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": true, "RcloneRemote": "", "Transport": 1, "FolderPath": "\\\\192.168.1.210\\media" },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);
            Assert.Equal(SyncProvider.Other, Dr(loaded).SyncProvider);
            Assert.Equal(DestinationKind.SyncFolder, Dr(loaded).Kind);
            Assert.Equal(@"\\192.168.1.210\media", Dr(loaded).FolderPath);
        }
        finally { File.Delete(path); }
    }

    // An out-of-range SyncProvider value (a hand-edited or future-version
    // file) must be reset to Other by Normalize, mirroring Kind's own
    // out-of-range guard just below.
    [Fact]
    public void OutOfRangeSyncProviderValueIsNormalizedToOtherOnLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": false, "RcloneRemote": "", "SyncProvider": 99 },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);
            Assert.Equal(SyncProvider.Other, Dr(loaded).SyncProvider);
        }
        finally { File.Delete(path); }
    }

    // An out-of-range Transport value (a hand-edited or future-version file)
    // must be reset to Rclone by Normalize rather than reaching a
    // destination's Kind as an undefined enum value.
    [Fact]
    public void OutOfRangeTransportValueIsNormalizedToRcloneOnLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Github": { "Enabled": false, "RemoteUrl": "", "Branch": "main" },
                  "Drive": { "Enabled": false, "RcloneRemote": "", "Transport": 99 },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);
            Assert.Equal(DestinationKind.Rclone, Dr(loaded).Kind);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>
/// S5: BackupConfigVersion migration. Modeled on AppSettingsTests'
/// NotificationStateVersion coverage - a legacy file (version 0, top-level
/// Include/Exclude non-empty) must migrate that ONE selection onto BOTH
/// destinations exactly once, and a file already at the current shape must
/// be left alone.
///
/// S17c: rewritten off the now-deleted <c>BackupConfig.Github</c>/<c>Drive</c>
/// shim - see <see cref="BackupConfigTests"/>' own remarks.
/// </summary>
public class BackupConfigMigrationTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"bkcfg-migrate-{Guid.NewGuid():N}.json");
    private static BackupDestination Gh(BackupConfig c) => c.Destinations.First(d => d.Id == "github");
    private static BackupDestination Dr(BackupConfig c) => c.Destinations.First(d => d.Id == "drive");

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

            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, Gh(loaded).Include);
            Assert.Equal(new[] { "projects/**" }, Gh(loaded).Exclude);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, Dr(loaded).Include);
            Assert.Equal(new[] { "projects/**" }, Dr(loaded).Exclude);
            Assert.Empty(loaded.Include);
            Assert.Empty(loaded.Exclude);
            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, loaded.BackupConfigVersion);

            // Migration persists immediately - the file on disk must already
            // reflect the migrated shape, not just the in-memory object.
            var onDisk = BackupConfig.Load(path);
            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, onDisk.BackupConfigVersion);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, Gh(onDisk).Include);
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
            // save - simulating a user editing a destination after
            // upgrading.
            Gh(first).Include = new() { "settings.json", "CLAUDE.md", "agents/**" };
            first.Save(path);

            // A second Load() must not re-run the legacy-copy branch (the
            // legacy fields are already empty and the version is already
            // current) - if it did, nothing would change here since the
            // legacy lists are empty, but critically it also must not
            // silently overwrite the customization with anything stale.
            var second = BackupConfig.Load(path);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md", "agents/**" }, Gh(second).Include);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, Dr(second).Include);
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

            Assert.Equal(new[] { "github-only.txt" }, Gh(loaded).Include);
            Assert.Equal(new[] { "drive-only.txt" }, Dr(loaded).Include);
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

            Assert.Empty(Gh(loaded).Include);
            Assert.Empty(Dr(loaded).Include);
        }
        finally { File.Delete(path); }
    }

    // S17a: a v1 file (per-destination Include/Exclude already exist, but no
    // "Destinations" array at all - this is what every real machine has on
    // disk right now, e.g. after S16) must migrate Github/Drive into
    // Destinations under the well-known "github"/"drive" ids, preserving
    // every field - connection info, selection, Kind derived from Transport.
    [Fact]
    public void V1TwoDestinationConfigMigratesIntoDestinationListWithWellKnownIds()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """
                {
                  "SourceRoot": "C:\\Users\\test\\.claude",
                  "Include": [],
                  "Exclude": [],
                  "Github": { "Enabled": true, "RemoteUrl": "git@github.com:me/repo.git", "Branch": "main",
                              "Include": ["settings.json"], "Exclude": ["projects/**"] },
                  "Drive": { "Enabled": true, "RcloneRemote": "gdrive:X", "Transport": 1,
                             "FolderPath": "D:\\SyncFolder", "SyncProvider": 2,
                             "Include": ["CLAUDE.md"], "Exclude": [], "KeepLastCount": 5 },
                  "Schedule": { "Frequency": "daily", "Time": "09:00" },
                  "BackupConfigVersion": 1
                }
                """);

            var loaded = BackupConfig.Load(path);

            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, loaded.BackupConfigVersion);
            Assert.Equal(2, loaded.Destinations.Count);

            var github = loaded.Destinations.Single(d => d.Id == "github");
            Assert.Equal(DestinationKind.GitHub, github.Kind);
            Assert.True(github.Enabled);
            Assert.Equal("git@github.com:me/repo.git", github.RemoteUrl);
            Assert.Equal("main", github.Branch);
            Assert.Equal(new[] { "settings.json" }, github.Include);
            Assert.Equal(new[] { "projects/**" }, github.Exclude);

            var drive = loaded.Destinations.Single(d => d.Id == "drive");
            Assert.Equal(DestinationKind.SyncFolder, drive.Kind); // Transport: 1 -> SyncFolder
            Assert.True(drive.Enabled);
            Assert.Equal(@"D:\SyncFolder", drive.FolderPath);
            Assert.Equal(SyncProvider.OneDrive, drive.SyncProvider);
            Assert.Equal(new[] { "CLAUDE.md" }, drive.Include);
            Assert.Equal(5, drive.KeepLastCount);
        }
        finally { File.Delete(path); }
    }

    // A file at BackupConfigVersion 0 with a non-empty legacy selection must
    // pass through BOTH steps in one Load(): the v0->v1 copy onto
    // Github/Drive, then the v1->v2 fold into Destinations - ending at the
    // Destinations list carrying what used to be the shared legacy selection.
    [Fact]
    public void V0ConfigMigratesThroughBothStepsIntoDestinationList()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, LegacyJson);

            var loaded = BackupConfig.Load(path);

            Assert.Equal(BackupConfig.CurrentBackupConfigVersion, loaded.BackupConfigVersion);
            var github = loaded.Destinations.Single(d => d.Id == "github");
            var drive = loaded.Destinations.Single(d => d.Id == "drive");
            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, github.Include);
            Assert.Equal(new[] { "projects/**" }, github.Exclude);
            Assert.Equal(new[] { "settings.json", "CLAUDE.md" }, drive.Include);
            Assert.Equal(new[] { "projects/**" }, drive.Exclude);
        }
        finally { File.Delete(path); }
    }

    // A config already at the current version with a real N-destination list
    // (including two destinations of the same Kind - explicitly allowed, see
    // BackupDestination's own doc comment) must round-trip through Save/Load
    // unchanged - the migration step must not fire, and every id must
    // survive independently.
    [Fact]
    public void NDestinationsOfTheSameKindRoundTripThroughFile()
    {
        var path = TempPath();
        try
        {
            var config = BackupConfig.Default();
            config.Destinations.Add(new BackupDestination
            {
                Id = BackupDestination.NewId(),
                Name = "Home NAS",
                Kind = DestinationKind.SyncFolder,
                Enabled = true,
                FolderPath = @"\\nas1\backups",
                Include = new() { "settings.json" },
                KeepLastCount = 10,
            });
            config.Destinations.Add(new BackupDestination
            {
                Id = BackupDestination.NewId(),
                Name = "Office NAS",
                Kind = DestinationKind.SyncFolder,
                Enabled = true,
                FolderPath = @"\\nas2\backups",
                Include = new() { "CLAUDE.md" },
                DeleteOlderThanDays = 30,
            });
            config.BackupConfigVersion = BackupConfig.CurrentBackupConfigVersion;
            config.Save(path);

            var loaded = BackupConfig.Load(path);

            // The two well-known destinations plus the two hand-added NAS
            // ones - four total, two of them sharing Kind SyncFolder.
            Assert.Equal(4, loaded.Destinations.Count);
            var nasEntries = loaded.Destinations.Where(d => d.Name is "Home NAS" or "Office NAS").ToList();
            Assert.Equal(2, nasEntries.Count);
            Assert.All(nasEntries, d => Assert.Equal(DestinationKind.SyncFolder, d.Kind));
            Assert.NotEqual(nasEntries[0].Id, nasEntries[1].Id);
            Assert.Equal(10, nasEntries.Single(d => d.Name == "Home NAS").KeepLastCount);
            Assert.Equal(30, nasEntries.Single(d => d.Name == "Office NAS").DeleteOlderThanDays);
        }
        finally { File.Delete(path); }
    }

    // BackupDestination.NewId() must never collide in practice.
    [Fact]
    public void NewIdGeneratesDistinctIds()
    {
        Assert.NotEqual(BackupDestination.NewId(), BackupDestination.NewId());
    }

    // A missing or corrupt backup.json must still degrade to Default()
    // (which is already at the current version, with a two-entry
    // Destinations list) rather than throwing - unchanged contract, just
    // reasserted against the new Destinations-bearing shape.
    [Fact]
    public void MissingOrCorruptFileDegradesToDefaultWithDestinationsPopulated()
    {
        var missing = BackupConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        Assert.Equal(2, missing.Destinations.Count);

        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json ][");
            var corrupt = BackupConfig.Load(path);
            Assert.Equal(2, corrupt.Destinations.Count);
        }
        finally { File.Delete(path); }
    }

    // S17c: a v2 file no longer carries the legacy top-level "Github"/
    // "Drive" JSON keys at all once resaved - the shim that used to keep
    // writing them out (as inert duplication of Destinations) is gone.
    [Fact]
    public void SavedV2FileNoLongerContainsLegacyTopLevelGithubOrDriveKeys()
    {
        var path = TempPath();
        try
        {
            BackupConfig.Default().Save(path);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("\"Github\"", json);
            Assert.DoesNotContain("\"Drive\"", json);
        }
        finally { File.Delete(path); }
    }
}
