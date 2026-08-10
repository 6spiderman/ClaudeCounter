using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// BackupHelpText is a pure static type precisely so its copy can be checked
/// here without constructing SettingsForm or any other Form (this project's
/// tests never do). The facts asserted below are the load-bearing ones from
/// docs/superpowers/specs/2026-08-10-settings-redesign.md "Help content" -
/// getting them wrong would mislead a user about where their credentials go.
/// </summary>
public class BackupHelpTextTests
{
    [Theory]
    [InlineData("GithubEnabled")]
    [InlineData("RemoteUrl")]
    [InlineData("Branch")]
    [InlineData("DriveEnabled")]
    [InlineData("RcloneRemote")]
    [InlineData("Include")]
    [InlineData("Exclude")]
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
            "GithubEnabled", "RemoteUrl", "Branch", "DriveEnabled", "RcloneRemote", "Include", "Exclude",
        };
        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal),
            BackupHelpText.FieldTopics.Keys.OrderBy(k => k, StringComparer.Ordinal));
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
}
