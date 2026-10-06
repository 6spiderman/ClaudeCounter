using ClaudeCounter.Notifications;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

public class AlertPopupStackingTests
{
    [Fact]
    public void NoOpenPopupsMeansNoOffset()
    {
        Assert.Equal(0, AlertPopupForm.StackOffsetFor(Array.Empty<int>(), gap: 8));
    }

    [Fact]
    public void SingleOpenPopupOffsetsByItsHeightPlusGap()
    {
        Assert.Equal(108, AlertPopupForm.StackOffsetFor(new[] { 100 }, gap: 8));
    }

    [Fact]
    public void MultipleOpenPopupsStackCumulatively()
    {
        Assert.Equal(40 + 8 + 60 + 8, AlertPopupForm.StackOffsetFor(new[] { 40, 60 }, gap: 8));
    }
}

// S13 part A: Critical previously fell into AlertPopupForm.Show's `_` switch
// arm and rendered on the plain theme background, so the 90% tier read as
// LESS urgent than the 75% Warn popup (a loud amber card). Critical must now
// share Maxed's red band, with its own readable foreground - see
// AlertPopupForm.ColorsFor's doc comment for the actual WCAG contrast
// numbers behind the black-vs-white choice.
public class AlertPopupColorsTests
{
    private static readonly ClaudeCounter.UI.Palette Palette =
        new(Color.FromArgb(32, 32, 32), Color.FromArgb(229, 229, 229),
            Color.FromArgb(150, 150, 150), Color.FromArgb(58, 58, 58),
            Color.FromArgb(70, 70, 70));

    [Fact]
    public void CriticalUsesTheRedBandLikeMaxed()
    {
        var critical = AlertPopupForm.ColorsFor(AlertLevel.Critical, Palette);
        var maxed = AlertPopupForm.ColorsFor(AlertLevel.Maxed, Palette);
        Assert.Equal(Theme.BandColor(Band.Red), critical.Back);
        Assert.Equal(maxed.Back, critical.Back); // Critical and Maxed deliberately share red
    }

    [Fact]
    public void CriticalForegroundIsReadableAgainstTheRedBand()
    {
        // Black gives ~6.3:1 contrast against Theme.BandColor(Band.Red);
        // White only gives ~3.4:1, which fails the 4.5:1 AA threshold this
        // popup's normal-weight body text needs. See ColorsFor's doc comment.
        var critical = AlertPopupForm.ColorsFor(AlertLevel.Critical, Palette);
        Assert.Equal(Color.Black, critical.Fore);
    }

    [Fact]
    public void CriticalDismissButtonUsesTheTranslucentOverlayNotBarBack()
    {
        // Before the fix, Critical fell into the default arm and got
        // palette.BarBack - a plain gray patch that would look wrong against
        // a now-red card.
        var critical = AlertPopupForm.ColorsFor(AlertLevel.Critical, Palette);
        Assert.Equal(Color.FromArgb(60, 0, 0, 0), critical.DismissBack);
        Assert.NotEqual(Palette.BarBack, critical.DismissBack);
    }

    [Fact]
    public void WarnAndDefaultColorsAreUnchanged()
    {
        var warn = AlertPopupForm.ColorsFor(AlertLevel.Warn, Palette);
        Assert.Equal(Theme.BandColor(Band.Amber), warn.Back);
        Assert.Equal(Color.Black, warn.Fore);
        Assert.Equal(Color.FromArgb(60, 0, 0, 0), warn.DismissBack);

        var none = AlertPopupForm.ColorsFor(AlertLevel.None, Palette);
        Assert.Equal(Palette.Back, none.Back);
        Assert.Equal(Palette.Fore, none.Fore);
        Assert.Equal(Palette.BarBack, none.DismissBack);
    }
}
