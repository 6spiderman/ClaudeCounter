// tests/ClaudeCounter.Tests/RestoreTests/ClaudeProcessDetectorTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

public class ClaudeProcessDetectorTests
{
    [Fact]
    public void AppearsRunningIsTrueWhenCountIsPositive() =>
        Assert.True(ClaudeProcessDetector.AppearsRunning(() => 3));

    [Fact]
    public void AppearsRunningIsFalseWhenCountIsZero() =>
        Assert.False(ClaudeProcessDetector.AppearsRunning(() => 0));

    // Restore rule 6 is a warning input, never a gate - a failure detecting
    // whether Claude Code is running must never propagate as an exception
    // out of this check (which would risk taking the whole restore flow
    // down with it).
    [Fact]
    public void AppearsRunningIsFalseWhenTheCounterThrows() =>
        Assert.False(ClaudeProcessDetector.AppearsRunning(() => throw new InvalidOperationException("denied")));

    // Exercises the real, non-fake path end to end - no assertion on the
    // result (environment-dependent), just that it never throws.
    [Fact]
    public void RealCheckDoesNotThrow()
    {
        var exception = Record.Exception(() => ClaudeProcessDetector.AppearsRunning());
        Assert.Null(exception);
    }
}
