using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests;

public class AppInfoTests
{
    [Fact]
    public void VersionNeverCarriesBuildMetadata() =>
        Assert.DoesNotContain('+', AppInfo.Version);

    [Fact]
    public void VersionIsParseable() =>
        Assert.True(SemVersion.TryParse(AppInfo.Version, out _),
            $"AppInfo.Version '{AppInfo.Version}' must parse, or the update check silently gives up.");

    [Fact]
    public void DisplayVersionIsPrefixed() =>
        Assert.StartsWith("v", AppInfo.DisplayVersion);

    [Fact]
    public void UserAgentIdentifiesTheApp() =>
        Assert.Equal($"ClaudeCounter/{AppInfo.Version}", AppInfo.UserAgent);

    [Fact]
    public void RepoConstantsAgree()
    {
        Assert.Equal($"{AppInfo.RepoOwner}/{AppInfo.RepoName}", AppInfo.RepoSlug);
        Assert.Equal($"https://github.com/{AppInfo.RepoSlug}", AppInfo.RepoUrl);
        Assert.StartsWith(AppInfo.RepoUrl, AppInfo.ReleasesUrl);
        Assert.Equal($"{AppInfo.RepoOwner}.{AppInfo.RepoName}", AppInfo.WingetId);
    }

    [Fact]
    public void LocalTestRunLooksLikeADevBuild() =>
        // Guards the wiring: if Directory.Build.props stopped supplying a
        // version, this flips and the update check would start nagging.
        Assert.True(AppInfo.IsDevBuild);
}
