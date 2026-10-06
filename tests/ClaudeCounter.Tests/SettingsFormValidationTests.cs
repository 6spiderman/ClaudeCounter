// tests/ClaudeCounter.Tests/SettingsFormValidationTests.cs
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// I3 and M6: SettingsForm.HasEmbeddedCredential and HasLeadingDash are pure,
/// static predicates precisely so they can be exercised here without
/// constructing a Form (this project's tests never do - a Form needs a
/// message loop / STA thread a headless test run does not have).
/// </summary>
public class SettingsFormValidationTests
{
    [Theory]
    [InlineData("https://user:token@github.com/org/repo.git", true)]
    [InlineData("https://ghp_abc123@github.com/org/repo.git", true)]
    [InlineData("HTTPS://TOKEN@GITHUB.COM/org/repo.git", true)]
    [InlineData("https://github.com/org/repo.git", false)]
    [InlineData("git@github.com:org/repo.git", false)] // SSH shorthand - not a credential
    [InlineData("ssh://git@github.com/org/repo.git", true)] // "git@" here IS a userinfo component
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a url at all", false)]
    public void HasEmbeddedCredentialDetectsUserinfoComponent(string? url, bool expected) =>
        Assert.Equal(expected, SettingsForm.HasEmbeddedCredential(url));

    [Theory]
    [InlineData("-rf", true)]
    [InlineData("--config-is-evil", true)]
    [InlineData("gdrive:backup", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasLeadingDashDetectsOptionLikeRemote(string? remote, bool expected) =>
        Assert.Equal(expected, SettingsForm.HasLeadingDash(remote));
}
