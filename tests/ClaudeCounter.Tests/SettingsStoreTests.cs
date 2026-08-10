using ClaudeCounter.Notifications;
using ClaudeCounter.Settings;
using Xunit;

namespace ClaudeCounter.Tests;

public class SettingsStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"ccsettings-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFileReturnsDefaultsAndIsFirstRun()
    {
        var path = TempPath();
        var store = new SettingsStore(path);

        var (settings, isFirstRun) = store.Load();

        Assert.True(isFirstRun);
        Assert.NotNull(settings);
        Assert.NotNull(settings.NotificationState);
        // Normalize() runs on this path too, so a brand-new install's
        // settings object is already stamped at the current version before
        // its first save - otherwise the second-ever launch would read the
        // saved version-0 file back and clear NotificationState again for no
        // reason.
        Assert.Equal(AppSettings.CurrentNotificationStateVersion, settings.NotificationStateVersion);
    }

    [Fact]
    public void RoundTripThroughSaveAndLoadPreservesSettings()
    {
        var path = TempPath();
        try
        {
            var store = new SettingsStore(path);
            var original = new AppSettings
            {
                PollIntervalMinutes = 10,
                WarnThreshold = 60,
                CriticalThreshold = 85,
                WarnAlertsEnabled = false,
                CriticalAlertsEnabled = false,
                PopupPlacement = PopupPlacement.Centered,
            };
            original.Normalize();
            store.Save(original);

            var (loaded, isFirstRun) = store.Load();

            Assert.False(isFirstRun);
            Assert.Equal(10, loaded.PollIntervalMinutes);
            Assert.Equal(60, loaded.WarnThreshold);
            Assert.Equal(85, loaded.CriticalThreshold);
            Assert.False(loaded.WarnAlertsEnabled);
            Assert.False(loaded.CriticalAlertsEnabled);
            Assert.Equal(PopupPlacement.Centered, loaded.PopupPlacement);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileWithoutNotificationStateVersionKeyIsMigrated()
    {
        // Simulates a settings.json written before Warn existed: the property
        // is simply absent from the JSON, so it deserializes to its default
        // (0), which Normalize() must recognize as "pre-Warn" and clear the
        // stale, now-ambiguously-encoded dedupe state.
        var path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "PollIntervalMinutes": 5,
                  "WarnThreshold": 75,
                  "CriticalThreshold": 90,
                  "NotificationState": {
                    "five_hour": { "LastAlertedLevel": 1, "LastResetsAt": null }
                  }
                }
                """);
            var store = new SettingsStore(path);

            var (settings, isFirstRun) = store.Load();

            Assert.False(isFirstRun);
            Assert.Empty(settings.NotificationState);
            Assert.Equal(AppSettings.CurrentNotificationStateVersion, settings.NotificationStateVersion);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileAtCurrentVersionKeepsNotificationState()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path,
                $$"""
                {
                  "NotificationStateVersion": {{AppSettings.CurrentNotificationStateVersion}},
                  "NotificationState": {
                    "five_hour": { "LastAlertedLevel": 1, "LastResetsAt": null }
                  }
                }
                """);
            var store = new SettingsStore(path);

            var (settings, isFirstRun) = store.Load();

            Assert.False(isFirstRun);
            var entry = Assert.Single(settings.NotificationState);
            Assert.Equal("five_hour", entry.Key);
            Assert.Equal(AlertLevel.Warn, entry.Value.LastAlertedLevel);
            Assert.Equal(AppSettings.CurrentNotificationStateVersion, settings.NotificationStateVersion);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CorruptFileReturnsNormalizedDefaultsWithoutThrowing()
    {
        // Regression test for a fix-round-1 finding: Load()'s catch block
        // must normalize its fallback AppSettings the same as the other two
        // return paths (missing file, successful parse) - otherwise a
        // corrupt-file recovery silently skips every invariant Normalize()
        // enforces, including the NotificationStateVersion stamp this task
        // added.
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json ");
            var store = new SettingsStore(path);

            var (settings, isFirstRun) = store.Load();

            Assert.False(isFirstRun);
            Assert.NotNull(settings);
            Assert.NotNull(settings.NotificationState);
            Assert.Empty(settings.NotificationState);
            Assert.Equal(AppSettings.CurrentNotificationStateVersion, settings.NotificationStateVersion);
        }
        finally { File.Delete(path); }
    }
}
