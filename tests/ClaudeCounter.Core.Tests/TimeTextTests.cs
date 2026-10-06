using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests;

public class TimeTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-5, "now")]
    [InlineData(45, "45 min")]
    [InlineData(135, "2 h 15 min")]
    public void CountdownFormatting(int minutesFromNow, string expected)
    {
        Assert.Equal(expected, TimeText.Countdown(Now.AddMinutes(minutesFromNow), Now));
    }

    [Fact]
    public void CountdownUsesDaysBeyondTwoDays()
    {
        Assert.Equal("3 d 4 h", TimeText.Countdown(Now.AddDays(3).AddHours(4), Now));
    }

    // S11b: Ago is Countdown's past-tense counterpart, used for "last
    // succeeded N ago" in BackupHealthPresenter.
    [Theory]
    [InlineData(0, "just now")]
    [InlineData(-1, "just now")]     // boundary: exactly 1 min ago is still "just now"
    [InlineData(-45, "45 min ago")]
    [InlineData(-135, "2 h ago")]    // 2 h 15 min - Ago is single-unit, unlike Countdown
    public void AgoFormatting(int minutesAgo, string expected)
    {
        Assert.Equal(expected, TimeText.Ago(Now.AddMinutes(minutesAgo), Now));
    }

    [Fact]
    public void AgoUsesDaysBeyondTwoDays()
    {
        Assert.Equal("3 d ago", TimeText.Ago(Now.AddDays(-3).AddHours(-4), Now));
    }
}
