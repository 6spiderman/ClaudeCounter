using ClaudeCounter.Core.Auth;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

public class PastedCodeTests
{
    [Fact]
    public void SplitsTheCodeHashStateFormAnthropicShows()
    {
        var parsed = PastedCode.Parse("abc123#xyz789");
        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.Code);
        Assert.Equal("xyz789", parsed.State);
    }

    [Fact]
    public void AcceptsABareCode()
    {
        var parsed = PastedCode.Parse("abc123");
        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.Code);
        Assert.Null(parsed.State);
    }

    [Theory]
    [InlineData("  abc123#xyz789  ")]
    [InlineData("\tabc123#xyz789\r\n")]
    [InlineData("\"abc123#xyz789\"")]
    [InlineData("'abc123#xyz789'")]
    public void ToleratesWhitespaceAndQuotes(string pasted)
    {
        var parsed = PastedCode.Parse(pasted);
        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.Code);
        Assert.Equal("xyz789", parsed.State);
    }

    [Fact]
    public void AcceptsTheWholeCallbackUrlFromTheAddressBar()
    {
        var parsed = PastedCode.Parse(
            "https://console.anthropic.com/oauth/code/callback?code=abc123&state=xyz789");

        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.Code);
        Assert.Equal("xyz789", parsed.State);
    }

    [Fact]
    public void UrlDecodesQueryValues()
    {
        var parsed = PastedCode.Parse("https://example.test/cb?code=a%2Bb&state=c%2Fd");
        Assert.NotNull(parsed);
        Assert.Equal("a+b", parsed!.Code);
        Assert.Equal("c/d", parsed.State);
    }

    [Fact]
    public void UrlWithNoStateParameterIsStillUsable()
    {
        var parsed = PastedCode.Parse("https://example.test/cb?code=abc123");
        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.Code);
        Assert.Null(parsed.State);
    }

    [Fact]
    public void UrlFragmentIsTreatedAsTheState()
    {
        var parsed = PastedCode.Parse("https://example.test/cb?code=abc123#xyz789");
        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.Code);
        Assert.Equal("xyz789", parsed.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("#xyz789")]                              // state but no code
    [InlineData("https://example.test/cb?state=xyz")]    // URL with no code
    [InlineData("https://example.test/cb")]              // URL with no query
    public void RejectsInputWithNoCode(string? pasted) =>
        Assert.Null(PastedCode.Parse(pasted));

    [Fact]
    public void EmptyStateAfterTheHashIsTreatedAsAbsent()
    {
        var parsed = PastedCode.Parse("abc123#");
        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.Code);
        Assert.Null(parsed.State);
    }

    [Fact]
    public void OnlyTheFirstHashSplits()
    {
        // Base64url never contains '#', but a state that somehow did must not
        // be silently truncated into a mismatch.
        var parsed = PastedCode.Parse("abc#xy#z");
        Assert.NotNull(parsed);
        Assert.Equal("abc", parsed!.Code);
        Assert.Equal("xy#z", parsed.State);
    }
}
