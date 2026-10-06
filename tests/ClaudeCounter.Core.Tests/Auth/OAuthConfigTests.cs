using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

/// <summary>
/// CC-01: scope names are not secrets, so a grant broader than the app needs
/// is logged rather than silently accepted - visible in the log a user might
/// paste into a bug report, not just in SECURITY.md.
/// </summary>
public class OAuthConfigTests
{
    private static string ReadLog()
    {
        if (Log.FilePath is not { } path || !File.Exists(path))
            return string.Empty;
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void LogsWhenTheBroadScopeWasGranted()
    {
        var before = ReadLog().Length;
        OAuthConfig.LogIfBroaderThanNeeded("org:create_api_key user:profile user:inference");
        Assert.Contains("org:create_api_key", ReadLog()[before..]);
    }

    [Theory]
    [InlineData("user:profile user:inference")]
    [InlineData(null)]
    [InlineData("")]
    public void StaysQuietWhenTheScopeIsNarrow(string? granted)
    {
        // Asserted with DoesNotContain rather than exact emptiness: xunit runs
        // test classes in parallel, and other tests write to this same real
        // log file concurrently, so a slice taken here is not otherwise
        // guaranteed to be empty.
        var before = ReadLog().Length;
        OAuthConfig.LogIfBroaderThanNeeded(granted);
        Assert.DoesNotContain("org:create_api_key", ReadLog()[before..]);
    }
}
