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
