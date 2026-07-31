using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// CC-02: Shell.OpenUrl is the last line of defense before UseShellExecute,
/// which dispatches through whatever handler is registered for a URL's
/// scheme - not just http(s). These cases must be refused before a process is
/// ever started, so they are safe to run in CI without launching anything.
/// </summary>
public class ShellTests
{
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("file://C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\attacker-host\share\payload.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://example.test")] // http, not https - also refused
    public void RefusesNonHttpsUrlsWithoutStartingAProcess(string url) =>
        Assert.False(Shell.OpenUrl(url));

    [Fact]
    public void NullIsRefusedRatherThanThrowing() =>
        Assert.False(Shell.OpenUrl(null!));
}
