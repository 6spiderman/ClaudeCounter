using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests;

public class SemVersionTests
{
    private static SemVersion Parse(string text)
    {
        Assert.True(SemVersion.TryParse(text, out var v), $"failed to parse '{text}'");
        return v;
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("V1.2.3", 1, 2, 3, null)]
    [InlineData("1.0.0-rc.1", 1, 0, 0, "rc.1")]
    [InlineData("v2.0.0-beta", 2, 0, 0, "beta")]
    [InlineData("1.2.3+9fceb02", 1, 2, 3, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("  1.2.3  ", 1, 2, 3, null)]
    public void ParsesSupportedForms(string text, int major, int minor, int patch, string? prerelease)
    {
        var v = Parse(text);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(prerelease, v.Prerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]
    public void RejectsGarbage(string? text) =>
        Assert.False(SemVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.2.4", "1.2.3")]
    [InlineData("1.3.0", "1.2.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("v1.0.1", "1.0.0")]
    public void NewerSortsAbove(string newer, string older) =>
        Assert.True(Parse(newer).CompareTo(Parse(older)) > 0);

    [Theory]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("1.2.3+abc", "1.2.3+def")]
    public void EquivalentVersionsCompareEqual(string left, string right) =>
        Assert.Equal(0, Parse(left).CompareTo(Parse(right)));

    [Fact]
    public void ReleaseOutranksPrereleaseOfSameNumbers() =>
        Assert.True(Parse("1.0.0").CompareTo(Parse("1.0.0-rc.1")) > 0);

    [Theory]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]     // numeric identifiers compare numerically
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9")]    // ...not as strings
    [InlineData("1.0.0-rc", "1.0.0-beta")]       // alphanumeric compares ordinally
    [InlineData("1.0.0-rc.1", "1.0.0-rc")]       // more identifiers sorts above fewer
    [InlineData("1.0.0-rc", "1.0.0-1")]          // alphanumeric outranks numeric
    public void PrereleaseOrdering(string higher, string lower) =>
        Assert.True(Parse(higher).CompareTo(Parse(lower)) > 0);

    [Fact]
    public void ComparingToNullSortsAbove() =>
        Assert.True(Parse("0.0.1").CompareTo(null) > 0);

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1")]
    public void ToStringDropsThePrefix(string input, string expected) =>
        Assert.Equal(expected, Parse(input).ToString());

    [Fact]
    public void IsPrereleaseReflectsTheSuffix()
    {
        Assert.True(Parse("1.0.0-rc.1").IsPrerelease);
        Assert.False(Parse("1.0.0").IsPrerelease);
    }
}
