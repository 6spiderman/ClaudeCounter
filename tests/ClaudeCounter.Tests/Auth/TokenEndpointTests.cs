using System.Net;
using System.Text.Json;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Tests.Fakes;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

/// <summary>Covers both grant types, which share one endpoint implementation.</summary>
public class TokenEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private static OAuthTokenRefresher Refresher(StubHandler handler) => new(handler, () => Now);
    private static OAuthCodeExchanger Exchanger(StubHandler handler) => new(handler, () => Now);

    // ---- refresh grant ----------------------------------------------------

    [Fact]
    public async Task RefreshParsesTokensAndComputesExpiry()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"access_token":"newaccess","refresh_token":"newrefresh","expires_in":28800,"scope":"user:profile"}""");

        var result = await Refresher(handler).RefreshAsync("old-refresh", default);

        var success = Assert.IsType<TokenResult.Success>(result);
        Assert.Equal("newaccess", success.Tokens.AccessToken);
        Assert.Equal("newrefresh", success.Tokens.RefreshToken);
        Assert.Equal("user:profile", success.Tokens.Scopes);
        Assert.Equal(Now.AddSeconds(28800).ToUnixTimeMilliseconds(), success.Tokens.ExpiresAtUnixMs);
    }

    [Fact]
    public async Task RefreshRequestCarriesGrantTypeClientIdAndToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","expires_in":3600}""");

        await Refresher(handler).RefreshAsync("the-refresh-token", default);

        Assert.Equal("https://console.anthropic.com/v1/oauth/token", handler.CapturedUri!.ToString());
        Assert.Contains("\"grant_type\":\"refresh_token\"", handler.CapturedBody);
        Assert.Contains(OAuthConfig.ClientId, handler.CapturedBody);
        Assert.Contains("the-refresh-token", handler.CapturedBody);
    }

    [Fact]
    public async Task RefreshWithoutRotationKeepsTheExistingToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"access_token":"a","expires_in":3600}""");
        var success = Assert.IsType<TokenResult.Success>(
            await Refresher(handler).RefreshAsync("keep-me", default));
        Assert.Equal("keep-me", success.Tokens.RefreshToken);
    }

    [Fact]
    public async Task MissingExpiresInFallsBackToAnHour()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"access_token":"a","refresh_token":"r"}""");
        var success = Assert.IsType<TokenResult.Success>(
            await Refresher(handler).RefreshAsync("x", default));
        Assert.Equal(Now.AddSeconds(3600).ToUnixTimeMilliseconds(), success.Tokens.ExpiresAtUnixMs);
    }

    // ---- authorization-code grant ------------------------------------------

    [Fact]
    public async Task ExchangeRequestCarriesEveryPkceField()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","expires_in":3600}""");

        await Exchanger(handler).ExchangeAsync("the-code", "the-state", "the-verifier", default);

        Assert.Equal("https://console.anthropic.com/v1/oauth/token", handler.CapturedUri!.ToString());

        // Parsed rather than substring-matched: the JSON encoder escapes some
        // characters, so a raw Contains on a URL is a coin flip.
        using var body = JsonDocument.Parse(handler.CapturedBody!);
        var sent = body.RootElement;

        Assert.Equal("authorization_code", sent.GetProperty("grant_type").GetString());
        Assert.Equal("the-code", sent.GetProperty("code").GetString());
        Assert.Equal("the-state", sent.GetProperty("state").GetString());
        Assert.Equal("the-verifier", sent.GetProperty("code_verifier").GetString());
        Assert.Equal(OAuthConfig.ClientId, sent.GetProperty("client_id").GetString());
        Assert.Equal(OAuthConfig.RedirectUri, sent.GetProperty("redirect_uri").GetString());
    }

    [Fact]
    public async Task ExchangeWithNoRefreshTokenIsAFailureNotASuccess()
    {
        // There is nothing to fall back to on a code exchange: a session with no
        // refresh token would die in an hour and look like a random logout.
        var handler = new StubHandler(HttpStatusCode.OK, """{"access_token":"a","expires_in":3600}""");
        Assert.IsType<TokenResult.Transient>(
            await Exchanger(handler).ExchangeAsync("c", "s", "v", default));
    }

    // ---- shared failure mapping --------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task RefusedGrantsAreRejected(HttpStatusCode status)
    {
        var handler = new StubHandler(status, """{"error":"invalid_grant"}""");
        var rejected = Assert.IsType<TokenResult.Rejected>(
            await Refresher(handler).RefreshAsync("dead", default));
        Assert.Contains("invalid_grant", rejected.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ServerProblemsAreTransient(HttpStatusCode status) =>
        Assert.IsType<TokenResult.Transient>(
            await Refresher(new StubHandler(status, "{}")).RefreshAsync("retry-me", default));

    [Fact]
    public async Task MalformedSuccessBodyIsTransient() =>
        Assert.IsType<TokenResult.Transient>(
            await Refresher(new StubHandler(HttpStatusCode.OK, "not json"))
                .RefreshAsync("x", default));

    [Fact]
    public async Task SuccessWithoutAnAccessTokenIsTransient() =>
        Assert.IsType<TokenResult.Transient>(
            await Refresher(new StubHandler(HttpStatusCode.OK, """{"refresh_token":"r"}"""))
                .RefreshAsync("x", default));

    [Fact]
    public async Task RejectionMessageNeverEchoesTheResponseBody()
    {
        // Error bodies can contain the credential that was refused, and this
        // message reaches the log and the UI.
        const string secret = "sk-ant-oat-SUPERSECRETVALUE";
        var handler = new StubHandler(HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"token {{secret}} is dead"}""");

        var rejected = Assert.IsType<TokenResult.Rejected>(
            await Refresher(handler).RefreshAsync("dead", default));

        Assert.DoesNotContain(secret, rejected.Message);
        Assert.DoesNotContain("error_description", rejected.Message);
        Assert.Contains("invalid_grant", rejected.Message);
    }

    [Fact]
    public async Task NonJsonErrorBodyStillProducesASafeMessage()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, "<html>Bad Request sk-ant-leak</html>");
        var rejected = Assert.IsType<TokenResult.Rejected>(
            await Refresher(handler).RefreshAsync("dead", default));
        Assert.DoesNotContain("sk-ant-leak", rejected.Message);
    }
}
