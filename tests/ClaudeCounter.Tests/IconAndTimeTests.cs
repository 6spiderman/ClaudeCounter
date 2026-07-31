using ClaudeCounter.Core;
using ClaudeCounter.Settings;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

public class IconAndTimeTests
{
    private static AppSettings Settings() => new() { WarnThreshold = 75, CriticalThreshold = 90 };

    [Theory]
    [InlineData(0, Band.Green)]
    [InlineData(74.9, Band.Green)]
    [InlineData(75, Band.Amber)]
    [InlineData(89.9, Band.Amber)]
    [InlineData(90, Band.Red)]
    [InlineData(100, Band.Red)]
    public void BandBoundaries(double utilization, Band expected)
    {
        Assert.Equal(expected, IconRenderer.BandFor(utilization, Settings()));
    }

    [Theory]
    [InlineData("42", Band.Green)]
    [InlineData("100", Band.Red)]
    [InlineData("--", Band.Gray)]
    public void RenderProducesAnIcon(string text, Band band)
    {
        using var icon = IconRenderer.Render(text, band);
        Assert.Equal(32, icon.Width);
    }

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
}
