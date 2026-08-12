using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// BackupHelpText is a pure static type precisely so its copy can be checked
/// here without constructing a Form (this project's tests never do). The
/// facts asserted below are the load-bearing ones from
/// docs/superpowers/specs/2026-08-10-settings-redesign.md "Help content" -
/// getting them wrong would mislead a user about where their credentials go.
///
/// S17c: rewritten for any-number-of-destinations-of-any-kind - the old
/// "GitHub plus one cloud/NAS" field-topic set (DriveEnabled) and the old
/// "one transport at a time" claim are both gone; see FieldTopics and
/// FullGuide's own doc comments for what replaced them.
/// </summary>
public class BackupHelpTextTests
{
    [Theory]
    [InlineData("Name")]
    [InlineData("GithubEnabled")]
    [InlineData("RemoteUrl")]
    [InlineData("Branch")]
    [InlineData("DestinationEnabled")]
    [InlineData("SyncFolder")]
    [InlineData("RcloneRemote")]
    [InlineData("Include")]
    [InlineData("Exclude")]
    [InlineData("Retention")]
    public void EveryFieldWithAnInfoButtonHasNonEmptyHelp(string key)
    {
        Assert.True(BackupHelpText.FieldTopics.ContainsKey(key), $"missing topic: {key}");
        Assert.False(string.IsNullOrWhiteSpace(BackupHelpText.FieldTopics[key]));
    }

    [Fact]
    public void FieldTopicsHasExactlyTheExpectedFields()
    {
        // Guards against a topic being silently added or removed without a
        // matching update to the list of fields that actually get a button -
        // the theory above only proves the listed keys are present, not that
        // the dictionary is limited to them.
        var expected = new[]
        {
            "Name", "GithubEnabled", "RemoteUrl", "Branch", "DestinationEnabled",
            "SyncFolder", "RcloneRemote", "Include", "Exclude", "Retention",
        };
        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal),
            BackupHelpText.FieldTopics.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    // S14b: the SyncFolder topic is the sync-folder transport's own help -
    // must name every sync client this feature was designed for, state that
    // no sign-in is needed, and give the UNC-over-mapped-letter guidance
    // (the single most important correctness fact about it - a mapped drive
    // letter is not guaranteed to resolve under Task Scheduler).
    [Fact]
    public void SyncFolderTopicNamesEverySupportedSyncClientAndTheNoSignInFact()
    {
        Assert.Contains("Google Drive", BackupHelpText.SyncFolder);
        Assert.Contains("OneDrive", BackupHelpText.SyncFolder);
        Assert.Contains("Dropbox", BackupHelpText.SyncFolder);
        Assert.Contains("NAS", BackupHelpText.SyncFolder);
        Assert.Contains("No sign-in", BackupHelpText.SyncFolder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyncFolderTopicPrefersUncOverAMappedDriveLetter()
    {
        Assert.Contains("UNC", BackupHelpText.SyncFolder);
        Assert.Contains("mapped drive", BackupHelpText.SyncFolder, StringComparison.OrdinalIgnoreCase);
    }

    // S17c: retention now lives per-destination (BackupDestinationEditDialog),
    // not in the Advanced dialog - its own topic must say so, including the
    // GitHub exception, so a user looking at a GitHub destination's page
    // (which shows no retention control at all) is not left wondering where
    // it went.
    [Fact]
    public void RetentionTopicStatesItDoesNotApplyToGitHub()
    {
        Assert.Contains("GitHub", BackupHelpText.Retention);
        Assert.Contains("does not apply", BackupHelpText.Retention, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuideIsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BackupHelpText.FullGuide));
    }

    [Fact]
    public void GuideStatesRepositoryMustBePrivate()
    {
        Assert.Contains("must be private", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuideStatesCredentialsAreNeverBackedUp()
    {
        Assert.Contains("NEVER BACKED UP", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".credentials.json", BackupHelpText.FullGuide);
        Assert.Contains("session.dat", BackupHelpText.FullGuide);
    }

    [Fact]
    public void GuideMentionsThePluginTreeCaveat()
    {
        Assert.Contains("plugins/**", BackupHelpText.FullGuide);
    }

    [Fact]
    public void GuideMentionsAllThreeRunNowOutcomes()
    {
        Assert.Contains("complete", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not run", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
    }

    // S7: the guide must describe what the Advanced dialog's schedule
    // settings actually do, including that the two battery settings default
    // OFF (the opposite of Windows' own default) - a user reading the guide
    // must not come away thinking the Windows default applies.
    [Fact]
    public void GuideDescribesScheduleRobustnessSettings()
    {
        Assert.Contains("missed backup", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("battery", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retries automatically", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
    }

    // S8: the guide must describe retention's union rule and the
    // never-empty-the-remote floor - both are load-bearing safety facts, not
    // just feature description.
    [Fact]
    public void GuideDescribesDriveRetentionRulesAndSafetyFloor()
    {
        Assert.Contains("retention", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("either rule", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never deletes the single most recent backup", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
    }

    // S14b: the guide must lead with the sync-folder route (mentioned before
    // the rclone appendix).
    [Fact]
    public void GuideLeadsWithSyncFolderAheadOfTheRcloneAdvancedAppendix()
    {
        var syncFolderIndex = BackupHelpText.FullGuide.IndexOf("SYNC FOLDER", StringComparison.Ordinal);
        var rcloneAdvancedIndex = BackupHelpText.FullGuide.IndexOf("ADVANCED: RCLONE REMOTE", StringComparison.Ordinal);
        Assert.True(syncFolderIndex >= 0, "guide should have a SYNC FOLDER section");
        Assert.True(rcloneAdvancedIndex >= 0, "guide should have an ADVANCED: RCLONE REMOTE section");
        Assert.True(syncFolderIndex < rcloneAdvancedIndex,
            "sync folder should be described before the rclone appendix");
    }

    // S17c: the old "one transport at a time" restriction (GitHub plus
    // exactly one cloud/NAS destination) was a UI limitation, never a real
    // constraint of the model - it is gone, and the guide must say so
    // explicitly rather than leaving stale wording that would now be false.
    [Fact]
    public void GuideStatesAnyNumberOfDestinationsOfAnyKindAreSupported()
    {
        Assert.DoesNotContain("one transport at a time", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("any number of destinations", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("any kind", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
    }

    // S17c: removing a destination must be documented as local-only - the
    // brief's explicit requirement is that nobody reading the guide (or the
    // remove confirmation) is left fearing it deletes their actual backups.
    [Fact]
    public void GuideStatesRemovingADestinationOnlyAffectsLocalConfiguration()
    {
        Assert.Contains("does not delete anything at the destination itself", BackupHelpText.FullGuide,
            StringComparison.OrdinalIgnoreCase);
    }

    // S17c: "all the details to all backups" - every kind must document what
    // it needs, how it authenticates (or that it needs none), and how
    // restore works for it, not just GitHub and the generic sync-folder
    // blurb from before.
    [Theory]
    [InlineData("GITHUB")]
    [InlineData("SYNC FOLDER")]
    [InlineData("ADVANCED: RCLONE REMOTE")]
    public void GuideHasADedicatedSectionForEveryDestinationKind(string sectionHeader)
    {
        Assert.Contains(sectionHeader, BackupHelpText.FullGuide);
    }

    [Fact]
    public void GuideDescribesAuthenticationForEveryKind()
    {
        Assert.Contains("AUTHENTICATION:", BackupHelpText.FullGuide);
        // The sync-folder kinds need none at all - the guide must say so
        // plainly rather than silently omitting the topic for them.
        Assert.Contains("none needed at all", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuideDescribesRestoreForEveryKind()
    {
        Assert.Contains("RESTORE:", BackupHelpText.FullGuide);
        Assert.Contains("commits", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timestamped zips", BackupHelpText.FullGuide, StringComparison.OrdinalIgnoreCase);
    }
}
