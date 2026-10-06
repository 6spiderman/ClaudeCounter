using ClaudeCounter.Core;
using ClaudeCounter.Settings;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>IconRenderer (WinForms). The TimeText cases live in ClaudeCounter.Core.Tests.</summary>
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

    // S11b: badge=false is the default, so every pre-existing call site above
    // (and everywhere else in the app) keeps rendering exactly as before.
    [Fact]
    public void RenderWithoutBadgeArgumentMatchesBadgeFalse()
    {
        using var implicitDefault = IconRenderer.Render("42", Band.Green);
        using var explicitFalse = IconRenderer.Render("42", Band.Green, badge: false);
        using var implicitBmp = implicitDefault.ToBitmap();
        using var explicitBmp = explicitFalse.ToBitmap();

        Assert.Equal(explicitBmp.GetPixel(26, 6), implicitBmp.GetPixel(26, 6));
    }

    // S11b: the badge must actually paint something, not just fail to throw -
    // sample a pixel inside the corner region DrawBadge targets and confirm
    // it differs from the same icon rendered without a badge.
    [Fact]
    public void RenderWithBadgeAddsAVisibleMarkerInTheCorner()
    {
        using var plain = IconRenderer.Render("42", Band.Green, badge: false);
        using var badged = IconRenderer.Render("42", Band.Green, badge: true);
        using var plainBmp = plain.ToBitmap();
        using var badgedBmp = badged.ToBitmap();

        Assert.NotEqual(plainBmp.GetPixel(26, 6), badgedBmp.GetPixel(26, 6));
    }

    // The badge must still be a well-formed 32x32 icon (it is downsampled by
    // Windows for the actual 16x16 tray strip, not rendered at that size
    // directly), for every text length this renders - "--" as well as a
    // 3-digit percentage.
    [Theory]
    [InlineData("--")]
    [InlineData("42")]
    [InlineData("100")]
    public void RenderWithBadgeStillProducesA32x32Icon(string text)
    {
        using var icon = IconRenderer.Render(text, Band.Red, badge: true);
        Assert.Equal(32, icon.Width);
        Assert.Equal(32, icon.Height);
    }
}
